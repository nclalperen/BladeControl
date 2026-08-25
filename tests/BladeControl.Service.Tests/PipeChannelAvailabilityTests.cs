using System.IO;
using System.IO.Pipes;
using System.Text;
using BladeControl.Ipc;
using BladeControl.Runtime;
using BladeControl.Runtime.Tests;

namespace BladeControl.Service.Tests;

/// <summary>
/// One client cannot take the channel away from every other client.
/// </summary>
/// <remarks>
/// <para>The pipe is created with a single server instance, so an occupied connection is not
/// one connection among several — it is the whole channel. Two ways to occupy it were found by
/// probing the running service, and neither needed anything more than the access the pipe's
/// DACL grants every locally signed-in user by design:</para>
/// <list type="number">
/// <item>Connect and never send. The server waited in its read with no deadline, and the
/// interface could not connect for as long as the silent client cared to hold on. The service
/// went on reporting itself Running throughout.</item>
/// <item>Send a valid request and never read the answer. The write took no cancellation token
/// at all, so the server blocked filling a buffer nobody was draining.</item>
/// </list>
/// <para>These drive the real <see cref="RuntimeNamedPipeServer"/> over the real pipe, because
/// the defect was in how a connection is serviced rather than in anything a unit can see.</para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class PipeChannelAvailabilityTests
{
    /// <summary>
    /// A client that connects and says nothing must not lock everyone else out.
    /// </summary>
    /// <remarks>
    /// Before the deadline this hung until the silent client was disposed; the second exchange
    /// never completed.
    /// </remarks>
    [TestMethod]
    public async Task ASilentClientDoesNotHoldTheChannelAgainstEveryoneElse()
    {
        await WithServerAsync(async () =>
        {
            Assert.IsTrue(await ExchangeSucceedsAsync(), "Baseline exchange must work.");

            using (var silent = new NamedPipeClientStream(
                ".",
                _endpoint,
                PipeDirection.InOut,
                PipeOptions.Asynchronous))
            {
                await silent.ConnectAsync(10_000);
                Assert.IsTrue(silent.IsConnected, "The silent client must actually connect.");

                // Long enough for the deadline to expire and the channel to come back, and
                // well short of hanging the suite if it does not.
                bool recovered = await ExchangeSucceedsAsync(
                    RuntimeNamedPipeServer.ClientMessageTimeout + TimeSpan.FromSeconds(15));

                Assert.IsTrue(
                    recovered,
                    "A client that connects and never sends occupied the only server instance, " +
                    "so nothing else could reach the runtime while it held on.");
            }

            Assert.IsTrue(
                await ExchangeSucceedsAsync(),
                "The channel must still serve after the silent client is gone.");
        });
    }

    /// <summary>
    /// A client that will not read its answer does not hold the channel either.
    /// </summary>
    /// <remarks>
    /// <para>Stated honestly, because it matters for what this test is worth: it passes with
    /// or without the write deadline. The response write cannot block against a client that is
    /// not reading, because every answer fits the pipe's output buffer — see
    /// <see cref="TheOutputBufferCanHoldTheLargestPermittedResponse"/>, which guards the
    /// invariant that makes it so.</para>
    /// <para>Kept anyway, as a check on the behaviour rather than a guard on the deadline: a
    /// client abandoning its read is an ordinary event — the interface exiting mid-exchange
    /// does exactly this — and the channel must survive it.</para>
    /// </remarks>
    [TestMethod]
    public async Task AClientThatNeverReadsItsAnswerDoesNotHoldTheChannel()
    {
        await WithServerAsync(async () =>
        {
            using (var deaf = new NamedPipeClientStream(
                ".",
                _endpoint,
                PipeDirection.InOut,
                PipeOptions.Asynchronous))
            {
                await deaf.ConnectAsync(10_000);
                var writer = new StreamWriter(deaf, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true
                };

                // A perfectly valid request, and then nothing — the answer is never read.
                await writer.WriteLineAsync(
                    "{\"Version\":1,\"RequestId\":\"33333333-3333-3333-3333-333333333333\"," +
                    "\"Operation\":\"GetRuntimeStatus\",\"Payload\":null}");

                bool recovered = await ExchangeSucceedsAsync(
                    RuntimeNamedPipeServer.ClientMessageTimeout + TimeSpan.FromSeconds(15));

                Assert.IsTrue(
                    recovered,
                    "A client that never reads its answer left the server blocked in the write, " +
                    "holding the only server instance.");
            }

            Assert.IsTrue(
                await ExchangeSucceedsAsync(),
                "The channel must still serve after the client is gone.");
        });
    }

    /// <summary>
    /// Every permitted response fits the pipe's output buffer.
    /// </summary>
    /// <remarks>
    /// <para>This is the invariant that makes an unread response harmless. The buffer is
    /// created at <see cref="RuntimeIpcEndpoint.MaximumMessageBytes"/> and a response is
    /// refused above <see cref="RuntimeIpcDispatcher.MaximumMessageBytes"/> — two constants,
    /// two assemblies, and nothing previously requiring them to agree.</para>
    /// <para>If the buffer were the smaller of the two, a client that stopped reading would
    /// leave the server blocked mid-write holding the only server instance, which is the
    /// unreachable-service failure the deadline exists to bound. Better to keep it
    /// unreachable.</para>
    /// </remarks>
    [TestMethod]
    public void TheOutputBufferCanHoldTheLargestPermittedResponse()
    {
        Assert.IsTrue(
            RuntimeIpcEndpoint.MaximumMessageBytes >= RuntimeIpcDispatcher.MaximumMessageBytes,
            $"The pipe's output buffer ({RuntimeIpcEndpoint.MaximumMessageBytes} bytes) must be " +
            "able to hold the largest response the dispatcher will emit " +
            $"({RuntimeIpcDispatcher.MaximumMessageBytes} bytes), or a client that stops reading " +
            "can block the server mid-write and occupy the only server instance.");
    }

    /// <summary>
    /// The deadline has to be generous against a real client and still bounded.
    /// </summary>
    /// <remarks>
    /// The interface connects with a 1.5 s timeout and writes its request immediately, so any
    /// value comfortably above that is invisible to it. The upper bound is the point: a
    /// deadline measured in minutes would leave the channel occupiable for minutes.
    /// </remarks>
    [TestMethod]
    public void TheClientMessageDeadlineIsGenerousButBounded()
    {
        Assert.IsTrue(
            RuntimeNamedPipeServer.ClientMessageTimeout >= TimeSpan.FromSeconds(2),
            "Too tight a deadline would abandon a real client on a loaded machine.");
        Assert.IsTrue(
            RuntimeNamedPipeServer.ClientMessageTimeout <= TimeSpan.FromSeconds(30),
            "The deadline bounds how long one connection can occupy the only server instance; " +
            "a long one gives most of the benefit away.");
    }

    /// <summary>
    /// An endpoint of this test's own. The installed service owns the production name, so
    /// serving it here would either fail to create the server or silently exchange messages
    /// with the running product — which is exactly what happened the first time these were
    /// written, and one of them passed without having tested anything.
    /// </summary>
    private static string _endpoint = string.Empty;

    private static async Task WithServerAsync(Func<Task> body)
    {
        _endpoint = $"BladeControl.Tests.{Guid.NewGuid():N}";
        var clock = new VirtualRuntimeClock();
        var telemetry = new FakeRuntimeTelemetry(clock);
        var hardware = new FakeRuntimeHardware();
        await using var runtime = new BladeRuntime(
            telemetry,
            telemetry,
            hardware,
            new InProcessRuntimeOwnershipGate(),
            clock);
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);
        var server = new RuntimeNamedPipeServer(dispatcher, pipeName: _endpoint);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        Task serving = Task.Run(() => server.RunAsync(cancellation.Token), CancellationToken.None);
        try
        {
            await body();
            Assert.IsFalse(
                serving.IsFaulted,
                $"The accept loop faulted: {serving.Exception?.GetBaseException().Message}");
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await serving;
            }
            catch (OperationCanceledException)
            {
                // How the loop is asked to stop.
            }
        }
    }

    private static async Task<bool> ExchangeSucceedsAsync(TimeSpan? within = null)
    {
        TimeSpan budget = within ?? TimeSpan.FromSeconds(10);
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                _endpoint,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)budget.TotalMilliseconds);

            using var reader = new StreamReader(
                pipe,
                new UTF8Encoding(false),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);
            using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(false),
                4096,
                leaveOpen: true)
            {
                AutoFlush = true
            };

            await writer.WriteLineAsync(
                "{\"Version\":1,\"RequestId\":\"44444444-4444-4444-4444-444444444444\"," +
                "\"Operation\":\"GetRuntimeStatus\",\"Payload\":null}");
            string? answer = await reader.ReadLineAsync().WaitAsync(budget);
            return answer?.Contains("\"Succeeded\":true", StringComparison.Ordinal) == true;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException)
        {
            return false;
        }
    }
}
