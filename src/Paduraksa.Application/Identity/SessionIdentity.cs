using System.Net;
using System.Security.Claims;

using Paduraksa.Application.Commands;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Identity;

/// <summary>
/// The identity of a request authenticated by a full session cookie, as the admin app's authentication established it (PLAN
/// §10.1). The dashboard reads it from the request that opens a page or a circuit, so the circuit acts for the same user, from
/// the same client, as the request that authenticated it; it carries no credential (the cookie value stays with the request).
/// </summary>
/// <remarks>
/// The claims are the conventional ones (<see cref="ClaimTypes.NameIdentifier"/> is the user's ID, <see cref="ClaimTypes.Name"/>
/// the username, <see cref="ClaimTypes.Role"/> each role's name, <see cref="SessionClaim"/> the session's ID), so framework
/// features that read claims (antiforgery's per-user tokens, <c>AuthorizeView Roles</c>) work unchanged; code in this solution
/// reads the typed properties instead.
/// </remarks>
public sealed class SessionIdentity : ClaimsIdentity
{
    /// <summary>The authentication type: a session cookie checked by <see cref="SessionService"/>.</summary>
    public const string Scheme = "Paduraksa.Session";

    /// <summary>The claim carrying the session's ID.</summary>
    public const string SessionClaim = "paduraksa:session";

    /// <summary>Creates the identity of a validated session.</summary>
    /// <param name="session">The session and its user, from <see cref="SessionService"/>.</param>
    /// <param name="clientIp">The client's address as the admin app resolved it (on the internal listener, the forwarded one).</param>
    /// <param name="adminRequest">The request facts the lock-out guard needs when the request came through the proxy (PLAN §3.1).</param>
    public SessionIdentity(SessionPrincipal session, IPAddress? clientIp = null, AdminRequestFacts? adminRequest = null)
        : base(ClaimsOf(session), Scheme, ClaimTypes.Name, ClaimTypes.Role)
    {
        Session = session;
        ClientIp = clientIp;
        AdminRequest = adminRequest;
    }

    /// <summary>The session and its user.</summary>
    public SessionPrincipal Session { get; }

    /// <summary>The client's address.</summary>
    public IPAddress? ClientIp { get; }

    /// <summary>Set when the request came through the proxy's internal admin listener.</summary>
    public AdminRequestFacts? AdminRequest { get; }

    /// <summary>The command origin of something this user does through <paramref name="channel"/>.</summary>
    public CommandOrigin OriginFor(Channel channel) => new(Core.Model.Actor.ForUser(Session.UserId), channel, ClientIp, AdminRequest);

    /// <summary>The same client and request facts with a re-read session (fresh roles and display name).</summary>
    public SessionIdentity With(SessionPrincipal session) => new(session, ClientIp, AdminRequest);

    /// <summary>The session identity of <paramref name="principal"/>, or null when it has none (anonymous, or another kind).</summary>
    public static SessionIdentity? Of(ClaimsPrincipal? principal) =>
        principal?.Identities.OfType<SessionIdentity>().FirstOrDefault();

    private static IEnumerable<Claim> ClaimsOf(SessionPrincipal session)
    {
        ArgumentNullException.ThrowIfNull(session);
        yield return new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString());
        yield return new Claim(ClaimTypes.Name, session.Username);
        yield return new Claim(SessionClaim, session.SessionId.ToString());
        foreach (Role role in session.Roles)
        {
            yield return new Claim(ClaimTypes.Role, role.ToName());
        }
    }
}
