using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Paduraksa.Admin.Json;
using Paduraksa.Contracts.Api;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.OpenApi;

/// <summary>
/// The plain text of an API description. The XML doc comments the descriptions come from are written for contributors, so what ASP.NET copies out of them
/// carries the source's line breaks and indentation, <c>&lt;see cref&gt;</c> rendered as the symbol's type and name ("bool? MaintenanceSettings.Enabled"), and
/// XML entities. One pass over the finished document turns that into what a reader of the reference sees: the crefs stay in the source (IDE navigation keeps
/// working), and the description says <c>`enabled`</c>.
/// </summary>
internal static partial class DescriptionText
{
    /// <summary>The text with its source formatting taken out: paragraphs kept as one blank line, every other run of whitespace one space.</summary>
    public static string? Tidy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var paragraphs = ParagraphBreak().Split(text.Replace("\r\n", "\n", StringComparison.Ordinal))
            .Select(paragraph => Whitespace().Replace(paragraph, " ").Trim())
            .Where(paragraph => paragraph.Length > 0)
            .Select(paragraph => Entities(Emphasis(CodeSpans(paragraph, ResolveCrefs))));
        return string.Join("\n\n", paragraphs);
    }

    /// <summary>The names of members the JSON of the API spells differently from the member: what a cref to <paramref name="member"/> should say.</summary>
    internal static string? NameOf(Type type, string member)
    {
        if (type.IsEnum)
        {
            return type.GetField(member, BindingFlags.Public | BindingFlags.Static) is { } field ? WireNameOf(type, field.Name) : null;
        }

        if (Find(type, member) is not { } found)
        {
            return null;
        }

        if (found is PropertyInfo { GetMethod.IsStatic: false } property && JsonNameOf(property) is { } json)
        {
            return json;
        }

        // Not a member of the JSON (a constant, a method, a computed property): the code name is the one a reader can look up.
        return $"{type.Name}.{found.Name}";
    }

    /// <summary>The member's name in the JSON of the API, or null when the member isn't part of it (ignored, or not serialized by the API's contexts at all).</summary>
    private static string? JsonNameOf(PropertyInfo property)
    {
        var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
        if (property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
        {
            return null;
        }

        try
        {
            // The serializer's own metadata is the authority on what a type writes; a type it doesn't know is taken to write its public properties.
            var info = AdminJsonContext.Default.Options.GetTypeInfo(property.DeclaringType!);
            return info.Properties.Any(p => p.Name == name) ? name : null;
        }
        catch (NotSupportedException)
        {
            return name;
        }
    }

    private static string WireNameOf(Type type, string name)
    {
        try
        {
            var json = JsonSerializer.Serialize(Enum.Parse(type, name), type, AdminJsonContext.Default.Options);
            return JsonSerializer.Deserialize<string>(json) ?? JsonNamingPolicy.CamelCase.ConvertName(name);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return JsonNamingPolicy.CamelCase.ConvertName(name);
        }
    }

    private static MemberInfo? Find(Type type, string member) =>
        type.GetMember(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .FirstOrDefault(m => m is PropertyInfo or FieldInfo or MethodInfo);

    /// <summary>Applies <paramref name="rewrite"/> to the text outside backtick spans, which are already code.</summary>
    private static string CodeSpans(string text, Func<string, string> rewrite)
    {
        var parts = text.Split('`');
        for (var i = 0; i < parts.Length; i += 2)
        {
            parts[i] = rewrite(parts[i]);
        }

        return string.Join('`', parts);
    }

    private static string ResolveCrefs(string text)
    {
        var rewritten = new StringBuilder(text.Length);
        var copied = 0;
        foreach (Match match in MemberReference().Matches(text))
        {
            if (!TypesByName.Value.TryGetValue(match.Groups["type"].Value, out var types))
            {
                continue;
            }

            foreach (var type in types)
            {
                var member = match.Groups["member"].Value;
                if (NameOf(type, member) is not { } name)
                {
                    continue;
                }

                var start = match.Index;
                if (!type.IsEnum && Find(type, member) is { } found)
                {
                    start -= PrefixLength(text.AsSpan(copied, match.Index - copied), TypeOf(found));
                }

                rewritten.Append(text, copied, start - copied).Append('`').Append(name).Append('`');
                copied = match.Index + match.Length;
                break;
            }
        }

        return rewritten.Append(text, copied, text.Length - copied).ToString();
    }

    private static Type? TypeOf(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        MethodInfo method => method.ReturnType,
        _ => null,
    };

    /// <summary>How much of the end of <paramref name="before"/> is the type ASP.NET puts in front of the member, with its space.</summary>
    private static int PrefixLength(ReadOnlySpan<char> before, Type? type)
    {
        if (type is null)
        {
            return 0;
        }

        var display = DisplayOf(type);
        foreach (var candidate in new[] { display, display + "?" })
        {
            foreach (var spelled in new[] { candidate.Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal), candidate })
            {
                if (before.EndsWith(spelled + " ", StringComparison.Ordinal) && (before.Length == spelled.Length + 1 || !char.IsLetterOrDigit(before[^(spelled.Length + 2)])))
                {
                    return spelled.Length + 1;
                }
            }
        }

        return 0;
    }

    private static string DisplayOf(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return DisplayOf(underlying) + "?";
        }

        if (type.IsArray)
        {
            return DisplayOf(type.GetElementType()!) + "[]";
        }

        if (Keywords.TryGetValue(type, out var keyword))
        {
            return keyword;
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(DisplayOf))}>";
    }

    private static readonly FrozenDictionary<Type, string> Keywords = new Dictionary<Type, string>
    {
        [typeof(bool)] = "bool",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
        [typeof(char)] = "char",
        [typeof(string)] = "string",
        [typeof(object)] = "object",
        [typeof(void)] = "void",
    }.ToFrozenDictionary();

    /// <summary>ASP.NET writes a cref to a property, field or method as "type Declaring.Name" (a method with its signature); the type in front is found from the member.</summary>
    [GeneratedRegex(@"(?<![\w.`])(?<type>[A-Z]\w*)\.(?<member>[A-Za-z_]\w*)(?:\((?<args>[^()]*)\))?")]
    private static partial Regex MemberReferenceExpression();

    private static Regex MemberReference() => MemberReferenceExpression();

    private static readonly Lazy<FrozenDictionary<string, Type[]>> TypesByName = new(() =>
        new[] { typeof(ApiProblem).Assembly, typeof(HostId).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsVisible && !type.IsGenericTypeDefinition && !type.Name.Contains('<', StringComparison.Ordinal))
            .GroupBy(type => type.Name, StringComparer.Ordinal)
            .ToFrozenDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal));

    private static string Emphasis(string text) =>
        Italic().Replace(Bold().Replace(text, "**$1**"), "*$1*");

    private static string Entities(string text) =>
        text.Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);

    [GeneratedRegex(@"[ \t]*\n[ \t]*\n\s*")]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"<(?:b|strong)>(.*?)</(?:b|strong)>")]
    private static partial Regex Bold();

    [GeneratedRegex(@"<(?:i|em)>(.*?)</(?:i|em)>")]
    private static partial Regex Italic();
}
