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

    public static RevisionRef Ref(RevisionInfo info) => new(info.Id, info.Seq);
}
