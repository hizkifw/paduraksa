using System.Reflection;
using System.Text.Json;

namespace Paduraksa.Dashboard.AboutArea;

/// <summary>One third-party component the server or its container image carries.</summary>
/// <param name="Name">The package or product name.</param>
/// <param name="Version">Its version; null for a component that has none of its own (the icon paths inside MudBlazor).</param>
/// <param name="License">The SPDX ID of the license it is used under: a key of <see cref="ThirdPartyNotices.Licenses"/>.</param>
/// <param name="Copyright">Its own copyright line, as its package states it.</param>
/// <param name="Url">Its project page.</param>
/// <param name="NoticesUrl">Where its own third-party notices are, for a component that is itself a bundle (the .NET runtime).</param>
public sealed record NoticeComponent(string Name, string? Version, string License, string Copyright, string? Url, string? NoticesUrl);

/// <param name="Name">The license's name.</param>
/// <param name="Text">Its full text, without any one holder's copyright line.</param>
public sealed record NoticeLicense(string Name, string Text);

/// <summary>
/// The open-source components Paduraksa ships with and their license texts, read from <c>THIRD-PARTY-NOTICES.json</c> at the repository root, which
/// <c>scripts/generate-third-party-notices.py</c> writes from the server's publish output and the dashboard assembly embeds.
/// </summary>
public sealed class ThirdPartyNotices
{
    /// <summary>The resource name of the embedded file.</summary>
    public const string ResourceName = "Paduraksa.Dashboard.About.THIRD-PARTY-NOTICES.json";

    private ThirdPartyNotices(IReadOnlyList<NoticeComponent> components, IReadOnlyDictionary<string, NoticeLicense> licenses)
    {
        Components = components;
        Licenses = licenses;
    }

    /// <summary>In the file's order (by name).</summary>
    public IReadOnlyList<NoticeComponent> Components { get; }

    /// <summary>Each license's text once, by SPDX ID.</summary>
    public IReadOnlyDictionary<string, NoticeLicense> Licenses { get; }

    /// <summary>The file embedded in this assembly.</summary>
    public static ThirdPartyNotices Embedded { get; } = FromAssembly(typeof(ThirdPartyNotices).Assembly);

    public static ThirdPartyNotices FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The assembly has no {ResourceName}; the Dashboard project embeds THIRD-PARTY-NOTICES.json from the repository root.");
        return Parse(JsonDocument.Parse(stream).RootElement);
    }

    /// <summary>Reads the file's JSON; a license a component names that the file has no text for is an error, never a silent gap.</summary>
    public static ThirdPartyNotices Parse(JsonElement root)
    {
        var licenses = root.GetProperty("licenses").EnumerateObject()
            .ToDictionary(license => license.Name, license => new NoticeLicense(license.Value.GetProperty("name").GetString()!, license.Value.GetProperty("text").GetString()!), StringComparer.Ordinal);
        var components = root.GetProperty("components").EnumerateArray().Select(component => new NoticeComponent(
            component.GetProperty("name").GetString()!,
            Optional(component, "version"),
            component.GetProperty("license").GetString()!,
            component.GetProperty("copyright").GetString()!,
            Optional(component, "url"),
            Optional(component, "noticesUrl"))).ToList();
        if (components.FirstOrDefault(component => !licenses.ContainsKey(component.License)) is { } missing)
        {
            throw new InvalidOperationException($"{missing.Name} names the license {missing.License}, which the notices have no text for.");
        }

        return new ThirdPartyNotices(components, licenses);
    }

    private static string? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() : null;
}
