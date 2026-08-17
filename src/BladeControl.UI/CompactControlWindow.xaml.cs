using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BladeControl.UI.Services;
using BladeControl.UI.ViewModels;

namespace BladeControl.UI;

public partial class CompactControlWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const int EffectiveDpi = 0;
    private bool _explicitClose;

    public CompactControlWindow(CompactControlViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        ContentRendered += (_, _) => PositionAtBottomRight();
    }

    public event Action? FullAppRequested;

    public event Action? DiagnosticsRequested;

    public event Action? CloseRequested;

    public void ShowAndActivate()
    {
        Show();
        PositionAtBottomRight();
        Activate();
        Focus();
    }

    public void CloseExplicitly()
    {
        _explicitClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_explicitClose)
        {
            e.Cancel = true;
            CloseRequested?.Invoke();
        }

        base.OnClosing(e);
    }

    private void PositionAtBottomRight()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || !GetCursorPos(out PointNative cursor))
        {
            return;
        }

        nint monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double dpiScaleX = dpi.DpiScaleX;
        double dpiScaleY = dpi.DpiScaleY;
        if (GetDpiForMonitor(monitor, EffectiveDpi, out uint dpiX, out uint dpiY) == 0)
        {
            dpiScaleX = dpiX / 96d;
            dpiScaleY = dpiY / 96d;
        }

        double desiredHeight = Math.Min(MaxHeight, Math.Max(1, ActualHeight));
        PixelRect target = CompactWindowPlacement.Calculate(
            new PixelRect(
                info.Work.Left,
                info.Work.Top,
                info.Work.Right - info.Work.Left,
                info.Work.Bottom - info.Work.Top),
            ActualWidth > 0 ? ActualWidth : Width,
            desiredHeight,
            dpiScaleX,
            dpiScaleY);
        _ = SetWindowPos(
            handle,
            0,
            target.Left,
            target.Top,
            target.Width,
            target.Height,
            SwpNoActivate | SwpNoZOrder);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnFullAppClick(object sender, RoutedEventArgs e) => FullAppRequested?.Invoke();

    private void OnDiagnosticsClick(object sender, RoutedEventArgs e) => DiagnosticsRequested?.Invoke();

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RectNative Monitor;
        public RectNative Work;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointNative point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(PointNative point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);
}
