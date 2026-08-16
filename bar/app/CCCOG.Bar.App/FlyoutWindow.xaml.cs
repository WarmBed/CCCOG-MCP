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
    private FileSystemWatcher? _watcher;
    private int _quotaPollInFlight;
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

        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(250);
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshView();
        };
        _quotaTimer = DispatcherQueue.CreateTimer();
        _quotaTimer.Interval = TimeSpan.FromSeconds(60);
        _quotaTimer.Tick += (_, _) => RefreshQuotaCards();
        Closed += (_, _) =>
        {
            _closing = true;
            _watcher?.Dispose();
            _refreshTimer.Stop();
            _quotaTimer.Stop();
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

        var rows = NativeControlClient.TryFetch(out var visibleJobs);
        Dashboard.RenderFlow(rows, visibleJobs);
        Dashboard.SetRefreshedText($"refreshed {DateTime.Now:HH:mm:ss}");
        RefreshQuotaCards(manualQuota);
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

    private void StartFileWatcher()
    {
        var root = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCCG", "dispatch");
        if (!Directory.Exists(root))
        {
            return;
        }
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler changed = (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing)
            {
                return;
            }
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
        RenamedEventHandler renamed = (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing)
            {
                return;
            }
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
        _watcher.Changed += changed;
        _watcher.Created += changed;
        _watcher.Deleted += changed;
        _watcher.Renamed += renamed;
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

    // ── WH_MOUSE_LL wheel-focus workaround ───────────────────────────────

    private delegate nint LowLevelMouseProc(int code, nint wParam, nint lParam);

    private LowLevelMouseProc? _mouseProc; // kept alive while the hook is set
    private nint _mouseHook;

    /// <summary>Last resort for wheel input: the system never delivers wheel
    /// messages to this focusless popup, so while the flyout is visible a
    /// WH_MOUSE_LL hook watches for wheel events inside the flyout rect,
    /// scrolls the dashboard, and swallows the event so the focused app
    /// doesn't also scroll. Installed only while visible — ported verbatim
    /// from TokenBar's FlyoutWindow.xaml.cs (the tray-flyout standard,
    /// EarTrumpet et al.).</summary>
    private void InstallWheelHook()
    {
        if (_mouseHook != 0)
        {
            return;
        }

        _mouseProc = (code, wParam, lParam) =>
        {
            if (code >= 0 && wParam == 0x020A /* WM_MOUSEWHEEL */)
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
            DotBrush = new SolidColorBrush(ProviderPalette.Color(provider)),
            DotOpacity = StatusVisual.Opacity(stateLabel),
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
}
