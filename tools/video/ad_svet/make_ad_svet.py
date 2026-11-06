#!/usr/bin/env python3
"""2026-10-29: сборка роликов «Свет погас» целиком: звук -> сцена -> четыре mp4 в <NurMarket-video>/ad/.
  ad-svet-ru-16x9.mp4  (40 с, голос ru, субтитры ru)        ad-svet-ky-16x9.mp4  (40 с, без голоса, субтитры ky)
  ad-svet-ru-9x16.mp4  (15 с, вертикаль, голос ru)           ad-svet-ky-9x16.mp4  (15 с, вертикаль, без голоса)
Запуск:  python tools/video/ad_svet/make_ad_svet.py [--no-audio] [--only ru-16x9 ky-16x9 ru-9x16 ky-9x16]
Для просмотра кадров без рендера: python tools/video/build_video.py tools/video/examples/ad-svet-ru-16x9.json --snap <папка>.
"""
import argparse
import os
import subprocess
import sys

HOME = os.environ.get("NM_VIDEO_HOME", r"C:\Users\User\NurMarket-video")
HERE = os.path.dirname(os.path.abspath(__file__))
VIDEO = os.path.normpath(os.path.join(HERE, ".."))
OUT = os.path.join(HOME, "ad")
VERS = {
    "ru-16x9": ("ad-svet-ru-16x9.json", "landscape"),
    "ky-16x9": ("ad-svet-ky-16x9.json", "landscape"),
    "ru-9x16": ("ad-svet-ru-9x16.json", "portrait"),
    "ky-9x16": ("ad-svet-ky-9x16.json", "portrait"),
}


def run(cmd):
    print(">", " ".join(cmd), flush=True)
    subprocess.check_call(cmd)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--no-audio", action="store_true", help="не пересобирать звук (взять готовый)")
    ap.add_argument("--only", nargs="*", choices=list(VERS))
    a = ap.parse_args()
    py = sys.executable
    if not a.no_audio:
        run([py, os.path.join(HERE, "make_audio.py")])
    run([py, os.path.join(HERE, "make_scene.py")])
    for key in (a.only or list(VERS)):
        js, fmt = VERS[key]
        final = os.path.join(OUT, "ad-svet-%s.mp4" % key)
        tmp = os.path.join(OUT, "work", "tmp-%s.mp4" % key)
        run([py, os.path.join(VIDEO, "build_video.py"), os.path.join(VIDEO, "examples", js), "-o", tmp, "--format", fmt])
        src = tmp if fmt == "landscape" else tmp[:-4] + "-vertical.mp4"   # сборщик дописывает -vertical для 9:16
        os.replace(src, final)
        print("Готово:", final, flush=True)


if __name__ == "__main__":
    main()
