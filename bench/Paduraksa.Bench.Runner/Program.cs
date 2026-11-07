// End-to-end benchmark runner: the real PaduraksaHost (core + public + admin) with an in-memory proxy config.
//
//   dotnet run -c Release --project bench/Paduraksa.Bench.Runner -- \
//       --upstream http://127.0.0.1:18080 [--http 18081] [--https 18443] [--admin 18943] [--cloudflare-tunnel true]
//
// Hosts:
//   bench.test    proxy host, one catch-all route to --upstream (YARP)
//   respond.test  Respond host: the gate answers 200 "ok\n" itself
//
// Configuration and logging are production's: the shipped appsettings.json (copied from Paduraksa.Server) plus the
// listener ports, a throwaway data directory (deleted on exit) and HTTP/3 off (the scenarios are TCP only; an H3
// listener would also add Alt-Svc to every response).
// HTTPS goes through the per-SNI certificate table: both hosts name the placeholder certificate (self-signed, in
// memory) as their own, so the handshake takes the exact-name path with the default TLS policy, as a configured host
// would. TODO(M2/M3): switch to whatever replaces Placeholders.StartupProxyConfig and PlaceholderCertificates.
//
// Trusted proxies are off (production's default) unless --cloudflare-tunnel true: then loopback is cloudflared, so
// CF-Connecting-IP and CF-Visitor from oha are honoured (the `cf` scenario). Tunnel mode exercises the same
// resolver path as Cloudflare proxy mode; the edge ranges of proxy mode can't be injected from the runner.
//
// Output (stdout): "READY ..." once every listener is bound, then one METRICS line per second (see RuntimeSampler).
// Stops gracefully on SIGINT/SIGTERM.
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Bench.Runner;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Server.Hosting;
using Paduraksa.Server.Placeholders;

Dictionary<string, string> options = ParseArgs(args);
if (!options.TryGetValue("upstream", out string? upstream) || !Uri.TryCreate(upstream, UriKind.Absolute, out _))
{
    await Console.Error.WriteLineAsync("usage: Paduraksa.Bench.Runner --upstream http://host:port [--http 18081] [--https 18443] [--admin 18943] [--cloudflare-tunnel true]");
    return 2;
}

UpstreamId upstreamId = UpstreamId.New();
ProxyConfigModel model = new()
{
    Revision = ConfigRevisionId.New(),
    Upstreams = [new UpstreamSpec { Id = upstreamId, Destinations = [new DestinationSpec(DestinationId.New(), upstream)] }],
    Hosts =
    [
        new HostSpec
        {
            Id = HostId.New(),
            Names = ["bench.test"],
            Certificate = PlaceholderCertificates.Id,
            Routes = [new RouteSpec { Id = RouteId.New(), Upstream = upstreamId }],
        },
        new HostSpec
        {
            Id = HostId.New(),
            Names = ["respond.test"],
            Certificate = PlaceholderCertificates.Id,
            Kind = HostKind.Respond,
            Respond = new RespondSpec { Body = "ok\n" },
        },
    ],
    Defaults = new DefaultsSpec
    {
        TrustedProxies = Get("cloudflare-tunnel", "false") == "true"
            ? new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareTunnel, Proxies = ["127.0.0.1", "::1"] }
            : TrustedProxySpec.Off,
    },
};

string dataDirectory = Path.Combine(Path.GetTempPath(), "paduraksa-bench-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
{
    ["Paduraksa:DataDirectory"] = dataDirectory,
    ["Paduraksa:Listen:Http"] = Get("http", "18081"),
    ["Paduraksa:Listen:Https"] = Get("https", "18443"),
    ["Paduraksa:Listen:Http3"] = "false",
    ["Paduraksa:Listen:Admin"] = Get("admin", "18943"),
    ["Paduraksa:Listen:AdminBind"] = "127.0.0.1",
    ["Paduraksa:ShutdownTimeout"] = "00:00:05",
};

try
{
    await using PaduraksaHost host = PaduraksaHost.Create(new PaduraksaHostOptions
    {
        EnvironmentName = "Production",
        ContentRootPath = AppContext.BaseDirectory,
        ConfigureConfiguration = builder => builder.AddInMemoryCollection(settings),
        ConfigureServices = (role, services) =>
        {
            if (role == HostRole.Core)
            {
                services.AddSingleton(new StartupProxyConfig(model));
            }
        },
    });

    TaskCompletionSource stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        stop.TrySetResult();
    }

    using PosixSignalRegistration sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
    using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);

    await host.StartAsync();
    if (!host.Shared.SnapshotStore.HasPublished)
    {
        await Console.Error.WriteLineAsync("The startup config isn't live.");
        return 1;
    }

    Console.WriteLine(
        $"READY public={string.Join(',', host.GetListenAddresses(HostRole.Public))} " +
        $"gc={(GCSettings.IsServerGC ? "server" : "workstation")} cpus={Environment.ProcessorCount} " +
        $"trusted={model.Defaults.TrustedProxies.Mode} " +
        $"DOTNET_GCDynamicAdaptationMode={Environment.GetEnvironmentVariable("DOTNET_GCDynamicAdaptationMode") ?? "(default)"} " +
        $"DOTNET_GCHeapCount={Environment.GetEnvironmentVariable("DOTNET_GCHeapCount") ?? "(default)"}");

    using CancellationTokenSource sampling = new();
    Task sampler = RuntimeSampler.RunAsync(Console.Out, sampling.Token);
    await Task.WhenAny(stop.Task, host.ShutdownRequested);
    await sampling.CancelAsync();
    await sampler;
    await host.StopAsync();
    return 0;
}
finally
{
    if (Directory.Exists(dataDirectory))
    {
        Directory.Delete(dataDirectory, recursive: true);
    }
}

string Get(string key, string fallback) => options.TryGetValue(key, out string? value) ? value : fallback;

static Dictionary<string, string> ParseArgs(string[] args)
{
    Dictionary<string, string> parsed = new(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i + 1 < args.Length; i += 2)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected argument '{args[i]}'.");
        }

        parsed[args[i][2..]] = args[i + 1];
    }

    return parsed;
}
