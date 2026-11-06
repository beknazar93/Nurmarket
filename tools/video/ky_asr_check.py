#!/usr/bin/env python3
"""2026-10-29: проверка кыргызской озвучки без слуха: wav -> ASR (facebook/mms-1b-all, язык kir) -> доля совпавших слов с текстом.
  python tools/video/ky_asr_check.py файл.wav "исходный текст" [...пары]   (запускать в venv kyvoice; сам перезапускается)
Текст сравнивается после нормализации (ky_norm): числа словами, регистр и знаки не важны."""
import difflib, os, re, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
KYDIR = os.environ.get("NM_KYVOICE_DIR", r"C:\Users\User\NurMarket-video\kyvoice")
VENV_PY = os.path.join(KYDIR, "venv", "Scripts", "python.exe")


def words(t):
    return re.findall(r"[^\W\d_]+", t.lower().replace("ё", "е"))


def score(ref, hyp):
    a, b = words(ref), words(hyp)
    m = difflib.SequenceMatcher(None, a, b, autojunk=False)
    same = sum(x.size for x in m.get_matching_blocks())
    # посимвольная близость (CER-подобная), т.к. ASR часто ошибается на 1-2 буквы
    cs = difflib.SequenceMatcher(None, " ".join(a), " ".join(b), autojunk=False).ratio()
    return same / max(len(a), 1), cs


def main():
    if os.path.normcase(sys.executable) != os.path.normcase(VENV_PY):
        sys.exit(subprocess.call([VENV_PY, os.path.abspath(__file__)] + sys.argv[1:]))
    import numpy as np, torch, soundfile as sf
    from transformers import AutoProcessor, Wav2Vec2ForCTC
    sys.path.insert(0, HERE)
    from ky_norm import normalize
    cache = os.path.join(KYDIR, "hf")
    proc = AutoProcessor.from_pretrained("facebook/mms-1b-all", cache_dir=cache, target_lang="kir")
    model = Wav2Vec2ForCTC.from_pretrained("facebook/mms-1b-all", cache_dir=cache, target_lang="kir",
                                           ignore_mismatched_sizes=True, torch_dtype=torch.bfloat16, low_cpu_mem_usage=True).eval()
    args = sys.argv[1:]
    for i in range(0, len(args) - 1, 2):
        wav, text = args[i], args[i + 1]
        x, sr = sf.read(wav, dtype="float32")
        if x.ndim > 1:
            x = x.mean(1)
        import torchaudio
        x = torchaudio.functional.resample(torch.from_numpy(x), sr, 16000).numpy()
        inp = proc(x, sampling_rate=16000, return_tensors="pt")
        with torch.no_grad():
            lg = model(inp.input_values.to(torch.bfloat16)).logits
        hyp = proc.decode(lg.argmax(-1)[0])
        w, c = score(normalize(text), hyp)
        print("%s\n  ждали:  %s\n  ASR:    %s\n  слов совпало: %.0f %%, схожесть текста: %.0f %%" % (wav, normalize(text), hyp, w * 100, c * 100), flush=True)


if __name__ == "__main__":
    main()
