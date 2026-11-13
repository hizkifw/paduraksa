using System.Collections.Concurrent;
using System.Reflection;

using Paduraksa.Core.Ids;

namespace Paduraksa.Admin.OpenApi;

/// <summary>What the OpenAPI schema of a typed ID says: a string with the ID's prefix and fixed length.</summary>
/// <param name="Name">The ID type's name (<c>HostId</c>).</param>
/// <param name="Prefix">The prefix before the underscore (<c>host</c>).</param>
/// <param name="Pattern">The regular expression of its string form.</param>
/// <param name="Example">An example value.</param>
internal sealed record TypedIdSchema(string Name, string Prefix, string Pattern, string Example);

internal static class TypedIdSchemas
{
    private static readonly MethodInfo DescribeMethod = typeof(TypedIdSchemas).GetMethod(nameof(Describe), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly ConcurrentDictionary<Type, TypedIdSchema?> Cache = new();

    /// <summary>
    /// The characters an example ID is made of, led by a '0' so it is in range for every kind (a 64-bit ID rejects a larger first digit). The example is
    /// the same on every boot because the generated document is shipped as a file and compared with a fresh generation: an ID from <c>New()</c> would
    /// differ each time.
    /// </summary>
    private const string ExampleCharacters = "0A1b2C3d4E5f6G7h8I9j0K";

    /// <summary>True when <paramref name="type"/> is a typed ID (implements <see cref="ITypedId{TSelf}"/>).</summary>
    public static bool TryDescribe(Type type, out TypedIdSchema schema)
    {
        ArgumentNullException.ThrowIfNull(type);
        var found = Cache.GetOrAdd(type, static t =>
            t.IsValueType && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ITypedId<>) && i.GenericTypeArguments[0] == t)
                ? (TypedIdSchema?)DescribeMethod.MakeGenericMethod(t).Invoke(null, null)
                : null);
        schema = found!;
        return found is not null;
    }

    private static TypedIdSchema Describe<TId>()
        where TId : struct, ITypedId<TId>
    {
        var width = TId.FormattedLength - TId.Prefix.Length - 1;
        return new TypedIdSchema(typeof(TId).Name, TId.Prefix, $"^{TId.Prefix}_[0-9A-Za-z]{{{width}}}$", TId.Parse($"{TId.Prefix}_{ExampleCharacters[..width]}").ToString() ?? string.Empty);
    }
}
