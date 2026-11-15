# Upgrading

Before you upgrade, make a [backup](backups.md) and read the [changelog](../changelog.md). A backup restores onto the same or a newer Paduraksa: restoring one made by a newer version is refused, so upgrade first.

With the published image (the default compose file):

```sh
# Pull the new image and recreate the container
docker compose pull
docker compose up -d
```

With an image you build from a checkout:

```sh
# Update the source, then rebuild and recreate
git pull
cd deploy
docker compose up -d --build
```

After the upgrade, run the host check. It reports a downgraded database, a master key that no longer opens the vault, a port something else took, and a running server that is still the old version:

```sh
# Check the host
docker compose exec paduraksa paduraksa doctor
```

If you can, run it before the upgrade too. A `[info]` line about pending migrations is then the upgrade's own, and anything else was already wrong. See [the CLI](../automation/cli.md#doctor).

Migrations run at startup, before the listeners bind. Stop with `docker compose down`, not `kill`, so open connections get their 30-second drain.

The compose example sets a stable `hostname`. Without one, a recreated container gets a new name, and the admin certificate is re-issued with a new serial. That is harmless, and `Paduraksa__Admin__CertificateNames` gives you a stable name instead.
