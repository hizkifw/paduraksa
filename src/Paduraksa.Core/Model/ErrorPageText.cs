using System.Text;

namespace Paduraksa.Core.Model;

/// <summary>The values an error page's template can name, as <c>{name}</c>.</summary>
public enum ErrorPageVariable : byte
{
    /// <summary>The numeric status, e.g. <c>404</c>. Fixed per page, so it is part of the compiled bytes.</summary>
    Status,

    /// <summary>The standard name of the status, e.g. <c>Not Found</c>. Fixed per page too.</summary>
    StatusText,

    /// <summary>The request's ID, which a visitor can quote and the access log carries.</summary>
    RequestId,

    /// <summary>The name the visitor asked for (the <c>Host</c> header without its port).</summary>
    Host,

    /// <summary>Cloudflare's <c>CF-Ray</c> for the request; empty unless Cloudflare's headers are trusted, as in the access log.</summary>
    CfRay,
}

/// <summary>A piece of a template: literal text, or a variable.</summary>
public readonly record struct ErrorPageTemplatePart(string? Text, ErrorPageVariable? Variable);

/// <summary>
/// The syntax and bounds of an error page's text, shared by the validator, the compiler and the dashboard so the three agree.
/// A variable is <c>{name}</c> with a name of ASCII letters and digits; any other brace is literal text, so the CSS in an
/// operator's document (<c>a{color:red}</c>) needs no escaping. <c>{{</c> writes a literal <c>{</c>, for the rare text
/// that has to show a variable's own spelling.
/// </summary>
public static class ErrorPageText
{
    /// <summary>The longest heading, in characters.</summary>
    public const int MaxHeadingLength = 120;

    /// <summary>The longest sentence under the heading, in characters.</summary>
    public const int MaxTextLength = 500;

    /// <summary>The longest HTML document, in UTF-8 bytes (64 KiB).</summary>
    public const int MaxHtmlBytes = 64 * 1024;

    /// <summary>The variable names, in the order the validator lists them.</summary>
    public static IReadOnlyList<string> VariableNames { get; } = Array.AsReadOnly(
        [NameOf(ErrorPageVariable.Status), NameOf(ErrorPageVariable.StatusText), NameOf(ErrorPageVariable.RequestId), NameOf(ErrorPageVariable.Host), NameOf(ErrorPageVariable.CfRay)]);

    /// <summary>The name a template writes for <paramref name="variable"/>: camel case, as the document spells its own fields.</summary>
    public static string NameOf(ErrorPageVariable variable) => variable switch
    {
        ErrorPageVariable.Status => "status",
        ErrorPageVariable.StatusText => "statusText",
        ErrorPageVariable.RequestId => "requestId",
        ErrorPageVariable.Host => "host",
        ErrorPageVariable.CfRay => "cfRay",
        _ => throw new ArgumentOutOfRangeException(nameof(variable), variable, null),
    };

    /// <summary>The variable names as the validator says them to an operator: <c>{status}, {statusText}, ...</c>.</summary>
    public static string VariableList { get; } = string.Join(", ", VariableNames.Select(static name => "{" + name + "}"));

    /// <summary>
    /// Splits <paramref name="text"/> into literal text and variables. The names that aren't variables come back in
    /// <paramref name="unknown"/> (each once, in order of appearance, and without braces), and their spelling stays in the
    /// literal text so a caller that goes on sees what the operator wrote.
    /// </summary>
    public static List<ErrorPageTemplatePart> Parse(string text, out List<string> unknown)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<ErrorPageTemplatePart> parts = [];
        unknown = [];
        StringBuilder literal = new();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '{')
            {
                literal.Append(c);
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '{')
            {
                literal.Append('{');
                i++;
                continue;
            }

            var end = i + 1;
            while (end < text.Length && char.IsAsciiLetterOrDigit(text[end]))
            {
                end++;
            }

            if (end == i + 1 || end >= text.Length || text[end] != '}' || !char.IsAsciiLetter(text[i + 1]))
            {
                literal.Append('{');
                continue;
            }

            var name = text.Substring(i + 1, end - i - 1);
            if (TryVariable(name, out var variable))
            {
                if (literal.Length > 0)
                {
                    parts.Add(new ErrorPageTemplatePart(literal.ToString(), null));
                    literal.Clear();
                }

                parts.Add(new ErrorPageTemplatePart(null, variable));
            }
            else
            {
                if (!unknown.Contains(name))
                {
                    unknown.Add(name);
                }

                literal.Append(text, i, end - i + 1);
            }

            i = end;
        }

        if (literal.Length > 0)
        {
            parts.Add(new ErrorPageTemplatePart(literal.ToString(), null));
        }

        return parts;
    }

    /// <summary>Whether a heading or sentence is free of control characters (a line break included) and well-formed Unicode.</summary>
    public static bool IsPlainLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return IsWellFormed(text) && !text.AsSpan().ContainsAny(ControlCharacters);
    }

    /// <summary>Whether a document has no control characters but tab, carriage return and line feed, and is well-formed Unicode.</summary>
    public static bool IsPlainDocument(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!IsWellFormed(text))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (char.IsControl(c) && c is not ('\t' or '\r' or '\n'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the document is no longer than <see cref="MaxHtmlBytes"/> as UTF-8.</summary>
    public static bool IsWithinSizeLimit(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return Encoding.UTF8.GetByteCount(html) <= MaxHtmlBytes;
    }

    private static readonly System.Buffers.SearchValues<char> ControlCharacters = System.Buffers.SearchValues.Create(
        [.. Enumerable.Range(0, 0x20).Select(static i => (char)i), '\x7f', .. Enumerable.Range(0x80, 0x20).Select(static i => (char)i)]);

    private static bool TryVariable(string name, out ErrorPageVariable variable)
    {
        foreach (var candidate in Enum.GetValues<ErrorPageVariable>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                variable = candidate;
                return true;
            }
        }

        variable = default;
        return false;
    }

    private static bool IsWellFormed(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            if (!Rune.TryGetRuneAt(text, i, out var rune))
            {
                return false;
            }

            i += rune.Utf16SequenceLength;
        }

        return true;
    }
}
