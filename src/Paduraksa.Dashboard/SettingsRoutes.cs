using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard;

/// <summary>The Settings subsections (PLAN §12, page 10) that are pages of their own, reached from the settings hub's tiles.</summary>
public static class SettingsRoutes
{
    public const string AcmeAccounts = "/settings/acme-accounts";

    public const string DnsProviders = "/settings/dns-providers";

    /// <summary>The global defaults: the bottom layer of host settings, unknown names, the default certificate, trusted proxies.</summary>
    public const string Defaults = "/settings/defaults";

    /// <summary>The profiles: named sets of host settings.</summary>
    public const string Profiles = "/settings/profiles";

    /// <summary>The form for a new profile.</summary>
    public const string NewProfile = "/settings/profiles/new";

    /// <summary>The users and their roles.</summary>
    public const string Users = "/settings/users";

    /// <summary>The API tokens: one's own, and everyone's with <c>tokens:all</c>.</summary>
    public const string Tokens = "/settings/tokens";

    /// <summary>A profile's page: <c>/settings/profiles/prof_...</c>. The page parses the segment back into a <see cref="ProfileId"/>.</summary>
    public static string Profile(ProfileId id) => $"{Profiles}/{id}";
}
