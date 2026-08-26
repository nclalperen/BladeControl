using BladeControl.Razer;
using BladeControl.Runtime;
using BladeControl.Thermal;

namespace BladeControl.Runtime.Tests;

/// <summary>
/// Every field of the status DTO carries the value it is named after.
/// </summary>
/// <remarks>
/// <para><c>RuntimeStatusDto</c> has grown past twenty parameters and contains runs of
/// same-typed neighbours: three adjacent nullable strings (failure, start rejection, emergency),
/// two adjacent duration statistics, two longs, two ints. A transposition inside any of those
/// runs compiles cleanly and passes every behavioural test, then surfaces as a diagnostic that
/// quietly reports the wrong thing — a refusal shown as a fault, actuator timings labelled as
/// acquisition.</para>
/// <para>The mapper uses named arguments so such a mistake cannot be made silently. This test
/// is the other half: it gives every field in a risky run a distinct value and checks each
/// arrives where its name says, so a future edit that reorders the record is caught by a
/// failing test rather than by someone eventually noticing a wrong number on a screen.</para>
/// </remarks>
[TestClass]
public sealed class StatusMappingTests
{
    [TestMethod]
    public void AdjacentSameTypedFieldsAreNotTransposed()
    {
        RuntimeStatusDto dto = RuntimeIpcDtoMapper.ToSummaryDto(Status());

        // The three adjacent nullable strings.
        Assert.AreEqual("the-failure", dto.LastFailureReason);
        Assert.AreEqual("the-rejection", dto.LastStartRejectionReason);
        Assert.AreEqual("the-emergency", dto.EmergencyStatus);

        // The two adjacent statistics, distinguished by sample count.
        Assert.AreEqual(11, dto.TelemetryAcquisition!.SampleCount);
        Assert.AreEqual(22, dto.ActuatorDuration!.SampleCount);

        // The two adjacent longs, and the two adjacent ints.
        Assert.AreEqual(33L, dto.WatchdogCoalescedCount);
        Assert.AreEqual(44L, dto.TotalEventCount);
        Assert.AreEqual(55, dto.RetainedThermalDecisionCount);
        Assert.AreEqual(66, dto.RetainedThermalTraceCount);
    }

    [TestMethod]
    public void ScalarIdentityFieldsSurviveTheMapping()
    {
        RuntimeStatusDto dto = RuntimeIpcDtoMapper.ToSummaryDto(Status());

        Assert.AreEqual("Running", dto.State);
        Assert.AreEqual("Thermal/default", dto.CurrentProfile);
        Assert.AreEqual("build-under-test", dto.RuntimeBuild);
        Assert.AreEqual("scheduler-health", dto.SchedulerHealth);
        Assert.AreEqual(4321, dto.CurrentEffectiveFanTargetRpm);
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(77),
            dto.LastTelemetryAcquisitionDuration);
    }

    /// <summary>The watchdog observation and its timestamp stay together.</summary>
    [TestMethod]
    public void WatchdogObservationKeepsItsOwnTimestamp()
    {
        var observedAt = new DateTimeOffset(2026, 8, 20, 9, 30, 0, TimeSpan.Zero);
        RuntimeStatusDto dto = RuntimeIpcDtoMapper.ToSummaryDto(Status(observedAt));

        Assert.AreEqual(observedAt, dto.LastRazerWatchdogObservedAt);
        Assert.IsNotNull(dto.LastRazerWatchdogState);
        Assert.AreEqual("Balanced", dto.LastRazerWatchdogState!.Zone1PerformanceMode);
    }

    private static RuntimeStatus Status(DateTimeOffset? observedAt = null) => new(
        RuntimeState.Running,
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        "Thermal/default",
        null,
        4321,
        null,
        null,
        SchedulerMetrics.Idle(TimeSpan.FromMilliseconds(500)),
        "scheduler-health",
        "build-under-test",
        new RuntimeRazerModeState(
            RazerPerformanceMode.Balanced,
            RazerFanMode.Manual,
            RazerPerformanceMode.Balanced,
            RazerFanMode.Manual,
            []),
        observedAt ?? DateTimeOffset.UtcNow,
        "the-failure",
        "the-rejection",
        "the-emergency",
        TimeSpan.FromMilliseconds(77),
        Statistics(11),
        Statistics(22),
        Statistics(88),
        Statistics(99),
        33,
        44,
        55,
        66,
        []);

    /// <summary>A statistic identifiable purely by its sample count.</summary>
    private static DurationStatistics Statistics(int samples) => new(
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        samples);
}
