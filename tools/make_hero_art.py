"""
Makes the Store's 16:9 Super hero art (branding/store/HeroArt-16x9-*.png): a fan of paper pages on warm paper, each
marked by one of Papery's tool families (a picture, a table, a stamp, a highlight, a signature), with a pen and soft
colour around them. Same look as the framed screenshots (make_store_art.py).

The Store's rules for this image: no text or product name, no app screens, nothing important in the bottom third
(a gradient may cover it), the key part in the middle (some layouts crop), little empty space. So the "writing" on the
pages is grey bars, the fan sits in the upper middle, and the corners carry colour and small floating pages.

    python tools/make_hero_art.py
"""
import math
import random
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

sys.path.insert(0, str(Path(__file__).resolve().parent))
from make_store_art import AMBER, CORAL, FOLD, INK, PAPER, PLUM, SKY, TEAL, mix, sparkle, stroke, wobbly  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / 'branding' / 'store'
W, H = 3840, 2160
S = 2                      # pages and doodles are drawn twice as big, then shrunk, for smooth edges
PAGE_W, PAGE_H = 760, 980
BAR = (226, 221, 214)      # the grey "writing"
WHITE = (255, 255, 255)


def background():
    img = Image.new('RGB', (W, H), mix(PAPER, CORAL, 0.04))
    blobs = Image.new('RGBA', (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(blobs)
    d.ellipse((-600, -700, 1500, 1300), fill=CORAL + (70,))
    d.ellipse((2700, -500, 4500, 1200), fill=TEAL + (60,))
    d.ellipse((2900, 900, 4600, 2700), fill=SKY + (58,))
    d.ellipse((-500, 1200, 1300, 2800), fill=AMBER + (58,))
    d.ellipse((1300, 1500, 2600, 2700), fill=PLUM + (30,))
    blobs = blobs.filter(ImageFilter.GaussianBlur(260))
    img.paste(blobs, (0, 0), blobs)
    return img


def blank_page(w=PAGE_W, h=PAGE_H, fold=110):
    """A white sheet with its top-right corner folded over, drawn at twice the size."""
    page = Image.new('RGBA', (w * S, h * S), (0, 0, 0, 0))
    d = ImageDraw.Draw(page)
    f = fold * S
    r = 26 * S
    d.rounded_rectangle((0, 0, w * S - 1, h * S - 1), radius=r, fill=WHITE + (255,))
    # Cut the corner, then lay the fold in it.
    d.polygon([(w * S - f, -2), (w * S + 2, -2), (w * S + 2, f)], fill=(0, 0, 0, 0))
    d.polygon([(w * S - f, 0), (w * S - f, f), (w * S, f)], fill=FOLD + (255,))
    return page, d


def bars(d, x, y, widths, gap=56, height=22, colour=BAR):
    """Lines of "writing" as soft grey bars. Returns where the next line would go."""
    for wd in widths:
        d.rounded_rectangle((x * S, y * S, (x + wd) * S, (y + height) * S), radius=height * S // 2, fill=colour + (255,))
        y += gap
    return y


def heading(d, x, y, width, colour):
    d.rounded_rectangle((x * S, y * S, (x + width) * S, (y + 38) * S), radius=19 * S, fill=colour + (255,))


def picture_page():
    page, d = blank_page()
    heading(d, 70, 90, 380, SKY)
    # A photo: sky, sun and two hills.
    x0, y0, x1, y1 = 70, 180, 690, 560
    d.rounded_rectangle((x0 * S, y0 * S, x1 * S, y1 * S), radius=22 * S, fill=mix(WHITE, SKY, 0.22) + (255,))
    d.ellipse((520 * S, 230 * S, 610 * S, 320 * S), fill=AMBER + (255,))
    d.polygon([(x0 * S, y1 * S), (250 * S, 330 * S), (430 * S, y1 * S)], fill=mix(SKY, INK, 0.15) + (255,))
    d.polygon([(300 * S, y1 * S), (500 * S, 380 * S), (x1 * S, 500 * S), (x1 * S, y1 * S)], fill=mix(SKY, WHITE, 0.25) + (255,))
    bars(d, 70, 620, [620, 560, 600, 410, 590])
    return page


def table_page():
    page, d = blank_page()
    heading(d, 70, 90, 330, TEAL)
    x0, y0, cw, rh, cols, rows = 70, 190, 155, 78, 4, 6
    d.rounded_rectangle((x0 * S, y0 * S, (x0 + cw * cols) * S, (y0 + rh) * S), radius=14 * S, fill=mix(WHITE, TEAL, 0.3) + (255,))
    line = mix(WHITE, TEAL, 0.45) + (255,)
    for r in range(rows + 1):
        d.line((x0 * S, (y0 + r * rh) * S, (x0 + cw * cols) * S, (y0 + r * rh) * S), fill=line, width=3 * S)
    for c in range(cols + 1):
        d.line(((x0 + c * cw) * S, y0 * S, (x0 + c * cw) * S, (y0 + rows * rh) * S), fill=line, width=3 * S)
    rng = random.Random(4)
    for r in range(1, rows):
        for c in range(cols):
            wd = rng.randint(55, 110)
            d.rounded_rectangle(((x0 + c * cw + 22) * S, (y0 + r * rh + 30) * S, (x0 + c * cw + 22 + wd) * S, (y0 + r * rh + 48) * S), radius=9 * S, fill=BAR + (255,))
    bars(d, 70, 710, [600, 520, 580])
    return page


def stamp_page():
    page, d = blank_page()
    heading(d, 70, 90, 420, PLUM)
    bars(d, 70, 190, [620, 590, 610, 540, 600, 380])
    # A round rubber stamp with a tick, set at a slant.
    stamp = Image.new('RGBA', (360 * S, 360 * S), (0, 0, 0, 0))
    sd = ImageDraw.Draw(stamp)
    ink = PLUM + (235,)
    sd.ellipse((10 * S, 10 * S, 350 * S, 350 * S), outline=ink, width=14 * S)
    sd.ellipse((44 * S, 44 * S, 316 * S, 316 * S), outline=ink, width=5 * S)
    stroke(sd, [(110 * S, 185 * S), (160 * S, 240 * S), (255 * S, 125 * S)], 26 * S, ink)
    stamp = stamp.rotate(-14, resample=Image.BICUBIC, expand=True)
    page.alpha_composite(stamp, (40 * S, 540 * S))  # on the left, the part the front page doesn't cover
    return page


def highlight_page():
    page, d = blank_page()
    heading(d, 70, 90, 360, AMBER)
    y = 190
    for i, wd in enumerate([620, 600, 560, 610, 590, 430, 600, 520]):
        if i in (2, 3):
            # The highlighter goes on first, a little wider than the line, like a real one.
            d.rounded_rectangle((60 * S, (y - 12) * S, (70 + wd + 14) * S, (y + 34) * S), radius=10 * S, fill=mix(WHITE, AMBER, 0.55) + (255,))
        bars(d, 70, y, [wd])
        y += 56
    # A note in the margin, in pen.
    pts = [((80 + i * 9) * S, (700 + 14 * math.sin(i / 3.2)) * S) for i in range(60)]
    stroke(d, wobbly(pts, 9, 4 * S), 7 * S, AMBER + (255,))
    return page


def signature_page():
    page, d = blank_page(fold=120)
    heading(d, 70, 90, 400, CORAL)
    bars(d, 70, 190, [620, 600, 610, 560, 590, 470, 600, 540])
    # Where to sign: a line, a cross, and the signature itself.
    d.line((90 * S, 860 * S, 520 * S, 860 * S), fill=BAR + (255,), width=5 * S)
    stroke(d, [(92 * S, 800 * S), (122 * S, 830 * S)], 6 * S, BAR + (255,))
    stroke(d, [(122 * S, 800 * S), (92 * S, 830 * S)], 6 * S, BAR + (255,))
    # Joined-up loops (a trochoid, the curve a pen makes writing "llll"), growing smaller, then a flourish back under.
    pts = []
    for i in range(320):
        t = i / 319 * math.pi * 9
        size = 1 - (i / 319) * 0.45
        x = 160 + t * 12.5 - 22 * math.sin(t) * size
        y = 815 - 36 * math.cos(t) * size - 18 * size
        pts.append((x * S, y * S))
    for i in range(1, 60):
        t = i / 59
        x = 160 + math.pi * 9 * 12.5 + 30 * math.sin(t * math.pi) - t * 340
        y = 815 + 34 * math.sin(t * math.pi * 0.9)
        pts.append((x * S, y * S))
    stroke(d, wobbly(pts, 21, 2 * S), 8 * S, mix(INK, SKY, 0.35) + (255,))
    # A tick in a circle: signed and checked.
    d.ellipse((560 * S, 760 * S, 680 * S, 880 * S), fill=CORAL + (255,))
    stroke(d, [(592 * S, 822 * S), (614 * S, 846 * S), (650 * S, 796 * S)], 12 * S, WHITE + (255,))
    return page


def place(img, page, centre, angle, shadow=True, zoom=1.0):
    """Shrinks a twice-size page, turns it, and lays it down with a soft shadow."""
    page = page.resize((round(page.width * zoom / S), round(page.height * zoom / S)), Image.LANCZOS)
    turned = page.rotate(angle, resample=Image.BICUBIC, expand=True)
    x = round(centre[0] - turned.width / 2)
    y = round(centre[1] - turned.height / 2)
    if shadow:
        alpha = turned.getchannel('A')
        dark = Image.new('RGBA', turned.size, (70, 45, 30, 0))
        dark.putalpha(alpha.point(lambda v: v * 70 // 255))
        layer = Image.new('RGBA', (W, H), (0, 0, 0, 0))
        layer.paste(dark, (x + 18, y + 46), dark)
        layer = layer.filter(ImageFilter.GaussianBlur(46))
        img.alpha_composite(layer)
    img.alpha_composite(turned, (x, y))


def pen(img, tip, angle):
    """A coral pen lying across the corner of the front page."""
    length, width = 640, 70
    p = Image.new('RGBA', ((length + 40) * S, (width + 40) * S), (0, 0, 0, 0))
    d = ImageDraw.Draw(p)
    o = 20 * S
    d.rounded_rectangle((o + 150 * S, o, o + length * S, o + width * S), radius=width * S // 2, fill=CORAL + (255,))
    d.rounded_rectangle((o + 470 * S, o, o + 530 * S, o + width * S), radius=12 * S, fill=mix(CORAL, INK, 0.25) + (255,))
    d.polygon([(o + 150 * S, o), (o, o + width * S // 2), (o + 150 * S, o + width * S)], fill=(246, 226, 200, 255))
    d.polygon([(o + 42 * S, o + width * S * 0.36), (o, o + width * S // 2), (o + 42 * S, o + width * S * 0.64)], fill=INK + (255,))
    p = p.resize((p.width // S, p.height // S), Image.LANCZOS)
    turned = p.rotate(angle, resample=Image.BICUBIC, expand=True)
    # Put the tip where it was asked for: the tip is the left end before turning.
    rad = math.radians(angle)
    cx, cy = turned.width / 2, turned.height / 2
    tip_off = -(length + 40) / 2 + 20
    tx = cx + tip_off * math.cos(rad)
    ty = cy - tip_off * math.sin(rad)
    x, y = round(tip[0] - tx), round(tip[1] - ty)
    layer = Image.new('RGBA', (W, H), (0, 0, 0, 0))
    shadow = Image.new('RGBA', turned.size, (70, 45, 30, 0))
    shadow.putalpha(turned.getchannel('A').point(lambda v: v * 80 // 255))
    layer.paste(shadow, (x + 14, y + 34), shadow)
    layer = layer.filter(ImageFilter.GaussianBlur(26))
    img.alpha_composite(layer)
    img.alpha_composite(turned, (x, y))


def small_page(img, centre, angle, tint, scale):
    page, d = blank_page(round(PAGE_W * scale), round(PAGE_H * scale), fold=round(110 * scale))
    heading(d, 60 * scale, 70 * scale, 300 * scale, tint)
    for i in range(5):
        d.rounded_rectangle((60 * scale * S, (150 + i * 50) * scale * S, (60 + [520, 470, 500, 380, 460][i]) * scale * S, (170 + i * 50) * scale * S),
                            radius=10 * scale * S, fill=BAR + (255,))
    faded = page.copy()
    faded.putalpha(page.getchannel('A').point(lambda v: v * 200 // 255))
    place(img, faded, centre, angle)


def swoosh(img):
    """A loose coral pen stroke under the fan, like the underline on the screenshots."""
    layer = Image.new('RGBA', (W * S, H * S), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    pts = [((1180 + i * 25) * S, (1560 + 24 * math.sin(i / 60 * math.pi * 3.0)) * S) for i in range(61)]
    stroke(d, wobbly(pts, 5, 6 * S), 16 * S, CORAL + (255,))
    layer = layer.resize((W, H), Image.LANCZOS)
    img.alpha_composite(layer)


def main():
    img = background().convert('RGBA')

    # Small pages drifting at the edges fill the corners without competing.
    small_page(img, (430, 470), -16, SKY, 0.46)
    small_page(img, (3400, 420), 14, TEAL, 0.44)
    small_page(img, (330, 1250), 10, AMBER, 0.38)
    small_page(img, (3520, 1300), -12, PLUM, 0.4)

    # The fan: back pages first, the signed page in front, all in the upper middle.
    cx, cy, z = 1920, 790, 1.13
    place(img, picture_page(), (cx - 700, cy + 80), 17, zoom=z)
    place(img, table_page(), (cx + 700, cy + 80), -17, zoom=z)
    place(img, stamp_page(), (cx - 360, cy + 22), 8, zoom=z)
    place(img, highlight_page(), (cx + 360, cy + 22), -8, zoom=z)
    place(img, signature_page(), (cx, cy), 0, zoom=z)
    pen(img, (cx + 300, cy + 440), 32)
    swoosh(img)

    d = ImageDraw.Draw(img)
    sparkle(d, cx - 1010, cy - 430, 46, CORAL)
    sparkle(d, cx - 930, cy - 330, 22, AMBER)
    sparkle(d, cx + 1000, cy - 470, 38, SKY)
    sparkle(d, cx + 1080, cy - 380, 18, CORAL)
    sparkle(d, cx + 960, cy + 470, 30, TEAL)
    sparkle(d, cx - 1050, cy + 450, 26, PLUM)
    for x, y, r, c in [(cx - 1180, cy - 80, 12, CORAL), (cx + 1190, cy + 60, 14, AMBER), (cx - 760, cy - 560, 10, TEAL), (cx + 780, cy - 590, 11, PLUM)]:
        d.ellipse((x - r, y - r, x + r, y + r), fill=c)

    img = img.convert('RGB')
    OUT.mkdir(parents=True, exist_ok=True)
    img.save(OUT / 'HeroArt-16x9-3840x2160.png', optimize=True)
    img.resize((1920, 1080), Image.LANCZOS).save(OUT / 'HeroArt-16x9-1920x1080.png', optimize=True)
    print('Saved the hero art')


if __name__ == '__main__':
    main()
