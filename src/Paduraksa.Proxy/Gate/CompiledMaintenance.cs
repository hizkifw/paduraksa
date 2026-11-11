using System.Globalization;

using Paduraksa.Core.Geo;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Pages;

namespace Paduraksa.Proxy.Gate;

/// <summary>
/// A host that is in maintenance, compiled once per snapshot: the answer with its page and status, the <c>Retry-After</c> as the string
/// it is sent as, and the access list whose allowed addresses skip the page. A host that isn't in maintenance has none of this (null on
/// the host), so the check on every request is one reference test. A request costs 0 B either way.
/// </summary>
/// <remarks>
/// The bypass is the access list's address side alone: a request skips the page when the list's allow rules (addresses, and countries in a
/// Cloudflare mode) admit it and its deny rules don't refuse it. A list with only deny rules, or a request outside every allow rule, is
/// served the page; a password is never asked for here, because a visitor who was refused a page has nothing to enter it into.
/// </remarks>
internal sealed class CompiledMaintenance
{
    public CompiledMaintenance(PrecomputedResponse response, string? retryAfter, CompiledAccessList? bypass, CustomErrorPage page)
    {
        Response = response;
        RetryAfter = retryAfter;
        Bypass = bypass;
        Page = page;
    }

    /// <summary>What a request that isn't bypassed is answered with.</summary>
    public PrecomputedResponse Response { get; }

    /// <summary>The precomputed <c>Retry-After</c> (seconds), or null for none.</summary>
    public string? RetryAfter { get; }

    public CompiledAccessList? Bypass { get; }

    public CustomErrorPage Page { get; }

    public int Status => Response.StatusCode;

    public bool Bypasses(IpKey client, CountryCode country) =>
        Bypass is { } list && list.AddressVerdictOf(client, country) == AddressVerdict.Allowed;

    public static string RetryAfterOf(int seconds) => seconds.ToString(CultureInfo.InvariantCulture);
}
