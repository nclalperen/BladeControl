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

    /// <summary>
    /// Reads the three core T.Limit <b>specifications</b> through nvmlDeviceGetFieldValues.
    /// Called once at qualification, never per sample.
    /// </summary>
    /// <remarks>
    /// Each output carries its own per-field <c>nvmlReturn</c> and declared value type, so a
    /// caller can tell a refused field from a successful one. The method's own return value is
    /// the outer call result only.
    /// </remarks>
    NvmlResult GetThermalLimitSpecifications(
        NvmlDevice device,
        out NvmlFieldReading shutdown,
        out NvmlFieldReading slowdown,
        out NvmlFieldReading gpuMax);

    /// <summary>
    /// Reads nvmlDeviceGetMarginTemperature: a <i>live</i> quantity, documented as the
    /// distance to the nearest slowdown threshold.
    /// </summary>
    NvmlResult GetMarginTemperature(NvmlDevice device, out int marginCelsius);

    /// <summary>
    /// Reads one absolute threshold through the legacy nvmlDeviceGetTemperatureThreshold API.
    /// Qualification-time corroboration only.
    /// </summary>
    NvmlResult GetTemperatureThreshold(
        NvmlDevice device,
        NvmlTemperatureThreshold threshold,
        out double celsius);

    /// <summary>
    /// Reads nvmlDeviceGetThermalSettings for one sensor index. Diagnostic only.
    /// </summary>
    NvmlResult GetThermalSettings(
        NvmlDevice device,
        uint sensorIndex,
        out uint count,
        out IReadOnlyList<NvmlThermalSensor> sensors);
}

/// <summary>One nvmlDeviceGetThermalSettings query and everything it returned.</summary>
internal sealed record NvmlThermalSettingsReading(
    uint RequestedIndex,
    NvmlResult Result,
    uint Count,
    IReadOnlyList<NvmlThermalSensor> Sensors);

/// <summary>One absolute threshold from the legacy API, with the driver's own status.</summary>
internal readonly record struct NvmlThresholdReading(
    NvmlTemperatureThreshold Threshold,
    NvmlResult Result,
    double? Celsius)
{
    internal string Describe() =>
        $"{Threshold}: result {Result}, " +
        (Celsius is { } value ? $"{value:F0} C" : "unavailable");
}

/// <summary>
/// One element of an nvmlDeviceGetFieldValues response, kept raw so a diagnostic can show what
/// the driver actually returned rather than only what was made of it.
/// </summary>
internal readonly record struct NvmlFieldReading(
    uint FieldId,
    NvmlResult Result,
    NvmlValueType ValueType,
    long RawValue,
    double? Celsius)
{
    internal bool IsUsable => Result == NvmlResult.Success && Celsius is not null;

    internal string Describe() =>
        $"field {FieldId}: result {Result}, valueType {ValueType}, raw 0x{RawValue:X16}, " +
        (Celsius is { } value ? $"{value:F0} C" : "not readable as a temperature");
}

/// <summary>
/// Everything the driver said about this device's thermal limits in one read-only pass, before
/// any interpretation. Exists so the conversion can be verified against the hardware instead of
/// inferred.
/// </summary>
internal sealed record NvmlThermalLimitProbe(
    NvmlResult FieldCallResult,
    NvmlFieldReading Shutdown,
    NvmlFieldReading Slowdown,
    NvmlFieldReading GpuMax,
    NvmlResult MarginResult,
    int? MarginCelsius,
    NvmlResult TemperatureResult,
    string TemperatureSource,
    double? CurrentTemperatureCelsius,
    NvmlThresholdReading LegacyShutdown,
    NvmlThresholdReading LegacySlowdown,
    NvmlThresholdReading LegacyGpuMax);

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

    public NvmlResult GetThermalLimitSpecifications(
        NvmlDevice device,
        out NvmlFieldReading shutdown,
        out NvmlFieldReading slowdown,
        out NvmlFieldReading gpuMax)
    {
        // Results are matched by fieldId on the way out, never by position, so a driver that
        // reorders or omits entries cannot silently swap one limit for another.
        NvmlFieldValue[] values = Array.ConvertAll(
            NvmlFieldId.CoreThermalLimitRequest,
            fieldId => new NvmlFieldValue { FieldId = fieldId });

        NvmlResult result = NvmlNativeMethods.GetFieldValues(
            device.Handle,
            values.Length,
            values);

        shutdown = Reading(values, NvmlFieldId.TemperatureShutdownTLimit, result);
        slowdown = Reading(values, NvmlFieldId.TemperatureSlowdownTLimit, result);
        gpuMax = Reading(values, NvmlFieldId.TemperatureGpuMaxTLimit, result);
        return result;
    }

    public NvmlResult GetMarginTemperature(NvmlDevice device, out int marginCelsius)
    {
        var request = new NvmlMarginTemperature
        {
            Version = NvmlNativeMethods.MarginTemperatureVersion
        };

        NvmlResult result = NvmlNativeMethods.GetMarginTemperature(device.Handle, ref request);
        marginCelsius = result == NvmlResult.Success ? request.MarginTemperature : 0;
        return result;
    }

    public NvmlResult GetTemperatureThreshold(
        NvmlDevice device,
        NvmlTemperatureThreshold threshold,
        out double celsius)
    {
        NvmlResult result = NvmlNativeMethods.GetTemperatureThreshold(
            device.Handle,
            threshold,
            out uint value);
        celsius = result == NvmlResult.Success ? value : double.NaN;
        return result;
    }

    public NvmlResult GetThermalSettings(
        NvmlDevice device,
        uint sensorIndex,
        out uint count,
        out IReadOnlyList<NvmlThermalSensor> sensors)
    {
        NvmlGpuThermalSettings settings = NvmlGpuThermalSettings.Create();
        NvmlResult result = NvmlNativeMethods.GetThermalSettings(
            device.Handle,
            sensorIndex,
            ref settings);
        if (result != NvmlResult.Success)
        {
            count = 0;
            sensors = [];
            return result;
        }

        // Trust the struct's own count over the array length, but never past the array: a
        // driver reporting more sensors than the header allows would otherwise read past it.
        count = settings.Count;
        int usable = (int)Math.Min(settings.Count, (uint)NvmlGpuThermalSettings.MaxSensors);
        sensors = settings.Sensors.Take(usable).ToArray();
        return result;
    }

    /// <summary>
    /// Extracts one field by identifier, preserving the driver's own per-field status.
    /// </summary>
    /// <remarks>
    /// A field the driver never populated is reported as <see cref="NvmlResult.NotFound"/>
    /// rather than defaulting to success with a zero value, which would read as a perfectly
    /// plausible 0 C specification.
    /// </remarks>
    private static NvmlFieldReading Reading(
        NvmlFieldValue[] values,
        uint fieldId,
        NvmlResult callResult)
    {
        foreach (NvmlFieldValue value in values)
        {
            if (value.FieldId != fieldId)
            {
                continue;
            }

            bool readable = value.Result == NvmlResult.Success &&
                value.TryReadCelsius(out double celsius);
            return new NvmlFieldReading(
                fieldId,
                callResult == NvmlResult.Success ? value.Result : callResult,
                value.ValueType,
                value.Value,
                readable ? RoundTrip(value) : null);
        }

        return new NvmlFieldReading(fieldId, NvmlResult.NotFound, default, 0, null);
    }

    private static double RoundTrip(NvmlFieldValue value)
    {
        _ = value.TryReadCelsius(out double celsius);
        return celsius;
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

    /// <summary>
    /// Reads everything the driver will say about this device's thermal limits, raw and
    /// uninterpreted.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="TryDiscoverThermalLimits"/> so a diagnostic can show the
    /// driver's own answers — per-field return codes, declared value types, raw union
    /// payloads — next to what was derived from them. Strictly read-only.
    /// </remarks>
    internal NvmlThermalLimitProbe ProbeThermalLimits()
    {
        NvmlResult fieldResult = _api.GetThermalLimitSpecifications(
            _device,
            out NvmlFieldReading shutdown,
            out NvmlFieldReading slowdown,
            out NvmlFieldReading gpuMax);

        NvmlResult marginResult = _api.GetMarginTemperature(_device, out int margin);

        string temperatureSource = "nvmlDeviceGetTemperatureV";
        NvmlResult temperatureResult = _api.GetTemperatureCurrent(
            _device,
            out double currentTemperature);
        if (temperatureResult != NvmlResult.Success)
        {
            temperatureSource = "nvmlDeviceGetTemperature";
            temperatureResult = _api.GetTemperatureLegacy(_device, out currentTemperature);
        }

        return new NvmlThermalLimitProbe(
            fieldResult,
            shutdown,
            slowdown,
            gpuMax,
            marginResult,
            marginResult == NvmlResult.Success ? margin : null,
            temperatureResult,
            temperatureSource,
            temperatureResult == NvmlResult.Success ? currentTemperature : null,
            Threshold(NvmlTemperatureThreshold.Shutdown),
            Threshold(NvmlTemperatureThreshold.Slowdown),
            Threshold(NvmlTemperatureThreshold.GpuMax));
    }

    private NvmlThresholdReading Threshold(NvmlTemperatureThreshold threshold)
    {
        NvmlResult result = _api.GetTemperatureThreshold(_device, threshold, out double celsius);
        return new NvmlThresholdReading(
            threshold,
            result,
            result == NvmlResult.Success && double.IsFinite(celsius) ? celsius : null);
    }

    /// <summary>
    /// Reads every member of nvmlTemperatureThresholds_t, for diagnostics only.
    /// </summary>
    /// <remarks>
    /// Nothing in the control path depends on this. It exists because the three thresholds the
    /// qualification gate consults turned out not to mean what their names suggest on Ada, and
    /// the fastest way to see that is the whole set side by side.
    /// </remarks>
    internal IReadOnlyList<NvmlThresholdReading> ProbeAllTemperatureThresholds() =>
        Enum.GetValues<NvmlTemperatureThreshold>().Select(Threshold).ToArray();

    /// <summary>
    /// Queries nvmlDeviceGetThermalSettings across the sensor indices the header allows, plus
    /// the all-sensors target value. Diagnostic only; nothing in the control path reads it.
    /// </summary>
    /// <remarks>
    /// The API takes a sensor <i>index</i> while NVML_THERMAL_TARGET_ALL is a <i>target</i>
    /// constant, so which one a given driver honours is not something to assume. Both are
    /// asked and both answers reported.
    /// </remarks>
    internal IReadOnlyList<NvmlThermalSettingsReading> ProbeThermalSettings()
    {
        uint[] indices =
        [
            0,
            1,
            2,
            (uint)NvmlThermalTarget.All
        ];

        return indices
            .Select(index =>
            {
                NvmlResult result = _api.GetThermalSettings(
                    _device,
                    index,
                    out uint count,
                    out IReadOnlyList<NvmlThermalSensor> sensors);
                return new NvmlThermalSettingsReading(index, result, count, sensors);
            })
            .ToArray();
    }

    /// <summary>
    /// Discovers the device's absolute thermal limits once, at qualification.
    /// </summary>
    /// <remarks>
    /// <para>The three T.Limit fields are <b>specifications</b>: signed offsets from the device
    /// maximum operating temperature. Converting them to absolute temperatures needs an anchor,
    /// and the anchor is a separate live quantity — the margin API — sampled together with a
    /// core temperature.</para>
    /// <para>Returns false rather than guessing. A device that will not report its limits is
    /// refused thermal ownership, because the alternative is inventing a threshold for silicon
    /// whose real limits are unknown.</para>
    /// </remarks>
    internal bool TryDiscoverThermalLimits(
        out GpuThermalLimits? limits,
        out string diagnostic)
    {
        limits = null;
        NvmlThermalLimitProbe probe = ProbeThermalLimits();

        if (probe.FieldCallResult != NvmlResult.Success)
        {
            diagnostic = Describe("GPU thermal limit discovery", probe.FieldCallResult);
            return false;
        }

        foreach (NvmlFieldReading reading in new[] { probe.Shutdown, probe.Slowdown, probe.GpuMax })
        {
            if (!reading.IsUsable)
            {
                diagnostic = $"GPU thermal limit discovery failed: {reading.Describe()}.";
                return false;
            }
        }

        if (probe.MarginResult != NvmlResult.Success || probe.MarginCelsius is not { } margin)
        {
            diagnostic = Describe("GPU thermal margin", probe.MarginResult);
            return false;
        }

        if (probe.TemperatureResult != NvmlResult.Success ||
            probe.CurrentTemperatureCelsius is not { } currentTemperature)
        {
            diagnostic = Describe(
                "GPU temperature for thermal limit anchoring",
                probe.TemperatureResult);
            return false;
        }

        // The T.Limit specifications are relative, so the derivation rests on what the live
        // margin is anchored to, and ordering and plausibility cannot detect a uniformly
        // shifted anchor. The interpretation is therefore established per GPU signature, by
        // hand, against hardware.
        //
        // What that signature pins is the *offsets*, which are static device properties. It
        // used to pin the derived temperatures instead, which pinned the anchor with them - and
        // the anchor is not a device property. It is the thermal target the driver is currently
        // enforcing and it follows the Razer performance mode, so pinning it refused a healthy
        // machine for being in a different mode from the one the evidence was collected in.
        //
        // The anchor is bounded rather than matched. The legacy absolute thresholds report a
        // different quantity and are not a corroborator, but the shutdown figure among them is
        // still the temperature the device says it will not survive, and that makes a sound
        // ceiling: BladeControl must never act on a threshold above it.
        if (!GpuThermalLimits.TryFromValidatedSignature(
                _device.Identity.Name,
                currentTemperature,
                margin,
                probe.GpuMax.Celsius!.Value,
                probe.Slowdown.Celsius!.Value,
                probe.Shutdown.Celsius!.Value,
                probe.LegacyShutdown.Result == NvmlResult.Success
                    ? probe.LegacyShutdown.Celsius
                    : null,
                out limits,
                out string? rejection))
        {
            diagnostic = rejection ?? "GPU thermal limits could not be derived.";
            return false;
        }

        diagnostic = limits!.Describe();
        return true;
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
