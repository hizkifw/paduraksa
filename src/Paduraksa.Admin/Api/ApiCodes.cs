namespace Paduraksa.Admin.Api;

/// <summary>
/// The stable machine-readable <c>code</c>s of the admin API's problems, grouped by area: dotted lowerCamelCase segments, an
/// area and then what happened, the shape the config errors use too (<c>host.certificate.unusable</c>), so a client can
/// branch on them and the words never have to change meaning. The config, issuance, certificate, simulation and identity
/// error codes (<c>ConfigError.Code</c>) live beside their validators in Application; these are the problem-level ones.
/// </summary>
/// <remarks>
/// A unit test (<c>ProblemCodeConventionTests</c>) scans every constant here and in the Application code classes and rejects
/// anything that doesn't match <c>^[a-z][a-zA-Z0-9]*(\.[a-z][a-zA-Z0-9]*)+$</c>. The OAuth <c>WWW-Authenticate</c>
/// <c>error=</c> values are not codes (RFC 6750 defines them) and stay <c>invalid_token</c>/<c>invalid_request</c>.
/// </remarks>
internal static class ApiCodes
{
    /// <summary>Authentication and authorization (401, 403, 429, 503).</summary>
    public static class Auth
    {
        /// <summary>401: no credential, or the caller isn't signed in.</summary>
        public const string Required = "auth.required";

        /// <summary>401: a bearer token that is not valid.</summary>
        public const string InvalidToken = "auth.invalidToken";

        /// <summary>401: an unknown, expired or revoked session cookie.</summary>
        public const string InvalidSession = "auth.invalidSession";

        /// <summary>401: an <c>Authorization</c> header that isn't <c>Bearer &lt;token&gt;</c>.</summary>
        public const string InvalidAuthorization = "auth.invalidAuthorization";

        /// <summary>401: two credentials, or the session cookie twice, in one request.</summary>
        public const string AmbiguousCredentials = "auth.ambiguousCredentials";

        /// <summary>401: a sign-in refused (wrong password, locked, disabled or unknown user: never which).</summary>
        public const string InvalidCredentials = "auth.invalidCredentials";

        /// <summary>401: a wrong second-factor or recovery code.</summary>
        public const string InvalidCode = "auth.invalidCode";

        /// <summary>401: the partial session is gone; start again with the password.</summary>
        public const string SessionExpired = "auth.sessionExpired";

        /// <summary>403: an API token tried to manage tokens or sessions; a signed-in session is required.</summary>
        public const string SessionRequired = "auth.sessionRequired";

        /// <summary>403: the caller lacks the permission the operation needs.</summary>
        public const string PermissionRequired = "auth.permissionRequired";

        /// <summary>429: too many attempts; see <c>Retry-After</c>.</summary>
        public const string RateLimited = "auth.rateLimited";

        /// <summary>503: every password-derivation slot is busy; nothing was tried.</summary>
        public const string Busy = "auth.busy";
    }

    public static class Setup
    {
        /// <summary>503: no Owner exists yet, so only the setup flow is served.</summary>
        public const string Required = "setup.required";

        /// <summary>503: the setup state can't be read (the config store is down), so the app stays closed.</summary>
        public const string Unavailable = "setup.unavailable";

        /// <summary>409: an Owner exists already.</summary>
        public const string Complete = "setup.complete";

        /// <summary>403: the setup token is wrong or missing.</summary>
        public const string InvalidToken = "setup.invalidToken";

        /// <summary>403: first-run setup is never reachable through the proxy's internal listener.</summary>
        public const string ViaProxy = "setup.viaProxy";
    }

    public static class Csrf
    {
        /// <summary>403: a cookie-authenticated unsafe request that a browser could have forged.</summary>
        public const string Refused = "csrf.refused";
    }

    /// <summary>The internal listener between the proxy and the admin app.</summary>
    public static class Admin
    {
        /// <summary>403: the internal listener's hop header is missing or wrong.</summary>
        public const string InternalHop = "admin.internalHop";

        /// <summary>403: the forwarded client address isn't in <c>Paduraksa:Listen:AdminAllowedSources</c>.</summary>
        public const string SourceDenied = "admin.sourceDenied";
    }

    public static class Ca
    {
        /// <summary>404: this process holds no local CA root.</summary>
        public const string NotFound = "ca.notFound";
    }

    public static class Backup
    {
        /// <summary>422: the passphrase is shorter than the minimum or longer than the maximum.</summary>
        public const string Passphrase = "backup.passphrase";

        /// <summary>409: another backup is already being made.</summary>
        public const string Busy = "backup.busy";

        /// <summary>500: the backup could not be made; the server's log says why.</summary>
        public const string Failed = "backup.failed";
    }

    public static class Config
    {
        /// <summary>500: the change is stored but could not go live; the previous config still serves.</summary>
        public const string PublishFailed = "config.publishFailed";
    }

    /// <summary>Identity outcomes that are one thing over several operations.</summary>
    public static class Identity
    {
        /// <summary>409: the change clashes with something that exists.</summary>
        public const string Conflict = "identity.conflict";

        /// <summary>409: the change would leave no enabled Owner.</summary>
        public const string LastOwner = "identity.lastOwner";

        /// <summary>422: the request was refused; see <c>errors</c>.</summary>
        public const string Refused = "identity.refused";

        /// <summary>422: the current password is wrong at a sensitive step (never a 401, so a UI doesn't read it as a dead session).</summary>
        public const string PasswordWrong = "identity.password.wrong";
    }
}
