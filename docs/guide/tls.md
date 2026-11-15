# TLS and certificates

Paduraksa serves a certificate per name (SNI) on 443. Certificates and the accounts and providers behind them live under **TLS** in the dashboard.

Certificates are not part of the config. A change to one acts at once, with no review sheet or draft, and makes no revision.

## How a host gets its certificate

Each host has a **Certificate** setting:

| Setting | Meaning |
|---|---|
| Automatic (the default) | For each name, the best usable certificate that covers it |
| A specific certificate | That certificate for every name of the host |
| None | No HTTPS for this host. It cannot force HTTPS |

- A certificate covers a name when it lists it, or lists the wildcard of its parent. `*.example.com` covers `www.example.com`, not `example.com` or `a.b.example.com`.
- The best match is an exact name over a wildcard, then the one that expires last.
- A host with only IP-address names is served with the **default certificate**.
- The **default certificate** (Global defaults) is served when the SNI matches no host, and for names no certificate covers. With none set, those handshakes are rejected.

A name no valid certificate covers is a warning in the review and a "degraded" name in the Hosts list. It never stops the config from loading. A change that binds a host to a certificate that is missing, expired or pending is refused.

The **Certificates** page lists every certificate with its source, status, a colour-coded countdown (amber inside 30 days, red inside 7), the hosts that use it and its issuer. A row opens the detail: names, validity, fingerprint, chain, and for ACME the account, challenge, order state and next renewal.

## ACME (Let's Encrypt and others)

Paduraksa has its own ACME client (RFC 8555), so the account key stays in the vault.

### 1. An ACME account

Add one under **ACME accounts**. Pick a CA:

| CA | Notes |
|---|---|
| Let's Encrypt | Free and trusted everywhere. Rate-limited, so try staging first |
| Let's Encrypt (staging) | Generous limits. The certificates are not trusted by browsers |
| ZeroSSL | Needs an EAB key from your ZeroSSL account |
| Google Trust Services | Needs an EAB key from Google Cloud |
| Any other | An `https://` directory URL |

- A contact email is optional. You must agree to the CA's terms.
- EAB (external account binding) is a key ID and an HMAC key the CA gives you. They are stored in the vault and never shown again.
- Deactivating an account is final.

### 2. Request a certificate

**Certificates**, **Add certificate**, **From an ACME CA**: the hostnames, the account, the challenge, a name and the key type (ECDSA P-256 by default, or P-384, RSA 2048 or RSA 3072).

| Challenge | How it works | Limits |
|---|---|---|
| HTTP-01 | The CA fetches a token from port 80 of each name. Paduraksa answers it before anything else | Port 80 must be reachable from the internet. No wildcards |
| DNS-01 | A TXT record is published through your DNS provider | Wildcards work, and nothing needs to be reachable. An IP address needs HTTP-01 |

HTTP-01 notes:

- Paduraksa answers the challenge only on plain HTTP, for a token it has published, whatever hosts exist. Any other request under `/.well-known/acme-challenge/` falls through to the host, so an upstream that runs its own ACME client keeps working.
- Behind a firewall or Cloudflare's orange cloud, the CA's validation must still reach port 80. See [Behind Cloudflare](cloudflare.md#origin-lockdown).

Live progress shows each step: order, authorizations, challenge published, DNS propagation (DNS-01 only), validated, finalized, issued. A failure shows the CA's message and the retry time, with **Retry now**.

### Renewal

Paduraksa renews every ACME certificate by itself:

- If the CA offers a renewal window (ARI), it renews inside that window.
- Otherwise it renews when a third of the lifetime remains. For a 90-day certificate, that is about 30 days before the end.
- After a failure it retries with a growing delay, and honours the CA's `Retry-After`. **Retry now** and **Renew now** skip the wait.
- It resumes unfinished orders after a restart.
- `Paduraksa__Acme__Enabled=false` pauses everything it does on its own. You can still request and renew by hand.

Expiry notices cover every certificate, whatever its source: 30, 14, 7 and 1 days before expiry. See [notifications](observability.md#notifications).

## DNS-01 providers

Add a provider under **DNS providers**: Cloudflare or deSEC, a label and an API token. The form names the one host the token is sent to (`api.cloudflare.com` or `desec.io`). The token is stored in the vault, never shown again and never sent anywhere else. **Test** checks it.

- **Cloudflare:** use a scoped API token with DNS edit on the zone. **Test** only reads, so a token that can read but not edit passes it and fails at the first issuance.
- **deSEC:** a token for the domain.

### Propagation check

Before the CA is told to validate, Paduraksa asks the zone's own name servers until they return the TXT record. If the check cannot run, it waits a fixed 30 seconds instead. If it times out with no server holding the record, the attempt fails without asking the CA, because a failed validation counts against the CA's limits.

Switch the check off, or change the wait, under Settings, Server, Integrations, ACME ([Server settings](server-settings.md)), or with `Paduraksa__Acme__DnsPropagationDelay`. Its timings and resolvers are environment settings, `Paduraksa__Acme__DnsPropagation__*`: see [Server settings](../operating/settings.md#acme-and-dns-01).

### CNAME delegation

If `_acme-challenge.example.com` is a CNAME to a name in another zone, Paduraksa follows it (up to 8 names deep) and publishes the TXT record at the end of the chain.

- The provider's credential must reach the target's zone.
- Use it to keep your main zone's DNS credentials out of the proxy.
- Turning the propagation check off turns following off too.
- There is no setting for it.

## Uploaded certificates

**Add certificate**, **Upload**:

- a PEM chain and a PKCS#8 private key, in one file or two; or
- a PFX with an optional password.

The file is capped at 1 MiB. Weak keys, a CA certificate in place of a leaf, and a key that does not match are refused with the reason.

An upload is the way to use a Cloudflare Origin CA certificate (which only Cloudflare trusts) or one from your own CA.

## Self-signed certificates

**Add certificate**, **Self-signed**: names, a label, validity (365 days by default, up to 825) and a key type. Browsers do not trust it. It suits a service reached by pinned fingerprint or a device you control.

## The local CA

The local CA is a root Paduraksa keeps in the vault to issue certificates for names a public CA cannot validate: `nas.lan`, `app.home.arpa`, a bare IP. The admin port's certificate comes from it.

- **Create it** from **Local CA**, or by issuing the first local-CA certificate, which creates it when there is none.
- **Issue leaves** from **Add certificate**, **From the local CA**: 90 days by default, up to 397.

### Trust the root

A device accepts the local CA's certificates only after you install its root. The Local CA page (and the Certificates page) offers the root as PEM and as `.crt` (DER), with its fingerprint and how to install it on Windows, macOS, Linux, Firefox and phones. The same files are served without signing in:

```sh
curl -k -o paduraksa.crt https://<host>:9443/api/v1/ca/root.pem      # PEM
curl -k -o paduraksa-root.crt https://<host>:9443/api/v1/ca/root.crt # DER, for Android and iOS
curl -k https://<host>:9443/api/v1/ca                                # fingerprint, subject, validity
```

These endpoints serve the current root only. On Linux, `update-ca-certificates` reads PEM: save `root.pem` with a `.crt` name.

### Limit what the root may issue for (name constraints)

A root is **unlimited by default**: it may issue for any name. A device that trusts it then trusts it for every site, so limiting it is safer.

- When you create or rotate the root, switch on **Limit what this root may issue for** and choose names.
- A device that trusts the root then accepts its certificates only for a listed domain (and everything under it), network or address.
- The dialog suggests private-use domains (`home.arpa`, `lan`, `local`, `internal`, `home` and others), private networks (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16` and others), the admin certificate's names and the names the CA already issued for.
- A limited root always keeps the admin certificate's names, so a rotation cannot lock you out of the admin port.
- A root's constraints cannot be edited. To change them, rotate.

### Rotate the root

**Rotate the root** makes a new root and destroys the old root's private key in the same step. Use it when the old key may have leaked, or to change the constraints.

- The admin port's certificate is issued again from the new root at once.
- The old root stays listed as *retiring* and downloadable. Certificates it signed keep serving, so nothing breaks for devices that have not installed the new root yet.
- **Re-issue** replaces those leaves under the same IDs from the new root. A leaf whose names the new constraints do not permit is left as it is and listed.
- **Retire** deletes an old root. It is refused while a certificate it signed is still in use.
- The root lasts 10 years. Nothing renews it, so rotate it before then.

### The admin certificate

The admin port's own certificate is a local-CA leaf that Paduraksa looks after. You cannot delete it or bind a host to it.

- It covers `localhost`, the loopback addresses, the machine's host name, and `Paduraksa__Listen__AdminBind` when that is a specific address.
- Add names with `Paduraksa__Admin__CertificateNames`, a comma-separated list or indexed keys.
- It is renewed when its names change or a third of its life is left.

## HTTP/3

443/**udp** is HTTP/3. Map it one to one, like the TCP ports.

- If UDP is missing or firewalled, browsers fall back to HTTP/2 over 443/tcp and nothing breaks.
- The image installs `libmsquic` and logs `HTTP/3 is on: QUIC on UDP 443 ...` at startup. If the log says HTTP/3 is off, the package did not load. On bare metal, install it yourself: see [Bare metal](../operating/bare-metal.md).
- Behind a port translation, set `Paduraksa__Listen__PublicHttpsPort` to the port clients use, because `Alt-Svc` and the HTTP to HTTPS redirect advertise it. Do not remap the container's own ports.
- A host can turn HTTP/3 off (**HTTP/3** under TLS in its settings). A host with Authenticated Origin Pulls never serves it, because Cloudflare does not connect to origins over QUIC.
- `Paduraksa__Listen__Http3=false` turns the listener off for every host.
- There is no PROXY protocol on QUIC.
