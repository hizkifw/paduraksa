using System.Data.Common;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Application.Simulation;

/// <summary>
/// The request simulator (PLAN §7.7): "what would this request get, and why". <see cref="SimulateAsync"/> is the one
/// function every surface calls (the API's <c>POST /api/v1/simulate</c>, MCP's <c>simulate_request</c>, the dashboard's
/// simulator page).
/// </summary>
/// <remarks>
/// <para><b>It runs the real gate.</b> The answer comes from <see cref="GateSimulator"/>, which runs
/// <c>GateMiddleware.Decide</c> over a compiled snapshot with a recording tracer; nothing here decides anything about a
/// request. This class only chooses the snapshot, builds the request and describes the result.</para>
/// <para><b>Targets.</b> Live: the snapshot serving traffic. Revision: the stored document, compiled through the normal
/// compiler. Draft document or command: run through the dry-run path (PLAN §6.1 steps 1 to 4: apply, validate, compile),
/// so a draft that a real command would reject is reported with the dry run's errors instead of simulated. Nothing is
/// ever stored, audited or published, and no snapshot but the live one is wired to YARP.</para>
/// <para><b>Keys.</b> Drafts and revisions never load a key. Whether a certificate is usable is decided from what the vault
/// describes (<c>ICertificateCatalog</c>: public facts), with the real loader's reasons, so a draft that a real command would
/// refuse for an unusable binding is refused here too; what is served is a placeholder
/// (<see cref="Certificates.SimulationCertificateSource"/>). The trace names the inventory certificate that would answer the
/// handshake.</para>
/// <para><b>Challenges</b> (ACME HTTP-01) are process state, not config: every target is asked against the live
/// <see cref="AcmeChallengeStore"/>.</para>
/// <para><b>Health</b> is live state in YARP. The upstream's destinations come from the target config; each one's health is
/// the live proxy's answer for the destination with the same ID and address (<see cref="SimulationHealth.Unknown"/> where the
/// live proxy has none). Load balancing is random: no destination is ever named as the one chosen.</para>
/// <para><b>Load.</b> A draft or revision compiles a whole config, so at most <see cref="MaxConcurrentCompiles"/> run at once;
/// one that can't start within <see cref="BusyWait"/> is answered <see cref="SimulationStatus.Unavailable"/>. The live
/// target takes no slot: it compiles nothing.</para>
/// </remarks>
public sealed class RequestSimulator : IDisposable
{
    /// <summary>How many drafts and revisions are compiled at the same time.</summary>
    public const int MaxConcurrentCompiles = 4;

    /// <summary>How long a draft or revision waits for a free slot before the simulator says it is busy.</summary>
    public static readonly TimeSpan BusyWait = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _compiles = new(MaxConcurrentCompiles, MaxConcurrentCompiles);
    private readonly TimeSpan _busyWait;
    private readonly ConfigService _config;
    private readonly SnapshotStore _store;
    private readonly IUpstreamHealth? _health;
    private readonly ISimulationAuthorizer _authorizer;
    private readonly AcmeChallengeStore? _challenges;
    private readonly ListenerFacts? _listeners;

    /// <param name="config">Where drafts and revisions come from.</param>
    /// <param name="store">The live snapshot.</param>
    /// <param name="health">The live proxy's destination health; null: unknown.</param>
    /// <param name="authorizer">Who may simulate; null: everyone (until roles exist).</param>
    /// <param name="challenges">The live HTTP-01 answers. Challenges are process state, not config, so every target (a draft
    /// or an old revision too) is asked against this one store, as the live gate is; null: no challenge is answered.</param>
    /// <param name="listeners">What the public listeners do before a request exists (PROXY protocol, HTTP/3); null: not
    /// modelled, any connection is let through.</param>
    /// <param name="busyWait">How long a draft or revision waits for a compile slot; default <see cref="BusyWait"/>.</param>
    internal RequestSimulator(
        ConfigService config,
        SnapshotStore store,
        IUpstreamHealth? health = null,
        ISimulationAuthorizer? authorizer = null,
        AcmeChallengeStore? challenges = null,
        ListenerFacts? listeners = null,
        TimeSpan? busyWait = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        _config = config;
        _store = store;
        _health = health;
        _authorizer = authorizer ?? AllowAllSimulations.Instance;
        _challenges = challenges;
        _listeners = listeners;
        _busyWait = busyWait ?? BusyWait;
    }

    /// <summary>
    /// Asks what <paramref name="request"/> would get against <paramref name="target"/>, and why. A target or request
    /// that can't be simulated is a result (<see cref="SimulationResult.Status"/>), not an exception.
    /// </summary>
    /// <param name="target">The config to ask about.</param>
    /// <param name="request">The request.</param>
    /// <param name="origin">Who asks, as the surface authenticated them.</param>
    /// <param name="cancellationToken">Stops the draft's compile or the revision's read.</param>
    public async Task<SimulationResult> SimulateAsync(
        SimulationTarget target, SimulationRequest request, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(origin);

        SimulationSource source = new(KindOf(target));
        if (!await _authorizer.IsAllowedAsync(target, origin, cancellationToken).ConfigureAwait(false))
        {
            return new SimulationResult(SimulationStatus.Denied, source);
        }

        if (!SimulationRequests.TryBuild(request, out GateProbe? probe, out List<ConfigError> problems))
        {
            return new SimulationResult(SimulationStatus.BadRequest, source, Errors: problems);
        }

        bool compiles = target is not LiveSimulationTarget;
        if (compiles && !await _compiles.WaitAsync(_busyWait, cancellationToken).ConfigureAwait(false))
        {
            return new SimulationResult(
                SimulationStatus.Unavailable,
                source,
                Errors: [new ConfigError(string.Empty, SimulationErrorCodes.Busy, $"{MaxConcurrentCompiles} drafts or revisions are being compiled already; try again in a moment.")]);
        }

        Resolved resolved;
        try
        {
            resolved = await ResolveAsync(target, origin, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException or RevisionIntegrityException or JsonException)
        {
            resolved = new Resolved(Unreadable(source, ex));
        }
        finally
        {
            if (compiles)
            {
                _compiles.Release();
            }
        }

        if (resolved.Failure is { } failure)
        {
            return failure;
        }

        GateSimulation run = GateSimulator.Run(resolved.Snapshot!, probe!, _challenges, _listeners);
        List<string> notes = [.. resolved.Notes];
        SimulationUpstream? upstream = run.Upstream is null ? null : DescribeUpstream(run.Upstream, resolved.Source.Kind != SimulationTargetKind.Live, notes);
        List<SimulationStep> trace = new(run.Steps.Count);
        foreach (GateStep step in run.Steps)
        {
            trace.Add(Describe(step));
        }

        return new SimulationResult(
            SimulationStatus.Simulated,
            resolved.Source,
            Describe(run),
            trace,
            upstream,
            Warnings: resolved.Warnings,
            Notes: notes);
    }

    /// <summary>The compile slots, for tests that need the simulator to be busy.</summary>
    internal SemaphoreSlim CompileSlots => _compiles;

    /// <summary>Releases the compile slots.</summary>
    public void Dispose() => _compiles.Dispose();

    private static SimulationResult Unreadable(SimulationSource source, Exception ex) => new(
        SimulationStatus.Unavailable,
        source,
        Errors: [new ConfigError(string.Empty, SimulationErrorCodes.StoreUnreadable, $"The config store can't be read: {ex.Message}")]);

    private async Task<Resolved> ResolveAsync(SimulationTarget target, CommandOrigin origin, CancellationToken cancellationToken)
    {
        switch (target)
        {
            case LiveSimulationTarget:
                RuntimeSnapshot live = _store.Current;
                return new Resolved(live, new SimulationSource(SimulationTargetKind.Live, live.Revision.IsEmpty ? null : live.Revision))
                {
                    Notes = _store.HasPublished ? [] : ["No config has been published yet: this is the bootstrap snapshot, which answers 503 to everything."],
                };
            case RevisionSimulationTarget revision:
                return await ResolveRevisionAsync(revision.Revision, cancellationToken).ConfigureAwait(false);
            case DraftDocumentSimulationTarget draft:
                return await ResolveDraftDocumentAsync(draft.Document, origin, cancellationToken).ConfigureAwait(false);
            case DraftCommandSimulationTarget command:
                return await ResolveDraftAsync(command.Command, SimulationTargetKind.DraftCommand, origin, cancellationToken).ConfigureAwait(false);
            default:
                throw new ArgumentException($"Unknown simulation target {target.GetType().Name}.", nameof(target));
        }
    }

    private async Task<Resolved> ResolveRevisionAsync(ConfigRevisionId id, CancellationToken cancellationToken)
    {
        SimulationSource source = new(SimulationTargetKind.Revision, id);
        if (_config.Status is { AcceptsCommands: false } unusable)
        {
            return new Resolved(new SimulationResult(
                SimulationStatus.Unavailable,
                source,
                Errors: [new ConfigError(string.Empty, SimulationErrorCodes.StoreUnreadable, $"The config store is unavailable: {unusable.Problem ?? "the process started degraded"}.")]));
        }

        if (await _config.ReadRevisionAsync(id, cancellationToken).ConfigureAwait(false) is not { } stored)
        {
            return new Resolved(new SimulationResult(SimulationStatus.RevisionNotFound, source));
        }

        if (stored.Document is not { } document)
        {
            // Corrupt on disk is the store's failure; a document this build can't read is the revision's.
            return new Resolved(new SimulationResult(
                stored.Problem?.Code == ConfigService.CorruptRevisionCode ? SimulationStatus.Unavailable : SimulationStatus.Invalid,
                source,
                Errors: [stored.Problem ?? new ConfigError(string.Empty, SimulationErrorCodes.RevisionUnreadable, "The revision's document can't be read by this build.")]));
        }

        ConfigService.SimulationCompilation compiled = await _config.CompileForSimulationAsync(document, id, cancellationToken).ConfigureAwait(false);
        ConfigCompilation compilation = compiled.Compilation;
        List<string> notes = [$"Revision {stored.Revision}, compiled now against today's certificate inventory; it was not published."];
        if (compiled.InventoryUnreadable)
        {
            notes.Add(InventoryNote);
        }

        return compilation.Snapshot is { } snapshot
            ? new Resolved(snapshot, source) { Warnings = compilation.Warnings, Notes = notes }
            : new Resolved(new SimulationResult(SimulationStatus.Invalid, source, Errors: compilation.Errors, Warnings: compilation.Warnings, Notes: notes));
    }

    private const string InventoryNote = "The certificate table couldn't be read, so only the built-in certificates were considered: hosts may show no certificate.";

    /// <summary>A whole document is a replace on the current head. The head is read just before: a commit in between is a
    /// conflict, which is retried once on the new head (the document doesn't depend on the head's content).</summary>
    private async Task<Resolved> ResolveDraftDocumentAsync(ConfigDocument document, CommandOrigin origin, CancellationToken cancellationToken)
    {
        Resolved resolved = default!;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ConfigRevisionId? head = (await _config.GetCurrentAsync(cancellationToken).ConfigureAwait(false)).Revision?.Id;
            resolved = await ResolveDraftAsync(new ReplaceConfigCommand(document, head), SimulationTargetKind.DraftDocument, origin, cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not { Status: SimulationStatus.DraftRejected })
            {
                break;
            }
        }

        return resolved;
    }

    private async Task<Resolved> ResolveDraftAsync(ConfigCommand command, SimulationTargetKind kind, CommandOrigin origin, CancellationToken cancellationToken)
    {
        SimulationSource source = new(kind);
        ConfigService.DraftPlan plan = await _config.PlanDraftAsync(command, origin, cancellationToken).ConfigureAwait(false);
        ConfigCommandResult result = plan.Result;
        switch (result.Status)
        {
            case ConfigCommandStatus.DryRun when plan.Candidate is { } candidate:
                return new Resolved(candidate, source) { Warnings = result.Warnings };
            case ConfigCommandStatus.Unchanged when plan.Candidate is { } unchanged:
                // The draft is the config as it already is (as of the head the dry run planned on): simulated, and said so.
                return new Resolved(unchanged, source) { Warnings = result.Warnings, Notes = ["The draft changes nothing: this is the current config."] };
            case ConfigCommandStatus.Invalid:
                return new Resolved(new SimulationResult(SimulationStatus.Invalid, source, Errors: result.Errors, RelatedErrors: result.RelatedErrors, Warnings: result.Warnings));
            case ConfigCommandStatus.Unavailable:
                return new Resolved(new SimulationResult(SimulationStatus.Unavailable, source, Errors: result.Errors));
            default:
                string why = result.Status switch
                {
                    ConfigCommandStatus.Conflict => "the draft was made on an older version of the config than the current one",
                    ConfigCommandStatus.NotFound => "the object (or revision) the draft refers to doesn't exist",
                    _ => $"the dry run ended as {result.Status}",
                };
                return new Resolved(new SimulationResult(
                    SimulationStatus.DraftRejected,
                    source,
                    Errors: result.Errors.Count > 0 ? result.Errors : [new ConfigError(string.Empty, SimulationErrorCodes.DraftRejected, $"The draft can't be applied: {why}.")]));
        }
    }

    private SimulationUpstream DescribeUpstream(GateUpstream upstream, bool targetIsNotLive, List<string> notes)
    {
        UpstreamHealthSnapshot? live = _health?.Lookup(upstream.Id);
        List<SimulationDestination> destinations = [];
        foreach (GateDestination destination in upstream.Destinations)
        {
            SimulationHealth health = SimulationHealth.Unknown;
            if (live is not null)
            {
                // The ID and the address both: a draft that moved a destination says nothing about the one the proxy knows.
                if (Contains(live.Healthy, destination))
                {
                    health = SimulationHealth.Healthy;
                }
                else if (Contains(live.Unhealthy, destination))
                {
                    health = SimulationHealth.Unhealthy;
                }
            }

            destinations.Add(new SimulationDestination(destination.Id, destination.Address, health));
        }

        if (live is null)
        {
            notes.Add("Destination health is unknown: the live proxy has no upstream with this ID (or isn't running).");
        }
        else if (targetIsNotLive)
        {
            notes.Add("Destination health is the live proxy's: it can't be known for a config that isn't live.");
        }

        return new SimulationUpstream(upstream.Id, destinations, upstream.LoadBalancing);

        static bool Contains(IReadOnlyList<LiveDestination> known, GateDestination destination)
        {
            foreach (LiveDestination candidate in known)
            {
                if (candidate.Id == destination.Id && string.Equals(candidate.Address, destination.Address, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static SimulationOutcome Describe(GateSimulation run)
    {
        GateOutcome outcome = run.Outcome;
        string summary = "forwarded to the route's upstream";
        foreach (GateStep step in run.Steps)
        {
            if (step.Verdict == GateStepVerdict.Stopped)
            {
                summary = step.Summary;
                break;
            }
        }

        SimulationOutcomeKind kind = outcome.Action switch
        {
            GateOutcomeAction.Forward => SimulationOutcomeKind.ForwardToUpstream,
            GateOutcomeAction.Abort => outcome.Reason switch
            {
                GateReason.HandshakeRejected => SimulationOutcomeKind.HandshakeRejected,
                GateReason.ConnectionRejected => SimulationOutcomeKind.ConnectionRejected,
                _ => SimulationOutcomeKind.Drop,
            },
            _ => outcome.Reason switch
            {
                GateReason.AcmeChallenge => SimulationOutcomeKind.AcmeChallengeAnswer,
                GateReason.DefaultHost => SimulationOutcomeKind.DefaultHost,
                GateReason.MisdirectedConnection => SimulationOutcomeKind.MisdirectedRequest,
                GateReason.NoRoute => SimulationOutcomeKind.NoRoute,
                GateReason.RejectedByServer => SimulationOutcomeKind.RejectedByServer,
                _ when outcome.Location is not null => SimulationOutcomeKind.Redirect,
                _ => SimulationOutcomeKind.FixedAnswer,
            },
        };
        return new SimulationOutcome(kind, summary, outcome.StatusCode, outcome.Location, outcome.Body);
    }

    private static SimulationStep Describe(GateStep step)
    {
        List<SimulationFact> facts = new(step.Facts.Count);
        foreach (GateFact fact in step.Facts)
        {
            facts.Add(new SimulationFact(fact.Name, fact.Value));
        }

        return new SimulationStep(Map(step.Kind), Map(step.Verdict), step.Summary, facts, step.Host, step.Route, step.PolicyIndex);
    }

    // Mapped by name, not by the enums' numbers: the two sides are free to grow in their own order.
    private static SimulationStepKind Map(GateStepKind kind) => kind switch
    {
        GateStepKind.Connection => SimulationStepKind.Connection,
        GateStepKind.TlsHandshake => SimulationStepKind.TlsHandshake,
        GateStepKind.Request => SimulationStepKind.Request,
        GateStepKind.ClientAddress => SimulationStepKind.ClientAddress,
        GateStepKind.AcmeChallenge => SimulationStepKind.AcmeChallenge,
        GateStepKind.HostMatch => SimulationStepKind.HostMatch,
        GateStepKind.TlsPolicy => SimulationStepKind.TlsPolicy,
        GateStepKind.HostPolicy => SimulationStepKind.HostPolicy,
        GateStepKind.HttpsRedirect => SimulationStepKind.HttpsRedirect,
        GateStepKind.FixedResponse => SimulationStepKind.FixedResponse,
        GateStepKind.RouteMatch => SimulationStepKind.RouteMatch,
        GateStepKind.RoutePolicy => SimulationStepKind.RoutePolicy,
        GateStepKind.Upstream => SimulationStepKind.Upstream,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A gate step the contract doesn't know."),
    };

    private static SimulationStepVerdict Map(GateStepVerdict verdict) => verdict switch
    {
        GateStepVerdict.Passed => SimulationStepVerdict.Passed,
        GateStepVerdict.Stopped => SimulationStepVerdict.Stopped,
        GateStepVerdict.Skipped => SimulationStepVerdict.Skipped,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "A step verdict the contract doesn't know."),
    };

    private static SimulationTargetKind KindOf(SimulationTarget target) => target switch
    {
        LiveSimulationTarget => SimulationTargetKind.Live,
        RevisionSimulationTarget => SimulationTargetKind.Revision,
        DraftDocumentSimulationTarget => SimulationTargetKind.DraftDocument,
        DraftCommandSimulationTarget => SimulationTargetKind.DraftCommand,
        _ => throw new ArgumentException($"Unknown simulation target {target.GetType().Name}.", nameof(target)),
    };

    /// <summary>The snapshot to run on, or the result that says why there is none.</summary>
    private sealed record Resolved
    {
        public Resolved(RuntimeSnapshot snapshot, SimulationSource source)
        {
            Snapshot = snapshot;
            Source = source;
        }

        public Resolved(SimulationResult failure)
        {
            Failure = failure;
            Source = failure.Source;
        }

        public RuntimeSnapshot? Snapshot { get; }

        public SimulationSource Source { get; }

        public SimulationResult? Failure { get; }

        public IReadOnlyList<ConfigWarning> Warnings { get; init; } = [];

        public IReadOnlyList<string> Notes { get; init; } = [];
    }
}
