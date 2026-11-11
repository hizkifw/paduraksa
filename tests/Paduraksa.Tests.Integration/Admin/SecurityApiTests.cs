using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Security;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Integration.Config;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

public sealed class SecurityApiTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("operator")]
    [InlineData("viewer")]
    [InlineData("auditor")]
    public async Task EveryBuiltInRoleReadsTheOverview(string caller)
    {
        using var response = await shared.Api.GetAsync("/api/v1/security/overview", caller);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NoCredentialsIsUnauthorized()
    {
        using var response = await shared.Api.GetAsync("/api/v1/security/overview", caller: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ACallerWithoutConfigReadIsForbidden()
    {
        // Every built-in role reads the config, so the one that doesn't is a permission set that lacks it.
        await using var api = await AdminApiHarness.StartAsync(Ct, adminServices: services =>
        {
            var real = services.Last(d => d.ServiceType == typeof(IPermissionEvaluator)).ImplementationInstance as IPermissionEvaluator
                ?? throw new InvalidOperationException("The shared permission evaluator is an instance.");
            services.AddSingleton<IPermissionEvaluator>(new WithoutConfigRead(real));
        });

        using var response = await api.GetAsync("/api/v1/security/overview");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheOverviewHasTheCountersAndTheLiveHostsProtections()
    {
        var api = shared.Api;
        var host = ConfigTestSupport.RespondHost("security-overview", "security-overview.test", "ok") with
        {
            Settings = new HostSettings { Robots = new RobotsSettings { Mode = RobotsMode.DisallowAll } },
        };
        using var created = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/hosts", host, AdminJsonContext.Default.HostConfig);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var response = await api.GetAsync("/api/v1/security/overview");
        var overview = await AdminApiHarness.ReadAsync(response, AdminJsonContext.Default.SecurityOverview, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(overview.SampledAt >= overview.Since);
        Assert.True(overview.ProofOfWork.ChallengesServed >= 0);
        Assert.True(overview.RateLimit.Allowed >= 0);
        var line = Assert.Single(overview.Hosts, h => h.Id == host.Id);
        Assert.Equal("security-overview.test", line.Name);
        Assert.Equal(RobotsMode.DisallowAll, line.Robots);
        Assert.False(line.ProofOfWork.Enabled);
        Assert.Null(line.RateLimit.Policy);
        Assert.Null(line.AccessList.List);
        Assert.Empty(line.AccessList.Overrides);
    }

    private sealed class WithoutConfigRead(IPermissionEvaluator inner) : IPermissionEvaluator
    {
        public async ValueTask<IReadOnlySet<Permission>> GetPermissionsAsync(Actor actor, CancellationToken cancellationToken) =>
            (await inner.GetPermissionsAsync(actor, cancellationToken)).Where(p => p != Permission.ConfigRead).ToHashSet();

        public async ValueTask<bool> IsAllowedAsync(CommandOrigin origin, Permission permission, CancellationToken cancellationToken) =>
            permission != Permission.ConfigRead && await inner.IsAllowedAsync(origin, permission, cancellationToken);
    }
}
