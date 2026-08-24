using System.Reflection;
using System.Text.Json;
using BladeControl.Runtime;

namespace BladeControl.Runtime.Tests;

/// <summary>
/// The dispatcher's linked cancellation source is disposed when it is replaced or finished
/// with, rather than stranded on the host token.
/// </summary>
/// <remarks>
/// <para><c>CreateLinkedTokenSource</c> registers a callback on the token it links to. The
/// dispatcher links to the host's token, which lives as long as the service does, so a linked
/// source that is never disposed is never collected either — it stays rooted for the lifetime
/// of a process meant to run for months.</para>
/// <para>Two ways it was reachable. A start replaced <c>_thermalCancellation</c> without
/// disposing what was already there, and a stop only cleared it on the success path: the
/// clear sat after two awaits and outside any <c>finally</c>, so a stop that threw left the
/// source behind for the next start to strand.</para>
/// <para>These read a private field. The invariant is about an object's lifetime rather than
/// about anything the dispatcher exposes, and the alternative — waiting to observe unbounded
/// growth — is not a test. The disposal itself is asserted through public behaviour:
/// <c>Token</c> throws once a source is disposed.</para>
/// </remarks>
[TestClass]
public sealed class ThermalCancellationLifetimeTests
{
    /// <summary>
    /// Starting a session disposes a source left over from a previous one.
    /// </summary>
    /// <remarks>
    /// Fails before the fix: the assignment overwrote the field and the old source stayed
    /// registered on the host token forever.
    /// </remarks>
    [TestMethod]
    public async Task StartingASessionDisposesACancellationSourceLeftBehindByAnEarlierOne()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);

        // Stand in for the source a failed stop leaves behind.
        var stranded = new CancellationTokenSource();
        SetCancellation(dispatcher, stranded);

        RuntimeIpcResponse start = await StartAsync(dispatcher);
        Assert.IsTrue(start.Succeeded, start.Error);

        Assert.ThrowsException<ObjectDisposedException>(
            () => _ = stranded.Token,
            "The source displaced by a new session must be disposed, or its registration on " +
            "the host token outlives the session that created it.");

        _ = await StopAsync(dispatcher);
    }

    /// <summary>
    /// A completed session leaves no source behind.
    /// </summary>
    [TestMethod]
    public async Task StoppingASessionClearsAndDisposesItsCancellationSource()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);

        RuntimeIpcResponse start = await StartAsync(dispatcher);
        Assert.IsTrue(start.Succeeded, start.Error);

        CancellationTokenSource? live = GetCancellation(dispatcher);
        Assert.IsNotNull(live, "A running session must own a cancellation source.");

        RuntimeIpcResponse stop = await StopAsync(dispatcher);
        Assert.IsTrue(stop.Succeeded, stop.Error);

        Assert.IsNull(
            GetCancellation(dispatcher),
            "A finished session must not leave its cancellation source in the field.");
        Assert.ThrowsException<ObjectDisposedException>(
            () => _ = live!.Token,
            "A finished session's cancellation source must be disposed.");
    }

    /// <summary>
    /// Repeated sessions do not accumulate sources.
    /// </summary>
    /// <remarks>
    /// The leak is per-session, so a single start/stop pair can hide it. Each source from a
    /// previous round must be dead by the end.
    /// </remarks>
    [TestMethod]
    public async Task RepeatedSessionsDisposeEverySourceTheyCreate()
    {
        RuntimeLifecycleTests.RuntimeRig rig = new();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);

        List<CancellationTokenSource> created = [];
        for (int round = 0; round < 4; round++)
        {
            RuntimeIpcResponse start = await StartAsync(dispatcher);
            Assert.IsTrue(start.Succeeded, start.Error);
            CancellationTokenSource? source = GetCancellation(dispatcher);
            Assert.IsNotNull(source);
            created.Add(source!);
            RuntimeIpcResponse stop = await StopAsync(dispatcher);
            Assert.IsTrue(stop.Succeeded, stop.Error);
        }

        for (int index = 0; index < created.Count; index++)
        {
            int round = index;
            Assert.ThrowsException<ObjectDisposedException>(
                () => _ = created[round].Token,
                $"The cancellation source from round {round} was never disposed.");
        }
    }

    private static ValueTask<RuntimeIpcResponse> StartAsync(RuntimeIpcDispatcher dispatcher) =>
        dispatcher.DispatchAsync(new RuntimeIpcRequest(
            RuntimeIpcDispatcher.ProtocolVersion,
            Guid.NewGuid(),
            RuntimeIpcOperation.StartThermalControl,
            JsonSerializer.SerializeToElement(new StartThermalControlRequest("default"))));

    private static ValueTask<RuntimeIpcResponse> StopAsync(RuntimeIpcDispatcher dispatcher) =>
        dispatcher.DispatchAsync(new RuntimeIpcRequest(
            RuntimeIpcDispatcher.ProtocolVersion,
            Guid.NewGuid(),
            RuntimeIpcOperation.StopThermalControl,
            null));

    private static FieldInfo CancellationField =>
        typeof(RuntimeIpcDispatcher).GetField(
            "_thermalCancellation",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new AssertFailedException(
            "RuntimeIpcDispatcher._thermalCancellation was renamed; update these tests rather " +
            "than deleting them — the invariant they guard is still real.");

    private static CancellationTokenSource? GetCancellation(RuntimeIpcDispatcher dispatcher) =>
        (CancellationTokenSource?)CancellationField.GetValue(dispatcher);

    private static void SetCancellation(
        RuntimeIpcDispatcher dispatcher,
        CancellationTokenSource? value) => CancellationField.SetValue(dispatcher, value);
}
