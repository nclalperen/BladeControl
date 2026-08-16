using System.IO;
using System.Xml.Linq;
using BladeControl.UI.Ipc;
using BladeControl.UI.Services;

namespace BladeControl.UI.Tests;

[TestClass]
public sealed class SettingsAndArchitectureTests
{
    [TestMethod]
    public void SettingsSanitizationRestoresSafeBoundsAndSupportedChoices()
    {
        var unsafeSettings = new UiSettings
        {
            Version = 99,
            WindowWidth = double.NaN,
            WindowHeight = 9_000,
            SelectedPage = " ",
            MinimizeToTray = false,
            GraphWindowSeconds = 30
        };

        UiSettings sanitized = unsafeSettings.Sanitized();

        Assert.AreEqual(UiSettings.CurrentVersion, sanitized.Version);
        Assert.AreEqual(1_100d, sanitized.WindowWidth);
        Assert.AreEqual(4_000d, sanitized.WindowHeight);
        Assert.AreEqual("Dashboard", sanitized.SelectedPage);
        Assert.IsFalse(sanitized.MinimizeToTray);
        Assert.AreEqual(120, sanitized.GraphWindowSeconds);
    }

    [TestMethod]
    public void SettingsStoreRoundTripsUiOnlyPreferencesAndRecoversFromCorruptJson()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "BladeControl.UI.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new UiSettingsStore(directory);
            var expected = new UiSettings
            {
                WindowWidth = 1_440,
                WindowHeight = 900,
                WindowMaximized = true,
                SelectedPage = "Diagnostics",
                MinimizeToTray = false,
                GraphWindowSeconds = 60
            };

            store.Save(expected);

            Assert.IsTrue(File.Exists(store.Path));
            Assert.AreEqual(expected.Sanitized(), store.Load());

            File.WriteAllText(store.Path, "{ definitely not valid JSON");
            Assert.AreEqual(new UiSettings(), store.Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ProductionFactoryCreatesOnlyLiveNamedPipeClientWithoutConnecting()
    {
        RuntimeClientSelection production = RuntimeClientFactory.Create([]);
        try
        {
            Assert.IsFalse(production.IsDesignPreview);
            Assert.IsTrue(production.Client.IsLiveRuntimeChannel);
            Assert.IsInstanceOfType(production.Client, typeof(NamedPipeRuntimeUiClient));
            Assert.IsNotInstanceOfType(production.Client, typeof(FakeRuntimeUiClient));
        }
        finally
        {
            (production.Client as IDisposable)?.Dispose();
        }
    }

    [TestMethod]
    public void FakeClientRequiresExplicitDesignSwitch()
    {
        RuntimeClientSelection preview = RuntimeClientFactory.Create(["--DESIGN"]);

        Assert.IsTrue(preview.IsDesignPreview);
        Assert.IsFalse(preview.Client.IsLiveRuntimeChannel);
        Assert.IsInstanceOfType(preview.Client, typeof(FakeRuntimeUiClient));
    }

    [TestMethod]
    public void UiProjectReferencesOnlyRuntimeContractProject()
    {
        string root = FindRepositoryRoot();
        string projectPath = Path.Combine(
            root,
            "src",
            "BladeControl.UI",
            "BladeControl.UI.csproj");
        XDocument project = XDocument.Load(projectPath);
        XNamespace xmlNamespace = project.Root?.Name.Namespace ?? XNamespace.None;
        string[] references = project
            .Descendants(xmlNamespace + "ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => include!)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { @"..\BladeControl.Runtime\BladeControl.Runtime.csproj" },
            references);
        Assert.IsFalse(references.Any(reference =>
            reference.Contains("Hardware.Windows", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(references.Any(reference =>
            reference.Contains("BladeControl.Service", StringComparison.OrdinalIgnoreCase)));
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

        throw new InvalidOperationException("Could not locate BladeControl.sln from the test output.");
    }
}
