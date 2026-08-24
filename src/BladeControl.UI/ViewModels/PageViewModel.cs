using BladeControl.UI.Ipc;
using BladeControl.UI.Services;

namespace BladeControl.UI.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    private static long s_statusRevision;
    private string? _statusMessage;
    private long _statusMessageRevision;
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

    /// <summary>
    /// The chart this page shows, or null for pages that show none.
    /// </summary>
    /// <remarks>
    /// Assigned by the shell from the single <c>TelemetryHistory</c> that Monitoring owns, rather
    /// than each page collecting its own. One buffer, one collection path, several views of it —
    /// a second history would drift from the first the moment either missed a sample.
    /// </remarks>
    public ChartViewModel? Chart { get; internal set; }

    public bool HasChart => Chart is not null;

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
            if (!string.Equals(_statusMessage, value, StringComparison.Ordinal))
            {
                _statusMessage = value;
                AdvanceStatusRevision();
                Raise();
                Raise(nameof(HasStatusMessage));
            }
        }
    }

    /// <summary>
    /// Application-wide presentation order for page operation messages. Compact mode projects
    /// results from more than one page, so page priority cannot tell it which result happened
    /// most recently.
    /// </summary>
    public long StatusMessageRevision => _statusMessageRevision;

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
        bool hadMessage = _statusMessage is not null;
        _statusMessage = null;
        AdvanceStatusRevision();
        if (hadMessage)
        {
            Raise(nameof(StatusMessage));
            Raise(nameof(HasStatusMessage));
        }

        StatusIsError = false;
    }

    private void AdvanceStatusRevision()
    {
        _statusMessageRevision = Interlocked.Increment(ref s_statusRevision);
        Raise(nameof(StatusMessageRevision));
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
        // Publish severity before making the message visible. CompactControlViewModel listens to
        // each child notification; the opposite order briefly rendered a new failure in the
        // previous success tone until the second notification arrived.
        StatusIsError = !outcome.Succeeded;
        StatusMessage = outcome.Message;
        if (Connection.IsOnline)
        {
            await Connection.RefreshProfilesNowAsync(Lifetime).ConfigureAwait(true);
        }

        Refresh();
    }
}
