using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

using Paduraksa.Bench.Runner;

namespace Paduraksa.Bench.Soak;

/// <summary>One request the mix asks for, and what counts as a good answer.</summary>
internal sealed class Probe
{
    public required string Protocol { get; init; }

    public required string Host { get; init; }

    public required string Path { get; init; }

    public HttpMethod Method { get; init; } = HttpMethod.Get;

    public Func<HttpContent>? Body { get; init; }

    public required int[] Ok { get; init; }

    /// <summary>Statuses the request provokes on purpose (a 429, a 403): they count as refused by design, not as errors.</summary>
    public int[] Refused { get; init; } = [];

    public Series Series { get; init; } = Series.Small;

    /// <summary>Looks at the answer; returns what is wrong with it, or null.</summary>
    public Func<HttpResponseMessage, byte[], string?>? Check { get; init; }

    public Action<HttpRequestMessage>? Prepare { get; init; }
}

/// <summary>The HTTP clients the load uses, one per protocol: a connection goes to loopback whatever the name says, so each request carries the real host in its SNI and Host.</summary>
internal sealed class TrafficClients : IDisposable
{
    private readonly HttpClient _h1;
    private readonly HttpClient _h2;
    private readonly HttpClient? _h3;
    private readonly HttpClient _http;

    public TrafficClients(SoakOptions options)
    {
        _h1 = Make(options.PublicHttps, tls: true, h2: false);
        _h2 = Make(options.PublicHttps, tls: true, h2: true);
        _http = Make(options.PublicHttp, tls: false, h2: false);
        if (options.Http3 && QuicConnection.IsSupported)
        {
            _h3 = Make(options.PublicHttps, tls: true, h2: false);
        }
    }

    public bool HasHttp3 => _h3 is not null;

    public HttpClient For(string protocol) => protocol switch
    {
        "h1" => _h1,
        "h2" => _h2,
        "h3" => _h3 ?? _h2,
        _ => _http,
    };

    public void Dispose()
    {
        _h1.Dispose();
        _h2.Dispose();
        _h3?.Dispose();
        _http.Dispose();
    }

    private static HttpClient Make(int port, bool tls, bool h2)
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 32,
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
            ConnectCallback = async (context, ct) => await Loopback.ConnectAsync(context.DnsEndPoint.Port, ct),
        };
        _ = tls;
        _ = port;
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestVersion = h2 ? HttpVersion.Version20 : HttpVersion.Version11,
            DefaultVersionPolicy = h2 ? HttpVersionPolicy.RequestVersionOrHigher : HttpVersionPolicy.RequestVersionExact,
        };
    }
}

internal static class Loopback
{
    public static async ValueTask<Stream> ConnectAsync(int port, CancellationToken ct)
    {
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>The mix of requests, at a fixed rate, in an open loop: a request is due at its scheduled time whether or not the earlier ones have finished, and its latency runs from then.</summary>
internal sealed class TrafficMix
{
    private const int MaxInFlight = 6000;

    private readonly SoakOptions _options;
    private readonly SoakConfig _config;
    private readonly TrafficClients _clients;
    private readonly Recorder _recorder;
    private readonly Events _events;
    private readonly PowCookie _pow;
    private readonly (string Name, int Weight, Func<Probe> Make)[] _mix;
    private readonly int _totalWeight;
    private int _inFlight;
    private long _canaryHighest;

    public TrafficMix(SoakOptions options, SoakConfig config, TrafficClients clients, Recorder recorder, Events events)
    {
        _options = options;
        _config = config;
        _clients = clients;
        _recorder = recorder;
        _events = events;
        _pow = new PowCookie(options.PublicHttp, SoakConfig.Name("pow"));
        var h3 = clients.HasHttp3;
        _mix =
        [
            ("page-h1", 250, () => Page(h3 && Random.Shared.Next(8) == 0 ? "h3" : "h1")),
            ("page-h2", 330, () => Page(h3 && Random.Shared.Next(6) == 0 ? "h3" : "h2")),
            ("page-http", 40, () => Page("http")),
            ("flap", 40, FlapPage),
            ("limit", 30, LimitPage),
            ("lan", 10, LanPage),
            ("rules", 15, RulesPage),
            ("auth", 15, AuthPage),
            ("headers", 20, HeadersPage),
            ("rewrite", 20, RewritePage),
            ("https-redirect", 20, ForceHttpsRedirect),
            ("https", 10, ForceHttpsPage),
            ("redirect", 10, RedirectHost),
            ("respond", 20, RespondHost),
            ("canary", 30, CanaryPage),
            ("download", 50, Download),
            ("upload", 35, Upload),
            ("compressed", 25, CompressedPage),
            ("unknown", 10, UnknownHost),
        ];
        // For finding what a symptom depends on: PADURAKSA_SOAK_MIXONLY=page-h2,canary keeps only those kinds. Never set by the script.
        if (Environment.GetEnvironmentVariable("PADURAKSA_SOAK_MIXONLY") is { Length: > 0 } only)
        {
            var keep = only.Split(',');
            _mix = _mix.Where(m => keep.Contains(m.Name)).ToArray();
        }

        _totalWeight = _mix.Sum(m => m.Weight);
    }

    public int PowSolves => _pow.Solves;

    public long CanaryHighest => Interlocked.Read(ref _canaryHighest);

    public int InFlight => Volatile.Read(ref _inFlight);

    public async Task RunAsync(CancellationToken ct)
    {
        var rate = Math.Max(1, _options.Rate);
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(5));
        var start = Stopwatch.GetTimestamp();
        long issued = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
                var due = (long)(elapsed * rate) - issued;
                var now = Stopwatch.GetTimestamp();
                for (var i = 0; i < due; i++)
                {
                    issued++;
                    if (Volatile.Read(ref _inFlight) >= MaxInFlight)
                    {
                        // The generator, not the server, is behind: say so, rather than queue forever.
                        _recorder.Skip();
                        continue;
                    }

                    _ = ExecuteAsync(Pick(), now, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The 429 client: a fixed 100 requests a second at the limited host, which the policy allows 20 of.</summary>
    public async Task RunLimitedAsync(int perSecond, CancellationToken ct)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(10));
        var start = Stopwatch.GetTimestamp();
        long issued = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var due = (long)(Stopwatch.GetElapsedTime(start).TotalSeconds * perSecond) - issued;
                var now = Stopwatch.GetTimestamp();
                for (var i = 0; i < due; i++)
                {
                    issued++;
                    _ = ExecuteAsync(
                        new Probe
                        {
                            Protocol = i % 2 == 0 ? "h2" : "h1",
                            Host = SoakConfig.Name("limited"),
                            Path = "/limited/" + Random.Shared.Next(1000),
                            Ok = [200],
                            Refused = [429],
                            Series = Series.Limited,
                            Check = (response, body) => response.StatusCode == HttpStatusCode.TooManyRequests && !response.Headers.Contains("Retry-After") && body.Length == 0 ? "a 429 with neither Retry-After nor a page" : null,
                        },
                        now,
                        ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task RunPowAsync(int perSecond, CancellationToken ct)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(10));
        var start = Stopwatch.GetTimestamp();
        long issued = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var due = (long)(Stopwatch.GetElapsedTime(start).TotalSeconds * perSecond) - issued;
                for (var i = 0; i < due; i++)
                {
                    issued++;
                    _ = PowRequestAsync(Stopwatch.GetTimestamp(), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Probe Pick()
    {
        var roll = Random.Shared.Next(_totalWeight);
        foreach (var (_, weight, make) in _mix)
        {
            if ((roll -= weight) < 0)
            {
                return make();
            }
        }

        return _mix[0].Make();
    }

    private async Task PowRequestAsync(long intended, CancellationToken ct)
    {
        Interlocked.Increment(ref _inFlight);
        var path = "/pow/" + Random.Shared.Next(500);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var cookie = await _pow.GetAsync(ct);
                using HttpRequestMessage request = new(HttpMethod.Get, $"http://{SoakConfig.Name("pow")}:{_options.PublicHttp}{path}");
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
                request.Headers.TryAddWithoutValidation("Accept", "text/html");
                using var response = await _clients.For("http").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                var body = await response.Content.ReadAsByteArrayAsync(ct);
                if (response.StatusCode == HttpStatusCode.OK && Encoding.UTF8.GetString(body).StartsWith("path=", StringComparison.Ordinal))
                {
                    _recorder.Done(Series.Small, Stopwatch.GetElapsedTime(intended).TotalMicroseconds, 200, false, SoakConfig.Name("pow"));
                    return;
                }

                // Not through: the challenge. Solve it once and send the same request again; a second miss is an error.
                _pow.Invalidate(cookie);
            }

            _recorder.Fail("wrong", SoakConfig.Name("pow"), path, "http", "the proof-of-work cookie was not accepted after a fresh solve");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _recorder.Fail(Recorder.KindOf(ex), SoakConfig.Name("pow"), path, "http", ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task ExecuteAsync(Probe probe, long intended, CancellationToken ct)
    {
        Interlocked.Increment(ref _inFlight);
        try
        {
            var scheme = probe.Protocol == "http" ? "http" : "https";
            var port = probe.Protocol == "http" ? _options.PublicHttp : _options.PublicHttps;
            // H3 can't use the connect callback (QUIC resolves the name itself), so it dials the address and names the host in Host.
            var authority = probe.Protocol == "h3" ? $"127.0.0.1:{port}" : $"{probe.Host}:{port}";
            using HttpRequestMessage request = new(probe.Method, $"{scheme}://{authority}{probe.Path}");
            request.Version = probe.Protocol switch
            {
                "h2" => HttpVersion.Version20,
                "h3" => HttpVersion.Version30,
                _ => HttpVersion.Version11,
            };
            request.VersionPolicy = probe.Protocol == "h3" ? HttpVersionPolicy.RequestVersionExact : probe.Protocol == "h2" ? HttpVersionPolicy.RequestVersionOrHigher : HttpVersionPolicy.RequestVersionExact;
            if (probe.Protocol == "h3")
            {
                request.Headers.Host = probe.Host;
            }

            if (probe.Body is not null)
            {
                request.Content = probe.Body();
            }

            probe.Prepare?.Invoke(request);
            using var response = await _clients.For(probe.Protocol).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            var micros = Stopwatch.GetElapsedTime(intended).TotalMicroseconds;
            var status = (int)response.StatusCode;
            var actualProtocol = response.Version.Major switch { 3 => "h3", 2 => "h2", _ => probe.Protocol == "http" ? "http" : "h1" };
            if (probe.Protocol is "h1" or "h2" or "h3" && actualProtocol != probe.Protocol)
            {
                _recorder.Fail("wrong", probe.Host, probe.Path, probe.Protocol, $"asked for {probe.Protocol}, answered over {response.Version}");
                return;
            }

            if (Array.IndexOf(probe.Ok, status) >= 0)
            {
                var problem = probe.Check?.Invoke(response, body);
                if (problem is not null)
                {
                    _recorder.Fail("wrong", probe.Host, probe.Path, probe.Protocol, problem);
                    return;
                }

                _recorder.Done(probe.Series, micros, status, refusedByDesign: false, probe.Host);
            }
            else if (Array.IndexOf(probe.Refused, status) >= 0)
            {
                var problem = probe.Check?.Invoke(response, body);
                if (problem is not null)
                {
                    _recorder.Fail("wrong", probe.Host, probe.Path, probe.Protocol, problem);
                    return;
                }

                _recorder.Done(probe.Series, micros, status, refusedByDesign: true, probe.Host);
            }
            else
            {
                _recorder.Fail(status >= 500 ? "status-5xx" : "status", probe.Host, probe.Path, probe.Protocol, $"HTTP {status}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _recorder.Fail(Recorder.KindOf(ex), probe.Host, probe.Path, probe.Protocol, ex.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message));
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private string AnyApp() => _config.AppNames[(int)(_config.AppNames.Count * Math.Pow(Random.Shared.NextDouble(), 2))];

    private static string AnyPath() => Random.Shared.Next(10) switch
    {
        0 => "/",
        1 => "/index.html",
        2 => "/static/app.js?v=" + Random.Shared.Next(50),
        3 => "/api/items/" + Random.Shared.Next(5000),
        _ => "/p/" + Random.Shared.Next(2000) + "/" + Random.Shared.Next(20),
    };

    private static string? PathEcho(HttpResponseMessage response, byte[] body, string expected) =>
        Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 300)).StartsWith("path=" + expected, StringComparison.Ordinal) ? null : "the upstream saw a different path than the one sent";

    private Probe Page(string protocol)
    {
        var path = AnyPath();
        var bare = path.Split('?')[0];
        return new Probe
        {
            Protocol = protocol,
            Host = AnyApp(),
            Path = path,
            Ok = [200],
            Check = (response, body) => PathEcho(response, body, bare),
        };
    }

    private Probe FlapPage()
    {
        var path = "/p/" + Random.Shared.Next(2000);
        return new Probe
        {
            Protocol = "h2",
            Host = _config.FlapNames[Random.Shared.Next(_config.FlapNames.Count)],
            Path = path,
            Ok = [200],
            Check = (response, body) => PathEcho(response, body, path),
        };
    }

    private Probe LimitPage() => new()
    {
        Protocol = Random.Shared.Next(2) == 0 ? "h1" : "h2",
        Host = _config.LimitNames[Random.Shared.Next(_config.LimitNames.Count)],
        Path = AnyPath().Split('?')[0],
        Ok = [200],
    };

    private static Probe LanPage() => new() { Protocol = "h2", Host = SoakConfig.Name("lan"), Path = "/lan/" + Random.Shared.Next(100), Ok = [200] };

    private static Probe RulesPage()
    {
        // One request in five is what the rule set exists to refuse.
        if (Random.Shared.Next(5) == 0)
        {
            var bad = Random.Shared.Next(3) switch { 0 => "/wp-admin/setup.php", 1 => "/.env", _ => "/p/1" };
            var agent = bad == "/p/1" ? "sqlmap/1.7" : "Mozilla/5.0";
            return new Probe
            {
                Protocol = "h1",
                Host = SoakConfig.Name("rules"),
                Path = bad,
                Ok = [],
                Refused = [403],
                Prepare = request => request.Headers.TryAddWithoutValidation("User-Agent", agent),
            };
        }

        return new Probe { Protocol = "h2", Host = SoakConfig.Name("rules"), Path = "/p/" + Random.Shared.Next(100), Ok = [200] };
    }

    private static Probe AuthPage() => Random.Shared.Next(20) == 0
        ? new Probe { Protocol = "h2", Host = SoakConfig.Name("auth"), Path = "/deny/" + Random.Shared.Next(10), Ok = [], Refused = [401, 403] }
        : new Probe
        {
            Protocol = "h2",
            Host = SoakConfig.Name("auth"),
            Path = "/ok/" + Random.Shared.Next(200),
            Ok = [200],
            Check = (response, body) => Encoding.UTF8.GetString(body, 0, Math.Min(40, body.Length)).StartsWith("path=", StringComparison.Ordinal) ? null : "the forward-auth host did not reach its upstream",
        };

    private static Probe HeadersPage() => new()
    {
        Protocol = "h2",
        Host = SoakConfig.Name("headers"),
        Path = "/h/" + Random.Shared.Next(100),
        Ok = [200],
        Prepare = request =>
        {
            request.Headers.TryAddWithoutValidation("X-Soak-Tenant", "mallory");
            request.Headers.TryAddWithoutValidation("X-Soak-Drop", "secret");
        },
        Check = (response, body) =>
        {
            if (!response.Headers.TryGetValues("X-Soak-Policy", out var policy) || policy.FirstOrDefault() != "v1")
            {
                return "the response header rule (X-Soak-Policy: v1) was not applied";
            }

            return response.Headers.TryGetValues("X-Soak-Seen-Tenant", out var tenant) && tenant.FirstOrDefault() == "acme"
                && response.Headers.TryGetValues("X-Soak-Seen-Drop", out var dropped) && dropped.FirstOrDefault() == "none"
                ? null
                : "the request header rules (set tenant, remove drop) were not applied upstream";
        },
    };

    private static Probe RewritePage()
    {
        var leaf = "/r" + Random.Shared.Next(500);
        return new Probe
        {
            Protocol = "h1",
            Host = SoakConfig.Name("rewrite"),
            Path = "/app" + leaf,
            Ok = [200],
            Check = (response, body) => Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 100)).StartsWith("path=/api" + leaf, StringComparison.Ordinal) ? null : "strip-prefix + add-prefix did not give /api" + leaf,
        };
    }

    private static Probe ForceHttpsRedirect() => new()
    {
        Protocol = "http",
        Host = SoakConfig.Name("https"),
        Path = "/x/" + Random.Shared.Next(100),
        Ok = [301, 307, 308],
        Check = (response, body) => response.Headers.Location?.Scheme == "https" ? null : "the redirect did not point at https",
    };

    private static Probe ForceHttpsPage() => new()
    {
        Protocol = "h2",
        Host = SoakConfig.Name("https"),
        Path = "/x/" + Random.Shared.Next(100),
        Ok = [200],
        Check = (response, body) => response.Headers.Contains("Strict-Transport-Security") ? null : "no HSTS header over TLS",
    };

    private static Probe RedirectHost() => new()
    {
        Protocol = "h1",
        Host = SoakConfig.Name("redirect"),
        Path = "/r/" + Random.Shared.Next(100),
        Ok = [301],
        Check = (response, body) => response.Headers.Location?.Host == SoakConfig.Name("app01") ? null : "the redirect host's Location was wrong",
    };

    private static Probe RespondHost() => new()
    {
        Protocol = "h2",
        Host = SoakConfig.Name("respond"),
        Path = "/",
        Ok = [200],
        Check = (response, body) => Encoding.UTF8.GetString(body) == "ok\n" ? null : "the respond host's body was not ok",
    };

    private Probe CanaryPage() => new()
    {
        Protocol = "h2",
        Host = SoakConfig.Name("canary"),
        Path = "/c",
        Ok = [200],
        Check = (response, body) =>
        {
            if (!response.Headers.TryGetValues(SoakConfig.RevisionHeader, out var values) || !long.TryParse(values.First(), out var seen))
            {
                return "the canary answered without its revision header";
            }

            // A revision once seen is never un-seen: the live snapshot only moves forward (the S6 publish ordering).
            long highest;
            while ((highest = Interlocked.Read(ref _canaryHighest)) < seen)
            {
                Interlocked.CompareExchange(ref _canaryHighest, seen, highest);
            }

            return seen < highest - CanaryReorderSlack ? $"the canary went back from revision {highest} to {seen}" : null;
        },
    };

    /// <summary>
    /// A request already in flight when a publish lands can come back from the older snapshot after a newer answer was seen on another connection. One revision of
    /// that is the race, not a regression: the check allows it and fails on any further step back.
    /// </summary>
    private const int CanaryReorderSlack = 3;

    private Probe Download()
    {
        var protocol = Random.Shared.Next(2) == 0 ? "h1" : "h2";
        return new Probe
        {
            Protocol = protocol,
            Host = AnyApp(),
            Path = "/big",
            Ok = [200],
            Series = Series.Big,
            Check = (response, body) => body.AsSpan().SequenceEqual(UpstreamNode.Big) ? null : $"a {body.Length}-byte download that is not the {UpstreamNode.BigBytes} bytes the upstream sent",
        };
    }

    private static readonly byte[] UploadBody = Enumerable.Range(0, 65_536).Select(i => (byte)(i * 7)).ToArray();

    private static readonly byte[] OversizedBody = new byte[SoakConfig.UploadLimitBytes + 4096];

    private Probe Upload()
    {
        // One upload in fifty is over the upload host's body limit and must be refused with a 413.
        if (Random.Shared.Next(50) == 0)
        {
            return new Probe
            {
                Protocol = "h2",
                Host = SoakConfig.Name("upload"),
                Path = "/upload",
                Method = HttpMethod.Post,
                Body = () => new ByteArrayContent(OversizedBody),
                Ok = [],
                Refused = [413],
                Series = Series.Big,
            };
        }

        return new Probe
        {
            Protocol = Random.Shared.Next(2) == 0 ? "h1" : "h2",
            Host = Random.Shared.Next(2) == 0 ? SoakConfig.Name("upload") : AnyApp(),
            Path = "/upload",
            Method = HttpMethod.Post,
            Body = () => new ByteArrayContent(UploadBody),
            Ok = [200],
            Series = Series.Big,
            Check = (response, body) => Encoding.UTF8.GetString(body) == "received=" + UploadBody.Length ? null : "the upstream received a different number of bytes: " + Encoding.UTF8.GetString(body),
        };
    }

    private Probe CompressedPage() => new()
    {
        Protocol = "h2",
        Host = SoakConfig.Name("headers"),
        Path = "/text",
        Ok = [200],
        Series = Series.Small,
        Prepare = request => request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip"),
        Check = (response, body) =>
        {
            var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
            return encoding is "br" or "gzip" && body.Length < 8000 ? null : $"/text came back {(encoding ?? "uncompressed")} at {body.Length} bytes";
        },
    };

    private Probe UnknownHost() => new()
    {
        Protocol = "http",
        Host = "nobody-" + Random.Shared.Next(1000) + ".unknown.test",
        Path = "/",
        Ok = [404],
    };
}

/// <summary>The proof-of-work cookie of the PoW host: solved on first use, again when the gate stops accepting it.</summary>
internal sealed class PowCookie(int port, string host)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cookie;

    public int Solves { get; private set; }

    public async Task<string> GetAsync(CancellationToken ct)
    {
        if (_cookie is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_cookie is null)
            {
                _cookie = await ProofOfWorkSolver.SolveAsync(port, host, SoakConfig.PowDifficulty, ct);
                Solves++;
            }

            return _cookie;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate(string cookie) => Interlocked.CompareExchange(ref _cookie, null, cookie);
}
