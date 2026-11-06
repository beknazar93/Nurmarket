#!/usr/bin/env python3
"""2026-10-29: кыргызская озвучка для роликов NurMarket, полностью локально (без облака).

  python tools/video/ky_voice.py "текст" -o out.wav [--ref образец.wav] [--engine knnvc|mms] [--speed 1.0] [--no-cache]

Конвейер: нормализация текста (числа словами, «сом», сокращения, латиница; tools/video/ky_norm.py) -> деление по знакам
препинания (паузы: запятая 0,25 с, точка/!/? 0,5 с, двоеточие 0,3 с) -> Meta MMS-TTS кыргызский (facebook/mms-tts-kir, VITS)
говорит текст -> kNN-VC (WavLM + HiFi-GAN) переносит тембр на голос владельца по образцу --ref -> громкость к ~-20 dBFS RMS,
пик не выше -1 dB -> WAV 48 кГц, 16 бит, моно. Результат кэшируется (ключ: текст+образец+настройки).
--engine mms даёт голос MMS без преобразования (быстрее, но «чужой» тембр).
Образец и модели лежат только в каталоге kyvoice (по умолчанию C:\\Users\\User\\NurMarket-video\\kyvoice), в git не попадают.
Скрипт сам перезапускается в venv этого каталога (venv\\Scripts\\python.exe), системный Python может быть любым.
Из build_video.py: сцена {"lang": "ky", "voice": "clone", "text": "..."} -> вызывается synth_to_file().
"""
import argparse
import hashlib
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
KYDIR = os.environ.get("NM_KYVOICE_DIR", r"C:\Users\User\NurMarket-video\kyvoice")
VENV_PY = os.path.join(KYDIR, "venv", "Scripts", "python.exe")
DEFAULT_REF = os.environ.get("NM_KYVOICE_REF", r"C:\Users\User\Desktop\Nurmarket\образец голоса на кыргызском.wav")
CACHE = os.path.join(KYDIR, "cache")
OUT_SR = 48000
PAUSES = {",": 0.25, ";": 0.3, ":": 0.3, ".": 0.5, "!": 0.5, "?": 0.5, "…": 0.5}
VERSION = "1"  # менять при смене алгоритма, чтобы сбросить кэш


def _in_venv():
    return os.path.normcase(os.path.abspath(sys.executable)) == os.path.normcase(os.path.abspath(VENV_PY))


def _file_hash(path):
    h = hashlib.sha1()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()[:12]


def split_chunks(text):
    """[(фрагмент, пауза_после_сек)] по знакам препинания."""
    out = []
    for m in re.finditer(r"([^,;:.!?…]+)([,;:.!?…]*)", text):
        frag, punct = m.group(1).strip(), m.group(2)
        if not frag:
            continue
        pause = max([PAUSES.get(c, 0) for c in punct] or [0.0])
        out.append((frag, pause))
    if out:
        out[-1] = (out[-1][0], 0.0)  # хвост паузы добавляет сборщик ролика
    return out


# ---------------------------------------------------------------- модели (ленивая загрузка)
_mms = {}
_knn = {}


def _device():
    import torch
    # 2026-10-29: на GTX 1070 с драйвером 456.71 cuBLAS (CUDA 11.8) падает -> по умолчанию CPU; GPU: NM_KYVOICE_GPU=1
    if os.environ.get("NM_KYVOICE_GPU") == "1" and torch.cuda.is_available():
        try:
            torch.zeros(1).cuda()
            return "cuda"
        except Exception:
            pass
    return "cpu"


def _clean_for_mms(tok, text):
    vocab = set(tok.get_vocab().keys())
    t = text.lower().replace("ё", "е")
    t = "".join(c if (c in vocab or c == " ") else " " for c in t)
    return re.sub(r"\s+", " ", t).strip()


def mms_speak(text, speed=1.0, seed=7):
    import torch
    from transformers import AutoTokenizer, VitsModel
    if "m" not in _mms:
        _mms["tok"] = AutoTokenizer.from_pretrained("facebook/mms-tts-kir", cache_dir=os.path.join(KYDIR, "hf"))
        _mms["m"] = VitsModel.from_pretrained("facebook/mms-tts-kir", cache_dir=os.path.join(KYDIR, "hf")).eval()
    m, tok = _mms["m"], _mms["tok"]
    m.speaking_rate = float(speed)
    ids = tok(_clean_for_mms(tok, text), return_tensors="pt")
    torch.manual_seed(seed)
    with torch.no_grad():
        w = m(**ids).waveform[0]
    return w.float().cpu(), m.config.sampling_rate  # 16000


def _knn_model(dev):
    import torch
    if "m" not in _knn:
        os.environ.setdefault("TORCH_HOME", os.path.join(KYDIR, "torchhub"))
        torch.hub.set_dir(os.path.join(KYDIR, "torchhub"))
        _knn["m"] = torch.hub.load("bshall/knn-vc", "knn_vc", prematched=True, trust_repo=True, pretrained=True,
                                   device=dev)
    return _knn["m"]


def _matching_set(model, ref, dev):
    """Признаки образца (WavLM) — считаются один раз и лежат в кэше рядом с моделями."""
    import torch
    p = os.path.join(CACHE, "ref-%s.pt" % _file_hash(ref))
    if os.path.isfile(p):
        return torch.load(p, map_location=dev, weights_only=True)
    import torchaudio
    w, sr = torchaudio.load(ref)
    w = w.mean(0, keepdim=True)
    if sr != 16000:
        w = torchaudio.functional.resample(w, sr, 16000)
    os.makedirs(CACHE, exist_ok=True)
    # 2026-10-29: образец режем на куски по 12 с — WavLM на всей записи сразу не помещается в память видеокарты
    seg = 12 * 16000
    paths = []
    for i in range(0, w.shape[1], seg):
        part = w[:, i:i + seg]
        if part.shape[1] < 16000:
            continue
        pth = os.path.join(CACHE, "ref16k-%03d.wav" % len(paths))
        torchaudio.save(pth, part, 16000)
        paths.append(pth)
    ms = model.get_matching_set(paths, vad_trigger_level=7)  # vad: убрать паузы образца
    torch.save(ms.cpu(), p)
    for pth in paths:
        try:
            os.remove(pth)
        except OSError:
            pass
    return ms.to(dev)


def convert(wave16, ref, topk=4):
    import torch
    dev = _device()
    model = _knn_model(dev)
    ms = _matching_set(model, ref, dev)
    q = model.get_features(wave16.to(dev).unsqueeze(0) if wave16.dim() == 1 else wave16.to(dev))
    with torch.no_grad():
        out = model.match(q, ms, topk=topk)
    return out.float().cpu()


# ---------------------------------------------------------------- сборка
def _resample(w, sr, to):
    import torchaudio
    if sr == to:
        return w
    return torchaudio.functional.resample(w.unsqueeze(0), sr, to)[0]


def _normalize_loudness(x, target_db=-20.0, peak_db=-1.0):
    import numpy as np
    a = np.abs(x)
    act = x[a > 0.01 * max(a.max(), 1e-9)] if a.max() > 0 else x
    rms = float(np.sqrt(np.mean(act ** 2))) if act.size else 0.0
    if rms > 0:
        x = x * (10 ** (target_db / 20) / rms)
    pk = float(np.abs(x).max()) if x.size else 0.0
    lim = 10 ** (peak_db / 20)
    if pk > lim:
        x = x * (lim / pk)
    return x


def synth(text, ref=None, engine="knnvc", speed=1.0, use_cache=True, log=print):
    """-> (numpy float32 моно 48 кГц, нормализованный текст)."""
    import numpy as np
    sys.path.insert(0, HERE)
    from ky_norm import normalize
    norm = normalize(text)
    ref = ref or DEFAULT_REF
    if engine == "knnvc" and not os.path.isfile(ref):
        raise SystemExit("Нет образца голоса: %s (--ref или --engine mms)" % ref)
    key = hashlib.sha1(("%s|%s|%s|%s|%s" % (VERSION, engine, speed, _file_hash(ref) if engine == "knnvc" else "-",
                                           norm)).encode("utf-8")).hexdigest()[:20]
    cp = os.path.join(CACHE, "ky-%s.npy" % key)
    if use_cache and os.path.isfile(cp):
        return np.load(cp), norm
    import torch
    pieces = []
    for frag, pause in split_chunks(norm):
        w, sr = mms_speak(frag, speed)
        if engine == "knnvc":
            w = convert(_resample(w, sr, 16000), ref)
            sr = 16000
        w = _resample(w, sr, OUT_SR).numpy()
        pieces.append(w)
        if pause:
            pieces.append(np.zeros(int(pause * OUT_SR), dtype=np.float32))
    if not pieces:
        raise SystemExit("Пустой текст после нормализации")
    x = _normalize_loudness(np.concatenate(pieces).astype(np.float32))
    os.makedirs(CACHE, exist_ok=True)
    np.save(cp, x)
    return x, norm


def write_wav16(path, x, sr=OUT_SR):
    import numpy as np
    import wave
    with wave.open(path, "wb") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(sr)
        f.writeframes((np.clip(x, -1, 1) * 32767).astype("<i2").tobytes())


def synth_to_file(text, out, ref=None, engine="knnvc", speed=1.0):
    """Для build_video.py: вызывается из системного Python, работу делает venv-процесс. -> путь к wav."""
    cmd = [VENV_PY, os.path.join(HERE, "ky_voice.py"), text, "-o", out, "--engine", engine, "--speed", str(speed)]
    if ref:
        cmd += ["--ref", ref]
    r = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env={**os.environ, "PYTHONIOENCODING": "utf-8"})
    if r.returncode != 0 or not os.path.isfile(out):
        raise RuntimeError("ky_voice не справился: " + r.stderr.decode("utf-8", "replace")[-600:])
    return out


def synth_batch_to_files(items):
    """2026-10-29: много реплик за ОДИН запуск venv (модели грузятся один раз).
    items: [{"text","out","engine","ref","speed"}] -> пишет wav по "out"."""
    import json
    import tempfile
    fd, jp = tempfile.mkstemp(suffix=".json")
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        json.dump(items, f, ensure_ascii=False)
    try:
        r = subprocess.run([VENV_PY, os.path.join(HERE, "ky_voice.py"), "--batch", jp], stdout=subprocess.PIPE,
                           stderr=subprocess.PIPE, env={**os.environ, "PYTHONIOENCODING": "utf-8"})
    finally:
        os.remove(jp)
    if r.returncode != 0:
        raise RuntimeError("ky_voice --batch не справился: " + r.stderr.decode("utf-8", "replace")[-800:])
    return r.stdout.decode("utf-8", "replace")


def _run_batch(path):
    import json
    import time
    for it in json.load(open(path, encoding="utf-8")):
        if os.path.isfile(it["out"]):
            continue
        t0 = time.time()
        x, norm = synth(it["text"], it.get("ref"), it.get("engine", "knnvc"), float(it.get("speed", 1.0)))
        os.makedirs(os.path.dirname(os.path.abspath(it["out"])), exist_ok=True)
        write_wav16(it["out"], x)
        print("%.1f с речи за %.1f с: %s" % (len(x) / OUT_SR, time.time() - t0, norm), flush=True)


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "--batch":
        if not _in_venv():
            sys.exit(subprocess.call([VENV_PY, os.path.abspath(__file__)] + sys.argv[1:]))
        return _run_batch(sys.argv[2])
    ap = argparse.ArgumentParser(description="Кыргызская озвучка (MMS-TTS + kNN-VC), локально")
    ap.add_argument("text")
    ap.add_argument("-o", "--out", required=True)
    ap.add_argument("--ref", default=None, help="образец голоса (wav); по умолчанию образец владельца")
    ap.add_argument("--engine", choices=("knnvc", "mms"), default="knnvc")
    ap.add_argument("--speed", type=float, default=1.0, help="скорость речи MMS (1.0 = обычная)")
    ap.add_argument("--no-cache", action="store_true")
    a = ap.parse_args()
    if not _in_venv():
        if not os.path.isfile(VENV_PY):
            raise SystemExit("Нет venv: %s (см. отчёт: python -m venv + pip install torch transformers)" % VENV_PY)
        sys.exit(subprocess.call([VENV_PY, os.path.abspath(__file__)] + sys.argv[1:]))
    import time
    t0 = time.time()
    x, norm = synth(a.text, a.ref, a.engine, a.speed, not a.no_cache)
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    write_wav16(a.out, x)
    dur = len(x) / OUT_SR
    print("текст: %s\nдлительность %.2f с, время %.1f с (%.2f с на секунду речи) -> %s"
          % (norm, dur, time.time() - t0, (time.time() - t0) / max(dur, 0.01), a.out), flush=True)


if __name__ == "__main__":
    main()
