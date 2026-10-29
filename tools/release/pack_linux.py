#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""2026-10-29: сборка кассы для Linux x64 на Windows: self-contained публикация NurMarketKassa.Portable и tar.gz
с README, ярлыками (install-desktop.sh), значком и скриптом программы владельца. Ничего не выкладывает.

Запуск из корня репозитория:
    python tools/release/pack_linux.py                 # артефакт в C:\\Users\\User\\NurMarket-rel1763
    python tools/release/pack_linux.py --skip-publish  # только упаковка уже опубликованного linux-pub
"""
import argparse, hashlib, os, re, shutil, subprocess, tarfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
SLN = os.path.join(ROOT, 'NurMarketKassa.Avalonia')
PROJ = os.path.join(SLN, 'NurMarketKassa.Portable', 'NurMarketKassa.Portable.csproj')
EXTRA_DIR = os.path.join(SLN, 'NurMarketKassa.Portable', 'linux')
EXTRA = ('README-LINUX.txt', 'nurmarket-owner.sh', 'install-desktop.sh', 'nurmarket-kassa.png')
EXECUTABLE = ('NurMarketKassa.Avalonia', 'nurmarket-owner.sh', 'install-desktop.sh', 'createdump')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default=r'C:\Users\User\NurMarket-rel1763')
    ap.add_argument('--skip-publish', action='store_true')
    a = ap.parse_args()
    version = re.search(r'<Version>([^<]+)</Version>', open(PROJ, encoding='utf-8').read()).group(1)
    pub = os.path.join(a.out, 'linux-pub')
    if not a.skip_publish:
        lock = None
        if os.name == 'nt':
            import ctypes
            k = ctypes.windll.kernel32
            h = k.CreateMutexW(None, False, 'Global\\NurMarketBuildLock')
            if k.WaitForSingleObject(h, 900000) not in (0, 0x80):
                raise SystemExit('BUILD-LOCK-BUSY: другой агент собирает, повторите позже')
            lock = (k, h)
        try:
            if os.path.isdir(pub):
                shutil.rmtree(pub)
            r = subprocess.run(['dotnet', 'publish', PROJ, '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'true',
                                '-o', pub, '-nologo', '-v', 'q', '-m:1', '-nodeReuse:false'], cwd=SLN)
            if r.returncode != 0:
                raise SystemExit('dotnet publish linux-x64 не удался (код %d)' % r.returncode)
        finally:
            if lock:
                lock[0].ReleaseMutex(lock[1])
                lock[0].CloseHandle(lock[1])

    files = []
    for r, _, fs in os.walk(pub):
        for f in fs:
            if f.lower().endswith('.pdb'):
                continue
            full = os.path.join(r, f)
            files.append((os.path.relpath(full, pub).replace('\\', '/'), full))
    for f in EXTRA:
        files.append((f, os.path.join(EXTRA_DIR, f)))
    files.sort()
    out = os.path.join(a.out, 'nurmarket-kassa-linux-x64-%s-adapt.tar.gz' % version)
    with tarfile.open(out, 'w:gz') as t:
        for arc, full in files:
            ti = tarfile.TarInfo(arc)
            data = open(full, 'rb').read()
            if arc.endswith('.sh'):
                data = data.replace(b'\r\n', b'\n')  # скрипты с CRLF в Linux не запускаются
            ti.size = len(data)
            ti.mtime = int(os.path.getmtime(full))
            ti.mode = 0o755 if arc in EXECUTABLE else 0o644
            ti.uid = ti.gid = 0
            import io
            t.addfile(ti, io.BytesIO(data))
    print(len(files), os.path.getsize(out), hashlib.sha256(open(out, 'rb').read()).hexdigest(), out)


if __name__ == '__main__':
    main()
