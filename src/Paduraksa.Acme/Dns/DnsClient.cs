using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Paduraksa.Acme.Dns;

/// <summary>A DNS query got no usable answer (timeout, network error, an answer that is not for the question).</summary>
internal sealed class DnsQueryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Asks one DNS server one question over UDP, and over TCP when the answer is truncated. It does what the propagation check
/// needs and no more: no cache, no retries (the check polls), no search list. Each query gets a random 16-bit ID and its own
/// connected socket (so the kernel drops datagrams from any other address and port); an answer is used only if its ID,
/// its question name (compared without case), type and class match, and a datagram that fails that or does not parse is
/// ignored until the timeout, as an off-path forger would otherwise win by speed.
/// </summary>
internal sealed class DnsClient(TimeSpan queryTimeout)
{
    private const int FormatError = 1;

    /// <summary>Queries <paramref name="server"/> for the records of <paramref name="name"/> and <paramref name="type"/>.</summary>
    /// <param name="server">Address and port of the server.</param>
    /// <param name="name">The name to look up.</param>
    /// <param name="type">The record type.</param>
    /// <param name="recursionDesired">Whether to ask for recursion (a resolver) or not (an authoritative server).</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <exception cref="DnsQueryException">No acceptable answer arrived in time.</exception>
    public async Task<DnsResponse> QueryAsync(IPEndPoint server, string name, DnsRecordType type, bool recursionDesired, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(queryTimeout);
        try
        {
            var response = await QueryUdpAsync(server, name, type, recursionDesired, edns: true, deadline.Token).ConfigureAwait(false);
            var edns = true;
            if (response.ResponseCode == FormatError)
            {
                // A server that does not know EDNS0 answers FORMERR to the OPT record: ask again without it.
                edns = false;
                response = await QueryUdpAsync(server, name, type, recursionDesired, edns, deadline.Token).ConfigureAwait(false);
            }

            return response.Truncated
                ? await QueryTcpAsync(server, name, type, recursionDesired, edns, deadline.Token).ConfigureAwait(false)
                : response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DnsQueryException($"{server} did not answer a {type} query for {name} in time.");
        }
        catch (SocketException ex)
        {
            throw new DnsQueryException($"{server} could not be asked for {name}: {ex.SocketErrorCode}.", ex);
        }
        catch (DnsFormatException ex)
        {
            throw new DnsQueryException($"{server} answered {name} with a malformed message: {ex.Message}", ex);
        }
        catch (EndOfStreamException ex)
        {
            throw new DnsQueryException($"{server} closed the connection while answering {name}.", ex);
        }
    }

    private static ushort NewId() => (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);

    private static bool Answers(DnsResponse response, ushort id, string name, DnsRecordType type) =>
        response.Id == id
        && response.QuestionType == (ushort)type
        && string.Equals(response.QuestionName, name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static async Task<DnsResponse> QueryUdpAsync(IPEndPoint server, string name, DnsRecordType type, bool recursionDesired, bool edns, CancellationToken cancellationToken)
    {
        var id = NewId();
        var query = DnsWire.BuildQuery(id, name, type, recursionDesired, edns);
        using Socket socket = new(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await socket.ConnectAsync(server, cancellationToken).ConfigureAwait(false);
        _ = await socket.SendAsync(query, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[4096];
        while (true)
        {
            var length = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            DnsResponse response;
            try
            {
                response = DnsWire.ParseResponse(buffer.AsSpan(0, length));
            }
            catch (DnsFormatException)
            {
                continue;
            }

            if (Answers(response, id, name, type))
            {
                return response;
            }
        }
    }

    private static async Task<DnsResponse> QueryTcpAsync(IPEndPoint server, string name, DnsRecordType type, bool recursionDesired, bool edns, CancellationToken cancellationToken)
    {
        var id = NewId();
        var query = DnsWire.BuildQuery(id, name, type, recursionDesired, edns);
        using Socket socket = new(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(server, cancellationToken).ConfigureAwait(false);
        var framed = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
        query.CopyTo(framed, 2);
        _ = await socket.SendAsync(framed, SocketFlags.None, cancellationToken).ConfigureAwait(false);

        var prefix = new byte[2];
        await ReadExactlyAsync(socket, prefix, cancellationToken).ConfigureAwait(false);
        var message = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)];
        await ReadExactlyAsync(socket, message, cancellationToken).ConfigureAwait(false);
        var response = DnsWire.ParseResponse(message);
        return Answers(response, id, name, type)
            ? response
            : throw new DnsQueryException($"{server} answered over TCP with a message that is not the answer to the question.");
    }

    private static async Task ReadExactlyAsync(Socket socket, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            buffer = buffer[read..];
        }
    }
}
