namespace BladeControl.Telemetry;

public interface ITelemetryProvider : IDisposable
{
    string Name { get; }

    TelemetryCapabilities Capabilities { get; }

    TelemetrySnapshot GetSnapshot();
}
