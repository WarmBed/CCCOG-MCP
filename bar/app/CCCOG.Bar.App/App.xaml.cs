using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using System.Windows.Input;
using IOPath = System.IO.Path;

namespace CCCOG.Bar.App;

public partial class App : Application
{
    private FlyoutWindow? _flyout;
    private TaskbarIcon? _tray;
    private DispatcherQueue? _uiQueue;
    private bool _quitting;

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
        var trayIconPath = IOPath.Combine(AppContext.BaseDirectory, "Assets", "tray-icon.ico");
        _tray = new TaskbarIcon
        {
            ToolTipText = "CCCOG-Bar - quota and flow",
            // Task 7 (2026-08-17): the tray previously showed WinUI's
            // generic default icon — no icon was ever set. First attempt
            // used TaskbarIcon.IconSource (a WinUI ImageSource/BitmapImage)
            // per the literal request, but BitmapImage's Uri constructor
            // decodes ASYNCHRONOUSLY — H.NotifyIcon's own docs say
            // "IconSource resolves an image source and updates the Icon
            // property accordingly", and that resolution ran before the
            // async decode finished, so the tray showed a blank/loading
            // placeholder glyph instead of the real icon (confirmed live
            // via UI Automation + PrintWindow on the notification overflow
            // popup — this is exactly the class of bug the "verify the tray
            // actually shows the new icon" gate exists to catch). Using
            // System.Drawing.Icon directly instead — synchronous, no async
            // race — is the SAME underlying property IconSource itself
            // ultimately updates, and is the exact pattern TokenBar's own
            // TrayService.cs already uses successfully
            // (`_icon.Icon = System.Drawing.Icon.FromHandle(hicon)`).
            // Requesting the 16x16 frame explicitly: Shell_NotifyIcon wants
            // a small icon, and `new Icon(path)` without a size defaults to
            // SystemInformation.IconSize (historically 32x32), not the
            // tray's own preferred size — this ensures the correctly-sized
            // frame from the multi-resolution .ico is the one used, not
            // whichever default the OS/library would otherwise pick.
            Icon = new System.Drawing.Icon(trayIconPath, 16, 16),
            LeftClickCommand = new DelegateCommand(_ => ToggleFlyout()),
            // NOT SecondWindow: that mode parks a transparent helper window
            // over the desktop, which would swallow hover/wheel input meant
            // for the flyout — TokenBar's TrayService carries the same
            // note (ContextMenuMode.PopupMenu instead).
            ContextMenuMode = ContextMenuMode.PopupMenu,
        };
        QuitApp = Quit;
        RebuildMenu();
        _tray.ForceCreate();
    }

    /// <summary>Ported from TokenBar's TrayService.RebuildMenu: PopupMenu
    /// mode converts this MenuFlyout to a NATIVE Win32 menu, so only
    /// Command fires (Click never does), and only ToggleMenuFlyoutItem's
    /// IsChecked survives the conversion — every actionable item here is
    /// Command-wired accordingly. CCCOG's menu is fixed (Open / Refresh now
    /// / Quit — no quota-source or menu-bar-mode radios, since this shell
    /// has no live tray-icon number and no settings window to route to;
    /// see bar/SYNC.md), so unlike TokenBar's per-tick rebuild this is
    /// built once, right after the icon is created.</summary>
    private void RebuildMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Open CCCOG Bar",
            Command = new DelegateCommand(_ => _flyout?.ShowFlyout()),
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Refresh now",
            Command = new DelegateCommand(_ => _flyout?.RefreshNow()),
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Quit",
            Command = new DelegateCommand(_ => QuitApp?.Invoke()),
        });
        if (_tray is not null)
        {
            _tray.ContextFlyout = menu;
        }
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

    /// <summary>The single shutdown path — the tray menu's Quit and the
    /// flyout footer's Quit button both route here (App.QuitApp), so the
    /// icon and the WH_MOUSE_LL hook are always released before the
    /// process exits, whichever entry point fired. Safe to call twice
    /// (a tray click racing the footer button, say).</summary>
    public static Action? QuitApp { get; private set; }

    private void Quit()
    {
        if (_quitting)
        {
            return; // every Quit entry routes here; make it safe to call twice
        }

        _quitting = true;
        _tray?.Dispose();
        _flyout?.Shutdown();
        Application.Current.Exit();
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
