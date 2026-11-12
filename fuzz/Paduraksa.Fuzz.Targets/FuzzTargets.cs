namespace Paduraksa.Fuzz;

/// <summary>Every target, by name.</summary>
public static class FuzzTargets
{
    /// <summary>The targets, in the order the scripts run them.</summary>
    public static IReadOnlyList<FuzzTarget> All { get; } =
    [
        ProxyProtocolTarget.Target,
        ProofOfWorkTargets.Cookie,
        ProofOfWorkTargets.Form,
        BasicAuthTarget.Target,
        ClientAddressTarget.Target,
        ForwardAuthAnswerTarget.Target,
        UpstreamErrorReplacementTarget.Target,
        ErrorPageTarget.Target,
    ];

    public static FuzzTarget Get(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal))
        ?? throw new ArgumentException($"No fuzz target named '{name}'; they are {string.Join(", ", All.Select(t => t.Name))}.", nameof(name));
}
