#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""2026-10-29: сборка кассы для macOS на Windows (osx-arm64 и osx-x64): self-contained публикация проекта
NurMarketKassa.Portable, упаковка в «NurMarket Kassa.app» (Info.plist, иконка .icns) и zip-архив.

Запуск из корня репозитория:
    python tools/release/build_mac.py                  # обе архитектуры, артефакты в C:\\Users\\User\\NurMarket-rel1763
    python tools/release/build_mac.py --rid osx-arm64  # только Apple Silicon
    python tools/release/build_mac.py --skip-publish   # только упаковка уже опубликованного (mac-pub-<rid>)
Подпись и нотаризация на Windows недоступны: приложение НЕ подписано (см. mac/README-MAC.txt в архиве:
xattr -cr + codesign -s - на самом Mac). Ничего не выкладывает, токены не использует.
"""
import argparse, os, plistlib, re, shutil, struct, subprocess, zipfile, io

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
SLN = os.path.join(ROOT, 'NurMarketKassa.Avalonia')
PROJ = os.path.join(SLN, 'NurMarketKassa.Portable', 'NurMarketKassa.Portable.csproj')
MAC_RES = os.path.join(SLN, 'NurMarketKassa.Portable', 'mac')
ICON_SRC = os.path.join(SLN, 'NurMarketKassa.Avalonia', 'Assets', 'app-icon.ico')
APP_NAME = 'NurMarket Kassa'
EXE_NAME = 'NurMarketKassa.Avalonia'   # AssemblyName проекта
BUNDLE_ID = 'kg.nurmarket.kassa'


def project_version():
    text = open(PROJ, encoding='utf-8').read()
    m = re.search(r'<Version>([^<]+)</Version>', text)
    return m.group(1) if m else '0.0.0'


def take_build_lock():
    """Общий замок сборки (Global\\NurMarketBuildLock): несколько агентов не собирают одновременно."""
    if os.name != 'nt':
        return None
    import ctypes
    k = ctypes.windll.kernel32
    h = k.CreateMutexW(None, False, 'Global\\NurMarketBuildLock')
    r = k.WaitForSingleObject(h, 900000)
    if r not in (0, 0x80):  # WAIT_OBJECT_0 / WAIT_ABANDONED
        raise SystemExit('BUILD-LOCK-BUSY: другой агент собирает, повторите позже')
    return (k, h)


def release_build_lock(lock):
    if lock:
        lock[0].ReleaseMutex(lock[1])
        lock[0].CloseHandle(lock[1])


def publish(rid, pub_dir):
    if os.path.isdir(pub_dir):
        shutil.rmtree(pub_dir)
    cmd = ['dotnet', 'publish', PROJ, '-c', 'Release', '-r', rid, '--self-contained', 'true',
           '-o', pub_dir, '-nologo', '-v', 'q', '-m:1', '-nodeReuse:false', '-p:DebugType=none', '-p:DebugSymbols=false']
    print('>', ' '.join(cmd), flush=True)
    r = subprocess.run(cmd, cwd=SLN)
    if r.returncode != 0:
        raise SystemExit('dotnet publish %s не удался (код %d)' % (rid, r.returncode))


def make_icns():
    """icns из самого крупного кадра app-icon.ico (PNG-кадры внутри icns поддерживаются macOS 10.7+)."""
    from PIL import Image
    ico = Image.open(ICON_SRC)
    best = max(ico.info.get('sizes', {(256, 256)}), key=lambda s: s[0])
    ico.size = best
    base = ico.convert('RGBA')
    chunks = []
    for tag, px in (('icp4', 16), ('icp5', 32), ('icp6', 64), ('ic07', 128), ('ic08', 256), ('ic09', 512),
                    ('ic11', 32), ('ic12', 64), ('ic13', 256), ('ic14', 512)):
        buf = io.BytesIO()
        base.resize((px, px), Image.LANCZOS).save(buf, 'PNG')
        data = buf.getvalue()
        chunks.append(tag.encode('ascii') + struct.pack('>I', len(data) + 8) + data)
    body = b''.join(chunks)
    return b'icns' + struct.pack('>I', len(body) + 8) + body


def info_plist(version):
    return plistlib.dumps({
        'CFBundleName': APP_NAME,
        'CFBundleDisplayName': APP_NAME,
        'CFBundleIdentifier': BUNDLE_ID,
        'CFBundleVersion': version,
        'CFBundleShortVersionString': version,
        'CFBundleExecutable': EXE_NAME,
        'CFBundleIconFile': 'NurMarketKassa',
        'CFBundlePackageType': 'APPL',
        'CFBundleInfoDictionaryVersion': '6.0',
        'CFBundleDevelopmentRegion': 'ru',
        'LSMinimumSystemVersion': '10.15',
        'LSApplicationCategoryType': 'public.app-category.business',
        'NSHighResolutionCapable': True,
        'NSSupportsAutomaticGraphicsSwitching': True,
        'NSCameraUsageDescription': 'Камера нужна для сканирования штрихкодов.',
        'NSMicrophoneUsageDescription': 'Микрофон нужен для голосового ввода.',
        'NSHumanReadableCopyright': 'NurMarket',
    })


def build_zip(rid, pub_dir, out_dir, version):
    arch = 'arm64' if rid.endswith('arm64') else 'x64'
    zip_path = os.path.join(out_dir, 'nurmarket-kassa-macos-%s-%s.zip' % (arch, version))
    if os.path.exists(zip_path):
        os.remove(zip_path)
    icns = make_icns()
    plist = info_plist(version)
    app = APP_NAME + '.app'

    def add(zf, arc, data, mode):
        zi = zipfile.ZipInfo(arc)
        zi.create_system = 3  # Unix: иначе права (бит запуска) не сохраняются
        zi.external_attr = (0o100000 | mode) << 16
        zi.compress_type = zipfile.ZIP_DEFLATED
        zi.date_time = (2026, 10, 29, 12, 0, 0)
        zf.writestr(zi, data)

    count = 0
    with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        add(zf, app + '/Contents/Info.plist', plist, 0o644)
        add(zf, app + '/Contents/Resources/NurMarketKassa.icns', icns, 0o644)
        for r, _, files in os.walk(pub_dir):
            for f in sorted(files):
                if f.lower().endswith('.pdb'):
                    continue
                full = os.path.join(r, f)
                rel = os.path.relpath(full, pub_dir).replace('\\', '/')
                mode = 0o755 if (f == EXE_NAME or f == 'createdump' or f.endswith('.dylib')) else 0o644
                with open(full, 'rb') as fh:
                    add(zf, app + '/Contents/MacOS/' + rel, fh.read(), mode)
                count += 1
        for name, mode in (('README-MAC.txt', 0o644), ('nurmarket-owner.command', 0o755)):
            with open(os.path.join(MAC_RES, name), 'rb') as fh:
                data = fh.read().replace(b'\r\n', b'\n')  # .command с CRLF в macOS не запускается
            add(zf, name, data, mode)
    print('%s: %d файлов, %.1f МБ' % (zip_path, count, os.path.getsize(zip_path) / 1048576))
    return zip_path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--rid', choices=['osx-arm64', 'osx-x64', 'both'], default='both')
    ap.add_argument('--out', default=r'C:\Users\User\NurMarket-rel1763')
    ap.add_argument('--skip-publish', action='store_true')
    a = ap.parse_args()
    rids = ['osx-arm64', 'osx-x64'] if a.rid == 'both' else [a.rid]
    os.makedirs(a.out, exist_ok=True)
    version = project_version()
    lock = None if a.skip_publish else take_build_lock()
    try:
        for rid in rids:
            pub_dir = os.path.join(a.out, 'mac-pub-' + rid)
            if not a.skip_publish:
                publish(rid, pub_dir)
            build_zip(rid, pub_dir, a.out, version)
    finally:
        release_build_lock(lock)


if __name__ == '__main__':
    main()
