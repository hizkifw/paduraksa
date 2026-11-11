using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.RuleSets;

/// <summary>
/// A path glob compiled to a small matcher: <c>*</c> is any run of characters inside a segment, <c>**</c> any run across
/// segments, and a <c>/**/</c> segment none or more segments (so <c>/**/.env</c> takes <c>/.env</c> as well as
/// <c>/a/b/.env</c>), compared ignoring ASCII case against the canonical matching path.
/// <para>The matcher simulates the pattern as an automaton with one bit of state per token, so a match reads each character
/// once whatever the pattern is: no backtracking, no allocation, and a path can't be shaped to make it slow. That is also
/// why a pattern is limited to <see cref="RuleSetLimits.MaxGlobTokens"/> tokens and to ASCII (a character above 0x7F matches only a wildcard).</para>
/// </summary>
public sealed class GlobPattern
{
    private const int TableSize = 128;

    private readonly UInt128[] _literal = new UInt128[TableSize];
    private readonly UInt128 _starSegment;
    private readonly UInt128 _starAny;
    private readonly UInt128 _skipOne;
    private readonly UInt128 _skipTwo;
    private readonly UInt128 _start;
    private readonly UInt128 _accept;

    private GlobPattern(string pattern, List<Token> tokens)
    {
        Pattern = pattern;
        for (var i = 0; i < tokens.Count; i++)
        {
            var bit = UInt128.One << i;
            switch (tokens[i].Kind)
            {
                case TokenKind.Literal:
                    _literal[tokens[i].Character] |= bit;
                    break;
                case TokenKind.Segment:
                    _starSegment |= bit;
                    _skipOne |= bit;
                    break;
                case TokenKind.Any:
                    _starAny |= bit;
                    _skipOne |= bit;
                    if (i > 0 && i + 1 < tokens.Count && tokens[i - 1] is { Kind: TokenKind.Literal, Character: '/' } && tokens[i + 1] is { Kind: TokenKind.Literal, Character: '/' })
                    {
                        _skipTwo |= bit;
                    }

                    break;
                default:
                    break;
            }
        }

        _accept = UInt128.One << tokens.Count;
        _start = Close(UInt128.One);
    }

    public string Pattern { get; }

    /// <summary>Compiles <paramref name="pattern"/>; false with the reason when it can't be used.</summary>
    public static bool TryCompile(string pattern, out GlobPattern? glob, out string? error)
    {
        glob = null;
        error = null;
        if (string.IsNullOrEmpty(pattern) || pattern[0] != '/')
        {
            error = "a glob starts with '/'";
            return false;
        }

        List<Token> tokens = [];
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c >= TableSize || c < ' ')
            {
                error = "a glob can hold ASCII only; use a regex for anything else";
                return false;
            }

            if (c != '*')
            {
                tokens.Add(new Token(TokenKind.Literal, ToLower(c)));
                continue;
            }

            var run = 1;
            while (i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                run++;
                i++;
            }

            tokens.Add(new Token(run == 1 ? TokenKind.Segment : TokenKind.Any, '*'));
        }

        if (tokens.Count > RuleSetLimits.MaxGlobTokens)
        {
            error = $"a glob can have at most {RuleSetLimits.MaxGlobTokens} characters and wildcards, got {tokens.Count}";
            return false;
        }

        glob = new GlobPattern(pattern, tokens);
        return true;
    }

    /// <summary>Whether the whole of <paramref name="path"/> matches. No allocation.</summary>
    public bool IsMatch(ReadOnlySpan<char> path)
    {
        var state = _start;
        foreach (var c in path)
        {
            var next = c < TableSize ? (state & _literal[ToLower(c)]) << 1 : UInt128.Zero;

            // Having just read the slash before a "/**/" segment, the pattern may also go straight to what follows its second
            // slash: no segments at all. Only on the way in, so that a path's later characters can't start from there. The state
            // it lands on can be another "/**/" (`/**/**/x` takes `/x`), so this repeats until nothing new is reached.
            var skipped = (next & _skipTwo) << 2;
            while ((skipped & ~next) != UInt128.Zero)
            {
                next |= skipped;
                skipped = (next & _skipTwo) << 2;
            }

            next |= state & _starAny;
            if (c != '/')
            {
                next |= state & _starSegment;
            }

            if (next == UInt128.Zero)
            {
                return false;
            }

            state = Close(next);
        }

        return (state & _accept) != UInt128.Zero;
    }

    // A wildcard can match nothing, so a state on one is also a state on the token after it. The "/**/" skip may land on a
    // wildcard that skips again, so closing twice is the whole closure: wildcards are never adjacent (a run of them is one).
    private UInt128 Close(UInt128 states)
    {
        states |= (states & _skipOne) << 1;
        return states | ((states & _skipOne) << 1);
    }

    private static char ToLower(char c) => c is >= 'A' and <= 'Z' ? (char)(c | 0x20) : c;

    private enum TokenKind : byte
    {
        Literal,
        Segment,
        Any,
    }

    private readonly record struct Token(TokenKind Kind, char Character);
}
