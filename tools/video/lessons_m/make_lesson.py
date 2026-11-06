#!/usr/bin/env python3
"""2026-10-30: сборка видеоурока мобилки NurCRM Mobile из сценария lessons/<ID>.json (формат: docs/Дизайн/Видеоуроки — формат и список.md).

Запуск (из корня репозитория):
  python tools/video/lessons_m/make_lesson.py M-01 [--draft] [--snap ПАПКА] [--no-music] [--format landscape|portrait]
Результат: C:\\Users\\User\\NurMarket-video\\lessons\\ru\\<ID>-<slug>.mp4 (+ .srt). Кадры телефона (PNG 1080x2340 и JSON с прямоугольниками элементов)
снимает test/lesson_frames_test.dart (NurMarketMobile) в C:\\Users\\User\\NurMarket-video\\lessons-frames\\.

Сценарий JSON:
{ "id": "M-01", "slug": "vhod-i-prodazha", "num": "Мобилка · 01", "title": "Вход и первая продажа", "goal": "За 2 минуты: ...",
  "result": "...", "next": "...", "version": "для NurCRM Mobile 1.18.12",
  "steps": [ { "title": "Войдите", "text": "реплика диктора (ru)", "duration": 5,
               "hint": {"text": "...", "at": "30%", "dur": 3},                 # необязательно: «Если не получилось», 3 с, не больше 1 на урок
               "example": "Пример",                                           # необязательно: подпись для нестандартного кадра (камера, звонок)
               "shots": [ {"img": "m/m01-login-empty", "w": 1, "tap": "login", "hl": "имя", "zoom": "имя", "zoomLevel": 1.6,
                           "targets": {"имя": [x, y, w, h]}} ] } ] }
Кадры: img — путь без .png относительно папки кадров; tap — касание цели (подсветка рамкой + жёлтый круг 48 px), zoom — наезд камеры на цель,
w — вес длительности кадра внутри шага. Движок сцен — lesson_step.html / lesson_card.html (общий конвейер tools/video/build_video.py).
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
import build_video as bv  # noqa: E402
import numpy as np  # noqa: E402

FR = os.environ.get("NM_LESSON_FRAMES", r"C:\Users\User\NurMarket-video\lessons-frames")
OUT = os.environ.get("NM_LESSON_OUT", r"C:\Users\User\NurMarket-video\lessons")
RATE = "-6%"       # спокойный обучающий темп
VOICE = "ru-RU-DmitryNeural"


def load_shot(sh):
    img = sh["img"]
    src = os.path.join(FR, img.replace("/", os.sep) + ".png")
    if not os.path.isfile(src):
        raise SystemExit("нет кадра %s" % src)
    targets = {}
    jp = os.path.splitext(src)[0] + ".json"
    if os.path.isfile(jp):
        targets.update(json.load(open(jp, encoding="utf-8")).get("targets", {}))
    targets.update(sh.get("targets", {}))
    o = {k: v for k, v in sh.items() if k not in ("img", "targets")}
    o["src"], o["targets"] = src, targets
    for key in ("tap", "zoom"):
        if o.get(key) and o[key] not in targets:
            raise SystemExit("в кадре %s нет цели %r (есть: %s)" % (img, o[key], ", ".join(targets) or "-"))
    return o


def build_scenario(L, lang="ru"):
    steps = L["steps"]
    n = len(steps)
    card = os.path.join(HERE, "lesson_card.html")
    page = os.path.join(HERE, "lesson_step.html")
    common = {"lessonTitle": L["title"], "lessonNum": L["num"], "steps": [s["title"] for s in steps]}
    scenes = [{"id": "intro", "html": card, "kind": "intro", "num": L["num"], "title": L["title"], "sub": "Видеоурок для владельца и кассира", "duration": 3, "transition": "none"}]
    first = load_shot(steps[0]["shots"][0])
    scenes.append(dict({"id": "goal", "html": page, "goal": L["goal"], "goalKicker": L["num"], "text": L["goal"], "subs": False, "duration": 3.5, "tail": 0.5,
                        "shots": [first]}, **common))
    hint_used = False
    for i, st in enumerate(steps):
        sc = dict({"id": "step%d" % (i + 1), "html": page, "stepNo": i + 1, "stepTotal": n, "stepTitle": st["title"], "text": st["text"],
                   "duration": st.get("duration", 4.0), "shots": [load_shot(s) for s in st["shots"]]}, **common)
        if st.get("example"):
            sc["example"] = st["example"]
        if st.get("hint"):
            if hint_used:
                raise SystemExit("подсказка «Если не получилось» — не больше одной на урок")
            hint_used = True
            sc["hint"] = st["hint"]
        scenes.append(sc)
    scenes.append({"id": "outro", "html": card, "kind": "outro", "result": L["result"], "next": L.get("next", ""), "version": L.get("version", ""), "duration": 4.0, "text": ""})
    return {"name": "%s-%s" % (L["id"], L.get("slug", "lesson")), "lang": lang, "voice": VOICE, "rate": RATE, "music": True, "music_volume": 0.09, "scenes": scenes}


def ping(total_samples):
    """Короткий фирменный «пинг» заставки."""
    t = np.arange(int(0.6 * bv.SR)) / bv.SR
    env = np.exp(-t * 7.0) * np.minimum(1, t * 200)
    return (0.22 * env * (np.sin(2 * np.pi * 880 * t) + 0.5 * np.sin(2 * np.pi * 1320 * t))).astype(np.float32)


def srt(scenes, path):
    def f(x):
        h, m, s = int(x // 3600), int(x % 3600 // 60), x % 60
        return "%02d:%02d:%06.3f" % (h, m, s)
    lines, k = [], 1
    for s in scenes:
        for a, b, txt in s["subs"]["landscape"]:
            lines += [str(k), "%s --> %s" % (f(s["start"] + a).replace(".", ","), f(s["start"] + b).replace(".", ",")), txt, ""]
            k += 1
    open(path, "w", encoding="utf-8").write("\n".join(lines))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("id")
    ap.add_argument("--draft", action="store_true")
    ap.add_argument("--no-music", action="store_true")
    ap.add_argument("--snap", metavar="DIR")
    ap.add_argument("--format", choices=["landscape", "portrait"], default="landscape")
    a = ap.parse_args()
    lp = os.path.join(HERE, "lessons", a.id + ".json")
    L = json.load(open(lp, encoding="utf-8"))
    scenario = build_scenario(L)
    scenario["logo"] = os.path.join(bv.HOME, "assets", "logo.png")
    for sc in scenario["scenes"]:
        sc["_logo"] = scenario["logo"]
    fps = 30
    print("Урок %s: %s, сцен %d" % (L["id"], L["title"], len(scenario["scenes"])), flush=True)
    scenes, total = bv.plan(scenario, fps, {"landscape": 42, "portrait": 28})
    print("Длительность: %.1f с" % total, flush=True)
    if a.snap:
        bv.snap_frames(scenes, a.format, a.snap, a.draft)
        return
    os.makedirs(os.path.join(OUT, "ru"), exist_ok=True)
    out = os.path.join(OUT, "ru", scenario["name"] + ".mp4")
    mix = bv.mix_audio(scenes, total, False if a.no_music else True, scenario["music_volume"])
    p = ping(0)
    mix[int(0.3 * bv.SR):int(0.3 * bv.SR) + len(p)] += p
    peak = float(np.abs(mix).max())
    if peak > 0.69:                       # пик не выше −3 дБ
        mix *= 0.69 / peak
    wav = os.path.join(bv.CACHE, "mixlesson-%s.wav" % a.id)
    bv.write_wav(wav, mix)
    srt(scenes, os.path.splitext(out)[0] + ".srt")
    if a.format == "portrait":
        out = os.path.splitext(out)[0] + "-vertical.mp4"
    bv.render_video(scenes, total, a.format, out, wav, fps, a.draft, "jpeg")
    print("Готово: %s\n%s" % (out, bv.probe(out)), flush=True)


if __name__ == "__main__":
    main()
