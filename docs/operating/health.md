# Health endpoints

The admin port serves two unauthenticated status endpoints for external monitors (Uptime Kuma, Gatus, Kubernetes probes).

| Endpoint | Answers `200` when |
|---|---|
| `GET /healthz` | The process is alive and a config snapshot is loaded. The body is `ok` |
| `GET /readyz` | The listeners are bound, a snapshot is live, the vault is unlocked and the process is not draining. The body is `ready` |

- If the config database is unavailable and the last-known-good config is live, `/readyz` answers `200` with the body `degraded`. The process is still ready, but the state deserves an alert.
- On shutdown, `/readyz` answers `503` first, then the public listeners drain, then the admin stops. The drain takes up to 30 seconds, so give the container `stop_grace_period: 35s`.
- Readiness means the process is serving traffic, not that the config database is reachable.

```sh
# Check readiness from another machine, trusting the local CA root
curl --cacert root.pem https://paduraksa.lan:9443/readyz
```

## The container's own check

The image has a `HEALTHCHECK` that runs `paduraksa doctor --liveness`. Every 30 seconds it requests the admin `/healthz` over loopback, with a 3-second timeout.

```sh
# Read the container's health status
docker inspect --format '{{.State.Health.Status}}' paduraksa
```

If you set `Paduraksa__Listen__AdminAllowedSources`, loopback is not implied. Add `127.0.0.1/32`, or the check fails and the container is marked unhealthy.

`paduraksa doctor` without `--liveness` checks the whole machine: data directory, master key, databases, ports, HTTP/3 and the clock. See [the CLI](../automation/cli.md#doctor). For certificates and DNS, use the dashboard (the Overview lists what needs attention) and the [troubleshooting](troubleshooting.md) page.

## Upstream health

Health checks of your own services are separate: see [Upstreams](../guide/upstreams.md#health-checks). Their alerts are [notifications](../guide/observability.md#notifications).
