using BenchmarkDotNet.Attributes;

using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Benchmarks;

/// <summary>
/// <see cref="HostTable.Find(ReadOnlySpan{char})"/> over the raw <c>Host</c> header (port and trailing dot stripped by span) in a ~500-host
/// table: exact, case-folded, with a port, a single-label wildcard, and a miss (exact miss + wildcard miss).
/// </summary>
[MemoryDiagnoser]
public class HostTableBenchmarks
{
    private HostTable _table = HostTable.Empty;

    [Params("h123.test", "H123.TEST", "h123.test:8443", "a.w7.test", "nope.example.org")]
    public string Host { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _table = BenchConfig.Snapshot.Hosts;
        var shouldHit = Host != "nope.example.org";
        if ((_table.Find(Host) is not null) != shouldHit)
        {
            throw new InvalidOperationException($"'{Host}' should {(shouldHit ? "" : "not ")}be found.");
        }
    }

    [Benchmark]
    public CompiledHost? Find() => _table.Find(Host);
}
