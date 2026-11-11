using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard;

/// <summary>
/// Every path the dashboard owns: its pages, the form endpoints its sign-in pages post to (served by the admin app's
/// <c>MapPaduraksaAdminWeb</c>, which the composition root hands these names), the circuit hub and the static files.
/// </summary>
public static class DashboardRoutes
{
    public const string Home = "/";

    public const string SignIn = "/signin";

    public const string SignInVerify = "/signin/verify";

    public const string Setup = "/setup";

    public const string Hosts = "/hosts";

    public const string Upstreams = "/upstreams";

    public const string Profiles = "/profiles";

    public const string Security = "/security";

    public const string RateLimits = "/rate-limits";

    public const string AccessLists = "/access-lists";

    public const string RuleSets = "/rules";

    public const string AuthProviders = "/sign-in";

    public const string Certificates = "/certificates";

    public const string LocalCa = Certificates + "/local-ca";

    public const string AcmeAccounts = Certificates + "/acme";

    public const string DnsProviders = Certificates + "/dns";

    public const string History = "/history";

    public const string Audit = "/audit";

    public const string Logs = "/logs";

    public const string Simulator = "/simulator";

    public const string Settings = "/settings";

    public const string Account = "/account";

    /// <summary>The last segment of a list's address that opens its drawer for a new object (<c>/hosts/new</c>).</summary>
    public const string NewSegment = "new";

    public const string NewHost = Hosts + "/" + NewSegment;

    public const string NewUpstream = Upstreams + "/" + NewSegment;

    public const string NewProfile = Profiles + "/" + NewSegment;

    public const string NewRateLimit = RateLimits + "/" + NewSegment;

    public const string NewAccessList = AccessLists + "/" + NewSegment;

    public const string NewRuleSet = RuleSets + "/" + NewSegment;

    public const string NewAuthProvider = AuthProviders + "/" + NewSegment;

    /// <summary>The query parameter of <see cref="NewRuleSet"/> that names the built-in preset to start from.</summary>
    public const string CopyParameter = "copy";

    /// <summary>The page shown for an address the dashboard has no page at.</summary>
    public const string NotFound = "/not-found";

    /// <summary>A host: <c>/hosts/host_...</c>, the hosts with its drawer open. The page parses the segment back into a <see cref="HostId"/>.</summary>
    public static string Host(HostId id) => $"{Hosts}/{id}";

    public static string Upstream(UpstreamId id) => $"{Upstreams}/{id}";

    /// <summary>A profile: <c>/profiles/prof_...</c>, the profiles with its drawer open. The page parses the segment back into a <see cref="ProfileId"/>.</summary>
    public static string Profile(ProfileId id) => $"{Profiles}/{id}";

    /// <summary>A rate-limit policy: <c>/rate-limits/rlp_...</c>, the policies with its drawer open. The page parses the segment back into a <see cref="RateLimitPolicyId"/>.</summary>
    public static string RateLimit(RateLimitPolicyId id) => $"{RateLimits}/{id}";

    /// <summary>An access list: <c>/access-lists/acl_...</c>, the lists with its drawer open. The page parses the segment back into an <see cref="AccessListId"/>.</summary>
    public static string AccessList(AccessListId id) => $"{AccessLists}/{id}";

    /// <summary>A rule set: <c>/rules/rs_...</c>, the rule sets with its drawer open. The page parses the segment back into a <see cref="RuleSetId"/>.</summary>
    public static string RuleSet(RuleSetId id) => $"{RuleSets}/{id}";

    /// <summary>An auth provider: <c>/sign-in/authp_...</c>, the providers with its drawer open. The page parses the segment back into an <see cref="AuthProviderId"/>.</summary>
    public static string AuthProvider(AuthProviderId id) => $"{AuthProviders}/{id}";

    /// <summary>A new rule set that starts as a copy of a preset: <c>/rules/new?copy=rs_...</c>.</summary>
    public static string CopyRuleSet(RuleSetId preset) => $"{NewRuleSet}?{CopyParameter}={preset}";

    public static string Certificate(CertificateId id) => $"{Certificates}/{id}";

    public static string Revision(ConfigRevisionId id) => $"{History}?revision={id}";

    public static string SimulateRevision(ConfigRevisionId id) => $"{Simulator}?revision={id}";

    public const string CircuitHub = "/_blazor";

    /// <summary>The static files: the framework's script and the component libraries' (MudBlazor's and this one's) assets.</summary>
    public static IReadOnlyList<string> StaticAssetPrefixes { get; } = ["/_framework", "/_content"];

    /// <summary>The form endpoints behind the sign-in, second-factor, sign-out and setup pages.</summary>
    public static class Actions
    {
        public const string SignIn = "/auth/signin";

        public const string SecondFactor = "/auth/second-factor";

        public const string SignOut = "/auth/signout";

        public const string Setup = "/auth/setup";
    }
}

/// <summary>
/// The names of the sign-in forms' fields and of the query parameters their answers carry. They match the admin app's
/// <c>AdminWebForm</c> (a test checks); the dashboard keeps its own copy because it does not reference the admin app.
/// </summary>
public static class DashboardForm
{
    public const string ErrorParameter = "error";

    public const string NoticeParameter = "notice";

    public const string SignedOutNotice = "signedOut";

    /// <summary>The notice when a page's session ended under it (revoked, expired, user disabled); the dashboard's own.</summary>
    public const string SessionEndedNotice = "sessionEnded";

    public const string ReturnUrl = "returnUrl";

    public const string RecoveryParameter = "recovery";

    public const string Username = "username";

    public const string Password = "password";

    public const string Code = "code";

    public const string RecoveryCode = "recoveryCode";

    public const string SetupToken = "setupToken";

    public const string DisplayName = "displayName";

    public const string ConfirmPassword = "confirmPassword";
}
