#!/usr/bin/env python3
# 2026-11-27 (просьба владельца «И в мобилку тоже Маркетплейс добавь!»): тексты Маркетплейса мобилки берутся из кассы, а не пишутся второй раз.
# Скрипт читает C#-файлы кассы (Services/MarketplaceModuleContent*.cs, MarketplaceContent.{Estimate,Rental,ServiceDesk}.cs, MarketplaceDemos*.cs,
# коды модулей) и собирает assets/marketplace/content.json: для каждого модуля / пакета — значок, цвет, строка «что делает», «что делает» подробно,
# «как включить», «для кого», шаги демонстрации «как работает» и «что получите» — всё на 5 языках (ru, ky, en, tr, uz).
# Перезапускать при каждой правке текстов кассы:  python tool/gen_marketplace_content.py  (из папки NurMarketMobile; C# не меняет, только читает).
# Тексты кассы остаются единственным источником; ручные добавки мобилки (названия сфер/групп) — в блоке EXTRA ниже.
import io
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..'))
SVC = os.path.join(REPO, 'NurMarketKassa.Avalonia', 'NurMarketKassa.Avalonia', 'Services')
INFRA = os.path.join(REPO, 'NurMarketKassa.Avalonia', 'NurMarketKassa.Infrastructure', 'Services')
OUT = os.path.join(HERE, '..', 'assets', 'marketplace', 'content.json')
LANGS = ['ru', 'ky', 'en', 'tr', 'uz']  # порядок аргументов Tr.T(ru, ky, en, tr, uz)

# ───────────────────────── ручные добавки мобилки (не из C#) ─────────────────────────
# Названия групп модулей = сферы сервера (поле group у public/modules). Источник — названия сфер кассы (profiles[].name сервера дают то же на ru/en).
EXTRA = {
    'groups': {
        'grocery': dict(ru='Продукты', ky='Азык-түлүк', en='Grocery', tr='Market', uz='Oziq-ovqat'),
        'hozmag': dict(ru='Хозтовары', ky='Чарба товарлары', en='Household goods', tr='Hırdavat', uz="Xo'jalik tovarlari"),
        'stroymag': dict(ru='Стройматериалы', ky='Курулуш материалдары', en='Building materials', tr='Yapı malzemeleri', uz='Qurilish materiallari'),
        'clothing': dict(ru='Одежда и обувь', ky='Кийим жана бут кийим', en='Clothing and footwear', tr='Giyim ve ayakkabı', uz='Kiyim va poyabzal'),
        'electronics': dict(ru='Электроника', ky='Электроника', en='Electronics', tr='Elektronik', uz='Elektronika'),
        'autoparts': dict(ru='Автозапчасти', ky='Автозапчасттар', en='Auto parts', tr='Oto yedek parça', uz='Avtoehtiyot qismlar'),
        'stationery': dict(ru='Канцтовары', ky='Кеңсе товарлары', en='Stationery', tr='Kırtasiye', uz='Kantstovarlar'),
        'bazaar': dict(ru='Базар и контейнеры', ky='Базар жана контейнерлер', en='Bazaar and containers', tr='Pazar ve konteynerler', uz='Bozor va konteynerlar'),
        'warehouse': dict(ru='Склад', ky='Кампа', en='Warehouse', tr='Depo', uz='Ombor'),
        'extra': dict(ru='Другие функции', ky='Башка функциялар', en='Other features', tr='Diğer özellikler', uz='Boshqa funksiyalar'),
        'other': dict(ru='Другое', ky='Башка', en='Other', tr='Diğer', uz='Boshqa'),
    },
}


# ───────────────────────── токенайзер C# ─────────────────────────
class Tok:
    __slots__ = ('k', 'v')

    def __init__(self, k, v):
        self.k, self.v = k, v

    def __repr__(self):
        return f'{self.k}:{self.v!r}'


ESC = {'n': '\n', 't': '\t', 'r': '\r', '"': '"', '\\': '\\', "'": "'", '0': '\0'}


def tokenize(src):
    src = src.lstrip('﻿')
    i, n, out = 0, len(src), []
    while i < n:
        c = src[i]
        if c.isspace():
            i += 1
        elif src.startswith('//', i):
            while i < n and src[i] != '\n':
                i += 1
        elif src.startswith('/*', i):
            j = src.find('*/', i + 2)
            i = n if j < 0 else j + 2
        elif c == '"' or (c == '$' and src.startswith('$"', i)) or (c == '@' and src.startswith('@"', i)) or src.startswith('$@"', i) or src.startswith('@$"', i):
            interp = c == '$' or src.startswith('@$"', i)
            verbatim = c == '@' or src.startswith('$@"', i)
            while src[i] != '"':
                i += 1
            i += 1
            buf = []
            while i < n:
                ch = src[i]
                if verbatim:
                    if ch == '"':
                        if src.startswith('""', i):
                            buf.append('"')
                            i += 2
                            continue
                        break
                    buf.append(ch)
                    i += 1
                    continue
                if ch == '\\':
                    nx = src[i + 1]
                    if nx == 'u':
                        buf.append(chr(int(src[i + 2:i + 6], 16)))
                        i += 6
                    elif nx == 'x':
                        m = re.match(r'[0-9a-fA-F]{1,4}', src[i + 2:])
                        buf.append(chr(int(m.group(0), 16)))
                        i += 2 + len(m.group(0))
                    else:
                        buf.append(ESC.get(nx, nx))
                        i += 2
                    continue
                if ch == '"':
                    break
                buf.append(ch)
                i += 1
            i += 1
            text = ''.join(buf)
            if interp and '{' in text.replace('{{', ''):
                out.append(Tok('istr', text))
            else:
                if interp:
                    text = text.replace('{{', '{').replace('}}', '}')
                out.append(Tok('str', text))
        elif c.isalpha() or c == '_' or c == '@':
            j = i + 1
            while j < n and (src[j].isalnum() or src[j] in '_.'):
                j += 1
            ident = src[i:j]
            while ident.endswith('.'):
                ident = ident[:-1]
                j -= 1
            out.append(Tok('id', ident))
            i = j
        elif c.isdigit():
            j = i + 1
            while j < n and (src[j].isalnum() or src[j] in '.'):
                j += 1
            out.append(Tok('num', src[i:j]))
            i = j
        elif src.startswith('=>', i):
            out.append(Tok('op', '=>'))
            i += 2
        elif c == "'":
            j = src.find("'", i + 1)
            if src[i + 1] == '\\':
                j = src.find("'", i + 3)
            out.append(Tok('str', src[i + 1:j]))
            i = j + 1
        else:
            out.append(Tok('op', c))
            i += 1
    return out


# ───────────────────────── разбор выражений ─────────────────────────
class P:
    def __init__(self, toks, pos=0):
        self.t, self.i = toks, pos

    def peek(self, k=0):
        j = self.i + k
        return self.t[j] if j < len(self.t) else Tok('eof', '')

    def eat(self, v=None):
        tk = self.peek()
        if v is not None and tk.v != v:
            raise SyntaxError(f'ожидалось {v!r}, а тут {tk!r} (поз. {self.i})')
        self.i += 1
        return tk

    def is_op(self, v, k=0):
        tk = self.peek(k)
        return tk.k == 'op' and tk.v == v

    def expr(self):
        left = self.primary()
        while self.is_op('+'):
            self.eat()
            right = self.primary()
            left = ('concat', left, right)
        return left

    def args(self, close):
        res = []
        while not self.is_op(close):
            if self.peek().k == 'id' and self.is_op(':', 1) and not self.is_op(':', 2):
                name = self.eat().v
                self.eat(':')
                res.append(('named', name, self.expr()))
            else:
                res.append(self.expr())
            if self.is_op(','):
                self.eat()
        self.eat(close)
        return res

    def primary(self):
        tk = self.peek()
        if tk.k == 'str':
            self.eat()
            return ('str', tk.v)
        if tk.k == 'istr':
            self.eat()
            return ('istr', tk.v)
        if tk.k == 'num':
            self.eat()
            return ('num', tk.v)
        if tk.k == 'op' and tk.v == '-' and self.peek(1).k == 'num':
            self.eat()
            return ('num', '-' + self.eat().v)
        if tk.k == 'op' and tk.v == '[':
            self.eat()
            return ('list', self.args(']'))
        if tk.k == 'op' and tk.v == '(':
            # лямбда () => expr, кортеж (a, b) или скобки
            if self.is_op(')', 1) and self.is_op('=>', 2):
                self.i += 3
                return ('lambda', self.expr())
            self.eat()
            items = self.args(')')
            return ('tuple', items)
        if tk.k == 'id':
            self.eat()
            name = tk.v
            if name == 'new':
                if self.is_op('('):
                    self.eat()
                    return ('call', 'new', self.args(')'))
                if self.peek().k == 'id':
                    self.eat()
                    if self.is_op('('):
                        self.eat()
                        return ('call', 'new', self.args(')'))
                return ('unknown', 'new')
            if self.is_op('('):
                self.eat()
                return ('call', name, self.args(')'))
            return ('id', name)
        raise SyntaxError(f'не разобрать токен {tk!r} (поз. {self.i})')


# ───────────────────────── вычисление ─────────────────────────
class Ctx:
    def __init__(self):
        self.consts = {}     # ModuleIds.Estimate -> 'estimate'
        self.helpers = {}    # Grocery -> expr
        self.warn = []

    def key(self, name):
        # ModuleIds.X / BazaarModules.X / голое X
        if name in self.consts:
            return self.consts[name]
        short = name.split('.')[-1]
        return self.consts.get(short, None)


def is_text(v):
    return isinstance(v, dict) and set(v.keys()) <= set(LANGS) and 'ru' in v


def ev(node, cx):
    k = node[0]
    if k == 'str':
        return node[1]
    if k == 'istr':
        cx.warn.append('интерполяция: ' + node[1][:60])
        return re.sub(r'\{[^}]*\}', '', node[1])
    if k == 'num':
        return node[1]
    if k == 'lambda':
        return ev(node[1], cx)
    if k == 'concat':
        a, b = ev(node[1], cx), ev(node[2], cx)
        if isinstance(a, str) and isinstance(b, str):
            return a + b
        if is_text(a) and is_text(b):
            return {l: a[l] + b[l] for l in LANGS}
        if is_text(a) and isinstance(b, str):
            return {l: a[l] + b for l in LANGS}
        if isinstance(a, str) and is_text(b):
            return {l: a + b[l] for l in LANGS}
        cx.warn.append('concat: ' + repr(a)[:80] + ' + ' + repr(b)[:80])
        return a
    if k == 'list':
        out = []
        for a in node[1]:
            out.append(ev(a, cx))
        return out
    if k == 'tuple':
        return ('tuple', [ev(a, cx) for a in node[1]])
    if k == 'id':
        name = node[1]
        if name in ('true', 'false'):
            return name == 'true'
        if name == 'null':
            return None
        if name.startswith('MockTone.'):
            return name.split('.')[1].lower()
        return ('id', name)
    if k == 'call':
        name, args = node[1], node[2]
        if name in ('T', 'Tr.T'):
            vals = [ev(a, cx) for a in args]
            if len(vals) == 5 and all(isinstance(v, str) for v in vals):
                return dict(zip(LANGS, vals))
            cx.warn.append('T() не из 5 строк')
            return {l: str(vals[0]) for l in LANGS}
        if not args and name in cx.helpers:
            return ev(cx.helpers[name], cx)
        return ('call', name, [(a[2] if a[0] == 'named' else a, ) for a in args], args)
    return ('unknown', str(node))


def pos_args(args, cx):
    """Позиционные значения + именованные: (list, dict)."""
    pos, named = [], {}
    for a in args:
        if a[0] == 'named':
            named[a[1]] = ev(a[2], cx)
        else:
            pos.append(ev(a, cx))
    return pos, named


# ───────────────────────── сбор данных ─────────────────────────
def read(path):
    with io.open(path, encoding='utf-8-sig') as f:
        return f.read()


def collect_consts(cx):
    for p in [os.path.join(INFRA, 'Modules', 'CompanyModules.cs'), os.path.join(INFRA, 'Spheres', 'BazaarModules.cs'),
              os.path.join(INFRA, 'Api', 'ServerCapabilities.cs'), os.path.join(SVC, 'MarketplaceFeatureCards.cs')]:
        src = read(p)
        cls = 'ModuleIds' if 'class ModuleIds' in src else ('BazaarModules' if 'class BazaarModules' in src else ('ServerCapabilities' if p.endswith('ServerCapabilities.cs') else 'MarketplaceFeatureCards'))
        for m in re.finditer(r'public const string ([^;]+);', src):
            for part in m.group(1).split(','):
                mm = re.match(r'\s*(\w+)\s*=\s*"([^"]+)"', part.strip() if '=' in part else part)
                if mm:
                    cx.consts[mm.group(1)] = mm.group(2)
                    cx.consts[cls + '.' + mm.group(1)] = mm.group(2)


def collect_helpers(cx, toks):
    # private static string Name() => expr;
    for i in range(len(toks) - 5):
        if toks[i].v == 'static' and toks[i + 1].v == 'string' and toks[i + 2].k == 'id' and toks[i + 3].v == '(' and toks[i + 4].v == ')' and toks[i + 5].v == '=>':
            try:
                p = P(toks, i + 6)
                cx.helpers[toks[i + 2].v] = p.expr()
            except SyntaxError:
                pass


def parse_entries(toks, cx):
    """Все `[Key] = expr` и `x["key"] = expr` из потока токенов."""
    res = []
    n = len(toks)
    for i in range(n - 4):
        a, b, c, d, e = toks[i:i + 5]
        if a.v == '[' and a.k == 'op' and b.k == 'id' and c.v == ']' and d.v == '=' and d.k == 'op':
            key = cx.key(b.v)
            start = i + 4
        elif a.k == 'id' and b.v == '[' and c.k == 'str' and d.v == ']' and e.v == '=' and e.k == 'op':
            key = c.v
            start = i + 5
        else:
            continue
        if key is None:
            if b.k == 'id' and '.' in b.v and toks[i + 4].v != '=':
                cx.warn.append(f'код {b.v} не найден среди констант (добавьте файл в collect_consts) — запись пропущена')
            continue
        try:
            p = P(toks, start)
            res.append((key, p.expr()))
        except SyntaxError as ex:
            cx.warn.append(f'{key}: не разобрано ({ex})')
    return res


def color_of(v):
    return v if isinstance(v, str) and v.startswith('#') else ''


def build():
    cx = Ctx()
    collect_consts(cx)
    modules, packs = {}, {}

    def mod(key):
        if key.startswith('pack:'):
            return packs.setdefault(key[5:], {})
        return modules.setdefault(key, {})

    # 1. MarketplaceModuleContent.cs / .Lines.cs
    mc_files = sorted(f for f in os.listdir(SVC) if re.match(r'MarketplaceModuleContent(\..*)?\.cs$', f))  # + .Lines, .Features и будущие части
    mc_toks = [tokenize(read(os.path.join(SVC, f))) for f in mc_files]
    for t_ in mc_toks:
        collect_helpers(cx, t_)
    entries = []
    for t_ in mc_toks:
        entries += parse_entries(t_, cx)
    for key, node in entries:
        m = mod(key)
        if node[0] == 'lambda':  # Lines: [id] = () => T(...)
            v = ev(node, cx)
            if is_text(v):
                m['line'] = v
        elif node[0] == 'tuple':  # OwnCardVisual: ("icon", "#color")
            v = ev(node, cx)[1]
            if len(v) == 2 and all(isinstance(x, str) for x in v):
                m['icon'], m['color'] = v
        elif node[0] == 'call' and node[1] == 'new':  # Specs: new("icon", "#color", () => new(Short, What, [steps], [audience]))
            args = node[2]
            if len(args) == 3 and args[2][0] == 'lambda':
                m['icon'] = ev(args[0], cx)
                m['color'] = color_of(ev(args[1], cx))
                inner = args[2][1]
                if inner[0] == 'call' and inner[1] == 'new':
                    vals = [ev(a, cx) for a in inner[2]]
                    if len(vals) == 4:
                        m['short'], m['what'], m['steps'], m['audience'] = vals

    # 2. Свои карточки (Смета, Прокат, Ремонт, Trade-in): i.Tagline / HowTo / Audience / Benefits / IconKey
    own = {'Estimate': 'estimate', 'Rental': 'rental', 'Repair': 'repair', 'TradeIn': 'trade_in'}
    for fname in ['MarketplaceContent.Estimate.cs', 'MarketplaceContent.Rental.cs', 'MarketplaceContent.ServiceDesk.cs']:
        toks = tokenize(read(os.path.join(SVC, fname)))
        cur = None
        for i in range(len(toks) - 3):
            if toks[i].v == 'static' and toks[i + 1].v == 'void' and toks[i + 2].v in own:
                cur = own[toks[i + 2].v]
            if cur and toks[i].k == 'id' and toks[i].v in ('i.IconKey', 'i.HowTo', 'i.Audience', 'i.Benefits', 'i.KeyFacts', 'i.Tagline'):
                if toks[i + 1].v != '=':
                    continue
                prop = toks[i].v[2:]
                try:
                    node = P(toks, i + 2).expr()
                except SyntaxError as ex:
                    cx.warn.append(f'{cur}.{prop}: {ex}')
                    continue
                m = modules.setdefault(cur, {})
                if prop == 'IconKey':
                    m['icon'] = ev(node, cx)
                elif prop == 'Tagline':
                    m['short'] = ev(node, cx)
                elif prop == 'HowTo':
                    m['steps'] = ev(node, cx)
                elif prop == 'Audience':
                    m['audience'] = ev(node, cx)
                elif prop == 'KeyFacts':
                    m['facts'] = ev(node, cx)
                elif prop == 'Benefits':
                    items = []
                    for b in node[1]:
                        if b[0] == 'call' and b[1] == 'B':
                            icon, title, text = [ev(a, cx) for a in b[2]]
                            items.append({'icon': icon, 'title': title, 'text': text})
                    m['benefits'] = items

    # 3. Демонстрации «как работает» и «что получите»
    demo_files = [f for f in sorted(os.listdir(SVC)) if re.match(r'MarketplaceDemos\..*\.cs$', f)]
    demo_toks = {f: tokenize(read(os.path.join(SVC, f))) for f in demo_files}
    for f in demo_files:  # общие помощники («сом», «кг» и т.п.) лежат в любом из файлов
        collect_helpers(cx, demo_toks[f])
    for fname in demo_files:
        toks = demo_toks[fname]
        for key, node in parse_entries(toks, cx):
            if not (node[0] == 'call' and node[1] == 'new'):
                continue
            args = node[2]
            m = mod(key)
            if args:
                m['demo'] = demo_steps(args[0], cx)
            if len(args) > 1 and args[1][0] != 'named':
                m['gains'] = gains(args[1], cx)
            for a in args[2:]:
                if a[0] == 'named' and a[1] == 'What':
                    m['what_demo'] = ev(a[2], cx)
                if a[0] == 'named' and a[1] == 'Audience':
                    m['audience_demo'] = ev(a[2], cx)
                if a[0] != 'named':
                    pass
            # What / Audience в записи пакета идут позиционно (3-й и 4-й)
            if len(args) > 2 and args[2][0] != 'named':
                v = ev(args[2], cx)
                if is_text(v):
                    m['what_demo'] = v
            if len(args) > 3 and args[3][0] != 'named':
                v = ev(args[3], cx)
                if isinstance(v, list):
                    m['audience_demo'] = v

    features = parse_feature_cards(cx)
    for key in features:
        modules.setdefault(key, {})
    extra = load_extra()
    for key, add in extra.items():
        m = modules.setdefault(key, {})
        for f, v in add.items():
            m.setdefault(f, v)  # запись кассы главнее
    return cx, modules, packs, features


def parse_feature_cards(cx):
    """Карточки функций, которых сервер как модулей не отдаёт (MarketplaceFeatureCards.Defs): режим (capability / fiscal / builtin / service), признак сервера, группа, название, «что нужно»."""
    path = os.path.join(SVC, 'MarketplaceFeatureCards.cs')
    if not os.path.exists(path):
        return {}
    toks = tokenize(read(path))
    collect_helpers(cx, toks)
    out = {}
    for i in range(len(toks) - 3):
        if toks[i].v == 'Defs' and toks[i + 1].v == '=' and toks[i + 2].v == '[':
            try:
                node = P(toks, i + 2).expr()
            except SyntaxError as ex:
                cx.warn.append(f'Defs не разобран: {ex}')
                break
            for d in node[1]:
                if not (d[0] == 'call' and d[1] == 'new') or len(d[2]) < 6:
                    continue
                a = d[2]
                id_ = cx.key(a[0][1]) if a[0][0] == 'id' else None
                if id_ is None:
                    cx.warn.append('карточка без кода: ' + str(a[0])[:60])
                    continue
                mode = a[1][1].split('.')[-1].lower() if a[1][0] == 'id' else ''
                flag = cx.key(a[2][1]) if a[2][0] == 'id' and a[2][1] != 'null' else None
                group = a[3][1].split('.')[-1].lower() if a[3][0] == 'id' else ''
                name, req = ev(a[4], cx), ev(a[5], cx)
                out[id_] = {'mode': mode, 'flag': flag, 'group': group, 'name': name if is_text(name) else None, 'need': req if is_text(req) else None}
            break
    return out


def load_extra_module():
    import importlib.util
    spec = importlib.util.spec_from_file_location('gen_marketplace_extra', os.path.join(HERE, 'gen_marketplace_extra.py'))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def load_extra():
    return load_extra_module().MODULES


def gains(node, cx):
    out = []
    seq = node[1] if node[0] == 'lambda' else node
    if seq[0] != 'list':
        return out
    for g in seq[1]:
        if g[0] == 'call' and g[1] in ('Gain', 'B'):
            icon, title, text = [ev(a, cx) for a in g[2]]
            out.append({'icon': icon, 'title': title, 'text': text})
    return out


def demo_steps(node, cx):
    seq = node[1] if node[0] == 'lambda' else node
    out = []
    if seq[0] != 'list':
        return out
    for st in seq[1]:
        if not (st[0] == 'call' and st[1] == 'Step'):
            cx.warn.append('не Step: ' + str(st[:2]))
            continue
        a = st[2]
        caption, title = ev(a[0], cx), ev(a[1], cx)
        parts = []
        for pn in a[2:]:
            if pn[0] != 'call':
                continue
            nm = pn[1]
            pos, named = pos_args(pn[2], cx)
            g = lambda i, name, d=None: pos[i] if len(pos) > i else named.get(name, d)
            if nm == 'Input':
                parts.append({'t': 'input', 'label': g(0, 'label'), 'value': g(1, 'value'), 'hot': bool(g(2, 'hot', False))})
            elif nm == 'Row':
                parts.append({'t': 'row', 'text': g(0, 'text'), 'right': g(1, 'right', ''), 'tone': g(2, 'tone', 'normal'), 'hot': bool(g(3, 'hot', False)), 'sub': g(4, 'sub', '')})
            elif nm == 'Btn':
                parts.append({'t': 'btn', 'text': g(0, 'text'), 'hot': bool(g(1, 'hot', False)), 'primary': bool(g(2, 'primary', True))})
            elif nm == 'Note':
                parts.append({'t': 'note', 'text': g(0, 'text'), 'tone': g(1, 'tone', 'normal')})
            elif nm == 'Toggle':
                parts.append({'t': 'toggle', 'label': g(0, 'label'), 'on': bool(g(1, 'on', False)), 'hot': bool(g(2, 'hot', False))})
            elif nm == 'Total':
                parts.append({'t': 'total', 'label': g(0, 'label'), 'value': g(1, 'value'), 'hot': bool(g(2, 'hot', False))})
            elif nm == 'Chips':
                parts.append({'t': 'chips', 'selected': int(pos[0]), 'items': pos[1:]})
            elif nm == 'Tiles':
                parts.append({'t': 'tiles', 'hot': int(pos[0]), 'items': pos[1:]})
            else:
                cx.warn.append('неизвестная часть кадра: ' + nm)
        out.append({'caption': caption, 'title': title, 'parts': parts})
    return out


def check(modules, packs, cx):
    problems = []

    def bad_text(v, where):
        if isinstance(v, dict):
            for l in LANGS:
                if not isinstance(v.get(l), str) or not v[l].strip():
                    problems.append(f'{where}: пустой язык {l}')
        elif isinstance(v, list):
            for j, x in enumerate(v):
                bad_text(x, f'{where}[{j}]')

    for kind, coll in (('module', modules), ('pack', packs)):
        for key, m in coll.items():
            for f in ('name', 'line', 'short', 'what', 'steps', 'audience'):
                if f in m:
                    bad_text(m[f], f'{kind} {key}.{f}')
            for st in m.get('demo', []):
                bad_text(st['caption'], f'{kind} {key}.demo.caption')
                bad_text(st['title'], f'{kind} {key}.demo.title')
    return problems


def normalize(m):
    """Чистим внутренние ключи: «что делает» из демо — запасное для пакетов; значки как есть."""
    out = {}
    for k in ('name', 'icon', 'color', 'line', 'short', 'what', 'benefits', 'steps', 'audience', 'facts', 'demo', 'gains'):
        if k in m:
            out[k] = m[k]
    if 'what' not in out and 'what_demo' in m:
        out['what'] = m['what_demo']
    if 'audience' not in out and 'audience_demo' in m:
        out['audience'] = m['audience_demo']
    return out


def main():
    cx, modules, packs, features = build()
    for key, nm in load_extra_module().NAMES.items():
        modules.setdefault(key, {})['name'] = nm
    for key, f in features.items():  # название карточки функции — из её описания в кассе
        if f.get('name'):
            modules.setdefault(key, {}).setdefault('name', f['name'])
    problems = check(modules, packs, cx)
    data = {
        'schema': 1,
        'langs': LANGS,
        'groups': EXTRA['groups'],
        'modules': {k: normalize(v) for k, v in sorted(modules.items())},
        'features': features,
        'packs': {k: normalize(v) for k, v in sorted(packs.items())},
    }
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with io.open(OUT, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(data, f, ensure_ascii=False, separators=(',', ':'), sort_keys=False)
    icons = set()
    for coll in (data['modules'], data['packs']):
        for m in coll.values():
            if m.get('icon'):
                icons.add(m['icon'])
            for g in m.get('gains', []) + m.get('benefits', []):
                if g.get('icon'):
                    icons.add(g['icon'])
    print('модулей:', len(data['modules']), 'пакетов:', len(data['packs']), 'размер:', os.path.getsize(OUT) // 1024, 'КБ')
    print('значки:', ' '.join(sorted(icons)))
    for key, m in sorted(data['modules'].items()):
        need = [f for f in ('icon', 'color', 'line', 'short', 'what', 'steps', 'audience', 'demo') if f not in m and not (f == 'what' and 'benefits' in m)]
        if need:
            print('  нет полей у', key, ':', ','.join(need))
    for w in cx.warn[:20]:
        print('  предупреждение:', w)
    for p in problems:
        print('  ПРОБЛЕМА:', p)
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
