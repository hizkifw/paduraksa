# Health endpoints

The admin port serves two unauthenticated endpoints for external monitors (Uptime Kuma, Gatus, Kubernetes probes). They say nothing beyond their status.

| Endpoint | Answers `200` when |
|---|---|
| `GET /healthz` | The process is alive and a config snapshot is loaded. The body is `ok` |
| `GET /readyz` | The listeners are bound, a snapshot is live, the vault is unlocked and the process is not draining. The body is `ready` |

- `/readyz` answers `200` with the body `degraded` when the last-known-good config is live because the config database is unavailable. It is still ready, and worth an alert.
- While the process shuts down, `/readyz` answers `503` first, then the public listeners drain, then the admin stops. Give it `stop_grace_period: 35s`: the drain takes up to 30 seconds.
- Readiness means "serving traffic", not "the config database is reachable".

```sh
curl --cacert root.pem https://paduraksa.lan:9443/readyz
```

## The container's own check

The image has a `HEALTHCHECK` that runs `paduraksa doctor --liveness`. It asks the admin `/healthz` over loopback with a 3-second budget, every 30 seconds, and exits 0 or 1. Check it with:

```sh
docker inspect --format '{{.State.Health.Status}}' paduraksa
```

If you set `Paduraksa__Listen__AdminAllowedSources`, loopback is not implied: add `127.0.0.1/32`, or the check fails and the container is marked unhealthy.

`paduraksa doctor` without `--liveness` checks the whole host: data directory, master key, databases, ports, HTTP/3 and the clock. See [the CLI](../automation/cli.md#doctor). For certificates and DNS use the dashboard (the Overview lists what needs you) and the [troubleshooting](troubleshooting.md) page.

## Upstream health

Health checks of your own services are separate: see [Upstreams](../guide/upstreams.md#health-checks). Alerts for them are [notifications](../guide/observability.md#notifications).
