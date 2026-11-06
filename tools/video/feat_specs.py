# -*- coding: utf-8 -*-
"""2026-10-30: спецификации роликов-фишек NurCRM №1-8 (русская версия) — по docs/Дизайн/Ролики-фишки — 15 сценариев.md.
Сцена: kind = hook (сценка с героями) | shot (экран/телефон со стенда) | finale (плашка 4 с).
lines: реплики (N — рассказчик, AT/NU/AI/AP/BA/ER/JY — герои); at — секунды от начала сцены, иначе подряд.
Рамки/кропы/маски — в пикселях исходного снимка (для видео — размер оригинального mp4)."""

FIN_TEXT = "Нур Си-Эр-Эм — касса, которая знает ваш магазин. Апп точка нур Си-Эр-Эм точка кей-джи."


def finale():
    return {"kind": "finale", "min": 4.0, "tail": 0.35, "lead": 0.15, "lines": [{"who": "N", "text": FIN_TEXT, "rate": "+16%"}]}


def store(counter=True, **kw):
    d = {"counter": {"x": 640, "y": 300, "s": 1.0} if counter else None}
    d.update(kw)
    return d


def ch(id_, img, x, feet=1010, s=0.95, **kw):
    d = {"id": id_, "img": img, "x": x, "feet": feet, "s": s, "bob": True}
    d.update(kw)
    return d


def back(id_, img, x, feet=790, s=0.9, **kw):
    return ch(id_, img, x, feet, s, layer="back", **kw)


SPECS = {}

# ---------------------------------------------------------------- 1. Телефон вместо сканера
SPECS[1] = {
    "slug": "telefon-vmesto-skanera", "name": "Телефон вместо сканера",
    "scenes": [
        {"kind": "hook", "min": 4.9, "intro": {"num": "01 / 15", "name": "Телефон вместо сканера"},
         "store": store(),
         "actors": [back("ainura", "char-ainura-stand", 1120, swaps=[{"at": 3.9, "img": "char-ainura-scan"}], shake={"at": 0.25, "dur": 0.2, "amp": 5}),
                    ch("ata", "char-ata-stand", 430),
                    ch("nurlan", "char-nurlan-walk", 1680, enter={"at": 2.1, "dur": 0.7, "dx": 420}, swaps=[{"at": 2.9, "img": "char-nurlan-angry"}], show=2.0)],
         "lines": [{"who": "AT", "text": "Дочка, он у тебя умер?", "at": 1.55, "b": "ata"},
                   {"who": "NU", "text": "Новый сканер — шесть тысяч!", "at": 3.0, "b": "nurlan", "bw": 520}],
         "sfx": [{"n": "click", "at": 0.35}, {"n": "click", "at": 0.9}],
         "titr": [{"text": "Сканер [[сломался]]", "at": 2.9, "dur": 2.0}]},
        {"kind": "shot", "min": 6.2, "lead": 0.5,
         "panels": [
             {"video": {"name": "01-mobile-pair", "from": 0, "to": 8.5}, "frame": "phone", "area": [120, 30, 420, 800], "rate": 1.3},
             {"image": "01-kassa-phonelink-qr.png", "crop": [275, 119, 745, 378], "area": [620, 140, 1240, 640],
              "masks": [{"rect": [808, 208, 106, 26], "blur": 8}],
              "clicks": [{"at": 1.3, "rect": [310, 222, 258, 255], "hold": 0.8}]}],
         "lines": [{"who": "N", "text": "Сканер сломался? Не беги в магазин за новым."},
                   {"who": "N", "text": "Телефон кассира подключается к кассе за пять секунд", "gap": 0.1}],
         "sfx": [{"n": "sent", "at": 3.75}],
         "titr": [{"text": "Подключение — [[5 секунд]]", "at": 1.2, "dur": 3.2}]},
        {"kind": "shot", "min": 4.4, "lead": 0.55,
         "panels": [
             {"video": {"name": "01-mobile-scan", "from": 0, "to": 4.4}, "frame": "phone", "area": [120, 30, 420, 800]},
             {"video": {"name": "01-kassa-line-added", "from": 0, "to": 4.0}, "delay": 0.5, "crop": [1600, 100, 992, 760], "area": [680, 50, 1100, 780],
              "clicks": [{"at": 2.0, "rect": [1685, 305, 875, 130], "fill": True, "hold": 0.6}]}],
         "lines": [{"who": "N", "text": "— и дальше «пик» делает он. По Вай-Фай, прямо в чек.", "sub": "— и дальше «пик» делает он. По Wi-Fi, прямо в чек."}],
         "sfx": [{"n": "pik", "at": 1.55}],
         "titr": [{"text": "Телефон [[=]] сканер", "at": 0.5, "dur": 3.6}]},
        {"kind": "hook", "min": 3.4, "lead": 0.2,
         "store": store(),
         "actors": [back("ainura", "char-ainura-scan", 1120),
                    ch("erlan", "char-erlan-stand", 430, swaps=[{"at": 1.0, "img": "char-erlan-happy"}], hop=[1.2]),
                    ch("jyldyz", "char-jyldyz-stand", 760),
                    ch("nurlan", "char-nurlan-angry", 1680, swaps=[{"at": 2.0, "img": "char-nurlan-happy"}])],
         "lines": [{"who": "ER", "text": "А так можно было?", "at": 0.9, "b": "erlan"},
                   {"who": "NU", "text": "Кошелёк цел!", "at": 2.1, "b": "nurlan", "bw": 400}],
         "sfx": [{"n": "pik", "at": 0.1}, {"n": "pik", "at": 0.55}, {"n": "pik", "at": 0.95}]},
        finale(),
    ],
}
