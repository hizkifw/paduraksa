using System.Net;

using CsCheck;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.ForwardAuth.ForwardAuthFixtures;

namespace Paduraksa.Tests.Proxy.ForwardAuth;

/// <summary>
/// The decision cache against a plain model of it. The provider's answer is a function of everything its call carries (the standard headers,
/// the extra ones it is told to read, the original method, scheme, host, target and client); the model remembers an allow under the whole
/// of that, at the grain the scope and the answer say (host grain only for an allow that named someone, which leaves the method and the
/// target out), and the gate must call the provider exactly when the model's cache has no entry. So two requests that differ in anything the
/// provider is sent never share an entry (the gate would skip a call the model makes, or serve an allow the provider didn't give), and an allow that
/// names no one never stands in for another path.
/// </summary>
public sealed class ForwardAuthCachePropertyTests
{
    private static readonly string[][] Cookies = [[], [""], ["id=a"], ["id=a", "x=1"], ["x=1"], ["id=b"], ["x=1", "id=a"]];

    private static readonly string[][] Authorizations = [[], ["Bearer 1"], ["Bearer 2"], ["Bearer 1", "Bearer 2"]];

    private static readonly string[][] UserAgents = [[], ["curl/8"], ["bad-bot"], ["Mozilla/5.0"]];

    private static readonly string[][] Accepts = [[], ["text/html"], ["*/*"]];

    private static readonly string[][] Languages = [[], ["en"], ["id"]];

    private static readonly string[][] Xrw = [[], ["XMLHttpRequest"]];

    private static readonly string[][] ApiKeys = [[], ["k1"], ["k2"], [""], ["k1", "k2"]];

    private static readonly string[] Targets = ["/public", "/admin", "/admin?x=1", "/public/a", "/Public", "/admin%20x"];

    private static readonly Gen<Req> Starts = Gen.Select(
        Gen.Select(
            Gen.Bool,
            Property.Pick("app.test", "app2.test"),
            Property.Pick("192.0.2.1", "192.0.2.2"),
            Property.Pick("GET", "POST"),
            Property.Pick(Targets)),
        Gen.Select(
            Property.Pick(Cookies),
            Property.Pick(Authorizations),
            Property.Pick<string[]>([], ["Basic x"]),
            Property.Pick(UserAgents),
            Property.Pick(Accepts),
            Property.Pick(Languages),
            Property.Pick(Xrw),
            Property.Pick(ApiKeys)),
        static (call, headers) => new Req(
            call.Item1, call.Item2, call.Item3, call.Item4, call.Item5,
            headers.Item1, headers.Item2, headers.Item3, headers.Item4, headers.Item5, headers.Item6, headers.Item7, headers.Item8));

    // Each later request is an earlier one with one field changed (or not changed at all), which is how two requests come to differ in exactly one thing.
    private static readonly Gen<(Req First, List<(int From, int Field, int Value)> Next)> Sequences =
        Gen.Select(Starts, Gen.Select(Gen.Int[0, 100], Gen.Int[0, 12], Gen.Int[0, 7]).List[1, 9]).Select(static (first, next) => (first, next));

    [Theory]
    [InlineData(ForwardAuthCacheScope.Request, false)]
    [InlineData(ForwardAuthCacheScope.Request, true)]
    [InlineData(ForwardAuthCacheScope.Host, false)]
    [InlineData(ForwardAuthCacheScope.Host, true)]
    public async Task TheGateCallsTheProviderExactlyWhenAPlainModelOfTheCacheHasNoEntry(ForwardAuthCacheScope scope, bool readsApiKey)
    {
        await using var auth = await FakeAuthServer.StartAsync(TestContext.Current.CancellationToken);
        auth.Handler = context =>
        {
            Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
            foreach (var header in context.Request.Headers)
            {
                headers[header.Key] = string.Join(header.Key == "Cookie" ? "; " : ", ", header.Value.ToArray());
            }

            var answer = Provider.Answer(
                headers.GetValueOrDefault("User-Agent", string.Empty),
                headers.GetValueOrDefault("X-Forwarded-Proto", string.Empty),
                headers.GetValueOrDefault("Cookie", string.Empty),
                headers.GetValueOrDefault("X-Forwarded-Uri", string.Empty),
                headers.GetValueOrDefault("X-Api-Key", string.Empty),
                headers.GetValueOrDefault("X-Forwarded-Method", string.Empty));
            if (answer == Answer.Deny)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
            else if (answer == Answer.AllowAs)
            {
                context.Response.Headers["Remote-User"] = "someone";
            }

            return Task.CompletedTask;
        };
        var spec = Spec(auth.Address, b =>
        {
            b.Scope = scope;
            b.RequestHeaders = readsApiKey ? ["X-Api-Key"] : [];
            b.CacheTtl = TimeSpan.FromMinutes(5);
        });
        var model = Model(spec, TestSnapshots.ProxyHost("app2", "app2.test", TestSnapshots.Route("app2-root", "web") with { AuthProvider = spec.Id }));

        Sequences.Check(
            150,
            t =>
            {
                var gate = GateFor(new ForwardAuthRuntime(), model, out _);
                var cache = new Cache(scope, readsApiKey);
                var requests = new List<Req> { t.First };
                foreach (var (from, field, value) in t.Next)
                {
                    requests.Add(requests[from % requests.Count].Mutate(field, value));
                }

                foreach (var request in requests)
                {
                    var before = auth.CallCount;
                    var expectedCalls = cache.Expect(request, out var expected);

                    var actual = RunAsync(gate, request.ToContext()).GetAwaiter().GetResult();

                    Assert.True(
                        expected == actual && auth.CallCount - before == expectedCalls,
                        $"{request}: the gate answered {actual} after {auth.CallCount - before} calls, the model {expected} after {expectedCalls}; earlier in this run: {string.Join(" | ", requests.TakeWhile(r => r != request))}");
                }
            });
    }

    private enum Answer
    {
        Deny,
        Allow,
        AllowAs,
    }

    /// <summary>What the provider decides, as a function of what its call carries: a scripted client is refused, a browser can't sign in over plain HTTP with a
    /// <c>tls</c> cookie, a cookie names a user, an open path and an API key admit no one in particular, and POST to /admin is refused.</summary>
    private static class Provider
    {
        public static Answer Answer(string userAgent, string proto, string cookie, string uri, string apiKey, string method)
        {
            if (userAgent.Contains("bad", StringComparison.Ordinal))
            {
                return ForwardAuthCachePropertyTests.Answer.Deny;
            }

            if (method == "POST" && uri.StartsWith("/admin", StringComparison.Ordinal))
            {
                return ForwardAuthCachePropertyTests.Answer.Deny;
            }

            if (cookie.Contains("id=a", StringComparison.Ordinal))
            {
                return proto == "http" && cookie.Contains("x=1", StringComparison.Ordinal) ? ForwardAuthCachePropertyTests.Answer.Deny : ForwardAuthCachePropertyTests.Answer.AllowAs;
            }

            return uri.StartsWith("/public", StringComparison.Ordinal) || apiKey.Split(", ").Contains("k1")
                ? ForwardAuthCachePropertyTests.Answer.Allow
                : ForwardAuthCachePropertyTests.Answer.Deny;
        }
    }

    /// <summary>The decision cache as a dictionary of everything a call carries.</summary>
    private sealed class Cache(ForwardAuthCacheScope scope, bool readsApiKey)
    {
        private readonly HashSet<string> _entries = [];

        /// <summary>The calls the model makes for <paramref name="request"/> (0 or 1) and the answer the gate must give; remembers an allow.</summary>
        public int Expect(Req request, out string answer)
        {
            var hostKey = "host|" + request.Key(includeCall: false, readsApiKey);
            var requestKey = "request|" + request.Key(includeCall: true, readsApiKey);
            if ((scope == ForwardAuthCacheScope.Host && _entries.Contains(hostKey)) || _entries.Contains(requestKey))
            {
                answer = "pass";
                return 0;
            }

            var decision = Provider.Answer(
                string.Join(", ", request.UserAgent),
                request.Https ? "https" : "http",
                string.Join("; ", request.Cookie),
                request.Target,
                readsApiKey ? string.Join(", ", request.ApiKey) : string.Empty,
                request.Method);
            answer = decision == Answer.Deny ? "401" : "pass";
            if (decision != Answer.Deny)
            {
                // Host grain only for an allow that named someone.
                _entries.Add(scope == ForwardAuthCacheScope.Host && decision == Answer.AllowAs ? hostKey : requestKey);
            }

            return 1;
        }
    }

    private sealed record Req(
        bool Https,
        string Host,
        string Client,
        string Method,
        string Target,
        string[] Cookie,
        string[] Authorization,
        string[] ProxyAuthorization,
        string[] UserAgent,
        string[] Accept,
        string[] Language,
        string[] RequestedWith,
        string[] ApiKey)
    {
        public Req Mutate(int field, int value) => field switch
        {
            0 => this with { Https = value % 2 == 0 },
            1 => this with { Host = new[] { "app.test", "app2.test" }[value % 2] },
            2 => this with { Client = new[] { "192.0.2.1", "192.0.2.2" }[value % 2] },
            3 => this with { Method = new[] { "GET", "POST" }[value % 2] },
            4 => this with { Target = Targets[value % Targets.Length] },
            5 => this with { Cookie = Cookies[value % Cookies.Length] },
            6 => this with { Authorization = Authorizations[value % Authorizations.Length] },
            7 => this with { ProxyAuthorization = value % 2 == 0 ? [] : ["Basic x"] },
            8 => this with { UserAgent = UserAgents[value % UserAgents.Length] },
            9 => this with { Accept = Accepts[value % Accepts.Length] },
            10 => this with { Language = Languages[value % Languages.Length] },
            11 => this with { RequestedWith = Xrw[value % Xrw.Length] },
            _ => this with { ApiKey = ApiKeys[value % ApiKeys.Length] },
        };

        /// <summary>Everything the provider is sent, as one string. A header's lines are kept apart, so an absent header, an empty one and
        /// two lines are three different things.</summary>
        public string Key(bool includeCall, bool readsApiKey)
        {
            static string Lines(string[] lines) => lines.Length + ":" + string.Join("\u0001", lines.Select(static l => l.Length + "=" + l));
            var key = string.Join(
                "|",
                Https,
                Host,
                Client,
                Lines(Cookie),
                Lines(Authorization),
                Lines(ProxyAuthorization),
                Lines(UserAgent),
                Lines(Accept),
                Lines(Language),
                Lines(RequestedWith),
                readsApiKey ? Lines(ApiKey) : string.Empty);
            return includeCall ? $"{Method} {Target}|{key}" : key;
        }

        public DefaultHttpContext ToContext()
        {
            var path = Target.Contains('?', StringComparison.Ordinal) ? Target[..Target.IndexOf('?', StringComparison.Ordinal)] : Target;
            var context = TestContexts.Create(Host, path, Method, Https ? "https" : "http", IPAddress.Parse(Client), Target);
            Set(context, "Cookie", Cookie);
            Set(context, "Authorization", Authorization);
            Set(context, "Proxy-Authorization", ProxyAuthorization);
            Set(context, "User-Agent", UserAgent);
            Set(context, "Accept", Accept);
            Set(context, "Accept-Language", Language);
            Set(context, "X-Requested-With", RequestedWith);
            Set(context, "X-Api-Key", ApiKey);
            context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new MemoryStream()));
            return context;
        }

        public override string ToString() =>
            $"{(Https ? "https" : "http")}://{Host} {Method} {Target} from {Client} cookie[{string.Join("|", Cookie)}] auth[{string.Join("|", Authorization)}] proxyAuth[{string.Join("|", ProxyAuthorization)}] "
            + $"ua[{string.Join("|", UserAgent)}] accept[{string.Join("|", Accept)}] lang[{string.Join("|", Language)}] xrw[{string.Join("|", RequestedWith)}] key[{string.Join("|", ApiKey)}]";

        private static void Set(DefaultHttpContext context, string name, string[] lines)
        {
            if (lines.Length > 0)
            {
#pragma warning disable ASP0019 // Add keeps an empty line, as a parsed "Cookie:" line does; the indexer would drop the header.
                context.Request.Headers.Add(name, new StringValues(lines));
#pragma warning restore ASP0019
            }
        }
    }
}
