using System.Net;

namespace Paduraksa.Application.Commands;

/// <summary>The caller's request facts when a config command arrived through the admin app's internal listener.</summary>
/// <param name="Host">The <c>Host</c> (<c>:authority</c>) the public client sent, port included when it wasn't the default.</param>
/// <param name="Https">The trusted request scheme (<c>X-Forwarded-Proto</c>).</param>
/// <param name="ClientIp">The resolved client address; null when the proxy couldn't resolve one.</param>
public sealed record AdminRequestFacts(string Host, bool Https, IPAddress? ClientIp);
