using Paduraksa.Application.Config;
using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Data;

namespace Paduraksa.Application.Commands;

/// <summary>The head of the config: the latest revision and its document.</summary>
internal static class ConfigHead
{
    /// <summary>The config before the first commit: no objects, default settings, every version 0.</summary>
    public static ConfigDocument Initial { get; } = CanonicalConfigJson.Canonicalize(new ConfigDocument());

    /// <summary>The latest revision and its canonical document; <see cref="Initial"/> and null before the first commit.</summary>
    /// <exception cref="RevisionIntegrityException">The latest revision is corrupt.</exception>
    public static async Task<(StoredRevision? Latest, ConfigDocument Document)> LoadAsync(ConfigDbContext context,
        CancellationToken cancellationToken)
    {
        var latest = await ConfigRevisionStore.GetLatestAsync(context, cancellationToken).ConfigureAwait(false);
        return latest is null
            ? (null, Initial)
            : (latest, CanonicalConfigJson.Canonicalize(CanonicalConfigJson.Deserialize(latest.Document.Span)));
    }

    /// <summary>
    /// <see cref="LoadAsync"/> for a command that is about to build on the head, also returning the password verifiers a head that
    /// still carried them held (the startup conversion couldn't be stored). Importing them is the caller's, and only for a command
    /// that will commit: a draft or a dry run leaves the vault alone, and the next real command or the next start imports them anyway.
    /// </summary>
    public static async Task<(StoredRevision? Latest, ConfigDocument Document, IReadOnlyList<LegacyCredential> Legacy)> LoadForCommandAsync(
        ConfigDbContext context, CancellationToken cancellationToken)
    {
        var latest = await ConfigRevisionStore.GetLatestAsync(context, cancellationToken).ConfigureAwait(false);
        if (latest is null)
        {
            return (null, Initial, []);
        }

        var document = CanonicalConfigJson.Canonicalize(CanonicalConfigJson.Deserialize(latest.Document.Span, out var upgrade));
        return (latest, document, upgrade.Credentials);
    }

    public static RevisionRef Ref(RevisionInfo info) => new(info.Id, info.Seq);
}
