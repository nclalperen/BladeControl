namespace BladeControl.Razer.Tests;

[TestClass]
public sealed class FanRpmTests
{
    [TestMethod]
    public void EveryHundredRpmIncrementInRangeIsAccepted()
    {
        for (int rpm = FanRpm.MinimumValue; rpm <= FanRpm.MaximumValue; rpm += 100)
        {
            var value = new FanRpm(rpm);
            Assert.AreEqual(rpm, value.Value);
        }

        Assert.AreEqual(2000, FanRpm.Minimum.Value);
        Assert.AreEqual(5000, FanRpm.Maximum.Value);
    }

    [DataTestMethod]
    [DataRow(1999)]
    [DataRow(5001)]
    [DataRow(5100)]
    public void OutOfRangeRpmIsRejected(int rpm)
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new FanRpm(rpm));
    }

    [TestMethod]
    public void NonHundredIncrementIsRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => new FanRpm(2050));
    }

    [TestMethod]
    public void DefaultValueCannotEnterFixedProfile()
    {
        Assert.ThrowsException<ArgumentException>(
            () => FanControlProfile.Fixed(default, new FanRpm(3000)));
    }
}
