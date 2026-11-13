// End-to-end benchmark runner: the real PaduraksaHost (core + public + admin), configured through its config service.
//
//   dotnet run -c Release --project bench/Paduraksa.Bench.Runner -- \
//       --upstream http://127.0.0.1:18080 [--http 18081] [--https 18443] [--admin 18943] [--cloudflare-tunnel true] [--websocket-upstream http://127.0.0.1:18090]
//
// Hosts:
//   bench.test          proxy host, one catch-all route to --upstream (YARP)
//   respond.test        Respond host: the gate answers 200 "ok\n" itself
//   access-log.test     like bench.test, with the access log on (the production default; the others turn it off)
//   rate-limit.test     like bench.test, under a policy whose rate and burst are far above the load (no 429s)
//   rate-limited.test   like bench.test, under 1 request per hour with a burst of 1: every request is refused
//   browser-check.test  like bench.test, behind the proof-of-work gate at 8 bits
//   error-page.test     like bench.test, but its upstream is a closed loopback port, so forwarding fails
//   compressed.test     like bench.test, with response compression on (Brotli or gzip, as the client asks)
//   headers.test        like bench.test, with custom header rules: request set x2, add, remove; response set x2, remove
//   rewrite.test        one route, /app stripped and /api put in front (Location mapping at its default, on)
//   rewrite-redirect.test  the same route, for an upstream that redirects into /api/
//   ws.test             like bench.test, but its upstream is the WebSocket echo server (--websocket-upstream)
//
// Configuration and logging are production's: the shipped appsettings.json (copied from Paduraksa.Server) plus the
// listener ports, a throwaway data directory (deleted on exit) and HTTP/3 off (the scenarios are TCP only; an H3
// listener would also add Alt-Svc to every response). The config goes in as one command once the host has started
// (revision #1 of the fresh database), like any admin change, and is live before READY is printed.
// HTTPS goes through the per-SNI certificate table: every host names the placeholder certificate (self-signed, in
// memory) as its own, so the handshake takes the exact-name path with the default TLS policy, as a configured host
// would. The placeholder is enabled explicitly (Paduraksa:Development:PlaceholderCertificate), as a release build needs.
//
// The new hosts share the one process on purpose: a single runner serves every scenario, so nothing restarts between
// them and each scenario's numbers keep the same warm process the h1 baseline had.
//
// Trusted proxies are off (production's default) unless --cloudflare-tunnel true: then loopback is cloudflared, so
// CF-Connecting-IP and CF-Visitor from oha are honoured (the `cf` scenario). Tunnel mode exercises the same
// resolver path as Cloudflare proxy mode; the edge ranges of proxy mode can't be injected from the runner.
//
// The browser-check host's difficulty is 8 bits, so the runner can solve one challenge itself at startup and print the
// cookie as a POW_COOKIE= line. A solve failure exits non-zero instead of letting the harness measure 403s.
//
// Output (stdout): "POW_COOKIE=..." once the config is live, then "READY ..." once every listener is bound, then one
// METRICS line per second (see RuntimeSampler). Stops gracefully on SIGINT/SIGTERM.
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

// The closed port the error-page host forwards to. It is inside the harness's own port block and nothing binds it.
const int ClosedUpstreamPort = 18079;
const int BrowserCheckDifficulty = 8;

var options = ParseArgs(args);
if (!options.TryGetValue("upstream", out var upstream) || !Uri.TryCreate(upstream, UriKind.Absolute, out _))
{
    await Console.Error.WriteLineAsync("usage: Paduraksa.Bench.Runner --upstream http://host:port [--http 18081] [--https 18443] [--admin 18943] [--cloudflare-tunnel true] [--websocket-upstream http://127.0.0.1:18090]");
    return 2;
}

var benchUpstreamId = UpstreamId.New();
var errorUpstreamId = UpstreamId.New();
var echoUpstreamId = UpstreamId.New();
var allowedPolicyId = RateLimitPolicyId.New();
var refusedPolicyId = RateLimitPolicyId.New();
CertificateBinding placeholderCertificate = new() { Mode = CertificateBindingMode.Specific, CertificateId = PlaceholderCertificates.Id };
ConfigDocument document = new(
    Settings: new GlobalSettings(
        // Access logs off, like every other proxy under test: the default is on, and logging every request would put
        // the writer's work into Paduraksa's measurement and into the host it shares with the benchmark's other work.
        // The access-log host turns it back on, which is what that scenario measures.
        Defaults: new HostSettings { AccessLog = false },
        TrustedProxies: Get("cloudflare-tunnel", "false") == "true"
            ? new TrustedProxySettings(TrustedProxyMode.CloudflareTunnel, ["127.0.0.1", "::1"])
            : new TrustedProxySettings()),
    Upstreams:
    [
        new UpstreamConfig(benchUpstreamId, "bench", Destinations: [new DestinationConfig(DestinationId.New(), upstream)]),
        // Nothing listens here, so every forward is a refused loopback connect: that is the error-page scenario.
        new UpstreamConfig(errorUpstreamId, "closed", Destinations: [new DestinationConfig(DestinationId.New(), $"http://127.0.0.1:{ClosedUpstreamPort}")]),
        new UpstreamConfig(
            echoUpstreamId, "echo", Destinations: [new DestinationConfig(DestinationId.New(), Get("websocket-upstream", "http://127.0.0.1:18090"))]),
    ],
    RateLimitPolicies:
    [
        // Far more tokens than a run can spend, and one client IP: the bucket is a single hot, contended key, and the
        // measurement is the CAS take itself rather than a refusal.
        new RateLimitPolicyConfig(
            allowedPolicyId, "bench-allowed", Requests: 1_000_000, Per: RateLimitPeriod.Second, Burst: 1_048_575, Key: RateLimitKey.ClientIp),
        // One token an hour with a burst of one. The pre-run probe spends it (the warm-up would too), so the
        // measured run is all 429s.
        new RateLimitPolicyConfig(refusedPolicyId, "bench-refused", Requests: 1, Per: RateLimitPeriod.Hour, Burst: 1, Key: RateLimitKey.ClientIp),
    ],
    Hosts:
    [
        new HostConfig(HostId.New(), ["bench.test"], Certificate: placeholderCertificate, Routes: [new RouteConfig(RouteId.New(), benchUpstreamId)]),
        new HostConfig(
            HostId.New(), ["respond.test"], Kind: HostKind.Respond, Certificate: placeholderCertificate, Respond: new RespondConfig(200, "ok\n")),
        new HostConfig(
            HostId.New(), ["access-log.test"], Certificate: placeholderCertificate,
            Settings: new HostSettings { AccessLog = true },
            Routes: [new RouteConfig(RouteId.New(), benchUpstreamId)]),
        new HostConfig(
            HostId.New(), ["rate-limit.test"], Certificate: placeholderCertificate,
            Settings: new HostSettings { RateLimit = new RateLimitSettings { PolicyId = allowedPolicyId } },
            Routes: [new RouteConfig(RouteId.New(), benchUpstreamId)]),
        new HostConfig(
            HostId.New(), ["rate-limited.test"], Certificate: placeholderCertificate,
            Settings: new HostSettings { RateLimit = new RateLimitSettings { PolicyId = refusedPolicyId } },
            Routes: [new RouteConfig(RouteId.New(), benchUpstreamId)]),
        new HostConfig(
            HostId.New(), ["browser-check.test"], Certificate: placeholderCertificate,
            // Eight bits is the lowest difficulty the config accepts, so the runner's own solve is quick.
            Settings: new HostSettings
            {
                ProofOfWork = new ProofOfWorkSettings { Enabled = true, Difficulty = BrowserCheckDifficulty, TokenLifetimeHours = 1 },
            },
            Routes: [new RouteConfig(RouteId.New(), benchUpstreamId)]),
        new HostConfig(
            HostId.New(), ["error-page.test"], Certificate: placeholderCertificate,
            Routes: [new RouteConfig(RouteId.New(), errorUpstreamId)]),
        new HostConfig(
            HostId.New(), ["compressed.test"], Certificate: placeholderCertificate,
            Settings: new HostSettings { Compression = new CompressionSettings { Enabled = true } },
            Routes: [new RouteConfig(RouteId.New(), benchUpstreamId)]),
        new HostConfig(
            HostId.New(), ["headers.test"], Certificate: placeholderCertificate,
            Settings: new HostSettings
            {
                Headers = new HeadersSettings
                {
                    Request =
                    [
                        new HeaderRuleConfig("X-Bench-Replace", HeaderOperation.Set, "replaced"),
                        new HeaderRuleConfig("X-Bench-Tenant", HeaderOperation.Set, "acme"),
                        new HeaderRuleConfig("X-Bench-Added", HeaderOperation.Add, "one"),
                        new HeaderRuleConfig("X-Bench-Drop", HeaderOperation.Remove),
                    ],
                    Response =
                    [
                        new HeaderRuleConfig("X-Frame-Options", HeaderOperation.Set, "DENY"),
                        new HeaderRuleConfig("X-Bench-Policy", HeaderOperation.Set, "max-age=31536000"),
                        new HeaderRuleConfig("X-Powered-By", HeaderOperation.Remove),
                    ],
                },
            },
            Routes: [new RouteConfig(RouteId.New(), benchUpstreamId)]),
        new HostConfig(
            HostId.New(), ["rewrite.test"], Certificate: placeholderCertificate,
            Routes: [new RouteConfig(RouteId.New(), benchUpstreamId, Path: "/app", Rewrite: new PathRewriteConfig(StripPrefix: true, AddPrefix: "/api"))]),
        new HostConfig(
            HostId.New(), ["rewrite-redirect.test"], Certificate: placeholderCertificate,
            Routes: [new RouteConfig(RouteId.New(), benchUpstreamId, Path: "/app", Rewrite: new PathRewriteConfig(StripPrefix: true, AddPrefix: "/api"))]),
        new HostConfig(HostId.New(), ["ws.test"], Certificate: placeholderCertificate, Routes: [new RouteConfig(RouteId.New(), echoUpstreamId)]),
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
    // The error-page scenario fails every forward, and YARP's forwarder logs a full stack trace at Warning for each
    // failure. Left on, the log writer (not the built-in 502 page) would be what that scenario measured; the
    // access-log scenario is where the logging cost belongs.
    ["Logging:LogLevel:Yarp.ReverseProxy.Forwarder"] = "None",
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

    string powCookie;
    try
    {
        powCookie = await ProofOfWorkSolver.SolveAsync(
            int.Parse(Get("http", "18081"), CultureInfo.InvariantCulture), "browser-check.test", BrowserCheckDifficulty, CancellationToken.None);
    }
    catch (InvalidOperationException exception)
    {
        await Console.Error.WriteLineAsync($"The browser-check proof of work could not be solved: {exception.Message}");
        return 1;
    }

    // Before READY: the harness reads this line from the log before it runs browser-check.
    Console.WriteLine($"POW_COOKIE={powCookie}");
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
