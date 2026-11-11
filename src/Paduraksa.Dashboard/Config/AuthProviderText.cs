using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>An auth provider in the dashboard's words: the name of a preset, one line for a provider, and the phrase a picker's inherit
/// choice carries. A provider is never named by ID.</summary>
public static class AuthProviderText
{
    /// <summary>The presets in the order the editor offers them.</summary>
    public static IReadOnlyList<ForwardAuthPreset> Presets { get; } =
        [ForwardAuthPreset.Authentik, ForwardAuthPreset.Authelia, ForwardAuthPreset.OAuth2Proxy, ForwardAuthPreset.Custom];

    public static string PresetLabel(ForwardAuthPreset preset) => preset switch
    {
        ForwardAuthPreset.Authentik => "authentik",
        ForwardAuthPreset.Authelia => "Authelia",
        ForwardAuthPreset.OAuth2Proxy => "oauth2-proxy",
        _ => "Other forward-auth endpoint",
    };

    /// <summary>What a bare address looks like for the preset, as the editor's placeholder.</summary>
    public static string UrlHint(ForwardAuthPreset preset) => preset switch
    {
        ForwardAuthPreset.Authentik => "http://authentik-outpost:9000",
        ForwardAuthPreset.Authelia => "http://authelia:9091",
        ForwardAuthPreset.OAuth2Proxy => "http://oauth2-proxy:4180",
        _ => "http://auth.internal:8080/verify",
    };

    /// <summary>What the preset fills in, as one sentence for the editor: the endpoint it appends to a bare address and what its answers carry.</summary>
    public static string PresetHelp(ForwardAuthPreset preset) => preset switch
    {
        ForwardAuthPreset.Authentik => "Adds /outpost.goauthentik.io/auth/traefik to a bare address, copies the X-authentik-* headers upstream and remembers an allow for the whole host. "
            + "Route /outpost.goauthentik.io/ on the same host to the outpost, with authentication off, for the sign-in pages.",
        ForwardAuthPreset.Authelia => "Adds /api/authz/forward-auth to a bare address, copies Remote-User, Remote-Groups, Remote-Name and Remote-Email upstream and remembers an allow for one path and method at a time, "
            + "since Authelia's rules decide per path.",
        ForwardAuthPreset.OAuth2Proxy => "Adds /oauth2/auth to a bare address, copies the X-Auth-Request-* headers upstream, sends a browser without a session to /oauth2/start and remembers an allow for the whole host. "
            + "Route /oauth2/ on the same host to oauth2-proxy, with authentication off.",
        _ => "The address is the whole URL. Nothing is copied upstream until you name the headers, and an allow is remembered for one path and method at a time.",
    };

    /// <summary>A provider as a list row reads it: "Authelia at http://authelia:9091".</summary>
    public static string Summary(AuthProviderConfig provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.ForwardAuth is not { } forward)
        {
            return "no endpoint";
        }

        var resolved = ForwardAuthPresets.Resolve(forward);
        return $"{PresetLabel(resolved.Preset)} at {Host(forward.Url)}";
    }

    /// <summary>What a layer would inherit, in the words of the picker's first choice: "no sign-in", or the provider's name. A switch that
    /// is off is no provider whatever is still remembered under it.</summary>
    public static string Inherited(AuthProviderSettings? inherited, IReadOnlyList<AuthProviderConfig> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (inherited is null || inherited.Enabled == false || inherited.AuthProviderId is not { IsEmpty: false } id)
        {
            return "no sign-in";
        }

        return NameOf(id, providers) ?? "a provider that isn't here";
    }

    /// <summary>The provider's name among <paramref name="providers"/>; null when none has it.</summary>
    public static string? NameOf(AuthProviderId id, IReadOnlyList<AuthProviderConfig> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        return providers.FirstOrDefault(provider => provider.Id == id)?.Name;
    }

    /// <summary>The cache scope in the editor's words.</summary>
    public static string ScopeLabel(ForwardAuthCacheScope scope) => scope switch
    {
        ForwardAuthCacheScope.Host => "The whole host",
        _ => "One path and method",
    };

    /// <summary>"authelia:9091" of a URL, or the text as typed when it isn't one yet.</summary>
    private static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : url;
}
