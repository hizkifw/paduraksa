using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Paduraksa.Application.Identity;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// First-run setup at startup, in the core host after <see cref="ConfigStartup"/> (the identity tables are migrated
/// by then): while no Owner exists, creates the one-time setup token (<c>&lt;data dir&gt;/setup-token</c>, owner-only) and logs it at
/// Warning on one line and prints the boxed banner to standard output; once an Owner exists, removes a stale file. A config store that could not start (a degraded start) is not
/// a reason to stop here: the failure is logged and the admin app will report setup as unavailable.
/// </summary>
internal sealed partial class IdentityStartup(SetupService setup, ILogger<IdentityStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await setup.AnnounceAsync(Console.Out, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            LogFailed(logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't check whether first-run setup is complete, so no setup token was prepared.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
