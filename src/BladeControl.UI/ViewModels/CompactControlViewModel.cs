using System.ComponentModel;
using System.Globalization;
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
        ApplyCustomCommand = Performance.ApplyCommand;

        // Cooling is about who drives the fans, and nothing else. Putting the firmware profile
        // names in this row conflated two independent things: performance mode is a power
        // ceiling, cooling is fan ownership, and every combination of the two is valid on this
        // hardware. Which firmware curve is running is shown as context beside the control
        // rather than duplicated as a second copy of the performance selector.
        SelectFirmwareCommand = new RelayCommand(
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

    public AsyncRelayCommand ApplyCustomCommand { get; }

    public RelayCommand SelectFirmwareCommand { get; }

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

    /// <summary>
    /// Sign-in launch preference, surfaced in the compact Settings expander because that is
    /// where a daily-use utility's own behaviour belongs. Delegates to the shell so the
    /// registry write and the persisted setting stay in step.
    /// </summary>
    public bool StartWithWindows
    {
        get => _shell.StartWithWindows;
        set
        {
            if (_shell.StartWithWindows != value)
            {
                _shell.StartWithWindows = value;
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

    public string ConnectionText => Connection.State == RuntimeConnectionState.Online
        ? "Online"
        : Connection.IsAwaitingRuntimeStartup ? "Starting…"
        : Connection.State == RuntimeConnectionState.Connecting ? "Connecting"
        : "Offline";

    public StatusTone ConnectionTone => Connection.State == RuntimeConnectionState.Online
        ? StatusTone.Good
        : Connection.IsAwaitingRuntimeStartup ||
            Connection.State == RuntimeConnectionState.Connecting
            ? StatusTone.Warning
            : StatusTone.Danger;

    public string CpuTemperature => _shell.Dashboard.CpuTemperature;

    public string GpuTemperature => _shell.Dashboard.GpuTemperature;

    public string TelemetryCaption => TelemetryPresentation.Text;

    public StatusTone TelemetryTone => TelemetryPresentation.Tone;

    // --- The FAN tile -------------------------------------------------------------------
    //
    // The compact window shows CPU, FAN and GPU side by side, and the fan figure is the one
    // that has to be handled carefully. No physical tachometer signal has been found on this
    // machine: 0x0D81 returns the firmware's commanded target echoed back, LibreHardwareMonitor
    // reports no fan sensors, and NVML reports fan speed as unavailable. So there is no measured
    // RPM to show, and presenting a commanded value as though it were one would be a claim the
    // evidence does not support.
    //
    // What is shown instead is what BladeControl actually knows: the target it asked for, said
    // plainly to be a target. Under firmware Auto there is no BladeControl target at all, and
    // the last one is meaningless, so the tile shows nothing rather than a stale number.

    /// <summary>The number under FAN, or an em dash when there is nothing truthful to show.</summary>
    public string FanValue => FanPresentation.Value;

    /// <summary>What the number above it is, in the user's words rather than the protocol's.</summary>
    public string FanCaption => FanPresentation.Caption;

    /// <summary>Reads "Firmware Auto" where a measured speed would otherwise sit.</summary>
    public string FanHeading => FanPresentation.Heading;

    public StatusTone FanTone => FanPresentation.Tone;

    /// <summary>
    /// One fan control for the user; both zones still travel over IPC explicitly.
    /// </summary>
    /// <remarks>
    /// This machine has two fans and the protocol addresses them separately, which the runtime
    /// keeps doing. Exposing that as two sliders asked the user to make a decision they have no
    /// basis for, and invited them to desynchronise the zones for no benefit. The compact window
    /// offers one number and keeps the zones together.
    /// </remarks>
    public int FanTarget
    {
        get => Fans.Fan1Target;
        set
        {
            Fans.LinkFans = true;
            Fans.Fan1Target = value;
            Raise(nameof(FanTarget));
            Raise(nameof(FanValue));
        }
    }

    public int MinimumFanRpm => Fans.MinimumFanRpm;

    public int MaximumFanRpm => Fans.MaximumFanRpm;

    public int FanRpmIncrement => Fans.FanRpmIncrement;

    // --- Emergency handoff --------------------------------------------------------------
    //
    // A latched terminal state for the session, not a transient one. The interface used to
    // describe it as in progress, which reads as "wait and it will resolve"; nothing resolves,
    // because resuming automatically after a thermal emergency is how a loop starts.

    public bool IsEmergencyHandoff => Connection.IsOnline &&
        string.Equals(Connection.RuntimeStateName, "EmergencyHandoff", StringComparison.Ordinal);

    public string EmergencyTitle => "Firmware Auto owns cooling";

    public string EmergencyDetail =>
        Connection.Status?.EmergencyStatus is { Length: > 0 } status
            ? status
            : "A thermal emergency ended the session. BladeControl no longer controls the fans.";

    /// <summary>Says what the person has to do, because nothing will happen on its own.</summary>
    public string EmergencyAction =>
        "The service is still running. Dynamic will not resume by itself — start it again " +
        "deliberately once the machine has cooled.";

    // --- Performance levels -------------------------------------------------------------

    /// <summary>
    /// The modelled CPU levels, including the ones this build will not send.
    /// </summary>
    /// <remarks>
    /// Shown disabled rather than omitted. A level that is simply absent looks like the hardware
    /// does not have it; a level that is present and greyed out says the protocol models it and
    /// this build has not validated it, which is what is actually true.
    /// </remarks>
    public IReadOnlyList<PolicyOptionViewModel> CpuLevels => Performance.CpuLevels;

    public IReadOnlyList<PolicyOptionViewModel> GpuLevels => Performance.GpuLevels;

    public bool IsBalancedSelected => Performance.SelectedMode == "Balanced";

    public bool IsSilentSelected => Performance.SelectedMode == "Silent";

    public bool IsCustomSelected => Performance.IsCustomSelected;

    /// <summary>Firmware owns the fans, whichever profile it is running.</summary>
    public bool IsFirmwareSelected => Fans.Mode == CoolingMode.FirmwareAuto;

    /// <summary>Which firmware curve is running, named rather than duplicated as a control.</summary>
    public string FirmwareCurveLabel => Performance.SelectedMode is "Silent" or "Balanced"
        ? $"{Performance.SelectedMode} curve"
        : "firmware curve";

    public bool IsFixedSelected => Fans.Mode == CoolingMode.Fixed;

    public bool IsDynamicSelected => Fans.Mode == CoolingMode.DynamicCurve;

    public bool IsDynamicRunning => Display.IsDynamicSessionEngaged(
        Connection.IsOnline,
        Connection.RuntimeStateName);

    public bool IsDynamicStopped => !IsDynamicRunning;

    public string DynamicState => !Connection.IsOnline &&
        !string.IsNullOrWhiteSpace(Connection.RuntimeStateName)
        ? $"Last reported · {Display.ThermalSession(Connection.RuntimeStateName)}"
        : Display.ThermalSession(Connection.RuntimeStateName);

    public string DynamicTarget => Fans.EffectiveFanTarget;

    public string? DynamicBlockedReason => Fans.StartBlockedReason;

    public bool HasDynamicBlockedReason => !string.IsNullOrWhiteSpace(DynamicBlockedReason);

    public string? OperationMessage => OperationPresentation.Message;

    public bool OperationIsError => OperationPresentation.IsError;

    public StatusTone OperationTone => OperationIsError ? StatusTone.Danger : StatusTone.Good;

    public bool HasOperationMessage => OperationPresentation.Message is not null;

    public string FooterText => FooterPresentation.Text;

    public StatusTone FooterTone => FooterPresentation.Tone;

    private (string Text, StatusTone Tone) TelemetryPresentation
    {
        get
        {
            if (Connection.Telemetry is null)
            {
                return ("No telemetry yet", StatusTone.Muted);
            }

            if (!Connection.IsOnline)
            {
                return ("Last known telemetry · Runtime Core offline", StatusTone.Muted);
            }

            // Only Running owns cooling. A fresh provider sample in any other state is a
            // monitoring snapshot; a stale one is the last observation, never live session
            // telemetry. Text and tone come from this one classification so they cannot drift.
            SessionObservationScope observation = Display.SessionObservation(
                Connection.IsOnline,
                Connection.RuntimeStateName);
            if (observation != SessionObservationScope.Current)
            {
                string text = observation switch
                {
                    SessionObservationScope.Starting =>
                        "Retained telemetry · session starting",
                    SessionObservationScope.Stopping =>
                        "Retained telemetry · session stopping",
                    SessionObservationScope.LastSession when Connection.IsTelemetryStale =>
                        $"Last known telemetry · " +
                            $"{Display.ThermalSession(Connection.RuntimeStateName)}",
                    _ => "Monitoring snapshot"
                };
                return (text, StatusTone.Muted);
            }

            return Connection.IsTelemetryStale
                ? ("Telemetry stale", StatusTone.Warning)
                : ("Live telemetry", StatusTone.Good);
        }
    }

    private (string? Message, bool IsError) OperationPresentation
    {
        get
        {
            PageViewModel latest = Performance.StatusMessageRevision >=
                Fans.StatusMessageRevision
                ? Performance
                : Fans;
            return string.IsNullOrWhiteSpace(latest.StatusMessage)
                ? (null, false)
                : (latest.StatusMessage, latest.StatusIsError);
        }
    }

    private (string Heading, string Value, string Caption, StatusTone Tone) FanPresentation
    {
        get
        {
            if (!Connection.IsOnline)
            {
                return ("FAN", Display.Unavailable, "runtime offline", StatusTone.Danger);
            }

            if (IsEmergencyHandoff)
            {
                return ("FIRMWARE", Display.Unavailable, "firmware owns cooling",
                    StatusTone.Warning);
            }

            bool authoritativeFixed = string.Equals(
                Connection.Status?.CurrentProfile,
                "Fan/Fixed",
                StringComparison.Ordinal);
            if (!authoritativeFixed &&
                Display.HasCurrentCoolingSnapshot(
                    Connection.IsOnline,
                    Connection.RuntimeStateName) &&
                IsFirmwareSelected)
            {
                // The direct fan profile has no timestamp. Stable Stopped state lets us show
                // the snapshot as context, but not promote it to a current ownership claim.
                return ("FIRMWARE", Display.Unavailable,
                    $"profile snapshot · {FirmwareCurveLabel}", StatusTone.Muted);
            }

            if (!authoritativeFixed &&
                Connection.RuntimeStateName is "Stopped" &&
                Fans.Mode == CoolingMode.Fixed)
            {
                // This is the editor value, not a command known to be in force. Keep the useful
                // preview but name it as unapplied so it cannot impersonate the current target.
                return ("FAN", Fans.Fan1Target.ToString("N0", CultureInfo.CurrentCulture),
                    "selected target · not applied", StatusTone.Neutral);
            }

            if (!Display.HasCurrentFanTarget(
                    Connection.IsOnline,
                    Connection.RuntimeStateName,
                    Connection.Status?.CurrentProfile))
            {
                string caption = Connection.RuntimeStateName switch
                {
                    "Faulted" => "runtime fault · open Diagnostics",
                    "Starting" => "session starting",
                    "Stopping" => "session stopping",
                    _ => "target unavailable"
                };
                return ("FAN", Display.Unavailable, caption,
                    Display.RuntimeStateTone(Connection.RuntimeStateName));
            }

            if (authoritativeFixed)
            {
                return Connection.Status?.CurrentEffectiveFanTargetRpm is { } target
                    ? ("FAN", target.ToString("N0", CultureInfo.CurrentCulture),
                        "target", StatusTone.Good)
                    : ("FAN", Display.Unavailable,
                        "fixed target unavailable", StatusTone.Warning);
            }

            return Connection.Status?.CurrentEffectiveFanTargetRpm is { } dynamicTarget
                ? ("FAN", Display.Rpm(dynamicTarget), "dynamic target", StatusTone.Good)
                : ("FAN", Display.Unavailable,
                    "dynamic target unavailable", StatusTone.Warning);
        }
    }

    private (string Text, StatusTone Tone) FooterPresentation
    {
        get
        {
            if (Connection.IsAwaitingRuntimeStartup)
            {
                return ("Connecting to BladeControl Runtime…", StatusTone.Muted);
            }

            // A retained status snapshot must never colour a transport failure. Previously a
            // disconnect after Running produced the literal text "Runtime offline" in green.
            if (!Connection.IsOnline)
            {
                return ("Runtime offline", StatusTone.Danger);
            }

            string? state = Connection.RuntimeStateName;
            if (state is "Running" && Fans.EffectiveFanTarget == Display.Unavailable)
            {
                return ("Dynamic · target unavailable", StatusTone.Warning);
            }

            string text = state switch
            {
                "EmergencyHandoff" => "Emergency handoff · firmware Auto owns cooling",
                "Faulted" =>
                    $"Runtime fault · {Connection.Status?.LastFailureReason ?? "open Diagnostics"}",
                "Running" => $"Dynamic · {Fans.EffectiveFanTarget}",
                "Starting" => "Dynamic · Starting",
                "Stopping" => "Dynamic · Stopping",
                "Stopped" when
                    string.Equals(
                        Connection.Status?.CurrentProfile,
                        "Fan/Fixed",
                        StringComparison.Ordinal) =>
                    Fans.EffectiveFanTarget == Display.Unavailable
                        ? "Fixed fan target"
                        : $"Fixed · {Fans.EffectiveFanTarget}",
                "Stopped" => "Stopped · no Dynamic session",
                _ => $"Runtime {Display.Text(state)}"
            };
            return (text, Display.RuntimeStateTone(state));
        }
    }

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
            nameof(IsFirmwareSelected), nameof(FirmwareCurveLabel),
            nameof(IsFixedSelected), nameof(IsDynamicSelected),
            nameof(IsDynamicRunning), nameof(IsDynamicStopped),
            nameof(DynamicState), nameof(DynamicTarget),
            nameof(DynamicBlockedReason), nameof(HasDynamicBlockedReason),
            nameof(OperationMessage), nameof(OperationIsError), nameof(OperationTone),
            nameof(HasOperationMessage),
            nameof(FooterText), nameof(FooterTone),
            nameof(StartWithWindows),
            nameof(FanValue), nameof(FanCaption), nameof(FanHeading), nameof(FanTone),
            nameof(FanTarget),
            nameof(IsEmergencyHandoff), nameof(EmergencyDetail));
        SelectBalancedCommand.RaiseCanExecuteChanged();
        SelectSilentCommand.RaiseCanExecuteChanged();
        SelectCustomCommand.RaiseCanExecuteChanged();
        SelectFirmwareCommand.RaiseCanExecuteChanged();
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
