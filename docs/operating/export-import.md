# Export and import

The whole config (hosts, routes, upstreams, profiles, rate limits, access lists, rule sets, auth providers and the global
settings) is one document, and it can leave and enter as YAML: **Settings, Export and import** in the dashboard, `GET
/api/v1/config/export` and `POST /api/v1/config/import` in the API, or the CLI. An export holds no secret: an access list's
users are a `username` and the vault's `credentialId`, never a password, and a certificate is only a binding to one.

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

Every `id` is optional on import: an object without one is the stored object of the same name (a host: the one that serves
the first name it lists), else a new one, so importing the same file twice changes nothing. A rename needs the `id`. A
reference to another object (`profile`, `upstream`, `policy`, `accessList`, `ruleSet`, `authProvider`) is its name; the
built-in rule-set presets can be named too. Unknown keys, a `version`, a `password` and every YAML tag are errors, and each
error comes with its path and line.

**Merge** (the default) adds the objects the file names and updates the ones it matches, as written (a key the file leaves
out is gone from that object), and deletes nothing. **Replace** makes the config the file, deleting what it leaves out. Both
are one audited revision that you can undo from History, and both can be tried first: the dashboard shows the diff before
Apply, the API takes `?dryRun=true`, the CLI `--dry-run`.

A user's `credentialId` must be one the vault holds a password under (set the password in the dashboard first), unless the
live config already names it; otherwise the import is refused, a dry run included. Importing an export in replace mode on
another instance therefore needs the same vault, or lists without users.

The CLI is a client of the running instance's admin API, like any script (the config database has one writer, the server).
Give it an API token with `config:read` to export and `config:write` to import, from a file or the environment, never the
command line:

```sh
export PADURAKSA_TOKEN=pdk_...                       # or --token-file /path/to/token
paduraksa config export > paduraksa.yaml             # --revision rev_... for a past one, --output FILE
paduraksa config import --dry-run paduraksa.yaml     # what would change
paduraksa config import --mode replace paduraksa.yaml --message "from git"
# in Docker, from the host:
docker compose exec -e PADURAKSA_TOKEN paduraksa paduraksa config export
```

The address defaults to this installation's own admin listener on loopback (`--url` or `PADURAKSA_URL` for another). A
loopback address is not certificate-checked, because the admin certificate is signed by the instance's own CA; any other
address must present a certificate your system trusts, and the CLI refuses to send the token over plain `http://` to one
that isn't loopback unless you pass `--allow-insecure-http`. Keep the token file private (`chmod 600`); the CLI warns when
others can read it. The YAML is limited to 4 MiB, 24 levels of nesting and 200 000 values
(an alias counts every time it is used).
