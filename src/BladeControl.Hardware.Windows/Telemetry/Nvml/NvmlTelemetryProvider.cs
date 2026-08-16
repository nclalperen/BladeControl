using System.Text;
using BladeControl.Telemetry;

namespace BladeControl.Hardware.Windows.Telemetry.Nvml;

internal sealed record NvmlDevice(
    IntPtr Handle,
    TelemetryGpuIdentity Identity);

internal interface INvmlApi
{
    NvmlResult Initialize();

    NvmlResult Shutdown();

    NvmlResult GetDevices(out IReadOnlyList<NvmlDevice> devices);

    NvmlResult GetTemperatureCurrent(NvmlDevice device, out double temperature);

    NvmlResult GetTemperatureLegacy(NvmlDevice device, out double temperature);

    NvmlResult GetPowerWatts(NvmlDevice device, out double watts);

    NvmlResult GetUtilization(
        NvmlDevice device,
        out double gpuPercent,
        out double memoryPercent);

    NvmlResult GetClockMegahertz(
        NvmlDevice device,
        NvmlClockType type,
        out double megahertz);

    NvmlResult GetMemory(
        NvmlDevice device,
        out ulong usedBytes,
        out ulong totalBytes);
}

internal sealed class NativeNvmlApi : INvmlApi
{
    internal NativeNvmlApi()
    {
        NvmlNativeMethods.EnsureResolverInstalled();
    }

    public NvmlResult Initialize() => NvmlNativeMethods.Initialize();

    public NvmlResult Shutdown() => NvmlNativeMethods.Shutdown();

    public NvmlResult GetDevices(out IReadOnlyList<NvmlDevice> devices)
    {
        devices = [];
        NvmlResult countResult = NvmlNativeMethods.GetDeviceCount(out uint count);
        if (countResult != NvmlResult.Success)
        {
            return countResult;
        }

        var found = new List<NvmlDevice>(checked((int)count));
        for (uint index = 0; index < count; index++)
        {
            NvmlResult handleResult = NvmlNativeMethods.GetDeviceHandle(index, out IntPtr handle);
            if (handleResult != NvmlResult.Success)
            {
                return handleResult;
            }

            NvmlResult nameResult = ReadString(
                (buffer, length) => NvmlNativeMethods.GetDeviceName(handle, buffer, length),
                96,
                out string name);
            if (nameResult != NvmlResult.Success)
            {
                return nameResult;
            }

            NvmlResult uuidResult = ReadString(
                (buffer, length) => NvmlNativeMethods.GetDeviceUuid(handle, buffer, length),
                96,
                out string uuid);
            if (uuidResult != NvmlResult.Success)
            {
                return uuidResult;
            }

            NvmlResult pciResult = NvmlNativeMethods.GetDevicePciInfo(handle, out NvmlPciInfo pci);
            if (pciResult != NvmlResult.Success)
            {
                return pciResult;
            }

            string pciBusId = string.IsNullOrWhiteSpace(pci.BusId)
                ? pci.BusIdLegacy
                : pci.BusId;
            found.Add(new NvmlDevice(
                handle,
                new TelemetryGpuIdentity(name, uuid, pciBusId)));
        }

        devices = found;
        return NvmlResult.Success;
    }

    public NvmlResult GetTemperatureCurrent(NvmlDevice device, out double temperature)
    {
        var value = new NvmlTemperature
        {
            Version = NvmlNativeMethods.TemperatureVersion,
            SensorType = 0
        };
        try
        {
            NvmlResult result = NvmlNativeMethods.GetTemperatureCurrent(device.Handle, ref value);
            temperature = value.Temperature;
            return result;
        }
        catch (EntryPointNotFoundException)
        {
            temperature = default;
            return NvmlResult.EntryPointUnavailable;
        }
    }

    public NvmlResult GetTemperatureLegacy(NvmlDevice device, out double temperature)
    {
        NvmlResult result = NvmlNativeMethods.GetTemperatureLegacy(
            device.Handle,
            sensorType: 0,
            out uint value);
        temperature = value;
        return result;
    }

    public NvmlResult GetPowerWatts(NvmlDevice device, out double watts)
    {
        NvmlResult result = NvmlNativeMethods.GetPowerUsage(device.Handle, out uint milliwatts);
        watts = milliwatts / 1000d;
        return result;
    }

    public NvmlResult GetUtilization(
        NvmlDevice device,
        out double gpuPercent,
        out double memoryPercent)
    {
        NvmlResult result = NvmlNativeMethods.GetUtilization(
            device.Handle,
            out NvmlUtilization utilization);
        gpuPercent = utilization.Gpu;
        memoryPercent = utilization.Memory;
        return result;
    }

    public NvmlResult GetClockMegahertz(
        NvmlDevice device,
        NvmlClockType type,
        out double megahertz)
    {
        NvmlResult result = NvmlNativeMethods.GetClockInfo(device.Handle, type, out uint value);
        megahertz = value;
        return result;
    }

    public NvmlResult GetMemory(
        NvmlDevice device,
        out ulong usedBytes,
        out ulong totalBytes)
    {
        NvmlResult result = NvmlNativeMethods.GetMemoryInfo(device.Handle, out NvmlMemory memory);
        usedBytes = memory.Used;
        totalBytes = memory.Total;
        return result;
    }

    private static NvmlResult ReadString(
        Func<byte[], uint, NvmlResult> read,
        int capacity,
        out string value)
    {
        var buffer = new byte[capacity];
        NvmlResult result = read(buffer, checked((uint)buffer.Length));
        int terminator = Array.IndexOf(buffer, (byte)0);
        value = Encoding.UTF8.GetString(buffer, 0, terminator < 0 ? buffer.Length : terminator);
        return result;
    }
}

internal sealed class NvmlTelemetryProvider : IDisposable
{
    private readonly INvmlApi _api;
    private readonly NvmlDevice _device;
    private bool _initialized;
    private bool _disposed;

    private NvmlTelemetryProvider(
        INvmlApi api,
        NvmlDevice device,
        IReadOnlyList<TelemetryGpuIdentity> devices)
    {
        _api = api;
        _device = device;
        Devices = devices.ToArray();
        _initialized = true;
    }

    internal TelemetryGpuIdentity SelectedGpu => _device.Identity;

    internal IReadOnlyList<TelemetryGpuIdentity> Devices { get; }

    internal static bool TryOpen(
        string? preferredPciBusId,
        out NvmlTelemetryProvider? provider,
        out bool ambiguous,
        out IReadOnlyList<TelemetryGpuIdentity> devices,
        out string diagnostic)
    {
        try
        {
            return TryOpen(
                new NativeNvmlApi(),
                preferredPciBusId,
                out provider,
                out ambiguous,
                out devices,
                out diagnostic);
        }
        catch (Exception exception) when (exception is
            DllNotFoundException or
            BadImageFormatException or
            EntryPointNotFoundException)
        {
            provider = null;
            ambiguous = false;
            devices = [];
            diagnostic = $"NVML is unavailable: {exception.Message}";
            return false;
        }
    }

    internal static bool TryOpen(
        INvmlApi api,
        string? preferredPciBusId,
        out NvmlTelemetryProvider? provider,
        out bool ambiguous,
        out IReadOnlyList<TelemetryGpuIdentity> devices,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(api);
        provider = null;
        ambiguous = false;
        devices = [];
        NvmlResult initialize = api.Initialize();
        if (initialize != NvmlResult.Success)
        {
            diagnostic = Describe("nvmlInit_v2", initialize);
            return false;
        }

        bool transferInitialization = false;
        try
        {
            NvmlResult enumerate = api.GetDevices(out IReadOnlyList<NvmlDevice> found);
            devices = found.Select(device => device.Identity).ToArray();
            if (enumerate != NvmlResult.Success)
            {
                diagnostic = Describe("NVML device enumeration", enumerate);
                return false;
            }

            NvmlDevice? selected = SelectDevice(found, preferredPciBusId, out ambiguous);
            if (selected is null)
            {
                diagnostic = found.Count == 0
                    ? "NVML enumerated no NVIDIA GPU."
                    : ambiguous
                        ? "Multiple plausible NVIDIA GPUs were found; automatic thermal control is disabled."
                        : $"No GPU matched preferred PCI identity '{preferredPciBusId}'.";
                return false;
            }

            provider = new NvmlTelemetryProvider(api, selected, devices);
            transferInitialization = true;
            diagnostic = "NVML initialized and one GPU was selected deterministically.";
            return true;
        }
        finally
        {
            if (!transferInitialization)
            {
                _ = api.Shutdown();
            }
        }
    }

    internal NvmlGpuReading Read(DateTimeOffset timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TelemetryMetric<double> temperature = ReadTemperature(timestamp);
        TelemetryMetric<double> power = ReadDouble(
            timestamp,
            _api.GetPowerWatts,
            "GPU power");
        NvmlResult utilizationResult = _api.GetUtilization(
            _device,
            out double gpuUtilization,
            out double memoryUtilization);
        TelemetryMetric<double> gpuLoad = CreateMetric(
            utilizationResult,
            gpuUtilization,
            timestamp,
            "GPU utilization");
        TelemetryMetric<double> memoryLoad = CreateMetric(
            utilizationResult,
            memoryUtilization,
            timestamp,
            "GPU memory utilization");
        TelemetryMetric<double> graphicsClock = ReadClock(
            timestamp,
            NvmlClockType.Graphics,
            "GPU graphics clock");
        TelemetryMetric<double> memoryClock = ReadClock(
            timestamp,
            NvmlClockType.Memory,
            "GPU memory clock");
        NvmlResult memoryResult = _api.GetMemory(
            _device,
            out ulong used,
            out ulong total);
        TelemetryMetric<ulong> usedMemory = CreateMetric(
            memoryResult,
            used,
            timestamp,
            "GPU VRAM used");
        TelemetryMetric<ulong> totalMemory = CreateMetric(
            memoryResult,
            total,
            timestamp,
            "GPU VRAM total");

        return new NvmlGpuReading(
            temperature,
            power,
            gpuLoad,
            memoryLoad,
            graphicsClock,
            memoryClock,
            usedMemory,
            totalMemory);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_initialized)
        {
            _ = _api.Shutdown();
            _initialized = false;
        }

        _disposed = true;
    }

    private static NvmlDevice? SelectDevice(
        IReadOnlyList<NvmlDevice> devices,
        string? preferredPciBusId,
        out bool ambiguous)
    {
        ambiguous = false;
        if (devices.Count == 1)
        {
            return devices[0];
        }

        if (devices.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredPciBusId))
        {
            NvmlDevice[] matches = devices
                .Where(device => device.Identity.PciBusId.Equals(
                    preferredPciBusId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length == 1)
            {
                return matches[0];
            }
        }

        ambiguous = true;
        return null;
    }

    private TelemetryMetric<double> ReadTemperature(DateTimeOffset timestamp)
    {
        NvmlResult result = _api.GetTemperatureCurrent(_device, out double value);
        if (result is NvmlResult.NotSupported or NvmlResult.EntryPointUnavailable)
        {
            result = _api.GetTemperatureLegacy(_device, out value);
        }

        return result == NvmlResult.Success
            ? TelemetryMetric<double>.Available(value, timestamp, TelemetrySources.GpuTemperature)
            : result == NvmlResult.NotSupported
                ? TelemetryMetric<double>.Unsupported(
                    TelemetrySources.GpuTemperature,
                    Describe("GPU temperature", result))
                : TelemetryMetric<double>.Invalid(
                    null,
                    timestamp,
                    TelemetrySources.GpuTemperature,
                    Describe("GPU temperature", result));
    }

    private TelemetryMetric<double> ReadDouble(
        DateTimeOffset timestamp,
        NvmlDoubleQuery query,
        string name)
    {
        NvmlResult result = query(_device, out double value);
        return CreateMetric(result, value, timestamp, name);
    }

    private TelemetryMetric<double> ReadClock(
        DateTimeOffset timestamp,
        NvmlClockType type,
        string name)
    {
        NvmlResult result = _api.GetClockMegahertz(_device, type, out double value);
        return CreateMetric(result, value, timestamp, name);
    }

    private static TelemetryMetric<T> CreateMetric<T>(
        NvmlResult result,
        T value,
        DateTimeOffset timestamp,
        string name) where T : struct
    {
        return result switch
        {
            NvmlResult.Success => TelemetryMetric<T>.Available(
                value,
                timestamp,
                TelemetrySources.GpuOptional),
            NvmlResult.NotSupported => TelemetryMetric<T>.Unsupported(
                TelemetrySources.GpuOptional,
                Describe(name, result)),
            _ => TelemetryMetric<T>.Invalid(
                null,
                timestamp,
                TelemetrySources.GpuOptional,
                Describe(name, result))
        };
    }

    private static string Describe(string operation, NvmlResult result) =>
        $"{operation} returned {result} ({(int)result}).";

    private delegate NvmlResult NvmlDoubleQuery(NvmlDevice device, out double value);
}

internal sealed record NvmlGpuReading(
    TelemetryMetric<double> TemperatureCelsius,
    TelemetryMetric<double> PowerWatts,
    TelemetryMetric<double> GpuUtilizationPercent,
    TelemetryMetric<double> MemoryUtilizationPercent,
    TelemetryMetric<double> GraphicsClockMegahertz,
    TelemetryMetric<double> MemoryClockMegahertz,
    TelemetryMetric<ulong> VramUsedBytes,
    TelemetryMetric<ulong> VramTotalBytes);
