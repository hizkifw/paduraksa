using BenchmarkDotNet.Attributes;

using Paduraksa.Proxy.Net;

namespace Paduraksa.Benchmarks;

/// <summary>
/// <see cref="CidrSet.Contains"/> (merged sorted ranges, binary search) at Cloudflare-list size (~20) and at a
/// WAF-list size (2,000). Half the blocks are IPv4 /24s, half IPv6 /48s, from a fixed seed.
/// </summary>
[MemoryDiagnoser]
public class CidrSetBenchmarks
{
    private CidrSet _set = CidrSet.Empty;
    private IpKey _hitV4;
    private IpKey _hitV6;
    private IpKey _missV4;

    [Params(20, 2_000)]
    public int Ranges { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        CidrSet.Builder builder = new();
        List<string> v4 = [];
        List<string> v6 = [];
        for (int i = 0; i < Ranges; i++)
        {
            if (i % 2 == 0)
            {
                // 11.0.0.0 – 126.255.255.0 /24s: never 10/8 or 127/8, so the miss below stays a miss.
                string block = $"{random.Next(11, 127)}.{random.Next(256)}.{random.Next(256)}.0/24";
                v4.Add(block);
                builder.Add(block);
            }
            else
            {
                string block = $"2001:db8:{random.Next(65536):x}::/48";
                v6.Add(block);
                builder.Add(block);
            }
        }

        _set = builder.Build();
        _hitV4 = IpKey.Parse(v4[v4.Count / 2].Replace("0/24", "77", StringComparison.Ordinal));
        _hitV6 = IpKey.Parse(v6[v6.Count / 2].Replace("::/48", ":1::5", StringComparison.Ordinal));
        _missV4 = IpKey.Parse("10.1.2.3");
        if (!_set.Contains(_hitV4) || !_set.Contains(_hitV6) || _set.Contains(_missV4))
        {
            throw new InvalidOperationException("Probe addresses don't hit/miss as intended.");
        }
    }

    [Benchmark]
    public bool HitV4() => _set.Contains(_hitV4);

    [Benchmark]
    public bool HitV6() => _set.Contains(_hitV6);

    [Benchmark]
    public bool MissV4() => _set.Contains(_missV4);
}
