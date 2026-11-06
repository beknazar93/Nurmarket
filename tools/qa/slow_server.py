# 2026-11-03: стенд «медленный сервер NurCRM» (аудит жалобы «сервер зависает — зависает касса»).
# 2026-11-06: В5 (шаг 25, п. 31.1) — перенесён в репозиторий (tools/qa) из стенда live-nbs-1103; используется weak-run.ps1. Секретов в файле нет.
# Поддельный сервер на 127.0.0.1 (HTTP/1.1, свой разбор). Режимы задаются правилами по пути (регулярное выражение, первое совпадение — новейшее):
#   fast            — сразу ответить заглушкой JSON
#   delay  s=N      — ответить через N секунд
#   blackhole       — принять соединение, прочитать запрос и молчать (до закрытия клиентом, максимум 15 мин)
#   slowbytes s=N   — заголовки сразу, тело по одному байту, растянуто на N секунд
#   status code=K   — сразу ответить кодом K (например 401, 502)
#   proxy  s=N      — переслать на --upstream (например https://app.nurcrm.kg) с задержкой N с (для живой кассы на тестовом аккаунте)
# Управление:  GET /__set?path=<regex>&mode=<режим>&s=<сек>&code=<код>   GET /__reset   GET /__stats
# Запуск: python -I tools/qa/slow_server.py --port 18777 [--upstream https://app.nurcrm.kg] [--rule "api/=delay:20"]
import argparse, json, re, socket, socketserver, threading, time, urllib.parse, urllib.request, ssl, sys

RULES = []          # (regex, mode, seconds, code)
LOCK = threading.Lock()
STATS = {"total": 0, "by_mode": {}, "open_blackholes": 0}
UPSTREAM = None


def stub_body(path):
    p = path.lower()
    if "auth/login" in p or "auth/refresh" in p:
        return {"access": "stand-access", "refresh": "stand-refresh"}
    if "users/profile" in p:
        return {"id": "u-stand", "email": "stand@test.local", "full_name": "Стенд", "role": "owner", "permissions": []}
    if "users/company" in p:
        return {"id": "c-stand", "name": "Стенд медленного сервера", "inn": "", "address": ""}
    return {"count": 0, "next": None, "previous": None, "results": []}


def pick(path):
    with LOCK:
        for rx, mode, s, code in RULES:
            if re.search(rx, path):
                return mode, s, code
    return "fast", 0.0, 200


class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        sock = self.request
        sock.settimeout(900)
        f = sock.makefile("rb")
        while True:
            line = f.readline()
            if not line:
                return
            try:
                method, target, _ = line.decode("latin-1").split(" ", 2)
            except ValueError:
                return
            headers = {}
            while True:
                h = f.readline()
                if not h or h in (b"\r\n", b"\n"):
                    break
                k, _, v = h.decode("latin-1").partition(":")
                headers[k.strip().lower()] = v.strip()
            body = b""
            n = int(headers.get("content-length", "0") or 0)
            if n:
                body = f.read(n)
            path = urllib.parse.urlsplit(target).path
            if path.startswith("/__"):
                self.control(sock, target)
                continue
            mode, s, code = pick(path)
            with LOCK:
                STATS["total"] += 1
                STATS["by_mode"][mode] = STATS["by_mode"].get(mode, 0) + 1
            t0 = time.time()
            print(f"{time.strftime('%H:%M:%S')} {method} {path} -> {mode} {s or ''}", flush=True)
            if mode == "blackhole":
                with LOCK:
                    STATS["open_blackholes"] += 1
                try:
                    sock.settimeout(1.0)
                    while time.time() - t0 < 900:
                        try:
                            if sock.recv(1, socket.MSG_PEEK) == b"":
                                break  # клиент закрыл соединение
                        except socket.timeout:
                            continue
                        except OSError:
                            break
                finally:
                    with LOCK:
                        STATS["open_blackholes"] -= 1
                return
            if mode == "proxy":
                time.sleep(s)
                self.proxy(sock, method, target, headers, body)
                continue
            if mode == "delay":
                time.sleep(s)
            if mode == "status":
                self.send(sock, code, {"detail": f"stand status {code}"})
                continue
            payload = json.dumps(stub_body(path), ensure_ascii=False).encode("utf-8")
            if mode == "slowbytes":
                payload = payload + b" " * max(0, int(s * 2) - len(payload))
                head = f"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {len(payload)}\r\n\r\n".encode()
                try:
                    sock.sendall(head)
                    step = s / max(1, len(payload))
                    for i in range(len(payload)):
                        sock.sendall(payload[i:i + 1])
                        time.sleep(step)
                except OSError:
                    return
                continue
            self.send(sock, 200, None, payload)

    def send(self, sock, code, obj, raw=None):
        payload = raw if raw is not None else json.dumps(obj).encode()
        reason = {200: "OK", 401: "Unauthorized", 404: "Not Found", 429: "Too Many Requests", 502: "Bad Gateway"}.get(code, "X")
        head = f"HTTP/1.1 {code} {reason}\r\nContent-Type: application/json\r\nContent-Length: {len(payload)}\r\n\r\n".encode()
        try:
            sock.sendall(head + payload)
        except OSError:
            pass

    def control(self, sock, target):
        u = urllib.parse.urlsplit(target)
        q = dict(urllib.parse.parse_qsl(u.query))
        if u.path == "/__set":
            with LOCK:
                RULES.insert(0, (q.get("path", ".*"), q.get("mode", "fast"), float(q.get("s", "0")), int(q.get("code", "200"))))
            self.send(sock, 200, {"ok": True, "rules": len(RULES)})
        elif u.path == "/__reset":
            with LOCK:
                RULES.clear()
            self.send(sock, 200, {"ok": True})
        else:
            with LOCK:
                self.send(sock, 200, dict(STATS, rules=[r[:2] + (r[2],) for r in RULES]))

    def proxy(self, sock, method, target, headers, body):
        url = UPSTREAM.rstrip("/") + target
        fwd = {k: v for k, v in headers.items() if k not in ("host", "connection", "content-length", "accept-encoding")}
        # 2026-11-03 (live-nbs-1103): сервер NurCRM (WAF) отвечает 403 на User-Agent «Python-urllib» — без своего UA подставляем нейтральный
        if "user-agent" not in fwd:
            fwd["User-Agent"] = "NurCRM-Kassa-Stand/1.0"
        req = urllib.request.Request(url, data=body or None, method=method, headers=fwd)
        try:
            with urllib.request.urlopen(req, timeout=60, context=ssl.create_default_context()) as r:
                data, code = r.read(), r.status
        except urllib.error.HTTPError as e:
            data, code = e.read(), e.code
        except Exception as e:
            data, code = json.dumps({"detail": f"proxy error {e}"}).encode(), 502
        # 2026-11-03 (live-nbs-1103): журнал — имена заголовков запроса и код ответа (без значений)
        print(f"   <- {code} hdrs={sorted(fwd)} body={len(body or b'')}", flush=True)
        self.send(sock, code, None, data)


class Server(socketserver.ThreadingTCPServer):
    daemon_threads = True
    allow_reuse_address = True


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=18777)
    ap.add_argument("--upstream", default=None)
    ap.add_argument("--rule", action="append", default=[], help="regex=mode[:seconds]")
    a = ap.parse_args()
    UPSTREAM = a.upstream
    for r in a.rule:
        rx, _, m = r.rpartition("=")
        mode, _, s = m.partition(":")
        RULES.insert(0, (rx, mode, float(s or 0), int(s or 200) if mode == "status" else 200))
    srv = Server(("127.0.0.1", a.port), Handler)
    print(f"slow-server on http://127.0.0.1:{a.port}  upstream={UPSTREAM}", flush=True)
    srv.serve_forever()
