# Installing Paduraksa

Paduraksa v0.1 is a single-node deployment: one process, one data directory, three listeners. This page is the practical
install guide; the design is in [PLAN.md](PLAN.md) §3, §8 and §16.

- **Ports:** 80/tcp (HTTP, ACME HTTP-01, redirects), 443/tcp (HTTP/1.1 and HTTP/2) and **443/udp** (HTTP/3), and
  9443/tcp (the admin app).
- **Data:** one directory, mounted at `/data` in the container. It holds `paduraksa.db` (config, users, audit),
  `vault.db` (certificate keys, secrets, the Data Protection key ring), `last-good.json`, the admin socket under `run/`
  and — unless you supply the master key another way — `master.key`.
- **Master key:** the one key that opens `vault.db`. Keep a copy somewhere other than the data volume; losing it loses
  every certificate key, DNS credential, TOTP secret and admin session key. See PLAN §8.3.

## Docker (recommended)

Requirements: Docker 20.10+ with Compose v2. Docker sets `net.ipv4.ip_unprivileged_port_start=0` in the container's
network namespace, so the image's non-root user can bind 80/443; the apphost also carries `cap_net_bind_service` for
runtimes that do not.

1. Copy [`deploy/compose.yaml`](../deploy/compose.yaml) somewhere on the host and start it:

   ```sh
   mkdir -p /srv/paduraksa && cd /srv/paduraksa
   cp /path/to/repo/deploy/compose.yaml .
   docker compose up -d
   ```

   To build the image from a checkout instead of using the published one, uncomment the `build:` block in the compose
   file (it expects the repository root one directory up) and run `docker compose up -d --build`.

2. **Read the setup token.** On the first start, Paduraksa prints a boxed banner at warning level with the one-time
   token that creates the first Owner. It is also written to `/data/setup-token`.

   ```sh
   docker logs paduraksa 2>&1 | grep -A8 "Paduraksa first-run setup"
   # or, at any time before setup completes:
   docker compose exec paduraksa paduraksa setup-token
   ```

   The token is single-use and is deleted once an Owner exists.

3. **Trust the local CA root**, so the admin UI is not a certificate warning in your browser. Paduraksa issues its own
   admin certificate from a local CA; the root is offered unauthenticated, before setup, precisely so you can trust it
   first:

   ```sh
   curl -k -o paduraksa-root.crt https://<host>:9443/api/v1/ca/root.crt   # DER; Android/iOS offer to install it
   curl -k -o paduraksa-root.pem https://<host>:9443/api/v1/ca/root.pem   # PEM; Linux/macOS trust stores
   curl -k https://<host>:9443/api/v1/ca                                 # fingerprint, subject and validity as JSON
   ```

   `-k` is needed only because the certificate is not trusted yet. On Linux:
   `sudo cp paduraksa-root.crt /usr/local/share/ca-certificates/paduraksa.crt && sudo update-ca-certificates`; on
   macOS, add it to the System keychain and mark it trusted; on Windows, import it into *Trusted Root Certification
   Authorities*. Then open **`https://<host>:9443/`** and enter the setup token.

   The admin port is HTTPS only. If you set `Paduraksa__Listen__AdminAllowedSources` (a comma-separated list of
   addresses/CIDRs), loopback is **not** implied — add `127.0.0.1/32` if you also want the container healthcheck
   (`paduraksa doctor --liveness`) and an SSH tunnel to work.

4. **Point a host at it.** Add a host and a route in the dashboard or the REST API (or later YAML import); issue a
   certificate for it (ACME with HTTP-01 needs port 80 reachable from the internet, DNS-01 does not). The public
   listeners answer as soon as a host matches; the admin certificate never serves proxied traffic.

### UDP and HTTP/3

443/**udp** is HTTP/3. Map it 1:1 like the TCP ports; if it is missing or firewalled, browsers fall back to HTTP/2 over
443/tcp and nothing breaks (Paduraksa only advertises `Alt-Svc` for H3 when libmsquic loaded). The image installs
libmsquic and logs `HTTP/3 is on: QUIC on UDP 443 …` at startup; if it says HTTP/3 is off, the package did not load.

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

Back up the **data directory and the master key separately**. A copy of `vault.db` without the key is useless, and a copy
of the key without the volume opens nothing; together they are every credential the proxy holds. `paduraksa backup`
arrives in M8. To move or restore, stop the container and copy the whole volume.

### Upgrading

```sh
docker compose pull
docker compose up -d
```

Migrations are applied at startup, before the listeners bind. Stop with `docker compose down` (not `kill`): the
`stop_grace_period: 35s` gives Kestrel its 30-second drain. A new container gets a new `Environment.MachineName`, which
is part of the admin certificate's names, so the admin certificate is re-issued under the same ID after a recreate —
harmless, and why you may see a new serial. Set `Paduraksa__Admin__CertificateNames` for a stable name.

### Health

The image has its own `HEALTHCHECK` (`paduraksa doctor --liveness` → the admin `/healthz` over loopback, 30 s
interval). Check it with:

```sh
docker inspect --format '{{.State.Health.Status}}' paduraksa
```

For external monitors use the admin endpoints directly: `/healthz` (process alive, a snapshot loaded) and `/readyz`
(serving traffic; `200 degraded` means the last-known-good config is live because the config database is unavailable —
still ready, worth an alert). See PLAN §11.3.

## Bare metal

1. Publish a self-contained build (no .NET runtime needed on the host):

   ```sh
   dotnet publish src/Paduraksa.Server/Paduraksa.Server.csproj -c Release -r linux-x64 --self-contained true -o /opt/paduraksa
   ```

2. Create the service user and data directory:

   ```sh
   sudo useradd --system --home /var/lib/paduraksa --shell /usr/sbin/nologin paduraksa
   sudo install -d -o paduraksa -g paduraksa /var/lib/paduraksa
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
   service and is never in the data directory. `TimeoutStopSec=40s` matches the 30-second drain.

4. Finish setup exactly as in the Docker steps: read the setup token from the journal, trust the CA root from
   `https://<host>:9443/api/v1/ca/root.crt`, open `https://<host>:9443/`.
