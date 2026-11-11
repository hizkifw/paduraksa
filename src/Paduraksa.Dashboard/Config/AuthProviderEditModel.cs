using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// An auth provider as its form edits it: a name, the preset, the endpoint, how long to wait and to remember an allow, and the header lists as
/// lines. A field the person leaves empty is left null in the config, so the preset's value applies; the form shows what that value is. A new
/// provider gets a fresh typed ID, and the version the form opened with makes a concurrent edit a conflict.
/// </summary>
public sealed class AuthProviderEditModel
{
    private readonly AuthProviderConfig? _original;

    private AuthProviderEditModel(AuthProviderConfig? original, AuthProviderId id, AuthProviderConfig? source = null)
    {
        _original = original;
        Id = id;
        source ??= original;
        Name = source?.Name ?? string.Empty;
        Notes = source?.Notes;
        var forward = source?.ForwardAuth;
        Preset = forward?.Preset ?? (source is null ? ForwardAuthPreset.Authentik : ForwardAuthPreset.Custom);
        Url = forward?.Url ?? string.Empty;
        TimeoutSeconds = forward?.TimeoutSeconds;
        CacheSeconds = forward?.CacheSeconds;
        CacheScope = forward?.CacheScope;
        RequestHeadersText = string.Join('\n', forward?.RequestHeaders ?? []);
        ResponseHeadersCustom = forward?.ResponseHeaders is not null;
        ResponseHeadersText = string.Join('\n', forward?.ResponseHeaders ?? []);
        SignInUrl = forward?.SignIn?.Url ?? string.Empty;
        SignInParameter = forward?.SignIn?.ReturnParameter ?? string.Empty;
    }

    public AuthProviderId Id { get; }

    public bool IsNew => _original is null;

    /// <summary>The version the form was opened with (0 for a new provider).</summary>
    public long Version => _original?.Version ?? 0;

    public string Name { get; set; }

    public string? Notes { get; set; }

    public ForwardAuthPreset Preset { get; set; }

    public string Url { get; set; }

    /// <summary>Null: the built-in wait.</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>Null: the built-in time an allow is remembered.</summary>
    public int? CacheSeconds { get; set; }

    /// <summary>Null: the preset's.</summary>
    public ForwardAuthCacheScope? CacheScope { get; set; }

    /// <summary>Extra client headers sent to the endpoint, one per line.</summary>
    public string RequestHeadersText { get; set; }

    /// <summary>The list below is the person's own (it may be empty: none); false takes the preset's list.</summary>
    public bool ResponseHeadersCustom { get; set; }

    /// <summary>The response headers copied upstream, one per line, when <see cref="ResponseHeadersCustom"/>.</summary>
    public string ResponseHeadersText { get; set; }

    public string SignInUrl { get; set; }

    public string SignInParameter { get; set; }

    public static AuthProviderEditModel New() => new(null, AuthProviderId.New());

    public static AuthProviderEditModel From(AuthProviderConfig provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new AuthProviderEditModel(provider, provider.Id);
    }

    /// <summary>A form for a provider made earlier and not applied yet (staged in a host's draft): still new, with what it was given.</summary>
    public static AuthProviderEditModel FromDraft(AuthProviderConfig provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new AuthProviderEditModel(null, provider.Id, provider);
    }

    /// <summary>What the preset (and the built-in values) give the fields the person left empty, for the form's hints.</summary>
    public ResolvedForwardAuth Resolved => ForwardAuthPresets.Resolve(ToConfig().ForwardAuth!);

    public AuthProviderConfig ToConfig() => new(
        Id,
        Name.Trim(),
        Version,
        string.IsNullOrWhiteSpace(Notes) ? null : Notes,
        new ForwardAuthConfig(
            Url.Trim(),
            Preset == ForwardAuthPreset.Custom ? null : Preset,
            TimeoutSeconds,
            CacheSeconds,
            CacheScope,
            Lines(RequestHeadersText) is { Count: > 0 } requests ? requests : null,
            ResponseHeadersFor(),
            string.IsNullOrWhiteSpace(SignInUrl) ? null : new ForwardAuthSignInConfig(SignInUrl.Trim(), string.IsNullOrWhiteSpace(SignInParameter) ? null : SignInParameter.Trim())));

    // A provider with no preset has no list of its own to fall back on, so an empty one is the same as none and stays out of the document.
    private IReadOnlyList<string>? ResponseHeadersFor()
    {
        if (!ResponseHeadersCustom && Preset != ForwardAuthPreset.Custom)
        {
            return null;
        }

        var lines = Lines(ResponseHeadersText);
        return lines.Count == 0 && Preset == ForwardAuthPreset.Custom ? null : lines;
    }

    /// <summary>The lines of <paramref name="text"/>, trimmed, without the empty ones.</summary>
    public static IReadOnlyList<string> Lines(string? text) =>
        (text ?? string.Empty).Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
