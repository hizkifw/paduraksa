using System.Text.Json;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Application.Commands;

/// <summary>
/// What the request simulator needs from the config service: configs to compile without publishing them.
/// None of it takes the writer's lock, audits, stores or publishes anything, and none of it asks the vault for a key
/// (<see cref="SimulationCertificateSource"/>).
/// </summary>
public sealed partial class ConfigService
{
    /// <summary>Error code of a stored revision that fails its integrity check.</summary>
    internal const string CorruptRevisionCode = "revision.corrupt";

    /// <summary>
    /// A stored revision's document, or null when there is no such revision. A document this build can't read is
    /// <see cref="StoredDocument.Problem"/>, not an exception.
    /// </summary>
    internal async Task<StoredDocument?> ReadRevisionAsync(ConfigRevisionId id, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            StoredRevision? stored;
            try
            {
                stored = await ConfigRevisionStore.GetAsync(context, id, cancellationToken).ConfigureAwait(false);
            }
            catch (RevisionIntegrityException ex)
            {
                LogRevisionCorrupt(ex, id);
                return new StoredDocument(
                    new RevisionRef(id, 0),
                    null,
                    new ConfigError(
                        string.Empty,
                        CorruptRevisionCode,
                        "The revision fails its integrity check, so it can't be read. The details are in the server log under this response's trace ID."));
            }

            if (stored is null)
            {
                return null;
            }

            var revision = Ref(stored.Info);
            return CanonicalConfigJson.TryDeserialize(stored.Document.Span, out var document, out var error)
                ? new StoredDocument(revision, CanonicalConfigJson.Canonicalize(document), null)
                : new StoredDocument(revision, null, error);
        }
    }

    /// <summary>
    /// Compiles <paramref name="document"/> against the certificate inventory as it is now, with placeholder serving
    /// certificates, and never publishes it. For a stored revision: what the startup compile does with it, minus the vault.
    /// </summary>
    internal async Task<SimulationCompilation> CompileForSimulationAsync(ConfigDocument document, ConfigRevisionId revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var degraded = false;
        CertificateInventory inventory;
        try
        {
            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                inventory = await _simulationCertificates.LoadAsync(context, document, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogInventoryUnreadable(ex);
            degraded = true;
            inventory = await _simulationCertificates.LoadAsync(null, document, cancellationToken).ConfigureAwait(false);
        }

        return new SimulationCompilation(_compiler.Compile(document, revision, inventory), degraded);
    }

    /// <summary>
    /// Runs <paramref name="command"/> through steps 1 to 4 of the write path as
    /// its dry run does, and hands back the compiled candidate instead of discarding it. Nothing is stored, audited or
    /// published, whatever <see cref="ConfigCommand.DryRun"/> says. <paramref name="origin"/> is who is asking (it
    /// decides, as for a real command, whether a binding to an unusable certificate is refused).
    /// </summary>
    /// <remarks>The command authorizer isn't asked: planning a draft needs the permission to read the config, which the
    /// simulator's own authorizer checks. <see cref="ConfigCommandResult.Status"/> is <see cref="ConfigCommandStatus.DryRun"/>
    /// with a candidate, <see cref="ConfigCommandStatus.Unchanged"/> when the command changes nothing, or what rejected it.</remarks>
    internal async Task<DraftPlan> PlanDraftAsync(ConfigCommand command, CommandOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(origin);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var dryRun = command with { DryRun = true };

        // Status is read without the writer's lock: planning writes nothing, so a start that finishes while this runs only
        // means the draft is planned against the store as it becomes usable, and a store that turns unusable fails below.
        if (Status is { AcceptsCommands: false } unusable)
        {
            return new DraftPlan(Unavailable(dryRun, unusable), null);
        }

        // What reading checks and a hand-built payload skips: a null element would be a NullReferenceException in the diff.
        if (Preflight(dryRun) is { } malformed)
        {
            return new DraftPlan(new ConfigCommandResult(ConfigCommandStatus.Invalid, Errors: [malformed]), null);
        }

        var message = string.IsNullOrWhiteSpace(dryRun.Message) ? null : dryRun.Message.Trim();
        if (MessageProblem(message) is { } problem)
        {
            return new DraftPlan(new ConfigCommandResult(ConfigCommandStatus.Invalid, Errors: [new ConfigError(string.Empty, MessageInvalidCode, problem)]), null);
        }

        // A dry run never commits, so the head can't move under it (that is a commit's retry): one attempt.
        try
        {
            var execution = await AttemptAsync(dryRun, origin, Describe(dryRun), message, cancellationToken, simulation: true).ConfigureAwait(false);
            return new DraftPlan(execution!.Value.Result, execution.Value.Candidate);
        }
        catch (Exception ex) when (ex is RevisionIntegrityException or JsonException)
        {
            // The latest revision is corrupt: the same state startup calls degraded. A result, not a crash; the exception is logged,
            // the caller gets a sentence and the response's trace ID.
            LogLatestRevisionCorrupt(ex);
            return new DraftPlan(
                new ConfigCommandResult(
                    ConfigCommandStatus.Unavailable,
                    Errors:
                    [
                        new ConfigError(
                            string.Empty,
                            CorruptRevisionCode,
                            "The latest config revision can't be read. The details are in the server log under this response's trace ID."),
                    ]),
                null);
        }
    }

    /// <summary>A draft that changes nothing, compiled as the head its attempt planned on.</summary>
    private async Task<Execution> UnchangedCandidateAsync(ConfigDbContext context, ConfigDocument current, StoredRevision? latest, CancellationToken cancellationToken)
    {
        var inventory = await _simulationCertificates.LoadAsync(context, current, cancellationToken).ConfigureAwait(false);
        var compilation = _compiler.Compile(current, latest?.Info.Id ?? ConfigRevisionId.Empty, inventory);
        return compilation.Snapshot is { } snapshot
            ? new Execution(new ConfigCommandResult(ConfigCommandStatus.Unchanged, Warnings: compilation.Warnings), Candidate: snapshot)
            : new Execution(new ConfigCommandResult(
                ConfigCommandStatus.Invalid,
                Errors: compilation.Errors.Count > 0 ? compilation.Errors : [new ConfigError(string.Empty, ConfigErrorCodes.CompileFailed, "the config didn't compile")]));
    }

    /// <summary>The checks reading applies, for the document or object a command carries; null: it passes.</summary>
    private static ConfigError? Preflight(ConfigCommand command)
    {
        (ConfigDocument Document, string Prefix)? payload = command switch
        {
            ReplaceConfigCommand c when c.Document is not null => (c.Document, string.Empty),
            UpsertHostCommand { Host: { } host } => (new ConfigDocument(Hosts: [host]), "/hosts/0"),
            UpsertUpstreamCommand { Upstream: { } upstream } => (new ConfigDocument(Upstreams: [upstream]), "/upstreams/0"),
            UpsertProfileCommand { Profile: { } profile } => (new ConfigDocument(Profiles: [profile]), "/profiles/0"),
            UpdateSettingsCommand { Settings: { } settings } => (new ConfigDocument(Settings: settings), "/settings"),
            _ => null,
        };
        if (payload is not { } carried || CanonicalConfigJson.Preflight(carried.Document) is not { } error)
        {
            return null;
        }

        return carried.Prefix.Length > 0 && error.Path.StartsWith(carried.Prefix, StringComparison.Ordinal)
            ? error with { Path = error.Path[carried.Prefix.Length..] }
            : error;
    }

    /// <summary>A compile for simulation, and whether it had to fall back to the built-in certificates because the
    /// certificate table couldn't be read.</summary>
    internal sealed record SimulationCompilation(ConfigCompilation Compilation, bool InventoryUnreadable);

    /// <summary>A stored revision's document, or why it can't be read.</summary>
    internal sealed record StoredDocument(RevisionRef Revision, ConfigDocument? Document, ConfigError? Problem);

    /// <summary>A draft command's dry run: its result, and the compiled candidate when it passed.</summary>
    internal sealed record DraftPlan(ConfigCommandResult Result, RuntimeSnapshot? Candidate);

    [LoggerMessage(Level = LogLevel.Error, Message = "Revision {Revision} failed its integrity check and can't be read.")]
    private partial void LogRevisionCorrupt(Exception exception, ConfigRevisionId revision);

    [LoggerMessage(Level = LogLevel.Error, Message = "The latest config revision can't be read; a draft can't be planned on it.")]
    private partial void LogLatestRevisionCorrupt(Exception exception);
}
