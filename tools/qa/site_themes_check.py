#!/usr/bin/env python3
# 2026-10-27: проверка готовых тем сайта (ТЗ «Сайт — темы для одежды», пакет С-5). Инструмент, не код продукта: ничего не пишет и не ходит в сеть.
# Что проверяет по каждой теме из Assets/SiteThemes (themes.json — 6 прежних, clothing/index.json — 10 новых):
#   - файл читается, есть code/name{5}/description{5}/patch, названия и описания на 5 языках не пустые;
#   - в patch только ключи и значения, которые понимает фронт market.nurcrm.kg (раздел 1.3 ТЗ), шрифт — из 7; sections в тему не входят;
#   - 17 цветов; у тёмной темы фон тёмный, у светлой — светлый (ловушка тёмного режима фронта);
#   - контраст WCAG AA (>= 4,5) для текста на фоне, accent_text на accent, шапка, подвал, цена, старая цена, значки (у кнопок-контуром — accent на фоне);
#   - картинки: hero 1920x640 до 250 КБ, ph 600x800 (3:4) до 60 КБ, thumb 480x300 до 60 КБ.
# Прежние шесть тем проверяются мягко: контраст и размеры картинок — только предупреждения (в них «старая цена» не проходит AA — отдельный пакет С-1).
# Запуск из корня репозитория: python -I tools/qa/site_themes_check.py   (код возврата 1 — у новых тем есть ошибки)
import json
import os
import re
import sys

ROOT = os.path.join("NurMarketKassa.Avalonia", "NurMarketKassa.Avalonia", "Assets", "SiteThemes")
LANGS = ("ru", "ky", "en", "tr", "uz")
FONTS = {"Inter", "Roboto", "Roboto Condensed", "Montserrat", "Nunito", "PT Sans", "Noto Sans"}
COLORS = ("background surface text text_muted accent accent_text border header_bg header_text footer_bg footer_text price old_price "
          "badge_new_bg badge_new_text badge_sale_bg badge_sale_text").split()


def enum(*v):
    return lambda x: None if x in v else f"значение «{x}» не из {v}"


def rng(a, b):
    return lambda x: None if isinstance(x, (int, float)) and not isinstance(x, bool) and a <= x <= b else f"число «{x}» вне {a}..{b}"


def boolean(x):
    return None if isinstance(x, bool) else f"«{x}» не логическое"


def null(x):
    return None if x is None else f"ожидался null, а «{x}»"


def hexc(x):
    return None if isinstance(x, str) and re.fullmatch(r"#[0-9A-Fa-f]{6}", x) else f"цвет «{x}»"


SCHEMA = {
    "theme": {"preset": null, "mode": enum("light", "dark"), "colors": {k: hexc for k in COLORS},
              "font": {"family": enum(*sorted(FONTS)), "base_size": rng(13, 18), "heading_weight": rng(400, 900)},
              "radius": rng(0, 28), "shadow": enum("none", "soft", "strong"), "button_style": enum("filled", "outline", "soft"),
              "background_image": null, "background_pattern": null},
    "header": {"layout": enum("logo_left", "logo_center"), "sticky": boolean, "show_search": boolean, "show_name": boolean},
    "hero": {"style": enum("card", "cover", "split", "hidden"), "overlay": rng(0, 0.8), "align": enum("left", "center")},
    "categories": {"style": enum("chips", "tiles", "list", "hidden"), "show_all": boolean, "show_count": boolean},
    "products": {"columns": {"desktop": rng(2, 6), "tablet": rng(2, 4), "mobile": rng(1, 3)}, "page_size": rng(6, 200),
                 "pagination": enum("pages", "load_more", "infinite"), "hide_zero_price": boolean},
    "card": {"template": enum("compact", "standard", "large", "list"), "photo_ratio": enum("1:1", "4:3", "3:4", "16:9"), "photo_fit": enum("contain", "cover"),
             "price_position": enum("photo_corner", "under_name"),
             "show": {"old_price": boolean, "discount_badge": boolean, "new_badge": boolean, "category": boolean, "stock": enum("hidden", "low_only", "always"),
                      "unit": boolean, "description": boolean, "add_button": boolean, "quantity_stepper": boolean},
             "new_badge_days": rng(1, 365), "shadow": boolean, "border": boolean, "placeholder_image": lambda x: None},
    "footer": {"enabled": boolean},
}


def walk(node, schema, path, errors):
    if isinstance(schema, dict):
        if not isinstance(node, dict):
            errors.append(f"{path}: ожидался объект")
            return
        for k, v in node.items():
            if k not in schema:
                errors.append(f"{path}.{k}: ключа нет в списке фронта")
            else:
                walk(v, schema[k], f"{path}.{k}", errors)
    else:
        e = schema(node)
        if e:
            errors.append(f"{path}: {e}")


def lum(h):
    def f(v):
        v /= 255
        return v / 12.92 if v <= 0.03928 else ((v + 0.055) / 1.055) ** 2.4
    r, g, b = (int(h[i:i + 2], 16) for i in (1, 3, 5))
    return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b)


def ratio(a, b):
    x, y = lum(a), lum(b)
    return (max(x, y) + 0.05) / (min(x, y) + 0.05)


def check_theme(folder, code, strict):
    errors, warns = [], []
    path = os.path.join(ROOT, folder, code + ".json")
    try:
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
    except Exception as ex:  # noqa: BLE001
        return [f"файл не читается: {ex}"], []
    for key in ("name", "description"):
        for lang in LANGS:
            if not str((doc.get(key) or {}).get(lang, "")).strip():
                errors.append(f"{key}.{lang} пусто")
    patch = doc.get("patch")
    if not isinstance(patch, dict):
        return errors + ["нет patch"], warns
    if "sections" in patch:
        errors.append("patch.sections: разделы в тему не входят")
    walk(patch, SCHEMA, "patch", errors)
    colors = patch.get("theme", {}).get("colors", {})
    missing = [k for k in COLORS if k not in colors]
    if missing:
        errors.append("нет цветов: " + ", ".join(missing))
        return errors, warns
    mode = patch["theme"].get("mode")
    bright = lum(colors["background"])
    if mode == "light" and bright <= 0.4 and strict:
        errors.append(f"mode=light, а фон {colors['background']} тёмный")
    if mode == "dark" and bright >= 0.2:
        errors.append(f"mode=dark, а фон {colors['background']} светлый (фронт подменит цвета своей тёмной палитрой)")
    pairs = [("text/background", "text", "background"), ("text/surface", "text", "surface"), ("muted/background", "text_muted", "background"),
             ("muted/surface", "text_muted", "surface"), ("accent_text/accent", "accent_text", "accent"), ("header_text/header_bg", "header_text", "header_bg"),
             ("footer_text/footer_bg", "footer_text", "footer_bg"), ("price/surface", "price", "surface"), ("old_price/surface", "old_price", "surface"),
             ("badge_new", "badge_new_text", "badge_new_bg"), ("badge_sale", "badge_sale_text", "badge_sale_bg")]
    if patch["theme"].get("button_style") == "outline":
        pairs += [("accent/background", "accent", "background"), ("accent/surface", "accent", "surface")]
    for name, a, b in pairs:
        r = ratio(colors[a], colors[b])
        if r < 4.5:
            (errors if strict else warns).append(f"контраст {name} = {r:.2f} (< 4,5)")
    # картинки
    try:
        from PIL import Image  # только для размеров; без Pillow картинки не проверяются
    except ImportError:
        Image = None
    for pth, want, max_kb in (("hero.image", (1920, 640), 250), ("card.placeholder_image", (600, 800), 60)):
        spec = (doc.get("images") or {}).get(pth)
        if not spec:
            (errors if strict else warns).append(f"нет картинки {pth}")
            continue
        f = os.path.join(ROOT, folder, spec["file"])
        if not os.path.exists(f):
            (errors if strict else warns).append(f"нет файла {spec['file']}")
            continue
        kb = os.path.getsize(f) / 1024
        if kb > max_kb:
            (errors if strict else warns).append(f"{spec['file']}: {kb:.0f} КБ > {max_kb}")
        if Image and strict:
            with Image.open(f) as im:
                if im.size != want:
                    errors.append(f"{spec['file']}: {im.size}, нужно {want}")
    thumb = os.path.join(ROOT, folder, code + "_thumb.png")
    if not os.path.exists(thumb):
        (errors if strict else warns).append(f"нет {code}_thumb.png")
    elif os.path.getsize(thumb) / 1024 > 60 and strict:
        errors.append(f"{code}_thumb.png: {os.path.getsize(thumb) / 1024:.0f} КБ > 60")
    elif Image and strict:
        with Image.open(thumb) as im:
            if im.size != (480, 300):
                errors.append(f"{code}_thumb.png: {im.size}, нужно (480, 300)")
    return errors, warns


def stray_files():
    """2026-10-27 (аудит редакторов): Assets/** собирается в программу как AvaloniaResource — служебные файлы стенда снимков
    (results-*.txt и т. п.) не должны попасть в выпуск. Разрешены только json, jpg, png и LICENSES.txt."""
    found = []
    for folder, _, files in os.walk(ROOT):
        for name in files:
            ext = os.path.splitext(name)[1].lower()
            if ext not in (".json", ".jpg", ".png") and name != "LICENSES.txt":
                found.append(os.path.join(folder, name))
    return found


def main():
    sys.stdout.reconfigure(encoding="utf-8")  # консоль Windows — не cp1251
    old = json.load(open(os.path.join(ROOT, "themes.json"), encoding="utf-8"))
    new = json.load(open(os.path.join(ROOT, "clothing", "index.json"), encoding="utf-8"))
    bad = 0
    for folder, codes, strict in (("", old, False), ("clothing", new, True)):
        for code in codes:
            errors, warns = check_theme(folder, code, strict)
            status = "FAIL" if errors else ("warn" if warns else "ok  ")
            print(f"{status} {code}")
            for e in errors:
                print("     ошибка:", e)
            for w in warns:
                print("     замечание:", w)
            bad += 1 if errors else 0
    for f in stray_files():
        print("FAIL лишний файл в Assets/SiteThemes (попадёт в выпуск):", f)
        bad += 1
    print(f"тем: {len(old) + len(new)}, с ошибками: {bad}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
