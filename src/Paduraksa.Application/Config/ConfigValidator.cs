using System.Buffers;
using System.Text;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Geo;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Core.Text;
using Paduraksa.Proxy.RateLimiting;
using static Paduraksa.Application.Config.ConfigErrorCodes;
using HostAccessListSpec = Paduraksa.Proxy.Config.HostAccessListSpec;
using HostHstsSpec = Paduraksa.Proxy.Config.HostHstsSpec;
using HostRateLimitSpec = Paduraksa.Proxy.Config.HostRateLimitSpec;
using HostRobotsSpec = Paduraksa.Proxy.Config.HostRobotsSpec;
using HostServerHeaderSpec = Paduraksa.Proxy.Config.HostServerHeaderSpec;
using HstsHeader = Paduraksa.Proxy.Tls.HstsHeader;
using IpNetwork = Paduraksa.Proxy.Net.IpNetwork;
using RoutePath = Paduraksa.Proxy.Snapshot.RoutePath;
using ServerHeader = Paduraksa.Proxy.Http.ServerHeader;
using UpstreamCertificatePins = Paduraksa.Proxy.Yarp.UpstreamCertificatePins;

namespace Paduraksa.Application.Config;

/// <summary>Validates a config document: field rules and cross-object rules, every error with the JSON pointer of the
/// field it's about. The snapshot compiler still runs afterwards as the backstop, so this doesn't re-implement YARP's
/// validator; it catches what the document can say on its own, where the compiler can only name keys.</summary>
/// <remarks>
/// <para>Validate the document <i>as the client sent it</i>, before <see cref="CanonicalConfigJson.Canonicalize"/>:
/// the paths then index what the client wrote, like those of <see cref="CanonicalConfigJson.TryDeserialize(ReadOnlySpan{byte}, out ConfigDocument?, out ConfigError?)"/>. Values
/// are judged in their normal form (host names, trimmed names and addresses), so a document that validates still
/// validates once canonical.</para>
/// <para>Cross-object rules: IDs are unique across the whole document, also ignoring case (YARP compares its IDs
/// that way), routes across all hosts and destinations across all upstreams; profile and upstream names are unique
/// ignoring case, and host names across <i>enabled</i> hosts. As in the compiler, a disabled host is fully validated
/// but its names only have to be unique within itself, so a replacement can be staged next to the live host. A host
/// can't have two routes with the same match kind and path (ignoring case, like matching): the second could never
/// match. References to profiles and upstreams must resolve, and an upstream that an enabled host routes to needs an
/// enabled destination; certificate references can't be checked here, as the validator does not read the inventory. A host without HTTPS
/// (certificate mode none) can't force HTTPS, judged on the setting it ends up with after the profile and global
/// layers. HSTS's preload pair is judged the same way, on each host's resolved settings, wherever its three fields
/// were set.</para>
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
        private readonly HashSet<PasswordCredentialId> _credentialIds = [];
        private string? _adminHostPath;

        public List<ConfigError> Errors { get; } = [];

        public void CheckDocument(ConfigDocument document)
        {
            if (document.ApiVersion != ConfigDocument.V1)
            {
                Add("/apiVersion", ApiVersionUnsupported, $"apiVersion '{document.ApiVersion}' isn't supported; this build reads '{ConfigDocument.V1}'");
            }

            CheckSettings(document.Settings ?? new(), "/settings");

            var profiles = document.Profiles ?? [];
            Dictionary<string, string> profileNames = new(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < profiles.Count; i++)
            {
                if (NotNull(profiles[i], $"/profiles/{i}"))
                {
                    CheckProfile(profiles[i], $"/profiles/{i}", profileNames);
                }
            }

            var hosts = document.Hosts ?? [];
            HashSet<UpstreamId> routedTo =
            [
                .. hosts.Where(h => h is { Enabled: true, Kind: HostKind.Proxy })
                    .SelectMany(h => h.Routes ?? [])
                    .Where(r => r is not null)
                    .Select(r => r.UpstreamId),
            ];
            var upstreams = document.Upstreams ?? [];
            Dictionary<string, string> upstreamNames = new(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < upstreams.Count; i++)
            {
                if (NotNull(upstreams[i], $"/upstreams/{i}"))
                {
                    CheckUpstream(upstreams[i], $"/upstreams/{i}", upstreamNames, routedTo);
                }
            }

            // A duplicate profile ID is reported above; the first one is the layer.
            Dictionary<ProfileId, HostSettings?> profileSettings = [];
            foreach (var profile in profiles.Where(p => p is not null))
            {
                profileSettings.TryAdd(profile.Id, profile.Settings);
            }

            var policies = document.RateLimitPolicies ?? [];
            Dictionary<string, string> policyNames = new(StringComparer.OrdinalIgnoreCase);
            HashSet<RateLimitPolicyId> policyIds = [];
            for (var i = 0; i < policies.Count; i++)
            {
                if (NotNull(policies[i], $"/rateLimitPolicies/{i}"))
                {
                    CheckRateLimitPolicy(policies[i], $"/rateLimitPolicies/{i}", policyNames, policyIds);
                }
            }

            var accessLists = document.AccessLists ?? [];
            Dictionary<string, string> accessListNames = new(StringComparer.OrdinalIgnoreCase);
            HashSet<AccessListId> accessListIds = [];
            for (var i = 0; i < accessLists.Count; i++)
            {
                if (NotNull(accessLists[i], $"/accessLists/{i}"))
                {
                    CheckAccessList(accessLists[i], $"/accessLists/{i}", accessListNames, accessListIds);
                }
            }

            HashSet<UpstreamId> upstreamIds = [.. upstreams.Where(u => u is not null).Select(u => u.Id)];
            var defaults = document.Settings?.Defaults;
            for (var i = 0; i < hosts.Count; i++)
            {
                if (NotNull(hosts[i], $"/hosts/{i}"))
                {
                    CheckHost(hosts[i], $"/hosts/{i}", defaults, profileSettings, upstreamIds, policyIds, accessListIds);
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
            else if (settings.DefaultCertificateId == AdminCertificate.Id)
            {
                Add($"{path}/defaultCertificateId", SettingsDefaultCertificateUnusable, "that certificate is reserved for the admin listener; choose another one");
            }

            CheckTrustedProxies(settings.TrustedProxies ?? new(), $"{path}/trustedProxies");
        }

        private void CheckTrustedProxies(TrustedProxySettings trusted, string path)
        {
            if (!CheckEnum(trusted.Mode, $"{path}/mode"))
            {
                return;
            }

            var proxies = trusted.Proxies ?? [];
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

            var generic = trusted.Mode == TrustedProxyMode.Generic;
            var cloudflare = trusted.Mode is TrustedProxyMode.CloudflareProxy or TrustedProxyMode.CloudflareTunnel;
            if (trusted.Header is { } header)
            {
                var name = header.Trim();
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

            var destinations = upstream.Destinations ?? [];
            if (destinations.Count == 0)
            {
                Add($"{path}/destinations", UpstreamDestinationsRequired, "an upstream needs at least one destination");
            }
            else if (routedTo.Contains(upstream.Id) && !destinations.Any(d => d is { Enabled: true }))
            {
                Add($"{path}/destinations", UpstreamDestinationsNoneEnabled, "an enabled host routes to this upstream, so it needs an enabled destination");
            }

            for (var i = 0; i < destinations.Count; i++)
            {
                var destinationPath = $"{path}/destinations/{i}";
                var destination = destinations[i];
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

            if (upstream.Tls is { } tls && CheckEnum(tls.Trust, $"{path}/tls/trust"))
            {
                var pins = tls.PinnedSha256 ?? [];
                if (tls.Trust == UpstreamTlsTrust.Pinned && pins.Count == 0)
                {
                    Add($"{path}/tls/pinnedSha256", UpstreamTlsPinsMissing, "pinned trust needs at least one SHA-256 fingerprint of the upstream's certificate");
                }
                else if (tls.Trust != UpstreamTlsTrust.Pinned && pins.Count > 0)
                {
                    Add($"{path}/tls/pinnedSha256", UpstreamTlsPinsUnused, "fingerprints are only used with trust: pinned");
                }

                for (var i = 0; i < pins.Count; i++)
                {
                    if (!UpstreamCertificatePins.TryNormalize(pins[i], out _))
                    {
                        Add($"{path}/tls/pinnedSha256/{i}", UpstreamTlsPinInvalid, "a fingerprint is the certificate's SHA-256: 64 hex digits, colons allowed");
                    }
                }
            }

            if (upstream.PassiveHealth?.ReactivationPeriod is { } reactivation && reactivation <= TimeSpan.Zero)
            {
                Add($"{path}/passiveHealth/reactivationPeriod", UpstreamReactivationPeriodInvalid, "the reactivation period must be positive");
            }

            if (upstream.ActiveHealth is { } active)
            {
                var activePath = $"{path}/activeHealth";
                var probe = active.Path ?? string.Empty;
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
            HashSet<UpstreamId> upstreams,
            HashSet<RateLimitPolicyId> policies,
            HashSet<AccessListId> accessLists)
        {
            CheckId(host.Id, path);
            CheckVersion(host.Version, path);
            CheckHostNames(host, path);
            var kindDefined = CheckEnum(host.Kind, $"{path}/kind");

            HostSettings? profile = null;
            if (host.ProfileId is { IsEmpty: true })
            {
                Add($"{path}/profileId", IdEmpty, "the profile ID can't be empty (leave it unset for no profile)");
            }
            else if (host.ProfileId is { } profileId && !profiles.TryGetValue(profileId, out profile))
            {
                Add($"{path}/profileId", HostProfileUnknown, $"profile '{profileId}' doesn't exist");
            }

            var certificate = host.Certificate ?? new();
            // One resolution per host: every rule that judges a value after layering reads it.
            var resolved = HostSettingsLayers.Resolve(defaults, profile, host.Settings);
            if (CheckEnum(certificate.Mode, $"{path}/certificate/mode"))
            {
                // Judged after layering: the host, else its profile, else the global defaults.
                if (certificate.Mode == CertificateBindingMode.None && host.Kind != HostKind.Admin && resolved.ForceHttps)
                {
                    var from = HostSettingsLayers.InheritedSuffix(
                        HostSettingsLayers.LayerOf(defaults, profile, host.Settings, static s => s.ForceHttps));
                    Add($"{path}/certificate/mode", HostCertificateNoneWithForceHttps, $"a host without HTTPS (certificate mode none) can't force HTTPS{from}; set its forceHttps to false");
                }

                if (certificate.Mode == CertificateBindingMode.Specific && certificate.CertificateId is null or { IsEmpty: true })
                {
                    Add($"{path}/certificate/certificateId", HostCertificateIdRequired, "a specific certificate binding needs a certificate ID");
                }
                else if (certificate.Mode == CertificateBindingMode.Specific && certificate.CertificateId == AdminCertificate.Id)
                {
                    Add($"{path}/certificate/certificateId", HostCertificateUnusable, "that certificate is reserved for the admin listener; choose another one");
                }
                else if (certificate.Mode != CertificateBindingMode.Specific && certificate.CertificateId is not null)
                {
                    Add($"{path}/certificate/certificateId", HostCertificateIdUnexpected, $"a certificate ID only goes with the specific mode, not {Name(certificate.Mode)}");
                }
            }

            CheckHostSettings(host.Settings, $"{path}/settings");
            CheckHstsPreload(resolved.Hsts, defaults, profile, host.Settings, $"{path}/settings/hsts");
            CheckServerHeader(resolved.ServerHeader, defaults, profile, host.Settings, $"{path}/settings/serverHeader");
            CheckRobots(resolved.Robots, defaults, profile, host.Settings, $"{path}/settings/robots");
            CheckRateLimit(resolved.RateLimit, policies, $"{path}/settings/rateLimit", HostRateLimitUnknown, defaults, profile, host.Settings);
            CheckAccessListReference(resolved.AccessList, accessLists, $"{path}/settings/accessList", HostAccessListUnknown, defaults, profile, host.Settings);
            if (kindDefined)
            {
                CheckKindParts(host, path);
                CheckAdminHost(host, certificate, path);
            }

            var routes = host.Routes ?? [];
            Dictionary<(RouteMatchKind, string), string> matches = new(RouteMatchComparer.Instance);
            for (var i = 0; i < routes.Count; i++)
            {
                if (NotNull(routes[i], $"{path}/routes/{i}"))
                {
                    CheckRoute(routes[i], $"{path}/routes/{i}", upstreams, matches, policies, accessLists);
                }
            }

            var tags = host.Tags ?? [];
            for (var i = 0; i < tags.Count; i++)
            {
                if (!ConfigNormalization.IsValidTag(ConfigNormalization.NormalizeTag(tags[i])))
                {
                    Add($"{path}/tags/{i}", HostTagInvalid, $"tag '{tags[i]}' must be 1-{ConfigNormalization.MaxTagLength} letters, digits or '- _ . : /'");
                }
            }

            CheckNotes(host.Notes, path);
        }

        private void CheckHostNames(HostConfig host, string path)
        {
            var names = host.Names ?? [];
            if (names.Count == 0)
            {
                Add($"{path}/names", HostNamesRequired, "a host needs at least one hostname");
            }

            Dictionary<string, string> own = new(StringComparer.Ordinal);
            for (var i = 0; i < names.Count; i++)
            {
                var namePath = $"{path}/names/{i}";
                if (!ConfigNormalization.TryNormalizeHostName(names[i], out var normalized, out var error))
                {
                    Add(namePath, HostNameInvalid, error);
                }
                else if (!own.TryAdd(normalized, namePath))
                {
                    Add(namePath, HostNameDuplicate, $"'{normalized}' is listed twice in this host (also at {own[normalized]})");
                }
                else if (host.Enabled && !_enabledHostNames.TryAdd(normalized, namePath))
                {
                    Add(namePath, HostNameDuplicate, $"'{normalized}' is already a hostname of the enabled host at {_enabledHostNames[normalized]}");
                }
            }
        }

        private void CheckKindParts(HostConfig host, string path)
        {
            var hasRoutes = host.Routes is { Count: > 0 };
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

                case HostKind.Admin:
                    CheckNoRoutes(hasRoutes, path);
                    CheckUnexpected(host.Redirect, $"{path}/redirect", HostRedirectUnexpected, "an admin host can't have a redirect");
                    CheckUnexpected(host.Respond, $"{path}/respond", HostRespondUnexpected, "an admin host can't have a fixed response");
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

        /// <summary>Admin hosts: one at most, and HTTPS is required (the mapping forces the redirect).</summary>
        private void CheckAdminHost(HostConfig host, CertificateBinding certificate, string path)
        {
            if (host.Kind != HostKind.Admin)
            {
                return;
            }

            if (_adminHostPath is { } existing)
            {
                Add($"{path}/kind", HostAdminDuplicate, $"only one admin host can exist; there is already one at {existing}");
            }
            else
            {
                _adminHostPath = path;
            }

            if (certificate.Mode == CertificateBindingMode.None)
            {
                Add($"{path}/certificate/mode", HostAdminHttpsRequired, "an admin host is served over HTTPS: it can't use certificate mode none");
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
            var target = (redirect.Target ?? string.Empty).Trim();
            if (target.AsSpan().ContainsAnyExcept(AsciiChars.Visible))
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
                Add($"{path}/statusCode", RespondStatusCodeInvalid, $"respond status {respond.StatusCode} must be 200-599");
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
            var contentType = respond.ContentType ?? string.Empty;
            if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > MaxContentTypeLength
                || contentType.Any(c => c is not ((>= '\x21' and <= '\x7E') or ' ' or '\t')))
            {
                Add($"{path}/contentType", RespondContentTypeInvalid, $"the content type must be 1-{MaxContentTypeLength} visible ASCII characters, spaces or tabs");
            }
        }

        private void CheckRoute(RouteConfig route, string path, HashSet<UpstreamId> upstreams, Dictionary<(RouteMatchKind, string), string> matches, HashSet<RateLimitPolicyId> policies, HashSet<AccessListId> accessLists)
        {
            CheckId(route.Id, path);
            CheckEnum(route.Match, $"{path}/match");

            var routePath = route.Path ?? string.Empty;
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

            if (route.UpstreamId.IsEmpty)
            {
                Add($"{path}/upstreamId", RouteUpstreamRequired, "the route needs an upstream");
            }
            else if (!upstreams.Contains(route.UpstreamId))
            {
                Add($"{path}/upstreamId", RouteUpstreamUnknown, $"upstream '{route.UpstreamId}' doesn't exist");
            }

            // Only the route's own named list is checked here: one inherited from the host is reported at the host.
            if (route.Settings?.AccessList?.AccessListId is { IsEmpty: false } routeList)
            {
                CheckAccessListReference(new HostAccessListSpec { Enabled = true, AccessListId = routeList }, accessLists, $"{path}/settings/accessList", RouteAccessListUnknown, defaults: null, profile: null, host: null);
            }

            if (route.Settings?.ProofOfWork is { } proofOfWork)
            {
                CheckProofOfWork(proofOfWork, $"{path}/settings/proofOfWork", route: true);
            }

            // Only the route's own named policy is checked here: one inherited from the host is reported at the host.
            if (route.Settings?.RateLimit?.PolicyId is { IsEmpty: false } routePolicyId)
            {
                CheckRateLimit(
                    new HostRateLimitSpec { Enabled = true, PolicyId = routePolicyId },
                    policies,
                    $"{path}/settings/rateLimit",
                    RouteRateLimitUnknown,
                    defaults: null,
                    profile: null,
                    host: null);
            }
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

            if (settings.RequestIdPropagation is { } propagation)
            {
                CheckEnum(propagation, $"{path}/requestIdPropagation");
            }

            if (settings.Hsts is { MaxAge: { } maxAge } && maxAge is < 0 or > HstsHeader.MaxMaxAgeSeconds)
            {
                Add($"{path}/hsts/maxAge", HstsMaxAgeInvalid, $"max-age must be 0-{HstsHeader.MaxMaxAgeSeconds} seconds (two years); 0 tells a browser to forget the host");
            }

            if (settings.ServerHeader is { } serverHeader)
            {
                if (serverHeader.Mode is { } mode)
                {
                    CheckEnum(mode, $"{path}/serverHeader/mode");
                }

                // A value is only used by the custom mode. Validate it where it is set, so an error points at the layer.
                if (serverHeader is { Mode: ServerHeaderMode.Custom, Value: { Length: > 0 } value } && !ServerHeader.IsValidValue(value))
                {
                    Add($"{path}/serverHeader/value", ServerHeaderValueInvalid, InvalidServerHeaderValue);
                }
            }

            if (settings.ProofOfWork is { } proofOfWork)
            {
                CheckProofOfWork(proofOfWork, $"{path}/proofOfWork", route: false);
            }

            if (settings.Robots is { } robots)
            {
                CheckRobots(robots, $"{path}/robots");
            }
        }

        /// <summary>
        /// The robots.txt values at the layer that sets them: a custom text belongs to the custom mode, and a text is
        /// bounded in bytes and free of control characters. A text set under a mode it inherits is fine, so only a layer
        /// that sets some other mode refuses one; whether a resolved custom mode actually has text is the host's rule
        /// below, since the text may come from a lower layer.
        /// </summary>
        private void CheckRobots(RobotsSettings settings, string path)
        {
            if (settings.Mode is { } mode)
            {
                CheckEnum(mode, $"{path}/mode");
            }

            if (settings.Mode is { } other && other != RobotsMode.Custom && settings.CustomText is not null)
            {
                Add($"{path}/customText", RobotsCustomTextUnexpected, $"a custom robots.txt text only belongs to the custom mode, not {Name(other)}");
            }

            if (settings.CustomText is not { } text)
            {
                return;
            }

            if (!RobotsText.IsWithinSizeLimit(text))
            {
                Add($"{path}/customText", RobotsCustomTextTooLong, $"the robots.txt text can't be longer than {RobotsText.MaxCustomBytes} bytes of UTF-8");
            }

            if (!RobotsText.HasNoControlCharacters(text))
            {
                Add($"{path}/customText", RobotsCustomTextInvalid, "the robots.txt text can't contain control characters other than tab and line breaks");
            }
        }

        /// <summary>
        /// The custom mode's cross-field rule, judged on the host's resolved settings like the <c>Server</c> header's
        /// value: a resolved custom mode needs a text, wherever the mode and the text were set, and the message names the
        /// layer that set the mode when the host didn't.
        /// </summary>
        private void CheckRobots(HostRobotsSpec resolved, HostSettings? defaults, HostSettings? profile, HostSettings? host, string path)
        {
            if (resolved.Mode != RobotsMode.Custom || !string.IsNullOrWhiteSpace(resolved.CustomText))
            {
                return;
            }

            // Name the layer that left it blank when one does, else the layer that chose the mode the text is missing for.
            var layer = HostSettingsLayers.LayerOf(defaults, profile, host, static s => s.Robots?.CustomText);
            if (layer == SettingsLayer.BuiltIn)
            {
                layer = HostSettingsLayers.LayerOf(defaults, profile, host, static s => s.Robots?.Mode);
            }

            Add($"{path}/customText", RobotsCustomTextRequired, $"the custom robots.txt needs text{HostSettingsLayers.InheritedSuffix(layer)}");
        }

        /// <summary>
        /// The proof-of-work values, judged where a layer sets them. A route's layer carries only the switch: the
        /// difficulty and lifetime belong to the host's resolved settings, so an override there could only be ignored.
        /// </summary>
        private void CheckProofOfWork(ProofOfWorkSettings settings, string path, bool route)
        {
            if (settings.Difficulty is { } difficulty)
            {
                if (route)
                {
                    Add($"{path}/difficulty", ProofOfWorkRouteDifficultyUnexpected, "a route can only override the proof-of-work switch (enabled); set the difficulty on the host");
                }
                else if (difficulty is < ProofOfWorkLimits.MinDifficulty or > ProofOfWorkLimits.MaxDifficulty)
                {
                    Add(
                        $"{path}/difficulty",
                        ProofOfWorkDifficultyInvalid,
                        $"the difficulty is leading zero bits of SHA-256 and must be {ProofOfWorkLimits.MinDifficulty}-{ProofOfWorkLimits.MaxDifficulty}, got {difficulty}");
                }
            }

            if (settings.TokenLifetimeHours is { } hours)
            {
                if (route)
                {
                    Add($"{path}/tokenLifetimeHours", ProofOfWorkRouteTokenLifetimeUnexpected, "a route can only override the proof-of-work switch (enabled); set the token lifetime on the host");
                }
                else if (hours is < ProofOfWorkLimits.MinTokenLifetimeHours or > ProofOfWorkLimits.MaxTokenLifetimeHours)
                {
                    Add(
                        $"{path}/tokenLifetimeHours",
                        ProofOfWorkTokenLifetimeInvalid,
                        $"the token lifetime must be {ProofOfWorkLimits.MinTokenLifetimeHours}-{ProofOfWorkLimits.MaxTokenLifetimeHours} hours, got {hours}");
                }
            }
        }

        /// <summary>
        /// A rate-limit policy's own rules. The rate is <see cref="RateLimitPolicyConfig.Requests"/> over the
        /// period's seconds, judged against the same bounds <see cref="RateLimitParameters"/> enforces, so a document
        /// that validates can't make the mapping throw.
        /// </summary>
        private void CheckRateLimitPolicy(
            RateLimitPolicyConfig policy, string path, Dictionary<string, string> names, HashSet<RateLimitPolicyId> ids)
        {
            CheckId(policy.Id, path);
            CheckVersion(policy.Version, path);
            CheckLabel(policy.Name, $"{path}/name", "rate-limit policy", names);
            ids.Add(policy.Id);

            if (policy.Requests < 1)
            {
                Add($"{path}/requests", RateLimitRequestsInvalid, "requests must be at least 1");
            }
            else if (RateLimitRate(policy) is { } rate
                && (rate < RateLimitParameters.MinTokensPerSecond || rate > RateLimitParameters.MaxTokensPerSecond))
            {
                Add(
                    $"{path}/requests",
                    RateLimitRateInvalid,
                    $"there are {policy.Requests} requests per {Name(policy.Per)}, which is {rate} a second; the rate must be between {RateLimitParameters.MinTokensPerSecond} and {RateLimitParameters.MaxTokensPerSecond} a second");
            }

            if (policy.Burst < 1 || policy.Burst > RateLimitParameters.MaxBurst)
            {
                Add($"{path}/burst", RateLimitBurstInvalid, $"burst must be between 1 and {RateLimitParameters.MaxBurst} tokens, got {policy.Burst}");
            }

            var keyDefined = CheckEnum(policy.Key, $"{path}/key");
            CheckEnum(policy.Per, $"{path}/per");
            var header = (policy.Header ?? string.Empty).Trim();
            if (keyDefined && policy.Key == RateLimitKey.Header && header.Length == 0)
            {
                Add($"{path}/header", RateLimitHeaderRequired, "a header-keyed policy needs the header whose value is its key");
            }
            else if (keyDefined && policy.Key != RateLimitKey.Header && header.Length > 0)
            {
                Add($"{path}/header", RateLimitHeaderUnexpected, $"a header only belongs to the header key, not {Name(policy.Key)}");
            }
            else if (header.Length > 0 && header.AsSpan().ContainsAnyExcept(HeaderNameChars))
            {
                Add($"{path}/header", RateLimitHeaderInvalid, $"'{policy.Header}' isn't a valid header name");
            }

            CheckNotes(policy.Notes, path);
        }

        /// <summary>The period in seconds; null when the enum value is undefined (reported by its own rule).</summary>
        private static double? Seconds(RateLimitPeriod period) => period switch
        {
            RateLimitPeriod.Second => 1,
            RateLimitPeriod.Minute => 60,
            RateLimitPeriod.Hour => 3600,
            _ => null,
        };

        private static double? RateLimitRate(RateLimitPolicyConfig policy) =>
            Seconds(policy.Per) is { } seconds ? policy.Requests / seconds : null;

        /// <summary>
        /// A host's or route's resolved rate limit: the policy must exist, and a limit that is on must name one. A
        /// named policy is checked even while the switch is off, because the reference is kept for turning it back on.
        /// </summary>
        /// <param name="resolved">The resolved switch and policy.</param>
        /// <param name="policies">The document's policy IDs.</param>
        /// <param name="path">The rate-limit settings path.</param>
        /// <param name="unknownCode">The code for a policy that doesn't exist (the host's or the route's).</param>
        /// <param name="defaults">The global defaults layer, when the resolved value is a host's.</param>
        /// <param name="profile">The profile layer, when the resolved value is a host's.</param>
        /// <param name="host">The host's own layer, when the resolved value is a host's.</param>
        private void CheckRateLimit(
            HostRateLimitSpec resolved,
            HashSet<RateLimitPolicyId> policies,
            string path,
            string unknownCode,
            HostSettings? defaults,
            HostSettings? profile,
            HostSettings? host)
        {
            // Nothing named is no limit: the built-in switch is on, so a layer only has to name a policy.
            if (resolved.PolicyId.IsEmpty || policies.Contains(resolved.PolicyId))
            {
                return;
            }

            var inherited = HostSettingsLayers.InheritedSuffix(
                HostSettingsLayers.LayerOf(defaults, profile, host, static s => s.RateLimit?.PolicyId));
            Add($"{path}/policyId", unknownCode, $"rate-limit policy '{resolved.PolicyId}' doesn't exist{inherited}");
        }

        /// <summary>
        /// HSTS's cross-field rule, judged on the host's resolved settings like the certificate rule above: an enabled HSTS
        /// whose <c>preload</c> is in force needs <c>includeSubDomains</c> and a year of <c>max-age</c>, wherever each of the
        /// three was set. Reported at the host, which is the one that would send the header; a profile or the global defaults
        /// are refused only through a host that resolves to the invalid pair, so a profile no host uses can't fail a document
        /// on its own. Off, the setting is inert: nothing is sent, so nothing to refuse.
        /// </summary>
        /// <param name="resolved">The host's resolved HSTS.</param>
        /// <param name="defaults">The global defaults layer.</param>
        /// <param name="profile">The host's profile layer, if it has one.</param>
        /// <param name="host">The host's own layer, on top.</param>
        /// <param name="path">The host's HSTS path (<c>...</c>/hsts).</param>
        private void CheckHstsPreload(HostHstsSpec resolved, HostSettings? defaults, HostSettings? profile, HostSettings? host, string path)
        {
            if (!resolved.Enabled || !resolved.Preload || HstsPreloadIsValid(resolved))
            {
                return;
            }

            // Where the preload came from, when it isn't this host's own: that is the layer to go and change.
            var inherited = HostSettingsLayers.InheritedSuffix(
                HostSettingsLayers.LayerOf(defaults, profile, host, static s => s.Hsts?.Preload));

            if (!resolved.IncludeSubDomains)
            {
                Add($"{path}/preload", HstsPreloadNeedsIncludeSubDomains, $"preload needs includeSubDomains{inherited}");
            }
            else
            {
                Add($"{path}/preload", HstsPreloadMaxAgeTooShort, $"preload needs a max-age of at least {HstsHeader.PreloadMinMaxAgeSeconds} seconds (a year), got {resolved.MaxAgeSeconds}{inherited}");
            }

            static bool HstsPreloadIsValid(HostHstsSpec spec) => spec.IncludeSubDomains && spec.MaxAgeSeconds >= HstsHeader.PreloadMinMaxAgeSeconds;
        }

        /// <summary>
        /// The <c>Server</c> header's cross-field rule, judged on each host's resolved settings like HSTS's preload pair: a
        /// resolved <see cref="ServerHeaderMode.Custom"/> needs a value, wherever the mode came from. A value's shape
        /// is checked at the layer that set it; this catches a mode that inherited none, or inherited a value set by a
        /// layer whose mode didn't use it, naming that layer because the host's admin can't change it there.
        /// </summary>
        /// <param name="resolved">The host's resolved <c>Server</c> header.</param>
        /// <param name="defaults">The global defaults layer.</param>
        /// <param name="profile">The host's profile layer, if it has one.</param>
        /// <param name="host">The host's own layer, on top.</param>
        /// <param name="path">The host's path (<c>...</c>/serverHeader).</param>
        private void CheckServerHeader(HostServerHeaderSpec resolved, HostSettings? defaults, HostSettings? profile, HostSettings? host, string path)
        {
            if (resolved.Mode != ServerHeaderMode.Custom)
            {
                return;
            }

            if (string.IsNullOrEmpty(resolved.Value))
            {
                var missingFrom = HostSettingsLayers.InheritedSuffix(
                    HostSettingsLayers.LayerOf(defaults, profile, host, static s => s.ServerHeader?.Mode));
                Add($"{path}/value", ServerHeaderValueRequired, $"the custom Server header needs a value{missingFrom}");
                return;
            }

            // A value set by a layer whose mode isn't custom is otherwise ignored, so its shape is judged where it is used.
            var valueLayer = HostSettingsLayers.LayerOf(defaults, profile, host, static s => s.ServerHeader?.Value);
            var checkedAtItsLayer = valueLayer switch
            {
                SettingsLayer.Host => host!.ServerHeader!.Mode == ServerHeaderMode.Custom,
                SettingsLayer.Profile => profile!.ServerHeader!.Mode == ServerHeaderMode.Custom,
                SettingsLayer.GlobalDefaults => defaults!.ServerHeader!.Mode == ServerHeaderMode.Custom,
                _ => false,
            };
            if (!checkedAtItsLayer && !ServerHeader.IsValidValue(resolved.Value))
            {
                Add($"{path}/value", ServerHeaderValueInvalid, InvalidServerHeaderValue + HostSettingsLayers.InheritedSuffix(valueLayer));
            }
        }

        private static string InvalidServerHeaderValue =>
            $"the Server header value must be 1-{ServerHeader.MaxValueLength} visible ASCII characters or spaces, with no leading or trailing space";

        /// <summary>An access list's own rules: its addresses, its realm and its users. The users' hashes are checked
        /// for shape only; a password that reaches here unhashed is a bug in the write path and is refused, so no document
        /// can hold one.</summary>
        private void CheckAccessList(AccessListConfig list, string path, Dictionary<string, string> names, HashSet<AccessListId> ids)
        {
            CheckId(list.Id, path);
            CheckVersion(list.Version, path);
            CheckLabel(list.Name, $"{path}/name", "access list", names);
            ids.Add(list.Id);
            CheckEnum(list.Satisfy, $"{path}/satisfy");
            CheckNotes(list.Notes, path);

            if (list.Ip is { } ip)
            {
                CheckAddresses(ip.Allow ?? [], $"{path}/ip/allow");
                CheckAddresses(ip.Deny ?? [], $"{path}/ip/deny");
                CheckCountries(ip.Countries, $"{path}/ip/countries");
                if (ip.DropDenied && (ip.Allow ?? []).Count == 0 && (ip.Deny ?? []).Count == 0
                    && (ip.Countries?.Allow ?? []).Count == 0 && (ip.Countries?.Deny ?? []).Count == 0)
                {
                    Add($"{path}/ip/dropDenied", AccessListDropDeniedWithoutAddresses, "an access list without addresses or countries refuses none, so there is nothing to drop");
                }
            }

            if (list.BasicAuth is not { } auth)
            {
                return;
            }

            if (auth.Realm is { } realm
                && (realm.Trim().Length is 0 or > AccessListLimits.MaxRealmLength || realm.Trim().AsSpan().ContainsAnyExcept(AsciiChars.Printable)))
            {
                Add($"{path}/basicAuth/realm", AccessListRealmInvalid, $"the realm must be 1-{AccessListLimits.MaxRealmLength} printable ASCII characters, or left out to use the list's name");
            }

            var users = auth.Users ?? [];
            if (users.Count > AccessListLimits.MaxUsers)
            {
                Add($"{path}/basicAuth/users", AccessListUsersTooMany, $"an access list can have at most {AccessListLimits.MaxUsers} users");
            }

            HashSet<string> usernames = new(StringComparer.Ordinal);
            for (var i = 0; i < users.Count; i++)
            {
                if (NotNull(users[i], $"{path}/basicAuth/users/{i}"))
                {
                    CheckUser(users[i], $"{path}/basicAuth/users/{i}", usernames);
                }
            }
        }

        private void CheckUser(AccessListUserConfig user, string path, HashSet<string> usernames)
        {
            var name = user.Username ?? string.Empty;
            if (name.Length is 0 or > AccessListLimits.MaxUsernameLength || name.AsSpan().ContainsAnyExcept(AsciiChars.UsernameChars))
            {
                Add($"{path}/username", AccessListUsernameInvalid, $"a username must be 1-{AccessListLimits.MaxUsernameLength} visible ASCII characters, without a colon");
            }
            else if (!usernames.Add(name))
            {
                Add($"{path}/username", AccessListUsernameDuplicate, $"the username '{name}' is listed twice (usernames are case-sensitive)");
            }

            // The server hands a password to the vault before it validates anything and leaves the credential's ID in its place, so a
            // password still here is empty, too long, or a write path that skipped the vault: never stored either way.
            if (user.Password is { } password)
            {
                if (password.Length == 0)
                {
                    Add($"{path}/password", AccessListPasswordEmpty, "a password can't be empty");
                }
                else if (Encoding.UTF8.GetByteCount(password) > AccessListLimits.MaxPasswordBytes)
                {
                    Add($"{path}/password", AccessListPasswordTooLong, $"a password can be at most {AccessListLimits.MaxPasswordBytes} bytes");
                }
                else
                {
                    Add($"{path}/password", AccessListPasswordUnhashed, "a password must be handed to the vault before it is validated");
                }
            }
            else if (user.CredentialId is not { IsEmpty: false } credential)
            {
                Add($"{path}/password", AccessListPasswordMissing, "the user needs a password");
            }
            else if (!_credentialIds.Add(credential))
            {
                Add($"{path}/credentialId", AccessListCredentialDuplicate, "another user already has this password credential; each user has their own");
            }
        }

        /// <summary>A host's or route's resolved access list: the list must exist. A named list is checked even while the
        /// switch is off, because the reference is kept for turning it back on.</summary>
        private void CheckAccessListReference(
            HostAccessListSpec resolved,
            HashSet<AccessListId> lists,
            string path,
            string unknownCode,
            HostSettings? defaults,
            HostSettings? profile,
            HostSettings? host)
        {
            if (resolved.AccessListId.IsEmpty || lists.Contains(resolved.AccessListId))
            {
                return;
            }

            var inherited = HostSettingsLayers.InheritedSuffix(
                HostSettingsLayers.LayerOf(defaults, profile, host, static s => s.AccessList?.AccessListId));
            Add($"{path}/accessListId", unknownCode, $"access list '{resolved.AccessListId}' doesn't exist{inherited}");
        }

        private void CheckCountries(AccessListCountries? countries, string path)
        {
            if (countries is null)
            {
                return;
            }

            var allowed = CheckCountryCodes(countries.Allow ?? [], $"{path}/allow");
            var denied = CheckCountryCodes(countries.Deny ?? [], $"{path}/deny");
            foreach (var code in denied)
            {
                if (allowed.Contains(code))
                {
                    Add($"{path}/deny", AccessListCountryConflict, $"{code} is in both the allowed and the denied countries");
                }
            }
        }

        // The codes that are known, upper-cased; a code twice (in either case) is reported once at its second entry.
        private HashSet<string> CheckCountryCodes(IReadOnlyList<string> codes, string path)
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            for (var i = 0; i < codes.Count; i++)
            {
                var code = ConfigNormalization.CanonicalCountry(codes[i]);
                if (!Countries.TryParseRuleCode(code, out _))
                {
                    Add($"{path}/{i}", AccessListCountryInvalid, $"'{codes[i]}' isn't a country code (two letters from ISO 3166-1, T1 for Tor or XX for unknown)");
                }
                else if (!seen.Add(code))
                {
                    Add($"{path}/{i}", AccessListCountryDuplicate, $"{code} is listed twice");
                }
            }

            return seen;
        }

        private void CheckAddresses(IReadOnlyList<string> addresses, string path)
        {
            for (var i = 0; i < addresses.Count; i++)
            {
                if (!IpNetwork.TryParse((addresses[i] ?? string.Empty).Trim(), out _, out var error))
                {
                    Add($"{path}/{i}", AddressInvalid, $"'{addresses[i]}' isn't an IP address or CIDR ({error})");
                }
            }
        }

        // Absolute http(s) URL without credentials (the document holds no secrets); null when invalid.
        private Uri? CheckHttpUrl(string? value, string path, string invalidCode, string credentialsCode, string what)
        {
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
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
            var normalized = ConfigNormalization.NormalizeName(name);
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
            var text = id.ToString()!;
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
