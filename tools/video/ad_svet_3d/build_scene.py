#!/usr/bin/env python3
"""2026-10-29: собирает одну HTML-страницу 3D-сцены «Свет погас»: Three.js (cdnjs 0.170.0, глобальная сборка из NurMarket-video/pylib/three),
js/*.js, временная шкала (ad/work/timeline.built.json после make_audio.py), QR на app.nurcrm.kg (segno), логотип.
Результат: <NurMarket-video>/ad3d/svet3d-scene.html
"""
import base64
import json
import os
import sys

HOME = os.environ.get("NM_VIDEO_HOME", r"C:\Users\User\NurMarket-video")
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HOME, "ad3d")
sys.path.insert(0, os.path.join(HOME, "pylib"))
JS = ["core", "world", "chars", "cast", "story", "ui"]
URL = "https://app.nurcrm.kg"


def qr_svg(url):
    import segno
    q = segno.make(url, error="m", micro=False)
    m = [list(r) for r in q.matrix]
    n, b = len(m), 2
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
    s = n + 2 * b
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %d %d" shape-rendering="crispEdges"><rect width="%d" height="%d" fill="#fff"/>'
            '<path d="%s" fill="#191919"/></svg>' % (s, s, s, s, "".join(d)))


def fonts_css():
    out = []
    for w, fn in [(400, "Montserrat-Regular.ttf"), (600, "Montserrat-SemiBold.ttf"), (700, "Montserrat-Bold.ttf"), (800, "Montserrat-ExtraBold.ttf")]:
        out.append("@font-face{font-family:'Montserrat';font-weight:%d;src:url('file:///%s/fonts/%s');}" % (w, HOME.replace("\\", "/"), fn))
    return "".join(out)


def main():
    tpl = open(os.path.join(HERE, "svet3d.html"), encoding="utf-8").read()
    built = os.path.join(HOME, "ad", "work", "timeline.built.json")
    tl_path = built if os.path.isfile(built) else os.path.join(HERE, "..", "ad_svet", "timeline.json")
    tl = json.load(open(tl_path, encoding="utf-8"))
    three = open(os.path.join(HOME, "pylib", "three", "three.global.js"), encoding="utf-8").read()
    js = "\n".join(open(os.path.join(HERE, "js", n + ".js"), encoding="utf-8").read() for n in JS)
    logo = "data:image/png;base64," + base64.b64encode(open(os.path.join(HOME, "assets", "logo.png"), "rb").read()).decode()
    html = tpl.replace("/*FONTS*/", fonts_css())
    html = html.replace("/*TIMELINE*/null", json.dumps(tl, ensure_ascii=False))
    html = html.replace("/*QR*/null", json.dumps({"app": qr_svg(URL)}))
    html = html.replace("/*LOGO*/", logo)
    html = html.replace("/*THREE*/", three.replace("</" + "script>", "<" + "\/script>"))
    html = html.replace("/*JS*/", js.replace("</" + "script>", "<" + "\/script>"))
    os.makedirs(OUT, exist_ok=True)
    p = os.path.join(OUT, "svet3d-scene.html")
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(html)
    print("OK", p, "%.0f КБ" % (len(html.encode("utf-8")) / 1024), "(шкала: %s)" % os.path.basename(tl_path))


if __name__ == "__main__":
    main()
