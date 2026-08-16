using BladeControl.Razer;
using BladeControl.Thermal;

namespace BladeControl.Thermal.Tests;

[TestClass]
public sealed class ThermalCurveTests
{
    [TestMethod]
    public void IncreasingNonDecreasingCurveIsAccepted()
    {
        ThermalCurve curve = Curve();

        Assert.AreEqual(3, curve.Points.Count);
    }

    [TestMethod]
    public void DuplicateTemperatureIsRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => new ThermalCurve(
        [
            Point(50, 3000),
            Point(50, 3300)
        ]));
    }

    [TestMethod]
    public void DecreasingRpmIsRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => new ThermalCurve(
        [
            Point(50, 3500),
            Point(60, 3400)
        ]));
    }

    [TestMethod]
    public void ThermalRpmBelow3000IsRejected()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new ThermalCurvePoint(50, new FanRpm(2900)));
    }

    [TestMethod]
    public void FanRpmAbove5000IsRejected()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new FanRpm(5100));
    }

    [TestMethod]
    public void InvalidRpmIncrementIsRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => new FanRpm(3350));
    }

    [TestMethod]
    public void ExactCurvePointIsReturned()
    {
        Assert.AreEqual(3300, Curve().Evaluate(60).Value);
    }

    [TestMethod]
    public void InterpolationQuantizesUpwardToNextHundred()
    {
        var curve = new ThermalCurve([Point(50, 3000), Point(60, 3700)]);

        Assert.AreEqual(3400, curve.Evaluate(55).Value);
    }

    [TestMethod]
    public void BelowRangeUsesFirstPoint()
    {
        Assert.AreEqual(3000, Curve().Evaluate(20).Value);
    }

    [TestMethod]
    public void AboveRangeUsesLastPoint()
    {
        Assert.AreEqual(4000, Curve().Evaluate(100).Value);
    }

    [TestMethod]
    public void DefaultProfileIsNamedAndConservative()
    {
        Assert.AreEqual("default", BuiltInThermalProfiles.Default.Name);
        Assert.IsTrue(BuiltInThermalProfiles.Default.CpuCurve.Points.All(
            point => point.TargetRpm.Value is >= 3000 and <= 5000));
    }

    private static ThermalCurve Curve() => new(
    [
        Point(50, 3000),
        Point(60, 3300),
        Point(70, 4000)
    ]);

    private static ThermalCurvePoint Point(double temperature, int rpm) =>
        new(temperature, new FanRpm(rpm));
}
