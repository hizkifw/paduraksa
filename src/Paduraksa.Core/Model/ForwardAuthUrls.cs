using System.Buffers;

namespace Paduraksa.Core.Model;

/// <summary>What a forward-auth provider may name as its endpoint and its sign-in page, shared by the validator and the compiler.</summary>
public static class ForwardAuthUrls
{
    private static readonly SearchValues<char> UrlChars =
        SearchValues.Create("!#$%&'()*+,-./0123456789:;=?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[]_abcdefghijklmnopqrstuvwxyz~");

    private static readonly SearchValues<char> ParameterChars =
        SearchValues.Create("-._~0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ");

    /// <summary>Why <paramref name="url"/> can't be an endpoint, or null: an absolute <c>http</c> or <c>https</c> URL with a host, no credentials and no fragment.</summary>
    public static string? EndpointProblem(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > ForwardAuthLimits.MaxUrlLength
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.HostNameType == UriHostNameType.Unknown
            || url.Contains('#', StringComparison.Ordinal))
        {
            return "the endpoint must be an absolute http or https URL";
        }

        return uri.UserInfo.Length > 0 ? "the endpoint can't carry a user name or password" : null;
    }

    /// <summary>Why <paramref name="url"/> can't be a sign-in page, or null: a path on the protected host, or an absolute <c>http</c> or <c>https</c> URL. A
    /// <c>//host</c> reference and every other scheme are refused: the proxy puts the client's own address into the query, so where it goes matters.</summary>
    public static string? SignInProblem(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > ForwardAuthLimits.MaxUrlLength || url.AsSpan().ContainsAnyExcept(UrlChars))
        {
            return "the sign-in URL must be a path on the host or an absolute http or https URL";
        }

        if (url[0] == '/')
        {
            return url.StartsWith("//", StringComparison.Ordinal) || url.Contains('\\', StringComparison.Ordinal) || url.Contains('#', StringComparison.Ordinal)
                ? "the sign-in URL must be a path on the host or an absolute http or https URL"
                : null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && !url.Contains('#', StringComparison.Ordinal)
            ? null
            : "the sign-in URL must be a path on the host or an absolute http or https URL";
    }

    /// <summary>Why <paramref name="name"/> can't carry the original address to the sign-in page, or null.</summary>
    public static string? ReturnParameterProblem(string? name) =>
        string.IsNullOrEmpty(name) || name.Length > ForwardAuthLimits.MaxHeaderNameLength || name.AsSpan().ContainsAnyExcept(ParameterChars)
            ? "the return parameter must be letters, digits and -._~"
            : null;
}
