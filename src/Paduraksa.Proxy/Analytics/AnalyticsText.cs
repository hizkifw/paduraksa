namespace Paduraksa.Proxy.Analytics;

/// <summary>Cleans request text into a table key: capped, and free of control characters, so a key can't break a table or a line it is shown on.</summary>
internal static class AnalyticsText
{
    /// <summary>
    /// <paramref name="text"/> as it is when it is clean and short enough (the common case, with no allocation), else cut at
    /// <paramref name="maxLength"/> with <c>...</c> and with each control character replaced by <c>?</c>. <paramref name="whenEmpty"/>
    /// stands for a missing or empty value.
    /// </summary>
    public static string Clean(string? text, int maxLength, string whenEmpty = "-")
    {
        if (string.IsNullOrEmpty(text))
        {
            return whenEmpty;
        }

        if (text.Length <= maxLength && !HasControl(text))
        {
            return text;
        }

        var kept = text.Length <= maxLength ? text.Length : maxLength - 3;
        return string.Create(Math.Min(text.Length, maxLength), (text, kept), static (span, state) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = i < state.kept ? Safe(state.text[i]) : '.';
            }
        });
    }

    private static bool HasControl(string text)
    {
        foreach (var c in text)
        {
            if (char.IsControl(c) || c is '\u2028' or '\u2029')
            {
                return true;
            }
        }

        return false;
    }

    private static char Safe(char c) => char.IsControl(c) || c is '\u2028' or '\u2029' ? '?' : c;
}
