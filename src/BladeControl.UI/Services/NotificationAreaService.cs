using System.ComponentModel;

namespace BladeControl.UI.Services;

/// <summary>
/// Notification-area surface for Runtime IPC commands. Exiting disposes only the GUI; it
/// never issues StopThermalControl and therefore never changes runtime ownership.
/// </summary>
public sealed class NotificationAreaService : IDisposable
{
    private readonly RuntimeConnection _connection;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _stateItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _startItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _stopItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _autoItem;
    private bool _disposed;

    public NotificationAreaService(RuntimeConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var openItem = new System.Windows.Forms.ToolStripMenuItem("Open BladeControl");
        openItem.Click += (_, _) => OpenRequested?.Invoke();
        _stateItem = new System.Windows.Forms.ToolStripMenuItem { Enabled = false };
        _startItem = new System.Windows.Forms.ToolStripMenuItem("Start Dynamic Cooling");
        _startItem.Click += (_, _) => StartCoolingRequested?.Invoke();
        _stopItem = new System.Windows.Forms.ToolStripMenuItem("Stop Dynamic Cooling");
        _stopItem.Click += (_, _) => StopCoolingRequested?.Invoke();
        _autoItem = new System.Windows.Forms.ToolStripMenuItem("Firmware Auto");
        _autoItem.Click += (_, _) => FirmwareAutoRequested?.Invoke();
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit UI");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.AddRange(
        [
            openItem,
            _stateItem,
            new System.Windows.Forms.ToolStripSeparator(),
            _startItem,
            _stopItem,
            _autoItem,
            new System.Windows.Forms.ToolStripSeparator(),
            exitItem
        ]);

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Text = "BladeControl · connecting",
            Visible = true
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _connection.PropertyChanged += OnConnectionPropertyChanged;
        _connection.Updated += Update;
        Update();
    }

    public event Action? OpenRequested;

    public event Action? StartCoolingRequested;

    public event Action? StopCoolingRequested;

    public event Action? FirmwareAutoRequested;

    public event Action? ExitRequested;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.PropertyChanged -= OnConnectionPropertyChanged;
        _connection.Updated -= Update;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(RuntimeConnection.State) or
            nameof(RuntimeConnection.Status) or
            nameof(RuntimeConnection.IsCommandInFlight) or
            nameof(RuntimeConnection.Doctor))
        {
            Update();
        }
    }

    private void Update()
    {
        if (_disposed)
        {
            return;
        }

        string runtimeState = _connection.RuntimeStateName ?? "No state";
        _stateItem.Text = _connection.IsOnline
            ? $"Runtime: {runtimeState}"
            : "Runtime: Offline";
        _startItem.Enabled = _connection.CanStartThermalControl;
        _stopItem.Enabled = _connection.CanStopThermalControl;
        _autoItem.Enabled = _connection.CanApplyStaticProfile;
        string text = _connection.IsOnline
            ? $"BladeControl · {runtimeState}"
            : "BladeControl · Runtime offline";
        _icon.Text = text.Length <= 63 ? text : text[..63];
    }
}
