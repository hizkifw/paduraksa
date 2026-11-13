# Server settings

Paduraksa's process settings come from `appsettings.json` and environment variables. In Docker and systemd they are environment variables: the section `Paduraksa` and its keys are joined with double underscores, so `Paduraksa:Listen:Admin` is `Paduraksa__Listen__Admin`.

These are settings for the **process** (ports, files, limits, retention). Everything about hosts, routes, certificates and policies is config you change in the dashboard or API: see the [user guide](../guide/getting-started.md).

- Invalid settings stop the start with `Invalid configuration:` and one line per problem on standard error, and exit code 3. Any other failure to start is `Paduraksa failed to start: ...` with exit code 2. `PADURAKSA_DEBUG=1` adds the stack trace.
- A relative `Paduraksa__DataDirectory` resolves against the application's directory (`/data` in the image). A leading `~` is refused.
- Durations are `[d.]hh:mm:ss`, for example `00:00:15` or `30.00:00:00`.
- Settings are read once at start. Change them and restart.

## List settings

Settings that take a list (`AdminAllowedSources`, `CertificateNames`, the PROXY protocol `TrustedSources`, ACME
`DnsPropagation:Resolvers`, the access log's `SensitiveQueryKeys`) take **either** one comma-separated value or indexed
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

Values are trimmed and empty entries dropped; if both forms are set the scalar wins and a startup warning says the
indexed entries were ignored.

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
| `Http3` | `true` | Serve HTTP/3. Without `libmsquic` it is off whatever this says |
| `Admin` | `9443` | The admin app's HTTPS port |
| `AdminBind` | `0.0.0.0` | The address the admin app binds. The compose file publishes the port on loopback instead: see [Exposing the admin port](admin-access.md) |
| `AdminAllowedSources` | empty (no restriction) | Addresses and CIDRs allowed to connect to the admin port, checked before TLS. Loopback is **not** implied |
| `AdminInternal` | `true` | The internal listener an admin host uses |
| `AdminLoopbackHttp` | `0` (off) | A plain-HTTP admin port, on loopback only, for a tunnel |
| `HttpProxyProtocol`, `HttpsProxyProtocol` | off | PROXY protocol on that listener: `Enabled`, `TrustedSources`, `HeaderTimeout` (3 seconds). See [Client addresses](../guide/connections-and-limits.md#proxy-protocol) |

`Paduraksa__ShutdownTimeout` (default `00:00:30`) is the shared budget for the graceful stop: the public listeners drain, then the admin stops, then the core. Give the container 5 seconds more.

## Connection and timeout limits

`Paduraksa__Limits__*` are applied when the process starts, not reloadable, public listeners only: the admin listener keeps Kestrel's own. Times are `hh:mm:ss`; a bad value stops the start with the reason.


| Setting | Default | Kestrel's own | What |
|---|---|---|---|
| `Paduraksa__Limits__RequestHeadersTimeout` | `00:00:15` | 30 s | From the first byte of a request to the end of its headers. A client that is slower gets 408 and is closed (a slowloris). It is per listener: the host isn't known until the headers are read, so there is no per-host timeout. |
| `Paduraksa__Limits__KeepAliveTimeout` | `00:02:00` | 130 s | How long an idle HTTP/1.1 or HTTP/2 connection waits for its next request. |
| `Paduraksa__Limits__TlsHandshakeTimeout` | `00:00:10` | 10 s | The TLS handshake over TCP. |
| `Paduraksa__Limits__MinRequestBodyRate` | `120` | 240 B/s | The slowest a request body may arrive, in bytes per second, once the grace is over. `0` turns it off. |
| `Paduraksa__Limits__MinRequestBodyGrace` | `00:00:10` | 5 s | Must be longer than one second. |
| `Paduraksa__Limits__MinResponseRate` | `240` | 240 B/s | The slowest a client may take a response. `0` turns it off. |
| `Paduraksa__Limits__MinResponseGrace` | `00:00:10` | 5 s | Must be longer than one second. |
| `Paduraksa__Limits__MaxConnections` | `10000` | none | Open connections on the public listeners together; at it Kestrel stops accepting. `0` is unlimited. |
| `Paduraksa__Limits__MaxUpgradedConnections` | `2000` | none | The WebSocket share of that; at it a new upgrade is refused. `0` is unlimited. |
| `Paduraksa__Limits__Http2MaxStreamsPerConnection` | `100` | 100 | Concurrent requests on one HTTP/2 connection. |

The rates are more forgiving than Kestrel's because the slow client worth keeping is a phone on a poor link. They do not cut a
WebSocket or an event stream that sits idle between messages: Kestrel stops enforcing the request rate on an upgraded connection
and times a response only while a write waits for the client. HTTP/3 has no stream setting (Kestrel's stable API has none): a QUIC
connection keeps the transport's default of 100 concurrent requests. Raise `MaxConnections` if you serve many long-lived
connections (a large number of WebSocket clients), and keep it below the process's open-file limit.

```sh
Paduraksa__Limits__RequestHeadersTimeout=00:00:10
Paduraksa__Limits__MaxConnections=20000
```

```sh
Paduraksa__Limits__RequestHeadersTimeout=00:00:10
Paduraksa__Limits__MaxConnections=20000
```

## Admin sign-in and sessions: `Paduraksa__Admin__*`

| Setting | Default | What |
|---|---|---|
| `MaxFailedAttempts` | `5` | Consecutive wrong passwords or codes before an account is locked (2 to 100) |
| `LockoutDuration` | `00:15:00` | How long a locked account refuses sign-in (1 minute to 24 hours) |
| `SessionLifetime` | `12:00:00` | A session's absolute lifetime (5 minutes to 30 days) |
| `SessionIdleTimeout` | `02:00:00` | A session unused for this long ends (1 minute up to the lifetime) |
| `PartialSessionLifetime` | `00:05:00` | How long you have to give the second factor after the password (30 seconds to 30 minutes) |
| `MinPasswordLength` | `12` | The shortest password (8 to 128 characters). There are no composition rules |
| `PasswordIterations` | `210000` | PBKDF2-HMAC-SHA-512 iterations for new password hashes (100 000 to 10 000 000). Older hashes are rehashed at sign-in |
| `TotpIssuer` | `Paduraksa` | The name authenticator apps show (1 to 64 characters, no colon) |
| `RateLimit__PermitsPerIp` | `20` | Sign-in attempts per client address per window, successful or not (an IPv6 client is a /64) |
| `RateLimit__PermitsPerUser` | `10` | Failed attempts per username per window |
| `RateLimit__Window` | `00:01:00` | The window of both |
| `CertificateNames` | none | Extra names the admin certificate covers (DNS names and IP addresses, at most 95, no wildcards) |
| `CertificateStartupTimeout` | `00:00:10` | How long the start waits for the admin certificate (1 second to 5 minutes) |
| `MaxRequestBodyBytes` | `65536` | The largest JSON body the admin API accepts (1 KiB to 16 MiB). Certificate uploads and config imports raise their own limits |
| `SessionRecheckInterval` | `00:00:30` | How often an open dashboard page re-checks its session |

An account that fails sign-in `MaxFailedAttempts` times in a row is locked for `LockoutDuration`, and every refusal reads as the same "invalid credentials". The lockout ends sessions only where the attacker already holds the password.

## ACME and DNS-01

Settings of `Paduraksa__Acme__*` and `Paduraksa__Certificates__*`.

| Setting | Default | What |
|---|---|---|
| `Acme__Enabled` | `true` | Run the renewal scheduler. Off pauses everything ACME does on its own; you can still request and renew by hand |
| `Acme__SchedulerStartDelay` | `00:00:15` | How long after start the scheduler's first look waits (0 to 1 hour) |
| `Acme__SchedulerPollInterval` | `00:01:00` | How often it looks for work (1 second to 1 day) |
| `Acme__DnsPropagationDelay` | `00:00:30` | The fixed wait after publishing DNS-01 records when the propagation check cannot run (0 to 1 hour) |
| `Acme__TrustedRootsFile` | none | A PEM file (at most 1 MiB) of self-signed root CAs to trust for ACME directory connections, for a private CA such as step-ca. Anything that is not a CA root stops the start |
| `Certificates__RecoveryStartupTimeout` | `00:00:10` | How long the start waits for certificate recovery after a lost database (1 second to 5 minutes) |

### DNS-01 propagation check

`Paduraksa__Acme__DnsPropagation__*`:

| Setting | Default | Range |
|---|---|---|
| `Enabled` | `true` | Off keeps the fixed delay above, for a host that cannot send DNS to the zone's servers. It also turns off CNAME delegation |
| `Timeout` | `00:05:00` | 1 second to 1 hour |
| `InitialInterval`, `MaxInterval` | `00:00:02`, `00:00:15` | 0.1 second to 10 minutes; the maximum not below the initial |
| `QueryTimeout` | `00:00:03` | 0.1 to 30 seconds |
| `UnreachableRounds` | `3` | 1 to 20: rounds without an authoritative answer before a name server is left out |
| `Resolvers` | empty (the system's) | At most 8 addresses: `1.1.1.1`, `10.0.0.2:53` |

## Cloudflare: `Paduraksa__Cloudflare__*`

| Setting | Default | What |
|---|---|---|
| `RangeRefresh__Enabled` | `true` | Refresh Cloudflare's edge ranges daily. Off keeps the list that shipped, or the last stored one |
| `RangeRefresh__StartDelay` | `00:02:00` | How long after start the first fetch waits (0 to 1 hour) |
| `RangeRefresh__Interval` | `1.00:00:00` | The time between fetches (1 minute to 7 days), with up to an hour of jitter added |
| `RangeRefresh__ApiUrl`, `TextV4Url`, `TextV6Url` | Cloudflare's own | `https` URLs (`http` only for loopback), for a mirror or a test |
| `OriginPullCaFile` | none | A PEM file that replaces the shipped Authenticated Origin Pulls CA |

See [Behind Cloudflare](../guide/cloudflare.md).

## Logs, metrics and retention

These are documented with what they configure, on [Logs, metrics and notifications](../guide/observability.md):

- `Paduraksa__Logs__*`: the log directory, level, rotation and retention.
- `Paduraksa__AccessLog__*`: the ring, the live tail size, the query string and the redacted keys.
- `Paduraksa__Metrics__*`: how long one-minute rollups are kept.
- `Paduraksa__Analytics__*`: how long analytics are kept.
- `Paduraksa__Notifications__*`: the dashboard address for links, and delivery timing.

## MCP

`Paduraksa__Mcp__Enabled` (default `false`) and `Paduraksa__Mcp__IdleTimeout` (default `00:10:00`). See [MCP](../automation/mcp.md).

## Development switches

`Paduraksa__Development__PlaceholderCertificate` and `Paduraksa__Development__AcmeInsecureHttp` exist for tests and development. Leave them off. The first makes a release build serve an in-memory self-signed certificate for names that have none, and the process warns at startup when it is on. The second allows `http://` ACME URLs for loopback hosts only.
