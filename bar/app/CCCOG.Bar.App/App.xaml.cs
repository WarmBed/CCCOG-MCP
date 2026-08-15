using H.NotifyIcon;
using Microsoft.UI.Xaml;
using System.Windows.Input;

namespace CCCOG.Bar.App;

public partial class App : Application
{
    private MainWindow? _window;
    private TaskbarIcon? _tray;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _tray = new TaskbarIcon
        {
            ToolTipText = "CCCOG-Bar — control graph and quotas",
            LeftClickCommand = new DelegateCommand(_ => ToggleWindow()),
        };
        _tray.ForceCreate();
        _window.Closed += (_, _) => _tray.Dispose();
        _window.ShowDashboard();
    }

    private void ToggleWindow()
    {
        if (_window is null)
        {
            return;
        }
        if (_window.AppWindow.IsVisible)
        {
            _window.AppWindow.Hide();
        }
        else
        {
            _window.ShowDashboard();
        }
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
