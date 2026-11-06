#!/usr/bin/env python3
"""2026-10-29: конвейер роликов NurMarket: JSON-сценарий -> mp4 (H.264 + AAC, yuv420p).

Запуск:
  python tools/video/build_video.py сценарий.json -o готовый.mp4 [--format landscape|portrait|both]
                                   [--draft] [--no-music] [--snap папка] [--probe файл.mp4]
  landscape = 1920x1080 (файл как указан), portrait = 1080x1920 (файл <имя>-vertical.mp4), 30 fps.
  --draft  половинный размер и 15 fps (быстро проверить сценарий).   --snap  только кадры-PNG сцен для просмотра.

Как работает: озвучка (edge-tts, кэш) задаёт длительность сцены -> HTML-шаблон рисуется кадр за кадром в Chromium
(Playwright; время t подаёт сборщик: все WAAPI/CSS-анимации ставятся на паузу и получают currentTime = t, шаблоны
считают состояние из t в window.__render(t)) -> кадры идут в ffmpeg -> звук (голос + лоуфай с «приглушением» под голос).
Субтитры вшиваются в кадр (шрифт Montserrat + Noto Sans для ү ө ң).

Формат сценария (JSON, пути — относительно файла сценария):
{ "name": "test-ru", "lang": "ru", "voice": "female|male|none|ru-RU-SvetlanaNeural", "rate": "+0%",
  "music": true | false | "путь.wav", "music_volume": 0.14, "fps": 30, "logo": "logo.png",
  "premix": "готовый-микс.wav",   # необязательно: вместо озвучки и музыки взять готовый WAV 48 кГц стерео (см. tools/video/ad_svet)
  "scenes": [
    { "id": "intro", "template": "intro", "title": "...", "subtitle": "...", "tag": "...",   # свободные поля = параметры шаблона
      "text": "текст озвучки", "subs": null|false|"свой текст"|[{"text":"..","at":0.5,"dur":2}],
      "duration": 4,            # МИНИМУМ, сек; реально = max(duration, озвучка + lead + tail)
      "lead": 0.4, "tail": 0.6, # пауза до голоса и после
      "voice": "male", "lang": "uz", "audio": "готовая-озвучка.wav",   # переопределения на сцену
      "transition": "fade|slide|wipe|zoom|none" },                      # вход сцены, 0.5 с
    { "template": "screenshot", "image": "shot.png", "steps": [ {"type":"zoom|highlight|cursor|title", ...} ] },
    { "template": "titlecard", "kicker": "1", "title": "..", "bullets": ["..",".."] },
    { "template": "slides", "images": ["a.png","b.png"] },
    { "template": "outro", "title": "..", "cta": "..", "url": "..", "note": ".." },
    { "html": "моя-сцена.html" }     # своя страница: подключите nm.js, определите window.__render(t)
  ] }
Языки: ru/uz — голос edge-tts; ky — голоса в edge-tts нет: режим «субтитры без голоса» (длительность по скорости чтения),
либо готовая озвучка в поле "audio", либо "voice": "clone" (2026-10-29: локальный MMS-TTS + kNN-VC голосом образца
владельца, tools/video/ky_voice.py; нужен venv в NurMarket-video/kyvoice; необязательные поля "ref": "образец.wav" и
"engine": "knnvc"|"mms" — mms = чистый MMS-TTS без смены тембра; короче: "voice": "mms").  Время шагов at/dur: секунды или "40%" длительности сцены.
"""
import argparse
import asyncio
import hashlib
import json
import math
import os
import re
import subprocess
import sys
import time
import wave

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

HOME = os.environ.get("NM_VIDEO_HOME", r"C:\Users\User\NurMarket-video")
HERE = os.path.dirname(os.path.abspath(__file__))
for _p in (os.path.join(HOME, "pylib"),):
    if os.path.isdir(_p) and _p not in sys.path:
        sys.path.insert(0, _p)
sys.path.insert(0, HERE)

import numpy as np  # noqa: E402

SR = 48000
TEMPLATES = os.path.join(HERE, "templates")
CACHE = os.path.join(HOME, "cache")
REPO_FONTS = [
    os.path.normpath(os.path.join(HERE, "..", "..", "NurMarketKassa.Avalonia", "NurMarketKassa.Avalonia", "Assets", "Fonts")),
    os.path.normpath(os.path.join(HERE, "..", "..", "NurMarketMobile", "assets", "fonts")),
]
RESERVED = {"id", "template", "html", "text", "subs", "voice", "lang", "audio", "duration", "tail", "lead",
            "transition", "params", "rate", "engine", "ref", "speed"}
VOICES = {
    "ru": {"female": "ru-RU-SvetlanaNeural", "male": "ru-RU-DmitryNeural"},
    "uz": {"female": "uz-UZ-MadinaNeural", "male": "uz-UZ-SardorNeural"},
    "en": {"female": "en-US-AriaNeural", "male": "en-US-GuyNeural"},
    "tr": {"female": "tr-TR-EmelNeural", "male": "tr-TR-AhmetNeural"},
    "ky": {},  # в edge-tts кыргызского голоса нет -> субтитры без голоса
}
FORMATS = {"landscape": (1920, 1080), "portrait": (1080, 1920)}
READ_CPS = 13.0  # скорость чтения субтитров без голоса, символов в секунду


def log(*a):
    print(*a, flush=True)


# ---------------------------------------------------------------- ffmpeg
def find_ffmpeg():
    cands = [os.environ.get("NM_FFMPEG")]
    try:
        cands.append(open(os.path.join(HOME, "ffmpeg-path.txt"), encoding="utf-8").read().strip())
    except OSError:
        pass
    try:
        import imageio_ffmpeg
        cands.append(imageio_ffmpeg.get_ffmpeg_exe())
    except Exception:
        pass
    for c in cands:
        if c and os.path.isfile(c):
            return c
    return "ffmpeg"


FFMPEG = find_ffmpeg()


def decode_audio(path):
    """Любой аудиофайл -> float32 mono 48 кГц."""
    r = subprocess.run([FFMPEG, "-v", "error", "-i", path, "-f", "f32le", "-ac", "1", "-ar", str(SR), "-"],
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if r.returncode != 0:
        raise RuntimeError("ffmpeg не прочитал звук %s: %s" % (path, r.stderr.decode("utf-8", "replace")[:300]))
    return np.frombuffer(r.stdout, dtype="<f4").copy()


def probe(path):
    """Вместо ffprobe (его нет в imageio-ffmpeg): разбор `ffmpeg -i` + полное декодирование."""
    r = subprocess.run([FFMPEG, "-hide_banner", "-i", path], stderr=subprocess.PIPE, stdout=subprocess.PIPE)
    info = r.stderr.decode("utf-8", "replace")
    d = subprocess.run([FFMPEG, "-v", "error", "-i", path, "-f", "null", "-"], stderr=subprocess.PIPE)
    out = [l.strip() for l in info.splitlines() if re.search(r"Duration|Stream #", l)]
    out.append("декодирование: " + ("OK, ошибок нет" if not d.stderr.strip() else "ОШИБКИ: " + d.stderr.decode("utf-8", "replace")[:300]))
    out.append("размер: %.2f МБ" % (os.path.getsize(path) / 1048576))
    return "\n".join(out)


# ---------------------------------------------------------------- озвучка
def resolve_voice(scene, scenario, lang):
    v = scene.get("voice", scenario.get("voice", "female"))
    if v in (None, False, "none", "off"):
        return None
    if v in ("female", "male"):
        return VOICES.get(lang, {}).get(v)  # для ky -> None (нет голоса)
    if v in ("clone", "mms"):  # 2026-10-29: "mms" = чистый MMS-TTS без смены тембра (то же, что clone + "engine":"mms")
        return "clone" if lang == "ky" else VOICES.get(lang, {}).get("male")  # клон только для ky
    return v


def synth_clone(text, ref=None, engine="knnvc", speed=1.0):
    """2026-10-29: кыргызский голос (ky_voice.py в venv, кэш там же) -> float32 mono 48 кГц."""
    sys.path.insert(0, HERE)
    import ky_voice
    os.makedirs(CACHE, exist_ok=True)
    out = _clone_out(text, ref, engine, speed)
    if not os.path.isfile(out):
        ky_voice.synth_to_file(text, out, ref, engine, speed)
    return decode_audio(out)


async def _tts_one(text, voice, rate, path):
    import edge_tts
    last = None
    for attempt in range(10):  # 2026-10-29: сервис иногда отвечает «No audio was received» на отдельные фразы — повторяем дольше
        try:
            await edge_tts.Communicate(text, voice, rate=rate).save(path)
            return
        except Exception as e:  # сеть/сервис
            last = e
            await asyncio.sleep(2.0 + 1.5 * attempt)
    raise RuntimeError("edge-tts не ответил (%s): %s" % (voice, last))


def synth(text, voice, rate):
    """Текст -> float32 mono 48 кГц, с кэшем по (голос, скорость, текст)."""
    os.makedirs(CACHE, exist_ok=True)
    key = hashlib.sha1(("%s|%s|%s" % (voice, rate, text)).encode("utf-8")).hexdigest()[:20]
    wav = os.path.join(CACHE, "tts-%s.f32" % key)
    if os.path.isfile(wav):
        return np.fromfile(wav, dtype="<f4")
    mp3 = os.path.join(CACHE, "tts-%s.mp3" % key)
    asyncio.run(_tts_one(text, voice, rate, mp3))
    a = decode_audio(mp3)
    a.astype("<f4").tofile(wav)
    return a


def trim_silence(a, thr=0.004, keep=0.08):
    idx = np.where(np.abs(a) > thr)[0]
    if len(idx) == 0:
        return a
    s, e = max(0, idx[0] - int(keep * SR)), min(len(a), idx[-1] + int(keep * SR))
    return a[s:e]


# ---------------------------------------------------------------- субтитры
def split_subs(text, maxc):
    text = re.sub(r"\s+", " ", text or "").strip()
    if not text:
        return []
    sents = re.split(r"(?<=[.!?…])\s+", text)
    out = []
    for s in sents:
        if len(s) <= maxc:
            out.append(s)
            continue
        parts = re.split(r"(?<=[,;:—–])\s+", s)
        cur = ""
        for p in parts:
            if cur and len(cur) + 1 + len(p) > maxc:
                out.append(cur)
                cur = p
            else:
                cur = (cur + " " + p).strip()
        if cur:
            out.append(cur)
    final = []
    for s in out:  # слишком длинный кусок без запятых — режем по словам поровну
        if len(s) <= maxc:
            final.append(s)
            continue
        n = math.ceil(len(s) / maxc)
        words, per, cur = s.split(" "), math.ceil(len(s) / n), ""
        for w in words:
            if cur and len(cur) + 1 + len(w) > per:
                final.append(cur)
                cur = w
            else:
                cur = (cur + " " + w).strip()
        if cur:
            final.append(cur)
    return final


def plan_subs(scene, text, lead, voice_dur, maxc):
    """-> [(start, end, text)] в секундах сцены."""
    subs = scene.get("subs")
    if subs is False:
        return []
    if isinstance(subs, list):
        return [(float(x["at"]), float(x["at"]) + float(x["dur"]), x["text"]) for x in subs]
    src = subs if isinstance(subs, str) else text
    chunks = split_subs(src, maxc)
    if not chunks:
        return []
    w = [len(c) + 6 for c in chunks]
    tot, t, res = float(sum(w)), lead, []
    for c, wi in zip(chunks, w):
        d = voice_dur * wi / tot
        res.append((t, t + d, c))
        t += d
    return res


# ---------------------------------------------------------------- сценарий
def load_scenario(path):
    with open(path, encoding="utf-8") as f:
        sc = json.load(f)
    base = os.path.dirname(os.path.abspath(path))

    def rp(p):
        if not isinstance(p, str) or re.match(r"^(file|https?|data):", p, re.I) or os.path.isabs(p):
            return p
        return os.path.normpath(os.path.join(base, p))

    def fix(obj, key_is_path=False):
        if isinstance(obj, dict):
            return {k: fix(v, k in ("image", "images", "html", "audio", "logo", "music", "premix")) for k, v in obj.items()}
        if isinstance(obj, list):
            return [fix(v, key_is_path) for v in obj]
        return rp(obj) if key_is_path and isinstance(obj, str) else obj

    sc = fix(sc)
    if not sc.get("scenes"):
        raise SystemExit("В сценарии нет сцен (scenes)")
    return sc


def scene_page(scene):
    if scene.get("html"):
        return scene["html"]
    tpl = scene.get("template")
    if not tpl:
        tpl = "screenshot" if scene.get("image") else "slides" if scene.get("images") else "titlecard"
    p = os.path.join(TEMPLATES, tpl + ".html")
    if not os.path.isfile(p):
        raise SystemExit("Нет шаблона %s (есть: %s)" % (tpl, ", ".join(sorted(f[:-5] for f in os.listdir(TEMPLATES) if f.endswith(".html")))))
    return p


def _clone_engine(sc, scenario):
    eng = sc.get("engine", scenario.get("engine"))
    if eng is None:
        eng = "mms" if sc.get("voice", scenario.get("voice")) == "mms" else "knnvc"
    if eng not in ("knnvc", "mms"):
        raise SystemExit("engine должен быть knnvc или mms, а не %r" % eng)
    return eng


def _clone_out(text, ref, eng, speed):
    key = text + "|" + str(ref) + "|" + eng + ("|%s" % speed if speed != 1.0 else "")
    return os.path.join(CACHE, "kyclone-%s.wav" % hashlib.sha1(key.encode("utf-8")).hexdigest()[:20])


def prefetch_clone(scenario):
    """2026-10-29: все кыргызские реплики синтезируем одним запуском venv (модели грузятся 1 раз)."""
    items = []
    for sc in scenario["scenes"]:
        lang = sc.get("lang", scenario.get("lang", "ru"))
        text = (sc.get("text") or "").strip()
        if text and not sc.get("audio") and resolve_voice(sc, scenario, lang) == "clone":
            ref, eng = sc.get("ref", scenario.get("ref")), _clone_engine(sc, scenario)
            sp = float(sc.get("speed", scenario.get("speed", 1.0)))
            out = _clone_out(text, ref, eng, sp)
            if not os.path.isfile(out):
                items.append({"text": text, "out": out, "engine": eng, "ref": ref, "speed": sp})
    if items:
        sys.path.insert(0, HERE)
        import ky_voice
        os.makedirs(CACHE, exist_ok=True)
        log("Озвучка ky: синтез %d реплик одним запуском..." % len(items))
        log(ky_voice.synth_batch_to_files(items))


def plan(scenario, fps, maxc_by_fmt):
    """Озвучка + тайминги всех сцен (не зависят от формата кадра, кроме субтитров)."""
    prefetch_clone(scenario)
    scenes, t0 = [], 0.0
    for i, sc in enumerate(scenario["scenes"]):
        lang = sc.get("lang", scenario.get("lang", "ru"))
        text = (sc.get("text") or "").strip()
        lead = float(sc.get("lead", 0.4 if text else 0.0))
        tail = float(sc.get("tail", 0.6 if text else 0.0))
        voice, audio, mode = resolve_voice(sc, scenario, lang), None, "none"
        if sc.get("audio"):
            audio, mode = trim_silence(decode_audio(sc["audio"])), "file"
        elif text and voice == "clone":
            audio, mode = trim_silence(synth_clone(text, sc.get("ref", scenario.get("ref")), _clone_engine(sc, scenario),
                                                   float(sc.get("speed", scenario.get("speed", 1.0))))), "clone"
        elif text and voice:
            audio, mode = trim_silence(synth(text, voice, sc.get("rate", scenario.get("rate", "+0%")))), "tts"
        elif text:
            mode = "silent"  # нет голоса (кыргызский): субтитры без озвучки
        vdur = len(audio) / SR if audio is not None else (len(text) / READ_CPS if text else 0.0)
        dmin = float(sc.get("duration", 3.0))
        dur = max(dmin, lead + vdur + tail)
        dur = math.ceil(dur * fps) / fps  # ровно по кадрам
        sid = sc.get("id") or "s%d" % (i + 1)
        scenes.append({
            "i": i, "id": sid, "raw": sc, "lang": lang, "text": text, "audio": audio, "mode": mode, "voice": voice,
            "lead": lead, "vdur": vdur, "dur": dur, "start": t0, "page": scene_page(sc),
            "transition": sc.get("transition", "none" if i == 0 else "fade"),
            "subs": {k: plan_subs(sc, text, lead, vdur, m) for k, m in maxc_by_fmt.items()},
            "params": dict({k: v for k, v in sc.items() if k not in RESERVED}, **(sc.get("params") or {})),
        })
        log("  сцена %-12s %s голос=%s озвучка=%.1fс -> %.2fс" % (sid, mode, voice or "-", vdur if mode != "silent" else 0, dur))
        t0 += dur
    return scenes, t0


# ---------------------------------------------------------------- звук
def music_track(spec, total, volume):
    """Лоуфай: зацикливание с кроссфейдом, fade in/out. spec: True | путь."""
    path = spec if isinstance(spec, str) else os.path.join(HOME, "music", "lofi.wav")
    if not os.path.isfile(path):
        import make_music
        make_music.write_wav(path, make_music.build())
    a = decode_audio(path)  # mono 48k (для фона достаточно)
    n = int(total * SR)
    if len(a) == 0:
        return np.zeros(n, dtype=np.float32)
    xf = min(int(1.0 * SR), len(a) // 4)
    out = np.zeros(n + xf, dtype=np.float32)
    pos = 0
    while pos < n:
        seg = a.copy()
        if pos > 0:
            seg[:xf] *= np.linspace(0, 1, xf)
            out[pos:pos + xf] *= np.linspace(1, 0, xf)[: len(out[pos:pos + xf])]
        e = min(len(out), pos + len(seg))
        out[pos:e] += seg[: e - pos]
        pos += len(a) - xf
    out = out[:n]
    fi, fo = int(1.2 * SR), int(2.5 * SR)
    out[:fi] *= np.linspace(0, 1, min(fi, n))
    out[-fo:] *= np.linspace(1, 0, min(fo, n))
    return out * volume


def mix_audio(scenes, total, music, volume):
    n = int(round(total * SR))
    voice = np.zeros(n, dtype=np.float32)
    for s in scenes:
        if s["audio"] is not None:
            a = s["audio"]
            i = int(round((s["start"] + s["lead"]) * SR))
            e = min(n, i + len(a))
            voice[i:e] += a[: e - i]
    rms = np.sqrt(np.mean(voice[np.abs(voice) > 0.005] ** 2)) if np.any(np.abs(voice) > 0.005) else 0.0
    if rms > 0:  # выравниваем громкость голоса до ~ -19 dBFS RMS
        voice *= min(0.115 / rms, 4.0)
    out = voice.copy()
    if music:
        m = music_track(music, total, volume)
        m = m[:n] if len(m) >= n else np.pad(m, (0, n - len(m)))
        # приглушение музыки, пока говорит голос: огибающая 60 мс -> сглаживание -> усиление 1.0 .. 0.4
        env = np.abs(voice)
        k = int(0.06 * SR)
        env = np.convolve(env, np.ones(k) / k, mode="same")
        gate = (env > 0.02).astype(np.float32)
        sm = int(0.35 * SR)
        gate = np.convolve(gate, np.ones(sm) / sm, mode="same")
        out += m * (1.0 - 0.6 * np.clip(gate * 1.6, 0, 1))
    out = np.tanh(out * 0.9) / np.tanh(0.9) if np.abs(out).max() > 0.95 else out  # мягкий лимитер
    return np.clip(out, -1, 1)


def write_wav(path, mono):
    pcm = (mono * 32767).astype("<i2")
    st = np.repeat(pcm[:, None], 2, axis=1)
    with wave.open(path, "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(st.tobytes())


# ---------------------------------------------------------------- рендер
def font_css():
    dirs = [os.path.join(HOME, "fonts")] + REPO_FONTS
    want = [("Montserrat", 400, "Montserrat-Regular.ttf"), ("Montserrat", 500, "Montserrat-Medium.ttf"),
            ("Montserrat", 600, "Montserrat-SemiBold.ttf"), ("Montserrat", 700, "Montserrat-Bold.ttf"),
            ("Montserrat", 800, "Montserrat-ExtraBold.ttf"), ("Noto Sans", 400, "NotoSans-Regular.ttf"),
            ("Noto Sans", 700, "NotoSans-Bold.ttf")]
    css = []
    for fam, w, fn in want:
        for d in dirs:
            p = os.path.join(d, fn)
            if os.path.isfile(p):
                css.append("@font-face{font-family:'%s';font-weight:%d;src:url('file:///%s');}" % (fam, w, p.replace("\\", "/")))
                break
    if not css:
        log("ВНИМАНИЕ: шрифты Montserrat/Noto не найдены, будет системный")
    return "".join(css)


INIT_JS = r"""
window.SCENE = %(scene)s;
(function(){
  const FONTCSS = %(fonts)s, TR = %(tr)s, SUBPX = %(subpx)s;
  function setup(){
    const st = document.createElement('style'); st.textContent = FONTCSS; document.head.appendChild(st);
    const sub = document.createElement('div'); sub.id = 'nm-sub';
    sub.style.cssText = 'position:fixed;left:50%%;transform:translateX(-50%%);width:max-content;bottom:' + SUBPX.bottom + 'vmin;max-width:' + SUBPX.maxw +
      'vw;text-align:center;z-index:99999;color:#fff;font:700 ' + SUBPX.size + 'vmin/1.28 Montserrat,"Noto Sans",sans-serif;' +
      'background:rgba(25,25,25,.80);padding:1.1vmin 2.4vmin;border-radius:1.6vmin;opacity:0;white-space:pre-wrap;' +
      'text-shadow:0 .2vmin .6vmin rgba(0,0,0,.6);box-shadow:0 .4vmin 1.6vmin rgba(0,0,0,.35)';
    document.documentElement.appendChild(sub);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', setup); else setup();
  const E = {out:t=>1-Math.pow(1-t,3), inOut:t=>t<.5?4*t*t*t:1-Math.pow(-2*t+2,3)/2};
  window.__nm_frame = function(t, text, alpha){
    document.getAnimations().forEach(a => { try { a.pause(); a.currentTime = t*1000; } catch(e){} });
    if (window.__render) window.__render(t);
    const b = document.body, p = Math.min(1, Math.max(0, t / TR.dur)), e = E.out(p);
    b.style.opacity = ''; b.style.transform = ''; b.style.clipPath = '';
    if (p < 1) {
      if (TR.kind === 'fade') b.style.opacity = p;
      else if (TR.kind === 'slide') b.style.transform = 'translateX(' + ((1-e)*100) + '%%)';
      else if (TR.kind === 'wipe') b.style.clipPath = 'inset(0 ' + ((1-e)*100) + '%% 0 0)';
      else if (TR.kind === 'zoom') { b.style.opacity = e; b.style.transform = 'scale(' + (1.12-0.12*e) + ')'; }
    }
    const s = document.getElementById('nm-sub');
    if (s) { s.textContent = text || ''; s.style.opacity = text ? alpha : 0; }
  };
})();
"""


def launch_browser(pw):
    last = None
    for kw in ({"channel": "msedge"}, {"channel": "chrome"}, {}):
        try:
            return pw.chromium.launch(headless=True, args=["--force-device-scale-factor=1", "--hide-scrollbars",
                                                           "--font-render-hinting=none", "--disable-lcd-text"], **kw)
        except Exception as e:
            last = e
    raise SystemExit("Не удалось запустить Chromium/Edge/Chrome: %s" % last)


def sub_state(subs, t):
    for a, b, txt in subs:
        if a <= t < b:
            return txt, min(1.0, (t - a) / 0.12, (b - t) / 0.12) if b - a > 0.3 else 1.0
    return "", 0.0


def prepare_page(browser, s, W, H, fmt, logo):
    ctx = browser.new_context(viewport={"width": W, "height": H}, device_scale_factor=1)
    page = ctx.new_page()
    scene = {"params": s["params"], "duration": s["dur"], "lang": s["lang"], "portrait": H > W, "W": W, "H": H,
             "logo": logo, "id": s["id"]}
    portrait = H > W
    subpx = {"bottom": 9 if portrait else 5, "maxw": 90 if portrait else 82, "size": 3.9 if portrait else 4.0}
    tr = {"kind": s["transition"], "dur": 0.5 if s["transition"] != "none" else 0.0001}
    page.add_init_script(INIT_JS % {"scene": json.dumps(scene, ensure_ascii=False), "fonts": json.dumps(font_css()),
                                    "tr": json.dumps(tr), "subpx": json.dumps(subpx)})
    page.goto("file:///" + s["page"].replace("\\", "/"))
    page.wait_for_function("document.readyState === 'complete'", timeout=20000)
    page.evaluate("Promise.resolve(window.__ready)")
    page.evaluate("""async () => {
      const L = ['Аа Бб Үү Өө Ңң Ыы Ёё 0123 Abc'];
      await Promise.all([400,500,600,700,800].map(w => document.fonts.load(w + ' 40px Montserrat', L[0]))
        .concat([400,700].map(w => document.fonts.load(w + ' 40px "Noto Sans"', L[0]))));
      await document.fonts.ready;
      document.getElementById('nm-sub').textContent = 'Үү Өө Ңң';
    }""")
    return ctx, page


def render_video(scenes, total, fmt, out_path, wav_path, fps, draft, frame_kind):
    from playwright.sync_api import sync_playwright
    W, H = FORMATS[fmt]
    scale = 0.5 if draft else 1.0
    W, H = int(W * scale), int(H * scale)
    maxfps = 15 if draft else fps
    n_total = sum(int(round(s["dur"] * maxfps)) for s in scenes)
    cmd = [FFMPEG, "-y", "-hide_banner", "-loglevel", "error", "-f", "image2pipe", "-framerate", str(maxfps),
           "-c:v", "png" if frame_kind == "png" else "mjpeg", "-i", "-", "-i", wav_path,
           "-vf", "scale=out_color_matrix=bt709:out_range=tv,format=yuv420p",
           "-c:v", "libx264", "-preset", "medium", "-crf", "17" if not draft else "23", "-profile:v", "high",
           "-r", str(maxfps), "-g", str(maxfps * 2), "-colorspace", "bt709", "-color_primaries", "bt709",
           "-color_trc", "bt709", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-movflags", "+faststart",
           "-shortest", out_path]
    ff = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    done, t_start = 0, time.time()
    with sync_playwright() as pw:
        browser = launch_browser(pw)
        try:
            for s in scenes:
                ctx, page = prepare_page(browser, s, W, H, fmt, s["raw"].get("_logo"))
                n = int(round(s["dur"] * maxfps))
                subs = s["subs"][fmt]
                for k in range(n):
                    t = k / maxfps
                    txt, al = sub_state(subs, t)
                    page.evaluate("([t,x,a]) => window.__nm_frame(t,x,a)", [t, txt, al])
                    if frame_kind == "png":
                        buf = page.screenshot(type="png")
                    else:
                        buf = page.screenshot(type="jpeg", quality=95)
                    try:
                        ff.stdin.write(buf)
                    except (BrokenPipeError, OSError):
                        raise SystemExit("ffmpeg закрыл вход (ошибка кодирования)")
                    done += 1
                    if done % 60 == 0:
                        el = time.time() - t_start
                        log("    кадр %d/%d (%.0f%%), осталось ~%.0f с" % (done, n_total, 100 * done / n_total, el / done * (n_total - done)))
                ctx.close()
        finally:
            browser.close()
            try:
                ff.stdin.close()
            except Exception:
                pass
    rc = ff.wait()
    if rc != 0:
        raise SystemExit("ffmpeg завершился с кодом %d" % rc)


def snap_frames(scenes, fmt, outdir, draft):
    from playwright.sync_api import sync_playwright
    os.makedirs(outdir, exist_ok=True)
    W, H = FORMATS[fmt]
    if draft:
        W, H = W // 2, H // 2
    with sync_playwright() as pw:
        browser = launch_browser(pw)
        try:
            for s in scenes:
                ctx, page = prepare_page(browser, s, W, H, fmt, s["raw"].get("_logo"))
                for fr in (0.12, 0.3, 0.5, 0.72, 0.95):
                    t = round(s["dur"] * fr * 30) / 30
                    txt, al = sub_state(s["subs"][fmt], t)
                    page.evaluate("([t,x,a]) => window.__nm_frame(t,x,a)", [t, txt, al or 1])
                    p = os.path.join(outdir, "%02d-%s-%s-%03d.png" % (s["i"], s["id"], fmt[:1], int(fr * 100)))
                    page.screenshot(path=p, type="png")
                ctx.close()
        finally:
            browser.close()
    log("кадры:", outdir)


def main():
    ap = argparse.ArgumentParser(description="JSON-сценарий -> mp4 (см. докстроку файла)")
    ap.add_argument("scenario", nargs="?")
    ap.add_argument("-o", "--out", help="выходной mp4 (для portrait: <имя>-vertical.mp4)")
    ap.add_argument("--format", choices=["landscape", "portrait", "both"], default="landscape")
    ap.add_argument("--draft", action="store_true", help="половинный размер, 15 fps")
    ap.add_argument("--fps", type=int)
    ap.add_argument("--no-music", action="store_true")
    ap.add_argument("--frames", choices=["png", "jpeg"], default="png")
    ap.add_argument("--snap", metavar="DIR", help="сохранить кадры сцен в PNG и выйти (без видео)")
    ap.add_argument("--probe", metavar="MP4", help="показать параметры готового файла и выйти")
    a = ap.parse_args()
    if a.probe:
        print(probe(a.probe))
        return
    if not a.scenario:
        ap.error("нужен файл сценария")
    scenario = load_scenario(a.scenario)
    fps = a.fps or int(scenario.get("fps", 30))
    if scenario.get("logo") is None and os.path.isfile(os.path.join(HOME, "assets", "logo.png")):
        scenario["logo"] = os.path.join(HOME, "assets", "logo.png")
    for sc in scenario["scenes"]:
        sc["_logo"] = scenario.get("logo")
    fmts = ["landscape", "portrait"] if a.format == "both" else [a.format]
    log("Сценарий: %s, формат: %s, fps %d" % (scenario.get("name", os.path.basename(a.scenario)), "+".join(fmts), fps))
    scenes, total = plan(scenario, fps, {"landscape": 88, "portrait": 52})
    log("Длительность ролика: %.1f с" % total)
    if a.snap:
        for f in fmts:
            snap_frames(scenes, f, a.snap, a.draft)
        return
    music = False if a.no_music else scenario.get("music", True)
    wav = os.path.join(HOME, "cache", "mix-%s.wav" % hashlib.sha1(a.scenario.encode()).hexdigest()[:8])
    if scenario.get("premix"):  # 2026-10-29: готовый микс (голос+SFX+музыка, 48 кГц стерео WAV) — например ролик «Свет погас» (tools/video/ad_svet)
        wav = scenario["premix"]
        if not os.path.isfile(wav):
            raise SystemExit("Нет готового микса: %s" % wav)
    else:
        write_wav(wav, mix_audio(scenes, total, music, float(scenario.get("music_volume", 0.14))))
    stem = a.out or os.path.join(os.path.dirname(os.path.abspath(a.scenario)), scenario.get("name", "video") + ".mp4")
    base, ext = os.path.splitext(stem)
    os.makedirs(os.path.dirname(os.path.abspath(stem)), exist_ok=True)
    for f in fmts:
        out = stem if f == "landscape" else base + "-vertical" + (ext or ".mp4")
        log("Рендер %s -> %s" % (f, out))
        t0 = time.time()
        render_video(scenes, total, f, out, wav, fps, a.draft, a.frames)
        log("Готово за %.0f с.\n%s" % (time.time() - t0, probe(out)))


if __name__ == "__main__":
    main()
