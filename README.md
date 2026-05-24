# WebsiteMOTD

A Puck mod that shows a webpage when you join a server and lets players queue
videos/streams onto shared in-game screens. Server admins can point it at any
URL; clients get a built-in browser overlay with video/GIF playback, a URL
bar, a trusted-sites prompt, and WebView2-backed rendering for JS-heavy pages.

## What's in this repo

- **[src/](src/)** — the Unity mod itself (C#, net48, built as `WebsiteMOTD.dll`).
  - [Plugin.cs](src/Plugin.cs) — entry point, netcode wiring, shared-queue server logic, chat commands.
  - [Config.cs](src/Config.cs) — `ServerConfig` (server-only JSON) and `ClientConfig` (client-only JSON).
  - [MOTDUI.cs](src/MOTDUI.cs) — the full-screen overlay: URL bar, back/forward, queue panel, settings, mute, zoom.
  - [MOTDWebView.cs](src/MOTDWebView.cs) — WebView2 host for rendering modern web pages inside the overlay.
  - [MOTDHtmlRenderer.cs](src/MOTDHtmlRenderer.cs) — lightweight fallback renderer (text/images/video tags) for when WebView2 isn't available.
  - [MOTDWorldScreen.cs](src/MOTDWorldScreen.cs) — the 3D in-world screens that play whatever the queue is showing.
- **[libs/](libs/)** — Puck game DLLs referenced at build time (not copied into the output).
- **[native/](native/)** — WebView2 native binaries, copied next to the built DLL.
- **[native/fetch-ublock.ps1](native/fetch-ublock.ps1)** — one-shot helper to grab the latest uBlock Origin chromium build into `native/x64/extensions/ublock0/` so the mod loads it as a real WebView2 extension (see _Ad-block_ below).
- **[MOTD.csproj](MOTD.csproj)** — SDK-style project. Output drops directly into `Puck/Plugins/WebsiteMOTD/`.

## Build

From the repo root:

```
dotnet build
```

The build writes `WebsiteMOTD.dll` + `native/` into `C:\Program Files (x86)\Steam\steamapps\common\Puck\Plugins\WebsiteMOTD\`. Adjust `<OutputPath>` in `MOTD.csproj` if Puck lives somewhere else.

Required files alongside the DLL after a clean install:
- `native/` folder with WebView2 loader DLLs (copied automatically by the build).
- No manual config files — the mod creates them on first run (see below).

## Configuration

Two JSON files live in `<puck-root>/config/` (two directories up from the mod
DLL). Each is only created on the side that needs it, so a server install
never gets a client file and vice versa.

- Windows client: `C:\Program Files (x86)\Steam\steamapps\common\Puck\config\`
- Linux server:   `/home/<user>/<unit>/config/`

The directory is created on demand. If you had configs sitting next to the DLL
from a pre-relocation build, they're moved into the new location on first run.

### Server — `server_config.json`

Auto-created on dedicated servers the first time the mod runs.

```json
{
  "screens_enabled": true,
  "queue_enabled": true,
  "motd_url": "https://poncepuck.net/motd/",
  "queue_allowed_sites": [
    "youtube.com",
    "youtu.be",
    "twitch.tv",
    "kick.com"
  ]
}
```

- **`screens_enabled`** — master switch for the 3D in-world screens. Disable for competitive lobbies.
- **`queue_enabled`** — master switch for the shared queue system.
- **`motd_url`** — the page pushed to every client on connect. Change without recompiling.
- **`queue_allowed_sites`** — host allowlist for `/q` requests. Matches subdomains (so `youtube.com` also allows `m.youtube.com`). Empty list = no restriction.

### Client — `client_config.json`

Auto-created on clients the first time the UI settings change or a site is trusted.

```json
{
  "volume": 0.5,
  "muted": false,
  "screens_disabled": false,
  "zoom": 1.25,
  "trusted_sites": ["poncepuck.net"]
}
```

- **`volume`**, **`muted`**, **`zoom`** — overlay UI preferences.
- **`screens_disabled`** — per-user override to hide the in-world screens even when the server has them on.
- **`trusted_sites`** — domains the user has clicked "Don't ask again" on in the confirmation dialog.

### Legacy migration

First launch after upgrading looks for the old split files (`server_config.ini`,
`motd_settings.ini`, `trusted_sites.txt`) and folds them into the new JSON.
The old files are left on disk so you can downgrade without losing data.

## Chat commands (client)

- `/web <url>`, `/motd <url>`, or `/browser <url>` — open any URL in the overlay.
- `/q <url>` or `/queue <url>` — add a URL to the shared screen queue. With no arg, just opens the overlay so you can see the queue panel.

## Shared queue

- Server is authoritative: clients send `add`/`vote`/`remove` via a named-message channel and the server broadcasts the full state back.
- Vote-skip threshold is majority of connected clients (including the server).
- Items are rejected if the host isn't on `queue_allowed_sites`; the requester gets a chat message listing the permitted domains.
- Disconnecting clients have their queued items and votes cleaned up automatically; if the owner of the currently playing item leaves, the queue advances.

## Ad-block (uBlock Origin)

The overlay's WebView2 instance supports loading Chromium browser extensions via `ICoreWebView2Profile7::AddBrowserExtensionAsync` (WebView2 runtime ≥ Edge 119, June 2024). The mod auto-detects any unpacked extensions under `native/x64/extensions/<name>/` and loads them once per process into the shared profile.

To install uBlock Origin:

```
pwsh native/fetch-ublock.ps1
```

The script pulls the latest `uBlock0_*.chromium.zip` from [gorhill/uBlock](https://github.com/gorhill/uBlock) releases and unpacks the contents (including `manifest.json`) into `native/x64/extensions/ublock0/`. On next mod load you should see:

```
[WebsiteMOTD] Loading browser extension: ...\native\x64\extensions\ublock0
[WebsiteMOTD] Browser extension loaded: cjpalhdlnbpafiamejdnhcphjbkeiagm
```

Extensions are profile-bound: once installed, they persist in `%LOCALAPPDATA%\UnityWebView2`, so subsequent runs are idempotent (no re-install on every launch). Removing the folder + re-running the script upgrades to the latest uBlock release.

If you see `ExtensionError:no-icorewebview2profile7`, the user's WebView2 runtime is too old — they need a Windows Update or to install the [Evergreen Standalone runtime](https://developer.microsoft.com/microsoft-edge/webview2/). The mod degrades gracefully without uBlock: the legacy JS ad-block (YouTube CSS selectors, skip-button click) still runs.

Other ad-block extensions (AdBlock, Adblock Plus, etc.) work the same way — drop their unpacked Chromium folder into `native/x64/extensions/<whatever>/` and they'll be picked up. The mod loads every subfolder that contains a `manifest.json`.

## Companion website

The mod works against a PHP backend on cPanel (`poncepuck.net`) that hosts the
MOTD content, player stats, Steam-login profiles, admin panel, and Discord
linking. That code is kept in a sibling repo (`Poncepuck.net/`)
and isn't required for the mod to run — any URL in `motd_url` works.

## License

Personal project — no license declared. Ask before redistributing.
