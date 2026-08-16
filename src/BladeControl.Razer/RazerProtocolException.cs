namespace BladeControl.Razer;

public sealed class RazerProtocolException : Exception
{
    internal RazerProtocolException(
        string message,
        IReadOnlyList<RazerExchangeTrace> exchanges,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Exchanges = exchanges.ToArray();
    }

    public IReadOnlyList<RazerExchangeTrace> Exchanges { get; }
}
