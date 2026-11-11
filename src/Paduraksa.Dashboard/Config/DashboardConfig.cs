using Microsoft.Extensions.Logging;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Cloudflare;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Revisions;
using Paduraksa.Application.Simulation;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Identity;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>The Application services behind <see cref="IDashboardConfig"/>, one per circuit.</summary>
internal sealed partial class DashboardConfig(
    ConfigService config,
    RevisionQueryService revisions,
    CertificateService certificates,
    RequestSimulator simulator,
    UserService users,
    CloudflareRangeReport cloudflareRanges,
    ILogger<DashboardConfig> logger) : IDashboardConfig
{
    public ConfigStoreStatus Status => config.Status;

    public IReadOnlyList<ConfigWarning> LiveWarnings => config.Warnings;

    public CloudflareRangeStatus CloudflareRanges => cloudflareRanges.Current;

    public Task<CurrentConfig> GetCurrentAsync(CancellationToken cancellationToken = default) => config.GetCurrentAsync(cancellationToken);

    public Task<ConfigCommandResult> ExecuteAsync(ConfigCommand command, CommandOrigin origin, CancellationToken cancellationToken = default) =>
        config.ExecuteAsync(command, origin, cancellationToken);

    public Task<RevisionTimelinePage?> ListRevisionsAsync(int limit, ConfigRevisionId? after, CancellationToken cancellationToken = default) =>
        revisions.ListWithChangesAsync(limit, after, cancellationToken);

    public Task<RevisionDetail?> GetRevisionAsync(ConfigRevisionId id, CancellationToken cancellationToken = default) =>
        revisions.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<CertificateSummary>> ListCertificatesAsync(CancellationToken cancellationToken = default) =>
        certificates.ListAsync(cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<UserSummary>> ListUsersAsync(CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        var result = await users.ListAsync(origin, cancellationToken).ConfigureAwait(false);
        return result.Status == IdentityStatus.Succeeded ? result.Users : [];
    }

    public Task<SimulationResult> SimulateAsync(SimulationTarget target, SimulationRequest request, CommandOrigin origin, CancellationToken cancellationToken = default) =>
        simulator.SimulateAsync(target, request, origin, cancellationToken);

    public IDisposable Watch(Func<ConfigCommitted, Task> onCommitted)
    {
        ArgumentNullException.ThrowIfNull(onCommitted);
        var subscription = config.Changes.Subscribe();
        _ = PumpAsync(subscription, onCommitted);
        return subscription;
    }

    /// <summary>Hands each commit to the callback until the subscription is disposed (which completes its reader).</summary>
    private async Task PumpAsync(ConfigChangeSubscription subscription, Func<ConfigCommitted, Task> onCommitted)
    {
        await foreach (var committed in subscription.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await onCommitted(committed).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A page that failed to refresh keeps what it shows; the next commit or a reload tries again.
                LogRefreshFailed(logger, ex, committed.Revision.Seq);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A dashboard page couldn't follow revision #{Seq}.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception, long seq);
}
