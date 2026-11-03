# VIPRPG file host

Game zips for the RPG Launcher, served from a Cloudflare R2 bucket, with `manifest.json` listing them.

```
viprpg-host/
  build-manifest.ps1      makes bucket/manifest.json
  upload.ps1              rebuilds the manifest and uploads the bucket folder to R2
  games.csv               titles, authors, threads… (not uploaded)
  .manifest-cache.json    hash cache (not uploaded)
  bucket/                 mirrors the R2 bucket; everything here is uploaded
    manifest.json
    games/
      matsuri/<matsuri-id>/<No>.zip
      thread/…/<anything>.zip
```

R2 has no per-file limit that matters here (rclone uploads big files in parts) and no file-count limit. Downloads are free; storage is free up to 10 GB, then $0.015 per GB per month.

## One-time setup

1. **Turn on R2.** In the Cloudflare dashboard, open **R2 Object Storage** and subscribe. It's free, but Cloudflare asks for a payment method to cover use beyond the free tier.
2. **Create the bucket.** Choose **Create bucket** and name it `viprpg`. Under **Location**, choose **Provide a location hint → Asia-Pacific (APAC)**, since most players are in Japan. Leave **Jurisdiction** alone. The location can't be changed later.
3. **Make it public.** In the bucket, open **Settings → Public access**:
   - **Custom domain (recommended):** connect something like `files.yourdomain.com`. The domain has to be on Cloudflare. Downloads through it are cached by Cloudflare.
   - **No domain yet:** enable the **r2.dev subdomain**. It's rate-limited and meant for testing, but fine to start with.
4. **Make an API token.** In **R2 Object Storage → Manage API tokens → Create API token**, choose **Object Read & Write**, limited to the `viprpg` bucket. Keep the **Access Key ID**, **Secret Access Key** and the **S3 endpoint** (`https://<account-id>.r2.cloudflarestorage.com`) somewhere safe; the secret is shown only once.
5. **Install rclone,** then open a new terminal:
   ```powershell
   winget install Rclone.Rclone
   ```
6. **Give rclone the token.** Fill in your own values:
   ```powershell
   rclone config create r2 s3 provider=Cloudflare access_key_id=<id> secret_access_key=<secret> endpoint=https://<account-id>.r2.cloudflarestorage.com acl=private no_check_bucket=true
   ```
   Then check it can reach the bucket. No output and no error means it works; a token limited to one bucket can't list all buckets, so `rclone lsd r2:` fails even when this works:
   ```powershell
   rclone lsf r2:viprpg
   ```

## Where games go

- **Matsuri works:** `games/matsuri/<matsuri-id>/<No>.zip`. Use the id and entry number from the launcher's `vip-catalog.json`, for example `games/matsuri/2025-kouhaku/1.zip`. `1` matches `001`. The title, author and engine come from the catalog.
- **Thread games and everything else:** anywhere under `games/thread/`, with any subfolders (for example `games/thread/2008/0001.zip`). Use ASCII names; the Japanese goes in `games.csv`.
- **A matsuri work with no entry number:** put it under `games/thread/` and fill in its `matsuri` column in `games.csv`.

The `bucket` folder can live anywhere, for example on E:. Pass `-Bucket "E:\VIPRPG Host\bucket"` to both scripts.

## games.csv

One row per file under `games/thread/`, with columns `path,title,author,thread,year,engine,matsuri,no,note`. A row also works for a matsuri file, to override what the catalog says.

Each run adds a row for every new zip and guesses the title from `RPG_RT.ini`'s `GameTitle` or the zip's top folder. Open the CSV in Excel (it's UTF-8 with a BOM), fix and fill in the rows, save it as CSV UTF-8, and run the script again.

## Updating

```powershell
.\upload.ps1 -Catalog "F:\ISO 2\MTL Madness\RPG Launcher\LauncherSource\Assets\vip-catalog.json"
```

- The script rebuilds the manifest first. Warnings show in yellow (no title, an unknown matsuri id or number, a non-ASCII name); problems show in red and stop the upload.
- Only new or changed zips are uploaded. `manifest.json` goes up last, so it never lists a zip that isn't in R2 yet.
- `-DryRun` shows what would be sent without sending anything.
- Files deleted locally stay in R2 unless you pass `-Delete`.
- Zips are served as downloads and cached for a day; the manifest is cached for 5 minutes.

To build the manifest without uploading, run `.\build-manifest.ps1` with the same `-Catalog` argument.

## manifest.json

```json
{
  "version": 1,
  "generated": "2026-11-02T13:31:20Z",
  "count": 2,
  "games": [
    { "path": "games/matsuri/2025-kouhaku/1.zip", "size": 123, "sha256": "…",
      "source": "matsuri", "matsuri": "2025-kouhaku", "no": "001",
      "title": "…", "author": "…", "engine": "…" },
    { "path": "games/thread/2008/0002.zip", "size": 248, "sha256": "…",
      "source": "thread", "title": "魔王城", "author": "名無し", "thread": "ツクールスレ 123", "year": 2008 }
  ]
}
```

`path` is relative to the bucket's public URL, so the download is `https://files.yourdomain.com/<path>`. Fields with no value are left out. `no` uses the catalog's spelling, so the launcher can match it exactly.
