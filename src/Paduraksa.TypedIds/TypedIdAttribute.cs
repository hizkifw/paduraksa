namespace Paduraksa.TypedIds;

/// <summary>Marks a <c>readonly partial record struct</c> as a typed ID.</summary>
/// <example><c>[TypedId("host")] public readonly partial record struct HostId;</c></example>
/// <param name="prefix">Lowercase prefix matching <c>^[a-z][a-z0-9]{1,7}$</c>. Rendered as <c>prefix_</c>.</param>
/// <param name="kind">How values are generated and stored.</param>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class TypedIdAttribute(string prefix, IdKind kind = IdKind.UuidV7) : Attribute
{
    /// <summary>The prefix, without the trailing underscore.</summary>
    public string Prefix { get; } = prefix ?? throw new ArgumentNullException(nameof(prefix));

    /// <summary>How values are generated and stored.</summary>
    public IdKind Kind { get; } = kind;
}
