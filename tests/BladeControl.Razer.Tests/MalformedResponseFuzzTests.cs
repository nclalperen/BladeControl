using BladeControl.Razer.Protocol;

namespace BladeControl.Razer.Tests;

/// <summary>
/// Whatever the hardware returns, the client raises a protocol exception carrying the exchange
/// — never a raw framework exception, and never a hang.
/// </summary>
/// <remarks>
/// <para>Every other input surface in this product has been fuzzed; this one had only been read.
/// It is the layer that parses bytes coming back from firmware, so its inputs are not chosen by
/// us: a different Blade model, a firmware revision that answers differently, a device that
/// glitches mid-exchange, all arrive here as ninety-one bytes that may mean nothing.</para>
/// <para>The property that matters is not "malformed responses are rejected" — the decoder
/// plainly checks length, checksum and data size. It is that the rejection always arrives as
/// <see cref="RazerProtocolException"/>. The client converts <see cref="ArgumentException"/>
/// from the codec into that type deliberately, and callers upstream catch the domain type; a
/// response that produced some other exception would slip past them as an unhandled fault, in a
/// service that owns cooling. That is the same seam mismatch that let one blank line stop the
/// runtime over IPC, so it is worth demonstrating rather than assuming.</para>
/// </remarks>
[TestClass]
public sealed class MalformedResponseFuzzTests
{
    private const int ReportLength = 91;

    /// <summary>
    /// Deterministic so a failure can be reproduced from the seed printed in the message.
    /// </summary>
    private const int Seed = 20260825;

    [TestMethod]
    public void ArbitraryResponseBytesAlwaysSurfaceAsAProtocolException()
    {
        var random = new Random(Seed);
        int rejected = 0;
        int accepted = 0;

        for (int iteration = 0; iteration < 4000; iteration++)
        {
            byte[] report = new byte[ReportLength];
            random.NextBytes(report);

            // Half the runs get a valid checksum, so the fuzz reaches past the first gate
            // instead of being turned away by it every time.
            if (iteration % 2 == 0)
            {
                FixChecksum(report);
            }

            AssertOnlyProtocolFaults(report, iteration, ref rejected, ref accepted);
        }

        // Measured: 4000 rejected, 0 accepted. Both halves matter — the second is the
        // property, and the first proves the fuzz reached the decoder rather than being turned
        // away before it.
        Assert.AreEqual(
            0,
            accepted,
            "Random bytes were accepted as a valid firmware status. A response is only a status " +
            "if it validates; anything else is a reading of nothing.");
        Assert.IsTrue(
            rejected > 0,
            "The fuzz never produced a rejection, so it was not exercising the decoder.");
    }

    /// <summary>
    /// The shapes a real device is most likely to produce, rather than uniform noise.
    /// </summary>
    /// <remarks>
    /// Uniform random bytes almost never look like a plausible response. These are aimed at the
    /// fields the decoder and the validator actually branch on: the data-size byte that bounds
    /// the argument area, the status byte, and the identity fields the response is matched
    /// against.
    /// </remarks>
    [TestMethod]
    public void StructuredlyPlausibleButWrongResponsesAlsoSurfaceAsProtocolExceptions()
    {
        int rejected = 0;
        int accepted = 0;
        int iteration = 0;

        foreach (byte dataSize in new byte[] { 0, 1, 79, 80, 81, 128, 200, 255 })
        {
            foreach (byte status in new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0xFF })
            {
                byte[] report = new byte[ReportLength];
                report[0] = 0x00;          // HID report id
                report[1] = status;
                report[2] = 0x01;          // transaction id
                report[6] = dataSize;
                report[9] = 0x0D;          // a plausible command class
                report[10] = 0x81;
                FixChecksum(report);

                AssertOnlyProtocolFaults(report, iteration++, ref rejected, ref accepted);
            }
        }

        Assert.AreEqual(
            0,
            accepted,
            "A crafted-but-wrong response was accepted as a valid firmware status.");
        Assert.IsTrue(rejected > 0, "None of the crafted responses was rejected.");
    }

    /// <summary>
    /// A device that answers with the wrong number of bytes is refused before decoding.
    /// </summary>
    /// <remarks>
    /// The transport type enforces the length itself, which is the earliest possible place and
    /// means a short or long report can never reach the decoder's slicing.
    /// </remarks>
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(64)]
    [DataRow(90)]
    [DataRow(92)]
    [DataRow(4096)]
    public void AResponseOfTheWrongLengthIsRefusedByTheTransport(int length)
    {
        Assert.ThrowsException<ArgumentException>(
            () => new RazerTransportResponse(new byte[length]),
            $"A {length}-byte report must be refused; only {ReportLength} is a report.");
    }

    private static void AssertOnlyProtocolFaults(
        byte[] report,
        int iteration,
        ref int rejected,
        ref int accepted)
    {
        var transport = new RawResponseTransport(report);
        var client = new RazerClient(transport);

        try
        {
            _ = client.GetStatus();
            accepted++;
        }
        catch (RazerProtocolException)
        {
            rejected++;
        }
        catch (Exception exception)
        {
            Assert.Fail(
                $"Iteration {iteration} (seed {Seed}) raised {exception.GetType().Name} instead " +
                $"of {nameof(RazerProtocolException)}: {exception.Message}. Callers upstream " +
                "catch the protocol type, so anything else escapes them as an unhandled fault " +
                $"in a service that owns cooling. Report: {Convert.ToHexString(report)}");
        }
    }

    /// <summary>
    /// Applies the codec's own checksum to the packet inside the report.
    /// </summary>
    /// <remarks>
    /// Calls the real implementation rather than replicating it. A hand-rolled copy that drifted
    /// from the offsets would leave every "valid checksum" case failing the first gate, and the
    /// fuzz would look like it was exercising the decoder while never reaching it.
    /// </remarks>
    private static void FixChecksum(byte[] report) =>
        report[89] = RazerPacketCodec.CalculateChecksum(report.AsSpan(1));

    /// <summary>Returns one fixed report to every request, whatever was asked.</summary>
    private sealed class RawResponseTransport : IRazerTransport
    {
        private readonly byte[] _report;

        internal RawResponseTransport(byte[] report)
        {
            _report = report;
        }

        public RazerDeviceInfo DeviceInfo { get; } = new(
            @"\\?\hid#fuzz",
            0x1532,
            0x029F,
            0x0001,
            0x0002,
            ReportLength);

        public RazerTransportResponse Exchange(RazerTransportRequest request) =>
            new(_report);

        public void Dispose()
        {
        }
    }
}
