using Paduraksa.Application.Backup;
using Paduraksa.Application.Commands;

namespace Paduraksa.Dashboard.Backup;

/// <summary>
/// What the backup page asks of the Application: the full backup as a download. The service checks <c>backup:create</c> and audits
/// every outcome itself; the page's own permission check only decides what to show.
/// </summary>
public interface IDashboardBackup
{
    /// <summary>Makes the archive under <paramref name="passphrase"/>; the download, when there is one, deletes its file when disposed.</summary>
    Task<BackupDownloadResult> CreateAsync(CommandOrigin origin, string passphrase, CancellationToken cancellationToken = default);
}

internal sealed class DashboardBackup(BackupService backups) : IDashboardBackup
{
    public Task<BackupDownloadResult> CreateAsync(CommandOrigin origin, string passphrase, CancellationToken cancellationToken = default) =>
        backups.CreateDownloadAsync(origin, passphrase, cancellationToken);
}
