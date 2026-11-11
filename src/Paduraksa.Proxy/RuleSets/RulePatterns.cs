using System.Text.RegularExpressions;

using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.RuleSets;

/// <summary>The one definition of how a rule's regex is built, shared by the validator (which reports why one fails) and the
/// compiler.</summary>
public static class RulePatterns
{
    /// <summary>
    /// Compiles <paramref name="pattern"/> without backtracking, ignoring case, with a match timeout; null with the reason in
    /// <paramref name="error"/> when it can't be built. <see cref="RegexOptions.NonBacktracking"/> runs in time linear in the
    /// input, so a pattern like <c>(a+)+$</c> costs no more than a plain one, and it refuses what only a backtracking engine can
    /// do (lookarounds, backreferences, atomic groups) when the pattern is built.
    /// </summary>
    public static Regex? TryCompileRegex(string pattern, out string? error) => TryCompileRegex(pattern, RuleSetLimits.RegexMatchTimeout, out error);

    // The timeout is a parameter for the one test that has to make a match run out of it.
    internal static Regex? TryCompileRegex(string pattern, TimeSpan timeout, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(pattern))
        {
            error = "a regex can't be empty";
            return null;
        }

        if (pattern.Length > RuleSetLimits.MaxPatternLength)
        {
            error = $"a pattern can have at most {RuleSetLimits.MaxPatternLength} characters";
            return null;
        }

        try
        {
            return new Regex(
                pattern,
                RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                timeout);
        }
        catch (ArgumentException ex)
        {
            // RegexParseException is an ArgumentException: its message names the offset and what was wrong.
            error = ex.Message;
            return null;
        }
        catch (NotSupportedException ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
