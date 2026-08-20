namespace BladeControl.Telemetry;

/// <summary>The T.Limit specifications a GPU reports: static, relative offsets.</summary>
public readonly record struct GpuThermalSpecifications(
    double MaxOperating,
    double Slowdown,
    double Shutdown);

/// <summary>
/// A GPU thermal signature: an NVML device identity, the static T.Limit offsets it reports, and
/// every anchor those offsets have been observed to be measured from.
/// </summary>
/// <param name="DeviceName">Exact NVML device name, matched ordinally.</param>
/// <param name="MaxOperatingSpecification">The GPU-max T.Limit offset. Static.</param>
/// <param name="SlowdownSpecification">The slowdown T.Limit offset. Static.</param>
/// <param name="ShutdownSpecification">The shutdown T.Limit offset. Static.</param>
/// <param name="Evidence">Where and how the signature was established, for the record.</param>
/// <param name="ValidatedAnchorsCelsius">
/// Every anchor this part has been observed to report, each confirmed against hardware.
/// <para>A bound cannot stand in for this. The anchor moves for two reasons that look identical
/// from inside the derivation: a legitimate performance-mode change (75 in Silent and Custom,
/// 87 in Balanced) and a margin measured against the wrong reference. The second is the failure
/// this mechanism exists to catch, and nothing about its shape gives it away.</para>
/// <para>So the anchors are enumerated. One that has been seen and checked qualifies; anything
/// else fails closed, including a mode or driver policy that may be perfectly legitimate but
/// has not been looked at yet.</para>
/// </param>
public sealed record ValidatedGpuThermalSignature(
    string DeviceName,
    double MaxOperatingSpecification,
    double SlowdownSpecification,
    double ShutdownSpecification,
    IReadOnlyList<double> ValidatedAnchorsCelsius,
    string Evidence);

/// <summary>
/// The GPU thermal signatures for which the NVML T.Limit interpretation has been checked
/// against real hardware.
/// </summary>
/// <remarks>
/// <para><b>What this actually gates on.</b> Three things: the NVML device name, matched
/// exactly; the static T.Limit offsets that device reports, matched exactly; and the anchor
/// those offsets are measured from, which must be one that has been observed and checked on
/// that part. Nothing else. It does not identify the laptop, the chassis, or the firmware —
/// there is no SMBIOS or WMI check here and no machine-model requirement.</para>
/// <para>It gated on the <i>derived</i> limits until the anchor was found to move. Pinning the
/// derived triple pinned the anchor along with it, and the anchor is not a device property: it
/// is the thermal target the driver is currently enforcing, and it follows the Razer
/// performance mode. That refused a healthy machine for being in a different mode from the one
/// the evidence happened to be collected in.</para>
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
    /// <para><b>The anchor is a function of the Razer performance mode.</b>
    /// Measured on the same machine,
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
    /// <para>Both are real. 75 is the signature of Silent and Custom; 87 is the signature of
    /// Balanced. The four original points were collected while the machine happened to be in one
    /// of the first two, and nvidia-smi agreed because it reports the same driver-side target
    /// read in the same mode - two views of one number rather than two witnesses.</para>
    ///
    /// <para>Both are listed, because a session runs in whichever mode the user chose and
    /// preserves it, so either anchor may legitimately be the one in force. Enumerating them
    /// keeps the property that matters: a margin measured against the wrong reference yields
    /// 77, which is in neither list and is still refused.</para>
    /// </remarks>
    public static ValidatedGpuThermalSignature Rtx4090Laptop { get; } = new(
        "NVIDIA GeForce RTX 4090 Laptop GPU",

        // The offsets, not the derived temperatures. These are what every reading on this part
        // has reported, in every performance mode, and they are what the evidence below
        // actually establishes about the T.Limit interpretation.
        MaxOperatingSpecification: 0,
        SlowdownSpecification: -2,
        ShutdownSpecification: -5,

        // Every anchor observed on this part, each confirmed by switching the performance
        // mode and reading it back: 75 in Silent and Custom, 87 in Balanced. Both were checked
        // against nvidia-smi in the same window, and both hold across a range of core
        // temperatures with the margin tracking temperature exactly.
        ValidatedAnchorsCelsius: [75, 87],
        Evidence:
            "T.Limit anchor confirmed at four operating points against nvidia-smi on a " +
            "Razer Blade 16 RZ09-0483, NVIDIA driver 610.88; offsets 0/-2/-5 unchanged " +
            "across Balanced, Silent and Custom");

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
        GpuThermalSpecifications? specifications,
        GpuThermalLimits derived,
        double? hardwareShutdownCelsius,
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

        // What the evidence actually established is the *interpretation* of this GPU's T.Limit
        // data: that the specifications are relative offsets from a live anchor, and which
        // offsets this part reports. Those offsets are static device properties - 0 / -2 / -5
        // on every reading ever taken here, in every performance mode.
        //
        // The anchor is not a device property. It is the thermal target the driver is
        // currently enforcing, and it legitimately follows the Razer performance mode: 87 in
        // Balanced, 75 in Silent and Custom, same GPU, same driver, same offsets. Pinning the
        // derived triple pinned the anchor, so it refused a healthy machine for being in a
        // different mode from the one the evidence happened to be collected in.
        //
        // So the offsets are matched exactly, and the anchor is bounded instead.
        if (specifications is not { } specs ||
            specs.MaxOperating != candidate.MaxOperatingSpecification ||
            specs.Slowdown != candidate.SlowdownSpecification ||
            specs.Shutdown != candidate.ShutdownSpecification)
        {
            rejection =
                $"GPU \"{deviceName}\" reported T.Limit specifications " +
                (specifications is { } observed
                    ? $"({observed.MaxOperating:F0}/{observed.Slowdown:F0}/" +
                        $"{observed.Shutdown:F0} C)"
                    : "(none)") +
                $" that do not match its validated signature " +
                $"({candidate.MaxOperatingSpecification:F0}/" +
                $"{candidate.SlowdownSpecification:F0}/" +
                $"{candidate.ShutdownSpecification:F0} C). These offsets are static device " +
                "properties, so a difference means the T.Limit interpretation established for " +
                "this part no longer describes it. No SET was sent.";
            return false;
        }

        // The safety-critical bound. An anchor that reads too low yields limits that are too
        // low, which makes the ladder act early - conservative, and not dangerous. An anchor
        // that reads too high yields limits above what the hardware will tolerate, and that is
        // the direction that matters, so the derived thresholds are held under the GPU's own
        // stated shutdown temperature.
        if (hardwareShutdownCelsius is { } hardwareShutdown &&
            derived.HardwareShutdownCelsius > hardwareShutdown)
        {
            rejection =
                $"GPU \"{deviceName}\" derived thermal limits " +
                $"({derived.MaxOperatingCelsius:F0}/{derived.HardwareSlowdownCelsius:F0}/" +
                $"{derived.HardwareShutdownCelsius:F0} C) whose shutdown limit is above the " +
                $"{hardwareShutdown:F0} C the device itself reports as its shutdown " +
                "temperature. BladeControl will not act on a threshold the hardware would not " +
                "survive. No SET was sent.";
            return false;
        }

        // The anchor itself must be one that has been observed and checked on this part.
        //
        // Bounds cannot do this job. A margin measured against the slowdown limit rather than
        // the maximum operating temperature produces 77/79/82 — correctly ordered, entirely
        // plausible, comfortably under the hardware shutdown temperature, and two degrees too
        // permissive. It is indistinguishable by shape from a legitimate anchor, which is
        // exactly why these are enumerated rather than bounded.
        //
        // A rejection here is usually not a fault in the device. The anchor is the thermal
        // target the driver is currently enforcing, and it follows the Razer performance mode:
        // 87 in Balanced, 75 in Silent and Custom, on the same GPU with the same offsets. An
        // unfamiliar value may be a mode nobody has checked yet rather than anything wrong, and
        // the message says so instead of announcing that the hardware has changed.
        double anchor = derived.MaxOperatingCelsius + candidate.MaxOperatingSpecification;
        if (!candidate.ValidatedAnchorsCelsius.Any(
                validated => Math.Abs(validated - anchor) < 0.5))
        {
            rejection =
                $"GPU \"{deviceName}\" derived thermal limits " +
                $"({derived.MaxOperatingCelsius:F0}/{derived.HardwareSlowdownCelsius:F0}/" +
                $"{derived.HardwareShutdownCelsius:F0} C) from a thermal anchor of " +
                $"{anchor:F0} C, which is not " +
                "one of the anchors validated for this part (" +
                string.Join(
                    ", ",
                    candidate.ValidatedAnchorsCelsius.Select(value => $"{value:F0} C")) +
                "). The anchor is the thermal target the driver is currently enforcing and it " +
                "varies with performance mode, so an unrecognised value may be a mode that has " +
                "not been checked yet, or a margin measured against the wrong reference — and " +
                "the two are indistinguishable from the derivation alone. No SET was sent.";
            return false;
        }

        signature = candidate;
        rejection = null;
        return true;
    }
}
