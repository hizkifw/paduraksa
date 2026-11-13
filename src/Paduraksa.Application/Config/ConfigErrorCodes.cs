namespace Paduraksa.Application.Config;

/// <summary>The stable <see cref="Contracts.Config.ConfigError.Code"/>s of <see cref="ConfigValidator"/> and of the
/// config compiler.</summary>
public static class ConfigErrorCodes
{
    /// <summary>The compiler rejected the document (<see cref="Compilation.ConfigCompiler"/>): something only it can
    /// check, such as a certificate that doesn't exist, or a rule the validator misses.</summary>
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
    public const string AccessListDropDeniedWithoutAddresses = "accessList.ip.dropDenied.withoutAddresses";
    public const string AccessListCountryInvalid = "accessList.countries.code.invalid";
    public const string AccessListCountryDuplicate = "accessList.countries.code.duplicate";
    public const string AccessListCountryConflict = "accessList.countries.code.conflict";
    public const string AccessListRealmInvalid = "accessList.realm.invalid";
    public const string AccessListUsersTooMany = "accessList.users.tooMany";
    public const string AccessListUsernameInvalid = "accessList.user.username.invalid";
    public const string AccessListUsernameDuplicate = "accessList.user.username.duplicate";
    public const string AccessListPasswordMissing = "accessList.user.password.missing";
    public const string AccessListCredentialDuplicate = "accessList.user.credentialId.duplicate";
    public const string AccessListPasswordEmpty = "accessList.user.password.empty";
    public const string AccessListPasswordTooLong = "accessList.user.password.tooLong";
    public const string AccessListPasswordUnhashed = "accessList.user.password.unhashed";
    public const string RuleSetIdReserved = "ruleSet.id.reserved";
    public const string RuleSetRulesTooMany = "ruleSet.rules.tooMany";
    public const string RuleEmpty = "ruleSet.rule.empty";
    public const string RulePathInvalid = "ruleSet.rule.path.invalid";
    public const string RulePathEncoded = "ruleSet.rule.path.encoded";
    public const string RulePathNotCanonical = "ruleSet.rule.path.notCanonical";
    public const string RulePathGlobInvalid = "ruleSet.rule.path.glob.invalid";
    public const string RulePathRegexInvalid = "ruleSet.rule.path.regex.invalid";
    public const string RuleMethodsInvalid = "ruleSet.rule.methods.invalid";
    public const string RuleUserAgentInvalid = "ruleSet.rule.userAgent.invalid";
    public const string AuthProviderForwardAuthRequired = "authProvider.forwardAuth.required";
    public const string AuthProviderUrlInvalid = "authProvider.url.invalid";
    public const string AuthProviderTimeoutInvalid = "authProvider.timeoutSeconds.invalid";
    public const string AuthProviderCacheSecondsInvalid = "authProvider.cacheSeconds.invalid";
    public const string AuthProviderHeadersTooMany = "authProvider.headers.tooMany";
    public const string AuthProviderHeaderInvalid = "authProvider.header.invalid";
    public const string AuthProviderHeaderCredential = "authProvider.header.credential";
    public const string AuthProviderSignInInvalid = "authProvider.signIn.invalid";
    public const string AuthProviderReturnParameterInvalid = "authProvider.signIn.returnParameter.invalid";
    public const string HstsMaxAgeInvalid = "hsts.maxAge.invalid";
    public const string HstsPreloadNeedsIncludeSubDomains = "hsts.preload.needsIncludeSubDomains";
    public const string HstsPreloadMaxAgeTooShort = "hsts.preload.maxAgeTooShort";
    public const string ServerHeaderValueRequired = "serverHeader.value.required";
    public const string ServerHeaderValueInvalid = "serverHeader.value.invalid";
    public const string ProofOfWorkDifficultyInvalid = "proofOfWork.difficulty.invalid";
    public const string ProofOfWorkTokenLifetimeInvalid = "proofOfWork.tokenLifetimeHours.invalid";
    public const string ProofOfWorkRouteDifficultyUnexpected = "proofOfWork.route.difficultyUnexpected";
    public const string ProofOfWorkRouteTokenLifetimeUnexpected = "proofOfWork.route.tokenLifetimeHoursUnexpected";
    public const string RateLimitRequestsInvalid = "rateLimit.requests.invalid";
    public const string RateLimitBurstInvalid = "rateLimit.burst.invalid";
    public const string RateLimitRateInvalid = "rateLimit.rate.invalid";
    public const string RateLimitHeaderRequired = "rateLimit.header.required";
    public const string RateLimitHeaderUnexpected = "rateLimit.header.unexpected";
    public const string RateLimitHeaderInvalid = "rateLimit.header.invalid";
    public const string RequestLimitsMaxBodySizeInvalid = "requestLimits.maxBodySize.invalid";
    public const string RequestLimitsMaxHeaderCountInvalid = "requestLimits.maxHeaderCount.invalid";
    public const string RequestLimitsMaxHeaderBytesInvalid = "requestLimits.maxHeaderBytes.invalid";
    public const string RequestLimitsMaxUrlLengthInvalid = "requestLimits.maxUrlLength.invalid";
    public const string RequestLimitsRouteMaxHeaderCountUnexpected = "requestLimits.route.maxHeaderCountUnexpected";
    public const string RequestLimitsRouteMaxHeaderBytesUnexpected = "requestLimits.route.maxHeaderBytesUnexpected";
    public const string RequestLimitsRouteMaxUrlLengthUnexpected = "requestLimits.route.maxUrlLengthUnexpected";
    public const string RobotsCustomTextRequired = "robots.customText.required";
    public const string RobotsCustomTextUnexpected = "robots.customText.unexpected";
    public const string RobotsCustomTextTooLong = "robots.customText.tooLong";
    public const string RobotsCustomTextInvalid = "robots.customText.invalid";
    public const string ErrorPagesHeadingRequired = "errorPages.heading.required";
    public const string ErrorPagesHeadingTooLong = "errorPages.heading.tooLong";
    public const string ErrorPagesHeadingInvalid = "errorPages.heading.invalid";
    public const string ErrorPagesHeadingUnexpected = "errorPages.heading.unexpected";
    public const string ErrorPagesTextTooLong = "errorPages.text.tooLong";
    public const string ErrorPagesTextInvalid = "errorPages.text.invalid";
    public const string ErrorPagesTextUnexpected = "errorPages.text.unexpected";
    public const string ErrorPagesHtmlRequired = "errorPages.html.required";
    public const string ErrorPagesHtmlTooLong = "errorPages.html.tooLong";
    public const string ErrorPagesHtmlInvalid = "errorPages.html.invalid";
    public const string ErrorPagesHtmlUnexpected = "errorPages.html.unexpected";
    public const string ErrorPagesVariableUnknown = "errorPages.variable.unknown";
    public const string RouteRewriteStripRoot = "route.rewrite.stripPrefix.root";
    public const string RouteRewriteAddPrefixInvalid = "route.rewrite.addPrefix.invalid";
    public const string RouteRewriteLocationUnused = "route.rewrite.rewriteLocation.unused";
    public const string CompressionMinimumSizeInvalid = "compression.minimumSize.invalid";
    public const string HeadersTooMany = "headers.tooMany";
    public const string HeadersNameInvalid = "headers.name.invalid";
    public const string HeadersNameReserved = "headers.name.reserved";
    public const string HeadersNameDuplicate = "headers.name.duplicate";
    public const string HeadersOperationInvalid = "headers.operation.invalid";
    public const string HeadersValueInvalid = "headers.value.invalid";
    public const string HeadersValueUnexpected = "headers.value.unexpected";
    public const string MaintenanceStatusInvalid = "maintenance.status.invalid";
    public const string MaintenanceRetryAfterInvalid = "maintenance.retryAfterSeconds.invalid";
    public const string HostMaintenanceBypassUnknown = "host.maintenance.bypassUnknown";
    public const string TrustedProxiesProxiesRequired = "trustedProxies.proxies.required";
    public const string TrustedProxiesProxiesUnexpected = "trustedProxies.proxies.unexpected";
    public const string TrustedProxiesHeaderInvalid = "trustedProxies.header.invalid";
    public const string TrustedProxiesOptionUnexpected = "trustedProxies.option.unexpected";
    public const string ConnectionsMaxPerIpInvalid = "connections.maxConnectionsPerIp.invalid";
    public const string ConnectionsDenyListUnknown = "connections.denyList.unknown";

    // Upstreams.
    public const string UpstreamDestinationsRequired = "upstream.destinations.required";
    public const string UpstreamDestinationsNoneEnabled = "upstream.destinations.noneEnabled";
    public const string UpstreamMaxConnectionsInvalid = "upstream.maxConnectionsPerServer.invalid";
    public const string UpstreamReactivationPeriodInvalid = "upstream.passiveHealth.reactivationPeriod.invalid";
    public const string UpstreamTlsPinsMissing = "upstream.tls.pinnedSha256.missing";
    public const string UpstreamTlsPinsUnused = "upstream.tls.pinnedSha256.unused";
    public const string UpstreamTlsPinInvalid = "upstream.tls.pinnedSha256.invalid";
    public const string UpstreamHostHeaderValueRequired = "upstream.hostHeader.value.required";
    public const string UpstreamHostHeaderValueUnused = "upstream.hostHeader.value.unused";
    public const string UpstreamHostHeaderValueInvalid = "upstream.hostHeader.value.invalid";
    public const string UpstreamActiveHealthPathInvalid = "upstream.activeHealth.path.invalid";
    public const string UpstreamActiveHealthIntervalInvalid = "upstream.activeHealth.interval.invalid";
    public const string UpstreamActiveHealthTimeoutInvalid = "upstream.activeHealth.timeout.invalid";
    public const string UpstreamConnectTimeoutInvalid = "upstream.timeouts.connect.invalid";
    public const string UpstreamIdleTimeoutInvalid = "upstream.timeouts.idle.invalid";
    public const string DestinationAddressInvalid = "destination.address.invalid";
    public const string DestinationAddressCredentials = "destination.address.credentials";
    public const string DestinationAddressQueryOrFragment = "destination.address.queryOrFragment";

    // Hosts.
    public const string HostNamesRequired = "host.names.required";
    public const string HostNameInvalid = "host.name.invalid";
    public const string HostNameDuplicate = "host.name.duplicate";
    public const string HostProfileUnknown = "host.profile.unknown";
    public const string HostRateLimitUnknown = "host.rateLimit.unknown";
    public const string HostAccessListUnknown = "host.accessList.unknown";
    public const string HostRuleSetUnknown = "host.ruleSet.unknown";
    public const string HostAuthProviderUnknown = "host.authProvider.unknown";
    public const string HostCertificateIdRequired = "host.certificate.idRequired";
    public const string HostCertificateIdUnexpected = "host.certificate.idUnexpected";
    public const string HostCertificateNoneWithForceHttps = "host.certificate.noneWithForceHttps";
    public const string HostCertificateUnusable = "host.certificate.unusable";
    public const string SettingsDefaultCertificateUnusable = "settings.defaultCertificate.unusable";
    public const string HostRoutesRequired = "host.routes.required";
    public const string HostRoutesUnexpected = "host.routes.unexpected";
    public const string HostAdminDuplicate = "host.admin.duplicate";
    public const string HostAdminHttpsRequired = "host.admin.httpsRequired";
    public const string HostAdminLockout = "host.admin.lockout";
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
    public const string RouteUpstreamRequired = "route.upstream.required";
    public const string RouteUpstreamUnknown = "route.upstream.unknown";
    public const string RouteRateLimitUnknown = "route.rateLimit.unknown";
    public const string RouteAccessListUnknown = "route.accessList.unknown";
    public const string RouteRuleSetUnknown = "route.ruleSet.unknown";
    public const string RouteAuthProviderUnknown = "route.authProvider.unknown";
}
