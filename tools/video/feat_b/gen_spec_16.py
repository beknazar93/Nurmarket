#!/usr/bin/env python3
"""2026-10-30: раскадровка ролика-фишки №16 «Напоминания по долгам» -> tools/video/examples/feat-16-dolgi.json.
Описание функции и сценарий: docs/Дизайн/Ролик 16 — напоминания по долгам.md. Снимки — shots-features/16-*.png (стенд ad-features,
сцены FeatDebts: выдуманные должники магазина «Нур», 275 000 сом). Затем: python make_feat_b.py 16"""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
EX = os.path.normpath(os.path.join(HERE, "..", "examples"))
COUNTER = {"x": 1470, "y": 705, "s": 0.85}


def final():
    return {"type": "final", "dur": 4.0, "say": [{"who": "N", "at": 0.5, "text": "Нур Си-Эр-Эм — касса, которая знает ваш магазин.", "show": False, "maxdur": 3.1}]}


scenes = [
    # 1. крючок: хозяин, блокнот, должники в очереди
    {"type": "stage", "dur": 5.7, "counter": COUNTER,
     "props": [{"id": "prop-notebook", "x": 1290, "y": 700, "s": 0.9}],
     "actors": [{"id": "ata", "who": "ata", "x": 520, "poses": [[0, "stand"], [3.0, "sad"]]},
                {"id": "erlan", "who": "erlan", "x": 840, "poses": [[0, "stand"], [3.6, "angry"]]},
                {"id": "nurlan", "who": "nurlan", "x": 1480, "z": "back", "poses": [[0, "stand"], [4.6, "angry"]]}],
     "titr": [{"at": 1.5, "dur": 3.4, "text": "[[275 000]] сом долгов — в голове?"}],
     "say": [{"who": "N", "at": 1.45, "maxdur": 3.1, "text": "Двести семьдесят пять тысяч долгов — а вы всё помните в голове?"},
             {"who": "NU", "actor": "nurlan", "at": 4.75, "maxdur": 0.9, "text": "Так, а сколько там?"}]},
    # 2. Сводка: «Требует внимания» -> «Долги клиентов»
    {"type": "shots", "dur": 2.7, "titr": [{"at": 0.2, "dur": 2.2, "text": "Долги — [[на виду]]"}],
     "views": [{"src": "16-owner-attention.png", "at": 0, "dur": 2.7, "crop": [0.70, 0.19, 0.30, 0.30],
                "marks": [{"at": 0.6, "kind": "hold", "dur": 2.0, "rect": [0.729, 0.255, 0.245, 0.067]}]}],
     "say": [{"who": "N", "at": 0.2, "maxdur": 1.9, "text": "Программа считает за вас."}]},
    # 3. «Напоминания должникам»: список и готовый текст
    {"type": "shots", "dur": 5.5, "titr": [{"at": 0.2, "dur": 5.0, "text": "Отметили — [[текст готов]]"}],
     "views": [{"src": "16-owner-reminders-list.png", "at": 0, "dur": 5.5, "crop": [0, 0.22, 1, 0.5],
                "marks": [{"at": 0.5, "kind": "fill", "dur": 0.8, "rect": [0.033, 0.322, 0.55, 0.068]},
                          {"at": 0.8, "kind": "fill", "dur": 0.8, "rect": [0.033, 0.401, 0.55, 0.068]},
                          {"at": 1.1, "kind": "fill", "dur": 0.8, "rect": [0.033, 0.480, 0.55, 0.068]},
                          {"at": 1.4, "kind": "fill", "dur": 0.8, "rect": [0.033, 0.559, 0.55, 0.068]},
                          {"at": 2.6, "kind": "hold", "dur": 1.3, "rect": [0.62, 0.455, 0.35, 0.12]},
                          {"at": 4.3, "kind": "click", "rect": [0.625, 0.38, 0.30, 0.062]}]}],
     "say": [{"who": "N", "at": 0.2, "maxdur": 3.3, "text": "Отметили должников — текст уже готов: имя, сумма, дни."},
             {"who": "N", "at": 3.75, "maxdur": 1.8, "text": "На русском, кыргызском, узбекском."}]},
    # 4. схема: отметили -> WhatsApp с текстом -> «Отправить» нажимает владелец
    {"type": "diagram", "kind": "remind16", "dur": 3.5, "titr": [{"at": 0.3, "dur": 2.9, "text": "«Отправить» — [[вы]]"}],
     "sfx": [{"n": "pik", "at": 2.9, "v": 0.8}],
     "say": [{"who": "N", "at": 0.2, "maxdur": 3.2, "text": "WhatsApp откроется с текстом — «Отправить» нажимаете вы."}]},
    # 5. после первого чата: кнопка «Следующий»
    {"type": "shots", "dur": 2.7, "titr": [{"at": 0.2, "dur": 2.2, "text": "Следующий — [[одной кнопкой]]"}],
     "views": [{"src": "16-owner-reminders-next.png", "at": 0, "dur": 2.7, "crop": [0, 0.19, 1, 0.81],
                "marks": [{"at": 0.4, "kind": "hold", "dur": 1.0, "rect": [0.02, 0.205, 0.56, 0.035]},
                          {"at": 1.4, "kind": "click", "rect": [0.02, 0.91, 0.29, 0.07]}]}],
     "say": [{"who": "N", "at": 0.2, "maxdur": 2.2, "text": "Следующий — одной кнопкой."}]},
    # 6. по расписанию: карточка в Нур Советнике
    {"type": "shots", "dur": 3.3, "titr": [{"at": 0.2, "dur": 2.8, "text": "Раз в неделю — [[само]]"}],
     "views": [{"src": "16-owner-advisor-due.png", "at": 0, "dur": 3.3, "crop": [0.10, 0.51, 0.80, 0.13],
                "marks": [{"at": 0.6, "kind": "hold", "dur": 1.6, "rect": [0.157, 0.54, 0.46, 0.055]},
                          {"at": 2.4, "kind": "click", "rect": [0.698, 0.549, 0.142, 0.04]}]}],
     "say": [{"who": "N", "at": 0.2, "maxdur": 3.2, "text": "А раз в неделю Нур Советник сам скажет — пора."}]},
    # 7. финальная сценка
    {"type": "stage", "dur": 2.9, "counter": COUNTER,
     "actors": [{"id": "erlan", "who": "erlan", "x": 640, "poses": [[0, "angry"], [0.9, "happy"]]},
                {"id": "ata", "who": "ata", "x": 940, "poses": [[0, "stand"], [1.0, "happy"]]},
                {"id": "nurlan", "who": "nurlan", "x": 1480, "z": "back", "poses": [[0, "stand"], [1.4, "happy"]]}],
     "say": [{"who": "ER", "actor": "erlan", "at": 0.25, "maxdur": 1.2, "text": "Сейчас принесу!"},
             {"who": "NU", "actor": "nurlan", "at": 1.65, "maxdur": 1.25, "text": "Блокнот — в мусор."}]},
    final(),
]
d = {"name": "feat-16-dolgi", "lang": "ru", "voice": "none", "music": False, "fps": 30,
     "feat": {"num": 16, "total": 16, "slug": "16-dolgi", "title": "Напоминания по долгам", "scenes": scenes}}
with open(os.path.join(EX, "feat-16-dolgi.json"), "w", encoding="utf-8") as f:
    json.dump(d, f, ensure_ascii=False, indent=1)
print("OK", sum(s["dur"] for s in scenes))
