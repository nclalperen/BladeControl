using System.ComponentModel;
using BladeControl.UI.Services;

namespace BladeControl.UI.ViewModels;

/// <summary>
/// Application shell and composition root for the five pages. Hardware access is not
/// represented here: every page shares the one <see cref="RuntimeConnection"/> IPC client.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private PageViewModel _selectedPage;
    private bool _minimizeToTray;
    private bool _disposed;

    public ShellViewModel(
        RuntimeConnection connection,
        UiSettings settings,
        bool isDesignPreview,
        Action<string>? copyToClipboard = null)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        ArgumentNullException.ThrowIfNull(settings);
        IsDesignPreview = isDesignPreview;
        _minimizeToTray = settings.MinimizeToTray;

        Performance = new PerformanceViewModel(connection, _lifetime.Token);
        Dashboard = new DashboardViewModel(connection, Performance, _lifetime.Token);
        FansThermal = new FansThermalViewModel(
            connection,
            _lifetime.Token,
            copyToClipboard);
        Monitoring = new MonitoringViewModel(
            connection,
            _lifetime.Token,
            settings.GraphWindowSeconds);
        Diagnostics = new DiagnosticsViewModel(
            connection,
            _lifetime.Token,
            copyToClipboard);

        Pages = [Dashboard, Performance, FansThermal, Monitoring, Diagnostics];
        _selectedPage = Pages.FirstOrDefault(page =>
                string.Equals(page.Key, settings.SelectedPage, StringComparison.Ordinal)) ??
            Dashboard;
        _selectedPage.IsSelected = true;

        ReconnectCommand = new AsyncRelayCommand(
            ReconnectAsync,
            () => Connection.State == RuntimeConnectionState.Offline);
        Connection.Updated += OnConnectionUpdated;
        Connection.PropertyChanged += OnConnectionPropertyChanged;
    }

    public RuntimeConnection Connection { get; }

    public DashboardViewModel Dashboard { get; }

    public PerformanceViewModel Performance { get; }

    public FansThermalViewModel FansThermal { get; }

    public MonitoringViewModel Monitoring { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public IReadOnlyList<PageViewModel> Pages { get; }

    public AsyncRelayCommand ReconnectCommand { get; }

    public bool IsDesignPreview { get; }

    public PageViewModel SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedPage))
            {
                return;
            }

            _selectedPage.IsSelected = false;
            if (Set(ref _selectedPage, value))
            {
                _selectedPage.IsSelected = true;
                RaiseAll(nameof(CurrentTitle), nameof(CurrentSubtitle));
            }
        }
    }

    public string CurrentTitle => _selectedPage.Title;

    public string CurrentSubtitle => _selectedPage.Subtitle;

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => Set(ref _minimizeToTray, value);
    }

    public string ConnectionLabel => Connection.State switch
    {
        RuntimeConnectionState.Online => "Runtime online",
        RuntimeConnectionState.Connecting => "Connecting",
        _ => "Runtime offline"
    };

    public StatusTone ConnectionTone => Connection.State switch
    {
        RuntimeConnectionState.Online => StatusTone.Good,
        RuntimeConnectionState.Connecting => StatusTone.Warning,
        _ => StatusTone.Danger
    };

    public string RuntimeStateLabel => Connection.RuntimeStateName ?? "No state";

    public bool HasConnectionNotice => IsDesignPreview ||
        Connection.State != RuntimeConnectionState.Online ||
        !string.IsNullOrWhiteSpace(Connection.LastReadError);

    public StatusTone ConnectionNoticeTone => IsDesignPreview
        ? StatusTone.Warning
        : Connection.State == RuntimeConnectionState.Offline
            ? StatusTone.Danger
            : StatusTone.Warning;

    public string ConnectionNoticeTitle
    {
        get
        {
            if (IsDesignPreview)
            {
                return "Design preview · synthetic data";
            }

            if (Connection.State == RuntimeConnectionState.Connecting)
            {
                return "Connecting to Runtime Core";
            }

            if (Connection.State == RuntimeConnectionState.Offline)
            {
                return "Runtime Core offline";
            }

            return "Runtime read failed";
        }
    }

    public string ConnectionNoticeDetail
    {
        get
        {
            if (IsDesignPreview)
            {
                return "No hardware is connected. State changes affect the in-memory preview only.";
            }

            if (Connection.State == RuntimeConnectionState.Connecting)
            {
                return "Waiting for the local named-pipe endpoint. No hardware fallback will be used.";
            }

            return Connection.TransportError ?? Connection.LastReadError ??
                "The most recent Runtime Core read did not complete.";
        }
    }

    public bool CanReconnect => Connection.State == RuntimeConnectionState.Offline;

    public void Start() => Connection.Start();

    public UiSettings CaptureSettings(
        double windowWidth,
        double windowHeight,
        bool windowMaximized) =>
        new()
        {
            WindowWidth = windowWidth,
            WindowHeight = windowHeight,
            WindowMaximized = windowMaximized,
            SelectedPage = SelectedPage.Key,
            MinimizeToTray = MinimizeToTray,
            GraphWindowSeconds = Monitoring.WindowSeconds
        };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Connection.Updated -= OnConnectionUpdated;
        Connection.PropertyChanged -= OnConnectionPropertyChanged;
        _lifetime.Cancel();
        Connection.Dispose();
        _lifetime.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Connection.Updated -= OnConnectionUpdated;
        Connection.PropertyChanged -= OnConnectionPropertyChanged;
        _lifetime.Cancel();
        await Connection.StopAsync().ConfigureAwait(false);
        Connection.Dispose();
        _lifetime.Dispose();
    }

    private async Task ReconnectAsync()
    {
        try
        {
            await Connection.ReconnectAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Application shutdown.
        }
    }

    private void OnConnectionUpdated()
    {
        Dashboard.Refresh();
        Performance.Refresh();
        FansThermal.Refresh();
        Monitoring.Refresh();
        if (ReferenceEquals(SelectedPage, Diagnostics))
        {
            Diagnostics.Refresh();
        }

        RaiseConnectionProperties();
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(RuntimeConnection.IsCommandInFlight) or
            nameof(RuntimeConnection.State) or
            nameof(RuntimeConnection.Status) or
            nameof(RuntimeConnection.Doctor))
        {
            Dashboard.Refresh();
            Performance.Refresh();
            FansThermal.Refresh();
        }

        RaiseConnectionProperties();
    }

    private void RaiseConnectionProperties()
    {
        RaiseAll(
            nameof(ConnectionLabel),
            nameof(ConnectionTone),
            nameof(RuntimeStateLabel),
            nameof(HasConnectionNotice),
            nameof(ConnectionNoticeTone),
            nameof(ConnectionNoticeTitle),
            nameof(ConnectionNoticeDetail),
            nameof(CanReconnect));
        ReconnectCommand.RaiseCanExecuteChanged();
    }
}
