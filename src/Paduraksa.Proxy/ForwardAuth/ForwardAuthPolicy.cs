using System.Globalization;
using System.Net.Http.Headers;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Pages;
using Paduraksa.Proxy.Simulation;

namespace Paduraksa.Proxy.ForwardAuth;

/// <summary>
/// A route's forward-auth check: the request is let through only when the provider's endpoint says it may be. A request whose credentials the
/// provider allowed a moment ago is let through from the decision cache, synchronously and allocating nothing; any other request defers
/// (<see cref="IDeferredPolicy"/>) and the gate waits for one <c>GET</c> to the endpoint before the chain goes on.
/// </summary>
/// <remarks>
/// <para><b>The call.</b> A <c>GET</c> with no body, like Traefik's and nginx's, carrying the client's credentials and what it asks about
/// (<see cref="ForwardAuthLimits.StandardRequestHeaders"/> and the provider's own additions, never the rest of its headers) and the original request in
/// <c>X-Forwarded-Method</c>, <c>-Proto</c> (the trusted scheme), <c>-Host</c>, <c>-Uri</c> (the target as the client sent it) and <c>-For</c> (the
/// resolved client). Whatever the client sent as <c>X-Forwarded-*</c> is not forwarded: these are ours.</para>
/// <para><b>The answer.</b> 2xx lets the request on, with the provider's response headers copied to the upstream request and the client's own
/// copies of them removed first (<see cref="CompiledAuthProvider.Owns"/>), so a client can never be the source of an identity header on a protected
/// route. A 401, 403 or 407 and a redirect are relayed to the client with their body and <c>Set-Cookie</c>; a 401 to a browser that asked for a page
/// goes to the provider's sign-in URL when one is configured. Anything else, a failed or slow call and a call made while too many are
/// running all refuse the request (502, 504, 503): the policy fails closed.</para>
/// <para><b>Allocation.</b> The cached allow, and every verdict that needs no call, allocates nothing (the key is hashed in a stack buffer). A
/// call allocates what an outbound HTTP request does; that cost is accepted and is why allows are remembered.</para>
/// </remarks>
public sealed class ForwardAuthPolicy : IDeferredPolicy, IPolicyTraceFacts
{
    private const int MaxCopiedHeaders = 64;

    private static readonly PolicyDecision Unavailable = PolicyDecision.Respond(
        PrecomputedResponse.Text(StatusCodes.Status502BadGateway, "The sign-in service didn't respond properly.\n", htmlPage: ErrorPages.AuthUnavailable));

    private static readonly PolicyDecision TimedOut = PolicyDecision.Respond(
        PrecomputedResponse.Text(StatusCodes.Status504GatewayTimeout, "The sign-in service took too long to respond.\n", htmlPage: ErrorPages.AuthTimeout));

    private static readonly PolicyDecision Busy = PolicyDecision.Respond(
        PrecomputedResponse.Text(StatusCodes.Status503ServiceUnavailable, "The sign-in service is busy.\n", htmlPage: ErrorPages.AuthBusy), "1");

    private readonly CompiledAuthProvider _provider;
    private readonly bool _routeOverride;

    internal ForwardAuthPolicy(CompiledAuthProvider provider, bool routeOverride = false)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _routeOverride = routeOverride;
    }

    /// <summary>The provider this policy asks, for the security overview.</summary>
    public CompiledAuthProvider Provider => _provider;

    public PolicyDecision Evaluate(in RequestView request)
    {
        var state = request.State;
        var provider = _provider;
        if (Peek(request) is { } allowance)
        {
            if (!request.IsSimulation)
            {
                provider.Counters.CountCacheHit();
                provider.Counters.CountAllowed();
                state.ForwardAuthProvider = provider;
                state.ForwardAuthHeaders = allowance.Headers;
            }

            return PolicyDecision.Continue;
        }

        // A simulation makes no call: it says it would, and the request goes on as if the answer were yes.
        return request.IsSimulation ? PolicyDecision.Continue : PolicyDecision.Defer;
    }

    string IRequestPolicy.Name => "forwardAuth";

    string IRequestPolicy.Explain(in RequestView request, PolicyDecision verdict)
    {
        var spec = _provider.Spec;
        if (Peek(request) is { } allowance)
        {
            var left = _provider.State.Runtime.Time.GetElapsedTime(_provider.State.Runtime.Time.GetTimestamp(), allowance.ExpiresAt);
            return string.Concat(
                "provider '", _provider.Name, "' allowed these credentials a moment ago (",
                Math.Ceiling(left.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                " s left in the decision cache), so no call is made and the request goes on");
        }

        return string.Concat(
            "would call provider '", _provider.Name, "' (GET ", spec.Url, " with the original request in X-Forwarded-*): "
            + "a 2xx lets the request on, a 401, 403 or redirect is relayed to the client, anything else refuses it; the simulation makes no call and shows the request going on");
    }

    void IPolicyTraceFacts.AddFacts(in RequestView request, PolicyDecision verdict, List<GateFact> facts)
    {
        var spec = _provider.Spec;
        facts.Add(new("provider", _provider.Name));
        if (_routeOverride)
        {
            facts.Add(new("source", "route"));
        }

        facts.Add(new("endpoint", spec.Url));
        facts.Add(new("outcome", Peek(request) is null ? "wouldCall" : "cached"));
        facts.Add(new("cacheScope", spec.CacheScope == ForwardAuthCacheScope.Host ? "host" : "request"));
        facts.Add(new("cacheSeconds", ((int)spec.CacheTtl.TotalSeconds).ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>The remembered allow for this request's credentials, or null. Reads the cache and nothing else, so a simulation can ask.</summary>
    private ForwardAuthAllowance? Peek(in RequestView request)
    {
        var state = _provider.State;
        if (state.TtlTicks <= 0)
        {
            return null;
        }

        var key = ForwardAuthKey.Compute(state.Runtime.CacheSecret, _provider.Spec.CacheScope, request.Http, request.State);
        return state.Cache.TryGet(key, state.Runtime.Time.GetTimestamp(), out var allowance) ? allowance : null;
    }

    async ValueTask<PolicyDecision> IDeferredPolicy.CompleteAsync(HttpContext http, GateRequestState state)
    {
        var provider = _provider;
        var providerState = provider.State;
        var counters = provider.Counters;
        if (!providerState.TryEnter())
        {
            counters.CountBusy();
            return Busy;
        }

        try
        {
            var key = ForwardAuthKey.Compute(providerState.Runtime.CacheSecret, provider.Spec.CacheScope, http, state);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            timeout.CancelAfter(provider.Spec.Timeout);
            counters.CountCall();
            using var message = BuildRequest(http, state);
            using var response = await providerState.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return await ConcludeAsync(response, http, state, key, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return PolicyDecision.Drop;
        }
        catch (OperationCanceledException)
        {
            counters.CountTimeout();
            providerState.Runtime.ReportFailure(providerState, provider.Name, "no answer in " + provider.Spec.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s");
            return TimedOut;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or FormatException or ArgumentException)
        {
            counters.CountError();
            providerState.Runtime.ReportFailure(providerState, provider.Name, e.GetType().Name + ": " + e.Message);
            return Unavailable;
        }
        finally
        {
            providerState.Exit();
        }
    }

    private HttpRequestMessage BuildRequest(HttpContext http, GateRequestState state)
    {
        var request = http.Request;
        var message = new HttpRequestMessage(HttpMethod.Get, _provider.State.Uri);
        var headers = message.Headers;
        foreach (var name in _provider.RequestHeaders)
        {
            var values = request.Headers[name];
            if (values.Count > 0)
            {
                headers.TryAddWithoutValidation(name, Join(name, values));
            }
        }

        headers.TryAddWithoutValidation(ForwardAuthHeaders.Method, request.Method);
        headers.TryAddWithoutValidation(ForwardAuthHeaders.Proto, state.IsHttps ? "https" : "http");
        var host = request.Headers.Host;
        if (host.Count == 1 && !string.IsNullOrEmpty(host[0]))
        {
            headers.TryAddWithoutValidation(ForwardAuthHeaders.Host, host[0]);
        }

        headers.TryAddWithoutValidation(ForwardAuthHeaders.Uri, ForwardAuthKey.RawTarget(http));
        if (state.ClientAddress != Net.IpKey.Unspecified)
        {
            headers.TryAddWithoutValidation(ForwardAuthHeaders.For, state.FormattedClientAddress());
        }

        return message;
    }

    // A Cookie header sent as several lines (HTTP/2 splits it) is one header with its pairs joined by "; ".
    private static string Join(string name, StringValues values) =>
        values.Count == 1
            ? values[0] ?? string.Empty
            : string.Join(string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase) ? "; " : ", ", values.ToArray());

    private async ValueTask<PolicyDecision> ConcludeAsync(HttpResponseMessage response, HttpContext http, GateRequestState state, ulong key, CancellationToken cancel)
    {
        var provider = _provider;
        var counters = provider.Counters;
        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300)
        {
            if (!TryCollectHeaders(response, out var copied))
            {
                counters.CountError();
                provider.State.Runtime.ReportFailure(provider.State, provider.Name, "answered " + status.ToString(CultureInfo.InvariantCulture) + " with a header that can't be forwarded");
                return Unavailable;
            }

            counters.CountAllowed();
            state.ForwardAuthProvider = provider;
            state.ForwardAuthHeaders = copied;
            var providerState = provider.State;
            if (providerState.TtlTicks > 0)
            {
                var now = providerState.Runtime.Time.GetTimestamp();
                providerState.Cache.Add(new ForwardAuthAllowance(key, now + providerState.TtlTicks, copied), now);
            }

            return PolicyDecision.Continue;
        }

        if (status is not (401 or 403 or 407 or 301 or 302 or 303 or 307 or 308))
        {
            counters.CountError();
            provider.State.Runtime.ReportFailure(provider.State, provider.Name, "answered " + status.ToString(CultureInfo.InvariantCulture));
            return Unavailable;
        }

        var body = await ReadBodyAsync(response.Content, cancel).ConfigureAwait(false);
        if (body is null)
        {
            counters.CountError();
            provider.State.Runtime.ReportFailure(provider.State, provider.Name, "answered " + status.ToString(CultureInfo.InvariantCulture) + " with a body over " + ForwardAuthLimits.MaxRelayedBodyBytes.ToString(CultureInfo.InvariantCulture) + " bytes");
            return Unavailable;
        }

        List<PrecomputedHeader> relayed = [];
        foreach (var name in RelayedHeaders)
        {
            if (response.Headers.NonValidated.TryGetValues(name, out var values))
            {
                var lines = new string[values.Count];
                var at = 0;
                foreach (var line in values)
                {
                    lines[at++] = line;
                }

                relayed.Add(new PrecomputedHeader(name, new StringValues(lines)));
            }
        }

        var isRedirect = status is 301 or 302 or 303 or 307 or 308;
        if (isRedirect && !relayed.Exists(static h => h.Name == "Location"))
        {
            counters.CountError();
            provider.State.Runtime.ReportFailure(provider.State, provider.Name, "answered " + status.ToString(CultureInfo.InvariantCulture) + " without a Location");
            return Unavailable;
        }

        if (status == 401 && SignInLocation(http, state) is { } signIn)
        {
            counters.CountRedirected();
            relayed.Add(new PrecomputedHeader("Location", signIn));
            return PolicyDecision.Respond(PrecomputedResponse.Create(StatusCodes.Status302Found, "Redirecting\n"u8, "text/plain; charset=utf-8", "no-store", [.. relayed]));
        }

        if (isRedirect)
        {
            counters.CountRedirected();
        }
        else
        {
            counters.CountDenied();
        }

        var contentType = response.Content.Headers.ContentType?.ToString();
        return PolicyDecision.Respond(PrecomputedResponse.Create(status, body, contentType, "no-store", [.. relayed]));
    }

    private static readonly string[] RelayedHeaders = ["Location", "Set-Cookie", "WWW-Authenticate", "Proxy-Authenticate"];

    /// <summary>The headers the provider owns that its answer carries, or false when one has a value an upstream request can't hold.</summary>
    private bool TryCollectHeaders(HttpResponseMessage response, out ForwardAuthHeader[] copied)
    {
        List<ForwardAuthHeader>? found = null;
        foreach (var header in response.Headers.NonValidated)
        {
            if (!_provider.Owns(header.Key))
            {
                continue;
            }

            var value = header.Value.ToString();
            if (value.Length == 0)
            {
                continue;
            }

            if (HasControlCharacter(value))
            {
                copied = [];
                return false;
            }

            found ??= [];
            if (found.Count == MaxCopiedHeaders)
            {
                copied = [];
                return false;
            }

            found.Add(new ForwardAuthHeader(header.Key, value));
        }

        copied = found is null ? [] : [.. found];
        return true;
    }

    // A header value an upstream request can carry has no control character but a tab: a line break in one would split the request.
    private static bool HasControlCharacter(string value)
    {
        foreach (var c in value)
        {
            if ((c < ' ' && c != '\t') || c == '\u007f')
            {
                return true;
            }
        }

        return false;
    }

    private static async ValueTask<byte[]?> ReadBodyAsync(HttpContent content, CancellationToken cancel)
    {
        var stream = await content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using MemoryStream buffer = new();
            var chunk = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > ForwardAuthLimits.MaxRelayedBodyBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    /// <summary>
    /// Where a browser without a session is sent: the configured sign-in URL with the page it asked for in the return parameter. Only a navigation
    /// is sent there (a GET or HEAD that accepts HTML and isn't a script's XHR); anything else gets the provider's 401 as it is.
    /// </summary>
    private string? SignInLocation(HttpContext http, GateRequestState state)
    {
        var spec = _provider.Spec;
        var request = http.Request;
        var host = request.Headers.Host;
        if (spec.SignInUrl is not { } url || host.Count != 1 || string.IsNullOrEmpty(host[0])
            || !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            || !MediaTypes.AcceptsHtml(request.Headers.Accept) || request.Headers.ContainsKey("X-Requested-With"))
        {
            return null;
        }

        var original = string.Concat(state.IsHttps ? "https://" : "http://", host[0], ForwardAuthKey.RawTarget(http));
        return string.Concat(url, url.Contains('?', StringComparison.Ordinal) ? "&" : "?", spec.SignInReturnParameter, "=", Uri.EscapeDataString(original));
    }
}

/// <summary>The headers that tell the endpoint what the original request was.</summary>
internal static class ForwardAuthHeaders
{
    public const string Method = "X-Forwarded-Method";
    public const string Proto = "X-Forwarded-Proto";
    public const string Host = "X-Forwarded-Host";
    public const string Uri = "X-Forwarded-Uri";
    public const string For = "X-Forwarded-For";
}
