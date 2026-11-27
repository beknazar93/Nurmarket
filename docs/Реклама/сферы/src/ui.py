# -*- coding: utf-8 -*-
"""Детали «макета кассы NurCRM» для рекламных роликов по сферам: CSS и мелкие кирпичики HTML.
Макеты — вымышленные данные, стиль как у кассы (светлая тема, тёмно-синие вкладки, жёлтый акцент, Montserrat).
Кадр — 420x460 css-пикселей (в видео 960x~1051). Состояния: «A» (до) и «B» (после): sw(a, b) — разные куски на одном месте, onb(x) — только в B."""
import pathlib

HERE = pathlib.Path(__file__).resolve().parent
FONTS = (HERE / '../../../../NurMarketMobile/assets/fonts').resolve().as_uri()
FW, FH = 420, 460
SCALE = 960 / FW  # в видео панель 960 px шириной

CSS = f"""
@font-face{{font-family:M;src:url('{FONTS}/Montserrat-Regular.ttf');font-weight:400}}
@font-face{{font-family:M;src:url('{FONTS}/Montserrat-Medium.ttf');font-weight:500}}
@font-face{{font-family:M;src:url('{FONTS}/Montserrat-SemiBold.ttf');font-weight:600}}
@font-face{{font-family:M;src:url('{FONTS}/Montserrat-Bold.ttf');font-weight:700}}
@font-face{{font-family:M;src:url('{FONTS}/Montserrat-ExtraBold.ttf');font-weight:800}}
*{{box-sizing:border-box;margin:0;padding:0}}
html,body{{margin:0;background:#fff;font-family:M,sans-serif;color:#111827}}
.fr{{position:absolute;width:{FW}px;height:{FH}px;overflow:hidden;background:#F3F4F6;font-size:14px}}
.sa .B{{visibility:hidden}} .sb .A{{visibility:hidden}}
.sw{{display:grid}} .sw>*{{grid-area:1/1}}
.top{{height:44px;background:#fff;border-bottom:1px solid #E5E7EB;display:flex;align-items:center;padding:0 14px;gap:10px;font-weight:800;font-size:15px}}
.top .ham{{width:18px;height:2px;background:#111827;box-shadow:0 6px 0 #111827,0 -6px 0 #111827}}
.top .logo{{width:22px;height:22px;border-radius:50%;background:radial-gradient(#FFB208 35%,#F77B14);margin-left:2px}}
.top .r{{margin-left:auto;font-weight:600;font-size:12px;color:#6B7280}}
.card{{background:#fff;border:1px solid #E3E6EA;border-radius:10px}}
.pad{{padding:12px}}
.row{{display:flex;align-items:center;gap:10px}}
.col{{display:flex;flex-direction:column}}
.f1{{flex:1;min-width:0}}
.b1{{font-weight:800}} .b2{{font-weight:700}} .g{{color:#6B7280}} .s12{{font-size:12px}} .s13{{font-size:13px}} .s16{{font-size:16px}} .s18{{font-size:18px}} .s22{{font-size:22px}} .s28{{font-size:28px}}
.btn{{display:inline-flex;align-items:center;justify-content:center;border-radius:9px;font-weight:800;padding:10px 14px;font-size:14px;white-space:nowrap}}
.amber{{background:#FFB208;color:#111827}} .dark{{background:#0F1E3C;color:#fff}} .ghost{{background:#fff;border:1.5px solid #D1D5DB;color:#111827}} .grn{{background:#16A34A;color:#fff}}
.tag{{display:inline-block;border-radius:6px;padding:3px 8px;font-size:11.5px;font-weight:800;white-space:nowrap}}
.t-g{{background:#DCFCE7;color:#0E7A3A}} .t-r{{background:#FEE2E2;color:#B42318}} .t-o{{background:#FFEDD5;color:#B54708}} .t-b{{background:#DBEAFE;color:#1D4ED8}} .t-n{{background:#E5E7EB;color:#374151}} .t-d{{background:#0F1E3C;color:#fff}}
.chip{{display:inline-block;padding:7px 12px;border-radius:8px;background:#E5E7EB;font-weight:700;font-size:13px;white-space:nowrap}}
.chip.on{{background:#0F1E3C;color:#fff}}
.hl{{outline:3px solid #FFB208;outline-offset:1px;border-radius:10px;background-color:#FFF8E6}}
.fld{{background:#fff;border:1.5px solid #D1D5DB;border-radius:9px;padding:8px 11px}}
.fld .l{{font-size:11px;color:#6B7280;font-weight:600}} .fld .v{{font-size:15px;font-weight:700;margin-top:1px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}}
.fld.hl{{border-color:#FFB208}}
.tile{{background:#fff;border:1px solid #E3E6EA;border-radius:10px;overflow:hidden}}
.tile .ph{{height:54px;display:flex;align-items:center;justify-content:center;font-weight:800;font-size:20px}}
.tile .nm{{padding:6px 9px 0;font-size:12.5px;font-weight:700;height:36px;line-height:15px;overflow:hidden}}
.tile .pr{{padding:0 9px 7px;font-weight:800;font-size:15px;color:#A16207}}
.grid{{display:grid;gap:8px}}
.sep{{height:1px;background:#E5E7EB}}
.tot{{display:flex;justify-content:space-between;align-items:baseline;font-weight:800}}
.ck{{width:20px;height:20px;border-radius:5px;border:2px solid #9CA3AF;background:#fff;display:inline-block;flex:none}}
.ck.on{{background:#FFB208;border-color:#FFB208;position:relative}} .ck.on:after{{content:'';position:absolute;left:5px;top:1px;width:5px;height:10px;border:solid #111;border-width:0 2.5px 2.5px 0;transform:rotate(45deg)}}
.dim{{position:absolute;inset:0;background:rgba(17,24,39,.5)}}
.dlg{{position:absolute;left:24px;right:24px;background:#fff;border-radius:14px;padding:16px;box-shadow:0 12px 40px rgba(0,0,0,.35)}}
.toast{{position:absolute;left:18px;right:18px;border-radius:10px;padding:11px 13px;font-weight:700;font-size:13px}}
"""


def sw(a, b):
    return f'<div class="sw"><div class="A">{a}</div><div class="B">{b}</div></div>'


def onb(x, style=''):
    return f'<div class="B" style="{style}">{x}</div>'


def ona(x, style=''):
    return f'<div class="A" style="{style}">{x}</div>'


def top(title, right=''):
    return f'<div class="top"><div class="ham"></div><div class="logo"></div><span>{title}</span><span class="r">{right}</span></div>'


def btn(text, kind='amber', style=''):
    return f'<span class="btn {kind}" style="{style}">{text}</span>'


def tag(text, kind='g'):
    return f'<span class="tag t-{kind}">{text}</span>'


def chip(text, on=False):
    return f'<span class="chip{" on" if on else ""}">{text}</span>'


def fld(label, value, hl=False, style=''):
    return f'<div class="fld{" hl" if hl else ""}" style="{style}"><div class="l">{label}</div><div class="v">{value}</div></div>'


def ck(on=False):
    return f'<span class="ck{" on" if on else ""}"></span>'


def money(v):
    s = f'{v:,.2f}'.rstrip('0').rstrip('.') if isinstance(v, float) and v != int(v) else f'{int(v):,}'
    return s.replace(',', ' ')


def tile(ini, color, name, price, extra='', style=''):
    return (f'<div class="tile" style="{style}"><div class="ph" style="background:{color}22;color:{color}">{ini}</div>'
            f'<div class="nm">{name}</div><div class="pr">{price}{extra}</div></div>')


def line(name, sub, right, badge='', cls='', style=''):
    """Строка чека: название, подпись, сумма справа."""
    return (f'<div class="card pad row {cls}" style="padding:9px 12px;{style}"><div class="f1"><div class="b2 s13" style="white-space:nowrap;overflow:hidden;text-overflow:ellipsis">{name}</div>'
            f'<div class="s12 g" style="margin-top:2px">{sub} {badge}</div></div><div class="b1 s16" style="white-space:nowrap">{right}</div></div>')


def barcode(w, h, seed=7):
    import random
    r = random.Random(seed)
    x, out = 0, ''
    while x < w - 4:
        bw = r.choice([2, 2, 3, 4, 5])
        if r.random() < .55:
            out += f'<rect x="{x}" y="0" width="{bw}" height="{h}" fill="#111"/>'
        x += bw + r.choice([2, 3])
    return f'<svg width="{w}" height="{h}">{out}</svg>'


def frame_html(body, state):
    return f'<div class="fr s{state}">{body}</div>'
