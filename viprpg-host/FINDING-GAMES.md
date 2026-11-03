# Finding the games the launcher can't download

`vip-hunt.csv` (in the launcher folder) lists every matsuri work the launcher has no download for: not in the uploader
archive, and no original link it can fetch (free itch.io, getuploader, Google Drive, Wayback Machine). Each copy you find and upload
here gets a ↓ in every launcher within about five minutes.

## The sheet

Open `vip-hunt.csv` in Excel (it's UTF-8, so the Japanese shows). Rows are sorted easiest first:

1. **Rows with a Wayback copy.** An original link pointed straight at a file, and the Wayback Machine saved it. Click
   it: it downloads the file itself.
2. **Rows with original links only.** Mostly Yahoo Box, OneDrive, MEGA, MediaFire, Geocities and fc2, nearly all dead.
3. **Rows with nothing to go on.** The matsuri listed no link.

| Column | Use |
|---|---|
| Save as | Where the zip goes under `viprpg-host\bucket` |
| Wayback copy | Direct download of the saved file; try first |
| Original links | What the matsuri listed, oldest first |
| Wiki | The work's VIPRPG wiki page; often has re-upload links in the comments |
| Matsuri page | The matsuri's archived site on fes.viprpg.org; sometimes has later links |
| Google | A search for the title, author and "VIPRPG" |
| Found? | Your notes: where you got it, or "gone" |

## Where to look, in order

1. **The Wayback copy**, if the row has one.
2. **The original links.** Dead ones can still have a saved copy: open `https://web.archive.org/web/*/<link>*` to list
   every snapshot under that address. For Yahoo Box, OneDrive and MEGA only the page was saved, never the file, so skip
   those.
3. **The wiki page**, including its comments, then **the matsuri page**.
4. **Search.** The Google link, then the title alone in Japanese with `ダウンロード`, `再配布` or `ふりーむ`. Many
   authors later moved their games to [ふりーむ](https://www.freem.ne.jp/), [itch.io](https://itch.io/) or a
   personal site.
5. **[uploader.viprpg.org](https://uploader.viprpg.org/)'s own search,** by title and by author. The launcher already
   matches this archive, but a file named oddly (`naitomea.zip`, `MQ4.zip`) can only be recognised by a person.
6. **People.** The author (many are on X under the same name), the current VIPRPG thread, or players who kept copies.

## Checking a find

- Open the zip: an RPG Maker 2000/2003 game has `RPG_RT.ldb` and `RPG_RT.lmt`; others have their own exe.
- Scan it with Windows Security before running it. Old uploads are mostly clean, but not all.
- Read the readme. If it says 転載禁止 (no re-uploading) or 再配布禁止, don't upload it; note that in **Found?**.

## Uploading

1. **Make it a zip.** The host only takes `.zip`. Unpack `.lzh`, `.rar`, `.7z` or `.exe` archives with 7-Zip and zip the
   folder again; Japanese file names are fine.
2. **Save it where the sheet says,** for example `viprpg-host\bucket\games\matsuri\2010-summer\70.zip`. The file name is
   the entry number (`007` can be `7.zip`); the title, author and engine come from the launcher's catalog.
3. **Upload,** from `viprpg-host` (the one-time R2 setup is in `README.md`):
   ```powershell
   .\upload.ps1 -Catalog "F:\ISO 2\MTL Madness\RPG Launcher\LauncherSource\Assets\vip-catalog.json" -DryRun
   .\upload.ps1 -Catalog "F:\ISO 2\MTL Madness\RPG Launcher\LauncherSource\Assets\vip-catalog.json"
   ```
   The first shows what would be sent. Red lines (a wrong matsuri id or entry number) stop the upload.
4. **Check it in the launcher:** press F5, and the work shows ↓.

Uploading puts other people's games on your server. Many VIPRPG authors are fine with that, but take a game down if its
author asks.
