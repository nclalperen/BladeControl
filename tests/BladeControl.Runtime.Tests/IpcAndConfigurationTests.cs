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
    public async Task RuntimeEventPollingUsesTypedBoundedCursorBatch()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);
        JsonElement payload = JsonSerializer.SerializeToElement(
            new GetRuntimeEventsRequest(0, 1));

        RuntimeIpcResponse response = await dispatcher.DispatchAsync(new RuntimeIpcRequest(
            1,
            Guid.NewGuid(),
            RuntimeIpcOperation.GetRuntimeEvents,
            payload));

        Assert.IsTrue(response.Succeeded, response.Error);
        Assert.IsInstanceOfType<RuntimeEventBatchDto>(response.Data);
        var batch = (RuntimeEventBatchDto)response.Data;
        Assert.AreEqual(1, batch.Events.Count);
        Assert.AreEqual("ProtocolExchange", batch.Events[0].Kind);
        Assert.IsNotNull(batch.Events[0].Exchange?.RequestReportHex);
        Assert.AreEqual("Stopped", batch.Status.State);
        Assert.AreEqual(0, batch.Status.RecentEvents.Count);
        Assert.IsTrue(batch.LatestAvailableSequence >= batch.Events[0].Sequence);
    }

    [TestMethod]
    public async Task RuntimeEventPollingRejectsOversizedBatchWithoutHardwareAccess()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);
        int operations = rig.Hardware.Operations.Count;
        JsonElement payload = JsonSerializer.SerializeToElement(
            new GetRuntimeEventsRequest(
                0,
                RuntimeIpcDispatcher.MaximumEventBatchSize + 1));

        RuntimeIpcResponse response = await dispatcher.DispatchAsync(new RuntimeIpcRequest(
            1,
            Guid.NewGuid(),
            RuntimeIpcOperation.GetRuntimeEvents,
            payload));

        Assert.IsFalse(response.Succeeded);
        Assert.AreEqual(operations, rig.Hardware.Operations.Count);
    }

    [TestMethod]
    public async Task StopThermalControlLeavesDispatcherAvailableForStatus()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);

        RuntimeIpcResponse stop = await dispatcher.DispatchAsync(new RuntimeIpcRequest(
            1,
            Guid.NewGuid(),
            RuntimeIpcOperation.StopThermalControl,
            null));
        RuntimeIpcResponse status = await dispatcher.DispatchAsync(new RuntimeIpcRequest(
            1,
            Guid.NewGuid(),
            RuntimeIpcOperation.GetRuntimeStatus,
            null));

        Assert.IsTrue(stop.Succeeded, stop.Error);
        StopThermalControlResultDto? stopData = stop.Data as StopThermalControlResultDto;
        Assert.IsNotNull(stopData);
        Assert.IsTrue(stopData.Succeeded);
        Assert.IsTrue(status.Succeeded, status.Error);
        RuntimeStatusDto? statusData = status.Data as RuntimeStatusDto;
        Assert.IsNotNull(statusData);
        Assert.AreEqual("Stopped", statusData.State);
    }

    [TestMethod]
    public async Task ReadOnlyRuntimeStatusWorksWhileThermalControlIsActive()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        runtime.StartThermalControl();
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);

        RuntimeIpcResponse response = await dispatcher.DispatchAsync(new RuntimeIpcRequest(
            1,
            Guid.NewGuid(),
            RuntimeIpcOperation.GetRuntimeStatus,
            null));

        Assert.IsTrue(response.Succeeded, response.Error);
        RuntimeStatusDto? status = response.Data as RuntimeStatusDto;
        Assert.IsNotNull(status);
        Assert.AreEqual("Running", status.State);
        _ = await runtime.StopThermalControlAsync();
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

    [DataTestMethod]
    [DataRow(0x00, "Balanced")]
    [DataRow(0x04, "Custom")]
    [DataRow(0x05, "Silent")]
    [DataRow(0x7E, "Unknown(0x7E)")]
    public async Task PerformanceModesRoundTripAsMeaningfulStrings(
        int rawMode,
        string expected)
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        rig.Hardware.SetMode(
            new RazerPerformanceMode(checked((byte)rawMode)),
            RazerFanMode.Auto);
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);

        JsonElement data = await DispatchRoundTripAsync(
            dispatcher,
            RuntimeIpcOperation.GetFanState);

        Assert.AreEqual(expected,
            data.GetProperty("Mode").GetProperty("Zone1PerformanceMode").GetString());
    }

    [DataTestMethod]
    [DataRow(0x00, "Auto")]
    [DataRow(0x01, "Manual")]
    [DataRow(0x7D, "Unknown(0x7D)")]
    public async Task FanModesRoundTripAsMeaningfulStrings(int rawMode, string expected)
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        rig.Hardware.SetMode(
            RazerPerformanceMode.Balanced,
            new RazerFanMode(checked((byte)rawMode)));
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);

        JsonElement data = await DispatchRoundTripAsync(
            dispatcher,
            RuntimeIpcOperation.GetFanState);

        Assert.AreEqual(expected,
            data.GetProperty("Mode").GetProperty("Zone1FanMode").GetString());
    }

    [DataTestMethod]
    [DataRow(0x00, "Low")]
    [DataRow(0x01, "Medium")]
    [DataRow(0x02, "High")]
    [DataRow(0x03, "Boost")]
    [DataRow(0x04, "Overclock")]
    [DataRow(0x7C, "Unknown(0x7C)")]
    public async Task CpuLevelsRoundTripAsMeaningfulStrings(int rawLevel, string expected)
    {
        JsonElement data = await PerformanceStateRoundTripAsync(
            new RazerCpuPerformanceLevel(checked((byte)rawLevel)),
            RazerGpuPerformanceLevel.Low);

        Assert.AreEqual(expected, data.GetProperty("CpuLevel").GetString());
    }

    [DataTestMethod]
    [DataRow(0x00, "Low")]
    [DataRow(0x01, "Medium")]
    [DataRow(0x02, "High")]
    [DataRow(0x7B, "Unknown(0x7B)")]
    public async Task GpuLevelsRoundTripAsMeaningfulStrings(int rawLevel, string expected)
    {
        JsonElement data = await PerformanceStateRoundTripAsync(
            RazerCpuPerformanceLevel.Low,
            new RazerGpuPerformanceLevel(checked((byte)rawLevel)));

        Assert.AreEqual(expected, data.GetProperty("GpuLevel").GetString());
    }

    [TestMethod]
    public async Task FanRpmAndFanStateRoundTripAsNumbers()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        rig.Hardware.SetFanRpm(3200, 4100);
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);

        JsonElement data = await DispatchRoundTripAsync(
            dispatcher,
            RuntimeIpcOperation.GetFanState);

        Assert.AreEqual(3200, data.GetProperty("Fan1Rpm").GetInt32());
        Assert.AreEqual(4100, data.GetProperty("Fan2Rpm").GetInt32());
    }

    [TestMethod]
    public async Task RuntimeAndWatchdogStateRoundTripWithoutEmptyModeObjects()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);

        RuntimeIpcResponse response = await dispatcher.DispatchAsync(new RuntimeIpcRequest(
            1,
            Guid.NewGuid(),
            RuntimeIpcOperation.GetRuntimeStatus,
            null));
        string json = RuntimeIpcDispatcher.SerializeResponse(response);
        JsonElement data = DeserializeData(json);
        JsonElement watchdog = data.GetProperty("LastRazerWatchdogState");

        Assert.AreEqual("Stopped", data.GetProperty("State").GetString());
        Assert.AreEqual("Custom", watchdog.GetProperty("Zone1PerformanceMode").GetString());
        Assert.AreEqual("Auto", watchdog.GetProperty("Zone1FanMode").GetString());
        Assert.IsTrue(watchdog.GetProperty("ZonesAgree").GetBoolean());
        Assert.IsFalse(json.Contains("{}", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ThermalCurveDtoRoundTripsWithoutFanRpmValueObjects()
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);
        JsonElement payload = JsonSerializer.SerializeToElement(
            new GetThermalCurveRequest("default"));
        RuntimeIpcResponse response = await dispatcher.DispatchAsync(new RuntimeIpcRequest(
            1,
            Guid.NewGuid(),
            RuntimeIpcOperation.GetThermalCurve,
            payload));

        string json = RuntimeIpcDispatcher.SerializeResponse(response);
        JsonElement data = DeserializeData(json);

        Assert.AreEqual("default", data.GetProperty("Name").GetString());
        Assert.AreEqual(3000,
            data.GetProperty("Cpu")[0].GetProperty("Rpm").GetInt32());
        Assert.IsFalse(json.Contains("{}", StringComparison.Ordinal));
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

    private static async Task<JsonElement> PerformanceStateRoundTripAsync(
        RazerCpuPerformanceLevel cpu,
        RazerGpuPerformanceLevel gpu)
    {
        var rig = new RuntimeLifecycleTests.RuntimeRig();
        await using BladeRuntime runtime = rig.CreateRuntime();
        Assert.IsTrue(runtime.InitializeHost());
        rig.Hardware.SetPerformanceLevels(cpu, gpu);
        await using var dispatcher = new RuntimeIpcDispatcher(runtime);
        return await DispatchRoundTripAsync(
            dispatcher,
            RuntimeIpcOperation.GetPerformanceState);
    }

    private static async Task<JsonElement> DispatchRoundTripAsync(
        RuntimeIpcDispatcher dispatcher,
        RuntimeIpcOperation operation)
    {
        RuntimeIpcResponse response = await dispatcher.DispatchAsync(new RuntimeIpcRequest(
            1,
            Guid.NewGuid(),
            operation,
            null));
        Assert.IsTrue(response.Succeeded, response.Error);
        string json = RuntimeIpcDispatcher.SerializeResponse(response);
        Assert.IsFalse(json.Contains("{}", StringComparison.Ordinal));
        return DeserializeData(json);
    }

    private static JsonElement DeserializeData(string json)
    {
        RuntimeIpcResponse roundTrip = JsonSerializer.Deserialize<RuntimeIpcResponse>(json) ??
            throw new AssertFailedException("IPC response did not deserialize.");
        Assert.IsInstanceOfType<JsonElement>(roundTrip.Data);
        return ((JsonElement)roundTrip.Data).Clone();
    }
}
