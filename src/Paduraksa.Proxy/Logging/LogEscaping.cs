namespace Paduraksa.Proxy.Logging;

/// <summary>
/// How one character of a request's text is written so that it cannot forge or break a log line: the one escaper the file's
/// quoted fields (<see cref="AccessLogFormatter"/>) and the live tail's fields share, so what the tail shows is what the file says.
/// </summary>
/// <remarks>
/// <c>"</c> and <c>\</c> become <c>\"</c> and <c>\\</c>; a C0 control, 0x7F and the C1 controls (0x80 to 0x9F, NEL among them) become
/// <c>\xNN</c>; the line and paragraph separators U+2028 and U+2029, which end a line for readers that split on them, become
/// <c>\u2028</c> and <c>\u2029</c>. Everything else is written as it is.
/// </remarks>
internal static class LogEscaping
{
    /// <summary>The longest escape: <c>\u2028</c>.</summary>
    public const int MaxEscapedLength = 6;

    /// <summary>How many characters <paramref name="c"/> takes once escaped: 1 when it is written as it is.</summary>
    public static int EscapedLength(char c) => c switch
    {
        '"' or '\\' => 2,
        '\u2028' or '\u2029' => 6,
        _ when c < ' ' || c is >= '\x7F' and <= '\x9F' => 4,
        _ => 1,
    };

    /// <summary>Writes the escape of <paramref name="c"/> (one that <see cref="EscapedLength"/> says takes more than 1) and returns its length.</summary>
    public static int Escape(char c, Span<char> destination)
    {
        switch (c)
        {
            case '"' or '\\':
                destination[0] = '\\';
                destination[1] = c;
                return 2;
            case '\u2028' or '\u2029':
                "\\u202".CopyTo(destination);
                destination[5] = c == '\u2028' ? '8' : '9';
                return 6;
            default:
                destination[0] = '\\';
                destination[1] = 'x';
                destination[2] = HexDigit(c >> 4);
                destination[3] = HexDigit(c & 0xF);
                return 4;
        }
    }

    private static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'a' + (value - 10));
}
