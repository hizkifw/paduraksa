# Export and import

The whole config is one document: hosts, routes, upstreams, profiles, rate limits, access lists, rule sets, auth providers and the global settings. It can leave and enter as YAML, from **Settings, Export and import** in the dashboard, with `GET /api/v1/config/export` and `POST /api/v1/config/import`, or with the CLI.

An export holds no secret. An access list's users are a `username` and the vault's `credentialId`, never a password, and a certificate is only a binding to one.

```yaml
apiVersion: paduraksa.dev/v1
kind: Config
profiles:
  - name: media
    settings: {forceHttps: true}
upstreams:
  - name: jellyfin
    destinations:
      - address: http://jellyfin:8096
hosts:
  - names: [jellyfin.example.com]
    profile: media          # a reference is the other object's name
    routes:
      - upstream: jellyfin
```

## How an import reads the file

- Every `id` is optional. An object without one is matched to the stored object of the same name, or to a new one. For a host, the match is the host that serves the first name it lists. Importing the same file twice changes nothing.
- A rename needs the `id`.
- A reference to another object (`profile`, `upstream`, `policy`, `accessList`, `ruleSet`, `authProvider`) is its name. The built-in rule-set presets can be named too.
- Unknown keys, a `version`, a `password` and every YAML tag are errors. Each error comes with its path and line.
- The YAML is limited to 4 MiB, 24 levels of nesting and 200 000 values.

## Merge or replace

- **Merge** (the default) adds the objects the file names and updates the ones it matches, as written. A key the file leaves out is gone from that object. It deletes nothing.
- **Replace** makes the config the file, and deletes what the file leaves out.

Both make one audited revision that you can undo from History. Both can be tried first: the dashboard shows the diff before Apply, the API takes `?dryRun=true`, and the CLI takes `--dry-run`.

A user's `credentialId` must be one the vault holds a password under. Set the password in the dashboard first, unless the live config already names it. Otherwise the import is refused, a dry run included. So importing an export in replace mode on another instance needs the same vault, or a file without users.

## The CLI

The CLI is a client of the running instance's admin API, like any script. Give it an API token with `config:read` to export and `config:write` to import, from a file or the environment, never the command line:

```sh
# The token, from the environment (or pass --token-file /path/to/token)
export PADURAKSA_TOKEN=pdk_...

# Export the live config (--revision rev_... for a past one, --output FILE)
paduraksa config export > paduraksa.yaml

# See what an import would change
paduraksa config import --dry-run paduraksa.yaml

# Import, replacing the config, with a note for History
paduraksa config import --mode replace paduraksa.yaml --message "from git"

# In Docker, from the host
docker compose exec -e PADURAKSA_TOKEN paduraksa paduraksa config export
```

- The address defaults to this installation's own admin listener on loopback. Use `--url` or `PADURAKSA_URL` for another.
- A loopback address is not certificate-checked, because the admin certificate is signed by the instance's own CA. Any other address must present a certificate your system trusts.
- The CLI refuses to send the token over plain `http://` to a non-loopback address unless you pass `--allow-insecure-http`.
- Keep the token file private (`chmod 600`). The CLI warns when others can read it.
