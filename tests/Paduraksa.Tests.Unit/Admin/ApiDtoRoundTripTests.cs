using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Paduraksa.Admin.Json;
using Paduraksa.Core.Ids;

namespace Paduraksa.Tests.Unit.Admin;

/// <summary>
/// Every DTO the API reads or writes, built by reflection with its optional members null and again with everything set, is written through the host's HTTP
/// JSON options (the API's contract applied by <c>AdminJsonContext.AddTo</c>) and read back strictly: a client gets back what the server wrote. The strict
/// reader demands every constructor parameter without a default, so a nullable parameter without one would make a response that omits its null unreadable.
/// </summary>
public sealed class ApiDtoRoundTripTests
{
    private static readonly Type[] Roots =
        [.. typeof(AdminJsonContext).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0])];

    private static JsonSerializerOptions HttpOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        AdminJsonContext.AddTo(options);
        return options;
    }

    public static TheoryData<string> RootNames => [.. Roots.Select(t => t.ToString())];

    [Theory]
    [MemberData(nameof(RootNames))]
    public void ADtoReadsBackWhatTheApiWrites(string name)
    {
        var root = Roots.Single(t => t.ToString() == name);
        var options = HttpOptions();

        // Several variants each, so every kind of a polymorphic member is tried.
        for (var variant = 0; variant < 4; variant++)
        {
            foreach (var full in new[] { false, true })
            {
                var instance = new Filler(full, variant).Build(root, 0)!;

                var written = JsonSerializer.Serialize(instance, root, options);
                var read = JsonSerializer.Deserialize(written, root, options);

                Assert.NotNull(read);
                Assert.Equal(written, JsonSerializer.Serialize(read, root, options));
            }
        }
    }

    [Fact]
    public void NoDtoOfTheApiHasANullableConstructorParameterWithoutADefault()
    {
        HashSet<Type> seen = [];
        List<string> offenders = [];
        foreach (var root in Roots)
        {
            Walk(root, seen, offenders);
        }

        Assert.Empty(offenders);
    }

    private static void Walk(Type type, HashSet<Type> seen, List<string> offenders)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsGenericType && type.GetGenericArguments() is { Length: 1 } args && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
        {
            Walk(args[0], seen, offenders);
            return;
        }

        if (!seen.Add(type) || type.Assembly != typeof(Paduraksa.Contracts.Api.ApiProblem).Assembly)
        {
            return;
        }

        foreach (var derived in type.GetCustomAttributes<JsonDerivedTypeAttribute>())
        {
            Walk(derived.DerivedType, seen, offenders);
        }

        if (type.IsAbstract || type.IsEnum)
        {
            return;
        }

        var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (constructor is null)
        {
            return;
        }

        NullabilityInfoContext nullability = new();
        foreach (var parameter in constructor.GetParameters())
        {
            if (IsNullable(nullability, parameter) && !parameter.HasDefaultValue)
            {
                offenders.Add($"{type.Name}.{parameter.Name}");
            }

            Walk(parameter.ParameterType, seen, offenders);
        }
    }

    private static bool IsNullable(NullabilityInfoContext context, ParameterInfo parameter) =>
        Nullable.GetUnderlyingType(parameter.ParameterType) is not null
        || (!parameter.ParameterType.IsValueType && context.Create(parameter).WriteState == NullabilityState.Nullable);

    /// <summary>Builds a value of any DTO type: optional members null (or set, when <c>full</c>), the first kind of a polymorphic type in turn.</summary>
    private sealed class Filler(bool full, int variant)
    {
        private readonly NullabilityInfoContext _nullability = new();

        public object? Build(Type type, int depth)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying is not null)
            {
                type = underlying;
            }

            if (type == typeof(string))
            {
                return "text";
            }

            if (type == typeof(bool))
            {
                return true;
            }

            if (type == typeof(int))
            {
                return 7;
            }

            if (type == typeof(long))
            {
                return 7L;
            }

            if (type == typeof(DateTimeOffset))
            {
                return new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
            }

            if (type == typeof(TimeSpan))
            {
                return TimeSpan.FromHours(1);
            }

            if (type == typeof(JsonElement))
            {
                using var document = JsonDocument.Parse("""{"a":1}""");
                return document.RootElement.Clone();
            }

            if (type.IsEnum)
            {
                var values = Enum.GetValues(type);
                return values.GetValue(variant % values.Length);
            }

            if (type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ITypedId<>)))
            {
                return type.GetMethod("New", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)!.Invoke(null, null);
            }

            if (type.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type.GetGenericArguments() is { Length: 1 } args)
            {
                var list = typeof(List<>).MakeGenericType(args[0]);
                var items = (System.Collections.IList)Activator.CreateInstance(list)!;
                if (depth < 6)
                {
                    items.Add(Build(args[0], depth + 1));
                }

                return items;
            }

            if (type.IsAbstract)
            {
                JsonDerivedTypeAttribute[] derived = [.. type.GetCustomAttributes<JsonDerivedTypeAttribute>()];
                return Build(derived[variant % derived.Length].DerivedType, depth + 1);
            }

            var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            object?[] arguments = [.. constructor.GetParameters().Select(p => Argument(p, depth))];
            return constructor.Invoke(arguments);
        }

        private object? Argument(ParameterInfo parameter, int depth)
        {
            var nullable = IsNullable(_nullability, parameter);
            if (nullable && (!full || depth > 5))
            {
                return parameter.HasDefaultValue ? parameter.DefaultValue : null;
            }

            return Build(parameter.ParameterType, depth + 1);
        }
    }
}
