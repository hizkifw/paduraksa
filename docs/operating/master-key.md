# The master key

The master key opens `vault.db`, which holds every certificate key, DNS and notification secret, and TOTP seed. It is one line of text: 32 random bytes in base64.

**Losing it loses the vault. Keep a copy somewhere other than the data volume.** A [backup](backups.md) does not contain the key and does not need it to restore.

Paduraksa reads the key from, in this order:

1. **`PADURAKSA_MASTER_KEY_FILE`**, the path of a file. The shipped `deploy/compose.yaml` sets it to a Docker secret, and the systemd unit to a `LoadCredential` file. A copy of the data directory is then not a copy of your keys. Use this for anything beyond a first try.
2. `PADURAKSA_MASTER_KEY`, the key itself in the environment. `docker inspect` shows it, so prefer a file.
3. `/data/master.key`, generated on the first start when neither is set. It sits beside `vault.db`, so **a copy of `/data` is a copy of every secret**. The dashboard warns about it on the Overview until you move the key.

The container runs as uid 10001, so a key file must belong to that uid (`chown 10001:10001`, `chmod 400`). A root-only `0600` file makes the container restart in a loop, and the startup error says which uid has to read it.

Admin sessions do **not** depend on the master key.

### Moving the key out of the data directory

Do this on an install that generated its own key, which is what the Overview's warning is about. The key stays the same, so nothing is re-encrypted.

```sh
# Stop the server
docker compose stop

# Copy the key to the host and lock it down
docker compose cp paduraksa:/data/master.key ./master.key
sudo chown 10001:10001 master.key && sudo chmod 400 master.key

# Add the secrets: and environment: blocks from deploy/compose.yaml (they point at ./master.key), then remove the old copy
docker compose run --rm --no-deps --entrypoint rm paduraksa /data/master.key

# Start again
docker compose up -d
```

The warning is gone after the restart. Back `master.key` up apart from the volume.

If the data directory, or a backup of it, was copied while the key was in it, that copy opens everything. Follow the move with `paduraksa vault rotate-kek` ([Changing the master key](#changing-the-master-key)).

## Changing the master key

`paduraksa vault rotate-kek` re-encrypts every secret in the vault under a new key. **Stop the service first, and restart it afterwards.** A running process keeps the old key and cannot read what another process has re-encrypted.

- With the generated `/data/master.key`, the command stores the new key itself and keeps `master.key.old`.
- With `PADURAKSA_MASTER_KEY_FILE`, it reads the new key from a file you keep.

In Docker:

```sh
# Stop and remove the container
docker compose down

# Re-encrypt the vault under the new key
docker compose run --rm -v "$PWD:/keys:ro" paduraksa vault rotate-kek --new-key-file /keys/new-master.key

# Put the new key where PADURAKSA_MASTER_KEY_FILE points (or let the command replace master.key), then start again
docker compose up -d
```

The order matters for surviving a crash: store the new key first, then rotate, then retire the old key. [At-rest encryption](../design/vault-and-certificates.md#at-rest-encryption) explains why.
