using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace BladeControl.Service;

/// <summary>
/// Refuses to let a second runtime host start while one already owns the hardware.
/// </summary>
/// <remarks>
/// <para>The named pipe already rejects a second IPC server (it is created with one instance),
/// but by then both processes have already opened HID and telemetry providers. This gate is
/// taken before any hardware is touched, so the loser exits without ever having claimed a
/// device — which is what stops a developer console host and the installed service from
/// fighting over the controller.</para>
///
/// <para>A named <see cref="Semaphore"/> rather than a <see cref="Mutex"/>, deliberately. A
/// mutex is owned by a <em>thread</em> and is reentrant, so a second acquisition on the same
/// thread succeeds and the gate silently fails to detect a duplicate. A mutex must also be
/// released by the acquiring thread, which suits async host code badly. A semaphore has
/// neither property, and it is the primitive the runtime's Manual-control ownership gate
/// already uses.</para>
///
/// <para>Crash safety comes from the kernel: the object exists only while a handle is open, so
/// a host that dies without disposing releases the gate when its handle closes.</para>
///
/// <para><b>Failing closed.</b> If the gate exists but this process may not open it, that means
/// a host is running under an account we cannot see — so ownership is refused. Assuming the
/// opposite would be the one mistake that lets two processes drive the controller at once.</para>
/// </remarks>
public sealed class RuntimeHostSingleton : IDisposable
{
    private readonly Semaphore? _semaphore;
    private bool _disposed;

    private RuntimeHostSingleton(Semaphore? semaphore, bool acquired, string scope)
    {
        _semaphore = semaphore;
        IsOwner = acquired;
        Scope = scope;
    }

    /// <summary>True when this process may proceed to open hardware.</summary>
    public bool IsOwner { get; }

    /// <summary>Which kernel namespace the gate ended up in, for diagnostics.</summary>
    public string Scope { get; }

    public static RuntimeHostSingleton Acquire() =>
        Acquire(RuntimeServiceIdentity.HostSingletonName);

    internal static RuntimeHostSingleton Acquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (TryGate(name, out RuntimeHostSingleton? gate))
        {
            return gate;
        }

        // Creating an object in the Global namespace needs SeCreateGlobalPrivilege, which
        // LocalSystem and elevated administrators hold but a plain user does not. Falling back
        // to Local keeps a non-elevated developer run working; it narrows the gate to that
        // session, which is acceptable because such a run cannot own hardware anyway.
        if (name.StartsWith(@"Global\", StringComparison.Ordinal))
        {
            string localName = string.Concat(@"Local\", name.AsSpan(@"Global\".Length));
            if (TryGate(localName, out RuntimeHostSingleton? local))
            {
                return local;
            }
        }

        // The gate could not be established at all. Refuse: an unguarded host is exactly the
        // situation this class exists to prevent.
        return new RuntimeHostSingleton(null, acquired: false, scope: "unavailable");
    }

    private static bool TryGate(string name, out RuntimeHostSingleton gate)
    {
        // Ask whether a gate already exists before trying to create one. Distinguishing the
        // two cases matters: SemaphoreAcl.Create raises UnauthorizedAccessException both when
        // the caller lacks the privilege to create the object and when the object exists but
        // is not openable, and those demand opposite answers.
        try
        {
            if (Semaphore.TryOpenExisting(name, out Semaphore? existing))
            {
                bool acquired = existing.WaitOne(TimeSpan.Zero, exitContext: false);
                gate = new RuntimeHostSingleton(existing, acquired, name);
                return true;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // A gate is there and we may not use it: another host owns the hardware.
            gate = new RuntimeHostSingleton(null, acquired: false, scope: name);
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            WaitHandleCannotBeOpenedException)
        {
            gate = null!;
            return false;
        }

        Semaphore? semaphore = null;
        try
        {
            semaphore = SemaphoreAcl.Create(
                initialCount: 1,
                maximumCount: 1,
                name,
                out bool _,
                CreateSecurity());
            bool acquired = semaphore.WaitOne(TimeSpan.Zero, exitContext: false);
            gate = new RuntimeHostSingleton(semaphore, acquired, name);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or
            IOException or NotSupportedException or WaitHandleCannotBeOpenedException)
        {
            semaphore?.Dispose();
            gate = null!;
            return false;
        }
    }

    /// <summary>
    /// Grants use of the gate to the accounts that can legitimately host the runtime, and to
    /// nobody else — a process that could Release it could break the mutual exclusion.
    /// </summary>
    private static SemaphoreSecurity CreateSecurity()
    {
        var security = new SemaphoreSecurity();
        const SemaphoreRights hostRights = SemaphoreRights.Synchronize |
            SemaphoreRights.Modify |
            SemaphoreRights.ReadPermissions;

        // The installed service.
        security.AddAccessRule(new SemaphoreAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            SemaphoreRights.FullControl,
            AccessControlType.Allow));

        // An elevated developer console host.
        security.AddAccessRule(new SemaphoreAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            SemaphoreRights.FullControl,
            AccessControlType.Allow));

        // Whoever created it, so a non-elevated run can still use its own Local gate. Without
        // this the creator could be locked out of the object it just made.
        using WindowsIdentity self = WindowsIdentity.GetCurrent();
        if (self.User is { } user)
        {
            security.AddAccessRule(new SemaphoreAccessRule(
                user,
                hostRights,
                AccessControlType.Allow));
        }

        return security;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_semaphore is null)
        {
            return;
        }

        if (IsOwner)
        {
            try
            {
                _semaphore.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already released; closing the handle below is all that remains.
            }
        }

        _semaphore.Dispose();
    }
}
