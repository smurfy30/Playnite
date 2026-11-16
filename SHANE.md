# shane/main: what's ours on top of upstream Playnite

Upstream: JosefNemec/Playnite (`upstream` remote, mirrored on `master`). Ours: `shane/main`.

## Layout

| path | what |
|---|---|
| `build.sh` | full solution build + copies BrowserProcess + writes `userdatadir.txt` |
| `source/Playnite/Settings/PlaynitePaths.cs` | patch: `userdatadir.txt` pins user data outside `bin/` |
| `source/Playnite/Common.config` | dev config pointed at the real add-on API, online |
| `extensions/` | our own plugins. `extensions/deploy.sh` builds and installs them |
| `extensions/Ledger/` | daily snapshots + Steam acquisition dates |
| `tools/install-addons.py` | install community add-ons from the official API |
| `tools/ledger.py` | query Ledger data: `stats`, `game`, `daily`, `backlog` (`--json`) |

User data: `E:/projects/playnite-data` (never in git; it holds store tokens).

## Rules
- Prefer a plugin in `extensions/` over patching `source/`. Patches make upstream merges hurt.
- Counts are queries (`tools/ledger.py`), never stored.
- Never overwrite data without a recorded way back (Ledger keeps `PreviousAdded`).

## Installed community add-ons (2026-11-16)
Metadata: IGDB, Universal Steam, PCGamingWiki, Extra Metadata Loader.
Tracking: GameActivity (sessions), SuccessStory (achievements), HowLongToBeat, CheckDlc, Year In Review.
Library: DuplicateHider. Mods: r2modman Launch Options, Nexus Mods checker.

## Idea backlog (PM'd 2026-11-16; keep/kill is Shane's call)

Ranked by value ÷ effort. Status: **built**, **try** (worth a spike), **park**.

1. **built** Daily snapshots (Ledger). Playtime history across all stores, from 2026-11-16 on.
2. **built, needs you once** Steam acquisition dates → `Date added`. Menu → Ledger →
   Import Steam acquisition dates (one Steam login in Playnite's browser).
3. **try P1** *First played* estimate per game: earliest of (SuccessStory's first achievement
   unlock, GameActivity's first session, Steam `rtime_last_played` when playtime is tiny). Gets
   real dates for the backlog that Steam never kept. Needs SuccessStory to have fetched
   achievements first (its menu → refresh all).
4. **try P1** Library on your phone via Bridge: `tools/ledger.py --json` behind bridge-agent
   (`/playnite/*`), plus a panel. Read-only, no Playnite running needed. The cheapest win
   for "use it away from the desktop". Compare with the community *Playnite Web* add-on
   (MQTT + web app) before building our own UI.
5. **try P2** Epic/GOG acquisition dates. Epic's order history API and GOG's
   `/account/getFilteredProducts` give purchase dates via the same logged-in webview trick.
6. **try P2** Mods panel: per game, mods installed via r2modman/Thunderstore profiles
   (`%AppData%/r2modmanPlus-local`), Vortex (`D:/Vortex Mods`, pc-status already reads it),
   Steam Workshop (`steamapps/workshop/content/<appid>`). Read-only first. Shows as a game
   details section in Playnite and as JSON for Bridge.
7. **try P2** "Should I play" view: backlog × HowLongToBeat × Steam review score ×
   installed. A filter preset plus a ledger command.
8. **park** Weekly digest to the phone (estate-mcp): hours played, new games, stale installs
   (installed, not played in 90 days, size on disk) as uninstall candidates.
9. **park** Rust/Tauri rewrite. Revisit only if WPF/.NET 4.6.2 blocks something we want.

## Known caveats
- Family-shared Steam games (95) share the Steam PluginId. They won't have licenses on
  your account, so they stay unmatched (correct).
- Prefix matching skips names under 6 chars to avoid false matches (e.g. Hades vs a bundle).
