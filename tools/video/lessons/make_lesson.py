#!/usr/bin/env python3
r"""2026-10-30: сборка видеоурока NurCRM: python make_lesson.py K-01 [--snap папка] [--draft] [--audio-only] [--qa]
Сценарий scenarios/K-01-*.json + кадры lessons/shots/K-01/ -> C:\Users\User\NurMarket-video\lessons\ru\K-01-<slug>-ru.mp4 (+ .srt).
Рендер — через build_video.py (одна html-сцена + готовый микс), как у роликов-фишек (feat_c). ffmpeg: C:\Tools\ffmpeg\ffmpeg.exe."""
import argparse
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
if os.path.isfile(r"C:\Tools\ffmpeg\ffmpeg.exe"):
    os.environ.setdefault("NM_FFMPEG", r"C:\Tools\ffmpeg\ffmpeg.exe")
import gen_lesson as G  # noqa: E402

HOME, VID = G.HOME, G.VID
OUTDIR = os.path.join(HOME, "lessons", "ru")
FF = os.environ.get("NM_FFMPEG", "ffmpeg")


def snap(html, spec, outdir):
    import build_video as BV
    from playwright.sync_api import sync_playwright
    os.makedirs(outdir, exist_ok=True)
    s = {"params": {}, "dur": spec["total"], "lang": "ru", "portrait": False, "id": "lesson", "page": html, "transition": "none"}
    ts = [0.6, 1.3, 2.4]
    ts += [spec["goal"]["start"] + 1.5]
    for st in spec["steps"]:
        ts += [st["start"] + 0.4, st["start"] + st["dur"] * 0.55, st["start"] + st["dur"] - 0.3]
    ts += [spec["final"]["start"] + 2.0]
    with sync_playwright() as pw:
        b = BV.launch_browser(pw)
        try:
            ctx, page = BV.prepare_page(b, s, 1920, 1080, "landscape", None)
            for i, t in enumerate(ts):
                if t >= spec["total"]:
                    continue
                page.evaluate("([t,x,a]) => window.__nm_frame(t,x,a)", [t, "", 0])
                page.screenshot(path=os.path.join(outdir, "%03d-t%06.2f.png" % (i, t)), type="png")
            ctx.close()
        finally:
            b.close()
    print("кадры:", outdir, len(ts))


def qa(mp4):
    r = subprocess.run([FF, "-hide_banner", "-i", mp4, "-af", "volumedetect", "-f", "null", "-"], stderr=subprocess.PIPE, stdout=subprocess.PIPE)
    err = r.stderr.decode("utf-8", "replace")
    mx = re.search(r"max_volume: (-?[\d.]+) dB", err)
    mean = re.search(r"mean_volume: (-?[\d.]+) dB", err)
    d = subprocess.run([FF, "-v", "error", "-i", mp4, "-f", "null", "-"], stderr=subprocess.PIPE)
    dur = re.search(r"Duration: (\d+):(\d+):([\d.]+)", err)
    sec = int(dur.group(1)) * 3600 + int(dur.group(2)) * 60 + float(dur.group(3)) if dur else 0
    res = dict(peak_db=float(mx.group(1)) if mx else None, mean_db=float(mean.group(1)) if mean else None, duration=round(sec, 1),
               decode="OK" if not d.stderr.strip() else "ОШИБКИ", mb=round(os.path.getsize(mp4) / 1048576, 1),
               size=(re.search(r"(\d{3,4}x\d{3,4})", err) or [None, "?"])[1] if False else re.findall(r"Video:.*?(\d{3,4}x\d{3,4})", err)[:1])
    # 2026-10-30: AAC иногда даёт пик выше пика WAV (до +3 дБ): если вышло за −3 дБ, убавляем звук в готовом файле без перерисовки видео
    if res["peak_db"] is not None and res["peak_db"] > -3.2:
        gain = -3.6 - res["peak_db"]
        tmp = mp4 + ".tmp.mp4"
        subprocess.run([FF, "-v", "error", "-y", "-i", mp4, "-c:v", "copy", "-af", "volume=%.2fdB" % gain, "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", tmp], check=True)
        os.replace(tmp, mp4)
        print("громкость убавлена на %.1f дБ" % -gain)
        return qa(mp4)
    print("QA", json.dumps(res, ensure_ascii=False))
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("id")
    ap.add_argument("--snap")
    ap.add_argument("--draft", action="store_true")
    ap.add_argument("--audio-only", action="store_true")
    ap.add_argument("--qa", action="store_true")
    ap.add_argument("--frames", default="jpeg")
    a = ap.parse_args()
    sc = G.load_scenario(a.id)
    spec, audio, total = G.build(sc)
    work = os.path.join(G.WORK, a.id)
    os.makedirs(work, exist_ok=True)
    wav = os.path.join(work, "mix.wav")
    import build_video as BV
    BV.write_wav(wav, audio)
    print("звук: пик %.2f dBFS, длительность %.1f с" % (G.MA.db(float(abs(audio).max())), total))
    json.dump(spec, open(os.path.join(work, "spec.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    html = os.path.join(work, "lesson.html")
    G.build_html(spec, html)
    os.makedirs(OUTDIR, exist_ok=True)
    slug = "%s-%s-ru" % (sc["id"], sc["slug"])
    G.write_srt(spec["subs"], os.path.join(OUTDIR, slug + ".srt"))
    if a.audio_only:
        return
    if a.snap:
        snap(html, spec, a.snap)
        return
    sj = os.path.join(work, "scenario.json")
    json.dump({"name": slug, "lang": "ru", "voice": "none", "music": False, "fps": 25, "premix": wav.replace("\\", "/"),
               "scenes": [{"id": "lesson", "html": html.replace("\\", "/"), "duration": round(total, 3), "subs": False, "lang": "ru", "transition": "none"}]},
              open(sj, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    out = os.path.join(OUTDIR, slug + ".mp4")
    cmd = [sys.executable, os.path.join(VID, "build_video.py"), sj, "-o", out, "--frames", a.frames]
    if a.draft:
        cmd.append("--draft")
    subprocess.run(cmd, check=True)
    qa(out)


if __name__ == "__main__":
    main()
