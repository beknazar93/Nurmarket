# -*- coding: utf-8 -*-
"""Макеты экранов кассы NurCRM для роликов по сферам (вымышленные данные).
Запуск: python screens.py [код_сферы ...]  -> screens/<сфера>/<ru|ky>_<сцена>_<a|b>.png (960x1051).
Для каждой сферы и языка рисуется ОДНА страница со всеми кадрами сразу (быстро), затем режется на PNG.
Каждая функция сцены принимает T(ru, ky) и возвращает HTML кадра 480x520; состояния A/B — через sw()/onb() из ui.py.
Нужен Chrome (headless). Что показано и откуда это в коде — «Сценарий и тексты по сферам.md»."""
import pathlib, subprocess, sys, tempfile, time
from PIL import Image
from ui import *

OUT = HERE / 'screens'
CHROME = r'C:\Program Files\Google\Chrome\Application\chrome.exe'
COL = ['#B8860B', '#5470E1', '#EE3083', '#3E9157', '#9F81DA', '#D16B60', '#0DA38A', '#C95523']
PAD = 'padding:12px 14px;display:flex;flex-direction:column;gap:8px'


def bottom(inner):
    return f'<div style="position:absolute;left:14px;right:14px;bottom:14px">{inner}</div>'


def total(label, a, b=None, pay=None):
    t = sw(f'<span class="s28">{a}</span>', f'<span class="s28">{b}</span>') if b else f'<span class="s28">{a}</span>'
    p = btn(pay, 'amber', 'width:100%;padding:14px;font-size:16px;margin-top:8px') if pay else ''
    return bottom(f'<div class="tot"><span class="s16">{label}</span>{t}</div>{p}')


# ───────────── ПРОДУКТЫ ─────────────
def g_hook(T):
    lab = (f'<div class="card pad row" style="gap:14px"><div>{barcode(150, 60, 3)}<div class="s12 g" style="text-align:center;letter-spacing:2px;margin-top:2px">2 100123 004205</div></div>'
           f'<div class="f1"><div class="b1 s16">{T("Сыр «Российский»", "Сыр «Российский»")}</div><div class="s13 g" style="margin:2px 0 5px">0,420 {T("кг", "кг")} · 218 {T("сом", "сом")}</div>{tag(T("Этикетка весов", "Таразы этикеткасы"), "n")}</div></div>')
    rows = (line(T('Хлеб', 'Нан'), '2 × 45', '90') + line(T('Молоко 1 л', 'Сүт 1 л'), '1 × 95', '95')
            + onb(line(T('Сыр «Российский»', 'Сыр «Российский»'), f'0,420 {T("кг", "кг")} × 520', '218', tag(T('вес с этикетки', 'салмагы этикеткадан'), 'g'), 'hl')))
    return (top(T('Касса — NurCRM', 'Касса — NurCRM'), T('Весы Штрих · Rongta · CAS', 'Таразы Штрих · Rongta · CAS'))
            + f'<div style="{PAD}">{lab}{rows}</div>' + total(T('Итого', 'Жыйынтык'), '185', '403', T('Оплатить', 'Төлөө')))


def g_exp(T):
    base = (top(T('Касса — NurCRM', 'Касса — NurCRM'))
            + f'<div style="{PAD}">' + line(T('Хлеб', 'Нан'), '1 × 45', '45')
            + line(T('Йогурт 0,5 л', 'Йогурт 0,5 л'), '1 × 85', '85', tag(T('до 25.11', '25.11 чейин'), 'r')) + '</div>')
    dlg = onb('<div class="dim"></div>' + f'<div class="dlg" style="top:120px"><div class="b1 s18" style="color:#B42318">{T("Срок годности вышел", "Жарактуулук мөөнөтү өттү")}</div>'
              f'<div class="b2 s16" style="margin-top:8px">{T("Йогурт 0,5 л", "Йогурт 0,5 л")}</div>'
              f'<div class="g s13" style="margin-top:3px">{T("Партия № 14 · годен до 25.11", "Партия № 14 · 25.11 чейин жарактуу")}</div>'
              f'<div class="b1 s18" style="margin-top:16px">{T("Продать всё равно?", "Баары бир сатабызбы?")}</div>'
              f'<div class="row" style="margin-top:14px">{btn(T("Нет", "Жок"), "ghost", "flex:1")}{btn(T("Да, по PIN", "Ооба, PIN менен"), "dark", "flex:1")}</div></div>', 'position:absolute;inset:0')
    return base + total(T('Итого', 'Жыйынтык'), '130') + dlg


def g_quick(T):
    names = [(T('Хлеб', 'Нан'), 'Х', 45), (T('Пакет', 'Пакет'), 'П', 5), (T('Вода 0,5', 'Суу 0,5'), 'В', 30), (T('Семечки', 'Күн карама'), 'С', 60), (T('Спички', 'Ширеңке'), 'Ш', 10), (T('Жвачка', 'Сагыз'), 'Ж', 25)]
    grid = '<div class="grid" style="grid-template-columns:repeat(3,1fr)">' + ''.join(
        tile(n[1], COL[i], n[0], f'{n[2]} {T("сом", "сом")}', style='box-shadow:0 1px 0 #E5E7EB' + (';outline:3px solid #FFB208' if i in (0, 1, 2) else '')) for i, n in enumerate(names[:3])) + '</div>'
    rec = sw(f'<div class="card pad g s13" style="text-align:center;padding:14px">{T("Чек пуст — ходовые товары одной кнопкой", "Чек бош — ыкчам товарлар бир баскыч менен")}</div>',
             line(T('Хлеб', 'Нан'), '1 × 45', '45') + line(T('Пакет', 'Пакет'), '1 × 5', '5') + line(T('Вода 0,5', 'Суу 0,5'), '1 × 30', '30'))
    return (top(T('Быстрые товары', 'Ыкчам товарлар')) + f'<div style="{PAD}">{grid}{rec}</div>'
            + total(T('Итого', 'Жыйынтык'), '0', '80'))


def g_label(T):
    prods = [(T('Печенье «Шоколад»', 'Печенье «Шоколад»'), 120), (T('Чай чёрный', 'Кара чай'), 185), (T('Сахар 1 кг', 'Шекер 1 кг'), 95), (T('Масло 0,9 л', 'Май 0,9 л'), 340)]
    rows = ''.join(f'<div class="card pad row" style="padding:9px 12px">{sw(ck(False), ck(i < 3))}<div class="f1 b2 s13">{n}</div><div class="b1">{p}</div></div>' for i, (n, p) in enumerate(prods))
    tagp = onb(f'<div class="card pad row hl" style="gap:12px;margin-top:2px"><div style="width:120px"><div class="s12 b1">{T("Чай чёрный", "Кара чай")}</div><div class="b1 s22">185 <span class="s12">{T("сом", "сом")}</span></div></div>{barcode(120, 46, 5)}</div>')
    return (top(T('Ценники и штрихкоды', 'Баа жана штрихкод'), f'{T("Список", "Тизме")} · {T("Плитки", "Плитка")}')
            + f'<div style="{PAD}">{rows}{tagp}</div>'
            + bottom(f'<div class="row">{sw(f"<span class=g>{T("Выбрано", "Тандалды")}: 0</span>", f"<span class=b2>{T("Выбрано", "Тандалды")}: 3</span>")}<span class="f1"></span>{btn(T("Печать", "Басып чыгаруу"), "amber")}</div>'))


def g_minus(T):
    tl = '<div class="grid" style="grid-template-columns:repeat(2,1fr)">' + tile('Х', COL[0], T('Хлеб белый', 'Ак нан'), f'45 {T("сом", "сом")}',
         extra='<div class="s12 b1" style="margin-top:2px">' + sw(f'<span style="color:#0E7A3A">{T("2 шт", "2 даана")}</span>', f'<span style="color:#B42318">{T("Минус −3 шт.", "Минус −3 даана")}</span>') + '</div>', style='outline:3px solid #FFB208') \
         + tile('М', COL[1], T('Молоко 1 л', 'Сүт 1 л'), f'95 {T("сом", "сом")}', extra=f'<div class="s12 b1" style="color:#0E7A3A;margin-top:2px">14 {T("шт", "даана")}</div>') + '</div>'
    st = f'<div class="card pad row"><span class="f1 b2 s13">{T("Продавать без остатка", "Калдыксыз сатуу")}</span><span style="width:40px;height:22px;border-radius:11px;background:#16A34A;position:relative"><i style="position:absolute;right:3px;top:3px;width:16px;height:16px;border-radius:50%;background:#fff"></i></span></div>'
    msg = onb(f'<div class="toast" style="position:static;background:#FEE2E2;color:#B42318">{T("Продано больше, чем есть на складе: остаток ушёл в минус", "Кампадагыдан көп сатылды: калдык минуска кетти")}</div>')
    return top(T('Касса — NurCRM', 'Касса — NurCRM')) + f'<div style="{PAD}">{st}{tl}{msg}</div>' + bottom(btn(T('Оплатить', 'Төлөө'), 'amber', 'width:100%;padding:14px;font-size:16px'))


# ───────────── АВТОЗАПЧАСТИ ─────────────
PARTS = [('Колодки передние', 'Алдыңкы колодкалар', 'AX-4410', '04465-X1', 2350, 6), ('Фильтр масляный', 'Май чыпкасы', 'FL-220', '15208-X7', 480, 14), ('Свеча зажигания', 'Ширетме шамы', 'SP-91', '90919-X3', 390, 20)]


def part_row(T, p, sub=None, hl=False, right_extra=''):
    return (f'<div class="card pad row {"hl" if hl else ""}" style="padding:9px 12px"><div class="f1"><div class="b2 s13">{T(p[0], p[1])}</div>'
            f'<div class="s12 g" style="margin-top:2px">{sub or f"{p[2]} · OEM {p[3]}"}</div></div><div style="text-align:right"><div class="b1 s16">{money(p[4])}</div><div class="s12" style="color:#0E7A3A;font-weight:700">{T("Остаток", "Калдык")}: {p[5]}</div></div></div>')


def a_hook(T):
    field = sw(f'<span class="g">{T("Номер, OEM или название", "Номери, OEM же аталышы")}</span>', '<b>AX-4410</b><span style="display:inline-block;width:2px;height:16px;background:#FFB208;vertical-align:middle;margin-left:2px"></span>')
    res = sw(f'<div class="g s13" style="padding:30px 0;text-align:center">{T("Название не помнят — только номер", "Аталышы эсте жок — номери гана")}</div>',
             part_row(T, PARTS[0], hl=True) + f'<div class="s12 g" style="margin:6px 2px 0">{T("Найдено по каталожному номеру, в названии номера нет", "Каталог номери боюнча табылды, аталышында номер жок")}</div>')
    return (top(T('Поиск детали', 'Тетикти издөө')) + f'<div style="{PAD}"><div class="fld hl" style="border-width:2px"><div class="v">{field}</div></div>{res}</div>')


def a_analog(T):
    orig = f'<div class="card pad row" style="padding:10px 12px"><div class="f1"><div class="b2 s13">{T("Колодки передние", "Алдыңкы колодкалар")} · AX-4410</div><div class="s12 g">{T("Оригинал", "Түп нуска")}</div></div>{tag(T("Нет в наличии", "Жок"), "r")}</div>'
    btn_a = btn(T('Аналоги (3)', 'Аналогдор (3)'), 'amber', 'margin-top:2px')
    an = ''.join(f'<div class="card pad row" style="padding:9px 12px"><div class="f1"><div class="b2 s13">{b} · {n}</div><div class="s12 g">{T("Аналог", "Аналог")}</div></div><div style="text-align:right"><div class="b1">{money(p)}</div><div class="s12" style="color:#0E7A3A;font-weight:700">{T("Есть", "Бар")}: {q}</div></div></div>'
                 for b, n, p, q in (('Brand A', 'BA-11', 2100, 4), ('Brand B', 'BB-207', 1890, 9), ('Brand C', 'BC-30', 1650, 3)))
    return top(T('Карточка детали', 'Тетиктин карточкасы')) + f'<div style="{PAD}">{orig}{sw(btn_a, "<div class=b1 s16>" + T("Аналоги и кроссы", "Аналогдор жана кросстор") + "</div>")}{onb(an)}</div>'


def a_cond(T):
    r1 = f'<div class="card pad row" style="padding:10px 12px"><div class="f1"><div class="b2 s13">{T("Фара передняя левая", "Алдыңкы сол фара")}</div><div class="s12 g">HL-52 · OEM 81150-X2</div></div>{tag(T("Новая", "Жаңы"), "g")}<div class="b1 s16" style="width:70px;text-align:right">8 400</div></div>'
    r2 = f'<div class="card pad row hl" style="padding:10px 12px"><div class="f1"><div class="b2 s13">{T("Фара передняя левая", "Алдыңкы сол фара")}</div><div class="s12 g">HL-52 · {T("с разборки", "чачылган машинадан")}</div></div>{tag(T("Б/У", "Б/У"), "o")}<div class="b1 s16" style="width:70px;text-align:right">3 200</div></div>'
    cell = onb(f'<div class="card pad row hl" style="gap:12px"><div style="font-size:26px">▦</div><div><div class="s12 g">{T("Ячейка склада", "Кампанын уячасы")}</div><div class="b1 s18">{T("Стеллаж 3, полка B", "3-стеллаж, B текче")}</div></div></div>')
    return top(T('Фары · наличие', 'Фаралар · бар болушу')) + f'<div style="{PAD}">{r1}{r2}{cell}</div>'


def a_level(T):
    lv = (f'<div class="card pad"><div class="b2 s13" style="margin-bottom:8px">{T("Цены детали", "Тетиктин баалары")} · {T("Фильтр масляный", "Май чыпкасы")}</div>'
          f'<div class="row" style="gap:8px"><div class="f1">{fld(T("Розница", "Чекене"), "1 100")}</div><div class="f1">{fld(T("Мастер / СТО", "Уста / СТО"), "900", True)}</div><div class="f1">{fld(T("Опт", "Дүң"), "800")}</div></div></div>')
    cl = sw(f'<div class="card pad row"><span class="f1 b2 s13">{T("Клиент: не выбран", "Кардар: тандалган жок")}</span></div>',
            f'<div class="card pad row hl"><span class="f1 b2 s13">{T("Клиент: СТО «Демо»", "Кардар: СТО «Демо»")}</span>{tag(T("Мастер / СТО", "Уста / СТО"), "b")}</div>')
    ln = line(T('Фильтр масляный', 'Май чыпкасы'), '1 ×', sw('1 100', '900'), onb(tag(T('цена СТО', 'СТО баасы'), 'b'), 'display:inline'))
    return top(T('Касса — NurCRM', 'Касса — NurCRM')) + f'<div style="{PAD}">{lv}{cl}{ln}</div>' + total(T('Итого', 'Жыйынтык'), '1 100', '900')


def a_order(T):
    form = (f'<div class="card pad" style="display:flex;flex-direction:column;gap:8px"><div class="b1 s16">{T("Заказ под клиента", "Кардарга заказ")}</div>'
            f'<div class="row" style="gap:8px"><div class="f1">{fld(T("Клиент", "Кардар"), "Нурлан Т.")}</div><div class="f1">{fld(T("Деталь", "Тетик"), T("Генератор", "Генератор"))}</div></div>'
            f'<div class="row" style="gap:8px"><div class="f1">{fld(T("Предоплата", "Алдын ала төлөм"), "3 000 " + T("сом", "сом"), True)}</div><div class="f1">{fld(T("Привезут через", "Алып келишет"), T("2 дня", "2 күндөн кийин"))}</div></div></div>')
    done = onb(f'<div class="toast" style="position:static;background:#DCFCE7;color:#0E7A3A;margin-top:2px">{T("Заказ записан · предоплата 3 000 сом", "Заказ жазылды · алдын ала төлөм 3 000 сом")}</div>')
    ready = onb(f'<div class="card pad row">{tag(T("Ждём деталь", "Тетикти күтөбүз"), "o")}<span class="f1"></span>{btn(T("Выдать", "Берүү"), "ghost")}</div>')
    return top(T('Заказ под клиента', 'Кардарга заказ')) + f'<div style="{PAD}">{form}{done}{ready}</div>' + bottom(btn(T('Записать заказ', 'Заказды жазуу'), 'amber', 'width:100%;padding:14px;font-size:16px'))


# ───────────── БАЗАР ─────────────
CATS = [('Обувь', 'Бут кийим', 124), ('Куртки', 'Куртка', 87), ('Сумки', 'Сумка', 56), ('Игрушки', 'Оюнчук', 203), ('Посуда', 'Идиш-аяк', 91), ('Платки', 'Жоолук', 64)]


def b_hook(T):
    tl = '<div class="grid" style="grid-template-columns:repeat(2,1fr);gap:10px">' + ''.join(
        f'<div class="card" style="height:96px;background:{COL[i]}22;border-color:{COL[i]}55;padding:12px;display:flex;flex-direction:column;justify-content:space-between{";outline:3px solid #FFB208" if i == 0 else ""}"><div class="b1 s18" style="color:{COL[i]}">{T(c[0], c[1])}</div><div class="s13 b2 g">{c[2]} {T("товаров", "товар")}</div></div>' for i, c in enumerate(CATS[:6])) + '</div>'
    lst = ''.join(line(n[0], f'{T("Обувь", "Бут кийим")}', money(n[2])) for n in ((T('Туфли женские', 'Аялдардын туфлиси'), '', 1200), (T('Кроссовки', 'Кроссовка'), '', 1850), (T('Сапоги зимние', 'Кышкы өтүк'), '', 2400)))
    back = f'<div class="b2 s13" style="margin-bottom:2px">← {T("Все категории", "Бардык категориялар")}</div>'
    return top(T('Касса — NurCRM', 'Касса — NurCRM'), T('Плитки категорий', 'Категория плиткалары')) + f'<div style="{PAD}">' + sw(tl, back + lst) + '</div>'


def b_volume(T):
    steps = (f'<div class="card pad"><div class="b2 s13" style="margin-bottom:8px">{T("Цена по количеству", "Санына жараша баа")} · {T("Туфли женские", "Аялдардын туфлиси")}</div>'
             f'<div class="row" style="gap:8px"><div class="f1">{fld(T("от 1 шт", "1 даанадан"), "1 200")}</div><div class="f1">{fld(T("от 3 шт", "3 даанадан"), "1 100")}</div><div class="f1">{fld(T("от 10 шт", "10 даанадан"), "1 000", True)}</div></div></div>')
    ln = (f'<div class="card pad row hl" style="padding:12px"><div class="f1"><div class="b2 s13">{T("Туфли женские", "Аялдардын туфлиси")}</div>'
          f'<div class="s12 g" style="margin-top:3px">{sw(T("2 пар", "2 жуп") + " × 1 200", T("10 пар", "10 жуп") + " × 1 000")}</div>'
          f'<div class="s12 b1" style="margin-top:4px;color:#0E7A3A">{sw("&nbsp;", T("Опт от 10 — 1 000", "Дүң, мин. 10 — 1 000"))}</div></div><div class="b1 s22">{sw("2 400", "10 000")}</div></div>')
    return top(T('Касса — NurCRM', 'Касса — NurCRM')) + f'<div style="{PAD}">{steps}{ln}</div>' + total(T('Итого', 'Жыйынтык'), '2 400', '10 000', T('Оплатить', 'Төлөө'))


def b_cur(T):
    ch = f'<div class="row" style="gap:8px">{chip("USD", True)}{chip("RUB")}{chip("CNY")}</div>'
    rate = f'<div class="s12 g">{T("Курс компании", "Компаниянын курсу")}: 1 USD = 89,50 {T("сом", "сом")}</div>'
    inp = sw(f'<div class="fld" style="border-width:2px"><div class="l">{T("Сумма в валюте", "Валютадагы сумма")}</div><div class="v g">—</div></div>',
             f'<div class="fld hl" style="border-width:2px"><div class="l">{T("Сумма в валюте", "Валютадагы сумма")}</div><div class="v">50 USD</div></div>')
    conv = onb(f'<div class="card pad hl"><div class="row"><span class="f1 b2 s13">{T("В сомах", "Сом менен")}</span><span class="b1 s18">4 475</span></div><div class="row" style="margin-top:6px"><span class="f1 b2 s13">{T("Сдача (сом)", "Кайтарым (сом)")}</span><span class="b1 s18" style="color:#0E7A3A">475</span></div></div>')
    return (top(T('Оплата наличными', 'Накта акча менен төлөө')) + f'<div style="{PAD}"><div class="tot"><span class="s16">{T("К оплате", "Төлөнүүчү")}</span><span class="s28">4 000</span></div>'
            f'<div class="b2 s13" style="margin-top:4px">{T("Принято в валюте", "Валютада алынды")}</div>{ch}{rate}{inp}{conv}</div>')


def b_cargo(T):
    rows = ''.join(f'<div class="row s13" style="padding:5px 0;border-bottom:1px solid #E5E7EB"><span class="f1 b2">{n}</span><span style="width:46px;text-align:right">{q}</span><span style="width:54px;text-align:right">{p}</span><span style="width:84px;text-align:right">{w}</span></div>'
                   for n, q, p, w in ((T('Куртка зимняя', 'Кышкы куртка'), 120, 4, 96), (T('Шапка', 'Баш кийим'), 300, 3, 45), (T('Перчатки', 'Мээлей'), 200, 2, 30)))
    hd = f'<div class="row s12 g b2" style="padding-bottom:4px;border-bottom:2px solid #111827"><span class="f1">{T("Товар", "Товар")}</span><span style="width:46px;text-align:right">{T("Шт", "Даана")}</span><span style="width:54px;text-align:right">{T("Мест", "Орун")}</span><span style="width:84px;text-align:right">{T("Брутто, кг", "Брутто, кг")}</span></div>'
    cargo = onb(f'<div class="card pad hl" style="margin-top:6px"><div class="b1 s16" style="margin-bottom:6px">{T("Карго", "Карго")}</div><div class="s13" style="line-height:20px">{T("Всего мест", "Жалпы орун")}: <b>12</b> · {T("Брутто", "Брутто")}: <b>171 кг</b> · {T("Мешков", "Баштык")}: <b>12</b><br>{T("Перевозчик", "Ташуучу")}: <b>{T("Карго-Демо", "Карго-Демо")}</b> · {T("Получатель", "Алуучу")}: <b>{T("г. Ош", "Ош ш.")}</b><br>{T("Трек", "Трек")}: <b>KD-482910</b></div></div>')
    return (top(T('Накладная A4', 'A4 накладной'), '№ 128') + f'<div style="{PAD}"><div class="card pad" style="background:#fff">{hd}{rows}</div>{cargo}</div>')


# ───────────── ОДЕЖДА ─────────────
def c_hook(T):
    three = ''.join(line(f'{T("Куртка зимняя", "Кышкы куртка")} {s}', f'{T("Остаток", "Калдык")}: {q}', '3 900') for s, q in (('M', 4), ('L', 6), ('XL', 2)))
    var = (f'<div class="card pad"><div class="row"><span class="f1 b1 s16">{T("Куртка зимняя", "Кышкы куртка")}</span>{tag(T("1 карточка", "1 карточка"), "g")}</div><div class="b1 s16" style="margin:4px 0 8px;color:#A16207">3 900 {T("сом", "сом")}</div>'
           f'<div class="row s12 g b2" style="padding-bottom:3px"><span style="width:58px">{T("Размер", "Өлчөм")}</span><span class="f1">{T("Синий", "Көк")}</span><span class="f1">{T("Чёрный", "Кара")}</span></div>'
           + ''.join(f'<div class="row s13" style="padding:6px 0;border-top:1px solid #E5E7EB"><span class="b1" style="width:58px">{s}</span><span class="f1 b2">{a}</span><span class="f1 b2">{b}</span></div>' for s, a, b in (('M', 4, 2), ('L', 6, 3), ('XL', 2, 0))) + '</div>')
    return top(T('Товары', 'Товарлар')) + f'<div style="{PAD}">' + sw(three, var) + '</div>'


def c_bar(T):
    vr = ''.join(f'<div class="card pad row {"hl" if s == "L" else ""}" style="padding:8px 12px"><span class="b1" style="width:34px">{s}</span><div class="f1">{barcode(110, 26, i + 20)}<div class="s12 g" style="letter-spacing:1px;margin-top:1px">46000{i}120{i}8</div></div><span class="b2 s13">{T("Остаток", "Калдык")} {q}</span></div>' for i, (s, q) in enumerate((('M', 4), ('L', 6), ('XL', 2))))
    ln = onb(line(f'{T("Куртка зимняя", "Кышкы куртка")} · L · {T("синий", "көк")}', f'1 × 3 900 · {T("размер можно сменить", "өлчөмдү алмаштырууга болот")}', '3 900', '', 'hl'))
    return top(T('У каждого размера свой штрихкод', 'Ар бир өлчөмдүн өз штрихкоду')) + f'<div style="{PAD}">{vr}{ln}</div>' + total(T('Итого', 'Жыйынтык'), '0', '3 900')


def c_exch(T):
    ret = f'<div class="card pad row"><div class="f1"><div class="s12 g b2">{T("Возвращает", "Кайтарат")}</div><div class="b2 s13">{T("Платье красное, 46", "Кызыл көйнөк, 46")}</div></div><div class="b1 s16">2 800</div></div>'
    take = sw(f'<div class="card pad row" style="height:58px"><span class="g s13 f1">{T("Берёт: выберите товар", "Алат: товарды тандаңыз")}</span></div>',
              f'<div class="card pad row hl"><div class="f1"><div class="s12 g b2">{T("Берёт", "Алат")}</div><div class="b2 s13">{T("Платье красное, 48", "Кызыл көйнөк, 48")}</div></div><div class="b1 s16">3 100</div></div>')
    diff = onb(f'<div class="card pad row hl" style="background:#FFF8E6"><span class="f1 b2 s16">{T("Доплата", "Кошумча төлөм")}</span><span class="b1 s28">300</span></div>')
    return top(T('Обмен товара', 'Товарды алмаштыруу')) + f'<div style="{PAD}">{ret}{take}{diff}</div>' + bottom(btn(T('Оформить обмен', 'Алмаштырууну тариздөө'), 'amber', 'width:100%;padding:14px;font-size:16px'))


def c_cons(T):
    cons = (f'<div class="card pad hl"><div class="b2 s13" style="margin-bottom:8px">{T("Консультант", "Консультант")}</div><div class="row" style="gap:8px"><div class="f1">{fld(T("Кто помог", "Ким жардам берди"), T("Айгуль", "Айгүл"))}</div>'
            f'<div style="width:110px">{fld(T("Процент", "Пайыз"), "5 %")}</div></div></div>')
    rep = onb(f'<div class="card pad row"><span class="f1 b2 s13">{T("Отчёт по зарплате", "Эмгек акы отчёту")} · {T("Айгуль", "Айгүл")}</span><span class="b1">5 % = 195</span></div>')
    return (top(T('Оплата', 'Төлөө')) + f'<div style="{PAD}">{line(T("Пальто женское", "Аялдар пальтосу"), "1 × 3 900", "3 900")}{cons}{rep}</div>' + total(T('К оплате', 'Төлөнүүчү'), '3 900', None, T('Оплатить', 'Төлөө')))


# ───────────── УСЛУГИ / РЕМОНТ ─────────────
def s_hook(T):
    form = (f'<div class="card pad" style="display:flex;flex-direction:column;gap:8px"><div class="b1 s16">{T("Новый приём в ремонт", "Оңдоого жаңы кабыл алуу")}</div>'
            f'<div class="row" style="gap:8px"><div class="f1">{fld(T("Клиент", "Кардар"), T("Нурлан Т.", "Нурлан Т."))}</div><div class="f1">{fld(T("Устройство", "Түзмөк"), T("Телефон", "Телефон") + " Demo X1")}</div></div>'
            f'<div class="row" style="gap:8px"><div class="f1">{fld("IMEI", "350000000000000")}</div><div class="f1">{fld(T("Неисправность", "Бузулуу"), T("не заряжается", "заряддалбайт"))}</div></div>'
            f'<div class="row" style="gap:8px"><div class="f1">{fld(T("Срок", "Мөөнөт"), T("3 дня", "3 күн"))}</div><div class="f1">{fld(T("Предоплата", "Алдын ала төлөм"), "500", True)}</div></div></div>')
    rc = (f'<div class="card pad" style="background:#fff;font-size:13px;line-height:19px"><div class="b1 s16" style="text-align:center">{T("Квитанция №", "Квитанция №")} Р-0001</div><div class="sep" style="margin:6px 0"></div>'
          f'{T("Клиент", "Кардар")}: <b>{T("Нурлан Т.", "Нурлан Т.")}</b><br>{T("Устройство", "Түзмөк")}: <b>Demo X1</b><br>{T("Неисправность", "Бузулуу")}: <b>{T("не заряжается", "заряддалбайт")}</b><br>{T("Срок", "Мөөнөт")}: <b>{T("3 дня", "3 күн")}</b> · {T("Предоплата", "Алдын ала төлөм")}: <b>500</b>'
          f'<div class="row s12 g" style="margin-top:12px"><span class="f1">{T("Клиент: ________", "Кардар: ________")}</span><span class="f1">{T("Принял: ________", "Кабыл алды: ________")}</span></div></div>')
    return top(T('Ремонт и сервис', 'Оңдоо жана сервис')) + f'<div style="{PAD}">' + sw(form, rc) + '</div>'


def s_status(T):
    st = f'<div class="row" style="gap:6px">{chip(T("Принят", "Кабыл алынды"))}{chip(T("В работе", "Иште"))}' + sw(chip(T('Готов', 'Даяр')), chip(T('Готов', 'Даяр'), True)) + f'{chip(T("Выдан", "Берилди"))}</div>'
    cur = (f'<div class="card pad row"><div class="f1"><div class="b2 s13">Р-0001 · Demo X1</div><div class="s12 g">{T("Нурлан Т.", "Нурлан Т.")} · {T("не заряжается", "заряддалбайт")}</div></div>{sw(tag(T("В работе", "Иште"), "o"), tag(T("Готов", "Даяр"), "g"))}</div>')
    msg = onb(f'<div class="toast" style="position:static;background:#DCFCE7;color:#0E7A3A">{T("Клиенту: «Ваш телефон готов — можно забирать»", "Кардарга: «Телефонуңуз даяр — алып кетсеңиз болот»")}</div>')
    act = onb(f'<div class="card pad row"><span class="f1 b2 s13">{T("Выдача: оплата и акт", "Берүү: төлөм жана акт")}</span>{btn(T("Выдать", "Берүү"), "amber")}</div>')
    return top(T('Ремонт и сервис', 'Оңдоо жана сервис')) + f'<div style="{PAD}">{cur}{st}{msg}{act}</div>' + bottom(sw(btn(T('Готово', 'Даяр'), 'dark', 'width:100%;padding:14px;font-size:16px'), btn(T('Выдать', 'Берүү'), 'amber', 'width:100%;padding:14px;font-size:16px')))


def s_rent(T):
    form = (f'<div class="card pad" style="display:flex;flex-direction:column;gap:8px"><div class="b1 s16">{T("Новый прокат", "Жаңы прокат")}</div>'
            f'<div class="row" style="gap:8px"><div class="f1">{fld(T("Клиент", "Кардар"), T("Бакыт С.", "Бакыт С."))}</div><div class="f1">{fld(T("Вещь", "Буюм"), T("Перфоратор", "Перфоратор"))}</div></div>'
            f'<div class="row" style="gap:8px"><div class="f1">{fld(T("Срок", "Мөөнөт"), T("2 дня", "2 күн"))}</div><div class="f1">{fld(T("Залог", "Күрөө"), "2 000", True)}</div></div></div>')
    lst = (f'<div class="card pad row"><div class="f1"><div class="b2 s13">{T("Перфоратор", "Перфоратор")} · {T("Бакыт С.", "Бакыт С.")}</div><div class="s12 g">{T("Залог", "Күрөө")} 2 000</div></div>{tag(T("вернуть сегодня", "бүгүн кайтаруу керек"), "o")}{btn(T("Вернуть", "Кайтаруу"), "ghost")}</div>')
    return top(T('Прокат', 'Прокат')) + f'<div style="{PAD}">' + sw(form, lst + f'<div class="toast" style="position:static;background:#FFEDD5;color:#B54708;margin-top:8px">{T("Напоминание: срок возврата сегодня", "Эскертме: кайтаруу мөөнөтү бүгүн")}</div>') + '</div>'


def s_journal(T):
    srch = f'<div class="fld hl" style="border-width:2px"><div class="v">{sw("<span class=g>" + T("Номер, телефон или клиент", "Номер, телефон же кардар") + "</span>", "Р-0001")}</div></div>'
    rows = (line('Р-0003 · Demo X2', T('Айбек Т.', 'Айбек Т.') + ' · 0700 000 003', '', tag(T('Принят', 'Кабыл алынды'), 'n')) + line('Р-0002 · Demo X5', T('Нурзат К.', 'Нурзат К.') + ' · 0700 000 002', '', tag(T('В работе', 'Иште'), 'o'))
            + line('Р-0001 · Demo X1', T('Нурлан Т.', 'Нурлан Т.') + ' · 0700 000 001', '', tag(T('Готов', 'Даяр'), 'g'), 'hl'))
    return (top(T('Журнал ремонтов', 'Оңдоолордун журналы')) + f'<div style="{PAD}">{srch}' + sw(rows, line('Р-0001 · Demo X1', T('Нурлан Т.', 'Нурлан Т.') + ' · 0700 000 001', '', tag(T('Готов', 'Даяр'), 'g'), 'hl')) + '</div>'
            + bottom(f'<div class="card pad row"><span class="f1 b2 s13">{T("Предоплата и оплата — через кассу", "Алдын ала төлөм жана төлөм — касса аркылуу")}</span>{tag(T("в смене", "сменада"), "g")}</div>'))


# ───────────── ХОЗМАГ / СТРОЙМАГ ─────────────
def h_hook(T):
    pad = (f'<div class="card pad"><div class="b2 s13">{T("Количество, м", "Саны, м")}</div><div class="b1" style="font-size:44px;margin:4px 0">{sw("1", "2,5")}</div>'
           f'<div class="grid" style="grid-template-columns:repeat(4,1fr);gap:6px">' + ''.join(f'<span class="btn ghost" style="padding:8px 0">{k}</span>' for k in ['7','8','9','⌫','4','5','6','0','1','2','3',',']) + '</div></div>')
    ln = line(T('Кабель ВВГ 3×2,5', 'ВВГ кабели 3×2,5'), sw(f'1 {T("м", "м")} × 85', f'2,5 {T("м", "м")} × 85'), sw('85', '212,5'), '', 'hl')
    return top(T('Касса — NurCRM', 'Касса — NurCRM'), T('Дробное количество', 'Бөлчөк сан')) + f'<div style="{PAD}">{ln}{pad}</div>' + total(T('Итого', 'Жыйынтык'), '85', '212,5')


def h_pack(T):
    box = (f'<div class="card pad row" style="gap:14px"><div>{barcode(150, 54, 9)}<div class="s12 g" style="text-align:center;letter-spacing:2px;margin-top:2px">4 600555 200001</div></div><div class="f1"><div class="b1 s16">{T("Саморезы 4×40", "Бурама 4×40")}</div><div class="s13 g">{T("Коробка", "Куту")} ×200</div></div></div>')
    ln = sw(f'<div class="card pad g s13" style="text-align:center;padding:16px">{T("Сканируйте коробку", "Кутуну сканерлеңиз")}</div>',
            line(T('Саморезы 4×40', 'Бурама 4×40'), f'200 {T("шт", "даана")} · {T("1 коробка", "1 куту")}', '1 600', tag(T('×200 одной строкой', '×200 бир сапта'), 'g'), 'hl'))
    two = onb(f'<div class="s12 g" style="text-align:center">{T("Ещё скан — ещё одна коробка", "Дагы сканер — дагы бир куту")}</div>')
    return top(T('Касса — NurCRM', 'Касса — NurCRM'), T('Упаковки', 'Таңгактар')) + f'<div style="{PAD}">{box}{ln}{two}</div>' + total(T('Итого', 'Жыйынтык'), '0', '1 600')


def h_price(T):
    cl = (f'<div class="card pad"><div class="row"><span class="f1 b1 s16">{T("Карточка клиента", "Кардардын карточкасы")}: {T("Бакыт С.", "Бакыт С.")}</span></div>'
          f'<div class="row hl" style="margin-top:8px;padding:10px;border-radius:10px"><span class="f1 b2 s13">{T("Покупает по оптовой цене (прораб)", "Дүң баада сатып алат (прораб)")}</span>'
          + sw('<span style="width:40px;height:22px;border-radius:11px;background:#D1D5DB;display:inline-block"></span>', '<span style="width:40px;height:22px;border-radius:11px;background:#16A34A;position:relative;display:inline-block"><i style="position:absolute;right:3px;top:3px;width:16px;height:16px;border-radius:50%;background:#fff"></i></span>') + '</div></div>')
    ln = line(T('Плитка 30×30', 'Плитка 30×30'), sw('10 × 520', '10 × 480'), sw('5 200', '4 800'), onb(tag(T('Цена прораба', 'Прорабдын баасы'), 'b'), 'display:inline'))
    ln2 = line(T('Клей плиточный', 'Плитка желими'), '2 × 450', '900')
    return top(T('Касса — NurCRM', 'Касса — NurCRM')) + f'<div style="{PAD}">{cl}{ln}{ln2}</div>' + total(T('Итого', 'Жыйынтык'), '6 100', '5 700')


def h_calc(T):
    inp = (f'<div class="row" style="gap:8px"><div class="f1">{fld(T("Длина, м", "Узундугу, м"), "5")}</div><div class="f1">{fld(T("Ширина, м", "Туурасы, м"), "4")}</div></div>')
    res = sw(f'<div class="card pad g s13" style="text-align:center;padding:18px">{T("Введите размеры комнаты", "Бөлмөнүн өлчөмүн киргизиңиз")}</div>',
             f'<div class="card pad hl"><div class="row"><span class="f1 b2 s13">{T("Площадь", "Аянты")}</span><span class="b1 s16">20 {T("м²", "м²")}</span></div><div class="row" style="margin-top:6px"><span class="f1 b2 s13">{T("С запасом нужно", "Запас менен керек")}</span><span class="b1 s22">10 {T("уп.", "таңгак")}</span></div></div>')
    return (top(T('Сколько нужно?', 'Канча керек?')) + f'<div style="{PAD}"><div class="card pad row"><div class="f1"><div class="b2 s13">{T("Ламинат 33 класс", "33-класстагы ламинат")}</div><div class="s12 g">2,4 {T("м²", "м²")} {T("в упаковке", "таңгакта")}</div></div>{tag(T("Комната", "Бөлмө"), "n")}</div>{inp}{res}</div>'
            + bottom(btn(T('В чек', 'Чекке'), 'amber', 'width:100%;padding:14px;font-size:16px')))


def h_est(T):
    rows = ''.join(f'<div class="row s13" style="padding:6px 0;border-bottom:1px solid #E5E7EB"><span class="f1 b2">{n}</span><span style="width:70px;text-align:right">{q}</span><span style="width:70px;text-align:right" class="b1">{s}</span></div>'
                   for n, q, s in ((T('Плитка 30×30', 'Плитка 30×30'), '12 ' + T('м²', 'м²'), '6 240'), (T('Клей плиточный', 'Плитка желими'), '8 ' + T('меш.', 'баш.'), '3 600'), (T('Затирка', 'Тегиздегич'), '4 ' + T('шт', 'даана'), '1 200'), (T('Ламинат', 'Ламинат'), '10 ' + T('уп.', 'таңгак'), '12 410')))
    doc = f'<div class="card pad" style="background:#fff"><div class="b1 s16" style="margin-bottom:6px">{T("Смета для клиента", "Кардар үчүн смета")}</div>{rows}<div class="tot" style="margin-top:8px"><span>{T("Итого", "Жыйынтык")}</span><span class="s22">23 450</span></div></div>'
    bt = onb(f'<div class="row" style="gap:8px">{btn(T("Печать", "Басып чыгаруу"), "ghost", "flex:1")}{btn("WhatsApp", "grn", "flex:1")}</div>')
    return top(T('Смета', 'Смета')) + f'<div style="{PAD}">{doc}{bt}</div>' + bottom(sw(btn(T('Посчитать', 'Эсептөө'), 'dark', 'width:100%;padding:14px;font-size:16px'), btn(T('Добавить всё в чек', 'Баарын чекке кошуу'), 'amber', 'width:100%;padding:14px;font-size:16px')))


# ───────────── ЭЛЕКТРОНИКА ─────────────
def e_hook(T):
    ln = line('Demo X1 128 GB', '1 × 18 900', '18 900')
    dlg = (f'<div class="dim"></div><div class="dlg" style="top:96px"><div class="b1 s18">{T("Серийный номер / IMEI", "Сериялык номер / IMEI")}</div><div class="g s13" style="margin:3px 0 12px">Demo X1 128 GB</div>'
           + sw(f'<div class="fld hl" style="border-width:2px"><div class="v g">{T("Введите или сканируйте", "Киргизиңиз же сканерлеңиз")}</div></div>',
                f'<div class="fld hl" style="border-width:2px"><div class="v">350000000000000</div></div><div class="s13 b2" style="color:#0E7A3A;margin-top:8px">✔ {T("IMEI верный — 15 цифр", "IMEI туура — 15 сан")}</div>')
           + f'<div class="row" style="margin-top:14px">{btn(T("Отмена", "Жокко чыгаруу"), "ghost", "flex:1")}{btn(T("Записать в чек", "Чекке жазуу"), "amber", "flex:1")}</div></div>')
    return top(T('Касса — NurCRM', 'Касса — NurCRM')) + f'<div style="{PAD}">{ln}</div>' + total(T('Итого', 'Жыйынтык'), '18 900') + dlg


def e_war(T):
    ln = line('Demo X1 128 GB', 'IMEI …0000', '18 900', tag(T('Гарантия 12 мес.', 'Кепилдик 12 ай'), 'g'))
    ret = (f'<div class="card pad" style="display:flex;flex-direction:column;gap:8px"><div class="b1 s16">{T("Возврат", "Кайтаруу")}</div><div class="row" style="gap:8px"><div class="f1">{fld(T("Причина", "Себеби"), T("Гарантия", "Кепилдик"), True)}</div><div class="f1">{fld(T("Куплен", "Сатылган"), T("8 мес. назад", "8 ай мурун"))}</div></div></div>')
    ok = onb(f'<div class="toast" style="position:static;background:#DCFCE7;color:#0E7A3A;font-size:14px">✔ {T("Гарантия действует — ещё 4 мес.", "Кепилдик иштейт — дагы 4 ай")}</div>')
    return top(T('Чек', 'Чек')) + f'<div style="{PAD}">{ln}{sw("", ret)}{ok}</div>'


def e_search(T):
    srch = f'<div class="fld hl" style="border-width:2px"><div class="v">{sw("<span class=g>" + T("Номер чека, товар или IMEI", "Чектин номери, товар же IMEI") + "</span>", "350000000000000")}</div></div>'
    res = (f'<div class="card pad hl"><div class="row"><span class="f1 b1 s16">{T("Чек", "Чек")} № 1041</span><span class="s12 g">12.03.2027</span></div><div class="b2 s13" style="margin:4px 0">Demo X1 128 GB · IMEI 350000000000000</div>{tag(T("Гарантия до 12.03.2028", "Кепилдик 12.03.2028 чейин"), "g")}</div>')
    empty = (f'<div class="card pad g s13" style="padding:40px 16px;text-align:center">{T("Покупатель пришёл с вопросом — найдите чек по номеру", "Сатып алуучу суроо менен келди — чекти номери боюнча табыңыз")}</div>')
    return top(T('История чеков', 'Чектердин тарыхы')) + f'<div style="{PAD}">{srch}{sw(empty, res)}</div>'


def e_trade(T):
    form = (f'<div class="card pad" style="display:flex;flex-direction:column;gap:8px"><div class="b1 s16">{T("Выкуп б/у", "Б/У сатып алуу")}</div>'
            f'<div class="row" style="gap:8px"><div class="f1">{fld(T("Устройство", "Түзмөк"), "Demo X1")}</div><div class="f1">{fld(T("Состояние", "Абалы"), T("хорошее", "жакшы"))}</div></div>'
            f'<div class="row" style="gap:8px"><div class="f1">{fld(T("Сумма выкупа", "Сатып алуу суммасы"), "7 000", True)}</div><div class="f1">{fld(T("Продавец", "Сатуучу"), T("Айбек Т.", "Айбек Т."))}</div></div></div>')
    ch = f'<div class="row" style="gap:8px">{btn(T("Выплатить из кассы", "Кассадан төлөө"), "dark", "flex:1")}{btn(T("Зачесть в покупку", "Сатып алууга эсептөө"), "amber", "flex:1")}</div>'
    done = onb(f'<div class="toast" style="position:static;background:#DCFCE7;color:#0E7A3A">{T("Акт приёма готов · на склад как б/у", "Кабыл алуу акты даяр · кампага б/у катары")}</div>')
    return top(T('Trade-in / скупка', 'Trade-in / сатып алуу')) + f'<div style="{PAD}">{form}{ch}{done}</div>'


# ───────────── КАНЦТОВАРЫ ─────────────
def t_hook(T):
    items = [(T('Тетрадь 12 л', 'Дептер 12 б'), 10, 0), (T('Ручка синяя', 'Көк калем'), 5, 0), (T('Дневник', 'Күндөлүк'), 1, 0), (T('Пенал', 'Калем сап'), 1, 0), (T('Альбом А4', 'А4 альбом'), 2, 1)]
    rows = ''.join(f'<div class="card pad row" style="padding:8px 12px">{sw(ck(False), ck(not o))}<div class="f1 b2 s13">{n}{" <span class=s12 style=color:#B42318>" + T("нет в наличии", "жок") + "</span>" if o else ""}</div><div class="b1">× {q}</div></div>' for n, q, o in items)
    ok = onb(f'<div class="toast" style="position:static;background:#DCFCE7;color:#0E7A3A">{T("В чек отправлено 4 позиции", "Чекке 4 позиция жөнөтүлдү")}</div>')
    return (top(T('Списки к школе', 'Мектепке тизмелер'), T('1 класс', '1-класс')) + f'<div style="{PAD}">{rows}{ok}</div>'
            + bottom(btn(T('В чек', 'Чекке'), 'amber', 'width:100%;padding:14px;font-size:16px')))


def t_pack(T):
    box = (f'<div class="card pad row" style="gap:14px"><div>{barcode(150, 54, 4)}<div class="s12 g" style="text-align:center;letter-spacing:2px;margin-top:2px">4 600555 300002</div></div><div class="f1"><div class="b1 s16">{T("Ручка синяя", "Көк калем")}</div><div class="s13 g">{T("Коробка", "Куту")} ×50</div></div></div>')
    ln = sw(f'<div class="card pad g s13" style="text-align:center;padding:16px">{T("Сканируйте коробку поставщика", "Жеткирүүчүнүн кутусун сканерлеңиз")}</div>',
            line(T('Ручка синяя', 'Көк калем'), f'50 {T("шт", "даана")}', '500', tag(T('коробка = 50 шт', 'куту = 50 даана'), 'g'), 'hl'))
    return top(T('Касса — NurCRM', 'Касса — NurCRM'), T('Скан коробки', 'Куту сканери')) + f'<div style="{PAD}">{box}{ln}</div>' + total(T('Итого', 'Жыйынтык'), '0', '500')


def t_quick(T):
    names = [(T('Ручка', 'Калем'), 'Р', 10), (T('Тетрадь', 'Дептер'), 'Т', 12), (T('Файл А4', 'Файл А4'), 'Ф', 3), (T('Скотч', 'Скотч'), 'С', 35), (T('Клей', 'Желим'), 'К', 40), (T('Ластик', 'Өчүргүч'), 'Л', 8)]
    names = [names[0], names[1], names[5]]
    grid = '<div class="grid" style="grid-template-columns:repeat(3,1fr)">' + ''.join(tile(n[1], COL[i], n[0], f'{n[2]} {T("сом", "сом")}', style=';outline:3px solid #FFB208') for i, n in enumerate(names[:3])) + '</div>'
    rec = sw(f'<div class="card pad g s13" style="text-align:center;padding:14px">{T("Чек пуст — ходовое одной кнопкой", "Чек бош — ыкчамы бир баскыч менен")}</div>',
             line(T('Ручка', 'Калем'), '1 × 10', '10') + line(T('Тетрадь', 'Дептер'), '1 × 12', '12') + line(T('Ластик', 'Өчүргүч'), '1 × 8', '8'))
    return top(T('Быстрые товары', 'Ыкчам товарлар')) + f'<div style="{PAD}">{grid}{rec}</div>' + total(T('Итого', 'Жыйынтык'), '0', '30')


def t_inv(T):
    rc = line(T('Тетрадь 12 л', 'Дептер 12 б'), '100 × 12', '1 200') + line(T('Ручка синяя', 'Көк калем'), '50 × 10', '500') + line(T('Бумага А4', 'А4 кагаз'), '5 × 350', '1 750')
    inv = (f'<div class="card pad" style="background:#fff"><div class="b1 s16" style="margin-bottom:4px">{T("Счёт на оплату", "Төлөм эсеби")} № 54</div><div class="s12 g" style="margin-bottom:8px">{T("Покупатель: школа № 5", "Сатып алуучу: № 5 мектеп")}</div>'
           + ''.join(f'<div class="row s13" style="padding:5px 0;border-bottom:1px solid #E5E7EB"><span class="f1 b2">{n}</span><span class="b1">{s}</span></div>' for n, s in ((T('Тетрадь 12 л', 'Дептер 12 б'), '1 200'), (T('Ручка синяя', 'Көк калем'), '500'), (T('Бумага А4', 'А4 кагаз'), '1 750')))
           + f'<div class="tot" style="margin-top:6px"><span>{T("Итого", "Жыйынтык")}</span><span class="s22">3 450</span></div></div>')
    return top(T('Касса — NurCRM', 'Касса — NurCRM')) + f'<div style="{PAD}">' + sw(rc, inv) + '</div>' + bottom(sw(btn(T('Счёт из чека', 'Чектен эсеп'), 'dark', 'width:100%;padding:14px;font-size:16px'), btn(T('Печать', 'Басып чыгаруу'), 'amber', 'width:100%;padding:14px;font-size:16px')))


# сфера -> {ключ сцены: функция}; ключи совпадают с build.py/texts.json (h = хук, s1..s4)
SPHERES = {
    'grocery': {'h': g_hook, 's1': g_exp, 's2': g_quick, 's3': g_label, 's4': g_minus},
    'autoparts': {'h': a_hook, 's1': a_analog, 's2': a_cond, 's3': a_level, 's4': a_order},
    'bazaar': {'h': b_hook, 's2': b_volume, 's3': b_cur, 's4': b_cargo},  # s1 (торг) — телефон, макеты Мини из ../../src/screens
    'clothing': {'h': c_hook, 's1': c_bar, 's2': c_exch, 's3': c_cons},
    'services': {'h': s_hook, 's1': s_status, 's2': s_rent, 's3': s_journal},
    'hardware': {'h': h_hook, 's1': h_pack, 's2': h_price, 's3': h_calc, 's4': h_est},
    'electronics': {'h': e_hook, 's1': e_war, 's2': e_search, 's3': s_hook, 's4': e_trade},
    'stationery': {'h': t_hook, 's1': t_pack, 's2': t_quick, 's3': t_inv},
}
# электроника: сцена s3 — ремонт (тот же экран, что у услуг), но подписи те же — ок


def render_sphere(code, langs=('ru', 'ky')):
    d = OUT / code
    d.mkdir(parents=True, exist_ok=True)
    tmpd = d / '_html'
    tmpd.mkdir(exist_ok=True)
    scenes = SPHERES[code]
    for lang in langs:
        T = (lambda ru, ky: ru) if lang == 'ru' else (lambda ru, ky: ky)
        frames, pos = [], []
        for i, (key, fn) in enumerate(scenes.items()):
            for j, st in enumerate('ab'):
                x, y = j * (FW + 10), i * (FH + 10)
                pos.append((key, st, x, y))
                frames.append(f'<div style="position:absolute;left:{x}px;top:{y}px">{frame_html(fn(T), st)}</div>')
        w, h = 2 * FW + 10, len(scenes) * (FH + 10)
        html = tmpd / f'{lang}.html'
        html.write_text(f'<!doctype html><html><head><meta charset="utf-8"><style>{CSS}body{{width:{w}px;height:{h}px;position:relative;background:#fff}}</style></head><body>{"".join(frames)}</body></html>', encoding='utf-8')
        png = tmpd / f'{lang}.png'
        png.unlink(missing_ok=True)
        subprocess.run([CHROME, '--headless', '--disable-gpu', '--no-sandbox', '--user-data-dir=' + tempfile.mkdtemp(), '--hide-scrollbars', f'--force-device-scale-factor={SCALE}',
                        f'--window-size={w},{h}', f'--screenshot={png}', '--virtual-time-budget=3000', html.as_uri()], check=True, capture_output=True)
        for _ in range(100):
            if png.exists() and png.stat().st_size > 5000:
                time.sleep(0.5)
                break
            time.sleep(0.2)
        im = Image.open(png).convert('RGB')
        W2, H2 = round(w * SCALE), round(h * SCALE)
        if im.size != (W2, H2):
            im = im.resize((W2, H2), Image.LANCZOS)
        for key, st, x, y in pos:
            im.crop((round(x * SCALE), round(y * SCALE), round(x * SCALE) + 960, round(y * SCALE) + round(FH * SCALE))).save(d / f'{lang}_{key}_{st}.png')
        print('ok', code, lang, len(pos), 'кадров', flush=True)


def main():
    codes = sys.argv[1:] or list(SPHERES)
    for c in codes:
        render_sphere(c)


if __name__ == '__main__':
    main()
