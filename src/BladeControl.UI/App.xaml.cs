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
    private CompactControlViewModel? _compactViewModel;
    private CompactControlWindow? _compactWindow;
    private MainWindow? _fullWindow;
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
        _compactViewModel = new CompactControlViewModel(_shell);
        _compactWindow = new CompactControlWindow(_compactViewModel);
        _compactWindow.FullAppRequested += ShowFullWindow;
        _compactWindow.DiagnosticsRequested += ShowDiagnostics;
        _compactWindow.CloseRequested += CloseCompactWindow;

        _notificationArea = new NotificationAreaService(connection);
        _notificationArea.OpenRequested += ShowCompactWindow;
        _notificationArea.StartCoolingRequested += () =>
            _shell.FansThermal.StartDynamicCommand.Execute(null);
        _notificationArea.StopCoolingRequested += () =>
            _shell.FansThermal.StopDynamicCommand.Execute(null);
        _notificationArea.FirmwareAutoRequested += () =>
            _shell.FansThermal.ApplyFirmwareAutoCommand.Execute(null);
        _notificationArea.ExitRequested += RequestExit;

        if (UiStartupPolicy.SelectInitialSurface(_loadedSettings) == InitialUiSurface.Full)
        {
            ShowFullWindow();
        }
        else
        {
            MainWindow = _compactWindow;
            _compactWindow.ShowAndActivate();
        }

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
        _compactViewModel?.Dispose();
        _shell?.Dispose();
        base.OnExit(e);
    }

    private void ShowCompactWindow()
    {
        if (_compactWindow is null)
        {
            return;
        }

        _fullWindow?.Hide();
        _shell?.SetAdvancedVisibility(false);
        MainWindow = _compactWindow;
        _compactWindow.ShowAndActivate();
    }

    private void ShowFullWindow()
    {
        if (_shell is null || _loadedSettings is null)
        {
            return;
        }

        if (_fullWindow is null)
        {
            _fullWindow = new MainWindow(_shell, _loadedSettings);
            _fullWindow.ExitRequested += RequestExit;
            _fullWindow.CompactRequested += ShowCompactWindow;
        }

        _compactWindow?.Hide();
        _shell.SetAdvancedVisibility(true);
        MainWindow = _fullWindow;
        _fullWindow.ShowAndActivate();
    }

    private void ShowDiagnostics()
    {
        if (_shell is not null)
        {
            _shell.SelectedPage = _shell.Diagnostics;
        }

        ShowFullWindow();
    }

    private void CloseCompactWindow()
    {
        if (_shell?.CompactCloseBehavior == CompactCloseBehavior.Exit)
        {
            RequestExit();
        }
        else
        {
            _compactWindow?.Hide();
        }
    }

    private async void RequestExit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        if (_settingsStore is not null && _shell is not null)
        {
            UiSettings settings = _fullWindow?.CaptureSettings() ?? _shell.CaptureSettings(
                _loadedSettings?.WindowWidth ?? 1100,
                _loadedSettings?.WindowHeight ?? 720,
                _loadedSettings?.WindowMaximized ?? false);
            _settingsStore.Save(settings);
        }

        _notificationArea?.Dispose();
        _notificationArea = null;
        if (_shell is not null)
        {
            await _shell.DisposeAsync();
        }

        _compactViewModel?.Dispose();
        _compactWindow?.CloseExplicitly();
        _fullWindow?.CloseExplicitly();
        Shutdown();
    }
}
