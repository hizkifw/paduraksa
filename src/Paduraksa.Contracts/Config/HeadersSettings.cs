using System.Text.Json.Serialization;

using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Config;

/// <summary>
/// Custom headers: rules for the request the upstream receives and for the response the client receives. The layers merge
/// rule by rule, keyed on the header name (ignoring case) per direction: a layer's rule for a name replaces the rule a lower
/// layer has for it, and every other inherited rule stays. A route's rules go over its host's the same way.
/// </summary>
/// <remarks>
/// A rule can't name a header the proxy owns (framing and hop-by-hop headers, <c>Host</c>, the forwarding, request-ID and
/// Cloudflare headers on the request, HSTS, <c>Alt-Svc</c> and the request ID on the response; see
/// <see cref="CustomHeaderRules"/>), nor one the host's or route's auth provider vouches for. Response rules apply to
/// forwarded responses and to the proxy's own answers for the host (error pages, redirects, maintenance), after the
/// built-in headers, so a rule for <c>Server</c> wins over the <c>Server</c> setting.
/// </remarks>
public sealed record HeadersSettings : ISettingsLayer
{
    /// <summary>Rules for the request the upstream receives, at most 32, one per header name; at most 32 also after the layers merge.</summary>
    public IReadOnlyList<HeaderRuleConfig>? Request { get; init; }

    /// <summary>Rules for the response the client receives, at most 32, one per header name; at most 32 also after the layers merge.</summary>
    public IReadOnlyList<HeaderRuleConfig>? Response { get; init; }

    [JsonIgnore]
    public bool SetsNothing => Request is not { Count: > 0 } && Response is not { Count: > 0 };
}

/// <summary>One custom header rule.</summary>
/// <param name="Name">The header's name: 1-64 token characters.</param>
/// <param name="Operation">What to do with the header.</param>
/// <param name="Value">The value <see cref="HeaderOperation.Set"/> and <see cref="HeaderOperation.Add"/> send: 1-4096 visible
/// ASCII characters, spaces or tabs, with no leading or trailing whitespace. Sent as written, with no placeholders. Absent for
/// <see cref="HeaderOperation.Remove"/>.</param>
public sealed record HeaderRuleConfig(string Name, HeaderOperation Operation = HeaderOperation.Set, string? Value = null);
