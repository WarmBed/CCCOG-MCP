# CCCOG Bar v2 — slim companion bar (Flow + Quota only)

Status: spec approved by operator 2026-08-16. Implementation by Claude agents.

## Product decision (operator)

Derive from TokenBar-Windows (author is a known contact; derivation approved and
this repo stays public — record attribution properly anyway) a **minimal
companion bar** that ships inside CCCOG-MCP. Exactly two surfaces, nothing else:

1. **Quota cards** — Claude / Codex / Grok "how full is my plan" bars
2. **Flow list** — who is controlling whom right now (the accepted TokenBar
   Flow-tab UX)

The old standalone `bar/app/CCCOG.Bar.App` prototype is retired and replaced.
TokenBar itself remains the full analytics product; this bar deliberately does
NOT parse usage history — that is what makes it instant.

## Why slim = fast (the core insight)

TokenBar's 2–3 minute first snapshot comes from parsing the full multi-billion
token usage history for charts. Flow + quota need none of that:

- Flow: `%LOCALAPPDATA%\CCCG\dispatch\{jobs,owners}` — milliseconds
  (`cccog-bar-core`, already tested)
- Quota: three small local/HTTP reads (codex rollout tail; Claude oauth/usage;
  Grok billing) — async with stale badges

**Performance contract: window visible with Flow data < 1 s cold; quota cards
arrive async; no operation may block the UI on history parsing (there is none).**

## Architecture (reuse, not rebuild)

```
bar/
  crates/cccog-bar-core     # EXISTS — flow snapshot core (29 tests)
  crates/cccog-bar-quota    # NEW — port of TokenBar quota agents:
                            #   codex: local rollout rate_limits (file-only)
                            #   claude: GET api.anthropic.com/api/oauth/usage
                            #           (+ platform.claude.com token refresh; 403 => honest unavailable)
                            #   grok: GET cli-chat-proxy.grok.com/v1/billing?format=credits
                            # injected HTTP client; >=60s poll; stale on failure;
                            # credentials read-only; nothing sent out
  crates/cccog-bar-ffi      # EXISTS — extend envelope with quota cards
  app/CCCOG.Bar.App         # REPLACED — TokenBar-style shell:
                            #   tray icon, left-click toggles Mica/Acrylic flyout (~500px)
                            #   section 1: quota cards; section 2: Flow rows
                            #   NO tabs, charts, settings pages, 3D, history
```

Vendored/derived TokenBar files carry a `SYNC.md` with upstream repo, commit,
date, allowlist, and MIT attribution (same discipline as TokenBar's own vendor
rules).

## Locked UX (carry over exactly; operator已驗收)

- Flow rows: `[status dot] source → target chain … elapsed | ×N` — chain in one
  line (`doc1 → codex → claude`); active first, aggregated terminals after;
  hover tooltip = bounded task summary; `callerLabel` when present, `unknown`
  fallback rendered低調灰.
- Provider colors: claude `#da7756` orange · codex `#3b82f6` blue · grok
  `#1f2937` gray-black. Status = dot brightness/pulse, never hue override.
- Quota cards: name + thin progress bar + `NN% used` + reset time; failures show
  the concrete reason (e.g. `HTTP 401`, `token refresh rejected`), never a vague
  "unavailable".

## Lessons-learned requirements (from v1 acceptance, all mandatory)

1. **Never hide a section while loading** — show the section with a loading
   line instead (operator hit this confusion live).
2. **Stale-queued handling** — a job queued > 6 h renders collapsed under a
   `stale ×N` group with gray badge, not as forever-"active".
3. **Crash log from day one** — keep the `bar-crash.log` UnhandledException
   handler (it found the real root cause once already); all background→UI
   updates via DispatcherQueue (the 0xc000027b lesson).
4. UI-thread init gate before any SelectionChanged/refresh path runs.

## TDD gates

- cccog-bar-quota: fixture tests per provider incl. 401/403/timeout/malformed +
  refresh flow with injected client (no real network in tests).
- FFI envelope: quota+flow combined snapshot, versioned, deterministic.
- Shell: view-model tests where the harness allows; 90-second real-run
  self-check (window <1s, quota async arrival, no crash log) is a hard gate.
- Existing 29 core tests stay green. Rust release + WinUI x64 builds 0 error.

## Out of scope

Usage/token history, charts, cost, model breakdowns, settings beyond
poll-interval, macOS. TokenBar upstream keeps those.
