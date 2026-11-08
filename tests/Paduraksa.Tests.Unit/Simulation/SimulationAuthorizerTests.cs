using System.Net;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Simulation;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Simulation;

/// <summary>Who may simulate (PLAN §7.7, §10.1): <c>simulate</c> for any target, plus <c>config:read</c> for a draft or a revision.</summary>
public sealed class SimulationAuthorizerTests
{
    private static readonly CommandOrigin User = new(Actor.ForUser(UserId.New()), Channel.Api, IPAddress.Loopback);

    private static readonly SimulationTarget[] Targets =
    [
        SimulationTarget.Live,
        new RevisionSimulationTarget(ConfigRevisionId.New()),
        new DraftDocumentSimulationTarget(new ConfigDocument()),
        new DraftCommandSimulationTarget(new Paduraksa.Contracts.Commands.DeleteHostCommand(HostId.New(), 1)),
    ];

    [Theory]
    [InlineData(new[] { Permission.Simulate }, new[] { true, false, false, false })]
    [InlineData(new[] { Permission.ConfigRead }, new[] { false, false, false, false })]
    [InlineData(new[] { Permission.Simulate, Permission.ConfigRead }, new[] { true, true, true, true })]
    [InlineData(new Permission[0], new[] { false, false, false, false })]
    public async Task ATargetNeedsSimulateAndForDraftsAndRevisionsConfigRead(Permission[] held, bool[] expected)
    {
        PermissionSimulationAuthorizer authorizer = new(new Fixed(held));

        for (int i = 0; i < Targets.Length; i++)
        {
            Assert.Equal(expected[i], await authorizer.IsAllowedAsync(Targets[i], User, TestContext.Current.CancellationToken));
        }
    }

    private sealed class Fixed(Permission[] held) : IPermissionEvaluator
    {
        public ValueTask<IReadOnlySet<Permission>> GetPermissionsAsync(Actor actor, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlySet<Permission>>(held.ToHashSet());

        public ValueTask<bool> IsAllowedAsync(CommandOrigin origin, Permission permission, CancellationToken cancellationToken) =>
            ValueTask.FromResult(held.Contains(permission));
    }
}
