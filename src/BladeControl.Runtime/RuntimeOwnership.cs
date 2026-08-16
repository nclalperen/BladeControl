namespace BladeControl.Runtime;

public interface IRuntimeOwnershipGate : IDisposable
{
    IRuntimeOwnershipLease? TryAcquire();
}

public interface IRuntimeOwnershipLease : IDisposable
{
}

public sealed class NamedSemaphoreRuntimeOwnershipGate : IRuntimeOwnershipGate
{
    private const string SemaphoreName = "Local\\BladeControl.Runtime.ManualControl";
    private readonly object _sync = new();
    private readonly Semaphore _semaphore = new(1, 1, SemaphoreName);
    private bool _leased;
    private bool _disposed;

    public IRuntimeOwnershipLease? TryAcquire()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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
                _semaphore.Release();
                _leased = false;
            }

            _semaphore.Dispose();
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

            _semaphore.Release();
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
