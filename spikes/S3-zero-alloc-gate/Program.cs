// Spike S3: zero-allocation gate on Kestrel. See docs/spikes/S3-zero-alloc-gate.md.
// Usage: dotnet run -c Release -- <scenario> [args]
//   selftest                          correctness checks + AllocAssert on the gate pieces (item 8)
//   bench [filter...]                 BenchmarkDotNet (HostLookup, HostSource, IpParse, Policy, Dispatch, Gate)
//   probe [requests=20000]            per-operation allocations on a real Kestrel, H1 + H2 (items 1-4, 6)
//   e2e <bare|gate> <h1|h2> <path> [conns=4] [depth=16] [warm=3] [measure=5] [cf] [xfoff] [noauthz] [browser] [generichost] [ticks] [vary]   (item 7)
//   upstream / load ...               child processes used by e2e
using System.Globalization;
using S3;

var scenario = args.Length > 0 ? args[0] : "selftest";
int Arg(int i, int def) => args.Length > i && int.TryParse(args[i], CultureInfo.InvariantCulture, out var v) ? v : def;

switch (scenario)
{
    case "selftest": return SelfTest.Run();
    case "bench": Bench.Run(args[1..]); return 0;
    case "probe": await Probe.RunAsync(Arg(1, 20000)); return 0;
    case "e2e":
        var flags = args.Skip(4).ToHashSet(StringComparer.Ordinal);
        return await E2E.RunAsync(new E2E.Options(args[1], args[2], args[3], Arg(4, 4), Arg(5, 16), Arg(6, 3), Arg(7, 5),
            flags.Contains("cf"), flags.Contains("xfoff"), flags.Contains("ticks"), flags.Contains("vary"), flags.Contains("noauthz"), flags.Contains("browser"), flags.Contains("generichost")));
    case "upstream": return await E2E.UpstreamAsync();
    case "load": return await E2E.LoadAsync(args);
    default: Console.Error.WriteLine("unknown scenario"); return 2;
}
