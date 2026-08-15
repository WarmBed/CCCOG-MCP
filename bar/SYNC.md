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
