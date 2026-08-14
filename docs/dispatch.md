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
| Named live-idle Grok/Codex | Type the prompt into the open CLI/GUI window and press Enter |
| Named live-working Grok/Codex | Same: type into the open window so it becomes a queued or immediate new turn |
| Active Claude Desktop session | Resolve by title with `mcp__ccd_session_mgmt__list_sessions`, then use `mcp__ccd_session_mgmt__send_message`; never CLI-resume it |

Named live Grok/Codex work is typed into the existing window. CCCG does not
start a second `grok -r` / `codex exec resume` against that live session.
CCCG-managed resume/create still serialize on the workspace lease. Auto-pick
(no `sessionId`) still skips unbound live-working peers.

## Tools

| Tool | Purpose |
|---|---|
| `cccg_list_peers` | List Claude, Grok, and/or Codex sessions and bindings |
| `cccg_inspect_peer` | Inspect title, model, cwd, and writer state |
| `cccg_dispatch` | Queue a background job and return `jobId` immediately |
| `cccg_dispatch_wait` | Keep the Claude tool call open and return the peer response automatically |
| `cccg_job_status` | Read queued/running/succeeded/failed status |
| `cccg_job_collect` | Collect normalized response and real provider session ID |
| `cccg_inbox_post/list/ack` | Shared cross-process mailbox |
| `cccg_runtime_status` | Show the active versioned Worker and hot-update mode |

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

Claude support in 0.4.5 reuses the existing string-valued `provider` argument,
so it is a Worker-only update and already-connected Hosts can call
`provider="claude"`. Updated tool descriptions/foreman instructions become
visible after a future MCP reconnect but are not required for the call to work.

An existing Claude Session that started the older pre-Host executable must be
restarted once. Future Worker-only updates are hot.

## Build and verification

```powershell
dotnet restore .\CCCG.sln
dotnet build .\src\CCCG.Dispatch.Worker\CCCG.Dispatch.Worker.csproj -c Release --no-restore
dotnet build .\src\CCCG.Dispatch\CCCG.Dispatch.csproj -c Release --no-restore -p:OutputPath=..\..\artifacts\build-validation\dispatch\
dotnet run --project .\tests\CCCG.Tests\CCCG.Tests.csproj -c Release
.\scripts\install-dispatch-worker.ps1 -Version 0.5.5
```

The alternate Host output path avoids overwriting the stable Host executable
while Claude Desktop has it open.

See [dispatch validation](dispatch-validation.md) for the Claude, Codex, and
restored-quota Grok live evidence.
