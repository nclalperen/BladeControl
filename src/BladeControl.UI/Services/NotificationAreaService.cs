using System.ComponentModel;
using BladeControl.UI.ViewModels;

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

    // One tray glyph, recolored per state rather than three unrelated icons, so a user
    // learns the shape once. Loaded from WPF pack resources (not a loose file path) so they
    // resolve the same way whether this runs from the build output, an MSI install, or the
    // portable zip.
    private readonly System.Drawing.Icon _iconIdle;
    private readonly System.Drawing.Icon _iconWarning;
    private readonly System.Drawing.Icon _iconEmergency;
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

        _iconIdle = LoadPackedIcon("tray-idle.ico");
        _iconWarning = LoadPackedIcon("tray-warning.ico");
        _iconEmergency = LoadPackedIcon("tray-emergency.ico");

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _iconIdle,
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
        _iconIdle.Dispose();
        _iconWarning.Dispose();
        _iconEmergency.Dispose();
    }

    /// <summary>Loads one of the tray-state icons embedded as a WPF pack resource.</summary>
    /// <remarks>
    /// The pack URI names the assembly explicitly rather than using a relative
    /// <c>UriKind.Relative</c> lookup. A relative URI resolves against whatever WPF considers
    /// the current entry assembly, which is BladeControl.UI.exe when the app runs normally —
    /// but not under the WPF smoke test, which hosts this assembly from a test-runner exe. The
    /// resource genuinely does not exist under that resolution path, hence the failure; naming
    /// the assembly removes the ambiguity in every hosting context, not only the test's.
    /// </remarks>
    private static System.Drawing.Icon LoadPackedIcon(string fileName)
    {
        var uri = new Uri(
            $"pack://application:,,,/BladeControl.UI;component/Assets/{fileName}",
            UriKind.Absolute);
        System.Windows.Resources.StreamResourceInfo info =
            System.Windows.Application.GetResourceStream(uri) ??
            throw new InvalidOperationException(
                $"Tray icon resource '{fileName}' was not found. It must be built into " +
                "BladeControl.UI as a WPF <Resource> item.");
        using System.IO.Stream stream = info.Stream;
        return new System.Drawing.Icon(stream);
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

        // The same tone the rest of the interface already uses for this state — Display
        // deliberately classifies EmergencyHandoff as Warning rather than Danger, because by
        // the time that state is reported the handoff already succeeded and the machine is
        // safe. Only a genuine Faulted runtime gets the emergency-red icon; offline reads as
        // idle rather than alarming, since a closed connection is not itself a hazard.
        _icon.Icon = _connection.IsOnline
            ? Display.RuntimeStateTone(runtimeState) switch
            {
                StatusTone.Danger => _iconEmergency,
                StatusTone.Warning => _iconWarning,
                _ => _iconIdle
            }
            : _iconIdle;
    }
}
