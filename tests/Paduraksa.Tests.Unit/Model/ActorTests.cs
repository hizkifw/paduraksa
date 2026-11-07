using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Model;

public sealed class ActorTests
{
    [Fact]
    public void UserAndTokenActorsCarryExactlyTheirId()
    {
        var user = UserId.New();
        var token = ApiTokenId.New();

        Assert.Equal((ActorKind.User, user, (ApiTokenId?)null), Parts(Actor.ForUser(user)));
        Assert.Equal((ActorKind.Token, (UserId?)null, token), Parts(Actor.ForToken(token)));
        Assert.Equal((ActorKind.System, (UserId?)null, (ApiTokenId?)null), Parts(Actor.System));
        Assert.Equal((ActorKind.Cli, (UserId?)null, (ApiTokenId?)null), Parts(Actor.Cli));
    }

    [Fact]
    public void FromPartsRoundTripsEveryKind()
    {
        foreach (var actor in new[] { Actor.System, Actor.Cli, Actor.ForUser(UserId.New()), Actor.ForToken(ApiTokenId.New()) })
        {
            Assert.Equal(actor, Actor.FromParts(actor.Kind, actor.UserId, actor.TokenId));
        }
    }

    public static TheoryData<ActorKind, bool, bool> MismatchedParts => new()
    {
        { ActorKind.System, true, false },
        { ActorKind.Cli, false, true },
        { ActorKind.User, false, false },
        { ActorKind.User, true, true },
        { ActorKind.Token, false, false },
        { ActorKind.Token, true, false },
        { (ActorKind)0, false, false },
    };

    [Theory]
    [MemberData(nameof(MismatchedParts))]
    public void FromPartsRejectsPartsThatDisagree(ActorKind kind, bool withUser, bool withToken) =>
        Assert.Throws<ArgumentException>(() =>
            Actor.FromParts(kind, withUser ? UserId.New() : null, withToken ? ApiTokenId.New() : null));

    [Fact]
    public void EmptyIdsAreNotActors()
    {
        Assert.Throws<ArgumentException>(() => Actor.ForUser(UserId.Empty));
        Assert.Throws<ArgumentException>(() => Actor.ForToken(ApiTokenId.Empty));
    }

    [Fact]
    public void ToStringIsTheKindNameOrTheTypedId()
    {
        var user = UserId.New();

        Assert.Equal("system", Actor.System.ToString());
        Assert.Equal("cli", Actor.Cli.ToString());
        Assert.Equal(user.ToString(), Actor.ForUser(user).ToString());
        Assert.StartsWith("tok_", Actor.ForToken(ApiTokenId.New()).ToString(), StringComparison.Ordinal);
        Assert.Equal("undefined", default(Actor).ToString());
    }

    private static (ActorKind, UserId?, ApiTokenId?) Parts(Actor actor) => (actor.Kind, actor.UserId, actor.TokenId);
}
