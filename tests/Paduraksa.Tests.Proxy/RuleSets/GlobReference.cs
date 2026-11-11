using System.Text;
using System.Text.RegularExpressions;

namespace Paduraksa.Tests.Proxy.RuleSets;

/// <summary>
/// The glob rules written as a backtracking regular expression, which is slow and obvious: <c>*</c> is <c>[^/]*</c>, <c>**</c>
/// is <c>.*</c>, and a <c>**</c> between two slashes (<c>/**/</c>) is the slash-closed run <c>(?:.*/)?</c>, which may be empty.
/// Case is folded for ASCII only on both sides, because a glob is ASCII and a character above it matches a wildcard alone.
/// </summary>
internal static class GlobReference
{
    public static bool IsMatch(string pattern, string path) =>
        Build(pattern).IsMatch(Lower(path));

    // A regex per pattern is cheap next to what it checks, and a run draws few patterns twice.
    private static Regex Build(string pattern)
    {
        List<(char Kind, char Character)> tokens = [];
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '*')
            {
                tokens.Add(('L', LowerChar(pattern[i])));
                continue;
            }

            var run = 1;
            while (i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                run++;
                i++;
            }

            tokens.Add((run == 1 ? 'S' : 'A', '*'));
        }

        StringBuilder text = new("^");
        for (var i = 0; i < tokens.Count; i++)
        {
            var (kind, character) = tokens[i];
            if (kind == 'L')
            {
                text.Append(Regex.Escape(character.ToString()));
            }
            else if (kind == 'S')
            {
                text.Append("[^/]*");
            }
            else if (i > 0 && i + 1 < tokens.Count && tokens[i - 1] == ('L', '/') && tokens[i + 1] == ('L', '/'))
            {
                text.Append("(?:.*/)?");
                i++;
            }
            else
            {
                text.Append(".*");
            }
        }

        return new Regex(text.Append('$').ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));
    }

    private static string Lower(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (var i = 0; i < span.Length; i++)
        {
            span[i] = LowerChar(source[i]);
        }
    });

    private static char LowerChar(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
}
