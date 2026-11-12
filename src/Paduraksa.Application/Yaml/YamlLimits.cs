namespace Paduraksa.Application.Yaml;

/// <summary>
/// What an imported YAML document may cost before anything is built from it. The input can come from the API, so each cap
/// bounds a different way of making the parser or the mapping expensive: bytes and scalar length (memory), depth
/// (the stack of anything that walks the tree), nodes counting every alias expansion (the "billion laughs" tree, which
/// is small as text and enormous as a tree), and anchors and aliases themselves.
/// </summary>
/// <param name="MaxBytes">Input size.</param>
/// <param name="MaxDepth">Nesting of mappings and sequences, aliases expanded.</param>
/// <param name="MaxNodes">Scalars, mappings and sequences in the tree, every alias expansion counted.</param>
/// <param name="MaxScalarLength">Characters in one scalar.</param>
/// <param name="MaxKeyLength">Characters in a mapping key.</param>
/// <param name="MaxAnchors">Distinct anchors.</param>
/// <param name="MaxAliases">Alias uses.</param>
public sealed record YamlLimits(
    int MaxBytes = 4 * 1024 * 1024,
    int MaxDepth = 24,
    int MaxNodes = 200_000,
    int MaxScalarLength = 512 * 1024,
    int MaxKeyLength = 256,
    int MaxAnchors = 64,
    int MaxAliases = 1_000)
{
    /// <summary>The caps the API, the CLI and the dashboard use.</summary>
    public static YamlLimits Default { get; } = new();
}
