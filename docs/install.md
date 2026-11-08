# Installing Paduraksa

Paduraksa v0.1 is a single-node deployment: one process, one data directory, three listeners. This page is the practical
install guide; the design is in [PLAN.md](PLAN.md) §3, §8 and §16.

- **Ports:** 80/tcp (HTTP, ACME HTTP-01, redirects), 443/tcp (HTTP/1.1 and HTTP/2) and **443/udp** (HTTP/3), and
  9443/tcp (the admin app).
- **Data:** one directory, mounted at `/data` in the container. It holds `paduraksa.db` (config, users, audit),
  `vault.db` (certificate keys, secrets, the Data Protection key ring), `last-good.json`, the admin socket under `run/`
  and, unless you supply the master key another way, `master.key`.
- **Master key:** the one key that opens `vault.db`. Keep a copy somewhere other than the data volume; losing it loses
  every certificate key, DNS credential, TOTP secret and the Data Protection key ring. Admin sessions do **not** depend on
  it: the admin session cookie is Paduraksa's own random value, stored hashed in `paduraksa.db`. See PLAN §8.3.
- **List settings** (`AdminAllowedSources`, `CertificateNames`, the PROXY protocol `TrustedSources`, ACME
  `DnsPropagation:Resolvers`) take **either** one comma-separated value or indexed keys; both bind, both are documented
  below.

## Docker (recommended)

Requirements: Docker 20.10+ with Compose v2. Docker sets `net.ipv4.ip_unprivileged_port_start=0` in the container's
network namespace, so the image's non-root user can bind 80/443; the apphost also carries `cap_net_bind_service` for
runtimes that do not.

1. From a checkout, build the image and start it (the compose file's `build.context` is the repository root, one
   directory up):

   ```sh
   cd deploy
   docker compose up -d --build
   ```

   If you prefer to keep the compose file elsewhere, set `build.context` to your checkout (and `dockerfile` to its
   `Dockerfile`), or build the image once with `docker build -t paduraksa:local .` in the repository root and replace
   the `build:` block with `image: paduraksa:local`. A published release image (`ghcr.io/...`, M11) is a drop-in
   replacement for either.

   **A bind-mounted data directory** (instead of the named volume) must be owned by the container's uid 10001:

   ```sh
   sudo install -d -o 10001 -g 10001 -m 0750 ./data
   # compose: - ./data:/data
   ```

   **Ports and capabilities.** The default bridge network works because Docker sets the unprivileged-port floor to 0. If
   you harden the container with `cap_drop: [ALL]`, add `cap_add: [NET_BIND_SERVICE]` back, or exec of the setcap'd
   apphost fails with EPERM. In Kubernetes "restricted", add
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

   The admin port is HTTPS only. If you set `Paduraksa__Listen__AdminAllowedSources` (addresses/CIDRs), loopback is
   **not** implied - add `127.0.0.1/32` if you also want the container healthcheck (`paduraksa doctor --liveness`) and
   an SSH tunnel to work.

   Every list setting takes **either** one comma-separated value or indexed keys, so both shell conventions work:

   ```sh
   Paduraksa__Listen__AdminAllowedSources=192.168.1.0/24,10.0.0.0/8
   Paduraksa__Listen__AdminAllowedSources__0=192.168.1.0/24
   Paduraksa__Listen__AdminAllowedSources__1=10.0.0.0/8

   Paduraksa__Admin__CertificateNames=paduraksa.lan,admin.example.com
   Paduraksa__Admin__CertificateNames__0=paduraksa.lan

   Paduraksa__Listen__HttpsProxyProtocol__TrustedSources=192.0.2.10
   Paduraksa__Acme__DnsPropagation__Resolvers=1.1.1.1,9.9.9.9:53
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

Back up the **data directory and the master key separately**. A copy of `vault.db` without the key is useless, and a copy
of the key without the volume opens nothing; together they are every credential the proxy holds. `paduraksa backup`
arrives in M10. To move or restore, stop the container and copy the whole volume.

### Upgrading

For a local build (the default compose file):

```sh
git pull
cd deploy
docker compose up -d --build
```

For a **published image** (replace the `build:` block with `image: ghcr.io/...` first):

```sh
docker compose pull
docker compose up -d
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
docker run --rm -it -v paduraksa-data:/data -v "$PWD:/keys:ro" paduraksa:local \
  vault rotate-kek --new-key-file /keys/new-master.key
# put the new key where PADURAKSA_MASTER_KEY_FILE points (or let the command replace master.key), then:
docker compose up -d
```

Read PLAN §8.3 before rotating: the order (new key stored first, then rotate, then retire the old one) is what makes a
crash safe.

### Health

The image has its own `HEALTHCHECK` (`paduraksa doctor --liveness` → the admin `/healthz` over loopback, 30 s
interval). Check it with:

```sh
docker inspect --format '{{.State.Health.Status}}' paduraksa
```

For external monitors use the admin endpoints directly: `/healthz` (process alive, a snapshot loaded) and `/readyz`
(serving traffic; `200 degraded` means the last-known-good config is live because the config database is unavailable;
still ready, worth an alert). See PLAN §11.3.

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
