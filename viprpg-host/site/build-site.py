"""Builds the VIPRPG archive's index website (static files on Cloudflare) from the bucket's manifest and the launcher's catalog.

The site is the index only: festival pages, 保管庫 pages, search and the work pictures, plus a copy of manifest.json
for the launcher. Every download button points at the download Worker, which keeps its per-IP limits, so the bucket is
only touched when someone actually downloads a game.

    python viprpg-host/site/build-site.py [--bucket "E:/VIPRPG Host/bucket"] [--out viprpg-host/site-out] [--no-downloads]

Then publish it with deploy-site.ps1 (wrangler deploy with site/wrangler.toml: static files, no request limit).
"""
import argparse
import gzip
import html
import json
import os
import re
import shutil
import sys
import urllib.parse
import zipfile
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
ASSETS = os.path.join(REPO, "LauncherSource", "Assets")
WORKER = "https://dl.viprpg-archive.com"
# Kept in the bucket for preservation, never listed (the Worker refuses them too).
WITHHELD = ("games/wolf/",)

SPECIAL_MONTHS = {
    "2024-kuradashifestival": 10, "2023-winter": 13, "2018-gw-2": 4, "2016-summer-2": 7,
    "2014-tsukusure-festival": 10, "2012-toppuu-festival": 10, "2012-magic-festival": 3,
    "2011-toppatsu-festival": 10, "2011-denpa-game-festival": 6, "2009-silver-week-miruge-festival": 9,
    "2009-ksgfestival": 3, "2008-jisaku-battle-festival": 11, "2008-seiyoku-festival": 10,
    "2008-chuuni-festival": 3, "2007-default-battle-festival": 6,
}


def esc(text):
    return html.escape(str(text or ""), quote=True)


def month(m):
    """When in its year a festival was held, as the launcher orders them (VipMatsuri.Month)."""
    start = re.match(r"^\d{4}/(\d{1,2})/", m.get("period") or "")
    if start:
        return int(start.group(1))
    if m["id"] in SPECIAL_MONTHS:
        return SPECIAL_MONTHS[m["id"]]
    text = f"{m.get('type') or ''} {m['id']} {m['name']}"
    if re.search("kouhaku|紅白", text, re.I):
        return 12
    if re.search("summer|夏", text, re.I):
        return 8
    if re.search("gw", text, re.I):
        return 5
    return 0


def short_name(name):
    rest = re.sub(r"^\s*VIPRPG(?:\s*ツクール)?[\s　-]*", "", name).strip()
    return rest or name


def work_key(matsuri, no, title):
    if not matsuri:
        return None
    if no and no.strip():
        return f"{matsuri.strip()}/{no.strip()}".lower()
    return f"{matsuri.strip()}/#{title.strip()}".lower() if title else None


def size_text(n):
    if not n:
        return ""
    for unit, scale in (("GB", 1 << 30), ("MB", 1 << 20), ("KB", 1 << 10)):
        if n >= scale:
            return f"{n / scale:.1f} {unit}"
    return f"{n} B"


def dl_url(path):
    return WORKER + "/" + "/".join(urllib.parse.quote(p) for p in path.split("/"))


def bi(ja, en):
    """Text in both languages; the page shows one (the 日本語 / English switch)."""
    return f'<span class="ja">{ja}</span><span class="en">{en}</span>'


def slug(text):
    return re.sub(r"[^A-Za-z0-9_-]+", "-", text).strip("-").lower() or "x"


# --- page shell ---

FONTS = ('<link rel="preconnect" href="https://fonts.googleapis.com"><link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>'
         '<link href="https://fonts.googleapis.com/css2?family=DotGothic16&family=Noto+Sans+JP:wght@400;700&display=swap" rel="stylesheet">')


def page(title, body, depth=0, description="", extra_head="", root=None):
    up = root if root is not None else "../" * depth
    return f"""<!doctype html>
<html lang="ja" data-lang="ja">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{esc(title)}</title>
<meta name="description" content="{esc(description or 'VIPRPGの祭り作品と保管庫のアーカイブ。')}">
{FONTS}
<link rel="stylesheet" href="{up}style.css">
<link rel="icon" href="{up}favicon.svg" type="image/svg+xml">
{extra_head}
<script>try{{var l=localStorage.getItem("lang");if(l)document.documentElement.dataset.lang=l;}}catch(e){{}}</script>
</head>
<body>
<header class="top">
  <a class="brand" href="{up}index.html">VIPRPG <span>アーカイブ</span></a>
  <nav>
    <a href="{up}index.html">{bi("祭り", "Festivals")}</a>
    <a href="{up}storage.html">{bi("保管庫", "Storage")}</a>
    <a href="{up}search.html">{bi("検索", "Search")}</a>
    <a href="{up}about.html">{bi("サイトについて", "About")}</a>
    <button class="lang" type="button" onclick="setLang()" aria-label="Language">{bi("English", "日本語")}</button>
  </nav>
</header>
<main>
{body}
</main>
<footer>
  <p>{bi("ダウンロードはIPアドレスごとに1日500ファイルまで。作品の権利はそれぞれの作者にあります。",
          "Downloads are limited to 500 files per IP address a day. Every work belongs to its author.")}</p>
</footer>
<script>
function setLang(){{var d=document.documentElement;d.dataset.lang=d.dataset.lang==="en"?"ja":"en";try{{localStorage.setItem("lang",d.dataset.lang);}}catch(e){{}}}}
</script>
</body>
</html>
"""


CSS = """:root{
  --bg:#0b1640; --bg2:#13235e; --win:#1d3a7a; --win2:#14295c; --edge:#c9d8ff; --ink:#f2f5ff; --dim:#a9b6dc;
  --accent:#ffd84a; --link:#9fd0ff; --ok:#7be3a0; --bad:#ff8f8f; --card:#162b63; --shadow:0 2px 0 #050b26;
  color-scheme: dark;
}
*{box-sizing:border-box}
html,body{margin:0}
body{background:linear-gradient(180deg,var(--bg) 0,var(--bg2) 420px,var(--bg) 100%);background-attachment:fixed;color:var(--ink);
  font:15px/1.6 "Noto Sans JP",system-ui,sans-serif;min-height:100vh}
a{color:var(--link);text-decoration:none} a:hover{text-decoration:underline}
html[data-lang="ja"] .en, html[data-lang="en"] .ja{display:none}
.top{display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap;padding:14px 20px;
  border-bottom:2px solid #2c4aa0;background:rgba(5,11,38,.55);position:sticky;top:0;z-index:5;backdrop-filter:blur(6px)}
.brand{font-family:"DotGothic16",monospace;font-size:24px;color:#bcd6ff;letter-spacing:1px}
.brand span{color:var(--accent)} .brand:hover{text-decoration:none}
nav{display:flex;gap:6px;align-items:center;flex-wrap:wrap}
nav a,.lang{font-family:"DotGothic16",monospace;font-size:15px;color:var(--ink);padding:4px 12px;border:2px solid #6f8fe0;border-radius:4px;
  background:var(--win2);cursor:pointer}
nav a:hover,.lang:hover{border-color:var(--edge);text-decoration:none}
main{max-width:1180px;margin:0 auto;padding:20px 16px 40px}
footer{max-width:1180px;margin:0 auto;padding:16px;color:var(--dim);font-size:13px;border-top:1px solid #2c4aa0}
h1,h2,h3{font-family:"DotGothic16",monospace;font-weight:400;line-height:1.3}
h1{font-size:30px;margin:6px 0 4px} h2{font-size:22px;margin:28px 0 10px;color:var(--accent)} h3{font-size:17px;margin:0}
.window{background:linear-gradient(180deg,var(--win),var(--win2));border:3px solid var(--edge);border-radius:6px;box-shadow:var(--shadow),inset 0 0 0 2px #0a1745;padding:16px 18px}
.lede{color:var(--dim);margin:0 0 6px}
.stats{display:flex;gap:10px;flex-wrap:wrap;margin:14px 0 0}
.stat{background:#0d1d4d;border:2px solid #3b5bb5;border-radius:4px;padding:6px 12px;font-family:"DotGothic16",monospace}
.stat b{color:var(--accent);font-weight:400;font-size:20px;margin-right:4px}
.links{display:flex;gap:8px;flex-wrap:wrap;margin-top:10px}
.btn{display:inline-block;font-family:"DotGothic16",monospace;font-size:14px;padding:3px 10px;border:2px solid #6f8fe0;border-radius:4px;background:#0f2257;color:var(--ink)}
.btn:hover{border-color:var(--edge);text-decoration:none}
.btn.dl{border-color:var(--accent);color:var(--accent)}
.year{display:grid;grid-template-columns:repeat(auto-fill,minmax(250px,1fr));gap:12px}
.fest{display:flex;flex-direction:column;gap:8px;padding:10px;background:var(--card);border:2px solid #34539f;border-radius:6px;color:var(--ink)}
.fest:hover{border-color:var(--edge);text-decoration:none}
.fest .banner{display:flex;align-items:center;justify-content:center;aspect-ratio:5/1;background:#050b26;border-radius:3px;overflow:hidden}
.fest .banner img{max-width:100%;max-height:100%;object-fit:contain;image-rendering:pixelated}
.fest .banner.plate{font-family:"DotGothic16",monospace;color:#6f8fe0;font-size:15px;padding:0 8px;text-align:center;
  background:repeating-linear-gradient(45deg,#08143d 0 6px,#0b1a4a 6px 12px);border:1px dashed #34539f}
.banners{display:flex;gap:8px;flex-wrap:wrap;margin:4px 0 8px}
.banners img{height:40px;width:auto;max-width:100%;image-rendering:pixelated;border-radius:2px}
.fest .name{font-family:"DotGothic16",monospace;font-size:16px;line-height:1.3}
.fest .count{color:var(--dim);font-size:13px}
.works{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:14px;margin-top:16px}
.work{background:var(--card);border:2px solid #34539f;border-radius:6px;overflow:hidden;display:flex;flex-direction:column;scroll-margin-top:80px}
.work:target{border-color:var(--accent);box-shadow:0 0 0 3px rgba(255,216,74,.35)}
.work .pic{aspect-ratio:4/3;background:#050b26;display:flex;align-items:center;justify-content:center;color:#5d6d9c;font-family:"DotGothic16",monospace}
.work .pic img{width:100%;height:100%;object-fit:contain;image-rendering:pixelated}
.work .body{padding:10px 12px;display:flex;flex-direction:column;gap:4px;flex:1}
.work .no{font-family:"DotGothic16",monospace;color:var(--accent);font-size:13px}
.work .title{font-weight:700;line-height:1.4;overflow-wrap:anywhere}
.work .meta{color:var(--dim);font-size:13px;overflow-wrap:anywhere}
.work .links{margin-top:auto;padding-top:6px}
.tag{font-size:12px;border-radius:3px;padding:0 6px;margin-left:4px;font-family:"DotGothic16",monospace;vertical-align:1px}
.tag.lost{background:#4a1d2b;color:var(--bad)} .tag.eternal{background:#2b2f4a;color:#b9b9d8}
.work.lost .pic,.work.eternal .pic{opacity:.55}
.toolbar{display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin:14px 0 0}
input[type=search]{font:inherit;color:var(--ink);background:#081440;border:2px solid #6f8fe0;border-radius:4px;padding:6px 10px;min-width:0;width:min(420px,100%)}
input[type=search]:focus{outline:none;border-color:var(--accent)}
table{width:100%;border-collapse:collapse;margin-top:12px;font-size:14px}
th,td{text-align:left;padding:6px 8px;border-bottom:1px solid #2c4aa0;vertical-align:top}
th{font-family:"DotGothic16",monospace;font-weight:400;color:var(--accent);position:sticky;top:64px;background:#0f2257}
td.t{overflow-wrap:anywhere} td.s{white-space:nowrap;color:var(--dim)} td.d{white-space:nowrap}
tr:target td{background:rgba(255,216,74,.15)}
.areas{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:10px}
.area{padding:10px 12px;background:var(--card);border:2px solid #34539f;border-radius:6px;color:var(--ink)}
.area:hover{border-color:var(--edge);text-decoration:none}
.area .count{color:var(--dim);font-size:13px}
.results{list-style:none;padding:0;margin:12px 0}
.results li{padding:8px 10px;border-bottom:1px solid #2c4aa0}
.results .where{color:var(--dim);font-size:13px}
.muted{color:var(--dim)}
.about{max-width:820px;margin:0 auto}
.about p{margin:0 0 14px;line-height:1.85}
.about .callout{background:#0d1d4d;border:2px solid var(--accent);border-radius:4px;padding:10px 14px;color:#fff3c4}
.about .contact{margin-top:18px;font-family:"DotGothic16",monospace;color:var(--dim)}
@media (max-width:600px){
  .brand{font-size:20px} h1{font-size:24px} .works{grid-template-columns:1fr 1fr;gap:8px}
  .work .body{padding:8px} .work .meta{font-size:12px} th{top:0} .hide-sm{display:none}
}
@media (max-width:380px){.works{grid-template-columns:1fr}}
"""

FAVICON = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" shape-rendering="crispEdges">
<rect width="16" height="16" rx="2" fill="#13235e"/><rect x="2" y="2" width="12" height="12" fill="none" stroke="#c9d8ff"/>
<rect x="5" y="5" width="2" height="6" fill="#ffd84a"/><rect x="9" y="5" width="2" height="6" fill="#ffd84a"/><rect x="7" y="9" width="2" height="2" fill="#ffd84a"/>
</svg>"""


CONTACT = "dekirutranslations@gmail.com"

ABOUT = f"""<section class="window about">
<h1>{bi("このサイトについて", "About")}</h1>

<div class="ja">
<p class="callout">このサイトに掲載されていないVIPRPGをお持ちの方は、ぜひお送りいただけますと大変助かります。アップロードしてアーカイブに追加いたします。</p>

<p>このサイトは、すべてのVIPRPGをひとつのサイトに集めることを目指したものです。作品がそれぞれ別々の不安定なサイトに置かれているために、時が経つにつれて失われていくことを少しでも防ぐのが目的です。ファイルはすべて、私が個人で費用を負担しているCloudflareのサーバーに保存しています。現時点ではサーバー代は問題になっておらず、万一必要になった場合でも、コミュニティの皆さまのご協力で十分にまかなえる程度の金額です。</p>

<p>このサイトの裏側は、私が開発した「RPG Launcher」とつながっています。もしもローダーのようなプログラムに作品を直接ダウンロードできるもので、こちらのほうがずっと便利ですが、このサイトから直接ダウンロードしていただくこともできます。ダウンロードには、お一人につき1日500件までの制限と、サイト全体で月1,000万件までの上限があります（それを超えると、新しいダウンロードに追加の費用がかかるためです）。</p>

<p>現在掲載しているファイルは、数週間かけてarchive.orgを調べ、ダウンロードできるものを集めた、できる限りのものです。ただ、アメリカからでは地域制限で見られないサイトがいくつもあったことや、多くのリンクがすでに失われていたことから、すべてを集めることはできませんでした。改めてのお願いになりますが、このサイトにないVIPRPGをお持ちの方は、ぜひお送りください。アップロードしてアーカイブに追加いたします。</p>

<p>作者の方で、ご自身の作品をこのアーカイブから<strong>削除</strong>してほしい場合は、<a href="mailto:{CONTACT}">こちら</a>までご連絡ください。他人になりすまして作品を削除させようとする方もいるかもしれませんので、ご本人であることを確認できるものをご用意いただけますと幸いです。</p>

<p>最後に、ダウンロードした作品によって生じたいかなる結果についても、当サイトは責任を負いかねます。ファイルはVIPRPGのwikiや各祭りのサイトからそのまま集めたもので、一つひとつの動作確認や検証は行っておりません。問題のあるファイルがございましたら、ご連絡いただければ確認いたします。</p>
</div>

<div class="en">
<p class="callout"><em>If you have a VIPRPG not on this website, it would be of great help to send it to me so it can be uploaded and added to the archive!</em></p>

<p>This website is an attempt to gather all VIPRPGs onto a single website in order to better combat games being lost over time due to being hosted on separate, unstable websites. These files are all hosted on a Cloudflare server that I'm paying for out of pocket; right now hosting fees are not a concern, and they're low enough that should the time come the community would easily be able to help pay for them.</p>

<p>The backend of this website is linked to an RPG Launcher I've developed that allows you to download the games directly into a Moshimo Loader-esque program, which is the significantly more convenient option, but this website is also available for direct downloading. There is a rate limit of 500 downloads per user per day, and a hard limit of 10,000,000 downloads overall per month (because that's when I need to start paying extra for new downloads).</p>

<p>The files on the website now are the best I could gather after weeks of effort digging through archive.org and downloading what I can, but my efforts were stymied by being region-blocked in America from various websites, and by the fact that several links have, in the time since, been lost. As stated previously, if you have a VIPRPG not on this website, it would be of great help to send it to me so it can be uploaded and added to the archive.</p>

<p>If you are a developer and would like your game <strong>REMOVED</strong> from this archive, please contact me <a href="mailto:{CONTACT}">here</a>. Please also be ready to provide some manner of proof of identity, because there may be bad actors pretending to be others just to get their game taken down.</p>

<p>And finally, this website takes no responsibility for any consequences of anything the games downloaded do. These are taken cleanly from the VIPRPG wiki and matsuri sites and have not been individually tested or verified. If there are any problem files, please contact me and they will be investigated.</p>
</div>

<p class="contact">{bi("連絡先", "Contact")}: <a href="mailto:{CONTACT}">{CONTACT}</a></p>
</section>"""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bucket", default="E:/VIPRPG Host/bucket")
    ap.add_argument("--out", default=os.path.join(REPO, "viprpg-host", "site-out"))
    ap.add_argument("--no-downloads", action="store_true", help="list the works without download buttons")
    args = ap.parse_args()
    downloads = not args.no_downloads

    catalog = json.load(open(os.path.join(ASSETS, "vip-catalog.json"), encoding="utf-8"))
    manifest_path = os.path.join(args.bucket, "manifest.json")
    manifest = json.load(open(manifest_path, encoding="utf-8"))
    games = [g for g in manifest["games"] if not g["path"].lower().startswith(WITHHELD)]

    out = args.out
    if os.path.isdir(out):
        shutil.rmtree(out)
    for sub in ("m", "s", "img"):
        os.makedirs(os.path.join(out, sub), exist_ok=True)

    # Pictures: everything the launcher has, but the withheld WOLF entries.
    pictures = set()
    for part in sorted(n for n in os.listdir(ASSETS) if re.match(r"vip-thumbs-\d+\.zip$", n)):
        with zipfile.ZipFile(os.path.join(ASSETS, part)) as z:
            for info in z.infolist():
                if info.is_dir() or info.filename.startswith("wolf/"):
                    continue
                target = os.path.join(out, "img", *info.filename.split("/"))
                os.makedirs(os.path.dirname(target), exist_ok=True)
                with open(target, "wb") as f:
                    f.write(z.read(info))
                pictures.add(info.filename[:-5])

    # The festivals' official link banners (fetch-banners.py), shown in place of a screenshot.
    banner_dir = os.path.join(HERE, "banners")
    banners = {}
    if os.path.exists(os.path.join(banner_dir, "banners.json")):
        banners = json.load(open(os.path.join(banner_dir, "banners.json"), encoding="utf-8"))
        for files in banners.values():
            for name in files:
                target = os.path.join(out, "b", *name.split("/"))
                os.makedirs(os.path.dirname(target), exist_ok=True)
                shutil.copyfile(os.path.join(banner_dir, *name.split("/")), target)

    def banner_imgs(festival, depth, alt, first_only=False):
        files = banners.get(festival, [])[: 1 if first_only else None]
        return "".join(f'<img src="{"../" * depth}b/{esc(urllib.parse.quote(n))}" alt="{esc(alt)}" loading="lazy" decoding="async">'
                       for n in files)

    def pic_url(key, depth):
        if key is None or key not in pictures:
            return None
        return "../" * depth + "img/" + "/".join(urllib.parse.quote(p) for p in key.split("/")) + ".webp"

    by_work = {}
    for g in games:
        k = work_key(g.get("matsuri"), g.get("no"), g.get("title"))
        if k and g.get("source") == "matsuri":
            by_work.setdefault(k, g)

    fests = sorted(catalog["matsuri"], key=lambda m: (-(m.get("year") or 0), -month(m)))
    works_by = defaultdict(list)
    for w in catalog["games"]:
        works_by[w["matsuri"]].append(w)

    search = []
    fest_cards = defaultdict(list)
    total_hosted = 0
    for m in fests:
        works = works_by.get(m["id"], [])
        if not works:
            continue
        hosted = 0
        cards = []
        for w in works:
            k = work_key(w["matsuri"], w.get("no"), w.get("title"))
            g = by_work.get(k)
            pkey = f"{w['matsuri']}/{w['no']}" if w.get("no") else None
            pic = pic_url(pkey, 1)
            anchor = "w-" + slug(w.get("no") or w["title"])
            eternal = bool(w.get("eternal"))
            if g:
                hosted += 1
            state = "" if g else (" eternal" if eternal else " lost")
            tag = "" if g else (f'<span class="tag eternal">{bi("エターナル", "never submitted")}</span>' if eternal
                                else f'<span class="tag lost">{bi("未所持", "missing")}</span>')
            meta = " · ".join(esc(x) for x in (w.get("genre"), w.get("engine")) if x and x not in ("-", "－"))
            links = []
            if g and downloads:
                links.append(f'<a class="btn dl" href="{esc(dl_url(g["path"]))}" rel="nofollow">{bi("ダウンロード", "Download")}</a>')
            source = next((d for d in (w.get("downloads") or []) if "/rate/" not in d), None)
            if source:
                links.append(f'<a class="btn" href="{esc(source)}" rel="nofollow noopener">{bi("配布元", "Source")}</a>')
            if w.get("forumUrl"):
                links.append(f'<a class="btn" href="{esc(w["forumUrl"])}" rel="noopener">{bi("感想スレ", "Thread")}</a>')
            if w.get("wikiUrl"):
                links.append(f'<a class="btn" href="{esc(w["wikiUrl"])}" rel="noopener">wiki</a>')
            img = (f'<img src="{esc(pic)}" alt="" loading="lazy" decoding="async" width="320" height="240">' if pic
                   else "NO IMAGE")
            cards.append(f"""<article class="work{state}" id="{anchor}">
  <div class="pic">{img}</div>
  <div class="body">
    <div class="no">No.{esc(w.get('no') or '—')}{tag}</div>
    <div class="title">{esc(w['title'])}</div>
    <div class="meta">{esc(w.get('author'))}</div>
    {f'<div class="meta">{meta}</div>' if meta else ''}
    {f'<div class="meta">{size_text(g.get("size"))}</div>' if g else ''}
    <div class="links">{''.join(links)}</div>
  </div>
</article>""")
            search.append([w["title"], w.get("author") or "", short_name(m["name"]), f"m/{m['id']}.html#{anchor}", 1 if g else 0])
        total_hosted += hosted
        flinks = []
        for label, url in ((bi("祭りwiki", "Wiki"), m.get("wikiUrl")), (bi("公式サイト", "Site"), m.get("siteUrl")),
                           (bi("アーカイブ", "Archive"), m.get("archiveUrl"))):
            if url:
                flinks.append(f'<a class="btn" href="{esc(url)}" rel="noopener">{label}</a>')
        body = f"""<section class="window">
  <p class="lede"><a href="../index.html">{bi("祭り一覧", "All festivals")}</a> › {esc(m.get('year') or '')}</p>
  {f'<div class="banners">{banner_imgs(m["id"], 1, m["name"])}</div>' if m["id"] in banners else ''}
  <h1>{esc(m['name'])}</h1>
  {f'<p class="lede">{esc(m["period"])}</p>' if m.get('period') else ''}
  <div class="stats"><div class="stat"><b>{len(works)}</b>{bi("作品", "works")}</div><div class="stat"><b>{hosted}</b>{bi("ダウンロード可", "downloadable")}</div></div>
  <div class="links">{''.join(flinks)}</div>
  <div class="toolbar"><input type="search" placeholder="この祭りから探す / Filter" oninput="filterWorks(this.value)"></div>
</section>
<div class="works" id="works">
{''.join(cards)}
</div>
<script>
function filterWorks(q){{q=q.trim().toLowerCase();for(const el of document.querySelectorAll('#works .work')){{el.style.display=!q||el.textContent.toLowerCase().includes(q)?'':'none';}}}}
</script>"""
        with open(os.path.join(out, "m", m["id"] + ".html"), "w", encoding="utf-8") as f:
            f.write(page(m["name"] + " — VIPRPG アーカイブ", body, depth=1, description=f"{m['name']}の参加作品 {len(works)}本。"))
        fest_cards[m.get("year") or 0].append(
            f'<a class="fest" href="m/{esc(m["id"])}.html">'
            + (f'<span class="banner">{banner_imgs(m["id"], 0, "", first_only=True)}</span>' if m["id"] in banners
               else f'<span class="banner plate">{esc(short_name(m["name"]))}</span>')
            + f'<span><span class="name">{esc(short_name(m["name"]))}</span><br><span class="count">{hosted} / {len(works)} {bi("作品", "works")}</span></span></a>')

    # 保管庫: the thread and wiki storage areas, and the server's other games.
    areas = defaultdict(list)
    for g in games:
        if g.get("source") != "thread":
            continue
        areas[g.get("area") or "server"].append(g)
    area_order = sorted(areas, key=lambda a: (0 if a.startswith("hokan") else 1, a))
    area_cards = {"保存库": [], "その他": []}
    for a in area_order:
        rows = sorted(areas[a], key=lambda g: g["path"])
        section = rows[0].get("section") or "その他"
        label = rows[0].get("thread") or a
        if a.startswith("hokan"):
            label = f"{int(a[5:])}代目保管庫" if a[5:].isdigit() else label
        trs = []
        for g in rows:
            anchor = "g-" + slug(os.path.splitext(g["path"].split("/")[-1])[0])
            pkey = "server/" + g["path"][:-4] if g["path"].lower().endswith(".zip") else None
            title = g.get("title") or os.path.splitext(g["path"].split("/")[-1])[0]
            pic = pic_url(pkey, 1)
            t = esc(title) + (f' <a href="{esc(pic)}" class="muted" title="picture">▣</a>' if pic else "")
            dl = f'<a class="btn dl" href="{esc(dl_url(g["path"]))}" rel="nofollow">↓</a>' if downloads else ""
            trs.append(f'<tr id="{anchor}"><td class="t">{t}</td><td class="hide-sm">{esc(g.get("author"))}</td>'
                       f'<td class="s">{size_text(g.get("size"))}</td><td class="d">{dl}</td></tr>')
            search.append([title, g.get("author") or "", label, f"s/{a}.html#{anchor}", 1])
        body = f"""<section class="window">
  <p class="lede"><a href="../storage.html">{bi("保管庫", "Storage")}</a></p>
  <h1>{esc(label)}</h1>
  <div class="stats"><div class="stat"><b>{len(rows)}</b>{bi("ファイル", "files")}</div></div>
  <div class="toolbar"><input type="search" placeholder="この保管庫から探す / Filter" oninput="filterRows(this.value)"></div>
</section>
<table id="rows"><thead><tr><th>{bi("タイトル", "Title")}</th><th class="hide-sm">{bi("作者", "Author")}</th><th>{bi("サイズ", "Size")}</th><th></th></tr></thead>
<tbody>
{''.join(trs)}
</tbody></table>
<script>
function filterRows(q){{q=q.trim().toLowerCase();for(const tr of document.querySelectorAll('#rows tbody tr')){{tr.style.display=!q||tr.textContent.toLowerCase().includes(q)?'':'none';}}}}
</script>"""
        with open(os.path.join(out, "s", a + ".html"), "w", encoding="utf-8") as f:
            f.write(page(label + " — VIPRPG アーカイブ", body, depth=1))
        area_cards.setdefault(section, []).append(
            f'<a class="area" href="s/{esc(a)}.html"><span class="name">{esc(label)}</span><br><span class="count">{len(rows)} {bi("ファイル", "files")}</span></a>')

    storage_body = f"""<section class="window">
  <h1>{bi("保管庫", "Storage")}</h1>
  <p class="lede">{bi("スレやwikiの作品保管庫と、アップローダーに残っていた作品。", "The thread and wiki storage archives, and games kept from the uploaders.")}</p>
</section>
<h2>{bi("作品保管庫", "Storage archives")}</h2>
<div class="areas">{''.join(area_cards.get("保存库", []))}</div>
<h2>{bi("その他", "Other")}</h2>
<div class="areas">{''.join(c for s, cs in area_cards.items() if s != "保存库" for c in cs)}</div>"""
    with open(os.path.join(out, "storage.html"), "w", encoding="utf-8") as f:
        f.write(page("保管庫 — VIPRPG アーカイブ", storage_body))

    years = "".join(f'<h2>{y or "—"}</h2><div class="year">{"".join(c)}</div>' for y, c in fest_cards.items())
    index_body = f"""<section class="window">
  <h1>{bi("VIPRPGの祭りと保管庫", "VIPRPG festivals and storage")}</h1>
  <p class="lede">{bi("2005年からのVIPRPG祭りの参加作品と、スレ・wikiの作品保管庫のアーカイブです。",
                       "An archive of the VIPRPG festivals' entries since 2005, and of the thread and wiki storage archives.")}</p>
  <div class="stats">
    <div class="stat"><b>{sum(len(c) for c in fest_cards.values())}</b>{bi("祭り", "festivals")}</div>
    <div class="stat"><b>{total_hosted:,}</b>{bi("祭り作品", "festival works")}</div>
    <div class="stat"><b>{sum(len(v) for v in areas.values()):,}</b>{bi("保管庫のファイル", "storage files")}</div>
  </div>
  <div class="toolbar"><form action="search.html"><input type="search" name="q" placeholder="作品名・作者で検索 / Search titles and authors"></form></div>
</section>
{years}"""
    with open(os.path.join(out, "index.html"), "w", encoding="utf-8") as f:
        f.write(page("VIPRPG アーカイブ", index_body))

    with open(os.path.join(out, "search.json"), "w", encoding="utf-8") as f:
        json.dump(search, f, ensure_ascii=False, separators=(",", ":"))
    search_body = f"""<section class="window">
  <h1>{bi("検索", "Search")}</h1>
  <div class="toolbar"><input type="search" id="q" placeholder="作品名・作者 / Title or author" autofocus></div>
  <p class="lede" id="count"></p>
</section>
<ul class="results" id="results"></ul>
<script>
const norm=s=>s.normalize('NFKC').toLowerCase();
let data=null;
async function load(){{data=(await (await fetch('search.json')).json()).map(r=>[...r,norm(r[0]+' '+r[1])]);run();}}
function esc(s){{return s.replace(/[&<>"]/g,c=>({{'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}})[c]);}}
function run(){{
  const q=norm(document.getElementById('q').value.trim());
  const list=document.getElementById('results'), count=document.getElementById('count');
  if(!data){{return;}}
  if(!q){{list.innerHTML='';count.textContent='';return;}}
  const words=q.split(/\\s+/), hits=data.filter(r=>words.every(w=>r[5].includes(w)));
  count.textContent=hits.length+(document.documentElement.dataset.lang==='en'?' found':'件');
  list.innerHTML=hits.slice(0,300).map(r=>`<li><a href="${{r[3]}}">${{esc(r[0])}}</a> <span class="where">${{esc(r[1])}} · ${{esc(r[2])}}</span></li>`).join('');
}}
const q=document.getElementById('q');q.value=new URLSearchParams(location.search).get('q')||'';
q.addEventListener('input',run);load();
</script>"""
    with open(os.path.join(out, "search.html"), "w", encoding="utf-8") as f:
        f.write(page("検索 — VIPRPG アーカイブ", search_body))

    with open(os.path.join(out, "about.html"), "w", encoding="utf-8") as f:
        f.write(page("サイトについて — VIPRPG アーカイブ", ABOUT, description="VIPRPGアーカイブについて。"))

    nf_body = f"""<section class="window"><h1>{bi("ページが見つからないよ", "Page not found")}</h1>
  <p class="lede"><a href="/index.html">{bi("祭り一覧へ", "Back to the festivals")}</a></p></section>"""
    with open(os.path.join(out, "404.html"), "w", encoding="utf-8") as f:
        # Shown at any address, so its links start at the site's root.
        f.write(page("404 — VIPRPG アーカイブ", nf_body, root="/"))
    with open(os.path.join(out, "robots.txt"), "w", encoding="utf-8") as f:
        f.write("User-agent: *\nDisallow: /img/\n")
    with open(os.path.join(out, "style.css"), "w", encoding="utf-8") as f:
        f.write(CSS)
    with open(os.path.join(out, "favicon.svg"), "w", encoding="utf-8") as f:
        f.write(FAVICON)

    # The launcher's list, without the withheld games (the manifest already leaves them out).
    shutil.copyfile(manifest_path, os.path.join(out, "manifest.json"))
    with open(os.path.join(out, "_headers"), "w", encoding="utf-8") as f:
        f.write("/manifest.json\n  Cache-Control: public, max-age=300\n  Access-Control-Allow-Origin: *\n"
                "/search.json\n  Cache-Control: public, max-age=3600\n"
                "/img/*\n  Cache-Control: public, max-age=604800\n")

    count = sum(len(files) for _, _, files in os.walk(out))
    print(f"site: {out}\n  {count} files, {len(pictures)} pictures, {sum(len(c) for c in fest_cards.values())} festival pages, "
          f"{len(areas)} storage pages, {len(search)} search entries")
    if count > 19000:
        print("warning: Cloudflare Pages allows 20,000 files per site", file=sys.stderr)


if __name__ == "__main__":
    main()
