using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Connections;

namespace Paduraksa.Tests.Proxy.Net;

/// <summary>Builders for PROXY protocol headers and a pipe-backed connection for middleware tests.</summary>
internal static class ProxyProtocolTestData
{
    public static readonly byte[] V2Signature = [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];

    public static byte[] V1(string line) => Encoding.ASCII.GetBytes(line);

    /// <summary>A v2 header. <paramref name="family"/>: 0 UNSPEC, 1 INET, 2 INET6, 3 UNIX; <paramref name="transport"/>:
    /// 0 UNSPEC, 1 STREAM, 2 DGRAM; command 0 LOCAL, 1 PROXY.</summary>
    public static byte[] V2(
        int command = 1,
        int family = 1,
        int transport = 1,
        string source = "203.0.113.7",
        string destination = "192.0.2.1",
        ushort sourcePort = 50123,
        ushort destinationPort = 443,
        int tlvBytes = 0,
        int? lengthOverride = null,
        int version = 2)
    {
        var addresses = new List<byte>();
        switch (family)
        {
            case 1:
                addresses.AddRange(IPAddress.Parse(source).GetAddressBytes());
                addresses.AddRange(IPAddress.Parse(destination).GetAddressBytes());
                addresses.AddRange(Port(sourcePort));
                addresses.AddRange(Port(destinationPort));
                break;
            case 2:
                addresses.AddRange(IPAddress.Parse(source).GetAddressBytes());
                addresses.AddRange(IPAddress.Parse(destination).GetAddressBytes());
                addresses.AddRange(Port(sourcePort));
                addresses.AddRange(Port(destinationPort));
                break;
            case 3:
                addresses.AddRange(new byte[216]);
                break;
            default:
                break;
        }

        // TLVs: PP2_TYPE_NOOP (0x04) entries with zero-filled values.
        var remaining = tlvBytes;
        while (remaining >= 3)
        {
            var valueLength = Math.Min(remaining - 3, 200);
            addresses.Add(0x04);
            addresses.AddRange(Port((ushort)valueLength));
            addresses.AddRange(new byte[valueLength]);
            remaining -= 3 + valueLength;
        }

        var header = new byte[16 + addresses.Count];
        V2Signature.CopyTo(header, 0);
        header[12] = (byte)((version << 4) | command);
        header[13] = (byte)((family << 4) | transport);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14), (ushort)(lengthOverride ?? addresses.Count));
        addresses.CopyTo(header, 16);
        return header;
    }

    private static byte[] Port(ushort port)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, port);
        return bytes;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (var part in parts)
        {
            all.AddRange(part);
        }

        return [.. all];
    }
}

/// <summary>A connection over in-memory pipes that records <see cref="Abort"/>.</summary>
internal sealed class TestConnection : DefaultConnectionContext
{
    private readonly Pipe _input = new();
    private readonly Pipe _output = new();

    public TestConnection(EndPoint? remote)
        : base("test")
    {
        RemoteEndPoint = remote;
        Transport = new DuplexPipe(_input.Reader, _output.Writer);
    }

    /// <summary>What the "client" writes into the connection.</summary>
    public PipeWriter ClientWriter => _input.Writer;

    public ConnectionAbortedException? AbortReason { get; private set; }

    public override void Abort(ConnectionAbortedException abortReason) => AbortReason = abortReason;

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }
}
