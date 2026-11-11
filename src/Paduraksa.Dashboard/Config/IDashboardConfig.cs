using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
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

    Task<CurrentConfig> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs a command (a dry run when it says so) for <paramref name="origin"/>.</summary>
    Task<ConfigCommandResult> ExecuteAsync(ConfigCommand command, CommandOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>A page of the history, newest first, each revision with what it changed; null when <paramref name="after"/> names none.</summary>
    Task<RevisionTimelinePage?> ListRevisionsAsync(int limit, ConfigRevisionId? after, CancellationToken cancellationToken = default);

    /// <summary>A revision with its document; null when there is none.</summary>
    Task<RevisionDetail?> GetRevisionAsync(ConfigRevisionId id, CancellationToken cancellationToken = default);

    /// <summary>The certificate inventory (public facts only).</summary>
    Task<IReadOnlyList<CertificateSummary>> ListCertificatesAsync(CancellationToken cancellationToken = default);

    /// <summary>The users, for naming who made a revision; empty when <paramref name="origin"/> may not list them.</summary>
    Task<IReadOnlyList<UserSummary>> ListUsersAsync(CommandOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>What a request would get against <paramref name="target"/>, and why.</summary>
    Task<SimulationResult> SimulateAsync(SimulationTarget target, SimulationRequest request, CommandOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls <paramref name="onCommitted"/> for every revision committed from now on (by anyone: this page, another admin, the API, the CLI
    /// in this process), in commit order, until the returned handle is disposed. The callback runs off the renderer's thread: a component
    /// hands it to <c>InvokeAsync</c>.
    /// </summary>
    IDisposable Watch(Func<ConfigCommitted, Task> onCommitted);
}
