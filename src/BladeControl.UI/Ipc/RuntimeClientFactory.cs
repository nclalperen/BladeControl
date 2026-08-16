namespace BladeControl.UI.Ipc;

public sealed record RuntimeClientSelection(IRuntimeUiClient Client, bool IsDesignPreview);

/// <summary>
/// Composition-root policy for the runtime channel. A production launch always receives
/// the named-pipe implementation; synthetic data is available only through the explicit
/// <c>--design</c> development switch.
/// </summary>
public static class RuntimeClientFactory
{
    public static RuntimeClientSelection Create(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        bool designPreview = arguments.Any(argument =>
            string.Equals(argument, "--design", StringComparison.OrdinalIgnoreCase));

        IRuntimeUiClient client = designPreview
            ? new FakeRuntimeUiClient { SimulateDrift = true }
            : new NamedPipeRuntimeUiClient();

        if (!designPreview && !client.IsLiveRuntimeChannel)
        {
            (client as IDisposable)?.Dispose();
            throw new InvalidOperationException(
                "A production BladeControl UI launch requires the live Runtime Core IPC channel.");
        }

        return new RuntimeClientSelection(client, designPreview);
    }
}
