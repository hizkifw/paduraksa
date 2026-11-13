# Backups and restore

Back up the **data directory and the master key separately**. A copy of `vault.db` without the key is useless, and a copy
of the key without the volume opens nothing; together they are every credential the proxy holds. Do not copy the files of
a running server (SQLite keeps part of the database in `-wal` files): use `paduraksa backup`, below, which also does not
need the master key to restore.

`paduraksa backup` writes **one file that holds everything**: the config database, the vault (certificate keys, ACME and
DNS secrets, TOTP secrets, the Data Protection ring) and the last-known-good config, encrypted under a passphrase you
choose (Argon2id and AES-256-GCM). It is made while the server runs and is consistent, it restores on **any** machine
with that machine's own master key (the old machine's key is not in the file and is not needed), and it is useless
without the passphrase: **nothing can recover a lost passphrase**. The log files are not in it.

Make one:

```sh
docker exec -it paduraksa paduraksa backup --out /data/backups/paduraksa-$(date +%F).pdkbak
```

It asks for the passphrase twice (at least 12 characters; a few unrelated words are better). The passphrase is never an
argument: for a script give it as a file (`--passphrase-file /run/secrets/backup_passphrase`, or `-` for standard input)
or as `PADURAKSA_BACKUP_PASSPHRASE` (`docker exec -e PADURAKSA_BACKUP_PASSPHRASE=... paduraksa ...`). The command never
overwrites a file. The Owner can also download one from **Settings > Backup**, or with an API token that has the
`backup:create` scope:

```sh
umask 077
printf 'Authorization: Bearer %s\n' "$TOKEN" > auth.hdr
printf '{"passphrase":"%s"}' "$PASSPHRASE" > body.json
curl -sS --cacert root.pem -H @auth.hdr -H 'Content-Type: application/json' --data @body.json \
  https://paduraksa.lan:9443/api/v1/backup -o paduraksa.pdkbak
shred -u auth.hdr body.json
```

The passphrase and the token go through 0600 files, not the command line: an argument shows in `ps` and in the shell's
history (`printf` is a shell built-in, so its arguments are not in `ps`). Better still, have your secret manager write the two files.

A routine that works for a homelab:

1. **Run it on a schedule** (a nightly cron job or systemd timer on the host; a backup takes a second or two and 64 MiB
   of memory). Name each file with the date.
2. **Copy the files off the machine.** `/data/backups` is inside the volume, on the same disk as the data it protects.
   `docker cp`, `rsync` or your backup tool of choice will do; the files are encrypted, so where they go is not a secret.
3. **Keep the passphrase somewhere that is not on this machine**, such as a password manager.
4. **Prune** old files by date as you see fit, and **try a restore** on a spare machine once, so the first time is not
   the day you need it.

Restore with the service **stopped**, because a restore replaces the databases (`paduraksa restore` refuses while a server
runs on the directory):

```sh
docker compose stop
docker compose run --rm paduraksa restore /data/backups/paduraksa-2026-11-12.pdkbak
docker compose up -d
```

On a new machine, set up the compose file first and do not start it, then run the same `restore` (give the passphrase
as `-e PADURAKSA_BACKUP_PASSPHRASE=...` or a mounted file, or let it prompt: `compose run` has a terminal). It checks the
whole file before it touches anything (the passphrase, every block, the contents, and that the backup is not from a
newer Paduraksa than this one: upgrade first if it is), moves the databases it replaces to `/data/pre-restore-<time>/`
(delete them once you have checked the result), and re-wraps the vault under **this machine's** master key. If the
machine has none (no `PADURAKSA_MASTER_KEY`, no key file, no `master.key`) it generates `/data/master.key`, as a first
start does: keep a copy of it apart from the data. If the restore fails after it began it puts everything back and says
so. After it, sign in as you did before: the users, their sessions and API tokens are the backup's.

**If the process dies in the middle of a restore** (killed, or the power goes), the rollback that undoes a failed restore cannot
run. The order of a restore is: the current `paduraksa.db`, `vault.db`, their `-wal` and `-shm` files and `last-good.json` move to
`/data/pre-restore-<time>/`; then the restored `vault.db`, `paduraksa.db` and `last-good.json` are put in `/data`. So after a crash
`pre-restore-<time>/` holds your previous files, and `/data` holds none, some or all of the restored ones; `/data/run/restore-<random>/`
holds the unpacked copy (delete it: it is plain). `master.key` is never moved (a restore onto a machine that had none may have
generated one, which a repeat reuses). With the service stopped, either:

- **finish it:** run the same `restore` again. It verifies the archive again, moves whatever is in `/data` aside into a new
  `pre-restore-<time>/` and installs the restored files; your original files stay in the first `pre-restore-<time>/`; or
- **go back:** delete `paduraksa.db`, `vault.db`, their `-wal` and `-shm` files and `last-good.json` from `/data`, then move
  every file in the older `pre-restore-<time>/` back into `/data` under its own name, and start the service.

If `/data` holds a `paduraksa.db` that is not the one you expect, check the `pre-restore-<time>/` first: nothing a restore moves is ever deleted.
