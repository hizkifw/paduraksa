using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Projection;

namespace Paduraksa.Application.Projection;

/// <summary>
/// Writes the config projection (PLAN §6.4, <see cref="GlobalSettingsRow"/>): the rows of the latest revision's
/// document, in the same transaction as the revision.
/// </summary>
/// <remarks>
/// <para><b>Incremental by change list.</b> A commit rewrites only the objects its change list touches: each touched
/// object's rows (with its names, routes or destinations) are deleted and, unless it was removed, inserted again from
/// the new document; untouched objects' rows aren't read or written. That keeps a one-host edit O(1) in the size of the
/// config, which matters for the change → live budget, and needs no row-by-row comparison: the diff already says what
/// changed, and a touched object's rows are few. Foreign keys are deferred to the commit, so the order of the deletes
/// and inserts doesn't matter.</para>
/// <para><b>Self-healing.</b> The settings row records the revision the projection reflects. When it isn't the parent
/// of the revision being written (an empty projection, rows from another build, a projection a migration cleared), the
/// whole projection is rewritten from the new document instead.</para>
/// </remarks>
internal static class ConfigProjection
{
    /// <summary>
    /// Brings the projection from <paramref name="parent"/>'s document to <paramref name="document"/> (canonical,
    /// versions stamped), committed as <paramref name="revision"/>. Runs inside the caller's transaction and saves the
    /// context.
    /// </summary>
    public static async Task WriteAsync(
        ConfigDbContext context,
        ConfigDocument document,
        IReadOnlyList<ConfigChange> changes,
        ConfigRevisionId revision,
        ConfigRevisionId? parent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(changes);

        await context.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON", cancellationToken).ConfigureAwait(false);
        if (parent is not null && await TryReflectedRevisionAsync(context, cancellationToken).ConfigureAwait(false) == parent)
        {
            TouchedObjects touched = TouchedObjects.From(changes);
            await DeleteAsync(context, touched, cancellationToken).ConfigureAwait(false);
            Add(context, document, touched.Profiles.Contains, touched.Upstreams.Contains, touched.Hosts.Contains);
        }
        else
        {
            await ClearAsync(context, cancellationToken).ConfigureAwait(false);
            Add(context, document, _ => true, _ => true, _ => true);
        }

        await context.GlobalSettings.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        context.GlobalSettings.Add(Settings(document.Settings, revision));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The revision the projection shows (its settings row's), or null when it has none.</summary>
    public static Task<ConfigRevisionId?> ReflectedRevisionAsync(ConfigDbContext context, CancellationToken cancellationToken) =>
        context.GlobalSettings
            .Where(s => s.Key == GlobalSettingsRow.SingletonKey)
            .Select(s => (ConfigRevisionId?)s.RevisionId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary><see cref="ReflectedRevisionAsync"/>, or null when the settings row can't be read (a damaged ID): such a
    /// projection shows nothing usable and is rewritten whole.</summary>
    public static async Task<ConfigRevisionId?> TryReflectedRevisionAsync(ConfigDbContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await ReflectedRevisionAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The settings row of <paramref name="settings"/>, reflecting <paramref name="revision"/>.</summary>
    public static GlobalSettingsRow Settings(GlobalSettings settings, ConfigRevisionId revision) => new()
    {
        RevisionId = revision,
        Version = settings.Version,
        UnknownHost = DocumentNames.Of(settings.UnknownHost),
        DefaultCertificateId = settings.DefaultCertificateId,
        TrustedProxyMode = DocumentNames.Of(settings.TrustedProxies.Mode),
        Defaults = JsonSerializer.Serialize(settings.Defaults, ConfigJsonContext.Default.HostSettings),
    };

    public static ProfileRow Profile(ProfileConfig profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        Version = profile.Version,
        Settings = JsonSerializer.Serialize(profile.Settings, ConfigJsonContext.Default.HostSettings),
        Notes = profile.Notes,
    };

    public static UpstreamRow Upstream(UpstreamConfig upstream) => new()
    {
        Id = upstream.Id,
        Name = upstream.Name,
        Version = upstream.Version,
        Notes = upstream.Notes,
    };

    public static IEnumerable<DestinationRow> Destinations(UpstreamConfig upstream) =>
        upstream.Destinations.Select(d => new DestinationRow { Id = d.Id, UpstreamId = upstream.Id, Address = d.Address, Enabled = d.Enabled });

    public static HostRow Host(HostConfig host) => new()
    {
        Id = host.Id,
        Version = host.Version,
        Kind = DocumentNames.Of(host.Kind),
        Enabled = host.Enabled,
        ProfileId = host.ProfileId,
        CertificateMode = DocumentNames.Of(host.Certificate.Mode),
        CertificateId = host.Certificate.CertificateId,
        Settings = JsonSerializer.Serialize(host.Settings, ConfigJsonContext.Default.HostSettings),
        Tags = [.. host.Tags],
        Notes = host.Notes,
    };

    public static IEnumerable<HostNameRow> Names(HostConfig host) =>
        host.Names.Select(n => new HostNameRow { HostId = host.Id, Name = n, HostEnabled = host.Enabled });

    public static IEnumerable<RouteRow> Routes(HostConfig host) => host.Routes.Select((r, i) => new RouteRow
    {
        Id = r.Id,
        HostId = host.Id,
        UpstreamId = r.UpstreamId,
        Position = i,
        Order = r.Order,
        Match = DocumentNames.Of(r.Match),
        Path = r.Path,
        Settings = JsonSerializer.Serialize(r.Settings, ConfigJsonContext.Default.RouteSettings),
    });

    private static async Task DeleteAsync(ConfigDbContext context, TouchedObjects touched, CancellationToken cancellationToken)
    {
        // Names, routes and destinations go with their parent (ON DELETE CASCADE).
        if (touched.Hosts.Count > 0)
        {
            List<HostId> hosts = [.. touched.Hosts];
            await context.Hosts.Where(h => hosts.Contains(h.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (touched.Upstreams.Count > 0)
        {
            List<UpstreamId> upstreams = [.. touched.Upstreams];
            await context.Upstreams.Where(u => upstreams.Contains(u.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (touched.Profiles.Count > 0)
        {
            List<ProfileId> profiles = [.. touched.Profiles];
            await context.Profiles.Where(p => profiles.Contains(p.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ClearAsync(ConfigDbContext context, CancellationToken cancellationToken)
    {
        await context.Hosts.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.Upstreams.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.Profiles.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Add(
        ConfigDbContext context,
        ConfigDocument document,
        Func<ProfileId, bool> profiles,
        Func<UpstreamId, bool> upstreams,
        Func<HostId, bool> hosts)
    {
        foreach (ProfileConfig profile in document.Profiles.Where(p => profiles(p.Id)))
        {
            context.Profiles.Add(Profile(profile));
        }

        foreach (UpstreamConfig upstream in document.Upstreams.Where(u => upstreams(u.Id)))
        {
            context.Upstreams.Add(Upstream(upstream));
            context.Destinations.AddRange(Destinations(upstream));
        }

        foreach (HostConfig host in document.Hosts.Where(h => hosts(h.Id)))
        {
            context.Hosts.Add(Host(host));
            context.HostNames.AddRange(Names(host));
            context.Routes.AddRange(Routes(host));
        }
    }
}
