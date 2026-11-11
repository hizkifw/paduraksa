using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.ForwardAuth;

/// <summary>
/// An auth provider as one snapshot compiled it: the label and the header lists a request is judged by, over the <see cref="ForwardAuthProviderState"/>
/// that holds the cache and the client. Every policy that names the provider shares it.
/// </summary>
public sealed class CompiledAuthProvider
{
    private readonly string[] _exact;
    private readonly string[] _prefixes;

    internal CompiledAuthProvider(AuthProviderSpec spec, ForwardAuthProviderState state)
    {
        Id = spec.Id;
        Name = spec.Name;
        Spec = spec;
        State = state;
        List<string> exact = [];
        List<string> prefixes = [];
        foreach (var name in spec.ResponseHeaders)
        {
            if (name.EndsWith('*'))
            {
                prefixes.Add(name[..^1]);
            }
            else
            {
                exact.Add(name);
            }
        }

        _exact = exact.ToArray();
        _prefixes = prefixes.ToArray();
        List<string> requests = new(ForwardAuthLimits.StandardRequestHeaders.Count + spec.RequestHeaders.Count);
        foreach (var standard in ForwardAuthLimits.StandardRequestHeaders)
        {
            requests.Add(standard);
        }

        foreach (var extra in spec.RequestHeaders)
        {
            if (!requests.Exists(known => string.Equals(known, extra, StringComparison.OrdinalIgnoreCase)))
            {
                requests.Add(extra);
            }
        }

        RequestHeaders = requests.ToArray();

        // What the decision cache key hashes beyond the three credential headers it always hashes: every header the provider is told to read.
        List<string> keyed = [];
        foreach (var extra in spec.RequestHeaders)
        {
            if (!IsStandardCredential(extra) && !keyed.Exists(known => string.Equals(known, extra, StringComparison.OrdinalIgnoreCase)))
            {
                keyed.Add(extra);
            }
        }

        KeyHeaders = keyed.ToArray();
        (SignInAuthority, SignInPath) = SignInTarget(spec.SignInUrl);
    }

    public AuthProviderId Id { get; }

    public string Name { get; }

    public AuthProviderSpec Spec { get; }

    public ForwardAuthProviderState State { get; }

    /// <summary>The counters the security overview reads.</summary>
    public ForwardAuthCounters Counters => State.Counters;

    /// <summary>The client headers sent to the endpoint: the standard set, then the provider's own additions.</summary>
    internal string[] RequestHeaders { get; }

    /// <summary>The headers the provider is told to read that are not <c>Cookie</c>, <c>Authorization</c> or <c>Proxy-Authorization</c>: part of the
    /// decision cache key, which always hashes those three.</summary>
    internal string[] KeyHeaders { get; }

    /// <summary>The host of an absolute sign-in URL (with its port, as <see cref="Uri.Authority"/> writes it); null for a path on the protected host or none.</summary>
    internal string? SignInAuthority { get; }

    /// <summary>The canonical path of the sign-in URL without a trailing separator, or null when none is configured.</summary>
    internal string? SignInPath { get; }

    /// <summary>
    /// Whether a header named <paramref name="name"/> is one the provider owns: its answer fills it and a client's own is removed. <c>_</c> and
    /// <c>-</c> are the same character here: a CGI or WSGI upstream folds both into one variable name, so <c>Remote_User</c> from a client would
    /// otherwise be read as the provider's <c>Remote-User</c>.
    /// </summary>
    internal bool Owns(string name)
    {
        foreach (var exact in _exact)
        {
            if (FoldedEquals(exact, name))
            {
                return true;
            }
        }

        foreach (var prefix in _prefixes)
        {
            if (name.Length >= prefix.Length && FoldedEquals(prefix, name.AsSpan(0, prefix.Length)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a request for <paramref name="path"/> on <paramref name="host"/> is already a request for the provider's sign-in page.</summary>
    internal bool IsSignInRequest(string? path, string host)
    {
        if (SignInPath is not { } signIn)
        {
            return false;
        }

        if (SignInAuthority is { } authority && !string.Equals(authority, host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var span = path.AsSpan();
        while (span.Length > 1 && span[^1] == '/')
        {
            span = span[..^1];
        }

        return RoutePath.MatchesCanonical(span, signIn);
    }

    private static bool IsStandardCredential(string name) =>
        string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase);

    private static bool FoldedEquals(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (Fold(left[i]) != Fold(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static char Fold(char c) => c == '_' ? '-' : char.ToUpperInvariant(c);

    private static (string? Authority, string? Path) SignInTarget(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return (null, null);
        }

        string? authority = null;
        string path;
        if (url[0] == '/')
        {
            var query = url.IndexOf('?', StringComparison.Ordinal);
            path = query < 0 ? url : url[..query];
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            authority = uri.Authority;
            path = uri.AbsolutePath;
        }
        else
        {
            return (null, null);
        }

        path = RoutePath.Canonicalize(path);
        while (path.Length > 1 && path[^1] == '/')
        {
            path = path[..^1];
        }

        return (authority, path);
    }
}
