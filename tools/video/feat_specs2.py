# -*- coding: utf-8 -*-
"""2026-10-30: спецификации роликов-фишек №2-8 (продолжение feat_specs.py, где №1 и помощники)."""
from feat_specs import SPECS, store, ch, back, finale  # noqa: F401


def stack_html(h=13):
    sheets = "".join('<div style="position:absolute;left:%dpx;bottom:%dpx;width:226px;height:30px;background:#fff;border:4px solid #191919;border-radius:3px"></div>' % ((i % 3) * 7, i * 19) for i in range(h))
    return '<div style="position:relative;width:250px;height:%dpx">%s</div>' % (19 * h + 40, sheets)


HOUSE = ('<div style="position:relative;width:260px;height:230px">'
         '<div style="position:absolute;left:30px;bottom:0;width:200px;height:130px;background:#fff;border:5px solid #191919"></div>'
         '<div style="position:absolute;left:100px;bottom:0;width:60px;height:80px;background:#FFB208;border:5px solid #191919"></div>'
         '<div style="position:absolute;left:8px;bottom:125px;width:244px;height:96px;background:#191919;clip-path:polygon(50% 0,100% 100%,0 100%)"></div>'
         '<div style="position:absolute;left:26px;bottom:131px;width:208px;height:70px;background:#FFB208;clip-path:polygon(50% 0,100% 100%,0 100%)"></div></div>')

TIMER = ('<div style="background:#191919;color:#FFB208;font:800 66px/1 Montserrat,sans-serif;padding:16px 30px;border-radius:16px;'
         'border:5px solid #FFB208;letter-spacing:.04em">0:58</div>')

FULL = [100, 36, 1720, 790]      # поле экрана
PHONE_C = [740, 20, 440, 820]    # телефон по центру
STACK_Y = 600 - (19 * 13 + 40)

# ---------------------------------------------------------------- 2. Спроси Нура
SPECS[2] = {
    "slug": "sprosi-nura", "name": "Спроси Нура",
    "scenes": [
        {"kind": "hook", "min": 4.0, "intro": {"num": "02 / 15", "name": "Спроси Нура"},
         "store": store(tint=0.18),
         "actors": [back("nurlan", "char-nurlan-stand", 1130, swaps=[{"at": 3.0, "img": "char-nurlan-angry"}]),
                    ch("apa", "char-apa-stand", 470)],
         "items": [{"img": "prop-notebook", "x": 1010, "y": 540, "s": 1.5, "layer": "counter"}],
         "lines": [{"who": "AP", "text": "Сколько заработали за неделю?", "at": 1.4, "b": "apa", "bw": 560},
                   {"who": "NU", "text": "…сейчас посчитаю", "at": 3.3, "b": "nurlan", "bw": 460}],
         "sfx": [{"n": "paper", "at": 0.4}]},
        {"kind": "shot", "min": 4.4, "lead": 0.5,
         "panels": [{"image": "02-owner-advisor-week.png", "crop": [250, 140, 1750, 330], "area": [90, 150, 1740, 520],
                     "clicks": [{"at": 1.4, "rect": [895, 283, 160, 42]}, {"at": 2.4, "rect": [765, 416, 80, 40]}, {"at": 3.1, "rect": [1270, 416, 95, 40]}]}],
         "lines": [{"who": "N", "text": "Вечером не тетрадка — спросите Нура."}, {"who": "N", "text": "Сколько заработали,", "gap": 0.1}],
         "titr": [{"text": "Спроси [[Нура]]", "at": 0.7, "dur": 3.6, "bottom": 190}]},
        {"kind": "shot", "min": 2.6, "lead": 0.2,
         "panels": [{"image": "02-owner-advisor-lowstock.png", "crop": [250, 140, 1750, 330], "area": [90, 150, 1740, 520],
                     "clicks": [{"at": 0.9, "rect": [990, 283, 355, 42]}, {"at": 1.7, "rect": [317, 346, 375, 84]}]}],
         "lines": [{"who": "N", "text": "что заканчивается,"}],
         "titr": [{"text": "Что [[заканчивается]]", "at": 0.3, "dur": 2.6, "bottom": 190}]},
        {"kind": "shot", "min": 4.6, "lead": 0.4,
         "panels": [{"video": {"name": "02-mobile-advisor-call", "from": 0, "to": 4.9}, "frame": "phone", "area": PHONE_C,
                     "clicks": [{"at": 2.7, "rect": [51, 110, 980, 255], "hold": 0.8}]}],
         "lines": [{"who": "N", "text": "какую цену поменять. Можно голосом — по-русски или по-кыргызски."}],
         "sfx": [{"n": "sent", "at": 2.5}],
         "chips": [{"text": "«Поменяй цену<br>на хлеб на [[28]]»", "x": 80, "y": 300, "size": 64, "color": "#fff", "at": 1.0}],
         "titr": [{"text": "Скажи — и [[готово]]", "at": 0.8, "dur": 3.6}]},
        {"kind": "shot", "min": 3.2, "lead": 0.2,
         "panels": [{"image": "02-mobile-ai-undo.png", "crop": [0, 60, 1080, 1100], "frame": "phone", "area": PHONE_C,
                     "clicks": [{"at": 1.1, "rect": [50, 550, 980, 230], "hold": 0.9}, {"at": 2.2, "rect": [50, 800, 980, 340], "hold": 0.7}]}],
         "lines": [{"who": "N", "text": "Ошиблись? Одна кнопка — и всё обратно."}],
         "chips": [{"text": "«Нан»: цена 28 сом<br>(было [[30 сом]])<br>— отменено", "x": 1260, "y": 300, "size": 54, "color": "#fff", "at": 1.2}],
         "titr": [{"text": "Любое действие — с [[отменой]]", "at": 0.4, "dur": 3.0}]},
        {"kind": "hook", "min": 2.4, "lead": 0.2,
         "store": store(tint=0.18),
         "actors": [back("nurlan", "char-nurlan-happy", 1130), ch("apa", "char-apa-happy", 470)],
         "lines": [{"who": "AP", "text": "Умный. Как я.", "at": 0.3, "b": "apa", "bw": 420}]},
        finale(),
    ],
}

# ---------------------------------------------------------------- 3. Наведи и забудь
SPECS[3] = {
    "slug": "navedi-i-zabud", "name": "Наведи и забудь",
    "scenes": [
        {"kind": "hook", "min": 3.9, "intro": {"num": "03 / 15", "name": "Наведи и забудь"},
         "store": store(),
         "actors": [back("ainura", "char-ainura-stand", 1130, swaps=[{"at": 2.6, "img": "char-ainura-angry"}]),
                    ch("jyldyz", "char-jyldyz-sad", 430), ch("bakyt", "char-bakyt-angry", 760, s=1.05, h=430)],
         "items": [{"img": "prop-bottle", "x": 1060, "y": 560, "s": 1.5, "layer": "counter",
                    "path": [{"at": 0, "x": 1060, "y": 560, "r": 0}, {"at": 3.0, "x": 1060, "y": 560, "r": 540}]}],
         "lines": [{"who": "BA", "text": "Там нет полосочек!", "at": 1.7, "b": "bakyt", "bw": 460}],
         "sfx": [{"n": "paper", "at": 0.5}]},
        {"kind": "shot", "min": 5.0, "lead": 0.5,
         "panels": [{"video": {"name": "03-mobile-smart-camera", "from": 0, "to": 5.0}, "frame": "phone", "area": PHONE_C,
                     "clicks": [{"at": 2.6, "rect": [100, 1730, 900, 130], "hold": 0.7}, {"at": 3.6, "rect": [60, 1920, 400, 80], "hold": 0.6}]}],
         "lines": [{"who": "N", "text": "Нет штрихкода — наведите камеру, касса узнает товар по виду."}],
         "sfx": [{"n": "click", "at": 1.15}, {"n": "bell", "at": 1.85, "v": 0.5}],
         "chips": [{"text": "Адыгене 1,5 л<br>[[55 сом]] · на складе 24 шт", "x": 1260, "y": 300, "size": 48, "color": "#fff", "at": 2.4}],
         "titr": [{"text": "Узнаёт по [[виду]]", "at": 1.2, "dur": 3.6}]},
        {"kind": "shot", "min": 6.0, "lead": 0.3,
         "panels": [{"video": {"name": "03-kassa-receiving-unknown", "from": 1.2, "to": 7.2}, "crop": [0, 0, 1296, 545], "area": FULL,
                     "clicks": [{"at": 1.7, "rect": [38, 458, 200, 48], "hold": 0.8}, {"at": 3.0, "rect": [785, 458, 125, 45], "hold": 0.8}]}],
         "lines": [{"who": "N", "text": "Новый штрихкод — название подставится само: в кассе уже справочник на сто двадцать восемь тысяч товаров."}],
         "sfx": [{"n": "bell", "at": 1.05, "v": 0.5}],
         "titr": [{"text": "128 000 товаров [[уже внутри]]", "at": 0.9, "dur": 4.6}]},
        {"kind": "hook", "min": 3.6, "lead": 0.2,
         "store": store(),
         "actors": [back("ainura", "char-ainura-happy", 1130), ch("jyldyz", "char-jyldyz-stand", 430), ch("bakyt", "char-bakyt-stand", 760, s=1.05, h=430)],
         "items": [{"img": "prop-bottle", "x": 1060, "y": 560, "s": 1.5, "layer": "counter"}],
         "lines": [{"who": "BA", "text": "А полосочки?", "at": 0.3, "b": "bakyt", "bw": 400},
                   {"who": "AI", "text": "Не нужны.", "at": 1.5, "b": "ainura", "bw": 360},
                   {"who": "N", "text": "Наведи — и забудь.", "at": 2.4}]},
        finale(),
    ],
}

# ---------------------------------------------------------------- 4. Накладная за минуту
SPECS[4] = {
    "slug": "nakladnaya-za-minutu", "name": "Накладная за минуту",
    "scenes": [
        {"kind": "hook", "min": 3.7, "intro": {"num": "04 / 15", "name": "Накладная за минуту"},
         "store": store(),
         "actors": [back("ainura", "char-ainura-stand", 1250), ch("nurlan", "char-nurlan-stand", 470), ch("bakyt", "char-bakyt-stand", 1700, s=1.05, h=430)],
         "items": [{"markup": stack_html(), "x": 960, "y": STACK_Y, "layer": "counter", "at": 0.3,
                    "path": [{"at": 0.25, "x": 960, "y": -300}, {"at": 0.6, "x": 960, "y": STACK_Y}]}],
         "lines": [{"who": "NU", "text": "До ночи.", "at": 1.7, "b": "nurlan", "bw": 360}],
         "sfx": [{"n": "thump", "at": 0.55}, {"n": "paper", "at": 0.6}]},
        {"kind": "shot", "min": 6.0, "lead": 0.4,
         "panels": [{"video": {"name": "04-kassa-receiving-xlsx", "from": 0.7, "to": 6.8}, "crop": [0, 0, 1296, 880], "area": [150, 30, 1620, 800],
                     "clicks": [{"at": 1.4, "rect": [90, 290, 1115, 440], "hold": 0.9}, {"at": 3.0, "rect": [978, 774, 220, 45], "hold": 0.5},
                                {"at": 4.4, "rect": [698, 415, 92, 365]}, {"at": 5.1, "rect": [908, 415, 145, 365], "hold": 0.6}]}],
         "lines": [{"who": "N", "text": "Накладная от поставщика — Excel, XML или ТОРГ-12 — загружается за минуту."}],
         "titr": [{"text": "Excel · XML · [[ТОРГ-12]]", "at": 0.6, "dur": 5.2}]},
        {"kind": "shot", "min": 2.8, "lead": 0.2,
         "panels": [{"image": "04-kassa-markup-all.png", "crop": [0, 0, 1296, 879], "area": [150, 30, 1620, 800],
                     "focus": [{"at": 0.3, "dur": 0.7, "rect": [330, 140, 640, 610]}],
                     "clicks": [{"at": 1.3, "rect": [370, 383, 117, 36], "hold": 0.7}]}],
         "lines": [{"who": "N", "text": "Наценку ставите одну на всех."}],
         "titr": [{"text": "Наценка — [[одной кнопкой]]", "at": 0.3, "dur": 2.3}]},
        {"kind": "shot", "min": 3.8, "lead": 0.2,
         "panels": [{"image": "04-kassa-below-cost.png", "crop": [20, 300, 1250, 490], "area": FULL,
                     "clicks": [{"at": 0.8, "rect": [35, 712, 1228, 52], "fill": True, "hold": 0.8}, {"at": 1.7, "rect": [430, 404, 370, 75], "hold": 0.8},
                                {"at": 2.8, "rect": [540, 507, 118, 47], "hold": 0.6}]}],
         "lines": [{"who": "N", "text": "А если цена вышла ниже закупки — касса не даст провести."}],
         "sfx": [{"n": "regbeep", "at": 0.8, "v": 0.6}],
         "titr": [{"text": "В минус не [[продадите]]", "at": 0.3, "dur": 3.3}]},
        {"kind": "hook", "min": 3.4, "lead": 0.2,
         "store": store(),
         "actors": [back("ainura", "char-ainura-happy", 1250), ch("nurlan", "char-nurlan-stand", 470), ch("bakyt", "char-bakyt-happy", 1700, s=1.05, hop=[1.0], h=430)],
         "items": [{"markup": HOUSE, "x": 1010, "y": 372, "layer": "counter", "at": 0.1}, {"markup": TIMER, "x": 1480, "y": 80, "layer": "back"}],
         "lines": [{"who": "N", "text": "Стопка бумаги теперь — домик для Бакыта."}],
         "titr": [{"text": "За [[минуту]]", "at": 0.3, "dur": 2.6}]},
        finale(),
    ],
}

# ---------------------------------------------------------------- 5. Куда уходят деньги
SPECS[5] = {
    "slug": "kuda-uhodyat-dengi", "name": "Куда уходят деньги",
    "scenes": [
        {"kind": "hook", "min": 3.7, "intro": {"num": "05 / 15", "name": "Куда уходят деньги"},
         "store": store(),
         "actors": [ch("nurlan", "char-nurlan-stand", 560, swaps=[{"at": 2.2, "img": "char-nurlan-angry"}]), back("ainura", "char-ainura-stand", 1250)],
         "items": [{"img": "prop-calculator", "x": 1000, "y": 540, "s": 1.5, "layer": "counter"}],
         "lines": [{"who": "NU", "text": "Было двадцать. Стало… шестнадцать?", "at": 1.5, "b": "nurlan", "bw": 600}],
         "sfx": [{"n": "calc", "at": 0.3, "times": [0.0, 0.14, 0.3, 0.44, 0.7]}]},
        {"kind": "shot", "min": 4.6, "lead": 0.3,
         "panels": [{"video": {"name": "05-kassa-revision-scan", "from": 1.0, "to": 10.6}, "rate": 2.2, "crop": [0, 90, 1296, 430], "area": [100, 60, 1720, 640]}],
         "lines": [{"who": "N", "text": "Деньги уходят тихо — по пачке, по бутылке."}, {"who": "N", "text": "Ревизия сканером —", "gap": 0.1}],
         "sfx": [{"n": "pik", "at": 0.3}, {"n": "pik", "at": 1.35}, {"n": "pik", "at": 2.5}, {"n": "pik", "at": 3.6}],
         "titr": [{"text": "Ревизия — [[сканером]]", "at": 0.7, "dur": 3.6}]},
        {"kind": "shot", "min": 6.8, "lead": 0.3,
         "panels": [{"image": "05-kassa-revision-finish.png", "crop": [165, 50, 970, 780], "area": [100, 20, 1720, 860], "until": 1.6},
                    {"image": "05-kassa-revision-finish.png", "crop": [185, 140, 890, 75], "area": [100, 330, 1720, 200], "from": 1.5, "until": 3.5,
                     "clicks": [{"at": 1.9, "rect": [190, 166, 545, 22], "hold": 0.9}, {"at": 2.8, "rect": [739, 166, 320, 22], "hold": 0.6}]},
                    {"image": "05-kassa-revision-finish.png", "crop": [185, 300, 925, 250], "area": [100, 170, 1720, 470], "from": 3.5,
                     "clicks": [{"at": 4.6, "rect": [190, 350, 915, 36], "fill": True, "hold": 0.6}, {"at": 5.3, "rect": [190, 386, 915, 36], "fill": True, "hold": 0.6}]}],
         "lines": [{"who": "N", "text": "и касса показывает, сколько потеряно по закупке"}, {"who": "N", "text": "и на чём именно. Теперь вы знаете, куда смотреть.", "gap": 0.1}],
         "sfx": [{"n": "thump", "at": 0.8, "v": 0.6}],
         "titr": [{"text": "[[−12 400 сом]] по закупке", "at": 0.4, "dur": 3.0}, {"text": "[[Топ]] недостач", "at": 3.7, "dur": 2.8}]},
        {"kind": "hook", "min": 3.2, "lead": 0.2,
         "store": store(),
         "actors": [ch("nurlan", "char-nurlan-angry", 560), ch("erlan", "char-erlan-stand", 1000, swaps=[{"at": 1.4, "img": "char-erlan-angry"}]), back("ainura", "char-ainura-stand", 1250)],
         "lines": [{"who": "ER", "text": "Я заплатил!", "at": 1.0, "b": "erlan", "bw": 420}],
         "sfx": [{"n": "tick", "at": 0.3}]},
        finale(),
    ],
}

# ---------------------------------------------------------------- 6. Зарплата от прибыли
SPECS[6] = {
    "slug": "zarplata-ot-pribyli", "name": "Зарплата от прибыли",
    "scenes": [
        {"kind": "hook", "min": 4.4, "intro": {"num": "06 / 15", "name": "Зарплата от прибыли"},
         "store": store(),
         "actors": [back("ainura", "char-ainura-angry", 1130), ch("nurlan", "char-nurlan-angry", 1600), ch("ata", "char-ata-stand", 330), ch("apa", "char-apa-stand", 620)],
         "lines": [{"who": "AI", "text": "Я продала на сто тысяч!", "at": 1.5, "b": "ainura", "bw": 520},
                   {"who": "NU", "text": "А закупка?!", "at": 3.1, "b": "nurlan", "bw": 360}],
         "titr": [{"text": "Спор о [[зарплате]]", "at": 2.0, "dur": 2.3}]},
        {"kind": "shot", "min": 7.0, "lead": 0.3,
         "panels": [{"image": "06-owner-salary-scheme.png", "crop": [40, 190, 2540, 800], "area": [60, 36, 1800, 790],
                     "clicks": [{"at": 3.6, "rect": [2260, 275, 260, 38]}, {"at": 4.4, "rect": [2260, 325, 260, 38]}, {"at": 5.2, "rect": [2260, 480, 260, 230], "hold": 0.6},
                                {"at": 6.0, "rect": [40, 722, 2480, 44], "fill": True, "hold": 0.6}]}],
         "lines": [{"who": "N", "text": "Спор о зарплате решает не голос громче, а экран."},
                   {"who": "N", "text": "Зарплата от прибыли: выручка минус закупка минус расходы — и доля сотрудника.", "gap": 0.2}],
         "titr": [{"text": "Выручка − закупка − [[расходы]]", "at": 3.2, "dur": 3.8, "bottom": 190}]},
        {"kind": "shot", "min": 3.6, "lead": 0.3,
         "panels": [{"image": "06-owner-salary-employee.png", "crop": [40, 985, 2420, 440], "area": [60, 140, 1800, 560],
                     "clicks": [{"at": 0.8, "rect": [60, 1020, 2420, 82], "hold": 0.8}, {"at": 1.8, "rect": [70, 1205, 1460, 190], "hold": 0.8}]}],
         "lines": [{"who": "N", "text": "Все видят одно и то же — и все довольны."}],
         "titr": [{"text": "Честно — в [[цифрах]]", "at": 0.3, "dur": 3.0, "bottom": 190}]},
        {"kind": "hook", "min": 3.4, "lead": 0.2,
         "store": store(),
         "actors": [back("ainura", "char-ainura-happy", 1130), ch("nurlan", "char-nurlan-happy", 1600, hop=[0.6]), ch("ata", "char-ata-stand", 330, swaps=[{"at": 1.4, "img": "char-ata-angry"}]), ch("apa", "char-apa-stand", 620)],
         "lines": [{"who": "N", "text": "Кроме ата — он ждёт.", "at": 0.3}, {"who": "AT", "text": "Лепёшку пробейте!", "at": 1.9, "b": "ata", "bw": 440}],
         "sfx": [{"n": "tick", "at": 0.5}]},
        finale(),
    ],
}

# ---------------------------------------------------------------- 7. Без интернета
BADGE = [903, 40, 208, 42]
SPECS[7] = {
    "slug": "bez-interneta", "name": "Без интернета",
    "scenes": [
        {"kind": "hook", "min": 4.0, "intro": {"num": "07 / 15", "name": "Без интернета"},
         "store": store(router={"x": 1190}),
         "actors": [ch("erlan", "char-erlan-angry", 460), back("ainura", "char-ainura-stand", 1130)],
         "lines": [{"who": "ER", "text": "Интернета нет — это что, опять конец смены?", "at": 1.3, "b": "erlan", "bw": 700},
                   {"who": "AI", "text": "Азыр.", "at": 3.5, "b": "ainura", "bw": 300}],
         "sfx": [{"n": "tik", "at": 0.3}, {"n": "tik", "at": 0.8}]},
        {"kind": "shot", "min": 4.2, "lead": 0.3,
         "panels": [{"video": {"name": "07-kassa-offline-badge", "from": 0, "to": 6.5}, "rate": 1.4, "crop": [880, 0, 1712, 830], "area": FULL,
                     "clicks": [{"at": 0.6, "rect": BADGE, "hold": 0.8}, {"at": 3.0, "rect": BADGE, "hold": 0.8}]}],
         "lines": [{"who": "N", "text": "Провайдер упал — касса продаёт дальше."}],
         "sfx": [{"n": "pik", "at": 1.5}, {"n": "receipt", "at": 2.95}],
         "titr": [{"text": "Провайдер упал. [[Касса — нет]]", "at": 0.5, "dur": 3.4}]},
        {"kind": "shot", "min": 3.4, "lead": 0.3,
         "panels": [{"image": "07-kassa-offline-debt.png", "crop": [680, 0, 1912, 1430], "area": FULL,
                     "clicks": [{"at": 0.7, "rect": BADGE, "hold": 0.7}, {"at": 1.6, "rect": [1055, 753, 390, 103], "hold": 0.7}, {"at": 2.4, "rect": [1892, 634, 652, 98], "hold": 0.7}]}],
         "lines": [{"who": "N", "text": "И новое: «в долг» —"}],
         "titr": [{"text": "[[В долг]] — без интернета", "at": 0.4, "dur": 3.3}]},
        {"kind": "shot", "min": 3.2, "lead": 0.2,
         "panels": [{"image": "07-kassa-offline-return.png", "crop": [150, 8, 995, 842], "area": [120, 40, 880, 780], "until": 2.0,
                     "clicks": [{"at": 0.5, "rect": [185, 62, 330, 34], "hold": 0.8}]},
                    {"image": "07-kassa-offline-debt-pay.png", "crop": [234, 75, 830, 730], "area": [1000, 40, 840, 780], "from": 1.8,
                     "masks": [{"rect": [335, 296, 175, 22], "blur": 8}, {"rect": [335, 335, 175, 22], "blur": 8}, {"rect": [335, 374, 175, 22], "blur": 8}],
                     "clicks": [{"at": 2.3, "rect": [260, 365, 780, 38], "hold": 0.6}, {"at": 2.9, "rect": [875, 747, 162, 44], "hold": 0.6}]}],
         "lines": [{"who": "N", "text": "возврат и оплата долга — тоже без интернета."}],
         "titr": [{"text": "Возврат и долг — [[тоже]]", "at": 0.3, "dur": 3.0}]},
        {"kind": "shot", "min": 4.6, "lead": 0.3,
         "panels": [{"video": {"name": "07-kassa-queue-flush", "from": 0, "to": 7.3}, "rate": 1.5, "crop": [880, 0, 1712, 830], "area": FULL,
                     "clicks": [{"at": 0.4, "rect": BADGE, "hold": 0.8}, {"at": 1.5, "rect": BADGE, "hold": 0.6}]}],
         "lines": [{"who": "N", "text": "Чеки копятся и уходят сами, когда связь вернётся."}],
         "sfx": [{"n": "sent", "at": 1.3}, {"n": "sent", "at": 2.8}],
         "titr": [{"text": "[[14 → 0]]. Сами", "at": 0.5, "dur": 4.0}]},
        {"kind": "hook", "min": 3.2, "lead": 0.2,
         "store": store(),
         "actors": [ch("erlan", "char-erlan-happy", 460), back("ainura", "char-ainura-happy", 1130), ch("nurlan", "char-nurlan-stand", 1650)],
         "lines": [{"who": "ER", "text": "И… всё?", "at": 0.4, "b": "erlan", "bw": 340},
                   {"who": "NU", "text": "Всё. Иди уже.", "at": 1.2, "b": "nurlan", "bw": 420}]},
        finale(),
    ],
}

# ---------------------------------------------------------------- 8. Одна сеть — один клиент
SPECS[8] = {
    "slug": "odna-set-odin-klient", "name": "Одна сеть — один клиент",
    "scenes": [
        {"kind": "hook", "min": 4.4, "intro": {"num": "08 / 15", "name": "Одна сеть — один клиент"},
         "store": store(sign="НУР-2"),
         "actors": [ch("apa", "char-apa-angry", 460), back("ainura", "char-ainura-stand", 1130)],
         "lines": [{"who": "AP", "text": "У меня были бонусы!", "at": 1.4, "b": "apa", "bw": 560},
                   {"who": "AI", "text": "Проверим.", "at": 3.1, "b": "ainura", "bw": 360}]},
        {"kind": "shot", "min": 4.2, "lead": 0.3,
         "panels": [{"image": "08-kassa-bonus-accrue.png", "crop": [0, 0, 2592, 1420], "area": FULL,
                     "focus": [{"at": 1.4, "dur": 0.8, "rect": [1010, 380, 1570, 960]}],
                     "clicks": [{"at": 0.6, "rect": [140, 36, 300, 55], "hold": 0.7}, {"at": 2.4, "rect": [1891, 818, 648, 128], "hold": 0.9}]}],
         "lines": [{"who": "N", "text": "Два магазина — одна сеть. Бонусы копятся здесь,"}],
         "sfx": [{"n": "tik", "at": 2.5}],
         "titr": [{"text": "Магазин 1: [[копим]]", "at": 0.5, "dur": 3.4}]},
        {"kind": "shot", "min": 3.0, "lead": 0.2,
         "panels": [{"image": "08-kassa-bonus-pay.png", "crop": [0, 0, 2592, 1420], "area": FULL,
                     "focus": [{"at": 0.3, "dur": 0.8, "rect": [1010, 380, 1570, 960]}],
                     "clicks": [{"at": 0.4, "rect": [140, 36, 300, 55], "hold": 0.6}, {"at": 1.4, "rect": [2330, 823, 180, 78], "hold": 0.6}, {"at": 2.0, "rect": [1295, 583, 340, 65], "hold": 0.6}]}],
         "lines": [{"who": "N", "text": "а тратятся — в другом."}],
         "sfx": [{"n": "bell", "at": 1.5, "v": 0.5}],
         "titr": [{"text": "Магазин 2: [[тратим]]", "at": 0.3, "dur": 2.6}]},
        {"kind": "shot", "min": 3.2, "lead": 0.2,
         "panels": [{"image": "08-owner-bonus-analytics.png", "crop": [30, 232, 2540, 620], "area": [90, 90, 1740, 600],
                     "clicks": [{"at": 0.7, "rect": [52, 258, 606, 160]}, {"at": 1.4, "rect": [679, 258, 615, 160]}, {"at": 2.1, "rect": [1930, 258, 600, 160]}]}],
         "lines": [{"who": "N", "text": "Клиент один, отчёт один,"}],
         "titr": [{"text": "Одна сеть — [[один клиент]]", "at": 0.3, "dur": 2.8, "bottom": 190}]},
        {"kind": "shot", "min": 4.6, "lead": 0.2,
         "panels": [{"image": "08-owner-branches.png", "crop": [0, 500, 2592, 1000], "area": [90, 60, 1740, 700],
                     "clicks": [{"at": 0.8, "rect": [75, 775, 650, 60], "hold": 0.8}, {"at": 1.8, "rect": [75, 1050, 650, 60], "hold": 0.8}, {"at": 2.8, "rect": [75, 1330, 650, 60], "hold": 0.8}]}],
         "lines": [{"who": "N", "text": "Остатки общие. Касса помнит каждого — даже Бубу-апу.", "sub": "Остатки общие. Касса помнит каждого — даже Бүбү-апу."}],
         "titr": [{"text": "Филиалы — [[общие остатки]]", "at": 0.4, "dur": 3.8, "bottom": 190}]},
        {"kind": "hook", "min": 3.0, "lead": 0.2,
         "store": store(sign="НУР-2"),
         "actors": [ch("apa", "char-apa-happy", 460, hop=[0.4]), back("ainura", "char-ainura-stand", 1130)],
         "items": [{"img": "prop-bag", "x": 560, "y": 760, "s": 1.0, "layer": "front"}],
         "lines": [{"who": "AP", "text": "Умница касса!", "at": 0.3, "b": "apa", "bw": 460},
                   {"who": "AI", "text": "Это не я…", "at": 1.8, "b": "ainura", "bw": 360}]},
        finale(),
    ],
}

# 2026-10-30: в ролике 2 много закадра — рассказчик чуть быстрее, чтобы уложиться в 30 с
for _sc in SPECS[2]["scenes"]:
    for _ln in _sc.get("lines", []):
        if _ln["who"] == "N" and "rate" not in _ln and _sc["kind"] != "finale":
            _ln["rate"] = "+14%"
SPECS[2]["scenes"][5]["tail"] = 0.25
