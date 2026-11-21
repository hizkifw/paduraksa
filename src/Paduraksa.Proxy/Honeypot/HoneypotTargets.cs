using System.Buffers;

using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>Where a path lies relative to a trap prefix.</summary>
internal static class HoneypotTargets
{
    /// <summary>
    /// Whether the request's path, in the canonical matching view routes use, lies under <paramref name="prefix"/>: one ordinal
    /// compare for a canonical path (the normal case), and the path rewritten on the stack, or in a pooled array past
    /// <see cref="RoutePath.StackLimit"/> characters, for another spelling. 0 B.
    /// </summary>
    public static bool IsUnder(ReadOnlySpan<char> path, string prefix)
    {
        if (RoutePath.IsCanonical(path))
        {
            return path.StartsWith(prefix, StringComparison.Ordinal);
        }

        if (path.Length < RoutePath.StackLimit)
        {
            Span<char> buffer = stackalloc char[RoutePath.StackLimit];
            return buffer[..RoutePath.Canonicalize(path, buffer)].StartsWith(prefix, StringComparison.Ordinal);
        }

        var rented = ArrayPool<char>.Shared.Rent(path.Length + 1);
        try
        {
            return rented.AsSpan(0, RoutePath.Canonicalize(path, rented)).StartsWith(prefix, StringComparison.Ordinal);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Whether the request is a <c>GET</c> or a <c>HEAD</c>, the only methods that read a page.</summary>
    public static bool IsRead(HttpRequest request) => HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
}
