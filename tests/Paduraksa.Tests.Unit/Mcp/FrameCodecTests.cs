using System.Buffers.Binary;
using System.Text;

using Paduraksa.Mcp;

namespace Paduraksa.Tests.Unit.Mcp;

public sealed class FrameCodecTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MemoryStream Framed(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var bytes = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes, body.Length);
        body.CopyTo(bytes, 4);
        return new MemoryStream(bytes);
    }

    [Fact]
    public async Task AFrameRoundTripsInBothDirectionsAndTheEndOfTheStreamIsNull()
    {
        using MemoryStream stream = new();
        FrameCodec.Write(stream, "{\"t\":\"a\"}"u8, 1024);
        await FrameCodec.WriteAsync(stream, "{\"t\":\"bé\"}"u8.ToArray(), 1024, Ct);
        stream.Position = 0;
        Assert.Equal("{\"t\":\"a\"}", Encoding.UTF8.GetString(FrameCodec.Read(stream, 1024)!));
        Assert.Equal("{\"t\":\"bé\"}", Encoding.UTF8.GetString((await FrameCodec.ReadAsync(stream, 1024, Ct))!));
        Assert.Null(FrameCodec.Read(stream, 1024));
        Assert.Null(await FrameCodec.ReadAsync(stream, 1024, Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(1025)]
    public async Task ALengthOutsideTheLimitIsAViolationBeforeAnyBodyIsRead(int length)
    {
        var bytes = new byte[4 + 16];
        BinaryPrimitives.WriteInt32BigEndian(bytes, length);
        Assert.Throws<SandboxProtocolException>(() => FrameCodec.Read(new MemoryStream(bytes), 1024));
        await Assert.ThrowsAsync<SandboxProtocolException>(async () => await FrameCodec.ReadAsync(new MemoryStream(bytes), 1024, Ct));
    }

    [Fact]
    public async Task AStreamThatEndsInsideAFrameIsAViolation()
    {
        var whole = Framed("{\"t\":\"abcdef\"}").ToArray();
        foreach (var cut in new[] { 2, 6, whole.Length - 1 })
        {
            Assert.Throws<SandboxProtocolException>(() => FrameCodec.Read(new MemoryStream(whole[..cut]), 1024));
            await Assert.ThrowsAsync<SandboxProtocolException>(async () => await FrameCodec.ReadAsync(new MemoryStream(whole[..cut]), 1024, Ct));
        }
    }

    [Fact]
    public void WritingMoreThanTheLimitIsRefused() =>
        Assert.Throws<SandboxProtocolException>(() => FrameCodec.Write(new MemoryStream(), new byte[2000], 1024));

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"no\":\"type\"}")]
    [InlineData("{\"t\":7}")]
    [InlineData("{\"t\":\"a\",\"x\":[[[[[[[[[[[[[[[[[[[[1]]]]]]]]]]]]]]]]]]]}")]
    public void AFrameThatIsNotAMessageIsAViolation(string json) =>
        Assert.Throws<SandboxProtocolException>(() => Messages.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void AFrameThatIsNotValidUtf8OrHoldsALoneSurrogateIsAViolationNotAnException()
    {
        // Found by the mcp-sandbox fuzz target (fuzz/regressions/mcp-sandbox): invalid UTF-8 inside a string reached JsonElement.GetString, which throws InvalidOperationException.
        byte[] invalid = [.. "{\"t\":\"x\",\"s\":\""u8, 0xFF, 0xFE, .. "\"}"u8];
        Assert.Throws<SandboxProtocolException>(() => Messages.Parse(invalid));

        using var lone = Messages.Parse("""{"t":"x","s":"\ud800"}"""u8.ToArray());
        Assert.Throws<SandboxProtocolException>(() => Messages.Text(lone.RootElement, "s", 10));
    }

    [Fact]
    public async Task ARequestWhoseOptionsHoldALoneSurrogateIsATypeErrorForTheScript()
    {
        RequestBridge bridge = new(RequestPolicy.FromSpec(System.Text.Json.JsonDocument.Parse(McpTestSpec.OpenApi).RootElement.Clone()), new ScriptedDispatcher(), SandboxLimits.Default);
        using var answer = Messages.Parse(await bridge.HandleAsync(1, "{\"method\":\"GET\",\"path\":\"/api/v1/\\ud800\"}", Ct));
        Assert.Equal("TypeError", answer.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public void FieldsAreReadByNameWithTheirLimits()
    {
        using var message = Messages.Parse("""{"t":"x","s":"abc","n":5,"f":true}"""u8.ToArray());
        var root = message.RootElement;
        Assert.Equal("abc", Messages.Text(root, "s", 3));
        Assert.Throws<SandboxProtocolException>(() => Messages.Text(root, "s", 2));
        Assert.Throws<SandboxProtocolException>(() => Messages.Text(root, "missing", 9));
        Assert.Equal(string.Empty, Messages.Text(root, "missing", 9, required: false));
        Assert.Throws<SandboxProtocolException>(() => Messages.Text(root, "n", 9));
        Assert.Equal(5, Messages.Number(root, "n"));
        Assert.Throws<SandboxProtocolException>(() => Messages.Number(root, "s"));
        Assert.True(Messages.Flag(root, "f"));
        Assert.False(Messages.Flag(root, "s"));
    }
}
