using System.Runtime.Versioning;

namespace BladeControl.Runtime;

public interface IRuntimeOwnershipGate : IDisposable
{
    IRuntimeOwnershipLease? TryAcquire();
}

public interface IRuntimeOwnershipLease : IDisposable
{
}

/// <summary>Why a gate can or cannot be evaluated by this process.</summary>
public enum RuntimeOwnershipGateAccess
{
    /// <summary>The gate is open to this process; acquisition reflects real contention.</summary>
    Open,

    /// <summary>
    /// The gate exists and this process may not open it, which means another BladeControl host
    /// created it and owns the hardware.
    /// </summary>
    OwnedByAnotherHost,

    /// <summary>
    /// No gate exists and this process may not create one in the machine-wide namespace, so
    /// ownership cannot be established. Refusing is the only safe answer.
    /// </summary>
    CannotCreate
}

/// <summary>
/// The machine-wide gate that serialises hardware ownership between every BladeControl
/// process, whichever Windows session each of them runs in.
/// </summary>
/// <remarks>
/// <para>This used to name the semaphore <c>Local\BladeControl.Runtime.ManualControl</c>.
/// The <c>Local\</c> namespace is per-session, and the runtime service runs in session 0
/// while a diagnostic CLI or a console host runs in the signed-in user's session — so the two
/// were never contending for the same kernel object, and the "machine-wide singleton" the
/// safety architecture rests on did not exist across that boundary. Observed directly: with
/// the service running and holding its lease, a user-session process acquired the same-named
/// semaphore immediately and a CLI fan write went through.</para>
/// <para><c>Global\</c> fixes the scope, and creating an object in that namespace needs
/// SeCreateGlobalPrivilege, which LocalSystem has and an ordinary user does not. So the order
/// matters: open an existing gate first, and only try to create one when none exists. The
/// three outcomes are deliberately distinguished, because they mean different things.</para>
/// <list type="bullet">
/// <item>Opened — a host is or has been running; wait with a zero timeout and honour the
/// answer.</item>
/// <item>Not found — nothing has created the gate, so no host holds the hardware, and
/// creating it here is the correct move.</item>
/// <item>Access denied — a gate exists and this process may not evaluate it. Refuse. This is
/// the case that must not be mistaken for "not found": both leave us without a handle, but
/// only one of them means the hardware is free.</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class NamedSemaphoreRuntimeOwnershipGate : IRuntimeOwnershipGate
{
    /// <summary>
    /// The kernel object every BladeControl process contends for. Machine-wide by prefix.
    /// </summary>
    public const string SemaphoreName = "Global\\BladeControl.Runtime.ManualControl";

    private readonly object _sync = new();
    private readonly Semaphore? _semaphore;

    private readonly RuntimeOwnershipGateAccess _access;

    private bool _leased;
    private bool _disposed;

    public NamedSemaphoreRuntimeOwnershipGate()
    {
        try
        {
            _semaphore = Semaphore.OpenExisting(SemaphoreName);
            _access = RuntimeOwnershipGateAccess.Open;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Nothing has created it, so no BladeControl host holds the hardware. Creating it
            // here is the correct move — and needs SeCreateGlobalPrivilege, which an ordinary
            // user does not have.
            try
            {
                _semaphore = new Semaphore(1, 1, SemaphoreName);
                _access = RuntimeOwnershipGateAccess.Open;
            }
            catch (UnauthorizedAccessException)
            {
                _access = RuntimeOwnershipGateAccess.CannotCreate;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Denied rather than not-found, so the object exists — a BladeControl host created
            // it and this process may not evaluate it. That distinction is the whole reason
            // these two exceptions are caught separately: both leave us without a handle, but
            // only this one means the hardware is already owned.
            _access = RuntimeOwnershipGateAccess.OwnedByAnotherHost;
        }
    }

    /// <summary>
    /// How this process stands with respect to the gate, so a caller can explain a refusal
    /// truthfully rather than guessing which of two very different causes applied.
    /// </summary>
    public RuntimeOwnershipGateAccess Access => _access;

    public IRuntimeOwnershipLease? TryAcquire()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_access != RuntimeOwnershipGateAccess.Open || _semaphore is null)
            {
                return null;
            }

            if (_leased || !_semaphore.WaitOne(0))
            {
                return null;
            }

            _leased = true;
            return new Lease(this);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_leased)
            {
                _semaphore?.Release();
                _leased = false;
            }

            _semaphore?.Dispose();
            _disposed = true;
        }
    }

    private void Release()
    {
        lock (_sync)
        {
            if (!_leased || _disposed)
            {
                return;
            }

            _semaphore?.Release();
            _leased = false;
        }
    }

    private sealed class Lease : IRuntimeOwnershipLease
    {
        private NamedSemaphoreRuntimeOwnershipGate? _owner;

        internal Lease(NamedSemaphoreRuntimeOwnershipGate owner)
        {
            _owner = owner;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

public sealed class InProcessRuntimeOwnershipGate : IRuntimeOwnershipGate
{
    private readonly object _sync = new();
    private bool _leased;
    private bool _disposed;

    public IRuntimeOwnershipLease? TryAcquire()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_leased)
            {
                return null;
            }

            _leased = true;
            return new Lease(this);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }
    }

    private void Release()
    {
        lock (_sync)
        {
            _leased = false;
        }
    }

    private sealed class Lease : IRuntimeOwnershipLease
    {
        private InProcessRuntimeOwnershipGate? _owner;

        internal Lease(InProcessRuntimeOwnershipGate owner)
        {
            _owner = owner;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
