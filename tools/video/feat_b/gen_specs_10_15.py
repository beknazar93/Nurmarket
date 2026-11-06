#!/usr/bin/env python3
"""2026-10-30: генератор раскадровок роликов-фишек №10–15 -> tools/video/examples/feat-NN-*.json (ролик 9 написан вручную).
После генерации make_feat_b.py дописывает в эти файлы пути premix/html. Запуск: python gen_specs_10_15.py"""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
EX = os.path.normpath(os.path.join(HERE, "..", "examples"))


def final():
    return {"type": "final", "dur": 4.0, "say": [{"who": "N", "at": 0.5, "text": "Нур Си-Эр-Эм — касса, которая знает ваш магазин.", "show": False, "maxdur": 3.1}]}


def W(num, slug, title, scenes, name):
    d = {"name": name, "lang": "ru", "voice": "none", "music": False, "fps": 30,
         "feat": {"num": num, "slug": slug, "title": title, "scenes": scenes + [final()]}}
    with open(os.path.join(EX, "feat-%02d-%s.json" % (num, name.split("-", 2)[2])), "w", encoding="utf-8") as f:
        json.dump(d, f, ensure_ascii=False, indent=1)


COUNTER = {"x": 1470, "y": 705, "s": 0.85}

# ---------------- 10
langs = ["10-kassa-lang-ru.png", "10-kassa-lang-ky.png", "10-kassa-lang-uz.png", "10-kassa-lang-tr.png", "10-kassa-lang-en.png"]
names = ["Русский", "Кыргызча", "Oʻzbek", "Türkçe", "English"]
seq = [" · ".join(("[[%s]]" % n) if i == k else n for i, n in enumerate(names)) for k in range(5)]
views = [{"src": f, "at": 1.5 * i, "dur": 1.5, "crop": [0, 0, 1, 1], "slide": 24,
          "marks": [{"at": 1.5 * i + 0.5, "kind": "click", "rect": [0.65, 0.918, 0.337, 0.066]}]} for i, f in enumerate(langs)]
sc10 = [
    {"type": "stage", "dur": 5.0, "counter": COUNTER,
     "actors": [{"id": "bakyt", "who": "bakyt", "x": 520, "poses": [[0, "stand"]]},
                {"id": "jyldyz", "who": "jyldyz", "x": 830, "poses": [[0, "stand"], [3.9, "sad"]]},
                {"id": "ainura", "who": "ainura", "x": 1480, "z": "back", "poses": [[0, "stand"]]}],
     "say": [{"who": "BA", "actor": "bakyt", "at": 1.45, "maxdur": 3.0, "text": "Оп-ла-та… Мама, а почему не по-кыргызски?",
              "tts": "Оп-ла-та... Мама, а почему не по-кыргызски?"}]},
    {"type": "shots", "dur": 2.8, "titr": [{"at": 0.3, "dur": 2.2, "text": "Одна [[кнопка]]"}],
     "views": [{"src": "10-kassa-lang-menu.png", "at": 0, "dur": 2.8, "crop": [0, 0.46, 0.5, 0.2],
                "marks": [{"at": 1.0, "kind": "click", "rect": [0.102, 0.549, 0.074, 0.045]}]}],
     "say": [{"who": "N", "at": 0.2, "text": "Язык — одна кнопка."}]},
    {"type": "shots", "dur": 7.5, "titr": [{"at": 0.1, "dur": 7.3, "seq": seq, "step": 1.5, "text": ""}], "views": views,
     "sfx": [{"n": "tick", "at": 1.5 * i, "v": 0.5} for i in range(1, 5)],
     "say": [{"who": "N", "at": 0.2, "text": "Русский, кыргызский, узбекский, турецкий, английский — касса говорит на пяти языках."}]},
    {"type": "shots", "dur": 3.4, "titr": [{"at": 0.3, "dur": 2.8, "text": "И [[телефон]] тоже"}],
     "views": [{"src": "10-mobile-lang-ky.png", "at": 0, "dur": 3.4, "frame": "phone", "area": [600, 26, 720, 826], "crop": [0, 0, 1, 0.56]}],
     "say": [{"who": "N", "at": 0.2, "text": "И телефон — вместе с кассой."}]},
    {"type": "stage", "dur": 4.4, "counter": COUNTER,
     "actors": [{"id": "bakyt", "who": "bakyt", "x": 420, "poses": [[0, "stand"], [0.3, "happy"]]},
                {"id": "jyldyz", "who": "jyldyz", "x": 700, "poses": [[0, "happy"]]},
                {"id": "ata", "who": "ata", "x": 960, "poses": [[0, "stand"], [1.0, "happy"]]},
                {"id": "ainura", "who": "ainura", "x": 1480, "z": "back", "poses": [[0, "stand"], [0.5, "happy"]]}],
     "say": [{"who": "BA", "actor": "bakyt", "at": 0.3, "maxdur": 1.2, "text": "Тө-лөм!", "tts": "Тё-лём!"},
             {"who": "N", "at": 1.7, "text": "Чтобы даже Бакыт прочитал, что написано."}]},
]
W(10, "10-yazyki", "На вашем языке", sc10, "feat-10-yazyki")

# ---------------- 11
sc11 = [
    {"type": "stage", "dur": 4.6, "counter": COUNTER,
     "actors": [{"id": "ata", "who": "ata", "x": 900, "poses": [[0, "stand"], [1.3, "angry"]]},
                {"id": "ainura", "who": "ainura", "x": 1480, "z": "back", "poses": [[0, "stand"], [3.0, "angry"]]}],
     "say": [{"who": "AT", "actor": "ata", "at": 1.45, "maxdur": 2.5, "text": "А сколько там вышло? Не вижу!"}]},
    {"type": "shots", "dur": 6.6, "titr": [{"at": 0.3, "dur": 5.8, "text": "Товары · сумма · [[скидка]]"}],
     "views": [{"clip": "11-customer-display-items.mp4", "from": 1.5, "len": 6.0, "at": 0, "dur": 6.6, "crop": [0, 0, 1, 1],
                "marks": [{"at": 0.6, "kind": "fill", "dur": 0.8, "rect": [0.034, 0.27, 0.656, 0.09]},
                          {"at": 1.6, "kind": "fill", "dur": 0.8, "rect": [0.034, 0.27, 0.656, 0.09]},
                          {"at": 2.6, "kind": "fill", "dur": 0.8, "rect": [0.034, 0.27, 0.656, 0.09]},
                          {"at": 3.6, "kind": "click", "rect": [0.16, 0.855, 0.13, 0.075]},
                          {"at": 4.2, "kind": "click", "rect": [0.78, 0.83, 0.21, 0.14]}]}],
     "say": [{"who": "N", "at": 0.15, "text": "Покупатель должен видеть, за что платит. Второй экран к кассе — товары, сумма, скидка."}]},
    {"type": "shots", "dur": 4.0, "titr": [{"at": 0.3, "dur": 3.4, "text": "QR оплаты — [[на месте]]"}],
     "views": [{"clip": "11-customer-display-qr.mp4", "from": 0.6, "len": 4.0, "at": 0, "dur": 4.0, "crop": [0, 0, 1, 1],
                "marks": [{"at": 0.4, "kind": "click", "rect": [0.78, 0.3, 0.15, 0.31]},
                          {"at": 2.4, "kind": "click", "rect": [0.72, 0.74, 0.26, 0.08]}]}],
     "sfx": [{"n": "pik", "at": 2.4, "v": 0.8}],
     "say": [{"who": "N", "at": 0.2, "text": "И QR для оплаты — прямо на экране."}]},
    {"type": "shots", "dur": 2.0, "titr": [{"at": 0.2, "dur": 1.6, "text": "Включается [[в настройках]]"}],
     "views": [{"src": "11-kassa-settings-customer-display.png", "at": 0, "dur": 2.0, "crop": [0, 0.12, 1, 0.56],
                "marks": [{"at": 0.5, "kind": "click", "rect": [0.03, 0.385, 0.3, 0.05]}]}]},
    {"type": "stage", "dur": 4.4, "counter": COUNTER,
     "actors": [{"id": "ata", "who": "ata", "x": 640, "poses": [[0, "happy"]]},
                {"id": "erlan", "who": "erlan", "x": 940, "poses": [[0, "stand"], [1.6, "happy"]]},
                {"id": "ainura", "who": "ainura", "x": 1480, "z": "back", "poses": [[0, "stand"]]}],
     "props": [{"id": "prop-lepyoshka", "x": 700, "y": 770, "s": 0.9, "in": 1.5}],
     "say": [{"who": "AT", "actor": "ata", "at": 0.3, "maxdur": 1.3, "text": "Вот теперь вижу!"},
             {"who": "ER", "actor": "erlan", "at": 1.8, "maxdur": 2.3, "text": "Ата, я тоже вижу — три лепёшки!"}]},
]
W(11, "11-ekran-pokupatelya", "Экран покупателя", sc11, "feat-11-ekran-pokupatelya")

# ---------------- 12
sc12 = [
    {"type": "stage", "dur": 5.2, "counter": COUNTER,
     "actors": [{"id": "ainura", "who": "ainura", "x": 1480, "z": "back", "poses": [[0, "stand"], [1.2, "calc"]]},
                {"id": "nurlan", "who": "nurlan", "x": 800, "poses": [[0, "stand"], [1.4, "angry"], [4.3, "stand"]]}],
     "props": [{"id": "prop-phone-scan", "x": 1290, "y": 700, "s": 0.8, "shake": [1.3, 4.9]}],
     "say": [{"who": "ER", "anchor": [1290, 520], "at": 1.4, "maxdur": 1.3, "text": "А молоко есть?"},
             {"who": "AI", "actor": "ainura", "at": 2.7, "maxdur": 0.8, "text": "Есть."},
             {"who": "ER", "anchor": [1290, 520], "at": 3.5, "maxdur": 1.0, "text": "А хлеб?"},
             {"who": "AI", "actor": "ainura", "at": 4.4, "maxdur": 0.8, "text": "Есть…"}]},
    {"type": "shots", "dur": 4.5, "titr": [{"at": 0.3, "dur": 3.9, "text": "Тема — под [[ваш магазин]]"}],
     "views": [{"src": "12-kassa-site-themes.png", "at": 0, "dur": 4.0, "crop": [0, 0.14, 0.8, 0.62],
                "marks": [{"at": 0.6, "kind": "click", "rect": [0.031, 0.256, 0.138, 0.045]},
                          {"at": 1.8, "kind": "hold", "dur": 1.8, "rect": [0.558, 0.565, 0.18, 0.058]}]}],
     "say": [{"who": "N", "at": 0.2, "text": "Сайт магазина — за вечер. Темы под вашу сферу."}]},
    {"type": "shots", "dur": 5.2, "titr": [{"at": 0.3, "dur": 4.6, "text": "Один [[вечер]]"}],
     "views": [{"clip": "12-kassa-site-apply.mp4", "from": 1.2, "len": 5.2, "at": 0, "dur": 5.2, "crop": [0, 0, 0.78, 0.7],
                "marks": [{"at": 0.6, "kind": "click", "rect": [0.03, 0.64, 0.14, 0.075]},
                          {"at": 1.7, "kind": "click", "rect": [0.536, 0.59, 0.132, 0.045]},
                          {"at": 3.9, "kind": "fill", "dur": 1.0, "rect": [0.015, 0.12, 0.97, 0.05]}]}],
     "say": [{"who": "N", "at": 0.2, "text": "Выбрали — применили. И тема уже в черновике."}]},
    {"type": "diagram", "kind": "flow12", "dur": 7.0,
     "titr": [{"at": 0.4, "dur": 2.6, "text": "Витрина с [[категориями]]"}, {"at": 3.4, "dur": 3.4, "text": "Наличие — [[из кассы]]"}],
     "say": [{"who": "N", "at": 0.2, "text": "Витрина с категориями — цены и товары из кассы."},
             {"who": "N", "at": 3.5, "text": "Продали последний — на сайте уже нет."}],
     "sfx": [{"n": "sent", "at": 4.9, "v": 0.7}]},
    {"type": "stage", "dur": 3.6, "counter": COUNTER,
     "actors": [{"id": "ainura", "who": "ainura", "x": 1480, "z": "back", "poses": [[0, "stand"], [0.4, "happy"]]},
                {"id": "nurlan", "who": "nurlan", "x": 800, "poses": [[0, "angry"]]}],
     "props": [{"id": "prop-phone-dark", "x": 1290, "y": 700, "s": 0.8}],
     "say": [{"who": "AI", "actor": "ainura", "at": 0.4, "maxdur": 2.2, "text": "Все на сайте смотрят."}]},
]
W(12, "12-sayt", "Свой сайт за вечер", sc12, "feat-12-sayt")

# ---------------- 13
dseq = [" · ".join(("[[%s]]" % n) if i == k else n for i, n in enumerate(["Windows", "Android", "Mac", "Linux", "телефон"])) for k in range(5)]
sc13 = [
    {"type": "stage", "dur": 5.0,
     "actors": [{"id": "nurlan", "who": "nurlan", "x": 820, "poses": [[0, "happy"]]},
                {"id": "ainura", "who": "ainura", "x": 1600, "poses": [[0, "stand"], [1.5, "calc"]]}],
     "props": [{"id": "laptop", "x": 1180, "y": 880, "s": 1.1, "in": 0.2}, {"id": "laptop", "x": 1380, "y": 880, "s": 0.9, "in": 0.5},
               {"id": "prop-phone-dark", "x": 1000, "y": 900, "s": 0.6, "in": 0.8}, {"id": "prop-calculator", "x": 1500, "y": 905, "s": 0.6, "in": 1.0}],
     "say": [{"who": "AI", "actor": "ainura", "at": 1.45, "maxdur": 1.5, "text": "Это всё к нам?"},
             {"who": "NU", "actor": "nurlan", "at": 3.1, "maxdur": 1.8, "text": "Это всё — одна касса!"}]},
    {"type": "shots", "dur": 9.0, "titr": [{"at": 0.1, "dur": 8.8, "seq": dseq, "step": 1.8, "text": ""}],
     "views": [
         {"src": "13-kassa-windows.png", "at": 0, "dur": 1.8, "frame": "monitor", "slide": 24},
         {"src": "13-kassa-android.png", "at": 1.8, "dur": 1.8, "frame": "tablet", "slide": 24},
         {"placeholder": "macOS", "at": 3.6, "dur": 1.8, "frame": "laptop", "slide": 24},
         {"placeholder": "Linux", "at": 5.4, "dur": 1.8, "frame": "monitor", "slide": 24},
         {"src": "13-mobile-home.png", "at": 7.2, "dur": 1.8, "frame": "phone", "area": [600, 26, 720, 826], "crop": [0, 0, 1, 0.58], "slide": 24}],
     "sfx": [{"n": "tick", "at": 1.8 * i, "v": 0.7} for i in range(5)],
     "say": [{"who": "N", "at": 0.2, "text": "Касса — это не коробка, а программа. Windows, Android-планшет, Mac, Linux, телефон."}]},
    {"type": "diagram", "kind": "sync13", "dur": 5.2, "titr": [{"at": 0.3, "dur": 4.6, "text": "Данные — [[общие]]"}],
     "say": [{"who": "N", "at": 0.2, "text": "Продали на одном — остаток поменялся на всех."}],
     "sfx": [{"n": "pik", "at": 1.1, "v": 0.8}, {"n": "sent", "at": 1.5, "v": 0.7}]},
    {"type": "stage", "dur": 3.8,
     "actors": [{"id": "erlan", "who": "erlan", "x": 860, "poses": [[0, "stand"], [0.5, "happy"]]},
                {"id": "nurlan", "who": "nurlan", "x": 1500, "flip": True, "poses": [[0, "happy"]]}],
     "props": [{"id": "laptop", "x": 1120, "y": 880, "s": 1.5}],
     "say": [{"who": "ER", "actor": "erlan", "at": 0.3, "maxdur": 1.6, "text": "Я на яблоке!"},
             {"who": "N", "at": 1.9, "text": "Любой экран — касса."}]},
]
W(13, "13-lyuboy-ekran", "Любой экран — касса", sc13, "feat-13-lyuboy-ekran")

# ---------------- 14
sc14 = [
    {"type": "stage", "dur": 4.4, "counter": COUNTER,
     "actors": [{"id": "ainura", "who": "ainura", "x": 1480, "z": "back", "poses": [[0, "stand"], [1.5, "calc"]]},
                {"id": "nurlan", "who": "nurlan", "x": 800, "poses": [[0, "stand"], [3.0, "angry"]]}],
     "chips": [{"text": "Enter", "x": 1480, "y": 400, "at": 1.5, "dur": 0.5}, {"text": "Enter", "x": 1480, "y": 400, "at": 2.0, "dur": 0.5},
               {"text": "Enter", "x": 1480, "y": 400, "at": 2.5, "dur": 0.5}],
     "sfx": [{"n": "thump", "at": 1.6, "v": 0.5}, {"n": "thump", "at": 2.1, "v": 0.5}, {"n": "thump", "at": 2.6, "v": 0.5}],
     "say": [{"who": "NU", "actor": "nurlan", "at": 3.05, "maxdur": 1.2, "text": "Три раза?!"}]},
    {"type": "diagram", "kind": "guard14", "dur": 6.2, "titr": [{"at": 0.3, "dur": 5.6, "text": "Двойной продажи [[нет]]"}],
     "say": [{"who": "N", "at": 0.2, "text": "Устали, нажали лишний раз — касса не проведёт продажу дважды."}],
     "sfx": [{"n": "thump", "at": 0.7, "v": 0.5}, {"n": "thump", "at": 1.3, "v": 0.5}, {"n": "thump", "at": 1.9, "v": 0.5}, {"n": "pik", "at": 5.0, "v": 0.8}]},
    {"type": "shots", "dur": 4.2, "titr": [{"at": 0.3, "dur": 3.6, "text": "Цена изменилась: 205 → [[195]]"}],
     "views": [{"src": "14-kassa-price-changed.png", "at": 0, "dur": 4.2, "crop": [0.39, 0.205, 0.605, 0.59],
                "marks": [{"at": 1.0, "kind": "hold", "dur": 2.6, "rect": [0.405, 0.66, 0.58, 0.055]}]}],
     "say": [{"who": "N", "at": 0.2, "text": "Владелец поменял цену — касса предупредит до оплаты."}]},
    {"type": "shots", "dur": 4.0, "titr": [{"at": 0.3, "dur": 3.4, "text": "Z-отчёт — [[понятный]]"}],
     "views": [{"src": "14-kassa-z-report.png", "at": 0, "dur": 4.0, "crop": [0.16, 0.04, 0.68, 0.485],
                "marks": [{"at": 0.9, "kind": "fill", "dur": 1.0, "rect": [0.19, 0.305, 0.35, 0.085]},
                          {"at": 2.0, "kind": "fill", "dur": 1.0, "rect": [0.19, 0.485, 0.3, 0.026]}]}],
     "sfx": [{"n": "receipt", "at": 0.3, "v": 0.6}],
     "say": [{"who": "N", "at": 0.2, "text": "А вечером — Z-отчёт, понятный без бухгалтера."}]},
    {"type": "stage", "dur": 6.2,
     "props": [{"id": "crate", "x": 900, "y": 930, "s": 1.0}],
     "actors": [{"id": "nurlan", "who": "nurlan", "x": 900, "y": 905, "poses": [[0, "stand"], [2.4, "happy"]]},
                {"id": "ainura", "who": "ainura", "x": 1400, "z": "back", "poses": [[0, "stand"], [0.3, "happy"]]}],
     "say": [{"who": "AI", "actor": "ainura", "at": 0.2, "maxdur": 2.0, "text": "Касса умнее нас обоих."},
             {"who": "NU", "actor": "nurlan", "at": 2.3, "maxdur": 1.2, "text": "Тебя — точно."},
             {"who": "N", "at": 3.5, "maxdur": 2.6, "text": "Ошибаться можно. Терять деньги — нельзя."}]},
]
W(14, "14-bez-oshibok", "Касса не даёт ошибиться", sc14, "feat-14-bez-oshibok")

# ---------------- 15
sc15 = [
    {"type": "stage", "dur": 4.2, "counter": COUNTER,
     "actors": [{"id": "apa", "who": "apa", "x": 1480, "z": "back", "poses": [[0, "stand"], [1.2, "sad"]]},
                {"id": "jyldyz", "who": "jyldyz", "x": 400, "poses": [[0, "stand"]]},
                {"id": "ata", "who": "ata", "x": 640, "poses": [[0, "stand"]]},
                {"id": "erlan", "who": "erlan", "x": 880, "poses": [[0, "stand"]]}],
     "say": [{"who": "AP", "actor": "apa", "at": 1.45, "maxdur": 2.2, "text": "Где тут… возврат?", "tts": "Где тут... возврат?"}]},
    {"type": "shots", "dur": 4.8, "titr": [{"at": 0.3, "dur": 4.2, "text": "Код — [[оператору]]"}],
     "chips": [{"text": "☎ Алло, Нур?", "x": 960, "y": 66, "at": 0.2, "dur": 1.6, "size": 42}],
     "views": [{"src": "15-kassa-remote-code.png", "at": 0, "dur": 4.8, "crop": [0.275, 0.265, 0.45, 0.25],
                "plates": [{"rect": [0.365, 0.375, 0.27, 0.05], "text": "••• ••• •••  PIN ••••"}],
                "marks": [{"at": 1.2, "kind": "click", "rect": [0.286, 0.279, 0.428, 0.22]}]}],
     "say": [{"who": "AP", "at": 0.3, "text": "Алло, Нур?", "show": False}, {"who": "N", "at": 1.9, "text": "Назовите оператору код и пин."}]},
    {"type": "shots", "dur": 4.2, "titr": [{"at": 0.3, "dur": 3.6, "text": "Только с вашего [[разрешения]]"}],
     "views": [{"src": "15-kassa-remote-consent.png", "at": 0, "dur": 4.2, "crop": [0.30, 0.375, 0.40, 0.25],
                "marks": [{"at": 1.4, "kind": "click", "rect": [0.326, 0.533, 0.096, 0.046]}]}],
     "say": [{"who": "N", "at": 0.2, "text": "Нажмите «Разрешить» — подключатся только с вашего согласия."}]},
    {"type": "shots", "dur": 6.0, "titr": [{"at": 0.3, "dur": 5.2, "text": "Показывает, где [[нажать]]"}],
     "views": [{"clip": "15-kassa-remote-session.mp4", "from": 1.0, "len": 6.0, "at": 0, "dur": 6.0, "crop": [0, 0, 1, 1],
                "cursor": {"label": "Айдар", "at": 0.4, "dur": 5.4, "path": [[0, 0.5, 0.5], [1.0, 0.027, 0.04], [2.2, 0.027, 0.04], [3.2, 0.8, 0.29], [5.4, 0.8, 0.29]]},
                "marks": [{"at": 1.0, "kind": "hold", "dur": 1.6, "rect": [0.012, 0.015, 0.035, 0.047]},
                          {"at": 3.3, "kind": "hold", "dur": 2.4, "rect": [0.65, 0.265, 0.34, 0.065]}]}],
     "say": [{"who": "N", "at": 0.2, "text": "Наш специалист показывает, где нажать. Без чужих программ."}]},
    {"type": "stage", "dur": 4.6, "counter": COUNTER,
     "actors": [{"id": "apa", "who": "apa", "x": 1480, "z": "back", "poses": [[0, "stand"], [0.3, "happy"]]},
                {"id": "jyldyz", "who": "jyldyz", "x": 400, "poses": [[0, "stand"], [0.8, "happy"]]},
                {"id": "ata", "who": "ata", "x": 640, "poses": [[0, "stand"], [1.0, "happy"]]},
                {"id": "erlan", "who": "erlan", "x": 880, "poses": [[0, "stand"], [1.2, "happy"]]}],
     "say": [{"who": "AP", "actor": "apa", "at": 0.3, "maxdur": 1.5, "text": "А-а, вот оно!"},
             {"who": "N", "at": 2.2, "text": "За минуту — даже Бүбү-апа.", "tts": "За минуту — даже Бубу-апа."}]},
]
W(15, "15-pomoshch", "Помощь за минуту", sc15, "feat-15-pomoshch")
print("OK")
