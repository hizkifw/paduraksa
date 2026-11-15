# Logs, metrics and notifications

Paduraksa writes two log files, counts every request, shows traffic in the dashboard and can tell you when something needs attention.

| Where | What |
|---|---|
| **Overview** | Live traffic charts, the hosts and upstreams that need attention, the latest changes |
| **Security overview** (Security) | What protects each host and how much work each protection has done: see [Access control and WAF](security.md#the-security-overview) |
| **Analytics** (Insight) | Visitors, content, clients and bots |
| **Logs**, **Live tail** (Insight) | The log files, and requests as they end |
| `/metrics` | Prometheus |
| **Notifications** (Settings) | Alerts to ntfy, Gotify, Discord, a webhook or email |
| **Audit** (Insight) and **History** | Who changed what, and every revision |

## Logs

Paduraksa writes two plaintext logs into `/data/logs/`:

- `error.log`: the process's own entries, Warning and above by default.
- `access.log`: one line per request, for every host.

Both rotate by size and by UTC day. A rotated file is named after the moment (`error-20261109T120000Z.log`) and gzip-compressed (`....log.gz`). Old ones are trimmed by count, age and total size. A request never waits for a disk. The directory is created 0700 and the files 0600, because the access log holds client IPs. The directory appears with the first log line, so a fresh install that has logged nothing has no `logs/` directory.

### Browsing the logs

Browse, tail and download the files from **Logs** in the dashboard (Insight), or through `GET /api/v1/logs`, `GET /api/v1/logs/{name}/tail` and `GET /api/v1/logs/{name}`. All of it needs `logs:read`, which Owner, Admin and Auditor hold. A token can be scoped to it. Reading a log is not an audited event.

A tail returns at most 1000 lines (200 unless you ask for more), each cut at 4096 characters. Downloading gives the whole file.

Requests as they end are in **Live tail** (Insight, `logs:read`). `GET /api/v1/logs/tail` streams them as Server-Sent Events:

```sh
# Follow server errors as they happen, starting with the last 50
curl -N -H "Authorization: Bearer ..." "https://.../api/v1/logs/tail?statusClass=5xx&backlog=50"
```

The tail shows what `access.log` would, redacted the same way. A host with its access log off is in neither. It keeps the newest `Paduraksa__AccessLog__TailSize` requests in memory. The API description lists the filters and events.

The first-run setup token's banner is logged at Warning, so on a fresh instance it is also written to `error.log`. The token is single-use, and Paduraksa deletes it when setup completes. Until an Owner exists, though, it can be found in the logs.

The rotation size and the three retention limits are [server settings](server-settings.md). Settings, Server changes them live, and a value set in the environment wins over the dashboard's. The directory and the error level stay here.

| Setting | Default | What |
|---|---|---|
| `Paduraksa__Logs__Directory` | empty (`/data/logs`) | Where the logs live. A relative path resolves against the data directory |
| `Paduraksa__Logs__ErrorLevel` | `Warning` | Lowest level `error.log` keeps (`Information`, `Warning`, `Error`, `Critical`). Independent of `Logging__LogLevel`, which is the console's |
| `Paduraksa__Logs__MaxFileSize` | `67108864` (64 MiB) | Rotate when the next line would pass this (1 KiB to 16 GiB) |
| `Paduraksa__Logs__RetainedFiles` | `30` | Rotated files kept per log, newest first (1 to 10 000) |
| `Paduraksa__Logs__MaxAge` | `30.00:00:00` | Delete rotated files older than this (1 minute to 10 years) |
| `Paduraksa__Logs__MaxTotalSize` | `1073741824` (1 GiB) | Delete the oldest rotated files until each log's rotated files total at most this (1 MiB to 1 TiB) |

The logs live in the data volume, so a backup of it carries them. Deleting them loses history and nothing else. To keep them on a bigger disk, mount that directory and point `Paduraksa__Logs__Directory` at it. It must be writable by uid 10001.

### Access log and request IDs

Every request gets one line in the access log, whatever answered it: a forwarded request, a 403, the HTTPS redirect, the 404 for an unknown name. A full write queue drops lines and says so at Warning in `error.log`.

A request carries an `X-Request-Id`. Paduraksa generates one, or keeps the one a **trusted proxy** sent. Settings, then a host, a profile or the global defaults, then Request ID, chooses whether it goes upstream, back to the client, or both. A client that isn't a trusted proxy can't choose its own ID.

| Setting | Default | What |
|---|---|---|
| `Paduraksa__AccessLog__RingSize` | `16384` | The write queue between requests and the log writer: a power of two from 1024 to 1048576 |
| `Paduraksa__AccessLog__TailSize` | `4096` | Requests the **Live tail** and `GET /api/v1/logs/tail` keep in memory: 64 to 65536. A full default buffer takes up to about 32 MiB, and 65536 up to about 512 MiB, so size it with that in mind if clients you don't trust can reach the proxy. A restart empties it |
| `Paduraksa__AccessLog__LogQueryString` | `true` | Write the query string. `false` logs the path only |
| `Paduraksa__AccessLog__SensitiveQueryKeys` | the well-known names | Query parameters whose value becomes `name=REDACTED`: `token`, `access_token`, `refresh_token`, `id_token`, `code`, `password`, `passwd`, `pwd`, `secret`, `client_secret`, `key`, `api_key`, `apikey`, `signature`, `sig`, `auth`, `session` (ignoring case). Setting it **replaces** that list |

Cookies and `Authorization` never reach the log, and a CR or LF in a path or user agent is escaped. One host can turn its access log off (Settings, the host, Access log, Off). Its requests still get a request ID, and they appear in neither the file nor the live tail.

```sh
# Environment variables: a bigger write queue, no query strings, and your own list of redacted keys
Paduraksa__AccessLog__RingSize=65536
Paduraksa__AccessLog__LogQueryString=false
Paduraksa__AccessLog__SensitiveQueryKeys=token,api_key,pin
```

**The line.** One request per line. Fields are separated by one space, text fields are in double quotes (`\"`, `\\` and `\xNN` escapes inside), and `-` stands for a value that isn't there:

```
2026-01-31T12:34:56.789Z 198.51.100.7 "app.example" "GET /a/b?token=REDACTED HTTP/2" 200 512 1.234 0.987 "http://10.0.0.2:8080/" "0k3m9x2p-00000000000abc" "8f2a1c3d4e5f6789" "https://example.com/" "Mozilla/5.0" 2.101 1 0.000 0.012 0.000 0.105 0.987
```

| # | Field | What |
|---|---|---|
| 1 | time | When the request ended, UTC, to the millisecond |
| 2 | client | The client's address (behind a trusted proxy, the one its header carried) |
| 3 | host | The `Host` as received |
| 4 | request | Method, path with its query (sensitive values redacted) and protocol |
| 5 | status | What the client got. `-` when the connection was dropped without an answer |
| 6 | bytes | Response body bytes sent |
| 7 | total | The whole request, in milliseconds with three decimals |
| 8 | upstream | The forward to the upstream (connect, send, wait for its headers). `-` when Paduraksa answered itself |
| 9 | upstream address | The destination the request went to |
| 10 | request ID | `X-Request-Id` |
| 11 | CF-Ray | Only from a trusted Cloudflare edge or tunnel |
| 12 | referer | |
| 13 | user agent | |
| 14 | TLS | The connection's TLS handshake, on its first request only. `-` on plain HTTP and on a connection's later requests |
| 15 | connection request | Which request of its connection this is, from 1. Above 1 on TLS, the handshake was reused |
| 16 | body | From the first to the last request body byte read. `0.000` without a body |
| 17 | gate | From the top of the request to the gate's hand-off: access lists, rules, rate limits, auth, proof of work |
| 18 | connect | The upstream connect (DNS, TCP, TLS), inside field 8. `0.000` on a reused connection, `-` when nothing was forwarded |
| 19 | send | From the response's headers to its last byte handed to the client. A slow reader or a streamed answer shows here |
| 20 | wait | Time to first byte: from when the request was sent upstream to the upstream's response headers. `0.000` when the upstream answered before the body was all sent, `-` when nothing was forwarded |

Durations are milliseconds. The stages overlap, so they don't add up to the total. Fields 14 to 19 came after the first 13, and field 20 after them, so a parser of the first fields still works. Lines written before field 20 existed have 19 fields.

The **Live tail** shows the same figures for an entry as a waterfall, like a browser's network timing. Rows for the proxy's own work and for time no stage covers are muted, so the rows add up to the whole request. Hover, focus or tap a row for what happens in that stage.

## Metrics

Every request is counted (by host, route, upstream and destination) and timed, whether or not its host writes an access log. The dashboard's home page draws the counters live. The last hour survives a restart, and the day, week and month ranges read one-minute rollups kept in `paduraksa.db`.

**Restarts and downtime.** A minute the server wasn't running shows as a shaded gap in the charts, not as zero requests. A quiet hour with the server up is a line at zero. Rates are over the seconds the server was running, so the minute of a restart isn't diluted.

A graceful stop (`docker stop`, a SIGTERM) writes the minute in progress. A crash loses it, and up to one write interval of finished minutes.

**Prometheus.** The admin port serves the text format at `/metrics` (the same endpoint is `/api/v1/metrics`). It needs a bearer API token with the `metrics:read` scope. Every role holds that scope, so create a token for a viewer and give it only that:

```yaml
scrape_configs:
  - job_name: paduraksa
    scheme: https
    metrics_path: /metrics
    authorization:
      credentials_file: /etc/prometheus/paduraksa-token   # pdk_...
    tls_config:
      ca_file: /etc/prometheus/paduraksa-root.pem          # GET /api/v1/ca/root.pem
    static_configs:
      - targets: ["proxy.lan:9443"]
```

A series is labelled `host` (the host's first name) and `host_id`. Upstreams, destinations and routes carry their typed IDs (`upstream_id`, `destination_id`, `route_id`), so a rename doesn't break a series. Requests answered for a name no host has are the `(unmatched)` host. The families:

| Family | Labels | What |
|---|---|---|
| `paduraksa_requests_total` | host, status_class | Requests answered, by class of status (`1xx` to `5xx`, `none` for a dropped connection) |
| `paduraksa_gate_outcomes_total` | host, outcome | What the gate did: `forwarded`, `answered`, `refused_access_list`, `refused_rule_set`, `refused_rate_limit`, `refused_proof_of_work`, `refused_forward_auth`, `refused_request_limits`, `refused_connection`, `no_route`, `unavailable`, `dropped`, `unknown_host`, `refused_other` |
| `paduraksa_response_size_bytes_total`, `paduraksa_request_size_bytes_total` | host | Body bytes sent to clients and read from them. An upstream's gzip counts compressed |
| `paduraksa_request_duration_seconds` | host | Histogram of the whole request |
| `paduraksa_host_upstream_duration_seconds` | host | Histogram of the upstream's share. The difference is the proxy's own time |
| `paduraksa_tls_handshake_duration_seconds` | host | Histogram of client TLS handshakes (TCP and QUIC), counted against the host of the connection's first request |
| `paduraksa_response_send_duration_seconds` | host | Histogram of the time from a response's headers to its last byte written: slow readers and streamed answers |
| `paduraksa_route_*` | host, route_id, path | The same counters per route |
| `paduraksa_upstream_requests_total`, `_errors_total` (kind), `paduraksa_upstream_duration_seconds` | upstream_id | Forwards, failures by kind, and upstream time |
| `paduraksa_upstream_response_size_bytes_total`, `_request_size_bytes_total` | upstream_id | Body bytes of the requests forwarded to the upstream, as the client sent and got them |
| `paduraksa_destination_requests_total`, `_errors_total` | upstream_id, destination_id | Per destination |
| `paduraksa_rate_limit_decisions_total`, `paduraksa_proof_of_work_events_total`, `paduraksa_access_list_events_total` | result / event | The process totals the Security overview shows |
| `paduraksa_connection_errors_total` | kind | `peer_gone`: a client that vanished mid-write, such as a dropped phone or a sleeping laptop. A sudden rise is worth a look |
| `paduraksa_config_generation`, `paduraksa_hosts` | | The live config |

Histogram buckets end at 2^e - 1 microseconds for e from 4 to 26 (`le="0.000015"` to `le="67.108863"`). An observation of exactly 16 microseconds is in `le="0.000031"`, not below.

**OpenTelemetry.** The same counters are `System.Diagnostics.Metrics` instruments on the meter `Paduraksa` (`paduraksa.requests`, `paduraksa.gate.outcomes`, `paduraksa.response.size`, `paduraksa.request.size`, `paduraksa.upstream.errors`, ...). A latency is a sum in seconds: `paduraksa.request.duration.sum`, and `paduraksa.request.upstream.duration.sum` for the upstream's share of it.

The retention is a [server setting](server-settings.md) (Settings, Server, live). The write interval stays here.

| Setting | Default | What |
|---|---|---|
| `Paduraksa__Metrics__Retention` | `90.00:00:00` | How long a one-minute rollup is kept (1 hour to 5 years). Older rows are deleted hourly |
| `Paduraksa__Metrics__WriteInterval` | `00:00:15` | How often finished minutes are written to the database (1 second to 10 minutes) |

Counters start from zero at every start, as Prometheus expects.

**The overview API.** `GET /api/v1/metrics/overview?range=hour|day|week|month` (`metrics:read`) returns the chart as `series`, oldest first. Each step has `start`, `widthSeconds`, `coveredSeconds` and the rates over those seconds.

- A step with `coveredSeconds` 0 is a time the server wasn't running, with all figures 0. Draw a gap, not a zero.
- A step with coverage and `requestsPerSecond` 0 was quiet.

## Analytics

The dashboard's **Analytics** page (Insight) and `GET /api/v1/analytics` show visitors, content and clients:

- unique visitors;
- the top paths, referers and user agents, the slowest paths and the largest responses;
- browsers, systems and devices, and bots by kind;
- the bots that ignore a host's `robots.txt`.

They are built from the same data as `access.log`, so **a host with its access log off is in neither**. Turn the log off for a host and its traffic is not recorded anywhere.

Under them, the **Security** section shows what the gate refused over the same range, by reason and over time, and the counters of proof of work, rate limits, access lists, the connection stage and sign-in (see [the security overview](security.md#the-security-overview)). Those come from the metrics, so they count every host whatever its access-log setting.

**What is stored.** Nothing in them is a client address. An address is hashed with a random salt that lives in memory only, then counted and discarded. What is stored can't be turned back into an address, even by someone who holds the database.

- A visitor active across a restart is counted once on each side of it, because the salt dies with the process.
- Query strings are never counted, and a referer is reduced to its scheme and host.
- User agents are the raw strings the clients sent.
- Visitors and the tables are approximate by design, and the page says so. Visitors are within about 1.6 %.

Analytics are kept for the retention, and finer detail for a shorter time. The retention is a [server setting](server-settings.md) (Settings, Server, live). The write interval stays here.

| Setting | Default | What |
|---|---|---|
| `Paduraksa__Analytics__Retention` | `90.00:00:00` | How long an hour of analytics is kept (1 hour to 5 years). Older rows are deleted hourly |
| `Paduraksa__Analytics__WriteInterval` | `00:00:15` | How often closed buckets are written to the database (1 second to 10 minutes) |

A graceful stop writes the bucket in progress. A crash loses it, and the time after it shows as a gap on the charts, not as zeros.

## Notifications

Add channels (ntfy, Gotify, Discord, a webhook, email) in the dashboard under Settings, Notifications. Their secrets are in the vault. These five settings are [server settings](server-settings.md). Set them under Settings, Server, or in the environment, which then wins:

| Setting | Default | What |
|---|---|---|
| `Paduraksa__Notifications__AdminUrl` | none | The address the dashboard is reached at (`https://admin.example.com`: http or https, no login, query or fragment). Messages link to the page that shows what happened. Empty sends no link, because Paduraksa can't know which name or port you reach it by |
| `Paduraksa__Notifications__HealthHold` | `00:00:10` | How long a destination's new health state must hold before it is reported (1 second to 10 minutes), so one failed probe sends nothing |
| `Paduraksa__Notifications__CoalesceWindow` | `00:00:03` | How long a channel waits after a message for more of the same kind, so ten upstreams going down at once is one digest (0 to 1 minute) |
| `Paduraksa__Notifications__MaxDeliveryAge` | `01:00:00` | A message still waiting after this long is marked expired instead of sent, so a long outage does not end in a burst of stale messages (1 minute to 7 days) |
| `Paduraksa__Notifications__HistoryRetention` | `14.00:00:00` | How long a finished event and its deliveries stay in the notification history (1 hour to 5 years). Older ones are deleted hourly |

**Delivery is durable and at least once.** A restart, a crash or a channel that is down loses nothing, apart from events from the last quarter second before a kill.

- Failed deliveries are retried after 15 seconds, then 1, 4 and 16 minutes, and the retries carry on after a restart.
- A crash between the service accepting a message and Paduraksa recording it sends the message again at the next start, so a message can arrive twice.
- A webhook receiver drops the repeat by the **event ID**: `eventId` in the JSON body and the `X-Paduraksa-Event-Id` header. Both are signed when the channel has a signing key. The ID is the same on every resend, and a digest has its own.
- Settings, Notification history lists every event and delivery, and a failed or expired one can be sent again. `/metrics` shows the queue depth and the deliveries sent, failed, expired and dropped, by kind of channel.

A channel's secrets (a webhook URL, a token, an SMTP password) are stored in the vault, never shown again and never in the config, the audit log or a backup of the config alone.

### Channels

Add them under **Settings**, **Notifications**. Adding one sends it a test message at once. Needs `providers:write`.

| Kind | How it sends |
|---|---|
| ntfy | A JSON publish to the server's root, with an optional bearer token |
| Gotify | `POST /message`, with the token in `X-Gotify-Key` |
| Discord | An embed, with mentions off |
| Webhook | JSON. With a signing key it sends `X-Paduraksa-Timestamp` and `X-Paduraksa-Signature: sha256=hex(HMAC-SHA-256("<timestamp>." + body))` |
| Email | SMTP. STARTTLS can be required, so a server that does not offer it is refused and never sent the password. A login without TLS is refused |

A channel's URL or SMTP host may be any address, including loopback and the admin port, because only someone with `providers:write` can set it. Redirects are refused. A service's response is never shown, only a fixed line such as "The server refused the credentials (HTTP 401)."

### Events

Each channel picks the events it wants:

- **Upstream down and up**, and **destination down and up**. A state must hold for `HealthHold` (10 seconds) before it is reported.
    - New channels start without the per-destination pair, which a one-destination upstream would say twice.
- **Certificate expiry**, for every certificate whatever its source: 30, 14, 7 and 1 days before. It is sent once per certificate and threshold, and a renewal starts over.
- **ACME failure and recovery:** the first failure of a streak, and the recovery after it.
- **Sign-in anomalies:** an account locked by failed sign-ins, and an Owner or Admin signing in from an address with no completed sign-in as that user in the last 30 days.
