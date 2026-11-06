#!/usr/bin/env python3
"""2026-10-30: индекс видеоуроков мобилки -> docs/Дизайн/Видеоуроки — индекс записей (мобилка).md (по lessons/*.json и готовым mp4)."""
import glob
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
OUT_DIR = r"C:\Users\User\NurMarket-video\lessons\ru"
FF = open(r"C:\Users\User\NurMarket-video\ffmpeg-path.txt", encoding="utf-8").read().strip()
DOC = os.path.join(ROOT, "docs", "Дизайн", "Видеоуроки — индекс записей (мобилка).md")


def info(mp4):
    r = subprocess.run([FF, "-hide_banner", "-i", mp4], stderr=subprocess.PIPE, stdout=subprocess.PIPE)
    m = re.search(r"Duration: (\d+):(\d+):([\d.]+)", r.stderr.decode("utf-8", "replace"))
    dur = int(m.group(1)) * 3600 + int(m.group(2)) * 60 + float(m.group(3)) if m else 0
    v = subprocess.run([FF, "-hide_banner", "-i", mp4, "-af", "volumedetect", "-vn", "-f", "null", "-"], stderr=subprocess.PIPE, stdout=subprocess.PIPE)
    pk = re.search(r"max_volume: (-?[\d.]+) dB", v.stderr.decode("utf-8", "replace"))
    d = subprocess.run([FF, "-v", "error", "-i", mp4, "-f", "null", "-"], stderr=subprocess.PIPE)
    return dur, (pk.group(1) if pk else "?"), ("OK" if not d.stderr.strip() else "ОШИБКИ")


rows = []
for jp in sorted(glob.glob(os.path.join(HERE, "lessons", "M-*.json"))):
    L = json.load(open(jp, encoding="utf-8"))
    name = "%s-%s.mp4" % (L["id"], L.get("slug", "lesson"))
    mp4 = os.path.join(OUT_DIR, name)
    if os.path.isfile(mp4):
        dur, peak, dec = info(mp4)
        st = "%d:%02d · пик %s дБ · декод %s" % (dur // 60, dur % 60, peak, dec)
    else:
        st = "не собран"
    rows.append((L, name, st))

lines = [
    "# Видеоуроки мобилки NurCRM Mobile — индекс записей (30.10.2026)",
    "",
    "Первая волна уроков по мобилке (версия в рабочей копии 1.18.12). Формат — `Видеоуроки — формат и список.md` (раздел 1): 16:9 1920×1080, рамка телефона по центру, заставка 3 с, титры шагов, подсветка касаний и зум, субтитры Montserrat (рядом .srt), закадр ru (Dmitry, edge-tts, темп −6 %), тихая музыка, финал 4 с. Вертикальная 9:16 версия не снималась (сначала 16:9).",
    "",
    "Папка готовых файлов: `C:\\Users\\User\\NurMarket-video\\lessons\\ru\\` (вне репозитория). Кадры телефона: `C:\\Users\\User\\NurMarket-video\\lessons-frames\\`.",
    "",
    "## Как собирать",
    "",
    "1. Кадры: `cd NurMarketMobile && flutter test test/lesson_frames_test.dart --dart-define=SNAP_DIR=C:/Users/User/NurMarket-video/lessons-frames/m --concurrency=2` (виджет-тесты на фейках: база в памяти, фейковый сервер, без сети/камеры/микрофона/боевой базы; рядом с PNG кладётся JSON с прямоугольниками элементов для подсветки касаний). Кадры камеры, звонка и сопряжения — из `test/ad_features_snapshots_test.dart` / `ad_details_snapshots_test.dart` (папки `_fr_*`). Эмулятор не нужен.",
    "2. Урок: `python tools/video/lessons_m/make_lesson.py M-01 [--draft] [--snap ПАПКА]` — сценарий `tools/video/lessons_m/lessons/M-01.json` (шаги → кадры → подсветка/зум → титр → закадр), движок `lesson_step.html` + `lesson_card.html` поверх общего конвейера `tools/video/build_video.py`.",
    "3. Индекс: `python tools/video/lessons_m/make_index.py`.",
    "",
    "Не снимаются честно и подписаны на экране «Пример: камера показана условно» / «Пример: звонок показан условно»: камера (сканер, умная камера, QR) и видеозвонок с ИИ — настоящие экраны приложения на тёмной заливке вместо живой камеры.",
    "",
    "## Уроки",
    "",
    "| № | Файл | Название | Шагов | Итог |",
    "|---|---|---|---|---|",
]
for L, name, st in rows:
    lines.append("| %s | `%s` | %s | %d | %s |" % (L["id"], name, L["title"], len(L["steps"]), st))
lines += ["", "## Шаги по урокам", ""]
for L, name, st in rows:
    lines.append("### %s · %s" % (L["id"], L["title"]))
    lines.append("Цель: %s." % L["goal"])
    for i, s in enumerate(L["steps"], 1):
        lines.append("%d. %s" % (i, s["title"]))
    lines.append("")
lines += [
    "## Не вошло в первую волну",
    "",
    "- M-01: установка APK/Play и «Оплата QR» (QR есть только на экране чека); вертикальная версия 9:16.",
    "- M-02: весовая этикетка с PLU в продаже — телефон на тесте не нашёл товар по коду этикетки, поэтому показан режим «Весы» (камера читает табло).",
    "- M-03: загрузка накладной xlsx/XML на телефоне (кадры есть в `ad_details_snapshots_test.dart`, в урок не включены).",
    "- M-04: обучение камеры тремя снимками и фото из галереи; M-08: сотрудники, права, журнал аудита, «Напомнить всем просроченным»; M-09: «остаток на плитке уменьшился сразу», «Связь → отправлено».",
    "- Озвучка uz и субтитры ky — отдельной волной (переводчик по готовым ru-сценариям).",
    "",
    "Перед публикацией: просмотреть уроки глазами (нет чужих данных, NurMarket, мусора), версия в финале — «для NurCRM Mobile 1.18.12».",
    "",
]
os.makedirs(os.path.dirname(DOC), exist_ok=True)
open(DOC, "w", encoding="utf-8").write("\n".join(lines))
print("записан", DOC)
