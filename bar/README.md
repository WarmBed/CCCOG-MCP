# CCCOG-Bar workspace

CCCOG Bar v2 is a slim Windows companion bar: a tray icon whose left click
toggles a small Acrylic flyout — TokenBar-Windows' own window chrome and card
visuals, ported verbatim (see `app/CCCOG.Bar.App/*.cs` doc comments and
[`SYNC.md`](SYNC.md)) — showing exactly two things — **provider quota** (Claude / Codex / Grok, "how full is my plan") and **flow** (who is
controlling whom right now, from `cccog-bar-core`'s dispatch-root snapshot).
No tabs, charts, settings pages, 3D, or usage-history parsing — see
[`docs/plans/2026-08-16-cccog-bar-v2-slim.md`](../docs/plans/2026-08-16-cccog-bar-v2-slim.md)
for the full spec this shipped against.

## Why slim = fast

Full token/cost history analytics (the TokenBar-Windows product this was
derived from) parses a multi-billion-token usage log for its charts, which is
where its multi-minute first snapshot comes from. CCCOG Bar v2 deliberately
carries none of that:

- **Flow**: reads `%LOCALAPPDATA%\CCCG\dispatch\{jobs,owners}` only —
  bounded, file-only, milliseconds (`cccog-bar-core`, unit-tested).
- **Quota**: three small local/HTTP reads (Codex rollout tail file; Claude
  `oauth/usage` + refresh; Grok billing) — async, arrives after the window is
  already visible, with an explicit stale/unavailable badge and a concrete
  failure reason on error (never a vague "unavailable").

**Performance contract:** the flyout is visible with flow data in well under
1 second cold (measured 413-574ms across repeat real launches on real
dispatch data); quota cards populate asynchronously afterward; no code path
in the shell parses usage history.

## Layout

```
bar/
  crates/cccog-bar-core     # flow snapshot core: dispatch-root scanner,
                             # graph reducer, token attribution. No network,
                             # no Windows dependency. (18 tests)
  crates/cccog-bar-quota    # provider quota agents behind an injected HTTP
                             # client: Codex local rollout rate_limits
                             # (file-only), Claude oauth/usage + platform
                             # .claude.com token refresh, Grok cli-chat-proxy
                             # billing. Concrete failure reasons only
                             # ("HTTP 401", "token refresh rejected", ...).
                             # (14 tests)
  crates/cccog-bar-ffi      # stable C ABI: versioned flow+quota envelope,
                             # background quota poller with a hard 60s floor.
                             # (7 tests)
  crates/cccog-bar-cli      # debug CLI: prints the envelope for a dispatch
                             # root, optionally polling quotas.
  app/CCCOG.Bar.App         # the WinUI 3 shell: tray icon, Acrylic flyout
                             # (~500px, TokenBar's DesktopAcrylicController
                             # chrome verbatim), quota-card section, flow-row
                             # section.
```

Quota logic is a minimal, reviewed derivation from TokenBar-Windows
(`crates/tb_core_ffi/src/agent_usage.rs`, `agent_grok.rs`) — endpoints,
field names, and the Claude refresh `client_id` were cross-checked against
that source, not bulk-copied. TokenBar-Windows itself remains the full
analytics product; this bar intentionally stays smaller.

## Locked UX

- **Quota cards**: name, thin progress bar, `NN% used`, reset time. A failed
  card still renders — with the concrete reason, never hidden.
- **Flow rows**: `[status dot] source → target chain … elapsed | ×N` — active
  jobs first (most recent), then a single collapsed gray `stale ×N` group for
  jobs queued/running past 6 hours (never rendered as forever-"active"), then
  aggregated terminal rows, then live PATH A owner rows. Hover shows the
  bounded task summary. An unresolved caller (`callerLabel` absent) renders
  in a muted gray, not white.
- **Provider colors are hue-only**: claude `#da7756`, codex `#3b82f6`, grok
  `#1f2937`. Status is communicated by dot brightness/pulse — a provider's
  color never changes meaning based on state.
- Sections never disappear while loading; a "Loading quota…"/"Loading
  flow…" line stands in until data arrives.
- `bar-crash.log` (`UnhandledException` handler) ships from day one, and
  every background→UI update crosses through `DispatcherQueue.TryEnqueue`.

## Scope boundary

The core reports explicit token counts and provider quota windows only.  It
has no cost fields, money arithmetic, pricing table, LiteLLM/OpenRouter
dependency, or inference request.  Quota HTTP polling is isolated behind an
injected read-only client; prompts, transcripts, paths, and credentials are
never sent as request data and credentials are never written back.

## Building

```powershell
cd bar
cargo test --workspace                 # 39 tests across core/quota/ffi
cargo build --release --workspace      # produces target/release/cccog_bar_ffi.dll

cd ..
dotnet build bar/app/CCCOG.Bar.App/CCCOG.Bar.App.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

The csproj copies `cccog_bar_ffi.dll` from `target/release` next to the
built exe automatically when it exists, so build the Rust workspace in
release mode first.

## Out of scope

Usage/token history, charts, cost, model breakdowns, settings beyond
poll-interval, macOS. TokenBar-Windows upstream keeps those.
