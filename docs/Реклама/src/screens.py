# -*- coding: utf-8 -*-
"""Макеты экранов мобильного приложения NurCRM (демо-данные, вымышленные товары и клиенты).
Цвета и шрифт — из NurMarketMobile/lib/core/theme/tokens.dart (Montserrat, акцент #FFB208).
Запуск: python screens.py  -> screens/<lang>_<имя>.png (560x1180). Нужен Edge/Chrome."""
import os, random, subprocess, sys, pathlib, tempfile, time
from PIL import Image

HERE = pathlib.Path(__file__).resolve().parent
FONTS = (HERE / '../../../NurMarketMobile/assets/fonts').resolve().as_uri()
EDGE = r'C:\Program Files\Google\Chrome\Application\chrome.exe'  # старый headless: Edge в этой среде файл не пишет
W, H = 360, 758

CSS = f"""
@font-face{{font-family:M;src:url('{FONTS}/Montserrat-Regular.ttf');font-weight:400}}
@font-face{{font-family:M;src:url('{FONTS}/Montserrat-Medium.ttf');font-weight:500}}
@font-face{{font-family:M;src:url('{FONTS}/Montserrat-SemiBold.ttf');font-weight:600}}
@font-face{{font-family:M;src:url('{FONTS}/Montserrat-Bold.ttf');font-weight:700}}
@font-face{{font-family:M;src:url('{FONTS}/Montserrat-ExtraBold.ttf');font-weight:800}}
*{{box-sizing:border-box;margin:0;padding:0}}
html,body{{width:{W}px;height:{H}px;overflow:hidden;font-family:M,sans-serif;color:#191919;background:#F4F4F4}}
.sb{{height:26px;display:flex;justify-content:space-between;align-items:center;padding:0 18px;font-size:12px;font-weight:700}}
.sb i{{display:inline-block;width:22px;height:11px;border:2px solid #191919;border-radius:3px;position:relative}}
.sb i:after{{content:'';position:absolute;left:1px;top:1px;bottom:1px;width:13px;background:#191919;border-radius:1px}}
.h1{{font-size:21px;font-weight:800;padding:6px 16px 8px}}
.card{{background:#fff;border:1px solid #E2E2E2;border-radius:14px}}
.btn{{background:#FFB208;color:#191919;font-weight:800;border-radius:14px;text-align:center}}
.abs{{position:absolute}}
.nav{{position:absolute;left:0;right:0;bottom:0;height:62px;background:#fff;border-top:1px solid #E2E2E2;display:flex}}
.nav div{{flex:1;display:flex;flex-direction:column;align-items:center;justify-content:center;font-size:10.5px;font-weight:600;color:#666;gap:3px}}
.nav b{{display:block;width:22px;height:22px;border-radius:7px;background:#D9D9D9}}
.nav .on{{color:#191919;font-weight:800}} .nav .on b{{background:#FFB208}}
.dim{{position:absolute;inset:0;background:rgba(0,0,0,.45)}}
.sheet{{position:absolute;left:0;right:0;bottom:0;background:#fff;border-radius:22px 22px 0 0;padding:16px 16px 20px;box-shadow:0 -6px 24px rgba(0,0,0,.25)}}
.grab{{width:40px;height:4px;background:#CCC;border-radius:2px;margin:0 auto 12px}}
.chip{{display:inline-block;padding:9px 14px;border-radius:12px;border:1.5px solid #CCCCCC;font-weight:800;font-size:14px;background:#fff}}
.chip.on{{background:#FFF1DB;border-color:#FFB208}}
"""

L = {
 'ru': dict(sale='Продажа', receive='Приход', totals='Итоги', more='Ещё', find='Найти по названию', pay='Оплатить', som='сом',
            haggle='Торг', got='Получено', other='Другая оплата', scanner='Сканер', tocheck='В чек', price='Узнать цену', recv='Приёмка',
            incheck='В чеке: 1', shoot='Сфотографировать', smart='Умная камера', rec='Распознано: 4 позиции', name='Название', qty='Кол-во',
            accept='Принять', nodisc='Без скидки', to='До', disc='Скидка', topay='К оплате', offmode='Работа без интернета',
            offbanner='Нет связи с сервером — продажи сохраняются на телефоне.', queue='В очереди: 3', sendnow='Азыр', sendnow_full='Отправить сейчас',
            allsent='Всё отправлено', inq='в очереди', search='Поиск', ph='Найти по названию…', left='Остаток', debts='Долги клиентов',
            owe='Должны мне', who='Кто должен', invoice='Накладная',
            items=['Печенье','Чай чёрный','Сахар 1 кг','Вода 1,5 л','Хлеб','Мыло','Конфеты','Масло','Рис 1 кг'],
            p1='Колодки тормозные передние', p2='Фильтр масляный', chat='Здравствуйте, Айбек! Напоминаем о долге в магазине «Демо»: 2 400 сом. Спасибо!',
            cname='Печенье «Шоколад»'),
 'ky': dict(sale='Сатуу', receive='Киреше', totals='Жыйынтык', more='Дагы', find='Аталышы боюнча издөө', pay='Төлөө', som='сом',
            haggle='Соодалашуу', got='Алынды', other='Башка төлөм', scanner='Сканер', tocheck='Чекке', price='Бааны билүү', recv='Кабыл алуу',
            incheck='Чекте: 1', shoot='Сүрөткө тартуу', smart='Акылдуу камера', rec='Таанылды: 4 позиция', name='Аталышы', qty='Саны',
            accept='Кабыл алуу', nodisc='Арзандатуусуз', to='Чейин', disc='Арзандатуу', topay='Төлөнүүчү', offmode='Интернетсиз иштөө',
            offbanner='Сервер менен байланыш жок — сатуулар телефондо сакталат.', queue='Кезекте: 3', sendnow='Азыр', sendnow_full='Азыр жөнөтүү',
            allsent='Баары жөнөтүлдү', inq='кезекте', search='Издөө', ph='Аталышы боюнча издөө…', left='Калдык', debts='Кардарлардын карыздары',
            owe='Мага карыз', who='Ким карыз', invoice='Накладной',
            items=['Печенье','Чай','Шекер 1 кг','Суу 1,5 л','Нан','Самын','Момпосуй','Май','Күрүч 1 кг'],
            p1='Алдыңкы тормоз колодкалары', p2='Май чыпкасы', chat='Саламатсызбы, Айбек! «Демо» дүкөнүндөгү карызыңызды эскертебиз: 2 400 сом. Рахмат!',
            cname='Печенье «Шоколад»'),
}
COL = ['#B8860B', '#5470E1', '#EE3083', '#3E9157', '#9F81DA', '#D16B60', '#C740B1', '#0DA38A', '#C95523']
PRICES = [120, 185, 95, 60, 40, 75, 210, 340, 130]


def sb():
    return '<div class="sb"><span>9:41</span><i></i></div>'


def nav(t, on=0):
    names = [t['sale'], t['receive'], t['totals'], t['more']]
    return '<div class="nav">' + ''.join(f'<div class="{"on" if i == on else ""}"><b></b>{n}</div>' for i, n in enumerate(names)) + '</div>'


def sale(t, sheet=''):
    tiles = ''
    for i, n in enumerate(t['items']):
        tiles += (f'<div class="card" style="padding:8px;text-align:center"><div style="height:58px;border-radius:10px;background:{COL[i]}22;'
                  f'display:flex;align-items:center;justify-content:center;font-size:26px;font-weight:800;color:{COL[i]}">{n[0]}</div>'
                  f'<div style="font-size:11.5px;font-weight:600;margin-top:6px;height:28px;line-height:14px">{n}</div>'
                  f'<div style="font-size:13px;font-weight:800">{PRICES[i]} {t["som"]}</div></div>')
    return f"""{sb()}<div class="h1">{t['sale']}</div>
<div style="display:flex;gap:8px;padding:0 16px"><div class="card" style="flex:1;height:42px;padding:12px;font-size:12px;color:#666">{t['find']}</div>
<div class="btn" style="width:42px;height:42px;font-size:18px;line-height:42px">▦</div></div>
<div style="display:grid;grid-template-columns:repeat(3,1fr);gap:8px;padding:12px 16px">{tiles}</div>
<div class="abs" style="left:12px;right:12px;bottom:72px;background:#fff;border:1px solid #E2E2E2;border-radius:18px;padding:10px;box-shadow:0 4px 16px rgba(0,0,0,.1)">
<div style="display:flex;justify-content:space-between;font-size:12.5px;font-weight:700;padding:2px 4px 8px"><span>{t['items'][1]}</span><span>− 2 +</span></div>
<div class="btn" style="height:52px;font-size:18px;line-height:52px">{t['pay']} 1 287 {t['som']}</div>
<div style="display:flex;gap:6px;margin-top:8px;font-size:11px;font-weight:700;color:#666"><span class="card" style="padding:7px 11px">{t['haggle']}</span><span class="card" style="padding:7px 11px">{t['got']}</span><span class="card" style="padding:7px 11px">{t['other']}</span></div></div>
{nav(t)}{sheet}"""


def haggle(t, on):
    vals = {10: 1280, 50: 1250, 100: 1200}
    chips = ''
    for s in (10, 50, 100):
        chips += (f'<div class="chip {"on" if s == on else ""}" style="flex:1;text-align:center"><div style="font-size:15px;white-space:nowrap">{t["to"]} {s}</div>'
                  f'<div style="font-size:12px;font-weight:600;color:#666">{format(vals[s], ',').replace(',', ' ')}</div></div>')
    if True:
        extra = (f'<div style="visibility:{"visible" if on else "hidden"}"><div style="margin-top:12px;background:#E7F6EC;border-radius:12px;padding:10px 12px;font-size:13px;font-weight:700;color:#0E7A3A">'
                 f'{t["disc"]}: {1287 - vals[on or 50]} {t["som"]} (2,9 %)</div>'
                 f'<div class="btn" style="margin-top:12px;height:50px;line-height:50px;font-size:17px">{t["topay"]} {format(vals[on or 50], ",").replace(",", " ")} {t["som"]}</div></div>')
    sheet = f"""<div class="dim"></div><div class="sheet"><div class="grab"></div>
<div style="font-size:20px;font-weight:800">{t['haggle']}</div>
<div style="font-size:12.5px;color:#666;margin:2px 0 14px">{t['nodisc']}: 1 287 {t['som']}</div>
<div style="display:flex;gap:8px">{chips}</div>{extra}</div>"""
    return sale(t, sheet)


def barcode(w, h):
    random.seed(7)
    x, out = 0, ''
    while x < w - 4:
        bw = random.choice([2, 2, 3, 4, 6])
        if random.random() < .55:
            out += f'<rect x="{x}" y="0" width="{bw}" height="{h}" fill="#111"/>'
        x += bw + random.choice([2, 3])
    return f'<svg width="{w}" height="{h}">{out}</svg>'


def scan(t, found):
    card = ''
    if found:
        card = f"""<div class="abs" style="left:14px;right:14px;bottom:80px;background:#fff;border-radius:18px;padding:14px;display:flex;gap:12px;align-items:center">
<div style="width:48px;height:48px;border-radius:12px;background:#C740B122;color:#C740B1;font-size:24px;font-weight:800;display:flex;align-items:center;justify-content:center">П</div>
<div style="flex:1"><div style="font-size:14px;font-weight:800">{t['cname']}</div><div style="font-size:12px;color:#0E7A3A;font-weight:700">{t['incheck']}</div></div>
<div style="font-size:16px;font-weight:800">120 {t['som']}</div></div>"""
    line = '' if found else '<div class="abs" style="left:46px;right:46px;top:300px;height:3px;background:#F77B14;box-shadow:0 0 18px 6px rgba(247,123,20,.7)"></div>'
    seg = ''.join(f'<span style="padding:9px 12px;border-radius:12px;font-size:12px;font-weight:800;{"background:#FFB208;color:#191919" if i == 0 else "background:#ffffff22;color:#fff"}">{x}</span>' for i, x in enumerate([t['tocheck'], t['price'], t['recv']]))
    corners = ''.join(f'<div class="abs" style="{p};width:34px;height:34px;border:4px solid #FFB208;{b}"></div>' for p, b in [
        ('left:36px;top:196px', 'border-right:0;border-bottom:0;border-radius:10px 0 0 0'), ('right:36px;top:196px', 'border-left:0;border-bottom:0;border-radius:0 10px 0 0'),
        ('left:36px;top:404px', 'border-right:0;border-top:0;border-radius:0 0 0 10px'), ('right:36px;top:404px', 'border-left:0;border-top:0;border-radius:0 0 10px 0')])
    return f"""<div style="position:absolute;inset:0;background:linear-gradient(180deg,#1b1b1b,#2b2b2b 50%,#161616)"></div>
<div style="position:relative;color:#fff">{sb().replace('#191919','#fff')}<div class="h1" style="color:#fff">{t['scanner']}</div></div>
<div class="abs" style="left:56px;right:56px;top:226px;height:150px;background:#f3f3ee;border-radius:10px;transform:rotate(-2deg);padding:16px 18px">{barcode(228, 96)}
<div style="text-align:center;font-size:10px;letter-spacing:3px;margin-top:6px;font-weight:600">4 600123 456789</div></div>
{corners}{line}
<div class="abs" style="left:0;right:0;top:470px;text-align:center">
<div style="display:inline-flex;gap:8px">{seg}</div></div>{card}"""


def invoice(t, done):
    if not done:
        rows = ''.join(f'<div style="display:flex;gap:8px;margin-top:9px"><div style="flex:1;height:7px;background:#bbb;border-radius:3px"></div><div style="width:34px;height:7px;background:#bbb;border-radius:3px"></div><div style="width:44px;height:7px;background:#999;border-radius:3px"></div></div>' for _ in range(9))
        return f"""<div style="position:absolute;inset:0;background:linear-gradient(180deg,#1b1b1b,#2b2b2b 50%,#161616)"></div>
<div style="position:relative">{sb().replace('#191919','#fff')}<div class="h1" style="color:#fff">{t['smart']}</div></div>
<div class="abs" style="left:34px;right:34px;top:92px;height:470px;background:#fbfbf6;border-radius:6px;transform:rotate(2.5deg);padding:24px 20px;box-shadow:0 10px 30px rgba(0,0,0,.5)">
<div style="font-size:15px;font-weight:800">{t['invoice']} № 128</div><div style="height:7px;width:120px;background:#bbb;border-radius:3px;margin-top:8px"></div>{rows}</div>
<div class="abs" style="left:0;right:0;bottom:84px;text-align:center"><div style="display:inline-block;background:#FFB208;font-weight:800;font-size:15px;padding:15px 30px;border-radius:30px">{t['shoot']}</div></div>"""
    data = [(t['items'][0], 24), (t['items'][1], 12), (t['items'][2], 30), (t['items'][3], 48)]
    rows = ''.join(f'<div class="card" style="display:flex;justify-content:space-between;padding:13px 14px;margin:0 16px 8px;font-size:14px;font-weight:700"><span>{n}</span><span>{q}</span></div>' for n, q in data)
    return f"""{sb()}<div class="h1">{t['recv']}</div>
<div style="padding:0 16px 10px;font-size:12.5px;font-weight:700;color:#0E7A3A">✔ {t['rec']}</div>
<div style="display:flex;justify-content:space-between;padding:0 30px 6px;font-size:11px;color:#666;font-weight:600"><span>{t['name']}</span><span>{t['qty']}</span></div>{rows}
<div class="btn abs" style="left:16px;right:16px;bottom:80px;height:54px;line-height:54px;font-size:17px">{t['accept']}</div>{nav(t, 1)}"""


def offline(t, ok):
    rows = ''
    for i, (no, s) in enumerate([(1041, 340), (1042, 185), (1043, 760)]):
        mark = ('<span style="color:#0E7A3A;font-weight:800">✔</span>' if ok else f'<span style="background:#FEF3E2;color:#B54708;font-size:11px;font-weight:700;padding:4px 8px;border-radius:8px">{t["inq"]}</span>')
        rows += f'<div class="card" style="display:flex;justify-content:space-between;align-items:center;padding:13px 14px;margin:0 16px 8px;font-size:14px;font-weight:700"><span>№ {no} · {s} {t["som"]}</span>{mark}</div>'
    if ok:
        ban = f'<div style="margin:0 16px 14px;background:#E7F6EC;border-radius:14px;padding:14px;font-size:13px;font-weight:700;color:#0E7A3A">✔ {t["allsent"]}</div>'
    else:
        ban = f'<div style="margin:0 16px 14px;background:#FEF3E2;border-radius:14px;padding:14px;font-size:13px;font-weight:700;color:#B54708">{t["offbanner"]}</div>'
    bot = '' if ok else f'<div style="padding:4px 16px;font-size:15px;font-weight:800">{t["queue"]}</div>'
    return f"""{sb()}<div class="h1">{t['offmode']}</div>{ban}{bot}<div style="height:8px"></div>{rows}
<div class="btn abs" style="left:16px;right:16px;bottom:80px;height:54px;line-height:54px;font-size:16px;{'opacity:.4' if ok else ''}">{t['sendnow_full']}</div>{nav(t, 3)}"""


def parts(t, typed):
    field = 'AX-4410' if typed else t['ph']
    fcol = '#191919' if typed else '#999'
    items = [(t['p1'], 'AX-4410 · OEM 04465-X1', 2350, 6), (t['p2'], 'FL-220 · OEM 15208-X7', 480, 14), (t['items'][4] if False else 'Свеча зажигания' if t is L['ru'] else 'Ширетме шамы', 'SP-91 · OEM 90919-X3', 390, 20)]
    if typed:
        items = items[:1]
    rows = ''.join(f'<div class="card" style="margin:0 16px 8px;padding:12px 14px;display:flex;justify-content:space-between;align-items:center"><div><div style="font-size:13.5px;font-weight:800">{n}</div>'
                   f'<div style="font-size:11.5px;color:#666;font-weight:600;margin-top:3px">{c}</div></div><div style="text-align:right"><div style="font-size:14px;font-weight:800;white-space:nowrap">{format(p, ',').replace(',', ' ')} {t["som"]}</div>'
                   f'<div style="font-size:11px;color:#0E7A3A;font-weight:700;white-space:nowrap">{t["left"]}: {q}</div></div></div>' for n, c, p, q in items)
    cur = '<span style="display:inline-block;width:2px;height:16px;background:#FFB208;vertical-align:middle"></span>' if typed else ''
    return f"""{sb()}<div class="h1">{t['search']}</div>
<div class="card" style="margin:0 16px 12px;height:48px;padding:14px;font-size:15px;font-weight:700;color:{fcol};border-color:{'#FFB208' if typed else '#E2E2E2'};border-width:2px">{field}{cur}</div>{rows}"""


def debt(t, chat):
    people = [('Айбек Т.', 2400), ('Нурзат К.', 3100), ('Бакыт С.', 2400)]
    rows = ''.join(f'<div class="card" style="margin:0 16px 8px;padding:12px 14px;display:flex;justify-content:space-between;align-items:center"><div><div style="font-size:14px;font-weight:800">{n}</div>'
                   f'<div style="font-size:12px;color:#C0362C;font-weight:700;margin-top:2px">{s:,} {t["som"]}'.replace(',', ' ') + f'</div></div><div style="background:#25D366;color:#fff;font-weight:800;font-size:13px;padding:10px 14px;border-radius:12px">WA</div></div>' for n, s in people)
    sheet = ''
    if chat:
        sheet = f"""<div class="dim"></div><div class="sheet" style="background:#ECE5DD;padding-bottom:26px"><div class="grab"></div>
<div style="font-size:14px;font-weight:800;margin-bottom:10px">Айбек Т.</div>
<div style="background:#DCF8C6;border-radius:14px 14px 4px 14px;padding:12px 14px;font-size:13.5px;line-height:19px;font-weight:500;margin-left:26px">{t['chat']}</div>
<div style="display:flex;gap:8px;margin-top:14px;align-items:center"><div style="flex:1;background:#fff;border-radius:22px;height:42px"></div><div style="width:42px;height:42px;border-radius:50%;background:#25D366;color:#fff;text-align:center;line-height:42px;font-size:18px">➤</div></div></div>"""
    return f"""{sb()}<div class="h1">{t['debts']}</div>
<div class="card" style="margin:0 16px 14px;padding:14px"><div style="font-size:12px;color:#666;font-weight:600">{t['owe']}</div><div style="font-size:24px;font-weight:800;color:#C0362C">7 900 {t['som']}</div></div>
<div style="padding:0 16px 8px;font-size:12px;font-weight:700;color:#666">{t['who']}</div>{rows}{nav(t, 3)}{sheet}"""


SCREENS = {
    'sale': lambda t: sale(t), 'scan_a': lambda t: scan(t, False), 'scan_b': lambda t: scan(t, True),
    'inv_a': lambda t: invoice(t, False), 'inv_b': lambda t: invoice(t, True),
    'hag_a': lambda t: haggle(t, 0), 'hag_b': lambda t: haggle(t, 50),
    'off_a': lambda t: offline(t, False), 'off_b': lambda t: offline(t, True),
    'parts_a': lambda t: parts(t, False), 'parts_b': lambda t: parts(t, True),
    'debt_a': lambda t: debt(t, False), 'debt_b': lambda t: debt(t, True),
}


def main():
    out = HERE / 'screens'
    out.mkdir(exist_ok=True)
    tmp = HERE / 'screens' / '_html'
    tmp.mkdir(exist_ok=True)
    scale = 560 / W
    for lang, t in L.items():
        for name, fn in SCREENS.items():
            h = tmp / f'{lang}_{name}.html'
            h.write_text(f'<!doctype html><html><head><meta charset="utf-8"><style>{CSS}</style></head><body style="position:relative">{fn(t)}</body></html>', encoding='utf-8')
            png = out / f'{lang}_{name}.png'
            subprocess.run([EDGE, '--headless', '--disable-gpu', '--no-sandbox', '--user-data-dir=' + tempfile.mkdtemp(), '--hide-scrollbars', f'--force-device-scale-factor={scale}', f'--window-size={W},{H}',
                            f'--screenshot={png}', '--virtual-time-budget=2000', h.as_uri()], check=True, capture_output=True)
            for _ in range(100):  # Chrome дописывает файл уже после выхода процесса
                if png.exists() and png.stat().st_size > 1000:
                    time.sleep(0.3)
                    break
                time.sleep(0.2)
            im = Image.open(png).convert('RGB')
            if im.size != (560, 1180):
                im = im.resize((560, 1180), Image.LANCZOS)
            im.save(png)
            print('ok', png.name)


if __name__ == '__main__':
    main()
