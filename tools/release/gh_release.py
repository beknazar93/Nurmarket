# -*- coding: utf-8 -*-
"""Выгрузка релиза на GitHub и починка прошлых релизов.

Шаги (каждый можно запустить отдельно):
  fix-feeds   — в релизах v1.17.10 и v1.17.11 releases.win.json переписывается так, чтобы в нём были
                только файлы самого релиза (из-за полной истории внутри откат качал пакет не оттуда)
  fix-tags    — теги v1.17.10 / v1.17.11 переставляются на коммиты, из которых эти версии собраны
  release     — создать релиз v<версия> с описанием и загрузить 7 файлов, releases.win.json последним
Токен берётся из адреса origin и нигде не печатается.
"""
import io, json, os, re, subprocess, sys, urllib.request, urllib.error

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")
REPO = "beknazar93/Nurmarket"
# 2026-10-05: проект переехал на D:\nurmarketDesctop — корень репозитория определяется по месту скрипта.
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
REL = ROOT + r"\NurMarketKassa.Avalonia\releases"


def token():
    # Токен в адресе origin отозван (401), рабочий лежит в token.txt.
    raw = open(os.path.join(ROOT, "token.txt"), encoding="utf-8-sig").read()
    m = re.search(r"(ghp_[A-Za-z0-9]+|github_pat_[A-Za-z0-9_]+)", raw)
    if not m:
        raise SystemExit("в token.txt нет токена")
    return m.group(1)


def git_push(*refspecs):
    """git push с токеном в заголовке, а не в адресе: он не попадает ни в вывод, ни в конфиг."""
    import base64
    header = "AUTHORIZATION: basic " + base64.b64encode(("x-access-token:" + TOK).encode()).decode()
    out = subprocess.run(
        ["git", "-C", ROOT, "-c", "credential.helper=", "-c", "http.extraheader=" + header,
         "push", f"https://github.com/{REPO}.git", *refspecs],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    text = (out.stdout + out.stderr).replace(TOK, "***")
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


def upload(release, path, name=None, ctype="application/octet-stream"):
    name = name or os.path.basename(path)
    for a in release["assets"]:
        if a["name"] == name:
            st, _ = api("DELETE", f"https://api.github.com/repos/{REPO}/releases/assets/{a['id']}")
            print(f"  удалён старый {name}: HTTP {st}")
    with open(path, "rb") as f:
        data = f.read()
    url = release["upload_url"].split("{")[0] + "?name=" + urllib.request.quote(name)
    st, r = api("POST", url, raw=data, headers={"Content-Type": ctype})
    print(f"  загружен {name} ({len(data)} байт): HTTP {st}")
    if st not in (200, 201):
        raise SystemExit(f"загрузка {name} не удалась: {r}")


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
    if release_by_tag(tag):
        raise SystemExit(f"{tag} уже существует — не трогаю")
    st, rel = api("POST", f"https://api.github.com/repos/{REPO}/releases", {
        "tag_name": tag, "target_commitish": commit_sha, "name": f"NurMarketKassa {version}",
        "body": body, "draft": False, "prerelease": prerelease,
    })
    print(f"релиз {tag}: HTTP {st}")
    if st != 201:
        raise SystemExit(rel)
    for name in ("NurMarketKassa-win-Setup.exe", "NurMarketKassa-win-Portable.zip",
                 f"NurMarketKassa-{version}-full.nupkg", f"NurMarketKassa-{version}-delta.nupkg",
                 "RELEASES", "assets.win.json"):
        upload(rel, os.path.join(REL, name))
    # releases.win.json — последним: по нему кассы узнают, что вышло обновление. И только со
    # своими файлами, иначе откат снова будет искать пакеты не в том релизе.
    with open(os.path.join(REL, "releases.win.json"), "rb") as f:
        new = own_feed(f.read(), version)
    tmp = os.path.join(os.environ.get("TEMP", "."), f"releases.win.{version}.json")
    with open(tmp, "wb") as f:
        f.write(new)
    upload(rel, tmp, "releases.win.json", "application/json")
    print("готово:", rel["html_url"])


if __name__ == "__main__":
    step = sys.argv[1]
    if step == "fix-feeds":
        fix_feeds()
    elif step == "fix-tags":
        fix_tags([p.split("=") for p in sys.argv[2:]])
    elif step == "release":
        version, sha, body_file = sys.argv[2], sys.argv[3], sys.argv[4]
        make_release(version, sha, io.open(body_file, encoding="utf-8").read(), prerelease="--prerelease" in sys.argv)
    elif step == "push":
        git_push(*sys.argv[2:])
    elif step == "check":
        st, u = api("GET", "https://api.github.com/user")
        print("токен:", "рабочий" if st == 200 else f"HTTP {st}", "| права push на репозиторий:",
              api("GET", f"https://api.github.com/repos/{REPO}")[1].get("permissions", {}).get("push"))


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


if __name__ == "__main__" and sys.argv[1] == "update":
    update_release(sys.argv[2], io.open(sys.argv[3], encoding="utf-8").read())
