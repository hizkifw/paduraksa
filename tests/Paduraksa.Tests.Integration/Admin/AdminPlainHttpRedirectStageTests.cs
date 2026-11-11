using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;

using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Server.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// The connection stage on its own, over a pipe pair instead of a socket: the peek that leaves a ClientHello for the TLS
/// middleware, the plain-HTTP answer, and the ways a connection ends without one (silence, a close, a head cut short).
/// Cheap enough to cover what a real host would make expensive: a ten-second head timeout, or the exact interleaving of
/// reads that a socket can't be made to produce on demand.
/// </summary>
public sealed class AdminPlainHttpRedirectStageTests
{
    private const byte TlsHandshakeRecord = 0x16;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_plain_request_is_answered_with_the_redirect_and_the_rest_of_the_pipeline_is_not_reached()
    {
        var (connection, requests, responses) = NewConnection();
        var reached = false;
        await requests.Writer.WriteAsync(Encoding.ASCII.GetBytes("GET /x?y=1 HTTP/1.1\r\nHost: admin.example\r\n\r\n"), Ct);

        await RunAsync(connection, _ =>
        {
            reached = true;
            return Task.CompletedTask;
        });

        var answer = Encoding.ASCII.GetString(await ReadAnswerAsync(responses));
        Assert.StartsWith("HTTP/1.1 308 Permanent Redirect\r\n", answer, StringComparison.Ordinal);
        Assert.Contains("Location: https://admin.example/x?y=1\r\n", answer, StringComparison.Ordinal);
        Assert.False(reached);
    }

    [Fact]
    public async Task A_client_hello_is_handed_on_unread_and_without_waiting_for_more()
    {
        var (connection, requests, _) = NewConnection();
        byte[] clientHello = [TlsHandshakeRecord, 0x03, 0x01, 0x00, 0x05, 1, 2, 3, 4, 5];
        await requests.Writer.WriteAsync(clientHello, Ct);

        byte[]? seen = null;
        await RunAsync(connection, async next =>
        {
            // Exactly what the TLS middleware does: its first read must see the ClientHello, and must complete without the
            // client sending another byte (the client is waiting for the ServerHello).
            var read = await next.Transport.Input.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
            seen = read.Buffer.ToArray();
            next.Transport.Input.AdvanceTo(read.Buffer.End);
        });

        Assert.Equal(clientHello, seen);
    }

    [Theory]
    [InlineData("GET /split HTTP/1.1\r\nHost: admin.example\r\n\r\n", "https://admin.example/split")]
    [InlineData("HEAD / HTTP/1.0\r\nHost: admin.example:8443\r\n\r\n", "https://admin.example:8443/")]
    [InlineData("GET /[::1] HTTP/1.1\r\nHost: [::1]:9443\r\n\r\n", "https://[::1]:9443/[::1]")]
    public async Task One_request_head_in_one_read_is_answered(string request, string location)
    {
        var (connection, requests, responses) = NewConnection();
        await requests.Writer.WriteAsync(Encoding.ASCII.GetBytes(request), Ct);

        await RunAsync(connection, static _ => Task.CompletedTask);

        var answer = Encoding.ASCII.GetString(await ReadAnswerAsync(responses));
        Assert.Contains($"Location: {location}\r\n", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_that_says_nothing_is_dropped_after_the_head_timeout()
    {
        var (connection, _, responses) = NewConnection();
        var started = Stopwatch.StartNew();

        await AdminPlainHttpRedirect.OnConnectionAsync(
                connection, static _ => Task.CompletedTask, NullLogger.Instance, TimeSpan.FromMilliseconds(100))
            .WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(80), $"closed after {started.Elapsed}");
        Assert.Empty(await ReadAnswerAsync(responses));
    }

    [Fact]
    public async Task A_client_that_connects_and_leaves_gets_no_answer()
    {
        var (connection, requests, responses) = NewConnection();
        await requests.Writer.CompleteAsync();

        await RunAsync(connection, static _ => Task.CompletedTask);

        Assert.Empty(await ReadAnswerAsync(responses));
    }

    [Fact]
    public async Task A_head_cut_short_when_the_client_leaves_is_a_400()
    {
        var (connection, requests, responses) = NewConnection();
        await requests.Writer.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHos"), Ct);
        await requests.Writer.CompleteAsync();

        await RunAsync(connection, static _ => Task.CompletedTask);

        Assert.StartsWith("HTTP/1.1 400 Bad Request", Encoding.ASCII.GetString(await ReadAnswerAsync(responses)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_head_end_is_found_across_arrivals_without_rescanning_what_came_before()
    {
        // "GET / HTTP/1.1\r\nHost: a\r\n\r\n": the terminator's first byte is at 23.
        var head = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: a\r\n\r\nand a body");
        long searched = 0;

        // Two bytes at a time, as a slow client's segments arrive: every call sees a longer buffer and scans only its tail.
        for (var length = 1; length < 27; length++)
        {
            Assert.False(AdminPlainHttpRedirect.TryFindHeadEnd(Slice(head, 0, length), ref searched, out _));
        }

        Assert.True(AdminPlainHttpRedirect.TryFindHeadEnd(Slice(head, 0, 28), ref searched, out var headEnd));
        Assert.Equal(23, headEnd);
    }

    [Fact]
    public void A_terminator_split_across_arrivals_is_still_found()
    {
        var head = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: a\r\n\r\n");
        long searched = 0;

        Assert.False(AdminPlainHttpRedirect.TryFindHeadEnd(Slice(head, 0, 25), ref searched, out _));
        Assert.True(AdminPlainHttpRedirect.TryFindHeadEnd(Slice(head, 0, 27), ref searched, out var headEnd));
        Assert.Equal(23, headEnd);
    }

    private static ReadOnlySequence<byte> Slice(byte[] bytes, int start, int length) => new(bytes, start, length);

    private static async Task RunAsync(
        ConnectionContext connection, ConnectionDelegate next, TimeSpan? headTimeout = null) =>
        await AdminPlainHttpRedirect.OnConnectionAsync(
                connection, next, NullLogger.Instance, headTimeout ?? TimeSpan.FromSeconds(5))
            .WaitAsync(TimeSpan.FromSeconds(15), Ct);

    /// <summary>Everything the stage wrote, read after it closed the connection.</summary>
    private static async Task<byte[]> ReadAnswerAsync(Pipe pipe)
    {
        using MemoryStream answer = new();
        while (true)
        {
            var read = await pipe.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
            foreach (var segment in read.Buffer)
            {
                answer.Write(segment.Span);
            }

            pipe.Reader.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
            {
                return answer.ToArray();
            }
        }
    }

    private static (ConnectionContext Connection, Pipe Requests, Pipe Responses) NewConnection()
    {
        Pipe requests = new();
        Pipe responses = new();
        DefaultConnectionContext connection = new()
        {
            Transport = new TestDuplexPipe(requests.Reader, responses.Writer),
        };
        return (connection, requests, responses);
    }

    private sealed class TestDuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }
}
