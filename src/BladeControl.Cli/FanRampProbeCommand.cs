using System.Diagnostics;
using BladeControl.Hardware.Windows;
using BladeControl.Razer;

namespace BladeControl.Cli;

internal static partial class Program
{
    /// <summary>
    /// Decides whether <c>0x0D81</c> is a tachometer or an echo of the last command.
    /// </summary>
    /// <remarks>
    /// <para>Everything shown to a user as a fan speed rests on this. The value has been treated
    /// as "firmware-reported fan state" — deliberately not called RPM — because nothing had
    /// distinguished a real measurement from the commanded target being read back. Sampling it
    /// once after a write cannot tell the two apart: both agree with the command.</para>
    /// <para>A step change can. Fans have inertia, so a physical reading crosses intermediate
    /// values over seconds and overshoots or lags; an echo arrives at the target on the very
    /// next read and sits there. This samples at 250 ms through a large step in both directions,
    /// and through the handover to firmware Auto, where nothing is commanding anything at all
    /// and any variation must come from the controller.</para>
    /// <para>The step is 2000 to 4500 RPM, both inside the validated range, and the machine is
    /// returned to firmware Auto at the end through a <c>finally</c>.</para>
    /// </remarks>
    private static int RunFanRampProbe()
    {
        using DirectHardwareOwnership? ownership = TryAcquireDirectHardwareOwnership();
        if (ownership is null)
        {
            Console.Error.WriteLine(
                "Another BladeControl Runtime host owns the hardware. Stop the " +
                "'BladeControl Runtime' service before running this probe.");
            return 2;
        }

        using WindowsRazerClientSession session = WindowsRazerClientSession.Open();
        RazerClient client = session.Client;
        var stopwatch = Stopwatch.StartNew();

        void Sample(string phase, int count, int intervalMs)
        {
            for (int index = 0; index < count; index++)
            {
                FanControlState state = client.GetFanControlState();
                Console.WriteLine(
                    "{0,-22} t+{1,6:F1}s  fan1 {2,5}  fan2 {3,5}  {4} + {5}",
                    phase,
                    stopwatch.Elapsed.TotalSeconds,
                    state.Fan1.FirmwareReportedRpm,
                    state.Fan2.FirmwareReportedRpm,
                    state.Zone1Mode.PerformanceMode,
                    state.Zone1Mode.FanMode);
                Thread.Sleep(intervalMs);
            }
        }

        Console.WriteLine("BladeControl fan ramp probe");
        Console.WriteLine(
            "Samples 0x0D81 through a 2000 -> 4500 RPM step and back to firmware Auto.");
        Console.WriteLine(
            "A tachometer crosses intermediate values; an echo arrives at the target at once.");
        Console.WriteLine();

        try
        {
            Sample("A as-found", 8, 250);

            client.ApplyFanControlProfile(
                FanControlProfile.Fixed(new FanRpm(2000), new FanRpm(2000)));
            Sample("B manual 2000", 12, 250);

            client.ApplyFanControlProfile(
                FanControlProfile.Fixed(new FanRpm(4500), new FanRpm(4500)));
            Sample("C stepped to 4500", 24, 250);

            client.ApplyFanControlProfile(
                FanControlProfile.Fixed(new FanRpm(2000), new FanRpm(2000)));
            Sample("D stepped to 2000", 20, 250);
        }
        finally
        {
            try
            {
                client.ApplyFanControlProfile(FanControlProfile.Auto);
                Sample("E firmware Auto", 16, 250);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"RESTORATION FAILED: {exception.Message} " +
                    "Run 'BladeControl.Cli fan apply auto' immediately.");
            }
        }

        return 0;
    }
}
