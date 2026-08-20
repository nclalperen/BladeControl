using BladeControl.Hardware.Windows;
using BladeControl.Razer;

namespace BladeControl.Cli;

internal static partial class Program
{
    /// <summary>
    /// Answers whether the controller honours a manual fan target outside Balanced.
    /// </summary>
    /// <remarks>
    /// <para>Thermal Control V1 takes fan ownership by writing <c>Balanced + Manual</c>, and
    /// forces Balanced whether or not that is the mode the user chose. Supporting Silent and
    /// Custom means writing those pairs instead, and nothing was known about whether the
    /// controller accepts them or quietly reverts to the mode's own fan curve.</para>
    /// <para>The sequence writes a pair, reads it back, writes a fan target, reads that back,
    /// waits, and reads once more — because a pair that is accepted and then silently reverted
    /// a second later is the failure mode that matters. Restoration to Balanced + Auto runs in
    /// a finally block, so an exception midway still hands the fans back to firmware.</para>
    /// </remarks>
    /// <summary>
    /// Writes Balanced + Auto directly, from any state.
    /// </summary>
    /// <remarks>
    /// Fan Control V1's own apply path refuses to act when the machine is in a Manual pair it
    /// does not model, which is precisely the state the probe can leave behind. This uses the
    /// mode-pair primitive instead, which every guard still validates, so there is always a way
    /// back to firmware ownership that does not depend on the state being one V1 recognises.
    /// </remarks>
    private static int RunModeRestore()
    {
        using WindowsRazerClientSession session = WindowsRazerClientSession.Open();
        RazerClient client = session.Client;
        Console.WriteLine("  " + client.ReadModeAndFans("before"));
        Console.WriteLine("  " + client.WriteModePairAndReadBack(
            "after Balanced + Auto",
            RazerPerformanceMode.Balanced,
            RazerFanMode.Auto));
        return 0;
    }

    private static int RunModeOwnershipProbe()
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
        var steps = new List<ModeProbeStep>();
        Console.WriteLine("BladeControl mode-ownership probe");
        Console.WriteLine(
            "Writes performance + fan mode pairs that Thermal Control V1 never writes, and");
        Console.WriteLine(
            "reads each one back. Restores Balanced + Auto before returning.");
        Console.WriteLine();

        try
        {
            steps.Add(client.ReadModeAndFans("baseline (as found)"));

            // A distinct target per mode, deliberately. The first run of this probe wrote the
            // same 3800 into every mode while the machine already happened to be reporting
            // 3800, so "the target held" was indistinguishable from "nothing happened". Each
            // mode now has to move the target to its own value and keep it.
            foreach ((RazerPerformanceMode mode, string label, int rpm) in new[]
            {
                (RazerPerformanceMode.Balanced, "Balanced", 3200),
                (RazerPerformanceMode.Silent, "Silent", 3500),
                (RazerPerformanceMode.Custom, "Custom", 4100)
            })
            {
                steps.Add(client.WriteModePairAndReadBack(
                    $"{label} + Manual, just written",
                    mode,
                    RazerFanMode.Manual));

                steps.Add(client.WriteFanTargetAndReadBack(
                    $"{label} + Manual, target {rpm}",
                    new FanRpm(rpm)));

                // The question is not whether the write is echoed but whether it survives -
                // a mode whose own curve reclaims the fans would show it here.
                Thread.Sleep(4000);
                steps.Add(client.ReadModeAndFans($"{label} + Manual, 4 s later"));
            }
        }
        finally
        {
            try
            {
                // Deliberately the mode-pair primitive, not ApplyFanControlProfile. V1's apply
                // path refuses to act from a Manual pair it does not model, which is exactly
                // the state this probe creates - the first run left the machine in
                // Custom + Manual with its restoration refused.
                steps.Add(client.WriteModePairAndReadBack(
                    "restored",
                    RazerPerformanceMode.Balanced,
                    RazerFanMode.Auto));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"RESTORATION FAILED: {exception.Message} " +
                    "Run 'BladeControl.Cli fan apply auto' immediately.");
            }
        }

        foreach (ModeProbeStep step in steps)
        {
            Console.WriteLine("  " + step);
        }

        return 0;
    }
}
