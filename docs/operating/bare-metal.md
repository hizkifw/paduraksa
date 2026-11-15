# Bare metal (systemd)

Use this when you do not want Docker. The service is the same binary, run by systemd with a master key in a credential file.

1. Publish a self-contained build, so the machine needs no .NET runtime:

   ```sh
   # Build into /opt/paduraksa
   dotnet publish src/Paduraksa.Server/Paduraksa.Server.csproj -c Release -r linux-x64 --self-contained true -o /opt/paduraksa
   ```

   **Install libmsquic** from Microsoft's Ubuntu 24.04 feed, or HTTP/3 is silently off. The startup log then says `HTTP/3 is off: libmsquic isn't available...`.

   ```sh
   # Add Microsoft's signing key and package feed
   curl -fsSL https://packages.microsoft.com/keys/microsoft.asc \
     | sudo gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg
   echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/ubuntu/24.04/prod noble main" \
     | sudo tee /etc/apt/sources.list.d/microsoft-prod.list >/dev/null

   # Install libmsquic
   sudo apt-get update && sudo apt-get install -y libmsquic=2.5.10
   ```

2. Create the service user and data directory:

   ```sh
   # The service user, and the data directory it owns
   sudo useradd --system --user-group --home /var/lib/paduraksa --shell /usr/sbin/nologin paduraksa
   sudo install -d -o paduraksa -g paduraksa -m 0750 /var/lib/paduraksa

   # The master key, readable by root only
   sudo install -d -m 0750 /etc/paduraksa
   head -c 32 /dev/urandom | base64 | sudo install -m 600 /dev/stdin /etc/paduraksa/master.key
   ```

3. Install [`deploy/paduraksa.service`](../../deploy/paduraksa.service) as `/etc/systemd/system/paduraksa.service` and
   start it:

   ```sh
   # Load the unit, then start it now and at boot
   sudo systemctl daemon-reload
   sudo systemctl enable --now paduraksa

   # Follow the log; the setup-token banner is here
   sudo journalctl -u paduraksa -f
   ```

   The unit runs the app as `paduraksa` with only `CAP_NET_BIND_SERVICE`, for ports 80 and 443. The master key reaches it through systemd's `LoadCredential`, so only the service can read it and it is never in the data directory. A failed start (exit 2) or an invalid configuration (exit 3) does not restart in a loop.

4. Finish setup as in [Getting started](../guide/getting-started.md):
    - read the setup token from the journal;
    - trust the CA root from `https://<host>:9443/api/v1/ca/root.pem` (PEM; save it as `paduraksa.crt` for `update-ca-certificates`) or `/api/v1/ca/root.crt` (DER, for Android and iOS);
    - open `https://<host>:9443/`.
