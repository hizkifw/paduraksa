namespace Paduraksa.Tests.Architecture.Fixtures;

/// <summary>
/// A <c>Paduraksa.*</c> type outside every layer (like test code or the ID generator).
/// No layer may depend on it, not even Server; the fake layer types reference it to prove that.
/// </summary>
public sealed class NonLayerType;
