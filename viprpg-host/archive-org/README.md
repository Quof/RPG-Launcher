# Uploading the archive to archive.org

A second home for every game, independent of Cloudflare and of this PC. WOLF RPG Editor contest games are never included: the build reads the bucket's `manifest.json`, which leaves them out.

## What gets uploaded

| archive.org item | Contents | Size |
|---|---|---|
| `viprpg-archive-festivals` | one folder per festival (`2015-kouhaku VIPRPG紅白2015/No.075 タイトル.zip`), every entry as its own zip | about 89 GB, 3,620 games |
| `viprpg-archive-storage` | `保管庫/` the storage archives 1–15 and `その他 (other uploaders)/` the other uploaders, one zip each, the games inside named `storage-number title.zip` | about 50 GB, 31,905 games in 63 zips |

Each item also has `index.csv` (every game: where it is in the item, title, author, festival and number, size, SHA-256, original file name) and `README.txt` (Japanese and English, with the website, the launcher and the removal contact).

## 1. Build (or rebuild after adding games)

```powershell
python "F:\ISO 2\MTL Madness\RPG Launcher\viprpg-host\archive-org\build-archive-org.py"
```

It writes `E:\VIPRPG Host\archive-org\`: the two item folders and their `*.metadata.json`. Festival files are hard links to the bucket's (no extra space); the storage zips are new files (about 50 GB). Run `build-manifest.ps1` first if the bucket changed.

## 2. Install archive.org's tool and sign in (once)

```powershell
pip install internetarchive
ia configure
```

`ia configure` asks for your archive.org email and password and saves a key for the tool. It's your account, so these items are yours to edit or take down later.

## 3. Upload

```powershell
python "F:\ISO 2\MTL Madness\RPG Launcher\viprpg-host\archive-org\upload-to-archive-org.py" viprpg-archive-festivals
python "F:\ISO 2\MTL Madness\RPG Launcher\viprpg-host\archive-org\upload-to-archive-org.py" viprpg-archive-storage
```

- 139 GB takes a long time (archive.org often accepts around 1–5 MB/s per upload). If it stops — a closed window, a network drop, archive.org busy — run the same command again: files already there with the same MD5 are skipped.
- The item page shows up within minutes of the first file; the rest appear as they arrive. archive.org may take a day or so to finish processing a large item.
- Nothing is "derived" (archive.org doesn't try to convert the zips), which keeps the upload fast.

## Later updates

After adding games: `build-manifest.ps1`, then `build-archive-org.py`, then the same upload commands. Only new or changed files are sent (a storage zip whose games changed is sent again as a whole).

## Things to know

- The items are public and permanent in practice: archive.org keeps a history even of deleted files, and copies spread. Removal requests from authors would also need handling there (archive.org's item editor can delete files).
- The README and the item description both mention the R-18/R-18G content.
