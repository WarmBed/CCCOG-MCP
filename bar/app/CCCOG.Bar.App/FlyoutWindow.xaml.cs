using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using IOPath = System.IO.Path;

namespace CCCOG.Bar.App;

/// <summary>
/// The slim CCCOG-Bar companion: a tray-anchored flyout with exactly two
/// sections — quota cards and flow rows. The window chrome (Acrylic
/// backdrop, borderless/topmost popup style, taskbar-edge positioning,
/// show/hide slide) is ported from TokenBar-Windows
/// <c>src/TokenBar.App/FlyoutWindow.xaml.cs</c> — see bar/SYNC.md for the
/// line-by-line derivation record and the named gaps (resize-drag
/// persistence, the WH_MOUSE_LL wheel-focus workaround) that were left out
/// because they depend on TokenBar features (a settings-backed store) this
/// shell doesn't have. The content refresh/poll logic below the chrome
/// section is CCCOG's own — see docs/plans/2026-08-16-cccog-bar-v2-slim.md
/// for the performance contract it exists to keep (window visible &lt;1s
/// cold; quota arrives async; no history parsing).
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    private const int FlyoutWidth = 500;
    private const int FlyoutHeight = 640;
    private const int Margin = 12;
    private const int SlideDistance = 24;

    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer _quotaTimer;
    private readonly DispatcherQueueTimer _flowTimer;
    /// <summary>Task 12 (2026-08-17, live-measured lag: bar sustained ~18% of
    /// one core with the flyout open, blocking the system-wide mouse cursor
    /// via the WH_MOUSE_LL hook while the UI thread sat inside a synchronous
    /// native fetch): a one-shot deferral timer for
    /// <see cref="TriggerRateLimitedRefresh"/> — when a watcher/timer-driven
    /// refresh request arrives less than <see cref="MinFlowRefreshInterval"/>
    /// after the last one COMPLETED, this fires exactly once at the
    /// remaining-time boundary instead of refreshing immediately.</summary>
    private readonly DispatcherQueueTimer _rateLimitTimer;
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _presenceWatcher;
    private int _quotaPollInFlight;
    /// <summary>Single-flight guard for the Flow fetch (task 12): 0 = idle,
    /// 1 = a background fetch is in progress. <see cref="_flowFetchPending"/>
    /// remembers that ANOTHER refresh was requested while one was already
    /// running — the in-flight fetch's own completion callback starts
    /// exactly one more round when it sees this set, so N rapid requests
    /// collapse to at most "the current fetch, then one more", never a
    /// queue of N.</summary>
    private int _flowFetchInFlight;
    private bool _flowFetchPending;
    /// <summary>When the last Flow fetch COMPLETED (render already applied)
    /// — <see cref="TriggerRateLimitedRefresh"/> measures against this, never
    /// against when a refresh merely started.</summary>
    private DateTimeOffset _lastFlowRefreshAt = DateTimeOffset.MinValue;
    private bool _rateLimitDeferralScheduled;
    /// <summary>Task 12: sustained watcher-triggered bursts (presence
    /// heartbeats fire near-continuously while a session is actively
    /// working) render at most this often — bursts still coalesce via the
    /// existing 250ms debounce, this is the SEPARATE floor on top of it.
    /// The manual Refresh button/menu item/Ctrl+R call <see cref="RefreshView"/>
    /// directly and never pass through this gate at all.</summary>
    private static readonly TimeSpan MinFlowRefreshInterval = TimeSpan.FromSeconds(5);
    /// <summary>Verification opt-out (task 8) for BOTH dismiss paths — the
    /// Activated/Deactivated handler and, as of task 13, the mouse hook's
    /// own light-dismiss check. Set once from argv in the constructor.</summary>
    private bool _keepOpen;
    private bool _closing;
    /// <summary>Set only by <see cref="Shutdown"/> (App.QuitApp's path): lets
    /// the real WM_CLOSE through instead of the tray-resident hide-on-close
    /// the AppWindow.Closing handler otherwise does.</summary>
    private bool _shuttingDown;
    /// <summary>UI-thread init gate (lessons-learned #4): no refresh path
    /// runs before the constructor finishes wiring the visual tree — the
    /// 0xc000027b crash class came from a background callback racing ahead
    /// of InitializeComponent.</summary>
    private bool _viewReady;
    private bool _everHadQuota;
    private bool _quotaTimerStarted;
    private bool _flowTimerStarted;

    public FlyoutWindow()
    {
        InitializeComponent();

        // Manual click: allowed to bypass the quota HTTP TTL (still capped
        // to once per 60s on the Rust side) — see NativeQuotaClient.TryPoll.
        Dashboard.RefreshRequested += () => RefreshView(manualQuota: true);

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;

        ApplyPopupChrome();

        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += (_, args) =>
        {
            if (_shuttingDown)
            {
                return; // Shutdown() wants the real close, not a hide.
            }

            args.Cancel = true;
            HideFlyout();
        };

        WireResizeGrip();

        // Transient surface → Acrylic, via the manual controller so the
        // backdrop stays translucent while unfocused: the flyout is a
        // glanceable dashboard, not a focused editor.
        SetupAlwaysOnAcrylic();

        // Task 8 (2026-08-17): clicking anywhere outside the flyout was
        // supposed to hide it (that's the whole point of a tray flyout) but
        // this port never carried the mechanism over — ported verbatim from
        // TokenBar-Windows FlyoutWindow.xaml.cs. --keep-open: verification
        // hook — screen-capture/UI-Automation helpers steal focus, which
        // would correctly dismiss the flyout right before the shot, so
        // live-check tooling (including this repo's own) needs an opt-out.
        // Task 13 (2026-08-18, operator live-confirmed with a real mouse):
        // this Activated/Deactivated path never actually fires for this
        // window — it's a focusless popup (SWP_NOACTIVATE in
        // ApplyPopupChrome, exactly why the WH_MOUSE_LL wheel hook exists
        // in the first place: the system never delivers normal input to it
        // either), so a window that never receives a real Activated
        // transition can never fire the matching Deactivated one. Kept
        // anyway (harmless if some future chrome change ever makes it
        // fire); the real, deterministic mechanism is now the mouse hook's
        // own light-dismiss check below — `_keepOpen` gates both paths.
        _keepOpen = Environment.GetCommandLineArgs().Contains("--keep-open");
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && !_keepOpen)
            {
                HideFlyout();
            }
        };

        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(250);
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            // Task 12: the 250ms debounce still coalesces BURSTS (many
            // watcher events arriving close together collapse to the one
            // tick that fires after they stop), but a SUSTAINED stream of
            // events spaced further apart than 250ms would otherwise fire
            // this every ~250ms indefinitely — TriggerRateLimitedRefresh is
            // the separate floor on top: at most one real refresh per
            // MinFlowRefreshInterval regardless of how often this tick
            // itself fires.
            TriggerRateLimitedRefresh();
        };
        _rateLimitTimer = DispatcherQueue.CreateTimer();
        _rateLimitTimer.Tick += (_, _) =>
        {
            _rateLimitTimer.Stop();
            _rateLimitDeferralScheduled = false;
            RefreshView();
        };
        _quotaTimer = DispatcherQueue.CreateTimer();
        _quotaTimer.Interval = TimeSpan.FromSeconds(60);
        _quotaTimer.Tick += (_, _) => RefreshQuotaCards();
        // Flow's primary data sources since 94ca2da (Claude session
        // transcripts under ~/.claude/projects, and the presence dir) are
        // NOT under the dispatch-root FileSystemWatcher below, and nothing
        // else was polling them — a quiet CCCG dispatch registry (zero
        // events) meant Flow could sit frozen on a stale "refreshed HH:MM"
        // for hours even while real cross-session activity kept happening
        // (found live: 2026-08-16, ~2h stall, no crash, process alive).
        // This periodic tick is the fix: it drives the SAME debounced
        // RefreshView path the file watchers already use (never calls
        // RefreshView directly), so there is exactly one refresh
        // choreography regardless of what triggered it. 60s (operator
        // direction, performance concern — an earlier 10s pass was
        // dialed back): this is the BOUNDED FALLBACK for transcript-only
        // changes (a fresh cross-session dispatch, a session aging past
        // its alive window) that neither watcher below sees; the presence
        // and dispatch-root watchers below still give sub-second updates
        // for what they cover (執行中/閒置 flips, CCCG job changes)
        // in between ticks — 60s is "how stale can the slow-path parts of
        // Flow ever get", matched to the quota timer's own cadence, not
        // "how fast is the scan" (which stays milliseconds either way —
        // see cccog_bar_core::claude_sessions).
        _flowTimer = DispatcherQueue.CreateTimer();
        _flowTimer.Interval = TimeSpan.FromSeconds(60);
        _flowTimer.Tick += (_, _) =>
        {
            if (_closing)
            {
                return;
            }
            _refreshTimer.Stop();
            _refreshTimer.Start();
        };
        Closed += (_, _) =>
        {
            _closing = true;
            _watcher?.Dispose();
            _presenceWatcher?.Dispose();
            _refreshTimer.Stop();
            _rateLimitTimer.Stop();
            _quotaTimer.Stop();
            _flowTimer.Stop();
            _acrylic?.Dispose();
            _acrylic = null;
            // Safety net: HideFlyout already uninstalls the hook, but Quit
            // can land while the flyout is visible (hook still installed) —
            // never leak a WH_MOUSE_LL hook past process shutdown.
            RemoveWheelHook();
        };

        // Deliberately do NOT read the dispatch root here: the constructor
        // must return so the caller can call AppWindow.Show() as early as
        // possible (performance contract: window visible < 1s cold). The
        // file read — milliseconds — happens once ShowFlyout() calls
        // RefreshView() after Show().
        _viewReady = true;
        StartFileWatcher();
    }

    public bool IsFlyoutVisible => AppWindow.IsVisible;

    public void ToggleFlyout()
    {
        if (IsFlyoutVisible)
        {
            HideFlyout();
        }
        else
        {
            ShowFlyout();
        }
    }

    /// <summary>The tray menu's "Refresh now" item — same path the footer's
    /// own Refresh button and Ctrl+R use, manual so it may bypass the quota
    /// HTTP TTL (still capped to once per 60s on the Rust side).</summary>
    public void RefreshNow() => RefreshView(manualQuota: true);

    /// <summary>The single real-close path (App.QuitApp): lets the pending
    /// AppWindow.Closing cancel-and-hide through instead of intercepting it,
    /// then closes for real. Safe to call once; App guards against a second
    /// Quit invocation.</summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        _closing = true;
        RemoveWheelHook();
        Close();
    }

    private bool _hiding;
    private long _slideToken;
    private long _hideGeneration;

    public void ShowFlyout()
    {
        if (_closing)
        {
            return;
        }

        _hiding = false;
        InstallWheelHook();
        var (resting, start) = PositionNearTray();
        // Start offset toward the taskbar edge so the WINDOW (backdrop
        // included) slides in as one surface, TokenBar's entrance motion.
        AppWindow.Move(start);
        AppWindow.Show();
        // Show can rebuild frame state; re-assert the chrome afterwards.
        ApplyPopupChrome();
        AppWindow.MoveInZOrderAtTop();
        Activate();
        _ = SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _ = SlideWindowAsync(start, resting, durationMs: 180, decelerate: true);
        FadeContent(from: 0f, to: 1f, durationMs: 180);

        RefreshView();
        if (!_quotaTimerStarted)
        {
            _quotaTimerStarted = true;
            _quotaTimer.Start();
        }
        if (!_flowTimerStarted)
        {
            _flowTimerStarted = true;
            _flowTimer.Start();
        }
    }

    public void HideFlyout()
    {
        if (_hiding || !AppWindow.IsVisible)
        {
            return;
        }

        _hiding = true;
        var generation = ++_hideGeneration;
        RemoveWheelHook();
        var resting = AppWindow.Position;
        var sink = Toward(resting, TaskbarEdge(), SlideDistance);
        FadeContent(from: 1f, to: 0f, durationMs: 120);
        _ = FinishHideAsync(resting, sink, generation);
    }

    private async Task FinishHideAsync(PointInt32 from, PointInt32 to, long generation)
    {
        await SlideWindowAsync(from, to, durationMs: 120, decelerate: false);
        if (!_hiding || generation != _hideGeneration)
        {
            // Superseded — either a ShowFlyout cleared _hiding, or a newer
            // hide bumped the generation. Only the current hide's
            // continuation may finish the job.
            return;
        }

        AppWindow.Hide();
        _hiding = false;
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
            .GetElementVisual(Content);
        visual.Opacity = 1f;
    }

    /// <summary>
    /// Flow rows are file-only and render synchronously — the &lt;1s cold
    /// window budget depends on this never touching the network or parsing
    /// usage history (the windowed Active + last-2h view Rust builds is the
    /// same bounded local read the old from-scratch scan was, just
    /// windowed/aggregated — see NativeControlClient below). Quota is
    /// kicked off separately and arrives async.
    /// </summary>
    private void RefreshView(bool manualQuota = false)
    {
        if (_closing || !_viewReady)
        {
            return;
        }

        RequestFlowFetch();
        RefreshQuotaCards(manualQuota);
    }

    /// <summary>Task 12 (2026-08-17): entry point for the manual/rate-limited
    /// Flow refresh — starts a background fetch if none is running, or
    /// coalesces to exactly one pending re-run if one already is (never
    /// queues N). Called both directly from <see cref="RefreshView"/> (the
    /// manual-button/menu path, unrate-limited) and, via the same
    /// <see cref="RefreshView"/> call, from <see cref="TriggerRateLimitedRefresh"/>
    /// once its own 5s floor clears.</summary>
    private void RequestFlowFetch()
    {
        if (_closing)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref _flowFetchInFlight, 1, 0) != 0)
        {
            _flowFetchPending = true;
            return;
        }
        StartFlowFetch();
    }

    /// <summary>The actual native fetch (Flow snapshot — now backed by
    /// cccog-bar-core's incremental transcript parser, task 12, but still a
    /// blocking native call in principle) runs on a threadpool worker, never
    /// the UI thread — a synchronous call here was directly responsible for
    /// the live-measured lag (~18% of one core sustained, system-wide mouse
    /// jank via the WH_MOUSE_LL hook while the UI thread was blocked inside
    /// it). Only the render-back touches XAML/DispatcherQueue state, per the
    /// 0xc000027b discipline every other background callback in this file
    /// already follows.</summary>
    private void StartFlowFetch()
    {
        _ = Task.Run(() =>
        {
            IReadOnlyList<FlowTreeRowViewModel> rows;
            int visibleJobs;
            try
            {
                rows = NativeControlClient.TryFetch(out visibleJobs);
            }
            catch (Exception exception)
            {
                CrashLog.Append(exception);
                rows = [];
                visibleJobs = 0;
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _flowFetchInFlight, 0);
                if (_closing)
                {
                    return;
                }
                Dashboard.RenderFlow(rows, visibleJobs);
                Dashboard.SetRefreshedText($"refreshed {DateTime.Now:HH:mm:ss}");
                _lastFlowRefreshAt = DateTimeOffset.UtcNow;
                if (_flowFetchPending)
                {
                    // A refresh was requested while this one was running —
                    // coalesced to exactly one more round, not dropped.
                    _flowFetchPending = false;
                    RequestFlowFetch();
                }
            });
        });
    }

    /// <summary>Task 12: the rate-limit floor on top of the existing 250ms
    /// debounce — reached only via the debounce timer's own tick (watcher
    /// bursts / the 60s fallback timer), never from the manual Refresh
    /// button/menu item/Ctrl+R, which call <see cref="RefreshView"/> directly
    /// and so always bypass this gate, per the operator's explicit ask.</summary>
    private void TriggerRateLimitedRefresh()
    {
        if (_closing)
        {
            return;
        }
        var elapsed = DateTimeOffset.UtcNow - _lastFlowRefreshAt;
        if (elapsed >= MinFlowRefreshInterval)
        {
            RefreshView();
            return;
        }
        if (_rateLimitDeferralScheduled)
        {
            return; // one is already queued for the boundary -- coalesce
        }
        _rateLimitDeferralScheduled = true;
        _rateLimitTimer.Interval = MinFlowRefreshInterval - elapsed;
        _rateLimitTimer.Start();
    }

    /// <summary>
    /// <paramref name="manual"/> distinguishes the Refresh-button click from
    /// the automatic timer tick / file-watcher debounce: claude/grok's live
    /// HTTP fetch sits behind a 300s TTL on the Rust side (TokenBar's own
    /// layering, agent_usage.rs's CLAUDE_HEADER_TTL_SECS) — a manual click
    /// may bypass that TTL but is itself still capped to once per 60s.
    /// </summary>
    private void RefreshQuotaCards(bool manual = false)
    {
        if (_closing || Interlocked.Exchange(ref _quotaPollInFlight, 1) != 0)
        {
            return;
        }
        Dashboard.ShowRefreshing();
        _ = Task.Run(() =>
        {
            IReadOnlyList<QuotaCardViewModel> cards;
            try
            {
                cards = NativeQuotaClient.TryPoll(manual);
            }
            catch (Exception exception)
            {
                CrashLog.Append(exception);
                cards = [];
            }
            finally
            {
                Interlocked.Exchange(ref _quotaPollInFlight, 0);
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closing)
                {
                    return;
                }
                if (cards.Count > 0)
                {
                    _everHadQuota = true;
                }
                if (cards.Count > 0 || !_everHadQuota)
                {
                    // Never hide the section while loading (lessons-learned
                    // #1) — Dashboard.RenderQuota keeps the loading line up
                    // instead of showing an empty card list.
                    Dashboard.RenderQuota(cards, _everHadQuota);
                }
                Dashboard.HideRefreshing();
            });
        });
    }

    /// <summary>Both file watchers below (dispatch root, presence dir) and
    /// the periodic <see cref="_flowTimer"/> all funnel into this one
    /// debounce-and-refresh trigger — one refresh choreography regardless
    /// of which of the three sources fired.</summary>
    private void RequestDebouncedRefresh()
    {
        if (_closing)
        {
            return;
        }
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void StartFileWatcher()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var dispatchRoot = IOPath.Combine(localAppData, "CCCG", "dispatch");
        if (Directory.Exists(dispatchRoot))
        {
            _watcher = new FileSystemWatcher(dispatchRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler changed = (_, _) => DispatcherQueue.TryEnqueue(RequestDebouncedRefresh);
            RenamedEventHandler renamed = (_, _) => DispatcherQueue.TryEnqueue(RequestDebouncedRefresh);
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += renamed;
        }

        // Presence heartbeats (hooks/cccg-presence-hook.js) — cheap,
        // low-churn events (one small file write per Claude Code hook
        // invocation), so watching directly makes 執行中/閒置 flips feel
        // instant instead of waiting up to 60s for the periodic timer.
        // Deliberately NOT watching ~/.claude/projects itself: transcript
        // writes are constant during an active turn and would fire this
        // handler continuously — the 60s timer already covers that source
        // without the event churn (bar/SYNC.md has the fuller rationale).
        var presenceRoot = IOPath.Combine(localAppData, "CCCG", "presence");
        if (Directory.Exists(presenceRoot))
        {
            _presenceWatcher = new FileSystemWatcher(presenceRoot)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler changed = (_, _) => DispatcherQueue.TryEnqueue(RequestDebouncedRefresh);
            RenamedEventHandler renamed = (_, _) => DispatcherQueue.TryEnqueue(RequestDebouncedRefresh);
            _presenceWatcher.Changed += changed;
            _presenceWatcher.Created += changed;
            _presenceWatcher.Deleted += changed;
            _presenceWatcher.Renamed += renamed;
        }
    }

    // ── Window chrome: ported from TokenBar-Windows FlyoutWindow.xaml.cs ──

    private enum Edge
    {
        Left = 0,
        Top = 1,
        Right = 2,
        Bottom = 3,
    }

    /// <summary>Which screen edge the taskbar sits on (ABM_GETTASKBARPOS);
    /// bottom when the shell won't say.</summary>
    private static Edge TaskbarEdge()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        return SHAppBarMessage(0x0005 /* ABM_GETTASKBARPOS */, ref data) != 0
            ? (Edge)data.uEdge
            : Edge.Bottom;
    }

    /// <summary>Offset a point toward a taskbar edge (the hide direction).</summary>
    private static PointInt32 Toward(PointInt32 p, Edge edge, int distance) => edge switch
    {
        Edge.Left => new PointInt32(p.X - distance, p.Y),
        Edge.Top => new PointInt32(p.X, p.Y - distance),
        Edge.Right => new PointInt32(p.X + distance, p.Y),
        _ => new PointInt32(p.X, p.Y + distance),
    };

    /// <summary>The grip-drag height persistence key, LocalSettings.cs's
    /// stand-in for TokenBar's "tokenbar.popover.height".</summary>
    private const string HeightSettingKey = "cccog.popover.height";

    /// <summary>Anchor against the taskbar corner of the work area, on
    /// whichever edge the taskbar occupies. Sizes the window and returns the
    /// resting position plus the slide start (offset toward the taskbar).
    /// Ported from TokenBar's PositionNearTray: a user-dragged height read
    /// from the settings store (LocalSettings here, AppSettings.Store
    /// there) is clamped into the work area; 0/unset falls back to the
    /// built-in FlyoutHeight.</summary>
    private (PointInt32 Resting, PointInt32 Start) PositionNearTray()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        if (dpi <= 96)
        {
            dpi = GetDpiForSystem();
        }
        var scale = dpi / 96.0;
        if (scale <= 0)
        {
            scale = 1;
        }
        var width = (int)(FlyoutWidth * scale);
        // cccog.popover.height (grip-drag persistence, stored in DIPs):
        // 0/unset = the built-in default; a pick is clamped into the work
        // area. Bounds are ordered explicitly — on a short work area at
        // high DPI the 480-DIP floor can exceed the ceiling and
        // Math.Clamp throws.
        var picked = double.TryParse(
            LocalSettings.GetString(HeightSettingKey), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var storedHeight)
            ? storedHeight : 0;
        var ceiling = work.Height - (int)(24 * scale);
        var floor = Math.Min((int)(480 * scale), ceiling);
        var wanted = picked > 0 ? picked * scale : FlyoutHeight * scale;
        var height = (int)Math.Clamp(wanted, floor, ceiling);
        AppWindow.Resize(new SizeInt32(width, height));

        var edge = TaskbarEdge();
        var resting = edge switch
        {
            // Tray lives at the taskbar's far end: bottom/top → right
            // corner, left/right → bottom corner.
            Edge.Left => new PointInt32(work.X + Margin, work.Y + work.Height - height - Margin),
            Edge.Top => new PointInt32(work.X + work.Width - width - Margin, work.Y + Margin),
            Edge.Right => new PointInt32(work.X + work.Width - width - Margin, work.Y + work.Height - height - Margin),
            _ => new PointInt32(work.X + work.Width - width - Margin, work.Y + work.Height - height - Margin),
        };
        return (resting, Toward(resting, edge, SlideDistance));
    }

    /// <summary>Move the top-level window each frame; DWM re-blurs the
    /// acrylic live, so the whole surface travels together.</summary>
    private async Task SlideWindowAsync(PointInt32 from, PointInt32 to, int durationMs, bool decelerate)
    {
        var token = ++_slideToken;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < durationMs)
        {
            if (token != _slideToken)
            {
                return; // superseded by a newer slide
            }

            var t = Math.Clamp(watch.ElapsedMilliseconds / (double)durationMs, 0, 1);
            var eased = decelerate ? 1 - Math.Pow(1 - t, 3) : Math.Pow(t, 2);
            AppWindow.Move(new PointInt32(
                (int)Math.Round(from.X + (to.X - from.X) * eased),
                (int)Math.Round(from.Y + (to.Y - from.Y) * eased)));
            await Task.Delay(8); // ~120Hz pacing; DWM coalesces to refresh rate
        }

        if (token == _slideToken)
        {
            AppWindow.Move(to);
        }
    }

    private void FadeContent(float from, float to, int durationMs)
    {
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
            .GetElementVisual(Content);
        var compositor = visual.Compositor;
        visual.Opacity = from;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, to);
        fade.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Opacity", fade);
    }

    /// <summary>Popup chrome: strip every residual frame style down to
    /// WS_POPUP (the presenter's borderless mode leaves WS_DLGFRAME behind,
    /// which draws a 1px outline DWMWA_BORDER_COLOR cannot remove), then
    /// round the corners and erase the DWM border color.</summary>
    private void ApplyPopupChrome()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        const int GWL_STYLE = -16;
        const long WS_POPUP = 0x80000000L;
        const long WS_VISIBLE = 0x10000000L;
        const long WS_CLIPCHILDREN = 0x02000000L;
        var oldStyle = GetWindowLongPtrW(hwnd, GWL_STYLE);
        var newStyle = (nint)(WS_POPUP | WS_CLIPCHILDREN | ((long)oldStyle & WS_VISIBLE));
        _ = SetWindowLongPtrW(hwnd, GWL_STYLE, newStyle);

        var corner = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref corner, sizeof(int));
        var borderColor = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        _ = DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref borderColor, sizeof(int));

        // HWND_TOPMOST directly: the presenter's IsAlwaysOnTop doesn't
        // survive the WS_POPUP style stomp, so the flyout sank under normal
        // windows whenever anything else was open. Re-assert through the
        // presenter as well (toggle forces a reapply of the z-band).
        const uint SWP_FRAMECHANGED = 0x0020;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOACTIVATE = 0x0010;
        var HWND_TOPMOST = new nint(-1);
        _ = SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = false;
            p.IsAlwaysOnTop = true;
        }
    }

    // ── Top-edge resize (the macOS footer drag handle, grown upward) ────

    private bool _resizing;
    private int _resizeStartCursorY;
    private int _resizeStartHeight;
    private int _resizeBottom;

    /// <summary>Drag the flyout's top edge to set its height; the bottom
    /// stays pinned at the taskbar. Global (physical) cursor coordinates
    /// keep the drag stable while the window itself moves, and the height
    /// key persists only on release — mid-drag writes would churn the
    /// settings file. Ported from TokenBar's WireResizeGrip, swapping
    /// AppSettings.Store for LocalSettings (bar/SYNC.md).</summary>
    private void WireResizeGrip()
    {
        ResizeGrip.PointerEntered += (_, _) => ResizeGripHint.Opacity = 1;
        ResizeGrip.PointerExited += (_, _) =>
        {
            if (!_resizing)
            {
                ResizeGripHint.Opacity = 0;
            }
        };
        ResizeGrip.PointerPressed += (_, e) =>
        {
            if (!GetCursorPos(out var pt))
            {
                return;
            }

            _resizing = true;
            _resizeStartCursorY = pt.Y;
            _resizeStartHeight = AppWindow.Size.Height;
            _resizeBottom = AppWindow.Position.Y + AppWindow.Size.Height;
            _ = ResizeGrip.CapturePointer(e.Pointer);
        };
        ResizeGrip.PointerMoved += (_, _) =>
        {
            if (!_resizing || !GetCursorPos(out var pt))
            {
                return;
            }

            var work = DisplayArea.GetFromWindowId(
                AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var scale = GetDpiForWindow(
                WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            var ceiling = Math.Min(
                _resizeBottom - work.Y, work.Height - (int)(24 * scale));
            var floor = Math.Min((int)(480 * scale), ceiling);
            var height = Math.Clamp(
                _resizeStartHeight + (_resizeStartCursorY - pt.Y), floor, ceiling);
            AppWindow.MoveAndResize(new RectInt32(
                AppWindow.Position.X, _resizeBottom - height,
                AppWindow.Size.Width, height));
        };
        ResizeGrip.PointerReleased += (_, e) =>
        {
            ResizeGrip.ReleasePointerCapture(e.Pointer);
            EndResize();
        };
        ResizeGrip.PointerCaptureLost += (_, _) => EndResize();
    }

    private void EndResize()
    {
        if (!_resizing)
        {
            return;
        }

        _resizing = false;
        ResizeGripHint.Opacity = 0;
        var scale = GetDpiForWindow(
            WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        LocalSettings.SetString(
            HeightSettingKey,
            (AppWindow.Size.Height / scale).ToString(CultureInfo.InvariantCulture));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORPOINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CURSORPOINT pt);

    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _acrylicConfig;

    /// <summary>Acrylic that never falls back to the opaque inactive color:
    /// IsInputActive is pinned true for the window's lifetime. This
    /// replaces the previous CCCOG shell's Mica-first / dark-tint fallback
    /// entirely — TokenBar never uses Mica for this popover, and this is
    /// its exact tint/opacity pair (not a CCCOG reinterpretation).</summary>
    private void SetupAlwaysOnAcrylic()
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
            return;
        }

        _acrylicConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
        };
        _acrylic = new DesktopAcrylicController
        {
            // Default acrylic reads nearly opaque; thin it out so the
            // desktop shows through.
            TintOpacity = 0.25f,
            LuminosityOpacity = 0.55f,
        };
        ApplyAcrylicTheme();
        Dashboard.ActualThemeChanged += (_, _) => ApplyAcrylicTheme();
        _acrylic.AddSystemBackdropTarget(
            WinRT.CastExtensions.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>(this));
        _acrylic.SetSystemBackdropConfiguration(_acrylicConfig);
    }

    private void ApplyAcrylicTheme()
    {
        if (_acrylic is null || _acrylicConfig is null)
        {
            return;
        }

        var dark = Dashboard.ActualTheme == ElementTheme.Dark;
        _acrylicConfig.Theme = dark
            ? SystemBackdropTheme.Dark
            : SystemBackdropTheme.Light;
        _acrylic.TintColor = dark
            ? Windows.UI.Color.FromArgb(255, 28, 28, 34)
            : Windows.UI.Color.FromArgb(255, 243, 243, 243);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        nint hwnd, nint hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    // ── WH_MOUSE_LL: wheel-focus workaround + task 13's light-dismiss ──────

    private delegate nint LowLevelMouseProc(int code, nint wParam, nint lParam);

    private LowLevelMouseProc? _mouseProc; // kept alive while the hook is set
    private nint _mouseHook;

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MOUSEWHEEL = 0x020A;

    /// <summary>Wheel input (unchanged, task 7) and, as of task 13, light-
    /// dismiss: the system never delivers normal input (wheel OR
    /// activation) to this focusless popup, so both problems share one
    /// root cause and — since we're already watching every system-wide
    /// mouse event for the wheel case — the same WH_MOUSE_LL hook, so a
    /// button-down anywhere outside the flyout hides it deterministically,
    /// independent of whatever the window's own Activated/Deactivated
    /// state does or doesn't do. Installed only while visible — ported
    /// verbatim from TokenBar's FlyoutWindow.xaml.cs (the tray-flyout
    /// standard, EarTrumpet et al.) for the wheel half.</summary>
    private void InstallWheelHook()
    {
        if (_mouseHook != 0)
        {
            return;
        }

        // Computed once per show, not per click inside the hook callback —
        // a WH_MOUSE_LL callback has to return fast (Windows silently
        // detaches a hook that blocks the input pipeline too long), so the
        // handful of FindWindow/GetWindowRect calls this needs happen here
        // instead, cached in a closure-captured local for the hook to just
        // read.
        var notificationAreaRect = GetNotificationAreaRect();

        _mouseProc = (code, wParam, lParam) =>
        {
            if (code >= 0)
            {
                if (wParam == WM_MOUSEWHEEL)
                {
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var pos = AppWindow.Position;
                    var size = AppWindow.Size;
                    var inside = AppWindow.IsVisible &&
                        data.pt.X >= pos.X && data.pt.X < pos.X + size.Width &&
                        data.pt.Y >= pos.Y && data.pt.Y < pos.Y + size.Height;
                    if (inside)
                    {
                        var delta = unchecked((short)(data.mouseData >> 16));
                        var windowX = data.pt.X - pos.X;
                        var windowY = data.pt.Y - pos.Y;
                        _ = DispatcherQueue.TryEnqueue(() =>
                            Dashboard.RouteGlobalWheel(windowX, windowY, delta));
                        return 1; // consumed: don't let the focused app scroll too
                    }
                }
                else if (!_keepOpen && AppWindow.IsVisible &&
                    (wParam == WM_LBUTTONDOWN || wParam == WM_RBUTTONDOWN || wParam == WM_MBUTTONDOWN))
                {
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var pos = AppWindow.Position;
                    var size = AppWindow.Size;
                    var flyoutRect = new RectInt32(pos.X, pos.Y, size.Width, size.Height);
                    if (ShouldLightDismiss(data.pt.X, data.pt.Y, flyoutRect, notificationAreaRect))
                    {
                        // Never swallow the click (return falls through to
                        // CallNextHookEx below either way) — the user's
                        // click on whatever else is under the cursor must
                        // still land normally; this only schedules OUR
                        // window to hide.
                        _ = DispatcherQueue.TryEnqueue(HideFlyout);
                    }
                }
            }

            return CallNextHookEx(0, code, wParam, lParam);
        };
        _mouseHook = SetWindowsHookExW(
            14 /* WH_MOUSE_LL */, _mouseProc, GetModuleHandleW(null), 0);
    }

    private void RemoveWheelHook()
    {
        if (_mouseHook != 0)
        {
            _ = UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
            _mouseProc = null;
        }
    }

    /// <summary>Pure hit-test (task 13): should a button-down at physical
    /// screen point (<paramref name="x"/>, <paramref name="y"/>) hide the
    /// flyout? True exactly when the click lands outside BOTH the
    /// flyout's own rect and the taskbar notification area. The second
    /// exclusion matters even though our own tray icon is a separate
    /// concern from "clicking away" — without it, clicking our OWN tray
    /// icon to close an already-open flyout races this hook's hide against
    /// <see cref="ToggleFlyout"/>'s own visibility check: if the hook's
    /// hide lands first, Toggle then sees "already hidden" and calls
    /// ShowFlyout instead of HideFlyout, silently reopening the very
    /// flyout the click was meant to close. Excluding the whole
    /// notification area (not just our one icon's exact bounds, which
    /// would need internals this app doesn't have access to — H.NotifyIcon
    /// doesn't expose the icon's own `Shell_NotifyIconGetRect` identifiers)
    /// is a deliberately generous but robust proxy: clicking any tray icon
    /// is already handled by ITS OWN click logic, ours included.
    /// No dependency on live window/game state — callable and testable in
    /// isolation.</summary>
    internal static bool ShouldLightDismiss(int x, int y, RectInt32 flyoutRect, RectInt32? notificationAreaRect)
    {
        if (Contains(flyoutRect, x, y))
        {
            return false;
        }
        if (notificationAreaRect is { } tray && Contains(tray, x, y))
        {
            return false;
        }
        return true;

        static bool Contains(RectInt32 rect, int px, int py) =>
            px >= rect.X && px < rect.X + rect.Width && py >= rect.Y && py < rect.Y + rect.Height;
    }

    /// <summary>The taskbar's notification-area child window
    /// ("TrayNotifyWnd", the standard Explorer window class hosting every
    /// tray icon plus the "show hidden icons" arrow and the clock) — a
    /// plain, fast, synchronous Win32 lookup (no COM, no UI Automation),
    /// safe to call right before installing the hook. `null` on any lookup
    /// failure (custom shells, a transient Explorer restart, etc.) — the
    /// light-dismiss check just skips the tray exclusion in that case
    /// rather than failing closed.</summary>
    private static RectInt32? GetNotificationAreaRect()
    {
        var trayWnd = FindWindowW("Shell_TrayWnd", null);
        if (trayWnd == 0)
        {
            return null;
        }
        var notifyWnd = FindWindowExW(trayWnd, 0, "TrayNotifyWnd", null);
        if (notifyWnd == 0)
        {
            return null;
        }
        if (!GetWindowRect(notifyWnd, out var rect))
        {
            return null;
        }
        return new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowExW(nint parent, nint childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public PointL pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint SetWindowsHookExW(
        int hookId, LowLevelMouseProc proc, nint module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);
}

internal static class NativeQuotaClient
{
    [DllImport("cccog_bar_ffi", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr cccog_bar_poll_quotas([MarshalAs(UnmanagedType.LPUTF8Str)] string inputJson);

    [DllImport("cccog_bar_ffi", CallingConvention = CallingConvention.Cdecl)]
    private static extern void cccog_bar_free_string(IntPtr value);

    public static IReadOnlyList<QuotaCardViewModel> TryPoll(bool manual = false)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var input = JsonSerializer.Serialize(new
        {
            codexSessionsPath = IOPath.Combine(profile, ".codex", "sessions"),
            claudeCredentialPath = IOPath.Combine(profile, ".claude", ".credentials.json"),
            grokAuthPath = IOPath.Combine(profile, ".grok", "auth.json"),
            // Enables the stale-snapshot age flag and the job-failure
            // quota-limit cross-check (Rust: enrich_quota_cards) — a codex
            // rollout-tail read that froze on the last pre-block value would
            // otherwise keep reporting "Fresh" forever.
            dispatchJobsPath = IOPath.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CCCG", "dispatch", "jobs"),
            // True only for the Refresh-button click — see
            // FlyoutWindow.RefreshQuotaCards and Rust's
            // precheck_http_provider (the 300s TTL / 60s manual-bypass gate).
            manual,
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        try
        {
            var pointer = cccog_bar_poll_quotas(input);
            if (pointer == IntPtr.Zero)
            {
                return [];
            }
            try
            {
                var json = Marshal.PtrToStringUTF8(pointer);
                return Parse(json);
            }
            finally
            {
                cccog_bar_free_string(pointer);
            }
        }
        catch (DllNotFoundException)
        {
            return [];
        }
        catch (EntryPointNotFoundException)
        {
            return [];
        }
        catch (BadImageFormatException)
        {
            return [];
        }
        catch (ExternalException)
        {
            return [];
        }
    }

    private static IReadOnlyList<QuotaCardViewModel> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("quotaCards", out var cards))
            {
                return [];
            }
            var result = new List<QuotaCardViewModel>();
            foreach (var card in cards.EnumerateArray())
            {
                var providerId = card.TryGetProperty("clientId", out var clientValue)
                    ? clientValue.GetString() ?? "provider"
                    : "provider";
                var state = card.TryGetProperty("state", out var stateValue)
                    ? stateValue.GetString() ?? "stale"
                    : "stale";
                var diagnostic = card.TryGetProperty("diagnostic", out var diagnosticValue)
                    ? diagnosticValue.GetString()
                    : null;
                // The fuller sentence `diagnostic` was condensed from
                // (dispatch-failure time, verbatim provider text) — never
                // deleted, just moved here for a hover tooltip (bar/SYNC.md).
                var diagnosticDetail = card.TryGetProperty("diagnosticDetail", out var detailValue)
                    ? detailValue.GetString()
                    : null;
                if (!card.TryGetProperty("windows", out var windows) || windows.GetArrayLength() == 0)
                {
                    // A failed/empty card still renders — with the concrete
                    // reason, never a vague "unavailable" (locked UX).
                    result.Add(new QuotaCardViewModel
                    {
                        ProviderId = providerId,
                        Label = providerId,
                        WindowLabel = providerId,
                        UsedPercent = 0,
                        ResetText = "Reset time unavailable",
                        StateText = diagnostic ?? $"{state.ToLowerInvariant()}",
                        DiagnosticDetail = diagnosticDetail,
                        IsFailure = true,
                        IsWholeProviderFailure = true,
                    });
                    continue;
                }
                foreach (var window in windows.EnumerateArray())
                {
                    var used = window.TryGetProperty("usedPercent", out var usedValue)
                        ? usedValue.GetDouble()
                        : 0;
                    var label = window.TryGetProperty("label", out var labelValue)
                        ? labelValue.GetString() ?? "Quota"
                        : "Quota";
                    // resetsAt already arrives fully formatted — Rust's
                    // cccog_bar_quota::normalize_reset_display is the single
                    // place that parses codex/claude/grok's different reset
                    // shapes and renders the unified bare "MM/DD HH:MM" (or
                    // "MM/DD") display text, so it is shown here verbatim
                    // rather than re-parsed on the C# side (see bar/SYNC.md).
                    var reset = window.TryGetProperty("resetsAt", out var resetValue)
                        ? resetValue.GetString() ?? "Reset time unavailable"
                        : "Reset time unavailable";
                    var clampedUsed = Math.Clamp(used, 0, 100);
                    var resetText = string.IsNullOrWhiteSpace(reset) ? "Reset time unavailable" : reset;
                    // displayLine is the single composed row
                    // (cccog_bar_quota::format_window_line: "<label>  <NN%>
                    // <date>") — the fallback composes the identical shape
                    // locally only if an older FFI build omitted the field.
                    var displayLine = window.TryGetProperty("displayLine", out var displayLineValue)
                        ? displayLineValue.GetString()
                        : null;
                    result.Add(new QuotaCardViewModel
                    {
                        ProviderId = providerId,
                        Label = $"{providerId} - {label}",
                        WindowLabel = label,
                        UsedPercent = clampedUsed,
                        ResetText = resetText,
                        DisplayLine = string.IsNullOrEmpty(displayLine)
                            ? $"{label}  {clampedUsed:0.#}%  {resetText}"
                            : displayLine,
                        StateText = state.Equals("fresh", StringComparison.OrdinalIgnoreCase)
                            ? "fresh"
                            : (diagnostic ?? "stale"),
                        DiagnosticDetail = diagnosticDetail,
                        IsFailure = !state.Equals("fresh", StringComparison.OrdinalIgnoreCase),
                    });
                }
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// The Flow tree via <c>cccog_bar_control_snapshot</c>'s <c>tree</c> field —
/// two live data sources combined in Rust
/// (<c>cccog_bar_core::tree::build_tree</c>): CCCG dispatch jobs (as before)
/// PLUS live local Claude Code sessions/subagents (new — see
/// <c>cccog_bar_core::claude_sessions</c>/<c>presence</c>). Replaces the
/// previous flat, aggregated-rows-only <c>NativeControlClient</c>, which
/// read the legacy <c>control.rows</c> field; see bar/SYNC.md.
/// </summary>
internal static class NativeControlClient
{
    [DllImport("cccog_bar_ffi", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr cccog_bar_control_snapshot([MarshalAs(UnmanagedType.LPUTF8Str)] string inputJson);

    [DllImport("cccog_bar_ffi", CallingConvention = CallingConvention.Cdecl)]
    private static extern void cccog_bar_free_string(IntPtr value);

    public static IReadOnlyList<FlowTreeRowViewModel> TryFetch(out int visibleRows)
    {
        visibleRows = 0;
        var dispatchRoot = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCCG", "dispatch");
        var input = JsonSerializer.Serialize(new
        {
            dispatchRoot,
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        try
        {
            var pointer = cccog_bar_control_snapshot(input);
            if (pointer == IntPtr.Zero)
            {
                return [];
            }
            try
            {
                var json = Marshal.PtrToStringUTF8(pointer);
                return Parse(json, out visibleRows);
            }
            finally
            {
                cccog_bar_free_string(pointer);
            }
        }
        catch (DllNotFoundException)
        {
            return [];
        }
        catch (EntryPointNotFoundException)
        {
            return [];
        }
        catch (BadImageFormatException)
        {
            return [];
        }
        catch (ExternalException)
        {
            return [];
        }
    }

    private static IReadOnlyList<FlowTreeRowViewModel> Parse(string? json, out int visibleRows)
    {
        visibleRows = 0;
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var result = new List<FlowTreeRowViewModel>();
            foreach (var row in tree.EnumerateArray())
            {
                result.Add(ToRow(row));
            }
            visibleRows = result.Count;
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static FlowTreeRowViewModel ToRow(JsonElement row)
    {
        var depth = row.TryGetProperty("depth", out var depthValue) ? depthValue.GetInt32() : 0;
        var kind = String(row, "kind") ?? "controller";
        var label = String(row, "label") ?? "";
        var typeModelText = String(row, "typeModelText");
        var provider = String(row, "provider") ?? "unknown";
        var stateLabel = String(row, "stateLabel") ?? "";
        var pulse = row.TryGetProperty("pulse", out var pulseValue) && pulseValue.GetBoolean();
        long? elapsedSeconds = row.TryGetProperty("elapsedSeconds", out var elapsedValue)
            && elapsedValue.ValueKind == JsonValueKind.Number
                ? elapsedValue.GetInt64()
                : null;
        long? contextTokens = row.TryGetProperty("contextTokens", out var contextTokensValue)
            && contextTokensValue.ValueKind == JsonValueKind.Number
                ? contextTokensValue.GetInt64()
                : null;
        double? contextPercent = row.TryGetProperty("contextPercent", out var contextPercentValue)
            && contextPercentValue.ValueKind == JsonValueKind.Number
                ? contextPercentValue.GetDouble()
                : null;
        var (contextText, contextTooltip) = FormatContextUsage(contextTokens, contextPercent);

        return new FlowTreeRowViewModel
        {
            Depth = depth,
            Kind = kind,
            Label = label,
            TypeModelText = typeModelText,
            Provider = provider,
            StateLabel = stateLabel,
            Pulse = pulse,
            ElapsedText = FormatTreeElapsed(elapsedSeconds),
            DotOpacity = StatusVisual.Opacity(stateLabel),
            ContextText = contextText,
            ContextTooltip = contextTooltip,
        };
    }

    private static string? String(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Bare duration, no "ago" suffix (operator's own tree mock:
    /// "45s", "2m", "12m", "2h" on the right edge) — every tree row shows an
    /// elapsed time next to its state word, so the word itself already says
    /// whether that's "still going" or "since it went idle".</summary>
    private static string FormatTreeElapsed(long? elapsedSeconds)
    {
        if (elapsedSeconds is not { } seconds || seconds < 0)
        {
            return "";
        }
        if (seconds < 60)
        {
            return $"{seconds}s";
        }
        var span = TimeSpan.FromSeconds(seconds);
        return span < TimeSpan.FromHours(1)
            ? $"{(int)span.TotalMinutes}m"
            : $"{(int)span.TotalHours}h{(int)span.TotalMinutes % 60:00}m";
    }

    /// <summary>Task 14 (2026-08-18): "ctx 36%" when the session/agent's
    /// latest-turn model resolves to a known context window, else
    /// "ctx 355k" (tokens only — never a guessed window, matching
    /// `cccog-bar-core::claude_sessions::context_window_for_model`'s own
    /// "unknown model, emit tokens only" rule). `(null, null)` when the row
    /// never carried usage at all — dispatch-job/terminal/owner rows, or a
    /// session/agent whose transcript has no assistant turn yet — so the
    /// caller renders nothing rather than a fabricated "ctx 0%".
    /// <para>The tooltip is reverse-derived from tokens+percent (the wire
    /// only carries those two numbers): `window = tokens / (percent/100)`,
    /// which — since percent always comes from one of the fixed lookup
    /// values (200k/1M) — rounds back to a clean number.</para></summary>
    private static (string? Text, string? Tooltip) FormatContextUsage(long? tokens, double? percent)
    {
        if (tokens is not { } tokenCount || tokenCount < 0)
        {
            return (null, null);
        }
        var tokensLabel = FormatTokenCount(tokenCount);
        if (percent is not { } pct || pct <= 0)
        {
            return ($"ctx {tokensLabel}", $"{tokensLabel} tokens");
        }
        var windowLabel = FormatTokenCount((long)Math.Round(tokenCount / (pct / 100.0)));
        return ($"ctx {pct:0}%", $"{tokensLabel} tokens ({pct:0}% of {windowLabel})");
    }

    /// <summary>"355.4k" / "1M" / "820" — one decimal place, trimmed when
    /// it's a whole number ("1M" not "1.0M").</summary>
    private static string FormatTokenCount(long tokens)
    {
        if (tokens >= 1_000_000)
        {
            return $"{tokens / 1_000_000.0:0.#}M";
        }
        if (tokens >= 1_000)
        {
            return $"{tokens / 1_000.0:0.#}k";
        }
        return tokens.ToString();
    }
}
