#!/usr/bin/env python3
"""2026-10-30: ролики-фишки NurCRM — подготовка ресурсов (один раз): PNG-позы персонажей и реквизит из design/Ad-Light,
кадры из mp4 стенда (JPEG, 12 к/с) и QR на app.nurcrm.kg. Запуск: python feat_assets.py
Подписи «stand/walk…» и название «БЕРЕКЕ» (в кассе героев — магазин «Нур») убираются из копий SVG, оригиналы не трогаем."""
import json
import os
import re
import subprocess
import sys

HOME = os.environ.get("NM_VIDEO_HOME", r"C:\Users\User\NurMarket-video")
sys.path.insert(0, os.path.join(HOME, "pylib"))
REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
AD = os.path.join(REPO, "design", "Ad-Light")
OUT = os.path.join(HOME, "feat-assets")
FRAMES = os.path.join(HOME, "feat-frames")
SHOTS = os.path.join(HOME, "shots-features")
FFMPEG = open(os.path.join(HOME, "ffmpeg-path.txt"), encoding="utf-8").read().strip()

CHARS = {"ainura": ["stand", "walk", "calc", "scan", "happy", "angry"], "apa": ["stand", "walk", "angry", "happy", "sad"],
         "ata": ["stand", "walk", "angry", "happy", "sad"], "bakyt": ["stand", "walk", "angry", "happy", "sad"],
         "erlan": ["stand", "walk", "angry", "happy", "flash"], "jyldyz": ["stand", "walk", "angry", "happy", "sad"],
         "nurlan": ["stand", "walk", "flash", "happy", "angry"]}


def read_svg(name):
    s = open(os.path.join(AD, name), encoding="utf-8").read()
    s = re.sub(r'<text [^>]*fill="#6B6B6B"[^>]*>[^<]*</text>', "", s)  # подписи листа
    s = s.replace("БЕРЕКЕ", "НУР")
    return s


def build_chars(pw_page):
    os.makedirs(OUT, exist_ok=True)
    for c, poses in CHARS.items():
        svg = read_svg("char-%s.svg" % c)
        pw_page.set_content("<body style='margin:0;background:transparent'>%s</body>" % svg)
        for i, p in enumerate(poses):
            # 2026-10-30: прячем соседние позы (луч фонарика «flash» заходит в соседний слот)
            pw_page.evaluate("([c,p]) => document.querySelectorAll('[id$=\"-slot\"]').forEach(e => e.style.visibility = e.id === c + '-' + p + '-slot' ? 'visible' : 'hidden')", [c, p])
            dst = os.path.join(OUT, "char-%s-%s.png" % (c, p))
            pw_page.screenshot(path=dst, clip={"x": i * 400, "y": 0, "width": 400, "height": 700}, omit_background=True)
    print("персонажи готовы")


def build_props(pw_page):
    svg = read_svg("props.svg")
    pw_page.set_content("<body style='margin:0;background:transparent'>%s</body>" % svg)
    ids = pw_page.evaluate("[...document.querySelectorAll('[id^=prop-]')].map(e=>e.id)")
    for pid in ids:
        r = pw_page.evaluate("(id)=>{const b=document.getElementById(id).getBoundingClientRect();return [b.x,b.y,b.width,b.height]}", pid)
        pad = 6
        pw_page.screenshot(path=os.path.join(OUT, pid + ".png"), omit_background=True,
                           clip={"x": r[0] - pad, "y": r[1] - pad, "width": r[2] + 2 * pad, "height": r[3] + 2 * pad})
    print("реквизит:", len(ids))


def build_bg(pw_page):
    for f, ids in (("bg-wall.svg", ["far-window", "far-door", "far-clock", "far-poster"]), ("bg-counter.svg", ["counter"])):
        svg = read_svg(f)
        pw_page.set_content("<body style='margin:0;background:transparent'>%s</body>" % svg)
        for i in ids:
            r = pw_page.evaluate("(id)=>{const b=document.getElementById(id).getBoundingClientRect();return [b.x,b.y,b.width,b.height]}", i)
            pw_page.screenshot(path=os.path.join(OUT, "bg-%s.png" % i.replace("far-", "")), omit_background=True,
                               clip={"x": r[0] - 4, "y": r[1] - 4, "width": r[2] + 8, "height": r[3] + 8})
    print("фон готов")


def build_qr():
    import segno
    q = segno.make("https://app.nurcrm.kg", error="m", micro=False)
    q.save(os.path.join(OUT, "qr-app.svg"), scale=1, border=1, dark="#191919", light="#ffffff", xmldecl=False)
    print("QR готов")


def extract_frames(name, fit):
    """mp4 -> JPEG-кадры 12 к/с; fit = 'w1400' (ширина не больше) или 'h1100'. Пишет meta.json (размер исходника, n, fps)."""
    src = os.path.join(SHOTS, name + ".mp4")
    d = os.path.join(FRAMES, name)
    if os.path.isfile(os.path.join(d, "meta.json")):
        return
    os.makedirs(d, exist_ok=True)
    info = subprocess.run([FFMPEG, "-hide_banner", "-i", src], stderr=subprocess.PIPE).stderr.decode("utf-8", "replace")
    m = re.search(r"Video:.*?, (\d{3,5})x(\d{3,5})", info)
    w0, h0 = int(m.group(1)), int(m.group(2))
    vf = "scale='min(1400,iw)':-2" if fit.startswith("w") else "scale=-2:'min(1100,ih)'"
    subprocess.run([FFMPEG, "-y", "-v", "error", "-i", src, "-vf", "fps=12," + vf, "-q:v", "3", os.path.join(d, "%04d.jpg")], check=True)
    n = len([f for f in os.listdir(d) if f.endswith(".jpg")])
    json.dump({"w": w0, "h": h0, "n": n, "fps": 12}, open(os.path.join(d, "meta.json"), "w"))
    print("кадры", name, n)


def main():
    from playwright.sync_api import sync_playwright
    os.makedirs(OUT, exist_ok=True)
    with sync_playwright() as pw:
        b = pw.chromium.launch(channel="msedge")
        page = b.new_context(device_scale_factor=2, viewport={"width": 2500, "height": 1100}).new_page()
        build_chars(page)
        build_props(page)
        build_bg(page)
        b.close()
    build_qr()
    for n, fit in (("01-kassa-line-added", "w1400"), ("01-mobile-pair", "h1100"), ("01-mobile-scan", "h1100"),
                   ("02-mobile-advisor-call", "h1100"), ("03-kassa-receiving-unknown", "w1400"), ("03-mobile-smart-camera", "h1100"),
                   ("04-kassa-receiving-xlsx", "w1400"), ("05-kassa-revision-scan", "w1400"),
                   ("07-kassa-offline-badge", "w1400"), ("07-kassa-queue-flush", "w1400")):
        extract_frames(n, fit)


if __name__ == "__main__":
    main()
