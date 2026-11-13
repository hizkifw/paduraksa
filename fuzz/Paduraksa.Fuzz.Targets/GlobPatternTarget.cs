using System.Text;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.RuleSets;

namespace Paduraksa.Fuzz;

/// <summary>
/// A rule set's path glob and the path it is matched against: <see cref="GlobPattern.TryCompile"/> and <see cref="GlobPattern.IsMatch"/>. The input is a length byte, that many
/// bytes of pattern, and the rest as the path, both read as Latin-1 text (so a character above 0x7F reaches the parser). Properties: no exception; the same verdict twice;
/// a pattern compiles exactly when it starts with a slash, holds only printable ASCII and fits <see cref="RuleSetLimits.MaxGlobTokens"/>, and says why when it doesn't; a match is
/// the answer of a plain memoised recursion over the same rules (no automaton in it), whatever the path; case in the path never changes it; a match allocates nothing; and a long path
/// against a pattern full of wildcards still takes time in proportion to the path (the automaton reads each character once).
/// </summary>
internal static class GlobPatternTarget
{
    // Far beyond any request target, so the longest input libFuzzer makes is covered, and small enough for the reference's table.
    private const int ReferencePathLimit = 600;

    public static FuzzTarget Target { get; } = new(
        "glob-pattern",
        "a rule set's path glob: compiling it, and matching a path with it against an independent recursion",
        Run,
        Seeds);

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        var patternLength = Math.Min(input[0], input.Length - 1);
        var pattern = Encoding.Latin1.GetString(input.Slice(1, patternLength));
        var path = Encoding.Latin1.GetString(input[(1 + patternLength)..]);

        _ = Checks.Deterministic(() => GlobPattern.TryCompile(pattern, out _, out var error) ? "ok" : "refused: " + error, "the compile verdict");
        var ok = GlobPattern.TryCompile(pattern, out var compiledGlob, out var compileError);
        Checks.That(ok == (compiledGlob is not null) && ok == (compileError is null), "a compile verdict, its glob and its reason disagree");
        var tokens = Tokens(pattern);
        var shouldCompile = pattern.Length > 0 && pattern[0] == '/' && pattern.All(static c => c is >= ' ' and < (char)128) && tokens.Count <= RuleSetLimits.MaxGlobTokens;
        Checks.That(ok == shouldCompile, $"'{Shown(pattern)}' {(ok ? "compiled" : "was refused (" + compileError + ")")} and the rules say it {(shouldCompile ? "should" : "should not")}");
        if (compiledGlob is null)
        {
            return;
        }

        Checks.That(compiledGlob.Pattern == pattern, "the compiled glob forgot its pattern");

        var match = Checks.Deterministic(() => compiledGlob.IsMatch(path), "the match verdict");
        Checks.AllocatesNothing(() => _ = compiledGlob.IsMatch(path), "GlobPattern.IsMatch");
        if (path.Length <= ReferencePathLimit)
        {
            var reference = Reference(tokens, path);
            Checks.That(match == reference, $"'{Shown(pattern)}' on '{Shown(path)}': the automaton says {match}, the recursion {reference}");
        }

        // ASCII case never matters; a character above ASCII only ever matches a wildcard, so changing letters cannot change the verdict either.
        var shouted = new string([.. path.Select(static c => c is >= 'a' and <= 'z' ? (char)(c - 32) : c)]);
        Checks.That(compiledGlob.IsMatch(shouted) == match, $"'{Shown(pattern)}': '{Shown(path)}' and the same in capitals differ");

        // A pattern that is only literals is the path itself, ignoring case.
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            Checks.That(match == string.Equals(pattern, path, StringComparison.OrdinalIgnoreCase) || path.Any(static c => c >= 128), $"a literal pattern '{Shown(pattern)}' matched '{Shown(path)}' wrongly");
        }

        CheckLinear(compiledGlob, path);
    }

    /// <summary>A path of 64 KiB through the pattern takes time in proportion: no backtracking, whatever the pattern and the path are.</summary>
    private static void CheckLinear(GlobPattern glob, string path)
    {
        var stretched = new StringBuilder();
        while (stretched.Length < 64 * 1024)
        {
            stretched.Append(path.Length == 0 ? "a" : path);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        _ = glob.IsMatch(stretched.ToString());
        // Microseconds in practice; a second is a failure of the algorithm on any runner, not of the machine.
        Checks.That(watch.Elapsed < TimeSpan.FromSeconds(1), $"'{Shown(glob.Pattern)}' took {watch.Elapsed.TotalMilliseconds:F0} ms on a path of {stretched.Length} characters");
    }

    private enum Kind
    {
        Literal,
        Segment,
        Any,
    }

    private static List<(Kind Kind, char Character)> Tokens(string pattern)
    {
        List<(Kind, char)> tokens = [];
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '*')
            {
                tokens.Add((Kind.Literal, pattern[i] is >= 'A' and <= 'Z' ? (char)(pattern[i] + 32) : pattern[i]));
                continue;
            }

            var run = 1;
            while (i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                run++;
                i++;
            }

            tokens.Add((run == 1 ? Kind.Segment : Kind.Any, '*'));
        }

        return tokens;
    }

    /// <summary>
    /// The rules, read directly: a literal matches itself in any ASCII case, <c>*</c> any run without a slash, <c>**</c> any run, and a <c>**</c> between two slashes (<c>/**/</c>)
    /// also none at all, so that <c>/**/x</c> takes <c>/x</c>. Memoised by (token, position), so a pattern full of wildcards costs tokens times path.
    /// </summary>
    private static bool Reference(List<(Kind Kind, char Character)> tokens, string path)
    {
        var memo = new sbyte[tokens.Count + 2, path.Length + 1];

        bool Match(int token, int at)
        {
            if (token == tokens.Count)
            {
                return at == path.Length;
            }

            ref var seen = ref memo[token, at];
            if (seen != 0)
            {
                return seen > 0;
            }

            var (kind, character) = tokens[token];
            bool result;
            if (kind == Kind.Literal)
            {
                result = at < path.Length && Lower(path[at]) == character && Match(token + 1, at + 1);
            }
            else if (kind == Kind.Segment)
            {
                result = false;
                for (var end = at; !result; end++)
                {
                    result = Match(token + 1, end);
                    if (end == path.Length || path[end] == '/')
                    {
                        break;
                    }
                }
            }
            else if (token > 0 && token + 1 < tokens.Count && tokens[token - 1] == (Kind.Literal, '/') && tokens[token + 1] == (Kind.Literal, '/'))
            {
                // "/**/": the second slash may follow the first at once, or follow some run of path that ends in a slash.
                result = Match(token + 2, at);
                for (var end = at; !result && end < path.Length; end++)
                {
                    result = path[end] == '/' && Match(token + 2, end + 1);
                }
            }
            else
            {
                result = false;
                for (var end = at; !result && end <= path.Length; end++)
                {
                    result = Match(token + 1, end);
                }
            }

            seen = (sbyte)(result ? 1 : -1);
            return result;
        }

        return Match(0, 0);
    }

    private static char Lower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;

    private static string Shown(string text) => text.Length <= 64 ? text : string.Concat(text.AsSpan(0, 64), "...");

    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        static (string, byte[]) Case(string name, string pattern, string path) =>
            (name, [(byte)pattern.Length, .. Encoding.Latin1.GetBytes(pattern), .. Encoding.Latin1.GetBytes(path)]);

        yield return Case("literal", "/robots.txt", "/robots.txt");
        yield return Case("literal-case", "/Admin", "/ADMIN");
        yield return Case("star-segment", "/*.php", "/index.php");
        yield return Case("star-not-across-slash", "/*.php", "/a/index.php");
        yield return Case("double-star", "/wp-**", "/wp-content/uploads/a.png");
        yield return Case("dotfile-anywhere", "/**/.env", "/a/b/.env");
        yield return Case("dotfile-at-root", "/**/.env", "/.env");
        yield return Case("dotfile-miss", "/**/.env", "/a/.envx");
        yield return Case("repeated-skip", "/**/**/x", "/x");
        yield return Case("slash-star-slash", "/a/*/c", "/a/b/c");
        yield return Case("slash-star-slash-empty", "/a/*/c", "/a//c");
        yield return Case("trailing-double-star", "/api/**", "/api/");
        yield return Case("only-wildcards", "/**", "/anything/at/all");
        yield return Case("pathological", "/*a*a*a*a*a*a*a*a*a*a*b", "/" + new string('a', 200));
        yield return Case("non-ascii-path", "/*", "/café");
        yield return Case("non-ascii-pattern", "/café", "/café");
        yield return Case("no-leading-slash", "robots.txt", "/robots.txt");
        yield return Case("control-character", "/a\tb", "/a\tb");
        yield return Case("too-many-tokens", "/" + new string('a', 130), "/" + new string('a', 130));
        yield return Case("empty-pattern", string.Empty, "/");
    }
}
