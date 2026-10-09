namespace UdpGame.Reliability;

public sealed class AdaptiveTimeout
{
    private const double Alpha = 0.125;
    private const double Beta = 0.25;
    private const double MinRtoMs = 100;
    private const double MaxRtoMs = 3000;

    public bool IsInitialized { get; private set; }
    public double SrttMs { get; private set; }
    public double RttVarMs { get; private set; }
    public double RtoMs { get; private set; } = 1000;

    public void OnSample(double rttMs)
    {
        if (!double.IsFinite(rttMs) || rttMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rttMs), "RTT must be finite and non-negative");
        }

        if (!IsInitialized)
        {
            SrttMs = rttMs;
            RttVarMs = rttMs / 2;
            IsInitialized = true;
        }
        else
        {
            // Variance uses the previous SRTT, before its update.
            RttVarMs = (1 - Beta) * RttVarMs + Beta * Math.Abs(SrttMs - rttMs);
            SrttMs = (1 - Alpha) * SrttMs + Alpha * rttMs;
        }

        RtoMs = Math.Clamp(SrttMs + 4 * RttVarMs, MinRtoMs, MaxRtoMs);
    }
}
