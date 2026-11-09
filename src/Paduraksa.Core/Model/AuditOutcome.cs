namespace Paduraksa.Core.Model;

/// <summary>How an audited action ended. Persisted by name (<see cref="ModelNames"/>).</summary>
public enum AuditOutcome
{
    /// <summary>It happened.</summary>
    Success = 1,

    /// <summary>Not authorised (authentication or role/scope check).</summary>
    Denied = 2,

    /// <summary>Rejected by validation or compilation; nothing was changed.</summary>
    Invalid = 3,

    /// <summary>Lost an optimistic-concurrency check (stale version or base revision); nothing was changed.</summary>
    Conflict = 4,

    /// <summary>An unexpected error; nothing was changed.</summary>
    Failed = 5,
}
