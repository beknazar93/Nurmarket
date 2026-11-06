#!/usr/bin/env python3
"""2026-10-30: генератор видеоурока NurCRM: сценарий JSON (шаги -> кадры -> метки/зум -> титр -> закадр) -> SPEC для lesson.js,
HTML-страница, звук (закадр ru Dmitry -3 %, фоновая музыка, щелчки), субтитры .srt. Вызывается из make_lesson.py.

Формат сценария (scenarios/K-01-*.json):
 { "id":"K-01", "slug":"pervaya-prodazha", "program":"Касса", "title":"Первая продажа", "version":"1.17.64",
   "goal":"За 2 минуты: как ...", "goal_frame":"имя-кадра", "outcome":"Продажа проведена.", "next":"Поиск товара",
   "outro_say":"текст закадра финала (необязательно)",
   "steps":[ { "title":"Отсканируйте товар", "say":"текст закадра", "tts":"необяз. — как читать вслух", "min":3.0,
       "frames":[ { "f":"имя-кадра", "at":0 | "40%",
                    "marks":[ {"r":"ключ|[x,y,w,h в долях]", "at":0.8, "dur":2.4, "click":true} ],
                    "zoom":{"r":"ключ", "at":1.0, "dur":2.0, "scale":1.6} } ],
       "kbd":[{"text":"Enter","at":1.2,"dur":1.4}], "hint":{"text":"...", "at":"55%", "dur":3.0},
       "schema":{ "title":"..", "boxes":[{x,y,w,h,text,c}], "arrows":[{x1,y1,x2,y2}], "notes":[...] }, "schema_at":0, "schema_dur":4 } ] }
 Кадры лежат в SHOTS/<ID>/<имя>.png, их метаданные — SHOTS/<ID>/frames.json: { имя: {"w":px,"h":px,"scale":2,"rects":{ключ:[x,y,w,h] (px окна, 1x)}} }.
"""
import glob
import hashlib
import json
import os
import re
import sys

HOME = os.environ.get("NM_VIDEO_HOME", r"C:\Users\User\NurMarket-video")
HERE = os.path.dirname(os.path.abspath(__file__))
VID = os.path.normpath(os.path.join(HERE, ".."))
REPO = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
SHOTS = os.path.join(HOME, "lessons", "shots")
WORK = os.path.join(HOME, "lessons", "_work")
sys.path.insert(0, os.path.join(HOME, "pylib"))
sys.path.insert(0, os.path.join(VID, "ad_svet"))
sys.path.insert(0, VID)
import numpy as np  # noqa: E402
import make_audio as MA  # noqa: E402

SR = MA.SR
MA.WHO["LS"] = dict(v="ru-RU-DmitryNeural", rate="-3%", pitch="+0Hz")   # закадр уроков: спокойный обучающий тон, −3 % скорости
INTRO, GOAL_MIN, FINAL_MIN = 3.0, 3.6, 3.6
LEAD, TAIL = 0.45, 0.55
MAXFRAME = 2400   # длинная сторона кадра в видео (память Chromium)

# как читать вслух (синтезатор не знает латиницу и сокращения)
TTS_FIX = [(r"NurCRM", "Нур Си Эр Эм"), (r"X-отчёт", "икс-отчёт"), (r"Z-отчёт", "зэт-отчёт"), (r"\bF(\d+)\b", r"эф \1"), (r"\bABC\b", "эй-би-си"),
           (r"\bQR\b", "кью-ар"), (r"\bPIN\b", "пин"), (r"\bWi-Fi\b", "вай-фай"), (r"\bExcel\b", "эксель"), (r"app\.nurcrm\.kg", "а-пэ-пэ точка нур с эр эм точка ке гэ"),
           (r"\bEnter\b", "энтер"), (r"\bCtrl\b", "контрол"), (r"\bsom\b", "сом")]


def log(*a):
    print(*a, flush=True)


def tts_text(s):
    for a, b in TTS_FIX:
        s = re.sub(a, b, s)
    return s


def load_scenario(id_):
    c = glob.glob(os.path.join(HERE, "scenarios", id_ + "-*.json"))
    if not c:
        raise SystemExit("нет scenarios/%s-*.json" % id_)
    sc = json.load(open(c[0], encoding="utf-8"))
    sc["_path"] = c[0]
    return sc


def pct(v, dur):
    if isinstance(v, str) and v.endswith("%"):
        return float(v[:-1]) / 100.0 * dur
    return float(v)


def prep_image(src, dst):
    """Кадр -> не больше MAXFRAME по длинной стороне (PNG)."""
    from PIL import Image
    if os.path.isfile(dst) and os.path.getmtime(dst) >= os.path.getmtime(src):
        im = Image.open(dst)
        return im.size
    im = Image.open(src).convert("RGB")
    k = min(1.0, MAXFRAME / max(im.size))
    if k < 1.0:
        im = im.resize((round(im.size[0] * k), round(im.size[1] * k)), Image.LANCZOS)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    im.save(dst, optimize=False)
    return im.size


class Shots:
    def __init__(self, id_):
        self.dir = os.path.join(SHOTS, id_)
        self.meta = json.load(open(os.path.join(self.dir, "frames.json"), encoding="utf-8"))
        self.wdir = os.path.join(WORK, id_, "frames")
        self.cache = {}

    def get(self, name):
        if name not in self.cache:
            if name not in self.meta:
                raise SystemExit("нет кадра %s в frames.json (%s)" % (name, self.dir))
            m = self.meta[name]
            src = os.path.join(self.dir, name + ".png")
            if not os.path.isfile(src):
                raise SystemExit("нет файла кадра " + src)
            dst = os.path.join(self.wdir, name + ".png")
            w, h = prep_image(src, dst)
            self.cache[name] = dict(m=m, src=dst.replace("\\", "/"), w=w, h=h)
        return self.cache[name]

    def rect(self, name, r):
        """Ключ или список -> доли кадра [fx,fy,fw,fh]."""
        g = self.get(name)
        if isinstance(r, str):
            if r not in g["m"]["rects"]:
                raise SystemExit("кадр %s: нет прямоугольника '%s' (есть: %s)" % (name, r, ", ".join(g["m"]["rects"])))
            x, y, w, h = g["m"]["rects"][r]
            sc, W0, H0 = g["m"].get("scale", 1), g["m"]["w"], g["m"]["h"]
            return [x * sc / W0, y * sc / H0, w * sc / W0, h * sc / H0]
        return [float(v) for v in r]


def voice_clip(text, cache_tag="LS"):
    return MA.tts_line("LS", tts_text(text))


def build(sc, want_audio=True):
    id_ = sc["id"]
    sh = Shots(id_)
    log("== %s %s" % (id_, sc["title"]))
    # ---- озвучка
    clips = {}

    def clip(key, text):
        if not text:
            return None
        if key not in clips:
            a = voice_clip(text)
            clips[key] = a
        return clips[key]

    goal_text = sc["goal"]
    goal_a = clip("goal", sc.get("goal_tts") or goal_text)
    gdur = max(GOAL_MIN, LEAD + (len(goal_a) / SR if goal_a is not None else 0) + 0.7)
    outro = sc.get("outro_say") or ("Готово. " + sc["outcome"] + " Дальше — " + sc["next"] + ".")
    outro_a = clip("outro", sc.get("outro_tts") or outro)
    fdur = max(FINAL_MIN, 0.9 + len(outro_a) / SR + 0.6)
    steps, t = [], INTRO
    goal_start = t
    t += gdur
    n = len(sc["steps"])
    sfx, subs = [{"n": "bell", "t": 0.55, "v": 0.5}], []
    voice_ev = []
    all_frames, id_seq = [], 0
    cur_kf, clicks_all = [], []
    for i, s in enumerate(sc["steps"]):
        a = clip("s%d" % i, s.get("tts") or s["say"]) if s.get("say") else None
        vd = len(a) / SR if a is not None else 0.0
        dur = max(float(s.get("min", 3.0)), LEAD + vd + TAIL)
        st = dict(n=i + 1, title=s["title"], start=round(t, 3), dur=round(dur, 3), frames=[], marks=[], clicks=[], zooms=[], kbd=[])
        if a is not None:
            voice_ev.append((t + LEAD, a))
            subs.extend(split_subs(s["say"], t + LEAD, vd))
        frs = s["frames"]
        for j, f in enumerate(frs):
            g = sh.get(f["f"])
            at = pct(f.get("at", 0), dur)
            id_seq += 1
            fr = dict(id=id_seq, src="file:///" + g["src"], w=g["w"], h=g["h"], t0=round(t + at, 3), name=f["f"])
            st["frames"].append(fr)
            all_frames.append(fr)
            for m in f.get("marks", []):
                r = sh.rect(f["f"], m["r"])
                mat = pct(m.get("at", 0.4), dur)
                mdur = m.get("dur", 2.4)
                t0 = t + mat
                mk = dict(f=id_seq, r=[round(v, 5) for v in r], t0=round(t0, 3), t1=round(t0 + (min(mdur, 99)), 3), _f=fr)
                st["marks"].append(mk)
                if m.get("click"):
                    cx, cy = r[0] + r[2] / 2, r[1] + r[3] / 2
                    cur_kf.append(dict(t=round(t0, 3), fx=round(cx, 5), fy=round(cy, 5)))
                    st["clicks"].append(dict(t=round(t0, 3), fx=round(cx, 5), fy=round(cy, 5)))
                    sfx.append({"n": "tick", "t": t0, "v": 0.7})
            z = f.get("zoom")
            if z:
                r = sh.rect(f["f"], z["r"])
                zat = pct(z.get("at", 0.8), dur)
                st["zooms"].append(dict(t0=round(t + zat, 3), t1=round(t + zat + float(z.get("dur", 2.0)), 3), cx=round(r[0] + r[2] / 2, 5), cy=round(r[1] + r[3] / 2, 5), scale=float(z.get("scale", 1.6))))
        # конец каждого кадра: начало следующего (+ перекрытие на плавное появление), последнего — конец шага
        for j, fr in enumerate(st["frames"]):
            fr["t1"] = round((st["frames"][j + 1]["t0"] if j + 1 < len(st["frames"]) else t + dur) + 0.14, 3)
        for mk in st["marks"]:
            mk["t1"] = round(min(mk["t1"], mk["_f"]["t1"] - 0.1), 3)
            del mk["_f"]
        for k in s.get("kbd", []):
            kt = t + pct(k.get("at", 0.5), dur)
            st["kbd"].append(dict(text=k["text"], t0=round(kt, 3), t1=round(kt + float(k.get("dur", 1.4)), 3)))
        if s.get("hint"):
            h = s["hint"]
            ht = t + pct(h.get("at", "50%"), dur)
            st["hint"] = dict(text=h["text"], t0=round(ht, 3), t1=round(ht + float(h.get("dur", 3.0)), 3))
        if s.get("schema"):
            st["schema"] = s["schema"]
            st["schT0"] = round(t + pct(s.get("schema_at", 0), dur), 3)
            st["schT1"] = round(st["schT0"] + float(s.get("schema_dur", dur)), 3)
        steps.append(st)
        t += dur
    final_start = t
    if outro_a is not None:
        voice_ev.append((final_start + 0.9, outro_a))
        subs.extend(split_subs(outro, final_start + 0.9, len(outro_a) / SR))
    sfx.append({"n": "pik", "t": final_start + 0.3, "v": 0.5})
    total = final_start + fdur
    voice_ev.append((goal_start + LEAD, goal_a))
    subs.extend(split_subs(goal_text, goal_start + LEAD, len(goal_a) / SR))
    subs.sort(key=lambda x: x["t0"])
    cur_kf.sort(key=lambda k: k["t"])
    gf = sc.get("goal_frame") or sc["steps"][0]["frames"][0]["f"]
    gg = sh.get(gf)
    spec = dict(
        title=sc["title"], label="%s · %s · %s" % (sc["program"], sc["id"].split("-")[1], sc["title"]), subtitle="Видеоуроки NurCRM",
        goalText=sc["goal"], goalFrame=dict(src="file:///" + gg["src"], w=gg["w"], h=gg["h"]), outcome=sc["outcome"], next=sc["next"],
        versionLine="для NurCRM " + sc.get("version", "1.17.64"), finalTitle="Готово",
        intro=dict(start=0, dur=INTRO), goal=dict(start=round(goal_start, 3), dur=round(gdur, 3)), final=dict(start=round(final_start, 3), dur=round(fdur, 3)),
        steps=steps, cursor=cur_kf, subs=subs, total=round(total, 3))
    audio = None
    if want_audio:
        audio = build_mix(voice_ev, sfx, total, id_)
    return spec, audio, total


def split_subs(text, t0, vd, maxc=42):
    """Реплика -> куски по <= 2 строк x 42 знака, время пропорционально длине."""
    import textwrap
    text = re.sub(r"\s+", " ", text).strip()
    sents = re.split(r"(?<=[.!?…])\s+", text)
    chunks = []
    for s in sents:
        lines = textwrap.wrap(s, maxc, break_long_words=False)
        for k in range(0, len(lines), 2):
            chunks.append("\n".join(lines[k:k + 2]))
    w = [len(c.replace("\n", " ")) + 5 for c in chunks]
    tot, t, out = float(sum(w)), t0, []
    for c, wi in zip(chunks, w):
        d = vd * wi / tot
        out.append(dict(t0=round(t, 3), t1=round(t + d, 3), text=c))
        t += d
    return out


def music_bed(total):
    sys.path.insert(0, VID)
    import build_video as BV
    return BV.music_track(True, total, 1.0).astype(np.float64)


def build_mix(voice_ev, sfx, total, id_):
    n = int(total * SR)
    voice = np.zeros(n)
    for t0, a in voice_ev:
        i = int(round(t0 * SR))
        e = min(n, i + len(a))
        voice[i:e] += a[: e - i]
    sx = MA.build_sfx(sfx, total)
    mus = music_bed(total)
    mus = mus[:n] if len(mus) >= n else np.pad(mus, (0, n - len(mus)))
    mix = MA.mix(voice, sx, mus, True, duck_depth=0.88, music_gain=0.055, sfx_gain=0.45)
    mix = mix * 0.9   # запас под AAC: пик около −3,9 dBFS
    # плавный вход/выход
    fi, fo = int(0.3 * SR), int(1.0 * SR)
    mix[:fi] *= np.linspace(0, 1, fi)
    mix[-fo:] *= np.linspace(1, 0, fo)
    return mix


def qr_svg(url):
    import segno
    q = segno.make(url, error="m", micro=False)
    m = [list(r) for r in q.matrix]
    n, b, d = len(m), 2, []
    for y, row in enumerate(m):
        x = 0
        while x < n:
            if row[x]:
                x0 = x
                while x < n and row[x]:
                    x += 1
                d.append("M%d %dh%dv1h-%dz" % (x0 + b, y + b, x - x0, x - x0))
            else:
                x += 1
    sz = n + 2 * b
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %d %d" shape-rendering="crispEdges"><rect width="%d" height="%d" fill="#fff"/>'
            '<path d="%s" fill="#191919"/></svg>' % (sz, sz, sz, sz, "".join(d)))


def build_html(spec, out_html):
    tpl = open(os.path.join(HERE, "lesson.html"), encoding="utf-8").read()
    js = open(os.path.join(HERE, "lesson.js"), encoding="utf-8").read()
    logo = open(os.path.join(REPO, "design", "Ad-Light", "logo.svg"), encoding="utf-8").read()
    logo = re.sub(r"<\?xml[^>]*\?>", "", logo)
    logo = re.sub(r"<title>.*?</title>", "", logo, flags=re.S)
    html = tpl.replace("<!--LOGO-->", logo)
    html = html.replace("/*SPEC*/null", json.dumps(spec, ensure_ascii=False))
    html = html.replace("/*QR*/null", json.dumps(qr_svg("https://app.nurcrm.kg")))
    html = html.replace("/*JS*/", js.replace("</script>", "<\\/script>"))
    os.makedirs(os.path.dirname(out_html), exist_ok=True)
    with open(out_html, "w", encoding="utf-8", newline="\n") as f:
        f.write(html)


def write_srt(subs, path):
    def ts(x):
        h, r = divmod(x, 3600)
        m, s = divmod(r, 60)
        return "%02d:%02d:%02d,%03d" % (h, m, int(s), round((s - int(s)) * 1000))
    with open(path, "w", encoding="utf-8") as f:
        for i, s in enumerate(subs, 1):
            f.write("%d\n%s --> %s\n%s\n\n" % (i, ts(s["t0"]), ts(s["t1"]), s["text"]))
