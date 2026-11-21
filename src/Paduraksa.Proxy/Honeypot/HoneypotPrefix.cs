using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// What a trap prefix may be. The gate compares it, ordinally, with the canonical path of a request, so a prefix that is not
/// its own canonical spelling could never match; and it sits in every link of the trap, so it stays short and plain.
/// </summary>
public static class HoneypotPrefix
{
    private const string WellKnown = "/.well-known/";

    /// <summary>Why <paramref name="path"/> can't be a trap prefix, or null when it can.</summary>
    public static string? Problem(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 3 || path[0] != '/' || path[^1] != '/')
        {
            return "the trap path must start and end with '/' and name at least one segment, such as /archive-k7f3/";
        }

        if (path.Length > Paduraksa.Core.Model.HoneypotLimits.MaxPathLength)
        {
            return $"the trap path can't be longer than {Paduraksa.Core.Model.HoneypotLimits.MaxPathLength} characters";
        }

        foreach (var c in path)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' or '/'))
            {
                return "the trap path can only hold letters, digits and - _ . ~ between its slashes";
            }
        }

        if (!RoutePath.IsCanonical(path))
        {
            return "the trap path isn't canonical (empty or dot segments, or a segment ending in a dot); write it as /name/";
        }

        // The gate's own endpoints live there (the proof-of-work verify path, ACME), and a prefix over them would answer them.
        if (path.Contains(WellKnown, StringComparison.OrdinalIgnoreCase))
        {
            return "the trap path can't contain /.well-known/";
        }

        return null;
    }
}
