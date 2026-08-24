using BladeControl.UI.Ipc;
using BladeControl.UI.Services;
using BladeControl.UI.ViewModels;

namespace BladeControl.UI.Tests;

/// <summary>
/// The GUI displays the runtime's qualification result; it does not compute one of its own.
/// </summary>
/// <remarks>
/// <para>An installed RC showed "Thermal ownership ready: Yes" under the heading "Last session
/// telemetry", on a machine where no session had run since the service restarted and where the
/// CLI was simultaneously reporting GPU thermal limits unavailable. Two of those three things
/// were presentation defects sitting on top of a correct backend.</para>
/// <para>These tests pin the presentation: readiness reflects the backend result, the reason is
/// shown next to it, and the most recent qualification is never filed under a session heading.</para>
/// </remarks>
[TestClass]
public sealed class QualificationDisplayTests
{
    [TestMethod]
    public async Task UnavailableGpuLimitsAreReportedAsNotReady()
    {
        var client = new FakeRuntimeUiClient
        {
            Doctor = RuntimeUiSampleData.Doctor(
                thermalOwnershipReady: false,
                reasons: ["GPU thermal limits could not be established."],
                gpuThermalLimitsKnown: false)
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);

        Assert.IsFalse(connection.IsThermalOwnershipReady);
        Assert.IsFalse(connection.CanStartThermalControl, "Start must not be offered.");
    }

    [TestMethod]
    public async Task QualifiedBackendIsReportedAsReady()
    {
        var client = new FakeRuntimeUiClient { Doctor = RuntimeUiSampleData.Doctor() };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);

        Assert.IsTrue(connection.IsThermalOwnershipReady);
    }

    /// <summary>
    /// Readiness and the GPU-limit prerequisite both come from the doctor report, so the GUI
    /// cannot show one without the other having been evaluated together.
    /// </summary>
    [TestMethod]
    public async Task ReadinessAndLimitsComeFromTheSameReport()
    {
        var client = new FakeRuntimeUiClient
        {
            Doctor = RuntimeUiSampleData.Doctor(
                thermalOwnershipReady: false,
                reasons: ["GPU thermal limits could not be established: field 193 failed."],
                gpuThermalLimitsKnown: false)
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);

        RuntimeDoctorReportDto doctor = connection.Doctor!;
        Assert.IsFalse(doctor.ThermalOwnershipReady);
        Assert.IsFalse(doctor.GpuThermalLimitsKnown);
        StringAssert.Contains(doctor.GpuThermalLimitDiagnostic!, "could not be established");
    }

    /// <summary>
    /// Qualification is a timestamped evaluation, not a session record. It lives in its own
    /// group so a stopped runtime cannot relabel it as session history or call it live.
    /// </summary>
    [TestMethod]
    public async Task QualificationIsNotPresentedAsLastSessionData()
    {
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(state: "Stopped"),
            Doctor = RuntimeUiSampleData.Doctor(
                thermalOwnershipReady: false,
                reasons: ["GPU thermal limits could not be established."],
                gpuThermalLimitsKnown: false)
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var diagnostics = new DiagnosticsViewModel(connection, CancellationToken.None);

        // The groups are built when the page becomes visible, as they are in the shell.
        diagnostics.Activate();

        StringAssert.Contains(diagnostics.Qualification.Title, "qualification");
        StringAssert.StartsWith(diagnostics.Qualification.Title, "Most recent");
        Assert.IsFalse(
            diagnostics.Qualification.Title.Contains("Last session", StringComparison.Ordinal),
            "The latest qualification must never be labelled as a past session.");

        DiagnosticItem readiness = diagnostics.Qualification.Items.Single(
            item => item.Label == "Thermal ownership ready");
        Assert.AreEqual("No", readiness.Value);

        DiagnosticItem limits = diagnostics.Qualification.Items.Single(
            item => item.Label == "GPU thermal limits");
        Assert.AreEqual("No", limits.Value);
        StringAssert.Contains(limits.Detail!, "could not be established");

        DiagnosticItem evaluated = diagnostics.Qualification.Items.Single(
            item => item.Label == "Evaluated");
        StringAssert.Contains(evaluated.Detail!, "Most recent qualification");
        Assert.IsFalse(evaluated.Detail!.Contains("Current", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// After an emergency handoff the fans belong to firmware, so a last-seen Manual
    /// observation must not be presented as current.
    /// </summary>
    /// <remarks>
    /// Found live. A session ended in a legitimate emergency handoff at 100 C, and the report
    /// still announced "Current watchdog observation: Balanced + Manual" — which reads as
    /// BladeControl still owning the fans at exactly the moment it had given them back. The
    /// historical labelling tested only for Stopped.
    /// </remarks>
    [DataTestMethod]
    [DataRow("Starting")]
    [DataRow("Stopping")]
    [DataRow("EmergencyHandoff")]
    [DataRow("Faulted")]
    [DataRow("Stopped")]
    public async Task NonRunningStatesPresentObservationsAsHistorical(string state)
    {
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(
                state: state,
                watchdog: RuntimeUiSampleData.Watchdog())
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var diagnostics = new DiagnosticsViewModel(connection, CancellationToken.None);
        diagnostics.Activate();

        Assert.AreEqual(
            "Razer",
            diagnostics.Razer.Title,
            "The group also contains a current doctor result and a direct profile snapshot; " +
            "a watchdog-history heading would mislabel those sources.");
        Assert.IsTrue(
            diagnostics.Razer.Items
                .Where(item => item.Label.Contains("watchdog", StringComparison.OrdinalIgnoreCase))
                .All(item => item.Label.StartsWith("Last watchdog", StringComparison.Ordinal)),
            $"Every watchdog row must identify history while state is {state}.");
        DiagnosticItem fan = diagnostics.Razer.Items.Single(
            item => item.Label.StartsWith("Firmware-reported fan state", StringComparison.Ordinal));
        StringAssert.Contains(fan.Detail!, "direct profile-read");
        StringAssert.Contains(fan.Detail!, "not a watchdog observation");
        Assert.IsFalse(
            fan.Detail!.Contains("Historical", StringComparison.OrdinalIgnoreCase),
            "Runtime state cannot date an independent profile read that has no timestamp.");
        Assert.AreEqual(
            "Telemetry",
            diagnostics.Telemetry.Title,
            "Capability reports and latest-sample provenance are not last-session values.");
    }

    /// <summary>A running session is the only state that reports current readings.</summary>
    [TestMethod]
    public async Task RunningStatePresentsObservationsAsCurrent()
    {
        var client = new FakeRuntimeUiClient { Status = RuntimeUiSampleData.Status(state: "Running") };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var diagnostics = new DiagnosticsViewModel(connection, CancellationToken.None);
        diagnostics.Activate();

        Assert.IsFalse(
            diagnostics.Razer.Title.Contains("Last", StringComparison.Ordinal),
            "A live session is not history.");
    }

    /// <summary>A direct profile read is not relabelled as watchdog or live fan data.</summary>
    /// <remarks>
    /// Connection.Fan is populated independently by GetFanState and has no timestamp. Runtime
    /// state cannot establish its age, so Diagnostics names the source without calling it
    /// current or historical. It still must not look like a physical tachometer reading.
    /// </remarks>
    [TestMethod]
    public async Task StoppedFirmwareFanValueNamesItsSourceWithoutInventingAge()
    {
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(state: "Stopped")
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var diagnostics = new DiagnosticsViewModel(connection, CancellationToken.None);
        diagnostics.Activate();

        DiagnosticItem fan = diagnostics.Razer.Items.Single(
            item => item.Label.StartsWith("Firmware-reported fan state", StringComparison.Ordinal));

        StringAssert.Contains(fan.Label, "last direct profile read");
        StringAssert.Contains(fan.Detail!, "Most recent direct profile-read snapshot");
        StringAssert.Contains(fan.Detail!, "not a watchdog observation");
        StringAssert.Contains(fan.Detail!, "live fan reading");
        StringAssert.Contains(fan.Detail!, "not proven to be a physical tachometer reading");
        Assert.IsFalse(
            fan.Detail!.Contains("Historical", StringComparison.OrdinalIgnoreCase),
            "Runtime state cannot date an independent profile read that has no timestamp.");
        Assert.IsFalse(
            fan.Detail!.Contains("current reading", StringComparison.OrdinalIgnoreCase),
            "The absent timestamp cannot support a current-versus-historical claim.");
    }

    /// <summary>A completed emergency handoff has one warning outcome on every surface.</summary>
    /// <remarks>
    /// Diagnostics coloured every non-empty Emergency status Danger even when the adjacent
    /// runtime state correctly said EmergencyHandoff in Warning. This test fails against that
    /// field-presence-only tone.
    /// </remarks>
    [TestMethod]
    public async Task SuccessfulEmergencyStatusUsesTheSafeHandoffTone()
    {
        var client = new FakeRuntimeUiClient
        {
            Status = RuntimeUiSampleData.Status(
                state: "EmergencyHandoff",
                emergencyStatus: "Emergency Balanced + Auto handoff completed.")
        };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var diagnostics = new DiagnosticsViewModel(connection, CancellationToken.None);
        diagnostics.Activate();

        DiagnosticItem emergency = diagnostics.Runtime.Items.Single(
            item => item.Label == "Emergency status");
        Assert.AreEqual(StatusTone.Warning, emergency.Tone);
        Assert.AreEqual(
            Display.RuntimeStateTone("EmergencyHandoff"),
            emergency.Tone);
    }
}
