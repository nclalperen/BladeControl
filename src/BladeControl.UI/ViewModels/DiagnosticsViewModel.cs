using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using BladeControl.Runtime;
using BladeControl.UI.Ipc;
using BladeControl.UI.Services;

namespace BladeControl.UI.ViewModels;

public sealed record DiagnosticItem(
    string Label,
    string Value,
    string? Detail = null,
    StatusTone Tone = StatusTone.Neutral)
{
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}

public sealed class DiagnosticGroup : ObservableObject
{
    public DiagnosticGroup(string title)
    {
        _title = title;
        Items = [];
    }

    private string _title;

    public string Title
    {
        get => _title;
        internal set => Set(ref _title, value);
    }

    public ObservableCollection<DiagnosticItem> Items { get; }

    internal void Replace(IEnumerable<DiagnosticItem> items)
    {
        Items.Clear();
        foreach (DiagnosticItem item in items)
        {
            Items.Add(item);
        }
    }
}

public sealed class RuntimeEventViewModel : ObservableObject
{
    private bool _isExpanded;

    public RuntimeEventViewModel(RuntimeEventDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Kind = item.Kind;
        Sequence = item.Sequence;
        Timestamp = Display.EventTimestamp(item.Timestamp);
        Message = item.Message;
        Detail = BuildDetail(item);
    }

    public string Kind { get; }

    public long Sequence { get; }

    public string Timestamp { get; }

    public string Message { get; }

    public string? Detail { get; }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public StatusTone Tone => Kind switch
    {
        "EmergencyHandoff" or "OwnershipLost" => StatusTone.Danger,
        "SchedulerOverrun" or "RecoveryAttempt" => StatusTone.Warning,
        "SessionStarted" or "RecoveryResult" => StatusTone.Good,
        "SessionStopped" => StatusTone.Neutral,
        _ => StatusTone.Muted
    };

    private static string? BuildDetail(RuntimeEventDto item)
    {
        var builder = new StringBuilder();
        if (item.ThermalDecision is { } decision)
        {
            builder.AppendLine(CultureInfo.CurrentCulture,
                $"Effective target: {decision.EffectiveTargetRpm} RPM");
            builder.AppendLine(CultureInfo.CurrentCulture,
                $"CPU curve: {Nullable(decision.CpuCurveTargetRpm)} · " +
                $"GPU curve: {Nullable(decision.GpuCurveTargetRpm)} · " +
                $"Requested: {Nullable(decision.RequestedTargetRpm)}");
            builder.AppendLine(CultureInfo.CurrentCulture,
                $"Demand source: {Display.Text(decision.DemandSource)} · " +
                $"Write: {Display.Boolean(decision.ShouldWrite)} · " +
                $"Emergency: {Display.Boolean(decision.EmergencyAuto)}");
            builder.AppendLine(CultureInfo.CurrentCulture,
                $"Health: {decision.Health.Kind} — {decision.Health.Reason}");
            builder.Append(CultureInfo.CurrentCulture, $"Reason: {decision.Reason}");
        }

        if (item.WatchdogState is { } watchdog)
        {
            Separate(builder);
            builder.AppendLine(CultureInfo.CurrentCulture,
                $"Zone 1: {watchdog.Zone1PerformanceMode}/{watchdog.Zone1FanMode} · " +
                $"Zone 2: {watchdog.Zone2PerformanceMode}/{watchdog.Zone2FanMode}");
            builder.Append(CultureInfo.CurrentCulture,
                $"Zones agree: {Display.Boolean(watchdog.ZonesAgree)} · " +
                $"Known Auto: {Display.Boolean(watchdog.IsKnownAuto)}");
        }

        if (item.TargetRpm is { } target)
        {
            Separate(builder);
            builder.Append(CultureInfo.CurrentCulture, $"Fan target: {target} RPM");
        }

        if (item.OverrunMilliseconds is { } overrun)
        {
            Separate(builder);
            builder.Append(CultureInfo.CurrentCulture,
                $"Overrun: {overrun.ToString("0.0", CultureInfo.CurrentCulture)} ms");
        }

        if (item.AcquisitionDurationMilliseconds is { } acquisition)
        {
            Separate(builder);
            builder.Append(CultureInfo.CurrentCulture,
                $"Acquisition: {acquisition.ToString("0.0", CultureInfo.CurrentCulture)} ms");
        }

        if (item.Succeeded is { } succeeded)
        {
            Separate(builder);
            builder.Append(CultureInfo.CurrentCulture,
                $"Succeeded: {Display.Boolean(succeeded)}");
        }

        if (item.SessionId is { } sessionId)
        {
            Separate(builder);
            builder.Append(CultureInfo.CurrentCulture, $"Session: {sessionId:D}");
        }

        if (item.Exchange is { } exchange)
        {
            Separate(builder);
            builder.Append(CultureInfo.CurrentCulture,
                $"Exchange {exchange.Command} (transaction 0x{exchange.TransactionId:X2}), " +
                $"response: {Display.Boolean(exchange.HasResponse)}");
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static void Separate(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.AppendLine();
        }
    }

    private static string Nullable(int? value) =>
        value is { } number
            ? number.ToString(CultureInfo.CurrentCulture)
            : Display.Unavailable;
}

/// <summary>
/// Read-only provenance and health view. It deliberately exposes no hardware write control:
/// everything here is a report obtained over IPC.
/// </summary>
public sealed class DiagnosticsViewModel : PageViewModel
{
    public const int MaximumRetainedEvents = 500;

    private readonly Action<string>? _copyToClipboard;
    private readonly ObservableCollection<RuntimeEventViewModel> _events = [];
    private readonly ObservableCollection<string> _kinds = ["All"];
    private string _selectedKind = "All";
    private bool _gapDetected;

    public DiagnosticsViewModel(
        RuntimeConnection connection,
        CancellationToken lifetime,
        Action<string>? copyToClipboard = null)
        : base(
            connection,
            lifetime,
            "Diagnostics",
            "Diagnostics",
            "Provenance, health and the runtime event stream",
            Icons.Diagnostics)
    {
        _copyToClipboard = copyToClipboard;
        Runtime = new DiagnosticGroup("Runtime");
        Razer = new DiagnosticGroup("Razer");
        Qualification = new DiagnosticGroup("Thermal ownership qualification");
        Telemetry = new DiagnosticGroup("Telemetry");
        PawnIo = new DiagnosticGroup("PawnIO");
        Scheduler = new DiagnosticGroup("Scheduler");
        Groups = [Runtime, Razer,
            Qualification, Telemetry, PawnIo, Scheduler];
        Events = new ReadOnlyObservableCollection<RuntimeEventViewModel>(_events);
        FilteredEvents = [];
        EventKinds = new ReadOnlyObservableCollection<string>(_kinds);
        RefreshDiagnosticsCommand = new AsyncRelayCommand(
            RefreshDiagnosticsAsync,
            () => Connection.IsOnline);
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics, () => _copyToClipboard is not null);
        ClearEventsCommand = new RelayCommand(ClearEvents);
        Connection.EventsReceived += OnEventsReceived;
        Connection.EventStreamReset += OnEventStreamReset;
    }

    public DiagnosticGroup Runtime { get; }

    public DiagnosticGroup Razer { get; }

    /// <summary>
    /// The authoritative qualification result, current whatever the runtime state is.
    /// </summary>
    /// <remarks>
    /// Its own group because it is not session data. It previously sat inside Telemetry, whose
    /// heading becomes "Last session telemetry" while stopped — so a live "may this machine
    /// take thermal ownership" answer was presented as a record of a session that, after a
    /// service restart, had never run.
    /// </remarks>
    public DiagnosticGroup Qualification { get; }

    public DiagnosticGroup Telemetry { get; }

    public DiagnosticGroup PawnIo { get; }

    public DiagnosticGroup Scheduler { get; }

    public IReadOnlyList<DiagnosticGroup> Groups { get; }

    public ReadOnlyObservableCollection<RuntimeEventViewModel> Events { get; }

    public ObservableCollection<RuntimeEventViewModel> FilteredEvents { get; }

    public ReadOnlyObservableCollection<string> EventKinds { get; }

    public RelayCommand CopyDiagnosticsCommand { get; }

    public AsyncRelayCommand RefreshDiagnosticsCommand { get; }

    public RelayCommand ClearEventsCommand { get; }

    public bool CanCopyDiagnostics => _copyToClipboard is not null;

    public string SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (Set(ref _selectedKind, string.IsNullOrEmpty(value) ? "All" : value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>True when the runtime's bounded log dropped events before we read them.</summary>
    public bool GapDetected
    {
        get => _gapDetected;
        private set => Set(ref _gapDetected, value);
    }

    public string EventSummary =>
        $"{_events.Count} of {MaximumRetainedEvents} retained · cursor {Connection.EventCursor}";

    public override void Refresh()
    {
        RebuildGroups();
        Raise(nameof(EventSummary));
        RefreshDiagnosticsCommand.RaiseCanExecuteChanged();
    }

    public override void Activate() => Refresh();

    public void ClearEvents()
    {
        _events.Clear();
        GapDetected = false;
        ApplyFilter();
        Raise(nameof(EventSummary));
    }

    /// <summary>Appends runtime events, newest first, bounded to <see cref="MaximumRetainedEvents"/>.</summary>
    public void AppendEvents(IReadOnlyList<RuntimeEventDto> items, bool gapDetected)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (gapDetected)
        {
            GapDetected = true;
        }

        foreach (RuntimeEventDto item in items)
        {
            var viewModel = new RuntimeEventViewModel(item);
            _events.Insert(0, viewModel);
            if (!_kinds.Contains(item.Kind, StringComparer.Ordinal))
            {
                _kinds.Add(item.Kind);
            }
        }

        while (_events.Count > MaximumRetainedEvents)
        {
            _events.RemoveAt(_events.Count - 1);
        }

        ApplyFilter();
        Raise(nameof(EventSummary));
    }

    public string BuildDiagnosticsText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("BladeControl UI diagnostics");
        builder.AppendLine(CultureInfo.CurrentCulture,
            $"Captured: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine(CultureInfo.CurrentCulture,
            $"Connection: {Connection.State}");
        if (Connection.TransportError is { } transport)
        {
            builder.AppendLine(CultureInfo.CurrentCulture, $"Transport error: {transport}");
        }

        foreach (DiagnosticGroup group in Groups)
        {
            builder.AppendLine();
            builder.AppendLine(CultureInfo.CurrentCulture, $"[{group.Title}]");
            foreach (DiagnosticItem item in group.Items)
            {
                builder.AppendLine(CultureInfo.CurrentCulture,
                    $"  {item.Label}: {item.Value}");
                if (item.HasDetail)
                {
                    builder.AppendLine(CultureInfo.CurrentCulture, $"    {item.Detail}");
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine(CultureInfo.CurrentCulture,
            $"[Runtime events] newest first, {_events.Count} retained");
        foreach (RuntimeEventViewModel item in _events)
        {
            builder.AppendLine(CultureInfo.CurrentCulture,
                $"  {item.Timestamp} #{item.Sequence} {item.Kind}: {item.Message}");
        }

        return builder.ToString();
    }

    private void CopyDiagnostics()
    {
        if (_copyToClipboard is null)
        {
            return;
        }

        try
        {
            _copyToClipboard(BuildDiagnosticsText());
            StatusMessage = "Diagnostics copied to the clipboard.";
            StatusIsError = false;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or
            System.Runtime.InteropServices.ExternalException)
        {
            StatusMessage = $"Clipboard unavailable: {exception.Message}";
            StatusIsError = true;
        }
    }

    private async Task RefreshDiagnosticsAsync()
    {
        ClearStatus();
        bool succeeded = await Connection.RefreshDiagnosticsNowAsync(Lifetime)
            .ConfigureAwait(true);
        StatusMessage = succeeded
            ? "Diagnostics refreshed from Runtime Core."
            : Connection.LastReadError ?? Connection.TransportError ??
                "Runtime Core diagnostics could not be refreshed.";
        StatusIsError = !succeeded;
        Refresh();
    }

    private void OnEventsReceived(IReadOnlyList<RuntimeEventDto> items, bool gap) =>
        AppendEvents(items, gap);

    private void OnEventStreamReset()
    {
        _events.Clear();
        _kinds.Clear();
        _kinds.Add("All");
        SelectedKind = "All";
        GapDetected = true;
        ApplyFilter();
        Raise(nameof(EventSummary));
    }

    private void ApplyFilter()
    {
        FilteredEvents.Clear();
        foreach (RuntimeEventViewModel item in _events)
        {
            if (_selectedKind == "All" ||
                string.Equals(item.Kind, _selectedKind, StringComparison.Ordinal))
            {
                FilteredEvents.Add(item);
            }
        }
    }

    private void RebuildGroups()
    {
        RuntimeStatusDto? status = Connection.Status;
        // Only a Running session produces current readings. Stopped, Faulted and
        // EmergencyHandoff are all showing the last thing observed, and an emergency handoff
        // is precisely when a stale "Balanced + Manual" would misrepresent who owns the fans.
        bool stopped = !string.Equals(status?.State, "Running", StringComparison.Ordinal);
        Razer.Title = stopped ? "Razer · Last watchdog observation" : "Razer";
        Telemetry.Title = stopped ? "Telemetry · last session values" : "Telemetry";
        Scheduler.Title = stopped ? "Last session scheduler" : "Scheduler";
        RuntimeDoctorReportDto? doctor = Connection.Doctor;
        RuntimeTelemetryCapabilitiesDto? capabilities = doctor?.Capabilities;
        RuntimePawnIoProvenanceDto? pawnIo = doctor?.PawnIoProvenance;

        Runtime.Replace(
        [
            new DiagnosticItem(
                "Connection",
                Connection.State.ToString(),
                Connection.TransportError,
                Connection.IsOnline ? StatusTone.Good : StatusTone.Danger),
            new DiagnosticItem(
                "Pipe",
                NamedPipeRuntimeUiClient.PipeName,
                Connection.Client.IsLiveRuntimeChannel
                    ? "Local named pipe, current user only."
                    : "Development preview client — not connected to hardware.",
                Connection.Client.IsLiveRuntimeChannel ? StatusTone.Neutral : StatusTone.Warning),
            new DiagnosticItem(
                "Runtime state",
                Display.Text(status?.State),
                Display.RuntimeStateDescription(status?.State),
                Display.RuntimeStateTone(status?.State)),
            new DiagnosticItem(
                "Session ID",
                status?.SessionId is { } id ? id.ToString("D") : Display.Unavailable,
                status?.StartTimestamp is { } start
                    ? $"Started {start.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                    : null),
            new DiagnosticItem(
                "Active profile",
                Display.Text(status?.CurrentProfile)),
            new DiagnosticItem(
                "Runtime version",
                Display.Unavailable,
                "Runtime Core V1 does not expose a version over IPC. " +
                "See docs/gui-backend-needs.md (item 4).",
                StatusTone.Muted),
            new DiagnosticItem(
                "Protocol version",
                RuntimeIpcDispatcher.ProtocolVersion.ToString(CultureInfo.CurrentCulture)),
            new DiagnosticItem(
                "Total events",
                status?.TotalEventCount.ToString("N0", CultureInfo.CurrentCulture) ??
                    Display.Unavailable),
            new DiagnosticItem(
                "Last failure",
                Display.Text(status?.LastFailureReason),
                null,
                string.IsNullOrWhiteSpace(status?.LastFailureReason)
                    ? StatusTone.Neutral
                    : StatusTone.Danger),
            new DiagnosticItem(
                "Emergency status",
                Display.Text(status?.EmergencyStatus),
                null,
                string.IsNullOrWhiteSpace(status?.EmergencyStatus)
                    ? StatusTone.Neutral
                    : StatusTone.Danger)
        ]);

        RuntimeRazerModeStateDto? watchdog = status?.LastRazerWatchdogState;
        Razer.Replace(
        [
            new DiagnosticItem(
                "HID available",
                doctor is null ? Display.Unavailable : Display.Boolean(doctor.RazerHidAvailable),
                "Microsoft HID; no proprietary Razer driver is required.",
                doctor is null ? StatusTone.Muted : Display.BooleanTone(doctor.RazerHidAvailable)),
            new DiagnosticItem(
                stopped ? "Last watchdog zone 1" : "Watchdog zone 1",
                watchdog is null
                    ? Display.Unavailable
                    : $"{watchdog.Zone1PerformanceMode} / {watchdog.Zone1FanMode}"),
            new DiagnosticItem(
                stopped ? "Last watchdog zone 2" : "Watchdog zone 2",
                watchdog is null
                    ? Display.Unavailable
                    : $"{watchdog.Zone2PerformanceMode} / {watchdog.Zone2FanMode}"),
            new DiagnosticItem(
                "Zones agree",
                watchdog is null ? Display.Unavailable : Display.Boolean(watchdog.ZonesAgree),
                null,
                watchdog is null || stopped
                    ? StatusTone.Muted
                    : Display.BooleanTone(watchdog.ZonesAgree)),
            new DiagnosticItem(
                "Known Auto",
                watchdog is null ? Display.Unavailable : Display.Boolean(watchdog.IsKnownAuto),
                "Auto confirmed by a firmware read rather than inferred.",
                watchdog is null || stopped
                    ? StatusTone.Muted
                    : Display.BooleanTone(watchdog.IsKnownAuto)),
            new DiagnosticItem(
                "Balanced manual",
                watchdog is null ? Display.Unavailable : Display.Boolean(watchdog.IsBalancedManual)),
            new DiagnosticItem(
                stopped
                    ? "Firmware-reported fan state · last observation"
                    : "Firmware-reported fan state",
                Connection.Fan is { } fan
                    ? $"Fan 1 {Display.FirmwareFanValue(fan.Fan1Rpm)} · " +
                        $"Fan 2 {Display.FirmwareFanValue(fan.Fan2Rpm)}"
                    : Display.Unavailable,
                // Two separate cautions, and both matter. The value is a firmware echo of the
                // last commanded target rather than a tachometer, and while stopped it is
                // whatever was last observed — which can be many minutes old and is not what
                // the fans are doing now.
                stopped
                    ? "Historical: the value last observed by the watchdog, not a current " +
                        "reading. Firmware-reported (Razer 0x0D81) and not proven to be a " +
                        "physical tachometer reading."
                    : "Firmware-reported value (Razer 0x0D81). Not proven to be a physical " +
                        "tachometer reading.",
                stopped ? StatusTone.Muted : StatusTone.Neutral)
        ]);

        Telemetry.Replace(
        [
            new DiagnosticItem(
                "CPU package sensor",
                doctor is null
                    ? Display.Unavailable
                    : Display.Boolean(doctor.CpuPackageTemperatureHealthy),
                capabilities is null
                    ? null
                    : $"Available: {Display.Boolean(capabilities.CpuPackageTemperatureAvailable)} · " +
                        $"Power: {Display.Boolean(capabilities.CpuPackagePowerAvailable)}",
                doctor is null
                    ? StatusTone.Muted
                    : Display.BooleanTone(doctor.CpuPackageTemperatureHealthy)),
            new DiagnosticItem(
                "CPU provider provenance",
                doctor is null
                    ? Display.Unavailable
                    : Display.Boolean(doctor.CpuProviderProvenanceSafe),
                "Whether the CPU sensor path is trusted for thermal ownership.",
                doctor is null
                    ? StatusTone.Muted
                    : Display.BooleanTone(doctor.CpuProviderProvenanceSafe)),
            new DiagnosticItem(
                "GPU sensor",
                doctor is null ? Display.Unavailable : Display.Boolean(doctor.GpuTemperatureHealthy),
                capabilities is null
                    ? null
                    : $"NVML: {Display.Boolean(capabilities.NvmlAvailable)} · " +
                        $"Temperature: {Display.Boolean(capabilities.GpuTemperatureSupported)} · " +
                        $"Power: {Display.Boolean(capabilities.GpuPowerSupported)}",
                doctor is null
                    ? StatusTone.Muted
                    : Display.BooleanTone(doctor.GpuTemperatureHealthy)),
            new DiagnosticItem(
                "GPU selection deterministic",
                doctor is null
                    ? Display.Unavailable
                    : Display.Boolean(doctor.GpuSelectionDeterministic),
                capabilities?.GpuSelectionAmbiguous == true
                    ? "More than one candidate GPU was enumerated."
                    : null,
                doctor is null
                    ? StatusTone.Muted
                    : Display.BooleanTone(doctor.GpuSelectionDeterministic)),
            new DiagnosticItem(
                "Selected GPU",
                Display.Text(capabilities?.SelectedGpu?.Name)),
            new DiagnosticItem(
                "GPU PCI ID",
                Display.Text(capabilities?.SelectedGpu?.PciBusId)),
            new DiagnosticItem(
                "LibreHardwareMonitor",
                Display.Text(capabilities?.LibreHardwareMonitorVersion)),
            new DiagnosticItem(
                "ACPI thermal zones",
                capabilities is null
                    ? Display.Unavailable
                    : Display.Boolean(capabilities.AcpiZonesAvailable)),
        ]);

        Qualification.Replace(
        [
            new DiagnosticItem(
                "Thermal ownership ready",
                doctor is null ? Display.Unavailable : Display.Boolean(doctor.ThermalOwnershipReady),
                Connection.ThermalReadinessReason,
                doctor is null
                    ? StatusTone.Muted
                    : Display.BooleanTone(doctor.ThermalOwnershipReady)),
            new DiagnosticItem(
                "GPU thermal limits",
                doctor is null ? Display.Unavailable : Display.Boolean(doctor.GpuThermalLimitsKnown),
                doctor?.GpuThermalLimitDiagnostic,
                doctor is null
                    ? StatusTone.Muted
                    : Display.BooleanTone(doctor.GpuThermalLimitsKnown)),
            new DiagnosticItem(
                "CPU provider trust",
                doctor is null
                    ? Display.Unavailable
                    : Display.Boolean(doctor.CpuProviderProvenanceSafe),
                null,
                doctor is null
                    ? StatusTone.Muted
                    : Display.BooleanTone(doctor.CpuProviderProvenanceSafe)),
            new DiagnosticItem(
                "CPU temperature",
                doctor is null
                    ? Display.Unavailable
                    : Display.Boolean(doctor.CpuPackageTemperatureHealthy),
                null,
                doctor is null
                    ? StatusTone.Muted
                    : Display.BooleanTone(doctor.CpuPackageTemperatureHealthy)),
            new DiagnosticItem(
                "GPU temperature",
                doctor is null
                    ? Display.Unavailable
                    : Display.Boolean(doctor.GpuTemperatureHealthy),
                null,
                doctor is null
                    ? StatusTone.Muted
                    : Display.BooleanTone(doctor.GpuTemperatureHealthy)),
            new DiagnosticItem(
                "GPU selection",
                doctor is null
                    ? Display.Unavailable
                    : Display.Boolean(doctor.GpuSelectionDeterministic),
                null,
                doctor is null
                    ? StatusTone.Muted
                    : Display.BooleanTone(doctor.GpuSelectionDeterministic)),
            new DiagnosticItem(
                "Razer HID",
                doctor is null ? Display.Unavailable : Display.Boolean(doctor.RazerHidAvailable),
                null,
                doctor is null
                    ? StatusTone.Muted
                    : Display.BooleanTone(doctor.RazerHidAvailable)),
            new DiagnosticItem(
                "Evaluated",
                doctor?.QualificationTimestamp is { } evaluated
                    ? evaluated.ToLocalTime().ToString("HH:mm:ss")
                    : Display.Unavailable,
                "Current qualification, not a record of the last session.")
        ]);

        PawnIo.Replace(
        [
            new DiagnosticItem(
                "Installed",
                pawnIo is null ? Display.Unavailable : Display.Boolean(pawnIo.Installed),
                null,
                pawnIo is null ? StatusTone.Muted : Display.BooleanTone(pawnIo.Installed)),
            new DiagnosticItem("Version", Display.Text(pawnIo?.Version)),
            new DiagnosticItem("File version", Display.Text(pawnIo?.FileVersion)),
            new DiagnosticItem(
                "Service state",
                Display.Text(pawnIo?.ServiceState),
                Display.Text(pawnIo?.DriverPath)),
            new DiagnosticItem(
                "Authenticode",
                Display.Text(pawnIo?.AuthenticodeStatus),
                Display.Text(pawnIo?.SignatureSource),
                string.Equals(pawnIo?.AuthenticodeStatus, "Valid", StringComparison.Ordinal)
                    ? StatusTone.Good
                    : pawnIo is null ? StatusTone.Muted : StatusTone.Warning),
            new DiagnosticItem(
                "Windows trusted signer",
                Display.Text(pawnIo?.WindowsTrustedSignerSubject)),
            new DiagnosticItem("Embedded signer", Display.Text(pawnIo?.EmbeddedSignerSubject)),
            new DiagnosticItem("Timestamp signer", Display.Text(pawnIo?.TimestampSignerSubject)),
            new DiagnosticItem("SHA256", Display.Text(pawnIo?.Sha256)),
            new DiagnosticItem(
                "Safe for thermal ownership",
                pawnIo is null
                    ? Display.Unavailable
                    : Display.Boolean(pawnIo.IsSafeForThermalOwnership),
                pawnIo?.Diagnostics is { Count: > 0 } diagnostics
                    ? string.Join(" ", diagnostics)
                    : null,
                pawnIo is null
                    ? StatusTone.Muted
                    : Display.BooleanTone(pawnIo.IsSafeForThermalOwnership))
        ]);

        SchedulerMetrics? metrics = status?.Scheduler;
        Scheduler.Replace(
        [
            new DiagnosticItem(
                "Requested period",
                metrics is null ? Display.Unavailable : Display.Duration(metrics.RequestedPeriod)),
            new DiagnosticItem(
                "Actual start-to-start",
                metrics is null
                    ? Display.Unavailable
                    : Display.Duration(metrics.LatestStartToStart)),
            new DiagnosticItem(
                "Cycle execution",
                metrics is null
                    ? Display.Unavailable
                    : Display.Duration(metrics.LatestCycleExecutionDuration)),
            new DiagnosticItem(
                "Deadline lateness",
                metrics is null ? Display.Unavailable : Display.Duration(metrics.LatestDeadlineLateness)),
            new DiagnosticItem(
                "Completed cycles",
                metrics?.CompletedCycles.ToString("N0", CultureInfo.CurrentCulture) ??
                    Display.Unavailable),
            new DiagnosticItem(
                "Overruns",
                metrics?.SlowCycleCount.ToString("N0", CultureInfo.CurrentCulture) ??
                    Display.Unavailable,
                null,
                metrics is null || stopped
                    ? StatusTone.Muted
                    : metrics.SlowCycleCount == 0 ? StatusTone.Good : StatusTone.Warning),
            new DiagnosticItem(
                "Maximum overrun",
                metrics is null ? Display.Unavailable : Display.Duration(metrics.MaximumCycleExecutionDuration)),
            new DiagnosticItem(
                "Skipped deadlines",
                metrics?.SkippedDeadlines.ToString("N0", CultureInfo.CurrentCulture) ??
                    Display.Unavailable),
            new DiagnosticItem(
                "Scheduler health",
                Display.Text(status?.SchedulerHealth),
                null,
                stopped ? StatusTone.Muted : Display.SchedulerTone(status?.SchedulerHealth)),
            new DiagnosticItem(
                "Last acquisition",
                status is null
                    ? Display.Unavailable
                    : Display.Duration(status.LastTelemetryAcquisitionDuration))
        ]);
    }
}
