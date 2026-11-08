using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Audit;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Audit;
using Paduraksa.Dashboard.Auth;
using Paduraksa.Dashboard.Identity;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The identity seam in memory: users, tokens and sessions, scripted refusals, a TOTP enrolment with a fixed code.</summary>
internal sealed class FakeDashboardIdentity : IDashboardIdentity
{
    public const string Password = "correct horse battery staple";
    public const string TotpCode = "123456";
    public const string ProvisioningUri = "otpauth://totp/Paduraksa:ada?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=Paduraksa";
    public const string TokenSecret = "pdk_fake0123456789abcdefghijklmnopqrstuvwxyz";

    private readonly List<Func<Task>> _watchers = [];

    public List<UserSummary> Users { get; } = [];

    public List<ApiTokenSummary> Tokens { get; } = [];

    public List<SessionSummary> Sessions { get; } = [];

    public List<string> Calls { get; } = [];

    public List<CreateUserRequest> Created { get; } = [];

    public List<(UserId Id, UpdateUserRequest Request)> Updates { get; } = [];

    public List<IssueApiTokenRequest> Issued { get; } = [];

    /// <summary>The answer to an update; succeeds by default.</summary>
    public IdentityStatus UpdateStatus { get; set; } = IdentityStatus.Succeeded;

    public IdentityStatus DeleteStatus { get; set; } = IdentityStatus.Succeeded;

    public bool Busy { get; set; }

    public IReadOnlyList<string> RecoveryCodes { get; } = [.. Enumerable.Range(1, 10).Select(i => $"code-{i:00}-abcd-efgh")];

    public static UserSummary User(string username, params Role[] roles) =>
        new(UserId.New(), username, username, roles, false, false, false, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);

    public Task<UserResult> GetUserAsync(CommandOrigin origin, UserId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Users.FirstOrDefault(u => u.Id == id) is { } user ? new UserResult(IdentityStatus.Succeeded, user) : new UserResult(IdentityStatus.NotFound));

    public Task<UserListResult> ListUsersAsync(CommandOrigin origin, CancellationToken cancellationToken = default) =>
        Task.FromResult(new UserListResult(IdentityStatus.Succeeded, [.. Users]));

    public Task<UserResult> CreateUserAsync(CommandOrigin origin, CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        Created.Add(request);
        UserSummary user = new(UserId.New(), request.Username, request.DisplayName ?? request.Username, request.Roles, false, false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Users.Add(user);
        return Task.FromResult(new UserResult(IdentityStatus.Succeeded, user));
    }

    public Task<UserResult> UpdateUserAsync(CommandOrigin origin, UserId id, UpdateUserRequest request, CancellationToken cancellationToken = default)
    {
        Updates.Add((id, request));
        if (UpdateStatus != IdentityStatus.Succeeded)
        {
            return Task.FromResult(new UserResult(UpdateStatus));
        }

        int index = Users.FindIndex(u => u.Id == id);
        UserSummary updated = Users[index] with
        {
            DisplayName = request.DisplayName ?? Users[index].DisplayName,
            Roles = request.Roles ?? Users[index].Roles,
            Disabled = request.Disabled ?? Users[index].Disabled,
        };
        Users[index] = updated;
        return Task.FromResult(new UserResult(IdentityStatus.Succeeded, updated));
    }

    public Task<IdentityResult> DeleteUserAsync(CommandOrigin origin, UserId id, CancellationToken cancellationToken = default)
    {
        Calls.Add($"delete {id}");
        if (DeleteStatus == IdentityStatus.Succeeded)
        {
            Users.RemoveAll(u => u.Id == id);
        }

        return Task.FromResult(new IdentityResult(DeleteStatus));
    }

    public Task<IdentityResult?> ChangePasswordAsync(CommandOrigin origin, ChangePasswordRequest request, SessionId current, CancellationToken cancellationToken = default)
    {
        Calls.Add("password");
        return Task.FromResult<IdentityResult?>(Busy ? null : new IdentityResult(request.CurrentPassword == Password ? IdentityStatus.Succeeded : IdentityStatus.InvalidCredentials));
    }

    public Task<TotpEnrollmentResult?> BeginTotpAsync(CommandOrigin origin, string password, CancellationToken cancellationToken = default)
    {
        Calls.Add("begin");
        return Task.FromResult<TotpEnrollmentResult?>(
            Busy ? null
            : password == Password ? new TotpEnrollmentResult(IdentityStatus.Succeeded, new TotpEnrollmentStarted(new Uri(ProvisioningUri)))
            : new TotpEnrollmentResult(IdentityStatus.InvalidCredentials));
    }

    public Task<RecoveryCodesResult> ConfirmTotpAsync(CommandOrigin origin, string code, CancellationToken cancellationToken = default)
    {
        Calls.Add("confirm");
        return Task.FromResult(code == TotpCode
            ? new RecoveryCodesResult(IdentityStatus.Succeeded, new RecoveryCodes(RecoveryCodes))
            : new RecoveryCodesResult(IdentityStatus.InvalidCredentials));
    }

    public Task<RecoveryCodesResult?> RegenerateRecoveryCodesAsync(CommandOrigin origin, string password, CancellationToken cancellationToken = default)
    {
        Calls.Add("regenerate");
        return Task.FromResult<RecoveryCodesResult?>(password == Password
            ? new RecoveryCodesResult(IdentityStatus.Succeeded, new RecoveryCodes(RecoveryCodes))
            : new RecoveryCodesResult(IdentityStatus.InvalidCredentials));
    }

    public Task<IdentityResult?> DisableTotpAsync(CommandOrigin origin, string password, CancellationToken cancellationToken = default)
    {
        Calls.Add("disable");
        return Task.FromResult<IdentityResult?>(new IdentityResult(password == Password ? IdentityStatus.Succeeded : IdentityStatus.InvalidCredentials));
    }

    public Task<SessionListResult> ListSessionsAsync(CommandOrigin origin, UserId owner, SessionId? current, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SessionListResult(IdentityStatus.Succeeded, [.. Sessions.Select(s => s with { Current = s.Id == current })]));

    public Task<IdentityResult> RevokeSessionAsync(CommandOrigin origin, SessionId id, CancellationToken cancellationToken = default)
    {
        Calls.Add($"revoke {id}");
        Sessions.RemoveAll(s => s.Id == id);
        return Task.FromResult(new IdentityResult(IdentityStatus.Succeeded));
    }

    public Task<IdentityResult> RevokeOtherSessionsAsync(CommandOrigin origin, SessionId keep, CancellationToken cancellationToken = default)
    {
        Calls.Add("revoke others");
        Sessions.RemoveAll(s => s.Id != keep);
        return Task.FromResult(new IdentityResult(IdentityStatus.Succeeded));
    }

    public Task<ApiTokenListResult> ListTokensAsync(CommandOrigin origin, UserId? owner = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ApiTokenListResult(IdentityStatus.Succeeded, [.. Tokens.Where(t => t.UserId == (owner ?? origin.Actor.UserId))]));

    public Task<ApiTokenResult> IssueTokenAsync(CommandOrigin origin, IssueApiTokenRequest request, CancellationToken cancellationToken = default)
    {
        Issued.Add(request);
        ApiTokenSummary summary = new(ApiTokenId.New(), origin.Actor.UserId!.Value, request.Name, request.Scopes, request.ExpiresAt, DateTimeOffset.UtcNow, null, false);
        Tokens.Add(summary);
        return Task.FromResult(new ApiTokenResult(IdentityStatus.Succeeded, new IssuedApiToken(summary, TokenSecret)));
    }

    public Task<ApiTokenResult> RevokeTokenAsync(CommandOrigin origin, ApiTokenId id, CancellationToken cancellationToken = default)
    {
        int index = Tokens.FindIndex(t => t.Id == id);
        Tokens[index] = Tokens[index] with { Revoked = true };
        return Task.FromResult(new ApiTokenResult(IdentityStatus.Succeeded, Token: Tokens[index]));
    }

    public IDisposable Watch(Func<Task> onChanged)
    {
        _watchers.Add(onChanged);
        return new Unwatch(() => _watchers.Remove(onChanged));
    }

    private sealed class Unwatch(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

/// <summary>The audit seam in memory: a list of events, newest first, filtered like the service, and a verification answer.</summary>
internal sealed class FakeDashboardAudit : IDashboardAudit
{
    public List<AuditEventSummary> Events { get; } = [];

    public List<AuditFilter> Queries { get; } = [];

    public AuditVerification Verification { get; set; } = new(true, 0);

    public int Verifications { get; private set; }

    /// <summary>Every query throws, as a busy or briefly unreachable database does.</summary>
    public bool Failing { get; set; }

    public Task<AuditPage> QueryAsync(AuditFilter filter, int limit, AuditEventId? after, CancellationToken cancellationToken = default)
    {
        Queries.Add(filter);
        if (Failing)
        {
            throw new InvalidOperationException("database is locked");
        }

        AuditEventSummary[] matching =
        [
            .. Events
                .Where(e => after is null || e.Id < after.Value)
                .Where(e => filter.Outcome is null || e.Outcome == filter.Outcome)
                .Where(e => filter.Action is null || (filter.Action.EndsWith('*') ? e.Action.StartsWith(filter.Action[..^1], StringComparison.Ordinal) : e.Action == filter.Action))
                .Where(e => filter.Target is null || e.Target == filter.Target)
                .OrderByDescending(e => e.Id),
        ];
        AuditEventSummary[] page = [.. matching.Take(limit)];
        return Task.FromResult(new AuditPage(page, matching.Length > limit ? page[^1].Id : null));
    }

    public Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken = default)
    {
        Verifications++;
        return Task.FromResult(Verification);
    }
}

/// <summary>A bUnit context for the settings, account and audit pages: the config pages' setup plus the identity and audit fakes.</summary>
internal static class IdentityPages
{
    public static (FakeDashboardIdentity Identity, FakeDashboardAudit Audit, FakeDashboardConfig Config, DashboardPrincipal User) SignIn(DashboardTestContext context, Role role)
    {
        // Registered first: the config pages' setup ends by setting the renderer up, which builds the service provider.
        FakeDashboardIdentity identity = new();
        FakeDashboardAudit audit = new();
        context.Services.AddSingleton<IDashboardIdentity>(identity);
        context.Services.AddSingleton<IDashboardAudit>(audit);
        context.Services.AddScoped<DashboardBrowser>();
        FakeDashboardConfig config = ConfigPages.SignIn(context, role, out SessionIdentity session);
        DashboardPrincipal user = new(session, RolePermissions.Of(session.Session.Roles));
        identity.Users.Add(new UserSummary(
            user.Session.Session.UserId, user.Session.Session.Username, user.Session.Session.DisplayName, user.Session.Session.Roles,
            false, false, false, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow));
        return (identity, audit, config, user);
    }
}
