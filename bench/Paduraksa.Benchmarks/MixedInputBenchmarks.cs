using System.Net;

using BenchmarkDotNet.Attributes;

using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Benchmarks;

/// <summary>
/// The single-input benchmarks repeat one value, so the branch predictor and caches learn it. These cycle through
/// 256 mixed inputs per invocation (reported per operation via <c>OperationsPerInvoke</c>), closer to real traffic:
/// <list type="bullet">
/// <item>Hosts: 60 % exact, 10 % upper case, 10 % with a port, 10 % wildcard, 10 % unknown, over ~500 hosts.</item>
/// <item>Addresses: 50 % IPv4, 30 % IPv6, 20 % v4-mapped IPv6, all distinct.</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class MixedInputBenchmarks
{
    private const int Inputs = 256;

    private readonly string[] _hosts = new string[Inputs];
    private readonly string[] _addresses = new string[Inputs];
    private HostTable _table = HostTable.Empty;

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        _table = BenchConfig.Snapshot.Hosts;
        for (var i = 0; i < Inputs; i++)
        {
            var host = random.Next(BenchConfig.ExactHosts);
            _hosts[i] = (i % 10) switch
            {
                6 => $"H{host}.TEST",
                7 => $"h{host}.test:8443",
                8 => $"x{i}.w{random.Next(BenchConfig.WildcardHosts)}.test",
                9 => $"nope{i}.example.org",
                _ => $"h{host}.test",
            };

            _addresses[i] = (i % 10) switch
            {
                < 5 => $"{random.Next(1, 224)}.{random.Next(256)}.{random.Next(256)}.{random.Next(1, 255)}",
                < 8 => $"2001:db8:{random.Next(65536):x}:{random.Next(65536):x}::{random.Next(1, 65536):x}",
                _ => $"::ffff:{random.Next(1, 224)}.{random.Next(256)}.{random.Next(256)}.{random.Next(1, 255)}",
            };
        }

        foreach (var address in _addresses)
        {
            if (!IpParser.TryParse(address, out _) || !IPAddress.TryParse(address, out _))
            {
                throw new InvalidOperationException($"'{address}' doesn't parse.");
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Inputs)]
    public int HostTableFind()
    {
        var found = 0;
        foreach (var host in _hosts)
        {
            if (_table.Find(host) is not null)
            {
                found++;
            }
        }

        return found;
    }

    [Benchmark(OperationsPerInvoke = Inputs)]
    public UInt128 IpParserChars()
    {
        UInt128 sum = 0;
        foreach (var address in _addresses)
        {
            _ = IpParser.TryParse(address, out var key);
            sum ^= key.Value;
        }

        return sum;
    }

    [Benchmark(OperationsPerInvoke = Inputs)]
    public int BclIpAddressChars()
    {
        var hash = 0;
        foreach (var address in _addresses)
        {
            _ = IPAddress.TryParse(address.AsSpan(), out var parsed);
            hash ^= parsed!.GetHashCode();
        }

        return hash;
    }
}
