namespace BladeControl.UI.ViewModels;

/// <summary>
/// Navigation glyphs as raw path geometry, drawn stroked on a 24x24 grid. Kept as plain
/// strings so views bind straight to <c>Path.Data</c> with no image assets in the repository.
/// </summary>
public static class Icons
{
    public const string Dashboard =
        "M4,4H10V11H4Z M14,4H20V9H14Z M14,13H20V20H14Z M4,15H10V20H4Z";

    public const string Performance =
        "M5,20V13 M12,20V5 M19,20V9";

    public const string Fans =
        "M12,3V21 M4.2,7.5L19.8,16.5 M4.2,16.5L19.8,7.5 " +
        "M9.4,5.4L12,8L14.6,5.4 M9.4,18.6L12,16L14.6,18.6";

    public const string Monitoring =
        "M4,19H20 M4,15L9,10L13,14L20,6";

    public const string Diagnostics =
        "M3,12H7L9.5,6.5L14,17.5L16.5,12H21";

    public const string Settings =
        "M3,7H21 M3,12H21 M3,17H21 " +
        "M8,5A2,2 0 0,1 8,9A2,2 0 0,1 8,5Z " +
        "M16,10A2,2 0 0,1 16,14A2,2 0 0,1 16,10Z " +
        "M10,15A2,2 0 0,1 10,19A2,2 0 0,1 10,15Z";
}
