using System.Windows.Threading;

namespace BladeControl.UI.Services;

/// <summary>
/// Marshals background poll results onto the UI thread. Abstracted so ViewModels stay
/// hardware-free and dispatcher-free under test.
/// </summary>
public interface IUiDispatcher
{
    bool IsOnUiThread { get; }

    void Post(Action action);
}

public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public bool IsOnUiThread => _dispatcher.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        // Keep the connection's backing state and its UI notifications in the same order.
        // This blocks only the background poller for the short property-update callback;
        // IPC and telemetry work never run on the UI thread.
        _dispatcher.Invoke(action, DispatcherPriority.Background);
    }
}

/// <summary>Runs callbacks inline. Used by tests and by the design-time preview.</summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => true;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}
