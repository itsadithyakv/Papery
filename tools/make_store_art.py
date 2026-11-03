"""
Turns the app screenshots in branding/screenshots into framed Store images in branding/store-screenshots:
warm paper with soft colour, a short headline, a hand-drawn underline, and the app window.
Same look as the app: brand coral, the tool family colours, Segoe UI Variable, doodles like the tour's.

Needs Python 3 with Pillow (pip install pillow). Run after tools/Make-Screenshots.ps1:
    python tools/make_store_art.py
"""
import math
import random
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent.parent
SHOTS = ROOT / 'branding' / 'screenshots'
OUT = ROOT / 'branding' / 'store-screenshots'
FONT = r'C:\Windows\Fonts\SegUIVar.ttf'

W, H = 3840, 2160
PAPER = (250, 248, 245)
INK = (44, 36, 32)
QUIET = (107, 98, 91)
CORAL = (229, 83, 61)
CORAL_TEXT = (204, 68, 48)
FOLD = (250, 205, 196)

# The tool family colours from ToolCatalog.cs.
SKY, TEAL, AMBER, PLUM, SAGE, SLATE = (59, 130, 196), (42, 157, 143), (224, 142, 31), (142, 91, 181), (78, 154, 107), (107, 122, 143)

# (file, screenshot, headline lines, accent word, line below, family colour). Store order: best first.
SLIDES = [
    ('01-every-tool', '01-home-tools', ['Every PDF tool,', 'in one calm app'], 'calm', 'Free to read. Pay once for the rest.', CORAL),
    ('02-redact', '07-redact', ['Private stays', 'private'], 'private', 'Redaction that really removes it', SLATE),
    ('03-filters', '09-filters', ['Scans,', 'cleaned up'], 'cleaned', 'Whiter paper, darker text, no shadows', TEAL),
    ('04-sign', '04-fill-and-sign', ['Sign in', 'seconds'], 'seconds', 'Draw, type or import your signature', CORAL),
    ('05-organise', '03-organise', ['Pages in', 'the right order'], 'order', 'Reorder, turn, delete and extract', SKY),
    ('06-merge', '06-merge', ['Many files,', 'one PDF'], 'one', 'With a bookmark for each file', SKY),
    ('07-compress', '05-compress', ['Smaller,', 'still sharp'], 'Smaller,', 'Fit under any size you choose', TEAL),
    ('08-annotate', '08-annotate', ['Mark up', 'anything'], 'anything', 'Highlight, draw, stamp and comment', AMBER),
    ('09-reading', '02-reading', ['Big files,', 'open fast'], 'fast', 'Free to read, with tabs and dark mode', SAGE),
]


def font(size, style):
    f = ImageFont.truetype(FONT, size)
    f.set_variation_by_name(style)
    return f


def mix(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def background(tint, seed):
    rng = random.Random(seed)
    img = Image.new('RGB', (W, H), mix(PAPER, tint, 0.06))
    blobs = Image.new('RGBA', (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(blobs)
    # Big soft shapes: one in the family colour behind the window, one in coral behind the words.
    d.ellipse((2000 + rng.randint(-120, 120), -500, 4300, 1700 + rng.randint(-100, 100)), fill=tint + (60,))
    d.ellipse((-700, 1250 + rng.randint(-100, 100), 1400, 2900), fill=CORAL + (34,))
    blobs = blobs.filter(ImageFilter.GaussianBlur(220))
    img.paste(blobs, (0, 0), blobs)
    return img


def wobbly(points, seed, amount):
    """Moves points a little with slow waves, like a steady hand: the same seed draws the same line."""
    rng = random.Random(seed)
    a, b, p, q = rng.uniform(0, 6), rng.uniform(0, 6), rng.uniform(0.6, 1.0), rng.uniform(1.3, 2.0)
    out = []
    n = len(points)
    for i, (x, y) in enumerate(points):
        t = i / max(1, n - 1)
        out.append((x + amount * math.sin(a + t * 6.3 * p) * 0.6, y + amount * math.sin(b + t * 6.3 * q)))
    return out


def stroke(draw, points, width, colour):
    draw.line(points, fill=colour, width=width, joint='curve')
    r = width / 2
    for x, y in (points[0], points[-1]):
        draw.ellipse((x - r, y - r, x + r, y + r), fill=colour)


def doodles(underline, seed):
    """Drawn at twice the size and shrunk, so the pen lines come out smooth."""
    s = 2
    layer = Image.new('RGBA', (W * s, H * s), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    # A wavy underline that runs a little past the word, as a pen would.
    (x0, x1, y) = underline
    pts = [((x0 - 10 + (x1 - x0 + 30) * i / 60) * s, (y + 9 * math.sin(i / 60 * math.pi * 3.2)) * s) for i in range(61)]
    stroke(d, wobbly(pts, seed, 5 * s), 13 * s, CORAL + (255,))

    return layer.resize((W, H), Image.LANCZOS)


def sparkle(draw, x, y, r, colour):
    """A small four-point twinkle."""
    pts = []
    for i in range(8):
        a = i * math.pi / 4 - math.pi / 2
        rr = r if i % 2 == 0 else r * 0.28
        pts.append((x + math.cos(a) * rr, y + math.sin(a) * rr))
    draw.polygon(pts, fill=colour)


def logo(img, x, y, size):
    s = size / 100
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((x, y, x + size, y + size), radius=20 * s, fill=CORAL)
    d.polygon([(x + 27 * s, y + 19 * s), (x + 57 * s, y + 19 * s), (x + 73 * s, y + 35 * s), (x + 73 * s, y + 81 * s), (x + 27 * s, y + 81 * s)], fill=(255, 255, 255))
    d.polygon([(x + 57 * s, y + 19 * s), (x + 73 * s, y + 35 * s), (x + 57 * s, y + 35 * s)], fill=FOLD)


def window(img, shot_path, box):
    """The screenshot with rounded corners, a hairline edge and a soft shadow, as if lifted off the page."""
    x, y, w = box
    shot = Image.open(shot_path).convert('RGB')
    h = round(shot.height * w / shot.width)
    shot = shot.resize((w, h), Image.LANCZOS)
    radius = 30
    mask = Image.new('L', (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w - 1, h - 1), radius=radius, fill=255)

    shadow = Image.new('RGBA', (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((x + 10, y + 50, x + w - 10, y + h + 40), radius=radius, fill=(60, 40, 30, 95))
    shadow = shadow.filter(ImageFilter.GaussianBlur(70))
    img.paste(shadow, (0, 0), shadow)
    img.paste(shot, (x, y), mask)
    ImageDraw.Draw(img).rounded_rectangle((x, y, x + w - 1, y + h - 1), radius=radius, outline=(0, 0, 0), width=2)
    return h


def slide(name, shot, lines, accent, below, tint, seed):
    img = background(tint, seed)
    draw = ImageDraw.Draw(img)

    logo(img, 170, 150, 104)
    draw.text((300, 156), 'Papery', font=font(78, 'Semibold Display'), fill=INK)

    # The window sits whole on the right, a little below the middle.
    win_x, win_w = 1500, 2180
    win_h = round(1125 * win_w / 1800)
    win_y = (H - win_h) // 2 + 70
    window(img, SHOTS / f'{shot}-Dark.png', (win_x, win_y, win_w))

    # The headline is as big as the words column allows, up to a limit.
    column = win_x - 170 - 130
    size = 160
    while size > 90 and max(draw.textlength(t, font=font(size, 'Bold Display')) for t in lines) > column:
        size -= 4
    head = font(size, 'Bold Display')
    sub = font(64, 'Semilight Text')
    line_h = round(size * 1.16)
    block = len(lines) * line_h + 76 + 80
    top = (H - block) // 2 + 30
    underline = None
    for i, text in enumerate(lines):
        y = top + i * line_h
        x = 170
        for word in text.split(' '):
            colour = CORAL_TEXT if word == accent else INK
            draw.text((x, y), word, font=head, fill=colour)
            left, _, right, bottom = draw.textbbox((x, y), word, font=head)
            if word == accent and underline is None:
                underline = (left, right, bottom + 20)
            x = right + head.getlength(' ')
    sub_y = top + len(lines) * line_h + 76
    draw.text((176, sub_y), below, font=sub, fill=QUIET)

    # Two twinkles in the family colour, above the words.
    widest = max(draw.textlength(t, font=head) for t in lines)
    sparkle(draw, 170 + widest + 20, top - 40, 34, tint)
    sparkle(draw, 170 + widest + 80, top + 18, 18, CORAL)

    layer = doodles(underline, seed)
    img.paste(layer, (0, 0), layer)

    OUT.mkdir(parents=True, exist_ok=True)
    img.save(OUT / f'{name}.png', optimize=True)


if __name__ == '__main__':
    for n, (name, shot, lines, accent, below, tint) in enumerate(SLIDES):
        slide(name, shot, lines, accent, below, tint, seed=11 + n * 7)
        print('Saved', name)
