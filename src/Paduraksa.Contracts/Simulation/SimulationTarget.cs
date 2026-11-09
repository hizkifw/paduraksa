using System.Text.Json.Serialization;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Json;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Simulation;

/// <summary>Which config the simulator runs the request against (PLAN §7.7).</summary>
[JsonConverter(typeof(ConfigEnumConverter<SimulationTargetKind>))]
public enum SimulationTargetKind
{
    /// <summary>The snapshot serving traffic now.</summary>
    Live,

    /// <summary>A stored revision, compiled the normal way; never published.</summary>
    Revision,

    /// <summary>A whole document that isn't stored.</summary>
    DraftDocument,

    /// <summary>A pending command applied to the current config, through the dry-run path.</summary>
    DraftCommand,
}

/// <summary>
/// The config to simulate against: the live snapshot, a stored revision, or a draft (a document or a pending command)
/// that is validated and compiled but never stored or published.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LiveSimulationTarget), "live")]
[JsonDerivedType(typeof(RevisionSimulationTarget), "revision")]
[JsonDerivedType(typeof(DraftDocumentSimulationTarget), "draftDocument")]
[JsonDerivedType(typeof(DraftCommandSimulationTarget), "draftCommand")]
public abstract record SimulationTarget
{
    /// <summary>The live snapshot.</summary>
    public static SimulationTarget Live { get; } = new LiveSimulationTarget();
}

/// <summary>The snapshot serving traffic now.</summary>
public sealed record LiveSimulationTarget : SimulationTarget;

/// <summary>A stored revision.</summary>
/// <param name="Revision">The revision.</param>
public sealed record RevisionSimulationTarget(ConfigRevisionId Revision) : SimulationTarget;

/// <summary>A whole document. Validated and compiled like an import; its versions are not looked at.</summary>
/// <param name="Document">The config to try.</param>
public sealed record DraftDocumentSimulationTarget(ConfigDocument Document) : SimulationTarget;

/// <summary>A pending command, applied to the current config and validated and compiled as its dry run would. Whatever
/// <see cref="ConfigCommand.DryRun"/> says, nothing is stored or published.</summary>
/// <param name="Command">The command.</param>
public sealed record DraftCommandSimulationTarget(ConfigCommand Command) : SimulationTarget;
