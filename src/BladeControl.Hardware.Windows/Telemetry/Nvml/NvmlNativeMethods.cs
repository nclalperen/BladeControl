using System.Reflection;
using System.Runtime.InteropServices;

namespace BladeControl.Hardware.Windows.Telemetry.Nvml;

internal enum NvmlResult
{
    Success = 0,
    Uninitialized = 1,
    InvalidArgument = 2,
    NotSupported = 3,
    NoPermission = 4,
    AlreadyInitialized = 5,
    NotFound = 6,
    InsufficientSize = 7,
    InsufficientPower = 8,
    DriverNotLoaded = 9,
    Timeout = 10,
    IrqIssue = 11,
    LibraryNotFound = 12,
    FunctionNotFound = 13,
    CorruptedInforom = 14,
    GpuIsLost = 15,
    ResetRequired = 16,
    OperatingSystem = 17,
    LibRmVersionMismatch = 18,
    InUse = 19,
    Memory = 20,
    NoData = 21,
    VgpuEccNotSupported = 22,
    InsufficientResources = 23,
    FreqNotSupported = 24,

    /// <summary>
    /// NVML_ERROR_ARGUMENT_VERSION_MISMATCH. Returned when a versioned struct's version field
    /// does not match what the driver expects, which is the first thing to suspect when a
    /// versioned call fails.
    /// </summary>
    ArgumentVersionMismatch = 25,
    Deprecated = 26,
    NotReady = 27,
    GpuNotFound = 28,
    InvalidState = 29,
    Unknown = 999,
    EntryPointUnavailable = -1000
}

/// <summary>
/// NVML field identifiers, from the R610 nvml.h.
/// </summary>
/// <remarks>
/// <para>Ada and later report thermal limits through the field-value API as signed T.Limit
/// <b>specifications</b>: offsets from the device maximum operating temperature. They are not
/// absolute temperatures, and none of them is the live margin — that is a separate API,
/// <c>nvmlDeviceGetMarginTemperature</c>.</para>
/// <para>These identifiers are dense and adjacent, so a wrong constant lands on a real but
/// unrelated field instead of failing. An earlier revision of this file used 191/192/194 and
/// called 194 the GPU maximum; 194 is in fact the slowdown specification and 192 is a power
/// limit in milliwatts, so the mistake would have produced plausible-looking numbers with no
/// error reported anywhere. Every constant below is therefore stated with its exact nvml.h
/// name.</para>
/// </remarks>
internal static class NvmlFieldId
{
    /// <summary>NVML_FI_DEV_TEMPERATURE_SHUTDOWN_TLIMIT.</summary>
    internal const uint TemperatureShutdownTLimit = 193;

    /// <summary>NVML_FI_DEV_TEMPERATURE_SLOWDOWN_TLIMIT.</summary>
    internal const uint TemperatureSlowdownTLimit = 194;

    /// <summary>
    /// NVML_FI_DEV_TEMPERATURE_MEM_MAX_TLIMIT. Memory rather than core; unused, and declared
    /// only so the neighbouring identifiers cannot be transcribed off by one unnoticed.
    /// </summary>
    internal const uint TemperatureMemoryMaxTLimit = 195;

    /// <summary>NVML_FI_DEV_TEMPERATURE_GPU_MAX_TLIMIT.</summary>
    internal const uint TemperatureGpuMaxTLimit = 196;

    /// <summary>
    /// NVML_FI_DEV_POWER_REQUESTED_LIMIT — <b>not</b> a thermal field, and reported in
    /// milliwatts. Declared so a test can assert that no thermal identifier collides with it.
    /// </summary>
    internal const uint PowerRequestedLimit = 192;

    /// <summary>
    /// Exactly the fields requested when discovering core thermal limits, in request order.
    /// </summary>
    /// <remarks>
    /// Named so the request itself is testable. The identifiers are otherwise buried in an
    /// array literal inside a P/Invoke wrapper, where a transposition is invisible.
    /// </remarks>
    internal static uint[] CoreThermalLimitRequest =>
    [
        TemperatureShutdownTLimit,
        TemperatureSlowdownTLimit,
        TemperatureGpuMaxTLimit
    ];
}

internal enum NvmlValueType : uint
{
    Double = 0,
    UnsignedInt = 1,
    UnsignedLong = 2,
    UnsignedLongLong = 3,
    SignedLongLong = 4,
    SignedInt = 5,
    UnsignedShort = 6
}

/// <summary>
/// nvmlFieldValue_t, matching the R610 nvml.h declaration.
/// </summary>
/// <remarks>
/// <para>Windows x64 layout, asserted by <c>NvmlInteropLayoutTests</c>:</para>
/// <code>
/// offset  0  unsigned int    fieldId
/// offset  4  unsigned int    scopeId
/// offset  8  long long       timestamp
/// offset 16  long long       latencyUsec
/// offset 24  nvmlValueType_t valueType    (enum, 4 bytes)
/// offset 28  nvmlReturn_t    nvmlReturn   (enum, 4 bytes)
/// offset 32  nvmlValue_t     value        (union, 8 bytes)
/// total  40
/// </code>
/// <para>The union's widest members are eight bytes, so <see cref="Value"/> spans it; narrower
/// members occupy only the low bytes and the remainder carries nothing meaningful. T.Limit
/// specifications are legitimately negative, so a narrow signed member has to be read at its
/// own width and sign-extended — reading all eight bytes would turn -5 into whatever happened
/// to sit next to it.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct NvmlFieldValue
{
    internal uint FieldId;
    internal uint ScopeId;
    internal long Timestamp;
    internal long LatencyUsec;
    internal NvmlValueType ValueType;
    internal NvmlResult Result;
    internal long Value;

    /// <summary>
    /// Reads the union as a temperature in degrees Celsius, at the width the driver declared.
    /// </summary>
    /// <remarks>
    /// Returns false for any value type outside the documented enumeration rather than
    /// guessing a width: an unrecognised type means the payload is not what this code believes
    /// it is, and a wrong temperature is worse than no temperature.
    /// </remarks>
    internal readonly bool TryReadCelsius(out double celsius)
    {
        celsius = ValueType switch
        {
            NvmlValueType.Double => BitConverter.Int64BitsToDouble(Value),

            // siVal: four bytes, signed. Truncating to the low word preserves the sign, which
            // is what carries -2 and -5 through intact.
            NvmlValueType.SignedInt => unchecked((int)Value),
            NvmlValueType.UnsignedInt => unchecked((uint)Value),
            NvmlValueType.UnsignedShort => unchecked((ushort)Value),

            // C "unsigned long" is four bytes in the Windows data model, not eight.
            NvmlValueType.UnsignedLong => unchecked((uint)Value),
            NvmlValueType.UnsignedLongLong => unchecked((ulong)Value),
            NvmlValueType.SignedLongLong => Value,
            _ => double.NaN
        };

        return double.IsFinite(celsius);
    }
}

/// <summary>
/// nvmlMarginTemperature_v1_t: the live thermal margin, documented as the distance to the
/// nearest slowdown threshold.
/// </summary>
/// <remarks>
/// A versioned struct. <see cref="Version"/> must be populated before the call or the driver
/// answers <see cref="NvmlResult.ArgumentVersionMismatch"/>.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct NvmlMarginTemperature
{
    internal uint Version;
    internal int MarginTemperature;
}

/// <summary>
/// nvmlTemperatureThresholds_t. The legacy absolute-temperature interface.
/// </summary>
/// <remarks>
/// <para>NVIDIA no longer treats this as the preferred interface on Ada and later and warns it
/// may be removed. It is used here <b>only</b> as an independent corroboration of the T.Limit
/// derivation at qualification, never as the primary source and never on the telemetry
/// path.</para>
/// <para>It earns its place because it is the one interface that reports these limits as
/// absolute degrees, so it can confirm what the relative specifications are anchored to.
/// Without it, a device whose margin was measured against a different reference would produce
/// a wrong but perfectly well-ordered set of limits.</para>
/// </remarks>
internal enum NvmlTemperatureThreshold : uint
{
    Shutdown = 0,
    Slowdown = 1,
    MemoryMax = 2,
    GpuMax = 3,
    AcousticMinimum = 4,
    AcousticCurrent = 5,
    AcousticMaximum = 6
}

/// <summary>nvmlThermalController_t. Signed: the header defines UNKNOWN as -1.</summary>
internal enum NvmlThermalController
{
    None = 0,
    GpuInternal = 1,
    Adm1032 = 2,
    Adt7461 = 3,
    Max6649 = 4,
    Max1617 = 5,
    Lm99 = 6,
    Lm89 = 7,
    Lm64 = 8,
    G781 = 9,
    Adt7473 = 10,
    SbMax6649 = 11,
    VbiosEvent = 12,
    OperatingSystem = 13,
    NvSysConCanoas = 14,
    NvSysConE551 = 15,
    Max6649R = 16,
    Adt7473S = 17,
    Unknown = -1
}

/// <summary>nvmlThermalTarget_t. Signed: the header defines UNKNOWN as -1.</summary>
internal enum NvmlThermalTarget
{
    None = 0,
    Gpu = 1,
    Memory = 2,
    PowerSupply = 4,
    Board = 8,
    VcdBoard = 9,
    VcdInlet = 10,
    VcdOutlet = 11,
    All = 15,
    Unknown = -1
}

/// <summary>
/// One element of nvmlGpuThermalSettings_t's sensor array.
/// </summary>
/// <remarks>
/// Five 4-byte fields, 20 bytes, no padding on x64:
/// <code>
/// offset  0  nvmlThermalController_t controller
/// offset  4  int                     defaultMinTemp
/// offset  8  int                     defaultMaxTemp
/// offset 12  int                     currentTemp
/// offset 16  nvmlThermalTarget_t     target
/// </code>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct NvmlThermalSensor
{
    internal NvmlThermalController Controller;
    internal int DefaultMinTemp;
    internal int DefaultMaxTemp;
    internal int CurrentTemp;
    internal NvmlThermalTarget Target;
}

/// <summary>
/// nvmlGpuThermalSettings_t.
/// </summary>
/// <remarks>
/// <code>
/// offset  0  unsigned int count
/// offset  4  sensor[NVML_MAX_THERMAL_SENSORS_PER_GPU]   (3 x 20 bytes)
/// total  64
/// </code>
/// <see cref="Sensors"/> must be allocated before the call; the array is marshalled by value
/// into fixed storage, so a null reference here is a marshalling failure rather than an
/// NVML error.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct NvmlGpuThermalSettings
{
    /// <summary>NVML_MAX_THERMAL_SENSORS_PER_GPU.</summary>
    internal const int MaxSensors = 3;

    internal uint Count;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxSensors)]
    internal NvmlThermalSensor[] Sensors;

    internal static NvmlGpuThermalSettings Create() =>
        new() { Sensors = new NvmlThermalSensor[MaxSensors] };
}

internal enum NvmlClockType : uint
{
    Graphics = 0,
    Memory = 2
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlTemperature
{
    internal uint Version;
    internal uint SensorType;
    internal int Temperature;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlUtilization
{
    internal uint Gpu;
    internal uint Memory;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlMemory
{
    internal ulong Total;
    internal ulong Free;
    internal ulong Used;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct NvmlPciInfo
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
    internal string BusIdLegacy;

    internal uint Domain;
    internal uint Bus;
    internal uint Device;
    internal uint PciDeviceId;
    internal uint PciSubSystemId;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    internal string BusId;
}

internal static class NvmlNativeMethods
{
    private const string LibraryName = "nvml.dll";
    private static int _resolverInstalled;

    internal static uint TemperatureVersion => StructVersion<NvmlTemperature>(1);

    internal static uint MarginTemperatureVersion => StructVersion<NvmlMarginTemperature>(1);

    /// <summary>
    /// NVML_STRUCT_VERSION: the struct size in the low bits, the revision in the high byte.
    /// </summary>
    private static uint StructVersion<T>(uint revision)
        where T : struct =>
        checked((uint)Marshal.SizeOf<T>()) | (revision << 24);

    internal static void EnsureResolverInstalled()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(
            typeof(NvmlNativeMethods).Assembly,
            ResolveLibrary);
    }

    [DllImport(LibraryName, EntryPoint = "nvmlInit_v2", ExactSpelling = true)]
    internal static extern NvmlResult Initialize();

    [DllImport(LibraryName, EntryPoint = "nvmlShutdown", ExactSpelling = true)]
    internal static extern NvmlResult Shutdown();

    [DllImport(LibraryName, EntryPoint = "nvmlDeviceGetCount_v2", ExactSpelling = true)]
    internal static extern NvmlResult GetDeviceCount(out uint count);

    [DllImport(LibraryName, EntryPoint = "nvmlDeviceGetHandleByIndex_v2", ExactSpelling = true)]
    internal static extern NvmlResult GetDeviceHandle(uint index, out IntPtr device);

    [DllImport(LibraryName, EntryPoint = "nvmlDeviceGetName", ExactSpelling = true)]
    internal static extern NvmlResult GetDeviceName(
        IntPtr device,
        [Out] byte[] name,
        uint length);

    [DllImport(LibraryName, EntryPoint = "nvmlDeviceGetUUID", ExactSpelling = true)]
    internal static extern NvmlResult GetDeviceUuid(
        IntPtr device,
        [Out] byte[] uuid,
        uint length);

    [DllImport(LibraryName, EntryPoint = "nvmlDeviceGetPciInfo_v3", ExactSpelling = true)]
    internal static extern NvmlResult GetDevicePciInfo(
        IntPtr device,
        out NvmlPciInfo pciInfo);

    [DllImport(LibraryName, EntryPoint = "nvmlDeviceGetTemperatureV", ExactSpelling = true)]
    internal static extern NvmlResult GetTemperatureCurrent(
        IntPtr device,
        ref NvmlTemperature temperature);

    [DllImport(LibraryName, EntryPoint = "nvmlDeviceGetTemperature", ExactSpelling = true)]
    internal static extern NvmlResult GetTemperatureLegacy(
        IntPtr device,
        uint sensorType,
        out uint temperature);

    /// <summary>
    /// nvmlDeviceGetFieldValues. Used only at device qualification: the T.Limit
    /// specifications are per-device constants, so nothing on the telemetry path calls this.
    /// </summary>
    /// <remarks>
    /// The outer return code covers the call as a whole; each element additionally carries its
    /// own <c>nvmlReturn</c>, and a driver can answer the call successfully while refusing an
    /// individual field. Both have to be checked.
    /// </remarks>
    [DllImport(LibraryName, EntryPoint = "nvmlDeviceGetFieldValues", ExactSpelling = true)]
    internal static extern NvmlResult GetFieldValues(
        IntPtr device,
        int valuesCount,
        [In, Out] NvmlFieldValue[] values);

    /// <summary>
    /// nvmlDeviceGetMarginTemperature. Unlike the T.Limit specifications this is a live
    /// quantity, so it is read alongside a temperature sample rather than cached as a constant.
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "nvmlDeviceGetMarginTemperature", ExactSpelling = true)]
    internal static extern NvmlResult GetMarginTemperature(
        IntPtr device,
        ref NvmlMarginTemperature marginTemperature);

    /// <summary>
    /// nvmlDeviceGetTemperatureThreshold. Returns an absolute temperature in degrees Celsius.
    /// </summary>
    /// <remarks>
    /// Qualification-time corroboration only. See <see cref="NvmlTemperatureThreshold"/> for
    /// why a deprecated interface is worth depending on here.
    /// </remarks>
    [DllImport(
        LibraryName,
        EntryPoint = "nvmlDeviceGetTemperatureThreshold",
        ExactSpelling = true)]
    internal static extern NvmlResult GetTemperatureThreshold(
        IntPtr device,
        NvmlTemperatureThreshold thresholdType,
        out uint temperatureCelsius);

    /// <summary>
    /// nvmlDeviceGetThermalSettings. Diagnostic only — nothing in the control path reads it.
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "nvmlDeviceGetThermalSettings", ExactSpelling = true)]
    internal static extern NvmlResult GetThermalSettings(
        IntPtr device,
        uint sensorIndex,
        ref NvmlGpuThermalSettings settings);

    [DllImport(LibraryName, EntryPoint = "nvmlDeviceGetPowerUsage", ExactSpelling = true)]
    internal static extern NvmlResult GetPowerUsage(IntPtr device, out uint milliwatts);

    [DllImport(
        LibraryName,
        EntryPoint = "nvmlDeviceGetPowerManagementLimit",
        ExactSpelling = true)]
    internal static extern NvmlResult GetPowerManagementLimit(
        IntPtr device,
        out uint milliwatts);

    [DllImport(LibraryName, EntryPoint = "nvmlDeviceGetUtilizationRates", ExactSpelling = true)]
    internal static extern NvmlResult GetUtilization(
        IntPtr device,
        out NvmlUtilization utilization);

    [DllImport(LibraryName, EntryPoint = "nvmlDeviceGetClockInfo", ExactSpelling = true)]
    internal static extern NvmlResult GetClockInfo(
        IntPtr device,
        NvmlClockType clockType,
        out uint megahertz);

    [DllImport(LibraryName, EntryPoint = "nvmlDeviceGetMemoryInfo", ExactSpelling = true)]
    internal static extern NvmlResult GetMemoryInfo(
        IntPtr device,
        out NvmlMemory memory);

    private static IntPtr ResolveLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals(LibraryName, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        if (NativeLibrary.TryLoad(
                LibraryName,
                assembly,
                DllImportSearchPath.System32,
                out IntPtr system32Handle))
        {
            return system32Handle;
        }

        string? programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            string nvSmiPath = Path.GetFullPath(Path.Combine(
                programFiles,
                "NVIDIA Corporation",
                "NVSMI",
                LibraryName));
            if (NativeLibrary.TryLoad(nvSmiPath, out IntPtr nvSmiHandle))
            {
                return nvSmiHandle;
            }
        }

        return IntPtr.Zero;
    }
}
