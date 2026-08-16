using BladeControl.Razer;
using BladeControl.Telemetry;
using BladeControl.Thermal;
using System.Text.Json.Serialization;

namespace BladeControl.Runtime;

public enum RuntimeEventKind
{
    TelemetrySample,
    ThermalDecision,
    FanTargetChanged,
    RazerWatchdogCheck,
    SchedulerOverrun,
    EmergencyHandoff,
    OwnershipLost,
    SessionStarted,
    SessionStopped,
    RecoveryAttempt,
    RecoveryResult,
    ProtocolExchange
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "eventType")]
[JsonDerivedType(typeof(TelemetrySampleEvent), "TelemetrySample")]
[JsonDerivedType(typeof(ThermalDecisionEvent), "ThermalDecision")]
[JsonDerivedType(typeof(FanTargetChangedEvent), "FanTargetChanged")]
[JsonDerivedType(typeof(RazerWatchdogCheckEvent), "RazerWatchdogCheck")]
[JsonDerivedType(typeof(SchedulerOverrunEvent), "SchedulerOverrun")]
[JsonDerivedType(typeof(EmergencyHandoffEvent), "EmergencyHandoff")]
[JsonDerivedType(typeof(OwnershipLostEvent), "OwnershipLost")]
[JsonDerivedType(typeof(SessionStartedEvent), "SessionStarted")]
[JsonDerivedType(typeof(SessionStoppedEvent), "SessionStopped")]
[JsonDerivedType(typeof(RecoveryAttemptEvent), "RecoveryAttempt")]
[JsonDerivedType(typeof(RecoveryResultEvent), "RecoveryResult")]
[JsonDerivedType(typeof(ProtocolExchangeEvent), "ProtocolExchange")]
public abstract record RuntimeEvent(
    RuntimeEventKind Kind,
    long Sequence,
    DateTimeOffset Timestamp,
    string Message);

public sealed record TelemetrySampleEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string Message,
    ThermalTelemetrySample Sample,
    TimeSpan AcquisitionDuration)
    : RuntimeEvent(RuntimeEventKind.TelemetrySample, Sequence, Timestamp, Message);

public sealed record ThermalDecisionEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string Message,
    ThermalDecision Decision)
    : RuntimeEvent(RuntimeEventKind.ThermalDecision, Sequence, Timestamp, Message);

public sealed record FanTargetChangedEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string Message,
    int TargetRpm)
    : RuntimeEvent(RuntimeEventKind.FanTargetChanged, Sequence, Timestamp, Message);

public sealed record RazerWatchdogCheckEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string Message,
    RuntimeRazerModeState State)
    : RuntimeEvent(RuntimeEventKind.RazerWatchdogCheck, Sequence, Timestamp, Message);

public sealed record SchedulerOverrunEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string Message,
    TimeSpan Overrun)
    : RuntimeEvent(RuntimeEventKind.SchedulerOverrun, Sequence, Timestamp, Message);

public sealed record EmergencyHandoffEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string Message,
    bool Succeeded)
    : RuntimeEvent(RuntimeEventKind.EmergencyHandoff, Sequence, Timestamp, Message);

public sealed record OwnershipLostEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string Message)
    : RuntimeEvent(RuntimeEventKind.OwnershipLost, Sequence, Timestamp, Message);

public sealed record SessionStartedEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string Message,
    Guid SessionId)
    : RuntimeEvent(RuntimeEventKind.SessionStarted, Sequence, Timestamp, Message);

public sealed record SessionStoppedEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string Message,
    Guid? SessionId)
    : RuntimeEvent(RuntimeEventKind.SessionStopped, Sequence, Timestamp, Message);

public sealed record RecoveryAttemptEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string Message)
    : RuntimeEvent(RuntimeEventKind.RecoveryAttempt, Sequence, Timestamp, Message);

public sealed record RecoveryResultEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string Message,
    bool Succeeded)
    : RuntimeEvent(RuntimeEventKind.RecoveryResult, Sequence, Timestamp, Message);

public sealed record ProtocolExchangeEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string Message,
    RazerExchangeTrace Exchange)
    : RuntimeEvent(RuntimeEventKind.ProtocolExchange, Sequence, Timestamp, Message);

public sealed class BoundedRuntimeEventLog
{
    private readonly object _sync = new();
    private readonly Queue<RuntimeEvent> _events;
    private readonly int _capacity;
    private long _totalCount;

    public BoundedRuntimeEventLog(int capacity = 2048)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _events = new Queue<RuntimeEvent>(Math.Min(capacity, 2048));
    }

    public long TotalCount
    {
        get
        {
            lock (_sync)
            {
                return _totalCount;
            }
        }
    }

    public int Capacity => _capacity;

    public void Add(RuntimeEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_sync)
        {
            if (_events.Count == _capacity)
            {
                _events.Dequeue();
            }

            _events.Enqueue(item);
            _totalCount++;
        }
    }

    public IReadOnlyList<RuntimeEvent> Snapshot()
    {
        lock (_sync)
        {
            return _events.ToArray();
        }
    }
}
