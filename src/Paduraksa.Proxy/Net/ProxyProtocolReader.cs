using System.IO.Pipelines;

namespace Paduraksa.Proxy.Net;

public enum ProxyProtocolReadStatus : byte
{
    Success = 0,

    /// <summary>The bytes received can't be a PROXY protocol header (or it exceeds the size cap).</summary>
    Invalid = 1,

    /// <summary>No complete header within the timeout.</summary>
    TimedOut = 2,

    /// <summary>The peer closed (or reset) the connection before sending a complete header.</summary>
    Closed = 3,
}

/// <summary>Outcome of <see cref="ProxyProtocolReader.ReadAsync"/>.</summary>
public readonly record struct ProxyProtocolReadResult(ProxyProtocolReadStatus Status, ProxyProtocolHeader Header);

/// <summary>Reads one PROXY protocol header from the front of a connection's input pipe, leaving every byte
/// after it in the pipe for TLS / HTTP.</summary>
public static class ProxyProtocolReader
{
    /// <summary>
    /// Reads and consumes the header, giving up after <paramref name="timeout"/>. Never throws for anything the
    /// peer sends or does (malformed bytes, slow drip, close, reset): those map to a non-success status.
    /// </summary>
    /// <remarks>Allocation: nothing when the header is already buffered (the usual case: proxies send it in the
    /// first segment); otherwise one timer-backed <see cref="CancellationTokenSource"/> and the async state
    /// machine, once per connection.</remarks>
    public static async ValueTask<ProxyProtocolReadResult> ReadAsync(
        PipeReader input,
        TimeSpan timeout,
        int maxV2Length = ProxyProtocolParser.DefaultMaxV2Length)
    {
        ArgumentNullException.ThrowIfNull(input);
        CancellationTokenSource? timer = null;
        try
        {
            while (true)
            {
                if (!input.TryRead(out var result))
                {
                    timer ??= new CancellationTokenSource(timeout);
                    result = await input.ReadAsync(timer.Token).ConfigureAwait(false);
                }

                var buffer = result.Buffer;
                var status = ProxyProtocolParser.TryParse(buffer, out var header, out var consumed, maxV2Length);
                if (status == ProxyProtocolParseStatus.Success)
                {
                    input.AdvanceTo(buffer.GetPosition(consumed));
                    return new ProxyProtocolReadResult(ProxyProtocolReadStatus.Success, header);
                }

                if (status == ProxyProtocolParseStatus.Invalid)
                {
                    input.AdvanceTo(buffer.Start);
                    return new ProxyProtocolReadResult(ProxyProtocolReadStatus.Invalid, default);
                }

                if (result.IsCompleted || result.IsCanceled)
                {
                    input.AdvanceTo(buffer.Start);
                    return new ProxyProtocolReadResult(ProxyProtocolReadStatus.Closed, default);
                }

                // A valid prefix so far: mark it examined so the next read waits for new bytes.
                input.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        catch (OperationCanceledException) when (timer?.IsCancellationRequested == true)
        {
            return new ProxyProtocolReadResult(ProxyProtocolReadStatus.TimedOut, default);
        }
        catch (OperationCanceledException)
        {
            // Kestrel's ConnectionAbortedException (the connection was aborted underneath us).
            return new ProxyProtocolReadResult(ProxyProtocolReadStatus.Closed, default);
        }
        catch (IOException)
        {
            // Includes ConnectionResetException.
            return new ProxyProtocolReadResult(ProxyProtocolReadStatus.Closed, default);
        }
        finally
        {
            timer?.Dispose();
        }
    }
}
