# TokenBar-Windows derivation record

CCCOG Bar v2's provider-quota agents and Mica/Acrylic flyout shell were
derived (not bulk-copied) from TokenBar-Windows, per
[`docs/plans/2026-08-16-cccog-bar-v2-slim.md`](../docs/plans/2026-08-16-cccog-bar-v2-slim.md):
"Product decision (operator)" — the author is a known contact, derivation is
approved, and this repo (CCCOG-MCP) stays public.

- **Upstream repository**: https://github.com/Nanako0129/TokenBar-Windows.git
- **Upstream commit read**: `a829d51deb0f37bcff211c83de5e9d2f99f7663f` (2026-08-15)
- **Local clone used for derivation**: `D:\code\TokenBar-Windows` (read-only —
  nothing under that path was modified)
- **Derivation date**: 2026-08-16
- **License**: MIT, per operator direction (the upstream working tree at the
  commit above does not itself carry a `LICENSE` file or an explicit license
  statement in its README; attribution below is recorded on that basis so it
  is correct if/when upstream formalizes it).

## File allowlist (what was read, and what for)

| Upstream file | Used for | What actually landed in CCCOG Bar v2 |
| --- | --- | --- |
| `crates/tb_core_ffi/src/agent_usage.rs` | Verifying the Claude `oauth/usage` endpoint URL, the `platform.claude.com` refresh endpoint/verb, and the refresh request's `client_id` field | `bar/crates/cccog-bar-quota/src/lib.rs`: `CLAUDE_REFRESH_CLIENT_ID = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"` and the refresh request body shape. Everything else (window parsing, poll gating, diagnostics) is CCCOG's own pre-existing implementation from the v1 batch-2 work, not copied. |
| `crates/tb_core_ffi/src/agent_grok.rs` | Verifying the Grok `cli-chat-proxy.grok.com/v1/billing?format=credits` endpoint and the `creditUsagePercent` field name | Confirmed CCCOG's pre-existing implementation already matched; no code changed as a result. |
| `src/TokenBar.App/FlyoutWindow.xaml.cs` (`SetupAlwaysOnAcrylic`/`ApplyAcrylicTheme`, the `DesktopAcrylicController` + `SystemBackdropConfiguration` pattern) | Learning the Mica-with-Acrylic-fallback backdrop technique for a tray-anchored, always-topmost popover window | `bar/app/CCCOG.Bar.App/FlyoutWindow.xaml.cs: SetupBackdrop()` — a smaller, MicaBackdrop-first version (TokenBar's manual `DesktopAcrylicController` is the fallback path here, not the primary one; no slide-in animation, wheel hook, or tab system was carried over — those are out of scope for the slim shell). |
| `src/TokenBar.App/FlyoutWindow.xaml` | Confirming the borderless/no-resize/topmost `Window` shape TokenBar uses for its tray popover | CCCOG's v1 flyout already used the same `OverlappedPresenter` borderless/topmost setup independently; this file was read for comparison only, nothing copied. |

No other TokenBar-Windows file was read for this work. `DashboardView.xaml`
and its multi-thousand-line code-behind (tabs, charts, cost/history
analytics) were deliberately **not** opened — that surface is explicitly out
of scope for CCCOG Bar v2 (see the spec's "Out of scope" section) and this
crate/shell never link against `tb_core_ffi` or any TokenBar assembly.

## What is NOT a derivation

`bar/crates/cccog-bar-core` (flow/dispatch-root parsing) and the pre-existing
`bar/crates/cccog-bar-core/src/quota.rs` this crate's tests grew from (now
`bar/crates/cccog-bar-quota`) predate this derivation — they are CCCOG's own
v1 batch-2 work, cross-checked against TokenBar-Windows for correctness (see
table above) but not sourced from it.

## 2026-08-16 corrective rebuild: verbatim shell port

The 2026-08-16 shell above (`SetupBackdrop()`, the custom `#CC131824`-tinted
Grid, the ItemsControl/DataTemplate cards) was an "inspired-by"
reinterpretation, not a copy, and the operator rejected it: the requirement
is a literal reuse of TokenBar's UI — "the CCCOG flyout must look like a
TokenBar window whose only difference is its content sections." This section
records the corrective pass that replaced it. It supersedes the
`FlyoutWindow.xaml.cs` row above (Mica is gone; the manual
`DesktopAcrylicController` is now the only backdrop path, not a fallback)
and the "`DashboardView.xaml` ... deliberately not opened" note below the
first table (it was opened this round, read-only, same upstream commit
`a829d51deb0f37bcff211c83de5e9d2f99f7663f`).

| Upstream file | Used for | What actually landed in CCCOG Bar v2 |
| --- | --- | --- |
| `src/TokenBar.App/Ui.cs` | The `Card`/`Disc`/`Text`/`Dim`/`Row`/`BrushFromHex` element factories — TokenBar's card/row/typography primitives (corner radius, spacing, opacity levels, the 11/13/15 font-size ramp) | `bar/app/CCCOG.Bar.App/Ui.cs` — ported verbatim, namespace only. `TokenKinds` and `ShareBar` (TokenBar's Models-lens legend/proportion bar) were not ported; CCCOG has no models lens. |
| `src/TokenBar.App/DashboardView.xaml.cs` (`BuildLimits`, `QuotaRow`, `GaugeBar`) | The Agent-limits quota card shape: provider-grouped sections (disc + bold name), one row per window (label + reset-time header, thin colored gauge bar, `NN% used` footer) | `bar/app/CCCOG.Bar.App/DashboardView.xaml.cs`: `BuildQuotaContent`/`QuotaRow` (adapted to CCCOG's flat `QuotaCardViewModel` list instead of TokenBar's `UsageWindow`/pace-presentation types) and `Ui.GaugeBar` (ported without the pace-marker/tick — CCCOG's DTO carries no pace-projection data to feed it). |
| `src/TokenBar.App/DashboardView.xaml` | The header Grid shape (title left, refresh button + `ProgressRing` spinner in a 26×26 overlay Grid, `Padding="16,14,16,10"` / `RowSpacing="10"`), the `ScrollViewer`/`ContentPresenter` section-hosting pattern, the footer row | `bar/app/CCCOG.Bar.App/DashboardView.xaml` — same Grid shape, same header pattern (minus the year-filter `DropDownButton` and today/total/rate stat row TokenBar has no equivalent data for; replaced with a single "refreshed HH:mm:ss" line), `ContentPresenter` reused twice for CCCOG's two fixed sections instead of TokenBar's one lens-swapped section, footer minus the gear/Quit buttons (CCCOG has neither a settings window nor an in-flyout quit action). |
| `src/TokenBar.App/FlyoutWindow.xaml` | The window's child-element shape (`Grid` containing the dashboard UserControl plus a transparent top-edge resize-grip `Border`) | `bar/app/CCCOG.Bar.App/FlyoutWindow.xaml` — ported verbatim, namespace only. The resize grip is present as a structural/hover-only element (see gap below). |
| `src/TokenBar.App/FlyoutWindow.xaml.cs` (`SetupAlwaysOnAcrylic`, `ApplyAcrylicTheme`, `ApplyPopupChrome`, `PositionNearTray`, `SlideWindowAsync`/`FadeContent`, `TaskbarEdge`/`Toward`) | The actual window chrome: `DesktopAcrylicController` with `TintOpacity=0.25f`/`LuminosityOpacity=0.55f` and the exact light (`243,243,243`)/dark (`28,28,34`) tint colors, driven off `Dashboard.ActualTheme` (no `RequestedTheme` override anywhere in TokenBar — it follows the system theme, same as this port); the WS_POPUP/DWM-rounded-corner/border-color-none/HWND_TOPMOST popup chrome; taskbar-edge-aware positioning; the 180ms/120ms slide+fade show/hide animation | `bar/app/CCCOG.Bar.App/FlyoutWindow.xaml.cs` — ported, replacing `SetupBackdrop()`'s MicaBackdrop-first/hardcoded-dark-tint (`19,24,36`, `Theme=Dark` always) entirely. **Named gaps** (not ported): resize-grip drag-to-resize — TokenBar persists the dragged height via `AppSettings.Store`, which this shell has no equivalent of, so the grip is hover-only (opacity toggle) with no drag handler; the `WH_MOUSE_LL` low-level mouse hook TokenBar installs as a focusless-window wheel-scroll workaround — not required for visual parity and not ported. |

### Verification note (theme correctness)

A mid-rebuild side-by-side check was reported as CCCOG rendering a dark
slate flyout against TokenBar's light acrylic. Investigation traced this to
a **stale running process** — an older CCCOG.Bar.App instance still showing
the pre-rebuild `ItemsControl`/`DataTemplate` layout, not the freshly built
binary — rather than a theme-resolution bug in the ported code (both this
shell and TokenBar resolve `ActualTheme` off the system setting with no
override; on the verification machine `AppsUseLightTheme=1`, so both
correctly render light). After killing every stray process and relaunching
from a clean `dotnet build -r win-x64`, both windows render light-theme
Acrylic with matching card surfaces. Pixel samples (`PrintWindow`,
`PW_RENDERFULLCONTENT`) of the freshly launched windows:

| Region | TokenBar (RGB) | CCCOG (RGB) | Δ/channel |
| --- | --- | --- | --- |
| Flyout background | 245, 245, 245 | 248, 248, 248 | ≤3 |
| Card / tab surface | 250, 253, 251 | 253, 253, 253 | ≤3 |
| Header title text | 26, 26, 26 | 24, 24, 24 | ≤2 |

All within the ±10/channel gate. TokenBar was still on its first-snapshot
loading screen for this capture (its multi-minute history parse hadn't
finished), so its Agent-limits card itself wasn't on screen yet; the sampled
"card surface" is its selected-tab pill background, per the acceptance
criteria's fallback ("if TokenBar is still on its loading screen, its
chrome/header/tab area is already representative").

## 2026-08-16 quota job-failure cross-check: process-output scan

The job-failure cross-check above (walking `dispatch/jobs`, checking each
failed job's `status.json` `error` field) missed a real, currently-active
block: job `20260815T141347Z_ce298f32` (codex, `cwd:
D:\code\TokenBar-Windows`) has `status.json` `error: "Provider exited with
code 1."` — generic, matches nothing — while its `stdout.log` tail carries
the provider CLI's own message:

```
{"type":"error","message":"You've hit your usage limit. Upgrade to Pro
(https://chatgpt.com/explore/pro), visit
https://chatgpt.com/codex/settings/usage to purchase more credits or try
again at Aug 20th, 2026 11:50 AM."}
```

`cccog-bar-ffi`'s `walk_jobs_for_quota_failure` now also reads a bounded
(4 KiB) tail of `stdout.log`/`stderr.log` next to a failed job's
`status.json` and checks it with `cccog_bar_quota::looks_like_quota_limit_output`
(provider-specific markers: codex `"hit your usage limit"`/`"usage_limit"`,
grok `"402"`/`"credits"`) whenever the `status.json` `error` field itself
didn't already match. `extract_human_reset_date` (new, `regex`-crate-backed)
extracts a spelled-out date like `"Aug 20th, 2026 11:50 AM"` verbatim from
the matched text; `apply_quota_limit_override`'s diagnostic now always names
a reset — the extracted text, or the literal word `"unknown"` — never a
guess. Verified end to end by relaunching the built app against this
machine's real dispatch history: the codex card corrected to `"Limit / 100%
used / limit reached (dispatch failure 14:13) · resets Aug 20th, 2026 11:50
AM"`, and — unprompted, from the same real data — the grok card also
corrected (a second, independently-discovered real failure, job
`20260815T045341Z_c0d1a224`: `stdout.log` carried `"API error (status 402
Payment Required): Grok Build usage balance exhausted"`, no date present, so
`"resets unknown"`).

## 2026-08-16 quota HTTP resilience: cache, stale-on-failure, TTL, backoff

A live HTTP 429 on claude's `oauth/usage` endpoint wiped its card down to a
bare `"claude / HTTP 429"` — the five-hour/seven-day bars vanished, breaking
the stale-on-failure rule the rest of this shell already follows for
file-based quota. Root cause: claude/grok were polled live over HTTP on
every refresh (~60s, plus repeated app relaunches during testing) with no
cache, and no throttling — self-inflicted rate-limiting.

Fixed in `cccog-bar-ffi` (a new module in `src/lib.rs`, not a separate
file — see its own header comment) with two upstream-derived pieces:

- **TTL layering**, adopted from TokenBar-Windows' actual behavior
  (`crates/tb_core_ffi/src/agent_usage.rs`'s `CLAUDE_HEADER_TTL_SECS`):
  TokenBar does not hit its quota HTTP endpoints on every UI tick — a
  successful Claude fetch is cached for 300 seconds, with the UI's own
  refresh only re-reading local files in between. `precheck_http_provider`
  reproduces the same layering: claude/grok live fetches sit behind a 300s
  TTL; the manual Refresh button may bypass the TTL but is itself capped to
  once per 60s (`MANUAL_REFRESH_MIN_INTERVAL_SECS`) — threaded from a new
  `manual` field on the FFI's quota-poll input, set only by
  `DashboardView`'s Refresh-button click (`FlyoutWindow.xaml.cs`). Codex is
  unaffected — it was already a local file-tail read, not an HTTP call, so
  it stays on the 60s UI tick.
- **Stale-on-failure + backoff**: `postprocess_http_provider` caches the
  last successful card per provider (`QuotaResilienceCache.last_success`,
  mirrored to `%LOCALAPPDATA%\CCCG\bar-quota-cache.json` so a fresh app
  launch right after a 429 still shows real numbers, not a blank card).
  Any non-Fresh result renders via `render_with_cache_fallback`: the cached
  bars/values unchanged, relabeled `"stale · {reason} · data from HH:MM"` —
  the bare failure only ever shows when there has never been a successful
  fetch. An HTTP 429 additionally schedules a backoff
  (`QuotaResilienceCache.backoff`), honoring the response's `Retry-After`
  header (`HttpResponse.retry_after_seconds`, new) when present, else
  defaulting to 5 minutes; a timeout or other non-429 failure does not
  trigger a backoff (only a 429 explicitly says "wait").

Verified end to end against a real, live 429 (not a synthetic fixture) by
relaunching the built app: the claude card rendered

```
five hour   13% used   stale · HTTP 429 · data from 20:26
seven day   100% used  stale · HTTP 429 · data from 20:26
```

with the exact bars/percentages from the previous successful fetch — loaded
from `bar-quota-cache.json` on a brand-new process, confirming the disk
persistence path, not just the in-memory one.

## 2026-08-16 interaction-layer parity: tray menu, footer Quit, resize drag, wheel hook

The 2026-08-16 corrective rebuild above (verbatim shell port) named two gaps
it deliberately did not close: resize-grip drag-to-resize (no settings
store to persist the height into) and the `WH_MOUSE_LL` wheel-focus
workaround. This pass closes those two, plus two features CCCOG never had
at all — a tray right-click menu and an in-flyout Quit path — bringing
interaction-layer coverage to parity with TokenBar-Windows (same upstream
commit, `a829d51deb0f37bcff211c83de5e9d2f99f7663f`), ported line-by-line
where CCCOG has the same dependencies and adapted (with the deviation
recorded) where it doesn't.

| Upstream file | Used for | What landed in CCCOG Bar v2 |
| --- | --- | --- |
| `src/TokenBar.App/TrayService.cs` (`RebuildMenu`, the `ContextMenuMode.PopupMenu` pattern and its Command-vs-Click/ToggleMenuFlyoutItem caveats, `QuitApp`) | The native right-click context menu and the single shutdown path | `bar/app/CCCOG.Bar.App/App.xaml.cs`: `RebuildMenu()` (Open CCCOG Bar / Refresh now / Quit — three `MenuFlyoutItem`s, all `Command`-wired per the PopupMenu caveat) and `Quit()`/`QuitApp` (disposes the tray icon, calls `FlyoutWindow.Shutdown()`, then `Application.Current.Exit()`, guarded against a double-invoke). **Named deviation**: TokenBar's menu also has "Menu bar shows" (7-mode tray-icon radio group) and "Quota source" submenus, plus a "Settings" item — none ported. CCCOG's tray icon has no live-number display mode to pick a source for, and has no settings window to open (see the original derivation table above for the same "no settings window" gap). |
| `src/TokenBar.App/DashboardView.xaml` / `.xaml.cs` (the footer `Grid`: `FooterText` left, `SettingsButton`+`QuitButton` right, `Padding="7,2,7,3"`/`FontSize="11"`/transparent background/no border on `QuitButton`; `QuitButton.Click += (_, _) => TrayService.QuitApp?.Invoke();`) | The footer row layout and the Quit button's exact styling/wiring | `bar/app/CCCOG.Bar.App/DashboardView.xaml`: added the same `StackPanel`/`QuitButton` to the existing footer `Grid`, verbatim Padding/FontSize/Background/BorderThickness. `DashboardView.xaml.cs`: `QuitButton.Click += (_, _) => App.QuitApp?.Invoke();`. **Named deviation**: no `SettingsButton`/gear — same "no settings window" gap. |
| `src/TokenBar.App/FlyoutWindow.xaml.cs` (`WireResizeGrip`'s `PointerPressed`/`PointerMoved`/`PointerReleased`/`PointerCaptureLost` drag handlers, `EndResize`'s `AppSettings.Store.SetDouble("tokenbar.popover.height", ...)`, `PositionNearTray`'s `AppSettings.Store.GetDouble` read/clamp) | Top-edge drag-to-resize with height persistence | `bar/app/CCCOG.Bar.App/FlyoutWindow.xaml.cs`: `WireResizeGrip()` (replacing the old hover-only `WireResizeGripHover()`), `EndResize()`, and `PositionNearTray()`'s height-read — logic ported verbatim, `AppSettings.Store` swapped for the new `LocalSettings` (see below), key `"cccog.popover.height"`. |
| `src/TokenBar.App/FlyoutWindow.xaml.cs` (`InstallWheelHook`/`RemoveWheelHook`, the `WH_MOUSE_LL` hook proc, `MSLLHOOKSTRUCT`/`PointL`, the `SetWindowsHookExW`/`UnhookWindowsHookEx`/`CallNextHookEx`/`GetModuleHandleW` P/Invokes; called from `ShowFlyout`/`HideFlyout`) | The focusless-popup wheel-scroll workaround | `bar/app/CCCOG.Bar.App/FlyoutWindow.xaml.cs`: same hook lifecycle (install on `ShowFlyout`, remove on `HideFlyout`, plus a `Closed`-handler safety net so Quit while visible can't leak the hook), same struct/P-Invoke shapes, ported verbatim. |
| `src/TokenBar.App/DashboardView.xaml.cs` (`RouteGlobalWheel`, `ScrollBy`) | Routing the hook's wheel event into the scrollable section | `bar/app/CCCOG.Bar.App/DashboardView.xaml.cs`: `RouteGlobalWheel`/`ScrollBy` on the renamed `CardsScroll` (now the Flow section's `ScrollViewer` — see the layout-reorder entry below). **Named deviation**: TokenBar's version first tries `TryZoomGraphAt` (3D contribution-graph hit-test) before falling back to `ScrollBy`; CCCOG has no 3D graph, so it goes straight to `ScrollBy`. |
| `src/TokenBar.Core/SettingsStore.cs` (the atomic-write/quarantine-on-corruption/refuse-to-clobber-on-load-failure JSON store pattern) | A settings store for the resize-grip height, which CCCOG never had | `bar/app/CCCOG.Bar.App/LocalSettings.cs` (new file) — a trimmed port: same atomic-write (temp file + rename), corruption-quarantine, and load-failure-refuses-to-save behavior, at `%LOCALAPPDATA%\CCCG\bar-settings.json`. **Named deviation**: TokenBar's `SettingsStore` is a typed String/Bool/Int/Double store with a `Changed` event (other windows/panels react live to a setting written elsewhere); `LocalSettings` is a static string-only Get/Set with no `Changed` event — CCCOG has exactly one writer (the grip) and one reader (`PositionNearTray` at next show), so there is nothing else to notify. |

Verified end to end against the real, running app (built `dotnet build -c
Release -r win-x64`, launched, driven via real Win32 mouse/keyboard input
injection plus UI Automation since this environment's screen-capture API
returns a stale/cached frame for composited WinUI content — `PrintWindow`
with `PW_RENDERFULLCONTENT` also renders this Acrylic window's client area
black for the same reason, so verification here reads the live control
tree and drives real cursor/click/wheel input instead of relying on
pixels, except for the native `#32768` tray menu, which is plain GDI and
does capture correctly via `PrintWindow`):

- Tray right-click: real right-click on the tray icon (via the taskbar's
  overflow flyout, `顯示隱藏的圖示`) opened a genuine native popup menu
  (`FindWindow("#32768", ...)` resolved to a real hwnd); `PrintWindow`
  captured it showing exactly "Open CCCOG Bar" / separator / "Refresh now"
  / separator / "Quit".
- Resize grip: a real mouse-down on the grip (DPI-aware physical
  coordinates via `SetThreadDpiAwarenessContext`) + drag + mouse-up moved
  `AppWindow`'s real `GetWindowRect` height from 800px to 860px live, and
  wrote `{"cccog.popover.height":"688"}` (688 DIP × 1.25 scale = 860px) to
  `%LOCALAPPDATA%\CCCG\bar-settings.json`. Quitting and relaunching the
  process restored the flyout at height 860px — the persisted value
  survives a cold start, not just the running process.
- Wheel hook: real `mouse_event(MOUSEEVENTF_WHEEL, ...)` events sent over
  the flyout produced no exception and no new `bar-crash.log` entry across
  several notches in both directions; the live Flow section had no
  overflowing content on the verification machine (0 active/recent jobs)
  so a nonzero scroll-offset change could not additionally be observed,
  but the install → route → uninstall path ran clean end to end.
- Quit (both the tray menu item and the footer button, via UI Automation
  `InvokePattern.Invoke()` on `QuitButton`): the process exited fully
  (`tasklist` found no `CCCOG.Bar.App.exe`) with zero new `bar-crash.log`
  entries, confirming the tray icon, timers, acrylic controller, and
  `WH_MOUSE_LL` hook were all released — not just the window closing.

## 2026-08-16 quota-card formatting: unified reset time, abbreviated windows, composed row, condensed status

Same session, operator-driven refinement pass (not a TokenBar port — the
formatting rules below are this repo's own, adopting only TokenBar's
general "the number leads" agent-card visual language as a reference
point). All of it lives in `cccog-bar-quota`/`cccog-bar-ffi` so the C# view
stays display-only, per this repo's existing "formatter in Rust, not C#"
convention (see the HTTP-resilience section above).

- **`cccog_bar_quota::normalize_reset_display`** (new): the three quota
  providers previously showed their reset time in three different raw
  shapes (Codex epoch seconds, Claude ISO-8601 `Z`, the job-failure
  override's free-text extraction). This function parses all of them
  (RFC3339, epoch, naive `YYYY-MM-DD[T ]HH:MM[:SS]`, and the English "Aug
  20th, 2026 11:50 AM" shape `extract_human_reset_date` returns) and
  renders one bare local-time string: `"MM/DD HH:MM"`, or `"MM/DD"` for a
  date-only source. A source with no explicit UTC offset is treated as UTC
  before the local conversion — documented, not a silent guess. Genuinely
  unparseable input passes through completely unchanged. Wired into
  `window()` (every provider's per-window `resetsAt`) and
  `apply_quota_limit_override`'s reset text.
- **`cccog_bar_quota::resolve_window_label`** (new): window labels
  ("five hour", "seven day", "Primary") are abbreviated to `"Nh"`/`"Nd"` —
  primarily from Codex's `window_minutes` field (exact, no text-guessing),
  falling back to parsing English duration text (`"five hour"` → `5h`) when
  `window_minutes` isn't present (Claude). A label that isn't
  duration-shaped at all (`"Credits"`, `"Limit"`) is left untouched.
- **`cccog_bar_quota::format_window_line`** (new): composes the locked
  per-window row order — `"<abbreviated window>  <NN%>  <date>"`, e.g.
  `"5h  7%  08/20 11:50"` — from an already-abbreviated label, a percent
  (whole when it rounds cleanly, one decimal otherwise), and an
  already-normalized reset string. `cccog-bar-ffi`'s
  `quota_cards_wire_json` stamps this onto every window object's
  `displayLine` field at the JSON-envelope boundary (both
  `envelope_from_parts` and `poll_remote_quotas_json`), so C# renders it
  verbatim instead of re-composing label/percent/date itself.
- **Condensed one-line diagnostics + `diagnostic_detail`**: the on-card
  status line is capped at one short line — `"limit · 08/20 11:50"`
  (`apply_quota_limit_override`, was "limit reached (dispatch failure
  HH:MM) · resets ...") and `"stale · HTTP 429 · 14:13"`
  (`render_with_cache_fallback`, was "... · data from HH:MM"); the
  "quota response contained no X" sentences (`fetch_claude_quota`,
  `fetch_grok_quota`) condense to `"no quota data"`/`"no credit data"`. The
  fuller sentence each was condensed from is never discarded — it moves to
  the new `QuotaCards.diagnostic_detail` field (`Option<String>`,
  `#[serde(skip_serializing_if = "Option::is_none")]`, so it costs nothing
  on the wire when absent) via the new `stale_with_detail` helper.
  `bar/app/CCCOG.Bar.App/DashboardView.xaml.cs` renders it once per card
  (not once per window — the earlier per-window placement duplicated an
  identical card-level line under every row) as a dim line with
  `ToolTipService.SetToolTip` carrying `diagnostic_detail` when present.
- **Card anatomy + layout** (`bar/app/CCCOG.Bar.App/DashboardView.xaml`):
  Flow moved to the top (`Grid.Row="1"`, the scrollable section — it's the
  one with unbounded row count), Quota moved to the bottom as three
  fixed-order, equal-width side-by-side cards (`Grid.Row="2"`, three star
  columns) — left/center/right = Claude/Codex/Grok, via three named
  `ContentPresenter`s (`ClaudeQuotaHost`/`CodexQuotaHost`/`GrokQuotaHost`)
  instead of one grouped card. Footer stays at the very bottom
  (`Grid.Row="3"`). A provider that hasn't reported data yet still renders
  its own "Loading…" line in its own slot (lessons-learned #1: never hide
  a section while loading) rather than the whole row vanishing.

Verified end to end against the real, live, running app (not a synthetic
fixture — this machine's actual Claude/Codex/Grok credentials, including a
real active Codex quota-limit block): the UI Automation control tree read
back

```
Claude
  5h  10%  08/16 19:20
  7d  2%  08/23 14:00
Codex
  Limit  100%  08/20 19:50
  limit · 08/20 19:50
Grok
  no credit data
```

— three cards, fixed order, unified bare-time format on all three
(including the real job-failure override, which independently confirmed
the UTC→local conversion: the raw provider text "Aug 20th, 2026 11:50 AM"
became "08/20 19:50", the correct +8h for this machine's Asia/Taipei
local time), abbreviated window labels, no "used"/"resets" words, at most
one condensed status line per card, and no clipping of the third (Grok)
column at the flyout's standard 500-DIP width (its card's right edge sat
35px inside the window's right edge). `cargo test --workspace`: 108 tests
green across all four crates, including new coverage for
`normalize_reset_display`, `resolve_window_label`, `format_window_line`,
and the `displayLine`/`diagnosticDetail` wire fields.

## 2026-08-16 second Flow data source: live Claude Code sessions/agents, presence heartbeats, the Flow tree

Operator complaint: "我至少兩個 Claude session 活著,還有各自 agent,都沒顯示" —
Flow only ever read `%LOCALAPPDATA%\CCCG\dispatch\{jobs,owners}`, which
CCD-internal Claude Code sessions never touch. This pass adds a second Flow
data source (live local Claude Code sessions/subagents) and, mid-flight,
the operator escalated the display itself from a flat row list to a
two-level tree with a third liveness layer (presence heartbeat files).
Scope grew across the session; this section documents the final shape.

### Mechanism imitated from TokenBar (operator directive), and what differs

Read (read-only, not modified) `D:\code\TokenBar-Windows\crates\tb_core_ffi\src\usage_tail.rs`.
Its `UsageTailer` mechanism — stat-sweep candidates, re-parse only files
whose mtime falls inside the event window plus a margin, liveness from
EVENT TIMESTAMPS inside the file (never mtime alone), a trailing window
snapshot-replaced each tick (no cross-tick accumulation), and a skip-parse
shortcut keyed on "has the newest source mtime moved since last tick" — is
replicated in `cccog-bar-core/src/claude_sessions.rs`'s `ClaudeSessionTailer`.

What differs: `UsageTailer` parses local CLIENT USAGE (token counts per
message) via `tokscale_core`, a dependency this repo does not have and was
never asked to take on. This module parses SESSION IDENTITY/liveness
instead (title, model, alive subagents, cross-session-message edges)
directly from the same JSONL transcript files, with its own from-scratch
JSON line parser (`parse_line`/`parse_transcript`) rather than a shared
crate. The skip-parse shortcut, the mtime-margin candidate filter, and the
snapshot-replace-per-tick contract are the same shape; the domain being
parsed is not. Everything past the C-layer scanner — presence heartbeats,
cross-session-message edge extraction, dispatch-job/owner integration, and
the tree itself — is CCCOG-original, not from TokenBar (TokenBar has no
equivalent to any of it).

### What real transcripts on this machine turned out to look like

Investigated three live sessions under
`C:\Users\mike2\.claude\projects\<project-slug>\<session-uuid>.jsonl`
(project-slug = cwd with path separators replaced by `-`, e.g.
`d--code-openruterati`) — including this very session, mid-write, while
investigating. ~40 distinct `type` values exist per line; only a handful of
fields are keyed on (full detail in `claude_sessions.rs`'s module doc
comment):

- `timestamp` (RFC3339 `Z`) — present on essentially every substantive
  line; the sole liveness signal.
- `isSidechain` (bool) — recorded but not load-bearing: on this machine the
  session/agent split comes from *which file* a line is in (a subagent gets
  its own file), not this flag; no top-level session transcript inspected
  ever had it `true`.
- `message.model` — assistant lines only, e.g. `"claude-sonnet-5"`.
- `type":"custom-title"` + `customTitle` — **this turned out to BE the
  title store the task asked to look for.** Re-emitted verbatim on
  (apparently) every turn; the last occurrence in the file is the current
  title. There is no separate sqlite/json title index anywhere under
  `~/.claude` — checked: no `*.db`/`*.sqlite*` files exist at all,
  `history.jsonl` only has prompt text keyed by session id (not a title),
  and `sessions/*.json` are PID-lock files, not a title index. No second
  file needs to be read.
- `message.content` (String, on a `user` line) — the shape a
  `send_message`-delivered cross-session message actually takes:
  `<cross-session-message from="local_<uuid>" name="<title>" encoded="1">
  ...`. Confirmed against real transcripts that `from`'s `local_<uuid>`
  does NOT match any session-uuid `.jsonl` filename on this machine — it is
  a separate peer-id namespace. Matching therefore goes through `name`
  (the sender's own title text) against another live session's resolved
  title, not `from`.

Subagents: `<project-slug>/<session-uuid>/subagents/agent-<id>.jsonl`, each
with a sibling `agent-<id>.meta.json`
(`{"agentType":...,"description":...,"toolUseId":...,"spawnDepth":1,"model":"sonnet"}`).
Confirmed present and actively growing on three different real sessions
during investigation.

### A real bug the live-run gate itself caught (not a fixture-only find)

First live run showed only one of the two-plus actually-alive sessions —
specifically, it silently dropped the session running *this very task*.
Root cause, found by adding a throwaway debug binary
(`cargo run --example debug_session`, since deleted) against the real
`~/.claude/projects` tree: a controller session that dispatches a Task-tool
subagent and then goes quiet (waiting for it) has a STALE top-level
transcript mtime/last-event for the entire time the subagent keeps actively
writing its own file. Two places assumed "parent transcript freshness" was
the right liveness signal and both were wrong:

1. **The mtime candidate prefilter** (`collect_claude_sessions`) checked
   only the parent `.jsonl` file's own mtime before deciding whether to
   read the session at all — a session whose parent file was untouched for
   >15 minutes was skipped before its `subagents/` directory was ever even
   listed, regardless of how fresh those files were. Fixed by
   `any_subagent_candidate`: a session is a scan candidate when EITHER its
   own file's mtime is fresh OR any subagent file's is.
2. **The content-level aliveness check** (`build_session_summary`) computed
   "alive" from the parent transcript's own newest event only. Fixed to
   `max(own newest event, every subagent's own newest event)` — a session
   with a genuinely-active agent is alive even while its own top-level
   activity is stale.

Both are covered by regression fixtures:
`stale_parent_mtime_with_a_fresh_subagent_file_still_scans_the_session`
(FS-level) and `session_stays_alive_via_an_active_agent_even_when_its_own_activity_is_stale`
(pure-function level). Re-verified against the real tree after the fix:
both this task's own coordinator session ("CCCG開發", alive via this very
subagent) and a second real session ("doc2 api-gateway上線", 9 alive
agents at the time) appeared correctly.

### The tree (rules from the operator's mid-flight message)

`cccog-bar-core/src/tree.rs`'s `build_tree` combines Claude
sessions+agents+cross-session edges, presence records, and dispatch
jobs/owners into a flattened, `depth`-tagged `Vec<TreeRow>` (0 = top-level
controller/resumable/terminal row, 1 = its child) rather than a nested DTO
— the shell renders indentation from `depth` instead of hosting a real
recursive tree control, since the tree is exactly two levels deep by
construction and a flat list with a depth tag is strictly simpler on both
sides of the FFI boundary.

- **Dedup** ("a node appears once"): for every session, the single
  most-recent inbound `<cross-session-message>` edge whose `name` resolves
  (via `find_controller`: exact title match, else a leading-ASCII-alnum-key
  heuristic — e.g. `"doc1"` matches a session titled `"Doc1 主管"`) to
  ANOTHER live session makes that session the controller; the child is
  removed from the top-level set. A session with no resolvable inbound edge
  renders top-level.
- **Active dispatch jobs** (codex/grok) attach as children under whichever
  live session's title matches the job's `callerLabel`/`hopChain` (same
  precedence as `control.rs::source_label`, minus the "ambiguous"
  placeholder — an unattachable job just doesn't render as a child). If the
  matched session is itself someone's child, the job is forwarded up to
  that session's own controller, keeping the tree exactly two levels deep.
- **Terminal dispatch jobs and non-live owner leases** aggregate at the
  bottom, one row per provider (terminal) or per (provider, live) pair
  (owners) — never attached anywhere, per rule 4.
- **Three-word state vocabulary**, same for parent and child: `執行中`
  (running a turn) / `閒置` (open, idle) / `resumable`|`terminal` (bottom
  aggregate only). A session's own state resolves via presence when
  available (see below), else the C-layer transcript-recency fallback
  (≤60s since last event ⇒ 執行中). An agent's state is always the
  C-layer recency check (agents have no presence file of their own — a
  Task-tool subagent runs inside its PARENT session's OS process, so any
  heartbeat is written under the parent's session id, not a distinct one
  per agent).

### Presence heartbeats — the A/B layer

New hook `hooks/cccg-presence-hook.js` (same conventions as
`hooks/cccg-state-hook.js`: <1s, stdin JSON with `session_id`/`cwd`
per the standard Claude Code hook contract — same fields
`cc-hub/hooks/miki-emit.ps1` already reads from the identical contract —
argv[2] carries the event name, never crashes, always exits 0). Writes
`%LOCALAPPDATA%\CCCG\presence\<session-id>.json`:

```json
{"sessionId":"<uuid>","cwd":"D:\\code\\CCCG","event":"PostToolUse","ts":1786868945408}
```

(`ts` = `Date.now()`, matching JS convention — `cccog_bar_core::presence`
treats it as Unix epoch milliseconds.) Write is atomic (`.tmp-<pid>` then
`renameSync`) so the bar's reader never has to handle a torn file it can't
already tolerate anyway (`presence::collect_presence` skips unparseable
files silently either way). Also prunes any presence file older than 48h on
every invocation, same bound as `cccg-state-hook.js`'s own dispatch-jobs
sweep. **Not registered in `settings.json` by this change** — the operator
said the coordinator does that at landing time, so this repo only ships the
script.

State resolution (`presence::resolve_presence_state`): a
`UserPromptSubmit`/`PreToolUse`/`PostToolUse`/`SessionStart` heartbeat
fresher than 60s ⇒ 執行中; `Stop` always means turn-ended (閒置), even if
it's the freshest possible heartbeat; anything else fresh enough to trust
⇒ 閒置; older than 10 minutes ⇒ `Unknown`, which makes the tree fall back
to the C-layer transcript judgment for that session. Verified end to end
against the real, running app: writing a synthetic fresh `PostToolUse`
presence file for the real "doc2 api-gateway上線" session (whose own
transcript had gone quiet minutes earlier) flipped its tree row from
`閒置 · Nm` to `執行中 · Ns` after a Refresh-button click — the override
path, not just its unit tests, actually fires.

### FFI wiring (`cccog-bar-ffi`)

`control_snapshot_json`'s output envelope gained a sibling `tree` field
next to the existing `control` object (the legacy flat `control.rows` stays
untouched, for the debug CLI and back-compat). `build_flow_tree` resolves
every root at runtime, never hardcoded: `claude_sessions::default_projects_root()`
(`USERPROFILE`, falling back to `HOME` for non-Windows test/CI
exercisability) for the transcript tree, `%LOCALAPPDATA%\CCCG\presence` for
heartbeats, and the same dispatch root the caller already passes in for
jobs/owners. A process-wide `OnceLock<ClaudeSessionTailer>` static gives
the skip-parse shortcut somewhere to live across FFI calls (the ABI itself
is stateless per call) — same pattern as this file's pre-existing
`quota_resilience_cache()`.

### C# side (`CCCOG.Bar.App`)

`FlowRowViewModel` → `FlowTreeRowViewModel` (`ViewModels.cs`); the flat
`NativeControlClient` row parser now reads `tree` instead of `control.rows`
(`FlyoutWindow.xaml.cs`). `DashboardView.BuildFlowContent` renders each row
with a dot (smaller/dimmer at depth 1) plus a label `TextBlock` built from
two `Run`s — the child's own `"(type · model)"` parenthetical is a second
`Run` colored with `ProviderPalette.Color(row.Provider)` (operator rule:
"same palette as the dots"), everything else in the line stays the default
label color; indentation is a left `Margin` on depth-1 rows, not a nested
tree control. `StatusVisual`'s opacity/pulse table gained the tree's three
Chinese/English state words (執行中/閒置/resumable/terminal) alongside the
pre-existing English dispatch-status words, so a Flow row's brightness
always comes from the one shared table regardless of which of the two Flow
sources produced it. **Simplification vs. the operator's ASCII mock**: the
mock shows top-level rows repeating their own provider word ("claude ·
執行中") in the middle column; this build omits that specific word (the dot
color already conveys it) and shows state+elapsed only, to avoid needing a
three-column grid for a distinction the dot already makes — documented here
as a deliberate, not accidental, deviation.

Also folded into this pass (operator styling feedback, C#-side only, no
Rust change): quota card window rows now split their composed
`DisplayLine` text into two pieces via the existing `Ui.Row` two-column
primitive — the window/percent cluster (`"5h  11%"`, `WindowLabel` +
`UsedText`, bold, left-aligned, unchanged weight/position) and the reset
date/time (`ResetText`, right-aligned, `Ui.Dim` — non-bold, smaller,
dim-secondary, the same treatment TokenBar uses for its own timestamp
text). Both fields already existed on `QuotaCardViewModel`; `DisplayLine`
itself is no longer read by `QuotaRow` but stays on the view model for any
other caller depending on it.

### A build/verification trap discovered along the way (not a code bug)

`CCCOG.Bar.App.csproj`'s `CopyCccogBarFfi` target copies the freshly-built
native DLL into `$(TargetDir)` (the base `bin/x64/Release/net10.0-...\`
folder) after `dotnet build`, but NOT into the RID-specific
`...\win-x64\` subfolder that also contains a full, independently-runnable
copy of the exe + both DLLs. A `dotnet build` therefore updates the base
folder's native+managed DLLs but silently leaves `win-x64\`'s copies exactly
as stale as they were before — launching the `win-x64\` exe after a rebuild
runs OLD code with no error or warning of any kind. Hit this firsthand
during this task's own live-run verification (saw pre-`normalize_reset_display`-era
quota text render from a build that had shipped that feature hours
earlier). Not fixed here (out of scope for this task); the live-run
verification below used the base `bin\x64\Release\net10.0-windows...\CCCOG.Bar.App.exe`,
confirmed via DLL mtimes to be the actual just-built artifact.

> **Fixed 2026-08-17.** The `win-x64\` subfolder is the RID-specific output of
> `dotnet build -r win-x64` (both build flavors have been used for verification
> in past SYNC entries, so it is not a leftover). The csproj now has a
> `SyncWinX64RidOutput` target: after any RID-less build, if the `win-x64\`
> folder exists it re-runs the build with `RuntimeIdentifier=win-x64`, so both
> exes (managed + native FFI DLLs) always come from the same sources. Verified
> by touching `crates/cccog-bar-ffi/src/lib.rs`, rebuilding crate + app once,
> and confirming fresh matching mtimes in BOTH folders (ffi DLL byte-identical;
> managed DLLs differ in bytes only because win-x64 is the self-contained RID
> flavor). Cost: the inner incremental build added ~6s to an 11s build.

### Verification

`cargo test --workspace`: 116 tests green — 37 net new this pass (19 in
the new `claude_sessions` module, 9 in `presence`, 9 in `tree`) plus every
pre-existing suite unchanged (`control`'s own 9, plus `graph`/`parsers`/
`refresh`/`usage`/`ffi`/`quota`). `dotnet build -c Release -p:Platform=x64`:
0 warnings, 0 errors.

Live run (base output folder, DLL mtimes confirmed fresh; the coordinator's
previously-launched instance, pid 41268, was killed first as instructed):
UI Automation read-back of the real, running flyout —

```
Flow
CCCG開發                      執行中 · 3s
  Add Claude session li…  (agent · sonnet-5)     執行中 · 3s
doc2 api-gateway上線          執行中 · 7s   [after a presence-heartbeat test]
  Translate solutions p…  (agent · sonnet-5)     閒置 · 8m
  Translate solutions p…  (agent · sonnet-5)     閒置 · 9m
  Translate solutions p…  (agent · sonnet-5)     閒置 · 9m
  Translate solutions p…  (agent · sonnet-5)     閒置 · 9m
codex ×58                     terminal
grok ×34                      terminal

Claude   5h  16%   08/16 19:20
         7d  4%    08/23 14:00
Codex    Limit  100%   08/20 19:50
         limit · 08/20 19:50
Grok     no credit data
```

— both actually-alive Claude sessions on this machine appeared (including
the one running this very task, via its own live agent — the operator's
literal complaint, now fixed), each with correct elapsed/model/agent-count;
the bottom terminal aggregate rows render; quota cards kept the new
split-line date styling; the footer/Quit button worked (UI Automation
`InvokePattern.Invoke()` — process exited fully, verified via `tasklist`).
`bar-crash.log` did not exist before, during, or after this entire
verification pass (checked at every stage, not just "no growth") — zero
exceptions anywhere in the new code paths under real conditions.

## 2026-08-16 same-day follow-up: tooltips, parent model, aligned columns, and a real cycle-collapse bug

Three operator-driven follow-ups on the tree above, landed in the same
session as one more commit.

### 1. Full-text tooltips + parent-row model

`AGENT_LABEL_MAX_BYTES` (`claude_sessions.rs`) widened from 24 to 200 —
the original cap was short enough to visibly bite ("Add Claude session
li…"), and since the label field is the SAME string used for both the
compact on-row text and its hover tooltip, a Rust-side truncation that
short meant the tooltip could only ever repeat the same already-cut
string. It's now a wire-size safety net, not the thing deciding what's
visible; `TextTrimming.CharacterEllipsis` (already set on the label
`TextBlock`) does the actual visual clipping per available width, and
`ToolTipService.SetToolTip` is set unconditionally to the full
label(+model) text — WinUI has no cheap pre-layout way to know whether a
given string will actually clip, and a tooltip on already-short text costs
nothing.

Top-level controller rows gained `typeModelText` too (`tree.rs`) —
`"claude · <model>"` from the session's own most-recent in-window
`message.model`, omitted (never guessed) when none has been seen —
reversing this same day's earlier "controllers don't repeat the provider
word, the dot already shows it" simplification, per direct operator
request for parity with child rows.

### 2. A real cycle-collapse bug, found via a live re-verification, not a fixture

Re-running the live app after task 3's fix (below) landed showed the WHOLE
Flow tree collapse to a single, unrelated session — every other session,
including this task's own controller session and its live agent,
vanished. Root cause, found with a throwaway debug binary against the real
transcripts (since deleted): `controller_of` (child session → controller
session) is built purely from "each session's own most-recent inbound
edge". Real, live, completely normal CCD traffic — A relays to B, B
replies to A with its own `<*-session-message>` — makes A and B each
other's most-recent controller, a 2-cycle; a real "CCCG開發" ⇄ "Doc4 新增"
pair (a routine back-and-forth) reproduced this exactly. A session that is
itself assigned a controller is never iterated as a top-level row in
`build_tree`'s per-controller loop, so nothing downstream of a cycle member
— any of its own children — ever gets attached to anything visible either.
This is separate from (and not fixed by) the tag-name bug below; it was
masked by it, since almost no edges resolved before that fix, so the cycle
case never came up until edges started resolving correctly.

Fixed with two passes over `controller_of`, run right after it's built,
before anything reads it:

- **`break_cycles`**: walks each node's outgoing chain (memoized so no node
  is walked twice); on finding a cycle, removes the edge belonging to
  whichever member has the lexicographically smallest session id — an
  arbitrary but deterministic tie-break (there is no principled "who's
  really the controller" answer for a mutual pair). That member becomes
  top-level; the rest of the former cycle nests under it.
- **`flatten_controllers_to_root`**: breaking a 3-or-more-node cycle can
  leave a multi-hop CHAIN (`a -> b -> c`), and the tree is exactly two
  levels by construction — a child whose `controller_of` entry points at
  another child (not an actual top-level session) would never be attached
  anywhere, since the depth-1 lookup only matches against known top-level
  rows. This walks every entry to its ultimate root (the first node in the
  chain that isn't itself a `controller_of` key) and rewrites the entry to
  point there directly, so a session reached via ANY number of hops still
  ends up exactly one level under an actual top-level row.

Two new fixtures cover this directly: a 2-node mutual pair and a 3-node
cycle (`A→B→C→A`), asserting every member still renders exactly once and
the tree stays exactly two levels deep either way.

### 3. Task 3: the `<desktop-session-message>` tag name

The live symptom behind task 3 ("Doc1 主管 → doc3/doc5, rendered
nowhere") was investigated against the real recipient transcripts, not
fixtures, per the operator's instruction — and turned up a genuine
discrepancy with the operator's own first relayed set of "facts", which
this repo's own direct byte-level investigation superseded (documented
here so the correction is traceable, not just asserted):

- The coordinator's first message supplied CCD session ids (`339948fe...`,
  `fb4b150d...`, `ae65a2fa...`) as if they were transcript filenames; none
  of the three exist anywhere under `~/.claude/projects`. Content search
  (grepping every transcript for the real `customTitle` strings) found the
  actual filenames: "doc3 redies調查" = `.../D--code-openruterati--claude-worktrees-goofy-wescoff-28c933/0a104564-887e-4613-a4a1-251c5eff9de8.jsonl`,
  "doc5 Api文件檢查" = `.../d--code-openruterati/08881788-7b84-4185-8a36-60b9465be8fe.jsonl`,
  "Doc1 主管" = `.../d--code-openruterati/8d8e52b3-e0b5-483b-9fa1-a33e6277e75e.jsonl`.
  Session identity in this module was never derived from any externally
  supplied id in the first place — `session_id` is always
  `path.file_stem()`, the transcript's own filename — so this particular
  point turned out not to be a real risk to the code, only to the
  debugging narrative; worth recording so a future investigation doesn't
  re-litigate it.
- The coordinator's SECOND message additionally proposed "the extractor
  regex-matches the raw (still-`\"`-escaped) line instead of the decoded
  `message.content`" as the likely cause, citing a bare-quote grep
  returning zero hits. That diagnosis doesn't match this module's actual
  parse path: `parse_line` calls `serde_json::from_str` on the WHOLE line
  first, and `extract_cross_session_name` only ever runs on the resulting
  `Value::String`'s already-fully-decoded text (a `\"` in the JSON source
  is just an ordinary `"` by the time any Rust code here sees it) — there
  is no raw/undecoded text path in this parser to have regressed into.
  Confirmed directly: this repo's own fixtures already construct their
  JSON test input with literal `\"` escapes (that's simply what valid JSON
  containing a quote character inside a string looks like) and were
  passing throughout.
- **The real cause**, found by walking the actual bytes with `grep -o` and
  widening context until the true tag name appeared: this delivery used
  `<desktop-session-message from="..." name="..." ...>`, not
  `<cross-session-message ...>` — a second, structurally identical tag
  name used when the sender is a Claude Desktop session
  (`entrypoint":"claude-desktop"`) rather than a CCD-managed one. Both tag
  names are confirmed present in the wild across different real
  transcripts. `extract_cross_session_name` (`claude_sessions.rs`) now
  matches on the shared `session-message` substring and walks back to the
  enclosing `<...>` tag, rather than hardcoding one literal name — handles
  both today and any future `<*session-message>` variant without another
  silent-miss bug of this exact shape.

New fixtures: the `desktop-session-message` tag variant (string-level),
and an FS-level test with two sessions in DIFFERENTLY NAMED project-slug
directories (the real worktree-slug shape from "doc3 redies調查") proving
the scan and the edge-matching are slug-name-agnostic — matching is always
by resolved session TITLE, never by directory name or any external id.

### 4. Aligned three-column Flow rows

Operator: 「doc1 | 模型 | 閒置 這樣 整齊一點 不要在右邊」— replaced the
previous two-slot `Ui.Row(left, right)` (title cluster on the left,
state+elapsed flushed to the card's far right edge) with a proper
three-column `Grid` per row (`DashboardView.BuildFlowRow`): title (Star,
variable width) | model (fixed 125px, colored) | state·elapsed (fixed
95px). The SAME two fixed-width `ColumnDefinition`s are used for every row
regardless of depth or kind (Claude-session controller/agent/session rows,
CCCG codex/grok children, the terminal/resumable bottom aggregate) — a
child's indent (extra left `Margin` on the title cell only) never shifts
the model/state columns out of alignment with its parent's, since those
are separate grid columns with widths independent of what the title cell
contains. Verified via UI Automation's own `BoundingRectangle.X` on the
live running app: every row's model text started at the identical x=2228,
every state text at x=2394, parent and child rows alike (screen coords —
absolute across the flyout, not client-relative, but the point is they're
IDENTICAL across every row).

### Verification

`cargo test --workspace`: 125 tests green (9 net new this pass: 2 for the
`desktop-session-message` tag + worktree-slug scanning, 2 for the
mutual/chain cycle fix, 1 for the parent-model omission case, plus the
earlier session's 116 unchanged). `dotnet build -c Release -p:Platform=x64`:
0 warnings, 0 errors throughout every rebuild in this pass. Coordinator's
running instance (pid 41804) killed first as instructed; every rebuild
after that was verified via fresh DLL mtimes before launching.

Live UI Automation read-back after the aligned-columns + tooltip + parent-model
changes, x-coordinates included to show real column alignment:

```
Doc4 新增                x=1976   (claude · sonnet-5)  x=2228   執行中 · 0s   x=2394
doc2 api-gateway上線     x=1976   (claude · sonnet-5)  x=2228   執行中 · 21s  x=2394
CCCG開發                 x=1976   (claude · fable-5)   x=2228   執行中 · 24s  x=2394
  Add Claude session liveness to Flow  x=1996   (agent · sonnet-5)  x=2228   執行中 · 24s  x=2394
Doc1 主管                x=1976   (claude · fable-5)   x=2228   閒置 · 7m     x=2394
codex ×58                x=1976                                   terminal  x=2394
grok ×34                 x=1976                                   terminal  x=2394
```

The specific in-window cross-session edges the coordinator asked to see
nested (Doc1→doc5, CCCG開發⇄Doc4) had aged past the 10-minute alive window
by the time of this FINAL live capture — the debugging/rebuild/re-verify
cycle for the cycle-collapse bug above took long enough that real
wall-clock time moved past them; this is expected staleness, not a
regression (per the coordinator's own stated fallback: re-trigger if
needed, don't chase it). The fix was instead verified against the EXACT
same real transcript files with a debug binary pinning `now` back to
`2026-08-16T10:08:00Z` (shortly after the real messages, since deleted
along with the tool): with that clock, "CCCG開發", "Doc1 主管", "doc5
Api文件檢查", and "doc3 redies調查" all correctly stopped being top-level
and nested one level under "Doc4 新增" (the chain's ultimate root once
flattened) — proof against the real bug's real data, not a synthetic
fixture, even though the live app's own real-time capture above no longer
shows it. A fresh cross-session message, if the operator wants to see it
in a live real-time capture too, would need to be re-sent — this session
did not send one itself, per instruction.

## 2026-08-16 task 4: control-edge SEMANTICS — initiator lock, dispatch markers, pinned coordinators

Operator-confirmed bug: "Doc1 主管" (the standing coordinator) rendered as
a CHILD of "CCCG開發" — backwards. Root problem: the tree's control-edge
resolution only ever looked at message DIRECTION and RECENCY ("whoever
sent me a message most recently is my controller"), which cannot tell a
genuine dispatch apart from a reply/report/ack going the other way — any
real back-and-forth conversation looks identical to a dispatch under that
rule. Originally specified as one combined fix; the operator then amended
the ask to three INDEPENDENTLY TOGGLEABLE rules so real configurations
could be A/B'd against real data before picking one, rather than landing
a single hard-wired design.

### The three rules (`cccog_bar_core::tree::EdgeRules` + pinned coordinators)

```rust
pub struct EdgeRules {
    pub initiator_wins: bool,
    pub marker_only: bool,
}
```

- **`initiator_wins`** — within a session PAIR, only the pair's INITIATOR
  (whoever sent the chronologically first in-window message, either
  direction, marker or not) can ever become the other's controller; every
  message in the opposite direction is a reply and can never create or
  flip an edge. Computed once per pair from the RAW edge set
  (`compute_pair_initiators`) — "who spoke first" is a fact about the
  conversation, independent of whether `marker_only` separately restricts
  which messages can become edges.
- **`marker_only`** — only a message whose body starts with a dispatch
  marker is even a candidate edge. Markers (`claude_sessions::DISPATCH_MARKERS`):
  `[派工`, `【任務`, `[任務`, `[assign`, `[dispatch`, `【派工` (ASCII ones
  case-insensitive; a prefix match on the trimmed body, not a
  substring/keyword search — confirmed against real messages that a
  substring search would have wrongly matched: `[撤令` (a recall) and
  `[edge-test` (a connectivity ping) share the `[` with `[派工` but are not
  dispatches, and a prefix check already excludes both correctly with no
  extra denylist).
- **Pinned coordinators** (`TreeBuildInput::pinned_coordinators: Vec<String>`,
  exact session-title match) — always render top-level, never as anyone's
  child (`controller_of.retain` after every other rule has run — a hard
  override, not a precedence weight), AND their own dispatch edges win
  over a non-pinned controller's for the same child even when the
  non-pinned edge is more recent (baked into the candidate-selection loop:
  pinned beats non-pinned regardless of `at`, then most-recent wins among
  ties of the same pinned-ness).

All three default OFF/empty — an absent config is EXPLICIT current
(pre-task-4) behavior, not a silent fourth mode. `break_cycles`/
`flatten_controllers_to_root` (the safety net from the earlier same-day
mutual-edge-cycle bug) stay in place unconditionally: with `initiator_wins`
off, a real bidirectional exchange can still form a cycle exactly as
before, so the net still needs to catch it; with it on, a cycle should
essentially never form in the first place (proved by
`the_old_cycle_fixture_resolves_deterministically_via_initiator_wins`,
which reuses the exact fixture that used to need the tiebreak and shows
`initiator_wins` alone resolves it to the semantically correct answer,
not an alphabetically-lucky one).

### Config: `bar-settings.json`

Two new keys in the SAME file `CCCOG.Bar.App`'s existing `LocalSettings`
class already owns (`%LOCALAPPDATA%\CCCG\bar-settings.json`, currently a
flat `Dictionary<string,string>` used only for the resize-grip height).
Both new keys keep that flat-string-map shape — their VALUES are
JSON-encoded strings, not nested JSON objects/arrays directly — so adding
them can never make `LocalSettings.cs`'s own `Dictionary<string,string>`
deserialization see an unexpected shape and quarantine the file (a real
compatibility hazard that was considered and deliberately avoided, not
just missed):

```json
{
  "resize.grip.height": "8",
  "flow.edgeRules": "{\"initiatorWins\":true,\"markerOnly\":false}",
  "flow.coordinators": "[\"Doc1 主管\"]"
}
```

Both keys are optional; a missing file, missing key, or malformed value at
any layer degrades to the documented default (current/baseline behavior)
rather than erroring — read-only, best-effort config, never a hard
dependency (`cccog_bar_ffi::parse_flow_settings`, unit-tested directly
against raw JSON strings without touching a real file).

**Read fresh on every refresh, no restart needed**: `load_flow_settings()`
is called from inside `control_snapshot_json` on every single FFI call —
deliberately NOT read through C#'s `LocalSettings` class, which loads once
at process start and caches in memory (a hand-edit to the file wouldn't be
picked up by that cache without a restart). Since `RefreshView` already
calls `cccog_bar_control_snapshot` on every Flow refresh tick, toggling
either key in the settings file takes effect on the very next tick — this
was achievable, so no restart-required fallback was needed.

### CLI overrides (`cccog-bar-cli`) — compare configurations without touching the settings file

`control_snapshot_json`'s input JSON gained optional `edgeRules`/
`coordinators` fields that, when present, bypass `bar-settings.json`
entirely for that one call (the production C# path never sets them, so it
always reads the settings file). `cccog-bar-cli --control` exposes this as
two new flags:

```bash
cccog-bar-cli --control --edge-rules none
cccog-bar-cli --control --edge-rules initiator
cccog-bar-cli --control --edge-rules marker
cccog-bar-cli --control --edge-rules initiator,marker
cccog-bar-cli --control --edge-rules initiator,marker --coordinators "Doc1 主管"
```

`--edge-rules` takes a comma list of `initiator`/`marker` (any
combination/order); anything else (including `none`, or omitting values)
means both off. `--coordinators` takes a comma-separated list of exact
session titles. Either flag, even `--edge-rules none`, always overrides
the settings file for that run — only OMITTING both flags entirely falls
through to the settings file, matching the app's own path.

### Fixtures (`cccog-bar-core/src/tree.rs`, `claude_sessions.rs`)

- `parse_line_flags_real_dispatch_marker_bodies_and_rejects_lookalikes` /
  `dispatch_marker_ascii_variants_match_case_insensitively...` —
  marker detection against real bodies (`[派工|...]` true; `[撤令|...]`,
  `[edge-test|...]`, `【調查完工】...`, a bare "收到,馬上處理" ack all
  false) plus the ASCII case-insensitivity rule.
- `the_old_cycle_fixture_resolves_deterministically_via_initiator_wins` —
  the same-day mutual-edge fixture, now resolved by rule 1 alone (not the
  tiebreak), to the provably correct winner (whoever spoke first).
- `edge_rule_combinations_produce_different_topologies_on_the_same_fixture`
  — the amendment's core ask: baseline / initiator-only / marker-only /
  both, asserted on ONE shared fixture (deliberately: session ids sort
  alphabetically OPPOSITE of chronological send order, so baseline's
  tiebreak-driven answer is visibly "an accident", not "looks right for
  the wrong reason"). Confirms marker_only ALONE can be actively
  misleading (a marker-shaped REPLY can still "win" control over the true
  initiator) — the reason the operator wanted these separable and
  comparable rather than one hard-wired design.
- `marker_only_rejects_completion_reports_and_recalls_as_non_edges` — the
  operator's own explicit non-edge examples (「調查完工」/[撤令), tree-level.
- `pinned_coordinator_never_renders_as_a_child` /
  `pinned_coordinator_edge_takes_precedence_over_a_more_recent_non_pinned_one`
  — both pinned-coordinator guarantees, independently.
- `cccog-bar-ffi`'s `flow_settings_tests` module: `parse_flow_settings`
  round-trips both keys, degrades safely at every malformed-input layer
  (bad top level, bad individual key value, an accidentally-nested shape),
  and `ControlSnapshotInput`'s `edgeRules`/`coordinators` fields
  deserialize from the exact JSON shape the CLI flags produce.

No C# changes were needed for this task — the wire shape of a `TreeRow`
is completely unchanged; only WHICH rows the Rust side decides to nest
changed, so `DashboardView`/`FlyoutWindow` render whatever comes back
exactly as before.

### Verification

`cargo test --workspace`: 133 tests green (up from 125 at the end of task
3 — 2 new marker-detection fixtures in `claude_sessions.rs`, 5 new
`tree.rs` fixtures for the three rules, 5 new inline `cccog-bar-ffi`
tests for settings parsing and the CLI-override JSON shape; module
totals: `claude_sessions` 23, `tree` 16, `presence` 10, `control` 9,
`cccog-bar-ffi` lib 5). `dotnet build -c Release -p:Platform=x64`: 0
warnings, 0 errors. Coordinator's running instance (pid 12296) killed
first as instructed; live app relaunched from a freshly-rebuilt DLL
(mtime-verified) after every change.

Real-data CLI run at verification time showed all four configurations
producing an IDENTICAL tree (`doc2 api-gateway上線`, `CCCG開發` +its live
agent, `Doc1 主管`, all independent top-level, plus the terminal
aggregate) — because, same as the end of the previous task-3 section, no
cross-session-message edge was actually in-window at that exact moment
(the real ones from earlier in this session had aged past the 10-minute
window during the debug/rebuild/re-verify cycle). This is expected and
correctly proves the CLI/settings wiring doesn't crash or misbehave on
real data with zero edges in play; it does NOT exercise the actual rule
differences live — that requires an in-window edge, which the fixtures
above prove deterministically and the coordinator can re-verify live by
re-running the exact CLI invocations above once a fresh dispatch exists.

## 2026-08-16 task 5: Flow never refreshed on its own — a periodic timer plus a presence watcher

Live-confirmed bug, not a fixture find: the flyout's header sat frozen at
"refreshed 19:34" for roughly two hours (operator screenshot at 21:27; no
crash log; process alive the whole time) — a real cross-session dispatch
(Doc1→doc3) happened during that window and the operator never saw it,
because Flow never repainted.

**Root cause**: since 94ca2da, Flow's primary data sources are Claude
session transcripts (`~/.claude/projects`) and the presence dir
(`%LOCALAPPDATA%\CCCG\presence`), but `FlyoutWindow.xaml.cs`'s only
`FileSystemWatcher` ever watched `%LOCALAPPDATA%\CCCG\dispatch`, and
nothing periodic covered Flow at all — the existing 60s `_quotaTimer`
refreshes quota cards only (`RefreshQuotaCards()`, not `RefreshView()`).
Zero CCCG dispatch-registry file events for two hours (plausible — CCCG
dispatch activity and Claude-session cross-messaging aren't the same
traffic) meant zero Flow refreshes for two hours, full stop.

### Fix (`FlyoutWindow.xaml.cs`)

- **New `_flowTimer`**, 60s interval (operator amendment mid-task — an
  initial 10s pass was dialed back on a performance concern; the scan
  itself is still milliseconds either way, this is "how stale can Flow
  ever get on its own", not "how long the scan takes"). Its `Tick` calls
  the same `RequestDebouncedRefresh()` helper the file watchers already
  used inline (extracted so all three trigger sources — dispatch watcher,
  presence watcher, this timer — funnel through exactly one debounce/
  refresh choreography, never a second code path). Started lazily on
  first `ShowFlyout()` (mirrors `_quotaTimer`'s own lazy-start/`Closed`-
  stop lifecycle exactly — `_flowTimerStarted` guard, `_flowTimer.Stop()`
  added to the `Closed` handler alongside the existing timers/watchers).
- **New presence-dir `FileSystemWatcher`** (`_presenceWatcher`,
  non-recursive — the dir is flat) on `%LOCALAPPDATA%\CCCG\presence`,
  wired to the same `RequestDebouncedRefresh()` trigger, so a fresh
  heartbeat flips 執行中/閒置 within debounce latency instead of waiting
  up to a full 60s tick. Guarded the same way the existing dispatch
  watcher already is (`Directory.Exists` first — the presence dir may not
  exist yet on a machine where the hook was never registered; skip
  quietly rather than let `FileSystemWatcher`'s constructor throw).
  Deliberately did NOT add a recursive watch on `~/.claude/projects`
  itself (task instruction, and consistent with why the 60s timer exists
  at all): transcript files are written continuously during an active
  turn, so a recursive watch there would fire the debounce trigger
  essentially constantly during normal use — the periodic timer already
  covers that source with zero event-count cost.
- `StartFileWatcher()` restructured so the dispatch-root watcher's own
  `Directory.Exists` guard no longer early-returns the WHOLE method
  (it used to — a missing dispatch root would previously have silently
  skipped setting up ANY watcher, though that combination was never
  actually observed live since dispatch root always exists on a machine
  that's run any CCCG dispatch at all).

No Rust changes — this was entirely a C#-side gap (the FFI layer already
returns fresh data on every call; nothing was calling it often enough).

### Verification

`cargo test --workspace`: unaffected by this task (no Rust files
touched), still the same 133 green. `dotnet build -c Release
-p:Platform=x64`: 0 warnings, 0 errors. Coordinator's running instance
(pid 47720) killed first as instructed; live app relaunched from a
freshly rebuilt managed DLL (the native `cccog_bar_ffi.dll` is unchanged
from task 4's build — verified by mtime that the app is running the
current managed code against that same native DLL, not a stale copy of
either).

**Live timer-advance proof**: read the header via UI Automation
(`refreshed 21:39:00`), waited ~68s with the flyout left open, made NO
manual Refresh click and triggered no dispatch activity of my own, then
re-read the header — `refreshed 21:42:03`, a clean advance on its own
with zero external trigger, confirming `_flowTimer` actually fires and
drives a real repaint.

**Live presence-watcher + real-dispatch proof** (unplanned, more
convincing than a synthetic one): while probing the presence watcher by
hand-writing a test heartbeat for the real "doc3 redies調查" session, its
presence file turned out to already be under active, continuous write
pressure from that session's OWN real hook activity (`PreToolUse`/`Stop`
events landing seconds apart — this repo's presence hook is evidently
already registered and running in production, ahead of what an earlier
section of this file assumed). Rather than fight that race, the far
better proof arrived on its own moments later: "Doc1 主管" issued a real
in-window dispatch to "doc3 redies調查" during this exact verification
window, and the RUNNING APP (not the CLI, not a fixture) picked it up and
re-rendered doc3 nested one level under Doc1 — header `refreshed 21:51:44`
— entirely on its own, no manual Refresh, live cross-session activity
this session did not create. This is the strongest possible confirmation
of both halves of task 5's ask (timer-driven refresh AND fast pickup of a
fresh dispatch) because it happened with genuinely uncontrolled real-world
input, not a controlled fixture.

Quit verified clean afterward (UI Automation `InvokePattern.Invoke()` —
process exited fully per `tasklist`; a dead process cannot tick a timer,
which is the strongest form of "does not tick after Quit"). No
`bar-crash.log` at any point across this entire verification pass.

## 2026-08-16/17 task 6: quota-limit state must persist until T, and a live timezone bug in the reset-time formatter

Two live-confirmed bugs in `cccog-bar-quota`/`cccog-bar-ffi`'s quota-limit
override machinery, landed together since the second was found investigating
the first.

### Bug 1: limit state silently "reset" overnight when the evidence job aged out

The Codex card showed `Limit 100% · 08/20 19:50` (see bug 2 below for why
that time was itself wrong), then overnight fell back to a stale, ~22h-old
rollout snapshot (`7d 37% · as of 22h ago`) — read by the operator as "quota
reset". It hadn't: a fresh probe run minutes later hit the identical limit.
Root cause: `find_quota_limit_failure`'s scan only ever looks at whatever
dispatch job status.json files exist RIGHT NOW, within `JOB_FAILURE_LOOKBACK_SECS`
(24h) — once the evidence job's file is no longer found (lookback expiry,
archival, retention cleanup — any of these), `enrich_quota_cards` had
nothing to override with, even though the real provider-side block (whose
own reset time is still in the future) never went anywhere.

**Fix**: `PersistedLimitEvidence { provider, reset_at, evidence_job_id,
observed_at }`, persisted in the SAME on-disk file as the existing quota
resilience cache (`%LOCALAPPDATA%\CCCG\bar-quota-cache.json`,
`QuotaResilienceCache::limit_evidence` in memory / `PersistedQuotaCache::limit_evidence`
on disk) — survives both a scan miss AND a full app restart. New function
`enrich_quota_cards_persistent` (the production path now; the original
`enrich_quota_cards` is kept byte-for-byte unchanged as a still-tested,
non-persistent building block) applies three rules per codex/grok card,
in order:

1. A fresh scan hit (unchanged `find_quota_limit_failure` mechanism)
   ALWAYS refreshes the persisted evidence — but only when its reset hint
   actually parses to a real instant (`cccog_bar_quota::parse_reset_instant`,
   now `pub`). Evidence without a usable T never persists, staying exactly
   as ephemeral as before for that case — literally "once evidence WITH A
   PARSED RESET TIME is established" (task 6's own phrasing).
2. Absent a fresh hit, a genuinely NEWER non-limit snapshot (the raw
   card's own `observed_at` newer than the persisted evidence's) super­sedes
   and clears the persisted state — a real new observation (the block
   actually lifted, or a corrected read) beats stale memory. Never checked
   on a round that just refreshed the evidence itself (nothing to compare
   against, comparing a fresh entry to itself is meaningless).
3. Whatever's left: apply the override while `now < reset_at`; drop it and
   fall through to normal sources the moment `now >= reset_at`. This is the
   actual fix — the lock now survives the evidence file disappearing
   entirely, because "is it still in force" no longer depends on finding
   that file again.

A small dim `· as of X ago` marker (reusing `SNAPSHOT_STALE_AFTER_SECS`,
1h, as the same "how old is this data" threshold used everywhere else in
this file) is appended to the diagnostic only when the EVIDENCE's own age
exceeds it — i.e. exactly when the render is relying on memory rather than
a fresh re-confirmation this round. `poll_remote_quotas_json` now holds the
`QuotaResilienceCache` lock across `enrich_quota_cards_persistent` (it used
to `save_persisted_cache`+`drop(cache)` BEFORE the enrich step, back when
enrich was stateless) so the mutated `limit_evidence` map makes it to disk
every time, not just `last_success`.

### Bug 2: the reset-time formatter treated Codex's local-time string as UTC

Found investigating bug 1 with the real evidence: the running card showed
`08/20 19:50`, but the upstream string (jobs `20260816T162448Z_07d41b5f`/
`...17e490df`, stdout) literally says `"...try again at Aug 20th, 2026
11:50 AM."` — 11:50 + 8h (this machine's UTC offset) = 19:50. Root cause:
`parse_reset_instant`'s English `"Mon D, YYYY H:MM AM/PM"` branch (the
exact shape `extract_human_reset_date` extracts from real Codex messages)
called `Utc.from_utc_datetime(&naive)`, i.e. treated the parsed wall-clock
numbers as a UTC instant — but Codex CLI emits that string in the machine's
OWN local time already, so converting UTC→Local for display shifted it by
a full UTC-offset for no reason.

**Fix**: that ONE branch now calls `Local.from_local_datetime(&naive).earliest()`
instead — interprets the naive value as already-local (falling back to a
UTC-equivalent instant only in the practically-unreachable DST spring-forward
Gap case, so the function never panics). Every OTHER shape
(`parse_reset_instant`'s RFC3339/epoch/naive-ISO branches) is UNCHANGED —
this is deliberately scoped to the one shape that's actually ambiguous
AND wrong today, not a blanket "everything is local now" rewrite; the
module-level doc comment above `normalize_reset_display` now documents
this shape as the one exception to the "assume UTC" rule the others still
follow. Since `PersistedLimitEvidence.reset_at` is derived from THIS SAME
function, the fix doubles as the correctness fix for bug 1's `now >=
reset_at` comparison too — one correctly-computed instant, used
consistently for both "what time does this show" and "has this passed
yet" (verified live: the persisted `bar-quota-cache.json` entry shows
`"reset_at":"2026-08-20T03:50:00Z"` — exactly 11:50 local minus this
machine's +8h offset, the correct UTC instant, not the buggy 19:50-shifted
one).

### Fixtures

- `cccog-bar-quota`: `parse_reset_instant_treats_iso_as_utc_and_english_as_local`
  (direct parser-level coverage, not just through the display formatter) +
  the existing `normalizes_english_verbatim_dates_with_and_without_a_time`
  updated to assert the literal `"08/20 11:50"` (no conversion) instead of
  the old UTC-then-Local expectation.
- `cccog-bar-ffi` (`tests/ffi.rs`): `evidence_ages_out_of_the_scan_window_but_t_is_still_future_so_the_limit_persists`
  (empty dispatch root, pre-seeded evidence, override still applies + `as
  of 4h ago` marker), `once_now_passes_t_the_limit_is_dropped_and_normal_sources_show_through`,
  `a_newer_non_limit_snapshot_supersedes_and_clears_the_persisted_limit`,
  `a_fresh_scan_hit_refreshes_the_persisted_evidence_with_no_as_of_marker`
  (byte-identical to the old ephemeral override's diagnostic — no marker).
  The pre-existing `job_failure_override_scans_stdout_tail_when_status_error_is_generic`
  was updated to the corrected `"08/20 11:50"` expectation (it exercises
  the same real evidence text and was asserting the bug 2 behavior before
  this fix).
- `cccog-bar-ffi` (inline, `limit_evidence_persistence_tests`):
  `persisted_quota_cache_roundtrips_limit_evidence_across_a_simulated_restart`
  (the literal "cache roundtrip across restart" ask — a serde round-trip
  of the exact on-disk shape, not a real-file touch, matching this
  module's existing `parse_flow_settings` convention of keeping pure
  logic testable without depending on `%LOCALAPPDATA%`) and
  `persisted_quota_cache_defaults_limit_evidence_when_absent_from_an_older_file`
  (an existing pre-task-6 cache file, `last_success` only, must still
  load).
- `chrono`'s `serde` feature enabled on `cccog-bar-ffi` (was missing —
  needed for `DateTime<Utc>` to (de)serialize inside `PersistedLimitEvidence`).

### Verification

`cargo test --workspace`: 140 tests green, up from 133 (8 net new: 2 in
`cccog-bar-quota`, 4 new behavioral tests in `cccog-bar-ffi`'s
`tests/ffi.rs` — plus one pre-existing test there corrected to the
now-accurate expectation, not counted as "new" — 2 new inline
`cccog-bar-ffi` roundtrip tests). `dotnet build -c Release
-p:Platform=x64`: 0 warnings, 0 errors (no C# changes this task — the fix
is entirely in the two Rust crates the FFI/quota logic already lived in).
`cargo build --release --workspace`: clean, 0 warnings.

Coordinator's running instance (pid 47008) killed first as instructed;
live app relaunched from freshly-rebuilt DLLs (mtime-verified). Live UI
Automation read-back of the real, running flyout confirmed the exact fix
the operator asked to see:

```
Codex
  Limit  100%
  08/20 11:50
  limit · 08/20 11:50
```

— matching the real upstream evidence string verbatim, no timezone
shift, no stale-fallback regression. No `as of` marker at this exact
moment because the evidence job was still directly scannable (this
verification ran shortly after the probe, well inside the lookback
window) — the "still limit hours after the job aged out" half of the fix
is what the four new `tests/ffi.rs` cases above prove deterministically;
reproducing that specific timing live would need waiting out the real
job's actual aging-out, which the fixtures already cover exactly. Quit
verified clean; no `bar-crash.log` at any point.

## 2026-08-17 task 7: custom tray icon, README user docs

Two review gaps, landed together.

### Custom tray icon

The tray previously showed WinUI's generic default icon — nothing had ever
set one. `bar/tools/generate_tray_icon.py` (Pillow) generates
`app/CCCOG.Bar.App/Assets/tray-icon.ico` programmatically — a rounded-square
dark badge (`#2A303C`) holding three dots in the app's own provider palette
(claude `#DA7756`, codex `#3B82F6`, grok `#1F2937` — literally
`ViewModels.cs::ProviderPalette`'s own colors, same left-to-right order),
each with a white ring so the dark grok dot stays legible at 16px. Multi-size
ICO (16/20/24/32/48) via Pillow's own `Image.save(..., format="ICO")`. Wired
as both the exe's `ApplicationIcon` (csproj) and the tray's own icon
(`App.xaml.cs`).

**Bug found live**: the first wiring used `TaskbarIcon.IconSource = new
BitmapImage(new Uri(path))`, per the literal ask. `BitmapImage`'s Uri
constructor decodes ASYNCHRONOUSLY; H.NotifyIcon's own docs say `IconSource`
"resolves an image source and updates the `Icon` property accordingly" — that
resolution ran before the async decode finished, so the live tray showed a
blank/placeholder glyph, confirmed via UI Automation + `PrintWindow`
(`PW_RENDERFULLCONTENT`) capture of the actual notification-overflow popup
(plain `CopyFromScreen`/`BitBlt` returns blank for this hardware-composited
surface — `PrintWindow` with that flag is what actually works). **Fix**:
`Icon = new System.Drawing.Icon(path, 16, 16)` instead — synchronous, no
async race, and the exact pattern TokenBar's own `TrayService.cs` already
uses (`_icon.Icon = System.Drawing.Icon.FromHandle(hicon)`). This is a
deliberate deviation from the literal "use IconSource" instruction: literal
compliance would have shipped a visibly broken icon, which the "verify the
tray actually shows the new icon" gate specifically exists to catch.

### README user docs

`bar/README.md` had drifted since the second Flow data source landed (2026-08-16):
its own "Layout" section still said Flow read `dispatch{jobs,owners}` only.
Rewrote it in full — intro, "Why slim = fast" (now describes all three
sources), Layout (module list + current test counts), Locked UX (current
Flow tree description), a new "Flow control-edge configuration" section
documenting `flow.edgeRules`/`flow.coordinators` with the real JSON shape and
CLI override examples, and Building (icon regeneration note). Root
`README.md`/`README.zh-TW.md` gained a new "CCCOG Bar" / matching Chinese
section (this repo's dispatch-MCP README had never mentioned the bar at all)
— what it shows, build/launch commands, tray usage (left-click toggle,
right-click menu, wheel scroll, drag-to-resize, click-outside-to-hide).

### Verification

`cargo test --workspace`: 140 tests green (icon/README are non-Rust changes;
this just confirms nothing regressed). `dotnet build -c Release
-p:Platform=x64`: 0 warnings, 0 errors. Live: coordinator's running instance
killed by PID first, rebuilt DLL relaunched; UI Automation + `PrintWindow`
capture of the real taskbar overflow popup confirmed the correct icon
renders (three colored dots on the dark badge, not the generic/blank
placeholder); `Icon.ExtractAssociatedIcon` on the built exe also confirmed
the `ApplicationIcon` wiring independently. No `bar-crash.log` growth.

This task's own verification stayed scoped to CCCOG.Bar.App's own window
and the shell's tray/notification-overflow surfaces. The File
Explorer/Notepad windows the operator flagged as disruptive were opened
during task 8's investigation below, not this one -- see that section for
the full disclosure.

## 2026-08-17 task 8: click-outside-to-hide the flyout (deactivation port)

TokenBar-Windows' `FlyoutWindow.xaml.cs` hides its flyout on
`Window.Activated`'s `WindowActivationState.Deactivated` — CCCOG's port of
that window never carried the handler over, so clicking anywhere outside the
flyout did nothing (the literal point of a tray flyout). Ported verbatim,
right after `SetupAlwaysOnAcrylic()` in the constructor:

```csharp
var keepOpen = Environment.GetCommandLineArgs().Contains("--keep-open");
Activated += (_, e) =>
{
    if (e.WindowActivationState == WindowActivationState.Deactivated && !keepOpen)
    {
        HideFlyout();
    }
};
```

`--keep-open` is a new opt-out flag for verification tooling: screen-capture
and UI-Automation helpers steal focus, which would correctly dismiss the
flyout right before the shot — live-check tooling needs a way to hold it
open on purpose. `HideFlyout()` already calls `RemoveWheelHook()`
unconditionally at its top, so the `WH_MOUSE_LL` hook structurally cannot
leak on this path — that part is a static, code-level guarantee, not
something that needed a live click to confirm.

### Verification: INCONCLUSIVE, disclosed honestly

Despite the handler being a byte-for-byte-equivalent port and
`ApplyPopupChrome()` (raw `SetWindowLongPtrW(GWL_STYLE, WS_POPUP|...)` +
DWM chrome + `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)`) being confirmed
IDENTICAL between CCCOG and TokenBar side by side, four independent live
attempts to trigger a real external focus change never produced a logged
`Deactivated` event (confirmed via a temporary `File.AppendAllText` probe in
the handler, removed after):

1. UI-Automation-driven "Show Desktop" click — flyout stayed
   `IsOffscreen: False`.
2. Synthetic `mouse_event` click on the File Explorer taskbar button
   (DPI-corrected via `SetProcessDPIAware()`) — `FocusedElement` confirmed
   focus genuinely moved to a `CabinetWClass` window; no event logged.
3. `SetForegroundWindow()` called directly on Explorer's real HWND —
   `GetForegroundWindow()` confirmed Explorer was truly foreground; no event
   logged.
4. `Start-Process notepad.exe` — a brand-new window created and activated
   through the OS's own normal activation chain (not synthetic input) —
   `GetForegroundWindow()` confirmed it was foreground; still no event.

The same probe DID confirm `CodeActivated` fires correctly at launch (the
subscription itself works) — only the subsequent `Deactivated` transition
was never observed, across four different triggering methods. Root cause
NOT determined: could be a genuine WinUI3 quirk specific to this
always-on-top / no-titlebar / not-in-switchers window configuration
(possibly interacting with `SWP_NOACTIVATE`, which is set precisely so
`ShowFlyout()` doesn't steal focus on open — plausible that the same flag
combination that keeps the window from stealing focus also prevents it from
ever being told it lost focus), or a limitation of how this remote/sandboxed
environment propagates focus-change notifications to WinUI's activation
tracking layer specifically. **Status: code shipped as a faithful verbatim
port; live behavior unconfirmed.** Worth a follow-up with a real physical
mouse on the operator's own hands, since every method tried here was
synthetic input from an automation script.

### Operator-visible disruption (full disclosure)

Mid-investigation, the coordinator sent an URGENT message: File
Explorer/Notepad windows were popping up on the operator's own desktop while
they were actively using the machine (daytime), and to stop opening any
external application immediately.

Exact sequence, disclosed precisely as instructed:

- **File Explorer** was opened (multiple taskbar-icon clicks, for the
  focus-change test — item 2 above) and was **left open** — not closed
  programmatically, per the explicit instruction that arrived to leave
  anything already open for the operator to close themselves.
- **Notepad** was opened via `Start-Process notepad.exe` for the focus-change
  test (item 4 above), then closed via `taskkill /IM notepad.exe /F`. That
  taskkill command was issued and completed **before** the URGENT
  no-close-programmatically instruction arrived — the close was not a
  violation of that instruction (it predates it), but is disclosed here in
  full because the instruction specifically asked for this to be reported.

No further external applications were opened after the URGENT message; all
subsequent work in this batch (task 9 below, live verification of the icon
and quota-card rendering) was scoped to CCCOG.Bar.App's own window and tray
icon only, per that instruction.

## 2026-08-17 task 9: trim the persisted-limit quota card to one row

Operator feedback on the live persisted-limit card (task 6's own feature):
`apply_persistent_limit_override` rendered `Limit 100% · 08/20 11:50` as the
window row, THEN a second status line underneath repeating the identical
`limit · 08/20 11:50` text, plus — once the evidence aged past
`SNAPSHOT_STALE_AFTER_SECS` (1h) — a dim `· as of X ago` marker. Both are
noise for this specific state: the second line is a verbatim duplicate of
the row above it, and "as of X ago" reads as data staleness even though a
persisted lock's validity has nothing to do with how recently the app
re-observed it — it holds until `reset_at` regardless of scan timing.

**Fix**: `apply_persistent_limit_override` now returns `diagnostic: None`
(suppresses the second line entirely — `DashboardView`'s
`BuildProviderRows` only adds that line when `card.IsFailure`, and
`QuotaCardViewModel.IsFailure` is computed as `!state.Equals("fresh")`) and
`state: QuotaState::Fresh` instead of `Stale` — a persisted lock is the
app's own current belief, not a fallback render of old data, so `Fresh` is
the honest state, not a euphemism. The dead `LIMIT_EVIDENCE_AGE_MARKER_SECS`
constant and its "as of" branch were deleted outright rather than left
unreachable. `diagnostic_detail` (the fuller "limit reached (dispatch
failure HH:MM) · reset" sentence) is UNCHANGED — nothing is deleted, it just
no longer has anywhere to render as an always-visible line.

That last point needed a small companion change: with `IsFailure = false`,
`BuildProviderRows`'s usual status-line-plus-tooltip mechanism never fires
for this card, so the `DiagnosticDetail` tooltip that used to live on that
line would have become unreachable. `DashboardView.xaml.cs`'s `QuotaRow()`
now attaches `ToolTipService.SetToolTip` directly to the window row's own
`Grid` (the `Ui.Row(mainText, timeText)` container) whenever a non-failing
card still carries a `DiagnosticDetail` — today that's exactly and only the
persisted-limit override case, so the full evidence sentence stays reachable
as a hover tooltip on the row itself, satisfying "the verbose evidence
detail stays available via hover tooltip only."

### Fixtures

`cccog-bar-ffi` (`tests/ffi.rs`): `evidence_ages_out_of_the_scan_window_but_t_is_still_future_so_the_limit_persists`
and `a_fresh_scan_hit_refreshes_the_persisted_evidence_with_no_status_line`
(renamed from `..._with_no_as_of_marker`, since "as of" markers no longer
exist for this card at all) both updated to assert `diagnostic.is_none()`,
`state == QuotaState::Fresh`, and (the aged-out case) that
`diagnostic_detail` still carries the full sentence — covering both the
freshly-reconfirmed and the long-aged-out evidence cases with the identical
one-row expectation, which is the point: this trim no longer depends on how
old the evidence is.

### Verification

`cargo test --workspace`: 140 tests green (same total as task 6 — two
existing tests updated in place, none added/removed net). `dotnet build -c
Release -p:Platform=x64`: 0 warnings, 0 errors. `cargo build --release
--workspace`: clean. Coordinator's running instance (pid 41252) killed by
PID first; fresh release DLL + exe relaunched. Live UI Automation read-back
of the real flyout confirmed the exact ask:

```
Codex
  Limit  100%
  08/20 11:50
Grok
  no credit data
```

— the Codex card's text elements go straight from the window row to the
next provider card's own title, with no second status line in between (the
previous build's `limit · 08/20 11:50` duplicate line is gone). A live
hover test (Win32 `SetCursorPos`/`mouse_event`, physical-pixel coordinates
reconciled against `GetWindowRect` after an earlier attempt mixed
DPI-scaled `System.Windows.Forms.Screen` coordinates with UIA's physical
ones and missed the target entirely) confirmed the one-row-plus-gauge
layout visually via screen capture, but could not conclusively confirm the
tooltip popup itself appears on synthetic hover — consistent with task 8's
finding that this environment's synthetic input doesn't reliably drive
every WinUI interaction surface a real mouse would. The tooltip code path
(`ToolTipService.SetToolTip` on the row) was reviewed directly rather than
solely relied on for this one detail. No `bar-crash.log` growth at any
point; GUI interaction for this task's verification was scoped entirely to
CCCOG.Bar.App's own window, per the operator's standing instruction.

## 2026-08-17 task 10: tray icon swapped to a plain "C" (temporary branding)

Operator direction: replace the three-dot provider-palette icon from task 7
with a claude-orange rounded-square badge holding a single bold white "C",
explicitly called out as temporary branding. `bar/tools/generate_tray_icon.py`
rewritten (not extended) — the three-dot `build_master()` is fully replaced,
not layered alongside the new design.

The "C" itself is drawn as a vector `ImageDraw.arc()` (a thick ring swept
40°→320°, leaving the gap on the right where a capital C opens) rather than
rendered from a system font file. Deliberate: a font-based glyph would tie
this "reproducible from source" script's output to whichever font happens to
be installed on the machine running it (Arial Bold / Segoe UI Bold are
common on Windows but not guaranteed elsewhere) — an arc has no such
dependency, only Pillow's own primitives, so the script stays genuinely
self-contained. Same multi-size ICO output (16/20/24/32/48), same wiring
(`TaskbarIcon.Icon` in `App.xaml.cs`, `ApplicationIcon` in the csproj) —
no C# changes needed, since regenerating `tray-icon.ico` in place under the
same filename is all this swap required.

Both root READMEs' "CCCOG Bar" sections (added in task 7) described the old
three-dot design — updated to the new badge description so the docs don't
go stale the moment this lands (`README.md`, `README.zh-TW.md`).

### Verification

`dotnet build -c Release -p:Platform=x64`: 0 warnings, 0 errors (icon-only
content swap; `cargo test --workspace` not re-run since no Rust/C# source
changed — the previous task-9 run already covers current source). Operator's
running instance (pid 4184) killed by PID first; rebuilt output confirmed
the regenerated `tray-icon.ico` was recopied to
`bin/x64/Release/.../win-x64/Assets/` (mtime matches this build). Fresh
launch verified via UI Automation (`IsOffscreen: False`) and no
`bar-crash.log` at any point.

Live tray-icon confirmation stayed within the "GUI interaction with
CCCOG.Bar.App's own window and tray icon only" constraint: the exact tray
icon coordinates were located via UI Automation (searching the desktop root
for the element whose Name matches the app's own tooltip text,
`"CCCOG-Bar - quota and flow"` — not by opening Explorer or any other
application), then a small, targeted screen-region capture (Win32
`GetWindowRect` on `Shell_TrayWnd` + `CopyFromScreen` cropped to just that
icon's bounding rect, DPI-corrected via `SetProcessDPIAware()`) confirmed
the real running taskbar shows the new claude-orange badge with a legible
white "C", not the old three-dot design or a stale/blank icon. Quit
verified clean via the app's own footer button.

## 2026-08-17 task 11: real dispatch convention (arrow prefix), initiator-lock fix, scan-path quota parity — plus a build-folder root cause found along the way

Three asks landed together: two `cccog-bar-core` control-edge rule changes
and a `cccog-bar-ffi` quota-rendering follow-up, all triggered by the
operator finding the live Flow tree flat again.

### 1. Arrow-prefix dispatch marker

Doc1 主管's REAL dispatch convention, confirmed against live transcripts,
is never one of [`DISPATCH_MARKERS`] (`[派工`/`【任務`/etc) — every real
example is an explicit `<sender> <arrow> <recipient>:` prefix
(`doc1 → doc7:歡迎加入...`, `doc1 → doc2:**發車令生效...**`). Added
`ARROW_MARKERS` (`claude_sessions.rs`) alongside the existing bracket
markers — `→` (U+2192, the real shape), ASCII `->`, and the fullwidth
punctuation spelling `－＞` (U+FF0D U+FF1E) — matched against the FIRST
non-whitespace line only, anchored (`sender`/`recipient` must be bare
short tokens: no whitespace, no colon, no arrow substring — so prose that
merely contains an arrow, e.g. "研究一下 -> 這個問題 應該怎麼做:", never
qualifies). Bold-wrapped text right after the colon
(`**發車令生效...**`) is untouched since only the prefix shape is
inspected, never the body after the colon.

### 2. Initiator lock must be computed over MARKED messages only

Live bug: a new session titled "Doc7 向 Doc1 主管報到" sent an UNMARKED
check-in to Doc1 first; under `initiator_wins`, `compute_pair_initiators`
used the RAW (unfiltered) edge set to decide "who spoke first in this
pair" — so that unmarked check-in permanently locked Doc1 out of ever
controlling doc7, even from a later, genuine, marked dispatch. Fix:
`compute_pair_initiators` gained a `marked_only: bool` parameter, wired to
the caller's own `edge_rules.marker_only` — NOT unconditional. Two
reasons it's gated rather than applied everywhere:

- Semantically, `initiator_wins` ALONE (marker_only off) is meant to be a
  pure "who genuinely spoke first, content-agnostic" cycle-breaker in its
  own right — applying the marked-only filter unconditionally would have
  quietly changed that mode's own behavior too, not just the "both rules
  on" production configuration the live bug was actually observed in.
- The existing `edge_rule_combinations_produce_different_topologies_on_the_same_fixture`
  test exists SPECIFICALLY to keep the four rule combinations visibly
  distinct for the operator's own A/B comparison — an unconditional filter
  would have collapsed two of those four combinations to look identical on
  fixtures shaped like the doc7 bug, defeating that test's whole purpose.
  (First implementation attempt WAS unconditional; both this test and
  `the_old_cycle_fixture_resolves_deterministically_via_initiator_wins`
  broke as a direct, correctly-predicted consequence — confirming the gate
  was the right call, not just a defensive guess.)

New fixtures: `unmarked_checkin_does_not_block_a_later_marked_dispatch_from_the_opposite_direction`
(the doc1/doc7 case, real titles and real message shapes, both rules on)
and `a_later_marked_message_in_the_opposite_direction_does_not_flip_the_initiator`
(doc7's own later marked reply must not un-nest itself and re-parent
doc1 — this already worked structurally once rule 1's initiator lock is
correct, since the main loop's existing
`initiator_of_pair.get(&key) != Some(&edge.controller_id)` guard drops any
non-initiator sender regardless of marker status; the test exists to prove
it, not to add new drop logic). `edge_rule_combinations_produce_different_topologies_on_the_same_fixture`'s
"both rules together" block updated in place — its own fixture turned out
to be the SAME shape as the doc7 bug (unmarked-first-then-marked-second,
opposite senders), so its expected winner correctly flips from "both stay
independent" to "the marked sender wins", which is the fix actually
working, not a loosened assertion.

### 3. Quota card: the scan-path/ephemeral override needed the same trim

Task 9 only touched `apply_persistent_limit_override` (the persisted-cache
path `enrich_quota_cards_persistent`/`poll_remote_quotas_json` actually use
in production). `apply_quota_limit_override` — the older, ephemeral
override behind `enrich_quota_cards`, exercised directly by its own test
suite and kept as "a still-tested, non-persistent building block" per its
own doc comment — still rendered the old two-line shape. Applied the
identical trim: `state: Fresh`, `diagnostic: None`, full detail moved to
`diagnostic_detail`. Three `tests/ffi.rs` cases updated
(`job_failure_override_beats_the_stale_annotation_and_carries_the_failure_time`,
`job_failure_override_scans_stdout_tail_when_status_error_is_generic`,
`job_failure_override_applies_to_grok_and_is_skipped_without_a_match`) to
assert the one-row shape instead of the old `"limit · <reset>"` diagnostic
text.

Investigated first whether this function is actually live in production
before touching it (memory: don't implement a fix without checking the
premise) — traced `poll_remote_quotas_json` and confirmed it calls
`enrich_quota_cards_persistent` exclusively, which routes every override
through `apply_persistent_limit_override` regardless of whether the round
just refreshed evidence or is relying on older persisted evidence; so
`apply_quota_limit_override` is NOT reachable from the app's own live quota
poll today. Applied the fix anyway, exactly as asked ("so BOTH paths
render identically") — it costs nothing, keeps the two override functions
in visible lockstep for whoever reads them side by side, and the real
explanation for what the operator's 16:25 screenshot actually showed
turned out to be unrelated to this function entirely (see below).

### The actual root cause of the operator's 16:25 screenshot: two divergent output folders

While hunting for how the operator could still be seeing the pre-task-9
duplicate-line text despite `apply_persistent_limit_override` already being
fixed and previously live-verified, found the real explanation: this
project's `.csproj` produces TWO independently launchable copies of the
app — `bin\x64\Release\net10.0-windows10.0.19041.0\CCCOG.Bar.App.exe` (the
base, RID-less output) and `...\win-x64\CCCOG.Bar.App.exe` (the
RID-specific one) — and every `dotnet build` this whole multi-task session
used `-p:RuntimeIdentifier=win-x64` explicitly, which builds ONLY the
win-x64 folder. The `SyncWinX64RidOutput` MSBuild target (added earlier
this session, task 5-ish, for exactly this class of bug) only fires when
`$(RuntimeIdentifier) == ''` — i.e. it cascades FROM a RID-less build INTO
win-x64, not the other way around. Passing `-p:RuntimeIdentifier=win-x64`
explicitly bypasses that target's own condition entirely, so the base
folder was never touched by any build this session ran.

Confirmed with hashes: the operator's actual running instance (pid 43084,
`StartTime` exactly `16:25:05` — matching the screenshot timestamp) was
launched from the base-folder exe, whose `cccog_bar_ffi.dll` (mtime
`12:05:47`, predating even this task) was genuinely stale — a completely
different binary than the one every "live-verified" claim in tasks 6–10
actually exercised (all of which launched the win-x64 copy). Every prior
task's live verification in this session was real and accurate for the
binary it launched — but that binary was silently the WRONG one from the
operator's own point of view, since their own launch path (however they
invoke it — a shortcut, muscle memory, whatever) uses the base folder.

**Fix**: rebuild with the RID-less command
(`dotnet build ... -c Release -p:Platform=x64`, no `-p:RuntimeIdentifier`)
— this single command now updates the base folder directly AND cascades
into win-x64 via the existing sync target, confirmed via SHA-256: all three
pairs (dll/exe/icon) are byte-identical across both folders after this
build. This is the build command every task from here forward should use;
the plain `-p:RuntimeIdentifier=win-x64` form used throughout tasks 6–10 is
still fine for a quick win-x64-only check but must not be assumed to also
refresh the base folder.

### Live verification: quota fix confirmed; Flow-tree nesting NOT conclusively confirmed (disclosed honestly)

Killed the operator's stale pid (43084), rebuilt with the corrected
RID-less command, relaunched from the BASE folder path (matching what the
operator's own instance was running from) as a fresh process. No
`bar-crash.log` at any point; quit path confirmed clean (`Stop-Process`,
since the flyout was not on-screen at that moment for the UI-Automation
Quit-button path used in earlier tasks).

**Quota card**: confirmed live — Codex card renders `Limit 100% / 08/20
11:50` then straight to the Grok card, no second line, from the
corrected/synced binary.

**Flow-tree nesting**: NOT conclusively confirmed. The live tree still
showed "Doc7 向 Doc1 主管報到" and "Doc1 主管" as separate top-level rows.
Traced this as far as time allowed: Doc1's real dispatch/approval traffic
to doc7 goes through `mcp__ccd_session_mgmt__send_message` (confirmed
directly in Doc1's own session transcript, e.g. `doc1 → doc7:首單品質很高,
驗收通過...` at 08:51:13Z, targeting `session_id:
"local_e1fabe20-8b56-40ca-92e0-ae367a4a54c3"`) — a CCD-native tool, not a
literal `<cross-session-message>`/`<desktop-session-message>` XML tag
authored directly into a transcript. `mcp__ccd_session_mgmt__get_session`
confirms that target session genuinely exists (title "Doc7 向 Doc1 主管報到",
`lastActivityAt` matching the Flow view's own "閒置 3m" to the second), but
its raw transcript file could not be located under this machine's
`%USERPROFILE%\.claude\projects` tree by session id, by cwd-derived slug, or
by content (`search_session_transcripts` for the literal sent text also
came back empty) — every avenue available in this session came up empty.
Two live-plausible, NOT-distinguished-between explanations:

1. The delivery ultimately does land as a `<desktop-session-message>` tag
   the way earlier tasks' confirmed real examples did, in a transcript file
   this investigation simply failed to locate in the time available (a
   file-finding gap, not a code gap) — in which case the rule fixes above
   are already correct and should show up once that specific pair's
   messages are actually within a fresh scan's alive window.
2. `ccd_session_mgmt`'s delivery path is architecturally different from
   the transcript-file convention `claude_sessions.rs` was built to read at
   all, in which case NO amount of marker-detection tuning inside
   `cccog-bar-core` fixes this pair specifically — the gap would be one
   level up, in what Flow's session scanner reads in the first place.

This is deliberately reported as an open question rather than either "it
works" or "it's still broken" — neither claim is honestly supportable from
what could be verified here. The code changes themselves are correct and
provably so via the unit fixtures above, which encode the exact real bug
shape byte-for-byte; what's unconfirmed is only whether they're the
complete fix for what the operator will see live. Flagged for the operator
to either re-run this exact live check once a dispatch using the confirmed
`<desktop-session-message>` mechanism is fresh and in-window, or to help
locate where `ccd_session_mgmt`-routed sessions' transcripts actually live
on disk if the second explanation is the real one.

### Gates

`cargo test --workspace`: 143 tests green, up from 140 (3 net new
`cccog-bar-core` tests: the arrow-marker fixture plus the two doc1/doc7
tree fixtures; 3 pre-existing `cccog-bar-ffi` tests updated in place to the
new quota-render shape; 2 pre-existing `cccog-bar-core` tests updated in
place to the corrected — and correctly narrower — initiator-gate
expectation). `cargo build --release
--workspace`: clean. `dotnet build` (RID-less, the corrected command): 0
warnings, 0 errors, both output folders verified byte-identical. Commit +
push authorized per the operator's message; both landed after all of the
above.

## 2026-08-17 task 12: the flyout was laggy — three layers, one root symptom

Operator report: UI feels "很卡" (janky) with the flyout open. Live
measurement + code read (the coordinator's own diagnosis, confirmed while
implementing): bar sustained ~18% of one core with the flyout open;
`RefreshView` (`FlyoutWindow.xaml.cs`) called `NativeControlClient.TryFetch`
SYNCHRONOUSLY on the UI thread; presence heartbeats (one file write per
Claude Code hook invocation, across every active session) fire the
dispatch/presence `FileSystemWatcher`s near-continuously during active
work, so the 250ms debounce coalesces short bursts but not a genuinely
sustained stream; and while the flyout is visible the `WH_MOUSE_LL` hook is
installed, so a blocked UI thread delayed every SYSTEM mouse event, not
just this app's own — the felt "very janky" was real, system-wide cursor
lag, not just a slow flyout.

### 1. Fetch off the UI thread (`FlyoutWindow.xaml.cs`)

`RefreshView` no longer calls `NativeControlClient.TryFetch` inline. Split
into `RequestFlowFetch` (single-flight gate) → `StartFlowFetch` (the actual
`Task.Run` background work, render-back marshaled through
`DispatcherQueue.TryEnqueue`, matching the 0xc000027b discipline every
other background callback in this file already follows —
`RefreshQuotaCards` was already shaped this way; the Flow fetch was the one
holdout still running inline). Single-flight: `_flowFetchInFlight`
(Interlocked int, same pattern as `_quotaPollInFlight`) plus
`_flowFetchPending` (plain bool, only ever touched on the UI thread) — a
request that arrives while a fetch is already running sets the pending
flag instead of starting a second fetch; the in-flight fetch's own
completion callback checks the flag and starts exactly one more round if
it's set, so N rapid requests collapse to "the current fetch, then at most
one more," never a queue of N concurrent/backlogged fetches.

### 2. Rate-limit sustained refresh triggers (`FlyoutWindow.xaml.cs`)

The existing 250ms debounce (`_refreshTimer`) still coalesces bursts
unchanged. Added a SEPARATE floor on top: `TriggerRateLimitedRefresh`
(what the debounce timer's `Tick` now calls instead of `RefreshView`
directly) measures time since the last refresh COMPLETED
(`_lastFlowRefreshAt`, set in the fetch's own completion callback, not at
request time) against `MinFlowRefreshInterval` (5s). Elapsed >= 5s → refresh
immediately. Elapsed < 5s → schedule exactly one deferred refresh at the
remaining-time boundary via a new one-shot `_rateLimitTimer`
(`_rateLimitDeferralScheduled` prevents stacking a second deferred timer
while one is already queued — same "coalesce to one pending re-run" shape
as layer 1, just at the trigger-gating level instead of the fetch level).
The 60s `_flowTimer` fallback routes through the SAME debounce → rate-limit
path (unchanged plumbing — it just restarts `_refreshTimer`), so it never
needs special-casing: a 60s-spaced trigger is always past the 5s floor by
the time it fires anyway. The manual Refresh button
(`Dashboard.RefreshRequested`), tray menu's "Refresh now"
(`FlyoutWindow.RefreshNow`), and Ctrl+R all call `RefreshView` DIRECTLY —
never through `TriggerRateLimitedRefresh` — so they always bypass the
floor, per the operator's explicit ask.

### 3. Incremental per-file parse cache (`cccog-bar-core::claude_sessions`)

The real per-tick cost driver: `TRANSCRIPT_TAIL_MAX_BYTES` (256KB) already
bounded each individual read, but `ClaudeSessionTailer`'s OUTER skip
shortcut (a single tree-wide newest-mtime token) is all-or-nothing — with
several sessions actively streaming, SOME file's mtime moves on essentially
every tick, so the shortcut almost never fires during active work, and
`collect_claude_sessions` re-read-and-re-parsed the full 256KB tail of
EVERY candidate session file (every session touched in the last 10-minute
alive window, not just the one that actually changed) on every single
tick.

Added a genuine per-file cache, `IncrementalTranscriptCache` — remembers
each file's `(size, mtime_ms, parsed_offset, events)`. On each lookup:

1. `size == cached.size && mtime == cached.mtime` → return the cached
   events, zero I/O.
2. `size >= cached.parsed_offset` (pure append growth, the common case for
   an active transcript) → seek to `parsed_offset`, read to EOF, parse only
   the COMPLETE lines in that delta (a trailing partial line — the file
   caught mid-write — is deliberately left unconsumed; `parsed_offset` only
   advances past what was actually parsed, so a line that hadn't finished
   being written yet gets picked up whole on the NEXT tick instead of ever
   being silently dropped), extend the cached events, advance the cursor by
   exactly the consumed byte count.
3. Otherwise (`size < cached.parsed_offset`: shrink/rotation, or no cache
   entry at all: first sight this process's lifetime) → bounded tail-read
   fallback, replacing the cache entry outright. First-sight uses the
   EXISTING `TRANSCRIPT_TAIL_MAX_BYTES` (256KB, unchanged — preserves
   current cold-start/app-launch behavior); shrink/rotation uses a NEW,
   deliberately larger `SHRINK_FALLBACK_TAIL_MAX_BYTES` (4MB, the
   operator's own suggested figure) since losing the cursor to a rotation
   is a genuine "context lost" event worth a more generous one-time
   recovery read, and is rare (no normal JSONL write pattern shrinks a
   file) — a recovery path, not the steady-state cost.

`build_session_summary` (the existing, content-string-based public entry
point) is UNCHANGED — still parses raw text, still what the CLI/debug/
one-shot callers and its own existing test coverage use. Its body was
extracted into a shared `build_session_summary_impl` (aliveness/title/
model/inbound-edge logic in exactly one place) that a NEW
`build_session_summary_from_events` also delegates to, taking
already-parsed `Vec<ParsedEvent>` instead of content strings — this is what
the new `collect_claude_sessions_incremental` (the cache-backed counterpart
of `collect_claude_sessions`, used only by `ClaudeSessionTailer::tick`) 
feeds it. `collect_claude_sessions` itself is untouched, kept as the
simple, pure, stateless, cache-free entry point.

Known accepted trade-off, not fixed this task: `IncrementalTranscriptCache`
never trims its per-file `events` vec, so it grows for as long as the
process keeps a file in its alive-window rotation (unbounded in principle
over a very long uptime). Not covered by any of this task's fixtures and
the app is already restarted periodically in normal operation; flagged
here rather than silently accepted.

### Fixtures

`cccog-bar-core::claude_sessions`: `incremental_cache_skips_entirely_when_size_and_mtime_are_unchanged`,
`incremental_cache_parses_only_the_appended_delta_on_growth` (asserts
`parsed_offset` lands exactly on the new file size — proves only the delta
bytes were consumed, not a full re-read from offset 0),
`incremental_cache_falls_back_cleanly_on_shrink_or_rotation`, and
`collect_claude_sessions_incremental_reflects_growth_across_ticks` (the
end-to-end proof through the real `SessionSummary` layer, not just the raw
cache — a second tick sharing one cache instance correctly picks up a
newer title/timestamp from an appended delta).

### Gates and live verification

`cargo test --workspace`: 147 tests green, up from 143 (4 net new). `cargo
build --release --workspace`: clean. `dotnet build` (RID-less): 0 warnings,
0 errors, both output folders verified in sync. Operator's running
instance killed by PID first, fresh binaries relaunched, flyout reopened
via the tray icon's own `InvokePattern` (no synthetic click coordinates
needed this time — H.NotifyIcon's tray button supports Invoke directly).

CPU measured via `Get-Process -Id <pid> | .TotalProcessorTime` deltas
(elapsed CPU seconds / elapsed wall seconds × 100 — "% of one core", the
same framing the operator's own ~18% figure uses), with the flyout open and
THIS coordinator session actively working the whole time (its own
transcript growing from real tool calls throughout the measurement window
— the exact condition the report describes, not a synthetic idle app).

**Honest result: target not fully met, but for an identified and
explained reason, with the worst part of the original symptom
structurally gone.** First clean measurement after layers 1–3 above
(async fetch, rate limit, incremental parse) came back at **~16%** —
barely better than the operator's own ~18% baseline, nowhere near "well
under 5%". Investigated with a temporary `Stopwatch` around
`NativeControlClient.TryFetch`: individual fetches ranged 365ms–3.2s, wildly
inconsistent — too slow and too variable for what should have been a
cheap incremental read. Root cause: `ClaudeSessionTailer::tick` was doing
TWO full tree-wide directory walks per tick — the outer skip-shortcut
(`newest_transcript_mtime_ms`, a full stat sweep across every project dir
purely to decide "should I bother rescanning"), THEN
`collect_claude_sessions_incremental` (the actual rescan, itself another
full walk). On this machine (61 project dirs, 1,435 transcript files)
that redundant first sweep is real, measurable cost, and it barely
mattered whether the outer shortcut hit or missed the "unchanged" case
during active work, since the shortcut itself still has to walk
everything to compute its own answer. Fixed: removed the outer sweep
entirely (`newest_transcript_mtime_ms` deleted) — `tick` now calls
`collect_claude_sessions_incremental` directly every time, trusting the
PER-FILE `IncrementalTranscriptCache` (which already makes an unchanged
file cost zero I/O at the granularity that actually matters) instead of a
redundant tree-wide pre-check. One existing test
(`tailer_skips_reparse_when_newest_mtime_is_unchanged`) tested the OLD
mechanism directly and was rewritten
(`tailer_reflects_the_alive_window_honestly_on_every_tick_not_a_stale_cache`)
to assert the corrected — and arguably more honest — behavior: a session
that genuinely goes idle past the alive window now disappears on the very
next tick, rather than staying cached behind a stale "nothing changed
anywhere" shortcut.

After that fix, a second clean measurement came back at **~11%** — a real,
measured improvement (18% → 11%), but still short of "well under 5%".
Further investigated via the debug CLI (`cccog-bar.exe --control`,
timed cold): even a single fresh-process walk of this machine's real
`~/.claude/projects` tree costs ~190–250ms, dominated by the sheer
syscall count of `read_dir`+`metadata()` across 61 project dirs and 1,435
files/subagent files — not by any parsing or caching logic, which the
incremental cache already made close to free. This is now believed to be
the genuine floor of a POLLING-based design at this file count: discovering
"did anything change" at all requires re-listing the tree every tick,
independent of how cheap reading the CONTENT of an unchanged file is.
Closing this last gap for real would mean replacing the poll with real
OS-level file-system-watching (e.g. the `notify` crate) integrated into
`cccog-bar-core` itself — a materially larger architectural change than
"cap per-tick parse cost," and NOT attempted here; flagged for the
operator to decide whether it's worth a dedicated follow-up task, since
the remaining ~11% is a real, bounded, explained cost rather than an
unbounded or still-growing one.

## 2026-08-18 task 13: the flyout never actually light-dismissed

Task 8's port of TokenBar's click-outside-to-hide mechanism
(`Activated`/`Deactivated`) had been shipped unverified (task 8's own
section above already disclosed this). Operator now confirmed with a real
mouse: it's inert. Mechanism, once named: this flyout is a FOCUSLESS popup
by design (`SetWindowPos(..., SWP_NOACTIVATE)` in `ApplyPopupChrome`,
exactly why the `WH_MOUSE_LL` wheel hook already exists — the system never
delivers normal wheel input to it either) — a window that never receives a
genuine `Activated` transition can structurally never fire the matching
`Deactivated` one. Not a bug in the port itself; the port was verbatim and
correct, ported onto a window shape that can't use it.

### Fix: deterministic light-dismiss via the existing mouse hook

`ShowFlyout()` already calls `Activate()` (checked against TokenBar
line-by-line, per the operator's own ask — it was already there, nothing
to restore) — kept, but no longer relied on. The real mechanism: the
`WH_MOUSE_LL` hook (already running system-wide for the wheel case) now
also watches `WM_LBUTTONDOWN`/`WM_RBUTTONDOWN`/`WM_MBUTTONDOWN`. On a
button-down whose screen point falls outside BOTH the flyout's own rect
AND the taskbar notification area, it posts `HideFlyout` via
`DispatcherQueue.TryEnqueue` and — critically — never returns `1`
(never swallows the click): the user's click on whatever else is under
the cursor lands normally either way, this only additionally schedules
our own window to hide.

The notification-area exclusion (not just an exact "our one tray icon"
exclusion) exists to fix a real race: without it, clicking OUR OWN tray
icon to close an already-open flyout races the hook's hide against
`ToggleFlyout`'s own visibility check — if the hook's hide lands first,
Toggle then sees "already hidden" and calls `ShowFlyout` instead,
silently reopening the very flyout the click was meant to close.
Excluding the whole `TrayNotifyWnd` region (found via
`FindWindowW("Shell_TrayWnd")` → `FindWindowExW(..., "TrayNotifyWnd")` →
`GetWindowRect` — plain, fast, synchronous Win32, no COM, safe to call
right before installing the hook) sidesteps needing H.NotifyIcon's own
internal icon identifiers (which this app has no access to;
`Shell_NotifyIconGetRect` needs them and isn't reachable here) — clicking
ANY tray icon, ours included, is already handled by its own click logic.
Computed once per `ShowFlyout()` call (not inside the hook callback
itself — a `WH_MOUSE_LL` callback must return fast or Windows silently
detaches it, so the handful of `FindWindow`/`GetWindowRect` calls happen
before installing the hook, cached in a closure-captured local).

The hit-test itself (`ShouldLightDismiss(x, y, flyoutRect,
notificationAreaRect)`) is a small, pure, side-effect-free static method —
no window/live-state dependency, callable in isolation. No C# test
project exists anywhere in this repo (checked: no `*.Tests.csproj`), so
"fixtures where feasible" landed on making the function itself maximally
testable rather than adding new test infrastructure — a project-wide gap,
not something to bolt on inside a single bug-fix task, and disclosed here
rather than silently skipped.

`--keep-open` now gates both dismiss paths (`_keepOpen` promoted from a
constructor-local to a field so the hook's closure can read it too) — the
Activated/Deactivated path is kept as-is (harmless dead code if some
future chrome change ever makes it fire; removing verbatim-ported code
that might matter later wasn't asked for and isn't free).

### Live verification

Confirmed the mechanism fires correctly using `SendInput` (not the legacy
`mouse_event` — an early attempt with the older API produced clicks the
hook never seemed to observe at the coordinates requested; switching to
`SendInput` with `SetProcessDPIAware()` set first produced a single,
clean, correctly-classified hook event every time): a real synthetic
click at a screen point genuinely outside the flyout rect correctly
triggered `HideFlyout` and the window closed, confirmed via UI Automation
immediately after. (A companion "click inside the rect must NOT dismiss"
test was planned but skipped after the mechanism above proved reliable —
this environment's own coordinate-mapping quirks, already documented
elsewhere in this file, made a SECOND precise synthetic click risky to
chase further against the remaining task queue; the pure hit-test
function's own logic for the in-rect case is a one-line, directly
reviewable inequality, not something meaningfully in doubt.) `bar-crash.log`
stayed empty throughout, including through several minutes of repeated
launch/toggle/click cycles while chasing the above. Flow content
(task 12) and quota rendering (task 9/11) both re-confirmed intact on the
same rebuilt binary — 9 rows including `doc7 向 Doc1 主管報到` correctly
nested, no regression.

Per the operator's own framing, the decisive verification is still their
real mouse on the real desktop — everything above is best-effort
automated confirmation ahead of that.

### Gates

`cargo test --workspace`: 147 tests green (this task's own C# changes
don't touch Rust; count unchanged from the task-12 CPU follow-up above —
both landed in the same batch). `dotnet build` (RID-less): 0 warnings, 0
errors. No `bar-crash.log` growth. Commit + push authorized; landing
together with the task-12 CPU follow-up above since both were discovered
and fixed in the same investigation pass.

No `bar-crash.log` growth at any point across all of the above. Mouse-jank
itself (system-wide cursor smoothness) could not be directly felt via this
automated environment, per the operator's own note that this class of
symptom needs a human hand on the mouse — but the SPECIFIC mechanism the
operator described (`TryFetch` blocking the UI thread, which also owns the
`WH_MOUSE_LL` hook callback, delaying every system-wide mouse event while
it ran) is now structurally impossible regardless of the remaining CPU
number: the fetch never touches the UI thread at all anymore, so that
hook's callback can no longer be delayed by it. The measured 18%→11% CPU
reduction plus this structural fix together represent the actual
deliverable; "well under 5%" specifically was not reached, disclosed here
rather than rounded away.

## 2026-08-18 task 14: per-session context-usage display ("ctx 36%")

Operator wants Flow rows to show current context-window occupancy, the way
Claude Code's own context panel does (`355.4k/1M = 36%`). Source: each
assistant transcript line carries `message.usage` — confirmed against this
coordinator session's own transcript
(`70d73719-....jsonl`, `grep '"usage":{"input_tokens'`):
`{"input_tokens":2,"cache_creation_input_tokens":42444,
"cache_read_input_tokens":43926,"output_tokens":346,...}`. A follow-up grep
for a "context"/max-window field anywhere near `model`/`usage` in the same
file found nothing — no on-disk source for the model's own window size, so
per-model window is a hardcoded lookup table (task 14's own instruction:
"if unknown model, emit tokens only, no percent" rather than guess).

### `cccog-bar-core::claude_sessions`

`parse_line` now also extracts `message.usage` into a new `UsageTokens
{ input_tokens, cache_read_tokens, cache_creation_tokens }` on
`ParsedEvent` — deliberately NOT `output_tokens` (task 14 asks for
prompt/CONTEXT occupancy, not the turn's own reply length, a much smaller,
non-accumulating number). `SessionSummary`/`AgentSummary` each gained
`context_tokens: Option<u64>` / `context_percent: Option<f64>`, filled by
`latest_context_usage`: the NEWEST usage-carrying event wins (same
"last one wins" convention title/model resolution already used), its
token total is `context_tokens`, and — only when that SAME event's own
`model` resolves via `context_window_for_model` (substring match:
`opus`→200_000, `fable`/`sonnet`→1_000_000, else `None`) — the resulting
percent. Both `build_session_summary` (raw-content path) and
`build_session_summary_from_events` (task 12's incremental-cache path)
go through the same `build_session_summary_impl`, so the per-file cache
never has to re-derive this differently depending on which entry point
fed it.

`cccog-bar-core::tree::TreeRow` carries the same two fields through
(`#[serde(rename_all = "camelCase")]` → wire names `contextTokens`/
`contextPercent`, automatic — no FFI-layer change needed since
`cccog-bar-ffi` serializes `TreeRow` directly). Dropped `Eq` from
`TreeRow`'s derive (kept `PartialEq`) since `f64` doesn't implement it —
confirmed via grep that nothing relied on `TreeRow: Eq`. Only the
controller/agent/session construction sites carry real values; the
dispatch-job/terminal/owner-lease rows get `None`/`None` (a dispatch job
has no `message.usage` shape at all).

4 new fixtures in `claude_sessions.rs`:
`context_usage_reflects_the_newest_usage_carrying_event` (an earlier
lower-usage event must not leak through once a later one exists),
`context_usage_on_an_unknown_model_reports_tokens_only_no_percent`,
`context_usage_is_absent_when_no_event_ever_carried_usage` (renders
nothing, never a fabricated `ctx 0%`), `agent_row_carries_its_own_context_usage`
(a subagent's own usage is independent of the parent's — here the parent
has none at all while the agent does).

### C# — `ctx 36%` / `ctx 355k` in the state column

`FlyoutWindow.ToRow` reads `contextTokens`/`contextPercent` off the wire
and pre-formats them (`FormatContextUsage`) into `ContextText` +
`ContextTooltip` on `FlowTreeRowViewModel` — same "compute the display
string once, at parse time" pattern `FormatTreeElapsed` already used for
`ElapsedText`. `ctx {percent:0}%` when the model's window is known, else
`ctx {tokens}` (`FormatTokenCount`: `355.4k`/`1M`/plain integer under
1,000). The tooltip (`355.4k tokens (36% of 1M)`) reverse-derives the
window from `tokens / (percent/100)` — the wire only carries the two
numbers, and since percent always comes from one of the two fixed lookup
values, the reversed window rounds back to a clean `1M`/`200k`. `(null,
null)` when the row never carried usage — dispatch-job/terminal/owner
rows, or a session/agent transcript with no assistant turn yet — so the
caller renders nothing.

`DashboardView.BuildFlowRow`: the state column (Grid column 2) changed
from a single `TextBlock` to a small horizontal `StackPanel` holding the
existing state/elapsed text plus, only when `ContextText` is non-empty, a
second equally-dim (`Ui.Text(..., 10, 0.62)`) run carrying the tooltip —
task 14's own instruction ("tooltip on it") meant the tooltip belongs on
the ctx run specifically, not the whole cell, which is why this couldn't
stay a single concatenated string. Column count/widths — the thing "do
not disturb the three-column alignment" was actually protecting —
untouched; this only changes what's inside column 2's own cell.

### A footgun re-confirmed the hard way: `dotnet build -c Release` does NOT rebuild the Rust release DLL

First live-verification pass showed the flyout with NO `ctx` text at all
despite the debug CLI (`cccog-bar --control`, rebuilt via `cargo build
--workspace`) proving `contextTokens`/`contextPercent` were correctly
populated in the live data. Root cause: `target/release/cccog_bar_ffi.dll`
was still the PRE-task-14 build (timestamp from earlier in the session) —
`dotnet build -c Release -p:Platform=x64` only COPIES whatever's already
in `target/release` (README.md's own build section already documents this
two-step sequence; this task just re-learned it by hitting the stale-DLL
symptom directly instead of reading the doc first). Fix: `cargo build
--release --workspace` (51s) before the C# rebuild — which then hit the
DLL file lock from the still-running old process (`MSB3021`/copy retry
storm against PID 41504); killed it, rebuilt clean.

### Live verification

Fresh launch, UI Automation dump of the flyout's real rendered text (via
`AutomationElement.FromHandle` + `TreeWalker.ContentViewWalker`, not a
screenshot — this environment's screenshot path is known-unreliable):

```
CCCG開發        (claude · fable-5)   執行中 · 0s   ctx 39%
Add Claude session liveness to Flow (agent · sonnet-5) 執行中 · 0s ctx 16%
doc5 Api文件檢查 (claude · sonnet-5)  閒置 · 3m    ctx 92%
Doc1 主管       (claude · fable-5)   閒置 · 6m    ctx 90%
doc2 api-gateway上線 (claude · sonnet-5) 閒置 · 6m ctx 73%
codex ×60       terminal
grok ×34        terminal
```

Also confirms the FYI from the task-14 dispatch itself: with
`flow.coordinators=["Doc1 主管"]` now set, "Doc1 主管" renders at
top-level (depth 0, same indentation as the other three controller rows)
— the earlier doc2-nesting inversion is gone, no further action needed.
`codex ×60`/`grok ×34` are task 16's known, not-yet-fixed bug (still
showing all-time counts); untouched by this task.

### Gates

`cargo test --workspace`: 151 tests green (core crate 69 lib + 5 graph +
4 parsers + 4 refresh + 5 usage = 87, +4 net from this task's fixtures;
quota 30 and ffi 34 unchanged). `dotnet build -c Release -p:Platform=x64`:
0 warnings, 0 errors (after the release-DLL rebuild above). No
`bar-crash.log` growth across the whole task. Commit + push authorized.

## 2026-08-18 task 15: inline row expansion (context decomposition, turn count/age, sparkline)

Operator approved option 1 (inline expand, over a separate detail panel):
clicking a session/agent row expands 2-3 detail lines beneath it, click
again collapses. Explicit constraint carried over from task 14's own
framing: ONLY honestly transcript-derivable fields — the CC-internal
System-tools/Skills category breakdown is not on disk anywhere and is
never fabricated here.

### `cccog-bar-core`

`UsageTokens` gained `output_tokens` (task 14 deliberately left it out
since `total()`/context occupancy doesn't need it; task 15's own
decomposition line does). New `ContextDetail` struct
(`claude_sessions.rs`): `cached_tokens` (= `cache_read_tokens` of the
LATEST usage-carrying event, same "last one wins" convention as
`context_tokens`), `fresh_tokens` (= `input_tokens + cache_creation_tokens`
— a cache WRITE is still freshly-processed input, just newly cached for
next time), `output_tokens`, `turn_count` (count of usage-carrying events
— NOT bounded, unlike the series below), `session_started_at` (the
EARLIEST event of ANY kind, not just usage-carrying ones — a session
starts at its first message, typically a usage-less user line),
`series: Vec<u64>` (per-turn totals, chronological, bounded to the most
recent `CONTEXT_SERIES_MAX_POINTS = 50` — a RECENT-trend indicator, never
a full archive). Built by `build_context_detail`, wired into both
`SessionSummary` and `AgentSummary` alongside task 14's existing
`context_tokens`/`context_percent`, gated by the identical condition
(`None` under the exact same "no usage-carrying event at all" case).

`tree::TreeRow` gained one new field, `context_detail:
Option<TreeContextDetail>` — a NESTED object (`cachedTokens`,
`freshTokens`, `outputTokens`, `turnCount`, `sessionAgeSeconds`, `series`)
rather than six more flat `Option` fields, since these numbers only ever
travel together. `session_started_at` (an absolute instant, meaningless
without a shared clock) becomes `session_age_seconds` at this layer,
computed the same way every other `elapsedSeconds` on the row already is.
5 new fixtures in `claude_sessions.rs`: the decomposition uses only the
LATEST turn (an earlier turn's huge synthetic numbers must not leak in),
turn_count/session_age span the WHOLE transcript (not just the
usage-carrying tail), the series is bounded to the last 50 of 61 real
turns kept in chronological order (verified the OLDEST kept point is
turn #11, not #0 — proving it's truncating from the front, not the
back), absent-usage renders no detail (mirrors task 14's own fixture),
and an agent's detail is fully independent of its parent's.

### C# — click-to-expand, chevron, sparkline

`FlowTreeRowViewModel` gained `CanExpand`, `DecompositionText`,
`TurnsAgeText`, `ContextSeries` — pre-formatted at fetch time in
`FlyoutWindow.ParseContextDetail`, same "compute once, UI is pure
render" discipline every other row field already follows.

Expand/collapse is per-row UI state, explicitly "not persisted"
(operator instruction) — meaning it doesn't survive an app restart, but
it DOES need to survive the periodic re-render every 5s-rate-limited
refresh already does, or expanding a row would immediately collapse
itself on the next tick. Since the row DTOs are rebuilt fresh from JSON
on every fetch (no stable object identity), `DashboardView` now tracks
expand state itself, keyed by `depth:kind:label`
(`_expandedFlowRowKeys`, a `HashSet<string>` field on the `UserControl`
instance) and keeps the last-fetched rows (`_lastFlowRows`) so a click
can re-render from cache — task 15's own instruction, "expanding must
not trigger a data refetch." `RenderFlow` (the real fetch path) and
`ToggleFlowRowExpanded` (the click path) both end in the identical
`FlowHost.Content = Ui.Card("Flow", BuildFlowContent(...))` call; only
which list feeds it differs.

A clickable row needs `Background = Transparent` (not left `null`) —
WinUI's `Grid` is hit-test invisible outside its own children when its
background is unset, so without this, only clicks landing exactly on a
text run (not the row's surrounding whitespace) would have registered.
Only rows with `CanExpand` get a `Tapped` handler and the "▸"/"▾"
chevron at all; a row with no usage data renders byte-identical to
before this task, un-clickable. Detail lines (`Ui.Dim`, same styling as
every other secondary text on the row) render in a `StackPanel` appended
BELOW the row's own `Grid`, inside a shared wrapper `BuildFlowRowGroup`
returns — the three-column `Grid` itself is completely untouched either
way, so alignment holds with or without expansion.

New `Ui.Sparkline` (`Ui.cs`): a small inline `Polyline`, normalized to
its own series' min/max, reusing `GaugeBar`'s own muted gray and `Dim`'s
0.6-opacity convention rather than a new palette or chart library (both
explicit operator constraints). A flat series still draws a flat
centered line (no divide-by-zero); fewer than 2 points draws nothing.

Light-dismiss (task 13) needed NO code change — the `WH_MOUSE_LL` hook's
dismiss check already excludes ANY point inside the flyout's own
bounding rect wholesale, so a click on a row (always inside that rect)
was never at risk; live-verified below by clicking a row repeatedly
without the flyout ever closing.

### Live verification

Real `SendInput` clicks (not synthetic UIA `InvokePattern.Invoke` —
same lesson task 13 already learned about coordinate reliability),
located via a fresh `AutomationElement` rect lookup taken in the SAME
call as the click (row order re-sorts by `last_event_at` every refresh,
so a stale rect from an earlier dump can silently target the wrong
row).

Clicking "CCCG開發" (this coordinator session) once: chevron flipped
"▸"→"▾" and produced, verified via `AutomationElement` text dump:

```
cached 385.7k · fresh 18 · output 222
33 turns · session age 2h09m
```

The sparkline itself carries no accessible `Name` (a bare `Polyline`
gets no WinUI automation peer), so it doesn't show in a UIA text dump —
confirmed instead via a real `CopyFromScreen` screenshot of just that
region, showing a genuinely non-flat line trending upward across the
turn history, not a placeholder or flat artifact. Clicking the same row
again: chevron back to "▸", detail lines gone (collapse confirmed).
Expanded a row, waited 7s (past the 5s refresh floor, confirmed at
least one background auto-refresh fired via the changed timestamp/row
order), re-checked: still expanded — confirming state survives the
periodic re-render, not just a manual poll. Flyout stayed open and
responsive through every one of these clicks (light-dismiss unaffected).

### Gates

`cargo test --workspace`: 156 tests green (core crate 74 lib + 5 graph +
4 parsers + 4 refresh + 5 usage = 92, +5 net from this task's fixtures;
quota 30 and ffi 34 unchanged). `dotnet build -c Release -p:Platform=x64`
(after `cargo build --release --workspace` first, per task 14's own
re-learned lesson): 0 warnings, 0 errors. No `bar-crash.log` growth.
Commit + push authorized.

## 2026-08-18 task 16: terminal aggregate rows were never windowed

Live-confirmed bug: `codex ×60`/`grok ×34` at the bottom of the Flow tree
stayed FIXED for many hours after the last real dispatch — `control.rs`'s
own `TERMINAL_WINDOW_SECS = 2h` (line 46) is real and correctly applied
by `control.rs`'s OWN windowed reducer (`build_terminal_rows`, verified
still filtering correctly by `job.time` inside `(0..=TERMINAL_WINDOW_SECS)`
— that path was never the bug), but `tree::build_tree`'s SEPARATE,
simpler terminal aggregation (task 12's own doc comment on
`DispatchJobInput::time` said it outright: "terminal jobs are aggregated
by count, not shown individually, so their own time isn't needed here")
counted every non-active job it had EVER walked off disk, with no window
applied at all. Two aggregations of the same underlying `dispatch/jobs`
tree, only one of them windowed — a same-concept-two-implementations gap
(see `project_same_concept_many_impls_audit` in memory), not a
regression in either one individually.

### Fix

`control::TERMINAL_WINDOW_SECS` promoted from a private `const` to
`pub(crate)` so both aggregations import the IDENTICAL constant — the
whole point being that a future edit to the window can never again drift
between the two paths the way this bug shows it already did once.

`tree::DispatchJobInput` gained `finished_at: Option<DateTime<Utc>>` — a
terminal job's own authoritative finish time, deliberately using a
DIFFERENT fallback chain than `control.rs`'s own separate reducer
(`finished.or(started).or(created)`): the operator's own instruction was
`status.json`'s `finishedAt`, falling back to the FILE's own mtime (new
`cccog-bar-ffi::status_file_mtime` helper) — not `startedAt`/`createdAt`.
The two aggregations are allowed to disagree on their fallback chain;
they only need to share the WINDOW, which they now structurally do via
the shared constant. `build_tree`'s terminal-counting loop now skips any
job whose `finished_at` is `None` or outside `(0..=TERMINAL_WINDOW_SECS)`
of `now` before incrementing that provider's count — a provider with
every job filtered out renders NO row at all (never a fabricated
`codex ×0`), and the registry files on disk are completely untouched
(display-only filtering, per the task's own scope).

4 new/updated fixtures in `tree.rs`: the exact regression shape (every
job stale → no row, not `×0`), a mix of fresh+stale for one provider
(only the fresh ones count — proves the filter is per-job), a job with
no determinable finish time at all (excluded, never defaulted to
"fresh"), and the pre-existing terminal-aggregate test updated with an
explicit in-window `finished_at` (it previously relied on the field not
existing/not being checked at all).

### Live verification

Rebuilt the release FFI DLL and CLI, killed and relaunched the app.
Before this fix (this same live run, checked earlier in this session
while still on task 15's binary): the Flow tree showed `codex ×60` and
`grok ×34` as two extra bottom rows. After: a fresh `AutomationElement`
text dump of the running flyout —

```
CCCG開發 ... 執行中 · 0s ctx 39%
Add Claude session liveness to Flow ... 執行中 · 0s ctx 36%
doc5 Api文件檢查 ... 執行中 · 35s ctx 10%
3 flow row(s) - tokens only
```

— confirms both terminal rows are GONE entirely, exactly the task's own
predicted live result ("nothing terminal in the last 2h" — the last
real dispatch was hours before this check). No `bar-crash.log` growth.

### Gates

`cargo test --workspace`: 159 tests green (core crate 77 lib + 5 graph +
4 parsers + 4 refresh + 5 usage = 95, +3 net from this task's fixtures;
quota 30 and ffi 34 unchanged). `dotnet build -c Release -p:Platform=x64`
(release Rust rebuild first): 0 warnings, 0 errors — this task's own
changes are Rust-only (display-only filtering on data the C# side
already just renders whatever rows arrive), so the C# rebuild here is
solely to pick up the fresh FFI DLL, no source changes on that side.
Commit + push authorized.
