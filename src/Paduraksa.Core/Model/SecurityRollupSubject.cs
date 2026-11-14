namespace Paduraksa.Core.Model;

/// <summary>What a security rollup row (a minute of the gate's process counters) is about. Stored as its number: never renumber.</summary>
public enum SecurityRollupSubject
{
    /// <summary>The whole process: proof of work, rate limits, access lists, the connection stage, and every auth provider and rule set summed.</summary>
    Total = 0,

    /// <summary>One auth provider by its <see cref="AuthProviderId"/>.</summary>
    AuthProvider = 1,

    /// <summary>One rule set by its <see cref="RuleSetId"/>.</summary>
    RuleSet = 2,
}
