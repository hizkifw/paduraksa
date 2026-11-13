using Paduraksa.Application.Commands;
using Paduraksa.Application.Drafts;
using Paduraksa.Application.Yaml;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Contracts.Identity;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// What the config pages ask of the Application layer: the current config and its commands (dry runs included), the
/// history, the certificate inventory's public listing, the simulator, and the change feed for live updates. A seam so the pages can be
/// tested without a database; <see cref="DashboardConfig"/> is the real one. Everything it hands out is a Contracts DTO.
/// </summary>
/// <remarks>
/// Reads are not authorized here, as on the API: the pages check <c>config:read</c> (and <c>certificates:read</c>) before they read.
/// Commands and simulations carry the caller's <see cref="CommandOrigin"/>, and the services decide.
/// </remarks>
public interface IDashboardConfig
{
    /// <summary>Whether the config store accepts changes, and what serves traffic.</summary>
    ConfigStoreStatus Status { get; }

    /// <summary>The live snapshot's warnings (hosts without certificate coverage, bindings that went bad); paths index the live document.</summary>
    IReadOnlyList<ConfigWarning> LiveWarnings { get; }

    /// <summary>The list of Cloudflare's edge ranges the proxy trusts in <c>CloudflareProxy</c> mode, and how its refresh is going.</summary>
    CloudflareRangeStatus CloudflareRanges { get; }

    /// <summary>The latest revision as it is live, with its revision: what the overview, the history and the simulator read.</summary>
    Task<CurrentConfig> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The config the config pages and their pickers read (docs/design/dashboard.md "Drafts (M10)", drafts): the latest revision with the caller's open draft applied, so a form
    /// sees an upstream the draft adds; the latest revision itself when there is no draft. <see cref="CurrentConfig.Revision"/> is the latest
    /// revision either way. Versions are the latest revision's, so a form's command is the version the entry will be checked against.
    /// </summary>
    Task<CurrentConfig> GetWorkingAsync(CancellationToken cancellationToken = default);

    /// <summary>The caller's open draft, each entry marked against the latest revision (<see cref="DraftStatus.NoDraft"/> when there is none).</summary>
    Task<DraftResult> GetDraftAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds <paramref name="commands"/> to the caller's draft, opening one first when there is none; a refused add leaves no empty draft behind.</summary>
    Task<DraftResult> AddToDraftAsync(IReadOnlyList<ConfigCommand> commands, CancellationToken cancellationToken = default);

    /// <summary>Takes entry <paramref name="number"/> out of the draft.</summary>
    Task<DraftResult> RemoveDraftEntryAsync(DraftId draft, int number, CancellationToken cancellationToken = default);

    /// <summary>The draft as a dry run: the diff, warnings and errors of the whole set.</summary>
    Task<DraftResult> ReviewDraftAsync(DraftId draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits the whole draft as one revision, or nothing: <paramref name="reviewToken"/> is the token of the review the person confirmed, and an apply of
    /// anything else (or of a draft with a blocked entry) commits nothing and says so.
    /// </summary>
    Task<DraftResult> ApplyDraftAsync(DraftId draft, string? reviewToken, string? message, CancellationToken cancellationToken = default);

    /// <summary>Drops the draft and what the vault holds for it.</summary>
    Task<DraftResult> DiscardDraftAsync(DraftId draft, CancellationToken cancellationToken = default);

    /// <summary>The notices of the caller's drafts that were dropped for being untouched, once.</summary>
    Task<IReadOnlyList<DraftNotice>> TakeDraftNoticesAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs a command (a dry run when it says so) for <paramref name="origin"/>.</summary>
    Task<ConfigCommandResult> ExecuteAsync(ConfigCommand command, CommandOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>A page of the history, newest first, each revision with what it changed; null when <paramref name="after"/> names none.</summary>
    Task<RevisionTimelinePage?> ListRevisionsAsync(int limit, ConfigRevisionId? after, CancellationToken cancellationToken = default);

    /// <summary>A revision with its document; null when there is none.</summary>
    Task<RevisionDetail?> GetRevisionAsync(ConfigRevisionId id, CancellationToken cancellationToken = default);

    /// <summary>The live config as YAML, with its revision.</summary>
    Task<ConfigExport> ExportAsync(CancellationToken cancellationToken = default);

    /// <summary>The command importing <paramref name="yaml"/> in <paramref name="mode"/> would run, for the review sheet to dry-run and apply; errors that point into the YAML when there is none.</summary>
    Task<ConfigImportPreparation> PrepareImportAsync(ReadOnlyMemory<byte> yaml, ConfigImportMode mode, CommandOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>The certificate inventory (public facts only).</summary>
    Task<IReadOnlyList<CertificateSummary>> ListCertificatesAsync(CancellationToken cancellationToken = default);

    /// <summary>The users, for naming who made a revision; empty when <paramref name="origin"/> may not list them.</summary>
    Task<IReadOnlyList<UserSummary>> ListUsersAsync(CommandOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>What a request would get against <paramref name="target"/>, and why.</summary>
    Task<SimulationResult> SimulateAsync(SimulationTarget target, SimulationRequest request, CommandOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls <paramref name="onCommitted"/> for every revision committed from now on (by anyone: this page, another admin, the API, the CLI
    /// in this process), in commit order, until the returned handle is disposed; also, with a revision of the latest and no changes, whenever the
    /// caller's draft changes (in this tab or another), since what a page reads through <see cref="GetWorkingAsync"/> changed. The callback runs off
    /// the renderer's thread: a component hands it to <c>InvokeAsync</c>.
    /// </summary>
    IDisposable Watch(Func<ConfigCommitted, Task> onCommitted);
}
