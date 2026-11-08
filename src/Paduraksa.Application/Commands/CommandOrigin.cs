using System.Net;

using Paduraksa.Core.Model;

namespace Paduraksa.Application.Commands;

/// <summary>
/// Who sent a command and through which surface, as the surface authenticated it. Never part of the command DTO: a
/// client can't claim to be someone else.
/// </summary>
/// <param name="Actor">Who acted.</param>
/// <param name="Channel">Where the command came in (recorded with the revision and the audit event).</param>
/// <param name="ClientIp">The client address, for commands that came in over the network.</param>
/// <param name="AdminRequest">Set when the command arrived through the admin app's internal listener, i.e. proxied from the
/// public listener (PLAN §3.1). The lock-out guard refuses a change that would cut this caller's own way in unless it says so.</param>
public sealed record CommandOrigin(Actor Actor, Channel Channel, IPAddress? ClientIp = null, AdminRequestFacts? AdminRequest = null)
{
    /// <summary>True when the command arrived through the admin app's internal listener.</summary>
    public bool ViaAdminProxy => AdminRequest is not null;

    /// <summary>Paduraksa itself.</summary>
    public static CommandOrigin System { get; } = new(Actor.System, Channel.System);

    /// <summary>The local CLI.</summary>
    public static CommandOrigin Cli { get; } = new(Actor.Cli, Channel.Cli);
}
