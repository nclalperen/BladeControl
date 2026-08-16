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
    Unknown = 999,
    EntryPointUnavailable = -1000
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

    internal static uint TemperatureVersion =>
        checked((uint)Marshal.SizeOf<NvmlTemperature>()) | (1U << 24);

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

    [DllImport(LibraryName, EntryPoint = "nvmlDeviceGetPowerUsage", ExactSpelling = true)]
    internal static extern NvmlResult GetPowerUsage(IntPtr device, out uint milliwatts);

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
