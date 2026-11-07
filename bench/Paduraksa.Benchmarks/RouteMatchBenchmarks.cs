using BenchmarkDotNet.Attributes;

using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Benchmarks;

/// <summary>
/// Canonical path view + route pick, the way the gate does it: <see cref="RoutePath.IsCanonical"/>, and when that
/// fails <see cref="RoutePath.Canonicalize(ReadOnlySpan{char}, Span{char})"/> into a stack buffer, then
/// <see cref="CompiledHost.Match"/> over a host with 9 ordered routes.
/// </summary>
[MemoryDiagnoser]
public class RouteMatchBenchmarks
{
    private CompiledHost _host = null!;

    [Params(
        "/api/v1/items/42",            // first route, canonical
        "/assets/app.3f9a1c.js",       // 8th route, canonical
        "/dashboard/settings",         // falls through to the catch-all
        "/api\\v1//items/./42;jsessionid=x")] // needs rewriting: '\', '//', '.', ';params'
    public string Path { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _host = BenchConfig.App;
        if (Pick() is null)
        {
            throw new InvalidOperationException($"No route for '{Path}'.");
        }
    }

    /// <summary>The canonical check alone (the common case for real traffic).</summary>
    [Benchmark]
    public bool IsCanonical() => RoutePath.IsCanonical(Path);

    /// <summary>Canonical view + first-match route pick (the gate's <c>MatchRoute</c>).</summary>
    [Benchmark]
    public CompiledRoute? Pick()
    {
        ReadOnlySpan<char> path = Path;
        if (RoutePath.IsCanonical(path))
        {
            return _host.Match(path);
        }

        Span<char> buffer = stackalloc char[RoutePath.StackLimit];
        return _host.Match(buffer[..RoutePath.Canonicalize(path, buffer)]);
    }
}
