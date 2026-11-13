# Troubleshooting

Start with the logs: `docker logs paduraksa`, then **Logs** and **Live tail** in the dashboard, then the **Simulator**, which says why a request was routed, redirected or refused. The messages below are the ones Paduraksa prints.

## It will not start

**The container restarts in a loop, and the log says `The master key file ... cannot be read by ...`.**
The full message ends with `This is a permissions problem, not a missing key.` A Docker file secret keeps the host's owner and mode, and the container runs as uid 10001. Run `sudo chown 10001:10001 master.key && sudo chmod 400 master.key`. See [The master key](master-key.md).

**`The value is sealed under a different master key.`**
The key you gave is not the one that created `vault.db`. Nothing can open a vault without its key. Find the original key, or [restore a backup](backups.md) onto a fresh data directory. A backup restores under the new machine's own key.

**`Invalid configuration:` followed by lines, exit code 3.**
A setting is out of range or malformed. Each line names the setting and the allowed range. See [Server settings](settings.md).

**`Paduraksa failed to start: ...`, exit code 2.**
Set `PADURAKSA_DEBUG=1` to print the stack trace. A port already in use, an unwritable data directory, a second server on the same data directory (the lock is `run/server.lock`) and a wrong master key all end here.

**The container is `unhealthy`.**
The check is `paduraksa doctor --liveness`. If you set `Paduraksa__Listen__AdminAllowedSources`, add `127.0.0.1/32`: loopback is not implied. See [Health endpoints](health.md).

## The first sign-in

**I lost the setup token.**
`docker compose exec paduraksa paduraksa setup-token` prints it again. After setup it says `Setup is complete: there is no setup token.`

**The browser warns about the admin certificate.**
It comes from the local CA, which your browser does not trust yet. Install its root: [Getting started](../guide/getting-started.md#3-trust-the-local-ca-root-optional).

**`https://<host>:9443/` does not answer from another machine.**
The compose file publishes it on loopback only. Use an SSH tunnel, or see [Exposing the admin port](admin-access.md).

**I forgot a password, or lost my authenticator.**
On the host: `paduraksa admin reset-password <user>` (reads the password from standard input or a prompt, ends the user's sessions and clears a lockout) or `paduraksa admin disable-2fa <user>`. See [The command line](../automation/cli.md).

**Sign-in answers 429, or the account is locked.**
Five wrong passwords in a row lock the account for 15 minutes. Sign-in is also throttled to 20 attempts a minute per client address. Wait, or reset the password from the host.

## A site does not work

**`502 Bad Gateway` and the error log shows `The remote certificate is invalid ... RemoteCertificateNameMismatch, RemoteCertificateChainErrors`.**
An `https://` destination with a self-signed certificate. Pin its fingerprint: [HTTPS upstreams](../guide/upstreams.md#https-upstreams).

**`502`, `503` or `504` from an upstream.**
`502` is a destination that refused or reset the connection, `504` is a connect or idle timeout, and `503` is no healthy destination. Check the Upstreams page for each destination's state. A WebSocket that is cut after an hour of silence is the idle timeout: see [Timeouts](../guide/upstreams.md#timeouts-websockets-and-event-streams).

**The page loads, but the app builds wrong URLs or refuses the host.**
The upstream gets the client's `Host` by default. If it needs its own name, set the upstream's **Host header sent to the upstream**.

**`404` for a name you set up.**
The host is disabled, the name is misspelled, or no route matches the path. A name no enabled host serves gets the global unknown-host answer (`404` by default). Ask the Simulator.

**`421 Misdirected Request`.**
Either the connection is weaker than the host allows (a minimum TLS version, HTTP/3 off, or Authenticated Origin Pulls with a connection that did not name the host in its SNI), or you chose `421` as the unknown-host answer. Browsers retry on a connection of their own.

**Every request to a host is `403`.**
An access list, a rule set or Cloudflare origin lockdown refused it. The Simulator names which, and the access log shows the status. Behind a proxy, check that it is listed as trusted: otherwise every visitor looks like the proxy.

**A redirect loop behind Cloudflare.**
Set the client address mode to a Cloudflare mode: Paduraksa then reads `CF-Visitor`. With "Flexible" SSL Cloudflare talks plain HTTP to the origin, and forcing HTTPS without `CF-Visitor` loops. Better still, use Full (strict).

**`413`, `414` or `431`.**
A request over a [request limit](../guide/connections-and-limits.md#request-limits). The 100 MiB body limit is the usual cause of a failed upload. Large cookies cause `431`.

**`408 Request Timeout`.**
A client took more than 15 seconds to send its headers, or a body or response is below the minimum rate. See [Slow clients](../guide/connections-and-limits.md#slow-clients-and-connection-limits).

**A sign-in provider's page is `502` or `504`.**
Forward-auth fails closed. `502` is an endpoint that cannot be reached or answers something Paduraksa cannot forward, `504` is the timeout, and `503` with `Retry-After: 1` is more than 64 calls in flight. A refusal with the wrong page usually means the provider's own sign-in route has sign-in **on**: it must be off.

**The browser check loops, or every request is `503`.**
`503` on a protected route means the check's signing key could not be opened. A loop means cookies are blocked, or the host serves plain HTTP: the cookie is `Secure` only over HTTPS.

## Certificates

**`no valid certificate covers <name>; they are served with the default certificate, if any`.**
No active certificate lists the name. Request one: [TLS and certificates](../guide/tls.md). Until then the name is served with the default certificate, or the handshake is rejected when there is none.

**ACME HTTP-01 fails.**
The CA must reach port 80 of the name from the internet. Check the firewall and the port mapping, and that the DNS record points at this machine. Wildcards and IP addresses are not HTTP-01 (wildcards need DNS-01). The certificate detail shows the CA's message and the next retry.

**ACME DNS-01 times out.**
The check asks the zone's own name servers. If your server cannot send DNS to them, turn the check off (`Paduraksa__Acme__DnsPropagation__Enabled=false`) to fall back to a fixed wait. A DNS provider **Test** shows whether the token works. A CNAME at `_acme-challenge` is followed, and the token must reach the target's zone.

**HTTP/3 is not used.**
The startup log says `HTTP/3 is on` or `HTTP/3 is off: libmsquic isn't available on this system`. Map 443/**udp**, and on bare metal install `libmsquic`.

## API and CLI

**`403` with `Cross-site request refused` (`csrf.refused`).**
A request with a session cookie must say where it comes from. Send an `Origin` header, or use an API token, which needs none.

**`428 Precondition required` or `412 Precondition failed`.**
A change to an existing object needs `If-Match` with the `ETag` you read. `412` means it changed since, and the problem's `conflict` says what is current. See [The REST API](../automation/api.md).

**`500` with `config.publishFailed`.**
The change was stored but could not go live. The answer carries the stored revision. Fix the cause and change again, or roll back from **History**.

**`paduraksa restore` says `A Paduraksa server is running on ...`.**
Stop the server first. A restore refuses while one runs on the data directory, and changes nothing.

**`paduraksa backup` says `... exists; a backup never overwrites.` or `The passphrase must be at least 12 characters.`**
Choose another file name, or a longer passphrase. Nothing was written.
