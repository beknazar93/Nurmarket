#!/usr/bin/env python3
# 2026-10-27: картинки 10 тем сайта для магазина одежды (ТЗ «Сайт — темы для одежды», 3.6): обложка главного блока 1920x640,
# заглушка товара 600x800 (3:4). Всё рисуется кодом по цветам темы (Pillow), без внешних ресурсов и без текста на картинке.
# Превью-миниатюры (_thumb.png 480x300) рисует программа — стенд site-themes-clothing, сервис SiteThemePreview.
# Запуск из корня репозитория: python -I tools/qa/site_themes_make_images.py
import json
import math
import os
import random
import sys

from PIL import Image, ImageDraw

ROOT = os.path.join("NurMarketKassa.Avalonia", "NurMarketKassa.Avalonia", "Assets", "SiteThemes", "clothing")


def rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def mix(a, b, t):
    return tuple(int(round(a[i] * (1 - t) + b[i] * t)) for i in range(3))


def lum(c):
    def f(v):
        v /= 255
        return v / 12.92 if v <= 0.03928 else ((v + 0.055) / 1.055) ** 2.4
    return 0.2126 * f(c[0]) + 0.7152 * f(c[1]) + 0.0722 * f(c[2])


# ---------------------------------------------------------------- вешалка и одежда (координаты — в долях высоты h, центр cx, верх y0)

def hanger(d, cx, y0, w, col):
    d.arc((cx - w * 0.06, y0 - w * 0.16, cx + w * 0.06, y0 - w * 0.02), 180, 450, fill=col, width=max(2, int(w * 0.018)))
    d.line((cx, y0 - w * 0.02, cx, y0 + w * 0.04), fill=col, width=max(2, int(w * 0.018)))
    d.line((cx, y0 + w * 0.04, cx - w * 0.45, y0 + w * 0.16), fill=col, width=max(2, int(w * 0.018)))
    d.line((cx, y0 + w * 0.04, cx + w * 0.45, y0 + w * 0.16), fill=col, width=max(2, int(w * 0.018)))
    d.line((cx - w * 0.45, y0 + w * 0.16, cx + w * 0.45, y0 + w * 0.16), fill=col, width=max(2, int(w * 0.018)))


def dress(d, cx, y0, w, h, col):
    s = y0 + w * 0.16
    d.polygon([(cx - w * 0.40, s), (cx - w * 0.12, s - w * 0.02), (cx + w * 0.12, s - w * 0.02), (cx + w * 0.40, s),
               (cx + w * 0.22, s + h * 0.30), (cx + w * 0.55, s + h), (cx - w * 0.55, s + h), (cx - w * 0.22, s + h * 0.30)], fill=col)


def shirt(d, cx, y0, w, h, col, long=False):
    s = y0 + w * 0.16
    body = h * (0.78 if not long else 0.95)
    d.polygon([(cx - w * 0.45, s), (cx - w * 0.10, s - w * 0.02), (cx + w * 0.10, s - w * 0.02), (cx + w * 0.45, s),
               (cx + w * 0.62, s + h * 0.38), (cx + w * 0.46, s + h * 0.42), (cx + w * 0.40, s + h * 0.30),
               (cx + w * 0.40, s + body), (cx - w * 0.40, s + body), (cx - w * 0.40, s + h * 0.30),
               (cx - w * 0.46, s + h * 0.42), (cx - w * 0.62, s + h * 0.38)], fill=col)


def trousers(d, cx, y0, w, h, col):
    s = y0 + w * 0.16
    d.polygon([(cx - w * 0.40, s), (cx + w * 0.40, s), (cx + w * 0.46, s + h * 0.95), (cx + w * 0.06, s + h * 0.95),
               (cx, s + h * 0.30), (cx - w * 0.06, s + h * 0.95), (cx - w * 0.46, s + h * 0.95)], fill=col)


def coat(d, cx, y0, w, h, col, line):
    shirt(d, cx, y0, w * 1.08, h * 1.05, col, long=True)
    s = y0 + w * 0.16
    d.line((cx, s, cx, s + h * 0.98), fill=line, width=max(2, int(w * 0.02)))
    d.line((cx - w * 0.10, s, cx, s + h * 0.22), fill=line, width=max(2, int(w * 0.02)))
    d.line((cx + w * 0.10, s, cx, s + h * 0.22), fill=line, width=max(2, int(w * 0.02)))


GARMENTS = ["dress", "shirt", "coat", "trousers", "dress", "shirt", "coat"]


def draw_garment(d, kind, cx, y0, w, h, col, line):
    if kind == "dress":
        dress(d, cx, y0, w, h * 0.95, col)
    elif kind == "shirt":
        shirt(d, cx, y0, w, h * 0.7, col)
    elif kind == "coat":
        coat(d, cx, y0, w, h * 0.95, col, line)
    else:
        trousers(d, cx, y0, w, h * 0.85, col)


# ---------------------------------------------------------------- фоновые узоры тем

def motif(d, code, W, H, c, S):
    acc, txt, bg, sur, bor = c["accent"], c["text"], c["background"], c["surface"], c["border"]
    rnd = random.Random(code)
    if code == "cl_minimal":
        for y in range(0, H, int(80 * S)):
            d.line((0, y, W, y), fill=mix(bg, bor, 0.7), width=int(2 * S))
    elif code == "cl_boutique":
        r = H * 0.62
        d.ellipse((W * 0.62 - r, H * 0.52 - r, W * 0.62 + r, H * 0.52 + r), fill=mix(bg, sur, 0.8))
        d.ellipse((W * 0.62 - r * 0.8, H * 0.52 - r * 0.8, W * 0.62 + r * 0.8, H * 0.52 + r * 0.8), fill=mix(bg, bor, 0.45))
    elif code == "cl_street":
        for i in range(-4, 16):
            x = i * 150 * S
            d.polygon([(x, H), (x + 70 * S, H), (x + 70 * S + H * 0.45, H * 0.55), (x + H * 0.45, H * 0.55)], fill=mix(bg, acc, 0.12))
        d.rectangle((0, H - 26 * S, W, H - 10 * S), fill=acc)
    elif code == "cl_kids":
        cols = [c["accent"], c["badge_new_bg"], c["badge_sale_bg"], mix(rgb_c(c, "accent"), (255, 255, 255), 0.5)]
        for _ in range(46):
            x, y, r = rnd.uniform(0, W), rnd.uniform(0, H), rnd.uniform(10, 34) * S
            col = mix(bg, rnd.choice(cols), 0.45)
            if rnd.random() < 0.5:
                d.ellipse((x - r, y - r, x + r, y + r), fill=col)
            else:
                d.polygon([(x, y - r), (x + r, y + r), (x - r, y + r)], fill=col)
    elif code == "cl_menswear":
        for x in range(0, int(W), int(46 * S)):
            d.line((x, 0, x, H), fill=mix(bg, txt, 0.07), width=int(2 * S))
    elif code == "cl_sport":
        for i in range(6):
            x = W * (0.05 + 0.17 * i)
            d.polygon([(x, H), (x + 60 * S, H), (x + 60 * S + H * 0.7, 0), (x + H * 0.7, 0)], fill=mix(bg, acc, 0.10 + 0.03 * (i % 2)))
        d.polygon([(0, H * 0.9), (W, H * 0.62), (W, H * 0.7), (0, H * 0.98)], fill=mix(bg, txt, 0.85) if lum(bg) > 0.5 else acc)
    elif code == "cl_modest":
        aw = 260 * S
        for i in range(-1, int(W / aw) + 2):
            x = i * aw + aw / 2
            d.rectangle((x - aw * 0.38, H * 0.35, x + aw * 0.38, H), fill=mix(bg, acc, 0.10))
            d.pieslice((x - aw * 0.38, H * 0.35 - aw * 0.38, x + aw * 0.38, H * 0.35 + aw * 0.38), 180, 360, fill=mix(bg, acc, 0.10))
            d.rectangle((x - aw * 0.30, H * 0.45, x + aw * 0.30, H), fill=mix(bg, sur, 0.5))
            d.pieslice((x - aw * 0.30, H * 0.45 - aw * 0.30, x + aw * 0.30, H * 0.45 + aw * 0.30), 180, 360, fill=mix(bg, sur, 0.5))
    elif code == "cl_evening":
        r = H * 0.7
        for k, t in enumerate((0.55, 0.35, 0.2)):
            rr = r - k * 38 * S
            d.arc((W * 0.5 - rr, H * 0.95 - rr, W * 0.5 + rr, H * 0.95 + rr), 180, 360, fill=mix(bg, acc, t), width=int(3 * S))
    elif code == "cl_dark_luxe":
        gold = c["accent"]
        d.rectangle((24 * S, 24 * S, W - 24 * S, H - 24 * S), outline=mix(bg, gold, 0.55), width=int(2 * S))
        d.rectangle((38 * S, 38 * S, W - 38 * S, H - 38 * S), outline=mix(bg, gold, 0.25), width=int(1 * S))
        r = H * 0.5
        d.ellipse((W * 0.5 - r, H * 0.5 - r, W * 0.5 + r, H * 0.5 + r), outline=mix(bg, gold, 0.25), width=int(2 * S))
    elif code == "cl_bazaar":
        n = 16
        sw = W / n
        for i in range(n):
            col = acc if i % 2 == 0 else sur
            d.rectangle((i * sw, 0, (i + 1) * sw, 70 * S), fill=col)
            d.pieslice((i * sw, 35 * S, (i + 1) * sw, 105 * S), 0, 180, fill=col)
        for _ in range(30):
            x, y, r = rnd.uniform(0, W), rnd.uniform(130 * S, H), rnd.uniform(8, 20) * S
            d.ellipse((x - r, y - r, x + r, y + r), fill=mix(bg, acc, 0.18))


def rgb_c(c, k):
    return c[k]


def load_theme(code):
    with open(os.path.join(ROOT, code + ".json"), encoding="utf-8") as fh:
        doc = json.load(fh)
    pal = {k: rgb(v) for k, v in doc["patch"]["theme"]["colors"].items()}
    return doc, pal


def hero(code, pal, align):
    S = 2
    W, H = 1920 * S, 640 * S
    bg = pal["background"]
    img = Image.new("RGB", (W, H), bg)
    # мягкий вертикальный градиент: от фона к поверхности/границе
    top = mix(bg, pal["surface"], 0.5)
    bottom = mix(bg, pal["border"], 0.6)
    g = ImageDraw.Draw(img)
    for y in range(0, H, 4):
        g.rectangle((0, y, W, y + 4), fill=mix(top, bottom, y / H))
    d = ImageDraw.Draw(img)
    motif(d, code, W, H, pal, S)
    dark = lum(bg) < 0.2
    rail = mix(pal["text"], bg, 0.35)
    line = mix(pal["text"], bg, 0.5)
    rail_y = 70 * S
    x_from, x_to = (W * 0.42, W * 0.97) if align == "left" else (W * 0.06, W * 0.94)
    d.line((x_from - 40 * S, rail_y, x_to + 40 * S, rail_y), fill=rail, width=int(8 * S))
    cols = [pal["accent"], mix(pal["text"], bg, 0.25), mix(pal["accent"], bg, 0.5), mix(pal["text"], bg, 0.6), mix(pal["accent"], pal["text"], 0.5), pal["badge_sale_bg"], mix(pal["accent"], bg, 0.3)]
    if lum(bg) > 0.5:
        # на светлом фоне светлые заливки (акцент, смешанный с фоном) должны отличаться от фона
        cols = [mix(c, pal["text"], 0.12) if lum(c) > lum(bg) - 0.04 else c for c in cols]
    n = 6 if align == "left" else 8
    step = (x_to - x_from) / (n - 1)
    rnd = random.Random(code + "h")
    for i in range(n):
        cx = x_from + i * step
        w = min(rnd.uniform(200, 250) * S, step * 0.78)
        h = rnd.uniform(380, 450) * S
        kind = GARMENTS[(i + len(code)) % len(GARMENTS)]
        col = cols[(i * 3 + len(code)) % len(cols)]
        hanger(d, cx, rail_y, w, rail)
        draw_garment(d, kind, cx, rail_y, w, h, col, line)
    return img.resize((W // S, H // S), Image.LANCZOS)


def placeholder(code, pal):
    S = 2
    W, H = 600 * S, 800 * S
    bg = mix(pal["surface"], pal["border"], 0.55) if lum(pal["background"]) > 0.2 else mix(pal["surface"], pal["border"], 0.7)
    img = Image.new("RGB", (W, H), bg)
    d = ImageDraw.Draw(img)
    r = 330 * S
    d.ellipse((W / 2 - r, H * 0.52 - r, W / 2 + r, H * 0.52 + r), fill=mix(bg, pal["surface"], 0.55))
    col = mix(bg, pal["text_muted"], 0.38)
    cx, y0, w = W / 2, H * 0.17, 300 * S
    hanger(d, cx, y0, w, col)
    s = y0 + w * 0.16
    d.polygon([(cx - w * 0.40, s), (cx - w * 0.12, s - w * 0.02), (cx + w * 0.12, s - w * 0.02), (cx + w * 0.40, s),
               (cx + w * 0.22, s + 190 * S), (cx + w * 0.62, s + 430 * S), (cx - w * 0.62, s + 430 * S), (cx - w * 0.22, s + 190 * S)], fill=col)
    return img.resize((W // S, H // S), Image.LANCZOS)


def main():
    codes = json.load(open(os.path.join(ROOT, "index.json"), encoding="utf-8"))
    for code in codes:
        doc, pal = load_theme(code)
        align = doc["patch"]["hero"]["align"]
        h = hero(code, pal, align)
        q = 82
        while True:
            path = os.path.join(ROOT, code + "_hero.jpg")
            h.save(path, "JPEG", quality=q, optimize=True, progressive=True)
            if os.path.getsize(path) <= 250 * 1024 or q <= 40:
                break
            q -= 6
        p = placeholder(code, pal)
        q = 82
        while True:
            path = os.path.join(ROOT, code + "_ph.jpg")
            p.save(path, "JPEG", quality=q, optimize=True)
            if os.path.getsize(path) <= 60 * 1024 or q <= 40:
                break
            q -= 6
        print(code, os.path.getsize(os.path.join(ROOT, code + "_hero.jpg")) // 1024, "KB hero,", os.path.getsize(os.path.join(ROOT, code + "_ph.jpg")) // 1024, "KB ph")
    return 0


if __name__ == "__main__":
    sys.exit(main())
