# CCCG Dispatch MCP

Claude Desktop remains the coordinator and the official Claude engine. CCCG
Dispatch lets Claude delegate a turn to Claude, Codex, or Grok without replacing the
Claude model, hooks, WebSearch, Remote Control, or Claude session tools.

## Delivery model

There are three deliberately different peer states:

| Peer | Dispatch behavior |
|---|---|
| CCCG-managed, currently busy | Accept immediately into CCCG's cross-process FIFO; resume the same provider session as soon as its writer is free |
| Closed/resumable | Start the provider CLI with the existing session ID |
| Named live-idle Grok/Codex | Deliver through the owner spool when a CCCG owner daemon holds the session lease; otherwise skip or fail closed |
| Named live-working Grok/Codex | Queue through the same CCCG owner; unowned live sessions are never keystroke targets |
| Active Claude Desktop session | Resolve by title with `mcp__ccd_session_mgmt__list_sessions`, then use `mcp__ccd_session_mgmt__send_message`; never CLI-resume it |

Named live Grok/Codex work uses the CCCG owner spool and requires a delivered
receipt. CCCG neither types into the existing window nor starts a second
`grok -r` / `codex exec resume` against an owned live session. CCCG-managed
resume/create still serialize on the workspace lease. Auto-pick (no
`sessionId`) skips unowned live peers.

## Tools

| Tool | Purpose |
|---|---|
| `cccg_list_peers` | List Claude, Grok, and/or Codex sessions and bindings |
| `cccg_inspect_peer` | Inspect title, model, cwd, and writer state |
| `cccg_watch_peers` | Snapshot comma-separated session IDs and report `found`, `status`, or `pid` changes since the same watch set's previous call |
| `cccg_dispatch` | Queue a background job, optionally override model/reasoning for this turn, and return `jobId` immediately |
| `cccg_dispatch_wait` | Dispatch with the same per-turn options and keep the tool call open until the peer responds |
| `cccg_job_status` | Read queued/running/succeeded/failed status |
| `cccg_job_collect` | Collect normalized response and real provider session ID |
| `cccg_inbox_post/list/ack` | Shared cross-process mailbox |
| `cccg_runtime_status` | Show the active versioned Worker and hot-update mode |

## Per-dispatch model

`cccg_dispatch` and `cccg_dispatch_wait` accept optional `model` and
`reasoningEffort` strings. Omitted, empty, or whitespace-only values preserve
the existing provider or owner defaults; non-empty values are trimmed and
passed through without aliases or an allow-list.

- Codex CLI create/resume adds `--model <id>` and
  `-c model_reasoning_effort=<value>` as `codex exec` options before the
  `resume` subcommand.
- Grok CLI create/resume adds `--model <id>` and
  `--reasoning-effort <value>`.
- A Codex owner delivery stores the same values on the spool message and uses
  them for that app-server turn only; later turns without overrides return to
  the owner process defaults.
- Claude rejects a job that sets either parameter before any provider launch
  or owner delivery. CCCG does not silently ignore Claude overrides.

`cccg_job_collect` keeps all existing field names and additively returns
`model` and `reasoningEffort` from the job.

## Session identity

- Grok create: CCCG generates a UUID and passes `--session-id` before launch.
- Codex create: CCCG runs `codex exec --json` and reads the first
  `thread.started.thread_id` event.
- Claude create: CCCG generates a UUID, runs the current official Claude Code
  binary in text-only safe mode, and passes `--session-id`. Resume uses the same
  UUID and retains prior conversational context.
- CCCG child Claude sessions run with no tools, MCP, skills, plugins, hooks,
  slash commands, project instructions, or auto-memory. Their contract is
  model-only text input/text output, preventing recursive CCCG delegation and
  workspace side effects.
- A binding is saved only after provider exit code `0` and a valid session ID.
- Later turns for that provider/workspace use the saved ID.
- A failed provider call is recorded but never promoted to a successful
  binding.

## Multi-Claude concurrency

Bindings, job status, and inbox files use OS-visible file locks and atomic
replacement. Dispatches are serialized by provider and workspace, so MCP
servers launched by different Claude Desktop sessions cannot write the same
provider transcript concurrently. Background jobs are executed by a detached
versioned Worker and survive the MCP Host that created them. A dead Worker PID
changes its unfinished job to `failed` instead of leaving it stuck forever.

## Hot update

Claude connects to the stable MCP Host:

```text
artifacts\cccg-dispatch\win-x64-full\cccg-dispatch.exe
```

The Host owns the fixed MCP tool contract. Every tool call verifies and starts
the Worker selected by:

```text
%LOCALAPPDATA%\CCCG\dispatch\worker-current.json
```

Install a new immutable Worker and atomically switch the descriptor:

```powershell
.\scripts\install-dispatch-worker.ps1 -Version 0.5.5
```

Already-connected Hosts use the new Worker on the next tool call. Changing
implementation does not require a Claude Session restart. Adding, removing, or
changing an MCP tool schema still requires an MCP reconnect because
`tools/list` is negotiated by the Host connection.

This batch changes the Host schema by adding `model` / `reasoningEffort` to
both dispatch tools and adding `cccg_watch_peers`. Deploy in this order:

1. install and activate the new Worker;
2. install the rebuilt Host at the stable `cccg-dispatch.exe` path;
3. reconnect the `cccg-dispatch` MCP server (or restart Claude Desktop) so it
   negotiates the new tool schema once.

Worker-first avoids the install window where a new Host can send overrides to
an older Worker that would ignore the unknown arguments.

Claude support in 0.4.5 reuses the existing string-valued `provider` argument,
so it is a Worker-only update and already-connected Hosts can call
`provider="claude"`. Updated tool descriptions/foreman instructions become
visible after a future MCP reconnect but are not required for the call to work.

An existing Claude Session that started the older pre-Host executable must be
restarted once. Future Worker-only updates are hot.

## Build and verification

```powershell
dotnet build .\src\CCCG.Dispatch.Worker\CCCG.Dispatch.Worker.csproj -c Release
dotnet build .\src\CCCG.Dispatch\CCCG.Dispatch.csproj -c Release -p:OutputPath=..\..\artifacts\build-validation\dispatch\
dotnet run --project .\tests\CCCG.Tests\CCCG.Tests.csproj -c Release
.\scripts\install-dispatch-worker.ps1 -Version <version>
```

The alternate Host output path avoids overwriting the stable Host executable
while Claude Desktop has it open.

Do not build `CCCG.sln` in this checkout; it references an absent
`experiments\` project.

See [dispatch validation](dispatch-validation.md) for the Claude, Codex, and
restored-quota Grok live evidence.
