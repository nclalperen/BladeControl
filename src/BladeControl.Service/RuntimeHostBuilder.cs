using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace BladeControl.Service;

/// <summary>How this process was asked to run.</summary>
public enum RuntimeHostMode
{
    /// <summary>Run under the SCM using the supported Windows service lifetime.</summary>
    WindowsService,

    /// <summary>Run in the foreground for development and diagnostics.</summary>
    Console,

    /// <summary>Same as <see cref="Console"/> plus extra diagnostic output.</summary>
    VerboseConsole,

    /// <summary>Arguments were not understood; print usage and exit non-zero.</summary>
    Usage
}

/// <summary>
/// Chooses the host mode and builds the generic host. Kept separate from
/// <c>Program</c> so mode selection is testable without starting a service.
/// </summary>
public static class RuntimeHostBuilder
{
    /// <summary>
    /// Decides how to run. An explicit switch always wins; otherwise the process asks
    /// Windows whether it was launched by the SCM, which is what lets the installer register
    /// the plain executable path with no arguments.
    /// </summary>
    public static RuntimeHostMode SelectMode(
        IReadOnlyList<string> arguments,
        bool isWindowsService)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 0)
        {
            return isWindowsService ? RuntimeHostMode.WindowsService : RuntimeHostMode.Usage;
        }

        bool console = Matches(arguments[0], "console");
        if (console && arguments.Count == 1)
        {
            return RuntimeHostMode.Console;
        }

        if (console && arguments.Count == 2 && Matches(arguments[1], "--verbose"))
        {
            return RuntimeHostMode.VerboseConsole;
        }

        if (arguments.Count == 1 && Matches(arguments[0], "--service"))
        {
            return RuntimeHostMode.WindowsService;
        }

        return RuntimeHostMode.Usage;
    }

    /// <summary>Convenience overload that queries the real process context.</summary>
    public static RuntimeHostMode SelectMode(IReadOnlyList<string> arguments) =>
        SelectMode(arguments, WindowsServiceHelpers.IsWindowsService());

    /// <summary>
    /// Builds the service host. <paramref name="run"/> is injectable so tests can drive the
    /// lifetime without hardware; production passes null and gets the validated host body.
    /// </summary>
    public static IHost BuildServiceHost(
        Func<CancellationToken, Task<int>>? run = null,
        Func<RuntimeHostSingleton>? singletonFactory = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        builder.Services.Configure<HostOptions>(options =>
        {
            // The safe shutdown path restores firmware state and drains events; it must be
            // allowed to finish. This is well inside the 30 s stop hint the service reports
            // to the SCM, and the SCM's own default is longer still.
            options.ShutdownTimeout = TimeSpan.FromSeconds(25);
            options.BackgroundServiceExceptionBehavior =
                BackgroundServiceExceptionBehavior.StopHost;
        });

        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = RuntimeServiceIdentity.ServiceName;
        });

        // Only a real service logs to the Windows Event Log. Registering the provider
        // unconditionally meant a `dotnet test` run wrote host-lifetime errors into the
        // installed product's event source, so anyone diagnosing their machine saw entries
        // like "Another BladeControl Runtime host already owns the hardware
        // (Local\BladeControl.Test.<guid>)" that had nothing to do with their system.
        if (WindowsServiceHelpers.IsWindowsService())
        {
            builder.Logging.AddEventLog(settings =>
            {
                settings.SourceName = RuntimeServiceIdentity.DisplayName;
            });
        }
        else
        {
            // The generic host registers an EventLog provider by default on Windows, so
            // suppressing our own is not enough — a test run would still write to the
            // machine's event log. Nothing but the real service should.
            RemoveEventLogProviders(builder.Services);
        }

        builder.Services.AddSingleton<RuntimeBackgroundService>(provider =>
            new RuntimeBackgroundService(
                provider.GetRequiredService<IHostApplicationLifetime>(),
                provider.GetRequiredService<ILogger<RuntimeBackgroundService>>(),
                run,
                singletonFactory));
        builder.Services.AddHostedService(provider =>
            provider.GetRequiredService<RuntimeBackgroundService>());

        return builder.Build();
    }

    private static void RemoveEventLogProviders(IServiceCollection services)
    {
        for (int index = services.Count - 1; index >= 0; index--)
        {
            ServiceDescriptor descriptor = services[index];
            if (descriptor.ServiceType == typeof(ILoggerProvider) &&
                descriptor.ImplementationType?.Name.Contains(
                    "EventLog",
                    StringComparison.Ordinal) == true)
            {
                services.RemoveAt(index);
            }
        }
    }

    private static bool Matches(string argument, string expected) =>
        argument.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
