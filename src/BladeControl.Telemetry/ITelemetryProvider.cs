namespace BladeControl.Telemetry;

public interface IControlTelemetryProvider : IDisposable
{
    string Name { get; }

    TelemetryCapabilities Capabilities { get; }

    /// <summary>How long the most recent CPU provider read took.</summary>
    /// <remarks>
    /// <para>Acquisition dominates the control period — around 390 ms of 500 ms on the
    /// reference machine — and the aggregate figure says the cycle is tight without saying
    /// which provider to look at. These two say. The windowing that turns them into
    /// percentiles belongs to the caller, which already keeps windows for the aggregate and
    /// the actuator.</para>
    /// <para>They were measured inside the Windows session from the start and went nowhere.
    /// known-limitations.md says the per-component statistics exist so that the
    /// acquisition/actuation split can be decided on distribution data; they could not, because
    /// the split between CPU and GPU was collected and discarded every cycle.</para>
    /// </remarks>
    TimeSpan LastCpuAcquisitionDuration { get; }

    /// <summary>How long the most recent GPU provider read took.</summary>
    TimeSpan LastGpuAcquisitionDuration { get; }

    ThermalTelemetrySample GetControlSample();

    ThermalOwnershipQualification QualifyThermalOwnership();
}

public interface ITelemetryProvider : IDisposable
{
    string Name { get; }

    TelemetryCapabilities Capabilities { get; }

    TelemetrySnapshot GetSnapshot();
}
