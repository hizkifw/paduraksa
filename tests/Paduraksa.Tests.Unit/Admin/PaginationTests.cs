using Microsoft.AspNetCore.Http;

using Paduraksa.Admin.Api;
using Paduraksa.Contracts.Api;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Admin;

public sealed class PaginationTests
{
    private static HostId[] Ids(int count) => [.. Enumerable.Range(0, count).Select(_ => HostId.New())];

    private static DefaultHttpContext Request(string query) => new() { Request = { QueryString = new QueryString(query) } };

    private static Page<HostId> Page(IEnumerable<HostId> items, string query)
    {
        Assert.True(Pagination.TryPage(Request(query), items, id => id, out var page, out var problem), "paging was refused");
        Assert.Null(problem);
        return page;
    }

    [Fact]
    public void PagesWalkTheWholeCollectionAscendingByIdWithoutRepeatsAndTheLastHasNoCursor()
    {
        var all = Ids(7);
        List<HostId> seen = [];
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = Page(all, cursor is null ? "?limit=3" : $"?limit=3&cursor={cursor}");
            Assert.InRange(page.Items.Count, 1, 3);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(all.Order(), seen);
    }

    [Fact]
    public void ACollectionThatFitsIsOnePageWithNoCursorAndTheDefaultLimitIsFifty()
    {
        Assert.Null(Page(Ids(3), string.Empty).NextCursor);
        var big = Page(Ids(Pagination.DefaultLimit + 5), string.Empty);
        Assert.Equal(Pagination.DefaultLimit, big.Items.Count);
        Assert.NotNull(big.NextCursor);
        Assert.Equal(Pagination.MaxLimit, Page(Ids(Pagination.MaxLimit + 1), $"?limit={Pagination.MaxLimit}").Items.Count);
        Assert.Empty(Page([], string.Empty).Items);
    }

    [Fact]
    public void ACursorStaysValidWhenTheItemItNamesIsDeletedBetweenTwoRequests()
    {
        var all = Ids(5);
        var first = Page(all, "?limit=2");
        var remaining = all.Except([first.Items[^1]]);

        var second = Page(remaining, $"?limit=2&cursor={first.NextCursor}");

        Assert.Equal(all.Order().Skip(2).Take(2), second.Items);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=201")]
    [InlineData("?limit=-1")]
    [InlineData("?limit=abc")]
    [InlineData("?limit=1&limit=2")]
    [InlineData("?cursor=!!!")]
    [InlineData("?cursor=a&cursor=b")]
    public void AnUnusableLimitOrCursorIsRefused(string query)
    {
        Assert.False(Pagination.TryPage(Request(query), Ids(3), id => id, out _, out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void ACursorOfAnotherKindOfIdIsRefused()
    {
        var foreign = Pagination.Encode(UpstreamId.New());

        Assert.False(Pagination.TryPage(Request($"?cursor={foreign}"), Ids(3), id => id, out _, out var problem));
        Assert.NotNull(problem);
    }
}
