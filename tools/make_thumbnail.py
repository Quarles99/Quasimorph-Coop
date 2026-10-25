#!/usr/bin/env python3
"""Draws workshop/thumbnail.png, the Steam Workshop preview image (512x512).

Usage: python3 tools/make_thumbnail.py
"""
import os

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

SIZE = 512
OUT = os.path.join(os.path.dirname(__file__), "..", "workshop", "thumbnail.png")

BG = (12, 13, 16)
GRID = (32, 28, 30)
RED = (214, 52, 48)
TEAL = (64, 196, 186)
TEXT = (232, 228, 220)
DIM = (150, 144, 138)

FONT_BLACK = "/usr/share/fonts/google-noto/NotoSans-ExtraCondensedBlack.ttf"
FONT_BOLD = "/usr/share/fonts/google-noto/NotoSans-ExtraBold.ttf"


def font(path, size):
    try:
        return ImageFont.truetype(path, size)
    except OSError:
        return ImageFont.load_default()


def merc(draw, cx, base, scale, color):
    """A helmeted merc bust: shoulders, neck, helmet with a visor slit."""
    s = scale
    draw.polygon([(cx - 70 * s, base), (cx - 58 * s, base - 62 * s), (cx - 22 * s, base - 82 * s),
                  (cx + 22 * s, base - 82 * s), (cx + 58 * s, base - 62 * s), (cx + 70 * s, base)], fill=color)
    draw.rectangle([cx - 16 * s, base - 100 * s, cx + 16 * s, base - 76 * s], fill=color)
    draw.ellipse([cx - 40 * s, base - 178 * s, cx + 40 * s, base - 94 * s], fill=color)
    draw.rounded_rectangle([cx - 30 * s, base - 146 * s, cx + 30 * s, base - 130 * s], radius=6 * s, fill=BG)


def main():
    img = Image.new("RGB", (SIZE, SIZE), BG)
    d = ImageDraw.Draw(img)

    # Tactical grid.
    for i in range(0, SIZE, 32):
        d.line([(i, 0), (i, SIZE)], fill=GRID, width=1)
        d.line([(0, i), (SIZE, i)], fill=GRID, width=1)

    # Glow behind the two mercs.
    glow = Image.new("RGB", (SIZE, SIZE), (0, 0, 0))
    g = ImageDraw.Draw(glow)
    g.ellipse([86, 110, 306, 330], fill=(110, 24, 22))
    g.ellipse([206, 110, 426, 330], fill=(18, 84, 78))
    glow = glow.filter(ImageFilter.GaussianBlur(48))
    img = ImageChops.add(img, glow)
    d = ImageDraw.Draw(img)

    merc(d, 196, 330, 1.0, RED)
    merc(d, 316, 330, 1.0, TEAL)

    # Link between them.
    for k in range(5):
        x = 236 + k * 10
        d.ellipse([x - 3, 196 - 3, x + 3, 196 + 3], fill=TEXT)

    # Corner brackets.
    for (x, y, dx, dy) in [(24, 24, 1, 1), (488, 24, -1, 1), (24, 488, 1, -1), (488, 488, -1, -1)]:
        d.line([(x, y), (x + 36 * dx, y)], fill=DIM, width=3)
        d.line([(x, y), (x, y + 36 * dy)], fill=DIM, width=3)

    title = font(FONT_BLACK, 104)
    small = font(FONT_BOLD, 30)
    tag = font(FONT_BOLD, 19)

    def centered(text, f, y, fill):
        w = d.textlength(text, font=f)
        d.text(((SIZE - w) / 2, y), text, font=f, fill=fill)

    centered("QUASIMORPH", small, 52, DIM)
    centered("CO-OP", title, 328, TEXT)
    centered("TWO PLAYERS  ·  ONLINE OR HOTSEAT", tag, 450, DIM)

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    img.save(OUT, optimize=True)
    print(os.path.abspath(OUT), os.path.getsize(OUT), "bytes")


if __name__ == "__main__":
    main()
