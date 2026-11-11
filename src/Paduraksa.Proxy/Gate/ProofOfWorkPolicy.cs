using System.Security.Cryptography;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;

namespace Paduraksa.Proxy.Gate;

/// <summary>
/// The proof-of-work gate of one route: a request without a valid <c>paduraksa_pow</c> token gets the challenge page
/// (a browser) or a 403 (anything else); with one, it goes on. It runs in the route chain after the route's other
/// policies, so a Redirect/Respond host (which has no routes) is never challenged and a route's own switch can exempt or
/// protect just its paths. The valid-token path allocates nothing.
/// </summary>
/// <remarks>
/// Exempt: <c>GET</c>/<c>HEAD /robots.txt</c> (a crawler must be able to read it) and the verify endpoint (the gate
/// answers it before route matching). On a key that can't be opened the policy answers 503 and never lets a request
/// through: the one safe failure for a gate.
/// </remarks>
public sealed class ProofOfWorkPolicy : IRequestPolicy
{
    private readonly HostProofOfWorkSpec _spec;
    private readonly byte[] _hostScope;
    private readonly bool _routeOverride;
    private readonly ProofOfWorkRuntime _runtime;
    private readonly ProofOfWorkPage _page;

    internal ProofOfWorkPolicy(HostProofOfWorkSpec spec, byte[] hostScope, bool routeOverride, ProofOfWorkRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(hostScope);
        ArgumentNullException.ThrowIfNull(runtime);
        _spec = spec;
        _hostScope = hostScope;
        _routeOverride = routeOverride;
        _runtime = runtime;
        _page = new ProofOfWorkPage(spec.Difficulty);
    }

    public PolicyDecision Evaluate(in RequestView request)
    {
        if (IsExempt(request.Http.Request))
        {
            return PolicyDecision.Continue;
        }

        if (_runtime.Key is not { } key)
        {
            _runtime.ReportUnavailable();
            return PolicyDecision.Respond(ProofOfWorkResponses.Unavailable);
        }

        var problem = Diagnose(request.Http.Request, request.State.ClientAddress, key, out _, count: !request.IsSimulation);
        if (problem == ProofOfWorkTokenProblem.None)
        {
            return PolicyDecision.Continue;
        }

        if (ProofOfWorkTargets.IsBrowserRequest(request.Http.Request))
        {
            if (!request.IsSimulation)
            {
                _runtime.Stats.CountChallengeServed();
            }

            Span<char> challenge = stackalloc char[ProofOfWorkChallenge.EncodedLength];
            Span<byte> nonce = stackalloc byte[ProofOfWorkChallenge.NonceLength];
            if (request.IsSimulation)
            {
                // A simulation must leave state alone: a fixed nonce is enough for the same status and page.
                nonce.Clear();
            }
            else
            {
                RandomNumberGenerator.Fill(nonce);
            }

            var now = Now();
            if (now is < 0 or > uint.MaxValue
                || !ProofOfWorkChallenge.TryCreate(key, _hostScope, request.State.ClientAddress, (uint)now, _spec.Difficulty, nonce, challenge, out var length))
            {
                return PolicyDecision.Respond(ProofOfWorkResponses.Unavailable);
            }

            return PolicyDecision.Respond(_page.Build(challenge[..length], ProofOfWorkTargets.OriginalTarget(request.Http)));
        }

        if (!request.IsSimulation)
        {
            _runtime.Stats.CountBlockedNonBrowser();
        }

        return PolicyDecision.Respond(ProofOfWorkResponses.NonBrowser);
    }

    string IRequestPolicy.Name => "proofOfWork";

    Metrics.GateOutcome IRequestPolicy.Outcome => Metrics.GateOutcome.RefusedProofOfWork;

    string IRequestPolicy.Explain(in RequestView request, PolicyDecision verdict)
    {
        var source = _routeOverride ? "the route's own override; " : string.Empty;
        if (IsExempt(request.Http.Request))
        {
            var path = request.Http.Request.Path.Value ?? string.Empty;
            return string.Concat(
                source,
                ProofOfWorkTargets.IsRobots(path)
                    ? "robots.txt is never challenged, so a crawler can read it"
                    : "the verify endpoint is never challenged: the gate answers it before route matching");
        }

        if (_runtime.Key is null)
        {
            return "the proof-of-work HMAC key isn't available, so the route fails closed with 503";
        }

        var problem = Diagnose(request.Http.Request, request.State.ClientAddress, _runtime.Key, out var expiresAt, count: false);
        if (problem == ProofOfWorkTokenProblem.None)
        {
            return string.Concat(source, "a valid token, expiring at ", DateTimeOffset.FromUnixTimeSeconds(expiresAt).UtcDateTime.ToString("u", System.Globalization.CultureInfo.InvariantCulture));
        }

        var answer = ProofOfWorkTargets.IsBrowserRequest(request.Http.Request)
            ? "the challenge page"
            : "403, because the request isn't a GET or HEAD that accepts text/html";
        var why = problem switch
        {
            ProofOfWorkTokenProblem.Missing => "there is no paduraksa_pow cookie",
            ProofOfWorkTokenProblem.Duplicate => "more than one paduraksa_pow cookie was sent, so none is trusted",
            ProofOfWorkTokenProblem.Malformed => "the paduraksa_pow cookie isn't a token this build issues",
            ProofOfWorkTokenProblem.BadTag => "the token's tag doesn't verify for this host and client address (an altered token, or one issued for another host or IP)",
            ProofOfWorkTokenProblem.Expired => "the token has expired",
            _ => $"the token's difficulty is below this host's {_spec.Difficulty}",
        };
        return string.Concat(source, why, ": ", answer);
    }

    /// <summary>Runs the same check <see cref="Evaluate"/> does, without a challenge and (with <paramref name="count"/> false) without counters.</summary>
    private ProofOfWorkTokenProblem Diagnose(HttpRequest request, IpKey client, IMacKey key, out uint expiresAt, bool count)
    {
        expiresAt = 0;
        Span<char> cookie = stackalloc char[ProofOfWorkToken.MaxCookieLength + 1];
        if (!ProofOfWorkToken.TryReadCookie(request, cookie, out var length, out var problem))
        {
            if (count)
            {
                Count(problem);
            }

            return problem;
        }

        var now = Now();
        if (now is < 0 or > uint.MaxValue)
        {
            problem = ProofOfWorkTokenProblem.Expired;
            if (count)
            {
                Count(problem);
            }

            return problem;
        }

        var valid = ProofOfWorkToken.TryValidate(cookie[..length], key, _hostScope, client, now, _spec.Difficulty, out expiresAt, out _, out problem);
        if (valid)
        {
            return ProofOfWorkTokenProblem.None;
        }

        if (count)
        {
            Count(problem);
        }

        return problem;
    }

    private void Count(ProofOfWorkTokenProblem problem)
    {
        switch (problem)
        {
            case ProofOfWorkTokenProblem.Duplicate:
            case ProofOfWorkTokenProblem.Malformed:
                _runtime.Stats.CountMalformed();
                break;
            case ProofOfWorkTokenProblem.BadTag:
                _runtime.Stats.CountBadTag();
                break;
            case ProofOfWorkTokenProblem.Expired:
                _runtime.Stats.CountExpired();
                break;
            case ProofOfWorkTokenProblem.TooEasy:
                _runtime.Stats.CountTooEasy();
                break;
        }
    }

    private long Now() => _runtime.Time.GetUtcNow().ToUnixTimeSeconds();

    private static bool IsExempt(HttpRequest request)
    {
        var path = request.Path.Value.AsSpan();
        if (ProofOfWorkTargets.IsVerifyPath(path))
        {
            return true;
        }

        return ProofOfWorkTargets.IsRobots(path)
            && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method));
    }
}
