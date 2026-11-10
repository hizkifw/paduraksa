// End-to-end benchmark runner: the real PaduraksaHost (core + public + admin), configured through its config service.
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
// listener would also add Alt-Svc to every response). The config goes in as one command once the host has started
// (revision #1 of the fresh database), like any admin change, and is live before READY is printed.
// HTTPS goes through the per-SNI certificate table: both hosts name the placeholder certificate (self-signed, in
// memory) as their own, so the handshake takes the exact-name path with the default TLS policy, as a configured host
// would. The placeholder is enabled explicitly (Paduraksa:Development:PlaceholderCertificate), as a release build needs.
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

using Paduraksa.Application.Commands;
using Paduraksa.Bench.Runner;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Server.Hosting;
using Paduraksa.Server.Placeholders;

var options = ParseArgs(args);
if (!options.TryGetValue("upstream", out var upstream) || !Uri.TryCreate(upstream, UriKind.Absolute, out _))
{
    await Console.Error.WriteLineAsync("usage: Paduraksa.Bench.Runner --upstream http://host:port [--http 18081] [--https 18443] [--admin 18943] [--cloudflare-tunnel true]");
    return 2;
}

var upstreamId = UpstreamId.New();
CertificateBinding placeholderCertificate = new() { Mode = CertificateBindingMode.Specific, CertificateId = PlaceholderCertificates.Id };
ConfigDocument document = new(
    Settings: new GlobalSettings(
        TrustedProxies: Get("cloudflare-tunnel", "false") == "true"
            ? new TrustedProxySettings(TrustedProxyMode.CloudflareTunnel, ["127.0.0.1", "::1"])
            : new TrustedProxySettings()),
    Upstreams: [new UpstreamConfig(upstreamId, "bench", Destinations: [new DestinationConfig(DestinationId.New(), upstream)])],
    Hosts:
    [
        new HostConfig(HostId.New(), ["bench.test"], Certificate: placeholderCertificate, Routes: [new RouteConfig(RouteId.New(), upstreamId)]),
        new HostConfig(
            HostId.New(), ["respond.test"], Kind: HostKind.Respond, Certificate: placeholderCertificate, Respond: new RespondConfig(200, "ok\n")),
    ]);

var dataDirectory = Path.Combine(Path.GetTempPath(), "paduraksa-bench-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
{
    ["Paduraksa:DataDirectory"] = dataDirectory,
    ["Paduraksa:Listen:Http"] = Get("http", "18081"),
    ["Paduraksa:Listen:Https"] = Get("https", "18443"),
    ["Paduraksa:Listen:Http3"] = "false",
    ["Paduraksa:Listen:Admin"] = Get("admin", "18943"),
    ["Paduraksa:Listen:AdminBind"] = "127.0.0.1",
    ["Paduraksa:ShutdownTimeout"] = "00:00:05",
    // The benchmark serves the in-memory placeholder certificate, which a release build only has on request.
    ["Paduraksa:Development:PlaceholderCertificate"] = "true",
};

try
{
    await using var host = PaduraksaHost.Create(new PaduraksaHostOptions
    {
        EnvironmentName = "Production",
        ContentRootPath = AppContext.BaseDirectory,
        ConfigureConfiguration = builder => builder.AddInMemoryCollection(settings),
    });

    TaskCompletionSource stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        stop.TrySetResult();
    }

    using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
    using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);

    await host.StartAsync();
    var configured = await host.Shared.Config.ExecuteAsync(new ReplaceConfigCommand(document, BaseRevision: null), CommandOrigin.Cli);
    if (configured.Publish is not { Succeeded: true })
    {
        var errors = configured.Errors.Select(e => $"{e.Path}: {e.Message}").Concat(configured.Publish?.Errors ?? []);
        await Console.Error.WriteLineAsync($"The config isn't live ({configured.Status}): {string.Join("; ", errors)}");
        return 1;
    }

    Console.WriteLine(
        $"READY public={string.Join(',', host.GetListenAddresses(HostRole.Public))} " +
        $"gc={(GCSettings.IsServerGC ? "server" : "workstation")} cpus={Environment.ProcessorCount} " +
        $"trusted={document.Settings.TrustedProxies.Mode} " +
        $"DOTNET_GCDynamicAdaptationMode={Environment.GetEnvironmentVariable("DOTNET_GCDynamicAdaptationMode") ?? "(default)"} " +
        $"DOTNET_GCHeapCount={Environment.GetEnvironmentVariable("DOTNET_GCHeapCount") ?? "(default)"}");

    using CancellationTokenSource sampling = new();
    var sampler = RuntimeSampler.RunAsync(Console.Out, sampling.Token);
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

string Get(string key, string fallback) => options.TryGetValue(key, out var value) ? value : fallback;

static Dictionary<string, string> ParseArgs(string[] args)
{
    Dictionary<string, string> parsed = new(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected argument '{args[i]}'.");
        }

        parsed[args[i][2..]] = args[i + 1];
    }

    return parsed;
}
