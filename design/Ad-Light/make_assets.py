# -*- coding: utf-8 -*-
# 2026-10-29: генератор SVG-ассетов ролика «Свет погас» (design/Ad-Light).
# Запуск: python make_assets.py — перезаписывает *.svg и character-sheet.html рядом со скриптом.
# Один плоский стиль: контур #191919 (6 px), палитра 12 цветов, сцена 1920×1080, персонаж 400×720 (ноги на y=700).
# Ярусы персонажа — отдельные <g id="…"> с data-pivot="x y" (точка вращения) и готовым transform=rotate(...).
import os, math

OUT = os.path.dirname(os.path.abspath(__file__))

# ---- палитра (12 цветов) --------------------------------------------------
INK = "#191919"; YEL = "#FFB208"; WHITE = "#FFFFFF"; CREAM = "#FFF1DB"
SKIN = "#F2C29B"; BROWN = "#8A5A3C"; BLUE = "#2F5FD0"; RED = "#C8352E"
GREEN = "#2E8B57"; GREY = "#6B6B6B"; LGREY = "#DADADA"; SKY = "#5BB8D4"
PALETTE = [
    ("ink", INK, "контур, тёмный фон, волосы"), ("yellow", YEL, "фирменный жёлтый: фартук, свет телефона, плашки"),
    ("white", WHITE, "калпак, чек, экран"), ("cream", CREAM, "стены, лепёшка, свет лампы"),
    ("skin", SKIN, "кожа"), ("brown", BROWN, "дерево полок, хлеб, прилавок"),
    ("blue", BLUE, "одежда, тетрадка"), ("red", RED, "платок апы, злость, корзина"),
    ("green", GREEN, "жилет хозяина, яблоки, купюры"), ("grey", GREY, "пол, кепка, металл"),
    ("lgrey", LGREY, "седина, калькулятор, плитка"), ("sky", SKY, "молоко, окно, наушники"),
]
SW = 6
FONT = "Montserrat, Arial, sans-serif"


def esc(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def attrs(**kw):
    return "".join(f' {k.replace("_", "-")}="{v}"' for k, v in kw.items() if v is not None)


def el(tag, inner="", **kw):
    return f"<{tag}{attrs(**kw)}>{inner}</{tag}>" if inner else f"<{tag}{attrs(**kw)}/>"


def g(inner, **kw):
    return el("g", inner, **kw)


def circle(cx, cy, r, fill, stroke=INK, sw=SW, **kw):
    return el("circle", cx=cx, cy=cy, r=r, fill=fill, stroke=stroke or "none", stroke_width=sw if stroke else None, **kw)


def ellipse(cx, cy, rx, ry, fill, stroke=INK, sw=SW, **kw):
    return el("ellipse", cx=cx, cy=cy, rx=rx, ry=ry, fill=fill, stroke=stroke or "none", stroke_width=sw if stroke else None, **kw)


def rect(x, y, w, h, fill, rx=0, stroke=INK, sw=SW, **kw):
    return el("rect", x=x, y=y, width=w, height=h, rx=rx, fill=fill, stroke=stroke or "none", stroke_width=sw if stroke else None, **kw)


def path(d, fill="none", stroke=INK, sw=SW, cap="round", **kw):
    return el("path", d=d, fill=fill, stroke=stroke or "none", stroke_width=sw if stroke else None,
              stroke_linecap=cap, stroke_linejoin="round", **kw)


def line(x1, y1, x2, y2, stroke=INK, sw=SW):
    return el("path", d=f"M{x1} {y1} L{x2} {y2}", fill="none", stroke=stroke, stroke_width=sw, stroke_linecap="round")


def limb(x1, y1, x2, y2, color, w=24):
    return line(x1, y1, x2, y2, INK, w + 2 * SW) + line(x1, y1, x2, y2, color, w)


def text(x, y, s, size, fill=INK, weight=800, anchor="middle", **kw):
    return el("text", esc(s), x=x, y=y, font_family=FONT, font_size=size, font_weight=weight,
              fill=fill, text_anchor=anchor, **kw)


def svg(w, h, inner, title, extra_defs=""):
    return (f'<?xml version="1.0" encoding="UTF-8"?>\n'
            f'<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" '
            f'width="{w}" height="{h}" viewBox="0 0 {w} {h}">\n<title>{esc(title)}</title>\n'
            f'<defs>{extra_defs}</defs>\n{inner}\n</svg>\n')


# ---- кыргызский узор «кочкор мүйүз» (рога барана) --------------------------
def horn(x, y, s, color, sw=3):
    return path(f"M{x} {y} c0 {-s} {-s} {-s} {-s} {-s * 0.25} M{x} {y} c0 {-s} {s} {-s} {s} {-s * 0.25}",
                stroke=color, sw=sw)


def horn_row(x0, x1, y, s, color, step=None):
    step = step or s * 3
    out = []
    x = x0 + s * 1.5
    while x < x1 - s:
        out.append(horn(x, y, s, color))
        x += step
    return "".join(out)


# ---- реквизит (центр x,y; масштаб s) --------------------------------------
def p_phone_scan(x, y, s=1.0, glow=True):
    o = []
    if glow:
        o.append(circle(x, y, 95 * s, YEL, stroke=None, opacity="0.35"))
    o.append(rect(x - 36 * s, y - 68 * s, 72 * s, 136 * s, INK, rx=10 * s))
    o.append(rect(x - 30 * s, y - 60 * s, 60 * s, 120 * s, WHITE, rx=4 * s, stroke=None))
    o.append(rect(x - 30 * s, y - 60 * s, 60 * s, 16 * s, YEL, stroke=None))
    # видоискатель
    o.append(rect(x - 22 * s, y - 38 * s, 44 * s, 40 * s, LGREY, rx=3 * s, stroke=None))
    o.append(path(f"M{x - 20 * s} {y - 30 * s} v{-6 * s} h{6 * s} M{x + 20 * s} {y - 30 * s} v{-6 * s} h{-6 * s} "
                  f"M{x - 20 * s} {y - 6 * s} v{6 * s} h{6 * s} M{x + 20 * s} {y - 6 * s} v{6 * s} h{-6 * s}",
                  stroke=YEL, sw=3 * s))
    o.append(line(x - 16 * s, y - 18 * s, x + 16 * s, y - 18 * s, RED, 3 * s))
    for i in range(5):
        o.append(line(x - 18 * s + i * 7 * s, y - 30 * s, x - 18 * s + i * 7 * s, y - 8 * s, INK, (2 if i % 2 else 3) * s))
    # строки чека
    for i in range(3):
        o.append(line(x - 22 * s, y + 12 * s + i * 12 * s, x + 22 * s, y + 12 * s + i * 12 * s, LGREY, 4 * s))
    o.append(rect(x - 22 * s, y + 44 * s, 44 * s, 10 * s, YEL, rx=3 * s, stroke=None))
    return "".join(o)


def p_phone_dark(x, y, s=1.0):
    o = [rect(x - 36 * s, y - 68 * s, 72 * s, 136 * s, INK, rx=10 * s),
         rect(x - 30 * s, y - 60 * s, 60 * s, 120 * s, GREY, rx=4 * s, stroke=None),
         circle(x, y - 48 * s, 7 * s, YEL, stroke=None)]
    return "".join(o)


def p_calculator(x, y, s=1.0):
    o = [rect(x - 40 * s, y - 56 * s, 80 * s, 112 * s, LGREY, rx=8 * s),
         rect(x - 32 * s, y - 48 * s, 64 * s, 24 * s, WHITE, rx=3 * s)]
    o.append(text(x + 26 * s, y - 30 * s, "0.", 16 * s, INK, 700, "end"))
    for r in range(4):
        for c in range(4):
            col = YEL if (c == 3 and r == 3) else (INK if c < 3 else RED)
            o.append(rect(x - 32 * s + c * 17 * s, y - 16 * s + r * 16 * s, 13 * s, 12 * s, col, rx=2 * s, stroke=None))
    return "".join(o)


def p_notebook(x, y, s=1.0):
    o = [rect(x - 44 * s, y - 58 * s, 88 * s, 116 * s, WHITE, rx=4 * s)]
    for i in range(7):
        o.append(line(x - 34 * s, y - 40 * s + i * 14 * s, x + 34 * s, y - 40 * s + i * 14 * s, SKY, 2 * s))
    o.append(line(x - 24 * s, y - 52 * s, x - 24 * s, y + 52 * s, RED, 2 * s))
    for i in range(6):
        o.append(circle(x - 44 * s, y - 44 * s + i * 18 * s, 4 * s, GREY, INK, 2 * s))
    o.append(path(f"M{x - 18 * s} {y - 26 * s} l{12 * s} 0 m{6 * s} 0 l{18 * s} 0 M{x - 18 * s} {y - 12 * s} l{30 * s} 0 "
                  f"M{x - 18 * s} {y + 2 * s} l{20 * s} 0 m{6 * s} 0 l{10 * s} 0", stroke=INK, sw=3 * s))
    return "".join(o)


def p_lepyoshka(x, y, s=1.0):
    o = [ellipse(x, y, 46 * s, 26 * s, BROWN), ellipse(x, y, 30 * s, 15 * s, CREAM, BROWN, 3 * s)]
    for i in range(8):
        a = i * math.pi / 4
        o.append(circle(x + 18 * s * math.cos(a), y + 9 * s * math.sin(a), 2.5 * s, BROWN, stroke=None))
    return "".join(o)


def p_bread(x, y, s=1.0):
    o = [path(f"M{x - 50 * s} {y} q0 {-26 * s} {30 * s} {-26 * s} h{40 * s} q{30 * s} 0 {30 * s} {26 * s} "
              f"q0 {22 * s} {-30 * s} {22 * s} h{-40 * s} q{-30 * s} 0 {-30 * s} {-22 * s} z", BROWN)]
    for i in range(4):
        o.append(line(x - 30 * s + i * 20 * s, y - 16 * s, x - 18 * s + i * 20 * s, y - 2 * s, CREAM, 4 * s))
    return "".join(o)


def p_milk(x, y, s=1.0):
    o = [path(f"M{x - 26 * s} {y + 50 * s} v{-72 * s} l{10 * s} {-22 * s} h{32 * s} l{10 * s} {22 * s} v{72 * s} z", SKY),
         rect(x - 10 * s, y - 48 * s, 20 * s, 8 * s, WHITE, rx=2 * s, stroke=None),
         rect(x - 18 * s, y - 6 * s, 36 * s, 30 * s, WHITE, rx=3 * s, stroke=None),
         text(x, y + 16 * s, "СҮТ", 13 * s, INK, 800)]
    return "".join(o)


def p_seeds(x, y, s=1.0):
    o = [path(f"M{x - 30 * s} {y - 50 * s} h{60 * s} l{-6 * s} {100 * s} h{-48 * s} z", CREAM),
         circle(x, y - 6 * s, 16 * s, BROWN, INK, 3 * s)]
    for i in range(10):
        a = i * math.pi / 5
        px, py = x + 24 * s * math.cos(a), y - 6 * s + 24 * s * math.sin(a)
        o.append(el("ellipse", cx=px, cy=py, rx=7 * s, ry=4 * s, fill=YEL, stroke=INK, stroke_width=2 * s,
                    transform=f"rotate({i * 36} {px} {py})"))
    o.append(text(x, y + 36 * s, "семечки", 10 * s, INK, 700))
    return "".join(o)


def p_bottle(x, y, s=1.0):
    o = [path(f"M{x - 18 * s} {y + 60 * s} v{-70 * s} q0 {-14 * s} {10 * s} {-20 * s} v{-14 * s} h{16 * s} v{14 * s} "
              f"q{10 * s} {6 * s} {10 * s} {20 * s} v{70 * s} z", SKY),
         rect(x - 8 * s, y - 50 * s, 16 * s, 10 * s, BLUE, rx=2 * s, stroke=None),
         rect(x - 14 * s, y + 4 * s, 28 * s, 24 * s, WHITE, rx=2 * s, stroke=None)]
    return "".join(o)


def p_bag(x, y, s=1.0):  # клетчатый баул
    o = [rect(x - 44 * s, y - 30 * s, 88 * s, 70 * s, WHITE, rx=6 * s)]
    for i in range(1, 4):
        o.append(line(x - 44 * s + i * 22 * s, y - 30 * s, x - 44 * s + i * 22 * s, y + 40 * s, BLUE, 5 * s))
    for i in range(1, 3):
        o.append(line(x - 44 * s, y - 30 * s + i * 23 * s, x + 44 * s, y - 30 * s + i * 23 * s, RED, 5 * s))
    o.append(path(f"M{x - 20 * s} {y - 30 * s} q{20 * s} {-34 * s} {40 * s} 0", stroke=INK, sw=6 * s))
    return "".join(o)


def p_receipt(x, y, s=1.0):
    o = [path(f"M{x - 32 * s} {y - 60 * s} h{64 * s} v{112 * s} l{-8 * s} {8 * s} l{-8 * s} {-8 * s} l{-8 * s} {8 * s} "
              f"l{-8 * s} {-8 * s} l{-8 * s} {8 * s} l{-8 * s} {-8 * s} l{-8 * s} {8 * s} l{-8 * s} {-8 * s} z", WHITE)]
    o.append(rect(x - 22 * s, y - 50 * s, 44 * s, 8 * s, YEL, rx=2 * s, stroke=None))
    for i in range(5):
        o.append(line(x - 22 * s, y - 30 * s + i * 14 * s, x + (22 if i < 4 else 6) * s, y - 30 * s + i * 14 * s, LGREY, 4 * s))
    o.append(line(x - 22 * s, y + 40 * s, x + 22 * s, y + 40 * s, INK, 4 * s))
    return "".join(o)


def p_basket(x, y, s=1.0):
    o = [path(f"M{x - 50 * s} {y - 10 * s} h{100 * s} l{-12 * s} {60 * s} h{-76 * s} z", RED),
         path(f"M{x - 30 * s} {y - 10 * s} q{30 * s} {-50 * s} {60 * s} 0", stroke=INK, sw=8 * s),
         circle(x - 18 * s, y - 16 * s, 14 * s, GREEN), circle(x + 10 * s, y - 20 * s, 14 * s, GREEN),
         circle(x + 30 * s, y - 12 * s, 12 * s, RED)]
    return "".join(o)


def p_eggs(x, y, s=1.0):
    o = [rect(x - 50 * s, y - 20 * s, 100 * s, 44 * s, LGREY, rx=4 * s)]
    for i in range(5):
        o.append(ellipse(x - 40 * s + i * 20 * s, y - 14 * s, 8 * s, 10 * s, CREAM, INK, 3 * s))
    return "".join(o)


def p_money(x, y, s=1.0):
    o = [rect(x - 40 * s, y - 20 * s, 80 * s, 40 * s, GREEN, rx=4 * s), circle(x, y, 10 * s, CREAM, INK, 3 * s),
         text(x - 30 * s, y - 6 * s, "200", 9 * s, CREAM, 800, "start"),
         circle(x + 34 * s, y + 20 * s, 12 * s, YEL), circle(x + 50 * s, y + 26 * s, 12 * s, YEL)]
    return "".join(o)


def p_switch(x, y, s=1.0, on=False):
    o = [rect(x - 30 * s, y - 40 * s, 60 * s, 80 * s, WHITE, rx=6 * s),
         rect(x - 18 * s, y - 26 * s, 36 * s, 52 * s, LGREY, rx=4 * s)]
    if on:
        o.append(rect(x - 16 * s, y - 24 * s, 32 * s, 24 * s, WHITE, rx=3 * s, stroke=None))
    else:
        o.append(rect(x - 16 * s, y, 32 * s, 24 * s, WHITE, rx=3 * s, stroke=None))
    return "".join(o)


def p_router(x, y, s=1.0):
    o = [line(x - 30 * s, y - 20 * s, x - 36 * s, y - 70 * s, INK, 8 * s), line(x + 30 * s, y - 20 * s, x + 36 * s, y - 70 * s, INK, 8 * s),
         rect(x - 60 * s, y - 20 * s, 120 * s, 40 * s, GREY, rx=8 * s),
         circle(x - 36 * s, y, 7 * s, RED, stroke=None), circle(x - 14 * s, y, 7 * s, INK, stroke=None), circle(x + 8 * s, y, 7 * s, INK, stroke=None)]
    return "".join(o)


def p_flashlight(x, y, s=1.0):
    o = [rect(x - 50 * s, y - 14 * s, 70 * s, 28 * s, GREY, rx=8 * s),
         path(f"M{x + 20 * s} {y - 20 * s} l{14 * s} {-6 * s} v{52 * s} l{-14 * s} {-6 * s} z", INK),
         path(f"M{x + 34 * s} {y - 26 * s} l{180 * s} {-70 * s} v{192 * s} l{-180 * s} {-70 * s} z", YEL, stroke=None, opacity="0.45")]
    return "".join(o)


PROPS = {
    "phone-scan": ("Телефон: экран сканера NurCRM", p_phone_scan),
    "phone-dark": ("Телефон: фонарик", p_phone_dark),
    "calculator": ("Калькулятор", p_calculator),
    "notebook": ("Тетрадка", p_notebook),
    "receipt": ("Чек", p_receipt),
    "basket": ("Корзина", p_basket),
    "lepyoshka": ("Лепёшка", p_lepyoshka),
    "bread": ("Хлеб (батон)", p_bread),
    "milk": ("Молоко", p_milk),
    "seeds": ("Семечки", p_seeds),
    "bottle": ("Вода", p_bottle),
    "eggs": ("Яйца", p_eggs),
    "bag": ("Баул клетчатый", p_bag),
    "money": ("Деньги", p_money),
    "switch-off": ("Выключатель: выкл", lambda x, y, s=1.0: p_switch(x, y, s, False)),
    "switch-on": ("Выключатель: вкл", lambda x, y, s=1.0: p_switch(x, y, s, True)),
    "router": ("Роутер (красный LED)", p_router),
    "flashlight": ("Фонарик с лучом", p_flashlight),
}


# ---- персонажи -------------------------------------------------------------
def face_features(mood, spec):
    o = []
    elder = spec.get("elder")
    # глаза
    if mood == "happy":
        o.append(path("M170 150 q10 -12 20 0", sw=5))
        o.append(path("M210 150 q10 -12 20 0", sw=5))
    elif mood == "angry":
        o.append(circle(180, 150, 6, INK, stroke=None)); o.append(circle(220, 150, 6, INK, stroke=None))
        o.append(line(170, 140, 192, 146, INK, 5)); o.append(line(230, 140, 208, 146, INK, 5))
    else:
        o.append(circle(180, 148, 6.5, INK, stroke=None)); o.append(circle(220, 148, 6.5, INK, stroke=None))
        o.append(circle(182, 146, 2, WHITE, stroke=None)); o.append(circle(222, 146, 2, WHITE, stroke=None))
    # брови
    if mood == "angry":
        o.append(line(164, 126, 190, 136, INK, 5)); o.append(line(236, 126, 210, 136, INK, 5))
    elif mood in ("sad", "worried"):
        o.append(line(166, 134, 190, 126, INK, 5)); o.append(line(234, 134, 210, 126, INK, 5))
    elif mood in ("happy", "surprised"):
        o.append(line(166, 126, 190, 122, INK, 5)); o.append(line(234, 126, 210, 122, INK, 5))
    else:
        o.append(line(167, 130, 191, 128, INK, 5)); o.append(line(233, 130, 209, 128, INK, 5))
    # рот
    if mood == "happy":
        o.append(path("M178 174 q22 34 44 0 z", INK, INK, 4))
        o.append(rect(186, 176, 28, 7, WHITE, rx=2, stroke=None))
    elif mood == "smile":
        o.append(path("M184 176 q16 16 32 0", sw=5))
    elif mood == "angry":
        o.append(path("M182 186 q18 -16 36 0", sw=5))
    elif mood == "sad":
        o.append(path("M186 184 q14 -10 28 0", sw=5))
    elif mood == "worried":
        o.append(path("M184 182 q8 -8 16 0 q8 8 16 0", sw=5))
    elif mood == "surprised":
        o.append(ellipse(200, 180, 8, 11, INK, stroke=None))
    else:
        o.append(line(188, 180, 212, 180, INK, 5))
    # щёки
    if mood in ("happy", "smile") or spec.get("kid"):
        o.append(circle(166, 166, 7, RED, stroke=None, ))
        o.append(circle(234, 166, 7, RED, stroke=None))
    if elder:
        o.append(path("M158 158 q4 10 10 14 M242 158 q-4 10 -10 14", sw=3))
    return "".join(o)


def hair(spec):
    """(задний слой, передний слой) причёски/головного убора"""
    st = spec["hair"]
    hc = spec.get("hair_color", INK)
    if st == "scarf":  # Айнура: белый платок с узором, чёлка
        back = circle(200, 146, 70, WHITE) + path("M254 170 q40 10 30 60 q-8 -30 -34 -36 z", WHITE)
        front = (path("M152 126 Q200 82 248 126 Q226 110 200 118 Q174 110 152 126 z", hc, INK, 4)
                 + path("M140 150 Q200 70 260 150", stroke=WHITE, sw=14)
                 + path("M140 150 Q200 70 260 150", stroke=INK, sw=4)
                 + horn(200, 90, 9, YEL) + horn(166, 112, 7, YEL) + horn(234, 112, 7, YEL))
        return back, front
    if st == "calpak":  # Токтогул-ата: белый войлочный калпак с чёрным узором, седая борода
        back = ""
        front = (path("M136 128 Q150 60 200 22 Q250 60 264 128 Q232 112 200 118 Q168 112 136 128 z", WHITE)
                 + path("M134 130 q20 -14 66 -16 q46 2 66 16 l-6 18 q-60 -18 -120 0 z", INK, INK, 4)
                 + path("M200 30 L200 112", stroke=INK, sw=4)
                 + horn(176, 96, 9, INK, 3) + horn(224, 96, 9, INK, 3)
                 + path("M156 168 Q164 222 200 232 Q236 222 244 168 Q224 190 200 186 Q176 190 156 168 z", LGREY)
                 + path("M178 170 q22 -10 44 0 q-10 8 -22 8 q-12 0 -22 -8 z", LGREY, INK, 4))
        return back, front
    if st == "mom":  # Жылдыз: длинные тёмные волосы, хвост
        back = path("M142 150 q-10 -70 58 -78 q68 8 58 78 l2 96 q-60 30 -120 0 z", hc)
        front = path("M146 142 Q180 76 230 94 Q252 110 256 142 Q240 112 214 112 Q200 124 184 112 Q160 112 146 142 z", hc, INK, 4)
        return back, front
    if st == "cap-headphones":  # Эрлан: кепка козырьком назад, наушники
        back = path("M150 118 q20 -50 100 -28 l22 20 l-10 14 z", BLUE) + path("M250 118 l34 -22 l18 10 l-28 24 z", BLUE)
        front = (path("M144 132 Q160 82 200 80 Q240 82 256 132 Q228 120 200 122 Q172 120 144 132 z", BLUE)
                 + path("M136 150 Q138 86 200 70 Q262 86 264 150", stroke=INK, sw=8)
                 + rect(126, 136, 24, 36, SKY, rx=8) + rect(250, 136, 24, 36, SKY, rx=8))
        return back, front
    if st == "kerchief":  # Бүбү-апа: красный платок в горошек, седая чёлка, очки
        back = circle(200, 148, 70, RED) + path("M172 206 q28 30 56 0 l10 40 q-38 10 -76 0 z", RED)
        dots = "".join(circle(x, y, 3, WHITE, stroke=None) for x, y in
                       [(150, 110), (170, 88), (200, 78), (230, 88), (250, 110), (160, 136), (240, 136)])
        front = (path("M154 128 Q200 90 246 128 Q226 112 200 118 Q174 112 154 128 z", LGREY, INK, 4)
                 + path("M138 150 Q200 74 262 150", stroke=RED, sw=14)
                 + path("M138 150 Q200 74 262 150", stroke=INK, sw=4) + dots
                 + circle(180, 150, 13, "none", INK, 4) + circle(220, 150, 13, "none", INK, 4) + line(193, 150, 207, 150, INK, 4))
        return back, front
    if st == "cap":  # Нурлан-байке: серая кепка, усы
        back = ""
        front = (path("M140 130 Q156 84 200 82 Q244 84 260 130 Q230 118 200 120 Q170 118 140 130 z", GREY)
                 + path("M140 130 Q200 120 260 130 L272 146 Q200 134 128 146 z", GREY)
                 + path("M176 170 q10 -12 24 -2 q14 -10 24 2 q-6 10 -24 8 q-18 2 -24 -8 z", INK, INK, 3))
        return back, front
    if st == "kid":  # Бакыт: хохолок
        back = ""
        front = (path("M156 128 Q170 100 200 104 Q230 100 244 128 Q222 118 200 120 Q178 118 156 128 z", hc, INK, 4)
                 + path("M196 106 l-6 -30 l14 14 l10 -22 l2 30", hc, INK, 4))
        return back, front
    return "", ""


def head(cid, pose, spec, mood, tilt):
    back, front = hair(spec)
    o = [back,
         circle(144, 154, 11, spec["skin"]), circle(256, 154, 11, spec["skin"]),
         circle(200, 150, 58, spec["skin"]),
         front, face_features(mood, spec)]
    return g("".join(o), id=f"{cid}-{pose}-head", data_pivot="200 205", transform=f"rotate({tilt} 200 205)")


def torso(cid, pose, spec, bob):
    o = [rect(184, 186, 32, 36, spec["skin"], rx=8)]
    o.append(path("M150 208 Q200 190 250 208 L262 402 Q200 416 138 402 Z", spec["top"]))
    if spec.get("collar"):
        o.append(path("M176 206 L200 230 L224 206", stroke=INK, sw=4))
    if spec.get("dress"):
        o.append(path("M142 330 L108 540 Q200 560 292 540 L258 330 Q200 344 142 330 Z", spec["dress"]))
    if spec.get("apron"):
        o.append(path("M166 250 L234 250 L246 480 Q200 492 154 480 Z", YEL))
        o.append(rect(178, 380, 44, 36, YEL, rx=4))
        o.append(line(166, 250, 180, 208, INK, 4)); o.append(line(234, 250, 220, 208, INK, 4))
        o.append(text(200, 290, "БЕРЕКЕ", 13, INK, 800))
    if spec.get("vest"):
        o.append(path("M150 208 L186 212 L186 400 L140 402 Z", spec["vest"]))
        o.append(path("M250 208 L214 212 L214 400 L260 402 Z", spec["vest"]))
        o.append(horn(166, 300, 8, YEL)); o.append(horn(234, 300, 8, YEL))
        o.append(horn(166, 350, 8, YEL)); o.append(horn(234, 350, 8, YEL))
    if spec.get("hoodie"):
        o.append(path("M160 214 Q200 262 240 214", stroke=INK, sw=5))
        o.append(line(190, 230, 190, 290, INK, 4)); o.append(line(210, 230, 210, 290, INK, 4))
    if spec.get("cardigan"):
        for y in (250, 290, 330, 370):
            o.append(circle(200, y, 5, INK, stroke=None))
    if spec.get("stripes"):
        for y in (250, 290, 330):
            o.append(line(146, y, 254, y, WHITE, 8))
    return g("".join(o), id=f"{cid}-{pose}-body", data_pivot="200 400", transform=f"translate(0 {bob})")


def leg(cid, pose, spec, side, out):
    f = side == "front"
    hx, hy = (228, 392) if f else (172, 392)
    rot = -out if f else out
    o = [limb(hx, hy, hx, hy + 266, spec["bottom"], 30),
         ellipse(hx + (10 if f else -10), hy + 282, 30, 13, spec.get("shoes", INK))]
    return g("".join(o), id=f"{cid}-{pose}-leg-{side}", data_pivot=f"{hx} {hy}", transform=f"rotate({rot} {hx} {hy})")


def arm(cid, pose, spec, side, out, bend, prop=None, prop_scale=0.9):
    f = side == "front"
    sx, sy = (250, 216) if f else (150, 216)
    rot = -out if f else out
    brot = bend if f else -bend
    ex, ey = sx, sy + 78
    hx, hy = sx, sy + 150
    sleeve = spec.get("sleeve", spec["top"])
    fore = spec.get("forearm", sleeve)
    upper = limb(sx, sy, ex, ey, sleeve, 26)
    lower = limb(ex, ey, hx, hy - 6, fore, 24) + circle(hx, hy, 16, spec["skin"])
    pr = ""
    if prop:
        pr = g(PROPS[prop][1](hx, hy + 10, prop_scale), id=f"{cid}-{pose}-prop-{prop}",
               transform=f"rotate({-(rot + brot)} {hx} {hy})")
    forearm = g(lower + pr, id=f"{cid}-{pose}-forearm-{side}", data_pivot=f"{ex} {ey}", transform=f"rotate({brot} {ex} {ey})")
    return g(upper + forearm, id=f"{cid}-{pose}-arm-{side}", data_pivot=f"{sx} {sy}", transform=f"rotate({rot} {sx} {sy})")


def fx(cid, pose, kind):
    if kind == "angry":
        o = path("M272 70 l16 0 m-8 -8 l0 16 m-6 -14 l12 12 m0 -12 l-12 12", stroke=RED, sw=5)
        o += path("M130 96 q10 -14 0 -28 M150 90 q10 -14 0 -28", stroke=GREY, sw=5)
    elif kind == "happy":
        o = "".join(path(f"M{x} {y - 12} v24 M{x - 12} {y} h24", stroke=YEL, sw=5) for x, y in [(120, 60), (290, 40), (310, 110)])
    elif kind == "sweat":
        o = path("M262 110 q-12 18 0 26 q12 -8 0 -26 z", SKY, INK, 4)
    elif kind == "zzz":
        o = text(290, 60, "…", 40, GREY, 800)
    else:
        return ""
    return g(o, id=f"{cid}-{pose}-fx")


# поза: out — угол отведения, bend — сгиб локтя (к телу), ноги — отведение, bob — подпрыгивание корпуса
POSES = {
    "stand":   dict(af=(6, 4), ab=(6, 4), lf=4, lb=4, mood="neutral", tilt=0, bob=0),
    "walk":    dict(af=(32, 56), ab=(-10, 22), lf=22, lb=18, mood="neutral", tilt=3, bob=-4),
    "angry":   dict(af=(62, 108), ab=(62, 108), lf=10, lb=10, mood="angry", tilt=-4, bob=0, fx="angry"),
    "happy":   dict(af=(146, 18), ab=(146, 18), lf=8, lb=8, mood="happy", tilt=4, bob=-6, fx="happy"),
    "sad":     dict(af=(0, 6), ab=(0, 6), lf=16, lb=6, mood="sad", tilt=8, bob=4),
    "calc":    dict(af=(36, 124), ab=(34, 118), lf=6, lb=6, mood="worried", tilt=-6, bob=0, fx="sweat",
                    prop_f="calculator", prop_b="notebook"),
    "scan":    dict(af=(58, 128), ab=(18, 48), lf=6, lb=6, mood="smile", tilt=-3, bob=0, prop_f="phone-scan"),
    "flash":   dict(af=(70, 70), ab=(8, 6), lf=6, lb=6, mood="surprised", tilt=0, bob=0, prop_f="flashlight"),
}

CHARACTERS = [
    dict(id="ainura", name="Айнура", role="продавщица-героиня", skin=SKIN, hair="scarf", top=BLUE, bottom=INK,
         dress=BLUE, apron=True, poses=["stand", "walk", "calc", "scan", "happy", "angry"],
         hold={"stand": None, "walk": None, "happy": None, "angry": None}),
    dict(id="ata", name="Токтогул-ата", role="пожилой покупатель", skin=SKIN, hair="calpak", top=BROWN, bottom=GREY,
         shoes=INK, collar=True, elder=True, poses=["stand", "walk", "angry", "happy", "sad"],
         hold={"stand": "lepyoshka", "walk": "lepyoshka", "sad": "lepyoshka", "happy": "lepyoshka"}),
    dict(id="jyldyz", name="Жылдыз", role="мама", skin=SKIN, hair="mom", top=GREEN, bottom=INK, dress=GREEN,
         cardigan=True, poses=["stand", "walk", "angry", "happy", "sad"],
         hold={"stand": "milk", "walk": "milk", "happy": "milk", "sad": "milk"}),
    dict(id="bakyt", name="Бакыт", role="ребёнок 6 лет", skin=SKIN, hair="kid", hair_color=BROWN, top=YEL, bottom=BLUE,
         stripes=True, kid=True, scale=0.58, poses=["stand", "walk", "angry", "happy", "sad"],
         hold={"stand": "milk", "walk": "milk", "happy": "milk"}),
    dict(id="erlan", name="Эрлан", role="нетерпеливый парень", skin=SKIN, hair="cap-headphones", top=INK, bottom=BLUE,
         hoodie=True, shoes=WHITE, poses=["stand", "walk", "angry", "happy", "flash"],
         hold={"stand": "seeds", "walk": "seeds", "happy": "seeds", "angry": None}),
    dict(id="apa", name="Бүбү-апа", role="бабушка с баулом", skin=SKIN, hair="kerchief", top=BROWN, bottom=GREY,
         dress=GREY, elder=True, poses=["stand", "walk", "angry", "happy", "sad"],
         hold={"stand": "bag", "walk": "bag", "happy": "bag", "sad": "bag", "angry": "bag"}),
    dict(id="nurlan", name="Нурлан-байке", role="хозяин магазина", skin=SKIN, hair="cap", top=WHITE, bottom=INK,
         vest=GREEN, poses=["stand", "walk", "flash", "happy", "angry"],
         hold={}),
]


def character_pose(spec, pose, slot_x):
    cid = spec["id"]
    P = POSES[pose]
    held = spec.get("hold", {}).get(pose)
    prop_f = P.get("prop_f") or held
    prop_b = P.get("prop_b")
    ps = 0.9 if not spec.get("kid") else 1.0
    parts = [arm(cid, pose, spec, "back", *P["ab"], prop=prop_b, prop_scale=ps),
             leg(cid, pose, spec, "back", P["lb"]),
             leg(cid, pose, spec, "front", P["lf"]),
             torso(cid, pose, spec, P["bob"]),
             arm(cid, pose, spec, "front", *P["af"], prop=prop_f, prop_scale=ps),
             head(cid, pose, spec, P["mood"], P["tilt"]),
             fx(cid, pose, P.get("fx", ""))]
    inner = "".join(parts)
    sc = spec.get("scale", 1.0)
    if sc != 1.0:  # ребёнок: масштаб, ноги остаются на y≈700
        inner = g(inner, transform=f"translate({200 * (1 - sc)} {700 * (1 - sc)}) scale({sc})")
    rig = g(inner, id=f"{cid}-{pose}", data_name=f"{spec['name']} — {pose}")
    return g(rig, id=f"{cid}-{pose}-slot", transform=f"translate({slot_x} 0)")


def build_characters():
    files = []
    for spec in CHARACTERS:
        poses = spec["poses"]
        inner = "".join(character_pose(spec, p, i * 400) for i, p in enumerate(poses))
        labels = "".join(text(i * 400 + 200, 716, p, 18, GREY, 600) for i, p in enumerate(poses))
        w = 400 * len(poses)
        content = svg(w, 720, inner + g(labels, id=f"{spec['id']}-labels"),
                      f"{spec['name']} ({spec['role']}) — позы: {', '.join(poses)}")
        name = f"char-{spec['id']}.svg"
        open(os.path.join(OUT, name), "w", encoding="utf-8").write(content)
        files.append((name, spec, w))
    return files


# ---- фон -------------------------------------------------------------------
def shelf_products(x, y, w, kinds, s=0.55):
    """ряд товаров на полке y (низ товаров)"""
    o = []
    step = 70 * s / 0.55
    n = int(w // step)
    for i in range(n):
        k = kinds[i % len(kinds)]
        cx = x + step * (i + 0.5)
        if k == "milk":
            o.append(p_milk(cx, y - 50 * s, s))
        elif k == "bottle":
            o.append(p_bottle(cx, y - 62 * s, s))
        elif k == "bread":
            o.append(p_bread(cx, y - 24 * s, s * 0.9))
        elif k == "lep":
            o.append(p_lepyoshka(cx, y - 24 * s, s * 0.9))
        elif k == "seeds":
            o.append(p_seeds(cx, y - 50 * s, s))
        elif k == "eggs":
            o.append(p_eggs(cx, y - 22 * s, s))
        elif k == "box":
            col = [YEL, RED, GREEN, BLUE, SKY][i % 5]
            o.append(rect(cx - 26 * s, y - 70 * s, 52 * s, 70 * s, col, rx=3 * s, sw=4))
        elif k == "can":
            o.append(rect(cx - 20 * s, y - 60 * s, 40 * s, 60 * s, LGREY, rx=6 * s, sw=4))
            o.append(rect(cx - 16 * s, y - 44 * s, 32 * s, 24 * s, RED, stroke=None))
    return "".join(o)


def gondola(x, y, w, h, kinds_rows, gid, s=0.55, color=BROWN):
    """стеллаж: боковины, полки с товарами"""
    rows = len(kinds_rows)
    o = [rect(x, y, w, h, CREAM, rx=4)]
    gap = h / rows
    for r in range(rows):
        sy = y + gap * (r + 1) - 10
        o.append(shelf_products(x + 12, sy - 8, w - 24, kinds_rows[r], s))
        o.append(rect(x, sy - 8, w, 16, color, rx=2))
    o.append(rect(x - 14, y - 10, 18, h + 20, color, rx=4))
    o.append(rect(x + w - 4, y - 10, 18, h + 20, color, rx=4))
    return g("".join(o), id=gid)


def build_backgrounds():
    files = []
    # 1. пол (2880×1080): перспективная плитка
    o = [rect(0, 640, 2880, 440, LGREY, stroke=None)]
    vx, vy = 1440, 330
    for i in range(-16, 17):
        x = 1440 + i * 220
        o.append(line(x, 1080, vx + (x - vx) * 0.3, 640, GREY, 3))
    for yy in (660, 700, 760, 840, 950, 1080):
        o.append(line(0, yy, 2880, yy, GREY, 3))
    o.append(rect(0, 634, 2880, 14, GREY, stroke=None))
    files.append(("bg-floor.svg", svg(2880, 1080, g("".join(o), id="floor"), "Пол магазина (2880×1080, средний слой параллакса)"), 2880))

    # 2. дальняя стена + окно + дверь + вывеска (2400×1080)
    o = [rect(0, 0, 2400, 660, CREAM, stroke=None), rect(0, 560, 2400, 100, LGREY, stroke=None)]
    # окно с горами
    win = [rect(300, 150, 560, 380, SKY, rx=6),
           path("M300 440 L420 330 L500 400 L600 290 L700 380 L760 330 L860 420 L860 530 L300 530 Z", GREEN, stroke=None),
           path("M560 330 L600 290 L640 330 Z", WHITE, stroke=None), path("M380 370 L420 330 L460 370 Z", WHITE, stroke=None),
           circle(760, 230, 36, YEL), line(580, 150, 580, 530, INK, 8), line(300, 340, 860, 340, INK, 8),
           rect(290, 140, 580, 400, "none", rx=6, sw=10)]
    o.append(g("".join(win), id="far-window"))
    # дверь
    door = [rect(1900, 220, 220, 440, GREY, rx=6), rect(1920, 240, 180, 260, SKY, rx=4), circle(2080, 540, 10, YEL),
            rect(1880, 150, 260, 54, YEL, rx=6), text(2010, 188, "ЧЫГУУ · ВЫХОД", 26, INK, 800)]
    o.append(g("".join(door), id="far-door"))
    # вывеска над кассой и часы
    sign = [rect(1150, 60, 520, 110, YEL, rx=10), text(1410, 136, "БЕРЕКЕ", 70, INK, 800),
            horn_row(1160, 1340, 150, 7, INK), horn_row(1480, 1660, 150, 7, INK)]
    o.append(g("".join(sign), id="far-sign"))
    clock = [circle(1000, 120, 46, WHITE), line(1000, 120, 1000, 92, INK, 5), line(1000, 120, 1022, 132, INK, 5)]
    o.append(g("".join(clock), id="far-clock"))
    # постер
    poster = [rect(1480, 240, 150, 200, WHITE, rx=4), p_bread(1555, 320, 0.8), text(1555, 410, "Жаңы нан", 18, INK, 800)]
    o.append(g("".join(poster), id="far-poster"))
    # выключатель и роутер на стене у двери
    o.append(g(p_switch(1780, 440, 0.8, True), id="far-switch-on"))
    o.append(g(p_router(1400, 230, 0.8), id="far-router"))
    files.append(("bg-wall.svg", svg(2400, 1080, g("".join(o), id="far-wall"), "Дальняя стена: окно с горами, дверь, вывеска «Береке», часы, роутер (2400×1080)"), 2400))

    # 3. дальние стеллажи у стены (2400×1080)
    o = []
    for i, x in enumerate((60, 920, 1700)):
        if i == 1:
            w = 700
        else:
            w = 560 if i == 0 else 180
        o.append(gondola(x, 300, w, 330, [["box", "can", "box"], ["bottle", "milk", "bottle"], ["seeds", "box", "can"]],
                         f"far-shelf-{i + 1}", s=0.42))
    files.append(("bg-shelves-far.svg", svg(2400, 1080, g("".join(o), id="far-shelves"), "Дальние стеллажи у стены (2400×1080)"), 2400))

    # 4. средние стеллажи (2880×1080): слева и справа от прохода
    o = [gondola(120, 360, 860, 420, [["bread", "lep", "bread", "lep"], ["milk", "bottle", "milk"], ["seeds", "eggs", "seeds"]],
                 "mid-shelf-left", s=0.6),
         gondola(1900, 360, 860, 420, [["box", "can", "box", "can"], ["bottle", "milk", "bottle"], ["lep", "bread", "lep"]],
                 "mid-shelf-right", s=0.6)]
    # ценники
    for x, t in ((240, "Нан 30 с"), (560, "Сүт 65 с"), (2060, "Суу 25 с"), (2420, "Нан 30 с")):
        o.append(g(rect(x, 300, 150, 44, YEL, rx=6) + text(x + 75, 331, t, 22, INK, 800), id=f"mid-tag-{x}"))
    files.append(("bg-shelves-mid.svg", svg(2880, 1080, g("".join(o), id="mid-shelves"), "Средние стеллажи слева и справа от прохода (2880×1080)"), 2880))

    # 5. ближний план (3840×1080): срезы стеллажей по краям, баннер, ящик яблок
    o = [gondola(-200, 120, 700, 820, [["bread", "lep"], ["milk", "bottle"], ["seeds", "eggs"], ["box", "can"]], "near-shelf-left", s=1.0),
         gondola(3340, 120, 700, 820, [["box", "can"], ["bottle", "milk"], ["lep", "bread"], ["seeds", "box"]], "near-shelf-right", s=1.0)]
    banner = [line(1400, 0, 1400, 110, INK, 6), line(1760, 0, 1760, 110, INK, 6),
              rect(1360, 100, 440, 120, YEL, rx=10), text(1580, 160, "АКЦИЯ", 48, INK, 800),
              text(1580, 200, "Лепёшка 3 = 80 с", 26, INK, 600)]
    o.append(g("".join(banner), id="near-banner"))
    crate = [rect(560, 900, 300, 150, BROWN, rx=6)] + [circle(610 + i * 50, 900, 26, RED) for i in range(5)] \
            + [circle(635 + i * 50, 870, 26, GREEN) for i in range(4)]
    o.append(g("".join(crate), id="near-crate"))
    files.append(("bg-shelves-near.svg", svg(3840, 1080, g("".join(o), id="near-shelves"), "Ближний план: срезы стеллажей, баннер, ящик яблок (3840×1080)"), 3840))

    # 6. касса-прилавок (1920×1080)
    o = [rect(520, 600, 880, 60, BROWN, rx=8), rect(540, 650, 840, 330, CREAM, rx=4),
         rect(540, 650, 840, 330, "none", rx=4), horn_row(560, 1360, 950, 14, BROWN)]
    o.append(text(960, 880, "КАССА", 44, BROWN, 800))
    # монитор кассы
    mon = [rect(620, 300, 300, 230, INK, rx=12), rect(730, 530, 80, 50, GREY, rx=4), rect(690, 578, 160, 22, GREY, rx=6)]
    on = [rect(636, 316, 268, 198, WHITE, rx=4, stroke=None), rect(636, 316, 268, 36, YEL, stroke=None),
          text(770, 342, "NurCRM", 22, INK, 800)]
    for i in range(4):
        on.append(line(652, 380 + i * 30, 870, 380 + i * 30, LGREY, 10))
    on.append(rect(652, 480, 220, 24, YEL, rx=4, stroke=None))
    off = [rect(636, 316, 268, 198, INK, rx=4, stroke=None), path("M660 340 L720 320", stroke=GREY, sw=4)]
    o.append(g("".join(mon) + g("".join(on), id="pos-screen-on") + g("".join(off), id="pos-screen-off"), id="pos-monitor"))
    # сканер, терминал, ящик, товары на прилавке
    o.append(g(path("M1000 600 v-60 q0 -30 30 -30 h20 q30 0 30 30 v60 z", GREY) + rect(1020, 520, 40, 14, RED, rx=4, stroke=None), id="pos-scanner"))
    o.append(g(rect(1120, 520, 110, 80, GREY, rx=8) + rect(1134, 532, 82, 30, SKY, rx=4), id="pos-terminal"))
    o.append(g(rect(560, 660, 300, 60, GREY, rx=4) + rect(690, 680, 40, 16, LGREY, rx=4), id="pos-drawer"))
    o.append(g(rect(1250, 520, 120, 80, BROWN, rx=6) + p_seeds(1290, 540, 0.5) + p_seeds(1330, 540, 0.5)
               + text(1310, 590, "семечки", 16, CREAM, 700), id="counter-seeds-box"))
    o.append(g(p_calculator(1080, 740, 0.8), id="counter-calculator"))
    o.append(g(p_notebook(1200, 740, 0.8), id="counter-notebook"))
    o.append(g(p_phone_scan(940, 740, 0.8, glow=False), id="counter-phone"))
    files.append(("bg-counter.svg", svg(1920, 1080, g("".join(o), id="counter"), "Касса: прилавок, монитор (экран вкл/выкл), сканер, терминал, калькулятор, тетрадка, телефон (1920×1080)"), 1920))

    # 7. лампы (1920×1080): подвес, плафон, конус света (lamp-on) и выключенная (lamp-off)
    o = []
    for i, x in enumerate((520, 1400)):
        cone = path(f"M{x - 60} 150 L{x - 700} 1080 L{x + 700} 1080 L{x + 60} 150 z", YEL, stroke=None, opacity="0.22")
        on = g(cone + path(f"M{x - 70} 150 q70 -60 140 0 z", YEL) + circle(x, 170, 22, CREAM, INK, 4), id=f"lamp-{i + 1}-on")
        off = g(path(f"M{x - 70} 150 q70 -60 140 0 z", GREY) + circle(x, 170, 22, LGREY, INK, 4), id=f"lamp-{i + 1}-off")
        o.append(g(line(x, 0, x, 120, INK, 6) + on + off, id=f"lamp-{i + 1}"))
    files.append(("bg-lamp.svg", svg(1920, 1080, g("".join(o), id="lamps"), "Лампы: включённая с конусом света и выключенная (1920×1080)"), 1920))

    # 8. затемнение и свет телефона (1920×1080)
    defs = ('<radialGradient id="glow-phone" cx="50%" cy="50%" r="50%">'
            f'<stop offset="0" stop-color="{YEL}" stop-opacity="0.95"/><stop offset="0.45" stop-color="{YEL}" stop-opacity="0.45"/>'
            f'<stop offset="1" stop-color="{YEL}" stop-opacity="0"/></radialGradient>'
            '<radialGradient id="glow-flash" cx="50%" cy="50%" r="50%">'
            f'<stop offset="0" stop-color="{WHITE}" stop-opacity="0.9"/><stop offset="1" stop-color="{WHITE}" stop-opacity="0"/></radialGradient>'
            '<radialGradient id="hole-grad" cx="50%" cy="50%" r="50%"><stop offset="0" stop-color="black"/>'
            '<stop offset="0.55" stop-color="black" stop-opacity="0.6"/><stop offset="1" stop-color="white"/></radialGradient>'
            '<mask id="dark-mask"><rect width="1920" height="1080" fill="white"/>'
            '<ellipse id="dark-hole" cx="960" cy="560" rx="460" ry="400" fill="url(#hole-grad)"/></mask>')
    o = [g(rect(0, 0, 1920, 1080, INK, stroke=None, opacity="0.92", mask="url(#dark-mask)"), id="dark-full"),
         g(rect(0, 0, 1920, 1080, INK, stroke=None, opacity="0.3"), id="vignette")]
    files.append(("overlay-dark.svg", svg(1920, 1080, "".join(o), "Затемнение «свет погас»: окно-дырка под телефон/фонарик (dark-hole), виньетка (1920×1080)",
                                          defs), 1920))
    o = [g(ellipse(960, 560, 440, 380, "url(#glow-phone)", stroke=None), id="glow-phone-spot"),
         g(ellipse(960, 560, 260, 180, "url(#glow-flash)", stroke=None), id="glow-flash-spot")]
    files.append(("overlay-glow.svg", svg(1920, 1080, "".join(o), "Свет телефона (glow-phone-spot) и фонарика (glow-flash-spot) (1920×1080)", defs), 1920))

    # 9. реквизит общим листом
    o = []
    keys = list(PROPS.keys())
    for i, k in enumerate(keys):
        cx, cy = 160 + (i % 6) * 300, 150 + (i // 6) * 300
        title, fn = PROPS[k]
        o.append(g(fn(cx, cy, 1.0), id=f"prop-{k}", data_name=title))
        o.append(text(cx, cy + 120, title, 18, GREY, 600))
    files.append(("props.svg", svg(1800, 900, "".join(o), "Реквизит: id prop-… (лист 1800×900)"), 1800))

    # 10. логотип
    o = [circle(200, 200, 196, INK, stroke=None)]
    for i in range(24):
        a = math.radians(i * 15 - 90)
        x1, y1 = 200 + 80 * math.cos(a), 200 + 80 * math.sin(a)
        x2, y2 = 200 + 178 * math.cos(a), 200 + 178 * math.sin(a)
        col = YEL if 115 <= i * 15 <= 245 else WHITE  # нижние лучи — жёлтые (гора)
        o.append(line(x1, y1, x2, y2, col, 14))
    o.append(path("M200 170 L96 330 L304 330 z", YEL, stroke=None))
    logo_mark = g("".join(o), id="logo-mark")
    files.append(("logo.svg", svg(400, 400, logo_mark, "Логотип NurCRM (знак: солнце-гора)"), 400))

    # 11. финальная карточка и титры
    o = [rect(0, 0, 1920, 1080, INK, stroke=None),
         g(logo_mark.replace('id="logo-mark"', 'id="end-logo"'), transform="translate(760 80) scale(1)"),
         text(960, 600, "NurCRM — касса,", 72, WHITE, 800),
         el("text", f'<tspan fill="{YEL}">которая знает</tspan> ваш магазин', x=960, y=690, font_family=FONT, font_size=72,
            font_weight=800, fill=WHITE, text_anchor="middle"),
         text(960, 770, "Работает без интернета. Говорит на вашем языке. Считает вашу прибыль.", 32, LGREY, 600),
         rect(260, 860, 1300, 90, YEL, rx=16),
         text(910, 916, "Установить: app.nurcrm.kg", 30, INK, 800)]
    qr = [rect(1600, 760, 220, 220, WHITE, rx=8)]
    import random
    rnd = random.Random(7)
    for r in range(9):
        for c in range(9):
            if (r < 3 and c < 3) or (r < 3 and c > 5) or (r > 5 and c < 3) or rnd.random() < 0.45:
                qr.append(rect(1612 + c * 22, 772 + r * 22, 20, 20, INK, stroke=None))
    qr.append(text(1710, 1010, "QR → релизы", 20, LGREY, 600))
    o.append(g("".join(qr), id="end-qr"))
    files.append(("end-card.svg", svg(1920, 1080, g("".join(o), id="end-card"), "Финальная карточка: логотип, слоган, подстрочник, ссылки, QR"), 1920))

    titles = [g(rect(0, 0, 1920, 1080, INK, stroke=None) + text(960, 500, "КАК РАНЬШЕ", 120, WHITE, 800)
                + text(960, 600, "МУРДА КАНДАЙ БОЛЧУ", 44, LGREY, 600), id="title-before"),
              g(rect(0, 0, 1920, 1080, YEL, stroke=None) + text(960, 500, "С NURCRM", 120, INK, 800)
                + text(960, 600, "NURCRM МЕНЕН", 44, INK, 600), id="title-after", transform="translate(0 1080)"),
              g(rect(0, 940, 1920, 100, INK, stroke=None, opacity="0.7") + text(960, 1004, "Жарык өчтү — касса да өчтү.", 44, WHITE, 600),
                id="subtitle-bar", transform="translate(0 2160)")]
    files.append(("title-cards.svg", svg(1920, 3240, "".join(titles), "Титры: «Как раньше» (0–1080), «С NurCRM» (1080–2160), плашка субтитра (2160–3240)"), 1920))

    for name, content, _ in files:
        open(os.path.join(OUT, name), "w", encoding="utf-8").write(content)
    return files


# ---- превью-лист ----------------------------------------------------------
def inline_svg(name, **style):
    s = open(os.path.join(OUT, name), encoding="utf-8").read()
    s = s[s.index("<svg"):]
    st = ";".join(f"{k}:{v}" for k, v in style.items())
    return s.replace("<svg ", f'<svg style="{st}" ', 1)


def build_sheet(char_files, bg_files):
    css = f"""
    @import url('https://fonts.googleapis.com/css2?family=Montserrat:wght@600;800&display=swap');
    :root{{--yel:{YEL};--ink:{INK};}}
    body{{margin:0;background:#F4F4F4;color:var(--ink);font-family:Montserrat,Arial,sans-serif;}}
    header{{background:var(--ink);color:#fff;padding:28px 40px;}}
    header h1{{margin:0;font-weight:800;font-size:32px}} header h1 span{{color:var(--yel)}}
    header p{{margin:8px 0 0;color:#ccc;font-weight:600;font-size:14px}}
    section{{padding:24px 40px}} h2{{font-weight:800;font-size:22px;margin:0 0 12px;border-left:8px solid var(--yel);padding-left:12px}}
    .pal{{display:flex;flex-wrap:wrap;gap:10px}} .sw{{width:150px;border:2px solid var(--ink);border-radius:10px;overflow:hidden;background:#fff;font-size:12px}}
    .sw div{{height:56px}} .sw b{{display:block;padding:6px 8px 0}} .sw i{{display:block;padding:0 8px 8px;color:#666;font-style:normal}}
    .char{{background:#fff;border:2px solid var(--ink);border-radius:14px;margin-bottom:18px;overflow:hidden}}
    .char .cap{{padding:10px 16px;display:flex;gap:16px;align-items:baseline;border-bottom:2px solid #ddd}}
    .char .cap b{{font-size:18px}} .char .cap span{{color:#666;font-size:13px;font-weight:600}}
    .char svg{{display:block;width:100%;height:auto;background:repeating-conic-gradient(#eee 0 25%,#fff 0 50%) 0 0/40px 40px}}
    .bg{{background:#fff;border:2px solid var(--ink);border-radius:14px;margin-bottom:18px;overflow:hidden}}
    .bg .cap{{padding:8px 16px;border-bottom:2px solid #ddd;font-weight:600;font-size:14px}} .bg .cap code{{color:#666}}
    .bg svg{{display:block;width:100%;height:auto;background:repeating-conic-gradient(#eee 0 25%,#fff 0 50%) 0 0/40px 40px}}
    .stage{{position:relative;width:960px;height:540px;overflow:hidden;border:2px solid var(--ink);border-radius:14px;background:{CREAM}}}
    .world{{position:absolute;left:0;top:0;width:1920px;height:1080px;transform:scale(.5);transform-origin:0 0}}
    .layer{{position:absolute;top:0;left:0;height:1080px}} .layer svg{{height:1080px;width:auto;display:block}}
    .actor{{position:absolute;width:400px;height:720px}} .actor svg{{width:400px;height:720px;display:block}}
    #l-far{{animation:far 12s linear infinite}} #l-mid{{animation:mid 12s linear infinite}} #l-near{{animation:near 12s linear infinite}}
    @keyframes far{{0%{{transform:translateX(0)}}45%,100%{{transform:translateX(-240px)}}}}
    @keyframes mid{{0%{{transform:translateX(0)}}45%,100%{{transform:translateX(-600px)}}}}
    @keyframes near{{0%{{transform:translateX(0)}}45%,100%{{transform:translateX(-1500px)}}}}
    #l-dark{{opacity:0;animation:dark 12s linear infinite}} @keyframes dark{{0%,50%{{opacity:0}}52%,100%{{opacity:1}}}}
    #l-glow{{opacity:0;animation:glow 12s linear infinite}} @keyframes glow{{0%,62%{{opacity:0}}66%,100%{{opacity:1}}}}
    #l-lamp [id$="-on"]{{animation:lampoff 12s linear infinite}} @keyframes lampoff{{0%,50%{{opacity:1}}52%,100%{{opacity:0}}}}
    #l-lamp [id$="-off"]{{opacity:0;animation:dark 12s linear infinite}}
    .note{{font-size:13px;color:#444;font-weight:600;max-width:960px;line-height:1.5}}
    table{{border-collapse:collapse;font-size:13px}} td,th{{border:1px solid #ccc;padding:4px 8px;text-align:left}} th{{background:#fff}}
    """
    out = [f'<!doctype html><html lang="ru"><head><meta charset="utf-8"><title>NurCRM · «Свет погас» — лист персонажей</title>'
           f'<meta name="viewport" content="width=device-width,initial-scale=1"><style>{css}</style></head><body>',
           '<header><h1><span>NurCRM</span> · ролик «Свет погас» — персонажи, фон, реквизит</h1>'
           '<p>design/Ad-Light · сгенерировано make_assets.py · плоский стиль с контуром 6 px · палитра 12 цветов · сцена 1920×1080 · персонаж 400×720 (ноги на y=700)</p></header>']
    out.append('<section><h2>Палитра</h2><div class="pal">' + "".join(
        f'<div class="sw"><div style="background:{hx}"></div><b>{hx}</b><i>{n} — {d}</i></div>' for n, hx, d in PALETTE) + "</div></section>")

    # сцена-демо
    actors = [("ainura", "scan", 760, 330), ("ata", "stand", 1060, 300), ("jyldyz", "stand", 1300, 320),
              ("bakyt", "stand", 1440, 330), ("erlan", "stand", 1560, 300), ("apa", "stand", 1760, 320)]
    actor_html = "".join(f'<div class="actor" style="left:{x}px;top:{y}px"><svg viewBox="0 0 400 720"><use href="#{c}-{p}"/></svg></div>'
                         for c, p, x, y in actors)
    out.append('<section><h2>Сцена-демо (CSS-анимация: пролёт-параллакс → щёлк → свет телефона)</h2>'
               '<div class="stage"><div class="world">'
               f'<div class="layer" id="l-far">{inline_svg("bg-wall.svg")}</div>'
               f'<div class="layer" id="l-far2" style="animation:far 12s linear infinite">{inline_svg("bg-shelves-far.svg")}</div>'
               f'<div class="layer" id="l-floor" style="animation:mid 12s linear infinite">{inline_svg("bg-floor.svg")}</div>'
               f'<div class="layer" id="l-mid">{inline_svg("bg-shelves-mid.svg")}</div>'
               f'<div class="layer" id="l-counter" style="left:300px">{inline_svg("bg-counter.svg")}</div>'
               f'{actor_html}'
               f'<div class="layer" id="l-lamp">{inline_svg("bg-lamp.svg")}</div>'
               f'<div class="layer" id="l-dark">{inline_svg("overlay-dark.svg")}</div>'
               f'<div class="layer" id="l-glow" style="left:-100px;top:-40px">{inline_svg("overlay-glow.svg")}</div>'
               f'<div class="layer" id="l-near">{inline_svg("bg-shelves-near.svg")}</div>'
               '</div></div>'
               '<p class="note">Слои сзади наперёд: bg-wall (далёкий, скорость 0,2) → bg-shelves-far (0,2) → bg-floor (0,5) → bg-shelves-mid (0,5) → bg-counter → актёры → bg-lamp → overlay-dark → overlay-glow → bg-shelves-near (1,25). '
               'Пролёт = разные скорости translateX; «свет погас» = overlay-dark opacity 0→0,92 за 120 мс + lamp-off; свет телефона = glow-phone-spot над актёром.</p></section>')

    # персонажи
    out.append("<section><h2>Персонажи (каждая поза — группа <code>id=«имя-поза»</code>; внутри: arm-back, leg-back, leg-front, body, arm-front, head, fx; data-pivot — точка вращения)</h2>")
    for name, spec, w in char_files:
        out.append(f'<div class="char"><div class="cap"><b>{spec["name"]}</b><span>{spec["role"]} · {name} · {w}×720 · позы: {", ".join(spec["poses"])}</span></div>'
                   f'{inline_svg(name)}</div>')
    out.append("</section>")

    out.append("<section><h2>Фон и реквизит</h2>")
    for name, content, w in bg_files:
        title = content[content.index("<title>") + 7:content.index("</title>")]
        out.append(f'<div class="bg"><div class="cap">{title} · <code>{name}</code></div>{inline_svg(name, **{"max-height": "520px"})}</div>')
    out.append("</section>")

    out.append("<section><h2>Как анимировать</h2><table><tr><th>Что</th><th>Где</th><th>Как</th></tr>"
               "<tr><td>Ходьба</td><td>leg-front/leg-back, arm-front/arm-back</td><td>rotate вокруг data-pivot ±22°/±30°, цикл 0,6 с; body translateY −4…0</td></tr>"
               "<tr><td>Злость</td><td>поза angry + fx</td><td>мелкая тряска головы ±3°, fx-крест мигает 6 Гц</td></tr>"
               "<tr><td>Скан</td><td>ainura-scan: forearm-front + prop-phone-scan</td><td>наклон предплечья −8° на «пик», красная линия на экране 300 мс, вспышка glow 120 мс</td></tr>"
               "<tr><td>Свет погас</td><td>overlay-dark #dark-full, bg-lamp lamp-N-off</td><td>opacity 0→0,92 за 120 мс (2 кадра), затем glow-phone-spot</td></tr>"
               "<tr><td>Экран кассы</td><td>bg-counter #pos-screen-off</td><td>показать в момент щелчка; при свете — скрыть</td></tr>"
               "</table></section></body></html>")
    open(os.path.join(OUT, "character-sheet.html"), "w", encoding="utf-8").write("\n".join(out))


if __name__ == "__main__":
    cf = build_characters()
    bf = build_backgrounds()
    build_sheet(cf, bf)
    print("ok:", len(cf), "characters,", len(bf), "background/prop files ->", OUT)
