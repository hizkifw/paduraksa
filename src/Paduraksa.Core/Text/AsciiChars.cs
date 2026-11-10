using System.Buffers;

namespace Paduraksa.Core.Text;

/// <summary>
/// Character ranges as <see cref="SearchValues{T}"/>, in place of <c>ContainsAnyExceptInRange</c> and its siblings: on
/// Microsoft's .NET 10.0.14 builds those allocate 96 B per call (the ReadyToRun code in CoreLib, and it never tiers away),
/// which put an allocation on every request's hot path. These are 0 B.
/// </summary>
public static class AsciiChars
{
    /// <summary><c>0</c>-<c>9</c>.</summary>
    public static readonly SearchValues<char> Digits = Range('0', '9');

    /// <summary>Visible ASCII, 0x21-0x7E: what a header token or a Location value may hold.</summary>
    public static readonly SearchValues<char> Visible = Range('\x21', '\x7E');

    /// <summary>Controls and the space, 0x00-0x20: what a local URL must not contain.</summary>
    public static readonly SearchValues<char> ControlOrSpace = Range('\0', ' ');

    private static SearchValues<char> Range(char first, char last)
    {
        Span<char> chars = stackalloc char[last - first + 1];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)(first + i);
        }

        return SearchValues.Create(chars);
    }
}
