# Damian.Rocks — live audience reactions for presentations

Two projects that work together:

| Project | What it is |
|---|---|
| [`server/`](server) | TypeScript Node server for Linux. Hosts the mobile web app and a WebSocket hub. |
| [`desktop/`](desktop) | C#/.NET 10 Windows tray app. Floats the audience's emojis up the left and right edges of your screen, on top of everything (e.g. PowerPoint). |

```
 phones ──(wss /ws)──►  damian.rocks server  ──(wss /display)──►  DamianRocks.exe overlay
```

## Server (`server/`)

```bash
cd server
npm install
npm run build
PORT=3000 npm start          # http://localhost:3000
```

- **`/`** — mobile app. It asks for your name the first time and saves it in a cookie (`dr_name`, 1 year) so returning visitors are remembered. Tapping an emoji floats it up your phone screen and sends it to the presenter.
- **`/ws`** — audience WebSocket. You need the name cookie and a same‑origin request to connect. See [Flood protection](#flood-protection) for limits.
- **`/display`** — WebSocket for the desktop overlay. It only receives reactions; anything the overlay sends is ignored.
- **`/healthz`** — returns JSON with audience, display, socket, IP and banned‑IP counts.

The reaction list lives in `src/server/reactions.ts`. To add a custom image, put a PNG in `public/reactions/` and add `{ id, label, image: '/reactions/x.png' }`. The overlay downloads it by URL.

### Flood protection

All limits are token buckets (a burst size, then a steady refill) defined in `src/server/protection.ts`. Limits are tracked per IP address, and IPv6 addresses are grouped by /64 so one machine can't rotate addresses.

| Layer | Default | Env vars | When exceeded |
|---|---|---|---|
| Reactions per connection | 5 burst, 2/s | `RL_CONN_BURST`, `RL_CONN_PER_SEC` | Dropped, `slow-down` sent, **strike** |
| Raw messages per connection | 20 burst, 8/s | `RL_MSG_BURST`, `RL_MSG_PER_SEC` | Connection closed, **strike ×5** |
| Invalid/binary/unknown message | — | — | Dropped, **strike ×2** |
| Reactions per IP (all tabs/connections combined) | 15 burst, 4/s | `RL_IP_BURST`, `RL_IP_PER_SEC` | Dropped, `busy` sent |
| Reactions to the screen (everyone) | 30 burst, 12/s | `RL_GLOBAL_BURST`, `RL_GLOBAL_PER_SEC` | Dropped, `busy` sent |
| WebSocket connection attempts per IP | 100 burst, 60/min | `RL_CONNECT_BURST`, `RL_CONNECT_PER_MIN` | HTTP 429 |
| Name sign‑ups per IP | 60 burst, 30/min | `RL_NAME_BURST`, `RL_NAME_PER_MIN` | HTTP 429 |
| Open sockets | 250 per IP, 5000 total | `MAX_CONN_PER_IP`, `MAX_CONN_TOTAL` | HTTP 429 / 503 |
| Ban | 20 strikes (strikes expire at 10/min) → 300 s | `BAN_STRIKES`, `BAN_STRIKE_DECAY_PER_MIN`, `BAN_SECONDS` (`0` = never ban) | All of that IP's sockets are closed (`1008 banned:N`); new connections and sign‑ups get 429 |

**Shared Wi‑Fi:** Everyone on venue Wi‑Fi usually shares one public IP, so limits a busy room can legitimately hit (per‑IP, global, connects, sign‑ups) only reject the request. They never add strikes. Strikes come only from things the official web app never does. The web app throttles itself a little below the per‑connection limit (4 burst, 1.6/s), so a fast tapper sees "slow down" on their phone and never gets banned. If you present to a large audience on one network, raise `RL_IP_BURST`/`RL_IP_PER_SEC`.

**Real client IP:** `X-Forwarded-For` is honored only when the request comes from a trusted proxy (`TRUST_PROXY`, default `loopback`, which matches Caddy on the same machine). In that case, the server uses the right‑most untrusted hop. Set `TRUST_PROXY=none` if Node is exposed directly, or set it to a comma‑separated list of proxy IPs.

### Deploying to Linux

1. Copy `server/` to `/opt/damian-rocks/server`, then run `npm ci && npm run build` (needs Node ≥ 20.11).
2. Start it with **pm2**: `pm2 start ecosystem.config.cjs && pm2 save` (run `pm2 startup` once to start on boot). Keep it to one instance; the rate limits are held in memory. **Or** install the systemd unit: `deploy/damian-rocks.service` → `/etc/systemd/system/`, create the `damianrocks` user, then run `systemctl enable --now damian-rocks`.
3. Put [Caddy](https://caddyserver.com) in front using `deploy/Caddyfile`. It gets an HTTPS certificate for damian.rocks automatically and proxies WebSockets.

## Desktop overlay (`desktop/`)

```powershell
cd desktop
.\publish.ps1                 # -> desktop\publish\DamianRocks.exe (self-contained single file)
```

Run `DamianRocks.exe`. It goes to the system tray (the notification‑area drawer) and connects to `wss://damian.rocks/display`. If the connection drops, it reconnects automatically.

To point it at a different server, for example while testing:

```powershell
DamianRocks.exe --server localhost:3000           # -> ws://localhost:3000/display
DamianRocks.exe --server http://192.168.1.20:3000
DamianRocks.exe --server wss://staging.example.com/display
```

The tray icon is gray while disconnected. Right‑click it for:

- the connection status and audience count
- **Show on**: all screens or one particular monitor (handy for a projector)
- **Pause reactions**
- **Send test emoji** (double‑clicking the icon does the same)
- **Start with Windows**
- **Quit**

Each reaction floats up from the bottom of the screen, randomly on the left or right side. It sways gently like a leaf on a slow breeze and bobs a little as it rises. The whole emoji, sway included, always stays inside the outer 10% of the screen on its side, so the middle stays clear. The overlay is click‑through and never takes focus.

Settings and the error log are saved in `%APPDATA%\DamianRocks\`.

Emoji artwork comes from [Noto Emoji](https://github.com/googlefonts/noto-emoji) (Apache 2.0; see `desktop/DamianRocks/Assets/Emoji/NOTO-LICENSE.txt`). WPF can't render color emoji fonts, which is why the images are bundled.
