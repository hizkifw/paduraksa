# Upgrading

Before you upgrade, make a [backup](backups.md) and read the [changelog](../changelog.md). A backup restores onto the same or a newer Paduraksa: restoring one made by a newer version is refused, so upgrade first.

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
