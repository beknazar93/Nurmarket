# -*- coding: utf-8 -*-
"""Сборка рекламного ролика NurCRM Mobile (9:16, 1080x1920, 30 к/с, H.264 + aac).

  python build.py                 — макеты экранов (если их нет) + оба ролика + обложки
  python build.py --texts-only    — только тексты/цена/«14 дней»: макеты экранов не перерисовываются
  python build.py --lang ru       — один язык
  python build.py --frames        — быстро: без видео, только PNG-кадры для просмотра (папка frames_preview)
Все надписи — в texts.json. Музыка — music.py (синтез, без чужих материалов). Нужны Pillow и ffmpeg (C:\\Tools\\ffmpeg)."""
import argparse, json, math, pathlib, subprocess, sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter, ImageChops

HERE = pathlib.Path(__file__).resolve().parent
OUT = HERE.parent
FONT_DIR = (HERE / '../../../NurMarketMobile/assets/fonts').resolve()
LOGO = (HERE / '../../../NurMarketMobile/assets/images/logo.png').resolve()
FFMPEG = r'C:\Tools\ffmpeg\ffmpeg.exe'
W, H, FPS = 1080, 1920, 30
ACC = (255, 178, 8)
WHITE = (255, 255, 255)
GRAY = (205, 205, 205)

# сцена: (ключ, длительность с, экран A, экран B, момент переключения A->B (доля), точка касания на экране (x, y) px или None)
SCENES = {
    'hook':    (3.6, 'sale', 'sale', 0.5, None),
    'scan':    (3.3, 'scan_a', 'scan_b', 0.55, None),
    'invoice': (3.3, 'inv_a', 'inv_b', 0.55, (280, 1013)),
    'haggle':  (3.3, 'hag_a', 'hag_b', 0.55, (280, 934)),
    'offline': (3.3, 'off_a', 'off_b', 0.55, (280, 1013)),
    'parts':   (3.3, 'parts_a', 'parts_b', 0.5, None),
    'debt':    (3.3, 'debt_a', 'debt_b', 0.55, (470, 320)),
    'mini':    (5.0, None, None, 0, None),
    'cta':     (4.0, None, None, 0, None),
}
DY = 100  # сдвиг финальных сцен вниз для баланса
XF = 0.35  # длительность перекрёстного перехода экранов
PH_W, PH_H, PH_X, PH_Y = 592, 1212, 244, 466  # корпус телефона; экран 560x1180 со смещением 16


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
    parts, acc = [], False
    for i, p in enumerate(line.split('*')):
        if p:
            parts.append((p, i % 2 == 1))
    return parts


def text_block(text, size, weight='ExtraBold', color=WHITE, maxw=940, minsize=40, line_gap=1.16, align='center', canvas_w=W):
    """RGBA-картинка шириной canvas_w; *слово* — оранжевым; размер подгоняется, чтобы строки влезли в maxw."""
    lines = text.split('\n')
    while True:
        f = font(weight, size)
        widths = [sum(f.getlength(p) for p, _ in seg_split(l)) for l in lines]
        if max(widths) <= maxw or size <= minsize:
            break
        size -= 2
    lh = int(size * line_gap)
    img = Image.new('RGBA', (canvas_w, lh * len(lines) + 20), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    for i, l in enumerate(lines):
        x = (canvas_w - widths[i]) / 2 if align == 'center' else 0
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


def paste_alpha(dst, src, xy, alpha=1.0):
    if src.mode != 'RGBA':
        src = src.convert('RGBA')
    if alpha < 1:
        a = src.getchannel('A').point(lambda v: int(v * alpha))
        src = src.copy()
        src.putalpha(a)
    dst.paste(src, xy, src)


class Video:
    def __init__(self, lang, texts):
        self.lang = lang
        v = texts['vars']
        t = dict(texts[lang])
        fmt = dict(v, plan=t['plan'])
        self.t = {k: (s.format(**fmt) if isinstance(s, str) else s) for k, s in t.items()}
        names = [s for s in texts['scenes'] if s in SCENES]
        self.timeline, x = [], 0.0
        for n in names:
            self.timeline.append((n, x, x + SCENES[n][0]))
            x += SCENES[n][0]
        self.total = x
        self.bg = make_bg()
        self.body, self.shadow, self.smask = make_phone()
        self.scr = {}
        for n in names:
            for key in (SCENES[n][1], SCENES[n][2]):
                if key and key not in self.scr:
                    self.scr[key] = Image.open(HERE / 'screens' / f'{lang}_{key}.png').convert('RGB').resize((560, 1180), Image.LANCZOS)
        lg = Image.open(LOGO).convert('RGBA').resize((512, 512), Image.LANCZOS)
        rm = Image.new('L', (512, 512), 0)
        ImageDraw.Draw(rm).rounded_rectangle((0, 0, 511, 511), 112, fill=255)
        lg.putalpha(rm)  # логотип — чёрная плитка; скругляем углы, как значок приложения
        self.logo = lg
        self.cap = {}
        for n in names:
            if f'{n}_1' in self.t:
                main = text_block(self.t[f'{n}_1'], 76)
                sub = text_block(self.t[f'{n}_2'], 52, 'Bold', GRAY, minsize=42) if f'{n}_2' in self.t and n not in ('cta',) else None
                self.cap[n] = (main, sub)
        self.phone_scene_end = next((s for n, s, e in self.timeline if n == 'mini'), self.total)

    def scene_at(self, t):
        for i, (n, s, e) in enumerate(self.timeline):
            if s <= t < e:
                return i
        return len(self.timeline) - 1

    def screen_image(self, i, t):
        n, s, e = self.timeline[i]
        dur, ka, kb, sw, tap = SCENES[n]
        lt = t - s
        a, b = self.scr[ka], self.scr[kb]
        swt = dur * sw
        img = b if lt >= swt + 0.25 else (a if lt <= swt else Image.blend(a, b, ease_io((lt - swt) / 0.25)))
        # переход от прошлой сцены
        if i > 0 and lt < XF:
            pn = self.timeline[i - 1][0]
            if SCENES[pn][2]:
                prev = self.scr[SCENES[pn][2]]
                k = ease_io(lt / XF)
                img = Image.blend(prev, img, k)
        # касание
        if tap:
            dt = lt - (swt - 0.45)
            if 0 <= dt < 0.55:
                ov = Image.new('RGBA', img.size, (0, 0, 0, 0))
                d = ImageDraw.Draw(ov)
                r = 20 + 26 * ease_out(dt / 0.55)
                al = int(190 * (1 - dt / 0.55))
                d.ellipse((tap[0] - r, tap[1] - r, tap[0] + r, tap[1] + r), fill=(255, 255, 255, int(al * 0.5)), outline=(255, 178, 8, al), width=5)
                d.ellipse((tap[0] - 14, tap[1] - 14, tap[0] + 14, tap[1] + 14), fill=(60, 60, 60, int(al * 0.8)))
                img = img.convert('RGBA')
                img.alpha_composite(ov)
                img = img.convert('RGB')
        return img

    def phone_group(self, i, t):
        n, s, e = self.timeline[i]
        scr = self.screen_image(i, t)
        grp = self.body.copy()
        grp.paste(scr, (16, 16), self.smask)
        ImageDraw.Draw(grp).rounded_rectangle((PH_W // 2 - 55, 30, PH_W // 2 + 55, 58), 14, fill=(8, 8, 8, 255))
        return grp

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
        # телефон
        pend = self.phone_scene_end
        if t < pend + 0.7 and n not in ('mini', 'cta') or (n == 'mini' and t - s < 0.7):
            y = PH_Y
            sc = 1.0
            if t < 0.9:
                k = ease_out(t / 0.9)
                y = int(PH_Y + (1 - k) * 900)
            elif n == 'mini':
                y = int(PH_Y + ease_io((t - s) / 0.7) * 1300)
            else:
                sc = 1 + 0.012 * (t / pend)
            ii = i if n != 'mini' else i - 1
            grp = self.phone_group(ii, min(t, self.timeline[ii][2] - 0.001) if n == 'mini' else t)
            if sc != 1.0:
                grp = grp.resize((int(PH_W * sc), int(PH_H * sc)), Image.BICUBIC)
            gx = PH_X - (grp.width - PH_W) // 2
            gy = y - (grp.height - PH_H) // 2
            c.alpha_composite(self.shadow, (gx - 80, gy - 60)) if gx - 80 >= 0 else None
            c.alpha_composite(grp, (gx, gy))
        if n in self.cap and n not in ('mini', 'cta'):
            self.caption(c, i, t)
        elif n == 'mini':
            self.mini(c, t - s)
        elif n == 'cta':
            self.cta(c, t - s)
        return c.convert('RGB')

    def mini(self, c, lt):
        t = self.t
        al = ease_out((lt - 0.3) / 0.4)
        lg = self.logo.resize((150, 150), Image.LANCZOS)
        paste_alpha(c, lg, ((W - 150) // 2, 190 + DY), al)
        title = text_block(t['mini_title'], 84)
        paste_alpha(c, title, (0, 370 + DY + int((1 - al) * 30)), al)
        # карточка с ценой — «выскакивает»
        k = ease_back((lt - 0.45) / 0.55)
        if k > 0.01:
            cw, ch = 880, 640
            card = Image.new('RGBA', (cw, ch), (0, 0, 0, 0))
            d = ImageDraw.Draw(card)
            d.rounded_rectangle((0, 0, cw - 1, ch - 1), 56, fill=ACC + (255,))
            price = text_block(t['mini_price'], 340, 'ExtraBold', (25, 25, 25), maxw=800, canvas_w=cw)
            card.alpha_composite(price, (0, 20))
            unit = text_block(t['mini_unit'], 70, 'ExtraBold', (25, 25, 25), maxw=800, canvas_w=cw)
            card.alpha_composite(unit, (0, 400))
            pk = ease_out((lt - 1.0) / 0.4)
            pill = text_block(t['mini_pill'], 52, 'Bold', WHITE, maxw=760, canvas_w=cw)
            pw = int(max(1, sum(font('Bold', 52).getlength(p) for p, _ in seg_split(t['mini_pill']))) + 90)
            pl = Image.new('RGBA', (cw, 100), (0, 0, 0, 0))
            ImageDraw.Draw(pl).rounded_rectangle(((cw - pw) // 2, 0, (cw + pw) // 2, 88), 44, fill=(25, 25, 25, 255))
            pl.alpha_composite(pill, (0, 6))
            if pk > 0.01:
                paste_alpha(card, pl, (0, 505), pk)
            sc = min(k, 1.08)
            card = card.resize((max(1, int(cw * sc)), max(1, int(ch * sc))), Image.LANCZOS)
            paste_alpha(c, card, ((W - card.width) // 2, 520 + DY + (ch - card.height) // 2), min(1, k * 1.5))
        # пробный период
        tk = ease_out((lt - 1.9) / 0.45)
        if tk > 0.01:
            tt = text_block(t['mini_trial'], 62, 'ExtraBold', WHITE, maxw=880)
            tw = int(sum(font('ExtraBold', 62).getlength(p) for p, _ in seg_split(t['mini_trial'])) + 100)
            bd = Image.new('RGBA', (W, 110), (0, 0, 0, 0))
            ImageDraw.Draw(bd).rounded_rectangle(((W - tw) // 2, 0, (W + tw) // 2, 104), 52, fill=(14, 122, 58, 255))
            bd.alpha_composite(tt, (0, 8))
            paste_alpha(c, bd, (0, 1235 + DY + int((1 - tk) * 40)), tk)

    def cta(self, c, lt):
        t = self.t
        al = ease_out(lt / 0.45)
        lg = self.logo.resize((220, 220), Image.LANCZOS)
        paste_alpha(c, lg, ((W - 220) // 2, 300 + DY + int((1 - al) * 30)), al)
        main = text_block(t['cta_1'], 170, line_gap=1.12)
        paste_alpha(c, main, (0, 580 + DY + int((1 - al) * 40)), al)
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
            paste_alpha(c, pill, ((W - pill.width) // 2, 1110 + DY + (170 - pill.height) // 2), min(1, k * 1.5))
        sk = ease_out((lt - 1.1) / 0.4)
        if sk > 0.01:
            sub = text_block(t['cta_3'], 54, 'Bold', GRAY, minsize=42)
            paste_alpha(c, sub, (0, 1340 + DY), sk)


def encode(video, out_mp4, audio):
    n = int(round(video.total * FPS))
    cmd = [FFMPEG, '-y', '-loglevel', 'error', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-s', f'{W}x{H}', '-r', str(FPS), '-i', '-',
           '-i', str(audio), '-c:v', 'libx264', '-preset', 'medium', '-crf', '19', '-pix_fmt', 'yuv420p', '-profile:v', 'high', '-r', str(FPS),
           '-c:a', 'aac', '-b:a', '160k', '-af', f'afade=t=in:d=0.6,afade=t=out:st={video.total - 1.8:.2f}:d=1.8',
           '-t', f'{video.total:.3f}', '-movflags', '+faststart', str(out_mp4)]
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    for k in range(n):
        p.stdin.write(video.render(k / FPS).tobytes())
        if k % 100 == 0:
            print(f'  кадр {k}/{n}', flush=True)
    p.stdin.close()
    p.wait()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--texts-only', action='store_true')
    ap.add_argument('--lang')
    ap.add_argument('--frames', action='store_true')
    a = ap.parse_args()
    texts = json.loads((HERE / 'texts.json').read_text(encoding='utf-8'))
    if not a.texts_only and not (HERE / 'screens' / 'ru_sale.png').exists():
        import screens
        screens.main()
    for lang in ([a.lang] if a.lang else ['ru', 'ky']):
        v = Video(lang, texts)
        print(lang, 'длительность', round(v.total, 1), 'с')
        if a.frames:
            d = HERE / 'frames_preview'
            d.mkdir(exist_ok=True)
            for k in range(0, int(v.total * 2) + 1):
                v.render(min(k * 0.5, v.total - 0.05)).save(d / f'{lang}_{k * 0.5:05.1f}.png')
            continue
        import music
        wav = HERE / f'music_{lang}.wav'
        music.make(wav, v.total + 0.5)
        encode(v, OUT / f'nurcrm-mobile-reklama-{lang}.mp4', wav)
        cover = v.render(3.0 if v.total > 3.2 else 1.0)
        cover.save(OUT / f'nurcrm-mobile-reklama-{lang}-cover.jpg', quality=90)
        wav.unlink(missing_ok=True)


if __name__ == '__main__':
    main()
