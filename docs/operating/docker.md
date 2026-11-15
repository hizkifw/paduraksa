# Running with Docker

[Getting started](../guide/getting-started.md) covers the first start. This page is the reference for the compose deployment.

Requirements: Docker 20.10 or later with Compose v2.

- **Ports:** 80/tcp (HTTP, ACME HTTP-01, redirects), 443/tcp (HTTP/1.1 and HTTP/2), **443/udp** (HTTP/3) and 9443/tcp (the admin app).
- **Data:** one directory, mounted at `/data` in the container. [Where things live](#where-things-live) lists what is in it.
- **Master key:** the one key that opens `vault.db`. See [The master key](master-key.md).

## The compose file

[`deploy/compose.yaml`](https://github.com/hizkifw/paduraksa/blob/main/deploy/compose.yaml) runs `ghcr.io/hizkifw/paduraksa:main`, which CI publishes from every commit on `main` that passes, for amd64 and arm64. Once there is a release, a tag such as `:0.1` will name it.

`master.key` is the vault's master key. It is mounted as a Docker secret, so it is not in the data volume, and Compose refuses to start without the file.

**To build the image yourself**, either:

- run compose from a checkout's `deploy/` directory with the commented `build:` block enabled, and start it with `docker compose up -d --build`; or
- build once with `docker build -t paduraksa:local .` in the repository root, and set `image: paduraksa:local`.

**A bind-mounted data directory** (instead of the named volume) must be owned by the container's uid 10001:

```sh
# Create the directory, owned by the container's user
sudo install -d -o 10001 -g 10001 -m 0750 ./data

# Then, in the compose file, mount it
# - ./data:/data
```

**Hardening.** The compose file runs the container with a read-only root filesystem (and a tmpfs `/tmp`), `no-new-privileges`, and `cap_drop: [ALL]` with `cap_add: [NET_BIND_SERVICE]` added back. Keep that capability if you change the file, or the container fails to start. In Kubernetes "restricted", add `securityContext.capabilities.add: ["NET_BIND_SERVICE"]`, or set the pod sysctl `net.ipv4.ip_unprivileged_port_start=0`.

**Ports.** Do not remap the container's ports: `Alt-Svc` and the HTTP to HTTPS redirect advertise 443, the port clients use. Behind a port translation, set `Paduraksa__Listen__PublicHttpsPort` to the outside port. Map 443/**udp** too: without it, browsers fall back to HTTP/2 over 443/tcp (see [HTTP/3](../guide/tls.md#http3)).

**Other compose settings:**

- a stable `hostname`, so the admin certificate's machine name does not change when the container is recreated;
- `stop_grace_period: 35s`, which allows the 30-second drain of open connections;
- a `json-file` log driver that keeps 5 files of 10 MB, so the setup-token banner stays readable in `docker logs`.

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
| `/data/run/server.lock` | held by the running server, so a second server or a restore finds the directory taken |
| `/data/logs/` | `access.log` and `error.log` (0700 directory, 0600 files), with rotated, gzip-compressed older files |

Back up the data directory and the master key separately: see [Backups and restore](backups.md).

## Settings

Settings are environment variables in the compose file (`Paduraksa__Listen__PublicHttpsPort=...`). [Server settings](settings.md) lists every one and shows how to write a list.
