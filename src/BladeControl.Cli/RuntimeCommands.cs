using System.Text.Json;
using BladeControl.Runtime;
using BladeControl.Service;

namespace BladeControl.Cli;

internal static partial class Program
{
    private static DirectHardwareOwnership? TryAcquireDirectHardwareOwnership()
    {
        var gate = new NamedSemaphoreRuntimeOwnershipGate();
        IRuntimeOwnershipLease? lease;
        try
        {
            lease = gate.TryAcquire();
        }
        catch
        {
            gate.Dispose();
            throw;
        }

        if (lease is null)
        {
            // Three refusals that mean different things. Reporting "the runtime owns the
            // hardware" for a privilege problem would send someone to stop a service that is
            // not running; reporting "run elevated" when the service genuinely holds the
            // hardware would send them to do something that will not help.
            RuntimeOwnershipGateAccess access = gate.Access;
            gate.Dispose();
            Console.Error.WriteLine(access switch
            {
                RuntimeOwnershipGateAccess.OwnedByAnotherHost =>
                    "Direct hardware write rejected: another BladeControl host owns the " +
                    "machine-wide hardware session. Stop the 'BladeControl Runtime' service " +
                    "first.",
                RuntimeOwnershipGateAccess.CannotCreate =>
                    "Direct hardware write rejected: hardware ownership cannot be established " +
                    "from this process. Run elevated — the machine-wide ownership gate has to " +
                    "be taken before any device is opened.",
                _ =>
                    "Direct hardware write rejected: BladeControl Runtime already owns the " +
                    "hardware session."
            });
            return null;
        }

        return new DirectHardwareOwnership(gate, lease);
    }

    private static int RunRuntimeCommand(string[] args)
    {
        // stop-thermal ends a running session through the same IPC operation the GUI uses,
        // which performs the safe firmware-Auto handoff and restores the captured performance
        // state. Without a command-line surface the only way to end a session was the GUI, so
        // a headless machine could be diagnosed but not returned to firmware control.
        if (args.Length >= 1 && args[0].Equals("stop-thermal", StringComparison.OrdinalIgnoreCase))
        {
            return RunThermalStopCommand();
        }

        // perf goes through the same IPC operation the interface uses, so a performance mode can
        // be changed while a thermal session is running. "perf apply" cannot: it takes the
        // hardware directly, and the service holds it whenever the runtime is up. Without this
        // there was no command-line way to exercise the one interaction most worth exercising -
        // changing a power ceiling without giving up cooling.
        if (args.Length >= 2 && args[0].Equals("perf", StringComparison.OrdinalIgnoreCase))
        {
            return RunRuntimePerformanceCommand(args[1]);
        }

        bool verbose = args.Length == 2 &&
            args[1].Equals("--verbose", StringComparison.OrdinalIgnoreCase);
        if ((args.Length != 1 && !verbose) ||
            (!args[0].Equals("status", StringComparison.OrdinalIgnoreCase) &&
             !args[0].Equals("doctor", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine(
                "Expected: runtime status [--verbose] OR runtime doctor [--verbose] " +
                "OR runtime stop-thermal OR runtime perf balanced|silent");
            return 2;
        }

        RuntimeIpcOperation operation = args[0].Equals(
            "status",
            StringComparison.OrdinalIgnoreCase)
            ? RuntimeIpcOperation.GetRuntimeStatus
            : RuntimeIpcOperation.GetRuntimeDoctor;
        try
        {
            RuntimeIpcResponse response = RuntimePipeClient.SendAsync(operation)
                .GetAwaiter().GetResult();
            if (!response.Succeeded)
            {
                Console.Error.WriteLine($"Runtime request failed: {response.Error}");
                return 1;
            }

            bool statusCommand = args[0].Equals(
                "status",
                StringComparison.OrdinalIgnoreCase);
            Console.WriteLine(statusCommand
                ? "BladeControl runtime status"
                : "BladeControl runtime doctor");
            if (verbose)
            {
                Console.WriteLine(
                    $"IPC protocol {response.Version}; request {response.RequestId}; " +
                    "verbose rendering does not alter runtime behavior.");
            }

            if (statusCommand)
            {
                PrintRuntimeStatus(ReadRuntimeData<RuntimeStatusDto>(response), verbose, Console.Out);
            }
            else
            {
                Console.WriteLine(JsonSerializer.Serialize(response.Data, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            }

            return 0;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            Console.Error.WriteLine(
                $"BladeControl runtime service is unavailable: {exception.Message}");
            return 1;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            Console.Error.WriteLine(
                $"BladeControl Runtime returned an invalid response: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Ends a running thermal session, handing the fans back to firmware.
    /// </summary>
    /// <remarks>
    /// A thin wrapper over the existing <see cref="RuntimeIpcOperation.StopThermalControl"/>
    /// operation — the same one the GUI issues. It introduces no new protocol and changes no
    /// safety behaviour: the runtime still establishes firmware Auto first and then restores
    /// the captured performance state.
    /// </remarks>
    /// <summary>Applies a performance mode over IPC, session running or not.</summary>
    private static int RunRuntimePerformanceCommand(string mode)
    {
        if (!mode.Equals("balanced", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("silent", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Expected: runtime perf balanced|silent");
            return 2;
        }

        string normalized = char.ToUpperInvariant(mode[0]) + mode[1..].ToLowerInvariant();
        try
        {
            RuntimeIpcResponse response = RuntimePipeClient.SendAsync(
                    RuntimeIpcOperation.ApplyPerformanceProfile,
                    new ApplyPerformanceProfileRequest(normalized, null, null))
                .GetAwaiter().GetResult();
            if (!response.Succeeded)
            {
                Console.Error.WriteLine($"Performance apply failed: {response.Error}");
                return 1;
            }

            Console.WriteLine($"Performance mode {normalized} applied.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            Console.Error.WriteLine(
                $"BladeControl runtime service is unavailable: {exception.Message}");
            return 1;
        }
    }

    private static int RunThermalStopCommand()
    {
        try
        {
            RuntimeIpcResponse response =
                RuntimePipeClient.SendAsync(RuntimeIpcOperation.StopThermalControl)
                    .GetAwaiter().GetResult();
            if (!response.Succeeded)
            {
                Console.Error.WriteLine($"Stop request failed: {response.Error}");
                return 1;
            }

            Console.WriteLine("Thermal control stop requested; firmware Auto handoff performed.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            Console.Error.WriteLine(
                $"BladeControl runtime service is unavailable: {exception.Message}");
            return 1;
        }
    }

    /// <summary>Human-readable age, coarse enough not to imply false precision.</summary>
    private static string FormatAge(TimeSpan age) => age switch
    {
        { TotalSeconds: < 1 } => "under a second",
        { TotalMinutes: < 1 } => $"{age.TotalSeconds:F0} s",
        { TotalHours: < 1 } => $"{age.TotalMinutes:F0} min",
        _ => $"{age.TotalHours:F1} h"
    };

    /// <summary>Renders one bounded statistic, or says plainly that it was not reported.</summary>
    private static string Describe(DurationStatistics? statistics) =>
        statistics is null
            ? "not reported by this runtime"
            : $"latest {statistics.Latest.TotalMilliseconds:F1} ms, " +
                $"p95 {statistics.P95.TotalMilliseconds:F1} ms, " +
                $"p99 {statistics.P99.TotalMilliseconds:F1} ms, " +
                $"max {statistics.Maximum.TotalMilliseconds:F1} ms " +
                $"({statistics.SampleCount} samples)";

    internal static void PrintRuntimeStatus(
        RuntimeStatusDto status,
        bool verbose,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(output);
        // Only a Running session produces current readings. Every other state — Stopped,
        // Faulted, EmergencyHandoff — is showing what was last observed, however long ago.
        //
        // Testing for Stopped alone was actively misleading after an emergency handoff: the
        // runtime had returned the fans to firmware Auto, and the report still announced
        // "Current watchdog observation: Balanced + Manual", which reads as BladeControl
        // still owning the fans. The most important moment to be truthful about ownership is
        // the moment ownership has just changed.
        bool live = status.State.Equals(nameof(RuntimeState.Running), StringComparison.Ordinal);
        string sessionPrefix = live ? "Current session" : "Last session";
        bool stopped = !live;

        output.WriteLine();
        output.WriteLine("Runtime state");
        output.WriteLine($"  Build                  {status.RuntimeBuild ?? "not reported"}");
        output.WriteLine($"  State                  {status.State}");
        output.WriteLine($"  Session ID             {status.SessionId?.ToString() ?? "<none>"}");
        output.WriteLine($"  Profile                {status.CurrentProfile ?? "<none>"}");

        output.WriteLine();
        output.WriteLine(stopped
            ? "Last session telemetry (historical; not a current hardware read)"
            : "Current session telemetry");
        output.WriteLine(
            $"  Health                 " +
            $"{status.TelemetryHealth?.Kind ?? "<unavailable>"}");
        if (verbose && !string.IsNullOrWhiteSpace(status.TelemetryHealth?.Reason))
        {
            output.WriteLine($"  Health reason          {status.TelemetryHealth.Reason}");
        }

        output.WriteLine(
            $"  Last acquisition       " +
            $"{status.LastTelemetryAcquisitionDuration.TotalMilliseconds:F1} ms");
        if (status.LatestAuthoritativeTelemetry is not null)
        {
            output.WriteLine(
                $"  Sample timestamp       {status.LatestAuthoritativeTelemetry.Timestamp:O}");
        }

        output.WriteLine();
        output.WriteLine(stopped
            ? "Last watchdog observation (historical; not current firmware state)"
            : "Current watchdog observation");

        // An ownership observation without a time is an assertion about the present made from
        // an unknown moment. Printing the age lets a reader judge it instead of trusting it.
        if (status.LastRazerWatchdogObservedAt is { } observedAt)
        {
            TimeSpan age = DateTimeOffset.UtcNow - observedAt;
            output.WriteLine(
                $"  Observed               {observedAt.ToLocalTime():HH:mm:ss} " +
                $"({FormatAge(age)} ago)");
        }

        if (status.LastRazerWatchdogState is null)
        {
            output.WriteLine("  Observation            <unavailable>");
        }
        else
        {
            output.WriteLine(
                $"  Zone 1                 " +
                $"{status.LastRazerWatchdogState.Zone1PerformanceMode} + " +
                $"{status.LastRazerWatchdogState.Zone1FanMode}");
            output.WriteLine(
                $"  Zone 2                 " +
                $"{status.LastRazerWatchdogState.Zone2PerformanceMode} + " +
                $"{status.LastRazerWatchdogState.Zone2FanMode}");
        }

        output.WriteLine();
        PrintSchedulerStatistics(status, sessionPrefix, output);

        if (!verbose)
        {
            return;
        }

        // Deliberately outside PrintSchedulerStatistics. These fields describe the runtime's
        // whole lifetime, not one session's scheduler, and they used to be rendered inside
        // that section — behind two early returns that fire when no session has run. A
        // process that crashed, was restarted by the SCM, and recovered orphaned Manual mode
        // has run no session by definition, so the one moment "Last failure" decides whether
        // the machine is safe was the one moment it was hidden. Scheduler history and runtime
        // diagnostics are different subjects; the absence of the first says nothing about the
        // second.
        output.WriteLine();
        output.WriteLine(stopped ? "Last session diagnostics" : "Current session diagnostics");
        output.WriteLine($"  Total events           {status.TotalEventCount}");
        output.WriteLine(
            $"  Retained decisions     {status.RetainedThermalDecisionCount}");
        output.WriteLine($"  Retained trace entries {status.RetainedThermalTraceCount}");
        output.WriteLine($"  Last failure           {status.LastFailureReason ?? "<none>"}");
        output.WriteLine($"  Emergency status       {status.EmergencyStatus ?? "<none>"}");
    }

    /// <summary>
    /// Renders the scheduler block, which is present only once a session has produced cycles.
    /// </summary>
    /// <remarks>
    /// Returning early here ends the scheduler block only. Nothing that describes the runtime
    /// itself may be rendered from inside this method.
    /// </remarks>
    private static void PrintSchedulerStatistics(
        RuntimeStatusDto status,
        string sessionPrefix,
        TextWriter output)
    {
        // A runtime that has never run a session has no scheduler history, and a table of
        // zeros under a "Healthy" heading is absence dressed as measurement — the same defect
        // as rendering an unreported metric as zero. Say there is nothing yet instead.
        if (status.Scheduler.CompletedCycles == 0)
        {
            output.WriteLine("Scheduler statistics");
            output.WriteLine("  No session has run since the runtime started.");
            return;
        }

        output.WriteLine($"{sessionPrefix} scheduler statistics");
        output.WriteLine($"  Health                 {status.SchedulerHealth}");
        output.WriteLine($"  Completed cycles       {status.Scheduler.CompletedCycles}");
        output.WriteLine(
            $"  Latest start-to-start  " +
            $"{status.Scheduler.LatestStartToStart.TotalMilliseconds:F1} ms");
        // A runtime older than these fields sends none of them, and a plain long deserialises
        // to zero. Printing "Slow cycles 0" for a runtime that just reported hundreds of
        // overruns would present an absence as a measurement, so the block is withheld
        // entirely. The nullable statistics object is the marker that distinguishes "none"
        // from "not sent"; a long cannot.
        if (status.Scheduler.CycleExecution is not { } cycleExecution)
        {
            output.WriteLine(
                "  Cycle timing           not reported by this runtime (older than these metrics)");
            return;
        }

        output.WriteLine(
            $"  Cycle execution        " +
            $"latest {status.Scheduler.LatestCycleExecutionDuration.TotalMilliseconds:F1} ms, " +
            $"p95 {cycleExecution.P95.TotalMilliseconds:F1} ms, " +
            $"p99 {cycleExecution.P99.TotalMilliseconds:F1} ms, " +
            $"max {status.Scheduler.MaximumCycleExecutionDuration.TotalMilliseconds:F1} ms");

        // Slow cycles are causes; catch-up cycles are the recovery tail one slow cycle leaves
        // behind. Reported separately because a single counter made a handful of events look
        // like a hundred faults.
        output.WriteLine($"  Slow cycles            {status.Scheduler.SlowCycleCount}");
        output.WriteLine($"  Catch-up cycles        {status.Scheduler.CatchUpCycleCount}");
        output.WriteLine($"  Missed periods         {status.Scheduler.MissedDeadlinePeriods}");
        output.WriteLine(
            $"  Maximum lateness       " +
            $"{status.Scheduler.MaximumDeadlineLateness.TotalMilliseconds:F1} ms");
        output.WriteLine(
            $"  Skipped deadlines      {status.Scheduler.SkippedDeadlines} " +
            "(the loop never skips an iteration)");
        output.WriteLine($"  Telemetry acquisition  {Describe(status.TelemetryAcquisition)}");
        output.WriteLine($"  Actuator duration      {Describe(status.ActuatorDuration)}");
        // Split the acquisition figure by provider. The aggregate says the cycle is tight; only
        // this says which read to go after, which is the question the scheduler limitation in
        // docs/known-limitations.md has been waiting on.
        output.WriteLine($"    of which CPU read    {Describe(status.CpuAcquisition)}");
        output.WriteLine($"    of which GPU read    {Describe(status.GpuAcquisition)}");
        output.WriteLine($"  Watchdog coalesced     {status.WatchdogCoalescedCount}");
    }

    private static T ReadRuntimeData<T>(RuntimeIpcResponse response)
    {
        if (response.Data is T typed)
        {
            return typed;
        }

        if (response.Data is JsonElement element)
        {
            return element.Deserialize<T>() ??
                throw new FormatException($"Runtime IPC data for {typeof(T).Name} was empty.");
        }

        throw new FormatException($"Runtime IPC data was not a {typeof(T).Name} value.");
    }

    private static int RunServiceCommand(string[] args)
    {
        bool verbose = args.Length == 2 &&
            args[1].Equals("--verbose", StringComparison.OrdinalIgnoreCase);
        if ((args.Length != 1 && !verbose) ||
            !args[0].Equals("console", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Expected: service console [--verbose]");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return RuntimeWindowsHost.RunAsync(cancellation.Token, verbose)
                .GetAwaiter().GetResult();
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private sealed class DirectHardwareOwnership : IDisposable
    {
        private IRuntimeOwnershipGate? _gate;
        private IRuntimeOwnershipLease? _lease;

        internal DirectHardwareOwnership(
            IRuntimeOwnershipGate gate,
            IRuntimeOwnershipLease lease)
        {
            _gate = gate;
            _lease = lease;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _lease, null)?.Dispose();
            Interlocked.Exchange(ref _gate, null)?.Dispose();
        }
    }
}
