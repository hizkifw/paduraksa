# Threat model

What Paduraksa defends against, what it does not, and what to do about it. The full analysis (assets, boundaries, every attacker with its mitigations and tests, and the open gaps) is the [design page](design/threat-model.md).

## How it is deployed

Paduraksa is a reverse proxy and WAF in **one process on one node**. It faces the internet, holds your TLS keys and DNS tokens, and runs a second, privileged web surface, the admin app.

- **Public listeners:** 80/tcp, 443/tcp and 443/udp (HTTP/3). They can be reachable from the internet, the LAN, or only from [Cloudflare](guide/cloudflare.md).
- **Admin port:** 9443/tcp, HTTPS only, with a certificate from the vault's local CA. The server binds it on every interface inside the container; the compose file publishes it on `127.0.0.1` only. The public listeners do not serve the admin routes at all.
- **Data:** `/data` holds the config database, the vault and the logs ([where things live](operating/docker.md#where-things-live)).
- **Master key:** read from `PADURAKSA_MASTER_KEY_FILE` (a Docker secret or systemd credential), else `PADURAKSA_MASTER_KEY`, else a generated `data/master.key` ([The master key](operating/master-key.md)).
- **Users:** a few trusted people with roles (Owner, Admin, Operator, Viewer, Auditor) and API tokens. It is not a multi-tenant service.
- **Packaging:** the Docker image (non-root uid 10001, read-only root file system, dropped capabilities, `no-new-privileges`) or a hardened systemd unit.

## What it defends against

- **An anonymous internet client.** Per-address connection caps, slow-client limits, request limits, rate limits, rule sets, access lists, the browser check and forward-auth, all before a request reaches an upstream. Client IP and scheme headers are trusted only from the proxies you name. See [Access control and WAF](guide/security.md) and [Connections, limits and client addresses](guide/connections-and-limits.md).
- **A low-privilege admin user.** Roles limit what each person can change, and no response ever returns a secret. A user cannot raise their own role.
- **A stolen API token.** A token shows its secret once, is stored hashed, can expire, and is capped by its owner's permissions. It cannot create or revoke tokens or sessions, change users or passwords, or make a backup unless an Owner holds it.
- **A malicious upstream.** It cannot read or plant the admin session cookie, see the admin routes, or choose a forward-auth identity.
- **A hostile forward-auth provider or notification endpoint.** Forward-auth fails closed, and notification senders refuse redirects and never echo a response.
- **A prompt-injected MCP script.** MCP is off by default, and a script runs in a separate, killable process with no network, no files and no secrets ([MCP](automation/mcp.md)).
- **Someone who copies `/data` or a backup file.** Secrets are encrypted under the master key, and a backup is encrypted under its own passphrase. Keep the key off the volume.
- **Dependencies.** Pinned base images, weekly dependency updates, a vulnerability audit in CI and an SBOM on the image.

## What it does not defend against

1. **Root on the host, or a shell as the process's user.** Either can read the data directory, the master key and process memory. The vault is a boundary inside one process, not a defence against a local attacker.
2. **The master key together with the data directory**, or with a backup's source vault. That is a total compromise of every stored secret and the local CA.
3. **A malicious Owner.** The Owner holds every permission, including backups. Operator and Admin are trusted with the node's network position too: `config:write` lets them route to any internal address.
4. **Volumetric DDoS and anything beyond one node.** There is no clustering and no scrubbing. Put Cloudflare in front and lock down the origin.
5. **Deep inspection.** Rule sets match the path, method and user agent. The proxy does not scan bodies, and does not protect a vulnerable upstream from its own bugs.
6. **Your applications' own sign-in.** Forward-auth and basic auth are gates in front of them, not a replacement.
7. **Kernel, runtime and framework vulnerabilities** (Kestrel, YARP, .NET, OpenSSL, the OS). Keep the image up to date.
8. **The MCP sandbox on Windows, macOS or a kernel without Landlock.** The child still has limits and a kill, and the server logs what applied.
9. **An admin port on an untrusted network.** The admin port is for a trusted LAN or an access-listed name. Exposed to the internet it has only sign-in throttling, lockout and optional TOTP in front of it.
10. **Multi-tenancy.** Users share one config. Roles limit what they can do, not what they can see of the topology.
11. **Several nodes.** There is one node; fleet mode is after v0.1.
12. **Browsers without `__Host-` cookie or `SameSite` support**, and Safari over the plain-HTTP loopback fallback.
13. **Passkeys, OIDC and trusted-header admin sign-in.** They are not built, so there is nothing to rely on.

## What to do about it

In order of how much risk each step removes:

1. **Keep the master key off the data volume.** Use `PADURAKSA_MASTER_KEY_FILE` with a Docker secret or systemd credential, as the shipped compose file does. `PADURAKSA_MASTER_KEY` shows in `docker inspect` and `/proc/<pid>/environ`. Back the key up apart from the volume. The overview warns the Owner while it sits beside the vault ([The master key](operating/master-key.md)).
2. **Do not expose the admin port to the internet.** Keep the loopback mapping, or map it to a management network, or set `Paduraksa__Listen__AdminAllowedSources`, or publish the admin only as a host behind an access list ([Exposing the admin port](operating/admin-access.md)). Turn on TOTP for every user. Never proxy a public name to `127.0.0.1:9443`.
3. **Behind Cloudflare, turn on origin lockdown** and, for the strongest form, Authenticated Origin Pulls, and firewall 80 and 443 to Cloudflare's ranges. Without them the origin can be reached by IP and Cloudflare's WAF bypassed ([Behind Cloudflare](guide/cloudflare.md)).
4. **Turn protections on.** A new host has no rate limit, access list, rule set or browser check. Add the ones you need, check each host in the Security overview and the Simulator, and force HTTPS on any host with basic auth or forward-auth.
5. **Leave MCP off unless you want it.** If you turn it on, give assistants a read-only token. The model provider sees what the token can read. Never give an assistant `backup:create` or `providers:write`.
6. **Back up regularly, and test a restore.** Use a long random passphrase, keep it apart from the archive and from the master key, and rotate DNS tokens, ACME accounts and certificate keys if an archive and its passphrase leak ([Backups and restore](operating/backups.md)).
7. **Give tokens a short expiry and the fewest scopes.** Revoke the ones you no longer use, and do not reuse an Owner's token for automation.
8. **Trust the local CA root only on devices you administer.** It can mint a certificate for any name on a device that trusts it, unless you limited it to names you chose. Rotate it if the vault may have leaked ([The local CA](guide/tls.md#the-local-ca)).
9. **Know what the logs hold.** The access log keeps client IPs, paths and user agents for 30 days or 1 GiB. Paths are logged as sent, so keep secrets out of URL paths. Token-like query parameters are redacted, and `Cookie` and `Authorization` are never logged. The Auditor role and the `logs:read` scope can read IPs ([Logs, metrics and notifications](guide/observability.md)).
10. **Update on a schedule.** Pin the image tag or digest, and read the release notes for security changes ([Upgrading](operating/upgrading.md)).
11. **Keep `/data` at 0750 or tighter**, owned by uid 10001, and do not make backups or the key file world-readable.
12. **Keep the container hardening** (`read_only`, `cap_drop: [ALL]` plus `NET_BIND_SERVICE`, `no-new-privileges`). With MCP on, leave room for four 384 MiB sandbox processes in the memory limit.
13. **Set up notifications** for certificate expiry, ACME failures, upstream health and sign-in anomalies. Run the audit verifier (`GET /api/v1/audit/verify`) and keep its head hash somewhere the server cannot write: the audit chain catches partial edits, not a rewrite by someone who can write the database.
