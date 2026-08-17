namespace BladeControl.Service;

/// <summary>
/// The one place that names the Windows service. The installer reads these values through
/// build properties and the host reads them directly, so the SCM registration and the
/// running process can never disagree about who they are.
/// </summary>
public static class RuntimeServiceIdentity
{
    /// <summary>SCM key name. Stable across versions; never localise or reformat it.</summary>
    public const string ServiceName = "BladeControl.Runtime";

    /// <summary>Name shown in services.msc and Task Manager.</summary>
    public const string DisplayName = "BladeControl Runtime";

    public const string Description =
        "Owns Razer Blade fan, performance and thermal hardware access for BladeControl, " +
        "and serves the local typed IPC channel used by the BladeControl user interface.";

    /// <summary>
    /// Machine-wide singleton for the runtime host process. This is deliberately separate
    /// from the Manual-control ownership gate in BladeControl.Runtime: that gate guards
    /// entry into Manual fan mode and is session-scoped, whereas this guards the far
    /// coarser invariant that only one process at a time may own the hardware at all.
    /// The Global prefix is what makes it visible across the service's session 0 and an
    /// interactive developer session.
    /// </summary>
    public const string HostSingletonName = @"Global\BladeControl.Runtime.Host";
}
