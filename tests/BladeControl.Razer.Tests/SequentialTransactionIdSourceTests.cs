namespace BladeControl.Razer.Tests;

[TestClass]
public sealed class SequentialTransactionIdSourceTests
{
    [TestMethod]
    public void CyclesFromFeThroughFfToOneAndNeverReturnsZero()
    {
        var source = new SequentialTransactionIdSource(0xFE);

        byte[] actual =
        [
            source.NextTransactionId(),
            source.NextTransactionId(),
            source.NextTransactionId(),
            source.NextTransactionId()
        ];

        CollectionAssert.AreEqual(
            new byte[] { 0xFE, 0xFF, 0x01, 0x02 },
            actual);
        Assert.IsFalse(actual.Contains((byte)0x00));
    }

    [TestMethod]
    public void ZeroInitialTransactionIdIsRejected()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new SequentialTransactionIdSource(0x00));
    }
}
