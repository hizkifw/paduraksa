using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Data.Commands;
using Paduraksa.Tests.Data.Config;

using static Paduraksa.Tests.Data.Identity.IdentityHarness;

namespace Paduraksa.Tests.Data.Identity;

public sealed class PermissionTests
{
    private static UpsertHostCommand NewHost(string name) => new(Objects.RespondHost(HostId.New(), name));

    [Theory]
    [InlineData(Role.Owner, true)]
    [InlineData(Role.Admin, true)]
    [InlineData(Role.Operator, true)]
    [InlineData(Role.Viewer, false)]
    [InlineData(Role.Auditor, false)]
    public async Task OnlyRolesWithConfigWriteMayRunConfigCommands(Role role, bool allowed)
    {
        var late = new LateAuthorizer();
        await using var config = await ConfigServiceHarness.CreateAsync(authorizer: late);
        await using var h = Over(config.Db);
        late.Inner = h.Get<IConfigCommandAuthorizer>();
        var user = await h.CreateUserAsync("someone", role);

        var result = await config.RunAsync(NewHost("app.example.com"), h.As(user));

        Assert.Equal(allowed ? ConfigCommandStatus.Committed : ConfigCommandStatus.Denied, result.Status);
        if (!allowed)
        {
            var audit = Assert.Single(await config.AuditAsync(), e => e.Outcome == AuditOutcome.Denied);
            Assert.Equal(user.Id, audit.Actor.UserId);
        }
    }

    [Fact]
    public async Task SystemAndTheCliMayAlwaysAndAnUnknownUserNeverMay()
    {
        var late = new LateAuthorizer();
        await using var config = await ConfigServiceHarness.CreateAsync(authorizer: late);
        await using var h = Over(config.Db);
        late.Inner = h.Get<IConfigCommandAuthorizer>();

        Assert.Equal(ConfigCommandStatus.Committed, (await config.RunAsync(NewHost("a.example.com"), CommandOrigin.System)).Status);
        Assert.Equal(ConfigCommandStatus.Committed, (await config.RunAsync(NewHost("b.example.com"), CommandOrigin.Cli)).Status);
        var stranger = h.Client.ToOrigin(Actor.ForUser(UserId.New()));
        Assert.Equal(ConfigCommandStatus.Denied, (await config.RunAsync(NewHost("c.example.com"), stranger)).Status);
    }

    [Fact]
    public async Task ATokenMayRunACommandOnlyWithTheWriteScopeAndOnlyWhileItsOwnerMay()
    {
        var late = new LateAuthorizer();
        await using var config = await ConfigServiceHarness.CreateAsync(authorizer: late);
        await using var h = Over(config.Db);
        late.Inner = h.Get<IConfigCommandAuthorizer>();
        var operatorUser = await h.CreateUserAsync("operator1", Role.Operator);
        var readOnly = (await h.Tokens.IssueAsync(h.As(operatorUser), new("ro", [Permission.ConfigRead], null), Ct)).Issued!;
        var writer = (await h.Tokens.IssueAsync(h.As(operatorUser), new("rw", [Permission.ConfigRead, Permission.ConfigWrite], null), Ct)).Issued!;

        Assert.Equal(ConfigCommandStatus.Denied, (await config.RunAsync(NewHost("a.example.com"), h.Client.ToOrigin(Actor.ForToken(readOnly.Summary.Id)))).Status);
        Assert.Equal(ConfigCommandStatus.Committed, (await config.RunAsync(NewHost("b.example.com"), h.Client.ToOrigin(Actor.ForToken(writer.Summary.Id)))).Status);

        await h.Users.UpdateAsync(CommandOrigin.Cli, operatorUser.Id, new(null, [Role.Viewer]), Ct);
        Assert.Equal(ConfigCommandStatus.Denied, (await config.RunAsync(NewHost("c.example.com"), h.Client.ToOrigin(Actor.ForToken(writer.Summary.Id)))).Status);
        var audit = (await config.AuditAsync())[^1];
        Assert.Equal((ActorKind.Token, AuditOutcome.Denied), (audit.Actor.Kind, audit.Outcome));
    }

    [Fact]
    public async Task CertificateChangesNeedCertificatesWriteAndRefusalsAreAudited()
    {
        await using var config = await ConfigServiceHarness.CreateAsync();
        await using var h = Over(config.Db);
        var certificates = new CertificateService(config.Db.Factory, config.Service, TimeProvider.System, NullLogger<CertificateService>.Instance, permissions: h.Permissions);
        var viewer = await h.CreateUserAsync("viewer");
        var operatorUser = await h.CreateUserAsync("operator1", Role.Operator);
        var id = CertificateId.New();

        var denied = await certificates.DeleteAsync(id, h.As(viewer), Ct);
        var passed = await certificates.DeleteAsync(id, h.As(operatorUser), Ct);
        var cli = await certificates.DeleteAsync(id, CommandOrigin.Cli, Ct);

        Assert.Equal(CertificateChangeStatus.Denied, denied.Status);
        Assert.Equal(CertificateChangeStatus.NotFound, passed.Status); // past the gate, there is simply no such certificate
        Assert.Equal(CertificateChangeStatus.NotFound, cli.Status);
        var audit = Assert.Single(await config.AuditAsync(), e => e.Outcome == AuditOutcome.Denied);
        Assert.Equal(("certificate.delete", id.ToString(), viewer.Id), (audit.Action, audit.Target, audit.Actor.UserId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheRegistrationReplacesTheAllowAllAuthorizerInEitherOrder(bool identityFirst)
    {
        await using var db = await TestConfigDatabase.CreateAsync();
        var services = new ServiceCollection().AddSingleton(db.Factory).AddSingleton<ITotpAuthenticator>(new FakeTotp());
        if (identityFirst)
        {
            services.AddPaduraksaIdentity("/tmp/unused");
        }

        // Stands in for AddPaduraksaConfigService, which adds its default with TryAdd.
        services.TryAddSingleton<IConfigCommandAuthorizer>(sp => throw new InvalidOperationException("the allow-all default must not win"));
        if (!identityFirst)
        {
            services.AddPaduraksaIdentity("/tmp/unused");
        }

        await using var provider = services.BuildServiceProvider();

        Assert.Equal("PermissionConfigCommandAuthorizer", provider.GetRequiredService<IConfigCommandAuthorizer>().GetType().Name);
    }

    /// <summary>The authorizer is needed to build the config service, whose database the users live in: bound afterwards.</summary>
    private sealed class LateAuthorizer : IConfigCommandAuthorizer
    {
        public IConfigCommandAuthorizer? Inner { get; set; }

        public ValueTask<bool> IsAllowedAsync(ConfigCommand command, CommandOrigin origin, CancellationToken cancellationToken) =>
            Inner!.IsAllowedAsync(command, origin, cancellationToken);
    }
}
