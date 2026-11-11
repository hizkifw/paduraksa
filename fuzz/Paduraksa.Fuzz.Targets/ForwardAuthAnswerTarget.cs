using System.Net;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Fuzz;

/// <summary>
/// What a forward-auth provider answers, bytes the proxy didn't write: the status, the headers it relays or copies, the body it relays. The provider is a handler that
/// answers with whatever the input says, so the whole gate runs, with no socket. The input is a status, a flag, then header lines (a blank line ends them) and a body.
/// Properties: no exception anywhere in the answer handling (the policy turns one into a 502, so a log that names an exception is the crash); and the answer is what a plain
/// reading of the rules says: a 2xx lets the request on with exactly the owned, non-empty, text identity headers copied; 401, 403, 407 and the redirects are relayed with their body
/// (at most 64 KiB) and only the four relayed headers, printable ones; a redirect has a Location; a 401 to a browser goes to the sign-in page; anything else is 502.
/// </summary>
internal static class ForwardAuthAnswerTarget
{
    private const string SignIn = "https://login.example/start";

    private static readonly int[] Statuses = [200, 204, 299, 401, 403, 407, 301, 302, 303, 307, 308, 400, 404, 429, 500, 503, 201, 304, 100];

    private static readonly string[] Owned = ["Remote-User", "Remote-Groups"];

    private static readonly Lazy<Setup> Shared = new(() => new Setup());

    public static FuzzTarget Target { get; } = new(
        "forward-auth-answer",
        "a forward-auth provider's answer: relayed headers and body, Location, identity headers",
        Run,
        Seeds);

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2)
        {
            return;
        }

        var setup = Shared.Value;
        var status = Statuses[input[0] % Statuses.Length];
        var oversized = (input[1] & 1) != 0;
        var (headers, body) = Parse(input[2..]);
        if (oversized)
        {
            // A body at, just under and just over the 64 KiB a refusal may carry.
            var padded = new byte[ForwardAuthLimits.MaxRelayedBodyBytes + (input[1] >> 1) - 20];
            body.AsSpan(0, Math.Min(body.Length, padded.Length)).CopyTo(padded);
            body = padded;
        }

        var (forwarded, context, state) = setup.Send(status, headers, body);
        setup.AssertNoException();

        var statusCode = context.Response.StatusCode;
        var relayedLines = headers.Where(h => Relayed.Contains(h.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var carried = relayedLines.All(h => h.Value.All(static c => c is '\t' or (>= ' ' and <= '~')));
        var hasLocation = relayedLines.Any(h => h.Name.Equals("Location", StringComparison.OrdinalIgnoreCase));
        var big = body.Length > ForwardAuthLimits.MaxRelayedBodyBytes;

        if (status is >= 200 and < 300)
        {
            CheckAllowed(forwarded, statusCode, headers, state);
            return;
        }

        Checks.That(!forwarded, $"a {status} let the request on");
        if (status is 401 or 403 or 407 or 301 or 302 or 303 or 307 or 308)
        {
            var isRedirect = status is 301 or 302 or 303 or 307 or 308;
            if (big || !carried || (isRedirect && !hasLocation))
            {
                Checks.That(statusCode == 502, $"a {status} that can't be relayed (body {body.Length} B, printable headers {carried}, Location {hasLocation}) was answered {statusCode}");
                return;
            }

            if (status == 401)
            {
                Checks.That(statusCode == 302, $"a 401 to a browser with a sign-in page was answered {statusCode}");
                var location = context.Response.Headers.Location.ToString();
                Checks.That(location.StartsWith(SignIn + "?rd=", StringComparison.Ordinal), $"the sign-in redirect goes to '{location}'");
                Checks.That(!location.Any(static c => c is < ' ' or > '~'), "the sign-in Location isn't printable ASCII");
                return;
            }

            Checks.That(statusCode == status, $"a relayable {status} was answered {statusCode}");
            foreach (var response in context.Response.Headers)
            {
                foreach (var value in response.Value)
                {
                    Checks.That(value is null || value.All(static c => c is '\t' or (>= ' ' and <= '~')), $"the relayed {response.Key} holds a character that can split the response");
                }
            }

            Checks.That(!isRedirect || context.Response.Headers.Location.ToString() == string.Join(", ", relayedLines.Where(h => h.Name.Equals("Location", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value)), "the Location was changed");
            return;
        }

        Checks.That(statusCode == 502, $"a {status} from the provider was answered {statusCode}, not 502");
    }

    private static readonly string[] Relayed = ["Location", "Set-Cookie", "WWW-Authenticate", "Proxy-Authenticate"];

    private static void CheckAllowed(bool forwarded, int statusCode, List<(string Name, string Value)> headers, ForwardAuthHeader[]? copied)
    {
        // The owned names: the two exact ones and the X-Auth- prefix, '_' and '-' one character, case ignored; an empty value is no header, a value that isn't text is left off.
        static string Fold(string name) => name.Replace('_', '-').ToUpperInvariant();
        var expected = headers
            .GroupBy(static h => h.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Name: group.First().Name, Value: string.Join(", ", group.Select(static h => h.Value))))
            .Where(h => Owned.Any(o => Fold(h.Name) == Fold(o)) || Fold(h.Name).StartsWith("X-AUTH-", StringComparison.Ordinal))
            .Where(static h => h.Value.Length > 0 && IsText(h.Value))
            .ToList();
        if (expected.Count > 64)
        {
            Checks.That(!forwarded && statusCode == 502, $"{expected.Count} identity headers were copied");
            return;
        }

        Checks.That(forwarded && statusCode == 200, $"a 2xx was answered {statusCode} (forwarded {forwarded})");
        var actual = copied ?? [];
        Checks.That(
            actual.Length == expected.Count && actual.Zip(expected).All(static p => p.First.Name.Equals(p.Second.Name, StringComparison.OrdinalIgnoreCase) && p.First.Value == p.Second.Value),
            $"copied [{string.Join("; ", actual.Select(static h => h.Name + "=" + h.Value))}], a plain reading says [{string.Join("; ", expected.Select(static h => h.Name + "=" + h.Value))}]");
    }

    // Text an upstream request can carry: no control character but tab, no DEL, no replacement character, no lone surrogate.
    private static bool IsText(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if ((c < ' ' && c != '\t') || c is '\u007f' or '�')
            {
                return false;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }

    // Header lines "Name: value" up to a blank line, then the body. A name that isn't a token is dropped here, as the transport would never deliver it.
    private static (List<(string Name, string Value)> Headers, byte[] Body) Parse(ReadOnlySpan<byte> bytes)
    {
        List<(string, string)> headers = [];
        while (!bytes.IsEmpty)
        {
            var feed = bytes.IndexOf((byte)'\n');
            var line = feed < 0 ? bytes : bytes[..feed];
            bytes = feed < 0 ? default : bytes[(feed + 1)..];
            if (line.IsEmpty)
            {
                break;
            }

            var colon = line.IndexOf((byte)':');
            if (colon > 0)
            {
                headers.Add((Encoding.Latin1.GetString(line[..colon]).Trim(), Encoding.UTF8.GetString(line[(colon + 1)..]).TrimStart(' ')));
            }
        }

        return (headers, bytes.ToArray());
    }

    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        static byte[] Answer(int statusIndex, byte flag, string head, string body = "") => [(byte)statusIndex, flag, .. Encoding.UTF8.GetBytes(head + "\n\n" + body)];

        yield return ("allow-alice", Answer(0, 0, "Remote-User: alice\nRemote-Groups: admins, dev\nX-Auth-Uid: 7\nX-Other: ignored"));
        yield return ("allow-underscore-name", Answer(0, 0, "Remote_User: root"));
        yield return ("allow-empty-and-control", Answer(0, 0, "Remote-User:\nRemote-Groups: a\rb\nX-Auth-Name: José"));
        yield return ("allow-many", Answer(0, 0, string.Join('\n', Enumerable.Range(0, 70).Select(i => $"X-Auth-{i}: v"))));
        yield return ("deny-401", Answer(3, 0, "WWW-Authenticate: Basic realm=\"x\"\nSet-Cookie: a=b; Path=/", "please sign in"));
        yield return ("deny-403", Answer(4, 0, "Content-Type: text/html", "<h1>no</h1>"));
        yield return ("redirect-302", Answer(7, 0, "Location: https://auth.example/login?next=/a\nSet-Cookie: s=1"));
        yield return ("redirect-no-location", Answer(7, 0, "Set-Cookie: s=1"));
        yield return ("redirect-bad-location", Answer(7, 0, "Location: https://a.example/é"));
        yield return ("deny-too-big", Answer(4, 43, "Content-Type: text/plain", "x"));
        yield return ("deny-exactly-the-limit", Answer(4, 41, "Content-Type: text/plain", "x"));
        yield return ("server-error", Answer(14, 0, string.Empty));
        yield return ("not-modified", Answer(17, 0, string.Empty));
    }

    /// <summary>The gate over one route behind one provider whose endpoint is a handler the target sets the answer of.</summary>
    private sealed class Setup : IDisposable
    {
        private readonly AnswerHandler _handler = new();
        private readonly CapturingLogger _log = new();
        private readonly GateMiddleware _gate;
        private readonly Clock _clock = new();

        public Setup()
        {
            var provider = AuthProviderId.FromValue(new Guid("01900000-0000-7000-8000-00000000a001"));
            var upstream = UpstreamId.FromValue(new Guid("01900000-0000-7000-8000-00000000a002"));
            AuthProviderSpec spec = new()
            {
                Id = provider,
                Name = "sso",
                Url = "http://auth.invalid/verify",
                Timeout = TimeSpan.FromSeconds(5),
                CacheTtl = TimeSpan.Zero,
                ResponseHeaders = ["Remote-User", "Remote-Groups", "X-Auth-*"],
                SignInUrl = SignIn,
                SignInReturnParameter = "rd",
            };
            ProxyConfigModel model = new()
            {
                Upstreams = [new UpstreamSpec { Id = upstream, Destinations = [new DestinationSpec(DestinationId.FromValue(new Guid("01900000-0000-7000-8000-00000000a003")), "http://127.0.0.1:1")] }],
                AuthProviders = [spec],
                Hosts =
                [
                    new HostSpec
                    {
                        Id = HostId.FromValue(new Guid("01900000-0000-7000-8000-00000000a004")),
                        Names = ["app.test"],
                        Routes = [new RouteSpec { Id = RouteId.FromValue(new Guid("01900000-0000-7000-8000-00000000a005")), Upstream = upstream, AuthProvider = provider }],
                    },
                ],
            };
            ForwardAuthRuntime runtime = new(_clock, _log, _ => _handler);
            var compiled = SnapshotCompiler.Compile(model, new SnapshotCompilerOptions { ForwardAuth = runtime });
            if (!compiled.Succeeded)
            {
                throw new InvalidOperationException(string.Join("; ", compiled.Errors));
            }

            var candidate = compiled.Snapshot!;
            var endpoints = new Endpoint[candidate.RouteIdBySlot.Length];
            for (var i = 0; i < endpoints.Length; i++)
            {
                endpoints[i] = new Endpoint(static _ => Task.CompletedTask, EndpointMetadataCollection.Empty, candidate.RouteIdBySlot[i].ToString());
            }

            SnapshotStore store = new();
            store.Publish(candidate.Bind(1, endpoints));
            _gate = new GateMiddleware(store, new TrustedProxyClientAddressSource());
        }

        public void Dispose() => _handler.Dispose();

        public (bool Forwarded, DefaultHttpContext Context, ForwardAuthHeader[]? Copied) Send(int status, List<(string Name, string Value)> headers, byte[] body)
        {
            _clock.Advance(TimeSpan.FromMinutes(2));
            _log.Clear();
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(body) };
            foreach (var (name, value) in headers)
            {
                if (!response.Headers.TryAddWithoutValidation(name, value))
                {
                    _ = response.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }

            _handler.Next = response;
            var context = new DefaultHttpContext();
            context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new MemoryStream()));
            context.Features.Set<IHttpRequestLifetimeFeature>(new Lifetime());
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
            context.Request.Method = "GET";
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("app.test");
            context.Request.Path = "/page";
            context.Request.Headers.Accept = "text/html";
            context.Request.Headers.Cookie = "session=abc";
            var forwarded = false;
            _gate.InvokeAsync(context, _ =>
            {
                forwarded = true;
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();
            return (forwarded, context, context.Features.Get<IGateFeature>()?.State.ForwardAuthHeaders);
        }

        /// <summary>The policy turns an exception in the answer handling into a 502 and a log line naming it: that line is the crash.</summary>
        public void AssertNoException() =>
            Checks.That(!_log.Messages.Any(static m => m.Contains("Exception:", StringComparison.Ordinal)), "an exception inside the answer handling: " + string.Join(" | ", _log.Messages));
    }

    private sealed class AnswerHandler : HttpMessageHandler
    {
        public HttpResponseMessage? Next { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Next!);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks = 1;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)by.TotalMilliseconds);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public void Clear() => Messages.Clear();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class Lifetime : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }

        public void Abort()
        {
        }
    }
}
