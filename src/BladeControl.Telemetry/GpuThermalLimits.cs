namespace BladeControl.Telemetry;

/// <summary>Where a set of GPU thermal limits came from.</summary>
public enum GpuThermalLimitSource
{
    /// <summary>No trustworthy limits; the device does not qualify for thermal ownership.</summary>
    Unavailable,

    /// <summary>
    /// Derived from the NVML T.Limit specifications anchored by the live margin API — the
    /// Ada-and-later representation, where every specification is a signed offset from the
    /// device maximum operating temperature rather than an absolute temperature.
    /// </summary>
    /// <remarks>
    /// Uncorroborated. Usable for arithmetic and diagnostics, but not sufficient on its own to
    /// qualify a device for thermal ownership — see
    /// <see cref="NvmlTemperatureLimitSpecificationsCorroborated"/>.
    /// </remarks>
    NvmlTemperatureLimitSpecifications,

    /// <summary>
    /// The T.Limit derivation, agreed with absolute temperatures reported independently by the
    /// device.
    /// </summary>
    /// <remarks>
    /// Not currently reachable. No NVML interface on driver 610.88 reports the Ada operating
    /// thresholds absolutely: nvmlDeviceGetTemperatureThreshold gives 105/97/100 and
    /// nvmlDeviceGetThermalSettings gives a 0-127 sensor range. Retained because it is the
    /// right gate the moment such an interface exists, and because the tests around it record
    /// why a shifted anchor is undetectable without one.
    /// </remarks>
    NvmlTemperatureLimitSpecificationsCorroborated,

    /// <summary>
    /// The T.Limit derivation matched to a validated thermal signature — a GPU identity whose
    /// T.Limit interpretation was established by hand against hardware, reproducing the limits
    /// observed there. This is what qualifies a device today.
    /// </summary>
    NvmlTemperatureLimitSpecificationsOnValidatedSignature
}

/// <summary>
/// Absolute GPU core temperatures at which the hardware itself changes behaviour, discovered
/// from the device rather than assumed.
/// </summary>
/// <remarks>
/// <para><b>Why this type exists.</b> BladeControl previously handed control back to firmware
/// at a hard-coded 80 C GPU reading. Live NVML data from the reference RTX 4090 Laptop GPU
/// showed what 80 C actually is on that part:</para>
/// <code>
/// GPU Current Temp                             : 66 C
/// GPU Current T.Limit Temp                     : +9 C   (live margin)
/// GPU Max Operating T.Limit Temp Specification :  0 C   -> 75 C
/// GPU Slowdown T.Limit Temp Specification      : -2 C   -> 77 C
/// GPU Shutdown T.Limit Temp Specification      : -5 C   -> 80 C
/// </code>
/// <para>The three specifications are static offsets read through
/// <c>nvmlDeviceGetFieldValues</c>; the live margin is a separate dynamic quantity from
/// <c>nvmlDeviceGetMarginTemperature</c>. Conflating the two is exactly the mistake this type
/// is shaped to prevent.</para>
/// <para>80 C is the temperature at which the <i>GPU shuts itself down</i>. Using the hardware
/// shutdown point as the ordinary software handoff threshold means never acting until the part
/// is already at its limit — no cooling response, no margin, and a handoff that arrives at the
/// worst possible moment. That is what fired in the field.</para>
/// <para>These limits are per-device and effectively immutable, so they are discovered once at
/// device qualification and cached. Nothing on the 500 ms telemetry path re-reads them.</para>
/// </remarks>
public sealed record GpuThermalLimits
{
    /// <summary>
    /// How far below the hardware shutdown point BladeControl hands off.
    /// </summary>
    /// <remarks>
    /// <b>BladeControl policy, not an NVIDIA specification.</b> NVML reports the shutdown
    /// temperature; it does not report this. Deliberately waiting for the hardware shutdown
    /// point would mean racing the GPU's own protection, so the handoff is placed one degree
    /// early. Named and modelled separately so nothing can mistake it for a device-reported
    /// value.
    /// </remarks>
    public const double PreShutdownPolicyMarginCelsius = 1;

    /// <summary>
    /// How far below the maximum operating temperature the GPU must fall before maximum
    /// cooling is released. BladeControl policy, chosen wide enough not to chatter.
    /// </summary>
    public const double CriticalRecoveryPolicyMarginCelsius = 3;

    /// <summary>Lowest max-operating temperature treated as plausible for a discrete GPU.</summary>
    private const double MinimumPlausibleMaxOperatingCelsius = 40;

    /// <summary>Highest max-operating temperature treated as plausible.</summary>
    private const double MaximumPlausibleMaxOperatingCelsius = 110;

    /// <summary>Largest plausible spread from max operating to hardware shutdown.</summary>
    private const double MaximumPlausibleSpreadCelsius = 30;

    private GpuThermalLimits(
        double maxOperatingCelsius,
        double hardwareSlowdownCelsius,
        double hardwareShutdownCelsius,
        GpuThermalLimitSource source)
    {
        MaxOperatingCelsius = maxOperatingCelsius;
        HardwareSlowdownCelsius = hardwareSlowdownCelsius;
        HardwareShutdownCelsius = hardwareShutdownCelsius;
        Source = source;
    }

    /// <summary>Device maximum operating temperature. Reference part: 75 C.</summary>
    public double MaxOperatingCelsius { get; }

    /// <summary>Temperature at which the hardware begins thermal slowdown. Reference: 77 C.</summary>
    public double HardwareSlowdownCelsius { get; }

    /// <summary>Temperature at which the hardware shuts itself down. Reference: 80 C.</summary>
    public double HardwareShutdownCelsius { get; }

    public GpuThermalLimitSource Source { get; }

    /// <summary>Demand maximum cooling at or above the device maximum operating temperature.</summary>
    public double CriticalCoolingCelsius => MaxOperatingCelsius;

    /// <summary>Release maximum cooling only below this. Reference: 72 C.</summary>
    public double CriticalRecoveryCelsius =>
        MaxOperatingCelsius - CriticalRecoveryPolicyMarginCelsius;

    /// <summary>Hand off if held at or above hardware slowdown. Reference: 77 C.</summary>
    public double SustainedEmergencyCelsius => HardwareSlowdownCelsius;

    /// <summary>Hand off from one sample this close to hardware shutdown. Reference: 79 C.</summary>
    public double ImmediateEmergencyCelsius =>
        HardwareShutdownCelsius - PreShutdownPolicyMarginCelsius;

    /// <summary>
    /// Converts NVML T.Limit specifications into absolute core temperatures.
    /// </summary>
    /// <remarks>
    /// <para>The three specifications are signed offsets from a single reference point, and a
    /// negative offset means <i>hotter</i>. The reference point is the device maximum
    /// operating temperature, which the specifications alone cannot locate — they are all
    /// relative to it. Locating it needs the live margin, which
    /// <c>nvmlDeviceGetMarginTemperature</c> reports as the distance from the current reading
    /// to that same reference:</para>
    /// <code>
    /// reference   = currentTemperature + liveMargin
    /// absolute(s) = reference - s
    /// </code>
    /// <para>On the reference part at 66 C with a live margin of 9: max operating
    /// 66 + 9 - 0 = 75, slowdown 66 + 9 - (-2) = 77, shutdown 66 + 9 - (-5) = 80.</para>
    /// <para>A specification of -5 is <b>not</b> a temperature of minus five degrees, and the
    /// live margin is <b>not</b> one of the specifications. Both confusions produce numbers
    /// that look reasonable and are wrong.</para>
    /// </remarks>
    /// <param name="currentTemperatureCelsius">Core temperature sampled with the margin.</param>
    /// <param name="liveMarginCelsius">
    /// nvmlDeviceGetMarginTemperature, sampled with the temperature. Dynamic, never cached.
    /// </param>
    /// <param name="maxOperatingSpecification">NVML_FI_DEV_TEMPERATURE_GPU_MAX_TLIMIT.</param>
    /// <param name="slowdownSpecification">NVML_FI_DEV_TEMPERATURE_SLOWDOWN_TLIMIT.</param>
    /// <param name="shutdownSpecification">NVML_FI_DEV_TEMPERATURE_SHUTDOWN_TLIMIT.</param>
    /// <returns>False with a diagnostic when the data cannot be trusted.</returns>
    public static bool TryFromTemperatureLimitSpecifications(
        double currentTemperatureCelsius,
        double liveMarginCelsius,
        double maxOperatingSpecification,
        double slowdownSpecification,
        double shutdownSpecification,
        out GpuThermalLimits? limits,
        out string? rejection)
    {
        limits = null;

        double[] inputs =
        [
            currentTemperatureCelsius,
            liveMarginCelsius,
            maxOperatingSpecification,
            slowdownSpecification,
            shutdownSpecification
        ];
        if (Array.Exists(inputs, value => !double.IsFinite(value)))
        {
            rejection = "NVML returned a non-finite thermal limit value.";
            return false;
        }

        double reference = currentTemperatureCelsius + liveMarginCelsius;
        double maxOperating = reference - maxOperatingSpecification;
        double slowdown = reference - slowdownSpecification;
        double shutdown = reference - shutdownSpecification;

        return TryCreate(
            maxOperating,
            slowdown,
            shutdown,
            GpuThermalLimitSource.NvmlTemperatureLimitSpecifications,
            out limits,
            out rejection);
    }

    /// <summary>
    /// Maximum disagreement tolerated between the T.Limit derivation and the absolute
    /// thresholds reported independently.
    /// </summary>
    /// <remarks>
    /// Zero. Both sides are integral static values on every device seen so far — the reference
    /// part reports 75/77/80 through both routes exactly — so any disagreement at all means the
    /// two interfaces are describing different quantities, which is precisely the condition
    /// this check exists to catch. A tolerance here would only widen the hole.
    /// </remarks>
    public const double CorroborationToleranceCelsius = 0;

    /// <summary>
    /// Derives absolute limits from the T.Limit specifications and requires them to agree with
    /// the absolute temperatures reported independently by nvmlDeviceGetTemperatureThreshold.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a second source is necessary.</b> The T.Limit specifications are relative,
    /// so the derivation depends entirely on what the live margin is measured against. The
    /// reference device measures it to the maximum operating temperature; the NVML
    /// documentation describes it as the distance to the nearest slowdown threshold. If a
    /// device followed the documentation literally, the same code would derive:</para>
    /// <code>
    /// temperature 44, margin 33, specs 0 / -2 / -5
    ///   -> max operating 77, slowdown 79, shutdown 82
    /// </code>
    /// <para>Those values are correctly ordered, plausibly spaced, and entirely wrong — every
    /// threshold shifted two degrees hot, which on a part that shuts down at 80 C means the
    /// pre-shutdown handoff would sit at 81 C and never fire. Ordering and plausibility checks
    /// cannot detect a uniform shift; only an independent absolute measurement can.</para>
    /// <para>nvmlDeviceGetTemperatureThreshold is deprecated on Ada and later and NVIDIA warns
    /// it may be removed. That is accepted for v0.1.0: the field API stays the primary source
    /// and this is a qualification-time cross-check. If it disappears, this fails closed —
    /// limits unavailable, thermal ownership refused, no SET sent — which is the correct
    /// outcome for a device whose limit semantics can no longer be established.</para>
    /// </remarks>
    public static bool TryFromCorroboratedNvmlSources(
        double currentTemperatureCelsius,
        double liveMarginCelsius,
        double maxOperatingSpecification,
        double slowdownSpecification,
        double shutdownSpecification,
        double legacyMaxOperatingCelsius,
        double legacySlowdownCelsius,
        double legacyShutdownCelsius,
        out GpuThermalLimits? limits,
        out string? rejection)
    {
        if (!TryFromTemperatureLimitSpecifications(
                currentTemperatureCelsius,
                liveMarginCelsius,
                maxOperatingSpecification,
                slowdownSpecification,
                shutdownSpecification,
                out GpuThermalLimits? derived,
                out rejection))
        {
            limits = null;
            return false;
        }

        limits = null;
        double[] absolutes =
            [legacyMaxOperatingCelsius, legacySlowdownCelsius, legacyShutdownCelsius];
        if (Array.Exists(absolutes, value => !double.IsFinite(value)))
        {
            rejection =
                "The independent absolute GPU thermal thresholds were not finite, so the " +
                "T.Limit derivation could not be corroborated.";
            return false;
        }

        (string Name, double Derived, double Absolute)[] comparisons =
        [
            ("maximum operating", derived!.MaxOperatingCelsius, legacyMaxOperatingCelsius),
            ("hardware slowdown", derived.HardwareSlowdownCelsius, legacySlowdownCelsius),
            ("hardware shutdown", derived.HardwareShutdownCelsius, legacyShutdownCelsius)
        ];

        foreach ((string name, double derivedvalue, double absolute) in comparisons)
        {
            if (Math.Abs(derivedvalue - absolute) > CorroborationToleranceCelsius)
            {
                rejection =
                    $"GPU {name} limit derived from T.Limit specifications " +
                    $"({derivedvalue:F0} C) disagrees with the absolute threshold the device " +
                    $"reports independently ({absolute:F0} C). The T.Limit margin is not " +
                    "anchored where this code assumes, so no limit can be trusted.";
                return false;
            }
        }

        return TryCreate(
            derived.MaxOperatingCelsius,
            derived.HardwareSlowdownCelsius,
            derived.HardwareShutdownCelsius,
            GpuThermalLimitSource.NvmlTemperatureLimitSpecificationsCorroborated,
            out limits,
            out rejection);
    }

    /// <summary>
    /// Derives absolute limits from the T.Limit specifications and requires them to match a
    /// validated thermal signature: an exact GPU identity plus the exact limits that identity
    /// was observed to produce.
    /// </summary>
    /// <remarks>
    /// <para>This is the qualification path in use. The preferred gate would be
    /// <see cref="TryFromCorroboratedNvmlSources"/>, but no NVML interface on the current
    /// driver reports the Ada operating thresholds as absolute temperatures, so there is
    /// nothing to corroborate against — see <see cref="ValidatedGpuThermalSignatures"/> for
    /// what was checked and why each candidate was rejected.</para>
    /// <para>What this validates is the interpretation of a GPU's T.Limit data, not a laptop
    /// model: the match is on NVML device name and derived limits only. A GPU with no
    /// validated signature gets no thermal limits at all, which refuses closed-loop thermal
    /// ownership rather than assuming a threshold. That is a smaller product than generalising
    /// the observed anchor to every Ada GPU, and a far smaller failure if the generalisation
    /// were wrong.</para>
    /// </remarks>
    public static bool TryFromValidatedSignature(
        string? deviceName,
        double currentTemperatureCelsius,
        double liveMarginCelsius,
        double maxOperatingSpecification,
        double slowdownSpecification,
        double shutdownSpecification,
        double? hardwareShutdownCelsius,
        out GpuThermalLimits? limits,
        out string? rejection)
    {
        if (!TryFromTemperatureLimitSpecifications(
                currentTemperatureCelsius,
                liveMarginCelsius,
                maxOperatingSpecification,
                slowdownSpecification,
                shutdownSpecification,
                out GpuThermalLimits? derived,
                out rejection))
        {
            limits = null;
            return false;
        }

        if (!ValidatedGpuThermalSignatures.TryMatch(
                deviceName,
                new GpuThermalSpecifications(
                    maxOperatingSpecification,
                    slowdownSpecification,
                    shutdownSpecification),
                derived!,
                hardwareShutdownCelsius,
                out ValidatedGpuThermalSignature? _,
                out rejection))
        {
            limits = null;
            return false;
        }

        return TryCreate(
            derived!.MaxOperatingCelsius,
            derived.HardwareSlowdownCelsius,
            derived.HardwareShutdownCelsius,
            GpuThermalLimitSource.NvmlTemperatureLimitSpecificationsOnValidatedSignature,
            out limits,
            out rejection);
    }

    /// <summary>
    /// Validates an absolute limit set. Exposed so tests and diagnostics can build one without
    /// going through the margin arithmetic.
    /// </summary>
    public static bool TryCreate(
        double maxOperatingCelsius,
        double hardwareSlowdownCelsius,
        double hardwareShutdownCelsius,
        GpuThermalLimitSource source,
        out GpuThermalLimits? limits,
        out string? rejection)
    {
        limits = null;

        if (source == GpuThermalLimitSource.Unavailable)
        {
            rejection = "No GPU thermal limit source was available.";
            return false;
        }

        if (!double.IsFinite(maxOperatingCelsius) ||
            !double.IsFinite(hardwareSlowdownCelsius) ||
            !double.IsFinite(hardwareShutdownCelsius))
        {
            rejection = "A derived GPU thermal limit was not a finite temperature.";
            return false;
        }

        // Ordering is the strongest signal that the data means what we think it means. A GPU
        // whose slowdown sits below its maximum operating temperature, or whose shutdown is
        // not above slowdown, is telling us we have misread the encoding — and guessing from
        // there would be worse than refusing.
        if (!(maxOperatingCelsius <= hardwareSlowdownCelsius) ||
            !(hardwareSlowdownCelsius < hardwareShutdownCelsius))
        {
            rejection =
                $"GPU thermal limits are not ordered (max operating {maxOperatingCelsius:F0} C, " +
                $"slowdown {hardwareSlowdownCelsius:F0} C, shutdown {hardwareShutdownCelsius:F0} C).";
            return false;
        }

        if (maxOperatingCelsius < MinimumPlausibleMaxOperatingCelsius ||
            maxOperatingCelsius > MaximumPlausibleMaxOperatingCelsius)
        {
            rejection =
                $"GPU maximum operating temperature {maxOperatingCelsius:F0} C is outside the " +
                $"plausible {MinimumPlausibleMaxOperatingCelsius:F0}-" +
                $"{MaximumPlausibleMaxOperatingCelsius:F0} C band.";
            return false;
        }

        if (hardwareShutdownCelsius - maxOperatingCelsius > MaximumPlausibleSpreadCelsius)
        {
            rejection =
                $"GPU limit spread {hardwareShutdownCelsius - maxOperatingCelsius:F0} C exceeds " +
                $"the plausible {MaximumPlausibleSpreadCelsius:F0} C.";
            return false;
        }

        // The pre-shutdown handoff must still sit above the cooling threshold, or the ladder
        // would collapse into a single stage.
        if (hardwareShutdownCelsius - PreShutdownPolicyMarginCelsius <= maxOperatingCelsius)
        {
            rejection =
                "GPU limits leave no room between maximum operating temperature and the " +
                "pre-shutdown handoff margin.";
            return false;
        }

        limits = new GpuThermalLimits(
            maxOperatingCelsius,
            hardwareSlowdownCelsius,
            hardwareShutdownCelsius,
            source);
        rejection = null;
        return true;
    }

    /// <summary>Human-readable summary for diagnostics, with provenance.</summary>
    public string Describe() =>
        $"max operating {MaxOperatingCelsius:F0} C, hardware slowdown " +
        $"{HardwareSlowdownCelsius:F0} C, hardware shutdown {HardwareShutdownCelsius:F0} C " +
        $"(source: {DescribeSource()})";

    public string DescribeSource() => Source switch
    {
        GpuThermalLimitSource.NvmlTemperatureLimitSpecificationsOnValidatedSignature =>
            "NVML device thermal limits (T.Limit specifications matched to a validated " +
            "thermal signature)",
        GpuThermalLimitSource.NvmlTemperatureLimitSpecificationsCorroborated =>
            "NVML device thermal limits (T.Limit specifications corroborated by an " +
            "independent absolute source)",
        GpuThermalLimitSource.NvmlTemperatureLimitSpecifications =>
            "NVML device thermal limits (uncorroborated)",
        _ => "unavailable"
    };
}

/// <summary>Graded GPU thermal condition, mirroring the CPU ladder.</summary>
public enum GpuThermalSeverity
{
    /// <summary>Below the maximum operating temperature; the curve governs.</summary>
    Normal,

    /// <summary>At or above maximum operating: demand maximum cooling, keep control.</summary>
    CriticalCooling,

    /// <summary>At or above hardware slowdown: hand off if it persists.</summary>
    SustainedEmergency,

    /// <summary>Within the policy margin of hardware shutdown: hand off now.</summary>
    ImmediateEmergency
}
