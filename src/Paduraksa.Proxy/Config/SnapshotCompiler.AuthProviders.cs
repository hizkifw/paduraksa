using Paduraksa.Core.Model;
using Paduraksa.Proxy.ForwardAuth;

namespace Paduraksa.Proxy.Config;

/// <summary>The named auth providers, compiled once per snapshot and shared by every route that names one.</summary>
public static partial class SnapshotCompiler
{
    /// <summary>What a route needs to bind its provider: the compiled providers that compiled.</summary>
    internal sealed record AuthProviderBinding(Dictionary<AuthProviderId, CompiledAuthProvider> Providers);

    private static AuthProviderBinding CompileAuthProviders(
        IReadOnlyList<AuthProviderSpec> specs, ForwardAuthRuntime runtime, bool publish, List<CompileError> errors)
    {
        Dictionary<AuthProviderId, CompiledAuthProvider> providers = [];
        foreach (var spec in specs)
        {
            if (providers.ContainsKey(spec.Id))
            {
                continue;
            }

            var where = CompileErrorSource.AuthProvider(spec.Id);
            var before = errors.Count;
            CheckAuthProvider(spec, where, errors);
            if (errors.Count == before)
            {
                providers.Add(spec.Id, new CompiledAuthProvider(spec, runtime.Bind(spec, publish)));
            }
        }

        if (publish)
        {
            runtime.Retain(providers.Keys.ToHashSet());
        }

        return new AuthProviderBinding(providers);
    }

    /// <summary>The backstop for the values the validator already judged: a provider whose endpoint or header lists are wrong never reaches the gate.</summary>
    private static void CheckAuthProvider(AuthProviderSpec spec, CompileErrorSource where, List<CompileError> errors)
    {
        var url = spec.Url ?? string.Empty;
        if (url.Length > ForwardAuthLimits.MaxUrlLength
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0
            || uri.Fragment.Length > 0
            || uri.HostNameType == UriHostNameType.Unknown)
        {
            errors.Add(new(where, "the endpoint must be an absolute http or https URL with no credentials or fragment"));
        }

        if (spec.Timeout <= TimeSpan.Zero || spec.Timeout > TimeSpan.FromSeconds(ForwardAuthLimits.MaxTimeoutSeconds))
        {
            errors.Add(new(where, $"the timeout must be 1-{ForwardAuthLimits.MaxTimeoutSeconds} seconds"));
        }

        if (spec.CacheTtl < TimeSpan.Zero || spec.CacheTtl > TimeSpan.FromSeconds(ForwardAuthLimits.MaxCacheSeconds))
        {
            errors.Add(new(where, $"the cache time must be 0-{ForwardAuthLimits.MaxCacheSeconds} seconds"));
        }

        if (!Enum.IsDefined(spec.CacheScope))
        {
            errors.Add(new(where, $"unknown cache scope {spec.CacheScope}"));
        }

        CheckHeaderNames(spec.RequestHeaders ?? [], response: false, where, errors);
        CheckHeaderNames(spec.ResponseHeaders ?? [], response: true, where, errors);
        if ((spec.SignInUrl is null) != (spec.SignInReturnParameter is null))
        {
            errors.Add(new(where, "a sign-in URL and its return parameter go together"));
        }
        else if (spec.SignInUrl is { } signIn
            && (signIn.Length is 0 or > ForwardAuthLimits.MaxUrlLength || signIn.AsSpan().ContainsAnyExcept(System.Buffers.SearchValues.Create("!#$%&'()*+,-./0123456789:;=?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[]_abcdefghijklmnopqrstuvwxyz~%"))
                || (!signIn.StartsWith('/') && !(Uri.TryCreate(signIn, UriKind.Absolute, out var signInUri) && signInUri.Scheme is "http" or "https"))
                || signIn.StartsWith("//", StringComparison.Ordinal)))
        {
            errors.Add(new(where, "the sign-in URL must be a path on the host or an absolute http or https URL"));
        }
        else if (spec.SignInReturnParameter is { } parameter && (parameter.Length is 0 or > ForwardAuthLimits.MaxHeaderNameLength
            || parameter.AsSpan().ContainsAnyExcept(System.Buffers.SearchValues.Create("-._~0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"))))
        {
            errors.Add(new(where, "the return parameter must be letters, digits and -._~"));
        }
    }

    private static void CheckHeaderNames(IReadOnlyList<string> names, bool response, CompileErrorSource where, List<CompileError> errors)
    {
        if (names.Count > ForwardAuthLimits.MaxHeaderNames)
        {
            errors.Add(new(where, $"at most {ForwardAuthLimits.MaxHeaderNames} header names"));
        }

        foreach (var name in names)
        {
            if (ForwardAuthHeaderNames.Problem(name ?? string.Empty, response) is { } problem)
            {
                errors.Add(new(where, problem));
            }
        }
    }

    internal sealed partial class CompileContext
    {
        /// <summary>The compiled provider <paramref name="id"/> names, or null for none (a missing one is the route's compile error).</summary>
        public CompiledAuthProvider? AuthProviderOf(AuthProviderId? id) =>
            id is { } named && AuthProviders.Providers.TryGetValue(named, out var provider) ? provider : null;
    }
}
