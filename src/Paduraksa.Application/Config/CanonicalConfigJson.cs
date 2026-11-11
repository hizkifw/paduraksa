using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Config;

/// <summary>
/// Canonical JSON of the config document: the bytes revisions store and hash. Two documents that mean the
/// same config serialize to the same bytes, and <c>Serialize(Deserialize(Serialize(d)))</c> equals
/// <c>Serialize(d)</c>. The byte-level rules are <see cref="ConfigJsonContext"/>'s; <see cref="Canonicalize"/> adds
/// the order and spelling of the content.
/// </summary>
/// <remarks>
/// <para><b>Canonical order.</b> Profiles, upstreams, rate-limit policies, access lists and hosts by ID; an access list's users by name (ordinal); destinations by ID (order means nothing to load
/// balancing). Routes by the order their host evaluates them in (<see cref="RouteOrderKey"/>): exact routes first, then
/// the longest prefix, then <see cref="RouteConfig.Order"/>, then the listed order. The canonical list reads top to
/// bottom the way requests are matched. Sets (host names, tags, IP allow-list addresses, trusted proxies) are sorted
/// ordinally without duplicates.</para>
/// <para><b>Canonical spelling.</b> Host names normalised (<see cref="ConfigNormalization.TryNormalizeHostName"/>).
/// Addresses and CIDRs in the form the parser prints (<c>2001:DB8::/32</c> → <c>2001:db8::/32</c>, a full-length prefix
/// as the bare address: <c>10.0.0.1/32</c> → <c>10.0.0.1</c>), so duplicates are found by meaning. Tags trimmed and
/// lowercase. The trusted-proxy header lowercase, and absent when it is the default (<c>x-forwarded-for</c>). Profile
/// and upstream names, destination addresses and redirect targets trimmed. Blank notes and a blank header are absent,
/// null lists and settings are empty, a nested settings record that sets nothing is no layer (<see cref="ISettingsLayer"/>), and active-health timings are set.
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

    /// <summary>Reads a document strictly (see <see cref="ConfigJsonContext"/>), as written: not canonicalised. A stored
    /// document that still has the inline <c>ipAllowList</c> is converted to access lists on the way in
    /// (<see cref="LegacyAccessListUpgrade"/>), and a user's inline <c>passwordHash</c> to a <c>credentialId</c>
    /// (<see cref="LegacyPasswordUpgrade"/>).</summary>
    /// <exception cref="JsonException">Not valid JSON, or not a valid document shape.</exception>
    public static ConfigDocument Deserialize(ReadOnlySpan<byte> utf8Json) => Deserialize(utf8Json, out _);

    /// <summary><see cref="Deserialize(ReadOnlySpan{byte})"/>, saying whether the document had to be converted (so startup can
    /// write the converted one back) and which password verifiers it carried (<see cref="LegacyPasswordUpgrade"/>: the caller that
    /// makes the document live imports them into the vault, or the passwords are gone).</summary>
    internal static ConfigDocument Deserialize(ReadOnlySpan<byte> utf8Json, out DocumentUpgrade upgrade)
    {
        if (UnsupportedApiVersion(utf8Json) is { } version)
        {
            throw new JsonException(UnsupportedApiVersionMessage(version), "$.apiVersion", null, null);
        }

        var converted = LegacyAccessListUpgrade.Upgrade(utf8Json);
        var passwords = LegacyPasswordUpgrade.Upgrade(converted ?? utf8Json);
        upgrade = converted is null && passwords is null ? DocumentUpgrade.None : new DocumentUpgrade(converted is not null, passwords is not null, passwords?.Credentials ?? []);
        var document = JsonSerializer.Deserialize(passwords?.Json ?? converted ?? utf8Json, ConfigJsonContext.Default.ConfigDocument)
                       ?? throw new JsonException("The config document can't be null.");
        RejectNullElements(document);
        return document;
    }

    /// <summary><see cref="Deserialize(ReadOnlySpan{byte})"/>, reporting a failure as a <see cref="ConfigError"/> with the JSON pointer of
    /// where reading stopped. A document of another <c>apiVersion</c> is reported as
    /// <see cref="ConfigErrorCodes.ApiVersionUnsupported"/> before anything else, so a newer document says so rather
    /// than failing on its first field this build doesn't know.</summary>
    public static bool TryDeserialize(ReadOnlySpan<byte> utf8Json, [NotNullWhen(true)] out ConfigDocument? document, [NotNullWhen(false)] out ConfigError? error) =>
        TryDeserialize(utf8Json, out document, out error, out _);

    /// <summary><see cref="TryDeserialize(ReadOnlySpan{byte}, out ConfigDocument?, out ConfigError?)"/>, saying what the document
    /// had to convert.</summary>
    internal static bool TryDeserialize(
        ReadOnlySpan<byte> utf8Json, [NotNullWhen(true)] out ConfigDocument? document, [NotNullWhen(false)] out ConfigError? error, out DocumentUpgrade upgrade)
    {
        upgrade = DocumentUpgrade.None;
        if (UnsupportedApiVersion(utf8Json) is { } version)
        {
            document = null;
            error = new ConfigError("/apiVersion", ConfigErrorCodes.ApiVersionUnsupported, UnsupportedApiVersionMessage(version));
            return false;
        }

        try
        {
            document = Deserialize(utf8Json, out upgrade);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            // The reader's own text names CLR types and is not written for a caller: the path is the useful part, and the message
            // is a fixed sentence.
            document = null;
            error = new ConfigError(ToJsonPointer(ex.Path), InvalidJsonCode, ReadMessage(ToJsonPointer(ex.Path)));
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
                var isApiVersion = reader.ValueTextEquals("apiVersion"u8);
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
        var settings = document.Settings ?? new();
        var trusted = settings.TrustedProxies ?? new();
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
            RateLimitPolicies = [.. NonNull(document.RateLimitPolicies).OrderBy(p => p.Id).Select(Canonical)],
            AccessLists = [.. NonNull(document.AccessLists).OrderBy(l => l.Id).Select(Canonical)],
            Hosts = [.. NonNull(document.Hosts).OrderBy(h => h.Id).Select(Canonical)],
        };
    }

    /// <summary>The checks reading applies, for a document that didn't come from reading (a draft handed in as an
    /// object): the <c>apiVersion</c> this build reads, and no null list elements. Without them a null element is a
    /// <see cref="NullReferenceException"/> deep inside the pipeline instead of an error with a path.</summary>
    internal static ConfigError? Preflight(ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.ApiVersion != ConfigDocument.V1)
        {
            return new ConfigError("/apiVersion", ConfigErrorCodes.ApiVersionUnsupported, UnsupportedApiVersionMessage(document.ApiVersion));
        }

        if (document.Settings?.TrustedProxies is null || document.Profiles is null || document.Upstreams is null || document.Hosts is null
            || document.RateLimitPolicies is null || document.AccessLists is null)
        {
            return new ConfigError(string.Empty, InvalidJsonCode, "The document needs its settings, trusted proxies, profiles, upstreams, rate-limit policies, access lists and hosts (empty lists, not null).");
        }

        try
        {
            RejectNullElements(document);
            return null;
        }
        catch (JsonException ex)
        {
            // The only JsonException this block throws is our own null-element check, so a fixed sentence with the pointer is exact.
            var pointer = ToJsonPointer(ex.Path);
            return new ConfigError(
                pointer,
                InvalidJsonCode,
                "The document has a null list element, which isn't allowed" + (pointer.Length == 0 ? "." : $" (at '{pointer}')."));
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
            for (var i = 0; i < (items?.Count ?? 0); i++)
            {
                if (items![i] is null)
                {
                    throw new JsonException($"A list element can't be null ({path}[{i}]).", $"{path}[{i}]", null, null);
                }
            }
        }

        Check(document.Settings.TrustedProxies.Proxies, "$.settings.trustedProxies.proxies");
        Check(document.Profiles, "$.profiles");

        Check(document.Upstreams, "$.upstreams");
        for (var i = 0; i < document.Upstreams.Count; i++)
        {
            Check(document.Upstreams[i].Destinations, $"$.upstreams[{i}].destinations");
        }

        Check(document.RateLimitPolicies, "$.rateLimitPolicies");

        Check(document.AccessLists, "$.accessLists");
        for (var i = 0; i < document.AccessLists.Count; i++)
        {
            var list = document.AccessLists[i];
            Check(list.Ip?.Allow, $"$.accessLists[{i}].ip.allow");
            Check(list.Ip?.Deny, $"$.accessLists[{i}].ip.deny");
            Check(list.BasicAuth?.Users, $"$.accessLists[{i}].basicAuth.users");
        }

        Check(document.Hosts, "$.hosts");
        for (var i = 0; i < document.Hosts.Count; i++)
        {
            var host = document.Hosts[i];
            var path = $"$.hosts[{i}]";
            Check(host.Names, $"{path}.names");
            Check(host.Routes, $"{path}.routes");
            Check(host.Tags, $"{path}.tags");
        }
    }

    /// <summary>
    /// A fixed sentence for a document this build can't read, with the pointer: the reader's own exception text names CLR types.
    /// </summary>
    private static string ReadMessage(string pointer) =>
        "The document isn't a valid Paduraksa config (does it carry `apiVersion: paduraksa.dev/v1`?): a member is missing, unknown, "
        + "repeated or of the wrong type"
        + (pointer.Length == 0 ? "." : $" (at '{pointer}').");

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
        var i = jsonPath[0] == '$' ? 1 : 0;
        while (i < jsonPath.Length)
        {
            if (jsonPath[i] == '.')
            {
                var end = jsonPath.IndexOfAny(['.', '['], i + 1);
                end = end < 0 ? jsonPath.Length : end;
                AppendToken(pointer, jsonPath[(i + 1)..end]);
                i = end;
            }
            else if (jsonPath.AsSpan(i).StartsWith("['"))
            {
                var end = jsonPath.IndexOf("']", i + 2, StringComparison.Ordinal);
                end = end < 0 ? jsonPath.Length : end;
                AppendToken(pointer, jsonPath[(i + 2)..end].Replace("\\'", "'", StringComparison.Ordinal));
                i = Math.Min(end + 2, jsonPath.Length);
            }
            else if (jsonPath[i] == '[')
            {
                var end = jsonPath.IndexOf(']', i + 1);
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

    private static RateLimitPolicyConfig Canonical(RateLimitPolicyConfig policy) => policy with
    {
        Name = ConfigNormalization.NormalizeName(policy.Name),
        Header = ConfigNormalization.CanonicalRateLimitHeader(policy.Header),
        Notes = ConfigNormalization.NormalizeNotes(policy.Notes),
    };

    private static AccessListConfig Canonical(AccessListConfig list) => list with
    {
        Name = ConfigNormalization.NormalizeName(list.Name),
        Notes = ConfigNormalization.NormalizeNotes(list.Notes),
        Ip = list.Ip is { } ip && (ip.Allow.Count > 0 || ip.Deny.Count > 0 || ip.DropDenied || ip.Countries is { Allow.Count: > 0 } or { Deny.Count: > 0 })
            ? ip with
            {
                Allow = SortedSet(ip.Allow, ConfigNormalization.CanonicalAddress),
                Deny = SortedSet(ip.Deny, ConfigNormalization.CanonicalAddress),
                Countries = ip.Countries is { Allow.Count: > 0 } or { Deny.Count: > 0 }
                    ? new AccessListCountries(SortedSet(ip.Countries.Allow, ConfigNormalization.CanonicalCountry), SortedSet(ip.Countries.Deny, ConfigNormalization.CanonicalCountry))
                    : null,
            }
            : null,

        // The password of a write never reaches a document: it is hashed first, and what is left of it is dropped here.
        BasicAuth = list.BasicAuth is { } auth && (auth.Users.Count > 0 || !string.IsNullOrWhiteSpace(auth.Realm))
            ? auth with
            {
                Realm = string.IsNullOrWhiteSpace(auth.Realm) ? null : auth.Realm.Trim(),
                Users = [.. NonNull(auth.Users).Select(u => u with { Username = u.Username ?? string.Empty, Password = null }).OrderBy(u => u.Username, StringComparer.Ordinal)],
            }
            : null,
    };

    private static HostConfig Canonical(HostConfig host) => host with
    {
        Names = SortedSet(host.Names, ConfigNormalization.CanonicalHostName),
        Certificate = host.Certificate ?? new(),
        Settings = Canonical(host.Settings),
        Routes = CanonicalRoutes(host.Routes),
        Redirect = host.Redirect is { } redirect ? redirect with { Target = Trim(redirect.Target) } : null,
        Tags = SortedSet(host.Tags, ConfigNormalization.NormalizeTag),
        Notes = ConfigNormalization.NormalizeNotes(host.Notes),
    };

    private static RouteConfig[] CanonicalRoutes(IReadOnlyList<RouteConfig>? routes)
    {
        var items = NonNull(routes).ToArray();
        var order = new int[items.Length];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => RouteOrderKey.For(items[a].Match, items[a].Path, items[a].Order, a)
            .CompareTo(RouteOrderKey.For(items[b].Match, items[b].Path, items[b].Order, b)));
        var canonical = new RouteConfig[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            canonical[i] = Canonical(items[order[i]]);
        }

        return canonical;
    }

    private static RouteConfig Canonical(RouteConfig route) => route with
    {
        Settings = (route.Settings ?? new()) with
        {
            ProofOfWork = DropIfNothingSet(route.Settings?.ProofOfWork),
            RateLimit = DropIfNothingSet(route.Settings?.RateLimit),
            AccessList = DropIfNothingSet(route.Settings?.AccessList),
        },
    };

    private static HostSettings Canonical(HostSettings? settings)
    {
        settings ??= new();
        return settings with
        {
            Tls = DropIfNothingSet(settings.Tls),
            Hsts = DropIfNothingSet(settings.Hsts),
            ServerHeader = DropIfNothingSet(settings.ServerHeader),
            ProofOfWork = DropIfNothingSet(settings.ProofOfWork),
            RateLimit = DropIfNothingSet(settings.RateLimit),
            Robots = DropIfNothingSet(settings.Robots),
            Cloudflare = DropIfNothingSet(settings.Cloudflare),
            AccessList = DropIfNothingSet(settings.AccessList),
        };
    }

    /// <summary>
    /// The one rule for a nested settings record: one that sets none of its fields is the same as no record at all, so it
    /// is written as absent and a config that never mentions it keeps its bytes (and its revision hash). Each record says
    /// what "sets nothing" means, so a new nested setting needs no line here.
    /// </summary>
    private static T? DropIfNothingSet<T>(T? settings)
        where T : class, ISettingsLayer => settings is { SetsNothing: true } ? null : settings;

    private static string[] SortedSet(IReadOnlyList<string>? values, Func<string?, string> normalize) =>
        [.. NonNull(values).Select(normalize).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static IEnumerable<T> NonNull<T>(IReadOnlyList<T>? items) => (items ?? []).Where(item => item is not null);

    private static string Trim(string? value) => (value ?? string.Empty).Trim();
}
