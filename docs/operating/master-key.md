# The master key

The master key opens `vault.db`, which holds every certificate key, DNS and notification secret, TOTP seed and the Data Protection
key ring. It is one line of text (32 random bytes in base64) and is read from, in this order:

1. **`PADURAKSA_MASTER_KEY_FILE`**, the path of a file. The shipped `deploy/compose.yaml` sets it to a Docker secret, and the
   systemd unit to a `LoadCredential` file. The key is then not in the data directory, and a copy of the directory or the volume
   is not a copy of your keys. This is the setup to use for anything beyond a first try.
2. `PADURAKSA_MASTER_KEY`, the key itself in the environment. Readable with `docker inspect` and from `/proc`, so prefer a file.
3. `/data/master.key`, generated on the first start when neither is set. It is beside `vault.db`, so **a copy of `/data` is a
   copy of every secret**, and the dashboard says so on the overview until the key is moved.

Non-swarm Compose mounts a file secret as a bind mount that keeps the host's owner and mode, and the container runs as uid 10001,
so the file must belong to that uid (`chown 10001:10001`, `chmod 400`). A root-only `0600` file makes the container restart in a
loop, and the startup error says which uid has to read it.

**Moving the key out of the data directory** of an install that generated its own (the overview's warning): stop it, copy the key
to the host, and point the file variable at it. The key stays the same, so nothing is re-encrypted:

```sh
docker compose stop
docker compose cp paduraksa:/data/master.key ./master.key
sudo chown 10001:10001 master.key && sudo chmod 400 master.key
# add the secrets: and environment: blocks from deploy/compose.yaml (they point at ./master.key), then remove the old copy:
docker compose run --rm --no-deps --entrypoint rm paduraksa /data/master.key
docker compose up -d
```

The overview's warning is gone after the restart. Back `master.key` up apart from the volume. If the data directory (or a backup of it) has
been copied while the key was in it, that copy opens everything: follow the move with `paduraksa vault rotate-kek`
([Changing the master key](#changing-the-master-key)).

Admin sessions do **not** depend on the master key: the admin session cookie is Paduraksa's own random value, stored hashed in `paduraksa.db`.
Keep a copy of the key somewhere other than the data volume. A [backup](backups.md) does not contain it and does not need it to restore.

## Changing the master key

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

Read [vault-and-certificates.md, At-rest encryption](../design/vault-and-certificates.md#at-rest-encryption) before rotating: the order (new key stored first, then rotate, then retire the old one) is what makes a
crash safe.
