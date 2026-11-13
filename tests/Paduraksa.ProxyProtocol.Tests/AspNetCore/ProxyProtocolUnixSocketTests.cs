using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Paduraksa.ProxyProtocol.AspNetCore;

namespace Paduraksa.ProxyProtocol.Tests;

/// <summary>Unix socket listeners (no peer address to trust), and the startup warning for a listener that trusts nobody.</summary>
public sealed class ProxyProtocolUnixSocketTests : IAsyncLifetime
{
    private const string Request = "PROXY TCP4 203.0.113.7 192.0.2.1 50123 443\r\nGET / HTTP/1.1\r\nHost: test\r\nConnection: close\r\n\r\n";

    private readonly string _directory = Directory.CreateTempSubdirectory("pp-").FullName;
    private readonly ConcurrentQueue<(string Listener, ProxyProtocolOutcome Outcome)> _outcomes = [];
    private readonly CapturedLogs _logs = new();
    private IHost? _host;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Path(string listener) => System.IO.Path.Combine(_directory, listener + ".sock");

    public async ValueTask InitializeAsync()
    {
        _host = new HostBuilder()
            .ConfigureLogging(logging => logging.AddProvider(_logs))
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel =>
                {
                    kestrel.ListenUnixSocket(Path("trusted"), l => l.UseProxyProtocol(o =>
                    {
                        o.TrustUnixSockets = true;
                        o.OnOutcome = (outcome, _) => _outcomes.Enqueue(("trusted", outcome));
                    }));

                    // Every network trusted, and still refused: a Unix peer has no address for them to match.
                    kestrel.ListenUnixSocket(Path("default"), l => l.UseProxyProtocol(o =>
                    {
                        o.TrustedProxies = [System.Net.IPNetwork.Parse("0.0.0.0/0"), System.Net.IPNetwork.Parse("::/0")];
                        o.OnOutcome = (outcome, _) => _outcomes.Enqueue(("default", outcome));
                    }));
                    kestrel.Listen(System.Net.IPAddress.Loopback, 0, l => l.UseProxyProtocol(static _ => { }));
                    kestrel.Listen(System.Net.IPAddress.Loopback, 0, l => l.UseProxyProtocol(static o => o.Required = false));
                    kestrel.Listen(System.Net.IPAddress.Loopback, 0, l => l.UseProxyProtocol(static o => o.TrustedProxies = [System.Net.IPNetwork.Parse("10.0.0.0/8")]));
                })
                .Configure(app => app.Run(static context => context.Response.WriteAsync(
                    $"{context.Connection.RemoteIpAddress}|{context.Connection.RemotePort}|{context.Features.Get<IProxyProtocolFeature>()?.ProxyEndPoint is UnixDomainSocketEndPoint}",
                    context.RequestAborted))))
            .Build();
        await _host.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }

        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task ATrustedUnixSocketListenerHonoursTheHeader()
    {
        var response = await ExchangeAsync("trusted");

        Assert.StartsWith("HTTP/1.1 200 ", response, StringComparison.Ordinal);
        Assert.Contains("203.0.113.7|50123|True", response, StringComparison.Ordinal);
        Assert.Contains(("trusted", ProxyProtocolOutcome.Accepted), _outcomes);
    }

    [Fact]
    public async Task AUnixSocketListenerRefusesEveryConnectionUntilItIsTrusted()
    {
        var response = await ExchangeAsync("default");

        Assert.Empty(response);
        Assert.Contains(("default", ProxyProtocolOutcome.UntrustedSource), _outcomes);
    }

    [Fact]
    public void AListenerThatTrustsNobodySaysSoOnceAtStartup()
    {
        var warnings = _logs.Entries.Where(static e => e.Level == LogLevel.Warning).Select(static e => e.Message).ToArray();

        // The untrusted Unix socket, the required TCP listener with no proxies, and the optional one with none; not the two that trust something.
        Assert.Equal(3, warnings.Length);
        Assert.Single(warnings, static w => w.Contains("refuses every connection", StringComparison.Ordinal) && w.Contains("TrustUnixSockets", StringComparison.Ordinal));
        Assert.Single(warnings, static w => w.Contains("refuses every connection", StringComparison.Ordinal) && w.Contains("TrustedProxies is empty", StringComparison.Ordinal));
        Assert.Single(warnings, static w => w.Contains("honours no PROXY protocol header", StringComparison.Ordinal) && w.Contains("TrustedProxies is empty", StringComparison.Ordinal));
    }

    private async Task<string> ExchangeAsync(string listener)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var received = new List<byte>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(Path(listener)), timeout.Token);
            await socket.SendAsync(Encoding.ASCII.GetBytes(Request), SocketFlags.None, timeout.Token);
            var buffer = new byte[4096];
            int read;
            while ((read = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token)) > 0)
            {
                received.AddRange(buffer.AsSpan(0, read));
            }
        }
        catch (SocketException)
        {
            // The server aborts an untrusted peer unread, so the reset can beat the write.
        }

        return Encoding.ASCII.GetString([.. received]);
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<(string Category, LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == "Paduraksa.ProxyProtocol.AspNetCore")
                {
                    entries.Enqueue((category, logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
