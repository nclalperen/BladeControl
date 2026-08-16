using BladeControl.Razer;
using BladeControl.Runtime;
using BladeControl.UI.Ipc;
using BladeControl.UI.Services;

namespace BladeControl.UI.ViewModels;

public enum CoolingMode
{
    /// <summary>Cooling handed back to the firmware's own Auto controller.</summary>
    FirmwareAuto,

    /// <summary>A constant commanded fan target held by the firmware.</summary>
    Fixed,

    /// <summary>Runtime Core's closed-loop controller drives the target from telemetry.</summary>
    DynamicCurve
}

public sealed class FansThermalViewModel : PageViewModel
{
    private CoolingMode _mode = CoolingMode.FirmwareAuto;
    private int _fan1Target = 3000;
    private int _fan2Target = 3000;
    private bool _linkFans = true;
    private bool _curveLoaded;

    public FansThermalViewModel(RuntimeConnection connection, CancellationToken lifetime)
        : base(
            connection,
            lifetime,
            "Fans",
            "Fans & Thermal",
            "Cooling ownership, fixed targets and the dynamic curve",
            Icons.Fans)
    {
        CurveEditor = new ThermalCurveEditorViewModel();
        ApplyFirmwareAutoCommand = new AsyncRelayCommand(
            ApplyFirmwareAutoAsync,
            () => Connection.CanApplyStaticProfile);
        ApplyFixedCommand = new AsyncRelayCommand(
            ApplyFixedAsync,
            () => Connection.CanApplyStaticProfile);
        StartDynamicCommand = new AsyncRelayCommand(
            StartDynamicAsync,
            () => Connection.CanStartThermalControl);
        StopDynamicCommand = new AsyncRelayCommand(
            StopDynamicAsync,
            () => Connection.CanStopThermalControl);
        ReloadCurveCommand = new AsyncRelayCommand(
            LoadCurveAsync,
            () => Connection.IsOnline);
    }

    public ThermalCurveEditorViewModel CurveEditor { get; }

    public AsyncRelayCommand ApplyFirmwareAutoCommand { get; }

    public AsyncRelayCommand ApplyFixedCommand { get; }

    public AsyncRelayCommand StartDynamicCommand { get; }

    public AsyncRelayCommand StopDynamicCommand { get; }

    public AsyncRelayCommand ReloadCurveCommand { get; }

    public int MinimumFanRpm => FanRpm.MinimumValue;

    public int MaximumFanRpm => FanRpm.MaximumValue;

    public int FanRpmIncrement => FanRpm.Increment;

    public CoolingMode Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
            {
                RaiseAll(
                    nameof(IsFirmwareAutoSelected),
                    nameof(IsFixedSelected),
                    nameof(IsDynamicSelected));
            }
        }
    }

    public bool IsFirmwareAutoSelected
    {
        get => _mode == CoolingMode.FirmwareAuto;
        set
        {
            if (value)
            {
                Mode = CoolingMode.FirmwareAuto;
            }
        }
    }

    public bool IsFixedSelected
    {
        get => _mode == CoolingMode.Fixed;
        set
        {
            if (value)
            {
                Mode = CoolingMode.Fixed;
            }
        }
    }

    public bool IsDynamicSelected
    {
        get => _mode == CoolingMode.DynamicCurve;
        set
        {
            if (value)
            {
                Mode = CoolingMode.DynamicCurve;
            }
        }
    }

    public int Fan1Target
    {
        get => _fan1Target;
        set
        {
            int snapped = Snap(value);
            if (Set(ref _fan1Target, snapped))
            {
                if (_linkFans && _fan2Target != snapped)
                {
                    _fan2Target = snapped;
                    Raise(nameof(Fan2Target));
                }

                Raise(nameof(FixedSummary));
            }
        }
    }

    public int Fan2Target
    {
        get => _fan2Target;
        set
        {
            int snapped = Snap(value);
            if (Set(ref _fan2Target, snapped))
            {
                if (_linkFans && _fan1Target != snapped)
                {
                    _fan1Target = snapped;
                    Raise(nameof(Fan1Target));
                }

                Raise(nameof(FixedSummary));
            }
        }
    }

    /// <summary>Keeps both fans on one value. Runtime IPC still receives both explicitly.</summary>
    public bool LinkFans
    {
        get => _linkFans;
        set
        {
            if (Set(ref _linkFans, value) && value)
            {
                Fan2Target = _fan1Target;
            }
        }
    }

    public string FixedSummary => $"Fan 1 {_fan1Target:N0} RPM · Fan 2 {_fan2Target:N0} RPM";

    // Live state ------------------------------------------------------------
    public string EffectiveFanTarget => Display.Rpm(Connection.Status?.CurrentEffectiveFanTargetRpm);

    public string ActiveCurve => Display.Text(Connection.Status?.CurrentProfile);

    public string ThermalSession => Display.ThermalSession(Connection.RuntimeStateName);

    public StatusTone ThermalSessionTone => Display.RuntimeStateTone(Connection.RuntimeStateName);

    public string TelemetryHealth => Connection.Status?.TelemetryHealth is { } health
        ? health.IsHealthy ? "Healthy" : health.Kind
        : Display.Unavailable;

    public string? TelemetryHealthDetail => Connection.Status?.TelemetryHealth?.Reason;

    public StatusTone TelemetryHealthTone => Display.HealthTone(Connection.Status?.TelemetryHealth);

    /// <summary>
    /// The firmware's own fan report. The Razer 0x0D81 field has not been proven to be a
    /// physical tachometer, so it is labelled as a firmware-reported value throughout.
    /// </summary>
    public string FirmwareFan1Value => Display.FirmwareFanValue(Connection.Fan?.Fan1Rpm ?? 0);

    public string FirmwareFan2Value => Display.FirmwareFanValue(Connection.Fan?.Fan2Rpm ?? 0);

    public string FirmwareFanMode => Display.Text(Connection.Fan?.Mode.Zone1FanMode);

    public string? ProfileBlockedReason => Connection.StaticProfileBlockedReason;

    public bool HasProfileBlockedReason => !string.IsNullOrEmpty(ProfileBlockedReason);

    public string? StartBlockedReason => Connection.CanStartThermalControl
        ? null
        : Connection.IsThermalOwnershipReady
            ? Connection.StaticProfileBlockedReason ??
                $"Runtime Core is {Display.Text(Connection.RuntimeStateName)}."
            : Connection.ThermalReadinessReason;

    public bool HasStartBlockedReason => !string.IsNullOrEmpty(StartBlockedReason);

    public override void Refresh()
    {
        RaiseAll(
            nameof(EffectiveFanTarget), nameof(ActiveCurve),
            nameof(ThermalSession), nameof(ThermalSessionTone),
            nameof(TelemetryHealth), nameof(TelemetryHealthDetail), nameof(TelemetryHealthTone),
            nameof(FirmwareFan1Value), nameof(FirmwareFan2Value), nameof(FirmwareFanMode),
            nameof(ProfileBlockedReason), nameof(HasProfileBlockedReason),
            nameof(StartBlockedReason), nameof(HasStartBlockedReason));
        ApplyFirmwareAutoCommand.RaiseCanExecuteChanged();
        ApplyFixedCommand.RaiseCanExecuteChanged();
        StartDynamicCommand.RaiseCanExecuteChanged();
        StopDynamicCommand.RaiseCanExecuteChanged();
        ReloadCurveCommand.RaiseCanExecuteChanged();
    }

    public override void Activate()
    {
        Refresh();
        if (!_curveLoaded && Connection.IsOnline)
        {
            _ = LoadCurveAsync();
        }
    }

    /// <summary>Reads the runtime's own curve document so the editor shows what is actually in force.</summary>
    public async Task LoadCurveAsync()
    {
        if (!Connection.IsOnline)
        {
            return;
        }

        try
        {
            StoredThermalCurveDocument document = await Connection.Client
                .GetThermalCurveAsync("default", Lifetime).ConfigureAwait(true);
            CurveEditor.Load(document);
            _curveLoaded = true;
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (RuntimeUiException exception)
        {
            CurveEditor.ReportLoadFailure(
                $"Could not read the runtime curve: {exception.Message}");
        }
    }

    private async Task ApplyFirmwareAutoAsync() =>
        await RunCommandAsync(async (client, token) =>
        {
            RuntimeCommandResultDto result = await client.ApplyFanProfileAsync(
                new ApplyFanProfileRequest("Auto", null, null),
                token).ConfigureAwait(false);
            return Describe(result, "Cooling returned to firmware Auto");
        }).ConfigureAwait(true);

    private async Task ApplyFixedAsync()
    {
        int fan1 = _fan1Target;
        int fan2 = _fan2Target;
        await RunCommandAsync(async (client, token) =>
        {
            RuntimeCommandResultDto result = await client.ApplyFanProfileAsync(
                new ApplyFanProfileRequest("Fixed", fan1, fan2),
                token).ConfigureAwait(false);
            return Describe(result, $"Fixed target applied ({fan1:N0} / {fan2:N0} RPM)");
        }).ConfigureAwait(true);
    }

    private async Task StartDynamicAsync() =>
        await RunCommandAsync(async (client, token) =>
        {
            RuntimeStatusDto status = await client
                .StartThermalControlAsync("default", token).ConfigureAwait(false);
            return string.Equals(status.State, "Running", StringComparison.Ordinal)
                ? RuntimeCommandOutcome.Ok("Dynamic cooling started on the built-in curve.")
                : RuntimeCommandOutcome.Fail(
                    status.LastFailureReason ??
                    $"Runtime Core reported {status.State} instead of Running.");
        }).ConfigureAwait(true);

    private async Task StopDynamicAsync() =>
        await RunCommandAsync(async (client, token) =>
        {
            StopThermalControlResultDto result = await client
                .StopThermalControlAsync(token).ConfigureAwait(false);
            return result.Succeeded
                ? RuntimeCommandOutcome.Ok(result.Message)
                : RuntimeCommandOutcome.Fail(result.Message);
        }).ConfigureAwait(true);

    private static RuntimeCommandOutcome Describe(RuntimeCommandResultDto result, string success)
    {
        string message = string.IsNullOrWhiteSpace(result.Message)
            ? $"{result.Outcome ?? "Applied"}."
            : $"{result.Outcome ?? "Applied"} — {result.Message}";
        return result.Succeeded
            ? RuntimeCommandOutcome.Ok($"{success}. {message}")
            : RuntimeCommandOutcome.Fail(message);
    }

    private int Snap(int value)
    {
        int clamped = Math.Clamp(value, FanRpm.MinimumValue, FanRpm.MaximumValue);
        int rounded = (int)Math.Round(
            clamped / (double)FanRpm.Increment,
            MidpointRounding.AwayFromZero) * FanRpm.Increment;
        return Math.Clamp(rounded, FanRpm.MinimumValue, FanRpm.MaximumValue);
    }
}
