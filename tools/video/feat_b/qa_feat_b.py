#!/usr/bin/env python3
"""2026-10-30: контроль готовых роликов-фишек: длительность, декодирование, пик и громкость (ffmpeg volumedetect/ebur128).
Запуск: python qa_feat_b.py [файл.mp4 ...]  (без аргументов — все NN-*-ru.mp4 из <NurMarket-video>/ad/features)"""
import glob
import os
import re
import subprocess
import sys

HOME = os.environ.get("NM_VIDEO_HOME", r"C:\Users\User\NurMarket-video")
FF = open(os.path.join(HOME, "ffmpeg-path.txt"), encoding="utf-8").read().strip()
files = sys.argv[1:] or sorted(glob.glob(os.path.join(HOME, "ad", "features", "[01][0-9]-*-ru.mp4")))
for f in files:
    r = subprocess.run([FF, "-hide_banner", "-i", f, "-af", "volumedetect,ebur128=peak=true", "-f", "null", "-"], stderr=subprocess.PIPE)
    t = r.stderr.decode("utf-8", "replace")
    dur = re.search(r"Duration: ([\d:.]+)", t)
    mx = re.search(r"max_volume: ([-\d.]+) dB", t)
    lufs = re.findall(r"I:\s+([-\d.]+) LUFS", t)
    tp = re.findall(r"Peak:\s+([-\d.]+) dBFS", t)
    err = subprocess.run([FF, "-v", "error", "-i", f, "-f", "null", "-"], stderr=subprocess.PIPE).stderr.strip()
    print("%-34s %s  max %s dB  LUFS %s  TP %s dBFS  декод: %s" % (os.path.basename(f), dur.group(1) if dur else "?", mx.group(1) if mx else "?",
          lufs[-1] if lufs else "?", tp[-1] if tp else "?", "OK" if not err else "ОШИБКИ " + err.decode("utf-8", "replace")[:100]))
