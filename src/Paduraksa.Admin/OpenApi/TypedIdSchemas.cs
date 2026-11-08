using System.Collections.Concurrent;
using System.Reflection;

using Paduraksa.Core.Ids;

namespace Paduraksa.Admin.OpenApi;

/// <summary>What the OpenAPI schema of a typed ID says (PLAN §4.1): a string with the ID's prefix and fixed length.</summary>
/// <param name="Name">The ID type's name (<c>HostId</c>).</param>
/// <param name="Prefix">The prefix before the underscore (<c>host</c>).</param>
/// <param name="Pattern">The regular expression of its string form.</param>
/// <param name="Example">An example value.</param>
internal sealed record TypedIdSchema(string Name, string Prefix, string Pattern, string Example);

/// <summary>Describes typed ID types to the OpenAPI generator.</summary>
internal static class TypedIdSchemas
{
    private static readonly MethodInfo DescribeMethod = typeof(TypedIdSchemas).GetMethod(nameof(Describe), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly ConcurrentDictionary<Type, TypedIdSchema?> Cache = new();

    /// <summary>True when <paramref name="type"/> is a typed ID (implements <see cref="ITypedId{TSelf}"/>).</summary>
    public static bool TryDescribe(Type type, out TypedIdSchema schema)
    {
        ArgumentNullException.ThrowIfNull(type);
        TypedIdSchema? found = Cache.GetOrAdd(type, static t =>
            t.IsValueType && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ITypedId<>) && i.GenericTypeArguments[0] == t)
                ? (TypedIdSchema?)DescribeMethod.MakeGenericMethod(t).Invoke(null, null)
                : null);
        schema = found!;
        return found is not null;
    }

    private static TypedIdSchema Describe<TId>()
        where TId : struct, ITypedId<TId>
    {
        int width = TId.FormattedLength - TId.Prefix.Length - 1;
        return new TypedIdSchema(typeof(TId).Name, TId.Prefix, $"^{TId.Prefix}_[0-9A-Za-z]{{{width}}}$", TId.New().ToString() ?? string.Empty);
    }
}
