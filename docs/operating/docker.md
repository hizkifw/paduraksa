# Running with Docker

[Getting started](../guide/getting-started.md) takes you through the first start. This page is the reference for the compose deployment.

Requirements: Docker 20.10+ with Compose v2. Docker sets `net.ipv4.ip_unprivileged_port_start=0` in the container's
network namespace, so the image's non-root user can bind 80/443; the apphost also carries `cap_net_bind_service` for
runtimes that do not.

- **Ports:** 80/tcp (HTTP, ACME HTTP-01, redirects), 443/tcp (HTTP/1.1 and HTTP/2) and **443/udp** (HTTP/3), and
  9443/tcp (the admin app).
- **Data:** one directory, mounted at `/data` in the container. It holds `paduraksa.db` (config, users, audit),
  `vault.db` (certificate keys, secrets, the Data Protection key ring), `last-good.json`, the admin socket under `run/`,
  the logs under `logs/` and, unless you supply the master key another way, `master.key`.
- **Master key:** the one key that opens `vault.db`. See [The master key](master-key.md).

## The compose file

[`deploy/compose.yaml`](https://github.com/hizkifw/paduraksa/blob/main/deploy/compose.yaml) runs `ghcr.io/hizkifw/paduraksa:main`, which CI publishes from every commit on `main` that passes, for amd64 and arm64.
Once there is a release, a tag such as `:0.1` will name it. `master.key` is the vault's master key, mounted as a Docker secret so that it is not in the data volume; compose refuses to start without the file.

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

Do not remap the container's ports: `Alt-Svc` and the HTTP→HTTPS redirect advertise 443, because that is the port
clients use. Behind a port translation, set `Paduraksa__Listen__PublicHttpsPort` to the outside port. Map 443/**udp** too: it is HTTP/3, and
without it browsers fall back to HTTP/2 over 443/tcp (see [HTTP/3](../guide/tls.md#http3)).

The compose file sets a stable `hostname`, so the admin certificate's machine name does not change when the container is recreated.
It also sets `stop_grace_period: 35s`, which gives Kestrel its 30-second drain, and a `json-file` log driver that keeps 5 files of 10 MB, so the setup-token banner stays readable in `docker logs`.

## Where things live

The named volume `paduraksa-data` holds everything:

| Path | What |
|---|---|
| `/data/paduraksa.db` | config, revisions, users, sessions, audit chain (SQLite, WAL) |
| `/data/vault.db` | sealed certificate keys, ACME/DNS secrets, TOTP secrets, the Data Protection key ring |
| `/data/master.key` | the KEK, **only** when you did not set `PADURAKSA_MASTER_KEY_FILE` or `PADURAKSA_MASTER_KEY` |
| `/data/last-good.json` | the last live config, used for a degraded start |
| `/data/setup-token` | until the first Owner exists |
| `/data/run/admin.sock` | the internal listener the public proxy uses for an admin host |
| `/data/run/server.lock` | held by the running server, so a second server (or a restore) finds the directory taken |
| `/data/logs/` | `access.log` and `error.log` (0700 directory, 0600 files), with rotated, gzip-compressed older files |

Back up the data directory and the master key separately: see [Backups and restore](backups.md).

## Settings

Settings are environment variables in the compose file (`Paduraksa__Listen__PublicHttpsPort=...`). Every one of them, and how a list is written, is in [Server settings](settings.md).
