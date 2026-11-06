using System.Net;
using System.Net.Sockets;

using Microsoft.AspNetCore.Http;

namespace Paduraksa.Proxy.Gate;

/// <summary>
/// The gate's seam for the client address (PLAN §7.2 C, §7.6). M1's default reads the TCP peer; integration replaces
/// it with the resolver from <c>Paduraksa.Proxy.Net</c> (PROXY protocol, trusted proxies, Cloudflare).
/// Must be allocation-free: it runs on every request.
/// </summary>
public interface IClientAddressSource
{
    /// <summary>The client address as a 128-bit key, IPv4 as IPv4-mapped IPv6; <see cref="UInt128.Zero"/> when unknown.</summary>
    UInt128 GetClientAddress(HttpContext context);
}

/// <summary>Default seam implementation: <c>HttpContext.Connection.RemoteIpAddress</c>, no proxy awareness.</summary>
public sealed class RemoteIpClientAddressSource : IClientAddressSource
{
    public static RemoteIpClientAddressSource Instance { get; } = new();

    public UInt128 GetClientAddress(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Connection.RemoteIpAddress is { } address ? ClientAddressKey.From(address) : UInt128.Zero;
    }
}

/// <summary>Converts addresses to the 128-bit key the gate and its policies use. Allocation-free.</summary>
internal static class ClientAddressKey
{
    private static readonly UInt128 MappedPrefix = new(0, 0x0000_FFFF_0000_0000UL);

    /// <summary>IPv4 (and IPv4-mapped IPv6) become <c>::ffff:a.b.c.d</c>, so dual-mode TCP and QUIC agree.</summary>
    public static UInt128 From(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        Span<byte> bytes = stackalloc byte[16];
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            _ = address.TryWriteBytes(bytes, out _);
            uint v4 = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            return MappedPrefix | v4;
        }

        _ = address.TryWriteBytes(bytes, out _);
        ulong high = 0;
        ulong low = 0;
        for (int i = 0; i < 8; i++)
        {
            high = (high << 8) | bytes[i];
            low = (low << 8) | bytes[i + 8];
        }

        return new UInt128(high, low);
    }
}
