using System.Buffers;

namespace Paduraksa.Proxy.Snapshot;

/// <summary>
/// The canonical <b>matching</b> view of a request path. Routes (and, later, path rules) are matched against it, while
/// the request itself is forwarded upstream as received. The view is lenient on purpose: many upstreams treat these
/// spellings as the same path, so a policy on <c>/admin</c> must see them as <c>/admin</c> too. Over-matching only
/// makes a policy stricter.
/// <list type="bullet">
/// <item><c>\</c>, <c>%2F</c> and <c>%5C</c> (any case; Kestrel leaves <c>%2F</c> encoded) separate segments like <c>/</c>;</item>
/// <item>repeated separators collapse;</item>
/// <item><c>;…</c> segment parameters are dropped;</item>
/// <item>trailing <c>.</c> and spaces are trimmed per segment;</item>
/// <item><c>.</c> and <c>..</c> segments are resolved (Kestrel already does this for <c>/</c>, but not across <c>\</c>);</item>
/// <item>a trailing separator is kept; case is kept (matching ignores it).</item>
/// </list>
/// Computing the view never allocates: canonical paths (the common case) are used as they are, others are rewritten
/// into a <see langword="stackalloc"/> buffer of <see cref="StackLimit"/> characters, or a pooled array for longer
/// paths. Kestrel's 8 KiB request-line limit bounds those.
/// Public (the cold-path members) so config validation (Application) checks route paths with the same rules.
/// </summary>
public static class RoutePath
{
    /// <summary>Paths shorter than this are canonicalised on the stack (512 bytes).</summary>
    internal const int StackLimit = 256;

    private static readonly SearchValues<char> Suspicious = SearchValues.Create("\\%;");

    /// <summary>True when <paramref name="path"/> is already its own canonical view.</summary>
    public static bool IsCanonical(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty || path[0] != '/' || path.ContainsAny(Suspicious))
        {
            return false;
        }

        // Each segment may not be empty (except a trailing one), ".", "..", or end in '.' or ' '.
        int start = 1;
        while (start <= path.Length)
        {
            int length = path[start..].IndexOf('/');
            int end = length < 0 ? path.Length : start + length;
            ReadOnlySpan<char> segment = path[start..end];
            if (segment.IsEmpty)
            {
                if (end != path.Length)
                {
                    return false;
                }
            }
            else if (segment[^1] is '.' or ' ')
            {
                return false;
            }

            start = end + 1;
        }

        return true;
    }

    /// <summary>Writes the canonical view of <paramref name="path"/> into <paramref name="destination"/> (at least
    /// <c>path.Length + 1</c> long) and returns its length.</summary>
    internal static int Canonicalize(ReadOnlySpan<char> path, Span<char> destination)
    {
        int written = 0;
        int i = 0;
        bool endsWithSeparator = false;
        while (i < path.Length)
        {
            int separator = SeparatorLength(path, i);
            if (separator > 0)
            {
                i += separator;
                endsWithSeparator = true;
                continue;
            }

            int start = i;
            while (i < path.Length && SeparatorLength(path, i) == 0)
            {
                i++;
            }

            endsWithSeparator = false;
            ReadOnlySpan<char> segment = path[start..i];
            int parameters = segment.IndexOf(';');
            if (parameters >= 0)
            {
                segment = segment[..parameters];
            }

            if (segment is ".")
            {
                continue;
            }

            if (segment is "..")
            {
                int parent = destination[..written].LastIndexOf('/');
                written = parent < 0 ? 0 : parent;
                continue;
            }

            segment = segment.TrimEnd(". ");
            if (segment.IsEmpty)
            {
                continue;
            }

            destination[written++] = '/';
            segment.CopyTo(destination[written..]);
            written += segment.Length;
        }

        if (written == 0 || endsWithSeparator)
        {
            destination[written++] = '/';
        }

        return written;
    }

    /// <summary>Allocating convenience for cold paths (the compiler).</summary>
    public static string Canonicalize(string path)
    {
        if (IsCanonical(path))
        {
            return path;
        }

        char[] buffer = new char[path.Length + 1];
        return new string(buffer, 0, Canonicalize(path, buffer));
    }

    private static int SeparatorLength(ReadOnlySpan<char> path, int i)
    {
        char c = path[i];
        if (c is '/' or '\\')
        {
            return 1;
        }

        if (c == '%' && i + 2 < path.Length)
        {
            char hi = path[i + 1];
            char lo = (char)(path[i + 2] | 0x20);
            if ((hi == '2' && lo == 'f') || (hi == '5' && lo == 'c'))
            {
                return 3;
            }
        }

        return 0;
    }
}
