namespace Paduraksa.Core.Ids;

/// <summary>
/// Marks a <c>readonly partial record struct</c> as a typed ID. The Paduraksa.Ids.Generator source generator
/// emits the value, factory, formatting, parsing, comparison and converter members.
/// </summary>
/// <example><c>[TypedId("host")] public readonly partial record struct HostId;</c></example>
/// <param name="prefix">Lowercase prefix matching <c>^[a-z][a-z0-9]{1,7}$</c>. Rendered as <c>prefix_</c>.</param>
/// <param name="kind">How values are generated and stored.</param>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class TypedIdAttribute(string prefix, IdKind kind = IdKind.UuidV7) : Attribute
{
    public string Prefix { get; } = prefix ?? throw new ArgumentNullException(nameof(prefix));

    public IdKind Kind { get; } = kind;
}
