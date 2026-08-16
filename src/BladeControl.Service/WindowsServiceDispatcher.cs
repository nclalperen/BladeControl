using System.Runtime.InteropServices;

namespace BladeControl.Service;

internal sealed class WindowsServiceDispatcher
{
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceAcceptShutdown = 0x00000004;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceControlShutdown = 0x00000005;

    private readonly Func<CancellationToken, Task<int>> _run;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ServiceMainDelegate _serviceMain;
    private readonly ServiceHandlerDelegate _handler;
    private IntPtr _statusHandle;

    private WindowsServiceDispatcher(Func<CancellationToken, Task<int>> run)
    {
        _run = run;
        _serviceMain = ServiceMain;
        _handler = Handler;
    }

    internal static int Run(Func<CancellationToken, Task<int>> run)
    {
        var dispatcher = new WindowsServiceDispatcher(run);
        return dispatcher.Dispatch();
    }

    private int Dispatch()
    {
        ServiceTableEntry[] table =
        [
            new ServiceTableEntry
            {
                ServiceName = RuntimeWindowsHost.ServiceName,
                ServiceMain = _serviceMain
            },
            new ServiceTableEntry()
        ];
        return StartServiceCtrlDispatcherW(table) ? 0 : Marshal.GetLastWin32Error();
    }

    private void ServiceMain(uint argumentCount, IntPtr arguments)
    {
        _ = argumentCount;
        _ = arguments;
        _statusHandle = RegisterServiceCtrlHandlerExW(
            RuntimeWindowsHost.ServiceName,
            _handler,
            IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            return;
        }

        SetStatus(ServiceStartPending, 0, 10_000);
        SetStatus(ServiceRunning, ServiceAcceptStop | ServiceAcceptShutdown, 0);
        int exitCode = 1;
        try
        {
            exitCode = _run(_cancellation.Token).GetAwaiter().GetResult();
        }
        finally
        {
            SetStatus(ServiceStopped, 0, 0, checked((uint)Math.Max(0, exitCode)));
        }
    }

    private uint Handler(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        _ = eventType;
        _ = eventData;
        _ = context;
        if (control is ServiceControlStop or ServiceControlShutdown)
        {
            SetStatus(ServiceStopPending, 0, 30_000);
            _cancellation.Cancel();
        }

        return 0;
    }

    private void SetStatus(
        uint currentState,
        uint controlsAccepted,
        uint waitHint,
        uint win32ExitCode = 0)
    {
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = currentState,
            ControlsAccepted = controlsAccepted,
            Win32ExitCode = win32ExitCode,
            WaitHint = waitHint
        };
        _ = SetServiceStatus(_statusHandle, ref status);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainDelegate(uint argumentCount, IntPtr arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ServiceHandlerDelegate(
        uint control,
        uint eventType,
        IntPtr eventData,
        IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        internal string? ServiceName;

        internal ServiceMainDelegate? ServiceMain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
    }

    [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW",
        ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcherW(
        [In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW",
        ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(
        string serviceName,
        ServiceHandlerDelegate handler,
        IntPtr context);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(
        IntPtr statusHandle,
        ref ServiceStatus serviceStatus);
}
