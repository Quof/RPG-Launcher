"""Saves each festival's official link banners from VIPRPG祭典アーカイブ (fes.viprpg.org) into site/banners/.

The index website shows them on the festival list and pages instead of a screenshot. Run it again when a new
festival is added; files already saved are kept.

    python viprpg-host/site/fetch-banners.py
"""
import json
import os
import re
import sys
import time
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
CATALOG = os.path.normpath(os.path.join(HERE, "..", "..", "LauncherSource", "Assets", "vip-catalog.json"))
OUT = os.path.join(HERE, "banners")
SITE = "https://fes.viprpg.org"
UA = "VIPRPG-archive-index/1.0 (banner copy for the RPG Launcher index site)"

# Festivals the archive has no banner list for, whose archived site shows one: its title banner or link banners.
EXTRA = {
    "2011-toppatsu-festival": ["/original-pages/2011-toppatsu-festival/assets/001.png"],
    "2009-summer": ["/original-pages/2009-summer/tk_m399.png", "/original-pages/2009-summer/tk_m400.png"],
    "2008-gw": ["/original-pages/2008-gw/image/gw.png"],
}


def get(url):
    request = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.read()


def main():
    catalog = json.load(open(CATALOG, encoding="utf-8"))
    ids = sorted((m["id"] for m in catalog["matsuri"]), key=len, reverse=True)
    names = {m["name"]: m["id"] for m in catalog["matsuri"]}
    home = get(SITE + "/").decode("utf-8")
    found = {}
    for alt, src in re.findall(r'<img alt="([^"]*?) — バナー"[^>]*?src="([^"]+)"', home):
        rest = src.split("/banners/", 1)[-1]
        festival = names.get(alt) or next((i for i in ids if rest.startswith(i + "/") or rest.startswith(i + "-")), None)
        if festival and src not in found.setdefault(festival, []):
            found[festival].append(src)
    for festival, srcs in EXTRA.items():
        found.setdefault(festival, srcs)

    index = {}
    for festival, srcs in sorted(found.items()):
        files = []
        for n, src in enumerate(srcs, 1):
            ext = os.path.splitext(urllib.parse.urlparse(src).path)[1].lower() or ".png"
            name = f"{festival}/{n}{ext}"
            path = os.path.join(OUT, *name.split("/"))
            if not os.path.exists(path):
                os.makedirs(os.path.dirname(path), exist_ok=True)
                data = get(urllib.parse.urljoin(SITE, src))
                with open(path, "wb") as f:
                    f.write(data)
                time.sleep(0.5)
            files.append(name)
        index[festival] = files
    with open(os.path.join(OUT, "banners.json"), "w", encoding="utf-8") as f:
        json.dump(index, f, ensure_ascii=False, indent=1)
    missing = [m["id"] for m in catalog["matsuri"] if m["id"] not in index]
    print(f"{sum(len(v) for v in index.values())} banners for {len(index)} festivals; none for: {', '.join(missing)}")


if __name__ == "__main__":
    sys.exit(main())
