using System.Net;
using System.Text;
using BenchmarkDotNet.Attributes;
using Paduraksa.Proxy.Net;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Benchmarks;

/// <summary>
/// Client-IP parsing (<c>CF-Connecting-IP</c>, <c>X-Forwarded-For</c> entries, PROXY v1 addresses): our span parser into
/// <see cref="IpKey"/>, from chars and from UTF-8, against <see cref="IPAddress.TryParse(ReadOnlySpan{char}, out IPAddress?)"/>,
/// which allocates the <see cref="IPAddress"/> (40 B for v4, more for v6).
/// </summary>
[MemoryDiagnoser]
public class IpParserBenchmarks
{
    private string _text = "";
    private byte[] _utf8 = [];

    [Params("203.0.113.7", "2001:db8:85a3::8a2e:370:7334", "::ffff:198.51.100.23")]
    public string Address { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _text = Address;
        _utf8 = Encoding.ASCII.GetBytes(Address);
        if (!IpParser.TryParse(_text, out _) || !IpParser.TryParse(_utf8, out _) || !IPAddress.TryParse(_text, out _))
        {
            throw new InvalidOperationException($"'{Address}' doesn't parse.");
        }
    }

    [Benchmark(Baseline = true)]
    public IPAddress? BclChars() => IPAddress.TryParse(_text.AsSpan(), out var address) ? address : null;

    [Benchmark]
    public IPAddress? BclUtf8() => IPAddress.TryParse(_utf8.AsSpan(), out var address) ? address : null;

    [Benchmark]
    public IpKey PaduraksaChars() => IpParser.TryParse(_text.AsSpan(), out var address) ? address : default;

    [Benchmark]
    public IpKey PaduraksaUtf8() => IpParser.TryParse(_utf8.AsSpan(), out var address) ? address : default;
}
