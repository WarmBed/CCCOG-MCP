using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices;
using System.Text.Json;
using IOPath = System.IO.Path;
using Windows.Graphics;

namespace CCCOG.Bar.App;

public sealed partial class MainWindow : Window
{
    private readonly LocalSnapshotReader _reader = new();
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer _quotaTimer;
    private readonly List<Storyboard> _runningAnimations = [];
    private FileSystemWatcher? _watcher;
    private int _quotaPollInFlight;
    private bool _closing;
    private bool _viewReady;
    private string _provider = "all";
    private string _timeFilter = "2h";

    public MainWindow()
    {
        InitializeComponent();
        Title = "CCCOG-Bar";
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = true;
        presenter.SetBorderAndTitleBar(true, true);
        AppWindow.Resize(new SizeInt32(1800, 820));
        AppWindow.IsShownInSwitchers = true;
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
            StopAnimations();
            AppWindow.Hide();
        };
        _viewReady = true;
        StartFileWatcher();
        RefreshView();
        _quotaTimer.Start();
    }

    public void ShowDashboard()
    {
        AppWindow.Show();
        AppWindow.MoveInZOrderAtTop();
        RefreshView();
    }

    private void ProviderFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderFilter.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _provider = tag;
            RefreshView();
        }
    }

    private void TimeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimeFilter.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _timeFilter = tag;
            RefreshView();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshView();

    private void RefreshView()
    {
        if (_closing || !_viewReady)
        {
            return;
        }
        var snapshot = _reader.Read(_provider, _timeFilter, DateTimeOffset.Now);
        StopAnimations();
        GraphCanvas.Children.Clear();
        var positions = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var leftNodes = snapshot.Nodes.Where(node => node.Provider is "caller" or "owner").ToArray();
        var rightNodes = snapshot.Nodes.Where(node => node.Provider is not ("caller" or "owner")).ToArray();
        var leftY = 20d;
        foreach (var node in leftNodes)
        {
            positions[node.Id] = (10, leftY);
            leftY += NodeHeight(node) + 12;
        }
        var rightY = 20d;
        foreach (var node in rightNodes)
        {
            positions[node.Id] = (210, rightY);
            rightY += NodeHeight(node) + 12;
        }
        GraphCanvas.Width = 400;
        GraphCanvas.Height = Math.Max(430, Math.Max(leftY, rightY) + 30);
        var edgeSlot = 0;
        foreach (var edge in snapshot.Edges)
        {
            if (!positions.TryGetValue(edge.SourceNodeId, out var source) ||
                !positions.TryGetValue(edge.TargetNodeId, out var target))
            {
                continue;
            }
            var path = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = Curve(source, target, edgeSlot++),
                Stroke = new SolidColorBrush(StatusColor(edge.Status)),
                StrokeThickness = edge.Count > 1 ? 4 : 2,
                IsHitTestVisible = true,
            };
            var task = edge.DetailSummaries.Count == 0
                ? "No task summary"
                : string.Join(Environment.NewLine, edge.DetailSummaries.Take(4));
            ToolTipService.SetToolTip(path, task);
            path.Tapped += (_, _) =>
            {
                DetailsText.Text = $"{edge.Kind} - {edge.Status} - {edge.Count} job(s): {task}";
            };
            GraphCanvas.Children.Add(path);
            if (!string.IsNullOrEmpty(edge.BadgeText))
            {
                var badge = new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 20, 27, 36)),
                    Padding = new Thickness(3, 1, 3, 1),
                    Child = new TextBlock
                    {
                        Text = edge.BadgeText,
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(StatusColor(edge.Status)),
                    },
                };
                Canvas.SetLeft(badge, 185);
                Canvas.SetTop(badge, Math.Min(source.Y, target.Y) + 4);
                GraphCanvas.Children.Add(badge);
            }
        }
        foreach (var node in snapshot.Nodes)
        {
            if (!positions.TryGetValue(node.Id, out var position))
            {
                continue;
            }
            var card = new Border
            {
                Width = 175,
                Height = NodeHeight(node),
                Padding = new Thickness(8, 4, 8, 4),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(node.Provider == "caller"
                    ? Windows.UI.Color.FromArgb(220, 45, 67, 86)
                    : Windows.UI.Color.FromArgb(220, 41, 82, 63)),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"{node.Provider} - {node.Label}", FontSize = 12 },
                        new TextBlock { Text = node.State, FontSize = 10, Opacity = 0.75 },
                    },
                },
            };
            Canvas.SetLeft(card, position.X);
            Canvas.SetTop(card, position.Y);
            GraphCanvas.Children.Add(card);
            if (node.State is "running" or "active")
            {
                var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 0.58, To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(900)),
                    AutoReverse = true, RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever,
                };
                var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                storyboard.Children.Add(animation);
                Storyboard.SetTarget(animation, card);
                Storyboard.SetTargetProperty(animation, "Opacity");
                storyboard.Begin();
                _runningAnimations.Add(storyboard);
            }
        }

        RefreshQuotaCards();
        StatusText.Text = $"{snapshot.Jobs} visible edges - {_timeFilter} - refreshed {DateTime.Now:HH:mm:ss}";
    }

    private static double NodeHeight(GraphNodeViewModel node) => node.Label.Length > 22 ? 64 : 48;

    private static Microsoft.UI.Xaml.Media.Geometry Curve(
        (double X, double Y) source,
        (double X, double Y) target,
        int slot)
    {
        var start = new Windows.Foundation.Point(source.X + 175, source.Y + 24);
        var end = new Windows.Foundation.Point(target.X, target.Y + 24);
        var bend = ((slot % 5) - 2) * 22;
        var control = new Windows.Foundation.Point(
            (start.X + end.X) / 2,
            (start.Y + end.Y) / 2 + bend);
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new QuadraticBezierSegment { Point1 = control, Point2 = end });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Windows.UI.Color StatusColor(string status) => status.ToLowerInvariant() switch
    {
        "succeeded" or "live" => Windows.UI.Color.FromArgb(230, 74, 196, 120),
        "failed" => Windows.UI.Color.FromArgb(230, 240, 104, 104),
        "running" or "working" => Windows.UI.Color.FromArgb(230, 247, 190, 76),
        "queued" or "starting" => Windows.UI.Color.FromArgb(230, 105, 175, 245),
        _ => Windows.UI.Color.FromArgb(210, 150, 165, 180),
    };

    private void StopAnimations()
    {
        foreach (var storyboard in _runningAnimations)
        {
            storyboard.Stop();
        }
        _runningAnimations.Clear();
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
                var clientId = card.TryGetProperty("clientId", out var clientValue)
                    ? clientValue.GetString() ?? "provider"
                    : "provider";
                var state = card.TryGetProperty("state", out var stateValue)
                    ? stateValue.GetString() ?? "stale"
                    : "stale";
                if (!card.TryGetProperty("windows", out var windows))
                {
                    continue;
                }
                if (windows.GetArrayLength() == 0)
                {
                    var diagnostic = card.TryGetProperty("diagnostic", out var diagnosticValue)
                        ? diagnosticValue.GetString() ?? "no quota data"
                        : "no quota data";
                    result.Add(new QuotaCardViewModel
                    {
                        Label = $"{clientId} quota",
                        UsedPercent = 0,
                        ResetText = "Reset time unavailable",
                        StateText = $"{state.ToLowerInvariant()}: {diagnostic}",
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
                        Label = $"{clientId} - {label}",
                        UsedPercent = Math.Clamp(used, 0, 100),
                        ResetText = FormatReset(reset),
                        StateText = state.Equals("fresh", StringComparison.OrdinalIgnoreCase)
                            ? "fresh"
                            : "stale",
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
