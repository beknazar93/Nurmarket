#!/usr/bin/env python3
"""2026-10-29: рендер 3D-ролика «Свет погас» кадр за кадром (Playwright + Chromium/Edge, WebGL) -> mp4 (ffmpeg, premix из ad_svet).
  python render3d.py snap --fmt 16x9 --lang ru [--times 0.5 1 2] [--scenes] [--at 1.0 2.0 ...] [--draft]     -> PNG в <NM-video>/ad3d/snaps
  python render3d.py video --fmt 16x9 --lang ru [--draft] [--workers 3] [--gl hw|swift] [--range 0 5]          -> ad3d/ad-svet-3d-<lang>-<fmt>.mp4
Браузер по умолчанию — Edge с аппаратным WebGL (D3D11); --gl swift — программный SwiftShader (если нет видеокарты).
Финальные файлы копируются в <NM-video>/ad/ (ad-svet-3d-ru-16x9.mp4 и т.д.).
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import time
from concurrent.futures import ProcessPoolExecutor

HOME = os.environ.get("NM_VIDEO_HOME", r"C:\Users\User\NurMarket-video")
D3 = os.path.join(HOME, "ad3d")
PAGE = os.path.join(D3, "svet3d-scene.html")
FFMPEG = open(os.path.join(HOME, "ffmpeg-path.txt"), encoding="utf-8").read().strip()
FMT = {"16x9": (1920, 1080, "full", 40.0), "9x16": (1080, 1920, "short", 15.0)}
SCENES_FULL = [0, 2, 3, 7.4, 9, 12, 15, 18, 20, 22, 25, 28, 30, 32, 34, 38]   # К1…К16
SCENES_SHORT = [0, 3, 7, 11]
GL = {"hw": [], "swift": ["--use-angle=swiftshader", "--use-gl=angle", "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist"]}


def launch(pw, gl):
    args = ["--force-device-scale-factor=1", "--hide-scrollbars", "--font-render-hinting=none", "--disable-lcd-text"] + GL[gl]
    last = None
    for kw in ({"channel": "msedge"}, {"channel": "chrome"}, {}):
        try:
            return pw.chromium.launch(headless=True, args=args, **kw)
        except Exception as e:
            last = e
    raise SystemExit("Не запустился браузер: %s" % last)


def open_page(pw, fmt, lang, gl, draft=False):
    W, H, var, dur = FMT[fmt]
    b = launch(pw, gl)
    ctx = b.new_context(viewport={"width": W, "height": H}, device_scale_factor=1)
    p = ctx.new_page()
    p.on("console", lambda m: print("  [page]", m.text[:200]) if m.type in ("error", "warning") else None)
    p.on("pageerror", lambda e: print("  [PAGEERR]", str(e)[:400]))
    p.add_init_script("window.SCENE=%s;" % json.dumps({"params": {}, "duration": dur, "lang": lang, "portrait": H > W, "W": W, "H": H, "id": "svet3d"}))
    p.goto("file:///" + PAGE.replace("\\", "/"))
    p.wait_for_function("window.__booted===true || window.__bootError", timeout=120000)
    err = p.evaluate("window.__bootError")
    if err:
        raise SystemExit("Ошибка сцены: " + err)
    return b, p


def snap(a):
    from playwright.sync_api import sync_playwright
    W, H, var, dur = FMT[a.fmt]
    ts = []
    if a.at:
        ts = a.at
    else:
        starts = SCENES_FULL if var == "full" else SCENES_SHORT
        offs = a.times or [0.5, 1.0, 2.0]
        for i, s in enumerate(starts):
            nxt = starts[i + 1] if i + 1 < len(starts) else dur
            for o in offs:
                if s + o < nxt - 0.02 or o == offs[0]:
                    ts.append(round(s + o, 3))
    out = os.path.join(D3, "snaps", "%s-%s" % (a.fmt, a.lang))
    os.makedirs(out, exist_ok=True)
    t0 = time.time()
    with sync_playwright() as pw:
        b, p = open_page(pw, a.fmt, a.lang, a.gl)
        for t in ts:
            p.evaluate("t=>window.__render(t)", t)
            path = os.path.join(out, "t%06.2f.png" % t)
            p.screenshot(path=path, type="png")
        b.close()
    print("снимков %d за %.1f с -> %s" % (len(ts), time.time() - t0, out))


def worker(args):
    fmt, lang, gl, frames, fps, outdir = args
    from playwright.sync_api import sync_playwright
    with sync_playwright() as pw:
        b, p = open_page(pw, fmt, lang, gl)
        for k in frames:
            p.evaluate("t=>window.__render(t)", k / fps)
            p.screenshot(path=os.path.join(outdir, "f%05d.jpg" % k), type="jpeg", quality=96)
        b.close()
    return len(frames)


def video(a):
    W, H, var, dur = FMT[a.fmt]
    fps = a.fps
    n = int(round(dur * fps))
    r0, r1 = 0, n
    if a.range:
        r0, r1 = int(a.range[0] * fps), int(a.range[1] * fps)
    work = os.path.join(D3, "frames-%s-%s" % (a.fmt, a.lang))
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)
    frames = list(range(r0, r1))
    chunks = [frames[i::a.workers] for i in range(a.workers)]
    t0 = time.time()
    with ProcessPoolExecutor(max_workers=a.workers) as ex:
        list(ex.map(worker, [(a.fmt, a.lang, a.gl, c, fps, work) for c in chunks if c]))
    render_s = time.time() - t0
    print("кадров %d, рендер %.0f с (%.2f с/кадр)" % (len(frames), render_s, render_s / max(1, len(frames))))
    if a.range:
        return
    wav = os.path.join(HOME, "ad", "work", "ad-svet-%s-%s.wav" % ("full" if var == "full" else "short", a.lang))
    out = os.path.join(D3, "ad-svet-3d-%s-%s.mp4" % (a.lang, a.fmt))
    cmd = [FFMPEG, "-y", "-hide_banner", "-loglevel", "error", "-framerate", str(fps), "-i", os.path.join(work, "f%05d.jpg"), "-i", wav,
           "-vf", "scale=out_color_matrix=bt709:out_range=tv,format=yuv420p", "-c:v", "libx264", "-preset", "medium", "-crf", "17", "-profile:v", "high",
           "-r", str(fps), "-g", str(fps * 2), "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
           "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-movflags", "+faststart", "-t", str(dur), out]
    subprocess.check_call(cmd)
    print("Готово:", out, "(рендер %.0f с)" % render_s)
    final = os.path.join(HOME, "ad", os.path.basename(out))
    if not a.draft_out:
        shutil.copyfile(out, final)
        print("->", final)
    shutil.rmtree(work, ignore_errors=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", choices=["snap", "video"])
    ap.add_argument("--fmt", choices=list(FMT), default="16x9")
    ap.add_argument("--lang", choices=["ru", "ky"], default="ru")
    ap.add_argument("--gl", choices=list(GL), default="hw")
    ap.add_argument("--times", nargs="*", type=float)
    ap.add_argument("--at", nargs="*", type=float)
    ap.add_argument("--fps", type=int, default=30)
    ap.add_argument("--workers", type=int, default=3)
    ap.add_argument("--range", nargs=2, type=float)
    ap.add_argument("--draft-out", action="store_true", help="не копировать в ad/")
    a = ap.parse_args()
    if a.mode == "snap":
        snap(a)
    else:
        video(a)


if __name__ == "__main__":
    main()
