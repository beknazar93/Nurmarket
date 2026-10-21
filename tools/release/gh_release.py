# -*- coding: utf-8 -*-
"""Выгрузка релиза на GitHub и починка прошлых релизов.

Шаги (каждый можно запустить отдельно):
  fix-feeds   — в релизах v1.17.10 и v1.17.11 releases.win.json переписывается так, чтобы в нём были
                только файлы самого релиза (из-за полной истории внутри откат качал пакет не оттуда)
  fix-tags    — теги v1.17.10 / v1.17.11 переставляются на коммиты, из которых эти версии собраны
  release     — создать релиз v<версия> с описанием и загрузить 7 файлов, releases.win.json последним
  resume      — догрузить недостающие файлы релиза (после обрыва 10054) и опубликовать его
  update      — заменить файлы и описание уже опубликованного тестового релиза
  push        — git push с токеном
  check       — проверить токен и права
Токен берётся из token.txt в корне репозитория и нигде не печатается.

Запускать из корня репозитория (пути к файлам описания — от текущей папки):
  python tools/release/gh_release.py release X.Y.Z <полный sha> NurMarketKassa.Avalonia/NurMarketKassa.Avalonia/release-notes-X.Y.Z.md [--prerelease]
  python tools/release/gh_release.py resume X.Y.Z
"""
import io, json, os, re, subprocess, sys, urllib.request, urllib.error

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")
REPO = "beknazar93/Nurmarket"
# 2026-10-05: проект переехал на D:\nurmarketDesctop — корень репозитория определяется по месту скрипта.
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
REL = ROOT + r"\NurMarketKassa.Avalonia\releases"


def token():
    # Токен в адресе origin отозван (401), рабочий лежит в token.txt.
    path = os.path.join(ROOT, "token.txt")
    if not os.path.exists(path):
        raise SystemExit(f"нет файла {path} — положите туда токен GitHub (файл в .gitignore)")
    raw = open(path, encoding="utf-8-sig").read()
    m = re.search(r"(ghp_[A-Za-z0-9]+|github_pat_[A-Za-z0-9_]+)", raw)
    if not m:
        raise SystemExit("в token.txt нет токена")
    return m.group(1)


def git_push(*refspecs):
    """git push с токеном в заголовке, а не в адресе: он не попадает ни в вывод, ни в конфиг."""
    import base64
    b64 = base64.b64encode(("x-access-token:" + TOK).encode()).decode()
    header = "AUTHORIZATION: basic " + b64
    # 2026-10-15: заголовок передаётся через GIT_CONFIG_* в окружении, а не "-c" в командной строке —
    # командную строку видно в списке процессов. Трассировка git (GIT_TRACE*, GIT_CURL_VERBOSE) печатает
    # заголовки, поэтому снимается; в выводе прячется и сам токен, и его base64.
    env = {k: v for k, v in os.environ.items()
           if not (k.startswith("GIT_TRACE") or k == "GIT_CURL_VERBOSE" or k.startswith("GIT_CONFIG_"))}
    env.update({"GIT_CONFIG_COUNT": "2",
                "GIT_CONFIG_KEY_0": "credential.helper", "GIT_CONFIG_VALUE_0": "",
                "GIT_CONFIG_KEY_1": "http.extraheader", "GIT_CONFIG_VALUE_1": header})
    out = subprocess.run(
        ["git", "-C", ROOT, "push", f"https://github.com/{REPO}.git", *refspecs],
        capture_output=True, text=True, encoding="utf-8", errors="replace", env=env)
    text = (out.stdout + out.stderr).replace(TOK, "***").replace(b64, "***")
    print(text.strip()[-1500:])
    if out.returncode != 0:
        raise SystemExit("git push не прошёл")


TOK = token()


def api(method, url, data=None, headers=None, raw=None):
    h = {"Authorization": "token " + TOK, "Accept": "application/vnd.github+json", "User-Agent": "nurkassa-release"}
    if headers:
        h.update(headers)
    body = raw if raw is not None else (json.dumps(data).encode() if data is not None else None)
    if data is not None and raw is None:
        h["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=body, method=method, headers=h)
    try:
        with urllib.request.urlopen(req, timeout=600) as r:
            txt = r.read()
            return r.status, (json.loads(txt) if txt else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")[:500]


def release_by_tag(tag):
    st, r = api("GET", f"https://api.github.com/repos/{REPO}/releases/tags/{tag}")
    return r if st == 200 else None


def release_assets(release):
    st, r = api("GET", f"https://api.github.com/repos/{REPO}/releases/{release['id']}/assets?per_page=100")
    return r if st == 200 and isinstance(r, list) else release.get("assets", [])


def upload(release, path, name=None, ctype="application/octet-stream"):
    name = name or os.path.basename(path)
    if not os.path.exists(path):
        raise SystemExit(f"нет файла {path}")
    with open(path, "rb") as f:
        data = f.read()
    url = release["upload_url"].split("{")[0] + "?name=" + urllib.request.quote(name)
    # 2026-10-15: до трёх попыток — загрузка ~350 МБ рвётся (10054). Перед каждой попыткой убирается
    # файл с тем же именем (в т. ч. недогруженный — state "starter"); не удалился — дальше не идём,
    # иначе GitHub ответит 422 «already_exists».
    last = None
    for attempt in range(1, 4):
        for a in release_assets(release):
            if a["name"] == name:
                st, _ = api("DELETE", f"https://api.github.com/repos/{REPO}/releases/assets/{a['id']}")
                print(f"  удалён старый {name}: HTTP {st}")
                if st != 204:
                    raise SystemExit(f"не удалось удалить старый {name} (HTTP {st}) — загрузка остановлена")
        try:
            st, r = api("POST", url, raw=data, headers={"Content-Type": ctype})
        except (urllib.error.URLError, ConnectionError, TimeoutError, OSError) as e:
            st, r = None, str(e)
        print(f"  загружен {name} ({len(data)} байт): HTTP {st}" + ("" if st in (200, 201) else f", попытка {attempt}"))
        if st in (200, 201):
            return
        last = r
    raise SystemExit(f"загрузка {name} не удалась: {last}")


def asset_names(version):
    return ("NurMarketKassa-win-Setup.exe", "NurMarketKassa-win-Portable.zip",
            f"NurMarketKassa-{version}-full.nupkg", f"NurMarketKassa-{version}-delta.nupkg",
            "RELEASES", "assets.win.json")


def find_release(tag):
    """Релиз по тегу, включая черновик (releases/tags/{tag} черновиков не видит)."""
    rel = release_by_tag(tag)
    if rel:
        return rel
    st, lst = api("GET", f"https://api.github.com/repos/{REPO}/releases?per_page=30")
    if st == 200 and isinstance(lst, list):
        return next((r for r in lst if r.get("tag_name") == tag), None)
    return None


def upload_feed_and_publish(rel, version, prerelease):
    """releases.win.json — последним (по нему кассы узнают об обновлении), затем черновик публикуется."""
    with open(os.path.join(REL, "releases.win.json"), "rb") as f:
        new = own_feed(f.read(), version)
    tmp = os.path.join(os.environ.get("TEMP", "."), f"releases.win.{version}.json")
    with open(tmp, "wb") as f:
        f.write(new)
    upload(rel, tmp, "releases.win.json", "application/json")
    if rel.get("draft"):
        st, pub = api("PATCH", f"https://api.github.com/repos/{REPO}/releases/{rel['id']}",
                      {"draft": False, "prerelease": prerelease})
        print(f"опубликован: HTTP {st}")
        if st != 200:
            raise SystemExit(f"файлы загружены, но релиз остался черновиком: {pub}")
        rel = pub
    print("готово:", rel["html_url"])


def own_feed(feed_bytes, version):
    feed = json.loads(feed_bytes.decode("utf-8-sig"))
    feed["Assets"] = [a for a in feed["Assets"] if a["Version"] == version]
    return json.dumps(feed, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def fix_feeds():
    for version in ("1.17.10", "1.17.11"):
        rel = release_by_tag("v" + version)
        if not rel:
            print(f"v{version}: релиза нет"); continue
        asset = next(a for a in rel["assets"] if a["name"] == "releases.win.json")
        req = urllib.request.Request(asset["browser_download_url"], headers={"User-Agent": "nurkassa-release"})
        with urllib.request.urlopen(req, timeout=60) as r:
            old = r.read()
        new = own_feed(old, version)
        kept = [(a["Version"], a["Type"]) for a in json.loads(new)["Assets"]]
        print(f"v{version}: в releases.win.json остаются {kept}")
        tmp = os.path.join(os.environ.get("TEMP", "."), f"releases.win.{version}.json")
        with open(tmp, "wb") as f:
            f.write(new)
        upload(rel, tmp, "releases.win.json", "application/json")


def fix_tags(pairs):
    for tag, sha in pairs:
        st, r = api("PATCH", f"https://api.github.com/repos/{REPO}/git/refs/tags/{tag}", {"sha": sha, "force": True})
        print(f"{tag} -> {sha[:7]}: HTTP {st}" + ("" if st == 200 else f" {r}"))


def make_release(version, commit_sha, body, prerelease=False):
    tag = "v" + version
    if find_release(tag):
        raise SystemExit(f"{tag} уже существует — не трогаю (недогруженный черновик догружает шаг resume)")
    # 2026-10-15: релиз создаётся черновиком и публикуется только после загрузки всех файлов —
    # при обрыве загрузки кассы не видят полупустой релиз, а шаг resume его догружает.
    st, rel = api("POST", f"https://api.github.com/repos/{REPO}/releases", {
        "tag_name": tag, "target_commitish": commit_sha, "name": f"NurMarketKassa {version}",
        "body": body, "draft": True, "prerelease": prerelease,
    })
    print(f"релиз {tag} (черновик): HTTP {st}")
    if st != 201:
        raise SystemExit(rel)
    for name in asset_names(version):
        upload(rel, os.path.join(REL, name))
    # releases.win.json — последним: по нему кассы узнают, что вышло обновление. И только со
    # своими файлами, иначе откат снова будет искать пакеты не в том релизе.
    upload_feed_and_publish(rel, version, prerelease)


def resume_release(version, prerelease):
    """2026-10-15: догрузить релиз после обрыва — загружаются только отсутствующие или недогруженные
    файлы (state != uploaded или размер не совпал), releases.win.json — заново и последним."""
    tag = "v" + version
    rel = find_release(tag)
    if not rel:
        raise SystemExit(f"{tag}: релиза нет — запустите шаг release")
    have = {a["name"]: a for a in release_assets(rel)}
    for name in asset_names(version):
        path = os.path.join(REL, name)
        a = have.get(name)
        if a and a.get("state") == "uploaded" and os.path.exists(path) and a.get("size") == os.path.getsize(path):
            print(f"  {name}: уже загружен")
            continue
        upload(rel, path)
    upload_feed_and_publish(rel, version, prerelease if rel.get("draft") else rel.get("prerelease", False))


def read_body(path):
    # 2026-10-15: utf-8-sig — описание из редактора с BOM иначе начиналось с невидимого символа.
    return io.open(path, encoding="utf-8-sig").read()


def update_release(version, body):
    """Обновить уже существующий тестовый релиз: описание и все файлы, releases.win.json последним.
    Пока файлы меняются, releases.win.json снят — кассы не увидят версию с полупустыми файлами."""
    tag = "v" + version
    rel = release_by_tag(tag)
    if not rel:
        raise SystemExit(f"{tag}: релиза нет")
    st, _ = api("PATCH", f"https://api.github.com/repos/{REPO}/releases/{rel['id']}",
                {"body": body, "name": f"NurMarketKassa {version}", "prerelease": True})
    print(f"описание и пометка «тестовая»: HTTP {st}")
    for a in rel["assets"]:
        if a["name"] == "releases.win.json":
            st, _ = api("DELETE", f"https://api.github.com/repos/{REPO}/releases/assets/{a['id']}")
            print(f"  снят releases.win.json на время загрузки: HTTP {st}")
    rel = release_by_tag(tag)
    for name in ("NurMarketKassa-win-Setup.exe", "NurMarketKassa-win-Portable.zip",
                 f"NurMarketKassa-{version}-full.nupkg", f"NurMarketKassa-{version}-delta.nupkg",
                 "RELEASES", "assets.win.json"):
        upload(rel, os.path.join(REL, name))
        rel = release_by_tag(tag)
    with open(os.path.join(REL, "releases.win.json"), "rb") as f:
        new = own_feed(f.read(), version)
    tmp = os.path.join(os.environ.get("TEMP", "."), f"releases.win.{version}.json")
    with open(tmp, "wb") as f:
        f.write(new)
    upload(rel, tmp, "releases.win.json", "application/json")
    rel = release_by_tag(tag)
    print("файлы:", sorted(a["name"] for a in rel["assets"]))
    print("готово:", rel["html_url"], "| prerelease =", rel["prerelease"])


USAGE = __doc__


if __name__ == "__main__":
    # 2026-10-15: один разбор аргументов (раньше update разбирался отдельным блоком после основного,
    # а запуск без аргументов падал с IndexError).
    args = [a for a in sys.argv[1:] if a != "--prerelease"]
    pre = "--prerelease" in sys.argv
    step = args[0] if args else ""
    need = {"release": 4, "resume": 2, "update": 3}
    if step not in ("fix-feeds", "fix-tags", "release", "resume", "update", "push", "check") or len(args) < need.get(step, 1):
        raise SystemExit(USAGE)
    if step == "fix-feeds":
        fix_feeds()
    elif step == "fix-tags":
        fix_tags([p.split("=") for p in args[1:]])
    elif step == "release":
        make_release(args[1], args[2], read_body(args[3]), prerelease=pre)
    elif step == "resume":
        resume_release(args[1], pre)
    elif step == "update":
        update_release(args[1], read_body(args[2]))
    elif step == "push":
        git_push(*args[1:])
    elif step == "check":
        st, u = api("GET", "https://api.github.com/user")
        st2, repo = api("GET", f"https://api.github.com/repos/{REPO}")
        push = repo.get("permissions", {}).get("push") if isinstance(repo, dict) else f"HTTP {st2}"
        print("токен:", "рабочий" if st == 200 else f"HTTP {st}", "| права push на репозиторий:", push)
