using System.Text.Json.Serialization;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Commands;

/// <summary>
/// A change to the config, as every surface (dashboard, API, MCP, CLI, YAML import) hands it to the
/// command pipeline. Who sent it is not part of the command: the surface passes the authenticated origin alongside.
/// </summary>
/// <remarks>
/// <para><b>Object commands</b> edit one top-level object (a host, upstream or profile, or the global settings) and name
/// the <c>Version</c> they edited: the object's own <c>Version</c> field for an upsert, 0 to create, and the
/// <c>Version</c> parameter for a delete. They conflict only when that object changed since, so two admins editing
/// different hosts never conflict. The version is the pipeline's: whatever a command sends, the stored object gets
/// the version the pipeline assigns.</para>
/// <para><b>Whole-document commands</b> (<see cref="ReplaceConfigCommand"/>, <see cref="RollbackConfigCommand"/>)
/// name the revision they were based on, which must still be the latest.</para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "command")]
[JsonDerivedType(typeof(UpsertHostCommand), "upsertHost")]
[JsonDerivedType(typeof(DeleteHostCommand), "deleteHost")]
[JsonDerivedType(typeof(UpsertUpstreamCommand), "upsertUpstream")]
[JsonDerivedType(typeof(DeleteUpstreamCommand), "deleteUpstream")]
[JsonDerivedType(typeof(UpsertProfileCommand), "upsertProfile")]
[JsonDerivedType(typeof(DeleteProfileCommand), "deleteProfile")]
[JsonDerivedType(typeof(UpdateSettingsCommand), "updateSettings")]
[JsonDerivedType(typeof(ReplaceConfigCommand), "replaceConfig")]
[JsonDerivedType(typeof(RollbackConfigCommand), "rollbackConfig")]
public abstract record ConfigCommand
{
    /// <summary>Run every check (authorize, apply, validate, compile) and return the diff, but persist nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Do the change even though it cuts the caller's own way into the admin. When a command arrives through the
    /// admin's internal listener, a change that deletes the admin host, disables it or removes its HTTPS is refused unless
    /// this is set (the API's <c>?force=true</c>, a command's <c>force</c>); on any other surface it has no effect.
    /// </summary>
    public bool Force { get; init; }

    /// <summary>An optional note stored with the revision (a commit message). At most 1000 characters.</summary>
    public string? Message { get; init; }
}

/// <summary>Creates a host (<see cref="HostConfig.Version"/> 0) or replaces one, routes included.</summary>
/// <param name="Host">The host as it should be. Its <see cref="HostConfig.Version"/> is the version edited.</param>
public sealed record UpsertHostCommand(HostConfig Host) : ConfigCommand;

/// <summary>Deletes a host and its routes.</summary>
/// <param name="HostId">The host.</param>
/// <param name="Version">The version the caller saw.</param>
public sealed record DeleteHostCommand(HostId HostId, long Version) : ConfigCommand;

/// <summary>Creates an upstream (<see cref="UpstreamConfig.Version"/> 0) or replaces one, destinations included.</summary>
/// <param name="Upstream">The upstream as it should be. Its <see cref="UpstreamConfig.Version"/> is the version edited.</param>
public sealed record UpsertUpstreamCommand(UpstreamConfig Upstream) : ConfigCommand;

/// <summary>Deletes an upstream and its destinations; no route may still use it.</summary>
/// <param name="UpstreamId">The upstream.</param>
/// <param name="Version">The version the caller saw.</param>
public sealed record DeleteUpstreamCommand(UpstreamId UpstreamId, long Version) : ConfigCommand;

/// <summary>Creates a profile (<see cref="ProfileConfig.Version"/> 0) or replaces one.</summary>
/// <param name="Profile">The profile as it should be. Its <see cref="ProfileConfig.Version"/> is the version edited.</param>
public sealed record UpsertProfileCommand(ProfileConfig Profile) : ConfigCommand;

/// <summary>Deletes a profile; no host may still use it.</summary>
/// <param name="ProfileId">The profile.</param>
/// <param name="Version">The version the caller saw.</param>
public sealed record DeleteProfileCommand(ProfileId ProfileId, long Version) : ConfigCommand;

/// <summary>Replaces the global settings (there is always exactly one; the empty config has version 0).</summary>
/// <param name="Settings">The settings as they should be. Their <see cref="GlobalSettings.Version"/> is the version edited.</param>
public sealed record UpdateSettingsCommand(GlobalSettings Settings) : ConfigCommand;

/// <summary>
/// Replaces the whole document: the import path. The versions in <paramref name="Document"/> are ignored:
/// an object whose config doesn't change keeps its version, every other one gets a new one.
/// </summary>
/// <param name="Document">The config as it should be.</param>
/// <param name="BaseRevision">The revision the document was based on: must be the latest. Null only while there is
/// no revision yet.</param>
public sealed record ReplaceConfigCommand(ConfigDocument Document, ConfigRevisionId? BaseRevision = null) : ConfigCommand;

/// <summary>
/// Restores the config of an earlier revision as a <b>new</b> revision; history is never rewritten. The
/// default <see cref="ConfigCommand.Message"/> is "Rollback to #N".
/// </summary>
/// <param name="Target">The revision whose config to restore.</param>
/// <param name="BaseRevision">The latest revision as the caller saw it: must still be the latest.</param>
public sealed record RollbackConfigCommand(ConfigRevisionId Target, ConfigRevisionId BaseRevision) : ConfigCommand;
