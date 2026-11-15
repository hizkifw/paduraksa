# Backups and restore

Back up the **data directory and the master key separately**. A copy of `vault.db` without the key is useless, and a copy of the key without the volume opens nothing. Together they are every credential the proxy holds.

Do not copy the files of a running server: SQLite keeps part of the database in `-wal` files. Use `paduraksa backup`, below.

`paduraksa backup` writes **one file that holds everything**:

- the config database;
- the vault (certificate keys, ACME and DNS secrets, TOTP secrets);
- the last-known-good config.

The log files are not in it. It is encrypted under a passphrase you choose, and **nothing can recover a lost passphrase**. It is made while the server runs, and it restores on **any** machine with that machine's own master key. The old machine's key is not in the file and is not needed.

Make one:

```sh
# Write a dated, encrypted backup into the data volume; it asks for the passphrase twice
docker exec -it paduraksa paduraksa backup --out /data/backups/paduraksa-$(date +%F).pdkbak
```

- The passphrase needs at least 12 characters. A few unrelated words are better.
- It is never an argument. For a script, give it as a file (`--passphrase-file /run/secrets/backup_passphrase`, or `-` for standard input) or as `PADURAKSA_BACKUP_PASSPHRASE` (`docker exec -e PADURAKSA_BACKUP_PASSPHRASE=... paduraksa ...`).
- The command never overwrites a file.

The Owner can also download one from **Settings > Backup**, or with an API token that has the `backup:create` scope:

```sh
# Keep the secrets in 0600 files: a command-line argument shows in ps and in shell history
umask 077
printf 'Authorization: Bearer %s\n' "$TOKEN" > auth.hdr
printf '{"passphrase":"%s"}' "$PASSPHRASE" > body.json

# Download the backup
curl -sS --cacert root.pem -H @auth.hdr -H 'Content-Type: application/json' --data @body.json \
  https://paduraksa.lan:9443/api/v1/backup -o paduraksa.pdkbak

# Remove the secrets
shred -u auth.hdr body.json
```

A routine that works for a homelab:

1. **Run it on a schedule.** A nightly cron job or systemd timer on the host will do. Name each file with the date.
2. **Copy the files off the machine.** `/data/backups` is on the same disk as the data it protects. Use `docker cp`, `rsync` or your backup tool. The files are encrypted, so where they go is not a secret.
3. **Keep the passphrase off this machine**, in a password manager for example.
4. **Prune** old files by date, and **try a restore** on a spare machine once, so the first time is not the day you need it.

## Restore

Stop the service first. A restore replaces the databases, and `paduraksa restore` refuses while a server runs on the directory.

```sh
# Stop the server
docker compose stop

# Restore from a backup file
docker compose run --rm paduraksa restore /data/backups/paduraksa-2026-11-12.pdkbak

# Start again
docker compose up -d
```

On a new machine, set up the compose file first and do not start it. Then run the same `restore`. Give the passphrase as `-e PADURAKSA_BACKUP_PASSPHRASE=...` or a mounted file, or let it prompt.

The restore:

- checks the whole file before it touches anything: the passphrase, the contents, and that the backup is not from a newer Paduraksa than this one (upgrade first if it is);
- moves the databases it replaces to `/data/pre-restore-<time>/`. Delete them once you have checked the result;
- re-wraps the vault under **this machine's** master key;
- generates `/data/master.key` if the machine has no key at all, as a first start does. Keep a copy of it apart from the data;
- puts everything back and says so, if it fails after it began.

Then sign in as you did before. The users, their sessions and API tokens are the backup's.

### If the process dies in the middle of a restore

A kill or a power cut stops the rollback that would undo a failed restore. The restore first moves the current `paduraksa.db`, `vault.db`, their `-wal` and `-shm` files and `last-good.json` to `/data/pre-restore-<time>/`. Then it puts the restored `vault.db`, `paduraksa.db` and `last-good.json` in `/data`.

After a crash:

- `pre-restore-<time>/` holds your previous files.
- `/data` holds none, some or all of the restored ones.
- `/data/run/restore-<random>/` holds the unpacked copy. Delete it: it is not encrypted.
- `master.key` is never moved.

With the service stopped, either:

- **finish it:** run the same `restore` again. It moves whatever is in `/data` aside into a new `pre-restore-<time>/` and installs the restored files. Your original files stay in the first `pre-restore-<time>/`; or
- **go back:** delete `paduraksa.db`, `vault.db`, their `-wal` and `-shm` files and `last-good.json` from `/data`. Move every file in the older `pre-restore-<time>/` back into `/data` under its own name, then start the service.

If `/data` holds a `paduraksa.db` that is not the one you expect, check `pre-restore-<time>/` first. Nothing a restore moves is ever deleted.
