#!/usr/bin/env python3
"""2026-10-30: сборка роликов-фишек NurCRM (русская версия, 16:9, 22-30 с) из спецификаций feat_specs.py.
Для каждого ролика: озвучка (edge-tts: рассказчик и герои), SFX и музыка (ad_svet/make_audio.py) -> готовый микс premix,
сценарий JSON tools/video/examples/feat-NN-*.json -> build_video.py (шаблон templates/feat.html).
Запуск:  python feat_gen.py 1 [2 3 ...|all] [--snap DIR] [--json-only] [--no-render]
Перед первым запуском: python feat_assets.py (позы героев, реквизит, кадры видео, QR)."""
import argparse
import json
import math
import os
import subprocess
import sys

HOME = os.environ.get("NM_VIDEO_HOME", r"C:\Users\User\NurMarket-video")
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HOME, "pylib"))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "ad_svet"))
import numpy as np  # noqa: E402
import make_audio as MA  # noqa: E402
import feat_specs  # noqa: E402
import feat_specs2  # noqa: E402,F401

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

SR = MA.SR
FPS = 30
ASSETS = os.path.join(HOME, "feat-assets")
FRAMES = os.path.join(HOME, "feat-frames")
SHOTS = os.path.join(HOME, "shots-features")
OUTDIR = os.path.join(HOME, "ad", "features")
WORK = os.path.join(HOME, "ad", "work-feat")
EXAMPLES = os.path.join(HERE, "examples")
MA.WHO["JY"] = dict(v="ru-RU-SvetlanaNeural", rate="+8%", pitch="+12Hz")
MA.WHO["N"] = dict(v="ru-RU-DmitryNeural", rate="+6%", pitch="+0Hz")
MA.OUT = WORK

# SFX сверх набора make_audio
MA.SFX["click"] = lambda e: MA.sx_click(0.8)
MA.SFX["pik"] = lambda e: MA.sx_pik()
MA.SFX["tik"] = lambda e: MA.sx_tick()


def log(*a):
    print(*a, flush=True)


def png_size(path):
    import struct
    d = open(path, "rb").read(32)
    return struct.unpack(">II", d[16:24])


def split_chunks(text, maxc=58):
    sys.path.insert(0, HERE)
    import build_video as BV
    return BV.split_subs(text, maxc)


# ---------------------------------------------------------------- планирование сцен
def plan_scene(sc):
    """-> (dur, lines) : у каждой реплики добавляются at/dur/audio."""
    lines, cur = [], float(sc.get("lead", 0.3))
    for ln in sc.get("lines", []):
        ln = dict(ln)
        a = MA.tts_line(ln["who"], ln["text"], ln.get("rate"))
        ln["audio"] = a
        ln["dur"] = len(a) / SR
        ln["at"] = float(ln["at"]) if "at" in ln else cur
        cur = ln["at"] + ln["dur"] + float(ln.get("gap", 0.22))
        lines.append(ln)
        log("    реплика %s at %.2f dur %.2f: %s" % (ln["who"], ln["at"], ln["dur"], ln["text"][:50]))
    end = max([l["at"] + l["dur"] for l in lines] + [0.0])
    dur = max(float(sc.get("min", 3.0)), end + float(sc.get("tail", 0.45)))
    return math.ceil(dur * FPS) / FPS, lines


def build_music(total, beat_in, finale_at):
    """Комуз-лид 100 BPM (как в «Свет погас»): в крючке — приглушённо и без бита, бит входит на первом экране."""
    mus = np.zeros(int(total * SR))
    drums, lead, pad, bass = (np.zeros(len(mus)) for _ in range(4))
    motifs_w = [[4, 5, 7, 5, 4, None, 2, 4], [5, 4, 2, 4, 5, None, 7, 5], [7, 5, 4, 2, 4, 5, 4, None], [5, None, 7, 9, 7, 5, 4, 2]]
    end = total - 0.9
    for b, bt in MA.bars_in(0.0, end):
        MA.lead_bar(lead, bt, MA.PENT_MAJ, 65, motifs_w[b % 4], 0.34 if bt < beat_in else 0.5, 0.993, 0.5 if bt < beat_in else 0.6)
    for b, bt in MA.bars_in(-MA.BAR, end):
        root, ch = MA.WARM_CH[b % 4]
        s = max(bt, 0.0)
        e = min(bt + MA.BAR, end)
        if e - s < 0.3:
            continue
        for n in ch:
            MA.put(pad, MA.pad_note(MA.midi(n), e - s + 0.4, 0.8), s)
    MA.groove(drums, beat_in, finale_at, 0.9)
    for b, bt in MA.bars_in(beat_in - MA.BAR, finale_at):
        root, ch = MA.WARM_CH[b % 4]
        for i in (0, 1.5, 2, 3.5):
            tt = bt + i * MA.BEAT
            if beat_in <= tt < finale_at:
                MA.put(bass, MA.bass_note(MA.midi(root), 0.5), tt, 0.8)
    for n in (53, 57, 60, 64):  # финал: аккорд на логотипе
        MA.put(pad, MA.pad_note(MA.midi(n), 3.2, 0.9), finale_at)
    drums = MA.lp(drums, 9000)
    lead = MA.lp(lead, 5500)
    mus = lead * 0.55 + pad * 0.55 + bass * 0.7 + drums * 0.7
    ta = np.arange(len(mus)) / SR
    mus *= np.clip((total - 0.15 - ta) / 1.0, 0, 1)
    return mus


# ---------------------------------------------------------------- сборка одного ролика
def expand_panel(p):
    p = json.loads(json.dumps(p))
    if p.get("image"):
        p["image"] = os.path.join(SHOTS, p["image"])
        p.setdefault("size", list(png_size(p["image"])))
    if p.get("video"):
        v = p["video"]
        d = os.path.join(FRAMES, v["name"])
        meta = json.load(open(os.path.join(d, "meta.json")))
        v["dir"], v["meta"] = d, meta
        p.setdefault("size", [meta["w"], meta["h"]])
    return p


def build(num, snap=None, json_only=False, render=True):
    spec = feat_specs.SPECS[num]
    os.makedirs(WORK, exist_ok=True)
    os.makedirs(OUTDIR, exist_ok=True)
    scenes, t0 = [], 0.0
    voice_events, sfx, ev_total = [], [], 0.0
    log("== ролик %02d «%s»" % (num, spec["name"]))
    beat_in, finale_at = None, None
    for i, sc in enumerate(spec["scenes"]):
        dur, lines = plan_scene(sc)
        if sc["kind"] == "finale":
            dur = max(dur, float(sc.get("min", 4.0)))
            dur = math.ceil(dur * FPS) / FPS
            finale_at = t0
        if beat_in is None and sc["kind"] != "hook":
            beat_in = t0
        subs, bubbles = [], []
        for ln in lines:
            voice_events.append((t0 + ln["at"], ln["audio"]))
            if ln["who"] == "N":
                if sc["kind"] != "finale":
                    chunks = split_chunks(ln.get("sub") or ln["text"], 58)
                    w = [len(c) + 6 for c in chunks]
                    tot, tt = float(sum(w)), ln["at"]
                    for c, wi in zip(chunks, w):
                        d = ln["dur"] * wi / tot
                        subs.append({"text": c, "at": round(tt, 3), "dur": round(d, 3)})
                        tt += d
            elif ln.get("b"):
                bubbles.append({"who": ln["b"], "text": ln.get("bt") or ln["text"], "at": round(ln["at"], 3), "dur": round(ln["dur"] + 0.45, 3),
                                "dy": ln.get("dy", 0), "w": ln.get("bw", 600)})
        params = {k: v for k, v in sc.items() if k not in ("lines", "min", "lead", "tail", "sfx", "kind")}
        params["kind"] = sc["kind"]
        params["assets"] = ASSETS
        if bubbles or sc.get("bubbles"):
            params["bubbles"] = bubbles + sc.get("bubbles", [])
        if sc.get("panels"):
            params["panels"] = [expand_panel(p) for p in sc["panels"]]
            for p in params["panels"]:
                for k in p.get("clicks", []):
                    sfx.append({"n": "tik", "t": t0 + k["at"] + 0.04, "v": 0.7})
        for e in sc.get("sfx", []):
            e = dict(e)
            e["t"] = t0 + e.pop("at")
            sfx.append(e)
        if i > 0 and sc.get("wipe_sfx", True) and sc["kind"] != "finale":
            sfx.append({"n": "whoosh", "t": t0 + 0.02, "dur": 0.3, "v": 0.25})
        if sc["kind"] == "finale":
            sfx.append({"n": "hit", "t": t0 + 0.2, "v": 0.45})
        if sc["kind"] == "hook" and i == 0:
            sfx.append({"n": "whoosh", "t": 1.2, "dur": 0.35, "v": 0.3})
        scene = {"id": "s%d" % (i + 1), "template": "feat", "duration": round(dur, 4), "subs": subs, "transition": "none"}
        scene.update(params)
        if sc["kind"] != "hook" or i > 0:
            pass
        scenes.append(scene)
        log("  сцена %d %-7s %5.2f–%5.2f с (%.2f) строк %d" % (i + 1, sc["kind"], t0, t0 + dur, dur, len(lines)))
        t0 += dur
    total = t0
    log("  длительность %.2f с" % total)
    # --- звук
    voice = np.zeros(int(total * SR))
    for at, a in voice_events:
        MA.put(voice, a, at)
    sfx_buf = MA.build_sfx([dict(e, t=e["t"]) for e in sfx], total)
    music = build_music(total, beat_in if beat_in is not None else 1.5, finale_at if finale_at is not None else total - 4.0)
    m = MA.mix(voice, sfx_buf, music, True, music_gain=0.15, sfx_gain=0.6)
    wav = os.path.join(WORK, "feat-%02d.wav" % num)
    MA.write_wav(wav, m)
    # 2026-10-30: после AAC истинный пик выходил на -0,8 dBFS при пике -3,0 в WAV -> лимитер на -5,7 dBFS (итог ролика <= -3 dBFS)
    tmp = wav + ".lim.wav"
    subprocess.run([MA.FFMPEG, "-y", "-v", "error", "-i", wav, "-af", "alimiter=limit=0.52:level=disabled:attack=3:release=40:asc=0", "-ar", "48000", "-ac", "2", tmp], check=True)
    os.replace(tmp, wav)
    log("  звук: пик WAV %.2f dBFS (до лимитера)" % MA.db(np.abs(m).max()))
    scenario = {"name": "feat-%02d-%s" % (num, spec["slug"]), "lang": "ru", "voice": "none", "music": False, "fps": FPS,
                "logo": os.path.join(HOME, "assets", "logo.png"), "premix": wav, "scenes": scenes}
    jpath = os.path.join(EXAMPLES, "feat-%02d-%s.json" % (num, spec["slug"]))
    json.dump(scenario, open(jpath, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    log("  сценарий:", jpath)
    if json_only:
        return jpath
    out = os.path.join(OUTDIR, "%02d-%s-ru.mp4" % (num, spec["slug"]))
    cmd = [sys.executable, os.path.join(HERE, "build_video.py"), jpath]
    if snap:
        cmd += ["--snap", os.path.join(snap, "%02d" % num)]
    elif render:
        cmd += ["-o", out]
    else:
        return jpath
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    subprocess.run(cmd, check=True, env=env)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("nums", nargs="+")
    ap.add_argument("--snap")
    ap.add_argument("--json-only", action="store_true")
    ap.add_argument("--no-render", action="store_true")
    a = ap.parse_args()
    nums = sorted(feat_specs.SPECS) if a.nums == ["all"] else [int(x) for x in a.nums]
    for n in nums:
        build(n, a.snap, a.json_only, not a.no_render)


if __name__ == "__main__":
    main()
