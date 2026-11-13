using Paduraksa.Application.Commands;
using Paduraksa.Application.Config;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Application.Yaml;

/// <summary>What an import is asked to do besides its mode.</summary>
/// <param name="DryRun">Run every check and return the diff and the warnings; commit nothing.</param>
/// <param name="BaseRevision">The revision the YAML was prepared against: the import conflicts if it is no longer the latest. Null: the latest as the import reads it.</param>
/// <param name="Force">The admin lock-out guard's override (<see cref="ConfigCommand.Force"/>).</param>
/// <param name="Message">The revision's note; null: "YAML import (merge)" or "(replace)".</param>
public sealed record ConfigImportOptions(bool DryRun = false, ConfigRevisionId? BaseRevision = null, bool Force = false, string? Message = null);

/// <summary>What an import would commit.</summary>
/// <param name="Command">The command to review (a dry run of it) and run; null when there is none, see <paramref name="Errors"/> and <paramref name="Denied"/>.</param>
/// <param name="Errors">Why there is no command: errors that point into the YAML, with their lines.</param>
/// <param name="Denied">The caller may not change the config.</param>
public sealed record ConfigImportPreparation(ReplaceConfigCommand? Command, IReadOnlyList<ConfigError>? Errors = null, bool Denied = false)
{
    public IReadOnlyList<ConfigError> Errors { get; init; } = Errors ?? [];
}

/// <summary>A config as YAML, with the revision it is.</summary>
/// <param name="Yaml">The text.</param>
/// <param name="Revision">Its revision; null for the empty config, which has none.</param>
public sealed record ConfigExport(string Yaml, RevisionRef? Revision);

/// <summary>
/// YAML import and export on the config pipeline (docs/design/admin-surfaces.md "YAML import and export"): what the API, the CLI and the dashboard call. An import reads the YAML under the <see cref="YamlLimits"/>, maps it onto the
/// document (<see cref="ConfigYamlMapper"/>), builds what the config would be (<see cref="ConfigImportMerge"/>) and sends it through <see cref="ConfigService"/> as one
/// <see cref="ReplaceConfigCommand"/>, so validation, compilation, the lock-out guard, the diff, the audit and the single revision are the pipeline's; this class adds only what the
/// pipeline can't know about YAML.
/// </summary>
/// <remarks>
/// <para><b>Errors point into the YAML.</b> The pipeline's errors point into the document it was sent; they are mapped back to the entry that wrote the object (and the key that wrote
/// the field, with its line), and an error in a stored object the import left alone (a merge) is a related error, still pointing into the document.</para>
/// <para><b>Credentials.</b> A user's <c>credentialId</c> must be one the vault holds a password under, or one the live config already names (the vault lost it, which the pipeline
/// reports as a warning). Otherwise the import is refused, and a dry run reports it as an error too, so a dry run says what the import would do. The check is only for callers who may write
/// config: it would tell anyone else which credential IDs exist.</para>
/// <para><b>One revision.</b> The import reads the config, builds the document on it and names it as the base revision: a change that lands in between makes the import conflict instead of
/// overwriting it. A dry run changes nothing and never audits, as with every command.</para>
/// </remarks>
public sealed class ConfigImportService
{
    private readonly ConfigService _config;
    private readonly RevisionQueryService _revisions;
    private readonly IPasswordCredentials _credentials;
    private readonly IPermissionEvaluator _permissions;
    private readonly Func<Type, string> _newId;
    private readonly YamlLimits _limits;

    public ConfigImportService(ConfigService config, RevisionQueryService revisions, IPasswordCredentials credentials, IPermissionEvaluator permissions)
        : this(config, revisions, credentials, permissions, ConfigYamlImporter.RandomIds, YamlLimits.Default)
    {
    }

    internal ConfigImportService(
        ConfigService config, RevisionQueryService revisions, IPasswordCredentials credentials, IPermissionEvaluator permissions, Func<Type, string> newId, YamlLimits limits)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(revisions);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(permissions);
        _config = config;
        _revisions = revisions;
        _credentials = credentials;
        _permissions = permissions;
        _newId = newId;
        _limits = limits;
    }

    /// <summary>The largest YAML an import reads, in bytes (<see cref="YamlLimits.MaxBytes"/>): what the API's body limit should be.</summary>
    public static int MaxBytes => YamlLimits.Default.MaxBytes;

    /// <summary>The live config, or the revision <paramref name="revision"/>, as YAML; null when there is no such revision. Whoever calls this has checked <see cref="Permission.ConfigRead"/>, as for every read.</summary>
    public async Task<ConfigExport?> ExportAsync(ConfigRevisionId? revision = null, CancellationToken cancellationToken = default)
    {
        if (revision is { } id)
        {
            return await _revisions.GetAsync(id, cancellationToken).ConfigureAwait(false) is { } detail
                ? new ConfigExport(ConfigYamlExporter.Export(CanonicalConfigJson.Canonicalize(detail.Document)), new RevisionRef(detail.Revision.Id, detail.Revision.Seq))
                : null;
        }

        var current = await _config.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return new ConfigExport(ConfigYamlExporter.Export(CanonicalConfigJson.Canonicalize(current.Document)), current.Revision);
    }

    /// <summary>
    /// What an import would commit, without running it: the command (a dry run of it is the review a page shows, and running it is the import), or why there is none.
    /// A credential the vault lacks is an error here, as in <see cref="ImportAsync"/>.
    /// </summary>
    public async Task<ConfigImportPreparation> PrepareAsync(
        ReadOnlyMemory<byte> yaml, ConfigImportMode mode, CommandOrigin origin, ConfigImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var prepared = await PrepareCoreAsync(yaml, mode, origin, options ?? new ConfigImportOptions(), cancellationToken).ConfigureAwait(false);
        return new ConfigImportPreparation(
            prepared.Denied ? null : prepared.CredentialErrors.Count > 0 ? null : prepared.Command,
            [.. prepared.Errors, .. prepared.CredentialErrors],
            prepared.Denied);
    }

    /// <summary>Imports <paramref name="yaml"/> in <paramref name="mode"/> and says how it ended, like any command; <see cref="ConfigCommandStatus.Invalid"/> carries errors that point into the YAML.</summary>
    public async Task<ConfigCommandResult> ImportAsync(
        ReadOnlyMemory<byte> yaml, ConfigImportMode mode, CommandOrigin origin, ConfigImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        options ??= new ConfigImportOptions();
        var prepared = await PrepareCoreAsync(yaml, mode, origin, options, cancellationToken).ConfigureAwait(false);
        if (prepared.Denied)
        {
            return new ConfigCommandResult(ConfigCommandStatus.Denied);
        }

        if (prepared.Command is null || prepared.Plan is null || prepared.Mapped is null)
        {
            return new ConfigCommandResult(ConfigCommandStatus.Invalid, Errors: prepared.Errors);
        }

        if (prepared.CredentialErrors.Count > 0 && !options.DryRun)
        {
            return new ConfigCommandResult(ConfigCommandStatus.Invalid, Errors: prepared.CredentialErrors);
        }

        var result = await _config.ExecuteAsync(prepared.Command, origin, cancellationToken).ConfigureAwait(false);
        result = PointIntoYaml(result, prepared.Plan, prepared.Mapped);
        return prepared.CredentialErrors.Count == 0
            ? result
            : result with { Status = ConfigCommandStatus.Invalid, Errors = [.. prepared.CredentialErrors, .. result.Errors] };
    }

    private sealed record Prepared(
        bool Denied, ReplaceConfigCommand? Command, ImportPlan? Plan, MappedYaml? Mapped, IReadOnlyList<ConfigError> Errors, IReadOnlyList<ConfigError> CredentialErrors);

    private async Task<Prepared> PrepareCoreAsync(
        ReadOnlyMemory<byte> yaml, ConfigImportMode mode, CommandOrigin origin, ConfigImportOptions options, CancellationToken cancellationToken)
    {
        if (!await _permissions.IsAllowedAsync(origin, OperationPermissions.ForConfigCommand, cancellationToken).ConfigureAwait(false))
        {
            return new Prepared(true, null, null, null, [], []);
        }

        var current = await _config.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var read = ConfigYamlImporter.Read(yaml.Span, current.Document, mode, _newId, _limits);
        if (read.Mapped is not { } mapped)
        {
            return new Prepared(false, null, null, null, read.Errors, []);
        }

        var plan = mode == ConfigImportMode.Merge ? ConfigImportMerge.Merge(current.Document, mapped) : ConfigImportMerge.Replace(mapped);
        ReplaceConfigCommand command = new(plan.Document, options.BaseRevision ?? current.Revision?.Id)
        {
            DryRun = options.DryRun,
            Force = options.Force,
            Message = string.IsNullOrWhiteSpace(options.Message) ? $"YAML import ({(mode == ConfigImportMode.Merge ? "merge" : "replace")})" : options.Message,
        };
        return new Prepared(false, command, plan, mapped, [], UnknownCredentials(current.Document, mapped));
    }

    private List<ConfigError> UnknownCredentials(ConfigDocument stored, MappedYaml imported)
    {
        var known = AccessListPasswords.Referenced(stored);
        List<ConfigError> errors = [];
        for (var i = 0; i < imported.Document.AccessLists.Count; i++)
        {
            var users = imported.Document.AccessLists[i].BasicAuth?.Users ?? [];
            for (var j = 0; j < users.Count; j++)
            {
                if (users[j].CredentialId is { IsEmpty: false } id && !known.Contains(id) && !_credentials.Open(id).IsSet)
                {
                    var pointer = $"/accessLists/{i}/basicAuth/users/{j}/credentialId";
                    errors.Add(Located(imported, pointer, new ConfigError(
                        pointer,
                        YamlErrorCodes.CredentialUnknown,
                        "The vault holds no password under this credential. Set the user's password in the dashboard, then name its credentialId (or leave credentialId out to keep the list's user of that name).")));
                }
            }
        }

        return errors;
    }

    // The pipeline's pointers index the document it was sent (validation) or its canonical form (the compiler's backstop); the YAML's are the entry the object came from.
    private static ConfigCommandResult PointIntoYaml(ConfigCommandResult result, ImportPlan plan, MappedYaml imported)
    {
        if (result.Errors.Count == 0)
        {
            return result;
        }

        var canonicalToSent = CommandErrors.IndexMap.Between(CanonicalConfigJson.Canonicalize(plan.Document), plan.Document);
        List<ConfigError> own = [];
        List<ConfigError> related = [.. result.RelatedErrors];
        foreach (var error in result.Errors)
        {
            var sent = error.Code == ConfigErrorCodes.CompileFailed ? canonicalToSent.Remap(error.Path) : error.Path;
            if (plan.ToYaml(sent) is { } yaml)
            {
                var pointer = YamlPointers.FromDocument(yaml);
                own.Add(Located(imported, pointer, error with { Path = pointer }));
            }
            else
            {
                related.Add(error with { Path = sent });
            }
        }

        return result with { Errors = own, RelatedErrors = related };
    }

    private static ConfigError Located(MappedYaml imported, string pointer, ConfigError error) =>
        imported.Positions.TryGetValue(pointer, out var position) && position.Line > 0
            ? error with { Message = $"{error.Message} (line {position.Line}, column {position.Column})" }
            : error;
}
