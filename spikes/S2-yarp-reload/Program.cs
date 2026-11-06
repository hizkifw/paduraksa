// Spike S2: YARP config churn + endpoint metadata binding. See docs/spikes/S2-yarp-reload.md.
// Usage: dotnet run -c Release -- <metadata|reload|load|health|all> [args]
//   reload [sizes=100,500,2000] [modes=one-route,10pct-routes,10pct-clusters,noop,gen-ids,path-only]
//   load   [hosts=500] [concurrency=64] [secondsPerPhase=15]
//   gate                                   (review follow-up probe (i)-(v): gate-selected YARP endpoints)
//   gate-cost [sizes=500,2000]             (publish cost of the gate design)
//   gate-load <GateSelect|RoutingFirst> [hosts=500] [concurrency=64] [seconds=5] [pending]
using System.Globalization;
using S2;

var scenario = args.Length > 0 ? args[0] : "all";
int Arg(int i, int def) => args.Length > i ? int.Parse(args[i], CultureInfo.InvariantCulture) : def;

switch (scenario)
{
    case "metadata": await Scenarios.MetadataAsync(); break;
    case "reload":
        await Scenarios.ReloadAsync(args.Length > 1 ? args[1].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray() : [100, 500, 2000],
            args.Length > 2 ? args[2].Split(',') : null);
        break;
    case "load": await Scenarios.LoadAsync(Arg(1, 500), Arg(2, 64), Arg(3, 15)); break;
    case "health": await Scenarios.HealthAsync(); break;
    case "gate":
        await GateScenarios.ProbeAsync();
        await GateScenarios.AllocBaselineAsync();
        break;
    case "gate-cost":
        foreach (var n in args.Length > 1 ? args[1].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)) : [500, 2000])
            await GateScenarios.PublishCostAsync(n);
        break;
    case "gate-load":
        await GateScenarios.LoadAsync(Enum.Parse<GateMode>(args[1]), Arg(2, 500), Arg(3, 64), Arg(4, 5), args.Length > 5 && args[5] == "pending");
        break;
    case "all":
        await Scenarios.MetadataAsync();
        await Scenarios.ReloadAsync([100, 500, 2000]);
        await Scenarios.LoadAsync(500, 64, 15);
        await Scenarios.HealthAsync();
        break;
    default: Console.Error.WriteLine("unknown scenario"); return 2;
}
return 0;
