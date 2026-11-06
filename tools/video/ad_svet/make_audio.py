#!/usr/bin/env python3
"""2026-10-29: звук ролика «Свет погас» — живая озвучка edge-tts (Dmitry / Svetlana), SFX и музыка синтезом numpy.
Без лицензий, без образца голоса владельца. Результат — готовые микшированные WAV 48 кГц стерео:
  ad-svet-full-ru.wav (голос+SFX+музыка), ad-svet-full-ky.wav (SFX+музыка, без голоса), то же для short (9:16, 15 с).
Заодно пишет timeline.built.json (длительности реплик) — его читает сцена для субтитров.
Запуск:  python make_audio.py [--only full|short]
"""
import argparse
import asyncio
import hashlib
import json
import os
import subprocess
import sys
import wave

HOME = os.environ.get("NM_VIDEO_HOME", r"C:\Users\User\NurMarket-video")
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HOME, "ad", "work")
CACHE = os.path.join(HOME, "cache")
sys.path.insert(0, os.path.join(HOME, "pylib"))
import numpy as np  # noqa: E402

SR = 48000
FFMPEG = open(os.path.join(HOME, "ffmpeg-path.txt"), encoding="utf-8").read().strip()
BPM = 100.0
BEAT = 60.0 / BPM
BAR = 4 * BEAT


def log(*a):
    print(*a, flush=True)


# ------------------------------------------------------------------ утилиты DSP
def midi(n):
    return 440.0 * 2 ** ((n - 69) / 12.0)


def fft_filter(x, lo=None, hi=None, order=4):
    """Мягкий LP/HP через FFT (достаточно для коротких кусков)."""
    n = len(x)
    if n == 0:
        return x
    sp = np.fft.rfft(x)
    f = np.fft.rfftfreq(n, 1.0 / SR)
    g = np.ones_like(f)
    if hi:
        g *= 1.0 / (1.0 + (f / hi) ** (2 * order))
    if lo:
        g *= 1.0 - 1.0 / (1.0 + (f / lo) ** (2 * order))
    return np.fft.irfft(sp * g, n)


def lp(x, c):
    return fft_filter(x, hi=c)


def hp(x, c):
    return fft_filter(x, lo=c)


def bp(x, lo, hi):
    return fft_filter(x, lo=lo, hi=hi)


def t_axis(dur):
    return np.arange(int(dur * SR)) / SR


def adsr(n, a, d, s, r):
    e = np.ones(n)
    na, nd, nr = int(a * SR), int(d * SR), int(r * SR)
    na = min(na, n)
    if na > 0:
        e[:na] = np.linspace(0, 1, na)
    nd = min(nd, n - na)
    if nd > 0:
        e[na:na + nd] = np.linspace(1, s, nd)
    e[na + nd:] = s
    if nr > 0 and nr < n:
        e[-nr:] *= np.linspace(1, 0, nr)
    return e


def put(buf, sig, at, gain=1.0):
    i = int(round(at * SR))
    if i >= len(buf) or len(sig) == 0:
        return
    if i < 0:
        sig = sig[-i:]
        i = 0
    j = min(len(buf), i + len(sig))
    buf[i:j] += sig[: j - i] * gain


def rms(x):
    return float(np.sqrt(np.mean(x ** 2))) if len(x) else 0.0


def db(x):
    return 20 * np.log10(max(x, 1e-9))


# ------------------------------------------------------------------ озвучка
WHO = {  # голос, скорость, высота, ресэмпл (детский голос), «хрипотца»
    "N": dict(v="ru-RU-DmitryNeural", rate="+8%", pitch="+0Hz"),
    "ER": dict(v="ru-RU-DmitryNeural", rate="+14%", pitch="+14Hz"),
    "AI": dict(v="ru-RU-SvetlanaNeural", rate="+8%", pitch="+0Hz"),
    "AT": dict(v="ru-RU-DmitryNeural", rate="+4%", pitch="-26Hz", hoarse=0.05),
    "NU": dict(v="ru-RU-DmitryNeural", rate="+10%", pitch="-8Hz"),
    "AP": dict(v="ru-RU-SvetlanaNeural", rate="+6%", pitch="-32Hz", hoarse=0.05),
    "BA": dict(v="ru-RU-SvetlanaNeural", rate="+6%", pitch="+38Hz", resample=1.14),
}


def decode(path):
    r = subprocess.run([FFMPEG, "-v", "error", "-i", path, "-f", "f32le", "-ac", "1", "-ar", str(SR), "-"],
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if r.returncode != 0:
        raise RuntimeError(r.stderr.decode("utf-8", "replace")[:300])
    return np.frombuffer(r.stdout, dtype="<f4").astype(np.float64)


async def _tts(text, voice, rate, pitch, path):
    import edge_tts
    last = None
    for k in range(14):  # сервис иногда отвечает «No audio was received» — повторяем
        try:
            await edge_tts.Communicate(text, voice, rate=rate, pitch=pitch).save(path)
            if os.path.getsize(path) > 500:
                return
        except Exception as e:
            last = e
        await asyncio.sleep(1.5 + 1.0 * k)
    raise RuntimeError("edge-tts не ответил: %s" % last)


def tts_line(who, text, rate=None):
    c = WHO[who]
    rate = rate or c["rate"]
    key = hashlib.sha1(("%s|%s|%s|%s" % (c["v"], rate, c["pitch"], text)).encode("utf-8")).hexdigest()[:20]
    os.makedirs(CACHE, exist_ok=True)
    mp3 = os.path.join(CACHE, "adsvet-%s.mp3" % key)
    if not os.path.isfile(mp3) or os.path.getsize(mp3) < 500:
        asyncio.run(_tts(text, c["v"], rate, c["pitch"], mp3))
    a = decode(mp3)
    if c.get("resample"):  # детский голос: ресэмпл вверх (выше тон и темп)
        k = c["resample"]
        idx = np.arange(0, len(a) - 1, k)
        a = np.interp(idx, np.arange(len(a)), a)
    if c.get("hoarse"):  # возрастная хрипотца: шум по огибающей голоса
        env = np.convolve(np.abs(a), np.ones(480) / 480, mode="same")
        rng = np.random.default_rng(5)
        a = a + hp(rng.standard_normal(len(a)), 1800) * env * c["hoarse"] * 6
    idx = np.where(np.abs(a) > 0.006)[0]  # обрезка тишины
    if len(idx):
        a = a[max(0, idx[0] - int(0.03 * SR)): min(len(a), idx[-1] + int(0.06 * SR))]
    n = int(0.012 * SR)
    a[:n] *= np.linspace(0, 1, n)
    a[-n:] *= np.linspace(1, 0, n)
    r = rms(a[np.abs(a) > 0.01])  # выравнивание громкости реплики
    if r > 0:
        a = a * (0.105 / r)
    return a


def atempo(a, ratio):
    """Ускорение без смены высоты (ffmpeg atempo); ratio 1.0..1.4."""
    tmp = os.path.join(OUT, "_tmp_in.f32")
    a.astype("<f4").tofile(tmp)
    r = subprocess.run([FFMPEG, "-v", "error", "-f", "f32le", "-ar", str(SR), "-ac", "1", "-i", tmp, "-filter:a",
                        "atempo=%.4f" % ratio, "-f", "f32le", "-ac", "1", "-ar", str(SR), "-"],
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    return np.frombuffer(r.stdout, dtype="<f4").astype(np.float64)


# ------------------------------------------------------------------ SFX
RNG = np.random.default_rng(11)


def sx_click(v=1.0):
    n = int(0.06 * SR)
    t = np.arange(n) / SR
    s = hp(RNG.standard_normal(n), 1800) * np.exp(-t / 0.004) * 0.9
    s += np.sin(2 * np.pi * 2700 * t) * np.exp(-t / 0.006) * 0.5
    s += np.sin(2 * np.pi * 95 * t) * np.exp(-t / 0.02) * 0.9
    return s * v


def sx_switch():
    out = np.zeros(int(0.2 * SR))
    put(out, sx_click(1.0), 0.0)
    put(out, sx_click(0.45), 0.045)
    return out


def sx_powerdown():
    d = 0.9
    t = t_axis(d)
    f = 1900 * np.exp(-t * 3.2) + 70
    ph = 2 * np.pi * np.cumsum(f) / SR
    s = (np.sin(ph) + 0.4 * np.sin(2 * ph) + 0.2 * np.sin(3 * ph)) * np.exp(-t / 0.32)
    s *= np.minimum(1.0, t / 0.01)
    th = np.sin(2 * np.pi * 60 * t) * np.exp(-((t - 0.78) ** 2) / 0.0008) * 0.6
    return lp(s * 0.55 + th, 6000)


def sx_lighton():
    d = 1.8
    t = t_axis(d)
    env = np.clip(t / 0.08, 0, 1) * np.exp(-t / 1.0)
    flick = np.where((t > 0.05) & (t < 0.16) & ((np.floor(t * 60) % 2) == 0), 0.4, 1.0)
    hum = sum(np.sin(2 * np.pi * 100 * k * t + k) / k for k in range(1, 7)) * 0.2
    hum += np.sin(2 * np.pi * 3600 * t) * 0.02 * np.exp(-t / 0.4)
    return hum * env * flick


def sx_bell(f0=1568.0):
    d = 1.6
    t = t_axis(d)
    parts = [(1.0, 1.0, 0.9), (2.76, 0.55, 0.5), (5.4, 0.3, 0.25), (8.93, 0.15, 0.12)]
    s = sum(a * np.sin(2 * np.pi * f0 * m * t) * np.exp(-t / (dd * 0.55)) for m, a, dd in parts)
    s *= np.minimum(1.0, t / 0.002)
    s += RNG.standard_normal(len(t)) * np.exp(-t / 0.004) * 0.15
    out = s * 0.45
    return out


def sx_whoosh(dur=1.0):
    n = int(dur * SR)
    t = np.arange(n) / SR
    x = RNG.standard_normal(n)
    a = lp(x, 1800)
    b = hp(x, 2500)
    sweep = np.sin(np.pi * t / dur) ** 2
    rise = np.linspace(0.2, 1, n)
    s = (a * (1 - rise * 0.5) + b * rise * 0.55) * sweep
    return s * 0.6


def sx_street(dur):
    n = int(dur * SR)
    x = lp(RNG.standard_normal(n), 320) * 0.5 + lp(RNG.standard_normal(n), 1100) * 0.05
    t = np.arange(n) / SR
    x *= 0.85 + 0.15 * np.sin(2 * np.pi * 0.7 * t)
    f = np.minimum(1.0, t / 0.2) * np.minimum(1.0, (dur - t) / 0.3)
    return x * f


def sx_car():
    d = 1.4
    n = int(d * SR)
    t = np.arange(n) / SR
    x = lp(RNG.standard_normal(n), 900) * (np.sin(np.pi * t / d) ** 2)
    x += np.sin(2 * np.pi * (70 + 18 * np.sin(np.pi * t / d)) * t) * (np.sin(np.pi * t / d) ** 2) * 0.4
    pan_lp = lp(x, 700 + 700 * np.abs(np.sin(np.pi * t / d)).mean())
    return pan_lp * 0.8


VOWELS = [(730, 1090), (270, 2290), (300, 870), (530, 1840), (660, 1720), (440, 1020)]


def _voice(dur, f0, rate, density, loud, rng, rise=0.0):
    n = int(dur * SR)
    out = np.zeros(n)
    t = 0.0
    while t < dur - 0.05:
        if rng.random() > density:
            t += rng.uniform(0.18, 0.5)
            continue
        L = rng.uniform(0.08, 0.2) / rate
        ns = int(L * SR)
        tt = np.arange(ns) / SR
        F1, F2 = VOWELS[rng.integers(len(VOWELS))]
        fs = f0 * (1 + 0.12 * np.sin(2 * np.pi * rng.uniform(2, 5) * tt) + rise * (t / dur))
        ph = 2 * np.pi * np.cumsum(fs) / SR
        s = np.zeros(ns)
        for k in range(1, 22):
            fk = k * f0
            if fk > 3800:
                break
            a = np.exp(-((fk - F1) / 170) ** 2) + 0.55 * np.exp(-((fk - F2) / 260) ** 2) + 0.12 / k
            s += a * np.sin(k * ph)
        s *= np.sin(np.pi * np.arange(ns) / ns) ** 1.5
        i = int(t * SR)
        out[i:i + ns] += s[: len(out[i:i + ns])] * rng.uniform(0.5, 1.0)
        t += L * rng.uniform(0.9, 1.35)
    return out * loud


def sx_murmur(dur, mood="calm"):
    rng = np.random.default_rng({"calm": 3, "angry": 4, "warm": 5}[mood])
    cfg = {"calm": (8, 0.55, 0.8, 0.0), "angry": (9, 0.8, 1.25, 0.18), "warm": (7, 0.55, 0.9, 0.06)}[mood]
    nv, dens, rate, rise = cfg
    n = int(dur * SR)
    out = np.zeros(n)
    for i in range(nv):
        f0 = rng.uniform(95, 260) * (1.12 if mood == "angry" else 1.0)
        out += _voice(dur, f0, rate, dens, 1.0 / nv ** 0.5, rng, rise)
    out = lp(out, 3400)
    out += lp(rng.standard_normal(n), 800) * 0.02
    f = np.minimum(1.0, np.arange(n) / SR / 0.5) * np.minimum(1.0, (dur - np.arange(n) / SR) / 0.5)
    if mood == "angry":
        f *= np.linspace(0.6, 1.2, n)
    r = rms(out)
    return out / max(r, 1e-6) * 0.11 * f


def sx_bag(dur=0.45):
    n = int(dur * SR)
    t = np.arange(n) / SR
    x = hp(RNG.standard_normal(n), 2500)
    bursts = (np.sin(2 * np.pi * 17 * t + 1.3) > 0.2) * np.exp(-t / 0.2)
    return x * bursts * 0.25


def sx_paper(dur=0.5):
    n = int(dur * SR)
    t = np.arange(n) / SR
    x = bp(RNG.standard_normal(n), 1500, 6000)
    bursts = np.abs(np.sin(2 * np.pi * 11 * t)) ** 3 * np.exp(-t / 0.3)
    return x * bursts * 0.3


def sx_regbeep():
    s = np.zeros(int(0.25 * SR))
    for at, f in ((0.0, 2900), (0.085, 2100)):
        t = t_axis(0.07)
        w = np.sign(np.sin(2 * np.pi * f * t)) * 0.5 + np.sin(2 * np.pi * f * t) * 0.5
        put(s, lp(w, 5000) * adsr(len(t), 0.003, 0.0, 1.0, 0.01), at)
    return s * 0.5


def sx_calc(times):
    n = int((max(times) + 0.2) * SR)
    out = np.zeros(n)
    for i, tm in enumerate(times):
        f = 2350 if i % 3 else 2600
        t = t_axis(0.055)
        put(out, np.sin(2 * np.pi * f * t) * adsr(len(t), 0.002, 0.0, 1.0, 0.012), tm, 0.5)
    return out


def sx_puff():
    d = 0.3
    t = t_axis(d)
    return lp(RNG.standard_normal(len(t)), 1600) * np.exp(-t / 0.07) * np.minimum(1, t / 0.01) * 0.7


def sx_nervous():
    d = 0.7
    t = t_axis(d)
    x = bp(RNG.standard_normal(len(t)), 300, 2200) * np.sin(np.pi * t / d) ** 2
    return x * 0.5


def sx_thump():
    d = 0.3
    t = t_axis(d)
    return (np.sin(2 * np.pi * (80 - 30 * t) * t) * np.exp(-t / 0.06) + lp(RNG.standard_normal(len(t)), 500) * np.exp(-t / 0.03) * 0.5) * 0.9


def sx_steps(dur, count):
    out = np.zeros(int(dur * SR))
    for i in range(count):
        tm = i * dur / count + RNG.uniform(-0.03, 0.03)
        t = t_axis(0.09)
        s = lp(RNG.standard_normal(len(t)), 900) * np.exp(-t / 0.018) + np.sin(2 * np.pi * 105 * t) * np.exp(-t / 0.03) * 0.8
        put(out, s * 0.7, max(tm, 0), RNG.uniform(0.7, 1.0))
    return out


def sx_rewind(dur):
    n = int(dur * SR)
    t = np.arange(n) / SR
    x = bp(RNG.standard_normal(n), 1800, 7000)
    chatter = 0.55 + 0.45 * np.sign(np.sin(2 * np.pi * 38 * t))
    whine_f = 900 + 2600 * np.sin(np.pi * t / dur) ** 0.7
    whine = np.sin(2 * np.pi * np.cumsum(whine_f) / SR) * 0.35
    env = np.minimum(1.0, t / 0.06) * np.minimum(1.0, (dur - t) / 0.03)
    return (x * chatter * 0.45 + whine) * env * 0.8


def sx_stopclick():
    n = int(0.12 * SR)
    t = np.arange(n) / SR
    s = hp(RNG.standard_normal(n), 900) * np.exp(-t / 0.01) * 0.8
    s += np.sin(2 * np.pi * 180 * t) * np.exp(-t / 0.03) * 0.8
    return s


def sx_screenon():
    out = np.zeros(int(1.0 * SR))
    for at, f in ((0.0, 1319), (0.11, 1760)):
        t = t_axis(0.8)
        s = (np.sin(2 * np.pi * f * t) + 0.3 * np.sin(2 * np.pi * f * 2.01 * t)) * np.exp(-t / 0.22)
        put(out, s * np.minimum(1.0, t / 0.004), at, 0.5)
    return out


def sx_pik():
    d = 0.22
    t = t_axis(d)
    s = np.sin(2 * np.pi * 2093 * t) * np.exp(-t / 0.055)
    s += 0.35 * np.sin(2 * np.pi * 4186 * t) * np.exp(-t / 0.02)
    s += 0.25 * np.sin(2 * np.pi * 1568 * t) * np.exp(-t / 0.09)
    s *= np.minimum(1.0, t / 0.0015)
    return s * 0.7


def sx_money():
    out = np.zeros(int(0.8 * SR))
    put(out, sx_paper(0.45), 0.0, 0.8)
    for at, f in ((0.4, 3300), (0.47, 4400)):
        t = t_axis(0.25)
        put(out, np.sin(2 * np.pi * f * t) * np.exp(-t / 0.05) * 0.3, at)
    return out


def sx_receipt():
    d = 0.5
    t = t_axis(d)
    x = bp(RNG.standard_normal(len(t)), 900, 4200)
    pulse = (np.sign(np.sin(2 * np.pi * 52 * t)) > 0) * 1.0
    env = np.minimum(1.0, t / 0.03) * np.minimum(1.0, (d - t) / 0.06)
    tone = np.sin(2 * np.pi * (1800 - 400 * t / d) * t) * 0.15
    return (x * pulse * 0.6 + tone) * env * 0.8


def sx_sent():
    d = 0.6
    t = t_axis(d)
    x = bp(RNG.standard_normal(len(t)), 600, 5000) * np.sin(np.pi * np.minimum(1, t / d) ** 0.7) ** 2 * 0.45
    ping = np.sin(2 * np.pi * 1976 * t) * np.exp(-t / 0.12) * 0.25 * (t > 0.3)
    return x + np.roll(ping, 0)


def sx_hit():
    d = 3.2
    t = t_axis(d)
    boom = np.sin(2 * np.pi * (48 + 60 * np.exp(-t * 14)) * t) * np.exp(-t / 0.8)
    noise = lp(RNG.standard_normal(len(t)), 2200) * np.exp(-t / 0.25) * 0.5
    ch = np.zeros(len(t))
    for f in (698.46, 880.0, 1046.5, 1318.5):  # F A C E (Fmaj7)
        ch += np.sin(2 * np.pi * f * t) * np.exp(-t / 1.3) + 0.2 * np.sin(2 * np.pi * f * 2.0 * t) * np.exp(-t / 0.6)
    ch *= np.minimum(1.0, t / 0.004) * 0.22
    return boom * 0.9 + noise + ch


def sx_tick():
    t = t_axis(0.05)
    return np.sin(2 * np.pi * 3800 * t) * np.exp(-t / 0.007) * 0.6


SFX = {
    "switch": lambda e: sx_switch(),
    "powerdown": lambda e: sx_powerdown(),
    "lighton": lambda e: sx_lighton(),
    "bell": lambda e: sx_bell(),
    "whoosh": lambda e: sx_whoosh(e.get("dur", 1.0)),
    "street": lambda e: sx_street(e.get("dur", 2.0)),
    "car": lambda e: sx_car(),
    "murmur": lambda e: sx_murmur(e.get("dur", 3.0), e.get("mood", "calm")),
    "bag": lambda e: sx_bag(),
    "paper": lambda e: sx_paper(),
    "regbeep": lambda e: sx_regbeep(),
    "calc": lambda e: sx_calc(e["times"]),
    "puff": lambda e: sx_puff(),
    "nervous": lambda e: sx_nervous(),
    "thump": lambda e: sx_thump(),
    "steps": lambda e: sx_steps(e.get("dur", 2.0), e.get("count", 5)),
    "rewind": lambda e: sx_rewind(e.get("dur", 1.0)),
    "stopclick": lambda e: sx_stopclick(),
    "screenon": lambda e: sx_screenon(),
    "pik": lambda e: sx_pik(),
    "money": lambda e: sx_money(),
    "receipt": lambda e: sx_receipt(),
    "sent": lambda e: sx_sent(),
    "hit": lambda e: sx_hit(),
    "tick": lambda e: sx_tick(),
}


def build_sfx(events, total):
    buf = np.zeros(int(total * SR))
    for e in events:
        s = SFX[e["n"]](e)
        put(buf, s, e["t"], e.get("v", 1.0))
    return buf


# ------------------------------------------------------------------ музыка
def ks_pluck(freq, dur, bright=0.55, decay=0.994):
    """Karplus–Strong: щипок струны (комуз-подобный лид)."""
    n = int(dur * SR)
    N = max(2, int(SR / freq))
    buf = RNG.uniform(-1, 1, N)
    buf = lp(buf, freq * 6) if bright < 0.7 else buf
    out = np.zeros(n)
    cur = buf.copy()
    pos = 0
    while pos < n:
        m = min(N, n - pos)
        out[pos:pos + m] = cur[:m]
        nxt = 0.5 * (cur + np.roll(cur, -1)) * decay
        cur = nxt
        pos += N
    out *= np.minimum(1.0, np.arange(n) / SR / 0.003)
    out *= np.minimum(1.0, (dur - np.arange(n) / SR) / 0.05)
    return out


def pad_note(freq, dur, vol=1.0):
    t = t_axis(dur)
    s = np.zeros(len(t))
    for det in (-0.004, 0.0, 0.005):
        ph = 2 * np.pi * freq * (1 + det) * t
        s += (np.sin(ph) + 0.5 * np.sin(2 * ph) * 0.5 + 0.2 * np.sin(3 * ph))
    s *= adsr(len(t), 0.35, 0.3, 0.8, 0.5)
    return lp(s, 1800) * vol * 0.25


def bass_note(freq, dur):
    t = t_axis(dur)
    s = np.sin(2 * np.pi * freq * t) + 0.3 * np.sin(2 * np.pi * freq * 2 * t)
    return s * adsr(len(t), 0.01, 0.1, 0.7, 0.08)


def kick():
    t = t_axis(0.35)
    f = 120 * np.exp(-t * 24) + 44
    return np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t / 0.11)


def clap():
    t = t_axis(0.25)
    x = bp(RNG.standard_normal(len(t)), 900, 5500)
    e = np.exp(-t / 0.07) * (1 + 0.8 * (np.floor(t * 130) % 2 == 0) * (t < 0.03))
    return x * e * 0.55


def hat(open_=False):
    t = t_axis(0.2 if open_ else 0.06)
    return hp(RNG.standard_normal(len(t)), 7000) * np.exp(-t / (0.07 if open_ else 0.015))


TENSE_CH = [  # Dm, Bb, Gm, A (3 ноты аккорда, MIDI)
    (38, [50, 53, 57, 60]), (34, [46, 50, 53, 58]), (31, [43, 46, 50, 55]), (33, [45, 49, 52, 57])]
WARM_CH = [  # F, C, Dm, Bb
    (41, [53, 57, 60, 64]), (36, [48, 52, 55, 59]), (38, [50, 53, 57, 62]), (34, [46, 50, 53, 58])]
PENT_MIN = [0, 3, 5, 7, 10]   # D минор-пентатоника от D
PENT_MAJ = [0, 2, 4, 7, 9]    # F мажор-пентатоника от F


def pent(deg, scale, root):
    return root + scale[deg % 5] + 12 * (deg // 5)


def lead_bar(buf, t0, scale, root, pattern, vol, decay=0.994, bright=0.55):
    for i, d in enumerate(pattern):
        if d is None:
            continue
        put(buf, ks_pluck(midi(pent(d, scale, root)), 0.7, bright, decay), t0 + i * BEAT / 2, vol)


def bars_in(t0, t1):
    b = int(np.ceil(t0 / BAR - 1e-6))
    while b * BAR < t1 - 1e-6:
        yield b, b * BAR
        b += 1


def groove(drums, t0, t1, kvol=0.9, hats=True, clap_=True, sparse=False):
    k, c, h, ho = kick(), clap(), hat(), hat(True)
    n0 = int(np.ceil(t0 / BEAT - 1e-6))
    nb = n0
    while nb * BEAT < t1 - 1e-6:
        t = nb * BEAT
        beat = nb % 4
        if beat in (0, 2):
            put(drums, k, t, kvol)
        if beat in (1, 3) and clap_:
            put(drums, c, t, 0.7)
        if hats:
            put(drums, h, t, 0.35)
            put(drums, ho if beat == 3 else h, t + BEAT / 2 + 0.012, 0.25)
        nb += 1


def build_music_full(total):
    """Тревожная -> тёплая. Секции по раскадровке (секунды)."""
    mus = np.zeros(int(total * SR))
    drums = np.zeros(len(mus))
    lead = np.zeros(len(mus))
    pad = np.zeros(len(mus))
    bass = np.zeros(len(mus))
    ambient = np.zeros(len(mus))

    def chord_pad(t0, t1, prog, vol, notes_up=0, pad=pad):
        for b, bt in bars_in(t0 - BAR, t1):
            root, ch = prog[b % 4]
            s = max(bt, t0)
            e = min(bt + BAR, t1)
            if e - s < 0.3:
                continue
            for n in ch:
                put(pad, pad_note(midi(n + notes_up), e - s + 0.4, vol), s)

    # ---- А (0–7,4): тёплый, но приглушённый комуз-лид и мягкая подушка
    motifs = [[4, None, 5, 4, 3, None, 2, None], [2, 3, 4, None, 5, 4, 3, 2], [4, None, 6, 5, 4, None, 3, None],
              [2, None, 3, 2, 0, None, 1, None]]
    a_lead, a_pad, a_drums = np.zeros(len(mus)), np.zeros(len(mus)), np.zeros(len(mus))
    for b, bt in bars_in(0.0, 7.4):
        if 3.0 <= bt < 4.4:
            continue
        lead_bar(a_lead, bt, PENT_MIN, 62, motifs[b % 4], 0.42, 0.991, 0.5)
    chord_pad(0.0, 3.0, TENSE_CH, 0.5, pad=a_pad)
    put(a_pad, pad_note(midi(57), 1.6, 0.55), 3.0)         # «замирает на одной ноте»
    put(a_pad, pad_note(midi(57), 1.8, 0.45), 4.4)
    chord_pad(4.8, 7.4, TENSE_CH, 0.5, pad=a_pad)
    groove(a_drums, 2.4, 7.4, 0.4, hats=True, clap_=False)
    ta0 = np.arange(len(mus)) / SR
    gA = np.clip((7.4 - ta0) / 0.03, 0, 1)             # обрыв старой темы на щелчке 7,4
    a_part = (lp(a_lead, 5500) * 0.55 + a_pad * 0.55 + lp(a_drums, 9000) * 0.7) * gA
    # ---- Б (7,4–18): тревожный фильтр, затем один низкий тон
    t = t_axis(18.0 - 7.4)
    drone = (np.sin(2 * np.pi * midi(38) * t) + 0.5 * np.sin(2 * np.pi * midi(50) * t * 1.003) + 0.3 * np.sin(2 * np.pi * midi(45) * t))
    drone *= np.minimum(1.0, t / 0.3) * (0.55 + 0.3 * np.sin(2 * np.pi * 0.18 * t))
    drone *= np.minimum(1.0, (18.0 - 7.4 - t) / 0.6)
    put(ambient, lp(drone, 700), 7.4, 0.55)
    for nb in range(int(np.ceil(8.0 / BEAT)), int(15.0 / BEAT)):  # тиканье часов-метронома
        put(drums, hat() * 0.5, nb * BEAT, 0.3 + 0.2 * (nb % 4 == 0))
    for b, bt in bars_in(8.4, 15.0):  # редкие диссонансные звоны
        put(lead, ks_pluck(midi(75 + (b % 2)), 1.4, 0.4, 0.995), bt + 0.0, 0.25)
        put(lead, ks_pluck(midi(63), 1.2, 0.4, 0.995), bt + BEAT * 1.5, 0.2)
    for b, bt in bars_in(12.0, 15.0):
        for i in range(8):
            put(bass, bass_note(midi(38), 0.2), bt + i * BEAT / 2, 0.35)
    # ---- В (19,2–20,4): полный бит на плашке
    groove(drums, 19.2, 20.4, 1.0)
    for b, bt in bars_in(19.2 - BAR, 20.4):
        root, ch = WARM_CH[b % 4]
        for i in range(4):
            tt = bt + i * BEAT
            if 19.2 <= tt < 20.4:
                put(bass, bass_note(midi(root), 0.35), tt, 0.9)
    chord_pad(19.2, 20.4, WARM_CH, 0.9)
    # ---- Г (21,2–31): «дзынь», лид возвращается, бит набирает
    motifs_w = [[4, 5, 7, 5, 4, None, 2, 4], [5, 4, 2, 4, 5, None, 7, 5], [7, 5, 4, 2, 4, 5, 4, None],
                [5, None, 7, 9, 7, 5, 4, 2]]
    for b, bt in bars_in(21.0, 31.0):
        lead_bar(lead, bt, PENT_MAJ, 65, motifs_w[b % 4], 0.5 if bt < 24 else 0.62, 0.993, 0.6)
    chord_pad(21.6, 31.0, WARM_CH, 0.8)
    groove(drums, 22.2, 25.0, 0.75, hats=True, clap_=False)
    groove(drums, 25.0, 31.0, 1.0)
    for b, bt in bars_in(22.2 - BAR, 31.0):
        root, ch = WARM_CH[b % 4]
        for i in (0, 1.5, 2, 3.5):
            tt = bt + i * BEAT
            if 22.2 <= tt < 31.0:
                put(bass, bass_note(midi(root), 0.5), tt, 0.8)
    # ---- Д (31–34,55): подъём, свет включается
    groove(drums, 31.0, 34.5, 1.0)
    for b, bt in bars_in(31.0 - BAR, 34.5):
        root, ch = WARM_CH[b % 4]
        for i in (0, 1.5, 2, 3.5):
            tt = bt + i * BEAT
            if 31.0 <= tt < 34.5:
                put(bass, bass_note(midi(root), 0.5), tt, 0.85)
        lead_bar(lead, bt, PENT_MAJ, 65, motifs_w[(b + 2) % 4], 0.7, 0.994, 0.6)
    chord_pad(31.0, 34.55, WARM_CH, 0.95, notes_up=0)
    # ---- Е (34,55–38,6): финальный удар и затухание
    for i, d in enumerate([4, 5, 7, 9, 7, 5, 4, 2]):
        put(lead, ks_pluck(midi(pent(d, PENT_MAJ, 65)), 0.9, 0.6, 0.995), 34.55 + 0.6 + i * BEAT / 2, 0.55 * (1 - i * 0.07))
    put(pad, pad_note(midi(53), 3.6, 0.9), 34.55)
    put(pad, pad_note(midi(57), 3.6, 0.9), 34.55)
    put(pad, pad_note(midi(60), 3.6, 0.9), 34.55)
    put(pad, pad_note(midi(64), 3.6, 0.8), 34.55)
    put(bass, bass_note(midi(41), 1.2), 34.55, 0.9)

    drums = lp(drums, 9000)
    lead = lp(lead, 5500)
    mus = lead * 0.55 + pad * 0.55 + bass * 0.7 + drums * 0.7 + ambient * 0.8 + a_part
    # приглушение: К1–К3 (0–7,4) мягче; конец — затухание
    gain = np.ones(len(mus))
    ta = np.arange(len(mus)) / SR
    gain[ta < 7.4] = 0.62
    gain *= np.clip((38.4 - ta) / 1.6, 0, 1)
    mus *= gain
    # резкая остановка на щелчках (7,4 и 20,4): музыка обрывается
    for cut, back in ((7.4, 7.4), (20.4, 21.05)):
        m = (ta >= cut) & (ta < back)
        if cut == 20.4:
            mus[m] *= np.clip(1 - (ta[m] - cut) / 0.03, 0, 1) * 0.0
    # хвост бита перед щелчком 20,4
    m = (ta >= 20.4 - 0.03) & (ta < 20.4)
    mus[m] *= np.linspace(1, 0, m.sum())
    return mus


def build_music_short(total):
    """Версия 9:16, 15 с: щелчок -> телефон -> очередь идёт -> слоган."""
    mus = np.zeros(int(total * SR))
    drums, lead, pad, bass, amb = (np.zeros(len(mus)) for _ in range(5))
    motifs = [[4, None, 5, 4, 3, None, 2, None], [2, 3, 4, None, 5, 4, 3, 2]]
    for b, bt in bars_in(0.0, 1.5):
        lead_bar(lead, bt, PENT_MIN, 62, motifs[b % 2], 0.4, 0.991, 0.5)
    put(pad, pad_note(midi(50), 1.6, 0.5), 0.0)
    put(pad, pad_note(midi(57), 1.6, 0.5), 0.0)
    # после щелчка 1,5: тревожный бас-тон 1,5–3,0
    t = t_axis(1.5)
    drone = (np.sin(2 * np.pi * midi(38) * t) + 0.4 * np.sin(2 * np.pi * midi(45) * t)) * np.minimum(1.0, t / 0.2) * np.minimum(1.0, (1.5 - t) / 0.3)
    put(amb, lp(drone, 700), 1.5, 0.5)
    motifs_w = [[4, 5, 7, 5, 4, None, 2, 4], [5, 4, 2, 4, 5, None, 7, 5], [7, 5, 4, 2, 4, 5, 4, None],
                [5, None, 7, 9, 7, 5, 4, 2]]
    for b, bt in bars_in(3.0, 11.0):
        lead_bar(lead, bt, PENT_MAJ, 65, motifs_w[b % 4], 0.5 if bt < 7 else 0.62, 0.993, 0.6)
    for b, bt in bars_in(3.0 - BAR, 11.4):
        root, ch = WARM_CH[b % 4]
        s = max(bt, 3.0)
        for n in ch:
            put(pad, pad_note(midi(n), min(bt + BAR, 11.4) - s + 0.4, 0.8), s)
    groove(drums, 4.8, 6.6, 0.7, hats=True, clap_=False)
    groove(drums, 6.6, 11.0, 1.0)
    for b, bt in bars_in(4.8 - BAR, 11.0):
        root, ch = WARM_CH[b % 4]
        for i in (0, 1.5, 2, 3.5):
            tt = bt + i * BEAT
            if 4.8 <= tt < 11.0:
                put(bass, bass_note(midi(root), 0.5), tt, 0.8)
    for i, d in enumerate([4, 5, 7, 9, 7, 5, 4, 2]):
        put(lead, ks_pluck(midi(pent(d, PENT_MAJ, 65)), 0.9, 0.6, 0.995), 11.0 + 0.6 + i * BEAT / 2, 0.55 * (1 - i * 0.07))
    for n in (53, 57, 60, 64):
        put(pad, pad_note(midi(n), 3.4, 0.9), 11.0)
    put(bass, bass_note(midi(41), 1.2), 11.0, 0.9)
    groove(drums, 11.0, 12.6, 0.9)
    drums = lp(drums, 9000)
    lead = lp(lead, 5500)
    mus = lead * 0.55 + pad * 0.55 + bass * 0.7 + drums * 0.7 + amb * 0.8
    ta = np.arange(len(mus)) / SR
    mus *= np.clip((14.0 - ta) / 1.5, 0, 1)
    m = (ta >= 1.5 - 0.03) & (ta < 1.5)
    mus[m] *= np.linspace(1, 0, m.sum())
    return mus


# ------------------------------------------------------------------ микс
def movavg(x, w):
    """Скользящее среднее через cumsum (быстро)."""
    w = max(1, int(w))
    c = np.cumsum(np.concatenate([np.zeros(w // 2 + 1), x, np.zeros(w // 2)]))
    return (c[w:w + len(x)] - c[:len(x)]) / w


def smooth_env(x, win=0.05, hold=0.8, sm=0.14):
    """Огибающая «голос говорит»: порог, удержание (паузы внутри фразы не поднимают музыку), сглаживание."""
    env = movavg(np.abs(x), win * SR)
    act = (env > 0.012).astype(np.float64)
    act = (movavg(act, hold * SR) * hold * SR > 0.5).astype(np.float64)
    return movavg(act, sm * SR)


def soft_limit(x, thr=0.62, top=0.95):
    ax = np.abs(x)
    over = ax > thr
    y = x.copy()
    y[over] = np.sign(x[over]) * (thr + (top - thr) * np.tanh((ax[over] - thr) / (top - thr)))
    return y


def mix(voice, sfx, music, with_voice, duck_depth=0.9, music_gain=0.15, sfx_gain=0.5):
    """Голос чётко (музыка и шумы под ним тише ~ на 12 дБ), мягкий лимитер, потолок -1,2 dBFS."""
    n = len(voice)
    sfx = sfx[:n] if len(sfx) >= n else np.pad(sfx, (0, n - len(sfx)))
    music = music[:n] if len(music) >= n else np.pad(music, (0, n - len(music)))
    r = rms(music[np.abs(music) > 0.003])
    music = music / max(r, 1e-6) * music_gain
    sfx = sfx * sfx_gain
    if with_voice:
        g = np.clip(smooth_env(voice) * 1.3, 0, 1)
        music = music * (1.0 - duck_depth * g)
        sfx = sfx * (1.0 - 0.5 * g)
    out = voice + sfx + music
    # общий уровень: громкость по активной речи (или по всему звуку, если голоса нет)
    ref = out[np.abs(voice) > 0.01] if with_voice and np.any(np.abs(voice) > 0.01) else out[np.abs(out) > 0.003]
    out = out * (0.14 / max(rms(ref), 1e-6))
    out = soft_limit(out)
    out *= 10 ** (-3.0 / 20) / max(np.abs(out).max(), 1e-6)  # 2026-10-29: потолок -3 dBFS (было -1,2): после AAC истинный пик выходил за 0 dBFS
    return out


def write_wav(path, mono):
    pcm = (np.clip(mono, -1, 1) * 32767).astype("<i2")
    st = np.repeat(pcm[:, None], 2, axis=1)
    with wave.open(path, "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(st.tobytes())


def process(tl, name, music_fn):
    cfg = tl[name]
    total = cfg["dur"]
    voice = np.zeros(int(total * SR))
    sections = []
    lines = cfg["voice"]
    for i, ln in enumerate(lines):
        a = tts_line(ln["who"], ln.get("tts") or ln["ru"])
        dur = len(a) / SR
        nxt = lines[i + 1]["at"] if i + 1 < len(lines) else total
        room = nxt - ln["at"] - 0.06
        if dur > room and "slow" not in ln:
            ratio = min(1.35, dur / room)
            if ratio > 1.02:
                a = atempo(a, ratio)
                dur = len(a) / SR
        ln["dur"] = round(dur, 3)
        put(voice, a, ln["at"])
        flag = "" if ln["at"] + dur <= nxt else "  <-- НАЛОЖЕНИЕ на следующую (до %.2f)" % nxt
        log("  %-4s %-3s at %5.2f  dur %4.2f -> %5.2f%s | %s" % (ln["id"], ln["who"], ln["at"], dur, ln["at"] + dur, flag, ln["ru"]))
    sp, mp = os.path.join(OUT, "sfx-%s.npy" % name), os.path.join(OUT, "music-%s.npy" % name)
    if REMIX and os.path.isfile(sp) and os.path.isfile(mp):   # --remix: звуки и музыку берём из прошлого прогона
        sfx, music = np.load(sp), np.load(mp)
    else:
        sfx = build_sfx(cfg["sfx"], total)
        music = music_fn(total)
        np.save(sp, sfx)
        np.save(mp, music)
    for lang, withv in (("ru", True), ("ky", False)):
        m = mix(voice if withv else np.zeros_like(voice), sfx, music, withv, music_gain=0.15 if withv else 0.2, sfx_gain=0.55 if withv else 0.7)
        p = os.path.join(OUT, "ad-svet-%s-%s.wav" % (name, lang))
        write_wav(p, m)
        log("  -> %s  пик %.2f dBFS  rms(речь+всё) %.1f dBFS" % (p, db(np.abs(m).max()), db(rms(m))))
    np.save(os.path.join(OUT, "voice-%s.npy" % name), voice)
    return cfg


REMIX = False


def main():
    global REMIX
    ap = argparse.ArgumentParser()
    ap.add_argument("--remix", action="store_true", help="не пересинтезировать SFX и музыку, только пересвести")
    ap.add_argument("--only", choices=["full", "short"])
    a = ap.parse_args()
    REMIX = a.remix
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(HERE, "timeline.json"), encoding="utf-8") as f:
        tl = json.load(f)
    for name, fn in (("full", build_music_full), ("short", build_music_short)):
        if a.only and a.only != name:
            continue
        log("== %s" % name)
        process(tl, name, fn)
    with open(os.path.join(OUT, "timeline.built.json"), "w", encoding="utf-8") as f:
        json.dump(tl, f, ensure_ascii=False, indent=1)
    log("timeline.built.json записан")


if __name__ == "__main__":
    main()
