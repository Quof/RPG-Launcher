"""Uploads a prepared item (build-archive-org.py) to archive.org with the official `internetarchive` library.

    pip install internetarchive
    ia configure                      (once: your archive.org email and password; they are saved for the library)
    python upload-to-archive-org.py viprpg-archive-festivals
    python upload-to-archive-org.py viprpg-archive-storage

Every file keeps its path inside the item. A file already on archive.org with the same MD5 is skipped, so an
interrupted upload is resumed by running the same command again. archive.org's busy replies (503) are retried.
"""
import json
import os
import sys

try:
    import internetarchive as ia
except ImportError:
    sys.exit("The internetarchive library isn't installed: run  pip install internetarchive  and then  ia configure")

ROOT = os.environ.get("ARCHIVE_ORG_OUT", "E:/VIPRPG Host/archive-org")


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    identifier = sys.argv[1]
    folder = os.path.join(ROOT, identifier)
    meta = json.load(open(os.path.join(ROOT, identifier + ".metadata.json"), encoding="utf-8"))
    size_hint = meta.pop("size_hint")
    meta.pop("identifier")
    files = {}
    for root, _, names in os.walk(folder):
        for name in names:
            full = os.path.join(root, name)
            files[os.path.relpath(full, folder).replace(os.sep, "/")] = full
    # README and index first, so the item page explains itself from the start.
    order = sorted(files, key=lambda k: (k not in ("README.txt", "index.csv"), k))
    print(f"{identifier}: {len(files)} files, {size_hint / 1e9:.1f} GB")
    item = ia.get_item(identifier)
    done = 0
    for start in range(0, len(order), 50):
        batch = {k: files[k] for k in order[start:start + 50]}
        responses = item.upload(batch, metadata=meta, headers={"x-archive-size-hint": str(size_hint)},
                                queue_derive=False, checksum=True, retries=20, retries_sleep=60, verbose=True)
        bad = [r for r in responses if getattr(r, "status_code", 200) >= 400]
        if bad:
            sys.exit(f"Upload stopped: {bad[0].status_code} {bad[0].text[:300]}  (run the same command again to resume)")
        done += len(batch)
        print(f"  {done}/{len(files)} files")
    print(f"Done: https://archive.org/details/{identifier}")


if __name__ == "__main__":
    main()
