using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>
/// Route constraint for a typed ID: <c>/hosts/{id:HostId}</c> matches only a string that parses as a <see cref="HostId"/> (right prefix, right
/// length, valid base62). Anything else doesn't match the route and is a 404, and the handler receives an ID, never a string.
/// </summary>
public sealed class TypedIdRouteConstraint<TId> : IRouteConstraint
    where TId : struct, ITypedId<TId>
{
    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection)
    {
        ArgumentNullException.ThrowIfNull(routeKey);
        ArgumentNullException.ThrowIfNull(values);
        return values.TryGetValue(routeKey, out object? value)
            && value is string text
            && TId.TryParse(text, null, out TId id)
            && !id.IsEmpty;
    }
}

/// <summary>The typed IDs the API takes in routes, with their constraint registrations.</summary>
internal static class TypedIdRouting
{
    /// <summary>Registers one constraint per typed ID under its type's name.</summary>
    public static void Register(RouteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Add<HostId>(options);
        Add<UpstreamId>(options);
        Add<ProfileId>(options);
        Add<RouteId>(options);
        Add<CertificateId>(options);
        Add<ConfigRevisionId>(options);
        Add<AcmeAccountId>(options);
        Add<DnsProviderId>(options);
    }

    private static void Add<TId>(RouteOptions options)
        where TId : struct, ITypedId<TId> =>
        options.ConstraintMap[typeof(TId).Name] = typeof(TypedIdRouteConstraint<TId>);
}
