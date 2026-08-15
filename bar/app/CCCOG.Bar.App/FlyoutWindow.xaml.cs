using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using IOPath = System.IO.Path;

namespace CCCOG.Bar.App;

public sealed partial class FlyoutWindow : Window
{
    private const int FlyoutWidth = 400;
    private const int FlyoutHeight = 780;
    private const int Margin = 12;

    private readonly LocalSnapshotReader _reader = new();
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer _quotaTimer;
    private readonly Action _openFullGraph;
    private FileSystemWatcher? _watcher;
    private int _quotaPollInFlight;
    private bool _closing = false;
    private bool _viewReady;

    public FlyoutWindow(Action openFullGraph)
    {
        InitializeComponent();
        _openFullGraph = openFullGraph;

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
        ExpandButton.Click += (_, _) =>
        {
            HideFlyout();
            _openFullGraph();
        };

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

        _viewReady = true;
        StartFileWatcher();
        RefreshView();
        _quotaTimer.Start();
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
    }

    public void HideFlyout()
    {
        if (!AppWindow.IsVisible)
        {
            return;
        }
        AppWindow.Hide();
    }

    private void RefreshView()
    {
        if (_closing || !_viewReady)
        {
            return;
        }

        var snapshot = _reader.Read("all", "2h", DateTimeOffset.Now);
        var labels = snapshot.Nodes.ToDictionary(node => node.Id, node => node.Label, StringComparer.Ordinal);
        var relations = snapshot.Edges
            .Where(edge => labels.ContainsKey(edge.SourceNodeId) && labels.ContainsKey(edge.TargetNodeId))
            .Select(edge => new FlyoutRelationViewModel
            {
                Label = $"{Shorten(labels[edge.SourceNodeId], 20)} \u2192 {Shorten(labels[edge.TargetNodeId], 24)}",
                CountText = edge.Count > 1 ? $"\u00D7{edge.Count}" : "",
                AgeText = FormatAge(edge.UpdatedAt, edge.IsActive),
                TaskSummary = edge.DetailSummaries.Count == 0
                    ? "No task summary"
                    : string.Join(Environment.NewLine, edge.DetailSummaries.Take(4)),
                StatusBrush = new SolidColorBrush(StatusColor(edge.Status)),
            })
            .ToArray();
        RelationList.ItemsSource = relations;
        StatusText.Text = $"{relations.Length} edges - tokens only";
        RefreshedText.Text = $"refreshed {DateTime.Now:HH:mm:ss}";
        RefreshQuotaCards();
    }

    private static string Shorten(string value, int length)
    {
        var clean = new string(value.Where(character => !char.IsControl(character)).ToArray());
        return clean.Length <= length ? clean : clean[..Math.Max(1, length - 1)] + "\u2026";
    }

    private static string FormatAge(DateTimeOffset? updatedAt, bool active)
    {
        if (active || updatedAt is null)
        {
            return "active";
        }
        var age = DateTimeOffset.Now - updatedAt.Value.ToLocalTime();
        if (age < TimeSpan.FromMinutes(1))
        {
            return "<1m ago";
        }
        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes}m ago";
        }
        return $"{(int)age.TotalHours}h ago";
    }

    private static Windows.UI.Color StatusColor(string status) => status.ToLowerInvariant() switch
    {
        "succeeded" or "live" => Windows.UI.Color.FromArgb(230, 74, 196, 120),
        "failed" => Windows.UI.Color.FromArgb(230, 240, 104, 104),
        "running" or "working" => Windows.UI.Color.FromArgb(230, 247, 190, 76),
        "queued" or "starting" => Windows.UI.Color.FromArgb(230, 105, 175, 245),
        _ => Windows.UI.Color.FromArgb(210, 150, 165, 180),
    };

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
                QuotaCardsList.ItemsSource = cards.Count == 0
                    ? new[]
                    {
                        new QuotaCardViewModel
                        {
                            Label = "Provider quota", UsedPercent = 0,
                            ResetText = "Refresh unavailable", StateText = "stale / unavailable",
                        },
                    }
                    : cards;
            });
        });
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

    private sealed class FlyoutRelationViewModel
    {
        public required string Label { get; init; }
        public required string CountText { get; init; }
        public required string AgeText { get; init; }
        public required string TaskSummary { get; init; }
        public required Brush StatusBrush { get; init; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);
}
