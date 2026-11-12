using System.Globalization;

namespace Paduraksa.Application.Yaml;

/// <summary>
/// What a plain scalar means, by the YAML 1.2 core schema (so <c>off</c>, <c>no</c> and <c>NO</c>, a country code, are text, not booleans), and which texts the writer must
/// quote so another reader of the file, YAML 1.1 ones included, takes them as text too.
/// </summary>
internal static class YamlScalars
{
    public static bool IsNull(YamlScalar scalar) => scalar.Plain && scalar.Text is "" or "~" or "null" or "Null" or "NULL";

    public static bool TryBool(YamlScalar scalar, out bool value)
    {
        value = false;
        if (!scalar.Plain)
        {
            return false;
        }

        switch (scalar.Text)
        {
            case "true" or "True" or "TRUE":
                value = true;
                return true;
            case "false" or "False" or "FALSE":
                return true;
            default:
                return false;
        }
    }

    /// <summary>A plain decimal integer, with an optional sign; no hex, octal or separators.</summary>
    public static bool TryInteger(YamlScalar scalar, out long value)
    {
        value = 0;
        return scalar.Plain && IsDecimalInteger(scalar.Text) && long.TryParse(scalar.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>A plain decimal number as a JSON number would write it (an integer or a fraction, optionally with an exponent).</summary>
    public static bool TryNumber(YamlScalar scalar, out double value)
    {
        value = 0;
        return scalar.Plain && IsDecimalNumber(scalar.Text) && double.TryParse(scalar.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    /// <summary>True when a string written plain would not read back as a string, in YAML 1.2 or 1.1: it would be null, a boolean, a number, or one of 1.1's boolean words.</summary>
    public static bool NeedsQuotes(string text) =>
        text.Length == 0
        || text is "~" or "null" or "Null" or "NULL" or "true" or "True" or "TRUE" or "false" or "False" or "FALSE"
            or "y" or "Y" or "n" or "N" or "yes" or "Yes" or "YES" or "no" or "No" or "NO" or "on" or "On" or "ON" or "off" or "Off" or "OFF"
            or "<<" or "=" or ".inf" or ".Inf" or ".INF" or "-.inf" or "-.Inf" or "-.INF" or "+.inf" or ".nan" or ".NaN" or ".NAN"
        || IsDecimalNumber(text)
        || LooksLikeOtherNumber(text);

    private static bool IsDecimalInteger(string text)
    {
        var i = text.Length > 0 && text[0] is '-' or '+' ? 1 : 0;
        if (i == text.Length)
        {
            return false;
        }

        for (; i < text.Length; i++)
        {
            if (text[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDecimalNumber(string text)
    {
        var i = text.Length > 0 && text[0] is '-' or '+' ? 1 : 0;
        var digits = 0;
        for (; i < text.Length && text[i] is >= '0' and <= '9'; i++)
        {
            digits++;
        }

        if (i < text.Length && text[i] == '.')
        {
            for (i++; i < text.Length && text[i] is >= '0' and <= '9'; i++)
            {
                digits++;
            }
        }

        if (digits == 0)
        {
            return false;
        }

        if (i < text.Length && text[i] is 'e' or 'E')
        {
            i++;
            if (i < text.Length && text[i] is '-' or '+')
            {
                i++;
            }

            var exponent = 0;
            for (; i < text.Length && text[i] is >= '0' and <= '9'; i++)
            {
                exponent++;
            }

            if (exponent == 0)
            {
                return false;
            }
        }

        return i == text.Length;
    }

    // 0x1F, 0o17, 1_000 and the sexagesimal 1:30 (a time span's 00:00:15 too): numbers to some readers, text to this one; quoted so every reader agrees.
    private static bool LooksLikeOtherNumber(string text) =>
        text.Length > 1 && (text.StartsWith("0x", StringComparison.Ordinal) || text.StartsWith("0o", StringComparison.Ordinal) || text.StartsWith("0b", StringComparison.Ordinal))
        || text.Length > 0 && text[0] is >= '0' and <= '9' && text.All(static c => c is >= '0' and <= '9' or '_' or ':') && text.AsSpan().ContainsAny('_', ':');
}
