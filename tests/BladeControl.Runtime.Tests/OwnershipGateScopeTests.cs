using System.Runtime.Versioning;
using BladeControl.Runtime;

namespace BladeControl.Runtime.Tests;

/// <summary>
/// The hardware ownership gate is machine-wide, not per-session.
/// </summary>
/// <remarks>
/// <para>The safety architecture rests on one sentence: a machine-wide singleton is taken
/// before any device is opened, so a second writer cannot start alongside the service. That
/// sentence was false. The semaphore was named <c>Local\…</c>, and the <c>Local\</c> namespace
/// is scoped to a Windows session — the service runs in session 0 and a diagnostic CLI or
/// console host runs in the signed-in user's session, so the two contended for different kernel
/// objects and neither ever saw the other.</para>
/// <para>Observed on the reference machine with the service running and holding its lease: a
/// user-session process acquired the same-named semaphore on the first attempt, and
/// <c>BladeControl.Cli fan apply auto</c> ran a hardware write to completion. Nothing was
/// harmed — the command asked for the state the machine was already in — but nothing stopped
/// it either.</para>
/// <para>A unit test cannot span Windows sessions, so this asserts the property that decides
/// the scope: the namespace prefix. That is exactly what was wrong, and it is what a future
/// edit would get wrong again.</para>
/// </remarks>
[TestClass]
[SupportedOSPlatform("windows")]
public sealed class OwnershipGateScopeTests
{
    /// <summary>
    /// Would have failed against the original name, which began with <c>Local\</c>.
    /// </summary>
    [TestMethod]
    public void TheGateIsNamedInTheMachineWideNamespace()
    {
        Assert.IsTrue(
            NamedSemaphoreRuntimeOwnershipGate.SemaphoreName.StartsWith(
                @"Global\",
                StringComparison.Ordinal),
            "The ownership gate must live in the Global namespace. A Local\\ name is scoped to " +
            "one Windows session, so the session-0 service and a user-session process would " +
            "hold separate objects and neither would exclude the other. Got: " +
            $"'{NamedSemaphoreRuntimeOwnershipGate.SemaphoreName}'.");

        Assert.IsFalse(
            NamedSemaphoreRuntimeOwnershipGate.SemaphoreName.StartsWith(
                @"Local\",
                StringComparison.Ordinal),
            "The gate must not be session-scoped.");
    }

    /// <summary>
    /// Two gates in one process still exclude each other, so widening the scope did not cost
    /// the exclusion the gate exists for.
    /// </summary>
    [TestMethod]
    public void ASecondGateCannotAcquireWhileTheFirstHoldsTheLease()
    {
        using var first = new NamedSemaphoreRuntimeOwnershipGate();
        if (first.Access != RuntimeOwnershipGateAccess.Open)
        {
            Assert.Inconclusive(
                "A gate exists that this test process may not open, so exclusion cannot be " +
                "evaluated here. This is the fail-closed path and is itself correct.");
        }

        IRuntimeOwnershipLease? held = first.TryAcquire();
        Assert.IsNotNull(held, "The first gate must acquire when nothing else holds it.");

        using (var second = new NamedSemaphoreRuntimeOwnershipGate())
        {
            Assert.IsNull(
                second.TryAcquire(),
                "A second gate must be refused while the lease is held.");
        }

        held!.Dispose();

        using var third = new NamedSemaphoreRuntimeOwnershipGate();
        IRuntimeOwnershipLease? afterRelease = third.TryAcquire();
        Assert.IsNotNull(
            afterRelease,
            "Releasing the lease must let the next contender through; a gate that never " +
            "reopens is a deadlock, not a safety property.");
        afterRelease!.Dispose();
    }
}
