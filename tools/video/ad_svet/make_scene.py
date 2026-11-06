#!/usr/bin/env python3
"""2026-10-29: собирает одну HTML-страницу сцены «Свет погас»: вшивает SVG-ассеты design/Ad-Light/, QR (segno), временную шкалу
(timeline.built.json после make_audio.py) и svet.js. Результат: <NurMarket-video>/ad/work/svet-scene.html.
Запуск:  python make_scene.py
"""
import json
import os
import re
import sys

HOME = os.environ.get("NM_VIDEO_HOME", r"C:\Users\User\NurMarket-video")
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
ASSETS = os.path.join(REPO, "design", "Ad-Light")
OUT = os.path.join(HOME, "ad", "work")
sys.path.insert(0, os.path.join(HOME, "pylib"))

FILES = ["char-ainura", "char-ata", "char-jyldyz", "char-bakyt", "char-erlan", "char-apa", "char-nurlan",
         "bg-wall", "bg-shelves-far", "bg-shelves-mid", "bg-shelves-near", "bg-counter", "bg-lamp", "props", "logo"]
URLS = {"app": "https://app.nurcrm.kg"}  # 2026-10-29: по просьбе владельца ссылки и QR на GitHub из роликов убраны


def inline_svg(name):
    s = open(os.path.join(ASSETS, name + ".svg"), encoding="utf-8").read()
    s = re.sub(r"<\?xml[^>]*\?>", "", s)
    s = re.sub(r"<title>.*?</title>", "", s, flags=re.S)
    s = s.replace("<svg ", '<svg id="src-%s" ' % name.replace("char-", "").replace("bg-", "bg-"), 1)
    return s


def qr_svg(url):
    import segno
    q = segno.make(url, error="m", micro=False)
    m = [list(r) for r in q.matrix]
    n = len(m)
    b = 2  # тихая зона
    d = []
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
    size = n + 2 * b
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %d %d" shape-rendering="crispEdges">'
            '<rect width="%d" height="%d" fill="#fff"/><path d="%s" fill="#191919"/></svg>' % (size, size, size, size, "".join(d)))


def main():
    tpl = open(os.path.join(HERE, "svet.html"), encoding="utf-8").read()
    js = open(os.path.join(HERE, "svet.js"), encoding="utf-8").read()
    built = os.path.join(OUT, "timeline.built.json")
    tl_path = built if os.path.isfile(built) else os.path.join(HERE, "timeline.json")
    tl = json.load(open(tl_path, encoding="utf-8"))
    assets = "\n".join(inline_svg(n) for n in FILES)
    qr = {k: qr_svg(u) for k, u in URLS.items()}
    html = tpl.replace("<!--ASSETS-->", assets)
    html = html.replace("/*TIMELINE*/null", json.dumps(tl, ensure_ascii=False))
    html = html.replace("/*QR*/null", json.dumps(qr))
    html = html.replace("/*JS*/", js)
    os.makedirs(OUT, exist_ok=True)
    p = os.path.join(OUT, "svet-scene.html")
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(html)
    print("OK", p, "%.0f КБ" % (len(html.encode("utf-8")) / 1024), "(шкала: %s)" % os.path.basename(tl_path))


if __name__ == "__main__":
    main()
