using UdpGame.Reliability;

namespace UdpGame.Reliability.Tests;

[TestClass]
public sealed class AdaptiveTimeoutTests
{
    [TestMethod]
    public void FirstSampleInitializesSrttAndRttVar()
    {
        var timeout = new AdaptiveTimeout();
        timeout.OnSample(100);
        Assert.IsTrue(timeout.IsInitialized);
        Assert.AreEqual(100d, timeout.SrttMs, 0.0001);
        Assert.AreEqual(50d, timeout.RttVarMs, 0.0001);
    }

    [TestMethod]
    public void FirstSampleCalculatesExpectedRto()
    {
        var timeout = new AdaptiveTimeout();
        timeout.OnSample(100);
        Assert.AreEqual(300d, timeout.RtoMs, 0.0001);
    }

    [TestMethod]
    public void SubsequentSampleUpdatesSrtt()
    {
        var timeout = new AdaptiveTimeout();
        timeout.OnSample(100);
        timeout.OnSample(200);
        Assert.AreEqual(112.5, timeout.SrttMs, 0.0001);
        Assert.AreEqual(362.5, timeout.RtoMs, 0.0001);
    }

    [TestMethod]
    public void SubsequentSampleUpdatesRttVar()
    {
        var timeout = new AdaptiveTimeout();
        timeout.OnSample(100);
        timeout.OnSample(200);
        Assert.AreEqual(62.5, timeout.RttVarMs, 0.0001);
        timeout.OnSample(80);
        Assert.AreEqual(55d, timeout.RttVarMs, 0.0001);
        Assert.AreEqual(108.4375, timeout.SrttMs, 0.0001);
        Assert.AreEqual(328.4375, timeout.RtoMs, 0.0001);
    }

    [TestMethod]
    public void RtoIsClampedToMinimum()
    {
        var timeout = new AdaptiveTimeout();
        timeout.OnSample(1);
        Assert.AreEqual(100d, timeout.RtoMs);
        timeout.OnSample(0);
        Assert.AreEqual(100d, timeout.RtoMs);
    }

    [TestMethod]
    public void RtoIsClampedToMaximum()
    {
        var timeout = new AdaptiveTimeout();
        timeout.OnSample(2000);
        Assert.AreEqual(3000d, timeout.RtoMs);
        timeout.OnSample(2500);
        Assert.AreEqual(3000d, timeout.RtoMs);
    }

    [TestMethod]
    public void DefaultRtoBeforeInitializationIsDefined()
    {
        var timeout = new AdaptiveTimeout();
        Assert.IsFalse(timeout.IsInitialized);
        Assert.AreEqual(1000d, timeout.RtoMs);
        Assert.AreEqual(0d, timeout.SrttMs);
        Assert.AreEqual(0d, timeout.RttVarMs);
    }

    [TestMethod]
    public void InvalidSamplesAreRejectedWithoutChangingState()
    {
        var timeout = new AdaptiveTimeout();
        foreach (double sample in new[] { -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => timeout.OnSample(sample));
        }

        Assert.IsFalse(timeout.IsInitialized);
        timeout.OnSample(100);
        Assert.Throws<ArgumentOutOfRangeException>(() => timeout.OnSample(double.NaN));
        Assert.AreEqual(100d, timeout.SrttMs);
        Assert.AreEqual(50d, timeout.RttVarMs);
        Assert.AreEqual(300d, timeout.RtoMs);
    }
}
