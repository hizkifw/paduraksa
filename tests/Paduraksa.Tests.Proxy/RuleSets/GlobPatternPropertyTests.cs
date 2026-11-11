using CsCheck;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.RuleSets;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Proxy.RuleSets;

/// <summary>The glob automaton against a backtracking regex of the same rules (<see cref="GlobReference"/>).</summary>
public sealed class GlobPatternPropertyTests
{
    // Wildcards and slashes are most of a pattern that matters: runs of them, a slash-closed run, and the dots of a dotfile.
    private static readonly string[] PatternPieces =
    [
        "/", "a", "b", "A", ".", "-", "*", "**", "/**/", "/*/", "**/", "/**", "ab", ".env", "*.php", "/**/**/", "/**/*",
    ];

    private static readonly Gen<string> Patterns = Gen.Int[0, PatternPieces.Length - 1].Select(i => PatternPieces[i]).List[0, 7]
        .Select(static parts => "/" + string.Concat(parts));

    // The characters a path may hold, with a few above ASCII (which only a wildcard matches) and a backslash and a space.
    private static readonly Gen<string> Paths = Property.StringOf("/abAB.-é \\", 0, 14);

    [Fact]
    public void TheAutomatonAgreesWithTheBacktrackingModelOnAnyPath() =>
        Gen.Select(Patterns, Paths).Check(
            20_000,
            t => Agree(t.Item1, t.Item2));

    [Fact]
    public void TheAutomatonAgreesWithTheModelOnPathsBuiltFromThePattern() =>
        Gen.Select(Patterns, Property.Pick("", "/", "a", "/a/b/").List[0, 6], Gen.Int[0, 1000])
            .Check(
                20_000,
                t =>
                {
                    // Each wildcard stands for something, so most of these paths are ones the pattern was written for.
                    var fills = t.Item2;
                    var at = 0;
                    var path = new System.Text.StringBuilder();
                    foreach (var c in t.Item1)
                    {
                        if (c == '*')
                        {
                            path.Append(fills.Count == 0 ? string.Empty : fills[(at++ + t.Item3) % fills.Count]);
                        }
                        else
                        {
                            path.Append(t.Item3 % 7 == 0 ? char.ToUpperInvariant(c) : c);
                        }
                    }

                    Agree(t.Item1, path.ToString());
                });

    [Fact]
    public void ARunOfSlashDoubleStarSlashMatchesWhatTheSameRunWithOneStarDoes() =>
        Gen.Select(Gen.Int[1, 5], Property.StringOf("/ab", 0, 10), Gen.Int[0, 2]).Check(
            10_000,
            t =>
            {
                // "/**/**/.../x" takes "/x" and everything "/**/x" does: repeating the run adds no path.
                var many = "/" + string.Concat(Enumerable.Repeat("**/", t.Item1)) + "x";
                var one = "/**/x";
                var path = t.Item3 switch
                {
                    0 => "/" + t.Item2 + "/x",
                    1 => "/x",
                    _ => t.Item2 + "x",
                };

                Assert.Equal(Compile(one).IsMatch(path), Compile(many).IsMatch(path));
                Agree(many, path);
            });

    [Fact]
    public void AGlobCompilesExactlyWhenItIsAsciiStartsWithASlashAndFitsTheTokenLimit() =>
        Gen.Select(Property.StringOf("/*aé\t", 0, 9), Gen.Int[0, 140], Gen.Bool).Check(
            20_000,
            t =>
            {
                var pattern = t.Item1 + (t.Item3 ? "/" + new string('a', t.Item2) : string.Empty);
                var tokens = 0;
                for (var i = 0; i < pattern.Length; i++)
                {
                    tokens++;
                    while (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        i++;
                    }
                }

                var expected = pattern.Length > 0 && pattern[0] == '/' && pattern.All(static c => c >= ' ' && c < 128) && tokens <= RuleSetLimits.MaxGlobTokens;

                Assert.Equal(expected, GlobPattern.TryCompile(pattern, out var glob, out var error));
                Assert.Equal(expected, glob is not null);
                Assert.Equal(!expected, error is not null);
            });

    private static void Agree(string pattern, string path)
    {
        var glob = Compile(pattern);
        var expected = GlobReference.IsMatch(pattern, path);

        Assert.True(glob.IsMatch(path) == expected, $"'{pattern}' on '{path}': the automaton says {!expected}, the model {expected}");
    }

    private static GlobPattern Compile(string pattern)
    {
        Assert.True(GlobPattern.TryCompile(pattern, out var glob, out var error), $"{pattern}: {error}");
        return glob!;
    }
}
