using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;

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

        _exact = [.. exact];
        _prefixes = [.. prefixes];
        RequestHeaders = [.. ForwardAuthLimits.StandardRequestHeaders, .. spec.RequestHeaders.Where(extra => !ForwardAuthLimits.StandardRequestHeaders.Contains(extra, StringComparer.OrdinalIgnoreCase))];
    }

    public AuthProviderId Id { get; }

    public string Name { get; }

    public AuthProviderSpec Spec { get; }

    public ForwardAuthProviderState State { get; }

    /// <summary>The counters the security overview reads.</summary>
    public ForwardAuthCounters Counters => State.Counters;

    /// <summary>The client headers sent to the endpoint: the standard set, then the provider's own additions.</summary>
    internal string[] RequestHeaders { get; }

    /// <summary>Whether a header named <paramref name="name"/> is one the provider owns: its answer fills it and a client's own is removed.</summary>
    internal bool Owns(string name)
    {
        foreach (var exact in _exact)
        {
            if (string.Equals(exact, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var prefix in _prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The names an upstream request must not carry from the client, exact ones; the prefixes are in <see cref="Prefixes"/>.</summary>
    internal string[] ExactNames => _exact;

    internal string[] Prefixes => _prefixes;
}
