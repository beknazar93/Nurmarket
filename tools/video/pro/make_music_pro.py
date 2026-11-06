#!/usr/bin/env python3
"""2026-10-30: музыка профессионального стиля роликов NurCRM (гид, раздел 3): минималистичный electronic/ambient pulse, синтез numpy.
Новый трек (не lofi.wav, не комуз, без перкуссии/хлопков/шейкеров): 96 BPM, ре-минор (Dm–Bb–F–C).
Слои: (1) низкий пэд — пила через ФНЧ 400–900 Гц с медленным LFO; (2) пульс восьмыми — короткий синус/треугольник 10/120 мс
с лёгким сайдчейном от доли; (3) редкий мягкий плак (FM) в верхнем регистре, 2–4 ноты на такт; (4) низкая «доля» на каждую
четверть — тон 57 Гц, огибающая 5/180 мс (без семплов ударных).
Структура: пэд до pulse_in -> пульс входит с первым экраном -> подъём (октава плака) на отрезке lift -> на финале остаётся только
пэд, последний удар на логотипе (final + 0,25 с), затем 0,8 с тишины.
API:  build(total, pulse_in, final_t, lift=None, bpm=96.0) -> float32 стерео (n,2), «полная» громкость (нормировка — в make_pro).
Запуск для проверки:  python make_music_pro.py 30 [pulse_in] [final] -> NurMarket-video/ad/pro/_work/music-test.wav
"""
import os
import sys
import wave

HOME = os.environ.get("NM_VIDEO_HOME", r"C:\Users\User\NurMarket-video")
sys.path.insert(0, os.path.join(HOME, "pylib"))
import numpy as np  # noqa: E402

SR = 48000
CHORDS = [  # (бас-тон, ноты пэда) в ре-миноре: Dm, Bb, F, C
    (38, [50, 57, 62, 65]),
    (34, [46, 53, 58, 62]),
    (41, [53, 57, 60, 65]),
    (36, [48, 55, 60, 64]),
]
PLUCK = [  # ноты плака по тактам (полутона относительно D5=74), None — пауза; позиции — в восьмых (0..7)
    [(1, 0), (4, 7), (6, 3)],
    [(0, 5), (3, 0), (6, 7)],
    [(1, 3), (4, 5), (7, 0)],
    [(0, 7), (3, 3), (5, 0), (7, 10)],
]
D_MIN = {0: 0, 3: 3, 5: 5, 7: 7, 10: 10}


def midi(n):
    return 440.0 * 2 ** ((n - 69) / 12.0)


def lp_fft(x, fc, order=3):
    n = len(x)
    sp = np.fft.rfft(x)
    f = np.fft.rfftfreq(n, 1.0 / SR)
    return np.fft.irfft(sp / (1.0 + (f / fc) ** (2 * order)), n)


def hp_fft(x, fc, order=2):
    n = len(x)
    sp = np.fft.rfft(x)
    f = np.fft.rfftfreq(n, 1.0 / SR)
    return np.fft.irfft(sp * (1.0 - 1.0 / (1.0 + (f / fc) ** (2 * order))), n)


def put(buf, sig, at, gain=1.0):
    i = int(round(at * SR))
    if i >= len(buf) or i + len(sig) <= 0:
        return
    a = max(0, -i)
    e = min(len(sig), len(buf) - i)
    buf[i + a:i + e] += sig[a:e] * gain


def saw_pad_note(f0, dur, detune_cents, nh=18):
    """Пила из гармоник (ограничена, без алиасинга) с медленной атакой/спадом."""
    n = int(dur * SR)
    t = np.arange(n) / SR
    out = np.zeros(n)
    f = f0 * 2 ** (detune_cents / 1200.0)
    for h in range(1, nh + 1):
        if f * h > 5000:
            break
        out += np.sin(2 * np.pi * f * h * t) / h
    a, r = int(0.7 * SR), int(0.9 * SR)
    env = np.ones(n)
    env[:min(a, n)] = np.linspace(0, 1, min(a, n)) ** 2
    if n > r:
        env[-r:] *= np.linspace(1, 0, r) ** 1.5
    return out * env


def pluck_fm(f, dur=0.9, idx=2.4):
    n = int(dur * SR)
    t = np.arange(n) / SR
    env_i = idx * np.exp(-t * 5.0)
    mod = np.sin(2 * np.pi * f * 2.0 * t) * env_i
    car = np.sin(2 * np.pi * f * t + mod)
    a = int(0.004 * SR)
    env = np.exp(-t * 4.2)
    env[:a] *= np.linspace(0, 1, a)
    return car * env


def pulse_tone(f, tri=False):
    n = int(0.22 * SR)
    t = np.arange(n) / SR
    ph = 2 * np.pi * f * t
    sig = (2 / np.pi) * np.arcsin(np.sin(ph)) if tri else np.sin(ph)
    a, d = 0.010, 0.120
    env = np.minimum(t / a, 1.0) * np.exp(-np.maximum(t - a, 0) / (d / 4.0))
    return sig * env


def sub_beat():
    n = int(0.36 * SR)
    t = np.arange(n) / SR
    a, d = 0.005, 0.18
    env = np.minimum(t / a, 1.0) * np.exp(-np.maximum(t - a, 0) / (d / 3.0))
    # лёгкое падение высоты (мягкий «бум» без щелчка): 62 -> 57 Гц
    f = 57.0 + 5.0 * np.exp(-t * 18.0)
    ph = 2 * np.pi * np.cumsum(f) / SR
    return np.sin(ph) * env


def build(total, pulse_in, final_t, lift=None, bpm=96.0, seed=7):
    beat = 60.0 / bpm
    bar = 4 * beat
    n = int(total * SR)
    padL, padR = np.zeros(n), np.zeros(n)
    pulse, pluck, sub = np.zeros(n), np.zeros(n), np.zeros(n)
    quarter_ts = []
    # ---- пэд: такты от t=0 (аккорд меняется каждые 2 такта от якоря pulse_in)
    t_bar = pulse_in - bar * int(np.ceil(pulse_in / bar))  # начало такта на сетке якоря (может быть <0)
    bars = []
    tb = t_bar
    while tb < total:
        bars.append(tb)
        tb += bar
    for bi, tb in enumerate(bars):
        nb = int(round((tb - pulse_in) / bar))
        if nb % 2 != 0:
            continue
        root, notes = CHORDS[(nb // 2) % 4]
        d = 2 * bar + 1.0
        for nn in notes[:3]:
            for side, cents in ((padL, -7), (padR, 6)):
                put(side, saw_pad_note(midi(nn), d, cents), tb, 0.55)
        put(padL, saw_pad_note(midi(root), d, -3, 10), tb, 0.8)
        put(padR, saw_pad_note(midi(root), d, 3, 10), tb, 0.8)
    # ФНЧ пэда 400–900 Гц с медленным LFO (смесь двух фильтраций)
    t = np.arange(n) / SR
    lfo = 0.5 + 0.5 * np.sin(2 * np.pi * t / 9.0 - 1.2)
    padL = lp_fft(padL, 420) * (1 - lfo) + lp_fft(padL, 900) * lfo
    padR = lp_fft(padR, 420) * (1 - lfo) + lp_fft(padR, 900) * lfo
    # ---- пульс, плак, доля: от pulse_in до final_t
    t0 = pulse_in
    k = 0
    beats_total = int((final_t - t0) / beat)
    for q in range(beats_total):
        tq = t0 + q * beat
        quarter_ts.append(tq)
        put(sub, sub_beat(), tq, 0.9)
    for e in range(beats_total * 2):
        te = t0 + e * beat / 2
        if te >= final_t - 0.05:
            break
        barn = int((te - t0) // bar)
        root, notes = CHORDS[(barn // 2) % 4]
        arp = [notes[1], notes[2], notes[3], notes[2]]
        f = midi(arp[e % 4] + 12)
        put(pulse, pulse_tone(f, tri=(e % 2 == 1)), te, 0.5 if e % 2 == 0 else 0.34)
    for barn in range(int((final_t - t0) // bar) + 1):
        tb = t0 + barn * bar
        for pos, semi in PLUCK[barn % 4]:
            tp = tb + pos * beat / 2
            if tp >= final_t - 0.2:
                continue
            f = midi(74 + semi)
            g = 0.28
            put(pluck, pluck_fm(f), tp, g)
            if lift and lift[0] <= tp < lift[1]:  # подъём: октава плака
                put(pluck, pluck_fm(f * 2, 0.7, 1.8), tp, g * 0.7)
    # сайдчейн от доли: притушить пэд/пульс на 0.25 с после каждой четверти
    duck = np.ones(n)
    env = 1 - 0.30 * np.exp(-np.arange(int(0.45 * SR)) / SR / 0.10)
    for tq in quarter_ts:
        i = int(tq * SR)
        if i < n:
            e = min(len(env), n - i)
            duck[i:i + e] = np.minimum(duck[i:i + e], env[:e])
    pad_g = np.ones(n)
    # финал: остановка всего, кроме пэда; последний удар на логотипе
    L = padL * duck * 0.9 + pulse * duck + pluck * 0.9 + sub
    R = padR * duck * 0.9 + pulse * duck + pluck * 0.9 + sub
    tl = final_t + 0.25
    hit = np.zeros(n)
    put(hit, sub_beat(), tl, 1.0)
    for nn, g in ((62, 0.5), (65, 0.4), (69, 0.4), (74, 0.5)):
        put(hit, pluck_fm(midi(nn), 1.6, 1.8), tl, g * 0.5)
    L += hit
    R += hit
    # пэд моложе: вход 0.8 с, на финале приглушается, хвост затухает; последние 0.8 с — тишина
    gate = np.ones(n)
    fin = int(final_t * SR)
    gate[:int(0.8 * SR)] = np.linspace(0, 1, int(0.8 * SR))
    tail0, tail1 = int((total - 1.6) * SR), int((total - 0.8) * SR)
    if tail1 > tail0 and tail0 > 0:
        gate[tail0:tail1] = np.linspace(1, 0, tail1 - tail0)
        gate[tail1:] = 0
    L, R = L * gate, R * gate
    out = np.stack([hp_fft(L, 30), hp_fft(R, 30)], axis=1)
    return out.astype(np.float32)


def write_wav(path, st):
    pcm = (np.clip(st, -1, 1) * 32767).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())


if __name__ == "__main__":
    total = float(sys.argv[1]) if len(sys.argv) > 1 else 30.0
    pin = float(sys.argv[2]) if len(sys.argv) > 2 else 1.0
    fin = float(sys.argv[3]) if len(sys.argv) > 3 else total - 4.0
    m = build(total, pin, fin, lift=(total * 0.6, fin - 2))
    m = m / (np.abs(m).max() + 1e-9) * 0.8
    out = os.path.join(HOME, "ad", "pro", "_work")
    os.makedirs(out, exist_ok=True)
    write_wav(os.path.join(out, "music-test.wav"), m)
    print("ok", os.path.join(out, "music-test.wav"))
