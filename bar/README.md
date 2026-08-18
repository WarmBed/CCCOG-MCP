# CCCOG-Bar workspace

CCCOG Bar v2 is a slim Windows companion bar: a tray icon whose left click
toggles a small Acrylic flyout — TokenBar-Windows' own window chrome and card
visuals, ported verbatim (see `app/CCCOG.Bar.App/*.cs` doc comments and
[`SYNC.md`](SYNC.md)) — showing exactly two things — **provider quota**
(Claude / Codex / Grok, "how full is my plan") and **flow** (a live tree of
who is controlling whom right now: local Claude Code sessions and their
subagents, plus CCCG dispatch jobs, combined by `cccog-bar-core`). No tabs,
charts, settings pages, 3D, or usage-history parsing — see
[`docs/plans/2026-08-16-cccog-bar-v2-slim.md`](../docs/plans/2026-08-16-cccog-bar-v2-slim.md)
for the full spec this shipped against.

## Why slim = fast

Full token/cost history analytics (the TokenBar-Windows product this was
derived from) parses a multi-billion-token usage log for its charts, which is
where its multi-minute first snapshot comes from. CCCOG Bar v2 deliberately
carries none of that:

- **Flow**: three bounded, file-only local reads, combined into one tree —
  `%LOCALAPPDATA%\CCCG\dispatch\{jobs,owners}` (CCCG dispatch jobs/owner
  leases), `~/.claude/projects` (live local Claude Code session transcripts
  and their Task-tool subagents, via a stat-sweep + skip-parse-shortcut
  tailer — TokenBar's own `usage_tail.rs` mechanism, adapted), and
  `%LOCALAPPDATA%\CCCG\presence` (optional heartbeat files a hook can write
  per turn, for near-instant 執行中/閒置 state instead of waiting on the
  next scan). All three are milliseconds, unit-tested, and never touch the
  network. See [`SYNC.md`](SYNC.md)'s 2026-08-16/17 sections for the full
  derivation record (why a second data source was added, the tree-topology
  rules, and two real bugs a live run caught and fixed along the way).
- **Quota**: three small local/HTTP reads (Codex rollout tail file; Claude
  `oauth/usage` + refresh; Grok billing) — async, arrives after the window is
  already visible, with an explicit stale/unavailable badge and a concrete
  failure reason on error (never a vague "unavailable"). A quota-limit
  observation (a dispatch job that failed with a usage-limit rejection)
  persists on the card until its own parsed reset time passes, independent
  of whether the evidence job is still around to re-scan — see `SYNC.md`'s
  task-6 section for the persistence mechanism and task-9 section for the
  card's exact one-row rendering.

**Performance contract:** the flyout is visible with flow data in well under
1 second cold (measured 413-574ms across repeat real launches on real
dispatch data); quota cards populate asynchronously afterward; no code path
in the shell parses usage history. Once open, Flow also refreshes on its own
— a 60s timer plus file watchers on the dispatch root and the presence dir —
so it never sits frozen on stale data while the flyout is open (see
`SYNC.md`'s task-5 section for the bug this fixed: zero CCCG dispatch
activity used to mean zero Flow refreshes, for hours, even while real
cross-session activity kept happening).

## Layout

```
bar/
  crates/cccog-bar-core     # flow tree core, no network/Windows dependency:
                             #   claude_sessions — the transcript tailer
                             #     (stat-sweep + skip-parse shortcut) and
                             #     per-session/agent summaries
                             #   presence      — heartbeat-file parsing and
                             #     the 執行中/閒置/unknown tri-state resolver
                             #   tree          — combines sessions + presence
                             #     + dispatch jobs/owners into the two-level
                             #     Flow tree (EdgeRules, pinned coordinators)
                             #   control, dispatch, graph, owners, refresh,
                             #     privacy, inbox — the CCCG dispatch-root
                             #     scanner/reducer this all grew from
                             # (95 tests: lib + graph/parsers/refresh/usage
                             # integration suites)
  crates/cccog-bar-quota    # provider quota agents behind an injected HTTP
                             # client: Codex local rollout rate_limits
                             # (file-only), Claude oauth/usage + platform
                             # .claude.com token refresh, Grok cli-chat-proxy
                             # billing. Concrete failure reasons only
                             # ("HTTP 401", "token refresh rejected", ...).
                             # (35 tests)
  crates/cccog-bar-ffi      # stable C ABI: versioned flow+quota envelope,
                             # background quota poller with a hard 60s floor,
                             # the persisted quota-limit cache, flow.edgeRules
                             # / flow.coordinators settings loading.
                             # (36 tests)
  crates/cccog-bar-cli      # debug CLI: prints the envelope for a dispatch
                             # root, optionally polling quotas; --edge-rules
                             # / --coordinators override the Flow tree's
                             # control-edge rules for one run (see below).
  app/CCCOG.Bar.App         # the WinUI 3 shell: tray icon (custom, see
                             # Assets/ + bar/tools/generate_tray_icon.py),
                             # Acrylic flyout (~500px, TokenBar's
                             # DesktopAcrylicController chrome verbatim),
                             # quota-card section, flow-tree section.
```

Quota logic is a minimal, reviewed derivation from TokenBar-Windows
(`crates/tb_core_ffi/src/agent_usage.rs`, `agent_grok.rs`) — endpoints,
field names, and the Claude refresh `client_id` were cross-checked against
that source, not bulk-copied. The Flow transcript tailer is a similar
derivation from TokenBar's `usage_tail.rs` (stat-sweep candidate filtering,
event-timestamp-based liveness, the skip-parse shortcut) — see `SYNC.md`'s
2026-08-16 section for exactly what was reused vs. written from scratch.
TokenBar-Windows itself remains the full analytics product; this bar
intentionally stays smaller.

## Locked UX

- **Quota cards**: name, thin progress bar, `<window> <NN%>` bold on the
  left with the reset date/time dim on the right, one condensed status line
  when stale or failed. A failed card still renders — with the concrete
  reason, never hidden. **Exception, by design (task 9, operator direction):**
  a persisted quota-limit card (see below) renders as ONLY the window row +
  gauge, no status line — the lock is the app's own current belief, not a
  fallback annotation on old data, so nothing needs flagging as stale; the
  full evidence (when the limit was observed, the exact reset instant)
  stays available as a hover tooltip on that row.
- **Flow tree**: a flattened, depth-tagged list — depth 0 is a top-level
  controller (a live Claude session, or a bottom-of-list `terminal`/
  `resumable` aggregate for finished dispatch jobs and non-live owner
  leases), depth 1 is its children (`agent` — a Task-tool subagent;
  `session` — another Claude session reached via a cross-session dispatch
  message; `codex`/`grok` — an active CCCG dispatch job). Every row shows
  `<title> (<kind> · <model>) <state> <elapsed>`; state is always one of
  three words, 執行中 (running) / 閒置 (idle) / terminal-or-resumable,
  regardless of row kind. A session with an inbound dispatch from another
  live session nests under it instead of also showing top-level — see
  `SYNC.md`'s task-4 section for the control-edge rules that decide who
  controls whom (and [Flow control-edge configuration](#flow-control-edge-configuration)
  below for tuning them).
- **Provider colors are hue-only**: claude `#da7756`, codex `#3b82f6`, grok
  `#1f2937`. Status is communicated by dot brightness/pulse — a provider's
  color never changes meaning based on state.
- Sections never disappear while loading; a "Loading quota…"/"No live
  sessions or recent jobs" line stands in until data arrives.
- `bar-crash.log` (`UnhandledException` handler) ships from day one, and
  every background→UI update crosses through `DispatcherQueue.TryEnqueue`.

## Flow control-edge configuration

Two optional keys in `%LOCALAPPDATA%\CCCG\bar-settings.json` (the same file
`LocalSettings.cs` already owns for the resize-grip height — both new keys
keep its flat `Dictionary<string,string>` shape; each value is itself a
JSON-encoded string, read fresh by the Rust FFI layer on every refresh, not
through that class's own in-memory cache, so a hand-edit takes effect on the
next Flow tick with no app restart):

```json
{
  "resize.grip.height": "8",
  "flow.edgeRules": "{\"initiatorWins\":true,\"markerOnly\":true}",
  "flow.coordinators": "[\"Doc1 主管\"]"
}
```

- **`flow.edgeRules`** — `initiatorWins`: within a session pair, only
  whoever sent the chronologically first in-window message can ever become
  the other's controller (a reply, even one that merely looks like a
  dispatch, can never create or flip a control edge). `markerOnly`: only a
  message whose body starts with a dispatch marker (`[派工`, `【任務`,
  `[任務`, `[assign`, `[dispatch`, `【派工`) is even a candidate edge — a
  bare conversation never restructures the tree. Both default `false`
  (latest-edge-wins, the original behavior); **the operator's own current
  default going forward is `{"initiatorWins":true,"markerOnly":true}`** —
  only a dispatch-marked message from the pair's actual initiator can ever
  create an edge.
- **`flow.coordinators`** — an array of exact session titles that always
  render top-level and never as anyone's child, and whose own dispatch
  edges win over a non-pinned controller's for the same child regardless of
  recency. Absent/empty = no pinned coordinators.

`cccog-bar-cli --control` exposes the same two rules as flags, for comparing
configurations against real transcripts without touching the settings file
or restarting the app:

```powershell
cargo run -p cccog-bar-cli -- --control --edge-rules none
cargo run -p cccog-bar-cli -- --control --edge-rules initiator
cargo run -p cccog-bar-cli -- --control --edge-rules marker
cargo run -p cccog-bar-cli -- --control --edge-rules initiator,marker
cargo run -p cccog-bar-cli -- --control --edge-rules initiator,marker --coordinators "Doc1 主管"
```

Either flag, even `--edge-rules none`, overrides the settings file for that
one run; omitting both falls through to whatever `bar-settings.json` says,
matching the app's own path. See `SYNC.md`'s task-4 section for the full
rule semantics, the real bug this fixed (a standing coordinator session
rendering as a child of one of its own reports), and why the three rules
are independently toggleable rather than one fixed design.

## Scope boundary

The core reports explicit token counts and provider quota windows only.  It
has no cost fields, money arithmetic, pricing table, LiteLLM/OpenRouter
dependency, or inference request.  Quota HTTP polling is isolated behind an
injected read-only client; prompts, transcripts, paths, and credentials are
never sent as request data and credentials are never written back. The Flow
transcript tailer only ever reads `~/.claude/projects` locally — no network
call, no external service — and only extracts the handful of fields
documented in `claude_sessions.rs`'s module doc comment (timestamps, model,
title, and the `name`/marker-prefix of an inbound cross-session message);
full message content is never parsed, stored, or displayed.

## Building

```powershell
cd bar
cargo test --workspace                 # 166 tests across core/quota/ffi
cargo build --release --workspace      # produces target/release/cccog_bar_ffi.dll

cd ..
dotnet build bar/app/CCCOG.Bar.App/CCCOG.Bar.App.csproj -c Release -p:Platform=x64
```

The csproj copies `cccog_bar_ffi.dll` from `target/release` next to the
built exe automatically when it exists, so build the Rust workspace in
release mode first. It also copies `Assets/tray-icon.ico` (regenerate it
with `python bar/tools/generate_tray_icon.py` if you ever change the
design — the `.ico` is generated from that script, never hand-edited).

**Do not pass `-p:RuntimeIdentifier=win-x64` on this command** (task 11,
2026-08-17, live-confirmed footgun): the csproj produces TWO independently
launchable copies of the app — the base
`bin\x64\Release\net10.0-windows10.0.19041.0\CCCOG.Bar.App.exe` and the
RID-specific `...\win-x64\CCCOG.Bar.App.exe` — and its own
`SyncWinX64RidOutput` target only cascades a base (RID-less) build INTO
win-x64, never the reverse. Passing `-p:RuntimeIdentifier=win-x64`
explicitly builds ONLY that one folder and silently leaves the base folder
stale; whichever shortcut/habit launches the base exe will keep running
old binaries indefinitely with no error or version warning. The RID-less
command above updates both (verify with a hash/mtime check on
`cccog_bar_ffi.dll` in both folders if in doubt — they should always
match after a build). Only pass `-p:RuntimeIdentifier=win-x64` for a
deliberate win-x64-only smoke build, never as the routine command.

## Out of scope

Usage/token history, charts, cost, model breakdowns, settings beyond
poll-interval, macOS. TokenBar-Windows upstream keeps those.
