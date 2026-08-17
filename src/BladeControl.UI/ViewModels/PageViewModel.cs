using BladeControl.UI.Ipc;
using BladeControl.UI.Services;

namespace BladeControl.UI.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    private string? _statusMessage;
    private bool _statusIsError;
    private bool _isSelected;

    protected PageViewModel(
        RuntimeConnection connection,
        CancellationToken lifetime,
        string key,
        string title,
        string subtitle,
        string glyph)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Lifetime = lifetime;
        Key = key;
        Title = title;
        Subtitle = subtitle;
        Glyph = glyph;
    }

    public RuntimeConnection Connection { get; }

    public string Key { get; }

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>Path geometry for the navigation rail icon.</summary>
    public string Glyph { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value) && value)
            {
                Activate();
            }
        }
    }

    /// <summary>Last operation result. Errors are never swallowed or rewritten.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        protected set
        {
            if (Set(ref _statusMessage, value))
            {
                Raise(nameof(HasStatusMessage));
            }
        }
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        protected set => Set(ref _statusIsError, value);
    }

    public bool HasStatusMessage => !string.IsNullOrEmpty(_statusMessage);

    protected CancellationToken Lifetime { get; }

    /// <summary>Called on the UI thread after every runtime poll tick.</summary>
    public virtual void Refresh()
    {
    }

    /// <summary>Called when the page becomes the selected page.</summary>
    public virtual void Activate()
    {
    }

    public void ClearStatus()
    {
        StatusMessage = null;
        StatusIsError = false;
    }

    /// <summary>
    /// Issues one state-changing runtime command, surfaces its result, and re-reads the
    /// resulting hardware state. A failure is reported and never retried automatically.
    /// </summary>
    protected async Task RunCommandAsync(
        Func<IRuntimeUiClient, CancellationToken, Task<RuntimeCommandOutcome>> command)
    {
        ClearStatus();
        RuntimeCommandOutcome outcome = await Connection
            .ExecuteAsync(command, Lifetime).ConfigureAwait(true);
        StatusMessage = outcome.Message;
        StatusIsError = !outcome.Succeeded;
        if (Connection.IsOnline)
        {
            await Connection.RefreshProfilesNowAsync(Lifetime).ConfigureAwait(true);
        }

        Refresh();
    }
}
