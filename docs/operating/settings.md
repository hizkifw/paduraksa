# Server settings

Paduraksa's process settings come from `appsettings.json` and environment variables. In Docker and systemd they are environment variables, with the section `Paduraksa` and its keys joined by double underscores: `Paduraksa:Listen:Admin` becomes `Paduraksa__Listen__Admin`.

These are settings for the **process** (ports, files, limits, retention). Hosts, routes, certificates and policies are config, changed in the dashboard or API: see the [user guide](../guide/getting-started.md).

- Invalid settings stop the start: standard error shows `Invalid configuration:` and one line per problem, and the exit code is 3. Any other failure to start prints `Paduraksa failed to start: ...` with exit code 2. `PADURAKSA_DEBUG=1` adds the stack trace.
- A relative `Paduraksa__DataDirectory` resolves against the application's directory (`/data` in the image), and a leading `~` is refused.
- Durations are `[d.]hh:mm:ss`, for example `00:00:15` or `30.00:00:00`.
- Settings are read once at start, so restart after changing them.
- The **[server settings](../guide/server-settings.md)** are the exception: retention, the admin sign-in policy, notification timing, the access log's query strings, the Cloudflare range refresh, the DNS-01 check and wait, MCP and the public connection limits. They can also be set in the dashboard (Settings, Server), and most apply live. A value set here wins and shows read-only there.

## List settings

A list setting (`AdminAllowedSources`, `CertificateNames`, the PROXY protocol `TrustedSources`, ACME
`DnsPropagation:Resolvers`, the access log's `SensitiveQueryKeys`) takes **either** one comma-separated value or indexed
keys.

```sh
Paduraksa__Listen__AdminAllowedSources=192.168.1.0/24,10.0.0.0/8
Paduraksa__Listen__AdminAllowedSources__0=192.168.1.0/24
Paduraksa__Listen__AdminAllowedSources__1=10.0.0.0/8

Paduraksa__Admin__CertificateNames=paduraksa.lan,admin.example.com
Paduraksa__Admin__CertificateNames__0=paduraksa.lan

Paduraksa__Listen__HttpsProxyProtocol__TrustedSources=192.0.2.10
Paduraksa__Acme__DnsPropagation__Resolvers=1.1.1.1,9.9.9.9:53
Paduraksa__AccessLog__SensitiveQueryKeys=token,api_key,pin
```

Paduraksa trims values and drops empty entries. If both forms are set, the comma-separated one wins, and a startup warning says the indexed entries were ignored.

## Environment variables

| Variable | What |
|---|---|
| `PADURAKSA_MASTER_KEY_FILE` | The path of the master key file. See [The master key](master-key.md) |
| `PADURAKSA_MASTER_KEY` | The master key itself |
| `PADURAKSA_BACKUP_PASSPHRASE` | The passphrase for `paduraksa backup` and `restore`. See [Backups](backups.md) |
| `PADURAKSA_TOKEN`, `PADURAKSA_URL` | The API token and the admin address for `paduraksa config`. See [The command line](../automation/cli.md) |
| `PADURAKSA_DEBUG` | `1` or `true`: print the full exception when the server cannot start |
| `Logging__LogLevel__Default` (and others) | The console's log level. `Information` by default |

## Listeners: `Paduraksa__Listen__*`

| Setting | Default | What |
|---|---|---|
| `Http`, `Https` | `80`, `443` | The public ports. 443 serves TCP and UDP. Ports are 1 to 65535 and must be distinct |
| `PublicHttpsPort` | `443` | The HTTPS port clients reach. Used in `Alt-Svc` and the HTTP to HTTPS redirect. Set it only behind a port mapping |
| `Http3` | `true` | Serve HTTP/3. Without `libmsquic` it is off regardless of this setting |
| `Admin` | `9443` | The admin app's HTTPS port |
| `AdminBind` | `0.0.0.0` | The address the admin app binds. The compose file publishes the port on loopback instead: see [Exposing the admin port](admin-access.md) |
| `AdminAllowedSources` | empty (no restriction) | Addresses and CIDRs allowed to connect to the admin port, checked before TLS. Loopback is **not** implied |
| `AdminInternal` | `true` | The internal listener an admin host uses |
| `AdminLoopbackHttp` | `0` (off) | A plain-HTTP admin port, on loopback only, for a tunnel |
| `HttpProxyProtocol`, `HttpsProxyProtocol` | off | PROXY protocol on that listener: `Enabled`, `TrustedSources`, `HeaderTimeout` (3 seconds). See [Client addresses](../guide/connections-and-limits.md#proxy-protocol) |

`Paduraksa__ShutdownTimeout` (default `00:00:30`) is the shared budget for the graceful stop: the public listeners drain, then the admin stops, then the core. Give the container 5 seconds more than this.

## Connection and timeout limits

`Paduraksa__Limits__*` apply to the public listeners only. The admin listener keeps the server's own defaults. All but `Http2MaxStreamsPerConnection` are also [server settings](../guide/server-settings.md), and a value set here wins over the dashboard's. Times are `hh:mm:ss`. A bad value stops the start and gives the reason.

| Setting | Default | What |
|---|---|---|
| `Paduraksa__Limits__RequestHeadersTimeout` | `00:00:15` | From the first byte of a request to the end of its headers. A slower client gets 408 and is closed. The timeout is per listener, because the host isn't known until the headers are read |
| `Paduraksa__Limits__KeepAliveTimeout` | `00:02:00` | How long an idle HTTP/1.1 or HTTP/2 connection waits for its next request |
| `Paduraksa__Limits__TlsHandshakeTimeout` | `00:00:10` | The TLS handshake over TCP |
| `Paduraksa__Limits__MinRequestBodyRate` | `120` | The slowest a request body may arrive, in bytes per second, once the grace is over. `0` turns it off |
| `Paduraksa__Limits__MinRequestBodyGrace` | `00:00:10` | Must be longer than one second |
| `Paduraksa__Limits__MinResponseRate` | `240` | The slowest a client may take a response. `0` turns it off |
| `Paduraksa__Limits__MinResponseGrace` | `00:00:10` | Must be longer than one second |
| `Paduraksa__Limits__MaxConnections` | `10000` | Open connections on the public listeners together. At the cap, new connections wait. `0` is unlimited |
| `Paduraksa__Limits__MaxUpgradedConnections` | `2000` | The WebSocket share of that. At the cap, a new upgrade is refused. `0` is unlimited |
| `Paduraksa__Limits__Http2MaxStreamsPerConnection` | `100` | Concurrent requests on one HTTP/2 connection. HTTP/3 keeps its fixed 100 |

The rates are low enough that a phone on a poor link still works, and they never cut a WebSocket or an event stream that sits idle between messages.

Raise `MaxConnections` if you serve many long-lived connections, such as a large number of WebSocket clients, and keep it below the process's open-file limit.

```sh
# Environment variables: a shorter header timeout, and room for more connections
Paduraksa__Limits__RequestHeadersTimeout=00:00:10
Paduraksa__Limits__MaxConnections=20000
```

## Admin sign-in and sessions: `Paduraksa__Admin__*`

The lockout, the session timings, the password length, the authenticator name and the sign-in rate limit are also [server settings](../guide/server-settings.md). Settings, Server, Sign-in changes them live, and a value set here wins and locks the dashboard's field.

| Setting | Default | What |
|---|---|---|
| `MaxFailedAttempts` | `5` | Consecutive wrong passwords or codes before an account is locked (2 to 100) |
| `LockoutDuration` | `00:15:00` | How long a locked account refuses sign-in (1 minute to 24 hours) |
| `SessionLifetime` | `12:00:00` | A session's absolute lifetime (5 minutes to 30 days) |
| `SessionIdleTimeout` | `02:00:00` | A session unused for this long ends (1 minute up to the session lifetime) |
| `PartialSessionLifetime` | `00:05:00` | How long you have to give the second factor after the password (30 seconds to 30 minutes) |
| `MinPasswordLength` | `12` | The shortest password (8 to 128 characters). There are no composition rules |
| `PasswordIterations` | `210000` | PBKDF2-HMAC-SHA-512 iterations for new password hashes (100 000 to 10 000 000). Older hashes are rehashed at the next sign-in |
| `TotpIssuer` | `Paduraksa` | The name authenticator apps show (1 to 64 characters, no colon) |
| `RateLimit__PermitsPerIp` | `20` | Sign-in attempts per client address per window, successful or not (an IPv6 client is a /64) |
| `RateLimit__PermitsPerUser` | `10` | Failed attempts per username per window |
| `RateLimit__Window` | `00:01:00` | The window of both |
| `CertificateNames` | none | Extra names the admin certificate covers (DNS names and IP addresses, at most 95, no wildcards) |
| `CertificateStartupTimeout` | `00:00:10` | How long the start waits for the admin certificate (1 second to 5 minutes) |
| `MaxRequestBodyBytes` | `65536` | The largest JSON body the admin API accepts (1 KiB to 16 MiB). Certificate uploads and config imports raise their own limits |
| `SessionRecheckInterval` | `00:00:30` | How often an open dashboard page re-checks its session |

An account that fails sign-in `MaxFailedAttempts` times in a row is locked for `LockoutDuration`. Every refusal shows the same "invalid credentials" message.

## ACME and DNS-01

These are the settings of `Paduraksa__Acme__*` and `Paduraksa__Certificates__*`. The DNS-01 check's switch and the fixed wait are also [server settings](../guide/server-settings.md) (Settings, Server, Integrations, ACME), and a value set here wins.

| Setting | Default | What |
|---|---|---|
| `Acme__Enabled` | `true` | Run the renewal scheduler. Off pauses all automatic ACME work, but you can still request and renew by hand |
| `Acme__SchedulerStartDelay` | `00:00:15` | How long after start the scheduler waits before its first look (0 to 1 hour) |
| `Acme__SchedulerPollInterval` | `00:01:00` | How often it looks for work (1 second to 1 day) |
| `Acme__DnsPropagationDelay` | `00:00:30` | The fixed wait after publishing DNS-01 records when the propagation check cannot run (0 to 1 hour) |
| `Acme__TrustedRootsFile` | none | A PEM file (at most 1 MiB) of self-signed root CAs to trust for ACME directory connections, for a private CA such as step-ca. A file with anything other than CA roots stops the start |
| `Certificates__RecoveryStartupTimeout` | `00:00:10` | How long the start waits for certificate recovery after a lost database (1 second to 5 minutes) |

### DNS-01 propagation check

`Paduraksa__Acme__DnsPropagation__*`:

| Setting | Default | Range |
|---|---|---|
| `Enabled` | `true` | Off keeps the fixed delay above, for a host that cannot send DNS to the zone's servers. Off also disables CNAME delegation |
| `Timeout` | `00:05:00` | 1 second to 1 hour |
| `InitialInterval`, `MaxInterval` | `00:00:02`, `00:00:15` | 0.1 second to 10 minutes; the maximum not below the initial |
| `QueryTimeout` | `00:00:03` | 0.1 to 30 seconds |
| `UnreachableRounds` | `3` | 1 to 20: rounds without an authoritative answer before a name server is dropped |
| `Resolvers` | empty (the system's) | At most 8 addresses: `1.1.1.1`, `10.0.0.2:53` |

## Cloudflare: `Paduraksa__Cloudflare__*`

`RangeRefresh__Enabled` and `RangeRefresh__Interval` are also [server settings](../guide/server-settings.md) (Settings, Server, Integrations, Cloudflare), and a value set here wins.

| Setting | Default | What |
|---|---|---|
| `RangeRefresh__Enabled` | `true` | Refresh Cloudflare's edge ranges daily. Off keeps the shipped list, or the last stored one |
| `RangeRefresh__StartDelay` | `00:02:00` | How long after start the first fetch waits (0 to 1 hour) |
| `RangeRefresh__Interval` | `1.00:00:00` | The time between fetches (1 minute to 7 days), with up to an hour of jitter added |
| `RangeRefresh__ApiUrl`, `TextV4Url`, `TextV6Url` | Cloudflare's own | `https` URLs (`http` only for loopback), for a mirror or a test |
| `OriginPullCaFile` | none | A PEM file that replaces the shipped Authenticated Origin Pulls CA |

See [Behind Cloudflare](../guide/cloudflare.md).

## Logs, metrics and retention

[Logs, metrics and notifications](../guide/observability.md) documents these with the features they configure:

- `Paduraksa__Logs__*`: the log directory, level, rotation and retention.
- `Paduraksa__AccessLog__*`: the write queue, the live tail size, the query string and the redacted keys.
- `Paduraksa__Metrics__*`: how long one-minute rollups are kept.
- `Paduraksa__Analytics__*`: how long analytics are kept.
- `Paduraksa__Notifications__*`: the dashboard address for links, and delivery timing.

## MCP

`Paduraksa__Mcp__Enabled` (default `false`) and `Paduraksa__Mcp__IdleTimeout` (default `00:10:00`). Both are also [server settings](../guide/server-settings.md): Settings, Server, Integrations, MCP turns it on live, with no restart. See [MCP](../automation/mcp.md).

## Honeypot

`Paduraksa__Honeypot__MaxConcurrent` (default `16`, 1 to 1024) caps the trap answers generated at once, across hosts. A request over the cap gets `503` with `Retry-After`. See [the honeypot](../guide/security.md#honeypot).

## Development switches

`Paduraksa__Development__PlaceholderCertificate` and `Paduraksa__Development__AcmeInsecureHttp` exist for tests and development and should stay off.

- The first serves a self-signed certificate for names that have none, and the process warns at startup if it is on.
- The second allows `http://` ACME URLs for loopback hosts only.
