#!/usr/bin/env python3
"""2026-10-30: заранее озвучить все реплики уроков мобилки в кэш (edge-tts капризничает: таймаут на попытку, повторы).
Запуск: python tools/video/lessons_m/prefetch_tts.py [M-01 M-02 ...]"""
import asyncio
import glob
import hashlib
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
sys.path.insert(0, HERE)
import build_video as bv  # noqa: E402
import make_lesson as ml  # noqa: E402


def key(text):
    return hashlib.sha1(("%s|%s|%s" % (ml.VOICE, ml.RATE, text)).encode("utf-8")).hexdigest()[:20]


async def _tts(text, mp3):
    import edge_tts
    await edge_tts.Communicate(text, ml.VOICE, rate=ml.RATE).save(mp3)


def one(text, attempts=2):
    os.makedirs(bv.CACHE, exist_ok=True)
    k = key(text)
    mp3 = os.path.join(bv.CACHE, "tts-%s.mp3" % k)
    for attempt in range(attempts):
        try:
            asyncio.run(asyncio.wait_for(_tts(text, mp3), 45))
            a = bv.decode_audio(mp3)
            a.astype("<f4").tofile(os.path.join(bv.CACHE, "tts-%s.f32" % k))
            return True
        except Exception as e:
            time.sleep(3)
    return False


ids = sys.argv[1:] or [os.path.splitext(os.path.basename(p))[0] for p in sorted(glob.glob(os.path.join(HERE, "lessons", "M-*.json")))]
texts = []
for i in ids:
    L = json.load(open(os.path.join(HERE, "lessons", i + ".json"), encoding="utf-8"))
    texts += [(i, t) for t in [L["goal"]] + [s_["text"] for s_ in L["steps"]]]
for passno in range(30):
    todo = [(i, t) for i, t in texts if not os.path.isfile(os.path.join(bv.CACHE, "tts-%s.f32" % key(t)))]
    print("проход", passno + 1, "осталось", len(todo), flush=True)
    if not todo:
        break
    for i, t in todo:
        t0 = time.time()
        ok = one(t)
        print(i, "OK" if ok else "fail", "%.0fс" % (time.time() - t0), t[:40], flush=True)
        time.sleep(2)
    time.sleep(20)
print("PREFETCH-DONE", flush=True)
