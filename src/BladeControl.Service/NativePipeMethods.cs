using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BladeControl.Service;

internal static class NativePipeMethods
{
    internal const int ErrorPipeLocal = 229;

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetNamedPipeClientComputerNameW",
        ExactSpelling = true,
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientComputerNameW(
        SafePipeHandle pipe,
        StringBuilder clientComputerName,
        uint clientComputerNameLength);
}
