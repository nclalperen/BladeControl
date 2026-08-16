using BladeControl.Runtime;
using BladeControl.UI.Ipc;
using BladeControl.UI.Services;
using BladeControl.UI.ViewModels;

namespace BladeControl.UI.Tests;

[TestClass]
public sealed class ViewModelPolicyTests
{
    [TestMethod]
    public async Task UnvalidatedPerformanceLevelsCannotBecomeSelectionsOrRequests()
    {
        var client = new FakeRuntimeUiClient();
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        await connection.PollOnceAsync(CancellationToken.None);
        var viewModel = new PerformanceViewModel(connection, CancellationToken.None);
        viewModel.Refresh();

        Assert.IsFalse(viewModel.TrySelectCpuLevel("High"));
        Assert.IsFalse(viewModel.TrySelectCpuLevel("Boost"));
        Assert.IsFalse(viewModel.TrySelectCpuLevel("Overclock"));
        Assert.IsFalse(viewModel.TrySelectGpuLevel("Medium"));
        Assert.IsFalse(viewModel.TrySelectGpuLevel("High"));
        Assert.IsFalse(viewModel.TrySelectCpuLevel("Raw-255"));

        Assert.AreEqual("Medium", viewModel.SelectedCpuLevel);
        Assert.AreEqual("Low", viewModel.SelectedGpuLevel);
        Assert.AreEqual(0, client.PerformanceRequests.Count);
        Assert.IsTrue(viewModel.CpuLevels
            .Where(option => !option.IsAvailable)
            .All(option => option.BlockedReason?.Contains(
                "not hardware validated",
                StringComparison.OrdinalIgnoreCase) == true));
        Assert.IsTrue(viewModel.GpuLevels
            .Where(option => !option.IsAvailable)
            .All(option => option.Tooltip.Contains("no bypass", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void DefaultRuntimeCurveSatisfiesEveryMirroredConstraint()
    {
        StoredThermalCurveDocument curve = RuntimeUiSampleData.DefaultCurve();

        ThermalCurveValidationResult cpu = ThermalCurveValidator.Validate(curve.Cpu);
        ThermalCurveValidationResult gpu = ThermalCurveValidator.Validate(curve.Gpu);

        Assert.IsTrue(cpu.IsValid);
        Assert.AreEqual(0, cpu.Errors.Count);
        Assert.IsTrue(gpu.IsValid);
        Assert.AreEqual(0, gpu.Errors.Count);
    }

    [DataTestMethod]
    [DataRow(50.0, 3000, 50.0, 3200, "strictly higher")]
    [DataRow(50.0, 3300, 60.0, 3200, "must not drop")]
    [DataRow(50.0, 3050, 60.0, 3300, "multiple of 100")]
    [DataRow(0.0, 3000, 60.0, 3300, "above 0 C")]
    [DataRow(50.0, 2900, 60.0, 3300, "between 3000 and 5000")]
    public void InvalidCurveShapesAreRejectedWithPointSpecificErrors(
        double firstTemperature,
        int firstRpm,
        double secondTemperature,
        int secondRpm,
        string expected)
    {
        StoredThermalCurvePoint[] points =
        [
            new(firstTemperature, firstRpm),
            new(secondTemperature, secondRpm)
        ];

        ThermalCurveValidationResult result = ThermalCurveValidator.Validate(points);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.PointErrors.Count > 0);
        Assert.IsTrue(result.Errors.Any(error =>
            error.Contains(expected, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CurveNeedsAtLeastTwoPoints()
    {
        ThermalCurveValidationResult result = ThermalCurveValidator.Validate(
            [new StoredThermalCurvePoint(50, 3000)]);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(error =>
            error.Contains("at least 2 points", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CurveEditorMarksEditsDirtyAndSurfacesInlineValidation()
    {
        var editor = new ThermalCurveEditorViewModel();
        editor.Load(RuntimeUiSampleData.DefaultCurve());

        Assert.IsTrue(editor.IsLoadedFromRuntime);
        Assert.IsTrue(editor.IsValid);
        Assert.IsFalse(editor.IsDirty);
        Assert.IsFalse(editor.CanApply);

        editor.CpuPoints[1].TemperatureCelsius = editor.CpuPoints[0].TemperatureCelsius;

        Assert.IsTrue(editor.IsDirty);
        Assert.IsFalse(editor.IsValid);
        Assert.IsTrue(editor.CpuPoints[1].HasError);
        StringAssert.Contains(editor.CpuPoints[1].Error!, "strictly higher");
        Assert.IsTrue(editor.Errors.Any(error => error.StartsWith("CPU curve", StringComparison.Ordinal)));
        StringAssert.Contains(editor.ApplyBlockedReason, "not sent to the runtime");
    }

    [TestMethod]
    public void FixedFanInputsAreClampedSnappedAndLinkedBeforeAnyRequest()
    {
        var client = new FakeRuntimeUiClient();
        using var connection = new RuntimeConnection(client, new ImmediateUiDispatcher());
        var viewModel = new FansThermalViewModel(connection, CancellationToken.None);

        viewModel.Fan1Target = 1;
        Assert.AreEqual(RuntimeUiPolicy.MinimumFanRpm, viewModel.Fan1Target);
        Assert.AreEqual(RuntimeUiPolicy.MinimumFanRpm, viewModel.Fan2Target);

        viewModel.LinkFans = false;
        viewModel.Fan1Target = 4_949;
        viewModel.Fan2Target = 5_999;

        Assert.AreEqual(4_900, viewModel.Fan1Target);
        Assert.AreEqual(RuntimeUiPolicy.MaximumFanRpm, viewModel.Fan2Target);
        Assert.AreEqual(0, client.FanRequests.Count);
    }
}
