using BenchmarkDotNet.Attributes;

using Paduraksa.Proxy.RateLimiting;

namespace Paduraksa.Benchmarks;

/// <summary>
/// The rate-limit table's hot path in the three shapes that matter: one key over and over (one client on one policy),
/// a working set of 64 Ki keys (many clients, so the table itself has to be walked), and one key from every core at
/// once (a single loud client, which is all CAS contention on one entry).
/// </summary>
[MemoryDiagnoser]
public class RateLimitTableBenchmarks
{
    private const ushort Slot = 3;
    private const int ManyKeys = 64 * 1024;
    private const int Threads = 4;
    private const int CallsPerThread = 50_000;

    private RateLimitTable _table = null!;
    private RateLimitParameters _parameters;
    private ulong[] _hashes = [];
    private long _now;

    [GlobalSetup]
    public void Setup()
    {
        _table = new RateLimitTable();

        // Full refill within a millisecond and the largest burst the state can hold: every call below is an allowed one.
        _parameters = new(RateLimitParameters.MaxTokensPerSecond, RateLimitParameters.MaxBurst);

        _hashes = new ulong[ManyKeys];
        for (var i = 0; i < _hashes.Length; i++)
        {
            _hashes[i] = (ulong)i * 0x9E37_79B9_7F4A_7C15UL & RateLimitPartition.HashMask;
        }

        // Create the buckets before measuring so the first benchmark call isn't the slow path for every key.
        foreach (var hash in _hashes)
        {
            _ = _table.TryAcquire(Slot, hash, _parameters, nowMs: 0, out _);
        }

        _ = _table.TryAcquire(Slot, 0x1234, _parameters, nowMs: 0, out _);
    }

    [Benchmark]
    public bool SingleKey()
    {
        _now++;
        return _table.TryAcquire(Slot, 0x1234, _parameters, _now, out _);
    }

    [Benchmark(OperationsPerInvoke = ManyKeys)]
    public int ManyKeysLookup()
    {
        var taken = 0;
        for (var i = 0; i < ManyKeys; i++)
        {
            _now++;
            if (_table.TryAcquire(Slot, _hashes[i], _parameters, _now, out _))
            {
                taken++;
            }
        }

        return taken;
    }

    [Benchmark(OperationsPerInvoke = Threads * CallsPerThread)]
    public void Contended()
    {
        // A whole second of refill per invocation keeps the bucket full without the clock moving under the threads.
        _now += 1_000;
        var nowMs = _now;
        var table = _table;
        var parameters = _parameters;

        Parallel.For(
            0,
            Threads,
            worker =>
            {
                for (var i = 0; i < CallsPerThread; i++)
                {
                    _ = table.TryAcquire(Slot, 0x1234, parameters, nowMs, out _);
                }
            });
    }
}
