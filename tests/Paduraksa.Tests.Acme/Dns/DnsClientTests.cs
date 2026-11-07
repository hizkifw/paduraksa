using System.Net;
using Paduraksa.Acme.Dns;
using static Paduraksa.Tests.Acme.Fakes.AcmeHarness;

namespace Paduraksa.Tests.Acme.Dns;

/// <summary>The DNS message codec (well-formed, malformed and hostile messages) and the UDP/TCP client against a fake server.</summary>
public sealed class DnsClientTests
{
    private const string Name = "_acme-challenge.example.com";

    private static byte[] TxtAnswer(string value) => DnsMessages.Record(DnsMessages.QuestionPointer, 16, DnsMessages.Txt(value));

    [Fact]
    public void AQueryHasOneQuestionAnOptRecordAndTheRecursionBitAsAsked()
    {
        byte[] query = DnsWire.BuildQuery(0xABCD, "a.example.com", DnsRecordType.TXT, recursionDesired: true);

        Assert.Equal([0xAB, 0xCD, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 1], query[..12]);
        Assert.Equal([.. DnsMessages.Name("a.example.com"), 0, 16, 0, 1], query[12..(12 + 15 + 4)]);
        Assert.Equal(0x00, DnsWire.BuildQuery(1, "a.example.com", DnsRecordType.NS, recursionDesired: false)[2]);
        Assert.Equal(41, query[^10] << 8 | query[^9]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a..b")]
    public void AQueryForAnInvalidNameIsRefused(string name) =>
        Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery(1, name, DnsRecordType.A, false));

    [Fact]
    public void AQueryForATooLongNameOrLabelIsRefused()
    {
        Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery(1, new string('a', 64) + ".com", DnsRecordType.A, false));
        Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery(1, string.Join('.', Enumerable.Repeat(new string('a', 60), 5)), DnsRecordType.A, false));
    }

    [Fact]
    public void AnswersOfEveryReadTypeAreParsedWithCompressionAndMultiStringTxt()
    {
        byte[] message = DnsMessages.Response(
            7,
            "Example.COM",
            16,
            [
                DnsMessages.Record(DnsMessages.QuestionPointer, 16, DnsMessages.Txt("ab", "cd")),
                DnsMessages.Record(DnsMessages.QuestionPointer, 1, [192, 0, 2, 1]),
                DnsMessages.Record(DnsMessages.QuestionPointer, 28, IPAddress.IPv6Loopback.GetAddressBytes()),
                DnsMessages.Record(DnsMessages.QuestionPointer, 2, [3, (byte)'n', (byte)'s', (byte)'1', 0xC0, 0x0C]),
                DnsMessages.Record(DnsMessages.QuestionPointer, 99, [1, 2, 3]),
            ],
            authoritative: true,
            truncated: false,
            rcode: 3);

        DnsResponse response = DnsWire.ParseResponse(message);

        Assert.Equal(7, response.Id);
        Assert.True(response.Authoritative);
        Assert.False(response.Truncated);
        Assert.Equal(3, response.ResponseCode);
        Assert.Equal("Example.COM", response.QuestionName);
        Assert.Equal(["abcd", null, null, null, null], response.Answers.Select(a => a.Text));
        Assert.Equal(IPAddress.Parse("192.0.2.1"), response.Answers[1].Address);
        Assert.Equal(IPAddress.IPv6Loopback, response.Answers[2].Address);
        Assert.Equal("ns1.Example.COM", response.Answers[3].Target);
        Assert.All(response.Answers, a => Assert.Equal("Example.COM", a.Name));
    }

    [Fact]
    public void EveryTruncationOfAValidMessageIsRejectedCleanlyOrParsed()
    {
        byte[] message = DnsMessages.Response(1, Name, 16, [TxtAnswer("hello"), TxtAnswer("world")]);

        for (int length = 0; length < message.Length; length++)
        {
            try
            {
                _ = DnsWire.ParseResponse(message.AsSpan(0, length));
            }
            catch (DnsFormatException)
            {
                // The only exception a bad message may cause.
            }
        }

        Assert.Equal(2, DnsWire.ParseResponse(message).Answers.Count);
    }

    [Fact]
    public void RandomlyDamagedMessagesOnlyEverCauseFormatErrors()
    {
        byte[] original = DnsMessages.Response(1, Name, 2, [DnsMessages.Record(DnsMessages.QuestionPointer, 2, [2, (byte)'n', (byte)'s', 0xC0, 0x10]), TxtAnswer("x")]);
        Random random = new(1234);
        for (int i = 0; i < 5000; i++)
        {
            byte[] damaged = (byte[])original.Clone();
            for (int flips = random.Next(1, 4); flips > 0; flips--)
            {
                damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
            }

            try
            {
                _ = DnsWire.ParseResponse(damaged);
            }
            catch (DnsFormatException)
            {
                // Rejected, which is allowed.
            }
        }
    }

    [Fact]
    public void ACompressionPointerToItselfOrForwardIsRejected()
    {
        // The answer's name is a pointer to its own position.
        byte[] self = DnsMessages.Response(1, Name, 16, [DnsMessages.Record([0xC0, (byte)(12 + DnsMessages.Name(Name).Length + 4)], 16, DnsMessages.Txt("x"))]);
        byte[] forward = DnsMessages.Response(1, Name, 16, [DnsMessages.Record([0xC0, 0xFF], 16, DnsMessages.Txt("x"))]);

        Assert.Contains("backward", Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(self)).Message, StringComparison.Ordinal);
        Assert.Contains("backward", Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(forward)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongChainOfBackwardPointersIsStopped()
    {
        // An unknown record's data holds a name "a" followed by 40 pointers that each point at the one before: every pointer is
        // legal (backward), but the chain is longer than the client follows. A second answer's name points at the last of them.
        byte[] question = [.. DnsMessages.Name(Name), .. DnsMessages.UInt16(16), .. DnsMessages.UInt16(1)];
        int chainStart = 12 + question.Length + 2 + 10;
        List<byte> chain = [1, (byte)'a', 0];
        int previous = chainStart;
        for (int i = 0; i < 40; i++)
        {
            int here = chainStart + chain.Count;
            chain.AddRange([(byte)(0xC0 | (previous >> 8)), (byte)previous]);
            previous = here;
        }

        byte[] carrier = DnsMessages.Record(DnsMessages.QuestionPointer, 99, [.. chain]);
        byte[] answer = DnsMessages.Record([(byte)(0xC0 | (previous >> 8)), (byte)previous], 16, DnsMessages.Txt("x"));
        byte[] message =
        [
            .. DnsMessages.UInt16(1), .. DnsMessages.UInt16(0x8400), .. DnsMessages.UInt16(1), .. DnsMessages.UInt16(2), 0, 0, 0, 0,
            .. question, .. carrier, .. answer,
        ];

        Assert.Contains("Too many", Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(message)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReservedLabelTypesBadBytesAndOverlongNamesAreRejected()
    {
        Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(DnsMessages.Response(1, Name, 16, [DnsMessages.Record([0x40, 0, 0], 16, DnsMessages.Txt("x"))])));
        Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(DnsMessages.Response(1, Name, 16, [DnsMessages.Record([2, (byte)'a', 0x01, 0], 16, DnsMessages.Txt("x"))])));
        byte[] longName = [.. Enumerable.Repeat<byte[]>([63, .. Enumerable.Repeat((byte)'a', 63)], 5).SelectMany(b => b), 0];
        Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(DnsMessages.Response(1, Name, 16, [DnsMessages.Record(longName, 16, DnsMessages.Txt("x"))])));
    }

    [Fact]
    public void LengthsThatDisagreeWithTheMessageAreRejected()
    {
        Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(DnsMessages.Response(1, Name, 16, [DnsMessages.Record(DnsMessages.QuestionPointer, 16, DnsMessages.Txt("x"), declaredLength: 200)])));
        Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(DnsMessages.Response(1, Name, 16, [DnsMessages.Record(DnsMessages.QuestionPointer, 16, [9, (byte)'a'])])));
        Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(DnsMessages.Response(1, Name, 1, [DnsMessages.Record(DnsMessages.QuestionPointer, 1, [1, 2, 3])])));
        Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(DnsMessages.Response(1, Name, 28, [DnsMessages.Record(DnsMessages.QuestionPointer, 28, [1, 2, 3, 4])])));
        Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(DnsMessages.Response(1, Name, 16, [], declaredAnswerCount: 3)));
        Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(DnsMessages.Response(1, Name, 16, [], declaredAnswerCount: 5000)));
    }

    [Fact]
    public void AQueryOrANonStandardOrWrongClassMessageIsNotAResponse()
    {
        byte[] query = DnsMessages.Response(1, Name, 16, []);
        query[2] &= 0x7F;
        byte[] opcode = DnsMessages.Response(1, Name, 16, []);
        opcode[2] |= 0x10;
        byte[] noQuestion = DnsMessages.Response(1, Name, 16, []);
        noQuestion[5] = 0;

        Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(query));
        Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(opcode));
        Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(noQuestion));
        Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(new byte[4]));
    }

    [Fact]
    public async Task AUdpQueryIsAnsweredAndSentWithoutRecursionForAnAuthoritativeServer()
    {
        using FakeDnsServer server = new();
        server.AddTxt(Name, "value");

        DnsResponse response = await new DnsClient(TimeSpan.FromSeconds(5)).QueryAsync(server.EndPoint, Name, DnsRecordType.TXT, recursionDesired: false, Token);

        Assert.True(response.Authoritative);
        Assert.Equal("value", Assert.Single(response.Answers).Text);
        FakeQuery query = Assert.Single(server.Queries);
        Assert.False(query.RecursionDesired);
        Assert.False(query.Tcp);
    }

    [Fact]
    public async Task ATruncatedUdpAnswerIsAskedAgainOverTcp()
    {
        using FakeDnsServer server = new() { TruncateUdp = true };
        server.AddTxt(Name, "value");

        DnsResponse response = await new DnsClient(TimeSpan.FromSeconds(5)).QueryAsync(server.EndPoint, Name, DnsRecordType.TXT, false, Token);

        Assert.Equal("value", Assert.Single(response.Answers).Text);
        Assert.Equal([false, true], server.Queries.Select(q => q.Tcp));
    }

    [Fact]
    public async Task DatagramsWithAWrongIdOrQuestionAreIgnoredUntilTheRightOneArrives()
    {
        using FakeDnsServer server = new();
        server.Override = q =>
        [
            DnsMessages.Response((ushort)(q.Id + 1), q.Name, q.Type, [TxtAnswer("forged-id")]),
            DnsMessages.Response(q.Id, "other.example.com", q.Type, [TxtAnswer("forged-question")]),
            DnsMessages.Response(q.Id, q.Name, 1, []),
            [1, 2, 3],
            DnsMessages.Response(q.Id, q.Name.ToUpperInvariant(), q.Type, [TxtAnswer("genuine")]),
        ];

        DnsResponse response = await new DnsClient(TimeSpan.FromSeconds(5)).QueryAsync(server.EndPoint, Name, DnsRecordType.TXT, false, Token);

        Assert.Equal("genuine", Assert.Single(response.Answers).Text);
    }

    [Fact]
    public async Task QueryIdsAreNotSequential()
    {
        using FakeDnsServer server = new();
        DnsClient client = new(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 8; i++)
        {
            _ = await client.QueryAsync(server.EndPoint, Name, DnsRecordType.TXT, false, Token);
        }

        List<int> ids = [.. server.Queries.Select(q => (int)q.Id)];
        Assert.True(ids.Distinct().Count() > 4, "ids should vary");
        Assert.False(ids.Zip(ids.Skip(1), (a, b) => b - a).All(d => d == 1));
    }

    [Fact]
    public async Task NoAnswerIsATimeout()
    {
        using FakeDnsServer server = new() { Override = _ => [] };

        DnsQueryException ex = await Assert.ThrowsAsync<DnsQueryException>(async () =>
            await new DnsClient(TimeSpan.FromMilliseconds(200)).QueryAsync(server.EndPoint, Name, DnsRecordType.TXT, false, Token));

        Assert.Contains("in time", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallerCancellationIsNotATimeout()
    {
        using FakeDnsServer server = new() { Override = _ => [] };
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new DnsClient(TimeSpan.FromSeconds(30)).QueryAsync(server.EndPoint, Name, DnsRecordType.TXT, false, cancel.Token));
    }

    [Fact]
    public async Task AMalformedTcpAnswerIsAQueryFailure()
    {
        using FakeDnsServer server = new() { TruncateUdp = true };
        server.Override = q => q.Tcp ? [[0x80, 0x00, 1, 2, 3]] : null;

        await Assert.ThrowsAsync<DnsQueryException>(async () =>
            await new DnsClient(TimeSpan.FromSeconds(5)).QueryAsync(server.EndPoint, Name, DnsRecordType.TXT, false, Token));
    }

    [Fact]
    public async Task ASilentTcpServerIsAQueryFailure()
    {
        using FakeDnsServer server = new() { TruncateUdp = true };
        server.Override = q => q.Tcp ? [] : [DnsMessages.Response(q.Id, q.Name, q.Type, [], truncated: true)];
        // Nothing comes over TCP and the connection stays open, so it ends as a timeout.
        await Assert.ThrowsAsync<DnsQueryException>(async () =>
            await new DnsClient(TimeSpan.FromMilliseconds(300)).QueryAsync(server.EndPoint, Name, DnsRecordType.TXT, false, Token));
    }

    [Fact]
    public async Task AServerNobodyListensOnIsAQueryFailure()
    {
        IPEndPoint closed;
        using (FakeDnsServer server = new())
        {
            closed = server.EndPoint;
        }

        await Assert.ThrowsAsync<DnsQueryException>(async () =>
            await new DnsClient(TimeSpan.FromSeconds(2)).QueryAsync(closed, Name, DnsRecordType.TXT, false, Token));
    }

    [Fact]
    public async Task AServerThatAnswersFormerrToEdnsIsAskedAgainWithoutIt()
    {
        using FakeDnsServer server = new();
        server.AddTxt(Name, "value");
        server.Override = q => q.Edns ? [DnsMessages.Response(q.Id, q.Name, q.Type, [], rcode: 1)] : null;

        DnsResponse response = await new DnsClient(TimeSpan.FromSeconds(5)).QueryAsync(server.EndPoint, Name, DnsRecordType.TXT, false, Token);

        Assert.Equal("value", Assert.Single(response.Answers).Text);
        Assert.Equal([true, false], server.Queries.Select(q => q.Edns));
    }

    [Fact]
    public void AQueryWithoutEdnsHasNoOptRecord()
    {
        byte[] with = DnsWire.BuildQuery(1, "a.example.com", DnsRecordType.TXT, false);
        byte[] without = DnsWire.BuildQuery(1, "a.example.com", DnsRecordType.TXT, false, edns: false);

        Assert.Equal(0, without[11]);
        Assert.Equal(with.Length - 11, without.Length);
    }

    [Fact]
    public void APointerIntoTheHeaderIsRejected()
    {
        // The answer's name points at offset 4, inside the header: backward, but not at a name.
        byte[] message = DnsMessages.Response(1, Name, 16, [DnsMessages.Record([0xC0, 4], 16, DnsMessages.Txt("x"))]);

        Assert.Throws<DnsFormatException>(() => DnsWire.ParseResponse(message));
    }
}
