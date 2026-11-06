using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Paduraksa.Tests.Proxy.Testing;

/// <summary>
/// Sends an HTTP/1.1 request with the request target exactly as given. <see cref="HttpClient"/> can't: its
/// <see cref="Uri"/> turns <c>\</c> into <c>/</c>, resolves dot segments and re-escapes characters.
/// </summary>
internal static class RawHttp
{
    public static async Task<(int Status, string Body, string Head)> GetAsync(Uri server, string host, string target, CancellationToken ct)
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, ct);
        await using NetworkStream stream = client.GetStream();
        byte[] request = Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request, ct);

        using MemoryStream received = new();
        await stream.CopyToAsync(received, ct);
        string response = Encoding.UTF8.GetString(received.ToArray());
        int split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string head = split < 0 ? response : response[..split];
        string body = split < 0 ? string.Empty : response[(split + 4)..];
        if (head.Contains("transfer-encoding: chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = Dechunk(body);
        }

        int status = int.Parse(head.AsSpan(9, 3), CultureInfo.InvariantCulture);
        return (status, body, head);
    }

    private static string Dechunk(string chunked)
    {
        StringBuilder body = new();
        int at = 0;
        while (true)
        {
            int lineEnd = chunked.IndexOf("\r\n", at, StringComparison.Ordinal);
            int size = int.Parse(chunked.AsSpan(at, lineEnd - at), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (size == 0)
            {
                return body.ToString();
            }

            body.Append(chunked, lineEnd + 2, size);
            at = lineEnd + 2 + size + 2;
        }
    }
}
