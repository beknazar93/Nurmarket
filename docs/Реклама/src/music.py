# -*- coding: utf-8 -*-
"""Лёгкая фоновая мелодия, синтезируется кодом (синусы, треугольник, шум) — своя, без чужой музыки и авторских прав.
make(путь.wav, секунды): 112 ударов/мин, до мажор, аккорды C - G - Am - F, тихий бас, арпеджио, мягкий бочок и шейкер."""
import math, random, struct, wave

SR = 44100
BPM = 112


def hz(midi):
    return 440.0 * 2 ** ((midi - 69) / 12)


def make(path, seconds):
    n = int(seconds * SR)
    buf = [0.0] * n
    beat = 60.0 / BPM
    random.seed(11)
    chords = [(48, [60, 64, 67, 72]), (43, [59, 62, 67, 71]), (45, [57, 60, 64, 69]), (41, [57, 60, 65, 69])]  # бас + арпеджио
    step = beat / 2  # восьмые
    k = 0
    while k * step < seconds:
        t0 = k * step
        bar = int(k // 8) % 4  # аккорд меняется раз в 2 такта (8 восьмых = 1 такт 4/4 -> 2 такта = 16 восьмых)
        bar = int(k // 16) % 4
        bass, arp = chords[bar]
        i0 = int(t0 * SR)
        # арпеджио-пэлл (pluck)
        note = arp[(k % 8) if (k % 8) < 4 else 7 - (k % 8)] + (12 if k % 16 >= 8 else 0)
        f = hz(note)
        L = int(0.35 * SR)
        for j in range(L):
            if i0 + j >= n:
                break
            e = math.exp(-j / (0.09 * SR))
            buf[i0 + j] += 0.075 * e * (math.sin(2 * math.pi * f * j / SR) + 0.35 * math.sin(4 * math.pi * f * j / SR))
        # бас на сильные доли
        if k % 2 == 0:
            f = hz(bass - 12 if k % 4 == 0 else bass)
            L = int(0.42 * SR)
            for j in range(L):
                if i0 + j >= n:
                    break
                e = math.exp(-j / (0.2 * SR))
                ph = (f * j / SR) % 1.0
                tri = 4 * abs(ph - 0.5) - 1
                buf[i0 + j] += 0.16 * e * tri
        # мягкий бочок на 1 и 3 доли
        if k % 4 == 0:
            L = int(0.18 * SR)
            for j in range(L):
                if i0 + j >= n:
                    break
                tt = j / SR
                f = 50 + 90 * math.exp(-tt * 28)
                buf[i0 + j] += 0.22 * math.exp(-tt * 16) * math.sin(2 * math.pi * f * tt)
        # шейкер на слабые восьмые
        if k % 2 == 1:
            L = int(0.05 * SR)
            for j in range(L):
                if i0 + j >= n:
                    break
                buf[i0 + j] += 0.035 * math.exp(-j / (0.012 * SR)) * (random.random() * 2 - 1)
        k += 1
    # пэд: длинный тихий аккорд
    for c in range(int(seconds // (16 * step)) + 1):
        bass, arp = chords[c % 4]
        s0 = int(c * 16 * step * SR)
        L = int(16 * step * SR)
        for note in arp[:3]:
            f = hz(note - 12)
            for j in range(0, L):
                if s0 + j >= n:
                    break
                env = min(1, j / (0.5 * SR)) * min(1, (L - j) / (0.5 * SR))
                buf[s0 + j] += 0.02 * env * math.sin(2 * math.pi * f * j / SR)
    peak = max(abs(x) for x in buf) or 1
    g = 0.5 / peak  # тихо: фон не должен заглушать картинку
    with wave.open(str(path), 'wb') as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(b''.join(struct.pack('<hh', int(x * g * 32767), int(x * g * 32767)) for x in buf))


if __name__ == '__main__':
    make('music_test.wav', 8)
