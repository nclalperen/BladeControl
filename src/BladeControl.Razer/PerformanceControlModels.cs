namespace BladeControl.Razer;

public sealed class PerformanceProfile
{
    private PerformanceProfile(
        RazerPerformanceMode performanceMode,
        RazerCpuPerformanceLevel? cpuLevel,
        RazerGpuPerformanceLevel? gpuLevel)
    {
        PerformanceMode = performanceMode;
        CpuLevel = cpuLevel;
        GpuLevel = gpuLevel;
    }

    public static PerformanceProfile Balanced { get; } = new(
        RazerPerformanceMode.Balanced,
        null,
        null);

    public static PerformanceProfile Silent { get; } = new(
        RazerPerformanceMode.Silent,
        null,
        null);

    public static PerformanceProfile Custom(
        RazerCpuPerformanceLevel cpuLevel,
        RazerGpuPerformanceLevel gpuLevel) => new(
            RazerPerformanceMode.Custom,
            cpuLevel,
            gpuLevel);

    public RazerPerformanceMode PerformanceMode { get; }

    public RazerFanMode FanMode => RazerFanMode.Auto;

    public RazerCpuPerformanceLevel? CpuLevel { get; }

    public RazerGpuPerformanceLevel? GpuLevel { get; }

    public bool IsCustom => PerformanceMode == RazerPerformanceMode.Custom;

    public override string ToString() => IsCustom
        ? $"Custom / Auto / CPU {CpuLevel} / GPU {GpuLevel}"
        : $"{PerformanceMode} / Auto";
}

public sealed class PerformanceState
{
    internal PerformanceState(RazerStatusSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    internal RazerStatusSnapshot Snapshot { get; }

    public RazerDeviceInfo Device => Snapshot.Device;

    public RazerFanReading Fan1 => Snapshot.Fan1;

    public RazerFanReading Fan2 => Snapshot.Fan2;

    public RazerModeReading Zone1Mode => Snapshot.Zone1Mode;

    public RazerModeReading Zone2Mode => Snapshot.Zone2Mode;

    public RazerCpuPerformanceLevel CpuPerformanceLevel =>
        Snapshot.CpuPerformanceLevel;

    public RazerGpuPerformanceLevel GpuPerformanceLevel =>
        Snapshot.GpuPerformanceLevel;

    public IReadOnlyList<RazerExchangeTrace> Exchanges => Snapshot.Exchanges;

    public bool ZonesAgree =>
        Zone1Mode.PerformanceMode == Zone2Mode.PerformanceMode &&
        Zone1Mode.FanMode == Zone2Mode.FanMode;
}

public enum PerformanceApplyOperationKind
{
    SetModeZone1,
    SetModeZone2,
    SetCpuLevel,
    SetGpuLevel
}

public sealed class PerformanceApplyOperation
{
    internal PerformanceApplyOperation(
        PerformanceApplyOperationKind kind,
        RazerPerformanceMode? performanceMode = null,
        RazerCpuPerformanceLevel? cpuLevel = null,
        RazerGpuPerformanceLevel? gpuLevel = null)
    {
        Kind = kind;
        PerformanceMode = performanceMode;
        CpuLevel = cpuLevel;
        GpuLevel = gpuLevel;
    }

    public PerformanceApplyOperationKind Kind { get; }

    public string Description => Kind switch
    {
        PerformanceApplyOperationKind.SetModeZone1 =>
            $"SET Zone 1 {PerformanceMode} + Auto",
        PerformanceApplyOperationKind.SetModeZone2 =>
            $"SET Zone 2 {PerformanceMode} + Auto",
        PerformanceApplyOperationKind.SetCpuLevel => $"SET CPU {CpuLevel}",
        PerformanceApplyOperationKind.SetGpuLevel => $"SET GPU {GpuLevel}",
        _ => Kind.ToString()
    };

    internal RazerPerformanceMode? PerformanceMode { get; }

    internal RazerCpuPerformanceLevel? CpuLevel { get; }

    internal RazerGpuPerformanceLevel? GpuLevel { get; }
}

public sealed class PerformanceApplyPlan
{
    internal PerformanceApplyPlan(IReadOnlyList<PerformanceApplyOperation> operations)
    {
        Operations = operations.ToArray();
    }

    public IReadOnlyList<PerformanceApplyOperation> Operations { get; }

    public bool IsNoOp => Operations.Count == 0;
}

public sealed record PerformanceOperationResult(
    PerformanceApplyOperation Operation,
    bool Succeeded,
    RazerExchangeTrace? Exchange,
    string? FailureReason);

public sealed record PerformanceVerificationResult(bool Succeeded, string Message);

public enum PerformanceApplyOutcome
{
    Applied,
    NoChangesRequired,
    Failed,
    Restored,
    RestorationFailed
}

public sealed class PerformanceRestorationResult
{
    internal PerformanceRestorationResult(
        bool succeeded,
        PerformanceApplyPlan? plan,
        IReadOnlyList<PerformanceOperationResult> operations,
        PerformanceState? finalState,
        string message)
    {
        Succeeded = succeeded;
        Plan = plan;
        Operations = operations.ToArray();
        FinalState = finalState;
        Message = message;
    }

    public bool Succeeded { get; }

    public PerformanceApplyPlan? Plan { get; }

    public IReadOnlyList<PerformanceOperationResult> Operations { get; }

    public PerformanceState? FinalState { get; }

    public string Message { get; }
}

public sealed class PerformanceApplyResult
{
    internal PerformanceApplyResult(
        PerformanceState initialState,
        PerformanceProfile requestedProfile,
        PerformanceApplyPlan plan,
        IReadOnlyList<PerformanceOperationResult> operations,
        PerformanceState? finalState,
        PerformanceVerificationResult verification,
        PerformanceApplyOutcome outcome,
        PerformanceRestorationResult? restoration)
    {
        InitialState = initialState;
        RequestedProfile = requestedProfile;
        Plan = plan;
        Operations = operations.ToArray();
        FinalState = finalState;
        Verification = verification;
        Outcome = outcome;
        Restoration = restoration;
    }

    public PerformanceState InitialState { get; }

    public PerformanceProfile RequestedProfile { get; }

    public PerformanceApplyPlan Plan { get; }

    public IReadOnlyList<PerformanceOperationResult> Operations { get; }

    public PerformanceOperationResult? FailedOperation =>
        Operations.FirstOrDefault(operation => !operation.Succeeded);

    public PerformanceState? FinalState { get; }

    public PerformanceState LastVerifiedState =>
        Restoration?.FinalState ?? FinalState ?? InitialState;

    public PerformanceVerificationResult Verification { get; }

    public PerformanceApplyOutcome Outcome { get; }

    public PerformanceRestorationResult? Restoration { get; }

    public bool Succeeded =>
        Outcome is PerformanceApplyOutcome.Applied or
            PerformanceApplyOutcome.NoChangesRequired;
}

public sealed class PerformanceCapabilityException : Exception
{
    internal PerformanceCapabilityException(string message)
        : base(message)
    {
    }
}

public sealed class PerformanceStateException : Exception
{
    internal PerformanceStateException(string message)
        : base(message)
    {
    }
}

public sealed record PerformanceSelfTestStageResult(
    string Stage,
    PerformanceProfile Target,
    PerformanceApplyResult ApplyResult);

public sealed class PerformanceSelfTestResult
{
    internal PerformanceSelfTestResult(
        PerformanceState initialState,
        IReadOnlyList<PerformanceSelfTestStageResult> stages,
        bool succeeded,
        string message)
    {
        InitialState = initialState;
        Stages = stages.ToArray();
        Succeeded = succeeded;
        Message = message;
    }

    public PerformanceState InitialState { get; }

    public IReadOnlyList<PerformanceSelfTestStageResult> Stages { get; }

    public bool Succeeded { get; }

    public string Message { get; }

    public PerformanceState LastVerifiedState =>
        Stages.Count == 0
            ? InitialState
            : Stages[^1].ApplyResult.LastVerifiedState;
}

public sealed class PerformanceSelfTestPreconditionException : Exception
{
    internal PerformanceSelfTestPreconditionException(
        string message,
        PerformanceState initialState)
        : base(message)
    {
        InitialState = initialState;
    }

    public PerformanceState InitialState { get; }
}
