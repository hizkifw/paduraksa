using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard;

/// <summary>
/// Every path the dashboard owns (PLAN §12): its pages, the form endpoints its sign-in pages post to (served by the admin app's
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

    /// <summary>The profiles: named sets of host settings.</summary>
    public const string Profiles = "/profiles";

    public const string Certificates = "/certificates";

    /// <summary>The local CA: its root to trust, the admin certificate it issues.</summary>
    public const string LocalCa = Certificates + "/local-ca";

    /// <summary>The ACME accounts certificates are ordered with.</summary>
    public const string AcmeAccounts = Certificates + "/acme";

    /// <summary>The DNS providers DNS-01 challenges are answered with.</summary>
    public const string DnsProviders = Certificates + "/dns";

    public const string History = "/history";

    public const string Audit = "/audit";

    public const string Simulator = "/simulator";

    public const string Settings = "/settings";

    /// <summary>The signed-in user's own account: profile, password, two-step verification and sessions.</summary>
    public const string Account = "/account";

    /// <summary>The last segment of a list's address that opens its drawer for a new object (<c>/hosts/new</c>).</summary>
    public const string NewSegment = "new";

    /// <summary>The hosts with the drawer for a new host open.</summary>
    public const string NewHost = Hosts + "/" + NewSegment;

    /// <summary>The upstreams with the drawer for a new upstream open.</summary>
    public const string NewUpstream = Upstreams + "/" + NewSegment;

    /// <summary>The profiles with the drawer for a new profile open.</summary>
    public const string NewProfile = Profiles + "/" + NewSegment;

    /// <summary>The page shown for an address the dashboard has no page at.</summary>
    public const string NotFound = "/not-found";

    /// <summary>A host: <c>/hosts/host_...</c>, the hosts with its drawer open. The page parses the segment back into a <see cref="HostId"/>.</summary>
    public static string Host(HostId id) => $"{Hosts}/{id}";

    /// <summary>An upstream: <c>/upstreams/ups_...</c>, the upstreams with its drawer open.</summary>
    public static string Upstream(UpstreamId id) => $"{Upstreams}/{id}";

    /// <summary>A profile: <c>/profiles/prof_...</c>, the profiles with its drawer open. The page parses the segment back into a <see cref="ProfileId"/>.</summary>
    public static string Profile(ProfileId id) => $"{Profiles}/{id}";

    /// <summary>A certificate: <c>/certificates/crt_...</c>, the certificates with its details open.</summary>
    public static string Certificate(CertificateId id) => $"{Certificates}/{id}";

    /// <summary>The history with a revision selected: <c>/history?revision=rev_...</c>.</summary>
    public static string Revision(ConfigRevisionId id) => $"{History}?revision={id}";

    /// <summary>The simulator against a stored revision.</summary>
    public static string SimulateRevision(ConfigRevisionId id) => $"{Simulator}?revision={id}";

    /// <summary>Blazor's circuit hub (its negotiate request and WebSocket).</summary>
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
