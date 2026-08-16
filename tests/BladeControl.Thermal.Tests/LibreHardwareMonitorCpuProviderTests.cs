using BladeControl.Hardware.Windows.Telemetry;

namespace BladeControl.Thermal.Tests;

[TestClass]
public sealed class LibreHardwareMonitorCpuProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void UniqueCpuPackageTemperatureIsSelectedExactly()
    {
        var backend = new FakeBackend
        {
            Sensors =
            [
                Sensor("Cpu", "Temperature", "Core Max", 60),
                Sensor("Cpu", "Temperature", "CPU Package", 58),
                Sensor("Cpu", "Power", "CPU Package", 18.5f)
            ]
        };
        using LibreHardwareMonitorCpuProvider provider =
            LibreHardwareMonitorCpuProvider.Open(backend);

        CpuTelemetryReading reading = provider.Read(Now);

        Assert.AreEqual(58d, reading.PackageTemperatureCelsius.Value);
        Assert.AreEqual(18.5d, reading.PackagePowerWatts.Value);
    }

    [TestMethod]
    public void MissingPackageTemperatureIsUnavailable()
    {
        using LibreHardwareMonitorCpuProvider provider =
            LibreHardwareMonitorCpuProvider.Open(new FakeBackend());

        CpuTelemetryReading reading = provider.Read(Now);

        Assert.IsFalse(reading.PackageTemperatureCelsius.IsValid);
        StringAssert.Contains(reading.PackageTemperatureCelsius.Diagnostic!, "No unique");
    }

    [TestMethod]
    public void DuplicatePackageCandidatesAreRejected()
    {
        var backend = new FakeBackend
        {
            Sensors =
            [
                Sensor("Cpu", "Temperature", "CPU Package", 55),
                Sensor("Cpu", "Temperature", "CPU Package", 56)
            ]
        };
        using LibreHardwareMonitorCpuProvider provider =
            LibreHardwareMonitorCpuProvider.Open(backend);

        CpuTelemetryReading reading = provider.Read(Now);

        Assert.IsFalse(reading.PackageTemperatureCelsius.IsValid);
        StringAssert.Contains(reading.PackageTemperatureCelsius.Diagnostic!, "2 CPU Package");
    }

    [TestMethod]
    public void PawnIoUnavailableDoesNotOpenBackend()
    {
        var backend = new FakeBackend { PawnIoInstalledValue = false };
        using LibreHardwareMonitorCpuProvider provider =
            LibreHardwareMonitorCpuProvider.Open(backend);

        CpuTelemetryReading reading = provider.Read(Now);

        Assert.AreEqual(0, backend.OpenCalls);
        Assert.IsFalse(reading.PackageTemperatureCelsius.IsSupported);
    }

    [TestMethod]
    public void OptionalPowerUnavailableDoesNotInvalidateRequiredTemperature()
    {
        var backend = new FakeBackend
        {
            Sensors = [Sensor("Cpu", "Temperature", "CPU Package", 55)]
        };
        using LibreHardwareMonitorCpuProvider provider =
            LibreHardwareMonitorCpuProvider.Open(backend);

        CpuTelemetryReading reading = provider.Read(Now);

        Assert.IsTrue(reading.PackageTemperatureCelsius.IsValid);
        Assert.IsFalse(reading.PackagePowerWatts.IsSupported);
    }

    [TestMethod]
    public void ConfigurationEnablesCpuOnly()
    {
        LibreHardwareMonitorConfiguration configuration =
            LibreHardwareMonitorConfiguration.CpuOnly;

        Assert.IsTrue(configuration.Cpu);
        Assert.IsFalse(configuration.Gpu);
        Assert.IsFalse(configuration.Motherboard);
        Assert.IsFalse(configuration.Controller);
        Assert.IsFalse(configuration.Storage);
        Assert.IsFalse(configuration.Network);
        Assert.IsFalse(configuration.Memory);
        Assert.IsFalse(configuration.Battery);
        Assert.IsFalse(configuration.Psu);
        Assert.IsFalse(configuration.PowerMonitor);
    }

    [TestMethod]
    public void NonCpuOnlyBackendConfigurationIsRejected()
    {
        var backend = new FakeBackend
        {
            ConfigurationValue = LibreHardwareMonitorConfiguration.CpuOnly with { Gpu = true }
        };

        Assert.ThrowsException<InvalidOperationException>(() =>
            LibreHardwareMonitorCpuProvider.Open(backend));
        Assert.IsTrue(backend.Disposed);
    }

    private static CpuSensorReading Sensor(
        string hardware,
        string type,
        string name,
        float? value) => new(hardware, type, name, value);

    private sealed class FakeBackend : ILibreHardwareMonitorBackend
    {
        internal bool PawnIoInstalledValue { get; set; } = true;

        internal LibreHardwareMonitorConfiguration ConfigurationValue { get; set; } =
            LibreHardwareMonitorConfiguration.CpuOnly;

        internal IReadOnlyList<CpuSensorReading> Sensors { get; set; } = [];

        internal int OpenCalls { get; private set; }

        internal bool Disposed { get; private set; }

        public bool PawnIoInstalled => PawnIoInstalledValue;

        public string LibraryVersion => "0.9.6.0";

        public LibreHardwareMonitorConfiguration Configuration => ConfigurationValue;

        public void Open() => OpenCalls++;

        public IReadOnlyList<CpuSensorReading> ReadSensors() => Sensors;

        public void Dispose() => Disposed = true;
    }
}
