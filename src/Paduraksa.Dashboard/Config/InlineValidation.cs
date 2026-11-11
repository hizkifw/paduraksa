using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// The forms' inline validation: the object being edited is put into the current config (last in its list, as the command pipeline
/// validates it) and run through the real validator, so a form shows the same rules a command enforces, as the person types. The dry
/// run in the review drawer stays the authority (it also compiles, and checks versions and certificates).
/// </summary>
public static class InlineValidation
{
    /// <summary>The errors of <paramref name="host"/> in <paramref name="current"/>, by JSON pointer relative to the host (<c>/names/0</c>).</summary>
    public static IReadOnlyDictionary<string, string> ForHost(ConfigDocument current, HostConfig host)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(host);
        var document = current with { Hosts = [.. current.Hosts.Where(h => h.Id != host.Id), host] };
        return Relative(ConfigValidator.Validate(document), $"/hosts/{document.Hosts.Count - 1}", document);
    }

    /// <summary>The errors of <paramref name="upstream"/> in <paramref name="current"/>, relative to the upstream (<c>/destinations/0/address</c>).</summary>
    public static IReadOnlyDictionary<string, string> ForUpstream(ConfigDocument current, UpstreamConfig upstream)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(upstream);
        var document = current with { Upstreams = [.. current.Upstreams.Where(u => u.Id != upstream.Id), upstream] };
        return Relative(ConfigValidator.Validate(document), $"/upstreams/{document.Upstreams.Count - 1}");
    }

    /// <summary>The errors of <paramref name="settings"/> in place of <paramref name="current"/>'s, relative to the settings (<c>/trustedProxies/proxies</c>).</summary>
    public static IReadOnlyDictionary<string, string> ForSettings(ConfigDocument current, GlobalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(settings);
        return Relative(ConfigValidator.Validate(current with { Settings = settings }), "/settings");
    }

    /// <summary>The errors of <paramref name="profile"/> in <paramref name="current"/>, relative to the profile (<c>/name</c>, <c>/settings/accessList/accessListId</c>).</summary>
    public static IReadOnlyDictionary<string, string> ForProfile(ConfigDocument current, ProfileConfig profile)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(profile);
        var document = current with { Profiles = [.. current.Profiles.Where(p => p.Id != profile.Id), profile] };
        return Relative(ConfigValidator.Validate(document), $"/profiles/{document.Profiles.Count - 1}");
    }

    /// <summary>The errors of <paramref name="policy"/> in <paramref name="current"/>, relative to it (<c>/requests</c>, <c>/header</c>).</summary>
    public static IReadOnlyDictionary<string, string> ForRateLimitPolicy(ConfigDocument current, RateLimitPolicyConfig policy)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(policy);
        var document = current with { RateLimitPolicies = [.. current.RateLimitPolicies.Where(p => p.Id != policy.Id), policy] };
        return Relative(ConfigValidator.Validate(document), $"/rateLimitPolicies/{document.RateLimitPolicies.Count - 1}");
    }

    /// <summary>
    /// The errors of <paramref name="list"/> in <paramref name="current"/>, relative to it (<c>/name</c>,
    /// <c>/basicAuth/users/0/username</c>). A typed password is checked as the server will check it, once the vault has it:
    /// a credential ID stands in for it here, so only an empty or too long one is an error and nothing reaches the vault.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ForAccessList(ConfigDocument current, AccessListConfig list)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(list);
        var handedOver = list with
        {
            BasicAuth = list.BasicAuth is { } auth
                ? auth with { Users = [.. auth.Users.Select(HandedToVault)] }
                : null,
        };
        var document = current with { AccessLists = [.. current.AccessLists.Where(l => l.Id != list.Id), handedOver] };
        return Relative(ConfigValidator.Validate(document), $"/accessLists/{document.AccessLists.Count - 1}");
    }

    /// <summary>The errors of <paramref name="set"/> in <paramref name="current"/>, relative to it (<c>/name</c>, <c>/rules/0/match/path/pattern</c>).
    /// A regex is built as the server will build it, so the reason it can't be is the message at its field.</summary>
    public static IReadOnlyDictionary<string, string> ForRuleSet(ConfigDocument current, RuleSetConfig set)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(set);
        var document = current with { RuleSets = [.. current.RuleSets.Where(r => r.Id != set.Id), set] };
        return Relative(ConfigValidator.Validate(document), $"/ruleSets/{document.RuleSets.Count - 1}");
    }

    // What the server leaves in a user's place once the vault has their password: a credential ID (a new one here, so none repeats).
    private static AccessListUserConfig HandedToVault(AccessListUserConfig user) =>
        user.Password is { Length: > 0 } password && System.Text.Encoding.UTF8.GetByteCount(password) <= AccessListLimits.MaxPasswordBytes
            ? user with { CredentialId = user.CredentialId ?? PasswordCredentialId.New(), Password = null }
            : user;

    /// <summary>The first message per path of <paramref name="errors"/> that point into <paramref name="prefix"/>, relative to it, as sentences.</summary>
    public static IReadOnlyDictionary<string, string> Relative(IEnumerable<ConfigError> errors, string prefix, ConfigDocument? document = null)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(prefix);
        Dictionary<string, string> byPath = new(StringComparer.Ordinal);
        var nested = prefix + "/";
        foreach (var error in errors)
        {
            if (error.Path == prefix || error.Path.StartsWith(nested, StringComparison.Ordinal))
            {
                byPath.TryAdd(error.Path[prefix.Length..], ReviewNotices.Text(error, document));
            }
        }

        return byPath;
    }
}
