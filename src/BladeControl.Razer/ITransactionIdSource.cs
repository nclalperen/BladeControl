namespace BladeControl.Razer;

internal interface ITransactionIdSource
{
    byte NextTransactionId();
}

internal sealed class SequentialTransactionIdSource : ITransactionIdSource
{
    private const byte MinimumTransactionId = 0x01;
    private const byte MaximumTransactionId = 0xFF;

    private readonly object _sync = new();
    private byte _next;

    internal SequentialTransactionIdSource(byte initialTransactionId = MinimumTransactionId)
    {
        if (initialTransactionId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialTransactionId),
                "Razer transaction ID 0x00 is not valid.");
        }

        _next = initialTransactionId;
    }

    public byte NextTransactionId()
    {
        lock (_sync)
        {
            byte current = _next;
            _next = current == MaximumTransactionId
                ? MinimumTransactionId
                : (byte)(current + 1);
            return current;
        }
    }
}
