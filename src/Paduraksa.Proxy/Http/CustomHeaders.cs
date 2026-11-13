using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;

namespace Paduraksa.Proxy.Http;

/// <summary>One compiled custom header rule: its value is precomputed, so applying it builds no string.</summary>
/// <param name="Name">The header's name, as written in the config.</param>
/// <param name="Operation">What the rule does.</param>
/// <param name="Value">The value a set or add sends; empty for a remove.</param>
/// <param name="OnContent">A header <c>HttpClient</c> keeps on a message's content (<c>Content-Type</c>, <c>Content-Language</c>,
/// ...), whose header stores refuse it everywhere else by throwing: a request's content, or an upstream response's.</param>
public readonly record struct CustomHeaderRule(string Name, HeaderOperation Operation, StringValues Value, bool OnContent = false);

/// <summary>
/// A host's or route's custom headers, compiled once per snapshot (docs/design/hot-path.md "Custom headers"). A route
/// without rules of its own shares its host's instance, and a host without rules shares <see cref="None"/>, so the
/// request path tests one length to know there is nothing to do.
/// </summary>
public sealed class CompiledHeaders
{
    private CompiledHeaders(CustomHeaderRule[] request, CustomHeaderRule[] response)
    {
        Request = request;
        Response = response;
    }

    /// <summary>No rule in either direction.</summary>
    public static CompiledHeaders None { get; } = new([], []);

    /// <summary>For the request the upstream receives, in the order they apply.</summary>
    public CustomHeaderRule[] Request { get; }

    /// <summary>For the response the client receives, in the order they apply.</summary>
    public CustomHeaderRule[] Response { get; }

    /// <summary>
    /// Compiles <paramref name="spec"/>, checking every rule again (the compiler's backstop for what the validator judged):
    /// a name the proxy owns or a value that could start another header line must never reach the wire.
    /// </summary>
    internal static CompiledHeaders Compile(HostHeadersSpec spec, Action<string> error)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(error);
        if (spec.IsEmpty)
        {
            return None;
        }

        return new CompiledHeaders(Rules(spec.Request, response: false, error), Rules(spec.Response, response: true, error));
    }

    private static CustomHeaderRule[] Rules(IReadOnlyList<HeaderRuleSpec> rules, bool response, Action<string> error)
    {
        var compiled = new CustomHeaderRule[rules.Count];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        var direction = response ? "response" : "request";

        // Each layer is capped on its own, and the merge adds the names of every layer up: the bound is on what a request applies.
        if (rules.Count > CustomHeaderRules.MaxRules)
        {
            error($"the layers merge to {rules.Count} custom {direction} header rules; at most {CustomHeaderRules.MaxRules} apply to a host or route");
        }
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (CustomHeaderRules.NameProblem(rule.Name, response) is { } nameProblem)
            {
                error($"a custom {direction} header: {nameProblem}");
            }
            else if (!names.Add(rule.Name))
            {
                error($"two custom {direction} header rules for '{rule.Name}'");
            }

            if (!Enum.IsDefined(rule.Operation))
            {
                error($"custom {direction} header '{rule.Name}' has an unknown operation {rule.Operation}");
            }
            else if (CustomHeaderRules.OperationProblem(rule.Name, rule.Operation, response) is { } operationProblem)
            {
                error($"custom {direction} header '{rule.Name}': {operationProblem}");
            }
            else if (rule.Operation != HeaderOperation.Remove && CustomHeaderRules.ValueProblem(rule.Value) is { } valueProblem)
            {
                error($"custom {direction} header '{rule.Name}': {valueProblem}");
            }

            compiled[i] = new CustomHeaderRule(
                rule.Name,
                rule.Operation,
                rule.Operation == HeaderOperation.Remove ? StringValues.Empty : new StringValues(rule.Value),
                IsContentHeader(rule.Name));
        }

        return compiled;
    }

    /// <summary>
    /// Applies response rules to <paramref name="headers"/>. A set or a remove is one assignment or removal of a precomputed
    /// value (0 B in Kestrel's header store once the connection has held the name). An add onto a header that already has
    /// a value builds the one array that holds both: the one cost the rules have, and only for that case.
    /// </summary>
    /// <param name="headers">The response's headers.</param>
    /// <param name="rules">The rules, in order.</param>
    /// <param name="again">The rules may already be on the response: an add whose value is already there is skipped. The reapply after a
    /// failed forward passes it, because YARP ran the response transform on its own 502/504 before, or cleared the response since (an
    /// upstream that failed after its headers), and only this tells the two apart without a flag that a clear would leave stale.</param>
    public static void ApplyResponse(IHeaderDictionary headers, CustomHeaderRule[] rules, bool again = false)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rules);
        foreach (var rule in rules)
        {
            switch (rule.Operation)
            {
                case HeaderOperation.Remove:
                    headers.Remove(rule.Name);
                    break;
                case HeaderOperation.Add when again && Holds(headers, rule):
                    break;
                case HeaderOperation.Add when headers.TryGetValue(rule.Name, out var existing) && existing.Count > 0:
                    headers[rule.Name] = StringValues.Concat(existing, rule.Value);
                    break;
                default:
                    headers[rule.Name] = rule.Value;
                    break;
            }
        }
    }

    private static bool Holds(IHeaderDictionary headers, in CustomHeaderRule rule)
    {
        if (!headers.TryGetValue(rule.Name, out var existing))
        {
            return false;
        }

        var value = rule.Value.ToString();
        foreach (var held in existing)
        {
            if (string.Equals(held, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Applies request rules to the request the upstream receives. A header that describes the body lives on the request's
    /// content, so a request without a body has nowhere to carry it and the rule does nothing there.
    /// </summary>
    public static void ApplyRequest(HttpRequestMessage request, CustomHeaderRule[] rules)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rules);
        foreach (var rule in rules)
        {
            System.Net.Http.Headers.HttpHeaders? headers = rule.OnContent ? request.Content?.Headers : request.Headers;
            if (headers is null)
            {
                continue;
            }

            if (rule.Operation != HeaderOperation.Add)
            {
                headers.Remove(rule.Name);
            }

            if (rule.Operation != HeaderOperation.Remove)
            {
                headers.TryAddWithoutValidation(rule.Name, rule.Value.ToString());
            }
        }
    }

    // The names HttpClient files under HttpContentHeaders (its HttpHeaderType.Content), fixed by the runtime.
    private static bool IsContentHeader(string name) => name.ToUpperInvariant() switch
    {
        "ALLOW" or "CONTENT-DISPOSITION" or "CONTENT-ENCODING" or "CONTENT-LANGUAGE" or "CONTENT-LENGTH" or "CONTENT-LOCATION" or "CONTENT-MD5"
            or "CONTENT-RANGE" or "CONTENT-TYPE" or "EXPIRES" or "LAST-MODIFIED" => true,
        _ => false,
    };
}
