using UdpGame.Telemetry;

namespace UdpGame.Telemetry.Tests;

[TestClass]
public sealed class TelemetryTrackerTests
{
    [TestMethod]
    public void Rtt_IsClientReceiveMinusClientSend()
    {
        var tracker = new TelemetryTracker();
        tracker.RegisterPing(1, 100_000, "baseline");

        PongResult result = tracker.RecordPong(1, 125_000);

        Assert.AreEqual(PongResultKind.Received, result.Kind);
        Assert.AreEqual(25d, result.Measurement!.RttMs!.Value, 0.0001d);
    }

    [TestMethod]
    public void FirstSrtt_EqualsFirstRtt()
    {
        var tracker = new TelemetryTracker();
        tracker.RegisterPing(1, 0, "baseline");

        PongResult result = tracker.RecordPong(1, 100_000);

        Assert.AreEqual(100d, result.Measurement!.SrttMs!.Value, 0.0001d);
    }

    [TestMethod]
    public void SubsequentSrtt_UsesRequiredSmoothing()
    {
        var tracker = new TelemetryTracker();
        tracker.RegisterPing(1, 0, "baseline");
        tracker.RecordPong(1, 100_000);
        tracker.RegisterPing(2, 200_000, "baseline");

        PongResult result = tracker.RecordPong(2, 400_000);

        Assert.AreEqual(112.5d, result.Measurement!.SrttMs!.Value, 0.0001d);
    }

    [TestMethod]
    public void Jitter_IsMeanAbsoluteChangeBetweenSuccessfulRtts()
    {
        var tracker = new TelemetryTracker();
        AddReceived(tracker, 1, 0, 100);
        AddReceived(tracker, 2, 1_000_000, 130);
        AddReceived(tracker, 3, 2_000_000, 90);

        TelemetryStatistics statistics = tracker.GetStatistics();

        Assert.AreEqual(35d, statistics.JitterMs, 0.0001d);
    }

    [TestMethod]
    public void Expire_MarksPendingAsTimeout()
    {
        var tracker = new TelemetryTracker(timeoutMilliseconds: 1000);
        tracker.RegisterPing(1, 0, "baseline");

        IReadOnlyList<InFlightMeasurement> expired = tracker.Expire(1_000_001);

        Assert.HasCount(1, expired);
        Assert.AreEqual(MeasurementStatus.Timeout, expired[0].Status);
    }

    [TestMethod]
    public void PongAfterExpire_IsLateResponse_NotDuplicate()
    {
        var tracker = new TelemetryTracker(timeoutMilliseconds: 1000);
        tracker.RegisterPing(1, 0, "baseline");
        tracker.Expire(1_000_001);

        PongResult result = tracker.RecordPong(1, 1_100_000);

        Assert.AreEqual(PongResultKind.LateResponse, result.Kind);
        Assert.AreEqual(MeasurementStatus.Timeout, result.Measurement!.Status);
    }

    [TestMethod]
    public void SecondPongAfterReceived_IsDuplicateResponse()
    {
        var tracker = new TelemetryTracker();
        tracker.RegisterPing(1, 0, "baseline");
        tracker.RecordPong(1, 100_000);

        PongResult result = tracker.RecordPong(1, 110_000);

        Assert.AreEqual(PongResultKind.DuplicateResponse, result.Kind);
    }

    [TestMethod]
    public void PongForUnknownSequence_IsUnknownResponse()
    {
        var tracker = new TelemetryTracker();

        PongResult result = tracker.RecordPong(99, 100_000);

        Assert.AreEqual(PongResultKind.UnknownResponse, result.Kind);
    }

    [TestMethod]
    public void LossRate_IsTimeoutsDividedBySent()
    {
        var tracker = new TelemetryTracker(timeoutMilliseconds: 1000);
        tracker.RegisterPing(1, 0, "baseline");
        tracker.RecordPong(1, 100_000);
        tracker.RegisterPing(2, 200_000, "baseline");
        tracker.Expire(1_200_001);

        TelemetryStatistics statistics = tracker.GetStatistics();

        Assert.AreEqual(50d, statistics.LossRatePercent, 0.0001d);
    }

    [TestMethod]
    public void Median_UsesSuccessfulRtts()
    {
        var tracker = new TelemetryTracker();
        AddReceived(tracker, 1, 0, 10);
        AddReceived(tracker, 2, 1_000_000, 30);
        AddReceived(tracker, 3, 2_000_000, 20);

        TelemetryStatistics statistics = tracker.GetStatistics();

        Assert.AreEqual(20d, statistics.MedianRttMs!.Value, 0.0001d);
    }

    private static void AddReceived(
        TelemetryTracker tracker,
        ushort sequence,
        ulong sendTimeUs,
        double rttMs)
    {
        tracker.RegisterPing(sequence, sendTimeUs, "baseline");
        tracker.RecordPong(sequence, sendTimeUs + (ulong)(rttMs * 1000d));
    }
}
