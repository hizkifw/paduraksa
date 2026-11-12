using System.Text.Json;

using Microsoft.EntityFrameworkCore;
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
/// What the request simulator needs from the config writer: configs to compile without publishing them. None of it takes the
/// writer's lock, audits, stores or publishes anything, and none of it asks the vault for a key
/// (<see cref="SimulationCertificateSource"/>).
/// </summary>
internal sealed partial class ConfigDrafts
{
    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly IConfigCompiler _compiler;
    private readonly CertificateInventoryLoader _simulationCertificates;
    private readonly ConfigPipeline _pipeline;
    private readonly ConfigPublishing _publishing;
    private readonly ILogger _logger;

    public ConfigDrafts(
        IDbContextFactory<ConfigDbContext> contexts, IConfigCompiler compiler, CertificateInventoryLoader simulationCertificates,
        ConfigPipeline pipeline, ConfigPublishing publishing, ILoggerFactory loggerFactory)
    {
        _contexts = contexts;
        _compiler = compiler;
        _simulationCertificates = simulationCertificates;
        _pipeline = pipeline;
        _publishing = publishing;
        _logger = loggerFactory.CreateLogger<ConfigDrafts>();
    }

    /// <summary>
    /// A stored revision's document, or null when there is no such revision. A document this build can't read is
    /// <see cref="StoredDocument.Problem"/>, not an exception.
    /// </summary>
    internal async Task<StoredDocument?> ReadRevisionAsync(ConfigRevisionId id, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_publishing.IsDisposed, nameof(ConfigService));
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
                        ConfigService.CorruptRevisionCode,
                        "The revision fails its integrity check, so it can't be read. The details are in the server log under this response's trace ID."));
            }

            if (stored is null)
            {
                return null;
            }

            var revision = ConfigHead.Ref(stored.Info);
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
        ObjectDisposedException.ThrowIf(_publishing.IsDisposed, nameof(ConfigService));
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

        return new SimulationCompilation(_compiler.Compile(document, revision, inventory, publish: false), degraded);
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
        ObjectDisposedException.ThrowIf(_publishing.IsDisposed, nameof(ConfigService));

        var dryRun = command with { DryRun = true };

        // Status is read without the writer's lock: planning writes nothing, so a start that finishes while this runs only
        // means the draft is planned against the store as it becomes usable, and a store that turns unusable fails below.
        if (_publishing.Status is { AcceptsCommands: false } unusable)
        {
            return new DraftPlan(_publishing.Unavailable(dryRun, unusable), null);
        }

        // What reading checks and a hand-built payload skips: a null element would be a NullReferenceException in the diff.
        if (Preflight(dryRun) is { } malformed)
        {
            return new DraftPlan(new ConfigCommandResult(ConfigCommandStatus.Invalid, Errors: [malformed]), null);
        }

        var message = string.IsNullOrWhiteSpace(dryRun.Message) ? null : dryRun.Message.Trim();
        if (ConfigCommandPlanner.MessageProblem(message) is { } problem)
        {
            return new DraftPlan(new ConfigCommandResult(ConfigCommandStatus.Invalid, Errors: [new ConfigError(string.Empty, ConfigService.MessageInvalidCode, problem)]), null);
        }

        try
        {
            var execution = await _pipeline.AttemptAsync(dryRun, origin, ConfigCommandPlanner.Describe(dryRun), message, cancellationToken, simulation: true).ConfigureAwait(false);
            return new DraftPlan(execution.Result, execution.Candidate);
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
                            ConfigService.CorruptRevisionCode,
                            "The latest config revision can't be read. The details are in the server log under this response's trace ID."),
                    ]),
                null);
        }
    }

    /// <summary>The checks reading applies, for the document or object a command carries; null: it passes.</summary>
    internal static ConfigError? Preflight(ConfigCommand command)
    {
        (ConfigDocument Document, string Prefix)? payload = command switch
        {
            ReplaceConfigCommand c when c.Document is not null => (c.Document, string.Empty),
            UpsertHostCommand { Host: { } host } => (new ConfigDocument(Hosts: [host]), "/hosts/0"),
            UpsertUpstreamCommand { Upstream: { } upstream } => (new ConfigDocument(Upstreams: [upstream]), "/upstreams/0"),
            UpsertRateLimitPolicyCommand { Policy: { } policy } => (new ConfigDocument(RateLimitPolicies: [policy]), "/rateLimitPolicies/0"),
            UpsertAccessListCommand { AccessList: { } list } => (new ConfigDocument(AccessLists: [list]), "/accessLists/0"),
            UpsertRuleSetCommand { RuleSet: { } set } => (new ConfigDocument(RuleSets: [set]), "/ruleSets/0"),
            UpsertAuthProviderCommand { AuthProvider: { } provider } => (new ConfigDocument(AuthProviders: [provider]), "/authProviders/0"),
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

    [LoggerMessage(Level = LogLevel.Error, Message = "The certificate inventory can't be read; compiling with the built-in certificates only. Hosts keep their config but lose certificates the vault would have served.")]
    private partial void LogInventoryUnreadable(Exception exception);
}
