using System.ComponentModel;
using BladeControl.UI.Services;

namespace BladeControl.UI.ViewModels;

/// <summary>
/// Compact projection of the application-level session. It owns no IPC client, poll loop,
/// hardware object or timer; commands delegate to the same validated view models as Full App.
/// </summary>
public sealed class CompactControlViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;
    private bool _disposed;

    public CompactControlViewModel(ShellViewModel shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        SelectBalancedCommand = new RelayCommand(
            () => ApplyPerformance("Balanced"),
            () => Connection.CanApplyStaticProfile);
        SelectSilentCommand = new RelayCommand(
            () => ApplyPerformance("Silent"),
            () => Connection.CanApplyStaticProfile);
        SelectCustomCommand = new RelayCommand(
            () => Performance.TrySelectMode("Custom"),
            () => Connection.CanApplyStaticProfile);
        SelectCpuLowCommand = new RelayCommand(
            () => Performance.TrySelectCpuLevel("Low"),
            () => Connection.CanApplyStaticProfile);
        SelectCpuMediumCommand = new RelayCommand(
            () => Performance.TrySelectCpuLevel("Medium"),
            () => Connection.CanApplyStaticProfile);
        ApplyCustomCommand = Performance.ApplyCommand;

        SelectAutoCommand = new RelayCommand(
            ApplyFirmwareAuto,
            () => Connection.CanApplyStaticProfile);
        SelectFixedCommand = new RelayCommand(
            () => Fans.Mode = CoolingMode.Fixed,
            () => Connection.CanIssueCommand);
        SelectDynamicCommand = new RelayCommand(
            () => Fans.Mode = CoolingMode.DynamicCurve,
            () => Connection.CanIssueCommand);
        ApplyFixedCommand = Fans.ApplyFixedCommand;
        StartDynamicCommand = Fans.StartDynamicCommand;
        StopDynamicCommand = Fans.StopDynamicCommand;

        Connection.Updated += Refresh;
        Connection.PropertyChanged += OnConnectionPropertyChanged;
        Performance.PropertyChanged += OnChildPropertyChanged;
        Fans.PropertyChanged += OnChildPropertyChanged;
        Refresh();
    }

    public RuntimeConnection Connection => _shell.Connection;

    public PerformanceViewModel Performance => _shell.Performance;

    public FansThermalViewModel Fans => _shell.FansThermal;

    public RelayCommand SelectBalancedCommand { get; }

    public RelayCommand SelectSilentCommand { get; }

    public RelayCommand SelectCustomCommand { get; }

    public RelayCommand SelectCpuLowCommand { get; }

    public RelayCommand SelectCpuMediumCommand { get; }

    public AsyncRelayCommand ApplyCustomCommand { get; }

    public RelayCommand SelectAutoCommand { get; }

    public RelayCommand SelectFixedCommand { get; }

    public RelayCommand SelectDynamicCommand { get; }

    public AsyncRelayCommand ApplyFixedCommand { get; }

    public AsyncRelayCommand StartDynamicCommand { get; }

    public AsyncRelayCommand StopDynamicCommand { get; }

    public IReadOnlyList<UiLaunchMode> LaunchModes { get; } = Enum.GetValues<UiLaunchMode>();

    public IReadOnlyList<CompactCloseBehavior> CloseBehaviors { get; } =
        Enum.GetValues<CompactCloseBehavior>();

    public UiLaunchMode LaunchMode
    {
        get => _shell.LaunchMode;
        set
        {
            if (_shell.LaunchMode != value)
            {
                _shell.LaunchMode = value;
                Raise();
            }
        }
    }

    public CompactCloseBehavior CompactCloseBehavior
    {
        get => _shell.CompactCloseBehavior;
        set
        {
            if (_shell.CompactCloseBehavior != value)
            {
                _shell.CompactCloseBehavior = value;
                Raise();
            }
        }
    }

    public string ConnectionText => Connection.State switch
    {
        RuntimeConnectionState.Online => "Online",
        RuntimeConnectionState.Connecting => "Connecting",
        _ => "Offline"
    };

    public StatusTone ConnectionTone => Connection.State switch
    {
        RuntimeConnectionState.Online => StatusTone.Good,
        RuntimeConnectionState.Connecting => StatusTone.Warning,
        _ => StatusTone.Danger
    };

    public string CpuTemperature => _shell.Dashboard.CpuTemperature;

    public string GpuTemperature => _shell.Dashboard.GpuTemperature;

    public string TelemetryCaption
    {
        get
        {
            if (Connection.Telemetry is null)
            {
                return "No telemetry yet";
            }

            if (string.Equals(Connection.RuntimeStateName, "Stopped", StringComparison.Ordinal))
            {
                return Connection.IsTelemetryStale
                    ? "Last session telemetry · Stopped"
                    : "Monitoring snapshot";
            }

            return Connection.IsTelemetryStale ? "Telemetry stale" : "Live telemetry";
        }
    }

    public StatusTone TelemetryTone =>
        string.Equals(Connection.RuntimeStateName, "Stopped", StringComparison.Ordinal)
            ? StatusTone.Muted
            : Connection.IsTelemetryStale ? StatusTone.Warning : StatusTone.Muted;

    public bool IsBalancedSelected => Performance.SelectedMode == "Balanced";

    public bool IsSilentSelected => Performance.SelectedMode == "Silent";

    public bool IsCustomSelected => Performance.IsCustomSelected;

    public bool IsCpuLowSelected => Performance.SelectedCpuLevel == "Low";

    public bool IsCpuMediumSelected => Performance.SelectedCpuLevel == "Medium";

    public bool IsAutoSelected => Fans.Mode == CoolingMode.FirmwareAuto;

    public bool IsFixedSelected => Fans.Mode == CoolingMode.Fixed;

    public bool IsDynamicSelected => Fans.Mode == CoolingMode.DynamicCurve;

    public bool IsDynamicRunning => Connection.RuntimeStateName is "Running" or "Starting";

    public bool IsDynamicStopped => !IsDynamicRunning;

    public string DynamicState => Display.ThermalSession(Connection.RuntimeStateName);

    public string DynamicTarget => Fans.EffectiveFanTarget;

    public string? DynamicBlockedReason => Fans.StartBlockedReason;

    public bool HasDynamicBlockedReason => !string.IsNullOrWhiteSpace(DynamicBlockedReason);

    public string? OperationMessage => Performance.StatusMessage ?? Fans.StatusMessage;

    public bool OperationIsError =>
        Performance.HasStatusMessage ? Performance.StatusIsError : Fans.StatusIsError;

    public StatusTone OperationTone => OperationIsError ? StatusTone.Danger : StatusTone.Good;

    public bool HasOperationMessage => !string.IsNullOrWhiteSpace(OperationMessage);

    public string FooterText
    {
        get
        {
            if (!Connection.IsOnline)
            {
                return "Runtime offline";
            }

            string? state = Connection.RuntimeStateName;
            if (state == "EmergencyHandoff")
            {
                return "Emergency handoff · firmware Auto requested";
            }

            if (state == "Faulted")
            {
                return $"Runtime fault · {Connection.Status?.LastFailureReason ?? "open Diagnostics"}";
            }

            if (state == "Running")
            {
                return $"Dynamic · {Fans.EffectiveFanTarget}";
            }

            return Connection.Fan?.Mode.Zone1FanMode is { } fanMode
                ? $"Firmware {fanMode}"
                : $"Runtime {Display.Text(state)}";
        }
    }

    public StatusTone FooterTone => Connection.RuntimeStateName switch
    {
        "Faulted" or "EmergencyHandoff" => StatusTone.Danger,
        "Running" => StatusTone.Good,
        _ when !Connection.IsOnline => StatusTone.Danger,
        _ => StatusTone.Neutral
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Connection.Updated -= Refresh;
        Connection.PropertyChanged -= OnConnectionPropertyChanged;
        Performance.PropertyChanged -= OnChildPropertyChanged;
        Fans.PropertyChanged -= OnChildPropertyChanged;
    }

    public void Refresh()
    {
        RaiseAll(
            nameof(ConnectionText), nameof(ConnectionTone),
            nameof(CpuTemperature), nameof(GpuTemperature),
            nameof(TelemetryCaption), nameof(TelemetryTone),
            nameof(IsBalancedSelected), nameof(IsSilentSelected), nameof(IsCustomSelected),
            nameof(IsCpuLowSelected), nameof(IsCpuMediumSelected),
            nameof(IsAutoSelected), nameof(IsFixedSelected), nameof(IsDynamicSelected),
            nameof(IsDynamicRunning), nameof(IsDynamicStopped),
            nameof(DynamicState), nameof(DynamicTarget),
            nameof(DynamicBlockedReason), nameof(HasDynamicBlockedReason),
            nameof(OperationMessage), nameof(OperationIsError), nameof(OperationTone),
            nameof(HasOperationMessage),
            nameof(FooterText), nameof(FooterTone));
        SelectBalancedCommand.RaiseCanExecuteChanged();
        SelectSilentCommand.RaiseCanExecuteChanged();
        SelectCustomCommand.RaiseCanExecuteChanged();
        SelectCpuLowCommand.RaiseCanExecuteChanged();
        SelectCpuMediumCommand.RaiseCanExecuteChanged();
        SelectAutoCommand.RaiseCanExecuteChanged();
        SelectFixedCommand.RaiseCanExecuteChanged();
        SelectDynamicCommand.RaiseCanExecuteChanged();
    }

    private void ApplyPerformance(string mode)
    {
        if (Performance.TrySelectMode(mode))
        {
            Performance.ApplyCommand.Execute(null);
        }
    }

    private void ApplyFirmwareAuto()
    {
        Fans.Mode = CoolingMode.FirmwareAuto;
        Fans.ApplyFirmwareAutoCommand.Execute(null);
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs args) => Refresh();

    private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs args) => Refresh();
}
