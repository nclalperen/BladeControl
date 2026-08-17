namespace BladeControl.UI.Services;

/// <summary>Pure, DPI-aware compact-window positioning in physical screen pixels.</summary>
public static class CompactWindowPlacement
{
    public static PixelRect Calculate(
        PixelRect workArea,
        double widthDip,
        double heightDip,
        double dpiScaleX,
        double dpiScaleY,
        double marginDip = 12)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }

        dpiScaleX = dpiScaleX > 0 && double.IsFinite(dpiScaleX) ? dpiScaleX : 1;
        dpiScaleY = dpiScaleY > 0 && double.IsFinite(dpiScaleY) ? dpiScaleY : 1;
        int width = Math.Min(workArea.Width, Math.Max(1, (int)Math.Ceiling(widthDip * dpiScaleX)));
        int height = Math.Min(workArea.Height, Math.Max(1, (int)Math.Ceiling(heightDip * dpiScaleY)));
        int marginX = Math.Max(0, (int)Math.Round(marginDip * dpiScaleX));
        int marginY = Math.Max(0, (int)Math.Round(marginDip * dpiScaleY));
        int left = Math.Max(workArea.Left, workArea.Right - width - marginX);
        int top = Math.Max(workArea.Top, workArea.Bottom - height - marginY);
        return new PixelRect(left, top, width, height);
    }
}

public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}
