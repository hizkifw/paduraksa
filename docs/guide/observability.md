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

The process writes two plaintext logs into `/data/logs/`: `error.log` (its own entries, Warning and above by default) and
`access.log` (one line per request, per host). Both rotate by size and by UTC day; a rotated file is named after the
moment (`error-20261109T120000Z.log`), gzip-compressed in the background (`....log.gz`), and trimmed by count, age and total
size. A request never waits for a disk, and a log write that fails is retried and reported to the console, never to the
error log itself. The directory is created 0700 and the files 0600, because the access log holds client IPs. It appears
with the first log line, so a fresh install that has logged nothing has no `logs/` directory.

### Browsing the logs

Browse, tail and download the files from **Logs** in the dashboard (Insight), or through `GET /api/v1/logs`,
`GET /api/v1/logs/{name}/tail` and `GET /api/v1/logs/{name}`. All of it needs `logs:read`, which Owner, Admin and
Auditor hold (and a token can be scoped to it). Reading a log is not an audited event, like every other read. A tail is
bounded: at most 1000 lines (200 unless asked otherwise), read from the last 1 MiB of a plain file or, for a compressed
one, from what 8 Mi characters of decompression yields, with each line cut at 4096 characters; downloading gives the whole
file.

Requests as they end are in **Live tail** (Insight, `logs:read`), and `GET /api/v1/logs/tail` streams them as
Server-Sent Events (`curl -N -H "Authorization: Bearer ..." "https://.../api/v1/logs/tail?statusClass=5xx&backlog=50"`). The tail
shows what `access.log` would, redacted and capped the same way, and a host with its access log off is in neither. It keeps the
newest `Paduraksa__AccessLog__TailSize` requests in memory; see the API description for the filters and the events.

The first-run setup token's banner is logged at Warning, so on a fresh instance it is also written to `error.log` (the one
exception documented in [PLAN section 2](../PLAN.md#2-development-rules-and-how-they-are-enforced)). Until an Owner exists it can therefore be found in the logs, rotated `.gz` files
included; it is single-use and worthless once an Owner exists, and the process deletes the token file when setup completes.

| Setting | Default | What |
|---|---|---|
| `Paduraksa__Logs__Directory` | empty (`/data/logs`) | Where the logs live; a relative path resolves against the data directory |
| `Paduraksa__Logs__ErrorLevel` | `Warning` | Lowest level `error.log` keeps (`Information`, `Warning`, `Error`, `Critical`); independent of `Logging__LogLevel`, which is the console's |
| `Paduraksa__Logs__MaxFileSize` | `67108864` (64 MiB) | Rotate when the next line would pass this (1 KiB to 16 GiB) |
| `Paduraksa__Logs__RetainedFiles` | `30` | Rotated files kept per log, newest first (1 to 10 000) |
| `Paduraksa__Logs__MaxAge` | `30.00:00:00` | Delete rotated files older than this (1 minute to 10 years) |
| `Paduraksa__Logs__MaxTotalSize` | `1073741824` (1 GiB) | Delete the oldest rotated files until each log's rotated files total at most this (1 MiB to 1 TiB) |

The logs are inside the data volume, so a backup of it carries them, and they are not part of the proxy's state: deleting
them loses history, nothing else. If you would rather keep them elsewhere (a bigger disk), mount that directory too and
point `Paduraksa__Logs__Directory` at it; it must be writable by uid 10001.

### Access log and request IDs

Every request gets one line in the access log, whatever answered it (a forwarded request, a 403, the HTTPS redirect, the
404 for an unknown name). A request carries an `X-Request-Id`: Paduraksa generates one, or keeps the one a **trusted
proxy** sent, and sends it upstream and back to the client depending on the host (Settings → a host, a profile or the
global defaults → Request ID). A client that isn't a trusted proxy can't choose its own ID.

The hot path only writes a struct into a fixed-size ring; a background writer formats the lines and appends them to
`access.log` (the log above: it rotates, compresses and is trimmed like `error.log`, and the dashboard's **Logs** page can
tail it), so a slow disk never stalls a request. A full ring drops lines and counts them, reported at Warning in the
process log.

| Setting | Default | What |
|---|---|---|
| `Paduraksa__AccessLog__RingSize` | `16384` | Slots between the request path and the writer: a power of two from 1024 to 1048576 (about 3 MiB at the default: each slot is padded to three cache lines) |
| `Paduraksa__AccessLog__TailSize` | `4096` | Entries the dashboard's **Live tail** and `GET /api/v1/logs/tail` keep in memory: from 64 to 65536. They are redacted and capped like the file's lines. An entry is typically a few hundred bytes to a KiB (2 to 4 MiB for a full default buffer) and at most about 8 KiB with every field at its cap, so the worst case is about 32 MiB at the default and about 512 MiB at 65536: size it for that if clients you don't trust can reach the proxy. The buffer is not on disk, so a restart empties it. |
| `Paduraksa__AccessLog__LogQueryString` | `true` | Write the query string. `false` logs the path only. |
| `Paduraksa__AccessLog__SensitiveQueryKeys` | the well-known names | Query parameters whose value becomes `name=REDACTED`: `token`, `access_token`, `refresh_token`, `id_token`, `code`, `password`, `passwd`, `pwd`, `secret`, `client_secret`, `key`, `api_key`, `apikey`, `signature`, `sig`, `auth`, `session` (ignoring case). Setting it **replaces** that list. |

Cookies and `Authorization` never reach the log, and a value that would break a line (a CR or LF in a path or user agent)
is escaped. One host can turn its access log off (Settings → the host → Access log → Off); its requests still get a request ID,
and they appear in neither the file nor the live tail.

```sh
Paduraksa__AccessLog__RingSize=65536
Paduraksa__AccessLog__LogQueryString=false
Paduraksa__AccessLog__SensitiveQueryKeys=token,api_key,pin
```

## Metrics

Every request is counted (by host, route, upstream and destination) and timed, whether or not its host writes an access
log. The counters cost no allocation and no lock on the request path. The home page of the dashboard draws them live: the
last hour from memory and from the rollups the server started from, a day, a week or a month from one-minute rollups kept in
`paduraksa.db`.

**Restarts and downtime.** The one-hour view survives a restart: at startup the server reads the last hour of rollups, so the
chart doesn't begin at the boot. Every rollup minute records how many of its seconds the server was running, and a minute
with no record is a minute it wasn't, so a restart, a crash or a day offline is a shaded gap in the charts (a line is never
drawn through it, and it isn't drawn as zero requests); a quiet hour with the server up is a line at zero. Rates are over the
seconds the server was running, so the minute of a restart isn't diluted. A graceful stop (a `docker stop`, a SIGTERM) writes
the minute in progress; a crash loses it and up to one write interval of finished minutes. Minutes stored by an earlier
version had no records for idle time, so quiet stretches from before the upgrade also show as gaps.

**Prometheus.** The admin port serves the text format at `/metrics` (the same endpoint is `/api/v1/metrics`). It needs a
bearer API token with the `metrics:read` scope, which every role holds, so create a token for a viewer and give it only that
scope:

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

A series is labelled `host` (the host's first name) and `host_id`; upstreams, destinations and routes carry their typed IDs
(`upstream_id`, `destination_id`, `route_id`), so a rename doesn't break a series. Requests the proxy answered for a name
no host has are the `(unmatched)` host. The families:

| Family | Labels | What |
|---|---|---|
| `paduraksa_requests_total` | host, status_class | Requests answered by the class of status (`1xx` to `5xx`, `none` for a dropped connection) |
| `paduraksa_gate_outcomes_total` | host, outcome | What the gate did: `forwarded`, `answered`, `refused_access_list`, `refused_rule_set`, `refused_rate_limit`, `refused_proof_of_work`, `refused_forward_auth`, `refused_request_limits`, `refused_connection`, `no_route`, `unavailable`, `dropped`, `unknown_host`, `refused_other` |
| `paduraksa_response_size_bytes_total`, `paduraksa_request_size_bytes_total` | host | Body bytes sent to clients and read from them, counted as they passed (a chunked or streamed body counts, an upstream's gzip counts compressed, a WebSocket's frames count when it closes) |
| `paduraksa_request_duration_seconds` | host | Histogram of the whole request |
| `paduraksa_host_upstream_duration_seconds` | host | Histogram of the upstream's share; the difference is the proxy's own time |
| `paduraksa_route_*` | host, route_id, path | The same counters per route |
| `paduraksa_upstream_requests_total`, `_errors_total` (kind), `paduraksa_upstream_duration_seconds` | upstream_id | Forwards, YARP's failures by kind, and upstream time |
| `paduraksa_upstream_response_size_bytes_total`, `_request_size_bytes_total` | upstream_id | Body bytes of the requests forwarded to the upstream, as the client sent and got them |
| `paduraksa_destination_requests_total`, `_errors_total` | upstream_id, destination_id | Per destination |
| `paduraksa_rate_limit_decisions_total`, `paduraksa_proof_of_work_events_total`, `paduraksa_access_list_events_total` | result / event | The process totals the Security overview shows |
| `paduraksa_connection_errors_total` | kind | `peer_gone`: Kestrel errors from a client that vanished mid-write (a dropped phone or a sleeping laptop). They are logged at Debug as one line instead of filling `error.log` with stacks; a sudden rise is worth a look |
| `paduraksa_config_generation`, `paduraksa_hosts` | | The live snapshot |

Histogram buckets end at 2^e - 1 microseconds for e from 4 to 26 (`le="0.000015"` to `le="67.108863"`): an observation of exactly 16 microseconds is in `le="0.000031"`, not below.

**OpenTelemetry.** The same counters are `System.Diagnostics.Metrics` instruments on the meter `Paduraksa`
(`paduraksa.requests`, `paduraksa.gate.outcomes`, `paduraksa.response.size`, `paduraksa.request.size`, `paduraksa.upstream.errors`, ...), read only when a listener asks. A latency is
a sum in seconds (`paduraksa.request.duration.sum`, and `paduraksa.request.upstream.duration.sum` for the upstream's share of it); the buckets are the Prometheus endpoint's.

| Setting | Default | What |
|---|---|---|
| `Paduraksa__Metrics__Retention` | `14.00:00:00` | How long a one-minute rollup is kept (1 hour to 5 years); older rows are deleted hourly |
| `Paduraksa__Metrics__WriteInterval` | `00:00:15` | How often finished minutes are written to the database (1 second to 10 minutes) |

Counters start from zero at every start, as Prometheus expects.

**The overview API.** `GET /api/v1/metrics/overview?range=hour|day|week|month` (`metrics:read`) returns the chart as `series`:
every step from the first one the server was running for to the last, oldest first. Each step has `start`, `widthSeconds`
(the hour's steps from before the last start are a minute wide, the rest ten seconds; a longer range's steps are all
`bucketWidth`), `coveredSeconds` and the rates over those seconds. A step with `coveredSeconds` 0 is a
time the server wasn't running (all its figures are 0): draw a gap, not a zero. A step with coverage and `requestsPerSecond`
0 was quiet.

## Analytics

The dashboard's **Analytics** page (Insight) and `GET /api/v1/analytics` show visitors, content and clients: unique visitors,
the top paths, referers and user agents, the slowest paths and the largest responses, browsers, systems and devices, bots by kind,
and the bots that ignore a host's `robots.txt`. They are computed by the same background writer that writes `access.log`, never on
the request path, so **a host with its access log off is in neither**: turn it off for a host and its traffic is not recorded anywhere.

Nothing in them is a client address: an address is hashed with a random salt that lives in memory only, goes into a fixed-size
counting sketch (HyperLogLog) and is gone, so what is stored can't be turned back into an address, even by someone who holds the
database. The price of a salt that dies with the process is that a visitor active across a restart is counted once on each side of it.
Query strings are never counted (paths are counted without them), a referer is reduced to its scheme and host, and the user
agents are the raw strings the clients sent. Visitors and the tables are approximate by design (the page says so): about 1.6 % for
visitors, and every top-table figure has a documented bound ([observability.md, Overview](../design/observability.md#overview)).

Closed five-minute buckets are written to the config database every 15 seconds; an hour that has passed is rolled up into one row
per host for the day and week ranges, and a day that has passed into one row per host for the month. The five-minute rows are kept two days once rolled up, the hourly rows for the retention.

| Setting | Default | What |
|---|---|---|
| `Paduraksa__Analytics__Retention` | `30.00:00:00` | How long an hour of analytics is kept (1 hour to 5 years); older rows are deleted hourly |
| `Paduraksa__Analytics__WriteInterval` | `00:00:15` | How often closed buckets are written to the database (1 second to 10 minutes) |

A graceful stop writes the bucket in progress; a crash loses it and whatever was not yet written, and the time after it shows as a gap on the charts, not as zeros.

## Notifications

The channels (ntfy, Gotify, Discord, a webhook, email) are added in the dashboard under Settings, Notifications; their secrets are in the vault. Five
settings belong to the process, since only you know them:

| Setting | Default | What |
|---|---|---|
| `Paduraksa__Notifications__AdminUrl` | none | The address the dashboard is reached at (`https://admin.example.com`: http or https, no login, query or fragment). Messages link to the page that shows what happened; empty sends no link, because the process cannot know which name or port you reach it by |
| `Paduraksa__Notifications__HealthHold` | `00:00:10` | How long a destination's new health state must hold before it is reported (1 second to 10 minutes), so one failed probe sends nothing |
| `Paduraksa__Notifications__CoalesceWindow` | `00:00:03` | How long a channel waits after a message for more of the same kind, so ten upstreams going down at once is one digest (0 to 1 minute) |
| `Paduraksa__Notifications__MaxDeliveryAge` | `01:00:00` | A delivery that has been waiting longer than this (its event is that old, at start or when the channel takes it) is marked expired instead of sent, so a long outage of the process or of the database does not end in a burst of stale messages (1 minute to 7 days) |
| `Paduraksa__Notifications__HistoryRetention` | `14.00:00:00` | How long a finished event and its deliveries stay in the notification history (1 hour to 5 years); older ones are deleted hourly |

**Delivery is durable and at least once.** An event is written to the config database, with one delivery per subscribed channel, within about a quarter of a
second of happening, and the first message to a quiet channel is sent at once (a burst behind it is digested); a restart, a crash or a channel that is down loses nothing but that quarter second (events still in memory when the process is killed).
Retries (15 seconds, 1, 4 and 16 minutes after a transient failure) are kept in the database and carry on after a restart. A crash between the service accepting a
message and Paduraksa recording it sends the message again at the next start, so a message can arrive twice. A webhook receiver drops the repeat by the **event
ID**, which is `eventId` in the JSON body and the `X-Paduraksa-Event-Id` header (both signed with the rest when the channel has a signing key): the same ID on
every resend of the same message, and a digest's own ID when several events go out as one message. The history is under Settings, Notification history, where a
failed or expired delivery can be sent again; `/metrics` shows the queue depth and the deliveries sent, failed, expired and dropped by kind of channel.

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

A channel's URL or SMTP host may be any address, including loopback and the admin port, because only someone with `providers:write` can set it. Redirects are refused and responses are never shown, only fixed lines such as "The server refused the credentials (HTTP 401)."

### Events

Each channel picks the events it wants:

- **Upstream down and up**, and **destination down and up**. A state must hold for `HealthHold` (10 seconds) before it is reported, so one failed probe sends nothing. New channels start without the per-destination pair, which a one-destination upstream says twice.
- **Certificate expiry**, for every certificate whatever its source: 30, 14, 7 and 1 days before. It is sent once per certificate and threshold, and a renewal starts over.
- **ACME failure and recovery:** the first failure of a streak, and the recovery after it.
- **Sign-in anomalies:** an account locked by failed sign-ins, and an Owner or Admin signing in from an address with no completed sign-in as that user in the last 30 days.

### History

**Notification history** (Settings) lists every event and delivery. A failed or expired delivery can be sent again. `/metrics` shows the queue depth and the deliveries sent, failed, expired and dropped, by kind of channel.
