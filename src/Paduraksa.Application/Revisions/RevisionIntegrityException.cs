using Paduraksa.Core.Model;

namespace Paduraksa.Application.Revisions;

/// <summary>
/// A stored revision document is unreadable: it doesn't decompress, or its bytes don't match the stored SHA-256.
/// The database was damaged or edited outside Paduraksa; the revision must not be used.
/// </summary>
public sealed class RevisionIntegrityException : Exception
{
    public RevisionIntegrityException()
    {
    }

    public RevisionIntegrityException(string message)
        : base(message)
    {
    }

    public RevisionIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RevisionIntegrityException(ConfigRevisionId revisionId, long seq, string problem, Exception? innerException = null)
        : base($"Config revision #{seq} ({revisionId}) is corrupt: {problem}.", innerException)
    {
        RevisionId = revisionId;
        Seq = seq;
    }

    public ConfigRevisionId? RevisionId { get; }

    /// <summary>The corrupt revision's <c>Seq</c> ("#42"), if known.</summary>
    public long? Seq { get; }
}
