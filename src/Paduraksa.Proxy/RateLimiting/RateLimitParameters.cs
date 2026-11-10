namespace Paduraksa.Proxy.RateLimiting;

/// <summary>
/// One rate-limit policy's numbers, as the table uses them: a token-bucket rate in tokens per second and a burst
/// capacity. Immutable, so a published policy table can be swapped atomically.
/// </summary>
/// <remarks>
/// <para>The rate is quantized to 1/1024 of a 1/16 token per second (the field is <c>rate x 16 x 1024</c>), which keeps
/// the refill in integer arithmetic: <c>elapsedMs x rateUnits x 16 / 1000</c> as PLAN §7.3 states, one division by a
/// constant. Rates below 1/16384 token per second are refused, and so are rates above
/// <see cref="MaxTokensPerSecond"/> and bursts above <see cref="MaxBurst"/>: those would overflow the 24-bit token field
/// or the product the refill computes.</para>
/// <para><c>default</c> is not a policy (burst 0 fails the constructor's rules); a parameters array uses it as "no
/// policy at this slot".</para>
/// </remarks>
public readonly struct RateLimitParameters : IEquatable<RateLimitParameters>
{
    /// <summary>Largest burst: 2<sup>20</sup> - 1 whole tokens, which is what the 20.4-bit token field holds.</summary>
    public const int MaxBurst = 1_048_575;

    /// <summary>Largest accepted rate. Faster than this, any bucket is full again within a millisecond.</summary>
    public const double MaxTokensPerSecond = 1_000_000;

    /// <summary>One whole token in the state's units (the low 24 bits hold tokens x 16).</summary>
    internal const long TokenUnit = 16;

    private const long RateScale = 1024;
    private const double RateUnitsPerSecondPerToken = TokenUnit * RateScale;

    internal const long RefillDivisor = 1000 * RateScale;

    public RateLimitParameters(double tokensPerSecond, int burst)
    {
        if (!double.IsFinite(tokensPerSecond) || tokensPerSecond < 1 / RateUnitsPerSecondPerToken || tokensPerSecond > MaxTokensPerSecond)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tokensPerSecond),
                tokensPerSecond,
                $"A rate-limit rate must be between {1 / RateUnitsPerSecondPerToken} and {MaxTokensPerSecond} tokens per second.");
        }

        if (burst < 1 || burst > MaxBurst)
        {
            throw new ArgumentOutOfRangeException(nameof(burst), burst, $"A rate-limit burst must be between 1 and {MaxBurst} tokens.");
        }

        TokensPerSecond = tokensPerSecond;
        Burst = burst;
        RateUnitsPerSecond = (long)Math.Round(tokensPerSecond * RateUnitsPerSecondPerToken, MidpointRounding.AwayFromZero);
        BurstUnits = (long)burst * TokenUnit;

        // The longest elapsed time whose refill product still fits a long. Past it the bucket is full for any policy
        // that could have got here, so the refill saturates instead of computing.
        SafeElapsedMs = long.MaxValue / RateUnitsPerSecond;
        FullRefillMs = Math.Max(1, (BurstUnits * RefillDivisor + RateUnitsPerSecond - 1) / RateUnitsPerSecond);
    }

    public double TokensPerSecond { get; }

    public int Burst { get; }

    /// <summary>The rate in 1/16-token-per-second steps, times 1024 (see the remarks).</summary>
    internal long RateUnitsPerSecond { get; }

    internal long BurstUnits { get; }

    internal long SafeElapsedMs { get; }

    /// <summary>How long an empty bucket needs to fill again (burst / rate), in whole milliseconds, at least 1.</summary>
    internal long FullRefillMs { get; }

    public bool Equals(RateLimitParameters other) => RateUnitsPerSecond == other.RateUnitsPerSecond && Burst == other.Burst;

    public override bool Equals(object? obj) => obj is RateLimitParameters other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(RateUnitsPerSecond, Burst);

    public static bool operator ==(RateLimitParameters left, RateLimitParameters right) => left.Equals(right);

    public static bool operator !=(RateLimitParameters left, RateLimitParameters right) => !left.Equals(right);

    /// <summary>The tokens (in 1/16 units) that <paramref name="elapsedMs"/> earns; at most the whole field, so callers clamp.</summary>
    internal long RefillUnits(long elapsedMs) =>
        elapsedMs > SafeElapsedMs ? long.MaxValue : (elapsedMs * RateUnitsPerSecond) / RefillDivisor;

    /// <summary>
    /// The bucket after a refill: never above <paramref name="burstUnits"/>. A token count above the burst means the
    /// burst was lowered since the entry was last touched, and that is where it gets clamped (PLAN §6.3).
    /// </summary>
    internal static long Clamp(long units, long refillUnits, long burstUnits) =>
        refillUnits >= burstUnits - units ? burstUnits : units + refillUnits;

    /// <summary>Whole seconds until <paramref name="shortfallUnits"/> more units are available, rounded up, at least 1.</summary>
    internal int RetryAfterSeconds(long shortfallUnits)
    {
        var seconds = (shortfallUnits * RateScale + RateUnitsPerSecond - 1) / RateUnitsPerSecond;
        return (int)Math.Max(1, seconds);
    }
}
