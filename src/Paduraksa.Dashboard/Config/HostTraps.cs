using Paduraksa.Application.Compilation;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>Which hosts of a document serve a honeypot, resolved the way the compiler resolves it: a host's own layer, its profile and the global defaults.</summary>
public static class HostTraps
{
    /// <summary>Whether <paramref name="host"/> serves a trap: it is on in the resolved settings, and the host is not an admin host (which can't have one).</summary>
    public static bool Has(ConfigDocument document, HostConfig host)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(host);
        if (host.Kind == HostKind.Admin)
        {
            return false;
        }

        var profile = host.ProfileId is { } profileId ? document.Profiles.FirstOrDefault(p => p.Id == profileId) : null;
        return HostSettingsLayers.MergeAll(HostSettingsLayers.BuiltIn, document.Settings.Defaults, profile?.Settings, host.Settings).Honeypot?.Enabled == true;
    }

    /// <summary>Whether an enabled host of <paramref name="document"/> serves a trap.</summary>
    public static bool Any(ConfigDocument document) => document.Hosts.Any(host => host.Enabled && Has(document, host));
}
