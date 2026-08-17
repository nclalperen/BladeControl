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
    private readonly System.Windows.Forms.ToolStripMenuItem _dynamicItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _autoItem;
    private bool _disposed;

    public NotificationAreaService(RuntimeConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var openItem = new System.Windows.Forms.ToolStripMenuItem("Open BladeControl");
        openItem.Click += (_, _) => OpenRequested?.Invoke();
        _autoItem = new System.Windows.Forms.ToolStripMenuItem("Firmware Auto");
        _autoItem.Click += (_, _) => FirmwareAutoRequested?.Invoke();
        _dynamicItem = new System.Windows.Forms.ToolStripMenuItem("Start Dynamic Cooling");
        _dynamicItem.Click += (_, _) =>
        {
            if (_connection.CanStopThermalControl)
            {
                StopCoolingRequested?.Invoke();
            }
            else
            {
                StartCoolingRequested?.Invoke();
            }
        };
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.AddRange(
        [
            openItem,
            _autoItem,
            _dynamicItem,
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
        _dynamicItem.Text = _connection.CanStopThermalControl
            ? "Stop Dynamic Cooling"
            : "Start Dynamic Cooling";
        _dynamicItem.Enabled = _connection.CanStopThermalControl ||
            _connection.CanStartThermalControl;
        _autoItem.Enabled = _connection.CanApplyStaticProfile;
        string text = _connection.IsOnline
            ? $"BladeControl · {runtimeState}"
            : "BladeControl · Runtime offline";
        _icon.Text = text.Length <= 63 ? text : text[..63];
    }
}
