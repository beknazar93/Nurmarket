#!/usr/bin/env python3
"""2026-10-30: сборка роликов-фишек №9–15 (русская версия, 1920x1080).
Читает tools/video/examples/feat-NN-*.json (формат: обычный сценарий build_video.py + поле "feat" с раскадровкой),
озвучивает реплики (edge-tts: Dmitry/Svetlana, голоса героев — как в «Свет погас», tools/video/ad_svet/make_audio.py),
собирает звук (голос + SFX + музыка 100 BPM с комузом, пик -3 dBFS), HTML-сцену (feat_b.html/feat_b.js + ассеты design/Ad-Light),
кадры видеофрагментов стенда, и запускает build_video.py.
Запуск:  python make_feat_b.py NN [--snap папка] [--draft] [--audio-only]
  NN = 9..15 (ищет examples/feat-NN-*.json; для 9 — feat-09-*.json)
"""
import argparse
import glob
import json
import os
import re
import subprocess
import sys

HOME = os.environ.get("NM_VIDEO_HOME", r"C:\Users\User\NurMarket-video")
HERE = os.path.dirname(os.path.abspath(__file__))
VID = os.path.normpath(os.path.join(HERE, ".."))
REPO = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
ASSETS = os.path.join(REPO, "design", "Ad-Light")
WORK = os.path.join(HOME, "ad", "work", "b")
SHOTS = os.path.join(HOME, "shots-features")
OUTDIR = os.path.join(HOME, "ad", "features")
sys.path.insert(0, os.path.join(HOME, "pylib"))
sys.path.insert(0, os.path.join(VID, "ad_svet"))
sys.path.insert(0, VID)
import numpy as np  # noqa: E402
import make_audio as MA  # noqa: E402  (звуки, музыка, голоса «Свет погас»)

SR = MA.SR
FF = MA.FFMPEG
MA.WHO["JY"] = dict(v="ru-RU-SvetlanaNeural", rate="+4%", pitch="-8Hz")  # Жылдыз
ACTOR_FILES = ["char-ainura", "char-ata", "char-jyldyz", "char-bakyt", "char-erlan", "char-apa", "char-nurlan",
               "bg-wall", "bg-counter", "props", "logo"]


def log(*a):
    print(*a, flush=True)


def inline_svg(name):
    s = open(os.path.join(ASSETS, name + ".svg"), encoding="utf-8").read()
    s = re.sub(r"<\?xml[^>]*\?>", "", s)
    s = re.sub(r"<title>.*?</title>", "", s, flags=re.S)
    return s.replace("<svg ", '<svg id="src-%s" ' % name.replace("char-", ""), 1)


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


def atempo(a, ratio, tag):
    tmp = os.path.join(WORK, "_tmp_%s.f32" % tag)
    a.astype("<f4").tofile(tmp)
    r = subprocess.run([FF, "-v", "error", "-f", "f32le", "-ar", str(SR), "-ac", "1", "-i", tmp, "-filter:a", "atempo=%.4f" % ratio,
                        "-f", "f32le", "-ac", "1", "-ar", str(SR), "-"], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    return np.frombuffer(r.stdout, dtype="<f4").astype(np.float64)


# ------------------------------------------------------------------ музыка серии
def build_music(total, beat_in, final_t, num):
    """Единая тема серии: тёплая пентатоника, комуз-лид (Karplus-Strong), 100 BPM. До beat_in (крючок) — приглушённо и без бита,
    с первого экрана стенда — бит и бас, на финале — последний удар на логотипе и затухание (последняя секунда тише)."""
    P, S = MA, np
    n = int(total * SR)
    lead, pad, bass, drums = (np.zeros(n) for _ in range(4))
    motifs = [[4, 5, 7, 5, 4, None, 2, 4], [5, 4, 2, 4, 5, None, 7, 5], [7, 5, 4, 2, 4, 5, 4, None], [5, None, 7, 9, 7, 5, 4, 2]]
    off = num % 4
    for b, bt in P.bars_in(0.0, final_t):
        root, ch = P.WARM_CH[b % 4]
        e = min(bt + P.BAR, total)
        for nn in ch:
            P.put(pad, P.pad_note(P.midi(nn), e - bt + 0.3, 0.8 if bt < beat_in else 0.9), bt)
        vol = 0.34 if bt < beat_in else 0.58
        P.lead_bar(lead, bt, P.PENT_MAJ, 65, motifs[(b + off) % 4], vol, 0.993, 0.6)
        if bt >= beat_in - 1e-6:
            for i in (0, 1.5, 2, 3.5):
                tt = bt + i * P.BEAT
                if tt < final_t:
                    P.put(bass, P.bass_note(P.midi(root), 0.5), tt, 0.75)
    P.groove(drums, beat_in, final_t, 0.9)
    gate = np.ones(n)
    ta = np.arange(n) / SR
    pre = ta < beat_in  # крючок: фильтр, тише
    mus = P.lp(lead, 5500) * 0.55 + pad * 0.55 + bass * 0.7 + P.lp(drums, 9000) * 0.7
    mus = np.where(pre, P.lp(mus, 1500) * 0.7, mus)
    # финал: удар на логотипе и аккорд
    for k, nt in enumerate((53, 57, 60, 64)):
        P.put(mus, P.pad_note(P.midi(nt), 3.0, 1.0), final_t + 0.28)
    P.put(mus, P.kick() * 1.1, final_t + 0.28)
    P.put(mus, P.bass_note(P.midi(41), 1.2), final_t + 0.28, 0.9)
    for i, d in enumerate([4, 5, 7, 9]):
        P.put(mus, P.ks_pluck(P.midi(P.pent(d, P.PENT_MAJ, 65)), 0.9, 0.6, 0.995), final_t + 0.28 + i * P.BEAT / 2, 0.5 * (1 - i * 0.12))
    gate *= np.clip((total - 0.3 - ta) / 0.7, 0, 1)  # последняя секунда — тишина
    return mus * gate


def build_audio(feat, scenes, total, num, name):
    voice = np.zeros(int(total * SR))
    sfx_ev = []
    # реплики
    for si, sc in enumerate(scenes):
        say = sc.get("say", [])
        for li, ln in enumerate(say):
            a = MA.tts_line(ln["who"], ln.get("tts") or ln["text"], ln.get("rate"))
            nxt = scenes[si]["dur"]
            dur = len(a) / SR
            if "maxdur" in ln and dur > ln["maxdur"]:
                a = atempo(a, min(1.35, dur / ln["maxdur"]), name)
                dur = len(a) / SR
            ln["dur"] = round(dur, 3)
            t0 = sc["start"] + ln["at"]
            i = int(round(t0 * SR))
            e = min(len(voice), i + len(a))
            voice[i:e] += a[: e - i]
            flag = ""
            if ln["at"] + dur > sc["dur"] + 0.05:
                flag = "  <-- ВЫХОДИТ ЗА СЦЕНУ (%.2f > %.2f)" % (ln["at"] + dur, sc["dur"])
            log("  s%d %-3s at %5.2f dur %4.2f -> %5.2f%s | %s" % (si, ln["who"], sc["start"] + ln["at"], dur, sc["start"] + ln["at"] + dur, flag, ln["text"]))
        # перекрытия реплик
        ev = sorted(say, key=lambda l: l["at"])
        for a_, b_ in zip(ev, ev[1:]):
            if a_["at"] + a_["dur"] > b_["at"] + 0.02:
                log("  !! НАЛОЖЕНИЕ реплик в сцене %d: '%s' (до %.2f) и '%s' (с %.2f)" % (si, a_["text"][:20], a_["at"] + a_["dur"], b_["text"][:20], b_["at"]))
        for k in sc.get("sfx", []):
            e = dict(k)
            e["t"] = sc["start"] + k["at"]
            sfx_ev.append(e)
        for v in sc.get("views", []):
            for m in v.get("marks", []):
                if m.get("silent"):
                    continue
                sfx_ev.append({"n": "tick", "t": sc["start"] + m["at"], "v": 0.9})
        if si > 0:
            sfx_ev.append({"n": "whoosh", "t": sc["start"] - 0.14, "dur": 0.3, "v": 0.3})
        for ch in sc.get("chips", []):
            pass
    sfx_ev.append({"n": "whoosh", "t": 1.38, "dur": 0.3, "v": 0.3})
    fin = [s for s in scenes if s["type"] == "final"][0]
    sfx_ev.append({"n": "hit", "t": fin["start"] + 0.28, "v": 0.45})
    sfx = MA.build_sfx(sfx_ev, total)
    first_shots = next((s["start"] for s in scenes if s["type"] != "stage"), 3.5)
    music = build_music(total, first_shots, fin["start"], num)
    mix = MA.mix(voice, sfx, music, True, music_gain=0.15, sfx_gain=0.55)
    return mix


# ------------------------------------------------------------------ кадры клипов и размеры
def prepare_media(feat):
    from PIL import Image
    frames = {}
    for sc in feat["scenes"]:
        for v in sc.get("views", []):
            if v.get("src"):
                im = Image.open(os.path.join(SHOTS, v["src"]))
                v["w"], v["h"] = im.size
            elif v.get("clip"):
                key = "%s|%g|%g" % (v["clip"], v.get("from", 0), v["len"])
                if key not in frames:
                    d = os.path.join(WORK, "frames", re.sub(r"[^0-9A-Za-z]+", "_", key))
                    os.makedirs(d, exist_ok=True)
                    pat = os.path.join(d, "f%03d.jpg")
                    if not os.path.isfile(pat % 1):
                        subprocess.run([FF, "-v", "error", "-y", "-ss", str(v.get("from", 0)), "-t", str(v["len"]), "-i", os.path.join(SHOTS, v["clip"]),
                                        "-vf", "fps=12,scale='min(1920,iw)':-2", "-q:v", "3", pat], check=True)
                    files = sorted(glob.glob(os.path.join(d, "f*.jpg")))
                    im = Image.open(files[0])
                    frames[key] = {"files": files, "fps": 12, "w": im.size[0], "h": im.size[1]}
    return frames


def build_html(feat, frames, out_html):
    tpl = open(os.path.join(HERE, "feat_b.html"), encoding="utf-8").read()
    js = open(os.path.join(HERE, "feat_b.js"), encoding="utf-8").read()
    assets = "\n".join(inline_svg(n) for n in ACTOR_FILES)
    feat = dict(feat)
    feat["shotsDir"] = SHOTS.replace("\\", "/")
    html = tpl.replace("<!--ASSETS-->", assets)
    html = html.replace("/*SPEC*/null", json.dumps(feat, ensure_ascii=False))
    html = html.replace("/*FRAMES*/null", json.dumps(frames))
    html = html.replace("/*QR*/null", json.dumps(qr_svg("https://app.nurcrm.kg")))
    html = html.replace("/*JS*/", js.replace("</script>", "<\\/script>"))
    with open(out_html, "w", encoding="utf-8", newline="\n") as f:
        f.write(html)


def snap(json_path, html_path, total, scenes, outdir):
    import build_video as BV
    from playwright.sync_api import sync_playwright
    os.makedirs(outdir, exist_ok=True)
    s = {"params": {}, "dur": total, "lang": "ru", "portrait": False, "id": "feat", "page": html_path, "transition": "none"}
    with sync_playwright() as pw:
        b = BV.launch_browser(pw)
        try:
            ctx, page = BV.prepare_page(b, s, 1920, 1080, "landscape", None)
            for i, sc in enumerate(scenes):
                ts = [0.5, 1.0, 2.0]
                if sc["type"] == "final":
                    ts = [0.5, 1.5, 3.0]
                if sc["type"] == "diagram":
                    ts = ts + [round(sc["dur"] * 0.75, 2), round(sc["dur"] * 0.93, 2)]
                ts = sorted(set(ts + [round(v["at"] + v["dur"] * 0.6, 2) for v in sc.get("views", []) if len(sc.get("views", [])) > 1]))
                for t in ts:
                    if t >= sc["dur"]:
                        continue
                    page.evaluate("([t,x,a]) => window.__nm_frame(t,x,a)", [sc["start"] + t, "", 0])
                    page.screenshot(path=os.path.join(outdir, "%02d-%s-%s.png" % (i, sc["type"], ("%.1f" % t).replace(".", "_"))), type="png")
            for t in (0.3, 0.9, 1.45):  # заставка
                page.evaluate("([t,x,a]) => window.__nm_frame(t,x,a)", [t, "", 0])
                page.screenshot(path=os.path.join(outdir, "intro-%s.png" % ("%.2f" % t).replace(".", "_")), type="png")
            ctx.close()
        finally:
            b.close()
    log("кадры:", outdir)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("num", type=int)
    ap.add_argument("--snap")
    ap.add_argument("--draft", action="store_true")
    ap.add_argument("--audio-only", action="store_true")
    ap.add_argument("--no-render", action="store_true")
    a = ap.parse_args()
    cands = glob.glob(os.path.join(VID, "examples", "feat-%02d-*.json" % a.num))
    if not cands:
        raise SystemExit("нет examples/feat-%02d-*.json" % a.num)
    jp = cands[0]
    scenario = json.load(open(jp, encoding="utf-8"))
    import copy
    feat = copy.deepcopy(scenario["feat"])  # расчётные поля (start, dur реплик) в файл сценария не пишем
    num = feat["num"]
    name = "feat-%02d" % num
    os.makedirs(WORK, exist_ok=True)
    os.makedirs(OUTDIR, exist_ok=True)
    t0 = 0.0
    for sc in feat["scenes"]:
        sc["start"] = t0
        t0 += sc["dur"]
    total = t0
    log("== %s: %d сцен, %.1f с" % (name, len(feat["scenes"]), total))
    mix = build_audio(feat, feat["scenes"], total, num, name)
    wav = os.path.join(WORK, "mix-%02d.wav" % num)
    MA.write_wav(wav, mix)
    log("звук: %s, пик %.2f dBFS" % (wav, MA.db(float(np.abs(mix).max()))))
    frames = prepare_media(feat)
    html = os.path.join(WORK, "feat-%02d.html" % num)
    build_html(feat, frames, html)
    # build_video-сценарий: одна html-сцена + готовый микс
    scenario["premix"] = wav.replace("\\", "/")
    scenario["scenes"] = [{"id": "feat", "html": html.replace("\\", "/"), "duration": round(total, 3), "subs": False, "lang": "ru", "transition": "none"}]
    scenario["voice"] = "none"
    scenario["music"] = False
    json.dump(scenario, open(jp, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    if a.audio_only or a.no_render:
        return
    if a.snap:
        snap(jp, html, total, feat["scenes"], a.snap)
        return
    out = os.path.join(OUTDIR, "%s-ru.mp4" % feat["slug"])
    cmd = [sys.executable, os.path.join(VID, "build_video.py"), jp, "-o", out]
    if a.draft:
        cmd.append("--draft")
    subprocess.run(cmd, check=True)


if __name__ == "__main__":
    main()
