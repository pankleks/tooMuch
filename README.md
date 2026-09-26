# tooMuch

<p align="center"><img src="tooMuch.png" width="180" alt="tooMuch time monitor icon"></p>

tooMuch is a Windows parental-control app. A Docker-hosted server stores device policies and usage; one LocalSystem Windows service enforces limits for a selected standard child account.

## Deploy the server

On the Docker host, clone the repository and configure it:

```sh
cp .env.example .env
# Edit .env: set a strong ADMIN_PASSWORD and the desired TIME_ZONE
docker compose up -d --build
```

The admin panel is at `http://<server-ip>:3020/admin` (user `admin`, password from `.env`). Persistent data is stored in `./data`; back it up. Default timezone is `Europe/Warsaw`. Set `TIME_ZONE` in `.env` to another IANA timezone if needed.

### Install the admin panel as an app

The admin panel is a responsive Progressive Web App (PWA) and uses the same interface on desktop and mobile. Browsers require a secure HTTPS origin for installation and offline app-shell support; plain HTTP works only on `localhost` for this purpose.

For direct HTTPS, obtain a certificate trusted by the devices that will use the panel, place its certificate chain and private key in `./certs/`, and set these paths in `.env`:

```dotenv
TLS_CERT_FILE=/certs/fullchain.pem
TLS_KEY_FILE=/certs/privkey.pem
```

Then restart with `docker compose up -d --build` and open `https://<certificate-hostname>:3020/admin`. The certificate must cover that hostname and be trusted by each phone and computer. A trusted HTTPS reverse proxy is also supported; in that setup, leave the TLS variables empty and proxy HTTPS traffic to the server's HTTP port. A self-signed certificate that devices do not trust will not enable PWA installation.

Use **Install app** in supported desktop/Android browsers. On iPhone or iPad, open the panel in Safari and choose **Share → Add to Home Screen**. The service worker caches only the app shell; device data, login, and changes still require a connection to the server.

To offer the Windows installer from the panel, copy the release assets into `./data/dist/`:

```sh
mkdir -p data/dist
curl -fL <release-zip-url> -o data/dist/tooMuch-win-x64.zip
curl -fL <release-version-json-url> -o data/dist/version.json
```

The zip and `version.json` are published on the GitHub Releases page. After server code updates, run `git pull` and `docker compose up -d --build`.

## Install the Windows client

1. Download and extract `tooMuch-win-x64.zip` on the child's PC.
2. From the child's Windows desktop, open PowerShell as administrator and run:

   ```powershell
   cd C:\path\to\tooMuch-win-x64
   .\install.ps1 -ServerUrl http://<server-ip>:3020
   ```

   The installer detects the interactive account by SID, registers the device, and installs the automatic `TooMuch` service as LocalSystem. If account detection is ambiguous, specify it explicitly with `-ChildAccount 'COMPUTER\child'`. The protected account should be a standard user, not an administrator.

3. Confirm installation with `Get-Service TooMuch`, then set that device's schedule and limits in the admin panel.

For upgrades, extract the new release and run `install.ps1` again with the same server URL; uninstalling first is not required. The device identity, protected SID, cached policy, and usage are preserved locally. `uninstall.ps1` removes the service and program but retains local data and the server's device record.

## Features

- Per-device weekday limits and allowed time windows, edited in the admin panel.
- Parents can add 15m, 30m, or 1h to a device's daily limit for today only; this never changes scheduled time windows.
- Active time is counted for the selected child's unlocked Windows session, including watching videos without input. Locked sessions, sleep, and other accounts are excluded.
- At the limit or outside an allowed window, the service disconnects the child's Windows session. Running apps remain open; signing in again disconnects the session while access is still blocked. This is not a Windows logon prohibition.
- A ten-minute warning before a daily limit or allowed-window end.
- Parent-to-child Windows messages with delivery/confirmation status and a 15-minute default expiry.
- Usage, connection status, recent messages, relative last-seen time, and compact durations such as `1h 30m` in the admin panel.
- A responsive admin panel with History and Config tabs, installable as a PWA on desktop and mobile.
- A child-session tray icon shows remaining time and access-window end. It is display-only; the service enforces limits independently.
- Cached policy continues to enforce limits while the client is offline; usage syncs when the server is reachable again.

The client installs one background service and a small tray process for the child session. Closing the tray never stops accounting or enforcement. The server uses file-backed storage and supports one server process per data directory (do not share it between replicas).
