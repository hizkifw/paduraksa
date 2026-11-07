namespace Paduraksa.Acme.Dns;

/// <summary>How a propagation wait ended.</summary>
public enum DnsPropagationOutcome
{
    /// <summary>Every authoritative name server that could be asked returns every record.</summary>
    Propagated = 0,

    /// <summary>The wait ran out while some server still did not return every record. See <see cref="DnsPropagationResult.AnyServerHadRecords"/>: if some did, the CA may still validate; if none did, it will not.</summary>
    TimedOut = 1,

    /// <summary>The check could not be made (no zone or name servers found, no server reachable or authoritative), so it says nothing about propagation; the caller falls back to a fixed delay.</summary>
    Unverifiable = 2,
}

/// <summary>The outcome of <see cref="IDnsPropagationCheck.WaitAsync"/>.</summary>
/// <param name="Outcome">How the wait ended.</param>
/// <param name="Detail">What an operator would want to read in a log (which servers lagged, why it could not be checked); no secrets.</param>
/// <param name="AnyServerHadRecords">For <see cref="DnsPropagationOutcome.TimedOut"/>: whether at least one name server returned at least one of the records. When none did, the CA cannot see them either.</param>
public sealed record DnsPropagationResult(DnsPropagationOutcome Outcome, string Detail, bool AnyServerHadRecords = false);

/// <summary>
/// Waits until published DNS-01 TXT records are visible to the CA (PLAN §9.3), replacing a fixed delay. The records have already
/// been created through <see cref="IDnsProvider"/>; this only looks.
/// </summary>
public interface IDnsPropagationCheck
{
    /// <summary>Returns when every record is visible, the wait times out, or it is clear the check cannot be made. Never throws for DNS trouble: that is <see cref="DnsPropagationOutcome.Unverifiable"/>.</summary>
    /// <param name="records">The records to look for; several values may share a name.</param>
    /// <param name="cancellationToken">Cancels the wait (and throws <see cref="OperationCanceledException"/>).</param>
    ValueTask<DnsPropagationResult> WaitAsync(IReadOnlyList<DnsChallengeRecord> records, CancellationToken cancellationToken);
}
