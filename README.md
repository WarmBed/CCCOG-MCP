# CCCG

English | [繁體中文](README.zh-TW.md)

You probably run Claude, Codex, and Grok in separate windows that know nothing
about each other. CCCG (Claude / Codex / Grok) fixes that on your own machine:
Claude becomes the coordinator — it can hand a task to Codex or Grok, pick
which model answers (`gpt-5.6-luna` at `xhigh`, say), wait for the result, and
even continue a conversation you started yourself in another window days ago.

Under the hood that takes real plumbing: a durable job queue that survives
crashes, delivery receipts that only fire when the other agent actually
processed the message (not "keystrokes were sent"), a session directory for
all three providers, and workers you can hot-swap without restarting anything.
That plumbing is the **Dispatch MCP**, and it is the core of this repo.

CCCG is designed for controlled testing, not for concealing which provider
answered. Every generated answer is labeled with the actual provider and
backend model. CCCG does not patch Claude binaries, reuse Claude credentials,
bypass billing or authentication, suppress product updates, or claim that a
third-party model is an Anthropic model.

## Architecture

```text
 Claude Desktop / Claude Code  (MCP client, coordinator)
        |
        |  cccg_list_peers . cccg_inspect_peer . cccg_watch_peers
        |  cccg_dispatch / cccg_dispatch_wait (model?, reasoningEffort?)
        |  cccg_job_status / cccg_job_collect . cccg_inbox_post/list/ack
        v
 +---------------------------+
 | cccg-dispatch.exe         |  MCP Host - owns the fixed tool contract
 |  (stable, reconnect-bound)|  (schema changes need one MCP reconnect)
 +---------------------------+
        | per tool call: read worker-current.json, verify SHA-256, spawn
        v
 +---------------------------+     %LOCALAPPDATA%\CCCG\dispatch\
 | cccg-dispatch-worker.exe  |     +-- workers\<version>\   (immutable)
 |  (versioned, HOT-SWAP)    |     +-- worker-current.json  (atomic switch)
 +---------------------------+     +-- jobs\<jobId>\        (status, prompt,
        |                          |                         stdout, receipts)
        |                          +-- bindings\ leases\ owners\ inbox.jsonl
        |
        +--> Peer directories: enumerate Grok / Codex / Claude session stores
        |      list, inspect, watch + diff (found / status / pid)
        |
        +--> Job store + FIFO leases: cross-process serialization per
        |      provider|cwd; background jobs run in a detached worker that
        |      SURVIVES the MCP Host; dead-PID jobs fail instead of hanging
        |
        +--> Resume / Create path (one-shot CLI per turn)
        |      grok  --model X --reasoning-effort Y  -r <sessionId>
        |      codex exec --json --model X -c model_reasoning_effort=Y resume <id>
        |      claude -p --safe-mode (text-only child, no tools/MCP/hooks)
        |
        +--> PATH A Deliver path (owned live sessions)
               owner registry (DeleteOnClose lease = crash-safe staleness)
                    |
                    v
             run-owner daemon ---- spool: incoming\ -> processing\ -> receipts\
                    |               receipt written ONLY after the turn
                    v               completes (true delivery semantics)
             codex app-server (stdin kept open, per-turn model/effort,
                               kill-on-close job object, transport rebuild)

 --------------------------------------------------------------------------
 Separate read-only observation plane (never launches or modifies Claude):

 Claude Desktop files/process metadata
        |  read-only
        v
 cccg-monitor worker  <-- ready-before-stop handoff -->  next worker
        ^
        |  cccg-host supervisor (SHA-256 staged, immutable versions)
```

## Dispatch MCP

| Tool | Purpose |
|---|---|
| `cccg_list_peers` | List Grok / Codex / Claude sessions and bindings |
| `cccg_inspect_peer` | Title, model, cwd, writer state for one session |
| `cccg_watch_peers` | Snapshot a list of session ids and diff against the previous snapshot |
| `cccg_dispatch` | Queue a background job, return `jobId` immediately |
| `cccg_dispatch_wait` | Keep the call open and return the peer's answer |
| `cccg_job_status` / `cccg_job_collect` | Poll status / collect the normalized response |
| `cccg_read_transcript` | Read recent turns of any peer session (bounded, read-only; transcript text is untrusted data) |
| `cccg_search_transcripts` | Case-insensitive substring search across peer transcripts, newest-first, honestly bounded |
| `cccg_set_title` | Rename a closed session where a provider-safe write exists (currently honest `unsupported` everywhere — no provider has a safe rename contract) |
| `cccg_archive_peer` | Reversible move of a closed session into `cccg-archive\` with a hash manifest; archived sessions vanish from list/watch/search |
| `cccg_inbox_post` / `list` / `ack` | Shared cross-process mailbox |
| `cccg_runtime_status` | Active versioned worker and hot-update mode |

### Recursion guardrails

Any provider child spawned by CCCG carries `CCCG_HOP`; a new dispatch computes
`jobHop = processHop + 1` and fails closed above `CCCG_MAX_HOP` (default 2), so
A-calls-B-calls-A loops die deterministically before any provider starts.
Per-caller daily usage is always counted atomically in the local ledger and
resets at local midnight. Daily quota rejection is opt-in: setting
`CCCG_QUOTA_CLAUDE` or `CCCG_QUOTA_DEFAULT` to a positive integer enables that
provider limit; with neither variable set, dispatches are unlimited by count.
Every recursive (`hop >= 1`) dispatch posts a
`fromRole=system` audit line to the inbox — who called whom, from which cwd,
with which model — never the prompt text. Claude child sessions default to
text-only; `CCCG_CLAUDE_CHILD_MODE=tools` grants exactly
`--allowed-tools WebSearch,WebFetch` (still zero MCP, hooks, or slash
commands), so a child can browse the web while recursion stays impossible.

### Per-dispatch model selection

`cccg_dispatch` and `cccg_dispatch_wait` accept optional `model` and
`reasoningEffort` strings that apply to that turn only:

```text
cccg_dispatch(provider="codex", model="gpt-5.6-luna", reasoningEffort="xhigh", ...)
cccg_dispatch(provider="grok",  model="grok-4.6",     reasoningEffort="high",  ...)
```

Values pass straight through to the provider CLI (`--model`,
`-c model_reasoning_effort=` / `--reasoning-effort`) and, on the owner path, to
the per-turn app-server params. Omitted values keep the provider defaults.
There are no CCCG aliases: pass whatever the installed CLI accepts. Setting
either field with `provider=claude` fails closed. See
[dispatch](docs/dispatch.md).

### Delivery model

| Peer state | Behavior |
|---|---|
| CCCG-owned live session (PATH A) | Write into the owner spool; receipt only after the provider turn completes |
| Closed / resumable | Start the provider CLI with the existing session id; Grok resume is verified by a `num_messages` read-back |
| Live session **not** owned by CCCG | Fail closed with relaunch guidance (keyboard injection is deprecated) |
| Active Claude Desktop session | Route via Desktop's own `send_message`; never CLI-resumed |

### Hot update

The Host owns the MCP contract; every tool call re-resolves the Worker through
`worker-current.json` (SHA-256 verified, immutable per-version directories):

```powershell
.\scripts\install-dispatch-worker.ps1 -Version 0.6.0
```

Worker-only changes apply on the next tool call with no restart. Adding or
changing MCP tools/parameters is Host-schema and needs one MCP reconnect —
install order: Worker first, then Host, then reconnect.

## Build and test

```powershell
dotnet build .\src\CCCG.Dispatch.Worker\CCCG.Dispatch.Worker.csproj -c Release
dotnet build .\src\CCCG.Dispatch\CCCG.Dispatch.csproj -c Release
dotnet run --project .\tests\CCCG.Tests\CCCG.Tests.csproj -c Release   # 135 tests
```

(`CCCG.sln` also references the experiments tree; per-project builds are the
supported path for dispatch work.)

## Other planes

- **Monitor** — read-only tail of Claude Desktop lifecycle/session metadata,
  content capture off by default, supervised hot handoff via `cccg-host`. See
  [monitor](docs/monitor.md) and [hot-update](docs/hot-update.md).
- **Router (Luna experiment)** — deterministic vertical slice that maps a
  Claude model alias to Codex app-server with full provider disclosure;
  packaged, reversible, not auto-installed. See
  [Desktop Luna experiment](docs/desktop-luna-experiment.md) and
  [provider adapters](docs/provider-adapters.md).

## Docs

[dispatch](docs/dispatch.md) ·
[PATH A owned sessions](docs/path-a-owned-sessions.md) ·
[dispatch validation](docs/dispatch-validation.md) ·
[architecture](docs/architecture.md) ·
[monitor](docs/monitor.md) ·
[test plan](docs/test-plan.md) ·
[safety boundaries](docs/safety.md)

## Limitations

- PATH A owner transport is implemented for Codex (app-server). The Grok owner
  transport is a fail-closed stub until the ACP contract is wired; Grok
  model/effort apply to the resume/create CLI path only.
- The Luna route supports text turns, interrupt/error lifecycle, and
  attribution; it does not reproduce Claude tools, hooks, web search, or
  session resume semantics.
- Live sessions opened by the human outside CCCG cannot receive dispatches
  until relaunched through an owned path; the mailbox remains the fallback.
