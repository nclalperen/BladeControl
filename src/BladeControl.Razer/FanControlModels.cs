namespace BladeControl.Razer;

public readonly struct FanRpm : IEquatable<FanRpm>, IComparable<FanRpm>
{
    public const int MinimumValue = 2000;
    public const int MaximumValue = 5000;
    public const int Increment = 100;

    private readonly int _value;

    public FanRpm(int value)
    {
        if (value < MinimumValue || value > MaximumValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"Fan RPM must be between {MinimumValue} and {MaximumValue}.");
        }

        if (value % Increment != 0)
        {
            throw new ArgumentException(
                $"Fan RPM must be divisible by {Increment}; received {value}.",
                nameof(value));
        }

        _value = value;
    }

    public static FanRpm Minimum => new(MinimumValue);

    public static FanRpm Maximum => new(MaximumValue);

    public int Value => _value;

    internal byte EncodedValue => checked((byte)(_value / Increment));

    internal bool IsValid =>
        _value >= MinimumValue &&
        _value <= MaximumValue &&
        _value % Increment == 0;

    public bool Equals(FanRpm other) => _value == other._value;

    public override bool Equals(object? obj) => obj is FanRpm other && Equals(other);

    public override int GetHashCode() => _value;

    public int CompareTo(FanRpm other) => _value.CompareTo(other._value);

    public override string ToString() => $"{_value} RPM";

    public static bool operator ==(FanRpm left, FanRpm right) => left.Equals(right);

    public static bool operator !=(FanRpm left, FanRpm right) => !left.Equals(right);
}

public enum FanControlProfileKind
{
    Auto,
    Fixed
}

public sealed class FanControlProfile
{
    private FanControlProfile(
        FanControlProfileKind kind,
        FanRpm? fan1Rpm,
        FanRpm? fan2Rpm)
    {
        Kind = kind;
        Fan1Rpm = fan1Rpm;
        Fan2Rpm = fan2Rpm;
    }

    public static FanControlProfile Auto { get; } = new(
        FanControlProfileKind.Auto,
        null,
        null);

    public static FanControlProfile Fixed(FanRpm fan1Rpm, FanRpm fan2Rpm) =>
        fan1Rpm.IsValid && fan2Rpm.IsValid
            ? new(FanControlProfileKind.Fixed, fan1Rpm, fan2Rpm)
            : throw new ArgumentException(
                "Both fixed fan targets must be valid FanRpm values.");

    public FanControlProfileKind Kind { get; }

    public FanRpm? Fan1Rpm { get; }

    public FanRpm? Fan2Rpm { get; }

    public bool IsFixed => Kind == FanControlProfileKind.Fixed;

    public override string ToString() => IsFixed
        ? $"Fixed / Balanced + Manual / Fan 1 {Fan1Rpm} / Fan 2 {Fan2Rpm}"
        : "Auto / Balanced + Auto";
}

public sealed class FanControlState
{
    internal FanControlState(
        PerformanceState performanceState,
        RazerFanReading? latestFan1 = null,
        RazerFanReading? latestFan2 = null,
        IReadOnlyList<RazerExchangeTrace>? observationExchanges = null)
    {
        PerformanceState = performanceState;
        Fan1 = latestFan1 ?? performanceState.Fan1;
        Fan2 = latestFan2 ?? performanceState.Fan2;
        ObservationExchanges = observationExchanges?.ToArray() ?? [];
    }

    public PerformanceState PerformanceState { get; }

    public RazerDeviceInfo Device => PerformanceState.Device;

    public RazerFanReading Fan1 { get; }

    public RazerFanReading Fan2 { get; }

    public RazerModeReading Zone1Mode => PerformanceState.Zone1Mode;

    public RazerModeReading Zone2Mode => PerformanceState.Zone2Mode;

    public RazerCpuPerformanceLevel CpuPerformanceLevel =>
        PerformanceState.CpuPerformanceLevel;

    public RazerGpuPerformanceLevel GpuPerformanceLevel =>
        PerformanceState.GpuPerformanceLevel;

    public IReadOnlyList<RazerExchangeTrace> InitialExchanges =>
        PerformanceState.Exchanges;

    public IReadOnlyList<RazerExchangeTrace> ObservationExchanges { get; }

    public bool ZonesAgree => PerformanceState.ZonesAgree;

    /// <summary>Both zones agree and hold Manual, in whatever performance mode.</summary>
    /// <remarks>
    /// Fan ownership is about the fan mode. The performance mode is the user's choice and is
    /// preserved, so it is not part of what makes a state owned — a machine held in
    /// Silent + Manual is exactly as owned as one in Balanced + Manual.
    /// </remarks>
    public bool IsManual => ZonesAgree && Zone1Mode.FanMode == RazerFanMode.Manual;

    /// <summary>Both zones agree and hold Auto, in whatever performance mode.</summary>
    public bool IsAuto => ZonesAgree && Zone1Mode.FanMode == RazerFanMode.Auto;

    /// <summary>
    /// The performance mode ownership should be taken in, or null when there is not one.
    /// </summary>
    /// <remarks>
    /// The mode the machine is already in, because taking fan ownership must not change the
    /// user's performance choice. Null when the zones disagree or the reported mode is not one
    /// this build knows: in either case there is no single current mode to preserve, and the
    /// caller has to decide what to do rather than being handed a guess.
    /// </remarks>
    public RazerPerformanceMode? OwnershipPerformanceMode =>
        ZonesAgree && Zone1Mode.PerformanceMode.IsKnown
            ? Zone1Mode.PerformanceMode
            : null;

    /// <summary>Firmware owns the fans, in a performance mode we can name.</summary>
    /// <remarks>
    /// This is the success condition for handing the fans back. It used to be
    /// <see cref="IsBalancedAuto"/>, which was equivalent only while every session ran in
    /// Balanced. Now that the mode is preserved, a session recovering from Silent lands in
    /// Silent + Auto — firmware owns the fans, exactly as intended — and testing for Balanced
    /// would have reported that successful handoff as a failed one.
    /// </remarks>
    public bool IsKnownAuto => IsAuto && Zone1Mode.PerformanceMode.IsKnown;

    public bool IsBalancedManual => IsManual &&
        Zone1Mode.PerformanceMode == RazerPerformanceMode.Balanced;

    public bool IsBalancedAuto => IsAuto &&
        Zone1Mode.PerformanceMode == RazerPerformanceMode.Balanced;
}

/// <summary>
/// The write a fan-control plan performs. The performance mode travels on the operation.
/// </summary>
/// <remarks>
/// These were <c>SetBalancedManualZone1</c> and friends, with Balanced baked into the name and
/// into the write. Taking fan ownership therefore moved the machine to Balanced whatever the
/// user had selected. The mode is now carried on the operation so ownership can be taken in the
/// mode the machine is already in.
/// </remarks>
public enum FanControlOperationKind
{
    SetManualZone1,
    SetManualZone2,
    SetFan1Rpm,
    SetFan2Rpm,
    SetAutoZone1,
    SetAutoZone2
}

public sealed class FanControlOperation
{
    internal FanControlOperation(
        FanControlOperationKind kind,
        FanRpm? rpm = null,
        RazerPerformanceMode? performanceMode = null)
    {
        Kind = kind;
        Rpm = rpm;
        PerformanceMode = performanceMode;
    }

    public FanControlOperationKind Kind { get; }

    public FanRpm? Rpm { get; }

    /// <summary>The performance mode this operation writes alongside the fan mode.</summary>
    /// <remarks>Null for the fan-RPM operations, which do not touch the mode pair.</remarks>
    public RazerPerformanceMode? PerformanceMode { get; }

    public string Description => Kind switch
    {
        FanControlOperationKind.SetManualZone1 =>
            $"SET Zone 1 {PerformanceMode} + Manual",
        FanControlOperationKind.SetManualZone2 =>
            $"SET Zone 2 {PerformanceMode} + Manual",
        FanControlOperationKind.SetFan1Rpm => $"SET Fan 1 {Rpm}",
        FanControlOperationKind.SetFan2Rpm => $"SET Fan 2 {Rpm}",
        FanControlOperationKind.SetAutoZone1 =>
            $"SET Zone 1 {PerformanceMode} + Auto",
        FanControlOperationKind.SetAutoZone2 =>
            $"SET Zone 2 {PerformanceMode} + Auto",
        _ => Kind.ToString()
    };
}

public sealed class FanControlPlan
{
    internal FanControlPlan(IReadOnlyList<FanControlOperation> operations)
    {
        Operations = operations.ToArray();
    }

    public IReadOnlyList<FanControlOperation> Operations { get; }

    public bool IsNoOp => Operations.Count == 0;
}

public sealed record FanControlOperationResult(
    FanControlOperation Operation,
    bool Succeeded,
    RazerExchangeTrace? Exchange,
    string? FailureReason);

public sealed record FanControlVerification(bool Succeeded, string Message);

public enum FanControlApplyOutcome
{
    Applied,
    NoChangesRequired,
    Failed,
    AutoRestored,
    AutoRestorationFailed
}

public sealed class FanAutoRecoveryResult
{
    internal FanAutoRecoveryResult(
        bool succeeded,
        IReadOnlyList<FanControlOperationResult> operations,
        FanControlState? finalState,
        string message)
    {
        Succeeded = succeeded;
        Operations = operations.ToArray();
        FinalState = finalState;
        Message = message;
    }

    public bool Succeeded { get; }

    public IReadOnlyList<FanControlOperationResult> Operations { get; }

    public FanControlState? FinalState { get; }

    public string Message { get; }
}

public sealed class FanControlApplyResult
{
    internal FanControlApplyResult(
        FanControlState initialState,
        FanControlProfile requestedProfile,
        FanControlPlan plan,
        IReadOnlyList<FanControlOperationResult> operations,
        FanControlState? finalState,
        IReadOnlyList<RazerExchangeTrace> observationExchanges,
        FanControlVerification verification,
        FanControlApplyOutcome outcome,
        FanAutoRecoveryResult? autoRecovery)
    {
        InitialState = initialState;
        RequestedProfile = requestedProfile;
        Plan = plan;
        Operations = operations.ToArray();
        FinalState = finalState;
        ObservationExchanges = observationExchanges.ToArray();
        Verification = verification;
        Outcome = outcome;
        AutoRecovery = autoRecovery;
    }

    public FanControlState InitialState { get; }

    public FanControlProfile RequestedProfile { get; }

    public FanControlPlan Plan { get; }

    public IReadOnlyList<FanControlOperationResult> Operations { get; }

    public FanControlOperationResult? FailedOperation =>
        Operations.FirstOrDefault(operation => !operation.Succeeded);

    public FanControlState? FinalState { get; }

    public IReadOnlyList<RazerExchangeTrace> ObservationExchanges { get; }

    public FanControlVerification Verification { get; }

    public FanControlApplyOutcome Outcome { get; }

    public FanAutoRecoveryResult? AutoRecovery { get; }

    public bool Succeeded => Outcome is
        FanControlApplyOutcome.Applied or FanControlApplyOutcome.NoChangesRequired;
}

public sealed class FanControlStateException : Exception
{
    internal FanControlStateException(string message)
        : base(message)
    {
    }
}

public sealed class FanControlSelfTestPreconditionException : Exception
{
    internal FanControlSelfTestPreconditionException(
        string message,
        FanControlState initialState)
        : base(message)
    {
        InitialState = initialState;
    }

    public FanControlState InitialState { get; }
}

public sealed class FanControlSelfTestStageResult
{
    internal FanControlSelfTestStageResult(
        string stage,
        bool succeeded,
        string message,
        FanControlState? state,
        IReadOnlyList<RazerExchangeTrace> exchanges,
        FanControlApplyResult? fanApply = null,
        PerformanceApplyResult? performanceApply = null)
    {
        Stage = stage;
        Succeeded = succeeded;
        Message = message;
        State = state;
        Exchanges = exchanges.ToArray();
        FanApply = fanApply;
        PerformanceApply = performanceApply;
    }

    public string Stage { get; }

    public bool Succeeded { get; }

    public string Message { get; }

    public FanControlState? State { get; }

    public IReadOnlyList<RazerExchangeTrace> Exchanges { get; }

    public FanControlApplyResult? FanApply { get; }

    public PerformanceApplyResult? PerformanceApply { get; }
}

public sealed class FanControlSelfTestResult
{
    internal FanControlSelfTestResult(
        FanControlState initialState,
        IReadOnlyList<FanControlSelfTestStageResult> stages,
        bool succeeded,
        string message,
        FanAutoRecoveryResult? autoRecovery = null,
        PerformanceApplyResult? performanceRestoration = null,
        FanControlState? finalState = null)
    {
        InitialState = initialState;
        Stages = stages.ToArray();
        Succeeded = succeeded;
        Message = message;
        AutoRecovery = autoRecovery;
        PerformanceRestoration = performanceRestoration;
        FinalState = finalState;
    }

    public FanControlState InitialState { get; }

    public IReadOnlyList<FanControlSelfTestStageResult> Stages { get; }

    public bool Succeeded { get; }

    public string Message { get; }

    public FanAutoRecoveryResult? AutoRecovery { get; }

    public PerformanceApplyResult? PerformanceRestoration { get; }

    public FanControlState? FinalState { get; }
}
