"""Builds the launcher's and the website's icons from app-icon.png (the character on a transparent background).

    python LauncherSource/make-icons.py

Small sizes (16-32 px: tabs, taskbar, title bars) show her head and shoulders, which stay readable there; 48 px and
up show the whole character. 128 and 256 are the pixel art itself at 1x and 2x, so they stay crisp.
Writes LauncherSource/app.ico, viprpg-host/site/favicon.ico and viprpg-host/site/apple-touch-icon.png.
"""
import io
import os
import struct

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SITE = os.path.join(HERE, "..", "viprpg-host", "site")
HEAD_BOX = (22, 2, 92, 72)  # head and shoulders, in app-icon.png's 112x112 pixels


def square(img, box):
    c = img.crop(box)
    n = max(c.size)
    out = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    out.paste(c, ((n - c.width) // 2, (n - c.height) // 2))
    return out


def shrink(img, n):
    # Premultiplied alpha, so the outline doesn't pick up a dark fringe from the transparent pixels.
    return img.convert("RGBa").resize((n, n), Image.LANCZOS).convert("RGBA")


def crisp(img, n):
    """The pixel art enlarged by a whole number (nearest neighbour), centred on an n x n canvas."""
    k = max(1, n // img.width)
    big = img.resize((img.width * k, img.height * k), Image.NEAREST)
    out = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    out.paste(big, ((n - big.width) // 2, (n - big.height) // 2))
    return out


def frames():
    sprite = Image.open(os.path.join(HERE, "app-icon.png")).convert("RGBA")
    full = square(sprite, sprite.getbbox())
    head = square(sprite, HEAD_BOX)
    out = {n: shrink(head, n) for n in (16, 20, 24, 32)}
    out.update({n: shrink(full, n) for n in (48, 64)})
    out.update({n: crisp(full, n) for n in (128, 256)})
    return out


def write_ico(path, images):
    """An .ico holding each size as PNG data (read by Windows Vista and later, and every browser)."""
    blobs = []
    for n, img in sorted(images.items()):
        buf = io.BytesIO()
        img.save(buf, "PNG", optimize=True)
        blobs.append((n, buf.getvalue()))
    header = struct.pack("<HHH", 0, 1, len(blobs))
    offset = 6 + 16 * len(blobs)
    entries, data = b"", b""
    for n, blob in blobs:
        entries += struct.pack("<BBBBHHII", n % 256, n % 256, 0, 0, 1, 32, len(blob), offset + len(data))
        data += blob
    with open(path, "wb") as f:
        f.write(header + entries + data)


if __name__ == "__main__":
    f = frames()
    write_ico(os.path.join(HERE, "app.ico"), f)
    write_ico(os.path.join(SITE, "favicon.ico"), {n: f[n] for n in (16, 32, 48)})
    # Phones' home-screen icon: the whole character, 180 px.
    sprite = Image.open(os.path.join(HERE, "app-icon.png")).convert("RGBA")
    shrink(square(sprite, sprite.getbbox()), 180).save(os.path.join(SITE, "apple-touch-icon.png"), optimize=True)
    print("app.ico:", sorted(f), "| favicon.ico: 16, 32, 48 | apple-touch-icon.png: 180")
