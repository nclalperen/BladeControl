using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BladeControl.UI.Services;
using BladeControl.UI.ViewModels;

namespace BladeControl.UI;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private bool _explicitClose;

    public MainWindow(ShellViewModel shell, UiSettings settings)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        ArgumentNullException.ThrowIfNull(settings);
        InitializeComponent();
        DataContext = shell;
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        if (settings.WindowMaximized)
        {
            Loaded += (_, _) => WindowState = WindowState.Maximized;
        }
    }

    public event Action? ExitRequested;

    public event Action? CompactRequested;

    public UiSettings CaptureSettings()
    {
        Rect bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        return _shell.CaptureSettings(
            bounds.Width > 0 ? bounds.Width : Width,
            bounds.Height > 0 ? bounds.Height : Height,
            WindowState == WindowState.Maximized);
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void CloseExplicitly()
    {
        _explicitClose = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_explicitClose)
        {
            e.Cancel = true;
            if (_shell.MinimizeToTray)
            {
                Hide();
                Dispatcher.BeginInvoke(() => CompactRequested?.Invoke());
            }
            else
            {
                Dispatcher.BeginInvoke(() => ExitRequested?.Invoke());
            }
        }

        base.OnClosing(e);
    }

    private void OnCompactPanelClick(object sender, RoutedEventArgs e) => CompactRequested?.Invoke();

    private static void TryEnableDarkTitleBar(nint handle)
    {
        const int useImmersiveDarkMode = 20;
        int enabled = 1;
        _ = DwmSetWindowAttribute(
            handle,
            useImmersiveDarkMode,
            ref enabled,
            Marshal.SizeOf<int>());
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint window,
        int attribute,
        ref int value,
        int valueSize);
}
