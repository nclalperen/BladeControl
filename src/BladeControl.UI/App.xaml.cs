using System.Windows;
using BladeControl.UI.Ipc;
using BladeControl.UI.Services;
using BladeControl.UI.ViewModels;

namespace BladeControl.UI;

public partial class App : Application
{
    private UiSettingsStore? _settingsStore;
    private UiSettings? _loadedSettings;
    private ShellViewModel? _shell;
    private MainWindow? _window;
    private NotificationAreaService? _notificationArea;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settingsStore = new UiSettingsStore();
        _loadedSettings = _settingsStore.Load();
        RuntimeClientSelection selection = RuntimeClientFactory.Create(e.Args);
        var connection = new RuntimeConnection(
            selection.Client,
            new WpfUiDispatcher(Dispatcher));
        _shell = new ShellViewModel(
            connection,
            _loadedSettings,
            selection.IsDesignPreview,
            text => Clipboard.SetText(text));
        _window = new MainWindow(_shell, _loadedSettings);
        _window.ExitRequested += RequestExit;

        _notificationArea = new NotificationAreaService(connection);
        _notificationArea.OpenRequested += ShowWindow;
        _notificationArea.StartCoolingRequested += () =>
            _shell.FansThermal.StartDynamicCommand.Execute(null);
        _notificationArea.StopCoolingRequested += () =>
            _shell.FansThermal.StopDynamicCommand.Execute(null);
        _notificationArea.FirmwareAutoRequested += () =>
            _shell.FansThermal.ApplyFirmwareAutoCommand.Execute(null);
        _notificationArea.ExitRequested += RequestExit;

        MainWindow = _window;
        _window.Show();
        _shell.Start();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        RequestExit();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notificationArea?.Dispose();
        _shell?.Dispose();
        base.OnExit(e);
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.ShowAndActivate();
    }

    private async void RequestExit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        if (_settingsStore is not null && _shell is not null && _window is not null)
        {
            _settingsStore.Save(_window.CaptureSettings());
        }

        _notificationArea?.Dispose();
        _notificationArea = null;
        if (_shell is not null)
        {
            await _shell.DisposeAsync();
        }

        _window?.CloseExplicitly();
        Shutdown();
    }
}
