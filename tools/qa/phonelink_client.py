#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""2026-10-27, пакеты К-П2/К-П5 (ТЗ «Мобилка и касса»): тестовый клиент протокола связи кассы с телефоном (v1).
ВНЕ ПРОДУКТА: проверяет сервер PhoneLinkServer + обработчик PhoneScanHandler на headless-стенде
(C:\\Users\\User\\NurMarket-rel1762\\stands\\phonelink-scan, управление по TCP 127.0.0.1:47899), настоящую кассу не трогает.

Запуск:  python tools/qa/phonelink_client.py --stand <путь к PhoneLinkStand.exe> [--log файл] [--fast]
Протокол: одна строка JSON на соединение; подпись HMAC-SHA256(key, "1|op|phone|ts|nonce|sha256hex(body)"), key — 32 байта (base64url).
"""
import argparse
import base64
import hashlib
import hmac
import json
import os
import random
import socket
import statistics
import subprocess
import sys
import threading
import time
from urllib.parse import parse_qs, urlparse

CTL_PORT = 47899
HOST = "127.0.0.1"
RESULTS = []
LOG_LINES = []


def log(text):
    print(text, flush=True)
    LOG_LINES.append(text)


def check(name, ok, detail=""):
    RESULTS.append((name, bool(ok)))
    log(("PASS " if ok else "FAIL ") + name + (" — " + str(detail) if detail != "" else ""))
    return ok


# ---------------------------------------------------------------- протокол

def b64u(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def b64u_decode(text: str) -> bytes:
    return base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))


def sha256hex(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def sign(key: bytes, op, phone, ts, nonce, body: str) -> str:
    msg = "1|%s|%s|%d|%s|%s" % (op, phone, ts, nonce, sha256hex(body))
    return b64u(hmac.new(key, msg.encode("utf-8"), hashlib.sha256).digest())


def new_nonce() -> str:
    return b64u(os.urandom(16))


def make_line(op, phone, key, body, ts=None, nonce=None, sig=None, v=1, body_raw=None) -> bytes:
    """body — dict (сериализуется в строку) или str; body_raw — подставить в поле body как есть (любой JSON-тип)."""
    ts = int(time.time()) if ts is None else ts
    nonce = nonce or new_nonce()
    body_text = body if isinstance(body, str) else json.dumps(body, ensure_ascii=False, separators=(",", ":"))
    msg = {
        "v": v, "op": op, "phone": phone, "ts": ts, "nonce": nonce,
        "body": body_text if body_raw is None else body_raw,
        "sig": sig if sig is not None else sign(key, op, phone, ts, nonce, body_text),
    }
    return (json.dumps(msg, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")


PACE = [0.06]  # пауза между запросами: человек не шлёт 20 запросов/с, иначе касса ответит busy (проверяется отдельно)


def send_raw(port, data: bytes, timeout=8.0, half_close=False):
    """Одно соединение: отправить байты, прочитать одну строку-ответ. Возвращает (dict | None, raw_text)."""
    if PACE[0] > 0:
        time.sleep(PACE[0])
    s = socket.create_connection((HOST, port), timeout=timeout)
    try:
        s.settimeout(timeout)
        try:
            s.sendall(data)
        except (ConnectionResetError, BrokenPipeError, ConnectionAbortedError):
            pass  # сервер уже ответил и закрыл (например, слишком большой запрос) — ответ всё равно читаем
        if half_close:
            try:
                s.shutdown(socket.SHUT_WR)
            except OSError:
                pass
        buf = b""
        try:
            while b"\n" not in buf:
                chunk = s.recv(65536)
                if not chunk:
                    break
                buf += chunk
        except (OSError, socket.timeout):
            pass  # сервер закрыл соединение (в т. ч. сбросом) — берём то, что успели прочитать
        text = buf.decode("utf-8", "replace").strip()
        try:
            return (json.loads(text) if text else None), text
        except json.JSONDecodeError:
            return None, text
    finally:
        s.close()


def call(port, op, phone, key, body, **kw):
    reply, _ = send_raw(port, make_line(op, phone, key, body, **kw))
    return reply or {}


def ctl(cmd: str):
    s = socket.create_connection((HOST, CTL_PORT), timeout=10)
    try:
        s.sendall((cmd + "\n").encode("utf-8"))
        buf = b""
        while b"\n" not in buf:
            chunk = s.recv(65536)
            if not chunk:
                break
            buf += chunk
        return json.loads(buf.decode("utf-8"))
    finally:
        s.close()


def port_open(port, timeout=0.6):
    try:
        socket.create_connection((HOST, port), timeout=timeout).close()
        return True
    except OSError:
        return False


def pair_key():
    """Нажатие «Подключить телефон»: стенд создаёт новый ключ; ключ берём из QR (поле k)."""
    res = ctl("pair")
    qr = res["qr"]
    check_qr_format(qr)
    q = parse_qs(urlparse(qr.replace("nurmarket-link:v1?", "x://x/?")).query)
    return b64u_decode(q["k"][0]), res["port"], qr


QR_CHECKED = []


def check_qr_format(qr):
    if QR_CHECKED:
        return
    QR_CHECKED.append(True)
    q = parse_qs(urlparse(qr.replace("nurmarket-link:v1?", "x://x/?")).query)
    ok = qr.startswith("nurmarket-link:v1?") and set(q.keys()) == {"d", "h", "p", "k", "n", "c"} \
        and len(b64u_decode(q["k"][0])) == 32 and len(q["c"][0]) == 8
    check("QR: формат nurmarket-link:v1?d,h,p,k,n,c; ключ 32 байта; c — 8 hex", ok, "n=%s c=%s" % (q.get("n"), q.get("c")))


def new_phone():
    return "phone-" + os.urandom(6).hex()


# ---------------------------------------------------------------- сценарии К-П2

def scenarios_p2():
    log("=== К-П2: сервер, подпись, время, nonce, размер, ключи, порты ===")
    # приём выключен → порт закрыт
    ctl("stop")
    closed = [p for p in range(47830, 47840) if not port_open(p)]
    check("приём выключен -> порты 47830..47839 закрыты", len(closed) == 10, "закрыто %d из 10" % len(closed))

    # занятый порт -> следующий
    ctl("occupy 47830")
    res = ctl("start")
    check("порт 47830 занят -> сервер берёт 47831", res.get("ok") and res.get("port") == 47831, "порт %s" % res.get("port"))
    ctl("stop")
    ctl("release")
    res = ctl("start")
    check("порты свободны -> сервер на 47830", res.get("ok") and res.get("port") == 47830, "порт %s" % res.get("port"))
    port = res["port"]

    # сопряжение
    key_a, _, _ = pair_key()
    phone_a = new_phone()
    r = call(port, "hello", phone_a, key_a, {"name": "Айбек"})
    check("hello с ключом сопряжения -> ok + kassa/company/shift_open",
          r.get("ok") is True and r.get("code") == "ok" and r.get("kassa") == "Касса стенда"
          and r.get("company") == hashlib.sha256(b"stand-company").hexdigest()[:8] and r.get("shift_open") is True, r)
    phones = ctl("phones")["phones"]
    check("телефон привязан: имя «Айбек» в списке", any(p["id"] == phone_a and p["name"] == "Айбек" for p in phones), phones)

    r = call(port, "hello", phone_a, key_a, {"name": "Айбек (кассир)"})
    phones = ctl("phones")["phones"]
    check("повторный hello сопряжённого -> ok, имя обновляется",
          r.get("ok") is True and any(p["id"] == phone_a and p["name"] == "Айбек (кассир)" for p in phones), r)

    # одноразовость ключа сопряжения
    phone_b = new_phone()
    r = call(port, "hello", phone_b, key_a, {"name": "Чужой"})
    check("ключ сопряжения одноразовый: второй телефон тем же ключом -> unknown_phone", r.get("ok") is False and r.get("code") == "unknown_phone", r)

    key_b, _, _ = pair_key()
    r = call(port, "hello", phone_b, key_b, {"name": "Бермет"})
    check("новое нажатие «Подключить» -> новый ключ -> второй телефон подключён", r.get("ok") is True, r)

    # неверная подпись
    r = call(port, "hello", phone_a, key_a, {"name": "x"}, sig=b64u(os.urandom(32)))
    check("неверная подпись -> bad_sig", r.get("ok") is False and r.get("code") == "bad_sig", r)
    # тело изменено после подписи
    ts, nonce = int(time.time()), new_nonce()
    sig = sign(key_a, "hello", phone_a, ts, nonce, '{"name":"a"}')
    r = call(port, "hello", phone_a, key_a, '{"name":"b"}', ts=ts, nonce=nonce, sig=sig)
    check("тело подменено после подписи -> bad_sig (подпись покрывает тело)", r.get("code") == "bad_sig", r)

    # время
    r = call(port, "hello", phone_a, key_a, {"name": "t"}, ts=int(time.time()) - 1200)
    check("время телефона на 20 мин раньше -> bad_time", r.get("ok") is False and r.get("code") == "bad_time", r)
    r = call(port, "hello", phone_a, key_a, {"name": "t"}, ts=int(time.time()) + 1200)
    check("время телефона на 20 мин позже -> bad_time", r.get("code") == "bad_time", r)
    r = call(port, "hello", phone_a, key_a, {"name": "t"}, ts=int(time.time()) - 540)
    check("время телефона на 9 мин раньше (в допуске ±10 мин) -> ok", r.get("ok") is True, r)

    # повтор nonce
    line = make_line("hello", phone_a, key_a, {"name": "r"})
    r1, _ = send_raw(port, line)
    r2, _ = send_raw(port, line)
    check("тот же запрос дважды: первый ok, второй replay",
          (r1 or {}).get("ok") is True and (r2 or {}).get("ok") is False and (r2 or {}).get("code") == "replay", (r1, r2))

    # размер
    big = {"name": "x" * 70000}
    reply, text = send_raw(port, make_line("hello", phone_a, key_a, big))
    check("тело > 64 КБ -> too_big", (reply or {}).get("code") == "too_big" and reply.get("ok") is False, text[:120])
    reply, text = send_raw(port, b"x" * 200000 + b"\n")
    check("строка 200 КБ без структуры -> too_big", (reply or {}).get("code") == "too_big", text[:120])
    # почти предел: ровно ≤ 64 КБ с верной подписью принимается
    body_ok = {"name": "n", "pad": "x" * 60000}
    r = call(port, "hello", phone_a, key_a, body_ok)
    check("запрос ~60 КБ (в пределе) принимается", r.get("ok") is True, r)

    # чужой ключ
    r = call(port, "hello", phone_a, os.urandom(32), {"name": "x"})
    check("известный телефон, подпись чужим ключом -> bad_sig", r.get("code") == "bad_sig", r)
    r = call(port, "scan", new_phone(), os.urandom(32), {"code": "1", "seq": 1})
    check("незнакомый телефон -> unknown_phone", r.get("code") == "unknown_phone", r)
    r = call(port, "scan", phone_a, key_b, {"code": "1", "seq": 1})
    check("ключ другого телефона -> bad_sig", r.get("code") == "bad_sig", r)

    # формат
    for name, data in [
        ("не JSON", b"hello there\n"),
        ("нет sig", (json.dumps({"v": 1, "op": "hello", "phone": phone_a, "ts": int(time.time()), "nonce": new_nonce(), "body": "{}"}) + "\n").encode()),
        ("версия v=2", make_line("hello", phone_a, key_a, {"name": "x"}, v=2)),
        ("body не строка (объект)", make_line("hello", phone_a, key_a, {"name": "x"}, body_raw={"name": "x"})),
        ("phone короче 8", make_line("hello", "short", key_a, {"name": "x"})),
        ("пустая строка", b"\n"),
    ]:
        reply, text = send_raw(port, data)
        check("bad_request: " + name, (reply or {}).get("code") == "bad_request" and reply.get("ok") is False, text[:100])
    r = call(port, "print", phone_a, key_a, {"x": 1})
    check("op, которого касса не знает (print) -> bad_request, без печати", r.get("code") == "bad_request", r)
    r = call(port, "hello", phone_a, key_a, "[1,2]")
    check("тело — не объект -> bad_request", r.get("code") == "bad_request", r)

    # таймаут чтения
    t0 = time.time()
    s = socket.create_connection((HOST, port), timeout=10)
    s.settimeout(10)
    try:
        data = s.recv(100)
    except socket.timeout:
        data = b"<timeout>"
    except OSError:
        data = b""
    dt = time.time() - t0
    s.close()
    check("молчащее соединение закрывается через ~5 с без ответа", data == b"" and 4.0 <= dt <= 7.5, "закрыто за %.1f с, данных %r" % (dt, data))

    # отозванный ключ
    ok = ctl("revoke " + phone_a).get("ok")
    r1 = call(port, "scan", phone_a, key_a, {"code": "4870000000001", "seq": 900})
    r2 = call(port, "hello", phone_a, key_a, {"name": "x"})
    check("«Отключить телефон» -> scan и hello -> revoked, без добавления",
          ok and r1.get("code") == "revoked" and r1.get("ok") is False and r2.get("code") == "revoked", (r1, r2))
    r = call(port, "hello", phone_b, key_b, {"name": "Бермет"})
    check("другой телефон при этом продолжает работать", r.get("ok") is True, r)

    # повторное подключение отозванного
    key_a2, _, _ = pair_key()
    r = call(port, "hello", phone_a, key_a2, {"name": "Айбек"})
    check("отозванный телефон подключается заново новым QR -> ok", r.get("ok") is True, r)
    r = call(port, "hello", phone_a, key_a, {"name": "Айбек"})
    check("старый ключ после повторного подключения не действует -> bad_sig", r.get("code") == "bad_sig", r)

    # повторное сопряжение сопряжённого (телефон заново отсканировал QR)
    key_a3, _, _ = pair_key()
    r = call(port, "hello", phone_a, key_a3, {"name": "Айбек"})
    r_old = call(port, "hello", phone_a, key_a2, {"name": "Айбек"})
    check("новый QR для уже сопряжённого телефона заменяет ключ (старый -> bad_sig)", r.get("ok") is True and r_old.get("code") == "bad_sig", (r, r_old))

    # ожидание сопряжения живёт 5 минут
    key_d, _, _ = pair_key()
    ctl("clock 301")
    r = call(port, "hello", new_phone(), key_d, {"name": "Опоздавший"})
    ctl("clock 0")
    check("ключ сопряжения старше 5 мин -> unknown_phone", r.get("code") == "unknown_phone", r)

    # ограничение частоты
    codes = []
    PACE[0] = 0
    for _ in range(40):
        reply, _ = send_raw(port, make_line("hello", phone_b, key_b, {"name": "Бермет"}), timeout=5)
        codes.append((reply or {}).get("code"))
    check("≥ 20 запросов/с с одного адреса -> busy", "busy" in codes, "busy=%d из 40" % codes.count("busy"))
    PACE[0] = 0.06
    time.sleep(1.3)
    r = call(port, "hello", phone_b, key_b, {"name": "Бермет"})
    check("через секунду связь снова принимается", r.get("ok") is True, r)

    # выключение закрывает порт
    ctl("stop")
    check("после выключения приёма порт закрыт", not port_open(port))
    res = ctl("start")
    check("повторное включение открывает порт", res.get("ok") and port_open(res["port"]), res.get("port"))
    return res["port"], phone_a, key_a3, phone_b, key_b


# ---------------------------------------------------------------- сценарии К-П5

def scenarios_p5(port, phone_a, key_a, phone_b, key_b, fast):
    log("=== К-П5: телефон-сканер ===")
    ctl("till on")
    ctl("reset")
    time.sleep(1.2)  # окно ограничения частоты от предыдущей серии

    # 30 кодов подряд, по порядку seq
    pace = 0.06 if fast else 1.0
    expected = []
    replies = []
    rtts = []
    send_ticks = []
    freq = ctl("ticks")["freq"]
    base_off = ctl("ticks")["now_ticks"] / freq - time.perf_counter()
    log("-- 30 сканов, пауза %.2f с (%s)" % (pace, "быстро" if fast else "30 секунд"))
    PACE[0] = 0  # время круга меряем без искусственной паузы клиента
    for i in range(1, 31):
        code = "4870000000%03d" % i
        expected.append(code)
        t0 = time.perf_counter()
        r = call(port, "scan", phone_a, key_a, {"code": code, "seq": i})
        rtts.append(time.perf_counter() - t0)
        send_ticks.append(t0)
        replies.append(r)
        time.sleep(pace)
    PACE[0] = 0.06
    dump = ctl("dump")
    all_added = all(r.get("ok") is True and r.get("code") == "added" and r.get("name") == "Товар %d" % (i + 1) for i, r in enumerate(replies))
    check("30 сканов: все ответы added с названием товара", all_added, [r.get("code") for r in replies if r.get("code") != "added"])
    check("30 сканов -> 30 строк в чеке, порядок по seq, без потерь и дублей", dump["codes"] == expected, "строк %d" % len(dump["codes"]))
    lat = []
    for i, t in enumerate(dump["ticks"]):
        enq = t / freq - base_off  # в шкале perf_counter клиента
        lat.append(enq - send_ticks[i])
    plausible = all(-0.05 < x < 2.0 for x in lat)
    if plausible:
        check("задержка от отправки скана до строки в очереди ≤ 0,5 с (макс %.0f мс, медиана %.0f мс)" % (max(lat) * 1000, statistics.median(lat) * 1000), max(lat) <= 0.5)
    check("полный круг запрос-ответ ≤ 0,5 с (макс %.0f мс, медиана %.0f мс)" % (max(rtts) * 1000, statistics.median(rtts) * 1000), max(rtts) <= 0.5)

    # окно кассы не активно
    ctl("reset")
    ctl("till off")
    r = call(port, "scan", phone_a, key_a, {"code": "4870000000001", "seq": 101})
    d = ctl("dump")
    check("окно кассы не активно -> till_inactive (ok:false), строка не добавлена", r.get("code") == "till_inactive" and r.get("ok") is False and d["codes"] == [], r)
    ctl("till on")
    r = call(port, "scan", phone_a, key_a, {"code": "4870000000001", "seq": 101})
    d = ctl("dump")
    check("тот же seq после till_inactive уже можно добавить (отказ seq не занимает)", r.get("code") == "added" and d["codes"] == ["4870000000001"], r)

    # дубль seq
    r1 = call(port, "scan", phone_a, key_a, {"code": "4870000000002", "seq": 102})
    r2 = call(port, "scan", phone_a, key_a, {"code": "4870000000002", "seq": 102})
    d = ctl("dump")
    check("тот же (phone, seq) повторно -> duplicate, ok:true, строка одна",
          r1.get("code") == "added" and r2.get("code") == "duplicate" and r2.get("ok") is True and d["codes"].count("4870000000002") == 1, (r1, r2))

    # другой телефон, тот же seq
    r = call(port, "scan", phone_b, key_b, {"code": "4870000000003", "seq": 102})
    check("тот же seq с другого телефона — это другой скан -> added", r.get("code") == "added", r)

    # не найден
    ctl("reset")
    r = call(port, "scan", phone_a, key_a, {"code": "9999999999999", "seq": 103})
    d = ctl("dump")
    check("штрихкода нет в каталоге -> not_found (ok:false), в чек не попадает", r.get("code") == "not_found" and r.get("ok") is False and d["codes"] == [], r)
    r = call(port, "scan", phone_a, key_a, {"code": "9999999999999", "seq": 103})
    check("после not_found тот же seq не помечен как дубль", r.get("code") == "not_found", r)

    # плохие тела
    for name, body in [("нет seq", {"code": "4870000000001"}), ("нет code", {"seq": 5}), ("пустой code", {"code": "  ", "seq": 6}),
                       ("seq — строка", {"code": "4870000000001", "seq": "x"}), ("code длиннее 128", {"code": "1" * 200, "seq": 7})]:
        r = call(port, "scan", phone_a, key_a, body)
        check("scan bad_request: " + name, r.get("code") == "bad_request" and r.get("ok") is False, r)

    # параллельная очередь: 15 разных seq одновременно — ничего не теряется и не дублируется
    ctl("reset")
    time.sleep(1.3)
    results = {}

    def worker(i):
        results[i] = call(port, "scan", phone_a, key_a, {"code": "4870000000%03d" % i, "seq": 200 + i})

    threads = [threading.Thread(target=worker, args=(i,)) for i in range(1, 16)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    d = ctl("dump")
    check("15 сканов одновременно -> 15 разных строк, без потерь и дублей",
          sorted(d["codes"]) == sorted("4870000000%03d" % i for i in range(1, 16)) and all(r.get("code") == "added" for r in results.values()),
          "строк %d, ответы %s" % (len(d["codes"]), sorted(set(r.get("code") for r in results.values()))))


# ---------------------------------------------------------------- сценарии ревью 27.10 (Р-4: Ф-1…Ф-6)

def extra_digest(reply):
    lines = []
    for k in sorted(reply.keys()):
        if k in ("ok", "code", "msg", "sig", "sv"):
            continue
        v = reply[k]
        lines.append("%s=%s" % (k, ("true" if v else "false") if isinstance(v, bool) else str(v)))
    return sha256hex("\n".join(lines))


def reply_sig(key: bytes, op, phone, nonce, reply) -> str:
    msg = "|".join(["1r", op, phone, nonce, "1" if reply.get("ok") else "0", reply.get("code", ""), extra_digest(reply)])
    return b64u(hmac.new(key, msg.encode("utf-8"), hashlib.sha256).digest())


def signed_call(port, op, phone, key, body, **kw):
    """Запрос с известным nonce; возвращает (ответ, nonce)."""
    nonce = kw.pop("nonce", None) or new_nonce()
    return call(port, op, phone, key, body, nonce=nonce, **kw), nonce


def reply_ok(reply, key, op, phone, nonce):
    return reply.get("sv") == 1 and isinstance(reply.get("sig"), str) and hmac.compare_digest(reply["sig"], reply_sig(key, op, phone, nonce, reply))


def scenarios_review(port, phone_a, key_a, phone_b, key_b):
    log("=== Ревью 27.10 (Р-4): подпись ответа, seq на диске, оплата, окно, возраст скана, смена компании ===")
    ctl("till on")
    ctl("held off")
    ctl("window ok")
    ctl("lookupdelay 0")
    ctl("reset")
    time.sleep(1.3)

    # --- Ф-4: подпись ответа
    vec_key = bytes(range(32))
    expected = "QqWfeYfZUTG3lkbblS5JFKDlM1eGGO32e6IxR6q6NNw"
    py_sig = reply_sig(vec_key, "scan", "AAAAAAAAAAAAAAAAAAAAAA", "AQIDBAUGBwgJCgsMDQ4PEA", {"ok": True, "code": "added", "seq": 1730000000, "name": "Товар 1"})
    cs_sig = ctl("replysig").get("sig")
    check("Ф-4: формула подписи ответа: Python и C# дают одну подпись на тестовом векторе", py_sig == cs_sig == expected, (py_sig, cs_sig))

    r, n = signed_call(port, "hello", phone_a, key_a, {"name": "Айбек"})
    check("Ф-4: ответ hello подписан (sv=1, sig), подпись верна", r.get("ok") is True and reply_ok(r, key_a, "hello", phone_a, n), r)
    r, n = signed_call(port, "scan", phone_a, key_a, {"code": "4870000000005", "seq": 5001})
    check("Ф-4: ответ scan added подписан, подпись верна", r.get("code") == "added" and reply_ok(r, key_a, "scan", phone_a, n), r)
    forged = dict(r)
    forged["code"] = "duplicate"
    check("Ф-4: подмена кода в ответе ломает подпись", not reply_ok(forged, key_a, "scan", phone_a, n))
    check("Ф-4: подпись привязана к nonce запроса (чужой nonce не подходит)", not reply_ok(r, key_a, "scan", phone_a, new_nonce()))
    check("Ф-4: подпись привязана к ключу телефона (ключ другого телефона не подходит)", not reply_ok(r, key_b, "scan", phone_a, n))
    r, n = signed_call(port, "scan", phone_a, key_a, {"code": "9999999999999", "seq": 5002})
    check("Ф-4: ответ not_found подписан", r.get("code") == "not_found" and reply_ok(r, key_a, "scan", phone_a, n), r)
    r, n = signed_call(port, "hello", phone_a, key_a, {"name": "t"}, ts=int(time.time()) - 1200)
    check("Ф-4: ответ bad_time (подпись запроса верна) подписан", r.get("code") == "bad_time" and reply_ok(r, key_a, "hello", phone_a, n), r)
    r, n = signed_call(port, "hello", phone_a, os.urandom(32), {"name": "x"})
    check("Ф-4: bad_sig НЕ подписан (ключ не подтверждён)", r.get("code") == "bad_sig" and "sig" not in r and "sv" not in r, r)
    r, n = signed_call(port, "scan", new_phone(), os.urandom(32), {"code": "1", "seq": 1})
    check("Ф-4: unknown_phone НЕ подписан", r.get("code") == "unknown_phone" and "sig" not in r, r)
    # старая мобилка читает ответ как раньше: нужные поля на месте
    r, n = signed_call(port, "scan", phone_a, key_a, {"code": "4870000000006", "seq": 5003})
    check("Ф-4: поля ответа для старой мобилки прежние (ok, code, seq, name)", r.get("ok") is True and r.get("code") == "added" and r.get("seq") == 5003 and r.get("name") == "Товар 6", r)

    # --- Ф-3: идёт оплата
    ctl("reset")
    ctl("held on")
    r = call(port, "scan", phone_a, key_a, {"code": "4870000000007", "seq": 5010})
    d = ctl("dump")
    check("Ф-3: идёт оплата -> busy (не added), в чек ничего не попало", r.get("code") == "busy" and r.get("ok") is False and d["codes"] == [], r)
    ctl("held off")
    r = call(port, "scan", phone_a, key_a, {"code": "4870000000007", "seq": 5010})
    d = ctl("dump")
    check("Ф-3: после оплаты тот же seq принимается (busy seq не занял)", r.get("code") == "added" and d["codes"] == ["4870000000007"], r)

    # --- Ф-2: окно свернули между проверкой и постановкой в очередь
    ctl("reset")
    ctl("window gone")
    r = call(port, "scan", phone_a, key_a, {"code": "4870000000008", "seq": 5020})
    d = ctl("dump")
    check("Ф-2: окно закрыто/свернуто в момент постановки в очередь -> till_inactive, строка не добавлена", r.get("code") == "till_inactive" and d["codes"] == [], r)
    ctl("window ok")
    r = call(port, "scan", phone_a, key_a, {"code": "4870000000008", "seq": 5020})
    check("Ф-2: окно снова активно -> тот же seq принимается", r.get("code") == "added", r)

    # --- С-1: возраст скана
    ctl("reset")
    now = int(time.time())
    r = call(port, "scan", phone_a, key_a, {"code": "4870000000009", "seq": 5030, "at": now - 120})
    d = ctl("dump")
    check("С-1: скан старше 30 с (at) -> stale (ok:false), в чек не попал", r.get("code") == "stale" and r.get("ok") is False and d["codes"] == [], r)
    r = call(port, "scan", phone_a, key_a, {"code": "4870000000009", "seq": 5031, "at": now - 5})
    check("С-1: свежий скан (at) -> added", r.get("code") == "added", r)
    r = call(port, "scan", phone_a, key_a, {"code": "4870000000010", "seq": 5032})
    check("С-1: без at (мобилка 1.18.7) -> added как раньше", r.get("code") == "added", r)

    # --- С-2: долгий поиск товара
    ctl("reset")
    ctl("lookupdelay 6000")
    t0 = time.time()
    r = call(port, "scan", phone_a, key_a, {"code": "4870000000011", "seq": 5040})
    dt = time.time() - t0
    d = ctl("dump")
    check("С-2: поиск товара дольше 2,5 с -> ответ за ~3 с (не 15), not_found «нет связи с сервером», в чек не попал",
          r.get("code") == "not_found" and dt < 4.0 and d["codes"] == [], "%.1f с, %s" % (dt, r))
    ctl("lookupdelay 1500")
    t0 = time.time()
    r = call(port, "scan", phone_a, key_a, {"code": "4870000000011", "seq": 5041})
    dt = time.time() - t0
    check("С-2: поиск 1,5 с (в пределе) -> added", r.get("code") == "added" and dt < 4.0, "%.1f с, %s" % (dt, r))
    # два одинаковых запроса одновременно: товар один раз
    ctl("reset")
    ctl("lookupdelay 1000")
    res = {}

    def w(i):
        res[i] = call(port, "scan", phone_a, key_a, {"code": "4870000000012", "seq": 5042})

    ts_ = [threading.Thread(target=w, args=(i,)) for i in range(2)]
    for t in ts_:
        t.start()
    for t in ts_:
        t.join()
    d = ctl("dump")
    codes_ = sorted(r_.get("code") for r_ in res.values())
    check("Ф-5/гонка: два одинаковых запроса одновременно -> товар один раз (added + busy/duplicate)", d["codes"].count("4870000000012") == 1 and "added" in codes_, codes_)
    ctl("lookupdelay 0")

    # --- Ф-5: seq переживает перезапуск кассы
    ctl("reset")
    r = call(port, "scan", phone_a, key_a, {"code": "4870000000013", "seq": 5050})
    check("Ф-5: скан принят (до перезапуска)", r.get("code") == "added", r)
    ctl("stop")
    ctl("restart")   # новая память, диск прежний
    res = ctl("start")
    port2 = res["port"]
    ctl("till on")
    time.sleep(1.2)
    ctl("reset")
    r = call(port2, "scan", phone_a, key_a, {"code": "4870000000013", "seq": 5050})
    d = ctl("dump")
    check("Ф-5: после перезапуска кассы повтор (phone, seq) -> duplicate, товар НЕ добавлен второй раз", r.get("code") == "duplicate" and r.get("ok") is True and d["codes"] == [], (r, d["codes"]))
    r = call(port2, "scan", phone_a, key_a, {"code": "4870000000013", "seq": 5051})
    d = ctl("dump")
    check("Ф-5: новый seq после перезапуска принимается", r.get("code") == "added" and d["codes"] == ["4870000000013"], r)
    port = port2

    # --- Ф-6: окно nonce 25 минут (запрос с ts на 9 мин вперёд, касса повторно «через 16 мин» — раньше окно 15 мин пропускало повтор)
    ts_ahead = int(time.time()) + 540
    line = make_line("hello", phone_a, key_a, {"name": "n"}, ts=ts_ahead)
    r1, _ = send_raw(port, line)
    ctl("clock 960")
    r2, _ = send_raw(port, line)
    ctl("clock 0")
    check("Ф-6: повтор запроса через 16 мин (ts +9 мин) -> replay (окно nonce 25 мин)", (r1 or {}).get("ok") is True and (r2 or {}).get("code") == "replay", (r1, r2))

    # --- Ф-1: смена компании сбрасывает ключи в памяти
    ctl("company other-company")
    r = call(port, "scan", phone_a, key_a, {"code": "4870000000014", "seq": 5060})
    d = ctl("dump")
    check("Ф-1: после смены компании ключи прежней компании не действуют (unknown_phone), товар не добавлен", r.get("code") == "unknown_phone" and 4870000000014 not in [int(c) for c in d["codes"]], r)
    phones = ctl("phones")["phones"]
    check("Ф-1: список телефонов новой компании пуст", phones == [], phones)
    ctl("company stand-company")
    time.sleep(1.2)
    r = call(port, "scan", phone_a, key_a, {"code": "4870000000014", "seq": 5061})
    check("Ф-1: возврат в прежнюю компанию -> данные перечитаны с диска, телефон снова работает", r.get("code") == "added", r)

    # --- Ф-6: отключение телефона
    ok = ctl("revoke " + phone_b).get("ok")
    r = call(port, "scan", phone_b, key_b, {"code": "4870000000015", "seq": 5070})
    check("Ф-6: отключённый телефон -> revoked, ответ без подписи (ключ стёрт)", ok and r.get("code") == "revoked" and "sig" not in r, r)
    return port


# ---------------------------------------------------------------- запуск

def main():
    global CTL_PORT
    ap = argparse.ArgumentParser()
    ap.add_argument("--stand", help="путь к PhoneLinkStand.exe (запустить стенд самим скриптом)")
    ap.add_argument("--log", help="куда записать итоговый журнал")
    ap.add_argument("--fast", action="store_true", help="30 сканов с паузой 60 мс вместо 1 с")
    ap.add_argument("--ctl-port", type=int, default=CTL_PORT)
    args = ap.parse_args()
    CTL_PORT = args.ctl_port
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass

    proc = None
    if args.stand:
        proc = subprocess.Popen([args.stand], cwd=os.path.dirname(os.path.abspath(args.stand)), stdout=subprocess.PIPE, text=True, encoding="utf-8")
        line = proc.stdout.readline()
        log("стенд: " + line.strip())
    try:
        port, pa, ka, pb, kb = scenarios_p2()
        scenarios_p5(port, pa, ka, pb, kb, args.fast)
        scenarios_review(port, pa, ka, pb, kb)  # 2026-10-27, ревью Р-4
    finally:
        try:
            ctl("quit")
        except Exception:
            pass
        if proc:
            try:
                proc.wait(timeout=10)
            except Exception:
                proc.kill()

    failed = [n for n, ok in RESULTS if not ok]
    log("=== ИТОГО: PASS %d, FAIL %d ===" % (len(RESULTS) - len(failed), len(failed)))
    for n in failed:
        log("  FAIL: " + n)
    if args.log:
        with open(args.log, "w", encoding="utf-8") as f:
            f.write("\n".join(LOG_LINES) + "\n")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
