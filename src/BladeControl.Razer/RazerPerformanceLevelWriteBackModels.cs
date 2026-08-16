namespace BladeControl.Razer;

public sealed class RazerPerformanceLevelWriteBackResult
{
    internal RazerPerformanceLevelWriteBackResult(
        RazerStatusSnapshot preWriteState,
        RazerExchangeTrace cpuWriteExchange,
        RazerExchangeTrace gpuWriteExchange,
        RazerStatusSnapshot postWriteState)
    {
        PreWriteState = preWriteState;
        CpuWriteExchange = cpuWriteExchange;
        GpuWriteExchange = gpuWriteExchange;
        PostWriteState = postWriteState;

        PerformanceUnchanged =
            preWriteState.Zone1Mode.PerformanceMode == postWriteState.Zone1Mode.PerformanceMode &&
            preWriteState.Zone2Mode.PerformanceMode == postWriteState.Zone2Mode.PerformanceMode;
        FanModeUnchanged =
            preWriteState.Zone1Mode.FanMode == postWriteState.Zone1Mode.FanMode &&
            preWriteState.Zone2Mode.FanMode == postWriteState.Zone2Mode.FanMode;
        CpuPerformanceLevelUnchanged =
            preWriteState.CpuPerformanceLevel.Value ==
            postWriteState.CpuPerformanceLevel.Value;
        GpuPerformanceLevelUnchanged =
            preWriteState.GpuPerformanceLevel.Value ==
            postWriteState.GpuPerformanceLevel.Value;

        Exchanges =
        [
            .. preWriteState.Exchanges,
            cpuWriteExchange,
            gpuWriteExchange,
            .. postWriteState.Exchanges
        ];
    }

    public RazerStatusSnapshot PreWriteState { get; }

    public RazerExchangeTrace CpuWriteExchange { get; }

    public RazerExchangeTrace GpuWriteExchange { get; }

    public RazerStatusSnapshot PostWriteState { get; }

    public bool PerformanceUnchanged { get; }

    public bool FanModeUnchanged { get; }

    public bool CpuPerformanceLevelUnchanged { get; }

    public bool GpuPerformanceLevelUnchanged { get; }

    public bool StateDriftDetected =>
        !PerformanceUnchanged ||
        !FanModeUnchanged ||
        !CpuPerformanceLevelUnchanged ||
        !GpuPerformanceLevelUnchanged;

    public bool Passed => !StateDriftDetected;

    public IReadOnlyList<RazerExchangeTrace> Exchanges { get; }
}

public sealed class RazerPerformanceLevelWriteBackPreconditionException : Exception
{
    internal RazerPerformanceLevelWriteBackPreconditionException(
        string message,
        RazerStatusSnapshot preWriteState)
        : base(message)
    {
        PreWriteState = preWriteState;
    }

    public RazerStatusSnapshot PreWriteState { get; }
}

public sealed class RazerPerformanceLevelWriteBackValidationException : Exception
{
    internal RazerPerformanceLevelWriteBackValidationException(
        string stage,
        RazerStatusSnapshot preWriteState,
        IReadOnlyList<RazerExchangeTrace> writeExchanges,
        RazerProtocolException innerException)
        : base(
            $"{stage} performance-level write-back response validation failed. " +
            "No further write, retry, or rollback was attempted.",
            innerException)
    {
        Stage = stage;
        PreWriteState = preWriteState;
        WriteExchanges = writeExchanges.ToArray();
    }

    public string Stage { get; }

    public RazerStatusSnapshot PreWriteState { get; }

    public IReadOnlyList<RazerExchangeTrace> WriteExchanges { get; }
}
