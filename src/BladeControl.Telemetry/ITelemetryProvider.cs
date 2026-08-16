namespace BladeControl.Telemetry;

public interface IControlTelemetryProvider : IDisposable
{
    string Name { get; }

    TelemetryCapabilities Capabilities { get; }

    ThermalTelemetrySample GetControlSample();
}

public interface ITelemetryProvider : IDisposable
{
    string Name { get; }

    TelemetryCapabilities Capabilities { get; }

    TelemetrySnapshot GetSnapshot();
}
