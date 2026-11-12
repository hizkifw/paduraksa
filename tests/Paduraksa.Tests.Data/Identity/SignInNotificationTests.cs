using System.Data.Common;
using System.Net;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Notifications;
using Paduraksa.Core.Model;
using static Paduraksa.Tests.Data.Identity.IdentityHarness;

namespace Paduraksa.Tests.Data.Identity;

/// <summary>The two sign-in anomalies that are reported: an account locked by failures, and an Owner or Admin signing in from an address not seen in 30 days.</summary>
public sealed class SignInNotificationTests
{
    private static readonly ClientContext Elsewhere = new(Channel.Dashboard, IPAddress.Parse("198.51.100.7"), "other/1.0");

    [Fact]
    public async Task TheFirstSignInEverIsNotAnAnomalyEvenFromAnyAddress()
    {
        await using var h = await CreateAsync();
        await h.CreateUserAsync("alice", Role.Owner);

        await h.SignInAsync("alice");

        Assert.Empty(h.Events.Published);
    }

    [Fact]
    public async Task AnOwnerOrAdminFromAnAddressNotSeenInThirtyDaysIsReportedOnceAndThenKnown()
    {
        await using var h = await CreateAsync();
        var alice = await h.CreateUserAsync("alice", Role.Admin);
        await h.SignInAsync("alice");

        var other = await h.Login.LoginAsync("alice", Password, Elsewhere, Ct);
        var again = await h.Login.LoginAsync("alice", Password, Elsewhere, Ct);
        var home = await h.Login.LoginAsync("alice", Password, h.Client, Ct);

        Assert.All(new[] { other, again, home }, r => Assert.Equal(LoginStatus.Succeeded, r.Status));
        var e = Assert.Single(h.Events.Published);
        Assert.Equal(NotificationEventKind.SignInNewAddress, e.Kind);
        Assert.Equal("alice signed in from a new address", e.Title);
        Assert.Contains("198.51.100.7", e.Body, StringComparison.Ordinal);
        Assert.Contains(new KeyValuePair<string, string>("userId", alice.Id.ToString()), e.Data!);
    }

    [Fact]
    public async Task AnAddressLastSeenMoreThanThirtyDaysAgoIsNewAgain()
    {
        await using var h = await CreateAsync();
        await h.CreateUserAsync("alice", Role.Owner);
        await h.SignInAsync("alice");
        _ = await h.Login.LoginAsync("alice", Password, Elsewhere, Ct);
        Assert.Single(h.Events.Published);

        h.Time.Advance(TimeSpan.FromDays(31));
        _ = await h.Login.LoginAsync("alice", Password, Elsewhere, Ct);

        Assert.Equal(2, h.Events.Published.Count);
    }

    [Fact]
    public async Task OtherRolesAreNotWatched()
    {
        await using var h = await CreateAsync();
        await h.CreateUserAsync("vera", Role.Viewer);
        await h.CreateUserAsync("olive", Role.Operator);
        await h.SignInAsync("vera");
        await h.SignInAsync("olive");

        _ = await h.Login.LoginAsync("vera", Password, Elsewhere, Ct);
        _ = await h.Login.LoginAsync("olive", Password, Elsewhere, Ct);

        Assert.Empty(h.Events.Published);
    }

    [Fact]
    public async Task APasswordThatWasNeverFollowedByTheSecondFactorDoesNotMakeAnAddressKnown()
    {
        await using var h = await CreateAsync();
        var alice = await h.CreateUserAsync("alice", Role.Owner);
        await h.SignInAsync("alice");
        await EnableSecondFactorAsync(h, alice);

        // Someone with the password signs in from a new address but never gets past the second factor.
        var partial = await h.Login.LoginAsync("alice", Password, Elsewhere, Ct);
        Assert.Equal(LoginStatus.SecondFactorRequired, partial.Status);
        Assert.Empty(h.Events.Published);

        // The owner later completes it from that address: still new, because the earlier password step was never a completed sign-in.
        var second = await h.Login.CompleteSecondFactorAsync(partial.Session!.CookieValue, h.Totp.CodeFor(h.Totp.SecretIds[0]), null, Elsewhere, Ct);
        Assert.Equal(LoginStatus.Succeeded, second.Status);
        Assert.Equal(NotificationEventKind.SignInNewAddress, Assert.Single(h.Events.Published).Kind);
    }

    [Fact]
    public async Task AnAccountLockedByFailuresIsReportedOnceWithTheTimeItEnds()
    {
        await using var h = await CreateAsync();
        var alice = await h.CreateUserAsync("alice", Role.Admin);

        for (var i = 0; i < h.Options.MaxFailedAttempts + 2; i++)
        {
            _ = await h.Login.LoginAsync("alice", "wrong password entirely", h.Client, Ct);
        }

        var e = Assert.Single(h.Events.Published);
        Assert.Equal((NotificationEventKind.SignInLockout, "Account alice is locked"), (e.Kind, e.Title));
        Assert.Contains("refused until 12:15 UTC", e.Body, StringComparison.Ordinal);
        Assert.Contains(new KeyValuePair<string, string>("userId", alice.Id.ToString()), e.Data!);
    }

    [Fact]
    public async Task AWrongNameOrAFewWrongPasswordsReportNothing()
    {
        await using var h = await CreateAsync();
        await h.CreateUserAsync("alice");

        _ = await h.Login.LoginAsync("nobody", "wrong password entirely", h.Client, Ct);
        _ = await h.Login.LoginAsync("alice", "wrong password entirely", h.Client, Ct);

        Assert.Empty(h.Events.Published);
    }

    [Fact]
    public async Task TheAddressCheckSeeksTheActorActionTimestampIndexAndNeverScansTheAuditTable()
    {
        await using var h = await CreateAsync();
        var user = (await h.CreateUserAsync("alice", Role.Admin)).Id;
        await h.SignInAsync("alice");
        PlanCapture capture = new();
        SignInWatch watch = new(h.Events, new NotificationOptions(), h.Time);

        await using (var context = h.Db.NewContext(capture))
        {
            Assert.False(await watch.IsNewAddressAsync(context, user, h.Client.ClientIp, Ct));
            Assert.True(await watch.IsNewAddressAsync(context, user, Elsewhere.ClientIp, Ct));
        }

        var audit = capture.Plans.Where(plan => plan.Sql.Contains("AuditEvents", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(audit);
        Assert.All(audit, plan =>
        {
            Assert.Contains("IX_AuditEvents_ActorUserId_Action_Timestamp", plan.Plan, StringComparison.Ordinal);
            Assert.DoesNotContain("SCAN AuditEvents", plan.Plan, StringComparison.Ordinal);
        });
    }

    private sealed class PlanCapture : DbCommandInterceptor
    {
        public List<(string Sql, string Plan)> Plans { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            using var explain = command.Connection!.CreateCommand();
            explain.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
            foreach (DbParameter parameter in command.Parameters)
            {
                var copy = explain.CreateParameter();
                copy.ParameterName = parameter.ParameterName;
                copy.Value = parameter.Value;
                explain.Parameters.Add(copy);
            }

            List<string> lines = [];
            using (var reader = explain.ExecuteReader())
            {
                while (reader.Read())
                {
                    lines.Add(reader.GetString(reader.FieldCount - 1));
                }
            }

            Plans.Add((command.CommandText, string.Join("\n", lines)));
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReaderExecuting(command, eventData, result));
    }

    private static async Task EnableSecondFactorAsync(IdentityHarness h, Paduraksa.Contracts.Identity.UserSummary user)
    {
        var origin = h.As(user);
        var begin = await h.TwoFactor.BeginEnrollmentAsync(origin, Password, Ct);
        Assert.Equal(IdentityStatus.Succeeded, begin.Status);
        var confirm = await h.TwoFactor.ConfirmEnrollmentAsync(origin, h.Totp.CodeFor(h.Totp.SecretIds[0]), Ct);
        Assert.Equal(IdentityStatus.Succeeded, confirm.Status);
        h.Totp.Rotate(h.Totp.SecretIds[0]);
    }
}
