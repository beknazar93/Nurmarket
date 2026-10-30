/* NurCRM Kassa — шаблон «Большие объёмы»: фейковые данные, умный поиск (ё=е, раскладка, транслит, слова в любом порядке),
   виртуальный список (рисуем только видимые строки). 30.10.2026. Без зависимостей. */
window.LL = (function () {
  'use strict';
  const CATS = ['Сигареты', 'Напитки', 'Молочные', 'Бакалея', 'Бытовая химия', 'Кондитерские', 'Хлеб', 'Снеки', 'Детское', 'Заморозка'];
  const SUPS = ['ООО Нур-Опт', 'ИП Асанов', 'Coca-Cola Бишкек', 'Шоро', 'Куликовский', 'ОсОО Эмир'];
  const BASE = [
    ['Winston Blue', 'Сигареты', 145], ['Parliament Aqua', 'Сигареты', 230], ['LD Red', 'Сигареты', 105], ['Kent Nano', 'Сигареты', 210],
    ['Coca-Cola 1 л', 'Напитки', 78], ['Fanta 0,5 л', 'Напитки', 45], ['Адыгене 1,5 л', 'Напитки', 30], ['Шоро Максым 1 л', 'Напитки', 70], ['Легенда 0,5 л', 'Напитки', 28],
    ['Молоко Riha 2,5 % 1 л', 'Молочные', 92], ['Кефир Эмир 1 л', 'Молочные', 85], ['Сметана Умут 20 % 400 г', 'Молочные', 120], ['Айран Шоро 1 л', 'Молочные', 65], ['Творог 9 % 500 г', 'Молочные', 180],
    ['Мука Казахстанская 2 кг', 'Бакалея', 130], ['Рис Краснодарский 1 кг', 'Бакалея', 115], ['Сахар 1 кг', 'Бакалея', 98], ['Макароны Макфа 400 г', 'Бакалея', 68], ['Масло Олейна 1 л', 'Бакалея', 215], ['Гречка 800 г', 'Бакалея', 110], ['Соль Экстра 1 кг', 'Бакалея', 22],
    ['Ariel 3 кг', 'Бытовая химия', 1150], ['Fairy 450 мл', 'Бытовая химия', 165], ['Domestos 1 л', 'Бытовая химия', 240], ['Зубная паста Colgate', 'Бытовая химия', 135], ['Шампунь Head&Shoulders 400 мл', 'Бытовая химия', 420],
    ['Snickers 50 г', 'Кондитерские', 55], ['Куликовский торт Наполеон', 'Кондитерские', 980], ['Печенье Юбилейное', 'Кондитерские', 72], ['Alpen Gold 90 г', 'Кондитерские', 95], ['Боорсок 300 г', 'Кондитерские', 60],
    ['Лепёшка тандырная', 'Хлеб', 25], ['Хлеб Бородинский', 'Хлеб', 35], ['Батон нарезной', 'Хлеб', 30],
    ['Lays 150 г', 'Снеки', 120], ['Семечки Джинн 200 г', 'Снеки', 60], ['Курут 100 г', 'Снеки', 40], ['Сухарики Кириешки', 'Снеки', 25],
    ['Подгузники Pampers 4 (52 шт)', 'Детское', 1890], ['Пюре Агуша 100 г', 'Детское', 65], ['Смесь Nutrilon 1 (800 г)', 'Детское', 2450],
    ['Пельмени Сибирские 900 г', 'Заморозка', 310], ['Мороженое Пломбир 100 г', 'Заморозка', 45], ['Манты замороженные 1 кг', 'Заморозка', 380]
  ];
  const VAR = ['', ' (блок 10)', ' акция', ' new', ' 2 шт', ' мини', ' XL', ' без сахара', ' семейная'];
  function rng(seed) { return function () { seed |= 0; seed = seed + 0x6D2B79F5 | 0; let t = Math.imul(seed ^ seed >>> 15, 1 | seed); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; }; }

  function makeItems(n, seed) {
    const r = rng(seed || 7); const out = [];
    for (let i = 0; i < n; i++) {
      const b = BASE[Math.floor(r() * BASE.length)];
      const v = i < BASE.length ? '' : VAR[Math.floor(r() * VAR.length)];
      const price = Math.round(b[2] * (0.85 + r() * 0.3) * 100) / 100;
      const ordered = 1 + Math.floor(r() * 60);
      const k = r();
      const qty = k < 0.07 ? Math.max(0, ordered - 1 - Math.floor(r() * 5)) : ordered;   // 7 % — расхождение
      const isNew = r() < 0.06;
      const err = r() < 0.03 ? (r() < 0.5 ? 'нет штрихкода' : 'цена ниже закупки') : '';
      const markup = Math.round(15 + r() * 30);
      const sell = err === 'цена ниже закупки' ? Math.round(price * 0.92 * 100) / 100 : Math.round(price * (1 + markup / 100) * 100) / 100;
      const unit = /кг$|г$/.test(b[0]) && r() < 0.15 ? 'кг' : 'шт';
      out.push({
        id: i + 1, name: b[0] + v, cat: b[1], sup: SUPS[Math.floor(r() * SUPS.length)],
        barcode: err === 'нет штрихкода' ? '' : '48' + String(7000000000 + Math.floor(r() * 999999999)).slice(0, 11),
        ordered, qty, unit, price, sell, markup: Math.round((sell / price - 1) * 100), sum: Math.round(qty * price * 100) / 100,
        isNew, err, diff: qty !== ordered, exp: price >= 1000
      });
    }
    return out;
  }

  // ---------- поиск ----------
  const EN = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`", RU = 'йцукенгшщзхъфывапролджэячсмитьбюё';
  const SWAP = {}; for (let i = 0; i < EN.length; i++) { SWAP[EN[i]] = RU[i]; SWAP[RU[i]] = EN[i]; }
  const TR = { a: 'а', b: 'б', c: 'ц', d: 'д', e: 'е', f: 'ф', g: 'г', h: 'х', i: 'и', j: 'ж', k: 'к', l: 'л', m: 'м', n: 'н', o: 'о', p: 'п', q: 'к', r: 'р', s: 'с', t: 'т', u: 'у', v: 'в', w: 'в', x: 'кс', y: 'й', z: 'з' };
  const TRD = { sh: 'ш', ch: 'ч', zh: 'ж', ya: 'я', yu: 'ю', yo: 'ё', kh: 'х', ts: 'ц', shch: 'щ' };
  const norm = s => String(s || '').toLowerCase().replace(/ё/g, 'е').replace(/[^\p{L}\p{N}\s,.]/gu, ' ').replace(/\s+/g, ' ').trim();
  const swap = s => s.split('').map(c => SWAP[c] || c).join('');
  function translit(s) { let o = s; for (const k in TRD) o = o.split(k).join(TRD[k]); return o.split('').map(c => TR[c] || c).join(''); }
  function variants(w) { const set = new Set([w, swap(w), translit(w), norm(translit(w))]); return [...set].filter(Boolean); }
  function makeMatcher(q) {
    const words = norm(q).split(' ').filter(Boolean);
    if (!words.length) return () => true;
    const vs = words.map(variants);
    return item => {
      const hay = item._hay || (item._hay = norm([item.name, item.barcode, item.cat, item.sup].join(' ')));
      return vs.every(v => v.some(x => hay.includes(x)));
    };
  }

  // ---------- форматы ----------
  const money = n => (Math.round(n * 100) / 100).toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).replace(/ /g, ' ');
  const int = n => Math.round(n).toLocaleString('ru-RU').replace(/ /g, ' ');
  const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  function debounce(fn, ms) { let t; return function () { clearTimeout(t); const a = arguments; t = setTimeout(() => fn.apply(null, a), ms); }; }

  // ---------- виртуальный список ----------
  // viewport: элемент с overflow:auto. Внутри — spacer (полная высота) и окно .vwin, которое сдвигается transform'ом.
  // items — плоский массив (строки и заголовки групп), heightOf(item) — высота строки, render(item, i) — HTML строки.
  function VList(viewport, opt) {
    const spacer = document.createElement('div'); spacer.className = 'vspacer';
    const win = document.createElement('div'); win.className = 'vwin';
    viewport.innerHTML = ''; viewport.appendChild(spacer); viewport.appendChild(win);
    let items = [], offs = [0], total = 0, raf = 0, lastKey = '';
    const overscan = opt.overscan || 6;
    function setItems(list) { items = list; offs = new Array(items.length + 1); offs[0] = 0; for (let i = 0; i < items.length; i++) offs[i + 1] = offs[i] + opt.heightOf(items[i]); total = offs[items.length]; spacer.style.height = total + 'px'; lastKey = ''; draw(); }
    function findIndex(y) { let lo = 0, hi = items.length - 1; while (lo < hi) { const m = (lo + hi + 1) >> 1; if (offs[m] <= y) lo = m; else hi = m - 1; } return lo; }
    function draw() {
      raf = 0; if (!items.length) { win.innerHTML = ''; opt.onRange && opt.onRange(0, 0, 0); return; }
      const top = viewport.scrollTop, h = viewport.clientHeight;
      let a = Math.max(0, findIndex(top) - overscan), b = Math.min(items.length, findIndex(top + h) + 1 + overscan);
      const key = a + ':' + b; if (key === lastKey && !opt.always) return; lastKey = key;
      const t0 = performance.now(); let html = '';
      for (let i = a; i < b; i++) html += opt.render(items[i], i);
      win.style.transform = 'translateY(' + offs[a] + 'px)'; win.innerHTML = html;
      opt.onRange && opt.onRange(findIndex(top), Math.min(items.length - 1, findIndex(top + h - 1)), performance.now() - t0, b - a);
    }
    viewport.addEventListener('scroll', () => { if (!raf) raf = requestAnimationFrame(draw); }, { passive: true });
    return {
      setItems, refresh() { lastKey = ''; draw(); }, get items() { return items; },
      scrollToIndex(i) { viewport.scrollTop = offs[Math.max(0, Math.min(i, items.length - 1))] || 0; },
      scrollTop() { viewport.scrollTop = 0; }, scrollEnd() { viewport.scrollTop = total; },
      page(dir) { viewport.scrollTop += dir * (viewport.clientHeight - 2 * opt.heightOf(items[0] || {})); },
      get total() { return total; }
    };
  }

  return { CATS, SUPS, makeItems, norm, makeMatcher, money, int, esc, debounce, VList };
})();
