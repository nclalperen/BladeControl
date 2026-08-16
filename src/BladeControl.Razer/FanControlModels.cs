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

    public bool IsBalancedManual => ZonesAgree &&
        Zone1Mode.PerformanceMode == RazerPerformanceMode.Balanced &&
        Zone1Mode.FanMode == RazerFanMode.Manual;

    public bool IsBalancedAuto => ZonesAgree &&
        Zone1Mode.PerformanceMode == RazerPerformanceMode.Balanced &&
        Zone1Mode.FanMode == RazerFanMode.Auto;
}

public enum FanControlOperationKind
{
    SetBalancedManualZone1,
    SetBalancedManualZone2,
    SetFan1Rpm,
    SetFan2Rpm,
    SetBalancedAutoZone1,
    SetBalancedAutoZone2
}

public sealed class FanControlOperation
{
    internal FanControlOperation(FanControlOperationKind kind, FanRpm? rpm = null)
    {
        Kind = kind;
        Rpm = rpm;
    }

    public FanControlOperationKind Kind { get; }

    public FanRpm? Rpm { get; }

    public string Description => Kind switch
    {
        FanControlOperationKind.SetBalancedManualZone1 =>
            "SET Zone 1 Balanced + Manual",
        FanControlOperationKind.SetBalancedManualZone2 =>
            "SET Zone 2 Balanced + Manual",
        FanControlOperationKind.SetFan1Rpm => $"SET Fan 1 {Rpm}",
        FanControlOperationKind.SetFan2Rpm => $"SET Fan 2 {Rpm}",
        FanControlOperationKind.SetBalancedAutoZone1 =>
            "SET Zone 1 Balanced + Auto",
        FanControlOperationKind.SetBalancedAutoZone2 =>
            "SET Zone 2 Balanced + Auto",
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
