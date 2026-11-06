// Spike S1: does per-SNI certificate selection work on Kestrel's QUIC/HTTP/3 listener?
//
// One process: builds a throwaway CA (root -> intermediate -> leaves), starts one Kestrel app per
// "mechanism" (each on its own port, H1+H2+H3 unless stated otherwise), probes every app over
// H2 (TCP) and H3 (QUIC) for several SNI values, hot-swaps the certificate store, probes again,
// and prints a results table. Follow-up probes: unknown-SNI rejection (throw vs null), a QUIC
// connection-stage IP gate inside the TLS callback, and IPv6 dual-mode listeners.
// See docs/spikes/S1-http3-sni.md for the write-up.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace S1.Http3Sni;

internal static class Program
{
    private const int BasePort = 8443;
    private const int PublicHttpsPort = 443; // what Paduraksa would advertise in Alt-Svc
    private static readonly string[] Snis = ["a.test", "b.test", "x.wild.test", "unknown.test", "127.0.0.1"];

    // --no-https-config: skip UseKestrelHttpsConfiguration() to reproduce the CreateSlimBuilder startup failure.
    private static bool s_skipHttpsConfig;

    public static async Task<int> Main(string[] args)
    {
        s_skipHttpsConfig = args.Contains("--no-https-config");
        Console.WriteLine($"OS:                       {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Console.WriteLine($".NET runtime:             {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"ASP.NET Core (Kestrel):   {typeof(KestrelServerOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
        Console.WriteLine($"System.Net.Quic:          {typeof(QuicListener).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
        Console.WriteLine($"QuicListener.IsSupported:   {QuicListener.IsSupported}");
        Console.WriteLine($"QuicConnection.IsSupported: {QuicConnection.IsSupported}");
        Console.WriteLine();
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
        {
            Console.WriteLine("QUIC not supported here (libmsquic missing?). Aborting.");
            return 2;
        }

        var pki = Pki.Create();
        var store = new CertStore(pki.MapV1);
        // A second, non-loopback source address (the container's eth0) for the IP-gate and dual-mode probes.
        var containerIp = Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))?.ToString() ?? "127.0.0.1";
        Console.WriteLine($"Container IPv4 (2nd source address): {containerIp}; IP gate denies {DeniedIp}");
        Console.WriteLine();

        var mechanisms = new Mechanism[]
        {
            new("static-cert", "UseHttps(X509Certificate2) baseline (default cert only)", HttpProtocols.Http1AndHttp2AndHttp3, (lo, m) => lo.UseHttps(pki.Default.Cert)),
            new("selector", "HttpsConnectionAdapterOptions.ServerCertificateSelector", HttpProtocols.Http1AndHttp2AndHttp3, (lo, m) =>
                lo.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificateSelector = (conn, name) =>
                    {
                        m.Record(name, conn is null ? "conn=null" : $"conn={conn.GetType().Name}");
                        return store.Current.Select(name).Cert;
                    },
                })),
            new("selector+chain", "ServerCertificateSelector + ServerCertificateChain (intermediate)", HttpProtocols.Http1AndHttp2AndHttp3, (lo, m) =>
                lo.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificateChain = [pki.Intermediate],
                    ServerCertificateSelector = (conn, name) =>
                    {
                        m.Record(name, conn is null ? "conn=null" : $"conn={conn.GetType().Name}");
                        return store.Current.Select(name).Cert;
                    },
                })),
            new("tlscb-ctx", "TlsHandshakeCallbackOptions -> new SslServerAuthenticationOptions{ServerCertificateContext}", HttpProtocols.Http1AndHttp2AndHttp3, (lo, m) =>
                lo.UseHttps(new TlsHandshakeCallbackOptions
                {
                    OnConnection = ctx =>
                    {
                        m.Record(ctx.ClientHelloInfo.ServerName, $"conn={ctx.Connection?.GetType().Name ?? "null"}");
                        var e = store.Current.Select(ctx.ClientHelloInfo.ServerName);
                        return ValueTask.FromResult(new SslServerAuthenticationOptions { ServerCertificateContext = e.Context });
                    },
                })),
            new("tlscb-shared", "TlsHandshakeCallbackOptions -> prebuilt, shared SslServerAuthenticationOptions per cert", HttpProtocols.Http1AndHttp2AndHttp3, (lo, m) =>
                lo.UseHttps(new TlsHandshakeCallbackOptions
                {
                    OnConnection = ctx =>
                    {
                        m.Record(ctx.ClientHelloInfo.ServerName, $"conn={ctx.Connection?.GetType().Name ?? "null"}");
                        return ValueTask.FromResult(store.Current.Select(ctx.ClientHelloInfo.ServerName).SharedOptions);
                    },
                })),
            new("tlscb-shared-alpn", "TlsHandshakeCallbackOptions -> prebuilt, shared options with ALPN preset [h3,h2,http/1.1]", HttpProtocols.Http1AndHttp2AndHttp3, (lo, m) =>
                lo.UseHttps(new TlsHandshakeCallbackOptions
                {
                    OnConnection = ctx =>
                    {
                        m.Record(ctx.ClientHelloInfo.ServerName, $"conn={ctx.Connection?.GetType().Name ?? "null"}");
                        return ValueTask.FromResult(store.Current.Select(ctx.ClientHelloInfo.ServerName).SharedOptionsAlpn);
                    },
                })),
            new("tlscb-cert", "TlsHandshakeCallbackOptions -> SslServerAuthenticationOptions{ServerCertificate}", HttpProtocols.Http1AndHttp2AndHttp3, (lo, m) =>
                lo.UseHttps(new TlsHandshakeCallbackOptions
                {
                    OnConnection = ctx =>
                    {
                        m.Record(ctx.ClientHelloInfo.ServerName, $"conn={ctx.Connection?.GetType().Name ?? "null"}");
                        return ValueTask.FromResult(new SslServerAuthenticationOptions { ServerCertificate = store.Current.Select(ctx.ClientHelloInfo.ServerName).Cert });
                    },
                })),
            new("tlscb-selcb", "TlsHandshakeCallbackOptions -> SslServerAuthenticationOptions{ServerCertificateSelectionCallback}", HttpProtocols.Http1AndHttp2AndHttp3, (lo, m) =>
                lo.UseHttps(new TlsHandshakeCallbackOptions
                {
                    OnConnection = ctx => ValueTask.FromResult(new SslServerAuthenticationOptions
                    {
                        ServerCertificateSelectionCallback = (sender, name) =>
                        {
                            m.Record(name, $"sender={sender?.GetType().Name ?? "null"}");
                            return store.Current.Select(name).Cert;
                        },
                    }),
                })),
            new("optsel-cb", "UseHttps(ServerOptionsSelectionCallback, state)", HttpProtocols.Http1AndHttp2AndHttp3, (lo, m) =>
                lo.UseHttps((stream, hello, state, ct) =>
                {
                    m.Record(hello.ServerName, $"stream={stream?.GetType().Name ?? "null"}");
                    return ValueTask.FromResult(new SslServerAuthenticationOptions { ServerCertificateContext = store.Current.Select(hello.ServerName).Context });
                }, state: null!)),
            // Section 7.5 fallback: same port, two Listen calls. TCP keeps per-SNI selection, QUIC gets one fixed cert.
            // (The QUIC side deliberately uses HttpsConnectionAdapterOptions + ServerCertificateChain to see whether the chain is sent.)
            new("split", "2x Listen same port: TCP H1/H2 per-SNI (tlscb-ctx) + QUIC H3 fixed wildcard cert", HttpProtocols.None, null,
                ConfigureCustom: (o, port, m) =>
                {
                    o.Listen(IPAddress.Loopback, port, lo =>
                    {
                        lo.Protocols = HttpProtocols.Http1AndHttp2;
                        lo.UseHttps(new TlsHandshakeCallbackOptions
                        {
                            OnConnection = ctx =>
                            {
                                m.Record(ctx.ClientHelloInfo.ServerName, "tcp-listener");
                                return ValueTask.FromResult(new SslServerAuthenticationOptions { ServerCertificateContext = store.Current.Select(ctx.ClientHelloInfo.ServerName).Context });
                            },
                        });
                    });
                    o.Listen(IPAddress.Loopback, port, lo =>
                    {
                        lo.Protocols = HttpProtocols.Http3;
                        lo.UseHttps(new HttpsConnectionAdapterOptions
                        {
                            ServerCertificate = pki.H3Fixed.Cert,
                            ServerCertificateChain = [pki.Intermediate],
                        });
                    });
                }, CustomAltSvc: true),
            // Candidate production layout: 2x Listen on the same port, BOTH per-SNI via TlsHandshakeCallbackOptions,
            // each returning prebuilt per-transport options (no per-handshake allocation, no shared-ALPN mutation).
            new("split-sni", "2x Listen same port: TCP H1/H2 + QUIC H3, both per-SNI, prebuilt per-transport options", HttpProtocols.None, null,
                ConfigureCustom: (o, port, m) =>
                {
                    o.Listen(IPAddress.Loopback, port, lo =>
                    {
                        lo.Protocols = HttpProtocols.Http1AndHttp2;
                        lo.DisableAltSvcHeader = true;
                        lo.UseHttps(new TlsHandshakeCallbackOptions
                        {
                            OnConnection = ctx =>
                            {
                                m.Record(ctx.ClientHelloInfo.ServerName, "tcp-listener");
                                return ValueTask.FromResult(store.Current.Select(ctx.ClientHelloInfo.ServerName).TcpOptions);
                            },
                        });
                    });
                    o.Listen(IPAddress.Loopback, port, lo =>
                    {
                        lo.Protocols = HttpProtocols.Http3;
                        lo.UseHttps(new TlsHandshakeCallbackOptions
                        {
                            OnConnection = ctx =>
                            {
                                m.Record(ctx.ClientHelloInfo.ServerName, "quic-listener");
                                return ValueTask.FromResult(store.Current.Select(ctx.ClientHelloInfo.ServerName).QuicOptions);
                            },
                        });
                    });
                }, CustomAltSvc: true),
            // Unknown-SNI rejection (split-sni layout, no default cert): throw from the callback vs return null.
            new("reject-throw", "split-sni; unknown/absent SNI => callback throws AuthenticationException", HttpProtocols.None, null,
                ConfigureCustom: (o, port, m) => SplitSni(o, IPAddress.Loopback, port, m, store, OnUnknown.Throw, gate: false),
                CustomAltSvc: true, MinLog: LogLevel.Debug),
            new("reject-null", "split-sni; unknown/absent SNI => callback returns null", HttpProtocols.None, null,
                ConfigureCustom: (o, port, m) => SplitSni(o, IPAddress.Loopback, port, m, store, OnUnknown.ReturnNull, gate: false),
                CustomAltSvc: true, MinLog: LogLevel.Debug),
            // Connection-stage IP gate. TCP: ListenOptions.Use (pre-TLS). QUIC: ListenOptions.Use is TCP-only, so the
            // gate runs inside the QUIC TLS callback using ctx.Connection.RemoteEndPoint. Denies 127.0.0.1.
            new("quic-gate", "split-sni on 0.0.0.0, deny 127.0.0.1: TCP via ListenOptions.Use, QUIC inside TLS callback", HttpProtocols.None, null,
                ConfigureCustom: (o, port, m) => SplitSni(o, IPAddress.Any, port, m, store, OnUnknown.DefaultCert, gate: true),
                CustomAltSvc: true, ExtraTargets: ["127.0.0.1", containerIp], MinLog: LogLevel.Debug),
            // IPv6 dual-mode: both Listens on [::].
            new("split-sni-v6any", "split-sni with both Listens on [::] (dual-mode TCP + UDP)", HttpProtocols.None, null,
                ConfigureCustom: (o, port, m) => SplitSni(o, IPAddress.IPv6Any, port, m, store, OnUnknown.DefaultCert, gate: false),
                CustomAltSvc: true, ExtraTargets: ["a.test", "[::1]", containerIp]),
            // Alt-Svc handling: Kestrel's header disabled, our own emitted with the public port.
            new("altsvc-custom", "tlscb-ctx + DisableAltSvcHeader=true + middleware emits h3=\":443\"", HttpProtocols.Http1AndHttp2AndHttp3, (lo, m) =>
                {
                    lo.DisableAltSvcHeader = true;
                    lo.UseHttps(new TlsHandshakeCallbackOptions
                    {
                        OnConnection = ctx => ValueTask.FromResult(new SslServerAuthenticationOptions { ServerCertificateContext = store.Current.Select(ctx.ClientHelloInfo.ServerName).Context }),
                    });
                }, CustomAltSvc: true),
        };

        // ---- Start every mechanism; record startup failures. -------------------------------------
        var running = new List<(Mechanism M, int Port, WebApplication App)>();
        Console.WriteLine("== Startup ==");
        for (var i = 0; i < mechanisms.Length; i++)
        {
            var m = mechanisms[i];
            var port = BasePort + i;
            var app = BuildApp(m, port);
            try
            {
                await app.StartAsync();
                running.Add((m, port, app));
                Console.WriteLine($"  [{m.Name,-17}] :{port} STARTED  ({m.Description})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{m.Name,-17}] :{port} STARTUP FAILED: {ex.GetType().Name}: {ex.Message}");
                await app.DisposeAsync();
            }
            foreach (var w in m.Logs) Console.WriteLine($"      log: {w}");
            m.Logs.Clear();
        }
        Console.WriteLine();

        // ---- Probe, hot-swap, probe again. --------------------------------------------------------
        var rows = new List<string[]>();
        var errors = new List<string>();
        await ProbeAll(running, "v1", rows, errors);
        store.Swap(pki.MapV2); // hot swap: one volatile write, listeners untouched
        await ProbeAll(running, "v2", rows, errors);

        // Extra probes (IP gate, dual-mode): fixed targets, run after the rejection probes above.
        var extraRows = new List<string[]>();
        foreach (var (m, port, _) in running)
        {
            if (m.ExtraTargets is null) continue;
            foreach (var version in new[] { HttpVersion.Version20, HttpVersion.Version30 })
                foreach (var target in m.ExtraTargets)
                {
                    var r = await Probe(target, port, version);
                    extraRows.Add([m.Name, version == HttpVersion.Version30 ? "H3" : "H2", target,
                        r.RespVersion is null ? r.Note : $"{Short(r.Served)}/{r.Chain} {r.RespVersion}"]);
                }
        }

        // Health: after every rejection above, does each server still serve a known name on both transports?
        var healthRows = new List<string[]>();
        foreach (var (m, port, _) in running)
        {
            var target = m.ExtraTargets?[^1] ?? "a.test";
            var h2 = await Probe(target, port, HttpVersion.Version20);
            var h3 = await Probe(target, port, HttpVersion.Version30);
            healthRows.Add([m.Name, target, h2.RespVersion ?? h2.Note, h3.RespVersion ?? h3.Note]);
        }

        Console.WriteLine("== Results: served leaf per SNI (label/chain-elements/certs-received) ==");
        Console.WriteLine("   chain-elements 2 => the client could build leaf+intermediate; certs-received = chain.ChainPolicy.ExtraStore.Count");
        Console.WriteLine("   labels: a1/b1/w1/def = a.test, b.test, *.wild.test, default (map v1); a2/w2 = after hot swap");
        Console.WriteLine("           (map v2: a.test and wildcard replaced, b.test removed => default); h3fix = fixed H3 cert");
        PrintTable(["mechanism", "phase", "proto", .. Snis, "negotiated", "alt-svc on response"], rows);
        Console.WriteLine();
        if (errors.Count > 0)
        {
            Console.WriteLine("== Probe errors ==");
            foreach (var e in errors.Distinct()) Console.WriteLine($"  {e}");
            Console.WriteLine();
        }

        Console.WriteLine("== Extra probes: IP gate and IPv6 dual-mode ==");
        PrintTable(["mechanism", "proto", "target", "result"], extraRows);
        Console.WriteLine();
        Console.WriteLine("== Health after all probes (incl. rejections) ==");
        PrintTable(["mechanism", "target", "H2", "H3"], healthRows);
        Console.WriteLine();

        // Did Kestrel mutate the prebuilt, shared SslServerAuthenticationOptions handed out by tlscb-shared?
        var shared = pki.MapV1.Select("a.test").SharedOptions;
        Console.WriteLine($"== tlscb-shared: ApplicationProtocols on the shared a.test v1 options after probing = " +
            $"[{string.Join(", ", shared.ApplicationProtocols?.Select(p => p.ToString()) ?? [])}] (was null when created) ==");
        var sharedAlpn = pki.MapV1.Select("a.test").SharedOptionsAlpn;
        Console.WriteLine($"== tlscb-shared-alpn: ApplicationProtocols after probing = [{string.Join(", ", sharedAlpn.ApplicationProtocols!.Select(p => p.ToString()))}] (preset h3, h2, http/1.1) ==");
        Console.WriteLine();

        Console.WriteLine("== Selection-callback invocations (by mechanism) ==");
        foreach (var (m, _, _) in running)
        {
            var groups = m.Invocations.GroupBy(x => x).Select(g => $"{g.Key} x{g.Count()}");
            Console.WriteLine($"  [{m.Name,-17}] {(m.Invocations.IsEmpty ? "(never invoked)" : string.Join("; ", groups))}");
        }
        Console.WriteLine();

        Console.WriteLine("== Kestrel logs during probing (Warning+; Debug+ from Kestrel categories for reject-*/quic-gate; connection ids/ports normalised) ==");
        foreach (var (m, _, _) in running)
        {
            var normalised = m.Logs
                .Select(w => System.Text.RegularExpressions.Regex.Replace(w, "\"[0-9A-Z]{13}\"", "\"<id>\""))
                .Select(w => System.Text.RegularExpressions.Regex.Replace(w, @":\d{4,5}\b", ":<port>"))
                .Select(w => w.Length > 260 ? w[..260] + "..." : w);
            foreach (var g in normalised.GroupBy(x => x))
                Console.WriteLine($"  [{m.Name,-17}] x{g.Count()} {g.Key}");
        }

        foreach (var (_, _, app) in running)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        return 0;
    }

    private static WebApplication BuildApp(Mechanism m, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new CaptureLoggerProvider(m.Logs, m.MinLog));
        builder.Logging.SetMinimumLevel(m.MinLog);
        builder.WebHost.UseQuic(); // CreateSlimBuilder does not register the QUIC transport by default
        // CreateSlimBuilder also omits the HTTPS configuration service. Without it, UseHttps(options/callback)
        // on an H3-enabled listener throws at startup ("Call UseKestrelHttpsConfiguration() ... for HTTP/3").
        if (!s_skipHttpsConfig) builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(o =>
        {
            if (m.ConfigureCustom is not null)
            {
                m.ConfigureCustom(o, port, m);
                return;
            }
            o.Listen(IPAddress.Loopback, port, lo =>
            {
                lo.Protocols = m.Protocols;
                m.Configure!(lo, m);
            });
        });
        var app = builder.Build();
        if (m.CustomAltSvc)
        {
            app.Use((ctx, next) =>
            {
                if (!HttpProtocol.IsHttp3(ctx.Request.Protocol))
                    ctx.Response.Headers.AltSvc = $"h3=\":{PublicHttpsPort}\"; ma=86400";
                return next(ctx);
            });
        }
        app.MapGet("/", (HttpContext ctx) => ctx.Request.Protocol);
        return app;
    }

    private static async Task ProbeAll(List<(Mechanism M, int Port, WebApplication App)> running, string phase, List<string[]> rows, List<string> errors)
    {
        foreach (var (m, port, _) in running)
            foreach (var version in new[] { HttpVersion.Version20, HttpVersion.Version30 })
            {
                if (m.ExtraTargets is not null) continue;
                var proto = version == HttpVersion.Version30 ? "H3" : "H2";
                var cells = new List<string> { m.Name, phase, proto };
                var negotiated = new SortedSet<string>(StringComparer.Ordinal);
                var altSvc = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var sni in Snis)
                {
                    var r = await Probe(sni, port, version);
                    if (r.RespVersion is null)
                    {
                        cells.Add("ERR");
                        errors.Add($"[{m.Name} {phase} {proto} {sni}] {r.Note}");
                        continue;
                    }
                    cells.Add($"{Short(r.Served)}/{r.Chain}");
                    negotiated.Add(r.RespVersion);
                    altSvc.Add(r.Note);
                }
                cells.Add(negotiated.Count == 0 ? "-" : string.Join(",", negotiated));
                cells.Add(altSvc.Count == 0 ? "-" : string.Join(" | ", altSvc));
                rows.Add([.. cells]);
            }
    }

    private enum OnUnknown { DefaultCert, Throw, ReturnNull }

    private static readonly IPAddress DeniedIp = IPAddress.Loopback;

    private static string Addr(EndPoint? ep) => ep switch
    {
        null => "null",
        IPEndPoint ip => ip.Address.ToString(),
        _ => ep.GetType().Name,
    };

    private static bool IsDenied(EndPoint? ep)
    {
        if (ep is not IPEndPoint ip) return false;
        var a = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
        return a.Equals(DeniedIp);
    }

    /// <summary>The split-sni layout (TCP H1/H2 + QUIC H3 on one port), parameterised for the S1 follow-up probes.</summary>
    private static void SplitSni(KestrelServerOptions o, IPAddress bind, int port, Mechanism m, CertStore store, OnUnknown unknown, bool gate)
    {
        o.Listen(bind, port, lo =>
        {
            lo.Protocols = HttpProtocols.Http1AndHttp2;
            lo.DisableAltSvcHeader = true;
            if (gate)
            {
                // Stage A on TCP: connection middleware, runs before TLS.
                lo.Use(next => conn =>
                {
                    if (!IsDenied(conn.RemoteEndPoint)) return next(conn);
                    m.Record(null, $"tcp-gate DENY remote={Addr(conn.RemoteEndPoint)}");
                    conn.Abort();
                    return Task.CompletedTask;
                });
            }
            lo.UseHttps(new TlsHandshakeCallbackOptions { OnConnection = ctx => ValueTask.FromResult(Choose(ctx, quic: false, m, store, unknown, gate)) });
        });
        o.Listen(bind, port, lo =>
        {
            lo.Protocols = HttpProtocols.Http3;
            lo.UseHttps(new TlsHandshakeCallbackOptions { OnConnection = ctx => ValueTask.FromResult(Choose(ctx, quic: true, m, store, unknown, gate)) });
        });
    }

    private static SslServerAuthenticationOptions Choose(TlsHandshakeCallbackContext ctx, bool quic, Mechanism m, CertStore store, OnUnknown unknown, bool gate)
    {
        var sni = ctx.ClientHelloInfo.ServerName;
        var remote = ctx.Connection?.RemoteEndPoint;
        var t = quic ? "quic" : "tcp";
        // Stage A on QUIC: ListenOptions.Use doesn't apply, so the IP gate runs here, before any cert is chosen.
        if (gate && quic && IsDenied(remote))
        {
            m.Record(sni, $"quic-gate DENY remote={Addr(remote)}");
            throw new System.Security.Authentication.AuthenticationException("denied by IP gate");
        }
        var e = store.Current.TrySelect(sni);
        if (e is null && unknown == OnUnknown.Throw)
        {
            m.Record(sni, $"{t} REJECT(throw)");
            throw new System.Security.Authentication.AuthenticationException("unknown SNI");
        }
        if (e is null && unknown == OnUnknown.ReturnNull)
        {
            m.Record(sni, $"{t} REJECT(null)");
            return null!;
        }
        m.Record(sni, $"{t} remote={Addr(remote)}");
        e ??= store.Current.Select(sni);
        return quic ? e.QuicOptions : e.TcpOptions;
    }

    private static string Short(string? cn) => cn switch
    {
        null => "?",
        "a.test v1" => "a1",
        "b.test v1" => "b1",
        "*.wild.test v1" => "w1",
        "default.test v1" => "def",
        "a.test v2" => "a2",
        "*.wild.test v2" => "w2",
        _ when cn.StartsWith("h3-fixed", StringComparison.Ordinal) => "h3fix",
        _ => cn,
    };

    private sealed record ProbeResult(string? Served, string Chain, string? RespVersion, string Note);

    private static async Task<ProbeResult> Probe(string host, int port, Version version)
    {
        string? served = null;
        var chainLen = "-";
        // Fresh handler per probe => fresh TCP/QUIC connection => fresh handshake (hot-swap visibility).
        using var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, chain, _) =>
                {
                    served = cert is null ? null : new X509Certificate2(cert).GetNameInfo(X509NameType.SimpleName, false);
                    // "<elements the client could build>/<certs actually received from the server (ExtraStore)>"
                    chainLen = chain is null ? "-/-" : $"{chain.ChainElements.Count}/{chain.ChainPolicy.ExtraStore.Count}";
                    return true;
                },
            },
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8),
            DefaultRequestVersion = version,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        try
        {
            using var resp = await client.GetAsync(new Uri($"https://{host}:{port}/"));
            var altSvc = resp.Headers.TryGetValues("alt-svc", out var v) ? string.Join(",", v) : "(none)";
            return new ProbeResult(served, chainLen, $"HTTP/{resp.Version}", altSvc);
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            var msg = inner.Message.ReplaceLineEndings(" ");
            return new ProbeResult(served, chainLen, null, $"ERR {inner.GetType().Name}: {(msg.Length > 90 ? msg[..90] + "..." : msg)}");
        }
    }

    private static void PrintTable(string[] header, List<string[]> rows)
    {
        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        string Line(string[] cells) => "| " + string.Join(" | ", cells.Select((c, i) => c.PadRight(widths[i]))) + " |";
        Console.WriteLine(Line(header));
        Console.WriteLine("|" + string.Join("|", widths.Select(w => new string('-', w + 2))) + "|");
        foreach (var r in rows) Console.WriteLine(Line(r));
    }
}

internal sealed record Mechanism(
    string Name,
    string Description,
    HttpProtocols Protocols,
    Action<ListenOptions, Mechanism>? Configure,
    Action<KestrelServerOptions, int, Mechanism>? ConfigureCustom = null,
    bool CustomAltSvc = false,
    string[]? ExtraTargets = null,          // non-null => probed only in the "extra probes" table, against these hosts
    LogLevel MinLog = LogLevel.Warning)     // Debug for the rejection probes, to see what Kestrel logs
{
    public ConcurrentQueue<string> Invocations { get; } = new();
    public ConcurrentQueue<string> Logs { get; } = new();
    public void Record(string? sni, string transport) => Invocations.Enqueue($"sni={sni ?? "null"} {transport}");
}

// ---- Certificate store with lock-free hot swap (a volatile reference to an immutable map). -------

internal sealed record CertEntry(
    string Label,
    X509Certificate2 Cert,
    SslStreamCertificateContext Context,
    SslServerAuthenticationOptions SharedOptions,      // ALPN left null (Kestrel fills it in)
    SslServerAuthenticationOptions SharedOptionsAlpn,  // ALPN preset to h3, h2, http/1.1
    SslServerAuthenticationOptions TcpOptions,         // ALPN preset to h2, http/1.1 (TCP listener only)
    SslServerAuthenticationOptions QuicOptions);       // ALPN preset to h3 (QUIC listener only)

internal sealed class CertMap(Dictionary<string, CertEntry> exact, Dictionary<string, CertEntry> wildcard, CertEntry @default)
{
    public CertEntry Select(string? sni) => TrySelect(sni) ?? @default;

    /// <summary>Exact, then wildcard; null when nothing matches (no default fallback).</summary>
    public CertEntry? TrySelect(string? sni)
    {
        if (string.IsNullOrEmpty(sni)) return null;
        var name = sni.ToLowerInvariant();
        if (exact.TryGetValue(name, out var e)) return e;
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && wildcard.TryGetValue(name[(dot + 1)..], out e)) return e;
        return null;
    }
}

internal sealed class CertStore(CertMap initial)
{
    private volatile CertMap _current = initial;
    public CertMap Current => _current;
    public void Swap(CertMap next) => _current = next;
}

// ---- Throwaway PKI: root CA -> intermediate CA -> leaf certs. ------------------------------------

internal sealed class Pki
{
    public required X509Certificate2 Intermediate { get; init; }
    public required CertEntry Default { get; init; }
    public required CertEntry H3Fixed { get; init; }
    public required CertMap MapV1 { get; init; }
    public required CertMap MapV2 { get; init; }

    public static Pki Create()
    {
        var root = MakeCa("S1 Root CA", null);
        var inter = MakeCa("S1 Intermediate CA", root);
        var chain = new X509Certificate2Collection(inter);

        CertEntry Leaf(string cn, params string[] dns)
        {
            var cert = MakeLeaf(cn, dns, inter);
            // Q5: offline:true => no AIA/OCSP fetches while building the chain at startup.
            var ctx = SslStreamCertificateContext.Create(cert, chain, offline: true);
            return new CertEntry(cn, cert, ctx,
                new SslServerAuthenticationOptions { ServerCertificateContext = ctx },
                new SslServerAuthenticationOptions
                {
                    ServerCertificateContext = ctx,
                    ApplicationProtocols = [SslApplicationProtocol.Http3, SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                },
                new SslServerAuthenticationOptions { ServerCertificateContext = ctx, ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11] },
                new SslServerAuthenticationOptions { ServerCertificateContext = ctx, ApplicationProtocols = [SslApplicationProtocol.Http3] });
        }

        var def = Leaf("default.test v1", "default.test");
        var a1 = Leaf("a.test v1", "a.test");
        var b1 = Leaf("b.test v1", "b.test");
        var w1 = Leaf("*.wild.test v1", "*.wild.test");
        var a2 = Leaf("a.test v2", "a.test");
        var w2 = Leaf("*.wild.test v2", "*.wild.test");
        var h3 = Leaf("h3-fixed (*.wild.test + a.test)", "*.wild.test", "a.test");

        return new Pki
        {
            Intermediate = inter,
            Default = def,
            H3Fixed = h3,
            MapV1 = new CertMap(new() { ["a.test"] = a1, ["b.test"] = b1 }, new() { ["wild.test"] = w1 }, def),
            // Hot swap: a.test and the wildcard get new certs; b.test is removed (=> default).
            MapV2 = new CertMap(new() { ["a.test"] = a2 }, new() { ["wild.test"] = w2 }, def),
        };
    }

    private static X509Certificate2 MakeCa(string cn, X509Certificate2? issuer)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var nb = DateTimeOffset.UtcNow.AddDays(-1);
        var na = DateTimeOffset.UtcNow.AddDays(30);
        if (issuer is null) return req.CreateSelfSigned(nb, na);
        using var signed = req.Create(issuer, nb, na, RandomNumberGenerator.GetBytes(16));
        return signed.CopyWithPrivateKey(key);
    }

    private static X509Certificate2 MakeLeaf(string cn, string[] dns, X509Certificate2 issuer)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var d in dns) san.AddDnsName(d);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var signed = req.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29), RandomNumberGenerator.GetBytes(16));
        return signed.CopyWithPrivateKey(key);
    }
}

// ---- Capture Kestrel warnings so silent fallbacks become visible. ---------------------------------

internal sealed class CaptureLoggerProvider(ConcurrentQueue<string> sink, LogLevel minLevel) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, sink, minLevel);
    public void Dispose() { }

    private sealed class CaptureLogger(string category, ConcurrentQueue<string> sink, LogLevel minLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        // Below Warning only Kestrel's own categories are kept (TLS/QUIC handshake failures log at Debug).
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Warning
            || (logLevel >= minLevel && category.StartsWith("Microsoft.AspNetCore.Server.Kestrel", StringComparison.Ordinal));
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception).ReplaceLineEndings(" ");
            if (exception is not null) msg += $" [{exception.GetType().Name}: {exception.Message.ReplaceLineEndings(" ")}]";
            sink.Enqueue($"{logLevel} {category.Split('.').Last()}: {msg}");
        }
    }
}
