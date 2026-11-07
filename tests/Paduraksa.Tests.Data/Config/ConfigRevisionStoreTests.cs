using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Paduraksa.Application.Revisions;
using Paduraksa.Core.Model;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Config;

public sealed class ConfigRevisionStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero).AddTicks(1234);

    private readonly ManualTimeProvider _clock = new(Now);
    private TestConfigDatabase _db = null!;
    private ConfigRevisionStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await CreateAsync();
        _store = new ConfigRevisionStore(_clock);
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task AppendNumbersRevisionsAndLinksEachToThePreviousOne()
    {
        var first = await AppendAsync(Revision("""{"v":1}"""));
        var second = await AppendAsync(Revision("""{"v":2}"""));
        var third = await AppendAsync(Revision("""{"v":3}"""));

        Assert.Equal((1L, (ConfigRevisionId?)null), (first.Seq, first.ParentId));
        Assert.Equal((2L, first.Id), (second.Seq, second.ParentId));
        Assert.Equal((3L, second.Id), (third.Seq, third.ParentId));
        Assert.Equal("#3", third.Label);
    }

    [Fact]
    public async Task AppendStoresTheDocumentCompressedWithTheHashOfTheUncompressedBytes()
    {
        var document = Json($$"""{"hosts":[{{string.Join(",", Enumerable.Repeat("""{"name":"example.com"}""", 200))}}]}""");
        var info = await AppendAsync(Revision(document));

        var row = (await _db.QueryAsync("SELECT Document, DocumentHash FROM ConfigRevisions")).Single();
        var stored = (byte[])row[0]!;
        Assert.True(stored.Length < document.Length / 10, $"{stored.Length} bytes stored for a {document.Length}-byte document");
        Assert.Equal(document, Decompress(stored));
        Assert.Equal(SHA256.HashData(document), (byte[])row[1]!);
        Assert.Equal(SHA256.HashData(document), info.DocumentHash.ToArray());
    }

    [Fact]
    public async Task GetLatestReturnsTheDocumentBytesAndMetadataExactlyAsCommitted()
    {
        Assert.Null(await ReadAsync(c => ConfigRevisionStore.GetLatestAsync(c, Ct)));
        var user = UserId.New();
        await AppendAsync(Revision("""{"v":1}"""));
        _clock.Now = Now.AddMinutes(1);
        var document = Json("""{"v":2, "unicode":"Paduraksa é 😀"}""");
        var appended = await AppendAsync(new NewRevision(document, """[{"op":"update"}]""", Actor.ForUser(user), Channel.Dashboard, "Second"));

        var latest = await ReadAsync(c => ConfigRevisionStore.GetLatestAsync(c, Ct));

        Assert.NotNull(latest);
        Assert.Equal(document, latest.Document.ToArray());
        var info = latest.Info;
        Assert.Equal(appended.Id, info.Id);
        Assert.Equal(2, info.Seq);
        Assert.Equal(Actor.ForUser(user), info.Actor);
        Assert.Equal(Channel.Dashboard, info.Channel);
        Assert.Equal("Second", info.Message);
        Assert.Equal("""[{"op":"update"}]""", info.Summary);
        Assert.Equal(Now.AddMinutes(1).UtcTicks, info.CreatedAt.UtcTicks);
        Assert.Equal(appended.CreatedAt, info.CreatedAt);
    }

    [Fact]
    public async Task GetFindsAnyRevisionById()
    {
        var first = await AppendAsync(Revision("""{"v":1}""", Actor.ForToken(ApiTokenId.New()), Channel.Api));
        await AppendAsync(Revision("""{"v":2}"""));

        var found = await ReadAsync(c => ConfigRevisionStore.GetAsync(c, first.Id, Ct));
        var missing = await ReadAsync(c => ConfigRevisionStore.GetAsync(c, ConfigRevisionId.New(), Ct));

        Assert.NotNull(found);
        Assert.Equal(Json("""{"v":1}"""), found.Document.ToArray());
        Assert.Equal(first.Actor, found.Info.Actor);
        Assert.Equal(Channel.Api, found.Info.Channel);
        Assert.Null(missing);
    }

    [Fact]
    public async Task ListPagesNewestFirstWithoutReadingDocuments()
    {
        var appended = new List<RevisionInfo>();
        for (var i = 1; i <= 5; i++)
        {
            appended.Add(await AppendAsync(Revision($$"""{"v":{{i}}}""")));
        }

        var capture = new SqlCapture();
        await using var context = _db.NewContext(capture);
        var page1 = await ConfigRevisionStore.ListAsync(context, limit: 2, cancellationToken: Ct);
        var page2 = await ConfigRevisionStore.ListAsync(context, limit: 2, beforeSeq: page1[^1].Seq, cancellationToken: Ct);
        var page3 = await ConfigRevisionStore.ListAsync(context, limit: 2, beforeSeq: page2[^1].Seq, cancellationToken: Ct);
        var page4 = await ConfigRevisionStore.ListAsync(context, limit: 2, beforeSeq: page3[^1].Seq, cancellationToken: Ct);

        Assert.Equal([5L, 4L], page1.Select(r => r.Seq));
        Assert.Equal([3L, 2L], page2.Select(r => r.Seq));
        Assert.Equal([1L], page3.Select(r => r.Seq));
        Assert.Empty(page4);
        Assert.Equal(appended[4].Id, page1[0].Id);
        Assert.Equal(appended[0].DocumentHash.ToArray(), page3[0].DocumentHash.ToArray());
        Assert.NotEmpty(capture.Queries);
        Assert.All(capture.Queries, sql => Assert.DoesNotContain("\"Document\"", sql, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ConfigRevisionStore.MaxPageSize + 1)]
    public async Task ListRejectsPageSizesOutOfRange(int limit)
    {
        await using var context = _db.NewContext();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ConfigRevisionStore.ListAsync(context, limit, cancellationToken: Ct));
    }

    [Fact]
    public async Task AppendNeedsTheCallersTransaction()
    {
        await using var context = _db.NewContext();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _store.AppendAsync(context, Revision("{}"), Ct));

        Assert.Contains("transaction", error.Message, StringComparison.Ordinal);
        Assert.Equal(0L, await _db.ScalarAsync("SELECT count(*) FROM ConfigRevisions"));
    }

    [Fact]
    public async Task AppendIsPartOfTheCallersTransactionAndRollsBackWithIt()
    {
        await using (var context = _db.NewContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await _store.AppendAsync(context, Revision("{}"), Ct);
            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(0L, await _db.ScalarAsync("SELECT count(*) FROM ConfigRevisions"));
        Assert.Equal(1, (await AppendAsync(Revision("{}"))).Seq);
    }

    public static TheoryData<string, string, int> InvalidRevisions => new()
    {
        { "", "[]", 1 }, // empty document
        { "{", "[]", 1 }, // not JSON
        { "{}", "not json", 1 },
        { "{}", "[]", 0 }, // default(Actor)
        { "{}", "[]", 2 }, // undefined channel
    };

    [Theory]
    [MemberData(nameof(InvalidRevisions))]
    public async Task AppendRejectsInvalidRevisions(string document, string summary, int variant)
    {
        var revision = variant switch
        {
            0 => new NewRevision(Json(document), summary, default, Channel.Cli),
            2 => new NewRevision(Json(document), summary, Actor.Cli, (Channel)99),
            _ => new NewRevision(Json(document), summary, Actor.Cli, Channel.Cli),
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => AppendAsync(revision));
    }

    [Fact]
    public async Task ConcurrentAppendsAreSerialisedIntoOneLinearHistory()
    {
        // SQLite transactions from EF start with BEGIN IMMEDIATE, so a second writer waits for the first to commit
        // and then sees its revision as the latest.
        var appended = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => AppendAsync(Revision($$"""{"v":{{i}}}""")), Ct)));

        var ordered = appended.OrderBy(r => r.Seq).ToList();
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (long)i), ordered.Select(r => r.Seq));
        Assert.Null(ordered[0].ParentId);
        Assert.All(ordered.Skip(1).Zip(ordered), pair => Assert.Equal(pair.Second.Id, pair.First.ParentId));
    }

    [Fact]
    public async Task ReadingADocumentThatDoesNotMatchItsHashFails()
    {
        var info = await AppendAsync(Revision("""{"v":1}"""));
        await _db.ExecuteAsync($"UPDATE ConfigRevisions SET Document = x'{Convert.ToHexString(Compress(Json("""{"v":2}""")))}'");

        var latest = await Assert.ThrowsAsync<RevisionIntegrityException>(() => ReadAsync(c => ConfigRevisionStore.GetLatestAsync(c, Ct)));
        var byId = await Assert.ThrowsAsync<RevisionIntegrityException>(() => ReadAsync(c => ConfigRevisionStore.GetAsync(c, info.Id, Ct)));

        Assert.Contains("SHA-256", latest.Message, StringComparison.Ordinal);
        Assert.Contains("#1", latest.Message, StringComparison.Ordinal);
        Assert.Equal(info.Id, byId.RevisionId);
    }

    [Fact]
    public async Task ReadingAHashThatWasChangedFails()
    {
        await AppendAsync(Revision("""{"v":1}"""));
        await _db.ExecuteAsync("UPDATE ConfigRevisions SET DocumentHash = randomblob(32)");

        await Assert.ThrowsAsync<RevisionIntegrityException>(() => ReadAsync(c => ConfigRevisionStore.GetLatestAsync(c, Ct)));
    }

    [Fact]
    public async Task ReadingADocumentThatDoesNotDecompressFails()
    {
        await AppendAsync(Revision("""{"v":1}"""));
        await _db.ExecuteAsync("UPDATE ConfigRevisions SET Document = x'FFFFFFFF00112233'");

        var error = await Assert.ThrowsAsync<RevisionIntegrityException>(() => ReadAsync(c => ConfigRevisionStore.GetLatestAsync(c, Ct)));

        Assert.Contains("decompress", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADocumentThatDecompressesPastTheCapIsRejected()
    {
        // A decompression bomb: a few KB that expand past MaxDocumentLength. Reading stops at the cap.
        await AppendAsync(Revision("""{"v":1}"""));
        var bomb = Compress(new byte[ConfigRevisionStore.MaxDocumentLength + 1]);
        Assert.True(bomb.Length < 64 * 1024);
        await _db.ExecuteAsync($"UPDATE ConfigRevisions SET Document = x'{Convert.ToHexString(bomb)}'");

        var error = await Assert.ThrowsAsync<RevisionIntegrityException>(() => ReadAsync(c => ConfigRevisionStore.GetLatestAsync(c, Ct)));

        Assert.Contains("decompress", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, error.Seq);
    }

    [Fact]
    public async Task CorruptMetadataIsAnIntegrityErrorToo()
    {
        await AppendAsync(Revision("""{"v":1}"""));
        await _db.ExecuteUncheckedAsync("UPDATE ConfigRevisions SET ActorUserId = randomblob(16)"); // a cli actor with a user ID

        await Assert.ThrowsAsync<RevisionIntegrityException>(() => ReadAsync(c => ConfigRevisionStore.GetLatestAsync(c, Ct)));
        await Assert.ThrowsAsync<RevisionIntegrityException>(() => ReadAsync(c => ConfigRevisionStore.ListAsync(c, limit: 10, cancellationToken: Ct)));
    }

    [Fact]
    public async Task MessagesAndSummariesAreCappedAndMustBeValidText()
    {
        var tooLong = new string('m', ConfigRevisionStore.MaxMessageLength + 1);
        var loneSurrogate = "Rollback " + (char)0xD800; // SQLite would store U+FFFD
        var summaryWithLoneSurrogate = "[\"" + (char)0xDC00 + "\"]";

        await Assert.ThrowsAsync<ArgumentException>(() => AppendAsync(Revision("{}") with { Message = tooLong }));
        await Assert.ThrowsAsync<ArgumentException>(() => AppendAsync(Revision("{}") with { Message = loneSurrogate }));
        await Assert.ThrowsAsync<ArgumentException>(() => AppendAsync(Revision("{}") with { Summary = summaryWithLoneSurrogate }));
        var ok = await AppendAsync(Revision("{}") with { Message = new string('m', ConfigRevisionStore.MaxMessageLength) });
        Assert.Equal(1, ok.Seq);
    }

    private async Task<RevisionInfo> AppendAsync(NewRevision revision)
    {
        await using var context = _db.NewContext();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        var info = await _store.AppendAsync(context, revision, Ct);
        await transaction.CommitAsync(Ct);
        return info;
    }

    private async Task<T> ReadAsync<T>(Func<Paduraksa.Data.ConfigDbContext, Task<T>> read)
    {
        await using var context = _db.NewContext();
        return await read(context);
    }

    private static NewRevision Revision(string json, Actor? actor = null, Channel channel = Channel.Cli) =>
        Revision(Json(json), actor, channel);

    private static NewRevision Revision(byte[] document, Actor? actor = null, Channel channel = Channel.Cli) =>
        new(document, "[]", actor ?? Actor.Cli, channel);

    private static byte[] Json(string json) => Encoding.UTF8.GetBytes(json);

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            brotli.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var brotli = new BrotliStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }
}
