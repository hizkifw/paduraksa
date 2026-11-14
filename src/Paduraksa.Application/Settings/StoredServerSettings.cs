using Paduraksa.Application.Config;
using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Config;
using Paduraksa.Data;

namespace Paduraksa.Application.Settings;

/// <summary>
/// The server settings the config database stores (the latest revision's <c>settings.server</c>), read without the config service: for the command
/// line, which must apply the same precedence as the server (the environment's value, then this, then the default) but never writes the config.
/// </summary>
public static class StoredServerSettings
{
    /// <summary>
    /// What the latest revision stores, or null when it stores none, the database is missing or the revision can't be read: a command-line tool
    /// then falls back to the environment and the defaults, as the server does when it can't read the config. Opens the database read-only, so it is
    /// safe beside a running server.
    /// </summary>
    public static async Task<ServerSettings?> ReadAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath))
        {
            return null;
        }

        try
        {
            var context = ConfigDatabase.OpenReadOnly(databasePath);
            await using (context.ConfigureAwait(false))
            {
                var latest = await ConfigRevisionStore.GetLatestAsync(context, cancellationToken).ConfigureAwait(false);
                if (latest is null || !CanonicalConfigJson.TryDeserialize(latest.Document.Span, out var document, out _))
                {
                    return null;
                }

                return document.Settings.Server is { SetsNothing: false } server ? server : null;
            }
        }
#pragma warning disable CA1031 // Any failure to read (a corrupt row, a schema this build can't read, a locked file) means "no stored settings" here, like the server's own degraded start.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
