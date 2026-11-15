# The command line

The `paduraksa` binary is the server and its command line. In the container it is `paduraksa`; from the host, run it with `docker compose exec paduraksa paduraksa ...` (or `docker compose run --rm paduraksa ...` when the service is stopped). `paduraksa --help` lists the commands.

| Command | What it does | Needs |
|---|---|---|
| `paduraksa` or `paduraksa serve` | Runs the proxy and the admin app. This is the default | |
| `paduraksa setup-token` | Prints the one-time setup token, or says setup is complete | The data directory |
| `paduraksa admin reset-password <user>` | Sets a user's password, ends their sessions and clears a lockout | The data directory |
| `paduraksa admin disable-2fa <user>` | Turns a user's second factor off and ends their sessions | The data directory |
| `paduraksa backup --out <file>` | Writes a passphrase-encrypted backup | The data directory. The server may run |
| `paduraksa restore <file>` | Replaces the config and the vault with a backup | The server **stopped** |
| `paduraksa vault rotate-kek --new-key-file <file>` | Re-wraps every vault key under a new master key | The server **stopped** |
| `paduraksa config export` | Writes the config as YAML | The running server, an API token |
| `paduraksa config import <file>` | Imports a YAML config as one revision | The running server, an API token |
| `paduraksa doctor` | Checks the host: data directory, master key, databases, ports, HTTP/3, clock. Changes nothing | The data directory. The server may run |
| `paduraksa doctor --liveness` | Exits 0 if the admin `/healthz` answers over loopback, 1 if not | The running server |

`paduraksa mcp-sandbox` is the process the [MCP](mcp.md) server starts for an assistant's code. Do not run it yourself.

Exit codes:

- `0`: success.
- `1`: a failure, or a mistake in the command line (an unknown command, a missing option), with a usage message.
- `2`: the server could not start.
- `3`: invalid settings. `doctor` is the exception: it reports them as a failed check and exits 1, so the rest of its report still prints.

`paduraksa <command> --help` describes every option.

## Recovery commands

These work on the data directory, so they need the same environment as the server (`Paduraksa__DataDirectory`, `PADURAKSA_MASTER_KEY_FILE`). In the container that is already true.

```sh
# Print the setup token again
docker compose exec paduraksa paduraksa setup-token

# Set a new password for a user, and end their sessions
docker compose exec paduraksa paduraksa admin reset-password alice

# Turn a user's second factor off
docker compose exec paduraksa paduraksa admin disable-2fa alice
```

`reset-password` reads the new password from standard input or a no-echo prompt, never from an argument. Both commands are audited with the actor `cli`.

`reset-password` checks the new password against the shortest length the server uses. That is the environment's value if set, else the one stored in Settings, else the default.

## Doctor

`paduraksa doctor` is a read-only check of the host, safe to run while the server runs. Run it after an upgrade, after changing a port or the master key, and before asking for help:

```sh
# Check the host
docker compose exec paduraksa paduraksa doctor
```

It prints one line per finding, tagged `[ ok ]`, `[info]`, `[warn]` or `[FAIL]`, and under a warning or a failure what to do. It exits 0 when nothing failed (warnings pass) and 1 when a check failed.

```text
[ ok ] options: The configuration is valid.
[ ok ] master-key: The master key comes from /run/secrets/master_key (a Docker secret) and opens vault.db.
[info] config-database: paduraksa.db needs 1 migration (20261114000000_Example): the next start applies it, as after any upgrade.
[FAIL] listener-http: public HTTP (tcp 80) is held by another process: the server cannot bind it.
       To fix: Find the holder (ss -ltnp 'sport = :80') and stop it, or move Paduraksa to another port (Paduraksa__Listen__Http). ...

Not healthy: 11 ok, 1 info, 0 warning(s), 1 failure(s).
```

| Option | What |
|---|---|
| `--json` | Print `{ "version", "passed", "summary": { "ok", "info", "warning", "failure" }, "checks": [ { "id", "status", "message", "fix" } ] }`. `status` is `ok`, `info`, `warning` or `failure`, `fix` is present on the last two, and `passed` is false exactly when the exit code is 1 |
| `--check-clock` | Also check the clock against a public server. Off by default: `doctor` makes no outward request unless asked |
| `--token-file <file>` | An API token (any scope; or `PADURAKSA_TOKEN`) so the running server's version can be compared with this binary's |
| `--liveness` | Only ask the admin `/healthz` over loopback: exit 0 when it answers, 1 if not. The image's `HEALTHCHECK` |

The checks, with their `id` in the report:

| Check | Looks at | Warning | Failure |
|---|---|---|---|
| `options` | The effective settings, validated as the server does at startup | | An invalid setting (the server would exit 3). The rest of the checks that need the settings are then skipped |
| `setting-locked` | Server settings that the environment or appsettings sets, which the dashboard therefore cannot change | The dashboard stores another value, which is ignored | |
| `data-directory` | It exists and this user can write it | It does not exist (the first start creates it: check it is the volume you meant) | It is a file, or not writable |
| `file-permissions` | `paduraksa.db` and `vault.db`, with their `-wal` and `-shm` files, are readable by their owner only | Others or the group can read one (`chmod 600`) | |
| `disk-space` | Free space on the data directory's volume | Under 1 GiB | Under 64 MiB |
| `master-key` | Where the key comes from (a file, a Docker secret, a systemd credential, the `PADURAKSA_MASTER_KEY` variable, or `master.key`), and that it opens `vault.db`. It never prints the key | | The key is missing, malformed, a file others can read, or not the vault's |
| `master-key-location` | The key is not stored beside the vault | It is, which the dashboard's Overview says too | |
| `config-database`, `vault-database` | Each opens read-only, and its migrations against this binary's | | The file can't be opened, or it has migrations this binary does not know: it was written by a newer version (a downgrade) |
| `listener-http`, `listener-https`, `listener-https-udp`, `listener-admin`, `listener-admin-loopback` | Each configured port is free, or held by the running Paduraksa (its admin answers `/healthz`), or held by something else | The port can't be tested as this user (a port below 1024 needs root or `CAP_NET_BIND_SERVICE`) | Something else holds it |
| `http3` | libmsquic loads | It does not: HTTP/3 stays off | |
| `clock` | The system clock against the `Date` header of `www.cloudflare.com` | More than 30 seconds off (authenticator codes can be refused) | More than 5 minutes off (ACME and certificate checks fail) |
| `server`, `server-version` | A server answering on the admin loopback, and its version against this binary's | It reports itself unavailable, or runs another version (restart it after an upgrade) | |

Notes:

- A migration the database lacks is `[info]`, not a warning: the next start applies it. It is a failure only the other way round.
- The clock check is the one outward request, to Cloudflare, so it runs only with `--check-clock`. Without the network it is skipped, never failed.
- The server's version needs a token, because `/api/v1/version` does. Without one the line says so.
- Doctor writes nothing, creates nothing and never generates a key.

## Backup, restore and master key rotation

These are the commands. The steps, and what to do when one goes wrong, are in [Backups and restore](../operating/backups.md) and [Changing the master key](../operating/master-key.md#changing-the-master-key).

- A backup passphrase comes from `--passphrase-file` (a file, or `-` for standard input), else `PADURAKSA_BACKUP_PASSPHRASE`, else a prompt. It is never an argument.
- The new master key for `vault rotate-kek` comes from `--new-key-file` (a file, or `-` for standard input), never from an argument.

## Config export and import

`config export` and `config import` are clients of the running instance's admin API. They never touch the database. Give them an API token with `config:read` to export and `config:write` to import. The steps are in [Export and import](../operating/export-import.md#the-cli); these are the options.

Options of `config import`:

| Option | What |
|---|---|
| `--mode merge\|replace` | `merge` (the default) adds and updates and deletes nothing. `replace` makes the config the file |
| `--dry-run` | Check everything and show the changes and warnings, but commit nothing |
| `--message`, `-m` | The revision's note (at most 1000 characters) |
| `--base-revision <rev_...>` | Refuse the import if this revision is no longer the latest |
| `--force` | Do it even if it removes the admin's own way in through the proxy |

`config export` also takes `--revision <rev_...>` and `--output`, `-o <file>`. Both take `--token-file`, and `--allow-insecure-http`.

- The token comes from `--token-file` (its first line) or `PADURAKSA_TOKEN`, never from the command line.
- The address is `--url`, else `PADURAKSA_URL`, else this installation's own admin listener on loopback. Certificate and `http://` rules are in [Export and import](../operating/export-import.md#the-cli).
- A dry run prints the changes (`+`, `-`, `~` and the path) and the warnings.
- A refusal prints each error with its YAML path and line, and exits 1.
- Exit 0 is success, including "nothing to change".
