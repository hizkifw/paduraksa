namespace Paduraksa.Core.Model;

/// <summary>
/// A route's place in the order its host tries routes: exact routes first, then the longest prefix, then
/// <see cref="Order"/>, then the order the route was listed in.
/// </summary>
/// <remarks>
/// A host sorts its routes by this key once, when the config is compiled, so matching stays a plain scan and the
/// list order never decides between routes with different paths. Exact routes come first: only one can match a path,
/// but it must beat a prefix that also does. Among prefixes, path length is specificity: route paths are canonical and
/// prefixes are segment-aware, so <c>/api</c> takes <c>/api</c> and <c>/api/x</c>, never <c>/apix</c>: when two prefixes
/// can both take a request, the shorter is a segment-ancestor of the longer, so the longer names the narrower set. <see cref="Order"/> used to decide outright (first match won); it
/// now only breaks a tie, which is what stops a catch-all listed first from silently shadowing every route after it.
/// </remarks>
/// <param name="Match">Exact routes come before prefix routes.</param>
/// <param name="PathLength">The canonical path's length: longer wins among prefixes.</param>
/// <param name="Order">The route's <c>Order</c>: lower wins between routes of equal specificity.</param>
/// <param name="Listed">The route's position in the list: lower wins the remaining ties.</param>
public readonly record struct RouteOrderKey(RouteMatchKind Match, int PathLength, int Order, int Listed) : IComparable<RouteOrderKey>
{
    /// <summary>The key of a route; a null path (an invalid document) ranks as the shortest.</summary>
    public static RouteOrderKey For(RouteMatchKind match, string? path, int order, int listed) =>
        new(match, path?.Length ?? 0, order, listed);

    public int CompareTo(RouteOrderKey other)
    {
        var byKind = Rank(Match).CompareTo(Rank(other.Match));
        if (byKind != 0)
        {
            return byKind;
        }

        // Length only ranks prefixes: two exact routes that can take one request have the same path.
        if (Match == RouteMatchKind.Prefix)
        {
            var byLength = other.PathLength.CompareTo(PathLength);
            if (byLength != 0)
            {
                return byLength;
            }
        }

        var byOrder = Order.CompareTo(other.Order);
        return byOrder != 0 ? byOrder : Listed.CompareTo(other.Listed);
    }

    // An explicit rank keeps the order total if a match kind is ever added.
    private static int Rank(RouteMatchKind match) => match switch
    {
        RouteMatchKind.Exact => 0,
        RouteMatchKind.Prefix => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(match), match, "A route match kind without a place in the route order."),
    };

    public static bool operator <(RouteOrderKey left, RouteOrderKey right) => left.CompareTo(right) < 0;

    public static bool operator <=(RouteOrderKey left, RouteOrderKey right) => left.CompareTo(right) <= 0;

    public static bool operator >(RouteOrderKey left, RouteOrderKey right) => left.CompareTo(right) > 0;

    public static bool operator >=(RouteOrderKey left, RouteOrderKey right) => left.CompareTo(right) >= 0;
}
