using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BladeControl.Service;

/// <summary>
/// Adapts the existing runtime host body to the generic-host lifetime used by
/// <c>UseWindowsService</c>.
/// </summary>
/// <remarks>
/// <para>This wrapper owns no hardware and reimplements no controller logic. It does exactly
/// three things: take the machine-wide host singleton before anything opens a device, run the
/// unchanged <see cref="RuntimeWindowsHost.RunAsync"/> body, and translate an SCM stop into
/// the cancellation that body already treats as "begin safe shutdown".</para>
///
/// <para>The safe shutdown path is the one that was hardware validated: cancelling the token
/// unwinds <c>RunAsync</c>, whose <c>finally</c> disposes the IPC dispatcher and then the
/// runtime, which is what performs the bounded final event drain, the single
/// StopThermalControl and the firmware restoration. Nothing here shortcuts that.</para>
/// </remarks>
public sealed class RuntimeBackgroundService : BackgroundService
{
    private readonly Func<CancellationToken, Task<int>> _run;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<RuntimeBackgroundService> _logger;
    private readonly Func<RuntimeHostSingleton> _singletonFactory;

    public RuntimeBackgroundService(
        IHostApplicationLifetime lifetime,
        ILogger<RuntimeBackgroundService> logger,
        Func<CancellationToken, Task<int>>? run = null,
        Func<RuntimeHostSingleton>? singletonFactory = null)
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _run = run ?? (token => RuntimeWindowsHost.RunAsync(token, verbose: false, _logger));
        _singletonFactory = singletonFactory ?? RuntimeHostSingleton.Acquire;
    }

    /// <summary>Exit code the host body produced, for the service's reported status.</summary>
    public int ExitCode { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using RuntimeHostSingleton singleton = _singletonFactory();
        if (!singleton.IsOwner)
        {
            // Another host already owns the hardware. Exiting non-zero is deliberate: it
            // lets the SCM recovery policy treat this as a failure worth retrying, because
            // the usual cause is an old host still shutting down during an upgrade.
            _logger.LogError(
                "Another BladeControl Runtime host already owns the hardware ({Scope}). " +
                "This process will not open any device.",
                singleton.Scope);
            ExitCode = RuntimeHostExitCode.HardwareAlreadyOwned;
            _lifetime.StopApplication();
            return;
        }

        try
        {
            ExitCode = await _run(stoppingToken).ConfigureAwait(false);
            if (ExitCode != 0)
            {
                _logger.LogError(
                    "BladeControl Runtime host exited with code {ExitCode}.",
                    ExitCode);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal SCM stop.
            ExitCode = RuntimeHostExitCode.Success;
        }
        finally
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                // The body returned on its own (initialisation refused, for example) rather
                // than because we were asked to stop. Tell the host so the service reports
                // Stopped instead of sitting there doing nothing.
                _lifetime.StopApplication();
            }
        }
    }
}
