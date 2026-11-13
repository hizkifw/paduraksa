using System.Buffers;
using System.Globalization;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Pages;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.RuleSets;

/// <summary>
/// A rule set in a host's or a route's chain: the first rule the request matches decides (deny answers 403, drop aborts, allow
/// ends the set and lets the chain go on), and a request no rule matches goes on too. It sits before the access list that asks
/// for passwords (the whole policy, so on a route whose list has users that list's address checks follow the set; the host chain's
/// address-only check precedes it) and before the rate limit, so a refusal is one span scan, long before a PBKDF2 or a bucket (docs/design/hot-path.md "D. Policy chains").
/// </summary>
/// <remarks>
/// <para><b>Path.</b> The rules read the canonical matching path routes match on, so <c>//admin</c>, <c>/admin;x</c> and
/// <c>/ADMIN</c> are <c>/admin</c> to a rule too. It is computed only for a set whose rules read the path, in a stack buffer
/// or a pooled array.</para>
/// <para><b>robots.txt.</b> <c>GET</c> and <c>HEAD /robots.txt</c> is never ruled on, like the rate limit and the proof-of-work
/// gate: a crawler that is turned away can still read what the host asks of it (a 403 there is read as "no restrictions").</para>
/// <para><b>Allocation.</b> Every verdict allocates nothing (the deny answer is precomputed); a regex rule may, see
/// <see cref="CompiledRuleSet"/>.</para>
/// </remarks>
public sealed class RuleSetPolicy : IRequestPolicy, IPolicyTraceFacts
{
    private static readonly PolicyDecision Denied = PolicyDecision.Respond(
        PrecomputedResponse.Text(StatusCodes.Status403Forbidden, "forbidden\n", htmlPage: ErrorPages.RequestBlocked));

    private readonly CompiledRuleSet _set;
    private readonly bool _routeOverride;

    internal RuleSetPolicy(CompiledRuleSet set, bool routeOverride = false)
    {
        ArgumentNullException.ThrowIfNull(set);
        _set = set;
        _routeOverride = routeOverride;
    }

    /// <summary>The set this policy applies, for the security overview.</summary>
    public CompiledRuleSet Set => _set;

    public PolicyDecision Evaluate(in RequestView request)
    {
        var http = request.Http.Request;
        if (RobotsTargets.IsRequest(http))
        {
            return PolicyDecision.Continue;
        }

        var rule = Match(http);
        if (rule == CompiledRuleSet.NoMatch)
        {
            return PolicyDecision.Continue;
        }

        var action = _set.ActionOf(rule);
        if (!request.IsSimulation)
        {
            _set.Counters.Count(action);
        }

        return action switch
        {
            RuleAction.Deny => Denied,
            RuleAction.Drop => PolicyDecision.Drop,
            _ => PolicyDecision.Continue,
        };
    }

    string IRequestPolicy.Name => "ruleSet";

    Metrics.GateOutcome IRequestPolicy.Outcome => Metrics.GateOutcome.RefusedRuleSet;

    string IRequestPolicy.Explain(in RequestView request, PolicyDecision verdict)
    {
        var http = request.Http.Request;
        if (RobotsTargets.IsRequest(http))
        {
            return string.Concat("rule set '", _set.Name, "' never rules on robots.txt, so a crawler can read it");
        }

        var rule = Match(http);
        if (rule == CompiledRuleSet.NoMatch)
        {
            return string.Concat("none of the ", _set.RuleCount.ToString(CultureInfo.InvariantCulture), " rules of '", _set.Name, "' match, so the request goes on");
        }

        var action = _set.ActionOf(rule);
        return string.Concat(
            "rule ",
            (rule + 1).ToString(CultureInfo.InvariantCulture),
            " of '",
            _set.Name,
            "' matches (",
            Describe(http, rule),
            ") and ",
            action switch
            {
                RuleAction.Deny => "refuses it with 403",
                RuleAction.Drop => "drops it",
                _ => "allows it, so the set's later rules are skipped",
            });
    }

    void IPolicyTraceFacts.AddFacts(in RequestView request, PolicyDecision verdict, List<GateFact> facts)
    {
        facts.Add(new("ruleSet", _set.Name));
        if (_routeOverride)
        {
            facts.Add(new("source", "route"));
        }

        var http = request.Http.Request;
        if (RobotsTargets.IsRequest(http))
        {
            facts.Add(new("rule", "exempt"));
            return;
        }

        var rule = Match(http);
        if (rule == CompiledRuleSet.NoMatch)
        {
            facts.Add(new("rule", "none"));
            return;
        }

        facts.Add(new("rule", (rule + 1).ToString(CultureInfo.InvariantCulture)));
        facts.Add(new("matched", Describe(http, rule)));
        facts.Add(new("action", _set.ActionOf(rule) switch { RuleAction.Deny => "deny", RuleAction.Drop => "drop", _ => "allow" }));
    }

    private int Match(HttpRequest http)
    {
        var userAgent = http.Headers.UserAgent;
        if (!_set.NeedsPath)
        {
            return _set.Match(http.Method, default, userAgent);
        }

        var path = http.Path.Value.AsSpan();
        if (RoutePath.IsCanonical(path))
        {
            return _set.Match(http.Method, path, userAgent);
        }

        if (path.Length < RoutePath.StackLimit)
        {
            Span<char> buffer = stackalloc char[RoutePath.StackLimit];
            return _set.Match(http.Method, buffer[..RoutePath.Canonicalize(path, buffer)], userAgent);
        }

        var rented = ArrayPool<char>.Shared.Rent(path.Length + 1);
        try
        {
            return _set.Match(http.Method, rented.AsSpan(0, RoutePath.Canonicalize(path, rented)), userAgent);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    // What the rule's conditions said about this request, for a sentence: only the ones the rule states.
    private string Describe(HttpRequest http, int rule)
    {
        var spec = _set.SpecOf(rule);
        List<string> parts = [];
        if (spec.PathKind is { } kind)
        {
            parts.Add($"path {Name(kind)} {spec.Pattern}");
        }

        if (spec.Methods.Count > 0)
        {
            parts.Add($"method {http.Method}");
        }

        if (spec.UserAgentContains.Count > 0 || spec.UserAgentEmpty)
        {
            var agent = http.Headers.UserAgent.ToString();
            string? token = null;
            foreach (var candidate in spec.UserAgentContains)
            {
                if (agent.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    token = candidate;
                    break;
                }
            }

            parts.Add(token is not null ? $"user agent contains '{token}'" : "no user agent");
        }

        return string.Join(", ", parts);
    }

    private static string Name(RulePathKind kind) => kind switch
    {
        RulePathKind.Exact => "is",
        RulePathKind.Prefix => "starts with",
        RulePathKind.Glob => "matches glob",
        _ => "matches regex",
    };
}
