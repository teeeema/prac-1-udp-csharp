namespace UdpGame.Transport;

public sealed record NetworkProfile(
    string Id,
    int BaseDelayMs,
    int JitterMinMs,
    int JitterMaxMs,
    double LossPercent,
    int Seed);

public readonly record struct EmulationDecision(bool Drop, int DelayMs);

public static class NetworkProfiles
{
    private const int FixedSeed = 20260923;

    public static IReadOnlyList<NetworkProfile> ExperimentProfiles { get; } =
    [
        new("baseline", 0, 0, 0, 0d, FixedSeed),
        new("delay_50", 50, 0, 0, 0d, FixedSeed),
        new("delay_100", 100, 0, 0, 0d, FixedSeed),
        new("jitter", 0, 50, 150, 0d, FixedSeed),
        new("loss_5", 0, 0, 0, 5d, FixedSeed),
        new("combined", 100, 50, 150, 5d, FixedSeed),
    ];
}

public sealed class NetworkEmulator
{
    private readonly NetworkProfile _profile;
    private readonly Random _random;

    public NetworkEmulator(NetworkProfile profile)
    {
        _profile = profile;
        _random = new Random(profile.Seed);
    }

    public EmulationDecision Next()
    {
        bool drop = _profile.LossPercent > 0d &&
            _random.NextDouble() * 100d < _profile.LossPercent;

        int jitter = _profile.JitterMaxMs > 0
            ? _random.Next(_profile.JitterMinMs, _profile.JitterMaxMs + 1)
            : 0;

        return new EmulationDecision(drop, _profile.BaseDelayMs + jitter);
    }
}
