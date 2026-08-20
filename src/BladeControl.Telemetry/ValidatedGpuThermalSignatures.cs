namespace BladeControl.Telemetry;

/// <summary>
/// A GPU thermal signature: an NVML device identity paired with the absolute limits that the
/// T.Limit derivation was observed to produce for it.
/// </summary>
/// <param name="DeviceName">Exact NVML device name, matched ordinally.</param>
/// <param name="MaxOperatingCelsius">Observed maximum operating temperature.</param>
/// <param name="HardwareSlowdownCelsius">Observed hardware slowdown temperature.</param>
/// <param name="HardwareShutdownCelsius">Observed hardware shutdown temperature.</param>
/// <param name="Evidence">Where and how the signature was established, for the record.</param>
/// <param name="ValidatedInPerformanceMode">
/// The Razer performance mode the evidence was collected in. This is not cosmetic: the anchor
/// the derivation rests on is the driver's current thermal target, and that target follows the
/// performance mode. A signature is only the signature of the mode it was measured in, and
/// saying which one turns an unexplainable refusal into an explicable one.
/// </param>
public sealed record ValidatedGpuThermalSignature(
    string DeviceName,
    double MaxOperatingCelsius,
    double HardwareSlowdownCelsius,
    double HardwareShutdownCelsius,
    string Evidence,
    string ValidatedInPerformanceMode);

/// <summary>
/// The GPU thermal signatures for which the NVML T.Limit interpretation has been checked
/// against real hardware.
/// </summary>
/// <remarks>
/// <para><b>What this actually gates on.</b> Two things, both exact: the NVML device name, and
/// the three absolute limits the derivation produces from that device's T.Limit data. Nothing
/// else. It does not identify the laptop, the chassis, or the firmware — there is no SMBIOS or
/// WMI check here and no machine-model requirement.</para>
/// <para><b>What it therefore validates.</b> The <i>interpretation of the T.Limit data</i>, not
/// a whole machine. The evidence was collected on a Razer Blade 16 (RZ09-0483), but what was
/// established there is how this GPU's relative T.Limit values convert to absolute
/// temperatures. Another chassis carrying the same GPU, whose T.Limit data derives to the same
/// signature, is running the same interpretation this evidence covers — so it qualifies. A
/// machine-model gate would be claiming something the evidence does not support in either
/// direction.</para>
/// <para><b>Why a signature list rather than a rule.</b> The T.Limit specifications are
/// relative, so every derived limit depends on what the live margin is anchored to. NVML
/// documents <c>nvmlDeviceGetMarginTemperature</c> as the distance to the nearest slowdown
/// threshold; on the reference part it is measurably the distance to the maximum operating
/// temperature. Those two readings differ by a uniform 2 C and both produce well-ordered,
/// plausible limit sets, so no amount of internal validation can tell them apart.</para>
/// <para>Three NVML interfaces were checked for an independent absolute witness that could
/// settle it on driver 610.88, and none can:</para>
/// <list type="bullet">
/// <item><description><c>nvmlDeviceGetTemperatureThreshold</c> returns 105 / 97 / 100 for
/// GPU_MAX / SLOWDOWN / SHUTDOWN — not the operating thresholds, and not even ordered as an
/// operating set, since the maximum sits above the shutdown point.</description></item>
/// <item><description><c>nvmlDeviceGetThermalSettings</c> reports the GPU sensor as
/// <c>defaultMin 0 / defaultMax 127</c>, a measurement span rather than a limit.</description></item>
/// <item><description>nvidia-smi's <c>GPU Target Temperature Specification</c> reads 75 C, but
/// it is a separate quantity that coincides here, so it proves nothing.</description></item>
/// </list>
/// <para><b>Failure is closed, and there is no generic Ada fallback.</b> An unrecognised device
/// name, or a recognised one whose derivation no longer reproduces its signature, yields no GPU
/// thermal limits at all. That refuses closed-loop thermal ownership and sends no fan write —
/// it does not fall back to the old fixed 80 C, and it does not lend one GPU's numbers to
/// another. Guessing the anchor wrong in the unsafe direction would put the pre-shutdown
/// handoff above the temperature at which the GPU shuts itself down, disabling the ladder
/// exactly where it is meant to act.</para>
/// <para>Adding a signature means running <c>bladectl telemetry gpu-thermal-probe</c> on that
/// hardware, correlating with <c>nvidia-smi -q -d TEMPERATURE</c> in the same window, and
/// confirming the anchor holds across at least two operating points.</para>
/// </remarks>
public static class ValidatedGpuThermalSignatures
{
    /// <summary>
    /// The RTX 4090 Laptop GPU signature.
    /// </summary>
    /// <remarks>
    /// Evidence collected on a Razer Blade 16 (RZ09-0483), NVIDIA driver 610.88. The anchor was
    /// confirmed at four operating points, each resolving to the same reference temperature:
    /// <code>
    /// core 66 C + margin  9 = 75
    /// core 47 C + margin 28 = 75
    /// core 46 C + margin 29 = 75
    /// core 44 C + margin 31 = 75
    /// </code>
    /// with static specifications GPU_MAX 0, SLOWDOWN -2, SHUTDOWN -5 throughout, matching
    /// nvidia-smi in the same time window.
    ///
    /// <para><b>This signature does not currently qualify, and the reason is now known.</b>
    /// The anchor is a function of the Razer performance mode. Measured on the same machine,
    /// same GPU UUID, same driver, same specifications, switching modes and reading back:</para>
    /// <code>
    /// Balanced                  core 47 + margin 40 = 87   ->  87 / 89 / 92
    /// Silent                    core 47 + margin 28 = 75   ->  75 / 77 / 80
    /// Custom (CPU low, GPU low) core 47 + margin 28 = 75   ->  75 / 77 / 80
    /// Balanced (returned)       core 48 + margin 39 = 87   ->  87 / 89 / 92
    /// </code>
    /// <para>The anchor follows the mode deterministically in both directions. Fan mode does
    /// not affect it - confirmed at 87 with the fan mode read back as Manual. Neither does
    /// temperature within a mode, nor elapsed time: ten idle samples over ninety seconds gave
    /// 87 with no variation, and the four points above gave 75 across a 22 C spread.</para>
    ///
    /// <para>So the numbers here are real, and they are the signature of <i>Silent or Custom</i>.
    /// BladeControl performs thermal control exclusively in Balanced + Manual, where the anchor
    /// is 87. The evidence above was collected while the machine happened to be in a mode the
    /// runtime never operates in, and nvidia-smi corroborated it because it reports the same
    /// driver-side target and was read in the same mode.</para>
    ///
    /// <para>The deeper problem is not the number. An exact-match allowlist pins a value that
    /// the driver is entitled to change, and qualification reads it at start-preflight, which
    /// may run before the runtime has entered the mode it will operate in. Left as is, a
    /// machine in Silent qualifies against 75 and then operates at an 87 target - conservative,
    /// so not dangerous, but not what was qualified. The constant is deliberately unchanged
    /// pending that design decision; see docs/release-notes-v0.1.0.md.</para>
    /// </remarks>
    public static ValidatedGpuThermalSignature Rtx4090Laptop { get; } = new(
        "NVIDIA GeForce RTX 4090 Laptop GPU",
        MaxOperatingCelsius: 75,
        HardwareSlowdownCelsius: 77,
        HardwareShutdownCelsius: 80,
        Evidence:
            "T.Limit anchor confirmed at four operating points against nvidia-smi on a " +
            "Razer Blade 16 RZ09-0483, NVIDIA driver 610.88",
        // Established after the fact by switching modes and reading the anchor back. Both
        // Silent and Custom (CPU low, GPU low) produce this anchor; which of the two the
        // original session ran in was not recorded, and the evidence cannot distinguish them.
        ValidatedInPerformanceMode: "Silent or Custom");

    public static IReadOnlyList<ValidatedGpuThermalSignature> All { get; } = [Rtx4090Laptop];

    /// <summary>
    /// Confirms that a derived limit set matches a signature whose T.Limit interpretation has
    /// been validated.
    /// </summary>
    /// <remarks>
    /// <para>Both halves are load-bearing. The device name alone would accept a same-model GPU
    /// whose vBIOS reports different limits. The derived values alone would accept any device
    /// that happened to land on the same numbers by a different route. Requiring both means a
    /// match says: this is the identity whose T.Limit data was decoded by hand, and it is still
    /// decoding to what was observed.</para>
    /// <para>Exact equality, no tolerance. Both sides are integral, so a disagreement of even
    /// one degree means something changed — a driver revision, a different vBIOS, a shifted
    /// anchor — and that is precisely the case to refuse rather than round away.</para>
    /// </remarks>
    public static bool TryMatch(
        string? deviceName,
        GpuThermalLimits derived,
        out ValidatedGpuThermalSignature? signature,
        out string? rejection)
    {
        ArgumentNullException.ThrowIfNull(derived);
        signature = null;

        if (string.IsNullOrWhiteSpace(deviceName))
        {
            rejection =
                "The GPU did not report a device name, so its thermal limit interpretation " +
                "cannot be matched against a validated signature.";
            return false;
        }

        ValidatedGpuThermalSignature? candidate = All.FirstOrDefault(
            entry => entry.DeviceName.Equals(deviceName, StringComparison.Ordinal));
        if (candidate is null)
        {
            rejection =
                $"GPU \"{deviceName}\" has no validated thermal signature. The relative " +
                "T.Limit specifications cannot be converted to absolute temperatures without " +
                "knowing what the live margin is measured against, and no NVML interface on " +
                "this driver reports that independently.";
            return false;
        }

        if (derived.MaxOperatingCelsius != candidate.MaxOperatingCelsius ||
            derived.HardwareSlowdownCelsius != candidate.HardwareSlowdownCelsius ||
            derived.HardwareShutdownCelsius != candidate.HardwareShutdownCelsius)
        {
            // This message used to end "The device is no longer behaving as it did when its
            // T.Limit data was interpreted." That is usually false. The anchor the derivation
            // rests on is the driver's current thermal target, and it follows the Razer
            // performance mode - Balanced reads one value, Silent and Custom another, on the
            // same GPU, driver and specifications. A perfectly healthy machine sitting in a
            // different mode from the one the signature was measured in lands here, and was
            // being told its hardware had changed. Name the real reason instead.
            rejection =
                $"GPU \"{deviceName}\" derived thermal limits " +
                $"({derived.MaxOperatingCelsius:F0}/{derived.HardwareSlowdownCelsius:F0}/" +
                $"{derived.HardwareShutdownCelsius:F0} C) do not match its validated signature " +
                $"({candidate.MaxOperatingCelsius:F0}/{candidate.HardwareSlowdownCelsius:F0}/" +
                $"{candidate.HardwareShutdownCelsius:F0} C), which was validated in " +
                $"{candidate.ValidatedInPerformanceMode} performance mode. The derived value is " +
                "the driver's current thermal target and follows the active performance mode, " +
                "so a different mode is the likely cause rather than a change in the device. " +
                "Thermal ownership is refused either way: these limits were not qualified.";
            return false;
        }

        signature = candidate;
        rejection = null;
        return true;
    }
}
