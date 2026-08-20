using System.Text;
using BladeControl.Runtime;

namespace BladeControl.Cli.Tests;

/// <summary>
/// What <c>runtime status</c> shows when there is no scheduler history to show.
/// </summary>
/// <remarks>
/// The scheduler block is withheld when no session has run, which is right: a table of zeros
/// under a "Healthy" heading is absence dressed as measurement. But the runtime's own
/// diagnostics — total events, last failure, emergency status — used to be rendered from
/// inside that block, behind its early returns, so withholding the scheduler withheld those
/// too. A process that crashed, was restarted by the SCM, and failed to recover orphaned
/// Manual mode has by definition run no session; that is precisely when "Last failure" decides
/// whether the machine is safe, and precisely when it was hidden.
/// </remarks>
[TestClass]
public sealed class StatusRenderingTests
{
    [TestMethod]
    public void FailureIsShownEvenWhenNoSessionHasRun()
    {
        string report = Render(Status(
            "Faulted",
            lastFailureReason: "ORPHANED MANUAL MODE RECOVERY FAILED: injected Auto failure"));

        StringAssert.Contains(report, "No session has run since the runtime started.");
        StringAssert.Contains(report, "Last failure");
        StringAssert.Contains(report, "ORPHANED MANUAL MODE RECOVERY FAILED");
    }

    [TestMethod]
    public void EmergencyStatusSurvivesAnEmptyScheduler()
    {
        string report = Render(Status(
            "EmergencyHandoff",
            emergencyStatus: "Firmware Auto owns cooling."));

        StringAssert.Contains(report, "Emergency status");
        StringAssert.Contains(report, "Firmware Auto owns cooling.");
    }

    /// <summary>The scheduler block stays withheld; hoisting the diagnostics did not restore it.</summary>
    [TestMethod]
    public void AnEmptySchedulerIsStillNotRenderedAsZeroes()
    {
        string report = Render(Status("Stopped"));

        StringAssert.Contains(report, "No session has run since the runtime started.");
        Assert.IsFalse(
            report.Contains("Completed cycles", StringComparison.Ordinal),
            "An empty scheduler must not be rendered as a table of zeroes.");
        Assert.IsFalse(
            report.Contains("Slow cycles", StringComparison.Ordinal),
            "An empty scheduler must not be rendered as a table of zeroes.");
    }

    private static string Render(RuntimeStatusDto status)
    {
        var writer = new StringWriter(new StringBuilder());
        BladeControl.Cli.Program.PrintRuntimeStatus(status, verbose: true, writer);
        return writer.ToString();
    }

    private static RuntimeStatusDto Status(
        string state,
        string? lastFailureReason = null,
        string? emergencyStatus = null) => new(
        State: state,
        SessionId: null,
        StartTimestamp: null,
        CurrentProfile: null,
        CapturedOriginalPerformanceState: null,
        CurrentEffectiveFanTargetRpm: null,
        LatestAuthoritativeTelemetry: null,
        TelemetryHealth: null,
        Scheduler: SchedulerMetrics.Idle(TimeSpan.FromMilliseconds(500)),
        SchedulerHealth: "Healthy",
        RuntimeBuild: "0.1.0+test",
        LastRazerWatchdogState: null,
        LastRazerWatchdogObservedAt: null,
        LastFailureReason: lastFailureReason,
        LastStartRejectionReason: null,
        EmergencyStatus: emergencyStatus,
        LastTelemetryAcquisitionDuration: TimeSpan.Zero,
        TelemetryAcquisition: null,
        ActuatorDuration: null,
        WatchdogCoalescedCount: 0,
        TotalEventCount: 3,
        RetainedThermalDecisionCount: 0,
        RetainedThermalTraceCount: 0,
        RecentEvents: []);
}
