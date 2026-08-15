using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
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

        Dashboard.RefreshRequested += RefreshView;

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
            args.Cancel = true;
            HideFlyout();
        };

        WireResizeGripHover();

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
    private void RefreshView()
    {
        if (_closing || !_viewReady)
        {
            return;
        }

        var rows = NativeControlClient.TryFetch(out var visibleJobs);
        Dashboard.RenderFlow(rows, visibleJobs);
        Dashboard.SetRefreshedText($"refreshed {DateTime.Now:HH:mm:ss}");
        RefreshQuotaCards();
    }

    private void RefreshQuotaCards()
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
                cards = NativeQuotaClient.TryPoll();
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

    /// <summary>Anchor against the taskbar corner of the work area, on
    /// whichever edge the taskbar occupies. Sizes the window and returns the
    /// resting position plus the slide start (offset toward the taskbar).
    /// Adapted from TokenBar's PositionNearTray: TokenBar clamps a
    /// user-dragged height read from AppSettings.Store; CCCOG has no
    /// persisted-settings store, so this always sizes to the fixed
    /// FlyoutHeight (still clamped into the work area, same as before this
    /// port).</summary>
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
        var ceiling = work.Height - (int)(24 * scale);
        var floor = Math.Min((int)(480 * scale), ceiling);
        var height = (int)Math.Clamp(FlyoutHeight * scale, floor, ceiling);
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

    /// <summary>Hover-only: brightens the resize-grip hint on pointer
    /// enter/exit. TokenBar also wires PointerPressed/Moved/Released to drag
    /// the top edge and persists the result via AppSettings.Store — CCCOG
    /// has no settings store, so that drag behavior was not ported (named
    /// gap; see bar/SYNC.md). The window keeps its fixed FlyoutHeight.</summary>
    private void WireResizeGripHover()
    {
        ResizeGrip.PointerEntered += (_, _) => ResizeGripHint.Opacity = 1;
        ResizeGrip.PointerExited += (_, _) => ResizeGripHint.Opacity = 0;
    }

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
}

internal static class NativeQuotaClient
{
    [DllImport("cccog_bar_ffi", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr cccog_bar_poll_quotas([MarshalAs(UnmanagedType.LPUTF8Str)] string inputJson);

    [DllImport("cccog_bar_ffi", CallingConvention = CallingConvention.Cdecl)]
    private static extern void cccog_bar_free_string(IntPtr value);

    public static IReadOnlyList<QuotaCardViewModel> TryPoll()
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
                    var reset = window.TryGetProperty("resetsAt", out var resetValue)
                        ? resetValue.GetString() ?? "Reset time unavailable"
                        : "Reset time unavailable";
                    result.Add(new QuotaCardViewModel
                    {
                        ProviderId = providerId,
                        Label = $"{providerId} - {label}",
                        WindowLabel = label,
                        UsedPercent = Math.Clamp(used, 0, 100),
                        ResetText = FormatReset(reset),
                        StateText = state.Equals("fresh", StringComparison.OrdinalIgnoreCase)
                            ? "fresh"
                            : (diagnostic ?? "stale"),
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

    private static string FormatReset(string value)
    {
        if (long.TryParse(value, out var epoch))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().ToString("g");
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }
        return string.IsNullOrWhiteSpace(value) ? "Reset time unavailable" : value;
    }
}

/// <summary>
/// Flow rows via <c>cccog_bar_control_snapshot</c> — the windowed (Active +
/// last-2h terminal), same-path-aggregated, target-title-resolved view built
/// by <c>cccog_bar_core::control</c>. Replaces the previous
/// <c>LocalSnapshotReader</c>, which re-scanned the whole dispatch root
/// unbounded by age on every refresh; see bar/SYNC.md.
/// </summary>
internal static class NativeControlClient
{
    [DllImport("cccog_bar_ffi", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr cccog_bar_control_snapshot([MarshalAs(UnmanagedType.LPUTF8Str)] string inputJson);

    [DllImport("cccog_bar_ffi", CallingConvention = CallingConvention.Cdecl)]
    private static extern void cccog_bar_free_string(IntPtr value);

    private static readonly SolidColorBrush StaleGroupBrush =
        new(Windows.UI.Color.FromArgb(255, 0x6B, 0x72, 0x80));
    private static readonly SolidColorBrush NormalLabelBrush =
        new(Windows.UI.Color.FromArgb(255, 0xF8, 0xFA, 0xFC));
    private static readonly SolidColorBrush UnknownSourceLabelBrush =
        new(Windows.UI.Color.FromArgb(180, 0x9A, 0xA5, 0xB4));

    public static IReadOnlyList<FlowRowViewModel> TryFetch(out int visibleJobs)
    {
        visibleJobs = 0;
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
                return Parse(json, out visibleJobs);
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

    private static IReadOnlyList<FlowRowViewModel> Parse(string? json, out int visibleJobs)
    {
        visibleJobs = 0;
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("control", out var control))
            {
                return [];
            }
            if (!control.TryGetProperty("available", out var availableValue) || !availableValue.GetBoolean())
            {
                return [];
            }
            if (!control.TryGetProperty("rows", out var rows))
            {
                return [];
            }
            var result = new List<FlowRowViewModel>();
            foreach (var row in rows.EnumerateArray())
            {
                result.Add(ToRow(row));
            }
            visibleJobs = result.Count;
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static FlowRowViewModel ToRow(JsonElement row)
    {
        var source = String(row, "source") ?? "unknown";
        var target = String(row, "target") ?? "";
        var targetProvider = String(row, "targetProvider") ?? "unknown";
        var status = String(row, "status") ?? "unknown";
        var active = row.TryGetProperty("active", out var activeValue) && activeValue.GetBoolean();
        var isStaleGroup = row.TryGetProperty("isStaleGroup", out var staleValue) && staleValue.GetBoolean();
        long? elapsedSeconds = row.TryGetProperty("elapsedSeconds", out var elapsedValue)
            && elapsedValue.ValueKind == JsonValueKind.Number
                ? elapsedValue.GetInt64()
                : null;
        var count = row.TryGetProperty("count", out var countValue) ? countValue.GetInt32() : 1;
        var sourceIsUnknown = row.TryGetProperty("sourceIsUnknown", out var unknownValue) && unknownValue.GetBoolean();

        var summaries = new List<string>();
        if (row.TryGetProperty("taskSummaries", out var summariesValue))
        {
            foreach (var item in summariesValue.EnumerateArray())
            {
                var text = item.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    summaries.Add(text);
                }
            }
        }

        return new FlowRowViewModel
        {
            ChainText = isStaleGroup ? target : $"{source} → {target}",
            CountText = count > 1 ? $"×{count}" : "",
            ElapsedText = FormatElapsed(elapsedSeconds, active, isStaleGroup),
            TaskSummary = summaries.Count == 0 ? "No task summary" : string.Join(Environment.NewLine, summaries),
            DotBrush = isStaleGroup
                ? StaleGroupBrush
                : new SolidColorBrush(ProviderPalette.Color(targetProvider)),
            DotOpacity = isStaleGroup ? StatusVisual.Opacity("stale") : StatusVisual.Opacity(status),
            Pulse = !isStaleGroup && StatusVisual.Pulses(status),
            LabelBrush = sourceIsUnknown ? UnknownSourceLabelBrush : NormalLabelBrush,
            IsStaleGroup = isStaleGroup,
        };
    }

    private static string? String(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FormatElapsed(long? elapsedSeconds, bool active, bool isStaleGroup)
    {
        if (isStaleGroup)
        {
            return "> 6h queued";
        }
        if (elapsedSeconds is not { } seconds)
        {
            return active ? "active" : "";
        }
        var span = TimeSpan.FromSeconds(Math.Max(seconds, 0));
        var elapsed = span < TimeSpan.FromMinutes(1) ? "<1m" :
            span < TimeSpan.FromHours(1) ? $"{(int)span.TotalMinutes}m" :
            $"{(int)span.TotalHours}h{(int)span.TotalMinutes % 60:00}m";
        return active ? elapsed : $"{elapsed} ago";
    }
}
