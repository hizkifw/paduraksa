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
| `paduraksa doctor --liveness` | Exits 0 if the admin `/healthz` answers over loopback, 1 if not | The running server |

`paduraksa mcp-sandbox` is the process the [MCP](mcp.md) server starts for an assistant's code. Do not run it yourself.

Exit codes: `0` success, `1` a failure, `2` the server could not start, `3` invalid settings.

## Recovery commands

These work on the data directory, so they need the same environment as the server (`Paduraksa__DataDirectory`, `PADURAKSA_MASTER_KEY_FILE`). In the container that is already true.

```sh
docker compose exec paduraksa paduraksa setup-token
docker compose exec paduraksa paduraksa admin reset-password alice
docker compose exec paduraksa paduraksa admin disable-2fa alice
```

`reset-password` reads the new password from standard input or a no-echo prompt, never from an argument. Both commands are audited with the actor `cli`.

## Backup and restore

```sh
docker exec -it paduraksa paduraksa backup --out /data/backups/paduraksa-$(date +%F).pdkbak
docker compose stop
docker compose run --rm paduraksa restore /data/backups/paduraksa-2026-11-12.pdkbak
```

The passphrase comes from `--passphrase-file` (a file, or `-` for standard input), else `PADURAKSA_BACKUP_PASSPHRASE`, else a prompt. It is never an argument. A backup never overwrites a file. See [Backups and restore](../operating/backups.md).

## Rotate the master key

```sh
docker compose down
docker compose run --rm -v "$PWD:/keys:ro" paduraksa vault rotate-kek --new-key-file /keys/new-master.key
```

The key comes from a file (or `-` for standard input), never from an argument. See [The master key](../operating/master-key.md#changing-the-master-key).

## Config export and import

`config export` and `config import` are clients of the running instance's admin API, like any script. They never touch the database. Give them an API token with `config:read` to export and `config:write` to import.

```sh
export PADURAKSA_TOKEN=pdk_...                       # or --token-file /path/to/token
paduraksa config export > paduraksa.yaml             # --revision rev_... for a past one, --output FILE
paduraksa config import --dry-run paduraksa.yaml     # what would change
paduraksa config import --mode replace paduraksa.yaml --message "from git"
```

Options of `config import`:

| Option | What |
|---|---|
| `--mode merge\|replace` | `merge` (the default) adds and updates and deletes nothing. `replace` makes the config the file |
| `--dry-run` | Check everything and show the changes and warnings, but commit nothing |
| `--message`, `-m` | The revision's note (at most 1000 characters) |
| `--base-revision <rev_...>` | Refuse the import if this revision is no longer the latest |
| `--force` | Do it even if it removes the admin's own way in through the proxy |

`config export` also takes `--revision <rev_...>` and `--output`, `-o <file>`. Both take `--token-file`, and `--allow-insecure-http`.

- The token comes from `--token-file` (its first line) or `PADURAKSA_TOKEN`, never from the command line. Keep the file private (`chmod 600`): the CLI warns when others can read it.
- The address is `--url` (an absolute `http://` or `https://` address), else `PADURAKSA_URL`, else this installation's own admin listener on loopback. A loopback address is not certificate-checked, because the admin certificate is signed by the instance's own CA. Any other address must present a certificate your system trusts. The CLI refuses to send the token over plain `http://` to a non-loopback address unless you pass `--allow-insecure-http`.
- A dry run prints the changes (`+`, `-`, `~` and the path) and the warnings. A refusal prints each error with its YAML path and line and exits 1. Exit 0 is success, including "nothing to change".

More on the format and its rules: [Export and import](../operating/export-import.md).
