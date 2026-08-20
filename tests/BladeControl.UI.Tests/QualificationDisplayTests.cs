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
/// shown next to it, and current qualification data is never filed under a session heading.</para>
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
    /// Qualification is current state, not a session record. It lives in its own group so a
    /// stopped runtime cannot relabel it as history.
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
        Assert.IsFalse(
            diagnostics.Qualification.Title.Contains("Last session", StringComparison.Ordinal),
            "Current qualification must never be labelled as a past session.");

        DiagnosticItem readiness = diagnostics.Qualification.Items.Single(
            item => item.Label == "Thermal ownership ready");
        Assert.AreEqual("No", readiness.Value);

        DiagnosticItem limits = diagnostics.Qualification.Items.Single(
            item => item.Label == "GPU thermal limits");
        Assert.AreEqual("No", limits.Value);
        StringAssert.Contains(limits.Detail!, "could not be established");
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
    [DataRow("EmergencyHandoff")]
    [DataRow("Faulted")]
    [DataRow("Stopped")]
    public async Task NonRunningStatesPresentObservationsAsHistorical(string state)
    {
        var client = new FakeRuntimeUiClient { Status = RuntimeUiSampleData.Status(state: state) };
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var diagnostics = new DiagnosticsViewModel(connection, CancellationToken.None);
        diagnostics.Activate();

        StringAssert.Contains(diagnostics.Razer.Title, "Last watchdog observation");
        DiagnosticItem fan = diagnostics.Razer.Items.Single(
            item => item.Label.StartsWith("Firmware-reported fan state", StringComparison.Ordinal));
        StringAssert.Contains(fan.Detail!, "Historical");
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

    /// <summary>
    /// A firmware fan value read minutes ago must not read as the current RPM while stopped.
    /// </summary>
    [TestMethod]
    public async Task StoppedFirmwareFanValueIsMarkedHistorical()
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

        StringAssert.Contains(fan.Label, "last observation");
        StringAssert.Contains(fan.Detail!, "Historical");
        StringAssert.Contains(
            fan.Detail!,
            "not a current reading",
            "The distinction that matters is age, not only provenance.");
    }
}
