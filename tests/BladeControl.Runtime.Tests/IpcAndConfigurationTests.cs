using System.Text.Json;
using BladeControl.Razer;
using BladeControl.Runtime;

namespace BladeControl.Runtime.Tests;

[TestClass]
public sealed class IpcAndConfigurationTests
{
    [TestMethod]
    public void MalformedIpcFailsClosed()
    {
        Assert.ThrowsException<FormatException>(() =>
            RuntimeIpcDispatcher.ParseRequest("{not-json"));
    }

    [TestMethod]
    public void IpcUnknownMemberIsRejected()
    {
        string json =
            "{\"Version\":1,\"RequestId\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"Operation\":\"GetRuntimeStatus\",\"Payload\":null,\"RawPacket\":\"00\"}";

        Assert.ThrowsException<FormatException>(() =>
            RuntimeIpcDispatcher.ParseRequest(json));
    }

    [TestMethod]
    public async Task UnsupportedIpcOperationDoesNotReachHardware()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);
        int operations = rig.Hardware.Operations.Count;
        var request = new RuntimeIpcRequest(
            1,
            Guid.NewGuid(),
            (RuntimeIpcOperation)999,
            null);

        RuntimeIpcResponse response = await dispatcher.DispatchAsync(request);

        Assert.IsFalse(response.Succeeded);
        Assert.AreEqual(operations, rig.Hardware.Operations.Count);
    }

    [TestMethod]
    public async Task TypedPerformanceProfileRoutesThroughExistingSafeApi()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);
        JsonElement payload = JsonSerializer.SerializeToElement(
            new ApplyPerformanceProfileRequest("Custom", "Medium", "Low"));
        var request = new RuntimeIpcRequest(
            1,
            Guid.NewGuid(),
            RuntimeIpcOperation.ApplyPerformanceProfile,
            payload);

        RuntimeIpcResponse response = await dispatcher.DispatchAsync(request);

        Assert.IsTrue(response.Succeeded, response.Error);
        Assert.AreEqual(1, rig.Hardware.PerformanceApplies);
        Assert.AreEqual(RazerCpuPerformanceLevel.Medium,
            rig.Hardware.LastPerformanceProfile!.CpuLevel);
        Assert.AreEqual(RazerGpuPerformanceLevel.Low,
            rig.Hardware.LastPerformanceProfile.GpuLevel);
    }

    [TestMethod]
    public void ConfigurationRoundTripUsesVersionedModels()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var store = new RuntimeConfigurationStore(directory);
            store.SavePreferences(RuntimePreferencesDocument.Default);
            store.SavePreferences(RuntimePreferencesDocument.Default with
            {
                WatchdogIntervalSeconds = 6
            });
            store.SaveUserCurve(RuntimeConfigurationStore.BuiltInDefault with
            {
                Name = "my_curve"
            });
            store.SaveUserProfile(new RuntimeUserProfileDocument(
                1,
                "my_profile",
                "CustomMediumLow",
                "Auto",
                "my_curve"));

            ConfigurationLoadResult<RuntimePreferencesDocument> preferences =
                store.LoadPreferences();
            Assert.IsTrue(preferences.Succeeded);
            Assert.AreEqual(6, preferences.Value!.WatchdogIntervalSeconds);
            Assert.IsTrue(store.LoadUserCurve("my_curve").Succeeded);
            Assert.IsTrue(store.LoadUserProfile("my_profile").Succeeded);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void CorruptConfigurationFailsSafely()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "runtime-preferences.json"), "{broken");
            var store = new RuntimeConfigurationStore(directory);

            ConfigurationLoadResult<RuntimePreferencesDocument> result = store.LoadPreferences();

            Assert.IsFalse(result.Succeeded);
            Assert.IsNull(result.Value);
            StringAssert.Contains(result.Message, "rejected safely");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void BuiltInCurveIsReturnedAsIndependentImmutableDefinition()
    {
        StoredThermalCurveDocument first = RuntimeConfigurationStore.BuiltInDefault;
        StoredThermalCurveDocument second = RuntimeConfigurationStore.BuiltInDefault;

        Assert.AreNotSame(first.Cpu, second.Cpu);
        Assert.AreEqual("default", first.Name);
        Assert.AreEqual(1, first.Version);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"BladeControl.Runtime.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
