using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

using Paduraksa.Contracts.Config;

namespace Paduraksa.Application.Config;

/// <summary>
/// Canonical JSON of the config document (PLAN §6.4): the bytes revisions store and hash. Two documents that mean the
/// same config serialize to the same bytes, and <c>Serialize(Deserialize(Serialize(d)))</c> equals
/// <c>Serialize(d)</c>. The byte-level rules are <see cref="ConfigJsonContext"/>'s; <see cref="Canonicalize"/> adds
/// the order and spelling of the content.
/// </summary>
/// <remarks>
/// <para><b>Canonical order.</b> Profiles, upstreams and hosts by ID; destinations by ID (order means nothing to load
/// balancing). Routes by a <i>stable</i> sort on <see cref="RouteConfig.Order"/>: routes with equal orders keep their
/// listed order, because that order decides which one matches (first match wins), so the canonical list reads top
/// to bottom the way requests are matched. Sets (host names, tags, IP allow-list addresses, trusted proxies) are sorted
/// ordinally without duplicates.</para>
/// <para><b>Canonical spelling.</b> Host names normalised (<see cref="ConfigNormalization.TryNormalizeHostName"/>).
/// Addresses and CIDRs in the form the parser prints (<c>2001:DB8::/32</c> → <c>2001:db8::/32</c>, a full-length prefix
/// as the bare address: <c>10.0.0.1/32</c> → <c>10.0.0.1</c>), so duplicates are found by meaning. Tags trimmed and
/// lowercase. The trusted-proxy header lowercase, and absent when it is the default (<c>x-forwarded-for</c>). Profile
/// and upstream names, destination addresses and redirect targets trimmed. Blank notes and a blank header are absent,
/// null lists and settings are empty, a TLS layer that sets nothing is no layer, and active-health timings are set.
/// Values that don't parse (host names, addresses) are only trimmed, for validation to report. Everything else
/// (URLs, route-path case, bodies, an explicit load-balancing policy) is kept as written, since changing it could
/// change what it means.</para>
/// <para>Null elements are dropped (only a hand-built document has them: reading rejects them). Objects with duplicate
/// IDs (invalid anyway) keep their relative order.</para>
/// </remarks>
public static class CanonicalConfigJson
{
    /// <summary>Error code of a document that isn't valid JSON for the model.</summary>
    public const string InvalidJsonCode = "json.invalid";

    /// <summary>The canonical UTF-8 bytes of <paramref name="document"/>.</summary>
    public static byte[] Serialize(ConfigDocument document) =>
        JsonSerializer.SerializeToUtf8Bytes(Canonicalize(document), ConfigJsonContext.Default.ConfigDocument);

    /// <summary>Reads a document strictly (see <see cref="ConfigJsonContext"/>), as written: not canonicalised. Null
    /// list elements are rejected too.</summary>
    /// <exception cref="JsonException">Not valid JSON, or not a valid document shape.</exception>
    public static ConfigDocument Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (UnsupportedApiVersion(utf8Json) is { } version)
        {
            throw new JsonException(UnsupportedApiVersionMessage(version), "$.apiVersion", null, null);
        }

        ConfigDocument document = JsonSerializer.Deserialize(utf8Json, ConfigJsonContext.Default.ConfigDocument)
            ?? throw new JsonException("The config document can't be null.");
        RejectNullElements(document);
        return document;
    }

    /// <summary><see cref="Deserialize"/>, reporting a failure as a <see cref="ConfigError"/> with the JSON pointer of
    /// where reading stopped. A document of another <c>apiVersion</c> is reported as
    /// <see cref="ConfigErrorCodes.ApiVersionUnsupported"/> before anything else, so a newer document says so rather
    /// than failing on its first field this build doesn't know.</summary>
    public static bool TryDeserialize(ReadOnlySpan<byte> utf8Json, [NotNullWhen(true)] out ConfigDocument? document, [NotNullWhen(false)] out ConfigError? error)
    {
        if (UnsupportedApiVersion(utf8Json) is { } version)
        {
            document = null;
            error = new ConfigError("/apiVersion", ConfigErrorCodes.ApiVersionUnsupported, UnsupportedApiVersionMessage(version));
            return false;
        }

        try
        {
            document = Deserialize(utf8Json);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            document = null;
            error = new ConfigError(ToJsonPointer(ex.Path), InvalidJsonCode, ex.Message);
            return false;
        }
    }

    // The top-level apiVersion when it is a string other than V1; null when it is V1, absent, or the input is no object
    // (reading proper reports those).
    private static string? UnsupportedApiVersion(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            Utf8JsonReader reader = new(utf8Json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isApiVersion = reader.ValueTextEquals("apiVersion"u8);
                if (!reader.Read())
                {
                    return null;
                }

                if (isApiVersion)
                {
                    return reader.TokenType == JsonTokenType.String && reader.GetString() is { } version && version != ConfigDocument.V1
                        ? version
                        : null;
                }

                reader.Skip();
            }
        }
        catch (JsonException)
        {
            // Malformed: reading proper reports where.
        }

        return null;
    }

    private static string UnsupportedApiVersionMessage(string version) =>
        $"apiVersion '{version}' isn't supported; this build reads '{ConfigDocument.V1}'";

    /// <summary>The canonical form of <paramref name="document"/> (see the remarks); idempotent.</summary>
    public static ConfigDocument Canonicalize(ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        GlobalSettings settings = document.Settings ?? new();
        TrustedProxySettings trusted = settings.TrustedProxies ?? new();
        return document with
        {
            Settings = settings with
            {
                Defaults = Canonical(settings.Defaults),
                TrustedProxies = trusted with
                {
                    Proxies = SortedSet(trusted.Proxies, ConfigNormalization.CanonicalAddress),
                    Header = ConfigNormalization.CanonicalForwardingHeader(trusted.Header),
                },
            },
            Profiles = [.. NonNull(document.Profiles).OrderBy(p => p.Id).Select(Canonical)],
            Upstreams = [.. NonNull(document.Upstreams).OrderBy(u => u.Id).Select(Canonical)],
            Hosts = [.. NonNull(document.Hosts).OrderBy(h => h.Id).Select(Canonical)],
        };
    }

    /// <summary>
    /// The checks reading applies, for a document that didn't come from reading (a draft handed in as an object): the
    /// <c>apiVersion</c> this build reads, and no null list elements. Null: it passes. Without them a null element is a
    /// <see cref="NullReferenceException"/> deep inside the pipeline instead of an error with a path.
    /// </summary>
    internal static ConfigError? Preflight(ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.ApiVersion != ConfigDocument.V1)
        {
            return new ConfigError("/apiVersion", ConfigErrorCodes.ApiVersionUnsupported, UnsupportedApiVersionMessage(document.ApiVersion));
        }

        if (document.Settings?.TrustedProxies is null || document.Profiles is null || document.Upstreams is null || document.Hosts is null)
        {
            return new ConfigError(string.Empty, InvalidJsonCode, "The document needs its settings, trusted proxies, profiles, upstreams and hosts (empty lists, not null).");
        }

        try
        {
            RejectNullElements(document);
            return null;
        }
        catch (JsonException ex)
        {
            return new ConfigError(ToJsonPointer(ex.Path), InvalidJsonCode, ex.Message);
        }
        catch (NullReferenceException)
        {
            return new ConfigError(string.Empty, InvalidJsonCode, "The document has a null where an object is required.");
        }
    }

    // Reading leaves null elements in lists (nullable annotations only cover properties); report the first one with
    // its path, like any other read error.
    private static void RejectNullElements(ConfigDocument document)
    {
        static void Check<T>(IReadOnlyList<T>? items, string path)
        {
            for (int i = 0; i < (items?.Count ?? 0); i++)
            {
                if (items![i] is null)
                {
                    throw new JsonException($"A list element can't be null ({path}[{i}]).", $"{path}[{i}]", null, null);
                }
            }
        }

        static void Settings(HostSettings? settings, string path) => Check(settings?.IpAllowList?.Addresses, $"{path}.ipAllowList.addresses");

        Settings(document.Settings.Defaults, "$.settings.defaults");
        Check(document.Settings.TrustedProxies.Proxies, "$.settings.trustedProxies.proxies");
        Check(document.Profiles, "$.profiles");
        for (int i = 0; i < document.Profiles.Count; i++)
        {
            Settings(document.Profiles[i].Settings, $"$.profiles[{i}].settings");
        }

        Check(document.Upstreams, "$.upstreams");
        for (int i = 0; i < document.Upstreams.Count; i++)
        {
            Check(document.Upstreams[i].Destinations, $"$.upstreams[{i}].destinations");
        }

        Check(document.Hosts, "$.hosts");
        for (int i = 0; i < document.Hosts.Count; i++)
        {
            HostConfig host = document.Hosts[i];
            string path = $"$.hosts[{i}]";
            Check(host.Names, $"{path}.names");
            Settings(host.Settings, $"{path}.settings");
            Check(host.Routes, $"{path}.routes");
            for (int j = 0; j < host.Routes.Count; j++)
            {
                Check(host.Routes[j].Settings.IpAllowList?.Addresses, $"{path}.routes[{j}].settings.ipAllowList.addresses");
            }

            Check(host.Tags, $"{path}.tags");
        }
    }

    /// <summary>
    /// Converts a System.Text.Json path (<c>$.hosts[3].names[0]</c>, <c>$['odd.name']</c>) into a JSON pointer
    /// (<c>/hosts/3/names/0</c>); null or <c>$</c> is the whole document (<c>""</c>).
    /// </summary>
    internal static string ToJsonPointer(string? jsonPath)
    {
        if (string.IsNullOrEmpty(jsonPath) || jsonPath == "$")
        {
            return string.Empty;
        }

        StringBuilder pointer = new();
        int i = jsonPath[0] == '$' ? 1 : 0;
        while (i < jsonPath.Length)
        {
            if (jsonPath[i] == '.')
            {
                int end = jsonPath.IndexOfAny(['.', '['], i + 1);
                end = end < 0 ? jsonPath.Length : end;
                AppendToken(pointer, jsonPath[(i + 1)..end]);
                i = end;
            }
            else if (jsonPath.AsSpan(i).StartsWith("['"))
            {
                int end = jsonPath.IndexOf("']", i + 2, StringComparison.Ordinal);
                end = end < 0 ? jsonPath.Length : end;
                AppendToken(pointer, jsonPath[(i + 2)..end].Replace("\\'", "'", StringComparison.Ordinal));
                i = Math.Min(end + 2, jsonPath.Length);
            }
            else if (jsonPath[i] == '[')
            {
                int end = jsonPath.IndexOf(']', i + 1);
                end = end < 0 ? jsonPath.Length : end;
                AppendToken(pointer, jsonPath[(i + 1)..end]);
                i = Math.Min(end + 1, jsonPath.Length);
            }
            else
            {
                break;
            }
        }

        return pointer.ToString();
    }

    private static void AppendToken(StringBuilder pointer, string token) =>
        pointer.Append('/').Append(token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));

    private static ProfileConfig Canonical(ProfileConfig profile) => profile with
    {
        Name = ConfigNormalization.NormalizeName(profile.Name),
        Settings = Canonical(profile.Settings),
        Notes = ConfigNormalization.NormalizeNotes(profile.Notes),
    };

    private static UpstreamConfig Canonical(UpstreamConfig upstream) => upstream with
    {
        Name = ConfigNormalization.NormalizeName(upstream.Name),
        Destinations = [.. NonNull(upstream.Destinations).OrderBy(d => d.Id).Select(d => d with { Address = Trim(d.Address) })],
        ActiveHealth = upstream.ActiveHealth is { } active
            ? active with
            {
                Interval = active.Interval ?? ActiveHealthConfig.DefaultInterval,
                Timeout = active.Timeout ?? ActiveHealthConfig.DefaultTimeout,
            }
            : null,
        Notes = ConfigNormalization.NormalizeNotes(upstream.Notes),
    };

    private static HostConfig Canonical(HostConfig host) => host with
    {
        Names = SortedSet(host.Names, ConfigNormalization.CanonicalHostName),
        Certificate = host.Certificate ?? new(),
        Settings = Canonical(host.Settings),
        Routes = [.. NonNull(host.Routes).OrderBy(r => r.Order).Select(Canonical)], // stable: ties keep the listed order
        Redirect = host.Redirect is { } redirect ? redirect with { Target = Trim(redirect.Target) } : null,
        Tags = SortedSet(host.Tags, ConfigNormalization.NormalizeTag),
        Notes = ConfigNormalization.NormalizeNotes(host.Notes),
    };

    private static RouteConfig Canonical(RouteConfig route) => route with
    {
        Settings = (route.Settings ?? new()) with { IpAllowList = Canonical(route.Settings?.IpAllowList) },
    };

    private static HostSettings Canonical(HostSettings? settings)
    {
        settings ??= new();
        return settings with
        {
            Tls = settings.Tls is { MinimumVersion: null, TcpAlpn: null, Http3: null } ? null : settings.Tls,
            IpAllowList = Canonical(settings.IpAllowList),
        };
    }

    private static IpAllowListSettings? Canonical(IpAllowListSettings? allowList) =>
        allowList is null ? null : allowList with { Addresses = SortedSet(allowList.Addresses, ConfigNormalization.CanonicalAddress) };

    private static string[] SortedSet(IReadOnlyList<string>? values, Func<string?, string> normalize) =>
        [.. NonNull(values).Select(normalize).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static IEnumerable<T> NonNull<T>(IReadOnlyList<T>? items) => (items ?? []).Where(item => item is not null);

    private static string Trim(string? value) => (value ?? string.Empty).Trim();
}
