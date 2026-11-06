using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Paduraksa.Tests.Proxy.Tls;

/// <summary>A loopback HTTP endpoint that counts connections and answers 404: the target of a would-be AIA fetch.</summary>
internal sealed class HitCounter : IAsyncDisposable
{
    private static readonly byte[] NotFound = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _hits;

    public HitCounter()
    {
        _listener.Start();
        Url = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/issuer.cer"));
        _loop = AcceptAsync();
    }

    public Uri Url { get; }

    public int Hits => Volatile.Read(ref _hits);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopped.
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            Interlocked.Increment(ref _hits);
            await client.GetStream().WriteAsync(NotFound, _stop.Token);
        }
    }
}
