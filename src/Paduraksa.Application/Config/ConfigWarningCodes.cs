namespace Paduraksa.Application.Config;

/// <summary>The stable <see cref="Contracts.Config.ConfigWarning.Code"/>s of the config compiler.</summary>
public static class ConfigWarningCodes
{
    /// <summary>A host with the Auto binding has names that no valid certificate covers.</summary>
    public const string CertificateAutoNoCoverage = "certificate.auto.noCoverage";

    /// <summary>A host's Specific binding names a certificate that is missing, expired, not yet valid, pending,
    /// failed or without a key in the vault. A command that <i>introduces</i> such a binding is invalid (<see
    /// cref="ConfigErrorCodes.HostCertificateUnusable"/>); one that finds it already there (the certificate expired
    /// since) only warns, so one bad certificate never blocks the rest.</summary>
    public const string CertificateSpecificUnusable = "certificate.specific.unusable";

    /// <summary>A host's Specific certificate doesn't cover some of the host's names (clients will see a name mismatch).</summary>
    public const string CertificateSpecificNoCoverage = "certificate.specific.noCoverage";

    /// <summary>The default certificate the config names is unusable (see <see cref="CertificateSpecificUnusable"/>);
    /// the inventory's fallback default, if any, serves instead.</summary>
    public const string CertificateDefaultUnusable = "certificate.default.unusable";

    /// <summary>An admin host set <c>forceHttps: false</c>; an admin host is always forced to HTTPS, so the setting is ignored.</summary>
    public const string AdminForceHttpsIgnored = "host.admin.forceHttpsIgnored";

    /// <summary>An admin host exists while <c>Paduraksa:Listen:AdminInternal</c> is off: it answers 503 until the listener is enabled.</summary>
    public const string AdminInternalDisabled = "host.admin.internalDisabled";

    /// <summary>An upstream with HTTPS destinations accepts any certificate (<c>tls.trust: any</c>).</summary>
    public const string UpstreamTlsUnverified = "upstream.tls.unverified";

    /// <summary>The proof-of-work gate is on for a host that also answers plain HTTP without forcing HTTPS, where its
    /// cookie cannot be marked <c>Secure</c>.</summary>
    public const string ProofOfWorkInsecureCookie = "proofOfWork.insecureCookie";

    /// <summary>A host sets origin lockdown or Authenticated Origin Pulls while the trusted-proxy mode is neither
    /// <c>CloudflareProxy</c> nor <c>CloudflareTunnel</c>: nothing is enforced.</summary>
    public const string CloudflareNeedsMode = "cloudflare.needsMode";

    /// <summary>A host requires Authenticated Origin Pulls but isn't served over HTTPS, so it can never present the
    /// certificate and every request is refused.</summary>
    public const string CloudflareAopNeedsHttps = "cloudflare.aop.needsHttps";

    /// <summary>A host requires Authenticated Origin Pulls under <c>CloudflareTunnel</c>, where <c>cloudflared</c> presents no
    /// client certificate, so every request is refused.</summary>
    public const string CloudflareAopTunnel = "cloudflare.aop.tunnel";

    /// <summary>The connection deny list is an access list that also has allow addresses, countries or users: before TLS only its
    /// deny addresses apply, and the rest is for the hosts that name the list.</summary>
    public const string ConnectionsDenyListRequestRules = "connections.denyList.requestRules";

    /// <summary>The trusted-proxy mode is <c>CloudflareProxy</c> and the list of Cloudflare's edge ranges is older than 30
    /// days, or several refreshes in a row failed.</summary>
    public const string CloudflareRangesStale = "cloudflare.ranges.stale";

    /// <summary>An access list with users guards a host that also answers plain HTTP without forcing HTTPS, so a password
    /// could travel in the clear.</summary>
    public const string AccessListInsecureCredentials = "accessList.insecureCredentials";


    /// <summary>An access-list user whose credential the vault holds no password for (it was deleted since, say by a rollback to a
    /// revision from before): the user is listed but can't sign in until a password is set.</summary>
    public const string AccessListUserNoPassword = "accessList.user.noPassword";

    /// <summary>An enabled host uses an access list with country rules while the trusted-proxy mode isn't a Cloudflare one, so every
    /// request's country is unknown.</summary>
    public const string AccessListCountriesNoSource = "accessList.countries.noSource";

    /// <summary>An auth provider guards a host that also answers plain HTTP without forcing HTTPS, so the session cookie it relies on
    /// could travel in the clear.</summary>
    public const string AuthProviderInsecureSession = "authProvider.insecureSession";

    /// <summary>An auth provider is set for an admin, redirect or respond host, which never asks one: the admin must not wait on a
    /// provider that could lock the operator out, and the other two have no route to ask it on.</summary>
    public const string AuthProviderIgnored = "authProvider.ignored";

    /// <summary>A layer names an auth provider while a less specific layer (the defaults or a profile, for a host; the host, for a route) has
    /// sign-in switched off: the switch stays off, so nothing asks the provider and the requests are let through unchecked.</summary>
    public const string AuthProviderNamedButOff = "authProvider.namedButOff";

    /// <summary>A provider's sign-in page is a path on the protected host that the host has no route for with sign-in off (the first route to take it
    /// is behind a provider too, or none does): a browser sent there is refused again and sent there again.</summary>
    public const string AuthProviderSignInUnreachable = "authProvider.signInUnreachable";

    /// <summary>A layer turns maintenance mode on for an admin host, which never serves it: a layer that covers every host must not lock the
    /// dashboard out.</summary>
    public const string MaintenanceAdminIgnored = "maintenance.adminIgnored";

    /// <summary>A layer turns on replacing upstream errors for an admin host, which never does: the admin app's JSON problems stay
    /// as they are for API clients.</summary>
    public const string ErrorPagesReplaceAdminIgnored = "errorPages.replaceUpstream.adminIgnored";

    /// <summary>Maintenance mode names a bypass access list with no allowed address or country, so nobody can bypass it.</summary>
    public const string MaintenanceBypassNoAllowRules = "maintenance.bypass.noAllowRules";
}
