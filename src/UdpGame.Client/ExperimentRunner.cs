using System.Globalization;
using System.Net;
using System.Net.Sockets;
using UdpGame.Protocol;
using UdpGame.Reliability;
using UdpGame.Telemetry;
using UdpGame.Transport;

namespace UdpGame.Client;

internal static class ExperimentRunner
{
    private const int SamplesPerSeries = 50;
    private const int IntervalMs = 200;
    private const int TimeoutMs = 1000;

    public static int Run(string host, int port, string csvPath)
    {
        ulong experimentStartUs = MonotonicClock.NowMicroseconds();
        ushort nextSequence = 1;
        var rows = new List<CsvRow>(NetworkProfiles.ExperimentProfiles.Count * SamplesPerSeries);

        Console.WriteLine(
            $"Latency experiment: {NetworkProfiles.ExperimentProfiles.Count} series x " +
            $"{SamplesPerSeries} PING, interval={IntervalMs} ms, timeout={TimeoutMs} ms");

        foreach (NetworkProfile profile in NetworkProfiles.ExperimentProfiles)
        {
            TelemetryStatistics statistics = RunSeries(
                host,
                port,
                profile,
                experimentStartUs,
                ref nextSequence,
                rows);

            Console.WriteLine(
                $"{profile.Id}: sent={statistics.Sent} received={statistics.Received} " +
                $"timeout={statistics.Timeout} mean={Format(statistics.MeanRttMs)} ms " +
                $"srtt={Format(statistics.SrttMs)} ms jitter={statistics.JitterMs:F3} ms " +
                $"loss={statistics.LossRatePercent:F2}%");
        }

        int expectedRows = NetworkProfiles.ExperimentProfiles.Count * SamplesPerSeries;
        if (rows.Count != expectedRows)
        {
            throw new InvalidOperationException(
                $"Experiment produced {rows.Count} rows, expected {expectedRows}");
        }

        WriteCsv(csvPath, rows);
        Console.WriteLine($"CSV written: {csvPath} ({rows.Count} samples)");
        return 0;
    }

    private static TelemetryStatistics RunSeries(
        string host,
        int port,
        NetworkProfile profile,
        ulong experimentStartUs,
        ref ushort nextSequence,
        List<CsvRow> rows)
    {
        using var transport = UdpTransport.Connect(host, port, 20, profile);
        var adaptiveTimeout = new AdaptiveTimeout();
        var tracker = new TelemetryTracker(TimeoutMs, capacity: 128);
        var sampleBySequence = new Dictionary<ushort, int>();
        var completed = new HashSet<ushort>();

        int sentSamples = 0;
        ulong seriesStartUs = MonotonicClock.NowMicroseconds();
        ulong nextSendUs = seriesStartUs;

        while (sentSamples < SamplesPerSeries || tracker.PendingCount > 0)
        {
            ulong nowUs = MonotonicClock.NowMicroseconds();
            RecordExpired(tracker, nowUs, experimentStartUs, sampleBySequence, completed, rows);

            if (sentSamples < SamplesPerSeries && nowUs >= nextSendUs)
            {
                int sample = sentSamples + 1;
                ushort sequence = nextSequence++;
                ulong sendTimeUs = MonotonicClock.NowMicroseconds();

                tracker.RegisterPing(sequence, sendTimeUs, profile.Id);
                sampleBySequence.Add(sequence, sample);

                byte[] ping = ProtocolSerializer.SerializePing(sequence, new Ping(sendTimeUs));
                if (!transport.Send(ping))
                {
                    Console.WriteLine($"{profile.Id}: emulator dropped PING sample={sample} seq={sequence}");
                }

                sentSamples++;
                nextSendUs = seriesStartUs + (ulong)sentSamples * IntervalMs * 1000UL;
            }

            if (sentSamples >= SamplesPerSeries && tracker.PendingCount == 0)
            {
                break;
            }

            transport.SetReceiveTimeout(CalculateReceiveWaitMs(sentSamples, nextSendUs));

            try
            {
                var sender = new IPEndPoint(IPAddress.Any, 0);
                byte[] responseBytes = transport.Receive(ref sender);
                ulong receiveTimeUs = MonotonicClock.NowMicroseconds();
                Packet response = ProtocolSerializer.Deserialize(responseBytes);

                if (response.Payload is not Pong pong)
                {
                    continue;
                }

                InFlightMeasurement? measurement = tracker.Find(response.Header.SequenceNumber);
                if (measurement is not null && pong.ClientSendTimeUs != measurement.SendTimeUs)
                {
                    Console.WriteLine(
                        $"{profile.Id}: ignored PONG seq={response.Header.SequenceNumber} with mismatched ClientSendTimeUs");
                    continue;
                }

                PongResult result = tracker.RecordPong(response.Header.SequenceNumber, receiveTimeUs);
                if (result.Kind != PongResultKind.Received)
                {
                    Console.WriteLine(
                        $"{profile.Id}: {ResultName(result.Kind)} seq={response.Header.SequenceNumber}");
                }

                if (result.Kind == PongResultKind.Received &&
                    result.Measurement is not null &&
                    completed.Add(result.Measurement.SequenceNumber))
                {
                    adaptiveTimeout.OnSample(result.Measurement.RttMs!.Value);
                    rows.Add(CreateRow(
                        result.Measurement,
                        experimentStartUs,
                        sampleBySequence[result.Measurement.SequenceNumber],
                        "received"));
                }
            }
            catch (SocketException error) when (
                error.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
            {
            }
            catch (ProtocolException error)
            {
                Console.WriteLine($"{profile.Id}: malformed response ignored: {error.Message}");
            }
        }

        return tracker.GetStatistics();
    }

    private static int CalculateReceiveWaitMs(int sentSamples, ulong nextSendUs)
    {
        if (sentSamples >= SamplesPerSeries)
        {
            return 20;
        }

        ulong nowUs = MonotonicClock.NowMicroseconds();
        if (nextSendUs <= nowUs)
        {
            return 1;
        }

        return Math.Min(20, Math.Max(1, (int)Math.Ceiling((nextSendUs - nowUs) / 1000d)));
    }

    private static void RecordExpired(
        TelemetryTracker tracker,
        ulong nowUs,
        ulong experimentStartUs,
        Dictionary<ushort, int> sampleBySequence,
        HashSet<ushort> completed,
        List<CsvRow> rows)
    {
        foreach (InFlightMeasurement measurement in tracker.Expire(nowUs))
        {
            if (!completed.Add(measurement.SequenceNumber))
            {
                continue;
            }

            rows.Add(CreateRow(
                measurement,
                experimentStartUs,
                sampleBySequence[measurement.SequenceNumber],
                "timeout"));
        }
    }

    private static CsvRow CreateRow(
        InFlightMeasurement measurement,
        ulong experimentStartUs,
        int sample,
        string status)
    {
        return new CsvRow(
            measurement.ExperimentId,
            sample,
            measurement.SequenceNumber,
            (measurement.SendTimeUs - experimentStartUs) / 1000d,
            measurement.RttMs,
            measurement.SrttMs,
            status);
    }

    private static void WriteCsv(string path, List<CsvRow> rows)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(path, append: false);
        writer.WriteLine("experiment_id,sample,sequence,sent_at_ms,rtt_ms,srtt_ms,status");
        foreach (CsvRow row in rows.OrderBy(row => row.Sequence))
        {
            writer.WriteLine(string.Join(",",
                row.ExperimentId,
                row.Sample.ToString(CultureInfo.InvariantCulture),
                row.Sequence.ToString(CultureInfo.InvariantCulture),
                row.SentAtMs.ToString("F3", CultureInfo.InvariantCulture),
                FormatCsv(row.RttMs),
                FormatCsv(row.SrttMs),
                row.Status));
        }
    }

    private static string ResultName(PongResultKind kind) => kind switch
    {
        PongResultKind.Received => "received",
        PongResultKind.LateResponse => "late_response",
        PongResultKind.DuplicateResponse => "duplicate_response",
        PongResultKind.UnknownResponse => "unknown_response",
        _ => "unknown_response",
    };

    private static string Format(double? value) =>
        value?.ToString("F3", CultureInfo.InvariantCulture) ?? "n/a";

    private static string FormatCsv(double? value) =>
        value?.ToString("F3", CultureInfo.InvariantCulture) ?? string.Empty;

    private readonly record struct CsvRow(
        string ExperimentId,
        int Sample,
        ushort Sequence,
        double SentAtMs,
        double? RttMs,
        double? SrttMs,
        string Status);
}
