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
    private UiLaunchMode _launchMode;
    private CompactCloseBehavior _compactCloseBehavior;
    private bool _startWithWindows;
    private bool _advancedVisible;
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
        _launchMode = settings.LaunchMode;
        _compactCloseBehavior = settings.CompactCloseBehavior;
        _startWithWindows = settings.StartWithWindows;

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

        // The pages that draw a chart take one from Monitoring's history. Each gets the series
        // that page is actually about: temperature on the dashboard, the fan target beside the
        // fan controls, and package power on Performance, which is where a mode or level change
        // shows up as a changed power ceiling rather than as a number that merely moved.
        Dashboard.Chart = Monitoring.Temperatures;
        FansThermal.Chart = Monitoring.FanTarget;
        Performance.Chart = Monitoring.Power;

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

        // The shell can be built after the connection already holds an authoritative
        // snapshot. Seed the pages from it — purely from cached state, no extra IPC — so
        // the first frame matches Runtime Core instead of staying uninitialised (and, for
        // Performance, unappliable) until the next poll tick.
        OnConnectionUpdated();
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
                Monitoring.SetPresentationActive(ChartsAreOnScreen);
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

    public UiLaunchMode LaunchMode
    {
        get => _launchMode;
        set => Set(ref _launchMode, value);
    }

    public CompactCloseBehavior CompactCloseBehavior
    {
        get => _compactCloseBehavior;
        set => Set(ref _compactCloseBehavior, value);
    }

    /// <summary>
    /// User's intent for sign-in launch. Setting it asks <see cref="StartupRegistrar"/> to
    /// write the per-user Run key; if Windows refuses, the property reverts so the checkbox
    /// keeps telling the truth about what will actually happen.
    /// </summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value)
            {
                return;
            }

            if (StartupRegistrar is { } registrar && !registrar.TrySet(value))
            {
                Raise();
                return;
            }

            Set(ref _startWithWindows, value);
        }
    }

    /// <summary>
    /// Injected by the application host. Null in design preview and in tests that do not
    /// exercise startup registration, in which case the preference is in-memory only.
    /// </summary>
    public StartupRegistration? StartupRegistrar { get; init; }

    public string ConnectionLabel => Connection.State == RuntimeConnectionState.Online
        ? "Runtime online"
        : Connection.IsAwaitingRuntimeStartup ? "Runtime starting"
        : Connection.State == RuntimeConnectionState.Connecting ? "Connecting"
        : "Runtime offline";

    public StatusTone ConnectionTone => Connection.State == RuntimeConnectionState.Online
        ? StatusTone.Good
        : Connection.IsAwaitingRuntimeStartup ||
            Connection.State == RuntimeConnectionState.Connecting
            ? StatusTone.Warning
            : StatusTone.Danger;

    public string RuntimeStateLabel => Connection.RuntimeStateName ?? "No state";

    public bool HasConnectionNotice => IsDesignPreview ||
        Connection.State != RuntimeConnectionState.Online ||
        !string.IsNullOrWhiteSpace(Connection.LastReadError);

    public StatusTone ConnectionNoticeTone => IsDesignPreview || Connection.IsAwaitingRuntimeStartup
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

            if (Connection.IsAwaitingRuntimeStartup)
            {
                return "Connecting to BladeControl Runtime…";
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

            if (Connection.IsAwaitingRuntimeStartup)
            {
                return "The BladeControl Runtime service starts a little after sign-in. " +
                    "This panel will connect on its own; no hardware fallback will be used.";
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

    /// <summary>
    /// True when the visible page draws charts, so history keeps redrawing rather than freezing.
    /// </summary>
    /// <remarks>
    /// Monitoring used to be the only page with charts, so its presentation flag doubled as
    /// "is Monitoring selected". Dashboard, Fans &amp; Thermal and Performance draw from the same
    /// history now, and gating on Monitoring alone would leave their charts static — collecting
    /// samples but never repainting.
    /// </remarks>
    private bool ChartsAreOnScreen =>
        _advancedVisible &&
        (ReferenceEquals(SelectedPage, Monitoring) ||
         ReferenceEquals(SelectedPage, Dashboard) ||
         ReferenceEquals(SelectedPage, FansThermal) ||
         ReferenceEquals(SelectedPage, Performance));

    public void SetAdvancedVisibility(bool visible)
    {
        _advancedVisible = visible;
        Monitoring.SetPresentationActive(ChartsAreOnScreen);
        if (visible && ReferenceEquals(SelectedPage, Diagnostics))
        {
            Diagnostics.Refresh();
        }
    }

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
            GraphWindowSeconds = Monitoring.WindowSeconds,
            LaunchMode = LaunchMode,
            StartWithWindows = StartWithWindows,
            CompactCloseBehavior = CompactCloseBehavior
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
        if (ChartsAreOnScreen)
        {
            Monitoring.Refresh();
        }
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
