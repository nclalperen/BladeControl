namespace BladeControl.Service;

/// <summary>
/// Process exit codes reported to the Service Control Manager.
/// </summary>
/// <remarks>
/// <para>The distinction that matters: these describe the <b>host process</b>, not the thermal
/// session. A deliberate emergency handoff to firmware is the safety system working correctly
/// and leaves the process running and serving IPC, so it never produces a non-zero code. Only
/// a host that could not start, or could not keep running, is a failure.</para>
/// <para>Getting this wrong has teeth: the SCM recovery policy restarts the service on a
/// non-zero exit, so classifying a session state as process failure would turn a safe handoff
/// into a restart loop that repeatedly grabs and releases the hardware.</para>
/// </remarks>
public static class RuntimeHostExitCode
{
    /// <summary>Intentional shutdown: the SCM asked us to stop and safe shutdown completed.</summary>
    public const int Success = 0;

    /// <summary>The host could not start, or stopped because of a fatal host condition.</summary>
    public const int HostFailure = 1;

    /// <summary>Command line was not understood.</summary>
    public const int UsageError = 2;

    /// <summary>
    /// Another process already owns the hardware. Non-zero on purpose: the usual cause is an
    /// old host still shutting down during an upgrade, which the recovery policy should retry.
    /// </summary>
    public const int HardwareAlreadyOwned = 3;
}
