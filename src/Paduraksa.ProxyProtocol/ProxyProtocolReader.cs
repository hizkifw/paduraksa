using System.IO.Pipelines;

namespace Paduraksa.ProxyProtocol;

/// <summary>Why <see cref="ProxyProtocolReader.ReadAsync"/> returned.</summary>
public enum ProxyProtocolReadStatus : byte
{
    /// <summary>A header was read and consumed.</summary>
    Success = 0,

    /// <summary>The bytes received can't be a PROXY protocol header (or it exceeds the size cap).</summary>
    Invalid = 1,

    /// <summary>No complete header within the timeout.</summary>
    TimedOut = 2,

    /// <summary>The peer closed (or reset) the connection before sending a complete header.</summary>
    Closed = 3,

    /// <summary>Only with <c>headerOptional</c>: the bytes don't begin a PROXY protocol header, so the peer sent none. Nothing was consumed.</summary>
    Absent = 4,
}

/// <summary>Outcome of <see cref="ProxyProtocolReader.ReadAsync"/>. <paramref name="Header"/> is default unless the status is <see cref="ProxyProtocolReadStatus.Success"/>.</summary>
public readonly record struct ProxyProtocolReadResult(ProxyProtocolReadStatus Status, ProxyProtocolHeader Header);

/// <summary>Reads one PROXY protocol header from the front of a connection's input pipe, leaving every byte
/// after it in the pipe for TLS / HTTP.</summary>
public static class ProxyProtocolReader
{
    /// <summary>Longest <c>timeout</c> of <see cref="ReadAsync"/>: what a timer takes (about 24.8 days).</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Reads and consumes the header, giving up after <paramref name="timeout"/>. Never throws for anything the
    /// peer sends or does (malformed bytes, slow drip, close, reset): those map to a non-success status.
    /// </summary>
    /// <param name="input">The connection's input; every byte after the header stays in it.</param>
    /// <param name="timeout">How long the peer has to deliver the complete header: greater than zero and at most <see cref="MaxTimeout"/>.
    /// There is no "no timeout": a header read without a deadline holds a connection open for as long as the peer likes.</param>
    /// <param name="maxV2Length">Cap on a whole v2 header, TLVs included; at least <see cref="ProxyProtocolParser.V2MaxLengthWithoutTlvs"/>.</param>
    /// <param name="headerOptional">Treat bytes that don't begin a header as application data: the result is <see cref="ProxyProtocolReadStatus.Absent"/>
    /// and nothing is consumed. A header that begins correctly and then breaks is still <see cref="ProxyProtocolReadStatus.Invalid"/>.</param>
    /// <remarks>Allocation: nothing when the header is already buffered (the usual case: proxies send it in the
    /// first segment); otherwise one timer-backed <see cref="CancellationTokenSource"/> and the async state
    /// machine, once per connection.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> or <paramref name="maxV2Length"/> is out of range.</exception>
    public static async ValueTask<ProxyProtocolReadResult> ReadAsync(
        PipeReader input,
        TimeSpan timeout,
        int maxV2Length = ProxyProtocolParser.DefaultMaxV2Length,
        bool headerOptional = false)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Checked here and not by the timer: the timer exists only when the header isn't buffered yet, so a bad value would throw on some calls and pass on others.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, MaxTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxV2Length, ProxyProtocolParser.V2MaxLengthWithoutTlvs);
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
                    // Decided before AdvanceTo: the buffer is not valid after it.
                    var absent = headerOptional && !ProxyProtocolParser.HasHeaderPrefix(buffer);
                    input.AdvanceTo(buffer.Start);
                    return new ProxyProtocolReadResult(absent ? ProxyProtocolReadStatus.Absent : ProxyProtocolReadStatus.Invalid, default);
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
