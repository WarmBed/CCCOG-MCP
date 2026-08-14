# PATH A — CCCG-owned provider sessions

Status: implemented (Worker-only). Baseline `7cfddcf` (57 tests) → PATH A
(63 tests) → post-review hardening (69 tests, all green).

## Why

`cccg_dispatch` needed CCD `send_message` parity for Grok/Codex: deliver a
message to a RUNNING provider session so the receiving AI processes it as a
new turn, with no human watching a window. The old path for live grok/codex
peers was keystroke injection (`DispatchAction.Inject`,
`WindowsLiveInputInjector`): it could only prove "keystrokes were sent",
never "the provider consumed them", and it fought focus, IME, and window
state. PATH A replaces routing to it entirely.

**Delivery semantics: exactly-once-or-known-failure.** A job is `succeeded`
only when the provider actually ran the message as a turn and produced a
reply; `succeeded` is persisted the moment the delivered receipt is recorded,
before any inbox bookkeeping, so a delivered turn can never be relabeled
`failed` afterwards (empty replies post the placeholder `(empty response)`).
A message is executed by the provider **at most once**: the owner claims it
into `processing\` before the turn, so an owner that dies mid-turn produces a
`failed` receipt with *unknown outcome* on restart instead of replaying the
turn. Every other outcome is `failed` with a diagnostic that says whether the
message was consumed or its outcome is unknown.

## Architecture

```text
Claude (MCP cccg_dispatch)
  └─ dispatch worker  ──选路──  PeerSelector / DispatchRunner.Select
        │  owner registry entry live?  ── yes ──►  DispatchAction.Deliver
        │                                              │ write spool message
        ▼ no                                           ▼ poll for receipt
     resume / create paths (unchanged)          owner daemon (run-owner)
                                                  │ holds DeleteOnClose lease
                                                  │ provider child, stdin OPEN
                                                  │ spool → provider turn
                                                  ▼
                                             receipt (delivered|failed)
```

### Owner daemon

A new mode of the existing worker executable — no new binary, no new install
script; the `scripts\install-dispatch-worker.ps1` / `worker-current.json`
hot-install pipeline covers it:

```text
cccg-dispatch-worker.exe run-owner --provider codex --cwd D:\code\x
    [--session-id <codex-thread-id>] [--model gpt-5.6-luna] [--effort medium]
```

On start it prints one JSON line: `{ok, mode, provider, sessionId, cwd,
ownerPid, spoolDir}`. It then:

1. acquires a **workspace lease** (`key = sha256(provider|cwd|_)`) — one
   workspace gets exactly one owner, so a second `run-owner` on the same
   provider+cwd fails fast with a clear message even when the per-session
   keys differ (workspace mode uses a synthetic GUID session id);
2. acquires the **per-session DeleteOnClose ownership lease** (same pattern
   as `CodexThreadBindingStore.AcquireLease`) — process death releases both
   leases automatically, so a dead owner can never look alive;
3. sweeps its spool's `processing\` directory: messages a previous owner
   claimed but never receipted get a `failed` receipt with
   `unknown outcome: the previous owner died mid-turn` and are **not**
   re-run;
4. registers itself in the **owner registry** (below);
5. keeps a stateful provider transport with **stdin kept open**
   (`IProviderTurnTransport`); the provider child sits in a best-effort
   Windows Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so a
   hard-killed owner takes the child down instead of orphaning it;
6. tails its spool (~250 ms poll), claims each message into `processing\`,
   runs it as a provider turn prefixed with the **single** sender label
   `[CCCG message from <fromRole> <fromSessionId>]` (Deliver spools the raw
   prompt — the legacy `[CCCG dispatch from ...]` header never reaches an
   owned provider), and writes a **receipt only after the turn completed**.

**Transport rebuild + exit policy.** A transport-level failure (codex
app-server death, broken pipe) tears the transport down; the next spooled
turn spawns a fresh app-server and resumes the bound thread. After 3
consecutive transport failures (`OwnerDaemon` `transportFailureLimit`,
counter resets on success) the daemon exits with code 5, releasing both
leases and the registration so dispatch immediately falls back to the resume
path instead of spooling into a lease-holding black hole. Provider errors on
a healthy transport write `failed` receipts but never trigger the exit.

`Ctrl+C` stops it cleanly (unregisters + releases the leases); a hard kill
releases the leases via DeleteOnClose and leaves a stale registration file
that the registry ignores and deletes on the next lookup.

### Owner registry

`%LOCALAPPDATA%\CCCG\dispatch\owners\`

| File | Meaning |
|---|---|
| `<key>.json` | registration, written via `CrossProcessFileGate.AtomicWriteAllText` |
| `<key>.lock` | DeleteOnClose ownership lease; held ⇔ owner alive |
| `<workspace-key>.lock` | workspace lease (`sessionId = "_"`); enforces one owner per provider+cwd |
| `<key>\spool\` | that owner's delivery queue |

`key = sha256(provider \| normalized-cwd \| sessionId)` (lowercase hex).

Registration schema (schemaVersion 1):

```json
{
  "schemaVersion": 1,
  "provider": "codex",
  "sessionId": "<provider-native id, updated after the first turn>",
  "cwd": "D:\\code\\x",
  "ownerPid": 9160,
  "spoolDir": "...\\owners\\<key>\\spool",
  "startedAt": "2026-08-15T...Z"
}
```

Stale detection mirrors `CodexPeerDirectory.LockIsHeld`: a registration
counts only while its lease file exists **and** is held (the probe opens
read-only with permissive sharing so it cannot break a concurrent legitimate
`AcquireLease`, which additionally retries once).
`OwnerRegistry.TryFind(provider, sessionId, cwd)` matches by session id when
one is given, else by workspace, and never returns a stale entry; stale
registrations it scans past are deleted, along with their per-key directory
when it holds no files (spools with pending messages or receipts are kept).

If the daemon starts without `--session-id` it registers a synthetic GUID and
**rewrites the registration with the provider-native id** (Codex thread id)
after the first turn completes (`OwnerDaemon.RefreshSessionIdentity`). Until
then the session is addressable by `cwd`.

### Spool and receipts

- `<spoolDir>\incoming\<yyyyMMddTHHmmssfff>_<messageId>.json` =
  `{messageId, fromRole, fromSessionId, text, createdAt, model?, reasoningEffort?}`
- `<spoolDir>\processing\` — the message currently (or last) being run; a
  leftover here after a crash means the turn outcome is unknown
- `<spoolDir>\receipts\<messageId>.json` =
  `{messageId, status: delivered|failed, deliveredAt, responseText, error}`

The owner consumes incoming files in filename order: it **claims** the file
into `processing\`, runs the provider turn, writes the receipt, then deletes
the processing file. A receipt with `status=delivered` is the **only**
success signal; it is written strictly after the provider turn returned.
A failed turn produces a `failed` receipt (the message is consumed, not
retried — the dispatcher surfaces the error and the sender decides). A crash
between claim and receipt yields an unknown-outcome `failed` receipt at the
next startup, never a replay.

### Dispatch integration

- New `DispatchAction.Deliver` in `PeerSelector`.
- `DispatchRunner.Select` consults the owner registry **first**: a live owner
  for the requested session/workspace wins even before peer-directory
  visibility (a freshly started owner has no rollout on disk yet).
- `PeerSelector.Select` takes an optional `hasLiveOwner` probe: explicit live
  grok/codex peers route to `Deliver` when owned; auto-selection only picks a
  live-idle grok/codex peer when it is owned, otherwise falls through to
  resume/create.
- `Deliver` in `DispatchRunner`: posts the job's **raw** prompt
  (`prompt.raw.txt`, no dispatch header) into the spool (`messageId =
  jobId`) together with any per-turn `model` / `reasoningEffort`, then polls
  for the receipt (250 ms default, 2 h timeout like every other wait). The
  owner is re-resolved by session id **and** by workspace so
  a concurrent `RefreshSessionIdentity` swap cannot fake a
  "released its lease" failure. During the wait, owner liveness is the
  **lease** (`OwnerRegistry.LeaseIsHeld` on the entry's key), not the PID —
  PID reuse could otherwise keep a dead owner "alive" for the whole timeout.
  `succeeded` **only** on `receipt.status == delivered`, persisted before the
  inbox post; `responseText` is written to the job's `stdout.log`, so
  `cccg_job_collect` works unchanged. If the owner's lease releases without a
  receipt the job fails fast with an unknown-outcome diagnostic. Deliver does
  not take the per-workspace dispatch lease — the owner serializes its own
  turns (`PersistentCodexAppServerClient` turnGate).
- New `DispatchJob` fields, all `JsonPropertyName` + null-ignored (old JSON
  keeps deserializing): `ownerPid`, `receiptStatus`, `deliveredAt`,
  `peerTurnsBefore`, `peerTurnsAfter`.

### Inject deprecation

`PeerSelector` no longer ever returns `Inject` for grok/codex:

- live peer **with** owner → `Deliver`;
- live peer **without** owner (explicitly targeted) → clear failure:
  *"The live &lt;provider&gt; session is not CCCG-owned; CCCG no longer types
  into live windows. Relaunch it via 'cccg-dispatch-worker.exe run-owner
  --provider &lt;provider&gt;' or close it to use resume."*;
- auto-selection skips unowned live peers and falls through to
  resume/create.

`ILiveInputInjector`, `RecordingLiveInputInjector`,
`WindowsLiveInputInjector`, and the `DispatchRunner` injector ctor slot are
kept (types and tests depend on them); `LiveInputInjector.cs` is unedited.
The live-Claude guards (`DispatchRunner` / `PeerSelector`) are preserved
verbatim.

### Grok resume read-back verification

Even the legacy resume path now proves consumption for grok: before a
`resume` job runs, the runner records the session's `num_messages` from the
grok summary (`peerTurnsBefore`); after a zero-exit run it re-reads with a
tolerant retry window (default 5 s, summary files may lag) and fails the job
with a diagnostic if the count did not increase (`peerTurnsAfter` recorded
either way). Sessions whose summary carries no message count skip the check.

## Provider transports

`IProviderTurnTransport` (Core/Providers): `SessionId` (provider-native id
once known) + `RunTurnAsync(text)` returning the reply. The only pre-existing
keep-stdin-open precedent, `ProcessJsonLineTransport`, sits underneath.

- **codex — implemented.** `CodexOwnerTurnTransport` wraps the
  previously-unused `PersistentCodexAppServerClient` (`codex app-server`
  JSON-RPC): turn/write gates, pending-message queue, thread start/resume,
  fail-closed on server-initiated approvals. `--session-id` pre-binds the
  thread so the client resumes it. Model resolution is per-turn spool
  override → owner process default (`--model` → `CCCG_OWNER_CODEX_MODEL` →
  `gpt-5.6-luna`, the locally verified backend in
  `config/routes.luna.json`). Reasoning resolution is per-turn
  `reasoningEffort` → owner process default (`--effort` →
  `CCCG_OWNER_CODEX_EFFORT` → `medium`). Per-turn values do not mutate the
  process defaults. `CodexAppServerClient` (live router code) is untouched.
- **grok — cleanly stubbed.** `docs/provider-adapters.md` names
  `grok agent stdio` (ACP) as the entrypoint but does not document the ACP
  message schema, and CCCG does not guess wire protocols.
  `UnimplementedGrokTurnTransport` fails closed with that reason, and
  `run-owner --provider grok` exits 3 before accepting any message.
  **TODO:** capture the real ACP handshake/turn frames (or the published ACP
  spec grok-build conforms to), then implement the transport alongside
  `CodexOwnerTurnTransport`'s shape; nothing else changes — registry, spool,
  selection, and receipts are provider-agnostic.

## Worker and Host schema boundary

The owner registry, spool, transport, and receipt mechanics live in
`CCCG.Core` + `CCCG.Dispatch.Worker` and ride the Worker hot-install. The
per-dispatch model batch also changes the Host schema: `cccg_dispatch` /
`cccg_dispatch_wait` expose `model` and `reasoningEffort`, and
`cccg_watch_peers` exposes snapshot/diff state. Install Worker first, then
Host, then reconnect the MCP server once.

Still deferred:

- a first-class `cccg_owner_*` tool family (start/stop/list owners) instead
  of launching `run-owner` by hand;
- supervising owners under `CCCG.Host`'s worker supervisor (restart, staged
  upgrade) — today an owner dies with its console;
- surfacing owner registry state in `cccg_list_peers` rows (an `owned` flag)
  rather than only via selection behavior;
- sender identity richer than `fromRole=claude` (per-session `fromSessionId`
  passthrough over MCP).

## Deviations from the brief

1. **Five Inject-era tests were rewritten, not preserved verbatim.** The
   brief requires both "all 57 existing tests still pass" and "Inject is no
   longer selected for grok/codex"; the five tests that asserted
   Inject-selection semantics contradict the second requirement, so they now
   assert the replacement behavior in the same scenarios (skip-unowned /
   deliver-owned / fail-closed / deliver-skips-lease / owner roundtrip).
   Every other pre-existing test is untouched and green.
2. **Owner registry is checked before peer directories** in selection (the
   brief only required routing when "the target peer has a live owner
   registry entry"). Reason: a freshly started owner's provider session may
   not be visible as a peer yet; the registry lease is the stronger, already
   race-safe signal.
3. **Grok owner transport is the documented stub** (allowed by the brief when
   the ACP contract is too thin) — see TODO above.
4. **`sessionId` in the registry starts synthetic** when `--session-id` is
   omitted and is replaced by the provider-native id after the first turn;
   the brief's schema is otherwise unchanged.

## Test map

| Behavior | Test |
|---|---|
| registry write + stale detection | `owner registry registers and detects a stale owner` |
| stale registration cleanup, non-empty spools kept | `owner registry cleans stale registrations but keeps non-empty spools` |
| spool → receipt roundtrip (fake transport) | `owner daemon turns spooled messages into receipts` |
| failed turn → failed receipt | `owner daemon writes a failed receipt when the provider turn fails` |
| Chinese text byte-exact through spool/turn/receipt | `owner spool preserves Chinese text byte-exactly through the turn roundtrip` |
| crashed owner's claimed turn → unknown-outcome receipt, no replay | `owner daemon fails abandoned processing turns as unknown outcome without re-running` |
| one owner per workspace | `second owner daemon in the same workspace fails fast` |
| transport failure limit → clean exit + lease release | `owner daemon exits after repeated transport failures so the lease releases` |
| Deliver selection when lease held | `peer selector delivers to an explicit owned live peer and fails closed otherwise` |
| owner lease releases mid-delivery → fast unknown-outcome failure | `dispatch deliver fails when the owner dies mid-delivery` |
| empty provider reply → still succeeded + inbox placeholder | `dispatch deliver succeeds and posts a placeholder for an empty provider reply` |
| Inject never selected for grok/codex | `dispatch runner refuses keystroke injection for an unowned live window` |
| Deliver ignores workspace lease | `dispatch runner deliver does not wait for a held workspace lease` |
| end-to-end deliver via owner daemon (single sender label) | `dispatch runner delivers through the owner daemon to a live-idle peer` |
| read-back pass / fail | `grok resume read-back confirms the recorded turn` / `... fails when no turn is recorded` |

## Build, test, install

```powershell
dotnet build src/CCCG.Dispatch.Worker/CCCG.Dispatch.Worker.csproj -c Release
dotnet build src/CCCG.Dispatch/CCCG.Dispatch.csproj -c Release
dotnet run --project tests/CCCG.Tests/CCCG.Tests.csproj -c Release   # 85/85

# hot-install the worker (unchanged pipeline; bump the version)
powershell -File scripts\install-dispatch-worker.ps1 -Version 0.6.0

# start an owned codex session for a workspace
cccg-dispatch-worker.exe run-owner --provider codex --cwd D:\code\x
```

Do not build `CCCG.sln` in this copy — it references an absent
`experiments\` project; build per-project as above.
