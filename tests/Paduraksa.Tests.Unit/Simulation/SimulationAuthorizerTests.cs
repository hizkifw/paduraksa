using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Simulation;

public sealed class SimulationAuthorizerTests
{
    public static TheoryData<SimulationTarget, Permission[]> Targets => new()
    {
        { SimulationTarget.Live, [Permission.Simulate] },
        { new RevisionSimulationTarget(ConfigRevisionId.New()), [Permission.Simulate, Permission.ConfigRead] },
        { new DraftDocumentSimulationTarget(new ConfigDocument()), [Permission.Simulate, Permission.ConfigRead] },
        { new DraftCommandSimulationTarget(new Paduraksa.Contracts.Commands.DeleteHostCommand(HostId.New(), 1)), [Permission.Simulate, Permission.ConfigRead] },
        { new DraftSimulationTarget(DraftId.New()), [Permission.Simulate, Permission.ConfigWrite] },
    };

    [Theory]
    [MemberData(nameof(Targets))]
    public void ATargetNeedsSimulateAndForDraftsAndRevisionsConfigRead(SimulationTarget target, Permission[] needed) =>
        Assert.Equal(needed, OperationPermissions.ForSimulation(target));
}
