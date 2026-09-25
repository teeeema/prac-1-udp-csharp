using System.Diagnostics;

namespace UdpGame.Telemetry;

public enum MeasurementStatus
{
    Pending,
    Received,
    Timeout,
}

public enum PongResultKind
{
    Received,
    LateResponse,
    DuplicateResponse,
    UnknownResponse,
}

public sealed class InFlightMeasurement
{
    internal InFlightMeasurement(
        ushort sequenceNumber,
        ulong sendTimeUs,
        string experimentId,
        int attempts)
    {
        SequenceNumber = sequenceNumber;
        SendTimeUs = sendTimeUs;
        ExperimentId = experimentId;
        Attempts = attempts;
    }

    public ushort SequenceNumber { get; }
    public ulong SendTimeUs { get; }
    public string ExperimentId { get; }
    public MeasurementStatus Status { get; internal set; } = MeasurementStatus.Pending;
    public int Attempts { get; }
    public double? RttMs { get; internal set; }
    public double? SrttMs { get; internal set; }
}

public readonly record struct PongResult(
    PongResultKind Kind,
    InFlightMeasurement? Measurement);

public readonly record struct TelemetryStatistics(
    int Sent,
    int Received,
    int Timeout,
    double? MinRttMs,
    double? MaxRttMs,
    double? MeanRttMs,
    double? MedianRttMs,
    double? SrttMs,
    double JitterMs,
    double LossRatePercent);

public sealed class TelemetryTracker
{
    private readonly Dictionary<ushort, InFlightMeasurement> _measurements = [];
    private readonly Queue<ushort> _insertionOrder = new();
    private readonly List<double> _successfulRtts = [];
    private readonly ulong _timeoutUs;
    private readonly int _capacity;
    private double? _srttMs;
    private double? _previousRttMs;
    private double _jitterDeltaSum;
    private int _jitterDeltaCount;
    private int _sent;
    private int _received;
    private int _timeout;

    public TelemetryTracker(int timeoutMilliseconds = 1000, int capacity = 1024)
    {
        if (timeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        }

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _timeoutUs = checked((ulong)timeoutMilliseconds * 1000UL);
        _capacity = capacity;
    }

    public int PendingCount => _measurements.Values.Count(item => item.Status == MeasurementStatus.Pending);

    public double? CurrentSrttMs => _srttMs;

    public void RegisterPing(
        ushort sequenceNumber,
        ulong sendTimeUs,
        string experimentId,
        int attempts = 1)
    {
        if (string.IsNullOrWhiteSpace(experimentId))
        {
            throw new ArgumentException("Experiment id is required", nameof(experimentId));
        }

        if (attempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempts));
        }

        if (_measurements.ContainsKey(sequenceNumber))
        {
            throw new InvalidOperationException($"Sequence {sequenceNumber} is already tracked");
        }

        MakeRoomIfNeeded();

        var measurement = new InFlightMeasurement(sequenceNumber, sendTimeUs, experimentId, attempts);
        _measurements.Add(sequenceNumber, measurement);
        _insertionOrder.Enqueue(sequenceNumber);
        _sent++;
    }

    public PongResult RecordPong(ushort sequenceNumber, ulong clientReceiveTimeUs)
    {
        if (!_measurements.TryGetValue(sequenceNumber, out InFlightMeasurement? measurement))
        {
            return new PongResult(PongResultKind.UnknownResponse, null);
        }

        if (measurement.Status == MeasurementStatus.Timeout)
        {
            return new PongResult(PongResultKind.LateResponse, measurement);
        }

        if (measurement.Status == MeasurementStatus.Received)
        {
            return new PongResult(PongResultKind.DuplicateResponse, measurement);
        }

        if (clientReceiveTimeUs < measurement.SendTimeUs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clientReceiveTimeUs),
                "Receive time cannot be earlier than send time");
        }

        double rttMs = (clientReceiveTimeUs - measurement.SendTimeUs) / 1000d;
        _srttMs = _srttMs is null
            ? rttMs
            : 0.875d * _srttMs.Value + 0.125d * rttMs;

        if (_previousRttMs is not null)
        {
            _jitterDeltaSum += Math.Abs(rttMs - _previousRttMs.Value);
            _jitterDeltaCount++;
        }

        _previousRttMs = rttMs;
        _successfulRtts.Add(rttMs);
        _received++;

        measurement.Status = MeasurementStatus.Received;
        measurement.RttMs = rttMs;
        measurement.SrttMs = _srttMs;
        return new PongResult(PongResultKind.Received, measurement);
    }

    public IReadOnlyList<InFlightMeasurement> Expire(ulong nowUs)
    {
        var expired = new List<InFlightMeasurement>();

        foreach (InFlightMeasurement measurement in _measurements.Values)
        {
            if (measurement.Status != MeasurementStatus.Pending || nowUs < measurement.SendTimeUs)
            {
                continue;
            }

            if (nowUs - measurement.SendTimeUs < _timeoutUs)
            {
                continue;
            }

            measurement.Status = MeasurementStatus.Timeout;
            measurement.SrttMs = _srttMs;
            _timeout++;
            expired.Add(measurement);
        }

        return expired;
    }

    public InFlightMeasurement? Find(ushort sequenceNumber) =>
        _measurements.GetValueOrDefault(sequenceNumber);

    public TelemetryStatistics GetStatistics()
    {
        double? min = _successfulRtts.Count == 0 ? null : _successfulRtts.Min();
        double? max = _successfulRtts.Count == 0 ? null : _successfulRtts.Max();
        double? mean = _successfulRtts.Count == 0 ? null : _successfulRtts.Average();
        double? median = CalculateMedian(_successfulRtts);
        double jitter = _jitterDeltaCount == 0 ? 0d : _jitterDeltaSum / _jitterDeltaCount;
        double lossRate = _sent == 0 ? 0d : _timeout * 100d / _sent;

        return new TelemetryStatistics(
            _sent,
            _received,
            _timeout,
            min,
            max,
            mean,
            median,
            _srttMs,
            jitter,
            lossRate);
    }

    private void MakeRoomIfNeeded()
    {
        while (_measurements.Count >= _capacity && _insertionOrder.Count > 0)
        {
            ushort oldestSequence = _insertionOrder.Peek();
            InFlightMeasurement oldest = _measurements[oldestSequence];

            if (oldest.Status == MeasurementStatus.Pending)
            {
                throw new InvalidOperationException("Telemetry inFlight capacity is full of pending measurements");
            }

            _insertionOrder.Dequeue();
            _measurements.Remove(oldestSequence);
        }
    }

    private static double? CalculateMedian(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        double[] sorted = [.. values.OrderBy(value => value)];
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2d;
    }
}

public static class MonotonicClock
{
    public static ulong NowMicroseconds()
    {
        long timestamp = Stopwatch.GetTimestamp();
        return checked((ulong)(timestamp * (1_000_000d / Stopwatch.Frequency)));
    }
}
