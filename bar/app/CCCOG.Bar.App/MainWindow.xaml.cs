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
    private FileSystemWatcher? _watcher;
    private string _provider = "all";

    public MainWindow()
    {
        InitializeComponent();
        Title = "CCCOG-Bar";
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = true;
        presenter.SetBorderAndTitleBar(true, true);
        AppWindow.Resize(new SizeInt32(900, 600));
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
            _watcher?.Dispose();
            _refreshTimer.Stop();
            _quotaTimer.Stop();
            AppWindow.Hide();
        };
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

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshView();

    private void RefreshView()
    {
        var snapshot = _reader.Read(_provider);
        GraphCanvas.Children.Clear();
        var positions = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var index = 0;
        foreach (var node in snapshot.Nodes)
        {
            var x = node.Provider == "caller" ? 25 : 320;
            var y = 20 + (index++ % 9) * 44;
            positions[node.Id] = (x, y);
        }
        foreach (var edge in snapshot.Edges)
        {
            if (!positions.TryGetValue(edge.SourceNodeId, out var source) ||
                !positions.TryGetValue(edge.TargetNodeId, out var target))
            {
                continue;
            }
            var line = new Line
            {
                X1 = source.X + 185, Y1 = source.Y + 20, X2 = target.X, Y2 = target.Y + 20,
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 92, 190, 255)),
                StrokeThickness = 2,
            };
            GraphCanvas.Children.Add(line);
            var task = new TextBlock
            {
                Text = edge.TaskSummary,
                FontSize = 10,
                MaxWidth = 260,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 210, 220, 230)),
            };
            Canvas.SetLeft(task, 205);
            Canvas.SetTop(task, Math.Min(source.Y, target.Y) + 1);
            GraphCanvas.Children.Add(task);
        }
        foreach (var node in snapshot.Nodes)
        {
            if (!positions.TryGetValue(node.Id, out var position))
            {
                continue;
            }
            var card = new Border
            {
                Width = 185,
                Height = 40,
                Padding = new Thickness(8, 4, 8, 4),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(node.Provider == "caller"
                    ? Windows.UI.Color.FromArgb(220, 45, 67, 86)
                    : Windows.UI.Color.FromArgb(220, 41, 82, 63)),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"{node.Provider} · {node.Label}", FontSize = 12 },
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
            }
        }

        RefreshQuotaCards();
        StatusText.Text = $"{snapshot.Jobs} dispatch records · refreshed {DateTime.Now:HH:mm:ss}";
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
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
        RenamedEventHandler renamed = (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
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
        var cards = NativeQuotaClient.TryPoll();
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
                var state = card.TryGetProperty("state", out var stateValue)
                    ? stateValue.GetString() ?? "stale"
                    : "stale";
                if (!card.TryGetProperty("windows", out var windows))
                {
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
                        Label = label,
                        UsedPercent = Math.Clamp(used, 0, 100),
                        ResetText = reset,
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
}
