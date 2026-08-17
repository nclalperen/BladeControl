using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BladeControl.UI.Services;

public enum UiLaunchMode
{
    Compact,
    Full
}

public enum CompactCloseBehavior
{
    Hide,
    Exit
}

/// <summary>
/// UI-only preferences. Hardware, performance and thermal configuration belong to Runtime
/// Core and are never mirrored here — see docs/gui-backend-needs.md (item 3).
/// </summary>
public sealed record UiSettings
{
    public const int CurrentVersion = 2;

    public int Version { get; init; } = CurrentVersion;

    public double WindowWidth { get; init; } = 1100;

    public double WindowHeight { get; init; } = 720;

    public bool WindowMaximized { get; init; }

    public string SelectedPage { get; init; } = "Dashboard";

    public bool MinimizeToTray { get; init; } = true;

    public int GraphWindowSeconds { get; init; } = 120;

    public UiLaunchMode LaunchMode { get; init; } = UiLaunchMode.Compact;

    public CompactCloseBehavior CompactCloseBehavior { get; init; } = CompactCloseBehavior.Hide;

    /// <summary>Clamps every field to a usable range so a corrupt file cannot break startup.</summary>
    public UiSettings Sanitized() => new()
    {
        Version = CurrentVersion,
        WindowWidth = double.IsFinite(WindowWidth) ? Math.Clamp(WindowWidth, 900, 6000) : 1100,
        WindowHeight = double.IsFinite(WindowHeight) ? Math.Clamp(WindowHeight, 600, 4000) : 720,
        WindowMaximized = WindowMaximized,
        SelectedPage = string.IsNullOrWhiteSpace(SelectedPage) ? "Dashboard" : SelectedPage,
        MinimizeToTray = MinimizeToTray,
        GraphWindowSeconds = GraphWindowSeconds is 60 or 120 ? GraphWindowSeconds : 120,
        LaunchMode = Enum.IsDefined(LaunchMode) ? LaunchMode : UiLaunchMode.Compact,
        CompactCloseBehavior = Enum.IsDefined(CompactCloseBehavior)
            ? CompactCloseBehavior
            : CompactCloseBehavior.Hide
    };
}

public sealed class UiSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public UiSettingsStore(string? directory = null)
    {
        string root = directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BladeControl");
        _path = System.IO.Path.Combine(root, "ui-settings.json");
    }

    public string Path => _path;

    public UiSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new UiSettings();
            }

            UiSettings? loaded = JsonSerializer.Deserialize<UiSettings>(
                File.ReadAllText(_path),
                Options);
            return (loaded ?? new UiSettings()).Sanitized();
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new UiSettings();
        }
    }

    public void Save(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(settings.Sanitized(), Options));
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            // Losing a window-size preference must never take the application down.
        }
    }
}
