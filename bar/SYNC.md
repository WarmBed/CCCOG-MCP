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
