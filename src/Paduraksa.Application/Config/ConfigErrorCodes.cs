namespace Paduraksa.Application.Config;

/// <summary>The stable <see cref="Contracts.Config.ConfigError.Code"/>s of <see cref="ConfigValidator"/> and of the
/// config compiler.</summary>
public static class ConfigErrorCodes
{
    /// <summary>The compiler rejected the document (<see cref="Compilation.ConfigCompiler"/>): something only it can
    /// check, such as a certificate that doesn't exist, or a rule the validator misses. The path is the object's.</summary>
    public const string CompileFailed = "compile.failed";

    // Whole document and any object.
    public const string ApiVersionUnsupported = "apiVersion.unsupported";
    public const string IdEmpty = "id.empty";
    public const string IdDuplicate = "id.duplicate";
    public const string VersionNegative = "version.negative";
    public const string EnumUndefined = "enum.undefined";
    public const string NameRequired = "name.required";
    public const string NameTooLong = "name.tooLong";
    public const string NameDuplicate = "name.duplicate";
    public const string NameInvalid = "name.invalid";
    public const string NotesTooLong = "notes.tooLong";
    public const string ValueNull = "value.null";
    public const string AddressInvalid = "address.invalid";

    // Settings layers.
    public const string IpAllowListDropDeniedWithoutAddresses = "ipAllowList.dropDenied.withoutAddresses";
    public const string TrustedProxiesProxiesRequired = "trustedProxies.proxies.required";
    public const string TrustedProxiesProxiesUnexpected = "trustedProxies.proxies.unexpected";
    public const string TrustedProxiesHeaderInvalid = "trustedProxies.header.invalid";
    public const string TrustedProxiesOptionUnexpected = "trustedProxies.option.unexpected";

    // Upstreams.
    public const string UpstreamDestinationsRequired = "upstream.destinations.required";
    public const string UpstreamDestinationsNoneEnabled = "upstream.destinations.noneEnabled";
    public const string UpstreamMaxConnectionsInvalid = "upstream.maxConnectionsPerServer.invalid";
    public const string UpstreamReactivationPeriodInvalid = "upstream.passiveHealth.reactivationPeriod.invalid";
    public const string UpstreamActiveHealthPathInvalid = "upstream.activeHealth.path.invalid";
    public const string UpstreamActiveHealthIntervalInvalid = "upstream.activeHealth.interval.invalid";
    public const string UpstreamActiveHealthTimeoutInvalid = "upstream.activeHealth.timeout.invalid";
    public const string DestinationAddressInvalid = "destination.address.invalid";
    public const string DestinationAddressCredentials = "destination.address.credentials";
    public const string DestinationAddressQueryOrFragment = "destination.address.queryOrFragment";

    // Hosts.
    public const string HostNamesRequired = "host.names.required";
    public const string HostNameInvalid = "host.name.invalid";
    public const string HostNameDuplicate = "host.name.duplicate";
    public const string HostProfileUnknown = "host.profile.unknown";
    public const string HostCertificateIdRequired = "host.certificate.idRequired";
    public const string HostCertificateIdUnexpected = "host.certificate.idUnexpected";
    public const string HostCertificateNoneWithForceHttps = "host.certificate.noneWithForceHttps";
    public const string HostCertificateUnusable = "host.certificate.unusable";
    public const string SettingsDefaultCertificateUnusable = "settings.defaultCertificate.unusable";
    public const string HostRoutesRequired = "host.routes.required";
    public const string HostRoutesUnexpected = "host.routes.unexpected";
    public const string HostRedirectRequired = "host.redirect.required";
    public const string HostRedirectUnexpected = "host.redirect.unexpected";
    public const string HostRespondRequired = "host.respond.required";
    public const string HostRespondUnexpected = "host.respond.unexpected";
    public const string HostTagInvalid = "host.tag.invalid";
    public const string RedirectTargetInvalid = "redirect.target.invalid";
    public const string RedirectTargetCredentials = "redirect.target.credentials";
    public const string RedirectTargetQueryWithPreservePath = "redirect.target.queryWithPreservePath";
    public const string RedirectStatusCodeInvalid = "redirect.statusCode.invalid";
    public const string RespondStatusCodeInvalid = "respond.statusCode.invalid";
    public const string RespondBodyForbidden = "respond.body.forbidden";
    public const string RespondBodyTooLong = "respond.body.tooLong";
    public const string RespondContentTypeInvalid = "respond.contentType.invalid";

    // Routes.
    public const string RoutePathInvalid = "route.path.invalid";
    public const string RoutePathEncoded = "route.path.encoded";
    public const string RoutePathNotCanonical = "route.path.notCanonical";
    public const string RoutePathDuplicate = "route.path.duplicate";
    public const string RouteUpstreamUnknown = "route.upstream.unknown";
}
