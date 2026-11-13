using System.Buffers;
using System.IO.Pipelines;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Shared.ProxyProtocolTestData;

namespace Paduraksa.ProxyProtocol.Tests;

public sealed class ProxyProtocolReaderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static async Task<Pipe> PipeWithAsync(byte[] bytes, bool complete = false)
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(bytes, TestContext.Current.CancellationToken);
        if (complete)
        {
            await pipe.Writer.CompleteAsync();
        }

        return pipe;
    }

    private static async Task<string> RemainderAsync(Pipe pipe)
    {
        await pipe.Writer.CompleteAsync();
        var result = await pipe.Reader.ReadAsync(TestContext.Current.CancellationToken);
        return System.Text.Encoding.ASCII.GetString(result.Buffer.ToArray());
    }

    [Fact]
    public async Task ReadsTheHeaderAndLeavesTheRestInThePipe()
    {
        var pipe = await PipeWithAsync(Concat(V2(family: 1, tlvBytes: 20), V1("GET / HTTP/1.1\r\n")));

        var result = await ProxyProtocolReader.ReadAsync(pipe.Reader, Timeout);

        Assert.Equal(ProxyProtocolReadStatus.Success, result.Status);
        Assert.Equal("203.0.113.7", result.Header.SourceAddress.ToString());
        Assert.Equal("GET / HTTP/1.1\r\n", await RemainderAsync(pipe));
    }

    [Fact]
    public async Task ARequiredHeaderThatIsMissingIsInvalid()
    {
        var pipe = await PipeWithAsync(V1("GET / HTTP/1.1\r\n"));

        var result = await ProxyProtocolReader.ReadAsync(pipe.Reader, Timeout);

        Assert.Equal(ProxyProtocolReadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task AnOptionalHeaderThatIsMissingIsAbsentAndNothingIsConsumed()
    {
        var pipe = await PipeWithAsync(V1("POST / HTTP/1.1\r\n"));

        var result = await ProxyProtocolReader.ReadAsync(pipe.Reader, Timeout, headerOptional: true);

        Assert.Equal(ProxyProtocolReadStatus.Absent, result.Status);
        Assert.Equal("POST / HTTP/1.1\r\n", await RemainderAsync(pipe));
    }

    [Theory]
    [InlineData("PROXY TCP4 1.2.3.4 5.6.7.8 1\r\n")]
    [InlineData("PROXY \r\n")]
    public async Task AnOptionalHeaderThatBeginsRightAndBreaksIsInvalid(string line)
    {
        var pipe = await PipeWithAsync(V1(line));

        var result = await ProxyProtocolReader.ReadAsync(pipe.Reader, Timeout, headerOptional: true);

        Assert.Equal(ProxyProtocolReadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task AnOptionalV2HeaderWithABrokenVersionIsInvalid()
    {
        var pipe = await PipeWithAsync(V2(version: 3));

        var result = await ProxyProtocolReader.ReadAsync(pipe.Reader, Timeout, headerOptional: true);

        Assert.Equal(ProxyProtocolReadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ASilentPeerTimesOut()
    {
        var pipe = new Pipe();

        var result = await ProxyProtocolReader.ReadAsync(pipe.Reader, TimeSpan.FromMilliseconds(100));

        Assert.Equal(ProxyProtocolReadStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task ACloseBeforeTheHeaderEndsIsClosed()
    {
        var pipe = await PipeWithAsync(V1("PROXY TCP4 1.2"), complete: true);

        var result = await ProxyProtocolReader.ReadAsync(pipe.Reader, Timeout);

        Assert.Equal(ProxyProtocolReadStatus.Closed, result.Status);
    }

    /// <summary>The timer exists only when the header isn't buffered, so a bad timeout must be refused before either path is taken.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATimeoutOutOfRangeIsRefusedWhetherOrNotTheHeaderIsBuffered(bool buffered)
    {
        var pipe = buffered ? await PipeWithAsync(V1("PROXY TCP4 203.0.113.7 192.0.2.1 50123 443\r\n")) : new Pipe();
        TimeSpan[] bad = [TimeSpan.Zero, TimeSpan.FromSeconds(-5), System.Threading.Timeout.InfiniteTimeSpan, ProxyProtocolReader.MaxTimeout + TimeSpan.FromTicks(1)];

        foreach (var timeout in bad)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await ProxyProtocolReader.ReadAsync(pipe.Reader, timeout));
        }

        if (buffered)
        {
            Assert.Equal(ProxyProtocolReadStatus.Success, (await ProxyProtocolReader.ReadAsync(pipe.Reader, ProxyProtocolReader.MaxTimeout)).Status);
        }
    }
}
