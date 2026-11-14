using System.Text.Json;

using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Identity;

public sealed class RolePermissionTests
{
    [Fact]
    public void OwnerHasEverything() =>
        Assert.Equal(Enum.GetValues<Permission>().Order(), RolePermissions.Of(Role.Owner).Order());

    [Fact]
    public void OnlyOwnerManagesUsersTheVaultAndOtherPeoplesTokensAndSessionsAndMakesBackups()
    {
        // A backup holds every key the vault does, so it is the Owner's alone (docs/design/vault-and-certificates.md "Backup and restore (M10)").
        Permission[] ownerOnly = [Permission.UsersWrite, Permission.VaultManage, Permission.TokensAll, Permission.SessionsAll, Permission.BackupCreate];

        foreach (var role in Enum.GetValues<Role>().Where(r => r != Role.Owner))
        {
            Assert.Empty(RolePermissions.Of(role).Intersect(ownerOnly));
        }
    }

    [Theory]
    [InlineData(Role.Admin, Permission.ConfigWrite, true)]
    [InlineData(Role.Admin, Permission.ProvidersWrite, true)]
    [InlineData(Role.Admin, Permission.AuditRead, true)]
    [InlineData(Role.Admin, Permission.LogsRead, true)]
    [InlineData(Role.Admin, Permission.ServerWrite, true)]
    [InlineData(Role.Operator, Permission.ServerWrite, false)]
    [InlineData(Role.Viewer, Permission.ServerWrite, false)]
    [InlineData(Role.Auditor, Permission.ServerWrite, false)]
    [InlineData(Role.Operator, Permission.ConfigWrite, true)]
    [InlineData(Role.Operator, Permission.CertificatesWrite, true)]
    [InlineData(Role.Operator, Permission.ProvidersWrite, false)]
    [InlineData(Role.Operator, Permission.AuditRead, false)]
    [InlineData(Role.Operator, Permission.LogsRead, false)]
    [InlineData(Role.Operator, Permission.UsersRead, false)]
    [InlineData(Role.Viewer, Permission.ConfigRead, true)]
    [InlineData(Role.Viewer, Permission.Simulate, true)]
    [InlineData(Role.Viewer, Permission.ConfigWrite, false)]
    [InlineData(Role.Viewer, Permission.CertificatesWrite, false)]
    [InlineData(Role.Viewer, Permission.AuditRead, false)]
    [InlineData(Role.Viewer, Permission.LogsRead, false)]
    [InlineData(Role.Auditor, Permission.AuditRead, true)]
    [InlineData(Role.Auditor, Permission.LogsRead, true)]
    [InlineData(Role.Auditor, Permission.ConfigRead, true)]
    [InlineData(Role.Auditor, Permission.ConfigWrite, false)]
    [InlineData(Role.Auditor, Permission.Simulate, false)]
    [InlineData(Role.Admin, Permission.MetricsRead, true)]
    [InlineData(Role.Operator, Permission.MetricsRead, true)]
    [InlineData(Role.Viewer, Permission.MetricsRead, true)]
    [InlineData(Role.Auditor, Permission.MetricsRead, true)]
    public void RolesGrantWhatThePlanSays(Role role, Permission permission, bool granted) =>
        Assert.Equal(granted, RolePermissions.Of(role).Contains(permission));

    [Fact]
    public void EveryRoleMayManageItsOwnTokensAndNoReadOnlyRoleMayWrite()
    {
        Permission[] writes = [Permission.ConfigWrite, Permission.CertificatesWrite, Permission.ProvidersWrite, Permission.UsersWrite, Permission.VaultManage];

        Assert.All(Enum.GetValues<Role>(), r => Assert.Contains(Permission.TokensOwn, RolePermissions.Of(r)));
        Assert.All([Role.Viewer, Role.Auditor], r => Assert.Empty(RolePermissions.Of(r).Intersect(writes)));
    }

    [Fact]
    public void EveryPermissionIsGrantedToSomeRole() =>
        Assert.Empty(Enum.GetValues<Permission>().Except(Enum.GetValues<Role>().SelectMany(r => RolePermissions.Of(r))));

    [Fact]
    public void AUserHoldsTheUnionOfTheirRoles()
    {
        var union = RolePermissions.Of([Role.Viewer, Role.Auditor]);

        Assert.Contains(Permission.Simulate, union);
        Assert.Contains(Permission.AuditRead, union);
        Assert.DoesNotContain(Permission.ConfigWrite, union);
        Assert.Empty(RolePermissions.Of([]));
    }

    [Fact]
    public void ScopesCanOnlyNarrow()
    {
        var owner = RolePermissions.Of(Role.Viewer);

        var token = RolePermissions.Narrow(owner, [Permission.ConfigRead, Permission.ConfigWrite, Permission.UsersWrite]);

        Assert.Equal([Permission.ConfigRead], token);
        Assert.Empty(RolePermissions.Narrow(owner, []));
        Assert.Subset(owner.ToHashSet(), token.ToHashSet());
    }

    [Fact]
    public void UndefinedRolesHaveNoPermissions() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RolePermissions.Of((Role)99));

    [Fact]
    public void NamesArePinnedAndRoundTrip()
    {
        Assert.Equal(["owner", "admin", "operator", "viewer", "auditor"], Enum.GetValues<Role>().Select(r => r.ToName()));
        Assert.Equal(
            [
                "config:read", "config:write", "certificates:read", "certificates:write", "providers:read", "providers:write",
                "users:read", "users:write", "tokens:own", "tokens:all", "sessions:all", "audit:read", "vault:manage", "simulate", "logs:read",
                "metrics:read", "backup:create", "server:write",
            ],
            Enum.GetValues<Permission>().Select(p => p.ToName()));
        Assert.All(Enum.GetValues<Role>(), r => Assert.Equal(r, ModelNames.ParseRole(r.ToName())));
        Assert.All(Enum.GetValues<Permission>(), p => Assert.Equal(p, ModelNames.ParsePermission(p.ToName())));
        Assert.Throws<FormatException>(() => ModelNames.ParseRole("Owner"));
        Assert.Throws<FormatException>(() => ModelNames.ParsePermission("config"));
        Assert.False(ModelNames.TryParsePermission(null, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => ((Permission)0).ToName());
    }

    [Fact]
    public void TheIdentityDtosRoundTripThroughTheirJsonContextWithNamesAndTypedIds()
    {
        var user = new UserSummary(UserId.New(), "alice", "Alice", [Role.Admin, Role.Auditor], true, false, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var token = new ApiTokenSummary(ApiTokenId.New(), user.Id, "ci", [Permission.ConfigRead, Permission.Simulate], null, DateTimeOffset.UnixEpoch, null, false);

        var userJson = JsonSerializer.Serialize(user, IdentityJsonContext.Default.UserSummary);
        var tokenJson = JsonSerializer.Serialize(token, IdentityJsonContext.Default.ApiTokenSummary);

        Assert.Contains("\"id\":\"usr_", userJson, StringComparison.Ordinal);
        Assert.Contains("\"roles\":[\"admin\",\"auditor\"]", userJson, StringComparison.Ordinal);
        Assert.Contains("\"scopes\":[\"config:read\",\"simulate\"]", tokenJson, StringComparison.Ordinal);
        Assert.Contains("\"expiresAt\":null", tokenJson, StringComparison.Ordinal);
        Assert.Equal(user.Id, JsonSerializer.Deserialize(userJson, IdentityJsonContext.Default.UserSummary)!.Id);
        Assert.Equal(token.Scopes, JsonSerializer.Deserialize(tokenJson, IdentityJsonContext.Default.ApiTokenSummary)!.Scopes);
    }

    [Theory]
    [InlineData("{\"username\":\"a\",\"password\":\"b\",\"extra\":1}")]
    [InlineData("{\"username\":\"a\"}")]
    public void ReadingIsStrict(string json) =>
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(json, IdentityJsonContext.Default.LoginRequest));

    [Fact]
    public void BadRoleAndPermissionNamesAreRejected()
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize("{\"displayName\":null,\"roles\":[\"root\"]}", IdentityJsonContext.Default.UpdateUserRequest));
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize("{\"name\":\"x\",\"scopes\":[\"everything\"],\"expiresAt\":null}", IdentityJsonContext.Default.IssueApiTokenRequest));
    }

    [Fact]
    public void RequestsAndSecretsPrintRedacted()
    {
        var token = new IssuedApiToken(
            new ApiTokenSummary(ApiTokenId.New(), UserId.New(), "ci", [Permission.ConfigRead], null, DateTimeOffset.UnixEpoch, null, false), "pdk_SECRET");

        Assert.DoesNotContain("hunter2", new LoginRequest("alice", "hunter2").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", new CreateUserRequest("alice", null, "hunter2", [Role.Viewer]).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", new ChangePasswordRequest("hunter2", "hunter3").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("123456", new SecondFactorRequest("123456", null).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", token.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("abcd", new RecoveryCodes(["abcd-efgh"]).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", new TotpEnrollmentStarted(new Uri("otpauth://totp/x?secret=ABC")).ToString(), StringComparison.OrdinalIgnoreCase);

        // The issuance bodies carry the external-account-binding key and DNS credentials: a stray log line must not print one.
        Assert.DoesNotContain(
            "SECRET",
            new Paduraksa.Contracts.Issuance.CreateAcmeAccountBody("https://ca.test/dir", true, EabKid: "kid", EabHmacKey: "SECRET").ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SECRET",
            new Paduraksa.Contracts.Issuance.CreateDnsProviderBody(Paduraksa.Core.Model.DnsProviderKind.Cloudflare, "cf", "SECRET").ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SECRET",
            new Paduraksa.Contracts.Issuance.RotateDnsCredentialBody("SECRET").ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void NoIssuanceSummaryCarriesAVaultSecretReference()
    {
        // The reference stays on the internal row: a vault SecretId must never reach a response.
        Assert.DoesNotContain(
            typeof(Paduraksa.Contracts.Issuance.AcmeAccountSummary).GetProperties(),
            p => p.PropertyType == typeof(SecretId) || p.PropertyType == typeof(SecretId?));
        Assert.DoesNotContain(
            typeof(Paduraksa.Contracts.Issuance.DnsProviderSummary).GetProperties(),
            p => p.PropertyType == typeof(SecretId) || p.PropertyType == typeof(SecretId?));
    }

    [Fact]
    public void TheAuthKindIsAnEnumWithThreeStableWireNames()
    {
        // It used to be a raw string; the enum pins the three words and serializes them by name like every other wire enum.
        Assert.Equal(["session", "token", "unspecified"], Enum.GetValues<AuthKind>().Select(kind => kind.ToString().ToLowerInvariant()).Order(StringComparer.Ordinal));

        var options = IdentityJsonContext.Default.Options;
        Assert.Equal("\"session\"", JsonSerializer.Serialize(AuthKind.Session, options));
        Assert.Equal("\"token\"", JsonSerializer.Serialize(AuthKind.Token, options));
        Assert.Equal("\"unspecified\"", JsonSerializer.Serialize(AuthKind.Unspecified, options));
        Assert.Equal(AuthKind.Token, JsonSerializer.Deserialize<AuthKind>("\"token\"", options));
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<AuthKind>("\"unknown\"", options));
    }
}
