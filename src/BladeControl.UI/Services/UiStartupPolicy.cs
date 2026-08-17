namespace BladeControl.UI.Services;

public enum InitialUiSurface
{
    Compact,
    Full
}

/// <summary>Deterministic startup choice kept separate from window creation for testing.</summary>
public static class UiStartupPolicy
{
    public static InitialUiSurface SelectInitialSurface(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.LaunchMode == UiLaunchMode.Full
            ? InitialUiSurface.Full
            : InitialUiSurface.Compact;
    }
}
