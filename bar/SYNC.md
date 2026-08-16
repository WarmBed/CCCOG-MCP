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
