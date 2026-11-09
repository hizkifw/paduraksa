using Paduraksa.Core.Ids;

namespace Paduraksa.Core.Model;

/// <summary>What kind of principal acted. Persisted by name (<see cref="ModelNames"/>).</summary>
public enum ActorKind
{
    /// <summary>Paduraksa itself: startup, schedulers (ACME renewals, retention), migrations.</summary>
    System = 1,

    /// <summary>The local CLI (<c>paduraksa ...</c> on the host), which runs with the operator's shell access.</summary>
    Cli = 2,

    /// <summary>A signed-in admin user (<see cref="Actor.UserId"/>).</summary>
    User = 3,

    /// <summary>An API token (<see cref="Actor.TokenId"/>).</summary>
    Token = 4,
}

/// <summary>
/// Who did something: the author of a config revision or the actor of an audit event. A user or token actor names
/// its typed ID; system and CLI actors name nothing. Shared by Data (columns) and Application (stores, commands).
/// </summary>
public readonly record struct Actor
{
    private Actor(ActorKind kind, UserId? userId, ApiTokenId? tokenId)
    {
        Kind = kind;
        UserId = userId;
        TokenId = tokenId;
    }

    public static Actor System { get; } = new(ActorKind.System, null, null);

    public static Actor Cli { get; } = new(ActorKind.Cli, null, null);

    public ActorKind Kind { get; }

    /// <summary>Set exactly when <see cref="Kind"/> is <see cref="ActorKind.User"/>.</summary>
    public UserId? UserId { get; }

    /// <summary>Set exactly when <see cref="Kind"/> is <see cref="ActorKind.Token"/>.</summary>
    public ApiTokenId? TokenId { get; }

    public static Actor ForUser(UserId userId) => new(ActorKind.User, NotEmpty(userId), null);

    public static Actor ForToken(ApiTokenId tokenId) => new(ActorKind.Token, null, NotEmpty(tokenId));

    /// <summary>
    /// Rebuilds an actor from its stored parts, e.g. the columns of a row. The parts must agree: a user ID exactly for
    /// <see cref="ActorKind.User"/>, a token ID exactly for <see cref="ActorKind.Token"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The parts don't describe an actor.</exception>
    public static Actor FromParts(ActorKind kind, UserId? userId, ApiTokenId? tokenId) => (kind, userId, tokenId) switch
    {
        (ActorKind.System, null, null) => System,
        (ActorKind.Cli, null, null) => Cli,
        (ActorKind.User, { } user, null) => ForUser(user),
        (ActorKind.Token, null, { } token) => ForToken(token),
        _ => throw new ArgumentException($"Not an actor: kind {kind}, user {userId?.ToString() ?? "none"}, token {tokenId?.ToString() ?? "none"}."),
    };

    /// <summary>
    /// <c>system</c>, <c>cli</c>, or the user's or token's typed ID (<c>usr_...</c>, <c>tok_...</c>); <c>undefined</c> for
    /// <c>default(Actor)</c>.
    /// </summary>
    public override string ToString() => Kind switch
    {
        ActorKind.User => UserId.ToString()!,
        ActorKind.Token => TokenId.ToString()!,
        ActorKind.System or ActorKind.Cli => Kind.ToName(),
        _ => "undefined",
    };

    private static T NotEmpty<T>(T id)
        where T : struct, ITypedId<T>
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException($"An actor can't be the empty {typeof(T).Name}.", nameof(id));
        }

        return id;
    }
}
