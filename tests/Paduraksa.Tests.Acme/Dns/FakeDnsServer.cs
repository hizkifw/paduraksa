using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Paduraksa.Tests.Acme.Dns;

/// <summary>A query the fake server received.</summary>
internal sealed record FakeQuery(ushort Id, string Name, ushort Type, bool RecursionDesired, bool Tcp, bool Edns = true);

/// <summary>Builds DNS messages byte by byte, so a test can also build the broken ones.</summary>
internal static class DnsMessages
{
    public static byte[] Name(string name)
    {
        List<byte> bytes = [];
        foreach (string label in name.Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }

        bytes.Add(0);
        return [.. bytes];
    }

    public static byte[] UInt16(int value) => [(byte)(value >> 8), (byte)value];

    public static byte[] Txt(params string[] strings) => [.. strings.SelectMany(s => new byte[] { (byte)s.Length }.Concat(Encoding.ASCII.GetBytes(s)))];

    /// <summary>A record: <paramref name="nameBytes"/> (a full name or a pointer), type, class IN, TTL 60, then the data.</summary>
    public static byte[] Record(byte[] nameBytes, int type, byte[] data, int? declaredLength = null) =>
        [.. nameBytes, .. UInt16(type), .. UInt16(1), 0, 0, 0, 60, .. UInt16(declaredLength ?? data.Length), .. data];

    /// <summary>A response with one question (<paramref name="name"/>, <paramref name="type"/>) and the given answer records.</summary>
    public static byte[] Response(ushort id, string name, int type, IEnumerable<byte[]> answers, bool authoritative = true, bool truncated = false, int rcode = 0, int? declaredAnswerCount = null)
    {
        List<byte[]> records = [.. answers];
        int flags = 0x8000 | (authoritative ? 0x0400 : 0) | (truncated ? 0x0200 : 0) | rcode;
        return
        [
            .. UInt16(id), .. UInt16(flags), .. UInt16(1), .. UInt16(declaredAnswerCount ?? records.Count), .. UInt16(0), .. UInt16(0),
            .. Name(name), .. UInt16(type), .. UInt16(1),
            .. records.SelectMany(r => r),
        ];
    }

    /// <summary>The pointer to the question name that sits right after the 12-byte header.</summary>
    public static byte[] QuestionPointer => [0xC0, 0x0C];
}

/// <summary>
/// A tiny DNS server on 127.0.0.1 (UDP and TCP on one port) that stands in for an authoritative server or a resolver:
/// it answers from a table, can set or clear the AA bit, truncate over UDP, and be told to send anything else instead.
/// </summary>
internal sealed class FakeDnsServer : IDisposable
{
    private readonly Socket _udp;
    private readonly Socket _tcp;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<(string Name, int Type, byte[] Data)> _records = [];
    private readonly List<FakeQuery> _queries = [];
    private readonly Lock _gate = new();

    /// <param name="address">The address to listen on; loopback by default.</param>
    /// <param name="port">The port, for UDP and TCP; 0 picks a free one.</param>
    public FakeDnsServer(IPAddress? address = null, int port = 0)
    {
        address ??= IPAddress.Loopback;

        // The same port for UDP and TCP: take a free UDP port, then the TCP port with that number (another test may hold it; try again).
        for (int attempt = 0; ; attempt++)
        {
            Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            Socket tcp = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                udp.Bind(new IPEndPoint(address, port));
                tcp.Bind(new IPEndPoint(address, ((IPEndPoint)udp.LocalEndPoint!).Port));
                tcp.Listen(8);
                (_udp, _tcp) = (udp, tcp);
                break;
            }
            catch (SocketException) when (port == 0 && attempt < 20)
            {
                udp.Dispose();
                tcp.Dispose();
            }
            catch (SocketException)
            {
                udp.Dispose();
                tcp.Dispose();
                throw;
            }
        }

        EndPoint = (IPEndPoint)_udp.LocalEndPoint!;
        _ = Task.Run(() => UdpLoopAsync(_stop.Token));
        _ = Task.Run(() => TcpLoopAsync(_stop.Token));
    }

    /// <summary>
    /// <paramref name="count"/> servers on 127.0.0.2, 127.0.0.3, ... that share one port (as real name servers share port 53), or
    /// null when this machine does not answer on those loopback addresses.
    /// </summary>
    public static List<FakeDnsServer>? TryCreateGroup(int count)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            List<FakeDnsServer> group = [];
            try
            {
                group.Add(new FakeDnsServer(IPAddress.Parse("127.0.0.2")));
                for (int i = 1; i < count; i++)
                {
                    group.Add(new FakeDnsServer(IPAddress.Parse($"127.0.0.{i + 2}"), group[0].EndPoint.Port));
                }

                return group;
            }
            catch (SocketException)
            {
                group.ForEach(g => g.Dispose());
            }
        }

        return null;
    }

    public IPEndPoint EndPoint { get; }

    /// <summary>Whether answers carry the AA bit.</summary>
    public bool Authoritative { get; set; } = true;

    /// <summary>Answers over UDP are cut to a truncated empty answer (TC set); TCP answers are whole.</summary>
    public bool TruncateUdp { get; set; }

    /// <summary>The response code of table answers.</summary>
    public int ResponseCode { get; set; }

    /// <summary>When set and it returns datagrams, those are sent instead of the table's answer (nothing is sent for an empty list).</summary>
    public Func<FakeQuery, IReadOnlyList<byte[]>?>? Override { get; set; }

    public IReadOnlyList<FakeQuery> Queries
    {
        get
        {
            lock (_gate)
            {
                return [.. _queries];
            }
        }
    }

    public void Add(string name, int type, byte[] data)
    {
        lock (_gate)
        {
            _records.Add((name.Trim('.').ToLowerInvariant(), type, data));
        }
    }

    public void AddTxt(string name, string value) => Add(name, 16, DnsMessages.Txt(value));

    public void RemoveAll(string name, int type)
    {
        lock (_gate)
        {
            _ = _records.RemoveAll(r => string.Equals(r.Name, name.Trim('.'), StringComparison.OrdinalIgnoreCase) && r.Type == type);
        }
    }

    public void AddA(string name, IPAddress address) => Add(name, address.AddressFamily == AddressFamily.InterNetwork ? 1 : 28, address.GetAddressBytes());

    public void AddNs(string zone, string target) => Add(zone, 2, DnsMessages.Name(target));

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
        _tcp.Dispose();
        _stop.Dispose();
    }

    /// <summary>The answer the table gives to <paramref name="query"/>.</summary>
    public byte[] TableAnswer(FakeQuery query, bool truncated)
    {
        List<byte[]> answers;
        lock (_gate)
        {
            answers = truncated
                ? []
                : [.. _records.Where(r => string.Equals(r.Name, query.Name, StringComparison.OrdinalIgnoreCase) && r.Type == query.Type).Select(r => DnsMessages.Record(DnsMessages.QuestionPointer, r.Type, r.Data))];
        }

        return DnsMessages.Response(query.Id, query.Name, query.Type, answers, Authoritative, truncated, ResponseCode);
    }

    private static FakeQuery? Parse(byte[] message, bool tcp)
    {
        if (message.Length < 17)
        {
            return null;
        }

        ushort id = BinaryPrimitives.ReadUInt16BigEndian(message);
        bool rd = (message[2] & 1) != 0;
        int position = 12;
        List<string> labels = [];
        while (message[position] != 0)
        {
            int length = message[position++];
            labels.Add(Encoding.ASCII.GetString(message, position, length));
            position += length;
        }

        ushort type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(position + 1));
        return new FakeQuery(id, string.Join('.', labels), type, rd, tcp, BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(10)) > 0);
    }

    private IReadOnlyList<byte[]> Answer(FakeQuery query)
    {
        lock (_gate)
        {
            _queries.Add(query);
        }

        return Override?.Invoke(query) ?? [TableAnswer(query, truncated: !query.Tcp && TruncateUdp)];
    }

    private async Task UdpLoopAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveFromResult received = await _udp.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken);
                if (Parse(buffer[..received.ReceivedBytes], tcp: false) is { } query)
                {
                    foreach (byte[] datagram in Answer(query))
                    {
                        _ = await _udp.SendToAsync(datagram, SocketFlags.None, received.RemoteEndPoint, cancellationToken);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped.
        }
    }

    private async Task TcpLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket client = await _tcp.AcceptAsync(cancellationToken);
                _ = Task.Run(() => ServeAsync(client, cancellationToken), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped.
        }
    }

    private async Task ServeAsync(Socket client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                byte[] prefix = new byte[2];
                while (await ReadAsync(client, prefix, cancellationToken))
                {
                    byte[] message = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)];
                    if (!await ReadAsync(client, message, cancellationToken))
                    {
                        return;
                    }

                    if (Parse(message, tcp: true) is { } query)
                    {
                        foreach (byte[] answer in Answer(query))
                        {
                            _ = await client.SendAsync((byte[])[.. DnsMessages.UInt16(answer.Length), .. answer], SocketFlags.None, cancellationToken);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Closed.
            }
        }
    }

    private static async Task<bool> ReadAsync(Socket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
