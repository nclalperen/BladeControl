using System.IO;
using Microsoft.Win32;

namespace BladeControl.UI.Services;

/// <summary>
/// Registers the BladeControl user interface to start when the interactive user signs in.
/// </summary>
/// <remarks>
/// <para>Uses the per-user <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> key, which
/// is the standard desktop mechanism and — importantly — writable without elevation. The UI
/// therefore never needs administrator rights: it is a thin IPC client, and only the runtime
/// service is privileged.</para>
///
/// <para>Per-user rather than machine-wide (<c>HKLM...\Run</c>) is deliberate. The setting
/// belongs to a person, not to the machine; each account decides for itself, uninstalling for
/// one user does not strand another's registration, and Task Manager's Startup tab lets the
/// user turn it off through the normal Windows affordance — which is a requirement, not a
/// side effect. The runtime service's autostart is separate and stays machine-wide.</para>
///
/// <para>The value is written with the executable path quoted so a path containing spaces —
/// <c>C:\Program Files\BladeControl\…</c> always does — cannot be misparsed into a different
/// executable plus arguments.</para>
/// </remarks>
public sealed class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Registry value name. Stable: renaming it would orphan the old entry.</summary>
    public const string ValueName = "BladeControl";

    private readonly IStartupRegistryKey _key;
    private readonly string _executablePath;

    public StartupRegistration(string? executablePath = null, IStartupRegistryKey? key = null)
    {
        _executablePath = string.IsNullOrWhiteSpace(executablePath)
            ? ResolveExecutablePath()
            : executablePath;
        _key = key ?? new CurrentUserRunKey();
    }

    /// <summary>The exact command line that would be registered.</summary>
    public string CommandLine => Quote(_executablePath);

    /// <summary>True when this user currently has BladeControl registered to start.</summary>
    public bool IsEnabled()
    {
        try
        {
            return _key.GetValue(ValueName) is not null;
        }
        catch (Exception exception) when (IsExpectedRegistryFailure(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// Applies the preference. Returns false when the registry refused the change, so the
    /// caller can keep the displayed state honest instead of claiming success.
    /// </summary>
    public bool TrySet(bool enabled)
    {
        try
        {
            if (enabled)
            {
                _key.SetValue(ValueName, CommandLine);
            }
            else
            {
                _key.DeleteValue(ValueName);
            }

            return true;
        }
        catch (Exception exception) when (IsExpectedRegistryFailure(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// Rewrites an existing registration so it points at the current executable. Called on
    /// startup: after an upgrade relocates the binaries, a stale path would silently stop
    /// launching. Does nothing when the user has startup disabled.
    /// </summary>
    public void RepairIfEnabled()
    {
        try
        {
            if (_key.GetValue(ValueName) is string existing &&
                !string.Equals(existing, CommandLine, StringComparison.OrdinalIgnoreCase))
            {
                _key.SetValue(ValueName, CommandLine);
            }
        }
        catch (Exception exception) when (IsExpectedRegistryFailure(exception))
        {
            // A startup preference is never worth failing launch over.
        }
    }

    private static string ResolveExecutablePath()
    {
        // Environment.ProcessPath is the host executable (BladeControl.UI.exe for both
        // framework-dependent and self-contained publishes). AppContext.BaseDirectory would
        // give the directory only, and Assembly.Location is empty for single-file builds.
        string? path = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(path) ? string.Empty : path;
    }

    private static string Quote(string path) =>
        string.IsNullOrEmpty(path) ? string.Empty : $"\"{path}\"";

    private static bool IsExpectedRegistryFailure(Exception exception) =>
        exception is UnauthorizedAccessException or System.Security.SecurityException or
            IOException or ObjectDisposedException;
}

/// <summary>Seam over the Run key so the policy is testable without touching the registry.</summary>
public interface IStartupRegistryKey
{
    object? GetValue(string name);

    void SetValue(string name, string value);

    void DeleteValue(string name);
}

/// <summary>Real per-user Run key.</summary>
public sealed class CurrentUserRunKey : IStartupRegistryKey
{
    public object? GetValue(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            StartupRegistration.RunKeyPath,
            writable: false);
        return key?.GetValue(name);
    }

    public void SetValue(string name, string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(
            StartupRegistration.RunKeyPath,
            writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            StartupRegistration.RunKeyPath,
            writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
