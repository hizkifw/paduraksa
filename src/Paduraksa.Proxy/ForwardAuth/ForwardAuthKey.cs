using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;

namespace Paduraksa.Proxy.ForwardAuth;

/// <summary>
/// The name a request's authentication state goes by in the decision cache: a keyed hash of everything the provider's answer may depend
/// on that the call carries. The credentials are the whole <c>Cookie</c>, <c>Authorization</c> and <c>Proxy-Authorization</c> headers (a provider's session cookie
/// has a name only the operator knows, and an answer may depend on more than one) and every other header the provider is told to read
/// (<see cref="CompiledAuthProvider.KeyHeaders"/>: an API key it decides on), the client address (Authelia's rules match networks, and
/// sessions can be bound to one), the host, and for a provider that decides per path also the method and the request target. Each field is
/// length-prefixed, a header also by its number of lines (so an absent header and an empty one differ), so no two different requests frame to
/// the same bytes. A <c>host</c>-scoped key leaves the method and target out: it assumes the provider decides per identity, never per path, which
/// is why an allow for a request with no credentials at all is never stored (<see cref="CarriesCredentials"/>). The key is the first 64 bits of SHA-256 over a per-process random
/// secret and the framed fields, as the vault's password verification cache names its entries: no key ever leaves the process, so there is
/// nothing for an attacker to measure, the cache holds no credential, and no one can choose a request whose key collides with another's.
/// </summary>
internal static class ForwardAuthKey
{
    private const int StackLimit = 2048;

    /// <summary>
    /// Whether the request carries something to tell it from any other from the same address: a non-empty <c>Cookie</c>, <c>Authorization</c> or
    /// <c>Proxy-Authorization</c>, or one of the headers the provider is told to read. An allow for a request that carries none is the provider
    /// letting an anonymous visitor see one path, which says nothing about another, so it is never remembered.
    /// </summary>
    public static bool CarriesCredentials(HttpContext http, string[] keyHeaders)
    {
        var headers = http.Request.Headers;
        if (HasValue(headers.Cookie) || HasValue(headers.Authorization) || HasValue(headers.ProxyAuthorization))
        {
            return true;
        }

        foreach (var name in keyHeaders)
        {
            if (HasValue(headers[name]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasValue(StringValues values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(values[i]))
            {
                return true;
            }
        }

        return false;
    }

    public static ulong Compute(ReadOnlySpan<byte> secret, ForwardAuthCacheScope scope, HttpContext http, GateRequestState state, string[] keyHeaders)
    {
        var request = http.Request;
        var perRequest = scope == ForwardAuthCacheScope.Request;
        var host = request.Headers.Host;
        var target = perRequest ? RawTarget(http) : string.Empty;
        var method = perRequest ? request.Method : string.Empty;
        var cookie = request.Headers.Cookie;
        var authorization = request.Headers.Authorization;
        var proxyAuthorization = request.Headers.ProxyAuthorization;

        var length = secret.Length + 1 + 16
            + Size(host) + Size(method) + Size(target) + Size(cookie) + Size(authorization) + Size(proxyAuthorization);
        foreach (var name in keyHeaders)
        {
            length += Size(request.Headers[name]);
        }

        byte[]? rented = null;
        Span<byte> buffer = length <= StackLimit ? stackalloc byte[StackLimit] : (rented = ArrayPool<byte>.Shared.Rent(length));
        try
        {
            secret.CopyTo(buffer);
            var at = secret.Length;
            buffer[at++] = (byte)scope;
            state.ClientAddress.WriteBytes(buffer.Slice(at, 16));
            at += 16;
            Write(buffer, ref at, host);
            Write(buffer, ref at, method);
            Write(buffer, ref at, target);
            Write(buffer, ref at, cookie);
            Write(buffer, ref at, authorization);
            Write(buffer, ref at, proxyAuthorization);
            foreach (var name in keyHeaders)
            {
                Write(buffer, ref at, request.Headers[name]);
            }

            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(buffer[..at], digest);
            return BinaryPrimitives.ReadUInt64LittleEndian(digest);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }

    /// <summary>The request target as the client sent it (<c>/a%20b?x=1</c>): what <c>X-Forwarded-Uri</c> carries and what a provider's
    /// resource rules read. Kestrel's decoded path would let two different targets look alike.</summary>
    internal static string RawTarget(HttpContext http)
    {
        var raw = http.Features.Get<IHttpRequestFeature>()?.RawTarget;
        return !string.IsNullOrEmpty(raw) && raw[0] == '/'
            ? raw
            : string.Concat(http.Request.PathBase.Value, http.Request.Path.Value, http.Request.QueryString.Value);
    }

    private static int Size(string value) => 4 + Encoding.UTF8.GetByteCount(value);

    private static int Size(StringValues values)
    {
        var size = 4;
        for (var i = 0; i < values.Count; i++)
        {
            size += 4 + Encoding.UTF8.GetByteCount(values[i] ?? string.Empty);
        }

        return size;
    }

    private static void Write(Span<byte> buffer, ref int at, string value)
    {
        var written = Encoding.UTF8.GetBytes(value, buffer[(at + 4)..]);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(at, 4), written);
        at += 4 + written;
    }

    private static void Write(Span<byte> buffer, ref int at, StringValues values)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(at, 4), values.Count);
        at += 4;
        for (var i = 0; i < values.Count; i++)
        {
            Write(buffer, ref at, values[i] ?? string.Empty);
        }
    }
}
