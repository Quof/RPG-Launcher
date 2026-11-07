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
3. **Keep it private.** Leave **Settings → Public access** off: no r2.dev subdomain and no custom domain on the bucket. Players download through the Worker instead (see [Download Worker](#download-worker)), which limits each IP and can't cost more than the free allowance. A public bucket bills every download past 10 million a month, with no ceiling.
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
- **WOLF RPG Editor contest entries (ウディコン):** `games/wolf/<contest NN>/<entry NN> - <title>.zip`, with rows whose `area` is `wodicon<NN>` and `section` is `ウディコン`. They are **withheld**: kept in the bucket for preservation, but `build-manifest.ps1 -Withheld` (default `games/wolf/`) leaves them out of the manifest and the download Worker refuses every path under `games/wolf/`, so no launcher lists or downloads them. Their games.csv rows stay. They're prepared in `E:\WolfRPG\_bucket` and copied in with its `merge-into-bucket.py`.

The `bucket` folder can live anywhere, for example on E:. Pass `-Bucket "E:\VIPRPG Host\bucket"` to both scripts.

## games.csv

One row per file under `games/thread/`, with columns `path,title,author,thread,year,engine,matsuri,no,note,area,section`. `area` and `section` group thread games in the launcher's 保管庫 list: games with the same `area` are one entry there, named by their `thread`, under the heading their `section` gives (`保存库` shows as 保管庫). Section `その他` is the Other heading: unique games from the old uploaders and the wiki, staged at `games/thread/other/<area>/`. Once the bucket has any of those, the launcher stops listing the uploader archive's own copies (archive.org), since every game there is either in その他 or a copy of a matsuri or 保管庫 game. An area with fewer than 3 games is hidden in the launcher, so small uploaders share the area `small-uploaders`. A row also works for a matsuri file, to override what the catalog says.

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

After uploading, `upload.ps1` also publishes the index website (next section), since the launcher reads its list from there; `-NoSite` skips that.

## Index website

`https://viprpg-archive.com` is the archive's index: a page per festival and per 保管庫, search, the work pictures, and a copy of `manifest.json`. It is static files on Cloudflare (Workers static assets, the current form of Pages), which are free and have no request limit, so browsing and launcher starts never touch the bucket or the download Worker. Its download buttons point at the Worker, which keeps its per-IP limits.

```powershell
.\deploy-site.ps1                # build site-out from E:\VIPRPG Host\bucket\manifest.json and the catalog, then publish
.\deploy-site.ps1 -BuildOnly     # build only (preview with the viprpg-site-local server in .claude/launch.json)
.\deploy-site.ps1 -NoDownloads   # the works without download buttons
```

- The generator is `site\build-site.py`; its settings are `site\wrangler.toml`. Pictures come from the launcher's `vip-thumbs-*.zip`; withheld (WOLF) games are left out.
- The launcher fetches `manifest.json` from the site first and falls back to the Worker. Never publish the site before the zips its manifest lists are uploaded; `upload.ps1` does it in that order.
- The Worker answers `robots.txt` with `Disallow: /`, so search engines that index the site don't follow its download links.

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

`path` is relative to the Worker's address, so the download is `https://dl.viprpg-archive.com/<path>`. Fields with no value are left out. `no` uses the catalog's spelling, so the launcher can match it exactly.

## Download Worker

`worker/` is a Cloudflare Worker that serves the private bucket to the launcher. It answers only `GET`/`HEAD` of `manifest.json` and files under `games/`; anything else is a 404, and the bucket can't be listed or written through it. For each IP address:

- **More than 30 requests in 10 seconds** blocks that IP for 10 minutes (`429`, with `Retry-After`).
- **More than 500 different game files in a UTC day** refuses further downloads from it until the next day. Downloading the same file again that day (after a failed or stopped try) doesn't count again, and the manifest doesn't count.
- Every answer carries the IP's usage (`X-Downloads-Used`, `X-Downloads-Limit`, `X-Downloads-Reset` in seconds), and a 429's JSON says which limit (`"limit": "daily"` or `"burst"`). The launcher uses them to warn when 50 or fewer downloads are left, and to explain a refusal.

The limits are the `[vars]` in `worker/wrangler.toml`. Each IP's count lives in a small Durable Object that deletes itself at the end of the day. If the counter can't answer, downloads are refused (`503`) rather than served without limits.

**Why it can't cost more:** keep the account on the **free Workers plan**. Past its daily request limit (100,000 a day when this was written), Cloudflare refuses requests instead of billing, which keeps downloads under R2's free 10 million reads a month. The worst an attacker can do is use up a day's requests, so downloads stop until the daily reset. Upgrading to Workers Paid removes that ceiling, so don't. Check Cloudflare's current Workers, Durable Objects and R2 limits before relying on these numbers.

### Deploying

You need [Node.js](https://nodejs.org) (LTS) for Cloudflare's `wrangler` tool:

```powershell
winget install OpenJS.NodeJS.LTS
```

Then, in a new terminal:

```powershell
cd viprpg-host\worker
npm install
npx wrangler login
npx wrangler deploy
```

`wrangler login` opens the browser to sign in to your Cloudflare account; no key goes in any file. `deploy` prints the Worker's address: `https://dl.viprpg-archive.com` (the `routes` in `wrangler.toml`; the workers.dev address is turned off). Check it with `<address>/manifest.json`. The launcher's address is `ServerAddress` in `LauncherSource/VipView.cs`.

### Domain and firewall rule

Requests the Worker blocks still count toward the free daily limit. A Cloudflare firewall rule runs before the Worker, so requests it blocks don't count:

1. Done: the Worker is on `dl.viprpg-archive.com` (`routes` in `wrangler.toml`, with `workers_dev = false`), and the index site on `viprpg-archive.com` and `www.viprpg-archive.com` (`site/wrangler.toml`).
2. In the domain's dashboard, open **Security → WAF → Rate limiting rules** and create a rule: hostname equals `dl.viprpg-archive.com`, **30 requests per 10 seconds** per IP, action **Block** for 10 minutes. The free plan allows one such rule; the shortest period it allows may be longer than 10 seconds, so use the closest one. The Worker's own limit stays as a backstop.
3. Turn on **Security → Bots → Bot Fight Mode**.
