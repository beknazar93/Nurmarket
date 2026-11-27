# -*- coding: utf-8 -*-
"""Сборка рекламных роликов NurCRM по сферам магазина (9:16, 1080x1920, 30 к/с, H.264 + aac, 20-28 с).

  python build.py                          — все сферы, оба языка (макеты экранов рисуются, если их нет)
  python build.py --sphere grocery         — одна сфера (можно несколько через запятую)
  python build.py --lang ky                — один язык
  python build.py --texts-only             — только тексты: макеты экранов НЕ перерисовываются
  python build.py --frames                 — быстро: без видео, кадры каждые 0,5 с в папку frames_preview/<сфера>
Все надписи — texts.json. Макеты — screens.py (+ ui.py). Музыка — music_s.py (синтез, без чужих материалов).
Результат: ../<сфера>/nurcrm-<сфера>-<ru|ky>.mp4 и ...-cover.jpg. Нужны Pillow, Chrome (макеты) и ffmpeg (C:\\Tools\\ffmpeg)."""
import argparse, json, math, pathlib, subprocess, sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter

HERE = pathlib.Path(__file__).resolve().parent
OUT = HERE.parent
MINI = (HERE / '../../src').resolve()  # готовый ролик «Мини»: из него берутся только макеты телефона (торг Базара)
FONT_DIR = (HERE / '../../../../NurMarketMobile/assets/fonts').resolve()
LOGO = (HERE / '../../../../NurMarketMobile/assets/images/logo.png').resolve()
FFMPEG = r'C:\Tools\ffmpeg\ffmpeg.exe'
W, H, FPS = 1080, 1920, 30
ACC = (255, 178, 8)
WHITE = (255, 255, 255)
GRAY = (205, 205, 205)
PNL_W, PNL_H, PNL_X, PNL_Y = 960, 1051, 60, 500  # панель кассы; текст сверху — в безопасной зоне Reels (150..1670)
PH_W, PH_H, PH_X, PH_Y = 592, 1212, 244, 466   # телефон (как в «Мини»)
XF = 0.4
T_HOOK, T_CTA = 3.6, 4.0
WARN = []


def ease_out(x):
    x = min(max(x, 0), 1)
    return 1 - (1 - x) ** 3


def ease_io(x):
    x = min(max(x, 0), 1)
    return x * x * (3 - 2 * x)


def ease_back(x):
    x = min(max(x, 0), 1)
    c1, c3 = 1.4, 2.4
    return 1 + c3 * (x - 1) ** 3 + c1 * (x - 1) ** 2


def font(name, size):
    return ImageFont.truetype(str(FONT_DIR / f'Montserrat-{name}.ttf'), size)


def seg_split(line):
    return [(p, i % 2 == 1) for i, p in enumerate(line.split('*')) if p]


def text_block(text, size, weight='ExtraBold', color=WHITE, maxw=960, minsize=40, line_gap=1.16, canvas_w=W, tag=''):
    """RGBA-картинка; *слово* — оранжевым; размер подгоняется, чтобы строки влезли в maxw (иначе предупреждение)."""
    lines = text.split('\n')
    while True:
        f = font(weight, size)
        widths = [sum(f.getlength(p) for p, _ in seg_split(l)) for l in lines]
        if max(widths) <= maxw or size <= minsize:
            break
        size -= 2
    if max(widths) > maxw:
        WARN.append(f'НЕ ВЛЕЗАЕТ ({int(max(widths))}>{maxw}px): {tag} «{text}»')
    lh = int(size * line_gap)
    img = Image.new('RGBA', (canvas_w, lh * len(lines) + 20), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    for i, l in enumerate(lines):
        x = (canvas_w - widths[i]) / 2
        for p, acc in seg_split(l):
            d.text((x, i * lh + 4), p, font=f, fill=ACC if acc else color)
            x += f.getlength(p)
    return img


def make_bg():
    bg = Image.new('RGB', (W, H), (20, 20, 20))
    for color, box, blur, al in (((247, 123, 20), (W - 200, -380, W + 700, 520), 220, 0.55), ((255, 178, 8), (-500, H - 500, 400, H + 400), 240, 0.30)):
        m = Image.new('L', (W, H), 0)
        ImageDraw.Draw(m).ellipse(box, fill=int(255 * al))
        m = m.filter(ImageFilter.GaussianBlur(blur))
        bg.paste(Image.new('RGB', (W, H), color), (0, 0), m)
    return bg


def paste_alpha(dst, src, xy, alpha=1.0):
    if src.mode != 'RGBA':
        src = src.convert('RGBA')
    if alpha < 1:
        a = src.getchannel('A').point(lambda v: int(v * alpha))
        src = src.copy()
        src.putalpha(a)
    dst.paste(src, (int(xy[0]), int(xy[1])), src)


def rounded_panel(img, radius=26):
    """Панель с закруглёнными углами и тенью -> RGBA (с запасом под тень)."""
    pad = 70
    out = Image.new('RGBA', (img.width + 2 * pad, img.height + 2 * pad), (0, 0, 0, 0))
    sh = Image.new('RGBA', out.size, (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle((pad, pad + 24, pad + img.width, pad + img.height + 24), radius, fill=(0, 0, 0, 160))
    out.alpha_composite(sh.filter(ImageFilter.GaussianBlur(26)))
    m = Image.new('L', img.size, 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, img.width - 1, img.height - 1), radius, fill=255)
    out.paste(img, (pad, pad), m)
    return out


PAD = 70


def make_phone():
    body = Image.new('RGBA', (PH_W, PH_H), (0, 0, 0, 0))
    d = ImageDraw.Draw(body)
    d.rounded_rectangle((0, 0, PH_W - 1, PH_H - 1), 78, fill=(12, 12, 12, 255), outline=(70, 70, 70, 255), width=3)
    sh = Image.new('RGBA', (PH_W + 160, PH_H + 160), (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle((80, 100, PH_W + 80, PH_H + 100), 78, fill=(0, 0, 0, 170))
    sh = sh.filter(ImageFilter.GaussianBlur(26))
    mask = Image.new('L', (560, 1180), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, 559, 1179), 62, fill=255)
    return body, sh, mask


class Video:
    def __init__(self, code, lang, texts):
        self.code, self.lang = code, lang
        sp = texts['spheres'][code]
        self.t = {k: (s.format(**texts['vars']) if isinstance(s, str) else s) for k, s in sp[lang].items()}
        self.disclaimer = texts['disclaimer'][lang]
        self.support = texts.get('support_line', {}).get(lang, '')
        self.stores = texts['vars'].get('stores', '')
        names = sp['scenes']
        n_s = sum(1 for n in names if n.startswith('s'))
        ds = 4.6 if n_s <= 3 else 4.1
        self.timeline, x = [], 0.0
        for n in names:
            d = T_HOOK if n == 'h' else T_CTA if n == 'cta' else ds
            self.timeline.append((n, x, x + d))
            x += d
        self.total = x
        self.bg = make_bg()
        self.body, self.shadow, self.smask = make_phone()
        self.pn, self.ph = {}, {}
        sdir = HERE / 'screens' / code
        for n in names:
            if n in ('cta',):
                continue
            a, b = sdir / f'{lang}_{n}_a.png', sdir / f'{lang}_{n}_b.png'
            if a.exists():
                self.pn[n] = (Image.open(a).convert('RGB'), Image.open(b).convert('RGB'))
            else:  # телефон: макеты «Мини» (торг)
                ma, mb = MINI / 'screens' / f'{lang}_hag_a.png', MINI / 'screens' / f'{lang}_hag_b.png'
                self.ph[n] = (Image.open(ma).convert('RGB').resize((560, 1180), Image.LANCZOS), Image.open(mb).convert('RGB').resize((560, 1180), Image.LANCZOS))
        self.rp = {n: tuple(rounded_panel(i) for i in v) for n, v in self.pn.items()}
        lg = Image.open(LOGO).convert('RGBA').resize((512, 512), Image.LANCZOS)
        rm = Image.new('L', (512, 512), 0)
        ImageDraw.Draw(rm).rounded_rectangle((0, 0, 511, 511), 112, fill=255)
        lg.putalpha(rm)
        self.logo = lg
        self.cap = {}
        for n in names:
            if f'{n}_1' in self.t and n != 'cta':
                big = n == 'h'
                self.cap[n] = (text_block(self.t[f'{n}_1'], 84 if big else 76, tag=f'{code}/{lang}/{n}_1'),
                               text_block(self.t[f'{n}_2'], 52, 'Bold', GRAY, minsize=40, tag=f'{code}/{lang}/{n}_2') if f'{n}_2' in self.t else None)

    def scene_at(self, t):
        for i, (n, s, e) in enumerate(self.timeline):
            if s <= t < e:
                return i
        return len(self.timeline) - 1

    # --- панель кассы или телефон для сцены i в момент t (RGBA со своим размером и позицией) ---
    def device(self, i, t, final=False):
        n, s, e = self.timeline[i]
        lt = (e - s) if final else t - s
        dur = e - s
        sw = dur * 0.42
        k = ease_io((lt - sw) / 0.3)
        if n in self.rp:
            a, b = self.rp[n]
            return (a if k <= 0 else b if k >= 1 else Image.blend(a, b, k)), (PNL_X - PAD, PNL_Y - PAD)
        a, b = self.ph[n]
        scr = a if k <= 0 else b if k >= 1 else Image.blend(a, b, k)
        grp = self.body.copy()
        scr = scr.convert('RGBA')
        tap = (280, 934)
        dt = lt - (sw - 0.45)
        if 0 <= dt < 0.55:
            ov = Image.new('RGBA', scr.size, (0, 0, 0, 0))
            d = ImageDraw.Draw(ov)
            r = 20 + 26 * ease_out(dt / 0.55)
            al = int(190 * (1 - dt / 0.55))
            d.ellipse((tap[0] - r, tap[1] - r, tap[0] + r, tap[1] + r), fill=(255, 255, 255, int(al * 0.5)), outline=(255, 178, 8, al), width=5)
            d.ellipse((tap[0] - 14, tap[1] - 14, tap[0] + 14, tap[1] + 14), fill=(60, 60, 60, int(al * 0.8)))
            scr.alpha_composite(ov)
        grp.paste(scr.convert('RGB'), (16, 16), self.smask)
        ImageDraw.Draw(grp).rounded_rectangle((PH_W // 2 - 55, 30, PH_W // 2 + 55, 58), 14, fill=(8, 8, 8, 255))
        canvas = Image.new('RGBA', (PH_W + 160, PH_H + 160), (0, 0, 0, 0))
        canvas.alpha_composite(self.shadow, (0, 0))
        canvas.alpha_composite(grp, (80, 60))
        return canvas, (PH_X - 80, PH_Y - 60)

    def caption(self, canvas, i, t):
        n, s, e = self.timeline[i]
        if n not in self.cap:
            return
        main, sub = self.cap[n]
        lt = t - s
        a_in = ease_out(lt / 0.45)
        a_out = 1 - ease_io((lt - (e - s - 0.25)) / 0.25) if lt > e - s - 0.25 else 1
        al = min(a_in, a_out)
        if al <= 0.01:
            return
        dy = int((1 - a_in) * 40)
        paste_alpha(canvas, main, (0, 156 + dy), al)
        if sub:
            paste_alpha(canvas, sub, (0, 156 + main.height + 2 + dy), al)

    def render(self, t):
        t = min(max(t, 0), self.total - 1 / FPS)
        i = self.scene_at(t)
        n, s, e = self.timeline[i]
        c = self.bg.convert('RGBA')
        if n == 'cta':
            self.cta(c, t - s)
            return c.convert('RGB')
        lt = t - s
        img, pos = self.device(i, t)
        if i == 0 and lt < 0.8:  # первый кадр: панель выезжает снизу
            k = ease_out(lt / 0.8)
            paste_alpha(c, img, (pos[0], pos[1] + (1 - k) * 700 + 0), min(1, k * 1.6))
        elif lt < XF and i > 0:  # переход: прошлый экран гаснет, новый выезжает
            k = ease_io(lt / XF)
            pimg, ppos = self.device(i - 1, t, final=True)
            paste_alpha(c, pimg, ppos, 1 - k)
            paste_alpha(c, img, (pos[0], pos[1] + (1 - k) * 60), k)
        else:
            paste_alpha(c, img, pos)
        self.caption(c, i, t)
        return c.convert('RGB')

    def cta(self, c, lt):
        t = self.t
        al = ease_out(lt / 0.45)
        lg = self.logo.resize((220, 220), Image.LANCZOS)
        paste_alpha(c, lg, ((W - 220) // 2, 330 + int((1 - al) * 30)), al)
        main = text_block(t['cta_1'], 150, line_gap=1.12, tag=f'{self.code}/{self.lang}/cta_1')
        paste_alpha(c, main, (0, 610 + int((1 - al) * 40)), al)
        k = ease_back((lt - 0.6) / 0.5)
        if k > 0.01:
            f = font('ExtraBold', 112)
            txt = t['cta_2']
            tw = int(f.getlength(txt) + 120)
            pill = Image.new('RGBA', (tw, 170), (0, 0, 0, 0))
            d = ImageDraw.Draw(pill)
            d.rounded_rectangle((0, 0, tw - 1, 169), 85, fill=ACC + (255,))
            d.text((60, 14), txt, font=f, fill=(25, 25, 25))
            pulse = 1 + 0.03 * math.sin(lt * 5) if lt > 1.2 else 1
            sc = min(k, 1.1) * pulse
            pill = pill.resize((int(tw * sc), int(170 * sc)), Image.LANCZOS)
            paste_alpha(c, pill, ((W - pill.width) // 2, 1010 + (170 - pill.height) // 2), min(1, k * 1.5))
        sk = ease_out((lt - 1.1) / 0.4)
        if sk > 0.01:
            sub = text_block(t['cta_3'], 54, 'Bold', GRAY, minsize=38, tag=f'{self.code}/{self.lang}/cta_3')
            paste_alpha(c, sub, (0, 1225), sk)
            if self.stores:  # магазины приложений (названия без ссылок)
                paste_alpha(c, text_block(self.stores, 56, 'ExtraBold', WHITE, minsize=38, tag='stores'), (0, 1305), sk)
            if self.support:
                paste_alpha(c, text_block(self.support, 44, 'Bold', GRAY, minsize=32, tag=f'{self.code}/{self.lang}/support'), (0, 1385), sk)
            if t.get('price_line'):  # необязательная строка про цену/пробный срок: по умолчанию пусто
                pl = text_block(t['price_line'], 54, 'ExtraBold', WHITE, minsize=38, tag=f'{self.code}/{self.lang}/price_line')
                paste_alpha(c, pl, (0, 1450), sk)
        dk = ease_out((lt - 1.5) / 0.4)
        if dk > 0.01:
            paste_alpha(c, text_block(self.disclaimer, 34, 'Medium', (150, 150, 150), minsize=26, tag='disclaimer'), (0, 1520), dk)


MUSIC = {  # (ударов/мин, сдвиг тональности в полутонах): своя мелодия на каждую сферу, всё синтезируется кодом
    'grocery': (112, 0), 'autoparts': (104, -2), 'bazaar': (120, 2), 'clothing': (108, 3), 'services': (100, -3), 'hardware': (110, -1), 'electronics': (116, 4), 'stationery': (106, 1),
}


def make_music(code, path, seconds):
    sys.path.insert(0, str(MINI))
    import music
    orig = music.hz
    bpm, shift = MUSIC.get(code, (112, 0))
    music.BPM = bpm
    music.hz = lambda midi: orig(midi + shift)
    try:
        music.make(path, seconds)
    finally:
        music.hz = orig
        music.BPM = 112


def encode(video, out_mp4, audio):
    n = int(round(video.total * FPS))
    cmd = [FFMPEG, '-y', '-loglevel', 'error', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-s', f'{W}x{H}', '-r', str(FPS), '-i', '-',
           '-i', str(audio), '-c:v', 'libx264', '-preset', 'medium', '-crf', '21', '-pix_fmt', 'yuv420p', '-profile:v', 'high', '-r', str(FPS),
           '-c:a', 'aac', '-b:a', '128k', '-af', f'afade=t=in:d=0.6,afade=t=out:st={video.total - 1.8:.2f}:d=1.8',
           '-t', f'{video.total:.3f}', '-movflags', '+faststart', str(out_mp4)]
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    for k in range(n):
        p.stdin.write(video.render(k / FPS).tobytes())
        if k % 150 == 0:
            print(f'  {video.code}/{video.lang} кадр {k}/{n}', flush=True)
    p.stdin.close()
    p.wait()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--texts-only', action='store_true')
    ap.add_argument('--lang')
    ap.add_argument('--sphere')
    ap.add_argument('--frames', action='store_true')
    a = ap.parse_args()
    texts = json.loads((HERE / 'texts.json').read_text(encoding='utf-8'))
    codes = a.sphere.split(',') if a.sphere else list(texts['spheres'])
    for code in codes:
        if not a.texts_only and not (HERE / 'screens' / code / 'ru_h_a.png').exists():
            import screens
            screens.render_sphere(code)
        for lang in ([a.lang] if a.lang else ['ru', 'ky']):
            v = Video(code, lang, texts)
            print(code, lang, 'длительность', round(v.total, 1), 'с', flush=True)
            if a.frames:
                d = HERE / 'frames_preview' / code
                d.mkdir(parents=True, exist_ok=True)
                for k in range(0, int(v.total * 2) + 1):
                    v.render(min(k * 0.5, v.total - 0.05)).save(d / f'{lang}_{k * 0.5:05.1f}.png')
                continue
            od = OUT / code
            od.mkdir(exist_ok=True)
            wav = HERE / f'music_{code}_{lang}.wav'
            make_music(code, wav, v.total + 0.5)
            encode(v, od / f'nurcrm-{code}-{lang}.mp4', wav)
            v.render(v.timeline[0][2] - 0.4).save(od / f'nurcrm-{code}-{lang}-cover.jpg', quality=90)
            wav.unlink(missing_ok=True)
    for w in WARN:
        print('ВНИМАНИЕ:', w)


if __name__ == '__main__':
    main()
