# Installing Paduraksa

Paduraksa v0.1 is a single-node deployment: one process, one data directory, three listeners. This page is the practical
install guide; the design is in [PLAN.md](PLAN.md) §3, §8 and §16.

- **Ports:** 80/tcp (HTTP, ACME HTTP-01, redirects), 443/tcp (HTTP/1.1 and HTTP/2) and **443/udp** (HTTP/3), and
  9443/tcp (the admin app).
- **Data:** one directory, mounted at `/data` in the container. It holds `paduraksa.db` (config, users, audit),
  `vault.db` (certificate keys, secrets, the Data Protection key ring), `last-good.json`, the admin socket under `run/`,
  the logs under `logs/` and, unless you supply the master key another way, `master.key`.
- **Master key:** the one key that opens `vault.db`. Keep a copy somewhere other than the data volume; losing it loses
  every certificate key, DNS credential, TOTP secret and the Data Protection key ring. Admin sessions do **not** depend on
  it: the admin session cookie is Paduraksa's own random value, stored hashed in `paduraksa.db`. See PLAN §8.3.
- **List settings** (`AdminAllowedSources`, `CertificateNames`, the PROXY protocol `TrustedSources`, ACME
  `DnsPropagation:Resolvers`, the access log's `SensitiveQueryKeys`) take **either** one comma-separated value or indexed
  keys; both bind, both are documented below.

## Docker (recommended)

Requirements: Docker 20.10+ with Compose v2. Docker sets `net.ipv4.ip_unprivileged_port_start=0` in the container's
network namespace, so the image's non-root user can bind 80/443; the apphost also carries `cap_net_bind_service` for
runtimes that do not.

1. Get [`deploy/compose.yaml`](../deploy/compose.yaml) into a directory of its own and start it. It runs
   `ghcr.io/hizkifw/paduraksa:main`, which CI publishes from every commit on `main` that passes, for amd64 and arm64:

   ```sh
   mkdir paduraksa && cd paduraksa
   curl -fsSLO https://raw.githubusercontent.com/hizkifw/paduraksa/main/deploy/compose.yaml
   docker compose up -d
   ```

   **To build the image yourself**, run compose from a checkout's `deploy/` directory with the commented `build:` block
   enabled (its context is the repository root, one directory up), and start it with `docker compose up -d --build`.
   Or build once with `docker build -t paduraksa:local .` in the repository root and set `image: paduraksa:local`.

   **A bind-mounted data directory** (instead of the named volume) must be owned by the container's uid 10001:

   ```sh
   sudo install -d -o 10001 -g 10001 -m 0750 ./data
   # compose: - ./data:/data
   ```

   **Ports and capabilities.** The default bridge network works because Docker sets the unprivileged-port floor to 0. The
   compose file runs the container hardened: a read-only root filesystem with a tmpfs `/tmp`, `no-new-privileges`, and
   `cap_drop: [ALL]` with `cap_add: [NET_BIND_SERVICE]` back. Keep that one capability if you change it, or exec of the
   setcap'd apphost fails with EPERM. In Kubernetes "restricted", add
   `securityContext.capabilities.add: ["NET_BIND_SERVICE"]`, or set the pod sysctl
   `net.ipv4.ip_unprivileged_port_start=0`.

2. **Read the setup token.** On the first start, Paduraksa prints a boxed banner at warning level with the one-time
   token that creates the first Owner. It is also written to `/data/setup-token`.

   ```sh
   docker logs paduraksa 2>&1 | grep -A8 "Paduraksa first-run setup"
   # or, at any time before setup completes:
   docker compose exec paduraksa paduraksa setup-token
   ```

   The token is single-use and is deleted once an Owner exists.

   **The master key as a Docker secret (recommended).** Uncomment the `secrets:`/`environment:` block in
   `deploy/compose.yaml`. Non-swarm Compose mounts a file secret as a bind mount and keeps the **host's** owner and mode,
   while the container runs as uid 10001, so the file must be readable by that uid:

   ```sh
   head -c 32 /dev/urandom | base64 | sudo tee master.key >/dev/null
   sudo chown 10001:10001 master.key
   sudo chmod 400 master.key
   ```

   A host-root `0600` file is unreadable for the container and makes it restart forever; the startup error says so, with
   the uid, when that happens. If you skip this step the vault generates `/data/master.key` (0600, uid 10001), which is
   fine for a quick try but puts the key in the same volume as the database.

3. **Trust the local CA root**, so the admin UI is not a certificate warning in your browser. Paduraksa issues its own
   admin certificate from a local CA; the root is offered unauthenticated, before setup, precisely so you can trust it
   first:

   ```sh
   curl -k -o paduraksa.crt https://<host>:9443/api/v1/ca/root.pem      # PEM: Linux/macOS trust stores
   curl -k -o paduraksa-root.crt https://<host>:9443/api/v1/ca/root.crt # DER: Android/iOS offer to install it
   curl -k https://<host>:9443/api/v1/ca                                # fingerprint, subject and validity as JSON
   ```

   `-k` is needed only because the certificate is not trusted yet. On Linux, `update-ca-certificates` reads PEM and
   ignores a DER file, so save `root.pem` as a `.crt`:
   `sudo cp paduraksa.crt /usr/local/share/ca-certificates/paduraksa.crt && sudo update-ca-certificates`; on macOS, add
   it to the System keychain and mark it trusted; on Windows, import it into *Trusted Root Certification Authorities*.
   Then open **`https://<host>:9443/`** and enter the setup token.

   The admin port is HTTPS only. A plain `http://<host>:9443/` GET is answered with a redirect to the same URL over
   `https://` (no page is ever served over plain HTTP), so a mistyped or old bookmark still lands on the panel. If you set
   `Paduraksa__Listen__AdminAllowedSources` (addresses/CIDRs), loopback is **not** implied - add `127.0.0.1/32` if you
   also want the container healthcheck (`paduraksa doctor --liveness`) and an SSH tunnel to work.

   Every list setting takes **either** one comma-separated value or indexed keys, so both shell conventions work:

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
   indexed entries were ignored. (`Paduraksa__Admin__CertificateNames` adds names; the process always covers
   `localhost`, the loopback addresses, `AdminBind` and its host name.)

4. **Point a host at it.** Add a host and a route in the dashboard or the REST API (or later YAML import); issue a
   certificate for it (ACME with HTTP-01 needs port 80 reachable from the internet, DNS-01 does not). The public
   listeners answer as soon as a host matches; the admin certificate never serves proxied traffic.

### UDP and HTTP/3

443/**udp** is HTTP/3. Map it 1:1 like the TCP ports; if it is missing or firewalled, browsers fall back to HTTP/2 over
443/tcp and nothing breaks (Paduraksa only advertises `Alt-Svc` for H3 when libmsquic loaded). The image installs
libmsquic and logs `HTTP/3 is on: QUIC on UDP 443 ...` at startup; if it says HTTP/3 is off, the package did not load.

Do not remap the container's ports: `Alt-Svc` and the HTTP→HTTPS redirect advertise 443, because that is the port
clients use. Behind a port translation, set `Paduraksa__Listen__PublicHttpsPort` to the outside port.

### Where things live, and backup

The named volume `paduraksa-data` holds everything:

| Path | What |
|---|---|
| `/data/paduraksa.db` | config, revisions, users, sessions, audit chain (SQLite, WAL) |
| `/data/vault.db` | sealed certificate keys, ACME/DNS secrets, TOTP secrets, the Data Protection key ring |
| `/data/master.key` | the KEK, **only** when you did not set `PADURAKSA_MASTER_KEY_FILE` or `PADURAKSA_MASTER_KEY` |
| `/data/last-good.json` | the last live config, used for a degraded start |
| `/data/setup-token` | until the first Owner exists |
| `/data/run/admin.sock` | the internal listener the public proxy uses for an admin host |
| `/data/logs/` | `access.log` and `error.log` (0700 directory, 0600 files), with rotated, gzip-compressed older files |

Back up the **data directory and the master key separately**. A copy of `vault.db` without the key is useless, and a copy
of the key without the volume opens nothing; together they are every credential the proxy holds. `paduraksa backup`
arrives in M10. To move or restore, stop the container and copy the whole volume.

### Logs

The process writes two plaintext logs into `/data/logs/`: `error.log` (its own entries, Warning and above by default) and
`access.log` (one line per request, per host). Both rotate by size and by UTC day; a rotated file is named after the
moment (`error-20261109T120000Z.log`), gzip-compressed in the background (`....log.gz`), and trimmed by count, age and total
size. A request never waits for a disk, and a log write that fails is retried and reported to the console, never to the
error log itself. The directory is created 0700 and the files 0600, because the access log holds client IPs. It appears
with the first log line, so a fresh install that has logged nothing has no `logs/` directory.

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
exception documented in PLAN §2). Until an Owner exists it can therefore be found in the logs, rotated `.gz` files
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

### Metrics

Every request is counted (by host, route, upstream and destination) and timed, whether or not its host writes an access
log. The counters cost no allocation and no lock on the request path. The home page of the dashboard draws them live: the
last hour from memory, a day, a week or a month from one-minute rollups kept in `paduraksa.db`.

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
| `paduraksa_response_size_bytes_total` | host | Bytes of responses with a known `Content-Length` |
| `paduraksa_request_duration_seconds` | host | Histogram of the whole request |
| `paduraksa_host_upstream_duration_seconds` | host | Histogram of the upstream's share; the difference is the proxy's own time |
| `paduraksa_route_*` | host, route_id, path | The same counters per route |
| `paduraksa_upstream_requests_total`, `_errors_total` (kind), `paduraksa_upstream_duration_seconds` | upstream_id | Forwards, YARP's failures by kind, and upstream time |
| `paduraksa_destination_requests_total`, `_errors_total` | upstream_id, destination_id | Per destination |
| `paduraksa_rate_limit_decisions_total`, `paduraksa_proof_of_work_events_total`, `paduraksa_access_list_events_total` | result / event | The process totals the Security overview shows |
| `paduraksa_connection_errors_total` | kind | `peer_gone`: Kestrel errors from a client that vanished mid-write (a dropped phone or a sleeping laptop). They are logged at Debug as one line instead of filling `error.log` with stacks; a sudden rise is worth a look |
| `paduraksa_config_generation`, `paduraksa_hosts` | | The live snapshot |

Histogram buckets end at 2^e - 1 microseconds for e from 4 to 26 (`le="0.000015"` to `le="67.108863"`): an observation of exactly 16 microseconds is in `le="0.000031"`, not below.

**OpenTelemetry.** The same counters are `System.Diagnostics.Metrics` instruments on the meter `Paduraksa`
(`paduraksa.requests`, `paduraksa.gate.outcomes`, `paduraksa.upstream.errors`, ...), read only when a listener asks. A latency is
a sum in seconds (`paduraksa.request.duration.sum`, and `paduraksa.request.upstream.duration.sum` for the upstream's share of it); the buckets are the Prometheus endpoint's.

| Setting | Default | What |
|---|---|---|
| `Paduraksa__Metrics__Retention` | `14.00:00:00` | How long a one-minute rollup is kept (1 hour to 5 years); older rows are deleted hourly |
| `Paduraksa__Metrics__WriteInterval` | `00:00:15` | How often finished minutes are written to the database (1 second to 10 minutes) |

A restart loses the minute in progress. Counters start from zero at every start, as Prometheus expects.

### Notifications

The channels (ntfy, Gotify, Discord, a webhook, email) are added in the dashboard under Settings, Notifications; their secrets are in the vault. Three
settings belong to the process, since only you know them:

| Setting | Default | What |
|---|---|---|
| `Paduraksa__Notifications__AdminUrl` | none | The address the dashboard is reached at (`https://admin.example.com`: http or https, no login, query or fragment). Messages link to the page that shows what happened; empty sends no link, because the process cannot know which name or port you reach it by |
| `Paduraksa__Notifications__HealthHold` | `00:00:10` | How long a destination's new health state must hold before it is reported (1 second to 10 minutes), so one failed probe sends nothing |
| `Paduraksa__Notifications__CoalesceWindow` | `00:00:03` | How long a channel waits after a message for more of the same kind, so ten upstreams going down at once is one digest (0 to 1 minute) |

### Upgrading

With the published image (the default compose file):

```sh
docker compose pull
docker compose up -d
```

With an image you build from a checkout:

```sh
git pull
cd deploy
docker compose up -d --build
```

Migrations are applied at startup, before the listeners bind. Stop with `docker compose down` (not `kill`): the
`stop_grace_period: 35s` gives Kestrel its 30-second drain. The compose example sets a stable `hostname`, so the admin
certificate's machine name does not change on a recreate; without it a new container gets a new name, the certificate is
re-issued under the same ID with a new serial (harmless), and `Paduraksa__Admin__CertificateNames` gives you a stable
name to use instead.

### Changing the master key

`paduraksa vault rotate-kek` re-wraps every key under a new KEK, including the Data Protection ring. **Stop the service
first, and restart it afterwards**: a running process keeps the old KEK in memory and cannot open rows another process
has re-wrapped; every write checks the database's key and refuses. With the generated `/data/master.key` the command
stores the new key itself and keeps `master.key.old`; with `PADURAKSA_MASTER_KEY_FILE` it can only read the new key from
a file you keep. In Docker:

```sh
docker compose down
docker compose run --rm -v "$PWD:/keys:ro" paduraksa vault rotate-kek --new-key-file /keys/new-master.key
# put the new key where PADURAKSA_MASTER_KEY_FILE points (or let the command replace master.key), then:
docker compose up -d
```

Read PLAN §8.3 before rotating: the order (new key stored first, then rotate, then retire the old one) is what makes a
crash safe.

### HTTPS upstreams

An `https://` destination is verified like a browser would: a chain to the system's trusted roots, issued for the name the
client asked for. Homelab gear with a self-signed certificate (a NAS, a router, Proxmox) fails that, and the error log shows
`The remote certificate is invalid ... RemoteCertificateNameMismatch, RemoteCertificateChainErrors` with a 502 to the client.
Set the upstream's **HTTPS certificate trust** (Advanced, in the upstream's form):

- **Pinned fingerprint**: accepts exactly that certificate. **Fetch from destinations** shows the certificate each
  `https://` destination presents (fingerprint, name, issuer, expiry) and **Pin** adds it; check it is the device you meant
  before you pin it. Or paste a SHA-256 fingerprint yourself (colons are fine), e.g. from
  `openssl s_client -connect nas.lan:8443 </dev/null | openssl x509 -noout -fingerprint -sha256`. When the device renews its
  certificate, fetch and pin the new one.
- **Any certificate**: no verification. Traffic is still encrypted, but anyone on the path can read and change it; the
  config review warns about it.

If the upstream speaks plain HTTP too, an `http://` destination needs none of this.

### Health

The image has its own `HEALTHCHECK` (`paduraksa doctor --liveness` → the admin `/healthz` over loopback, 30 s
interval). Check it with:

```sh
docker inspect --format '{{.State.Health.Status}}' paduraksa
```

For external monitors use the admin endpoints directly: `/healthz` (process alive, a snapshot loaded) and `/readyz`
(serving traffic; `200 degraded` means the last-known-good config is live because the config database is unavailable;
still ready, worth an alert). See PLAN §11.3.

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

## Bare metal

1. Publish a self-contained build (no .NET runtime needed on the host):

   ```sh
   dotnet publish src/Paduraksa.Server/Paduraksa.Server.csproj -c Release -r linux-x64 --self-contained true -o /opt/paduraksa
   ```

   **Install libmsquic** from Microsoft's Ubuntu 24.04 feed, or HTTP/3 is silently off (the startup log says
   `HTTP/3 is off: libmsquic isn't available...` and `Alt-Svc` is not advertised):

   ```sh
   curl -fsSL https://packages.microsoft.com/keys/microsoft.asc \
     | sudo gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg
   echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/ubuntu/24.04/prod noble main" \
     | sudo tee /etc/apt/sources.list.d/microsoft-prod.list >/dev/null
   sudo apt-get update && sudo apt-get install -y libmsquic=2.5.10
   ```

2. Create the service user and data directory:

   ```sh
   sudo useradd --system --user-group --home /var/lib/paduraksa --shell /usr/sbin/nologin paduraksa
   sudo install -d -o paduraksa -g paduraksa -m 0750 /var/lib/paduraksa
   sudo install -d -m 0750 /etc/paduraksa
   head -c 32 /dev/urandom | base64 | sudo tee /etc/paduraksa/master.key >/dev/null
   sudo chmod 600 /etc/paduraksa/master.key
   ```

3. Install [`deploy/paduraksa.service`](../deploy/paduraksa.service) as `/etc/systemd/system/paduraksa.service` and
   start it:

   ```sh
   sudo systemctl daemon-reload
   sudo systemctl enable --now paduraksa
   sudo journalctl -u paduraksa -f     # the setup-token banner is here
   ```

   The unit runs the app as `paduraksa`, gives it only `CAP_NET_BIND_SERVICE` (for 80/443), and passes the master key
   through systemd's `LoadCredential` (`PADURAKSA_MASTER_KEY_FILE=%d/master.key`), so the key is readable only by the
   service and is never in the data directory. `TimeoutStopSec=40s` matches the 30-second drain;
   `RestartPreventExitStatus=2 3` stops a restart loop when startup fails (exit 2) or the configuration is invalid (exit 3).

4. Finish setup exactly as in the Docker steps: read the setup token from the journal, trust the CA root from
   `https://<host>:9443/api/v1/ca/root.pem` (PEM; save it as `paduraksa.crt` for `update-ca-certificates`) or
   `/api/v1/ca/root.crt` (DER, for Android/iOS), open `https://<host>:9443/`.
