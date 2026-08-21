using System.IO;
using System.Reflection;
using System.Xml.Linq;
using BladeControl.UI.Ipc;
using BladeControl.UI.Services;
using BladeControl.UI.ViewModels;

namespace BladeControl.UI.Tests;

/// <summary>
/// Release-engineering invariants: startup registration, readiness presentation, where
/// configuration is allowed to live, and one authoritative version. Nothing here writes to the
/// real registry, installs anything, or touches hardware.
/// </summary>
[TestClass]
public sealed class ReleaseEngineeringTests
{
    // --- Startup registration -------------------------------------------------------------

    [TestMethod]
    public void StartWithWindowsDefaultsToOnForAFreshInstall() =>
        Assert.IsTrue(
            new UiSettings().StartWithWindows,
            "The runtime service starts with Windows; a panel that must be launched by hand " +
            "would make the product useless by default.");

    [TestMethod]
    public void StartupRegistrationUsesThePerUserRunKeySoItNeverNeedsElevation()
    {
        // HKLM would require the UI to elevate, and the UI is a thin IPC client with no
        // business holding administrator rights. HKCU is also what Task Manager's Startup tab
        // manages, which is how a user is expected to turn this off.
        Assert.AreEqual(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            StartupRegistration.RunKeyPath);
        Assert.AreEqual("BladeControl", StartupRegistration.ValueName);
    }

    [TestMethod]
    public void EnablingAndDisablingStartupWritesAndRemovesTheValue()
    {
        var key = new FakeRunKey();
        var registration = new StartupRegistration(@"C:\Program Files\BladeControl\BladeControl.UI.exe", key);

        Assert.IsFalse(registration.IsEnabled());

        Assert.IsTrue(registration.TrySet(true));
        Assert.IsTrue(registration.IsEnabled());

        Assert.IsTrue(registration.TrySet(false));
        Assert.IsFalse(registration.IsEnabled());
        Assert.AreEqual(0, key.Values.Count);
    }

    [TestMethod]
    public void RegisteredCommandIsQuotedSoAProgramFilesPathCannotBeMisparsed()
    {
        var key = new FakeRunKey();
        var registration = new StartupRegistration(
            @"C:\Program Files\BladeControl\BladeControl.UI.exe",
            key);
        registration.TrySet(true);

        // Unquoted, Windows would try C:\Program.exe with "Files\BladeControl\..." as
        // arguments — the classic unquoted-path problem.
        Assert.AreEqual(
            @"""C:\Program Files\BladeControl\BladeControl.UI.exe""",
            key.Values[StartupRegistration.ValueName]);
    }

    [TestMethod]
    public void UpgradeRepairsAStaleRegistrationWithoutCreatingOne()
    {
        var key = new FakeRunKey();
        key.Values[StartupRegistration.ValueName] = @"""C:\Old\BladeControl.UI.exe""";

        var registration = new StartupRegistration(
            @"C:\Program Files\BladeControl\BladeControl.UI.exe",
            key);
        registration.RepairIfEnabled();

        Assert.AreEqual(
            @"""C:\Program Files\BladeControl\BladeControl.UI.exe""",
            key.Values[StartupRegistration.ValueName],
            "An upgrade can relocate the executable; a stale path stops launching silently.");

        // A user who turned startup off must stay off across upgrades.
        var empty = new FakeRunKey();
        new StartupRegistration(@"C:\Program Files\BladeControl\BladeControl.UI.exe", empty)
            .RepairIfEnabled();
        Assert.AreEqual(0, empty.Values.Count);
    }

    [TestMethod]
    public void ARefusedRegistryWriteLeavesTheDisplayedSettingHonest()
    {
        var key = new FakeRunKey { ThrowOnWrite = true };
        using var shell = CreateShell(new StartupRegistration(@"C:\BladeControl.UI.exe", key));

        Assert.IsTrue(shell.StartWithWindows, "Default is on.");
        shell.StartWithWindows = false;

        Assert.IsTrue(
            shell.StartWithWindows,
            "If Windows refuses the change, the checkbox must not claim it succeeded.");
    }

    [TestMethod]
    public void StartupPreferenceRoundTripsThroughSavedSettings()
    {
        using var shell = CreateShell(startupRegistrar: null);
        shell.StartWithWindows = false;

        Assert.IsFalse(shell.CaptureSettings(1100, 720, false).StartWithWindows);
    }

    // --- Service readiness presentation ---------------------------------------------------

    [TestMethod]
    public async Task RuntimeStillStartingReadsAsConnectingRatherThanAsAFailure()
    {
        var client = new FakeRuntimeUiClient { IsOnline = false };
        using var connection = new RuntimeConnection(
            client,
            new ImmediateUiDispatcher(),
            startupGracePeriod: TimeSpan.FromMinutes(5));
        connection.Start();
        await connection.PollOnceAsync(CancellationToken.None);

        using var shell = new ShellViewModel(connection, new UiSettings(), false);

        Assert.IsTrue(connection.IsAwaitingRuntimeStartup);
        StringAssert.Contains(shell.ConnectionNoticeTitle, "Connecting to BladeControl Runtime");
        Assert.AreEqual(
            StatusTone.Warning,
            shell.ConnectionNoticeTone,
            "A service that is merely still starting is not a fault.");

        using var compact = new CompactControlViewModel(shell);
        StringAssert.Contains(compact.ConnectionText, "Starting");
        StringAssert.Contains(compact.FooterText, "Connecting to BladeControl Runtime");
    }

    /// <summary>
    /// The grace window changes wording only. Every command gate still sees an offline
    /// runtime, so nothing can be sent to hardware that is not there.
    /// </summary>
    [TestMethod]
    public async Task StartupGraceNeverEnablesCommandsWhileOffline()
    {
        var client = new FakeRuntimeUiClient { IsOnline = false };
        using var connection = new RuntimeConnection(
            client,
            new ImmediateUiDispatcher(),
            startupGracePeriod: TimeSpan.FromMinutes(5));
        connection.Start();
        await connection.PollOnceAsync(CancellationToken.None);

        Assert.IsTrue(connection.IsAwaitingRuntimeStartup);
        Assert.AreEqual(RuntimeConnectionState.Offline, connection.State);
        Assert.IsFalse(connection.IsOnline);
        Assert.IsFalse(connection.CanIssueCommand);
        Assert.IsFalse(connection.CanApplyStaticProfile);
        Assert.IsFalse(connection.CanStartThermalControl);
    }

    [TestMethod]
    public async Task OnceTheRuntimeHasAnsweredALaterOutageIsReportedAsTheFaultItIs()
    {
        var client = new FakeRuntimeUiClient();
        using var connection = new RuntimeConnection(
            client,
            new ImmediateUiDispatcher(),
            startupGracePeriod: TimeSpan.FromMinutes(5));
        connection.Start();
        await connection.PollOnceAsync(CancellationToken.None);
        Assert.IsFalse(connection.IsAwaitingRuntimeStartup, "Connected: not awaiting startup.");

        client.IsOnline = false;
        await connection.PollOnceAsync(CancellationToken.None);

        Assert.AreEqual(RuntimeConnectionState.Offline, connection.State);
        Assert.IsFalse(
            connection.IsAwaitingRuntimeStartup,
            "A runtime that answered and then vanished is a real failure, not a slow start.");

        using var shell = new ShellViewModel(connection, new UiSettings(), false);
        Assert.AreEqual(StatusTone.Danger, shell.ConnectionNoticeTone);
    }

    [TestMethod]
    public void ReconnectProbeStaysConservativeRatherThanSpinning() =>
        Assert.IsTrue(
            RuntimeConnection.DefaultReconnectInterval >= TimeSpan.FromSeconds(5),
            "Waiting for a delayed-start service must not become a busy loop.");

    // --- Install vs configuration paths ---------------------------------------------------

    [TestMethod]
    public void UserConfigurationLivesUnderLocalAppDataNeverInsideProgramFiles()
    {
        string path = new UiSettingsStore().Path;

        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        StringAssert.StartsWith(path, localAppData);
        Assert.AreEqual("ui-settings.json", Path.GetFileName(path));

        // Program Files is read-only for a non-elevated process, and the UI never elevates.
        foreach (Environment.SpecialFolder programFiles in new[]
        {
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86
        })
        {
            string folder = Environment.GetFolderPath(programFiles);
            if (!string.IsNullOrEmpty(folder))
            {
                Assert.IsFalse(
                    path.StartsWith(folder, StringComparison.OrdinalIgnoreCase),
                    "Writable user configuration must never live in the install directory.");
            }
        }
    }

    // --- One authoritative version --------------------------------------------------------

    [TestMethod]
    public void EveryShippingAssemblyCarriesTheVersionFromDirectoryBuildProps()
    {
        string root = FindRepositoryRoot();
        XDocument props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        string? prefix = props.Descendants("BladeControlVersionPrefix").FirstOrDefault()?.Value.Trim();

        Assert.IsFalse(
            string.IsNullOrWhiteSpace(prefix),
            "Directory.Build.props must declare BladeControlVersionPrefix.");

        foreach (Assembly assembly in new[]
        {
            typeof(NamedPipeRuntimeUiClient).Assembly,
            typeof(BladeControl.Ipc.RuntimeIpcEndpoint).Assembly,
            typeof(BladeControl.Runtime.RuntimeIpcDispatcher).Assembly
        })
        {
            string? informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            Assert.IsNotNull(informational, $"{assembly.GetName().Name} has no version.");
            StringAssert.StartsWith(
                informational,
                prefix,
                $"{assembly.GetName().Name} disagrees with Directory.Build.props.");

            Version? assemblyVersion = assembly.GetName().Version;
            Assert.AreEqual(
                $"{prefix}.0",
                assemblyVersion?.ToString(),
                $"{assembly.GetName().Name} must use the major.minor.patch.0 form the MSI compares.");
        }
    }

    [TestMethod]
    public void InstallerDerivesItsVersionFromTheSamePropertyRatherThanARepeatedLiteral()
    {
        string root = FindRepositoryRoot();
        string wixproj = File.ReadAllText(
            Path.Combine(root, "installer", "BladeControl.Installer.wixproj"));

        StringAssert.Contains(
            wixproj,
            "$(BladeControlVersionPrefix)",
            "The installer must read the shared version property, not restate a number.");

        string product = File.ReadAllText(Path.Combine(root, "installer", "Product.wxs"));
        StringAssert.Contains(
            product,
            "$(ProductVersion)",
            "Product.wxs must use the preprocessor version, not a literal.");
    }

    /// <summary>
    /// Diagnostics report the runtime's own build, not one inferred from this executable.
    /// </summary>
    /// <remarks>
    /// The two can differ, and the case where they differ is the one worth catching: an upgrade
    /// that replaced the GUI while the previous service was still running. Deriving the value
    /// from the UI assembly would describe the wrong process precisely then. This asserted
    /// "Runtime Core V1 does not expose a version over IPC", which stopped being true when
    /// RuntimeStatusDto gained RuntimeBuild.
    /// </remarks>
    [TestMethod]
    public void DiagnosticsShowTheRuntimeReportedBuildIdentifier()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "BladeControl.UI",
            "ViewModels",
            "DiagnosticsViewModel.cs"));

        StringAssert.Contains(source, "status?.RuntimeBuild");
        Assert.IsFalse(
            source.Contains("does not expose a version over IPC", StringComparison.Ordinal),
            "The runtime reports its build; the interface must stop saying it cannot.");
    }

    /// <summary>
    /// Both shipping packages carry the licence the binaries are conveyed under.
    /// </summary>
    /// <remarks>
    /// GPL-3.0 requires the licence text to accompany the object code it covers (sections 4
    /// through 6). The MSI shipped THIRD-PARTY-NOTICES.md under a comment reading "licensing
    /// and attribution travel with the product", and the portable zip copied the same file —
    /// so every third party's licence travelled with the product and BladeControl's own did
    /// not. Asserted against the packaging sources because the failure is silent: nothing about
    /// a build or an install goes wrong when the file is simply absent.
    /// </remarks>
    [TestMethod]
    public void BothPackagesShipTheLicenceTheBinariesAreConveyedUnder()
    {
        string root = FindRepositoryRoot();

        Assert.IsTrue(
            File.Exists(Path.Combine(root, "LICENSE")),
            "The repository must carry the licence it conveys binaries under.");

        string product = File.ReadAllText(Path.Combine(root, "installer", "Product.wxs"));
        StringAssert.Contains(
            product,
            "$(RepositoryRoot)LICENSE",
            "The MSI must install the licence, not only the third-party attributions.");

        string pack = File.ReadAllText(Path.Combine(root, "build", "pack.ps1"));
        StringAssert.Contains(
            pack,
            "'LICENSE'",
            "The portable zip must carry the licence alongside the binaries.");
    }

    /// <summary>
    /// The installer's licence dialog states the licence that was actually chosen.
    /// </summary>
    /// <remarks>
    /// License.rtf predated the licence decision and told every installing user that the
    /// project "has not yet selected a final open-source licence". Left alone it would have
    /// shipped that claim to every machine while LICENSE in the same install folder said
    /// GPL-3.0 — the installer contradicting the product on the one point a user consults an
    /// installer about.
    /// </remarks>
    [TestMethod]
    public void TheInstallerLicenceDialogMatchesTheChosenLicence()
    {
        string rtf = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "installer", "License.rtf"));

        StringAssert.Contains(rtf, "GNU General Public License, version 3");
        StringAssert.Contains(rtf, "15. Disclaimer of Warranty");
        Assert.IsFalse(
            rtf.Contains("has not yet selected", StringComparison.OrdinalIgnoreCase),
            "The licence dialog must not still describe the project as unlicensed.");
    }

    /// <summary>
    /// The GUI must stay a thin IPC client. BladeControl.Ipc is allowed because it is a
    /// dependency-free policy assembly; the hardware providers and the service host are not.
    /// </summary>
    [TestMethod]
    public void UiStillHasNoHardwareOrServiceHostReference()
    {
        // The project file's reference list is asserted by
        // SettingsAndArchitectureTests.UiProjectReferencesOnlyRuntimeContractProject. This
        // checks the stronger property: what the built assembly actually pulls in, which a
        // transitive reference would reveal even if the project file looked clean.
        string[] loadable = typeof(NamedPipeRuntimeUiClient).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();
        foreach (string forbidden in new[]
        {
            "BladeControl.Hardware.Windows",
            "BladeControl.Service",
            "LibreHardwareMonitorLib"
        })
        {
            Assert.IsFalse(
                loadable.Contains(forbidden, StringComparer.OrdinalIgnoreCase),
                $"The GUI must not reference {forbidden}.");
        }
    }

    private static ShellViewModel CreateShell(StartupRegistration? startupRegistrar)
    {
        var connection = new RuntimeConnection(
            new FakeRuntimeUiClient(),
            new ImmediateUiDispatcher());
        return new ShellViewModel(connection, new UiSettings(), false)
        {
            StartupRegistrar = startupRegistrar
        };
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BladeControl.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate BladeControl.sln.");
    }

    private sealed class FakeRunKey : IStartupRegistryKey
    {
        internal Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal bool ThrowOnWrite { get; init; }

        public object? GetValue(string name) =>
            Values.TryGetValue(name, out string? value) ? value : null;

        public void SetValue(string name, string value)
        {
            if (ThrowOnWrite)
            {
                throw new UnauthorizedAccessException("Policy blocked the write.");
            }

            Values[name] = value;
        }

        public void DeleteValue(string name)
        {
            if (ThrowOnWrite)
            {
                throw new UnauthorizedAccessException("Policy blocked the write.");
            }

            Values.Remove(name);
        }
    }
}
