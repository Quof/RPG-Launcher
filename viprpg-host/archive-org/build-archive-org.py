"""Prepares the bucket for archive.org, in two items, from the manifest (so WOLF games, withheld, are never included).

    python viprpg-host/archive-org/build-archive-org.py [--out "E:/VIPRPG Host/archive-org"] [--bucket "E:/VIPRPG Host/bucket"]

viprpg-archive-festivals   one folder per festival ("2015-kouhaku VIPRPG紅白2015/No.075 タイトル.zip"), each game its
                           own zip, hard-linked from the bucket (no extra disk space).
viprpg-archive-storage     the 保管庫 generations and the other uploaders, one zip each ("hokan06 6代目 かにかま保管庫.zip"),
                           the games inside named by their storage number and title. Stored, not recompressed.
Each item gets index.csv (every game: where it is in the item, title, author, festival, size, SHA-256, original name),
README.txt (Japanese and English) and metadata.json (for upload-to-archive-org.ps1).
"""
import argparse
import csv
import json
import os
import re
import shutil
import sys
import unicodedata
import zipfile
from collections import defaultdict

sys.stdout.reconfigure(encoding="utf-8")
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
CATALOG = os.path.join(REPO, "LauncherSource", "Assets", "vip-catalog.json")
WITHHELD = ("games/wolf/",)
CONTACT = "dekirutranslations@gmail.com"
FESTIVALS, STORAGE = "viprpg-archive-festivals", "viprpg-archive-storage"


def safe(text, limit=90):
    """A file or folder name that Windows, Linux and archive.org all accept."""
    t = unicodedata.normalize("NFC", str(text or "")).replace("\u3000", " ")
    t = re.sub(r'[\\/:*?"<>|\x00-\x1f]', "_", t)
    t = re.sub(r"\s+", " ", t).strip(" .")
    return (t[:limit].rstrip(" .") or "_")


def unique(name, taken):
    stem, ext = os.path.splitext(name)
    n, candidate = 2, name
    while candidate.lower() in taken:
        candidate = f"{stem} ({n}){ext}"
        n += 1
    taken.add(candidate.lower())
    return candidate


README = """VIPRPG アーカイブ / VIPRPG Archive — {part_ja} / {part_en}
{date}

■ 日本語
VIPRPG（2ちゃんねる／5ちゃんねるVIP板のRPGツクールスレ発の作品）の祭り作品と保管庫を、一か所にまとめて保存するためのアーカイブです。
ウェブサイト: https://viprpg-archive.com （作品の検索・閲覧・ダウンロード）
ランチャー: https://github.com/Quof/RPG-Launcher （作品を直接ダウンロードして遊べるプログラム）

{layout_ja}
index.csv に全作品の一覧（ファイルの場所、タイトル、作者、祭り・番号、サイズ、SHA-256、元のファイル名）があります。

このアーカイブには成人向け（R-18）やグロテスクな表現（R-18G）を含む作品があり、すべてに表示があるわけではありません。
作品の権利はそれぞれの作者にあります。作者の方で、ご自身の作品の削除を希望される場合は {contact} までご連絡ください。
ファイルはVIPRPGのwikiや各祭りのサイト・アップローダーから集めたもので、一つひとつの動作確認は行っていません。

■ English
An archive that gathers the VIPRPG festivals' entries and the thread's storage archives (保管庫) in one place: games made in
RPG Maker by the 2channel/5channel VIP board's RPG Maker thread.
Website: https://viprpg-archive.com (browse, search and download)
Launcher: https://github.com/Quof/RPG-Launcher (downloads and plays the games directly)

{layout_en}
index.csv lists every game: where it is in this item, its title, author, festival and number, size, SHA-256 and original file name.

Some games contain adult (R-18) or grotesque (R-18G) content, and not every one of them is labelled as such.
Every work belongs to its author. Authors who want a game removed can write to {contact}.
The files were gathered from the VIPRPG wiki and the festivals' sites and uploaders, and have not been individually tested.
"""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bucket", default="E:/VIPRPG Host/bucket")
    ap.add_argument("--out", default="E:/VIPRPG Host/archive-org")
    args = ap.parse_args()
    bucket = args.bucket.rstrip("/") + "/"
    manifest = json.load(open(bucket + "manifest.json", encoding="utf-8"))
    catalog = json.load(open(CATALOG, encoding="utf-8"))
    fests = {m["id"]: m for m in catalog["matsuri"]}
    works = {(w["matsuri"], (w.get("no") or "").lstrip("0")): w for w in catalog["games"]}
    games = [g for g in manifest["games"] if not g["path"].lower().startswith(WITHHELD)]
    date = manifest.get("generated", "")[:10]

    # --- festivals: one folder per festival, every game linked as its own file ---
    root = os.path.join(args.out, FESTIVALS)
    if os.path.exists(root):
        shutil.rmtree(root)
    os.makedirs(root)
    rows, taken, folder_of = [], defaultdict(set), {}
    for g in sorted((g for g in games if g.get("source") == "matsuri"),
                    key=lambda g: (fests.get(g["matsuri"], {}).get("year") or 0, g["matsuri"], (g.get("no") or "").zfill(4))):
        m = fests.get(g["matsuri"], {"name": g["matsuri"], "year": ""})
        folder = folder_of.setdefault(g["matsuri"], safe(f"{g['matsuri']} {m['name']}", 70))
        no = g.get("no") or ""
        w = works.get((g["matsuri"], no.lstrip("0")), {})
        name = unique(safe(f"No.{no} {g.get('title')}" if no else g.get("title")) + ".zip", taken[folder])
        os.makedirs(os.path.join(root, folder), exist_ok=True)
        os.link(bucket + g["path"], os.path.join(root, folder, name))
        rows.append({"file": f"{folder}/{name}", "inside bundle": "", "festival id": g["matsuri"], "festival": m["name"],
                     "year": m.get("year") or "", "no": no, "title": g.get("title") or "", "author": g.get("author") or w.get("author") or "",
                     "engine": g.get("engine") or w.get("engine") or "", "genre": w.get("genre") or "", "size": g["size"],
                     "sha256": g.get("sha256") or "", "source / original name": g.get("note") or "", "bucket path": g["path"]})
    finish(root, rows, date, FESTIVALS,
           "祭り作品", "festival entries",
           f"{len(folder_of)}の祭りのフォルダ（「祭りID 祭りの名前」）に、各作品が「No.番号 タイトル.zip」として1つずつ入っています。",
           f"One folder per festival ({len(folder_of)}, named \"festival-id festival name\"), each entry as its own zip named \"No.number title.zip\".",
           f"VIPRPG festival entries, {min(r['year'] for r in rows if r['year'])}–{max(r['year'] for r in rows if r['year'])}")

    # --- storage: one zip per 保管庫 generation and per other uploader ---
    root = os.path.join(args.out, STORAGE)
    if os.path.exists(root):
        shutil.rmtree(root)
    os.makedirs(root)
    areas = defaultdict(list)
    for g in games:
        if g.get("source") != "matsuri":
            areas[g.get("area") or "server"].append(g)
    rows = []
    for area in sorted(areas, key=lambda a: (0 if a.startswith("hokan") else 1, a)):
        items = sorted(areas[area], key=lambda g: g["path"])
        section = items[0].get("section") or "その他"
        label = items[0].get("thread") or area
        sub = "保管庫" if area.startswith("hokan") else "その他 (other uploaders)"
        os.makedirs(os.path.join(root, sub), exist_ok=True)
        bundle = safe(f"{area} {label}", 80) + ".zip"
        inner_taken = set()
        with zipfile.ZipFile(os.path.join(root, sub, bundle), "w", zipfile.ZIP_STORED, allowZip64=True) as z:
            for g in items:
                stem = os.path.splitext(g["path"].rsplit("/", 1)[-1])[0]
                inner = unique(safe(f"{stem} {g.get('title') or ''}") + ".zip", inner_taken)
                z.write(bucket + g["path"], inner)
                rows.append({"file": f"{sub}/{bundle}", "inside bundle": inner, "festival id": "", "festival": "",
                             "year": g.get("year") or "", "no": "", "title": g.get("title") or "", "author": g.get("author") or "",
                             "engine": g.get("engine") or "", "genre": "", "size": g["size"], "sha256": g.get("sha256") or "",
                             "source / original name": f"{label} — {g.get('note') or ''}".strip(" —"), "bucket path": g["path"]})
        print(f"  {sub}/{bundle}: {len(items)} games")
    finish(root, rows, date, STORAGE,
           "保管庫・その他のアップローダー", "storage archives and other uploaders",
           "「保管庫」フォルダに初代〜15代目の作品保管庫、「その他」フォルダにそのほかのアップローダーが、それぞれ1つのzipにまとめて入っています"
           "（中の作品は「保管庫の番号 タイトル.zip」）。",
           "The 保管庫 folder holds the thread's storage archives, generations 1 to 15, and the other folder the other uploaders, "
           "each as one zip; inside, every game is \"storage-number title.zip\".",
           "VIPRPG storage archives (保管庫 1–15) and other uploaders")


def finish(root, rows, date, identifier, part_ja, part_en, layout_ja, layout_en, title_en):
    fields = list(rows[0].keys())
    with open(os.path.join(root, "index.csv"), "w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fields)
        w.writeheader()
        w.writerows(rows)
    with open(os.path.join(root, "README.txt"), "w", encoding="utf-8-sig", newline="\r\n") as f:
        f.write(README.format(part_ja=part_ja, part_en=part_en, date=f"{date} 時点 / as of {date}", layout_ja=layout_ja,
                              layout_en=layout_en, contact=CONTACT))
    size = sum(os.path.getsize(os.path.join(r, n)) for r, _, fs in os.walk(root) for n in fs)
    meta = {
        "identifier": identifier,
        "title": f"VIPRPG アーカイブ — {part_ja} / VIPRPG Archive — {title_en}",
        "mediatype": "software",
        "collection": "open_source_software",
        "creator": "VIPRPG community (VIP RPGツクールスレ)",
        "language": "jpn",
        "subject": ["VIPRPG", "RPGツクール", "RPG Maker", "RPG Maker 2000", "RPG Maker 2003", "freeware games",
                    "Japanese games", "2channel", "game preservation"],
        "description": (f"VIPRPGの{part_ja}のアーカイブ（{date}時点）。{layout_ja} 一覧は index.csv にあります。"
                        f"<br><br>An archive of the VIPRPG {part_en} (as of {date}): games made in RPG Maker by the "
                        f"2channel/5channel VIP board's RPG Maker thread. {layout_en} index.csv lists every game."
                        f"<br><br>Website: https://viprpg-archive.com<br>"
                        f"Some games contain adult (R-18) or grotesque (R-18G) content. Every work belongs to its author; "
                        f"authors who want a game removed can write to {CONTACT}."),
        "date": date,
        "size_hint": size,
    }
    with open(os.path.join(os.path.dirname(root), identifier + ".metadata.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=1)
    files = sum(len(fs) for _, _, fs in os.walk(root))
    print(f"{identifier}: {len(rows)} games, {files} files, {size / 1e9:.1f} GB")


if __name__ == "__main__":
    main()
