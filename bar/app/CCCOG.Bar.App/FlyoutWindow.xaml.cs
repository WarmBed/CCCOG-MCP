using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Graphics;
using IOPath = System.IO.Path;

namespace CCCOG.Bar.App;

/// <summary>
/// The slim CCCOG-Bar companion: a tray-anchored, Mica/Acrylic-backed
/// flyout with exactly two sections — quota cards and flow rows. No tabs,
/// charts, settings pages, or history parsing; see
/// docs/plans/2026-08-16-cccog-bar-v2-slim.md for the performance contract
/// this exists to keep (window visible &lt;1s cold; quota arrives async).
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    private const int FlyoutWidth = 500;
    private const int FlyoutHeight = 640;
    private const int Margin = 12;

    private readonly LocalSnapshotReader _reader = new();
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

    public FlyoutWindow()
    {
        InitializeComponent();

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            HideFlyout();
        };
        RefreshButton.Click += (_, _) => RefreshView();

        SetupBackdrop();

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
        };

        // Deliberately do NOT read the dispatch root here: the constructor
        // must return so the caller can call AppWindow.Show() as early as
        // possible (performance contract: window visible < 1s cold). The
        // file read — milliseconds, per docs/plans/2026-08-16-cccog-bar-v2-slim.md
        // — happens once ShowFlyout() calls RefreshView() after Show().
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

    private bool _quotaTimerStarted;

    public void ShowFlyout()
    {
        if (_closing)
        {
            return;
        }
        AppWindow.Show();
        PositionNearTray();
        AppWindow.MoveInZOrderAtTop();
        Activate();
        _ = SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        RefreshView();
        if (!_quotaTimerStarted)
        {
            _quotaTimerStarted = true;
            _quotaTimer.Start();
        }
    }

    public void HideFlyout()
    {
        if (!AppWindow.IsVisible)
        {
            return;
        }
        AppWindow.Hide();
    }

    /// <summary>
    /// Flow rows are file-only and render synchronously — the &lt;1s cold
    /// window budget depends on this never touching the network or parsing
    /// usage history. Quota is kicked off separately and arrives async.
    /// </summary>
    private void RefreshView()
    {
        if (_closing || !_viewReady)
        {
            return;
        }

        var snapshot = _reader.Read(DateTimeOffset.Now);
        FlowLoadingText.Visibility = Visibility.Collapsed;
        RowsList.ItemsSource = snapshot.Rows;
        FlowEmptyText.Visibility = snapshot.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = $"{snapshot.VisibleJobs} flow row(s) - tokens only";
        RefreshedText.Text = $"refreshed {DateTime.Now:HH:mm:ss}";
        RefreshQuotaCards();
    }

    private void RefreshQuotaCards()
    {
        if (_closing || Interlocked.Exchange(ref _quotaPollInFlight, 1) != 0)
        {
            return;
        }
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
                    QuotaLoadingText.Visibility = Visibility.Collapsed;
                    QuotaCardsList.ItemsSource = cards;
                }
                else if (!_everHadQuota)
                {
                    // Never hide the section while loading (lessons-learned
                    // #1) — keep the loading line up instead of showing an
                    // empty card list.
                    QuotaLoadingText.Visibility = Visibility.Visible;
                }
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

    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _acrylicConfig;

    /// <summary>Mica when the compositor supports it (the TokenBar-style
    /// glass look); Acrylic is the fallback for older builds. This is a
    /// tray-resident popover, so the backdrop config stays pinned "active"
    /// rather than dimming when the window loses focus.</summary>
    private void SetupBackdrop()
    {
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            return;
        }
        if (!DesktopAcrylicController.IsSupported())
        {
            return;
        }
        _acrylicConfig = new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Dark };
        _acrylic = new DesktopAcrylicController
        {
            TintOpacity = 0.35f,
            LuminosityOpacity = 0.6f,
            TintColor = Windows.UI.Color.FromArgb(255, 19, 24, 36),
        };
        _acrylic.AddSystemBackdropTarget(
            WinRT.CastExtensions.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>(this));
        _acrylic.SetSystemBackdropConfiguration(_acrylicConfig);
        Closed += (_, _) =>
        {
            _acrylic?.Dispose();
            _acrylic = null;
        };
    }

    private void PositionNearTray()
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
        var desiredHeight = (int)(FlyoutHeight * scale);
        var height = Math.Min(desiredHeight, work.Height - (int)(Margin * 2 * scale));
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(
            work.X + work.Width - width - (int)(Margin * scale),
            work.Y + work.Height - height - (int)(Margin * scale)));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);
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
                        UsedPercent = 0,
                        ResetText = "Reset time unavailable",
                        StateText = diagnostic ?? $"{state.ToLowerInvariant()}",
                        IsFailure = true,
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
