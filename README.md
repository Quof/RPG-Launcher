# RPG Launcher

A Windows and Linux launcher for RPG Maker, Wolf RPG Editor, Ren'Py, TyranoBuilder and Unity games that shows title screens as cards and fills in titles, tags and images from DLsite. 

...Or to be more honest, this is basically an HRPG launcher built around organizing DLsite games and checking new releases on the site more easily. The main feature of convenience is built-in network adapter + proxy settings to more easily check DLsite's page through a Japanese VPN without needing to constantly turn the VPN on or off. This launcher was completely vibecoded and while it works for me I really have no idea how any of the network stuff works, so go in with due caution and all that.

![RPG Launcher](docs/screenshot1.png)

## Quick start

1. Download the launcher from the releases and put it in the holder with your games (or anywhere).
2. Run it. It scans that folder up to three levels deep. You can add other folders by going to the options menu (the ... menu) and choosing "Add Directory."
3. Click a game to play. Press `F5` to refresh the page after adding new games.

## Supported engines

| Engine | Detected by |
| --- | --- |
| RPG Maker MV / MZ | `www\data\System.json` or `data\System.json` |
| RPG Maker VX Ace / VX / XP | `Game.ini` with `Data\` or a `.rgss3a` / `.rgss2a` / `.rgssad` archive |
| RPG Maker 2000 / 2003 | `RPG_RT.exe` / `RPG_RT.ldb` |
| Wolf RPG Editor | `Game.exe` with `Data.wolf`, `Data\*.wolf`, `Data\BasicData` or `GuruguruSMF4.dll` |
| Ren'Py | `renpy\` and `game\` beside the game's exe |
| TyranoBuilder / TyranoScript | `tyrano\` and `data\`, loose or in `package.nw`, `resources\app.asar` or the exe itself |
| Unity | `Name.exe` beside `Name_Data\` |

Encrypted MV/MZ images and RGSS archives are read directly. Wolf games use the folder name as title until DLsite supplies one.

- **Ren'Py** titles come from `game\options.rpy`, else from the game's `log.txt` (once it has been played) or its Mac app; the card picture is `game\gui\main_menu.png` or the loading screen.
- **TyranoBuilder / TyranoScript** titles come from `data\system\Config.tjs`, and the card picture is the title scenario's background, read from inside the exe or package when the game is packed.
- **Unity** titles come from `Name_Data\app.info`. Unity games keep their pictures packed away, so their cards show the DLsite image or a placeholder.
- A game shipped as 32- and 64-bit builds side by side (`Windows_32bit`, `Windows_64bit`) is listed once, as the 64-bit build.

## Features

- **Sort** by title, folder, rating, last or most played, release date or download date.
- **Filter** by engine, group, favorites, tags, or games without a DLsite page. **Search** covers titles, folder names, circles and tags in English and Japanese.
- **Ratings, favorites, play count and play time.**
- **Groups** — right-click a card → **Groups**. Groups appear in the sort menu; drag them there to reorder.
- **Tags** — click a tag once to show only games with it, twice to hide them, a third time to clear it. Right-click a tag to pin it. Right-click a card → **Add Tags…** for your own tags.
- **Names and pictures** — cards use the DLsite title and image, or the game's cleaned title and title screen. Right-click a card to rename it, change or reframe its picture, or choose title screen or DLsite image.
- **New on DLsite** — games released in the last 30 days, with price, discount, rating and tags.
- **Display** — four styles, rectangular or square cards, optional paging, English or Japanese metadata. On screens of 2560×1440 or more the window opens at 1920×1080.
- **Cheats** — right-click an MV or MZ game → **Play with Cheats** to change gold, levels, EXP, HP, MP and stats while playing, heal the party, walk faster or walk through walls. **Advanced…** edits variables, switches and items, runs and replays events, opens the save and load screens anywhere, and has a script console. Other RPG Maker games get **Play in Test Mode**. See [Cheats](#cheats).

| Key | Action |
| --- | --- |
| `F5` | Rescan (New on DLsite: refresh today) |
| `Ctrl+F` / `Esc` | Search / clear search |
| `Ctrl+PageUp` / `Ctrl+PageDown` | Previous / next page |

## DLsite

- Each game is looked up once, by the RJ code in its folder name or by searching its title. Japanese and English data are cached, so switching language needs no network. Only search terms and product codes are sent.
- A miss is remembered, not searched again on every launch. Right-click → **Look up metadata** retries; **Set DLsite page…** takes a link or RJ code.
- New on DLsite caches each day's releases for 30 days.
- Requests to DLsite pages are at least a second apart. If DLsite answers "too many requests", the launcher pauses and stops its queues.
- **⋯ → DLsite connection…** sends DLsite requests through a chosen network adapter (such as a VPN) or a SOCKS/HTTP proxy. If that route is unavailable, requests fail rather than go direct. **Test** shows which country DLsite sees. Proxy settings are saved encrypted (Windows DPAPI), readable only by your Windows account.

## Cheats

- **Play with Cheats** (MV, MZ) opens a cheat window beside the game. Values update live; a field you're editing is kept until you press **Apply**. Stats stop at the game's own caps. Faster walking (up to 4×) leaves cutscenes at their own pace; walking through walls also stops random battles, as in a playtest. Both last until the game closes. Other changes are in the running game, so save in the game to keep them.
- **Advanced…** in the cheat window opens a second window with these tabs:
  - **Variables** and **Switches** — every one the game has, by ID and name. Click a value to edit it (a whole number, text, or JSON such as `[1,2]`); click ON/OFF to flip a switch. The filter matches names or an exact ID.
  - **Items** — how many of each item, weapon and armor the party holds, up to the game's maximum.
  - **Events** — **Run** a common event or an event on the current map. **Replay** runs any page of a map event again, whatever its conditions. **Reset** turns its self switches off, so a one-time event can happen again. Events run on the map, once no other event is running; MZ games pause while their window isn't active, so click the game to see it.
  - **Saves** — opens the game's own save or load screen, even where the game has turned saving off, and can turn the menu's Save command back on.
  - **Console** — runs JavaScript in the game (Ctrl+Enter) and shows the result, like the game's developer console.
- To connect, the launcher appends a short script to the game's `js/plugins.js` and removes it as soon as the game has loaded it. The file is restored byte for byte, with its original date. The script only answers the launcher (a one-time token over a local port). A copy left behind by a crash stops working after 5 minutes and is removed the next time the game is played.
- **Play in Test Mode** (VX Ace, VX, XP, 2000/2003) starts the engine's own test play: hold Ctrl to walk through walls; F9 opens the switch and variable editor.
- Wolf RPG Editor, Ren'Py, TyranoBuilder and Unity games have no cheats.

## Linux

- The launcher is a native Linux program. Unpack the `.tar.gz` (it keeps the file executable) and run `RPG Launcher`; it needs X11 or XWayland and the ICU library (`libicu`), which desktop distributions include.
- Windows games start through Wine (`wine` on the path). Ren'Py games that include a Linux build start natively with their own `Name.sh` (when that build is marked executable), as do Linux builds of Unity games. **⋯ → Wine command…** sets another, such as `flatpak run org.winehq.Wine`. Games get a Japanese locale (`LANG=ja_JP.UTF-8`) so their text shows; `env LANG=… wine` overrides it.
- Cheats and test mode work the same through Wine.
- Proxy settings are encrypted with a key only your user can read, in `~/.config/rpg-launcher`.
- Sending DLsite requests through a network adapter needs Linux 5.7 or newer.

## Data

Stored next to the exe:

| Path | Contents |
| --- | --- |
| `launcher-data.json` | Ratings, groups, tags, play time, metadata, settings |
| `launcher-library.json`, `launcher-thumbnails\` | Scan cache (safe to delete) |
| `launcher-images\` | Custom and DLsite card images |
| `launcher-dlsite\` | New-release cache |

Delete them all to reset. If `launcher-data.json` can't be read, a copy is kept as `launcher-data.json.broken`.

## Building

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). The interface uses [Avalonia](https://avaloniaui.net/).

```powershell
.\build.ps1                       # Windows: self-contained single-file exe
.\build.ps1 -Runtime linux-x64    # Linux: single file in LauncherSource\bin\publish\linux-x64
.\build.ps1 -FrameworkDependent   # smaller; needs the .NET 8 runtime
.\build.ps1 -NoCompression        # larger, faster startup
```

Without PowerShell: `dotnet publish LauncherSource/RPGLauncher.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`.

## Troubleshooting

- **SmartScreen warning** — the exe is unsigned: **More info → Run anyway**.
- **Game missing** — check the engine table and folder depth, then press `F5`.
- **No metadata** — sort menu → **No DLsite Page** lists these games; use **Set DLsite page…**.
- **Linux: a game doesn't start** — check that `wine` runs it from a terminal, or set **⋯ → Wine command…**.
- **Crash** — see `%TEMP%\rpglauncher.log` (Linux: `/tmp/rpglauncher.log`).
