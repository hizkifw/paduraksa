# Bare metal (systemd)

Use this when you do not want Docker. The service is the same binary, run by systemd with a master key in a credential file.

1. Publish a self-contained build (no .NET runtime needed on the host):

   ```sh
   dotnet publish src/Paduraksa.Server/Paduraksa.Server.csproj -c Release -r linux-x64 --self-contained true -o /opt/paduraksa
   ```

   **Install libmsquic** from Microsoft's Ubuntu 24.04 feed, or HTTP/3 is silently off (the startup log says
   `HTTP/3 is off: libmsquic isn't available...` and `Alt-Svc` is not advertised):

   ```sh
   curl -fsSL https://packages.microsoft.com/keys/microsoft.asc \
     | sudo gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg
   echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/ubuntu/24.04/prod noble main" \
     | sudo tee /etc/apt/sources.list.d/microsoft-prod.list >/dev/null
   sudo apt-get update && sudo apt-get install -y libmsquic=2.5.10
   ```

2. Create the service user and data directory:

   ```sh
   sudo useradd --system --user-group --home /var/lib/paduraksa --shell /usr/sbin/nologin paduraksa
   sudo install -d -o paduraksa -g paduraksa -m 0750 /var/lib/paduraksa
   sudo install -d -m 0750 /etc/paduraksa
   head -c 32 /dev/urandom | base64 | sudo install -m 600 /dev/stdin /etc/paduraksa/master.key
   ```

3. Install [`deploy/paduraksa.service`](../../deploy/paduraksa.service) as `/etc/systemd/system/paduraksa.service` and
   start it:

   ```sh
   sudo systemctl daemon-reload
   sudo systemctl enable --now paduraksa
   sudo journalctl -u paduraksa -f     # the setup-token banner is here
   ```

   The unit runs the app as `paduraksa`, gives it only `CAP_NET_BIND_SERVICE` (for 80/443), and passes the master key
   through systemd's `LoadCredential` (`PADURAKSA_MASTER_KEY_FILE=%d/master.key`), so the key is readable only by the
   service and is never in the data directory. `TimeoutStopSec=40s` matches the 30-second drain;
   `RestartPreventExitStatus=2 3` stops a restart loop when startup fails (exit 2) or the configuration is invalid (exit 3).

4. Finish setup as in [Getting started](../guide/getting-started.md): read the setup token from the journal, trust the CA root from
   `https://<host>:9443/api/v1/ca/root.pem` (PEM; save it as `paduraksa.crt` for `update-ca-certificates`) or
   `/api/v1/ca/root.crt` (DER, for Android/iOS), open `https://<host>:9443/`.
