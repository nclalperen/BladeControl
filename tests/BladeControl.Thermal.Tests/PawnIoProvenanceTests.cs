using System.Text.Json;
using BladeControl.Hardware.Windows.Telemetry;

namespace BladeControl.Thermal.Tests;

[TestClass]
public sealed class PawnIoProvenanceTests
{
    [TestMethod]
    public void SignerSourcesAreReportedExplicitly()
    {
        var provenance = new PawnIoProvenance(
            true,
            "1.0",
            @"C:\Windows\System32\drivers\PawnIO.sys",
            "Running",
            "1.0",
            "Valid",
            "CN=Microsoft Windows Hardware Compatibility Publisher",
            "CN=namazso.eu",
            "CN=Microsoft Time-Stamp Service",
            "WindowsCatalog",
            "FCA6E7D58B0CF38DBB913A2B9E532F48629145D395F454B16A9F58E97B8D3940",
            true,
            []);

        string json = JsonSerializer.Serialize(provenance);

        StringAssert.Contains(json, "WindowsTrustedSignerSubject");
        StringAssert.Contains(json, "EmbeddedSignerSubject");
        StringAssert.Contains(json, "TimestampSignerSubject");
        StringAssert.Contains(json, "WindowsCatalog");
        Assert.IsFalse(json.Contains("\"SignerSubject\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowsTrustFailureBlocksThermalOwnership()
    {
        Assert.IsFalse(PawnIoProvenanceReader.IsSafeForThermalOwnership(
            installed: true,
            windowsTrustValid: false));
    }

    [TestMethod]
    public void InstalledAndWindowsTrustedAreBothRequired()
    {
        Assert.IsFalse(PawnIoProvenanceReader.IsSafeForThermalOwnership(
            installed: false,
            windowsTrustValid: true));
        Assert.IsTrue(PawnIoProvenanceReader.IsSafeForThermalOwnership(
            installed: true,
            windowsTrustValid: true));
    }
}
