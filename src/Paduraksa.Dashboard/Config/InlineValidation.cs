using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;

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

    /// <summary>The errors of <paramref name="profile"/> in <paramref name="current"/>, relative to the profile (<c>/name</c>, <c>/settings/ipAllowList</c>).</summary>
    public static IReadOnlyDictionary<string, string> ForProfile(ConfigDocument current, ProfileConfig profile)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(profile);
        var document = current with { Profiles = [.. current.Profiles.Where(p => p.Id != profile.Id), profile] };
        return Relative(ConfigValidator.Validate(document), $"/profiles/{document.Profiles.Count - 1}");
    }

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
