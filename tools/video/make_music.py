#!/usr/bin/env python3
"""2026-10-29: простой лоуфай-бит кодом (numpy) — фоновая музыка для роликов без лицензий.
Всё синтезируется с нуля: электропиано, бас, кик/снейр/хэт, лёгкий шум пластинки. Автор — этот скрипт, лицензий нет.
Запуск:  python make_music.py [выходной.wav] [секунд] [bpm] [seed]
По умолчанию: <NM_VIDEO_HOME>/music/lofi.wav, ~49 с (16 тактов), 78 bpm. Файл можно зациклить."""
import os
import sys
import wave

import numpy as np

SR = 44100


def midi(n):
    return 440.0 * 2 ** ((n - 69) / 12.0)


def lowpass(x, cutoff):
    spec = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1.0 / SR)
    spec *= 1.0 / (1.0 + (f / cutoff) ** 4)
    return np.fft.irfft(spec, len(x))


def highpass(x, cutoff):
    return x - lowpass(x, cutoff)


def env_ad(n, a, d):
    t = np.arange(n) / SR
    return np.minimum(t / max(a, 1e-4), 1.0) * np.exp(-t / d)


def epiano(freq, dur, vel=1.0):
    n = int(SR * dur)
    t = np.arange(n) / SR
    # электропиано: основной тон + мягкий «колокольчик» 2-й/3-й гармоник, лёгкая расстройка
    s = (np.sin(2 * np.pi * freq * t) + 0.35 * np.sin(2 * np.pi * freq * 2.003 * t) * np.exp(-t / 0.25)
         + 0.12 * np.sin(2 * np.pi * freq * 3.01 * t) * np.exp(-t / 0.12))
    s += 0.5 * np.sin(2 * np.pi * freq * 1.004 * t)
    e = env_ad(n, 0.008, dur * 0.55)
    tail = np.minimum(1.0, (dur - t) / 0.08)
    return s * e * tail * vel


def bass(freq, dur):
    n = int(SR * dur)
    t = np.arange(n) / SR
    s = np.sin(2 * np.pi * freq * t) + 0.25 * np.sin(2 * np.pi * freq * 2 * t)
    return s * env_ad(n, 0.01, dur * 0.7) * np.minimum(1.0, (dur - t) / 0.05)


def kick(dur=0.35):
    n = int(SR * dur)
    t = np.arange(n) / SR
    f = 110 * np.exp(-t * 22) + 42
    ph = 2 * np.pi * np.cumsum(f) / SR
    return np.sin(ph) * np.exp(-t / 0.12)


def snare(rng, dur=0.25):
    n = int(SR * dur)
    t = np.arange(n) / SR
    noise = highpass(rng.standard_normal(n), 1500)
    tone = np.sin(2 * np.pi * 190 * t) * np.exp(-t / 0.05)
    return (noise * np.exp(-t / 0.07) * 0.7 + tone * 0.5)


def hat(rng, dur=0.08):
    n = int(SR * dur)
    t = np.arange(n) / SR
    return highpass(rng.standard_normal(n), 7000) * np.exp(-t / 0.018)


# Am9 - Dm9 - G13 - Cmaj9 (MIDI), по такту каждый
PROG = [
    (45, [57, 60, 64, 67, 71]),   # Am9
    (50, [53, 57, 60, 64, 69]),   # Dm9
    (43, [55, 59, 62, 65, 69]),   # G13
    (48, [55, 59, 62, 64, 67]),   # Cmaj9
]


def build(bpm=78, bars=16, seed=7):
    rng = np.random.default_rng(seed)
    beat = 60.0 / bpm
    bar = 4 * beat
    total = int(SR * (bars * bar + 1.0))
    out = np.zeros(total)
    drums = np.zeros(total)

    def put(buf, sig, at):
        i = int(at * SR)
        j = min(total, i + len(sig))
        if i < total:
            buf[i:j] += sig[: j - i]

    k, sn = kick(), snare(rng)
    for b in range(bars):
        root, chord = PROG[b % 4]
        t0 = b * bar
        # аккорды: «медленный» арпеджио-комп с небольшим человеческим разбросом по времени
        for ci, note in enumerate(chord):
            put(out, epiano(midi(note), bar * 0.92 - ci * 0.03, 0.22), t0 + ci * 0.018 + rng.uniform(0, 0.01))
        # повтор аккорда на «и» третьей доли (синкопа)
        for ci, note in enumerate(chord[1:4]):
            put(out, epiano(midi(note), beat * 1.2, 0.16), t0 + 2.5 * beat + ci * 0.012)
        # бас: корень на 1 и «и» 3
        put(out, bass(midi(root), beat * 1.6) * 0.9, t0)
        put(out, bass(midi(root + (7 if b % 2 else 0)), beat * 1.0) * 0.6, t0 + 2.5 * beat)
        # ударные со свингом
        put(drums, k * 0.9, t0)
        put(drums, k * 0.7, t0 + 2.5 * beat + 0.02)
        put(drums, sn * 0.55, t0 + 1 * beat + 0.01)
        put(drums, sn * 0.55, t0 + 3 * beat + 0.01)
        for e in range(8):
            sw = 0.16 * beat if e % 2 else 0.0
            put(drums, hat(rng) * (0.22 if e % 2 else 0.32) * rng.uniform(0.8, 1.1), t0 + e * beat / 2 + sw)

    mix = lowpass(out, 2800) * 0.9 + lowpass(drums, 6500) * 0.8
    # шум пластинки + редкие щелчки
    crackle = np.zeros(total)
    idx = rng.integers(0, total, size=int(total / SR * 6))
    crackle[idx] = rng.uniform(-1, 1, size=len(idx))
    mix += lowpass(rng.standard_normal(total), 3000) * 0.004 + highpass(crackle, 800) * 0.03
    # мягкий «тёплый» компрессор
    mix = np.tanh(mix * 1.4) / np.tanh(1.4)
    # стерео: слегка разные фазы эха
    d = int(0.19 * SR)
    left = mix + 0.18 * np.roll(mix, d)
    right = mix + 0.18 * np.roll(mix, int(d * 1.31))
    st = np.stack([left, right], axis=1)
    n_fade = int(0.05 * SR)
    st[:n_fade] *= np.linspace(0, 1, n_fade)[:, None]
    st[-n_fade:] *= np.linspace(1, 0, n_fade)[:, None]
    st = st[: int(SR * bars * bar)]  # ровно N тактов — удобно зациклить
    st *= 0.89 / max(np.abs(st).max(), 1e-6)
    return st


def write_wav(path, st):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    pcm = (np.clip(st, -1, 1) * 32767).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())


def default_path():
    home = os.environ.get("NM_VIDEO_HOME", r"C:\Users\User\NurMarket-video")
    return os.path.join(home, "music", "lofi.wav")


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else default_path()
    bpm = float(sys.argv[3]) if len(sys.argv) > 3 else 78
    bars = 16
    if len(sys.argv) > 2:
        bars = max(4, int(round(float(sys.argv[2]) / (240.0 / bpm))))
    seed = int(sys.argv[4]) if len(sys.argv) > 4 else 7
    write_wav(out, build(bpm, bars, seed))
    print("OK", out)
