using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace CCCOG.Bar.App;

public partial class App : Application
{
    private FlyoutWindow? _flyout;
    private TaskbarIcon? _tray;
    private DispatcherQueue? _uiQueue;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _uiQueue = DispatcherQueue.GetForCurrentThread();
        _flyout = new FlyoutWindow();
        // Show as early as possible — the performance contract is window
        // visible < 1s cold; tray icon registration happens after.
        _flyout.ShowFlyout();
        _tray = new TaskbarIcon
        {
            ToolTipText = "CCCOG-Bar - quota and flow",
            LeftClickCommand = new DelegateCommand(_ => ToggleFlyout()),
        };
        _tray.ForceCreate();
    }

    private void ToggleFlyout()
    {
        if (_uiQueue is { HasThreadAccess: false })
        {
            _uiQueue.TryEnqueue(ToggleFlyoutCore);
            return;
        }
        ToggleFlyoutCore();
    }

    private void ToggleFlyoutCore()
    {
        _flyout?.ToggleFlyout();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        CrashLog.Append(args.Exception);
        // Keep the tray alive after logging a stowed XAML exception.  The next
        // watcher refresh can rebuild the affected visual tree safely.
        args.Handled = true;
    }

    private sealed class DelegateCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute(parameter);
    }
}

internal static class CrashLog
{
    private static readonly object Gate = new();

    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "bar-crash.log");

    public static void Append(Exception exception)
    {
        try
        {
            var text = $"[{DateTimeOffset.Now:O}] pid={Environment.ProcessId} thread={Environment.CurrentManagedThreadId}\n" +
                       Expand(exception) + Environment.NewLine + Environment.NewLine;
            lock (Gate)
            {
                File.AppendAllText(Path, text);
            }
        }
        catch
        {
            // Logging must not throw from WinUI's exception boundary.
        }
    }

    private static string Expand(Exception exception)
    {
        var lines = new List<string>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        for (var current = exception; current is not null && seen.Add(current); current = current.InnerException)
        {
            lines.Add($"type={current.GetType().FullName}");
            lines.Add($"hresult=0x{unchecked((uint)current.HResult):X8}");
            lines.Add($"message={current.Message}");
            lines.Add($"stack={current.StackTrace}");
            if (current is COMException com)
            {
                lines.Add($"comErrorCode=0x{unchecked((uint)com.ErrorCode):X8}");
            }
            foreach (var item in current.Data.Keys)
            {
                lines.Add($"data[{item}]={current.Data[item]}");
            }
        }
        lines.Add("fullException=" + exception);
        return string.Join(Environment.NewLine, lines);
    }
}
