using System.Buffers;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;

using static Paduraksa.Application.Config.ConfigErrorCodes;

using IpNetwork = Paduraksa.Proxy.Net.IpNetwork;
using RoutePath = Paduraksa.Proxy.Snapshot.RoutePath;

namespace Paduraksa.Application.Config;

/// <summary>
/// Validates a config document (PLAN §6.1 step 3, §6.4): field rules and cross-object rules, every error with the JSON
/// pointer of the field it's about. Pure and cold-path. The snapshot compiler still runs afterwards as the backstop,
/// so this doesn't re-implement YARP's validator; it catches what the document can say on its own, where the
/// compiler can only name keys.
/// </summary>
/// <remarks>
/// <para>Validate the document <i>as the client sent it</i>, before <see cref="CanonicalConfigJson.Canonicalize"/>:
/// the paths then index what the client wrote, like those of <see cref="CanonicalConfigJson.TryDeserialize"/>. Values
/// are judged in their normal form (host names, trimmed names and addresses), so a document that validates still
/// validates once canonical.</para>
/// <para>Cross-object rules: IDs are unique across the whole document, also ignoring case (YARP compares its IDs
/// that way), routes across all hosts and destinations across all upstreams; profile and upstream names are unique
/// ignoring case, and host names across <i>enabled</i> hosts. As in the compiler, a disabled host is fully validated
/// but its names only have to be unique within itself, so a replacement can be staged next to the live host. A host
/// can't have two routes with the same match kind and path (ignoring case, like matching): the second could never
/// match. References to profiles and upstreams must resolve, and an upstream that an enabled host routes to needs an
/// enabled destination; certificate references can't be checked until certificates exist (M3). A host without HTTPS
/// (certificate mode none) can't force HTTPS, judged on the setting it ends up with after the profile and global
/// layers.</para>
/// </remarks>
public static class ConfigValidator
{
    /// <summary>Longest notes, in characters.</summary>
    public const int MaxNotesLength = 4096;

    /// <summary>Longest fixed-response body, in characters.</summary>
    public const int MaxBodyLength = 65536;

    /// <summary>Longest content type, in characters.</summary>
    public const int MaxContentTypeLength = 256;

    private static readonly SearchValues<char> HeaderNameChars =
        SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>Every error in <paramref name="document"/>, in document order; empty when it is valid.</summary>
    public static IReadOnlyList<ConfigError> Validate(ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Run run = new();
        run.CheckDocument(document);
        return run.Errors;
    }

    private sealed class Run
    {
        // Typed IDs by their string form: prefixes keep the types apart, and the comparison ignores case.
        private readonly Dictionary<string, string> _ids = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _enabledHostNames = new(StringComparer.Ordinal);

        public List<ConfigError> Errors { get; } = [];

        public void CheckDocument(ConfigDocument document)
        {
            if (document.ApiVersion != ConfigDocument.V1)
            {
                Add("/apiVersion", ApiVersionUnsupported, $"apiVersion '{document.ApiVersion}' isn't supported; this build reads '{ConfigDocument.V1}'");
            }

            CheckSettings(document.Settings ?? new(), "/settings");

            IReadOnlyList<ProfileConfig> profiles = document.Profiles ?? [];
            Dictionary<string, string> profileNames = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < profiles.Count; i++)
            {
                if (NotNull(profiles[i], $"/profiles/{i}"))
                {
                    CheckProfile(profiles[i], $"/profiles/{i}", profileNames);
                }
            }

            IReadOnlyList<HostConfig> hosts = document.Hosts ?? [];
            HashSet<UpstreamId> routedTo =
            [
                .. hosts.Where(h => h is { Enabled: true, Kind: HostKind.Proxy })
                    .SelectMany(h => h.Routes ?? [])
                    .Where(r => r is not null)
                    .Select(r => r.UpstreamId),
            ];
            IReadOnlyList<UpstreamConfig> upstreams = document.Upstreams ?? [];
            Dictionary<string, string> upstreamNames = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < upstreams.Count; i++)
            {
                if (NotNull(upstreams[i], $"/upstreams/{i}"))
                {
                    CheckUpstream(upstreams[i], $"/upstreams/{i}", upstreamNames, routedTo);
                }
            }

            // A duplicate profile ID is reported above; the first one is the layer.
            Dictionary<ProfileId, HostSettings?> profileSettings = [];
            foreach (ProfileConfig profile in profiles.Where(p => p is not null))
            {
                profileSettings.TryAdd(profile.Id, profile.Settings);
            }

            HashSet<UpstreamId> upstreamIds = [.. upstreams.Where(u => u is not null).Select(u => u.Id)];
            HostSettings? defaults = document.Settings?.Defaults;
            for (int i = 0; i < hosts.Count; i++)
            {
                if (NotNull(hosts[i], $"/hosts/{i}"))
                {
                    CheckHost(hosts[i], $"/hosts/{i}", defaults, profileSettings, upstreamIds);
                }
            }
        }

        private void CheckSettings(GlobalSettings settings, string path)
        {
            CheckVersion(settings.Version, path);
            CheckHostSettings(settings.Defaults, $"{path}/defaults");
            CheckEnum(settings.UnknownHost, $"{path}/unknownHost");
            if (settings.DefaultCertificateId is { IsEmpty: true })
            {
                Add($"{path}/defaultCertificateId", IdEmpty, "the default certificate ID can't be empty (leave it unset for none)");
            }

            CheckTrustedProxies(settings.TrustedProxies ?? new(), $"{path}/trustedProxies");
        }

        private void CheckTrustedProxies(TrustedProxySettings trusted, string path)
        {
            if (!CheckEnum(trusted.Mode, $"{path}/mode"))
            {
                return;
            }

            IReadOnlyList<string> proxies = trusted.Proxies ?? [];
            switch (trusted.Mode)
            {
                case TrustedProxyMode.Off when proxies.Count > 0:
                    Add($"{path}/proxies", TrustedProxiesProxiesUnexpected, "proxies are listed but the mode is off");
                    break;
                case TrustedProxyMode.CloudflareProxy when proxies.Count > 0:
                    Add($"{path}/proxies", TrustedProxiesProxiesUnexpected, "cloudflareProxy mode trusts Cloudflare's published edge ranges; list no proxies");
                    break;
                case TrustedProxyMode.Generic or TrustedProxyMode.CloudflareTunnel when proxies.Count == 0:
                    Add($"{path}/proxies", TrustedProxiesProxiesRequired, $"{Name(trusted.Mode)} mode needs at least one proxy address or CIDR");
                    break;
            }

            CheckAddresses(proxies, $"{path}/proxies");

            bool generic = trusted.Mode == TrustedProxyMode.Generic;
            bool cloudflare = trusted.Mode is TrustedProxyMode.CloudflareProxy or TrustedProxyMode.CloudflareTunnel;
            if (trusted.Header is { } header)
            {
                string name = header.Trim();
                if (!generic)
                {
                    Add($"{path}/header", TrustedProxiesOptionUnexpected, "a header is only configurable in generic mode (Cloudflare modes read CF-Connecting-IP)");
                }
                else if (name.Length == 0 || name.AsSpan().ContainsAnyExcept(HeaderNameChars))
                {
                    Add($"{path}/header", TrustedProxiesHeaderInvalid, $"'{header}' isn't a valid header name");
                }
            }

            if (trusted.UseTrueClientIp && !cloudflare)
            {
                Add($"{path}/useTrueClientIp", TrustedProxiesOptionUnexpected, "True-Client-IP only applies to the Cloudflare modes");
            }

            if (trusted.ForwardedForFallback && !cloudflare)
            {
                Add($"{path}/forwardedForFallback", TrustedProxiesOptionUnexpected, "the X-Forwarded-For fallback only applies to the Cloudflare modes");
            }

            if (trusted.TrustForwardedProto && !generic)
            {
                Add($"{path}/trustForwardedProto", TrustedProxiesOptionUnexpected, "trustForwardedProto only applies to generic mode (Cloudflare modes read CF-Visitor)");
            }
        }

        private void CheckProfile(ProfileConfig profile, string path, Dictionary<string, string> names)
        {
            CheckId(profile.Id, path);
            CheckVersion(profile.Version, path);
            CheckLabel(profile.Name, $"{path}/name", "profile", names);
            CheckHostSettings(profile.Settings, $"{path}/settings");
            CheckNotes(profile.Notes, path);
        }

        private void CheckUpstream(UpstreamConfig upstream, string path, Dictionary<string, string> names, HashSet<UpstreamId> routedTo)
        {
            CheckId(upstream.Id, path);
            CheckVersion(upstream.Version, path);
            CheckLabel(upstream.Name, $"{path}/name", "upstream", names);

            IReadOnlyList<DestinationConfig> destinations = upstream.Destinations ?? [];
            if (destinations.Count == 0)
            {
                Add($"{path}/destinations", UpstreamDestinationsRequired, "an upstream needs at least one destination");
            }
            else if (routedTo.Contains(upstream.Id) && !destinations.Any(d => d is { Enabled: true }))
            {
                Add($"{path}/destinations", UpstreamDestinationsNoneEnabled, "an enabled host routes to this upstream, so it needs an enabled destination");
            }

            for (int i = 0; i < destinations.Count; i++)
            {
                string destinationPath = $"{path}/destinations/{i}";
                DestinationConfig destination = destinations[i];
                if (!NotNull(destination, destinationPath))
                {
                    continue;
                }

                CheckId(destination.Id, destinationPath);
                if (CheckHttpUrl(destination.Address, $"{destinationPath}/address", DestinationAddressInvalid, DestinationAddressCredentials, "a destination address") is { } address
                    && (address.Query.Length > 0 || address.Fragment.Length > 0))
                {
                    Add($"{destinationPath}/address", DestinationAddressQueryOrFragment, "a destination address can't have a query or fragment (a base path is fine)");
                }
            }

            if (upstream.LoadBalancing is { } loadBalancing)
            {
                CheckEnum(loadBalancing, $"{path}/loadBalancing");
            }

            if (upstream.MaxConnectionsPerServer is < 1)
            {
                Add($"{path}/maxConnectionsPerServer", UpstreamMaxConnectionsInvalid, "maxConnectionsPerServer must be positive (leave it unset for no limit)");
            }

            if (upstream.PassiveHealth?.ReactivationPeriod is { } reactivation && reactivation <= TimeSpan.Zero)
            {
                Add($"{path}/passiveHealth/reactivationPeriod", UpstreamReactivationPeriodInvalid, "the reactivation period must be positive");
            }

            if (upstream.ActiveHealth is { } active)
            {
                string activePath = $"{path}/activeHealth";
                string probe = active.Path ?? string.Empty;
                if (probe is not ['/', ..] || probe.Contains('?', StringComparison.Ordinal) || probe.Contains('#', StringComparison.Ordinal))
                {
                    Add($"{activePath}/path", UpstreamActiveHealthPathInvalid, $"the probe path must start with '/' and have no query or fragment, got '{probe}'");
                }

                if (active.Interval is { } interval && interval <= TimeSpan.Zero)
                {
                    Add($"{activePath}/interval", UpstreamActiveHealthIntervalInvalid, "the probe interval must be positive");
                }

                if (active.Timeout is { } timeout && timeout <= TimeSpan.Zero)
                {
                    Add($"{activePath}/timeout", UpstreamActiveHealthTimeoutInvalid, "the probe timeout must be positive");
                }
            }

            CheckNotes(upstream.Notes, path);
        }

        private void CheckHost(
            HostConfig host,
            string path,
            HostSettings? defaults,
            Dictionary<ProfileId, HostSettings?> profiles,
            HashSet<UpstreamId> upstreams)
        {
            CheckId(host.Id, path);
            CheckVersion(host.Version, path);
            CheckHostNames(host, path);
            bool kindDefined = CheckEnum(host.Kind, $"{path}/kind");

            HostSettings? profile = null;
            if (host.ProfileId is { } profileId && !profiles.TryGetValue(profileId, out profile))
            {
                Add($"{path}/profileId", HostProfileUnknown, $"profile '{profileId}' doesn't exist");
            }

            CertificateBinding certificate = host.Certificate ?? new();
            if (CheckEnum(certificate.Mode, $"{path}/certificate/mode"))
            {
                // Judged after layering (PLAN §6.4): the host, else its profile, else the global defaults.
                if (certificate.Mode == CertificateBindingMode.None
                    && (host.Settings?.ForceHttps ?? profile?.ForceHttps ?? defaults?.ForceHttps ?? false))
                {
                    string from = host.Settings?.ForceHttps is not null ? string.Empty
                        : profile?.ForceHttps is not null ? " (inherited from its profile)"
                        : " (inherited from the global defaults)";
                    Add($"{path}/certificate/mode", HostCertificateNoneWithForceHttps, $"a host without HTTPS (certificate mode none) can't force HTTPS{from}; set its forceHttps to false");
                }

                if (certificate.Mode == CertificateBindingMode.Specific && certificate.CertificateId is null or { IsEmpty: true })
                {
                    Add($"{path}/certificate/certificateId", HostCertificateIdRequired, "a specific certificate binding needs a certificate ID");
                }
                else if (certificate.Mode != CertificateBindingMode.Specific && certificate.CertificateId is not null)
                {
                    Add($"{path}/certificate/certificateId", HostCertificateIdUnexpected, $"a certificate ID only goes with the specific mode, not {Name(certificate.Mode)}");
                }
            }

            CheckHostSettings(host.Settings, $"{path}/settings");
            if (kindDefined)
            {
                CheckKindParts(host, path);
            }

            IReadOnlyList<RouteConfig> routes = host.Routes ?? [];
            Dictionary<(RouteMatchKind, string), string> matches = new(RouteMatchComparer.Instance);
            for (int i = 0; i < routes.Count; i++)
            {
                if (NotNull(routes[i], $"{path}/routes/{i}"))
                {
                    CheckRoute(routes[i], $"{path}/routes/{i}", upstreams, matches);
                }
            }

            IReadOnlyList<string> tags = host.Tags ?? [];
            for (int i = 0; i < tags.Count; i++)
            {
                if (!ConfigNormalization.IsValidTag(ConfigNormalization.NormalizeTag(tags[i])))
                {
                    Add($"{path}/tags/{i}", HostTagInvalid, $"tag '{tags[i]}' must be 1–{ConfigNormalization.MaxTagLength} letters, digits or '- _ . : /'");
                }
            }

            CheckNotes(host.Notes, path);
        }

        private void CheckHostNames(HostConfig host, string path)
        {
            IReadOnlyList<string> names = host.Names ?? [];
            if (names.Count == 0)
            {
                Add($"{path}/names", HostNamesRequired, "a host needs at least one name");
            }

            Dictionary<string, string> own = new(StringComparer.Ordinal);
            for (int i = 0; i < names.Count; i++)
            {
                string namePath = $"{path}/names/{i}";
                if (!ConfigNormalization.TryNormalizeHostName(names[i], out string? normalized, out string? error))
                {
                    Add(namePath, HostNameInvalid, error);
                }
                else if (!own.TryAdd(normalized, namePath))
                {
                    Add(namePath, HostNameDuplicate, $"'{normalized}' is listed twice in this host (also at {own[normalized]})");
                }
                else if (host.Enabled && !_enabledHostNames.TryAdd(normalized, namePath))
                {
                    Add(namePath, HostNameDuplicate, $"'{normalized}' is already served by the enabled host at {_enabledHostNames[normalized]}");
                }
            }
        }

        private void CheckKindParts(HostConfig host, string path)
        {
            bool hasRoutes = host.Routes is { Count: > 0 };
            switch (host.Kind)
            {
                case HostKind.Proxy:
                    if (!hasRoutes)
                    {
                        Add($"{path}/routes", HostRoutesRequired, "a proxy host needs at least one route");
                    }

                    CheckUnexpected(host.Redirect, $"{path}/redirect", HostRedirectUnexpected, "a proxy host can't have a redirect");
                    CheckUnexpected(host.Respond, $"{path}/respond", HostRespondUnexpected, "a proxy host can't have a fixed response");
                    break;

                case HostKind.Redirect:
                    CheckNoRoutes(hasRoutes, path);
                    CheckUnexpected(host.Respond, $"{path}/respond", HostRespondUnexpected, "a redirect host can't have a fixed response");
                    if (host.Redirect is not { } redirect)
                    {
                        Add($"{path}/redirect", HostRedirectRequired, "a redirect host needs a redirect target");
                    }
                    else
                    {
                        CheckRedirect(redirect, $"{path}/redirect");
                    }

                    break;

                case HostKind.Respond:
                    CheckNoRoutes(hasRoutes, path);
                    CheckUnexpected(host.Redirect, $"{path}/redirect", HostRedirectUnexpected, "a respond host can't have a redirect");
                    if (host.Respond is not { } respond)
                    {
                        Add($"{path}/respond", HostRespondRequired, "a respond host needs a status code");
                    }
                    else
                    {
                        CheckRespond(respond, $"{path}/respond");
                    }

                    break;
            }
        }

        private void CheckNoRoutes(bool hasRoutes, string path)
        {
            if (hasRoutes)
            {
                Add($"{path}/routes", HostRoutesUnexpected, "only proxy hosts have routes");
            }
        }

        private void CheckUnexpected(object? part, string path, string code, string message)
        {
            if (part is not null)
            {
                Add(path, code, message);
            }
        }

        private void CheckRedirect(RedirectConfig redirect, string path)
        {
            // The target becomes the Location header: printable ASCII only (punycode host, percent-encoded path), so
            // nothing can break the header (CR/LF) or put raw non-ASCII on the wire.
            string target = (redirect.Target ?? string.Empty).Trim();
            if (target.AsSpan().ContainsAnyExceptInRange('\x21', '\x7E'))
            {
                Add($"{path}/target", RedirectTargetInvalid, "the redirect target must be printable ASCII without spaces (use punycode and percent-encoding)");
            }
            else if (CheckHttpUrl(target, $"{path}/target", RedirectTargetInvalid, RedirectTargetCredentials, "the redirect target") is { } uri
                && redirect.PreservePath && (uri.Query.Length > 0 || uri.Fragment.Length > 0))
            {
                Add($"{path}/target", RedirectTargetQueryWithPreservePath, "a redirect that preserves the path can't have a query or fragment in its target");
            }

            if (redirect.StatusCode is not (301 or 302 or 303 or 307 or 308))
            {
                Add($"{path}/statusCode", RedirectStatusCodeInvalid, $"redirect status {redirect.StatusCode} isn't one of 301, 302, 303, 307, 308");
            }
        }

        private void CheckRespond(RespondConfig respond, string path)
        {
            if (respond.StatusCode is < 200 or > 599)
            {
                Add($"{path}/statusCode", RespondStatusCodeInvalid, $"respond status {respond.StatusCode} must be 200–599");
            }
            else if (respond.StatusCode is 204 or 205 or 304 && !string.IsNullOrEmpty(respond.Body))
            {
                Add($"{path}/body", RespondBodyForbidden, $"a {respond.StatusCode} response can't have a body");
            }

            if (respond.Body is { Length: > MaxBodyLength })
            {
                Add($"{path}/body", RespondBodyTooLong, $"the body can't be longer than {MaxBodyLength} characters");
            }

            // A header value: visible ASCII, spaces and tabs.
            string contentType = respond.ContentType ?? string.Empty;
            if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > MaxContentTypeLength
                || contentType.Any(c => c is not ((>= '\x21' and <= '\x7E') or ' ' or '\t')))
            {
                Add($"{path}/contentType", RespondContentTypeInvalid, $"the content type must be 1–{MaxContentTypeLength} visible ASCII characters, spaces or tabs");
            }
        }

        private void CheckRoute(RouteConfig route, string path, HashSet<UpstreamId> upstreams, Dictionary<(RouteMatchKind, string), string> matches)
        {
            CheckId(route.Id, path);
            CheckEnum(route.Match, $"{path}/match");

            string routePath = route.Path ?? string.Empty;
            if (routePath is not ['/', ..] || routePath.Contains('?', StringComparison.Ordinal) || routePath.Contains('#', StringComparison.Ordinal))
            {
                Add($"{path}/path", RoutePathInvalid, $"the path must start with '/' and have no query or fragment, got '{routePath}'");
            }
            else if (routePath.Contains('%', StringComparison.Ordinal))
            {
                Add($"{path}/path", RoutePathEncoded, $"write the path decoded, without percent-encoding, got '{routePath}'");
            }
            else if (!RoutePath.IsCanonical(routePath))
            {
                // Requests are matched on the canonical view, so a non-canonical route path could never match.
                Add($"{path}/path", RoutePathNotCanonical, $"the path isn't canonical ('\\', '//', ';', dot segments, trailing dots or spaces); use '{RoutePath.Canonicalize(routePath)}'");
            }
            else if (!matches.TryAdd((route.Match, routePath), $"{path}/path"))
            {
                Add($"{path}/path", RoutePathDuplicate, $"the route at {matches[(route.Match, routePath)]} already matches '{routePath}' the same way (paths ignore case), so this one could never match");
            }

            if (!upstreams.Contains(route.UpstreamId))
            {
                Add($"{path}/upstreamId", RouteUpstreamUnknown, $"upstream '{route.UpstreamId}' doesn't exist");
            }

            CheckIpAllowList(route.Settings?.IpAllowList, $"{path}/settings/ipAllowList");
        }

        private void CheckHostSettings(HostSettings? settings, string path)
        {
            if (settings is null)
            {
                return;
            }

            if (settings.Tls is { } tls)
            {
                if (tls.MinimumVersion is { } minimum)
                {
                    CheckEnum(minimum, $"{path}/tls/minimumVersion");
                }

                if (tls.TcpAlpn is { } alpn)
                {
                    CheckEnum(alpn, $"{path}/tls/tcpAlpn");
                }
            }

            CheckIpAllowList(settings.IpAllowList, $"{path}/ipAllowList");
        }

        private void CheckIpAllowList(IpAllowListSettings? allowList, string path)
        {
            if (allowList is null)
            {
                return;
            }

            IReadOnlyList<string> addresses = allowList.Addresses ?? [];
            CheckAddresses(addresses, $"{path}/addresses");
            if (allowList.DropDenied && addresses.Count == 0)
            {
                Add($"{path}/dropDenied", IpAllowListDropDeniedWithoutAddresses, "an empty allow list restricts nothing, so there is nothing to drop");
            }
        }

        private void CheckAddresses(IReadOnlyList<string> addresses, string path)
        {
            for (int i = 0; i < addresses.Count; i++)
            {
                if (!IpNetwork.TryParse((addresses[i] ?? string.Empty).Trim(), out _, out string? error))
                {
                    Add($"{path}/{i}", AddressInvalid, $"'{addresses[i]}' isn't an IP address or CIDR ({error})");
                }
            }
        }

        // Absolute http(s) URL without credentials (the document holds no secrets); null when invalid.
        private Uri? CheckHttpUrl(string? value, string path, string invalidCode, string credentialsCode, string what)
        {
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || uri.Host.Length == 0)
            {
                Add(path, invalidCode, $"{what} must be an absolute http or https URL, got '{value}'");
                return null;
            }

            if (uri.UserInfo.Length > 0)
            {
                Add(path, credentialsCode, $"{what} can't contain credentials: the config document holds no secrets");
                return null;
            }

            return uri;
        }

        private void CheckLabel(string? name, string path, string what, Dictionary<string, string> names)
        {
            string normalized = ConfigNormalization.NormalizeName(name);
            if (normalized.Length == 0)
            {
                Add(path, NameRequired, $"a {what} needs a name");
            }
            else if (normalized.Length > ConfigNormalization.MaxNameLength)
            {
                Add(path, NameTooLong, $"a {what} name can't be longer than {ConfigNormalization.MaxNameLength} characters");
            }
            else if (normalized.Any(char.IsControl))
            {
                Add(path, NameInvalid, $"a {what} name can't contain control characters");
            }
            else if (!names.TryAdd(normalized, path))
            {
                Add(path, NameDuplicate, $"the {what} name '{normalized}' is already used at {names[normalized]} (names ignore case)");
            }
        }

        private void CheckNotes(string? notes, string path)
        {
            if (notes is { Length: > MaxNotesLength })
            {
                Add($"{path}/notes", NotesTooLong, $"notes can't be longer than {MaxNotesLength} characters");
            }
        }

        private void CheckId<TId>(TId id, string path)
            where TId : struct, ITypedId<TId>
        {
            string text = id.ToString()!;
            if (id.IsEmpty)
            {
                Add($"{path}/id", IdEmpty, "the ID can't be empty");
            }
            else if (!_ids.TryAdd(text, path))
            {
                Add($"{path}/id", IdDuplicate, $"ID '{text}' is already used at {_ids[text]} (IDs are compared ignoring case)");
            }
        }

        private void CheckVersion(long version, string path)
        {
            if (version < 0)
            {
                Add($"{path}/version", VersionNegative, "the version can't be negative");
            }
        }

        private bool CheckEnum<TEnum>(TEnum value, string path)
            where TEnum : struct, Enum
        {
            if (Enum.IsDefined(value))
            {
                return true;
            }

            Add(path, EnumUndefined, $"{value} isn't a {typeof(TEnum).Name}");
            return false;
        }

        // Only a hand-built document has null elements; reading rejects them.
        private bool NotNull(object? value, string path)
        {
            if (value is not null)
            {
                return true;
            }

            Add(path, ValueNull, "a list element can't be null");
            return false;
        }

        private void Add(string path, string code, string message) => Errors.Add(new ConfigError(path, code, message));

        private static string Name<TEnum>(TEnum value)
            where TEnum : struct, Enum =>
            System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    }

    // Same match kind and the same path ignoring case: the gate's matching ignores case.
    private sealed class RouteMatchComparer : IEqualityComparer<(RouteMatchKind Match, string Path)>
    {
        public static readonly RouteMatchComparer Instance = new();

        public bool Equals((RouteMatchKind Match, string Path) x, (RouteMatchKind Match, string Path) y) =>
            x.Match == y.Match && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((RouteMatchKind Match, string Path) obj) =>
            HashCode.Combine(obj.Match, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path));
    }
}
