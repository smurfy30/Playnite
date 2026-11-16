# shane/main: what's ours on top of upstream Playnite

Upstream: JosefNemec/Playnite (`upstream` remote, mirrored on `master`). Ours: `shane/main`.

## Layout

| path | what |
|---|---|
| `build.sh` | full solution build + copies BrowserProcess + writes `userdatadir.txt` |
| `source/Playnite/Settings/PlaynitePaths.cs` | patch: `userdatadir.txt` pins user data outside `bin/` |
| `source/Playnite.DesktopApp/ViewModels/DesktopAppViewModel.cs` | patch: `playnite://playnite/downloadmetadata[/all]` |
| `source/Playnite/Common.config` | dev config pointed at the real add-on API, online |
| `extensions/Ledger/` | our plugin: history, acquisitions, first-played, mods, Up next |
| `extensions/deploy.sh` | build our plugins → user data `Extensions/` (closes Playnite) |
| `extensions/render.sh` | deploy + start + render the Ledger page to PNG (UI check without clicking) |
| `extensions/Ledger/tests/run-tests.ps1` | offline tests for parsing/matching/KV (exit code = failures) |
| `tools/install-addons.py` | install community add-ons from the official API (newest SDK-compatible) |
| `tools/ledger.py` | query Ledger data: `stats`, `game`, `daily`, `backlog` (`--json` for hub/Bridge) |
| `tools/steam_local_stats.py` | read Steam's local achievement cache (first/last unlock per app) |

User data: `E:/projects/playnite-data` (never in git; it holds store tokens).
Startup: Playnite starts closed to tray at login (`StartOnBoot` → Startup\Playnite.lnk) so the tracker always runs.
Backups: `E:/projects/playnite-data-backups/` (manual tarballs + Playnite auto-backup in `auto/`).

## Rules
- Prefer a plugin in `extensions/` over patching `source/`. Patches make upstream merges hurt.
  Core patches so far are tiny and marked `shane/main` in a comment. Keep it that way.
- Counts are queries (`tools/ledger.py`, snapshot JSONL), never stored.
- Never overwrite data without a recorded way back (Ledger keeps `PreviousAdded`; Undo in menu).
- Back up `playnite-data` with Playnite **closed** (LiteDB files are locked while it runs).

## Ledger (extensions/Ledger)
Sidebar page **Ledger**, tabs: Overview (cards, insights, on this day, up next) · Backlog (shelves, quick wins,
up next + Pick for me, oldest unplayed) · Installs (drives, stale, installed-never-played) · Stats (genre, store,
decade, developer, how-you-got-it; period-aware) · Money (spend, in-game, saved, library value, £/h, money on the
shelf) · Timeline (play calendar heatmap + (started vs got, comfort/forgotten, most played) · Mods. Also + game menu **Ledger → History… / Mods…** + main menu **Extensions → Ledger**.

| feature | source of truth |
|---|---|
| Daily snapshots (`snapshots/yyyy-MM-dd.jsonl`) | Playnite DB on startup / after library update |
| Acquisition dates → Game.Added | Steam licenses page (all pages) + Epic order history (`ajaxGetOrderHistory`), both via Playnite's logged-in webview; one undo store for both |
| First played | earliest unlock in `Steam/appcache/stats/UserGameStats_<acct>_<app>.bin`: no key, no network |
| Timeline (started vs bought per year) | the two above |
| Up next / Pick for me | taste = playtime share per genre; + rating; + installed |
| Stale installs | installed, unplayed 180 days, by size |
| HowLongToBeat times | the HLTB add-on: Ledger reads its cache, fills gaps by calling its `AddData(Game)` (non-interactive; `Get()` opens a picker). HLTB set to UseMatchValue 90 |
| Tidy names | ™/® mangled to "?" (Epic) fixed only when a store record confirms it; log in `renames.json` |
| Filters | `FilterState` (period, multi-store, multi-genre, installed, played, paid, hours range, HLTB ≤, release-year range, search, family sharing): Filters panel, removable chips, persisted. Charts are crossfilters: clicking a slice/bar/row sets the filter |
| Saved views | Views dropdown: 6 built-ins + "Save view" (filter + tab) in settings |
| Explorer tab | sortable DataGrid of every game (hours, HLTB, done %, dates, how got, paid, £/h, GB, year, rating, genres) |
| Detail drawer | click any game anywhere: cover, Play/Install/Show in library, key numbers, HLTB progress bars, dated timeline (acquired, first/last played, status, sessions, purchases), mods, notes |
| Discover tab | Free now/next: Epic freeGamesPromotions (official) + GamerPower (Steam, GOG; third-party giveaways labelled), "already in your library" check, daily + notification for new freebies. Picks: SteamSpy tag profile of your top-25 Steam games (hours-weighted, generic tags dropped) → tag lists → shortlist 150 verified against their own top tags → cosine × reviews³ × popularity; "Because you like <tag>", "More like <game>"; owned anywhere excluded. Weekly, cached (state/steamspy, covers). Plus own unplayed by genre |
| Lists tab | user lists (lists.json): owned games or not-owned Steam apps, ordered, notes, emoji icon; "Play next" + "Want to buy" by default; Play next strip on Overview; drawer "＋ List" |
| Smart lists | ⚡ list = saved FilterState evaluated live, sort options, "Open as filter"; made from current filters |
| Goals | goals.json, yearly: finish N (status events), start N from backlog (first played), shrink unplayed pile (vs first snapshot of year), spend cap, hours cap; rings with pace on Overview |
| List export | Copy as Markdown; standalone HTML page with embedded covers (exports/) |
| Drag-to-reorder | tiles in normal lists (8px threshold so clicks still work) |
| Right-click menus | every game tile: Details, Play/Install, Show in library, Add to list ▸ (incl. New list…); Discover picks: Open on Steam, Add to list, Not interested (dismissed.json, excluded from picks) |
| Customise (⚙) | show/hide + reorder tabs, show/hide sections per tab (settings TabOrder/HiddenTabs/HiddenSections, keys "Tab/Section") |
| UI perf | tabs build lazily on first view; data loads off the UI thread; any render error is shown in-page (SafeShow) not thrown |
| Money: spend | Steam purchase history (logged-in webview, "load more" clicked via JS), attributed per game (DLC to base game, bundles split by list price, collections across franchise, refunds subtracted) + Epic order totals |
| Money: value | Steam store `appdetails?filters=price_overview` (public, batched 50, cached 7 days, region in settings) |
| Activity | background process watcher (60s, exe path under a game's install dir → session; skips Playnite-running games) + Playnite OnGameStopped + GameActivity + snapshot deltas; day hours = max(sessions, delta) |
| Event log (`events.jsonl`) | installs/uninstalls (size, drive), completion-status changes with dates, wishlist adds/removes (bought vs dropped), mod adds/removes per game, verdicts |
| Daily collect (`daily/*.json`, hourly check, once a day) | free space per drive, Steam wishlist (public API), prices for wishlist + unplayed (own price history) → sale alerts (50%+ or lowest seen after 14 days, once per sale) |
| Verdict prompt | marking Beaten/Completed/Abandoned asks 1–10 → UserScore and a one-line note → Notes (setting) |
| Snapshot compaction | full `YYYY-MM-DD.jsonl` weekly (Mon) + `YYYY-MM-DD.d.jsonl` changed rows; readers overlay (C# `Snapshots.ReconstructLatest`, py `states()`) |
| Wishlist tab | names via IStoreBrowseService/GetItems (public), on sale, owned on another store, waiting longest |
| Mods | Workshop `appworkshop_*.acf` (titles via public GetPublishedFileDetails, cached), Vortex staging dirs, r2modman `mods.yml` |

URIs (for scripts): `playnite://ledger/render/<tab|full>`, `hltb-fill[/all]`, `tidy-names`, `render-mods/<name>`, `snapshot`, `import-steam`, `import-epic`, `probe/<url>` (dev: dump what the logged-in browser sees).

## Emulation (2026-11-16)
- Emulators: portable in `E:\Emulators\<Name>` via `tools/install-emulators.py` (DuckStation, PPSSPP, Dolphin, Cemu,
  melonDS, mGBA, Azahar, Eden, RetroArch + chdman). PCSX2 in Program Files; RPCS3 in `E:\Emulators` root.
- Games: `E:\emulator_games\<system>` via `tools/organise-roms.py` (PS2 → CHD, PS1/PS2 by SYSTEM.CNF, PS3 extracted;
  originals moved to `_archives`, never deleted; resumable). Run it detached; a session sign-out kills child processes.
- Playnite: `playnite://ledger/setup-emulators` registers emulators (built-in definitions) + one scanner per system
  folder (in global update, so dropping a file in a folder + Update Library imports it).
  `playnite://playnite/updatelibrary/emulated` scans now. Tidy names turns Redump names into titles for matching.
- Ledger: emulated games show by platform (Origin = store or platform) in filters/stats/Explorer.

## Community add-ons (2026-11-16)
Metadata: IGDB, Universal Steam, PCGamingWiki, Extra Metadata Loader.
Tracking: GameActivity, SuccessStory **pinned to 3.7** (3.7.1 has Steam always-disabled bug:
upstream commit 90467e5 dropped `EnabledInSettings`; don't accept the update), HowLongToBeat,
CheckDlc, Year In Review. Library: DuplicateHider (auto, Steam > GOG > Epic > …; 47 hidden).
Mods: r2modman Launch Options, Nexus Mods checker.

Known add-on gaps. **Won't fix** (Shane, 2026-11-16: little Epic/GOG achievement or DLC use, EA unused):
- SuccessStory Steam unlocks need a Steam login *inside SuccessStory* (it scrapes the web profile);
  Ledger already gets unlock times locally, so this only matters for its achievement UI.
- SuccessStory / CheckDlc Epic + GOG: "not authenticated". EA linked-account import fails.

## Idea backlog (keep/kill is Shane's call)

Status: **built**, **try** (worth a spike), **park**, **blocked** (needs a decision).

1. **built** Daily snapshots.
2. **built** Steam acquisition dates (340 matched of 360 own Steam games).
3. **built** First played from Steam local cache (152 games, back to 2015).
4. **built** Mods index + per-game window (3,012 mods, 21 games).
5. **built** Up next + Pick for me.
6. **built** Metadata fill (covers missing 321 → 16; genres 735 → 44).
7. **park** (Shane, 2026-11-16) Phone view. Option A: `ledger.py --json` behind bridge-agent `/playnite/*`
   + a Bridge panel (cheap, fits the setup). Option B: Playnite Web add-on + its server container
   on the Pi. Both touch live infra, so neither was done unattended.
8. **built** Epic acquisition dates (194/213). GOG skipped: Shane doesn't use it much.
9. **try P2** Weekly digest to phone (estate-mcp): hours, new games, stale installs.
10. **try P3** Vortex mod sizes (dir walks are slow; do it async + cache).
11. **park** Rust/Tauri rewrite.

## Known caveats
- Epic doesn't report last-played: played Epic games count as "last played unknown", never stale/forgotten.
- Family-shared Steam games share the Steam PluginId; they have no licenses on your account.
- Prefix matching: names < 6 chars only match exactly; never across a digit (Fallout ≠ Fallout 76).
- A cluster of first-play dates on 2018-01-01/02 is probably a Steam sync artifact.
- "Median wait = same day" only covers games with achievements *and* a license date. Biased
  towards games played immediately.
