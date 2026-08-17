namespace BladeControl.Ipc;

/// <summary>
/// Identity of the local Runtime IPC channel. Both the service (which creates the pipe) and
/// the user interface (which connects to it) read the name from here, so the two ends cannot
/// drift apart.
/// </summary>
public static class RuntimeIpcEndpoint
{
    /// <summary>
    /// Pipe name, without the <c>\\.\pipe\</c> prefix. Versioned: a breaking protocol change
    /// gets a new name so an old client fails to connect rather than misparsing.
    /// </summary>
    public const string PipeName = "BladeControl.Runtime.v1";

    /// <summary>Full Win32 path, used only for diagnostics and documentation.</summary>
    public const string PipePath = @"\\.\pipe\" + PipeName;

    /// <summary>
    /// Protocol message ceiling, mirrored from the runtime dispatcher. Enforced on both ends
    /// so a malformed or hostile peer cannot force unbounded allocation.
    /// </summary>
    public const int MaximumMessageBytes = 64 * 1024;
}
