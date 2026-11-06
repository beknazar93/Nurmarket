# -*- coding: utf-8 -*-
"""2026-10-29: нормализация кыргызского текста для озвучки: числа словами (0..999999), «сом», сокращения, латиница.
Без зависимостей. Используется tools/video/ky_voice.py."""
import re

_ONES = ["", "бир", "эки", "үч", "төрт", "беш", "алты", "жети", "сегиз", "тогуз"]
_TENS = ["", "он", "жыйырма", "отуз", "кырк", "элүү", "алтымыш", "жетимиш", "сексен", "токсон"]


def _below1000(n):
    parts = []
    h, r = divmod(n, 100)
    if h:
        parts.append("жүз" if h == 1 else _ONES[h] + " жүз")
    t, o = divmod(r, 10)
    if t:
        parts.append(_TENS[t])
    if o:
        parts.append(_ONES[o])
    return " ".join(parts)


def number_to_words(n):
    """Целое 0..999999 -> слова по-кыргызски (1000 = «миң», 100 = «жүз»)."""
    n = int(n)
    if n < 0:
        return "минус " + number_to_words(-n)
    if n == 0:
        return "нөл"
    if n > 999999999:
        return " ".join(number_to_words(int(c)) for c in str(n))  # очень длинное: по цифрам
    parts = []
    if n > 999999:  # миллионы (запас сверх задания 0..999999)
        mi, n = divmod(n, 1000000)
        parts.append(_below1000(mi) + " миллион")
        if n == 0:
            return " ".join(parts)
    th, r = divmod(n, 1000)
    if th:
        parts.append("миң" if th == 1 else _below1000(th) + " миң")
    if r:
        parts.append(_below1000(r))
    return " ".join(parts)


_LAT = {
    "nurmarket": "НурМаркет", "nurcrm": "Нур Си Ар Эм", "crm": "СиАрЭм", "nbs": "ЭнБиЭс", "pos": "пос", "qr": "кюар",
    "wi-fi": "вайфай", "wifi": "вайфай", "android": "андроид", "windows": "виндоус", "linux": "линукс",
    "macos": "макос", "usb": "юэсби", "pin": "пин", "ai": "эй-ай", "excel": "эксель", "kg": "килограмм",
}
_LETTERS = dict(zip("abcdefghijklmnopqrstuvwxyz",
                    ["а", "б", "к", "д", "е", "ф", "г", "х", "и", "ж", "к", "л", "м", "н", "о", "п", "к", "р", "с",
                     "т", "у", "в", "в", "кс", "й", "з"]))
_ABBR = [
    (r"\bкг\b\.?", "килограмм"), (r"\bшт\b\.?", "даана"), (r"\bлит\b\.?", "литр"), (r"\bмин\b\.?", "мүнөт"),
    (r"\bсек\b\.?", "секунд"), (r"\bт\.\s?б\.", "ушул сыяктуу"), (r"\bж\.б\.", "жана башкалар"),
    (r"\bт\.\s?е\.", "башкача айтканда"), (r"\bмис\.", "мисалы"),
]


def _lat_word(m):
    w = m.group(0)
    lw = w.lower()
    if lw in _LAT:
        return _LAT[lw]
    return "".join(_LETTERS.get(c, "") for c in lw)


def _num_sub(m):
    s = m.group(0).replace(" ", "").replace(" ", "")
    s = s.replace(",", ".") if re.fullmatch(r"\d+,\d{1,2}", s) else s.replace(",", "")
    if "." in s:
        a, b = s.split(".", 1)
        return number_to_words(int(a or 0)) + " бүтүн " + number_to_words(int(b)) if int(b) else number_to_words(int(a))
    return number_to_words(int(s))


def normalize(text):
    t = text.replace("«", " ").replace("»", " ").replace("“", " ").replace("”", " ")
    t = t.replace('"', " ").replace("(", ", ").replace(")", ", ")
    t = re.sub(r"[‑–—−]", ", ", t)  # тире -> пауза
    t = re.sub(r"(\d)\s*%", r"\1 пайыз", t)
    t = re.sub(r"№\s?", "номер ", t)
    t = re.sub(r"(?i)\b(сом|сомдон|сомго|с\.)\b", "сом", t)
    t = re.sub(r"(\d)\s*(сом|сомго|сомдон)\b", r"\1 сом", t)
    t = re.sub(r"(\d)\s?(?:⃀|с\b)", r"\1 сом", t)
    for pat, rep in _ABBR:
        t = re.sub(pat, rep, t, flags=re.IGNORECASE)
    t = re.sub(r"[A-Za-z][A-Za-z\-]*", _lat_word, t)
    t = re.sub(r"\d+(?:[  ]\d{3})*(?:,\d{1,2})?|\d+\.\d+", _num_sub, t)
    t = re.sub(r"\s+", " ", t).strip()
    return t


if __name__ == "__main__":
    for s in ("120 сом", "1500 сом", "1 000 000", "NurMarket — 5%", "Адыгене — 55 сом, 2 кг", "999999", "100 200 3,5"):
        print(s, "->", normalize(s))
