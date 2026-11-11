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
/// on that the call carries, so two requests that differ in anything the provider is sent never share an entry. The credentials are the whole <c>Cookie</c>,
/// <c>Authorization</c> and <c>Proxy-Authorization</c> headers (a provider's session cookie
/// has a name only the operator knows, and an answer may depend on more than one) and every other header the call carries
/// (<see cref="CompiledAuthProvider.KeyHeaders"/>: an API key it decides on, and the <c>User-Agent</c> and <c>Accept</c> hints a rule may read), the client address (Authelia's rules match networks, and
/// sessions can be bound to one), the scheme, the host, and, at <see cref="ForwardAuthCacheScope.Request"/> grain, also the method and the request target. Each field is
/// length-prefixed, a header also by its number of lines (so an absent header and an empty one differ), so no two different requests frame to
/// the same bytes. The grain is a property of the entry, not of the request: a <c>host</c>-scoped provider's allow is stored at host grain only when the
/// provider vouched for an identity (it answered with an identity header), and at request grain otherwise, and a lookup probes both. What a request
/// carries says nothing about that (any cookie, any header the provider reads, can be sent by an anonymous visitor), so the key never decides it.
/// The key is the first 64 bits of SHA-256 over a per-process random
/// secret and the framed fields, as the vault's password verification cache names its entries: no key ever leaves the process, so there is
/// nothing for an attacker to measure, the cache holds no credential, and no one can choose a request whose key collides with another's.
/// </summary>
internal static class ForwardAuthKey
{
    private const int StackLimit = 2048;

    /// <summary>The key of a request at one grain: <see cref="ForwardAuthCacheScope.Host"/> leaves the method and the target out,
    /// <see cref="ForwardAuthCacheScope.Request"/> keeps them.</summary>
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

        var length = secret.Length + 2 + 16
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
            buffer[at++] = state.IsHttps ? (byte)1 : (byte)0;
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
