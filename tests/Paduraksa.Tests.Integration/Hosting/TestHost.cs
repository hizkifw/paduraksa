using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Admin.Certificates;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Logging;
using Paduraksa.Server.Hosting;

namespace Paduraksa.Tests.Integration.Hosting;

/// <summary>Builds isolated <see cref="PaduraksaHost"/>s: ephemeral ports, loopback admin, own config, no globals.</summary>
internal static class TestHost
{
    /// <summary>
    /// The access log's file writer replaced by the null sink. Its <c>Flush</c> at shutdown waits for the writer thread to
    /// notice the request, which takes up to its one-second poll: the admin tests don't read <c>access.log</c>, and where the
    /// process's own writer is under test (the access-log end-to-end tests) the host is built without this.
    /// </summary>
    public static Action<SharedServices.Builder> NoAccessLogFile { get; } = static shared => shared.Replace<IAccessLogSink>(NullAccessLogSink.Instance);

    public static PaduraksaHost Create(
        EventLog? events = null,
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<HostRole, IServiceCollection>? configureServices = null,
        bool allowEphemeralPorts = true,
        Action<SharedServices.Builder>? configureShared = null,
        string? contentRootPath = null,
        LogCapture? logs = null,
        string? dataDirectory = null) =>
        PaduraksaHost.Create(CreateOptions(events, settings, configureServices, allowEphemeralPorts, configureShared, contentRootPath, logs, dataDirectory));

    /// <summary>The options <see cref="Create"/> builds the host from (for callers that build it themselves, e.g. the CLI).</summary>
    public static PaduraksaHostOptions CreateOptions(
        EventLog? events = null,
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<HostRole, IServiceCollection>? configureServices = null,
        bool allowEphemeralPorts = true,
        Action<SharedServices.Builder>? configureShared = null,
        string? contentRootPath = null,
        LogCapture? logs = null,
        string? dataDirectory = null)
    {
        Dictionary<string, string?> config = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Paduraksa:DataDirectory"] = dataDirectory ?? TestDataDirectories.NewPath(), // the config database and last-good.json
            ["Paduraksa:Listen:Http"] = "0",
            ["Paduraksa:Listen:Https"] = "0",
            ["Paduraksa:Listen:Http3"] = "false", // needs a fixed port; the QUIC tests pick one
            ["Paduraksa:Listen:Admin"] = "0",
            ["Paduraksa:Listen:AdminBind"] = "127.0.0.1",
            ["Paduraksa:ShutdownTimeout"] = "00:00:05",
            ["Paduraksa:Development:PlaceholderCertificate"] = "true", // the tests are Release builds and serve (and provide) placeholder certificates
            ["Paduraksa:Admin:PasswordIterations"] = "100000", // the validator's floor: a test host creates an Owner and a few users, and each hash is real work
            ["Logging:LogLevel:Default"] = "Warning",
        };
        foreach ((string key, string? value) in settings ?? new Dictionary<string, string?>())
        {
            config[key] = value;
        }

        return new PaduraksaHostOptions
        {
            EnvironmentName = "Testing",
            AllowEphemeralPorts = allowEphemeralPorts,
            ContentRootPath = contentRootPath ?? AppContext.BaseDirectory,
            ConfigureConfiguration = builder => builder.AddInMemoryCollection(config),
            ConfigureShared = shared =>
            {
                if (events is not null)
                {
                    shared.Add(new DisposalProbe(events));
                }

                configureShared?.Invoke(shared);
            },
            ConfigureServices = (role, services) =>
            {
                if (events is not null)
                {
                    services.AddSingleton<IHostedService>(new LifecycleProbe(events, role));
                }

                configureServices?.Invoke(role, services);
            },
            ConfigureLogging = logs is null ? null : logging => logging.AddProvider(logs),
        };
    }

    /// <summary>Loopback URI of a bound listener, e.g. <c>https://127.0.0.1:41234/</c>. With HTTP/3 the HTTPS address is
    /// listed twice (TCP and UDP share it).</summary>
    public static Uri Endpoint(PaduraksaHost host, HostRole role, string scheme)
    {
        string address = host.GetListenAddresses(role).Distinct().Single(a => a.StartsWith(scheme + "://", StringComparison.Ordinal));
        return new UriBuilder(scheme, IPAddress.Loopback.ToString(), new Uri(address).Port).Uri;
    }

    /// <summary>
    /// HTTP client for the <b>public</b> listener: it trusts exactly the in-memory placeholder certificate the tests serve
    /// there (pinned, not "accept anything").
    /// </summary>
    public static HttpClient CreateClient(PaduraksaHost host)
    {
        string pin = host.Shared.ServerCertificate.Certificate.GetCertHashString(HashAlgorithmName.SHA256);
        SocketsHttpHandler handler = new()
        {
            UseCookies = false, // a test that wants a cookie sends it itself
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null
                    && string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), pin, StringComparison.Ordinal),
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// HTTP client for the <b>admin</b> listener (PLAN §3): it trusts <b>only</b> a certificate chaining to the process's local CA
    /// root, and by default still checks the name it connects to, so a test cannot pass on the placeholder fallback by
    /// accident (its SANs include <c>localhost</c> and the loopback addresses, which is what every test connects to). The
    /// root is read lazily, so a client built before the admin certificate exists still works once it does. The strict TLS
    /// assertions live in the dedicated handshake tests, which use <see cref="System.Net.Security.SslStream"/> directly.
    /// </summary>
    /// <remarks>
    /// <paramref name="checkServerName"/> false is for clients that deliberately send a <c>Host</c> header the certificate
    /// doesn't cover (the hardening tests reach the admin by a LAN name or a made-up one over a loopback connection;
    /// <c>SocketsHttpHandler</c> takes its TLS target host from that header, so the name check would refuse the handshake).
    /// <paramref name="allowPlaceholder"/> true is for the tests that deliberately reach a host serving the fallback (an empty
    /// vault, or one whose config store refuses changes).
    /// </remarks>
    public static HttpClient CreateAdminClient(
        PaduraksaHost host, Action<SocketsHttpHandler>? configure = null, bool checkServerName = true, bool allowPlaceholder = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        X509Certificate2? root = null;
        SocketsHttpHandler handler = new()
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (certificate is null
                        || (checkServerName && (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0))
                    {
                        return false;
                    }

                    // The OS trusts it (a real CA), or it chains to the process's local CA root.
                    return (errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) == System.Net.Security.SslPolicyErrors.None
                        || IsIssuedBy(certificate, root ??= LocalCaRoot(host))
                        || (allowPlaceholder && IsPinned(certificate, host.Shared.ServerCertificate.Certificate));
                },
            },
        };
        configure?.Invoke(handler);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static bool IsPinned(X509Certificate certificate, X509Certificate2 pin) =>
        string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), pin.GetCertHashString(HashAlgorithmName.SHA256), StringComparison.Ordinal);

    private static bool IsIssuedBy(X509Certificate certificate, X509Certificate2? root)
    {
        if (root is null)
        {
            return false;
        }

        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return chain.Build(leaf);
    }

    /// <summary>The process's local CA root (public data), or null before it exists.</summary>
    private static X509Certificate2? LocalCaRoot(PaduraksaHost host)
    {
        VaultCertificate? root = host.Shared.Catalog.ListAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult()
            .FirstOrDefault(static certificate => certificate.IsLocalCaRoot);
        return root is null ? null : X509CertificateLoader.LoadCertificate(CaRoot.PemToDer(root.Info.PublicChainPem));
    }
}

/// <summary>Captures every log entry that passes the configured filters (tests run at Warning and above).</summary>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner._entries.Enqueue(new LogEntry(category, logLevel, eventId, formatter(state, exception), exception));
    }
}

internal sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception)
{
    public override string ToString() => $"{Level} {Category}[{EventId.Id}] {Message}";
}

/// <summary>Thread-safe ordered record of lifecycle events across hosts.</summary>
internal sealed class EventLog
{
    private readonly ConcurrentQueue<string> _events = new();

    public IReadOnlyList<string> Events => [.. _events];

    public void Add(string entry) => _events.Enqueue(entry);
}

/// <summary>Records start/stop of the host it's registered in.</summary>
internal sealed class LifecycleProbe(EventLog events, HostRole role) : IHostedService
{
    public Func<CancellationToken, Task>? OnStop { get; init; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        events.Add($"{role}:start");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        events.Add($"{role}:stop");
        if (OnStop is not null)
        {
            await OnStop(cancellationToken);
        }
    }
}

/// <summary>A core background service that fails during startup, synchronously or after its first await.</summary>
internal sealed class CrashingService(bool afterYield) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (afterYield)
        {
            await Task.Yield();
        }

        throw new InvalidOperationException("Simulated crash during startup.");
    }
}

/// <summary>Stops its own host from <see cref="StartAsync"/>, deterministically, while the host is starting.</summary>
internal sealed class StopsHostOnStart(IHostApplicationLifetime lifetime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.StopApplication();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Holds its host's start in <see cref="StartAsync"/> until the start is cancelled.</summary>
internal sealed class BlocksStart : IHostedService
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Entered.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Public middleware holding <see cref="Path"/> requests until <see cref="Release"/> completes.</summary>
internal sealed class SlowEndpoint : IStartupFilter
{
    public const string Path = "/slow";

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Path != Path)
            {
                await nextMiddleware(context);
                return;
            }

            Entered.TrySetResult();
            await Release.Task;
            await context.Response.WriteAsync("drained\n");
        });
        next(app);
    };
}

/// <summary>A shared singleton that counts how often it's disposed.</summary>
internal sealed class DisposalProbe(EventLog events) : IAsyncDisposable
{
    private int _disposeCount;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        events.Add("shared:disposed");
        return ValueTask.CompletedTask;
    }
}
