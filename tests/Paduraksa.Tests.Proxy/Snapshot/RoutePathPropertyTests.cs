using System.Net;
using System.Text.RegularExpressions;

using CsCheck;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Snapshot;

/// <summary>
/// The canonical matching path against a plain model of the same rules, and against what Kestrel really hands the gate:
/// raw request targets go over a socket to a bare Kestrel that echoes <c>Request.Path</c>, and through the gate with a
/// guarded <c>/admin</c> route.
/// </summary>
public sealed class RoutePathPropertyTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    // Pieces a hostile client combines: every separator spelling, dot segments, parameters, trailing dots and spaces,
    // an escape cut short, and non-ASCII.
    private static readonly string[] Pieces =
    [
        "/", "a", "adm", "in", "admin", "B", "x.", "/", "\\", "%2F", "%2f", "%5C", "%5c", ".", "..", "...", ";", ";x", ";/", " ", "%", "%2", "%5", "é", "€", "%2F%2F", "/.", "/..", "./", "../",
    ];

    private static readonly Gen<string> ManyPieces = Gen.Int[0, Pieces.Length - 1].Select(i => Pieces[i]).List[0, 14].Select(static parts => string.Concat(parts));

    // Kestrel's side of it: only spellings a request line can carry, percent-escapes included.
    private static readonly string[] Targets =
    [
        "/", "/", "/", "a", "adm", "in", "admin", "B", "x.", "\\", "%2F", "%2f", "%5C", "%5c", "%2e", "%2E", "%2e%2e", ".", "..", ";", ";x", "%20", "%61", "%41dmin", "%C3%A9", "%252F", "+", "~", "!", "%7B", "%2", "%zz", "%00", "%25",
    ];

    private static readonly Gen<string> ManyTargets = Gen.Int[0, Targets.Length - 1].Select(i => Targets[i]).List[1, 10]
        .Select(static parts => "/" + string.Concat(parts));

    [Fact]
    public void TheCanonicalViewEqualsAPlainModel() =>
        ManyPieces.Check(
            20_000,
            path =>
            {
                var expected = Model.Canonicalize(path);
                Assert.Equal(expected, RoutePath.Canonicalize(path));

                var buffer = new char[path.Length + 1];
                Assert.Equal(expected, new string(buffer, 0, RoutePath.Canonicalize(path, buffer)));
            });

    [Fact]
    public void TheCanonicalViewIsItsOwnCanonicalViewAndIsCanonicalSaysSo() =>
        ManyPieces.Check(
            20_000,
            path =>
            {
                var canonical = RoutePath.Canonicalize(path);

                Assert.Equal(canonical, RoutePath.Canonicalize(canonical));

                // A path with a '%' is never taken as canonical, even when it is: the fast path only has to be right when it says yes.
                Assert.Equal(canonical == path && !path.Contains('%', StringComparison.Ordinal), RoutePath.IsCanonical(path));
                Assert.False(canonical.AsSpan().ContainsAny("\\;"), canonical);
                Assert.DoesNotContain("//", canonical, StringComparison.Ordinal);
                Assert.DoesNotContain("%2f", canonical, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("%5c", canonical, StringComparison.OrdinalIgnoreCase);
                Assert.Equal('/', canonical[0]);
            });

    [Fact]
    public void MatchesCanonicalAgreesOnTheStackAndInAPooledBufferAlike() =>
        Gen.Select(ManyPieces, Gen.Int[0, 300], ManyPieces)
            .Check(
                10_000,
                t =>
                {
                    // Past RoutePath.StackLimit the view is built in a pooled array; the answer is the same.
                    var path = t.Item1 + string.Concat(Enumerable.Repeat("/p", t.Item2)) + t.Item3;
                    var canonical = Model.Canonicalize(path);

                    Assert.True(RoutePath.MatchesCanonical(path, canonical), path);
                    Assert.True(RoutePath.MatchesCanonical(path, canonical.ToUpperInvariant()), path);
                    Assert.False(RoutePath.MatchesCanonical(path, canonical + "x"), path);
                });

    [Fact]
    public async Task WhatKestrelHandsOverCanonicalisesLikeThePlainModelAndEverySpellingOfAGuardedPathIsGuarded()
    {
        await using var echo = await EchoPath.StartAsync();
        await using var proxy = await ProxyTestServer.StartAsync(new ProxyConfigModel
        {
            Upstreams = [Upstream("ua", upstream.Address("a"))],
            Hosts =
            [
                ProxyHost(
                    "guard",
                    "guard.test",
                    Route("guard-admin", "ua", "/admin") with { Policies = [new DenyPolicySpec(403, "denied\n")] },
                    Route("guard-root", "ua")),
            ],
        });
        var answered = 0;
        var refused = 0;
        var token = TestContext.Current.CancellationToken;

        foreach (var target in Sample(ManyTargets, 150, nameof(WhatKestrelHandsOverCanonicalisesLikeThePlainModelAndEverySpellingOfAGuardedPathIsGuarded)))
        {
            var (status, escaped, _) = await RawHttp.GetAsync(echo.BaseAddress, "echo.test", target, token);
            var handedOver = Unescape(escaped);
            if (status != 200)
            {
                // Kestrel's own 400 comes before the gate; the gate answers the same target 400 or never sees it.
                Assert.Equal(400, status);
                refused++;
                continue;
            }

            answered++;

            // The simulator's own model of Kestrel's decoding and dot-segment removal is what traces are built on.
            Assert.True(
                handedOver == GateSimulator.RemoveDotSegments(GateSimulator.DecodePath(target)),
                $"{target}: Kestrel hands over '{handedOver}', the simulator's model '{GateSimulator.RemoveDotSegments(GateSimulator.DecodePath(target))}'");
            var canonical = RoutePath.Canonicalize(handedOver);
            Assert.Equal(Model.Canonicalize(handedOver), canonical);
            Assert.False(canonical.AsSpan().ContainsAny("\\;"), $"{target} -> {handedOver} -> {canonical}");
            Assert.DoesNotContain("//", canonical, StringComparison.Ordinal);
            Assert.DoesNotContain("%2f", canonical, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%5c", canonical, StringComparison.OrdinalIgnoreCase);

            var guarded = canonical.Equals("/admin", StringComparison.OrdinalIgnoreCase) || canonical.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase);
            var (gateStatus, body, _) = await RawHttp.GetAsync(proxy.BaseAddress, "guard.test", target, token);
            Assert.True(
                (gateStatus == 403) == guarded,
                $"{target} reaches the gate as '{handedOver}', canonical '{canonical}': the gate answered {gateStatus}, the plain model says {(guarded ? "guarded" : "open")}");
            if (guarded)
            {
                Assert.Equal("denied\n", body);
            }
        }

        Assert.True(answered > 50, $"only {answered} targets got past Kestrel ({refused} refused)");
    }

    // The echo writes ASCII only (the raw client counts a chunk's size in characters): anything else, and the backslash that
    // escapes it, is \uXXXX.
    private static string Unescape(string escaped) =>
        Regex.Replace(escaped, @"\\u([0-9a-f]{4})", static m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    private static string Escape(string path) =>
        string.Concat(path.Select(static c => c is < ' ' or > '~' or '\\' ? $"\\u{(int)c:x4}" : c.ToString()));

    // The inputs of a generator as a list, drawn the way Property.Check draws them (so the count scales the same way).
    private static List<T> Sample<T>(Gen<T> gen, long baseline, string name)
    {
        List<T> items = [];
        gen.Check(baseline, items.Add, name);
        return items;
    }

    /// <summary>The rules of <see cref="RoutePath"/> written the obvious way: replace the separator spellings, split, resolve, join.</summary>
    private static class Model
    {
        public static string Canonicalize(string path)
        {
            var separated = Regex.Replace(path, @"\\|%2[fF]|%5[cC]", "/");
            List<string> kept = [];
            foreach (var raw in separated.Split('/'))
            {
                var semicolon = raw.IndexOf(';', StringComparison.Ordinal);
                var segment = semicolon < 0 ? raw : raw[..semicolon];
                if (segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    if (kept.Count > 0)
                    {
                        kept.RemoveAt(kept.Count - 1);
                    }

                    continue;
                }

                segment = segment.TrimEnd('.', ' ');
                if (segment.Length > 0)
                {
                    kept.Add(segment);
                }
            }

            if (kept.Count == 0)
            {
                return "/";
            }

            return "/" + string.Join('/', kept) + (separated.EndsWith('/') ? "/" : string.Empty);
        }
    }

    /// <summary>A bare Kestrel that answers with the path the gate would be given.</summary>
    private sealed class EchoPath : IAsyncDisposable
    {
        private readonly IHost _host;

        private EchoPath(IHost host)
        {
            _host = host;
            var addresses = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            BaseAddress = new Uri(Assert.Single(addresses.Addresses));
        }

        public Uri BaseAddress { get; }

        public static async Task<EchoPath> StartAsync()
        {
            var host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0))
                    .Configure(app => app.Run(context =>
                    {
                        context.Response.ContentType = "text/plain; charset=utf-8";
                        return context.Response.WriteAsync(Escape(context.Request.Path.Value ?? string.Empty));
                    })))
                .Build();
            await host.StartAsync(TestContext.Current.CancellationToken);
            return new EchoPath(host);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync(TestContext.Current.CancellationToken);
            _host.Dispose();
        }
    }
}
