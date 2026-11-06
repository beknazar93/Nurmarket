#!/usr/bin/env python3
"""2026-10-29: кыргызская озвучка ролика «Свет погас» (MMS-TTS, без смены тембра) поверх готовых SFX и музыки.
Этап 1 (--synth, запускается в venv kyvoice): все реплики из timeline.json (поле ky) -> wav в NurMarket-video/cache/svet-ky-*.wav.
  Разные герои — одной моделью MMS меняем скорость речи (speed) и высоту/темп ресэмплом (resample >1 выше, <1 ниже).
Этап 2 (системный Python, без --synth): расстановка реплик по "at", ускорение atempo при нехватке места,
  сведение с sfx-/music-*.npy из прошлого прогона make_audio.py -> ad/work/ad-svet-{full,short}-ky-mms.wav.
Образец голоса не нужен.
"""
import hashlib
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
HOME = os.environ.get("NM_VIDEO_HOME", r"C:\Users\User\NurMarket-video")
CACHE = os.path.join(HOME, "cache")
# герой -> (скорость MMS, ресэмпл-высота). Ресэмпл меняет и тон, и темп; скорость компенсирует темп.
WHO_KY = {
    "N": (1.0, 1.0),
    "ER": (1.0, 1.10),     # Эрлан — помоложе, чуть выше
    "AI": (1.0, 1.18),     # Айнура — женский (выше)
    "AT": (0.92, 0.84),    # Ата — старик, ниже и медленнее
    "NU": (1.0, 0.93),     # Нурлан — ниже
    "AP": (0.95, 0.96),    # Апа — пожилая женщина
    "BA": (1.05, 1.32),    # Бакыт — ребёнок, высокий
}


# Сжатые реплики: MMS говорит медленнее живого диктора, на длинные слоты не хватает времени (смысл сохранён).
SHORT = {
    "n2": "Айнура жалгыз. Сатып алуучулар — беш.",
    "er1": "Эй… смена бүттүбү?",
    "nu1": "Баары кайда?",
    "n6": "Телефон — касса.",
    "ap1": "Айнура, сен жарыксың!",
    "n7": "Ошол эле кеч. Паникасыз.",
}


def kytext(ln, name="full"):
    return SHORT.get(ln["id"], ln.get("ky")) if name == "full" else ln.get("ky")


def wavpath(who, text):
    k = hashlib.sha1(("%s|%s" % (who, text)).encode("utf-8")).hexdigest()[:16]
    return os.path.join(CACHE, "svet-ky-%s.wav" % k)


def lines(tl):
    seen = {}
    for name in ("full", "short"):
        for ln in tl[name]["voice"]:
            t = kytext(ln, name)
            if t:
                seen[(ln["who"], t)] = 1
    return list(seen)


def stage_synth(tl):
    sys.path.insert(0, os.path.dirname(HERE))
    import ky_voice
    import numpy as np
    for who, text in lines(tl):
        out = wavpath(who, text)
        if os.path.isfile(out):
            continue
        sp = WHO_KY[who][0]
        x, norm = ky_voice.synth(text, None, "mms", sp)
        ky_voice.write_wav16(out, x)
        print("%s %.1f с: %s" % (who, len(x) / ky_voice.OUT_SR, norm), flush=True)


def stage_mix(tl):
    import make_audio as M
    import numpy as np
    for name in ("full", "short"):
        cfg = tl[name]
        total = cfg["dur"]
        voice = np.zeros(int(total * M.SR))
        vl = cfg["voice"]
        for i, ln in enumerate(vl):
            a = M.decode(wavpath(ln["who"], kytext(ln, name)))
            k = WHO_KY[ln["who"]][1]
            if k != 1.0:  # высота/тембр героя: ресэмпл
                idx = np.arange(0, len(a) - 1, k)
                a = np.interp(idx, np.arange(len(a)), a)
            idx = np.where(np.abs(a) > 0.006)[0]
            if len(idx):
                a = a[max(0, idx[0] - int(0.03 * M.SR)): min(len(a), idx[-1] + int(0.06 * M.SR))]
            n = int(0.012 * M.SR)
            a[:n] *= np.linspace(0, 1, n)
            a[-n:] *= np.linspace(1, 0, n)
            r = M.rms(a[np.abs(a) > 0.01])
            if r > 0:
                a = a * (0.105 / r)
            dur = len(a) / M.SR
            nxt = vl[i + 1]["at"] if i + 1 < len(vl) else total
            room = nxt - ln["at"] - 0.06
            if dur > room:
                ratio = min(1.45, dur / room)
                if ratio > 1.02:
                    a = M.atempo(a, ratio)
                    dur = len(a) / M.SR
            M.put(voice, a, ln["at"])
            flag = "" if ln["at"] + dur <= nxt else "  <-- НАЛОЖЕНИЕ (до %.2f)" % nxt
            print("  %-4s %-3s at %5.2f dur %4.2f -> %5.2f%s | %s" % (ln["id"], ln["who"], ln["at"], dur, ln["at"] + dur, flag, kytext(ln, name)))
            ln["ky"] = kytext(ln, name)
            ln["dur"] = round(dur, 3)
        sfx = np.load(os.path.join(M.OUT, "sfx-%s.npy" % name))
        music = np.load(os.path.join(M.OUT, "music-%s.npy" % name))
        m = M.mix(voice, sfx, music, True, music_gain=0.15, sfx_gain=0.55)
        p = os.path.join(M.OUT, "ad-svet-%s-ky-mms.wav" % name)
        M.write_wav(p, m)
        print("  -> %s  пик %.2f dBFS" % (p, M.db(np.abs(m).max())))
    json.dump(tl, open(os.path.join(M.OUT, "timeline.ky-mms.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    # сцена с субтитрами по длительностям этой озвучки: подменяем встроенную шкалу в готовом svet-scene.html
    built = os.path.join(M.OUT, "timeline.built.json")
    old = json.dumps(json.load(open(built, encoding="utf-8")), ensure_ascii=False)
    html = open(os.path.join(M.OUT, "svet-scene.html"), encoding="utf-8").read()
    if old not in html:
        print("ВНИМАНИЕ: шкала в svet-scene.html не найдена, субтитры останутся прежними")
    else:
        open(os.path.join(M.OUT, "svet-scene-ky-mms.html"), "w", encoding="utf-8", newline=chr(10)).write(
            html.replace(old, json.dumps(tl, ensure_ascii=False)))


if __name__ == "__main__":
    for s in (sys.stdout, sys.stderr):
        s.reconfigure(encoding="utf-8", errors="replace")
    tl = json.load(open(os.path.join(HERE, "timeline.json"), encoding="utf-8"))
    if "--synth" in sys.argv:
        stage_synth(tl)
    else:
        sys.path.insert(0, HERE)
        stage_mix(tl)
