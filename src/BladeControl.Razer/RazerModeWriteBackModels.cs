namespace BladeControl.Razer;

public sealed class RazerModeWriteBackResult
{
    internal RazerModeWriteBackResult(
        RazerStatusSnapshot preWriteState,
        RazerExchangeTrace zone1WriteExchange,
        RazerExchangeTrace zone2WriteExchange,
        RazerStatusSnapshot postWriteState)
    {
        PreWriteState = preWriteState;
        Zone1WriteExchange = zone1WriteExchange;
        Zone2WriteExchange = zone2WriteExchange;
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
            zone1WriteExchange,
            zone2WriteExchange,
            .. postWriteState.Exchanges
        ];
    }

    public RazerStatusSnapshot PreWriteState { get; }

    public RazerExchangeTrace Zone1WriteExchange { get; }

    public RazerExchangeTrace Zone2WriteExchange { get; }

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

public sealed class RazerModeWriteBackPreconditionException : Exception
{
    internal RazerModeWriteBackPreconditionException(
        string message,
        RazerStatusSnapshot preWriteState)
        : base(message)
    {
        PreWriteState = preWriteState;
    }

    public RazerStatusSnapshot PreWriteState { get; }
}

public sealed class RazerModeWriteBackValidationException : Exception
{
    internal RazerModeWriteBackValidationException(
        string stage,
        RazerStatusSnapshot preWriteState,
        IReadOnlyList<RazerExchangeTrace> writeExchanges,
        RazerProtocolException innerException)
        : base(
            $"{stage} write-back response validation failed. " +
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
