/* 2026-10-29: движок сцены ролика «Свет погас». Всё — функции времени (без состояния): window.__render(t).
   Слои параллакса (скорость относительно камеры): дальний 0,4 — сцена 1,0 — ближний 2,5. Актёры собираются из SVG-ярусов
   design/Ad-Light/char-*.svg (data-pivot / transform), позы смешиваются интерполяцией углов. */
(function () {
'use strict';
const S = window.SCENE, TL = window.TIMELINE, P = S.params || {};
const W = S.W, H = S.H, PORTRAIT = H > W;
const LANG = (P.lang || S.lang) === 'ky' ? 'ky' : 'ru';
const VAR = PORTRAIT ? 'short' : 'full';
const V = TL[VAR];
const BASE = PORTRAIT ? W / 1080 : W / 1920;
const NS = 'http://www.w3.org/2000/svg';
const L = (ru, ky) => (LANG === 'ky' ? ky : ru);
const VM = Math.min(W, H) / 100;   // 1 vmin в пикселях

/* ---------------------------------------------------------------- утилиты */
const cl = (x, a = 0, b = 1) => Math.min(b, Math.max(a, x));
const lerp = (a, b, k) => a + (b - a) * k;
const E = {
  lin: u => u, sm: u => u * u * (3 - 2 * u), inS: u => 1 - Math.cos(u * Math.PI / 2), outS: u => Math.sin(u * Math.PI / 2),
  inOutS: u => -(Math.cos(Math.PI * u) - 1) / 2, out3: u => 1 - Math.pow(1 - u, 3), in3: u => u * u * u,
  inOut3: u => (u < .5 ? 4 * u * u * u : 1 - Math.pow(-2 * u + 2, 3) / 2),
  back: u => { const c1 = 1.70158, c3 = c1 + 1; return 1 + c3 * Math.pow(u - 1, 3) + c1 * Math.pow(u - 1, 2); },
};
const seg = (t, t0, d, f) => (f || E.inOutS)(cl((t - t0) / d));
const pulse = (t, t0, d, fi = .25, fo = .25) => Math.min(seg(t, t0, fi, E.out3), 1 - seg(t, t0 + d - fo, fo, E.in3));
function mk(tag, attrs, parent) {
  const e = document.createElementNS(NS, tag);
  if (attrs) for (const k in attrs) e.setAttribute(k, attrs[k]);
  if (parent) parent.appendChild(e);
  return e;
}
function div(cls, parent, html) {
  const e = document.createElement('div');
  if (cls) e.className = cls;
  if (html != null) e.innerHTML = html;
  (parent || document.getElementById('ui')).appendChild(e);
  return e;
}
const hash = n => { const x = Math.sin(n * 127.1 + 311.7) * 43758.5453; return x - Math.floor(x); };
// ключевые кадры: keys = [[t, v1, v2, ..., ease?]] ; ease на «конечном» ключе сегмента
function kf(t, keys) {
  if (t <= keys[0][0]) return keys[0].slice(1).filter(x => typeof x === 'number');
  for (let i = 1; i < keys.length; i++) {
    if (t <= keys[i][0]) {
      const a = keys[i - 1], b = keys[i], last = b[b.length - 1], f = typeof last === 'function' ? last : E.inOutS;
      const u = f(cl((t - a[0]) / Math.max(1e-6, b[0] - a[0])));
      const out = [];
      for (let j = 1; j < b.length; j++) if (typeof b[j] === 'number') out.push(lerp(a[j], b[j], u));
      return out;
    }
  }
  return keys[keys.length - 1].slice(1).filter(x => typeof x === 'number');
}

const svg = document.getElementById('sv');
svg.setAttribute('width', W); svg.setAttribute('height', H); svg.setAttribute('viewBox', '0 0 ' + W + ' ' + H);
const UI = document.getElementById('ui');
const defs = mk('defs', null, svg);

/* ---------------------------------------------------------------- слои */
const gInt = mk('g', null, svg);          // интерьер
const gFar = mk('g', null, gInt), gMidSh = mk('g', null, gInt), gFloor = mk('g', null, gInt), gLamp = mk('g', null, gInt);
const gBehind = mk('g', null, gInt), gCounter = mk('g', null, gInt), gFront = mk('g', null, gInt), gNear = mk('g', null, gInt);
const gExt = mk('g', null, svg);          // улица (К1)
const gFX = mk('g', null, svg);           // свет, тьма, глаза (в координатах экрана)

function setLayer(g, f, cam) {
  const s = BASE * cam.z, cxf = cam.cx * f + 960 * (1 - f);
  g.setAttribute('transform', 'translate(' + (W / 2 - s * cxf).toFixed(2) + ' ' + (H / 2 - s * cam.cy).toFixed(2) + ') scale(' + s.toFixed(5) + ')');
}
const w2s = (X, Y, cam) => [W / 2 + (X - cam.cx) * BASE * cam.z, H / 2 + (Y - cam.cy) * BASE * cam.z];

/* ---------------------------------------------------------------- работа с ассетами */
const src = id => document.getElementById('src-' + id);
function tagIds(root) {   // id -> data-k, чтобы клоны не дублировали id
  const all = [root, ...root.querySelectorAll('[id]')];
  all.forEach(e => { if (e.id) { e.setAttribute('data-k', e.id); e.removeAttribute('id'); } });
}
function cloneOf(svgId, gid, keepTr) {
  const o = src(svgId).querySelector('[id="' + gid + '"]');
  if (!o) throw new Error('нет группы ' + svgId + '#' + gid);
  const bb = o.getBBox();
  const c = o.cloneNode(true);
  tagIds(c);
  return { el: c, bb, o };
}
const kq = (root, k) => root.querySelector('[data-k="' + k + '"]');
function placeAt(parent, cl_, tx, ty, sc) {
  const w = mk('g', { transform: 'translate(' + tx + ' ' + ty + ')' + (sc ? ' scale(' + sc + ')' : '') }, parent);
  w.appendChild(cl_.el);
  return w;
}
// поставить группу так, чтобы центр её рамки оказался в (cx, cy)
function placeCenter(parent, c, cx, cy, sc) {
  sc = sc || 1;
  const bx = c.bb.x + c.bb.width / 2, by = c.bb.y + c.bb.height / 2;
  return placeAt(parent, c, cx - bx * sc, cy - by * sc, sc);
}

/* ---------------------------------------------------------------- интерьер: стена (дальний слой, 0,4) */
const WALL = '#FFF1DB';
mk('rect', { x: -4000, y: -4000, width: 12000, height: 4660, fill: WALL }, gFar);
mk('rect', { x: -4000, y: 560, width: 12000, height: 100, fill: '#DADADA' }, gFar);
const FAR = {};
function farPut(id, cx, key) {
  const c = cloneOf('bg-wall', id);
  const bx = c.bb.x + c.bb.width / 2;
  FAR[key || id] = placeAt(gFar, c, cx - bx, 0);
  return c;
}
farPut('far-door', 330);
farPut('far-window', 790);
farPut('far-clock', 1210);
const farSign = farPut('far-sign', 1866);
farPut('far-router', 1866 + 60);
farPut('far-poster', 2160);
(function () {  // дальние стеллажи
  const c = cloneOf('bg-shelves-far', 'far-shelf-1');
  const bx = c.bb.x + c.bb.width / 2;
  placeAt(gFar, c, 1560 - bx, 0);
  const c3 = cloneOf('bg-shelves-far', 'far-shelf-3');
  placeAt(gFar, c3, 2620 - (c3.bb.x + c3.bb.width / 2), 0);
})();
// выключатель на стене рядом с кассой: рисуем крупнее из props.svg (два состояния)
const swX = 2330, swY = 330;
const swOn = cloneOf('props', 'prop-switch-on'), swOff = cloneOf('props', 'prop-switch-off');
const gSwOn = placeCenter(gFar, swOn, swX, swY, 0.8), gSwOff = placeCenter(gFar, swOff, swX, swY, 0.8);
const swClick = mk('g', null, gFar);
for (let i = 0; i < 6; i++) {
  const a = i * Math.PI / 3 + 0.3;
  mk('path', { d: 'M' + (swX + Math.cos(a) * 52) + ' ' + (swY + Math.sin(a) * 52) + ' L' + (swX + Math.cos(a) * 78) + ' ' + (swY + Math.sin(a) * 78),
    stroke: '#FFB208', 'stroke-width': 7, 'stroke-linecap': 'round' }, swClick);
}
swClick.setAttribute('opacity', 0);

/* пол (ступень параллакса 1,0) */
(function () {
  mk('rect', { x: -3000, y: 640, width: 12000, height: 4000, fill: '#DADADA' }, gFloor);
  const ys = [680, 735, 805, 900, 1020, 1180, 1400, 1700];
  ys.forEach(y => mk('path', { d: 'M-3000 ' + y + ' H9000', stroke: '#6B6B6B', 'stroke-width': 3, opacity: .45 }, gFloor));
  const vpx = 2000;
  for (let k = -60; k <= 60; k++) {
    mk('path', { d: 'M' + (vpx + k * 86) + ' 640 L' + (vpx + k * 150 + 0) + ' 1100 L' + (vpx + k * 150) + ' 2600', fill: 'none', stroke: '#6B6B6B', 'stroke-width': 3, opacity: .38 }, gFloor);
  }
  mk('rect', { x: -3000, y: 636, width: 12000, height: 8, fill: '#6B6B6B' }, gFloor);
})();

/* средние стеллажи (сцена, 1,0) */
(function () {
  const l = cloneOf('bg-shelves-mid', 'mid-shelf-left'), r = cloneOf('bg-shelves-mid', 'mid-shelf-right');
  placeAt(gMidSh, l, 300 - l.bb.x, 0);
  placeAt(gMidSh, r, 1250 - r.bb.x, 0);
  placeAt(gMidSh, cloneOf('bg-shelves-mid', 'mid-shelf-left'), 3400 - l.bb.x, 0);
  for (const [id, dx] of [['mid-tag-240', 300 - 0], ['mid-tag-560', 300 - 0]]) {
    const t = cloneOf('bg-shelves-mid', id); placeAt(gMidSh, t, dx - 120, 0);
  }
})();

/* лампы */
const LAMPS = [];
[640, 1400, 2140, 2860, 3600].forEach(x => {
  const c = cloneOf('bg-lamp', 'lamp-1');
  const bx = 520;
  const w = placeAt(gLamp, c, x - bx, 0);
  mk('path', { d: 'M' + x + ' -3000 V0', stroke: '#191919', 'stroke-width': 6 }, w.parentNode);
  const onG = kq(c.el, 'lamp-1-on');
  mk('path', { d: 'M-180 1080 L1220 1080 L2128 2400 L-1088 2400 Z', fill: '#FFB208', opacity: .22 }, onG);
  LAMPS.push({ on: onG, off: kq(c.el, 'lamp-1-off') });
});

/* касса (сцена) */
const CT_X = 2480;
const counter = mk('g', { transform: 'translate(' + (CT_X - 960) + ' 0)' }, gCounter);
let POS = {};
(function () {
  const root = src('bg-counter');
  [...root.children].forEach(ch => {
    if (!ch.id || ch.tagName !== 'g') return;
    if (['counter-calculator', 'counter-notebook', 'counter-phone'].includes(ch.id)) return;
    const c = ch.cloneNode(true); tagIds(c); counter.appendChild(c);
    POS[ch.id] = c;
  });
  ['counter-calculator', 'counter-notebook', 'counter-phone'].forEach(k => { const e = kq(counter, k); if (e) e.style.display = 'none'; });
  POS.on = kq(counter, 'pos-screen-on'); POS.off = kq(counter, 'pos-screen-off');
  // цена на экране старой кассы (К3)
  POS.price = mk('g', null, POS.on);
  mk('rect', { x: 652, y: 372, width: 236, height: 96, rx: 6, fill: '#FFFFFF' }, POS.price);
  mk('text', { x: 770, y: 446, 'font-family': 'Montserrat, Arial', 'font-size': 60, 'font-weight': 800, fill: '#191919', 'text-anchor': 'middle' }, POS.price).textContent = '30 с';
  POS.price.style.display = 'none';
})();

/* ближний слой (2,5): срезы стеллажей, баннер, ящик */
(function () {
  const items = [['near-shelf-left', 1960], ['near-banner', 2330], ['near-crate', 2600]];
  items.forEach(([id, x]) => {
    const c = cloneOf('bg-shelves-near', id);
    placeAt(gNear, c, x - c.bb.x, 0);
  });
})();

/* ---------------------------------------------------------------- актёры */
const PARTS = ['arm-back', 'leg-back', 'leg-front', 'body', 'arm-front'];
function parseT(s) {
  if (!s) return null;
  let m = s.match(/rotate\(\s*([-\d.eE]+)[ ,]+([-\d.eE]+)[ ,]+([-\d.eE]+)\s*\)/);
  if (m) return { r: 1, a: +m[1], px: +m[2], py: +m[3] };
  m = s.match(/translate\(\s*([-\d.eE]+)[ ,]+([-\d.eE]+)\s*\)/);
  if (m) return { r: 0, x: +m[1], y: +m[2] };
  return null;
}
const POSES = {
  ainura: ['stand', 'walk', 'calc', 'scan', 'happy', 'angry'], ata: ['stand', 'walk', 'angry', 'happy', 'sad'],
  jyldyz: ['stand', 'walk', 'angry', 'happy', 'sad'], bakyt: ['stand', 'walk', 'angry', 'happy', 'sad'],
  erlan: ['stand', 'walk', 'angry', 'happy', 'flash'], apa: ['stand', 'walk', 'angry', 'happy', 'sad'],
  nurlan: ['stand', 'walk', 'flash', 'happy', 'angry'],
};
const NOBLINK = { happy: 1 };

function buildActor(name, parent, o) {
  o = o || {};
  const poses = POSES[name], root = src(name);
  const a = { name, o, prm: {}, parts: {}, flip: o.flip ? -1 : 1, seed: o.seed || 0, cache: {} };
  a.g = mk('g', null, parent);
  a.shadow = mk('ellipse', { cx: 200, cy: 684, rx: 105, ry: 15, fill: 'rgba(25,25,25,.22)' }, a.g);
  const inner = mk('g', null, a.g);
  const p0 = root.querySelector('[id="' + name + '-' + poses[0] + '"]');
  const fc = p0.firstElementChild;
  const wrap = (fc && fc.tagName === 'g' && !fc.id) ? fc.getAttribute('transform') : null;
  if (wrap) inner.setAttribute('transform', wrap);
  a.slot = {};
  PARTS.forEach(k => { a.slot[k] = mk('g', null, inner); });   // порядок ярусов: arm-back, leg-back, leg-front, body, arm-front, head, fx
  a.slot.hw = mk('g', null, inner);
  a.slot.fx = mk('g', null, inner);
  a.head = {}; a.fx = {};
  poses.forEach(p => {
    const pg = root.querySelector('[id="' + name + '-' + p + '"]');
    const cont = wrap ? pg.firstElementChild : pg;
    const prm = {}; a.prm[p] = prm;
    const pre = name + '-' + p + '-';
    [...cont.children].forEach(child => {
      if (!child.id) return;
      const part = child.id.slice(pre.length);
      const orig = [child, ...child.querySelectorAll('[id]')];
      const c = child.cloneNode(true);
      const cls = [c, ...c.querySelectorAll('[id]')];
      const tr = [];
      orig.forEach((od, i) => {
        const key = od.id.slice(pre.length), pr = parseT(od.getAttribute('transform'));
        if (pr) prm[key] = pr;
        cls[i].setAttribute('data-k', key); cls[i].removeAttribute('id');
        if (pr && !(part === 'head' && i === 0)) tr.push({ el: cls[i], key });
      });
      if (part === 'head') { c.removeAttribute('transform'); a.head[p] = c; a.slot.hw.appendChild(c); c.style.display = 'none'; }
      else if (part === 'fx') { a.fx[p] = c; a.slot.fx.appendChild(c); c.style.display = 'none'; }
      else if (PARTS.includes(part)) {
        (a.parts[part] = a.parts[part] || {})[p] = { el: c, tr };
        a.slot[part].appendChild(c); c.style.display = 'none';
      }
    });
  });
  // моргание и маркер для глаз в темноте — внутри группы головы
  a.blink = mk('g', null, a.slot.hw);
  [180, 220].forEach(x => {
    mk('ellipse', { cx: x, cy: 148, rx: 9.5, ry: 8.6, fill: '#F2C29B' }, a.blink);
    mk('path', { d: 'M' + (x - 9) + ' 149 Q' + x + ' 155 ' + (x + 9) + ' 149', stroke: '#191919', 'stroke-width': 3, fill: 'none', 'stroke-linecap': 'round' }, a.blink);
  });
  a.blink.style.display = 'none';
  a.mark = mk('rect', { x: 0, y: 0, width: 1, height: 1, fill: 'none' }, a.slot.hw);
  // глаза для сцены «свет погас» (в слое эффектов)
  a.eyes = mk('g', null, gFX);
  a.eyeW = [], a.eyeP = [];
  const es = o.eyeScale || 1;
  [180, 220].forEach(x => {
    a.eyeW.push(mk('ellipse', { cx: x, cy: 148, rx: 11.5 * es, ry: 8 * es, fill: '#fff' }, a.eyes));
    a.eyeP.push(mk('circle', { cx: x, cy: 148, r: 4.2 * es, fill: '#101018' }, a.eyes));
  });
  a.eyes.style.display = 'none';
  // треки
  a.acts = {};
  return a;
}

/* ----- треки событий ----- */
function track(init) { return { init: Object.assign({ x: 0, pose: 'stand' }, init), pose: [], face: [], move: [], gest: [], hide: [], eyes: [], look: null }; }
const TR = {
  p(tr, t, pose, d) { tr.pose.push({ t, pose, d: d == null ? .3 : d }); return TR; },
  f(tr, t, pose, d) { tr.face.push({ t, pose, d: d == null ? .18 : d }); return TR; },
  mv(tr, t0, t1, x1, ease) { const prev = tr.move.length ? tr.move[tr.move.length - 1].x1 : tr.init.x; tr.move.push({ t0, t1, x0: prev, x1, ease: ease || E.inOutS }); return TR; },
  g(tr, t0, t1, fn, fi, fo) { tr.gest.push({ t0, t1, fn, fi: fi == null ? .22 : fi, fo: fo == null ? .22 : fo }); return TR; },
  hide(tr, key, t0, t1) { tr.hide.push({ key, t0, t1: t1 == null ? 1e9 : t1 }); return TR; },
};
function poseAt(list, t, init) {
  let A = init, B = init, k = 1;
  for (let i = 0; i < list.length; i++) {
    if (t >= list[i].t) { A = i ? list[i - 1].pose : init; B = list[i].pose; k = E.inOutS(cl((t - list[i].t) / list[i].d)); } else break;
  }
  return { A, B, k };
}
function xAt(tr, t) {
  let x = tr.init.x, m = 0, phi = 0, x0 = x;
  for (const mv of tr.move) {
    if (t >= mv.t1) { x = mv.x1; continue; }
    if (t >= mv.t0) {
      const u = (t - mv.t0) / (mv.t1 - mv.t0);
      x = lerp(mv.x0, mv.x1, mv.ease(u));
      m = Math.min(E.sm(cl((t - mv.t0) / .18)), E.sm(cl((mv.t1 - t) / .22)));
      phi = (x - mv.x0) * 2 * Math.PI / 340;
    }
    break;
  }
  return { x, m, phi };
}

const FEET = 685;
function evalActor(a, tr, t, flipOn) {
  const st = { add: {}, ov: {} };
  const pb = poseAt(tr.pose, t, tr.init.pose);
  Object.assign(st, pb);
  const mx = xAt(tr, t);
  st.x = mx.x; st.m = mx.m; st.phi = mx.phi + a.seed * 3;
  // лицо
  if (tr.face.length && t >= tr.face[0].t) {
    const pf = poseAt(tr.face, t, tr.init.face || tr.init.pose);
    st.fA = pf.A; st.fB = pf.B; st.fk = pf.k;
  } else if (tr.init.face) { st.fA = st.fB = tr.init.face; st.fk = 1; }
  else { st.fA = st.A; st.fB = st.B; st.fk = st.k; }
  // идл
  const ph = a.seed * 6.28, idl = 1 - st.m;
  st.add['head'] = idl * 1.8 * Math.sin(2 * Math.PI * .31 * t + ph);
  st.add['arm-back'] = idl * 1.6 * Math.sin(2 * Math.PI * .27 * t + ph + 1);
  st.add['arm-front'] = idl * 1.6 * Math.sin(2 * Math.PI * .23 * t + ph + 2);
  if (a.o.shuffle) {
    st.add['leg-front'] = idl * a.o.shuffle * Math.sin(2 * Math.PI * .8 * t + ph);
    st.add['leg-back'] = idl * a.o.shuffle * Math.sin(2 * Math.PI * .8 * t + ph + Math.PI);
  }
  if (a.o.stomp && t >= a.o.stomp[0] && t < a.o.stomp[1]) {  // Эрлан притоптывает в такт 100 BPM
    const bt = (t * 100 / 60) % 1, tap = Math.max(0, Math.sin(Math.PI * Math.min(1, bt * 2))) * (bt < .5 ? 1 : 0);
    st.add['leg-front'] = (st.add['leg-front'] || 0) + tap * -9; st.add['head'] += tap * 2;
  }
  // жесты
  for (const g of tr.gest) {
    if (t < g.t0 || t > g.t1) continue;
    const w = Math.min(E.sm(cl((t - g.t0) / g.fi)), E.sm(cl((g.t1 - t) / g.fo)));
    const o = g.fn(t - g.t0, t);
    for (const k in o) {
      if (k === 'x') { st.dx = (st.dx || 0) + o[k] * w; continue; }
      if (k === 'lean') { st.lean = (st.lean || 0) + o[k] * w; continue; }
      if (k === 'dy') { st.dy = (st.dy || 0) + o[k] * w; continue; }
      if (k.startsWith('+')) { st.add[k.slice(1)] = (st.add[k.slice(1)] || 0) + o[k] * w; continue; }
      const prev = st.ov[k];
      st.ov[k] = prev ? { v: lerp(prev.v, o[k], w), w: Math.max(prev.w, w) } : { v: o[k], w };
    }
  }
  st.hide = tr.hide.filter(h => t >= h.t0 && t < h.t1).map(h => h.key);
  return st;
}

function mixP(pa, pb, k) {
  if (!pa) return pb; if (!pb) return pa;
  if (pb.r) return { r: 1, a: lerp(pa.a, pb.a, k), px: pb.px, py: pb.py };
  return { r: 0, x: lerp(pa.x, pb.x, k), y: lerp(pa.y, pb.y, k) };
}
function paramFor(a, key, st) {
  const pB = a.prm[st.B][key] || a.prm[st.A][key], pA = a.prm[st.A][key] || pB;
  if (!pB) return null;
  let v = mixP(pA, pB, st.k);
  if (st.m > 0 && a.prm.walk && a.prm.walk[key]) v = mixP(v, a.prm.walk[key], st.m);
  return v;
}
function finalAngle(key, base, st) {
  let ang = base;
  const m = st.m;
  if (m > 0) {
    if (key === 'leg-back') ang = lerp(ang, 21 * Math.sin(st.phi), m);
    else if (key === 'leg-front') ang = lerp(ang, -21 * Math.sin(st.phi), m);
    else if (key === 'arm-back') ang += m * 13 * Math.sin(st.phi + Math.PI);
    else if (key === 'arm-front') ang += m * 5 * Math.sin(st.phi);
  }
  if (st.add[key] !== undefined) ang += st.add[key];
  const o = st.ov[key];
  if (o) ang = lerp(ang, o.v, o.w);
  return ang;
}
function setDisp(a, key, el, on) {
  if (a.cache[key] === on) return;
  a.cache[key] = on;
  el.style.display = on ? '' : 'none';
}
function applyActor(a, st, x, y, sc, t, visible) {
  a.g.style.display = visible ? '' : 'none';
  if (!visible) { a.eyes.style.display = 'none'; return; }
  const poses = POSES[a.name];
  const armArt = st.m > 0.5 ? 'walk' : (st.k < .5 ? st.A : st.B);
  PARTS.forEach(part => {
    const isArm = part.startsWith('arm');
    const pose = isArm ? armArt : poses[0];
    const map = a.parts[part];
    for (const p in map) {
      const ent = map[p], on = (p === pose) && !(st.hide && st.hide.includes(part));
      setDisp(a, part + p, ent.el, on);
      if (!on) continue;
      for (const tr of ent.tr) {
        const base = paramFor(a, tr.key, st);
        if (!base) continue;
        if (base.r) {
          const ang = finalAngle(tr.key, base.a, st);
          tr.el.setAttribute('transform', 'rotate(' + ang.toFixed(2) + ' ' + base.px + ' ' + base.py + ')');
          if (st.hide.length) tr.el.style.display = st.hide.includes(tr.key) ? 'none' : '';
        } else {
          tr.el.setAttribute('transform', 'translate(' + base.x.toFixed(2) + ' ' + base.y.toFixed(2) + ')');
        }
      }
    }
  });
  // голова и fx
  const fp = st.fk < .5 ? st.fA : st.fB;
  for (const p in a.head) setDisp(a, 'h' + p, a.head[p], p === fp);
  for (const p in a.fx) setDisp(a, 'x' + p, a.fx[p], p === fp);
  const hpA = a.prm[st.fA] && a.prm[st.fA].head, hpB = a.prm[st.fB] && a.prm[st.fB].head;
  const hp = mixP(hpA || hpB, hpB || hpA, st.fk);
  let hang = hp ? hp.a : 0;
  hang = finalAngle('head', hang, st);
  a.slot.hw.setAttribute('transform', 'rotate(' + hang.toFixed(2) + ' ' + (hp ? hp.px : 200) + ' ' + (hp ? hp.py : 205) + ')');
  const fxEl = a.fx[fp];
  if (fxEl) {
    const pu = 1 + .05 * Math.sin(t * 9 + a.seed * 9);
    fxEl.setAttribute('transform', 'translate(200 110) scale(' + pu.toFixed(3) + ') translate(-200 -110)');
  }
  // моргание (только если глаза открыты)
  const bl = blinkAmt(t, a.seed + 1) > .5 && !NOBLINK[fp] && st.manualBlink !== false;
  a.blink.style.display = bl ? '' : 'none';
  // позиция всей фигуры
  const bob = -st.m * 7 * Math.abs(Math.sin(st.phi));
  const breath = 1 + .007 * Math.sin(2 * Math.PI * .4 * t + a.seed * 6);
  const rock = (1 - st.m) * .5 * Math.sin(2 * Math.PI * .22 * t + a.seed * 4) + (st.lean || 0);
  a.g.setAttribute('transform', 'translate(' + (x + (st.dx || 0)).toFixed(2) + ' ' + (y + bob + (st.dy || 0)).toFixed(2) + ') scale(' + (sc * a.flip).toFixed(4) + ' ' + (sc * breath).toFixed(4) + ') rotate(' + rock.toFixed(2) + ' 200 ' + FEET + ') translate(-200 ' + (-FEET) + ')');
  a.last = { x, y, sc, st };
}
function blinkAmt(t, seed) {
  const P0 = 2.7 + 1.6 * hash(seed);
  const k = Math.floor((t + seed * 1.7) / P0), s0 = k * P0 - seed * 1.7 + hash(seed + k) * (P0 - .6);
  const u = (t - s0) / .14;
  return u >= 0 && u <= 1 ? 1 - Math.abs(u * 2 - 1) : 0;
}

/* ---------------------------------------------------------------- свет */
function radial(id, stops) {
  const g = mk('radialGradient', { id }, defs);
  stops.forEach(([o, c, a]) => mk('stop', { offset: o, 'stop-color': c, 'stop-opacity': a }, g));
}
radial('hg', [[0, '#000', 1], [.5, '#000', .92], [.8, '#000', .35], [1, '#000', 0]]);
radial('gp', [[0, '#FFB208', .62], [.45, '#FFB208', .32], [1, '#FFB208', 0]]);
radial('gf', [[0, '#E8F0FF', .45], [.5, '#DDE8FF', .18], [1, '#DDE8FF', 0]]);
radial('vg', [[.55, '#000', 0], [1, '#000', .28]]);
const mask = mk('mask', { id: 'mDark', maskUnits: 'userSpaceOnUse', x: 0, y: 0, width: W, height: H }, defs);
mk('rect', { x: 0, y: 0, width: W, height: H, fill: '#fff' }, mask);
const HOLES = [0, 1, 2, 3].map(() => mk('ellipse', { cx: -999, cy: -999, rx: 1, ry: 1, fill: 'url(#hg)' }, mask));
const gDarkWrap = mk('g', null, gFX);
const darkRect = mk('rect', { x: 0, y: 0, width: W, height: H, fill: '#060913', mask: 'url(#mDark)', opacity: 0 }, gDarkWrap);
const beam = mk('path', { d: 'M0 0', fill: 'rgba(225,235,255,.16)' }, gFX);
const glowF = mk('ellipse', { cx: -999, cy: -999, rx: 1, ry: 1, fill: 'url(#gf)' }, gFX);
const glowP = mk('ellipse', { cx: -999, cy: -999, rx: 1, ry: 1, fill: 'url(#gp)', style: 'mix-blend-mode:screen' }, gFX);
const gEyes = mk('g', null, gFX);
const gHud = mk('g', null, gFX);
const flashRect = mk('rect', { x: 0, y: 0, width: W, height: H, fill: '#fff', opacity: 0 }, gFX);
const vign = mk('rect', { x: 0, y: 0, width: W, height: H, fill: 'url(#vg)' }, gFX);
vign.setAttribute('opacity', .8);
function hole(i, x, y, rx, ry) {
  const h = HOLES[i];
  h.setAttribute('cx', x); h.setAttribute('cy', y); h.setAttribute('rx', rx); h.setAttribute('ry', ry);
}
function clearHoles() { HOLES.forEach((h, i) => hole(i, -999, -999, 1, 1)); }

/* ---------------------------------------------------------------- улица (К1) */
const EXT = { sc: 1 };
let apaExt = null, doorC = [2015, 350], extLetter = null, extWinBox = null;
(function () {
  gExt.setAttribute('opacity', 1);
  const g = mk('g', null, gExt);
  EXT.g = g;
  mk('rect', { x: -2000, y: -3000, width: 7000, height: 3200, fill: '#161A33' }, g);                 // небо
  for (let i = 0; i < 40; i++) mk('circle', { cx: -500 + hash(i) * 3400, cy: -200 - hash(i + 9) * 600, r: .8 + hash(i + 3) * 1.8, fill: '#E9EDFF', opacity: .4 + hash(i + 5) * .5 }, g);
  mk('rect', { x: -2000, y: -300, width: 7000, height: 300, fill: '#232844' }, g);                   // парапет
  const wall = cloneOf('bg-wall', 'far-wall'); g.appendChild(wall.el);
  ['far-window', 'far-door', 'far-sign', 'far-clock', 'far-router'].forEach(id => { const c = cloneOf('bg-wall', id); g.appendChild(c.el); if (id === 'far-door') { doorC = [c.bb.x + c.bb.width / 2, c.bb.y + c.bb.height / 2]; } if (id === 'far-window') extWinBox = c.bb; });
  mk('rect', { x: -2000, y: 0, width: 7000, height: 660, fill: '#0E1535', opacity: .52 }, g);        // вечерний тон
  // тротуар и дорога
  mk('rect', { x: -2000, y: 660, width: 7000, height: 110, fill: '#3A405F' }, g);
  mk('rect', { x: -2000, y: 660, width: 7000, height: 12, fill: '#5A6285' }, g);
  mk('rect', { x: -2000, y: 770, width: 7000, height: 1400, fill: '#1C2036' }, g);
  mk('rect', { x: -2000, y: 764, width: 7000, height: 10, fill: '#2A2F4A' }, g);
  for (let i = -6; i < 24; i++) mk('rect', { x: i * 260, y: 935, width: 140, height: 12, fill: '#5A6285', opacity: .6 }, g);
  // светящееся окно с силуэтами очереди
  const wb = extWinBox;
  const wg = mk('g', null, g);
  mk('rect', { x: wb.x + 14, y: wb.y + 14, width: wb.width - 28, height: wb.height - 28, fill: '#FFC24A' }, wg);
  mk('rect', { x: wb.x + 14, y: wb.y + wb.height * .62, width: wb.width - 28, height: wb.height * .38 - 14, fill: '#E39B1F' }, wg);
  EXT.heads = [];
  [0.14, 0.3, 0.46, 0.62, 0.78].forEach((fx, i) => {
    const x = wb.x + wb.width * fx, s = mk('g', null, wg), h = 22 + (i % 2) * 4;
    mk('circle', { cx: 0, cy: -34, r: h, fill: '#2A1D2B' }, s);
    mk('path', { d: 'M-46 70 Q-46 -6 0 -8 Q46 -6 46 70 Z', fill: '#2A1D2B' }, s);
    s.setAttribute('transform', 'translate(' + x + ' ' + (wb.y + wb.height - 50) + ')');
    EXT.heads.push(s); s._x = x; s._y = wb.y + wb.height - 50; s._i = i;
  });
  mk('rect', { x: wb.x + 14, y: wb.y + 14, width: wb.width - 28, height: wb.height - 28, fill: 'none', stroke: '#191919', 'stroke-width': 6 }, wg);
  mk('path', { d: 'M' + (wb.x + wb.width / 2) + ' ' + (wb.y + 14) + ' V' + (wb.y + wb.height - 14) + ' M' + (wb.x + 14) + ' ' + (wb.y + wb.height / 2) + ' H' + (wb.x + wb.width - 14), stroke: '#191919', 'stroke-width': 6 }, wg);
  // тёплое свечение на асфальте от окна
  mk('ellipse', { cx: wb.x + wb.width / 2, cy: 790, rx: 420, ry: 60, fill: '#FFB208', opacity: .14 }, g);
  // вывеска светится (поверх тона)
  const sg = cloneOf('bg-wall', 'far-sign'); g.appendChild(sg.el);
  const txt = [...src('bg-wall').querySelectorAll('#far-sign text')][0];
  if (txt && txt.getNumberOfChars() > 3) {
    const ex = txt.getExtentOfChar(2);
    extLetter = mk('rect', { x: ex.x - 4, y: ex.y - 2, width: ex.width + 8, height: ex.height + 4, fill: '#FFB208' }, g);
  }
  mk('ellipse', { cx: sg.bb.x + sg.bb.width / 2, cy: sg.bb.y + sg.bb.height / 2, rx: 340, ry: 120, fill: '#FFB208', opacity: .13 }, g);
  const dg = cloneOf('bg-wall', 'far-door'); g.appendChild(dg.el);
  
  // машина
  EXT.car = mk('g', null, g);
  mk('path', { d: 'M-190 0 L-190 -62 Q-190 -84 -165 -86 L-100 -90 L-70 -135 Q-60 -148 -40 -148 H70 Q90 -148 100 -132 L130 -88 L172 -84 Q196 -80 196 -56 V0 Z', fill: '#2E3C80', stroke: '#191919', 'stroke-width': 6, 'stroke-linejoin': 'round' }, EXT.car);
  mk('path', { d: 'M-52 -90 L-34 -128 H62 L88 -90 Z', fill: '#8FB4E8', stroke: '#191919', 'stroke-width': 4 }, EXT.car);
  [-110, 112].forEach(x => { mk('circle', { cx: x, cy: 0, r: 32, fill: '#191919' }, EXT.car); mk('circle', { cx: x, cy: 0, r: 13, fill: '#9AA3C4' }, EXT.car); });
  mk('path', { d: 'M196 -62 L600 -110 L600 -20 Z', fill: '#FFE8A0', opacity: .28 }, EXT.car);
  EXT.car.setAttribute('transform', 'translate(2600 905)');
  EXT.apaG = mk('g', null, g);
})();

/* ---------------------------------------------------------------- актёры: создание */
const ACT = {};
const ROW = { front: { y: 1010, sc: 1.0 }, back: { y: 972, sc: .95 }, behind: { y: 858, sc: .95 } };
function actor(name, parent, o) { const a = buildActor(name, parent, o); ACT[o.id || name] = a; return a; }
actor('apa', gBehind, { id: 'apaB', seed: .31, shuffle: 3 });
const aAinura = actor('ainura', gBehind, { seed: .12 });
const aNurlan = actor('nurlan', gBehind, { seed: .47, flip: true });
const aApa = ACT.apaB, aAta = actor('ata', gFront, { seed: .21, shuffle: 2.5 });
const aJyl = actor('jyldyz', gFront, { seed: .66, shuffle: 3 }), aBak = actor('bakyt', gFront, { seed: .83, shuffle: 4, eyeScale: 1.3 });
const aErl = actor('erlan', gFront, { seed: .55, shuffle: 2, stomp: [2, 7.4], eyeScale: 1.0 });
// рядом лежат лепёшка и пр. «реквизит на прилавке»
const COUNTER_BREAD = (() => {
  const c = cloneOf('props', 'prop-lepyoshka');
  const g = placeCenter(gCounter, c, CT_X - 360, 590, 0.62);
  g.style.display = 'none';
  return g;
})();
// порядок слоёв в gFront: дальние сзади. apa «back» — в gBehind (позади), поэтому переставим
gFront.insertBefore(aApa.g, gFront.firstChild);
gFront.insertBefore(aJyl.g, aAta.g);

apaExt = buildActor('apa', EXT.apaG, { id: 'apaX', seed: .31 });

/* ---------------------------------------------------------------- сценарий: позиции и треки */
const T = VAR === 'full' ? {
  click: 20.4, glow: 21.05, hud: 22.9, pik0: 24.0, piks: [25.2, 25.8, 26.4], lightOn: 31.0, rewind: [18.0, 19.1], swap: 19.24,
} : {
  click: 1.5, glow: 3.0, hud: 4.9, pik0: 6.0, piks: [7.2, 7.8, 8.4], lightOn: 1e9, rewind: null, swap: 0,
};
const QX = VAR === 'full'
  ? { ata: 2160, apa: 2000, bak: 1850, jyl: 1740, erl: 1500 }
  : { ata: 2310, apa: 2160, bak: 2020, jyl: 1900, erl: 1870 };
const AI_X = 2530;

function setupTracksFull() {
  const A = {}, B = {};
  /* ======== Акт А (τ = t): «как раньше» ======== */
  A.ainura = track({ x: AI_X, pose: 'stand' });
  A.ata = track({ x: QX.ata, pose: 'stand' });
  A.apa = track({ x: QX.apa, pose: 'stand' });
  A.bak = track({ x: QX.bak, pose: 'stand' });
  A.jyl = track({ x: QX.jyl, pose: 'stand' });
  A.erl = track({ x: QX.erl, pose: 'stand' });
  A.nur = track({ x: 3600, pose: 'walk' });
  // К3: ата протягивает лепёшку, Айнура берёт, «пик» старой кассы, возвращает
  TR.f(A.ainura, 3.0, 'scan', .3);
  TR.g(A.ata, 3.4, 5.5, (u) => ({ 'arm-front': -78 + 3 * Math.sin(u * 6), 'prop-lepyoshka': 78 }), .5, .6);
  TR.g(A.ainura, 3.55, 5.3, (u) => ({ 'arm-back': 60, 'forearm-back': -14 }), .45, .5);
  TR.g(A.bak, 2.3, 3.7, (u) => ({ 'arm-back': 150 + 14 * Math.sin(u * 11), 'forearm-back': 12 * Math.sin(u * 11 + 1) }), .3, .3);
  TR.f(A.bak, 2.3, 'happy', .15); TR.f(A.bak, 3.8, 'stand', .15);
  // К4: тьма. Эрлан достаёт телефон-фонарик
  TR.p(A.erl, 8.6, 'flash', .35); TR.f(A.erl, 7.9, 'flash', .12);
  TR.f(A.erl, 12.3, 'angry', .15);
  // К5: Айнура — калькулятор и тетрадка
  TR.p(A.ainura, 8.95, 'calc', .4);
  TR.g(A.ainura, 10.1, 14.9, (u) => ({ '+forearm-front': 7 * Math.sin(u * 19) }), .3, .3);
  TR.g(A.ainura, 10.15, 10.65, (u) => ({ '+head': 12 * Math.sin(Math.PI * u / .5), lean: 4 * Math.sin(Math.PI * u / .5) }), .1, .1);
  // К6: реакция очереди
  TR.p(A.ata, 12.9, 'angry', .3);
  TR.g(A.ata, 14.0, 15.1, (u) => ({ 'arm-front': -165 + 6 * Math.sin(u * 8), 'forearm-front': 10, 'prop-lepyoshka': 160 }), .25, .25);
  TR.p(A.apa, 13.15, 'sad', .3);
  TR.g(A.apa, 13.15, 13.65, (u) => ({ '+head': -16 * Math.sin(Math.PI * u / .5) }), .08, .08);
  TR.p(A.jyl, 13.7, 'angry', .25);
  TR.p(A.bak, 13.75, 'sad', .25);
  TR.g(A.bak, 13.75, 15.3, (u) => ({ 'arm-front': -28 + 14 * Math.sin(u * 12), 'forearm-front': 18 }), .2, .2);
  // «злая волна» — руки вверх слева направо
  [[A.erl, 14.85], [A.apa, 14.95], [A.jyl, 15.05], [A.ata, 15.15]].forEach(([tr, t0]) => {
    TR.g(tr, t0, t0 + .75, () => ({ 'arm-back': 148, 'forearm-back': -10, 'arm-front': -148, 'forearm-front': 10 }), .15, .25);
  });
  TR.g(A.bak, 15.05, 15.8, () => ({ 'arm-back': 140, 'arm-front': -140 }), .15, .25);
  // К7: люди уходят
  TR.mv(A.erl, 15.75, 18.4, 80); TR.p(A.erl, 15.7, 'walk', .2); TR.f(A.erl, 15.7, 'angry', .15);
  TR.mv(A.jyl, 16.1, 19.2, -200); TR.p(A.jyl, 16.05, 'walk', .2); TR.f(A.jyl, 16.05, 'angry', .15);
  TR.mv(A.bak, 16.1, 19.2, -40); TR.p(A.bak, 16.05, 'walk', .2); TR.f(A.bak, 16.05, 'sad', .15);
  TR.g(A.apa, 15.95, 16.55, (u) => ({ 'arm-front': -60 + 40 * Math.sin(u * 14), 'forearm-front': 20 }), .1, .1);
  TR.mv(A.apa, 16.45, 19.4, 500);
  TR.p(A.apa, 16.4, 'walk', .2); TR.f(A.apa, 16.4, 'sad', .15);
  TR.g(A.ata, 16.15, 16.75, (u) => ({ 'arm-front': -88, 'prop-lepyoshka': 88, 'forearm-front': 0 }), .3, .25);
  TR.hide(A.ata, 'prop-lepyoshka', 16.62);
  TR.mv(A.ata, 17.1, 19.4, 700); TR.p(A.ata, 16.95, 'sad', .3);
  // Нурлан выбегает с фонариком
  TR.mv(A.nur, 15.1, 15.95, 2790); TR.p(A.nur, 15.0, 'walk', .05); TR.p(A.nur, 15.9, 'flash', .25); TR.f(A.nur, 15.1, 'angry', .1);
  TR.f(A.nur, 15.9, 'flash', .2);
  TR.p(A.ainura, 16.55, 'stand', .4); TR.f(A.ainura, 16.55, 'calc', .1);
  TR.g(A.ainura, 16.85, 17.9, (u) => ({ 'arm-back': 38, 'forearm-back': 28, 'arm-front': -38, 'forearm-front': -28 }), .3, .35);

  /* ======== Акт Б (t абсолютное): «с NurCRM» ======== */
  B.ainura = track({ x: AI_X, pose: 'stand', face: 'scan' });
  B.ata = track({ x: QX.ata, pose: 'stand' });
  B.apa = track({ x: QX.apa, pose: 'stand' });
  B.bak = track({ x: QX.bak, pose: 'stand' });
  B.jyl = track({ x: QX.jyl, pose: 'stand' });
  B.erl = track({ x: QX.erl, pose: 'stand' });
  B.nur = track({ x: 3600, pose: 'walk' });
  TR.f(B.ainura, 20.5, 'stand', .1);
  TR.p(B.erl, 20.75, 'angry', .3);
  TR.p(B.ainura, T.glow, 'scan', .35); TR.f(B.ainura, T.glow, 'scan', .3);
  // К10: телефон наводится на лепёшку
  TR.g(B.ata, 22.2, 24.3, (u) => ({ 'arm-front': -80, 'prop-lepyoshka': 80, lean: 3.5 * E.sm(cl(u / .6)), '+head': 5 }), .45, .4);
  TR.p(B.ata, 24.25, 'happy', .25);
  TR.g(B.ainura, 22.5, 24.6, (u) => ({ 'arm-front': -64 + 4 * Math.sin(u * 5), 'forearm-front': 124 }), .5, .5);
  // К11: пик-пик-пик
  const piks = T.piks;
  TR.mv(B.bak, piks[0] - .2, piks[0] + .35, QX.bak + 46); TR.p(B.bak, piks[0] - .2, 'walk', .15); TR.p(B.bak, piks[0] + .3, 'stand', .25); TR.f(B.bak, piks[0] + .1, 'happy', .15);
  TR.mv(B.erl, piks[1] - .2, piks[1] + .35, QX.erl + 46); TR.p(B.erl, piks[1] - .2, 'walk', .15); TR.p(B.erl, piks[1] + .3, 'stand', .25); TR.f(B.erl, piks[1] + .1, 'stand', .15);
  TR.g(B.erl, piks[1] + .5, 27.4, (u) => ({ 'arm-front': -128, 'forearm-front': 70 }), .3, .3);
  TR.f(B.erl, 26.2, 'happy', .2);
  TR.mv(B.apa, piks[2] - .2, piks[2] + .35, QX.apa + 40); TR.p(B.apa, piks[2] - .2, 'walk', .15); TR.p(B.apa, piks[2] + .3, 'stand', .25); TR.f(B.apa, piks[2] + .1, 'happy', .15);
  TR.mv(B.jyl, piks[1] - .25, piks[1] + .3, QX.jyl + 40); TR.p(B.jyl, piks[1] - .25, 'walk', .15); TR.p(B.jyl, piks[1] + .25, 'stand', .25); TR.f(B.jyl, piks[1] + .1, 'happy', .15);
  // К12: оплата, чек, апа хлопает
  TR.g(B.ata, 28.0, 28.9, (u) => ({ 'arm-front': -82, 'prop-lepyoshka': 82 }), .25, .3);
  TR.g(B.ainura, 28.15, 29.0, (u) => ({ 'arm-back': 52, 'forearm-back': -12 }), .25, .3);
  TR.mv(B.apa, 28.0, 28.85, 2330); TR.p(B.apa, 28.0, 'walk', .15); TR.p(B.apa, 28.8, 'stand', .25);
  TR.g(B.apa, 29.0, 29.9, (u) => ({ 'arm-front': -96 + 8 * Math.sin(u * 16), 'forearm-front': 8 }), .2, .3);
  TR.f(B.ainura, 29.0, 'happy', .2); TR.f(B.ainura, 30.6, 'scan', .2);
  // К13: Нурлан с фонариком, свет включается
  TR.mv(B.nur, 29.4, 30.35, 2790); TR.p(B.nur, 29.35, 'walk', .05); TR.p(B.nur, 30.3, 'flash', .25); TR.f(B.nur, 29.4, 'happy', .1); TR.f(B.nur, 30.3, 'flash', .2);
  TR.p(B.nur, 30.9, 'stand', .35); TR.f(B.nur, 30.9, 'stand', .2); TR.f(B.nur, 31.5, 'happy', .2);
  TR.f(B.ata, 31.0, 'happy', .08); TR.f(B.apa, 31.0, 'happy', .08); TR.f(B.erl, 31.0, 'happy', .08); TR.f(B.jyl, 31.0, 'happy', .08);
  TR.p(B.bak, 31.05, 'happy', .15);
  // К14: все выходят к двери
  TR.mv(B.erl, 32.35, 35.4, 200); TR.p(B.erl, 32.3, 'walk', .2); TR.f(B.erl, 32.3, 'happy', .1);
  TR.mv(B.jyl, 32.2, 35.4, 380); TR.p(B.jyl, 32.15, 'walk', .2);
  TR.mv(B.bak, 32.2, 35.4, 540); TR.p(B.bak, 32.15, 'walk', .2);
  TR.mv(B.apa, 32.45, 35.4, 640); TR.p(B.apa, 32.4, 'walk', .2);
  TR.g(B.apa, 32.45, 33.8, (u) => ({ 'arm-back': 152 + 10 * Math.sin(u * 10) }), .2, .3);
  TR.mv(B.ata, 32.0, 35.4, 900); TR.p(B.ata, 31.95, 'walk', .2);
  TR.mv(B.nur, 33.0, 36.0, 3600); TR.p(B.nur, 32.95, 'walk', .2);
  return { A, B };
}

function setupTracksShort() {
  const B = {};
  B.ainura = track({ x: AI_X, pose: 'stand', face: 'scan' });
  B.ata = track({ x: QX.ata, pose: 'stand' });
  B.apa = track({ x: QX.apa, pose: 'stand' });
  B.bak = track({ x: QX.bak, pose: 'stand' });
  B.jyl = track({ x: QX.jyl, pose: 'stand' });
  B.erl = track({ x: QX.erl, pose: 'stand' });
  B.nur = track({ x: 3600, pose: 'walk' });
  TR.f(B.ainura, 1.7, 'stand', .1);
  TR.p(B.erl, 1.9, 'angry', .3);
  TR.p(B.ainura, T.glow, 'scan', .35); TR.f(B.ainura, T.glow, 'scan', .3);
  TR.g(B.ata, 4.2, 6.4, (u) => ({ 'arm-front': -80, 'prop-lepyoshka': 80, lean: 3.5 * E.sm(cl(u / .6)), '+head': 5 }), .45, .4);
  TR.p(B.ata, 6.35, 'happy', .25);
  TR.g(B.ainura, 4.4, 6.5, (u) => ({ 'arm-front': -64 + 4 * Math.sin(u * 5), 'forearm-front': 124 }), .5, .5);
  const piks = T.piks;
  TR.mv(B.bak, piks[0] - .2, piks[0] + .35, QX.bak + 36); TR.p(B.bak, piks[0] - .2, 'walk', .15); TR.p(B.bak, piks[0] + .3, 'stand', .25); TR.f(B.bak, piks[0] + .1, 'happy', .15);
  TR.mv(B.erl, piks[1] - .2, piks[1] + .35, QX.erl + 40); TR.p(B.erl, piks[1] - .2, 'walk', .15); TR.p(B.erl, piks[1] + .3, 'stand', .25); TR.f(B.erl, piks[1] + .1, 'happy', .15);
  TR.g(B.erl, piks[1] + .5, 9.5, (u) => ({ 'arm-front': -128, 'forearm-front': 70 }), .3, .3);
  TR.mv(B.apa, piks[2] - .2, piks[2] + .35, QX.apa + 36); TR.p(B.apa, piks[2] - .2, 'walk', .15); TR.p(B.apa, piks[2] + .3, 'stand', .25); TR.f(B.apa, piks[2] + .1, 'happy', .15);
  TR.f(B.apa, 10.05, 'happy', .15);
  TR.g(B.apa, 10.2, 11.3, (u) => ({ 'arm-front': -96 + 8 * Math.sin(u * 16), 'forearm-front': 8 }), .2, .3);
  TR.f(B.ainura, 9.8, 'happy', .2);
  TR.f(B.ata, 6.35, 'happy', .1);
  delete B.jyl;
  return { A: null, B };
}
const TRK = VAR === 'full' ? setupTracksFull() : setupTracksShort();
const NAMES = { ainura: aAinura, ata: aAta, apa: aApa, bak: aBak, jyl: aJyl, erl: aErl, nur: aNurlan };

/* ---------------------------------------------------------------- камеры */
const camAKeys = [
  [0, 960, 540, 1.12], [1.9, 960, 540, 1.12],
  [4.4, 2100, 540, 1.0, E.inOut3],
  [7.4, 2150, 545, 1.045, E.sm],
  [8.5, 2150, 545, 1.045],
  [9.0, 2540, 430, 1.7, E.inOutS],
  [12.0, 2540, 436, 1.85, E.lin],
  [12.9, 2540, 436, 1.85],
  [13.1, 2010, 520, 1.6, E.inOutS],
  [13.5, 2010, 520, 1.6],
  [13.7, 1800, 545, 1.5, E.inOutS],
  [13.85, 1800, 545, 1.5],
  [14.05, 2180, 545, 1.6, E.inOutS],
  [14.8, 2180, 545, 1.6],
  [15.25, 2080, 540, 1.0, E.inOutS],
  [18.0, 1900, 540, 0.92, E.inOutS],
];
const camBKeys = [
  [19.2, 2150, 545, 1.0], [20.4, 2200, 545, 1.03, E.sm],
  [21.9, 2200, 545, 1.03],
  [22.45, 2440, 470, 1.85, E.inOutS],
  [24.5, 2440, 470, 1.85],
  [25.3, 2150, 545, 1.0, E.inOutS],
  [27.6, 2200, 545, 1.05, E.sm],
  [28.1, 2330, 560, 1.3, E.inOutS],
  [29.5, 2330, 560, 1.3],
  [30.1, 2050, 540, 1.0, E.inOutS],
  [32.0, 2050, 540, 1.0],
  [34.2, 1050, 540, 1.0, E.inOutS],
];
const camShort = [
  [0, 2330, 730, 0.92], [1.5, 2330, 730, 0.96, E.sm], [2.9, 2330, 730, 0.96],
  [3.5, 2450, 520, 1.45, E.inOutS], [5.2, 2450, 520, 1.55, E.sm], [6.8, 2450, 520, 1.55],
  [7.3, 2260, 730, 0.95, E.inOutS], [11.0, 2260, 730, 0.95],
];
function camera(t, tau, act) {
  if (VAR === 'short') { const k = kf(t, camShort); return { cx: k[0], cy: k[1], z: k[2] }; }
  const k = act === 'A' ? kf(tau, camAKeys) : kf(t, camBKeys);
  return { cx: k[0], cy: k[1], z: k[2] };
}

/* ---------------------------------------------------------------- рендер кадра */
const subsEl = div('', null); subsEl.id = 'subs';
const wipe = document.getElementById('wipe') || div('', null); wipe.id = 'wipe';
wipe.style.transform = 'translateX(-101%)';
let ptr = null;

function actOf(t) {
  if (VAR === 'short') return { act: 'B', tau: t };
  if (t < T.rewind[0]) return { act: 'A', tau: t };
  if (t < T.swap) {
    const u = E.inOutS(cl((t - T.rewind[0]) / (T.rewind[1] - T.rewind[0])));
    return { act: 'A', tau: lerp(T.rewind[0], 3.2, u), rewind: true };
  }
  return { act: 'B', tau: t };
}
function lightsOn(t, tau, act) {
  if (VAR === 'short') return t < T.click;
  if (act === 'A') return tau < 7.4 || tau >= 18.99;
  return t < T.click || t >= T.lightOn;
}
function darkAlpha(t, tau, act) {
  if (VAR === 'short') { return t >= T.click ? (t < T.click + .034 ? .6 : .92) : 0; }
  if (act === 'A') {
    if (tau < 7.4) return 0;
    if (tau < 7.45) return .55;
    return tau < 8.3 ? .92 : lerp(.92, .82, seg(tau, 8.3, .6));
  }
  if (t < T.click) return 0;
  if (t < T.click + .034) return .55;
  if (t < T.lightOn) return .92;
  if (t < T.lightOn + .067) return .45;
  return 0;
}
function eyesOn(t, tau, act) {
  if (VAR === 'short') return t >= T.click + .06 && t < 11.0;
  if (act === 'A') return tau >= 7.46 && tau < 19;
  return t >= T.click + .07 && t < T.lightOn;
}

function renderInterior(t, tau, act, cam) {
  setLayer(gFar, .4, cam); setLayer(gMidSh, 1, cam); setLayer(gFloor, 1, cam); setLayer(gLamp, 1, cam);
  setLayer(gBehind, 1, cam); setLayer(gCounter, 1, cam); setLayer(gFront, 1, cam); setLayer(gNear, 2.5, cam);
  const on = lightsOn(t, tau, act);
  LAMPS.forEach(l => { l.on.style.display = on ? '' : 'none'; l.off.style.display = on ? 'none' : ''; });
  if (POS.on) { POS.on.style.display = on ? '' : 'none'; POS.off.style.display = on ? 'none' : ''; }
  POS.price.style.display = (act === 'A' && tau >= 4.45 && tau < 6.0) ? '' : 'none';
  // выключатель
  gSwOn.style.display = on ? '' : 'none'; gSwOff.style.display = on ? 'none' : '';
  const clicks = VAR === 'full' ? (act === 'A' ? [[7.4]] : [[T.click], [T.lightOn]]) : [[T.click]];
  let sc = 0;
  clicks.forEach(([c]) => { const tt = act === 'A' ? tau : t; if (tt >= c && tt < c + .35) sc = Math.max(sc, 1 - (tt - c) / .35); });
  swClick.setAttribute('opacity', sc.toFixed(3));
  // ближний слой гаснет к концу пролёта
  const nearOp = VAR === 'full' ? (act === 'A' ? (tau < 4.0 ? 1 : 1 - seg(tau, 4.0, .35)) : 0) : 0;
  gNear.setAttribute('opacity', nearOp.toFixed(3));
  gNear.style.display = nearOp > .01 ? '' : 'none';
  // актёры
  const tracks = act === 'A' ? TRK.A : TRK.B;
  const ctt = act === 'A' ? tau : t;
  const rowOf = { ainura: 'behind', nur: 'behind', ata: 'front', apa: 'back', bak: 'front', jyl: 'back', erl: 'front' };
  for (const k in NAMES) {
    const a = NAMES[k], tr = tracks && tracks[k];
    if (!tr) { a.g.style.display = 'none'; a.eyes.style.display = 'none'; continue; }
    const st = evalActor(a, tr, ctt);
    const row = ROW[rowOf[k]];
    const vis = k === 'nur' ? st.x < 3500 : true;   // Нурлан за кадром, пока не «выбежал» из подсобки
    applyActor(a, st, st.x, row.y, row.sc, ctt, vis);
  }
  // лепёшка на прилавке (ата положил её обратно)
  COUNTER_BREAD.style.display = (act === 'A' && tau >= 16.62) ? '' : 'none';
}

function renderLight(t, tau, act, cam) {
  const da = darkAlpha(t, tau, act);
  darkRect.setAttribute('opacity', da.toFixed(3));
  gDarkWrap.style.display = da > .001 ? '' : 'none';
  clearHoles();
  beam.setAttribute('d', 'M0 0');
  glowF.setAttribute('cx', -999); glowP.setAttribute('cx', -999);
  const ctt = act === 'A' ? tau : t;
  const s = BASE * cam.z;
  // свет фонарика (акт А) и телефона (акт Б)
  if (da > .01) {
    if (act === 'A' && tau >= 8.6 && tau < 18.9) {
      let tgt;
      const jx = (hash(Math.floor(ctt * 10)) - .5) * 24 / cam.z, jy = (hash(Math.floor(ctt * 10) + 50) - .5) * 20 / cam.z;
      if (tau < 15.25) tgt = [cam.cx + jx, cam.cy - 30 + jy, 250, 190];
      else if (tau < 15.9) tgt = [lerp(cam.cx, 2400, 0), cam.cy + jy, 520, 330];
      else tgt = [lerp(2300, 2700, .0) + jx, 560 + jy, 560, 340];
      const fade = Math.min(seg(tau, 8.6, .4), 1 - seg(tau, 18.4, .5));
      if (tau >= 15.9) { tgt[0] = (aNurlan.last ? aNurlan.last.x - 480 : 2400) + jx; }
      const [sx, sy] = w2s(tgt[0], tgt[1], cam);
      hole(0, sx, sy, tgt[2] * s * fade, tgt[3] * s * fade);
      glowF.setAttribute('cx', sx); glowF.setAttribute('cy', sy); glowF.setAttribute('rx', tgt[2] * s * .95 * fade); glowF.setAttribute('ry', tgt[3] * s * .95 * fade);
      // луч от источника
      let hx, hy;
      const srcA = tau < 15.6 ? aErl : aNurlan;
      const el = srcA.parts['arm-front'] && srcA.parts['arm-front'][srcA.last && srcA.last.st ? (srcA.last.st.k < .5 ? srcA.last.st.A : srcA.last.st.B) : 'flash'];
      const hand = el && el.el.querySelector('[data-k$="prop-flashlight"],[data-k*="prop-"]');
      if (hand && srcA.g.style.display !== 'none') {
        const r = hand.getBoundingClientRect();
        hx = r.left + r.width / 2; hy = r.top + r.height / 2;
        const dx = sx - hx, dy = sy - hy, len = Math.hypot(dx, dy) || 1, nx = -dy / len, ny = dx / len, rr = Math.min(tgt[3] * s * .55, 260) * fade;
        beam.setAttribute('d', 'M' + hx.toFixed(1) + ' ' + hy.toFixed(1) + ' L' + (sx + nx * rr).toFixed(1) + ' ' + (sy + ny * rr).toFixed(1) + ' L' + (sx - nx * rr).toFixed(1) + ' ' + (sy - ny * rr).toFixed(1) + ' Z');
        beam.setAttribute('opacity', (.9 * fade).toFixed(2));
      }
    }
    if (act === 'B' && ctt >= T.glow) {
      const up = seg(ctt, T.glow, .32, E.out3);
      const ph = aAinura.parts['arm-front'] && aAinura.parts['arm-front'][aAinura.last && aAinura.last.st ? (aAinura.last.st.k < .5 ? aAinura.last.st.A : aAinura.last.st.B) : 'scan'];
      let px = 0, py = 0, got = false;
      const pe = ph && ph.el.querySelector('[data-k*="prop-phone"]');
      if (pe && aAinura.g.style.display !== 'none' && (aAinura.last.st.B === 'scan' || aAinura.last.st.A === 'scan')) {
        const r = pe.getBoundingClientRect(); px = r.left + r.width / 2; py = r.top + r.height / 2; got = true;
      }
      if (!got) { [px, py] = w2s(AI_X + 70, 520, cam); }
      const breath = 1 + .04 * Math.sin(2 * Math.PI * ctt / 2.4);
      const widen = VAR === 'full' ? seg(ctt, 24.7, 1.0) * 380 + seg(ctt, 27.8, 1.2) * 340 : seg(ctt, 6.8, 1.0) * 380;
      const cyHole = lerp(H + 300 * s, py - 60 * s, up);
      const rx = (440 + widen) * s * breath, ry = 380 * s * breath;
      const hxx = px - widen * s * .55;
      hole(1, hxx, cyHole, rx, ry);
      glowP.setAttribute('cx', hxx); glowP.setAttribute('cy', cyHole); glowP.setAttribute('rx', rx * .9); glowP.setAttribute('ry', ry * .9);
      glowP.setAttribute('opacity', (VAR === 'full' && ctt > T.lightOn - .2 ? 1 - seg(ctt, T.lightOn - .2, .2) : 1).toFixed(2));
    }
  }
  // глаза
  const eo = eyesOn(t, tau, act);
  for (const k in NAMES) {
    const a = NAMES[k];
    if (!eo || a.g.style.display === 'none') { a.eyes.style.display = 'none'; continue; }
    const m = a.mark.getCTM();
    if (!m) { a.eyes.style.display = 'none'; continue; }
    a.eyes.style.display = '';
    a.eyes.setAttribute('transform', 'matrix(' + m.a + ' ' + m.b + ' ' + m.c + ' ' + m.d + ' ' + m.e + ' ' + m.f + ')');
    const bl = 1 - blinkAmt(ctt, a.seed + 1);
    const tt0 = act === 'A' ? 7.46 : T.click + .07;
    const fadeIn = cl((ctt - tt0) / .12);
    let lx = 0, ly = 0;
    const idle = .6;
    lx = Math.sin(ctt * .9 + a.seed * 9) * idle; ly = Math.cos(ctt * .7 + a.seed * 7) * .3;
    if (a === aErl) {
      if (VAR === 'full' && act === 'A' && ctt > 7.8 && ctt < 8.7) { lx = 0; ly = -1.1; }
      if (act === 'B' && ctt > T.click + .5 && ctt < T.click + 1.5) { const u = (ctt - T.click - .5) * 6; lx = Math.sin(u); ly = -Math.cos(u) * 1.1; }
    }
    a.eyeW.forEach((e, i) => e.setAttribute('ry', (8 * (a.o.eyeScale || 1) * (.12 + .88 * bl)).toFixed(2)));
    a.eyeP.forEach((p, i) => { p.setAttribute('cx', (i ? 220 : 180) + lx * 3.4); p.setAttribute('cy', 148 + ly * 2.6); p.setAttribute('opacity', bl > .5 ? 1 : 0); });
    let lit = 0;
    HOLES.forEach(h => {
      const hx = +h.getAttribute('cx'), hy = +h.getAttribute('cy'), hrx = +h.getAttribute('rx'), hry = +h.getAttribute('ry');
      if (hx < -900) return;
      const d = Math.hypot((m.e - hx) / hrx, (m.f - hy) / hry);
      const ha = d < .5 ? 1 - .08 * d / .5 : d < .8 ? lerp(.92, .35, (d - .5) / .3) : lerp(.35, 0, cl((d - .8) / .2));
      lit = Math.max(lit, ha);
    });
    a.eyes.setAttribute('opacity', (fadeIn * Math.pow(1 - lit, 2.2) * cl((da - .45) / .3)).toFixed(3));
  }
  // вспышка «свет включается»
  let fl = 0;
  if (VAR === 'full' && act === 'B' && t >= T.lightOn) fl = .45 * (1 - seg(t, T.lightOn + .06, .22, E.out3)) * (t < T.lightOn + .06 ? 0 : 1) + (t >= T.lightOn && t < T.lightOn + .06 ? .45 : 0);
  if (VAR === 'full' && t >= 34.52 && t < 34.62) fl = Math.max(fl, .0);
  flashRect.setAttribute('opacity', fl.toFixed(3));
}

/* ----- HUD: видоискатель, цена, список, деньги, чек ----- */
const hudDom = {
  chip: div('chip', null, ''), panel: div('panel', null, ''), sent: div('chip', null, ''),
};
hudDom.chip.style.opacity = 0; hudDom.panel.style.opacity = 0; hudDom.sent.style.opacity = 0;
const hudG = mk('g', null, gHud);
const vf = mk('g', null, hudG), vfLine = mk('rect', { fill: '#E53935', height: 4 }, vf);
const vfC = [0, 1, 2, 3].map(() => mk('path', { fill: 'none', stroke: '#FFB208', 'stroke-width': 7, 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }, vf));
const sprMoney = cloneOf('props', 'prop-money'), sprRec = cloneOf('props', 'prop-receipt');
const gMoney = mk('g', null, hudG), gRec = mk('g', null, hudG);
placeCenter(gMoney, sprMoney, 0, 0, 0.9); placeCenter(gRec, sprRec, 0, 0, 1.0);
gMoney.style.display = 'none'; gRec.style.display = 'none';
const dust = mk('g', null, gHud);
for (let i = 0; i < 5; i++) mk('circle', { cx: 0, cy: 0, r: 20, fill: '#F4EFE6' }, dust);
dust.setAttribute('opacity', 0);
function propRect(a, key) {
  for (const el of a.g.querySelectorAll('[data-k="' + key + '"]')) {
    if (el.closest('[style*="display: none"]')) continue;
    const r = el.getBoundingClientRect();
    if (r.width) return { x: r.left + r.width / 2, y: r.top + r.height / 2, w: r.width, h: r.height };
  }
  return null;
}
function renderHud(t, tau, act, cam) {
  const ctt = act === 'A' ? tau : t, s = BASE * cam.z;
  // пыль из тетрадки (К5)
  if (VAR === 'full' && act === 'A' && tau >= 10.2 && tau < 11.1) {
    const [dx, dy] = w2s(AI_X - 80, 600, cam), u = (tau - 10.2) / .9;
    dust.setAttribute('opacity', (1 - u) * .85);
    [...dust.children].forEach((c, i) => { const a = i * 1.26 + .4; c.setAttribute('cx', dx + Math.cos(a) * u * 150 * s * (.6 + .15 * i)); c.setAttribute('cy', dy - u * 90 * s + Math.sin(a) * u * 50 * s); c.setAttribute('r', (14 + 38 * u) * s * (.7 + .1 * i)); });
  } else dust.setAttribute('opacity', 0);
  // видоискатель
  const hudStart = T.hud, hudEnd = T.pik0 + .25;
  vf.style.display = 'none';
  if (act === 'B' && ctt >= hudStart && ctt < hudEnd + .25) {
    const br = propRect(aAta, 'prop-lepyoshka');
    const bread = br || (() => { const [x, y] = w2s(2370, 590, cam); return { x, y, w: 90 * s, h: 60 * s }; })();
    const u = seg(ctt, hudStart, T.pik0 - hudStart, E.inOutS);
    const half = lerp(150, 78, u) * s, cx = bread.x, cy = bread.y, l = 34 * s;
    vf.style.display = '';
    const fade = Math.min(seg(ctt, hudStart, .2), 1 - seg(ctt, hudEnd, .25));
    vf.setAttribute('opacity', fade.toFixed(2));
    const co = [[-1, -1], [1, -1], [1, 1], [-1, 1]];
    vfC.forEach((p, i) => { const [sx, sy] = co[i]; const x = cx + sx * half, y = cy + sy * half; p.setAttribute('d', 'M' + x + ' ' + (y - sy * l) + ' V' + y + ' H' + (x - sx * l)); p.setAttribute('stroke-width', 7 * Math.max(.7, s)); });
    const ly = cy - half + (u * 2 * half) * (.5 + .5 * Math.sin(ctt * 9));
    vfLine.setAttribute('x', cx - half); vfLine.setAttribute('y', ly); vfLine.setAttribute('width', half * 2);
    vfLine.setAttribute('height', 4 * Math.max(.7, s));
  }
  // цена «Лепёшка — 30 с»
  const chip = hudDom.chip;
  if (act === 'B' && ctt >= T.pik0 && ctt < T.pik0 + 1.9) {
    const u = ctt - T.pik0, br = propRect(aAta, 'prop-lepyoshka');
    const pos = br || { x: w2s(2370, 590, cam)[0], y: w2s(2370, 590, cam)[1] };
    chip.innerHTML = L('Лепёшка — 30 с', 'Нан — 30 с');
    chip.style.fontSize = (PORTRAIT ? 4.4 : 3.1) * VM + 'px';
    chip.style.left = Math.max(2 * VM, Math.min(W - chip.offsetWidth - 2 * VM, pos.x - 40 * s)) + 'px'; chip.style.top = (pos.y - (PORTRAIT ? 130 : 110) * s) + 'px';
    const p = E.back(cl(u / .25)), o = 1 - seg(ctt, T.pik0 + 1.5, .4);
    chip.style.opacity = Math.min(1, u / .08) * o; chip.style.transform = 'translate(0,-100%) scale(' + (.6 + .4 * p).toFixed(3) + ')';
  } else chip.style.opacity = 0;
  // список «пик-пик-пик»
  const pan = hudDom.panel;
  const rows = [[L('Лепёшка', 'Нан'), '30 с'], [L('Молоко', 'Сүт'), '65 с'], [L('Семечки', 'Күн карама'), '40 с'], [L('Хлеб', 'Нан (батон)'), '35 с']];
  const t0s = [T.pik0, ...T.piks];
  if (act === 'B' && ctt >= T.pik0 + 1.9 && ctt < (VAR === 'full' ? 29.2 : 11.0)) {
    const n = t0s.slice(1).filter(x => ctt >= x).length + 1;
    pan.innerHTML = rows.slice(0, n).map(r => '<div class="row"><span>' + r[0] + '</span><span>' + r[1] + '</span></div>').join('');
    pan.style.fontSize = (PORTRAIT ? 3.8 : 2.5) * VM + 'px';
    const [px0, py0] = w2s(AI_X + 10, 330, cam);
    pan.style.left = (PORTRAIT ? W * .5 - 20 * VM : W - 29 * VM) + 'px';
    pan.style.top = (PORTRAIT ? H * .205 : 19 * VM) + 'px';
    const ent = seg(ctt, T.pik0 + 1.9, .3, E.out3), ext = 1 - seg(ctt, (VAR === 'full' ? 29.2 : 11.0) - .3, .3);
    pan.style.opacity = Math.min(ent, ext);
    pan.style.transform = 'translateY(' + (1 - ent) * 12 + 'px)';
    pan.style.minWidth = (PORTRAIT ? 30 : 17) * VM + 'px';
  } else pan.style.opacity = 0;
  // деньги и чек (К12)
  if (VAR === 'full') {
    if (act === 'B' && t >= 28.2 && t < 28.75) {
      const u = (t - 28.2) / .55, [x0, y0] = w2s(2320, 600, cam), [x1, y1] = w2s(AI_X - 60, 560, cam);
      gMoney.style.display = ''; gMoney.setAttribute('transform', 'translate(' + lerp(x0, x1, E.inOutS(u)) + ' ' + (lerp(y0, y1, E.inOutS(u)) - Math.sin(Math.PI * u) * 70 * s) + ') scale(' + s * .9 + ')');
    } else gMoney.style.display = 'none';
    if (act === 'B' && t >= 28.55 && t < 29.4) {
      const u = (t - 28.55) / .8, [x0, y0] = w2s(AI_X + 60, 520, cam), [x1, y1] = w2s(2400, 640, cam);
      gRec.style.display = ''; gRec.setAttribute('transform', 'translate(' + lerp(x0, x1, E.out3(u)) + ' ' + lerp(y0, y1, E.out3(u)) + ') scale(' + s * .95 + ') rotate(' + (-10 + 20 * u) + ')');
      gRec.setAttribute('opacity', 1 - seg(t, 29.2, .2));
    } else gRec.style.display = 'none';
  }
  // «Чеки отправлены 5/5»
  const sd = hudDom.sent;
  if (VAR === 'full' && act === 'B' && t >= 32.3 && t < 34.0) {
    const u = t - 32.3;
    sd.style.fontSize = 3.0 * VM + 'px';
    sd.innerHTML = '✓ ' + L('Чеки отправлены ', 'Чектер жөнөтүлдү ') + Math.min(5, Math.floor(1 + u * 3.2)) + '/5';
    sd.style.left = (W - 56 * VM) + 'px'; sd.style.top = (H - 20 * VM) + 'px';
    sd.style.opacity = Math.min(seg(t, 32.3, .3, E.out3), 1 - seg(t, 33.7, .3));
  } else sd.style.opacity = 0;
}

/* ----- улица: кадр К1 ----- */
function renderExt(t) {
  const on = VAR === 'full' && t < 2.25;
  gExt.style.display = on ? '' : 'none';
  if (!on) return;
  const fly = seg(t, 1.1, .85, E.inOut3);
  const z = lerp(1.0 + .06 * seg(t, 0, 1.4, E.lin), 4.6, fly), ex = lerp(1225, doorC[0], fly), ey = lerp(500, doorC[1] + 40, fly);
  const sc = BASE * z;
  EXT.g.setAttribute('transform', 'translate(' + (W / 2 - sc * ex) + ' ' + (H / 2 - sc * ey) + ') scale(' + sc + ')');
  gExt.setAttribute('opacity', (1 - seg(t, 1.9, .3)).toFixed(3));
  if (extLetter) extLetter.setAttribute('opacity', (Math.sin(t * 17) > .55 || (t > .7 && t < .86) ? 1 : 0));
  EXT.heads.forEach(h => { h.setAttribute('transform', 'translate(' + h._x + ' ' + (h._y + 4 * Math.sin(t * 2 + h._i)) + ')'); });
  // машина
  const cu = seg(t, .55, 1.5, E.lin);
  EXT.car.setAttribute('transform', 'translate(' + lerp(3000, -700, cu) + ' 905) scale(1.1)');
  // Бүбү-апа идёт к двери и входит
  const g = EXT.apaG;
  const st = { add: {}, ov: {}, A: 'walk', B: 'walk', k: 1, m: 1, fA: 'walk', fB: 'walk', fk: 1, hide: [] };
  const x0 = 1480, x1 = doorC[0] + 8, u = E.inOutS(cl((t - .05) / 1.15));
  const x = lerp(x0, x1, u);
  st.phi = (x - x0) * 2 * Math.PI / 220;
  st.m = Math.min(1, 1 - seg(t, 1.1, .12));
  apaExt.g.style.display = '';
  const al = 1 - seg(t, 1.05, .2);
  apaExt.g.setAttribute('opacity', al.toFixed(3));
  applyActor(apaExt, st, x, 758, .56 * (1 - .12 * seg(t, .9, .35)), t, al > .01);
}

/* ----- титры, субтитры, финальная карточка ----- */
const PL = [];
function addPlate(t0, d, htmlRu, htmlKy, o) {
  const p = div('plate', null, '<i></i><b></b>');
  p.querySelector('b').innerHTML = LANG === 'ky' ? htmlKy : htmlRu;
  p.style.fontSize = (PORTRAIT ? 8.6 : 6.4) * VM + 'px';
  p.style.top = (PORTRAIT ? .1 : .075) * H + 'px';
  PL.push(Object.assign({ el: p, t0, d }, o || {}));
  p.style.opacity = 0;
  return p;
}
if (VAR === 'full') {
  addPlate(3.3, 1.35, 'КАК <em>РАНЬШЕ</em>', 'МУРДА <em>КАНДАЙ</em> БОЛЧУ');
  addPlate(9.15, 2.6, 'Касса <em>умерла</em>', 'Касса <em>өлдү</em>');
  addPlate(12.15, 2.7, 'Лепёшка — <em>30? 35?</em>', 'Нан — <em>30? 35?</em>');
  addPlate(15.25, 2.7, 'Люди <em>ушли</em>', 'Адамдар <em>кетти</em>');
  addPlate(19.5, 1.25, 'С <em>NURCRM</em>', 'NURCRM <em>МЕНЕН</em>');
  addPlate(22.25, 2.5, 'Телефон <em>= касса</em>', 'Телефон <em>= касса</em>');
  addPlate(28.25, 1.75, 'Чек. Сдача. <em>Дальше</em>', 'Чек. Кайтарым. <em>Андан ары</em>');
  addPlate(32.15, 1.8, 'Тот же вечер — <em>без паники</em>', 'Ошол эле кеч — <em>паникасыз</em>');
  // «Пик. Пик. Пик.» — слова по ударам
  const pw = addPlate(25.0, 2.9, '<span class="w">Пик.</span> <span class="w">Пик.</span> <span class="w">Пик.</span>', '<span class="w">Пик.</span> <span class="w">Пик.</span> <span class="w">Пик.</span>', { words: T.piks });
} else {
  addPlate(0.35, 2.6, 'Свет <em>погас?</em>', 'Жарык <em>өчтүбү?</em>');
  addPlate(3.6, 3.0, 'Телефон <em>= касса</em>', 'Телефон <em>= касса</em>');
  addPlate(7.0, 3.5, '<span class="w">Пик.</span> <span class="w">Пик.</span> <span class="w">Пик.</span>', '<span class="w">Пик.</span> <span class="w">Пик.</span> <span class="w">Пик.</span>', { words: T.piks });
}
const WIPES = VAR === 'full' ? [3.0, 19.0, 34.0] : [10.8];
const END_T = VAR === 'full' ? 34.0 : 10.8;
const VOICE = V.voice;

function renderPlates(t) {
  PL.forEach(p => {
    const u = pulse(t, p.t0, p.d, .3, .28);
    p.el.style.opacity = u;
    p.el.style.transform = 'translate(-50%,' + ((1 - u) * -2.2 * VM) + 'px)';
    if (p.words) {
      [...p.el.querySelectorAll('.w')].forEach((w, i) => {
        const a = cl((t - p.words[i]) / .12);
        w.style.display = 'inline-block'; w.style.opacity = a; w.style.transform = 'scale(' + (.7 + .3 * E.back(a)).toFixed(3) + ')';
        w.style.color = a > 0 && t - p.words[i] < .35 ? '#FFB208' : '#fff';
      });
    }
  });
}
function renderWipe(t) {
  let x = -101;
  WIPES.forEach(w => { const u = (t - w) / .5; if (u >= 0 && u <= 1) x = lerp(-101, 101, E.inOutS(u)); });
  wipe.style.transform = 'translateX(' + x + '%)';
}
function renderSubs(t) {
  let cur = null;
  for (const v of VOICE) if (t >= v.at - .04 && t < v.at + (v.dur || 1.5) + .25) cur = v;
  if (!cur) { subsEl.style.opacity = 0; return; }
  const who = TL.who[cur.who][LANG];
  const txt = cur[LANG] || cur.ru;
  subsEl.innerHTML = (who ? '<span class="who">' + who + '</span>' : '') + txt;
  subsEl.style.fontSize = (PORTRAIT ? 4.5 : 4.0) * VM + 'px';
  subsEl.style.bottom = (PORTRAIT ? .125 : .06) * H + 'px';
  subsEl.style.maxWidth = (PORTRAIT ? 90 : 78) + '%';
  subsEl.style.opacity = Math.min(cl((t - cur.at + .04) / .12), cl((cur.at + (cur.dur || 1.5) + .25 - t) / .15));
}

/* финальная карточка */
const END = div('', null); END.id = 'end'; UI.insertBefore(END, subsEl); END.style.display = 'none';
(function () {
  const lg = src('logo').cloneNode(true); lg.removeAttribute('id'); lg.setAttribute('width', 400); lg.setAttribute('height', 400); lg.style.position = 'absolute';
  lg.querySelectorAll('[id]').forEach(e => e.removeAttribute('id'));
  END.appendChild(lg);
  END.logo = lg;
  const m = [LANG === 'ky' ? ['NurCRM —', 'дүкөнүңүздү <span class="y">билген</span> касса'] : ['NurCRM — касса,', '<span class="y">которая знает</span> ваш магазин']][0];
  END.t1 = div('t1', END, m[0]); END.t2 = div('t2', END, m[1]);
  END.sub = div('sub', END, L('Работает без интернета. Говорит на вашем языке. Считает вашу прибыль.', 'Интернетсиз иштейт. Сиздин тилде сүйлөйт. Пайдаңызды эсептейт.'));
  END.links = div('links', END, L('Установить: app.nurcrm.kg', 'Орнотуу: app.nurcrm.kg'));
  if (PORTRAIT) END.links.innerHTML = L('Установить:', 'Орнотуу:') + '<br>app.nurcrm.kg';
  END.qrs = div('qrs', END);
  const q2 = div('qr', END.qrs, window.QR.app + '<div>app.nurcrm.kg</div>'); // 2026-10-29: только app.nurcrm.kg (GitHub убран по просьбе владельца)
  END.q = [q2];
  const u = VM;
  const qs = (PORTRAIT ? 19 : 16.5) * u;
  [q2].forEach(q => { q.querySelector('svg').setAttribute('width', qs); q.querySelector('svg').setAttribute('height', qs); q.style.fontSize = 1.9 * u + 'px'; });
  if (!PORTRAIT) {
    lg.style.width = lg.style.height = 21 * u + 'px'; lg.style.left = (W - 21 * u) / 2 + 'px'; lg.style.top = 2 * u + 'px';
    END.t1.style.top = 24.5 * u + 'px'; END.t1.style.fontSize = 8.6 * u + 'px';
    END.t2.style.top = 35.5 * u + 'px'; END.t2.style.fontSize = 8.6 * u + 'px';
    END.sub.style.top = 48.5 * u + 'px'; END.sub.style.fontSize = 3.3 * u + 'px'; END.sub.style.fontWeight = 600; END.sub.style.color = '#d8d8d8';
    END.links.style.fontSize = 3.0 * u + 'px'; END.links.style.padding = '2.2vmin 3.4vmin';
    END.links.style.top = 66 * u + 'px'; END.qrs.style.left = (W - qs - 12 * u) + 'px'; END.qrs.style.top = 58.5 * u + 'px';
    END.links.style.transform = 'none'; END.links.style.left = '8vmin'; END.links.style.right = '';
  } else {
    lg.style.width = lg.style.height = 30 * u + 'px'; lg.style.left = (W - 30 * u) / 2 + 'px'; lg.style.top = 9 * u + 'px';
    END.t1.style.top = 46 * u + 'px'; END.t1.style.fontSize = 8.9 * u + 'px';
    END.t2.style.top = 57 * u + 'px'; END.t2.style.fontSize = 8.9 * u + 'px'; END.t2.style.padding = '0 3vmin';
    END.sub.style.top = 80 * u + 'px'; END.sub.style.fontSize = 3.9 * u + 'px'; END.sub.style.fontWeight = 600; END.sub.style.color = '#d8d8d8'; END.sub.style.padding = '0 8vmin';
    END.links.style.fontSize = 4.1 * u + 'px'; END.links.style.padding = '2.2vmin 3vmin';
    END.links.style.left = '5vmin'; END.links.style.right = '5vmin'; END.links.style.transform = 'none';
    END.links.style.top = 95 * u + 'px'; END.qrs.style.left = (W - qs) / 2 + 'px'; END.qrs.style.top = 113 * u + 'px'; // 2026-10-29: выше, чтобы субтитр не налезал на QR
  }
})();
function renderEnd(t) {
  const t0 = END_T + .24;
  if (t < t0) { END.style.display = 'none'; return; }
  END.style.display = '';
  const a = t - t0;
  const lp = E.out3(cl(a / .5));
  END.logo.style.opacity = lp; END.logo.style.transform = 'scale(' + (.96 + .04 * lp + (a > .25 && a < .55 ? .05 * Math.sin(Math.PI * (a - .25) / .3) : 0)).toFixed(4) + ')';
  const l1 = cl((a - .45) / .22), l2 = cl((a - .57) / .22), l3 = cl((a - .95) / .4);
  END.t1.style.opacity = l1; END.t1.style.transform = 'translateY(' + (1 - E.out3(l1)) * 3 * VM + 'px)';
  END.t2.style.opacity = l2; END.t2.style.transform = 'translateY(' + (1 - E.out3(l2)) * 3 * VM + 'px)';
  END.sub.style.opacity = l3;
  const lt = VAR === 'full' ? 38.0 : 12.9, qt = VAR === 'full' ? 38.45 : 13.5;
  const lk = E.out3(cl((t - lt) / .4)), qk = cl((t - qt) / .3);
  END.links.style.opacity = lk; END.links.style.transform = 'translateY(' + (1 - lk) * 14 * VM + 'px)';
  END.q.forEach((q, i) => { const k = cl((t - qt - i * .12) / .3); q.style.opacity = k; q.style.transform = 'scale(' + (.85 + .15 * E.back(k)).toFixed(3) + ')'; });
  // финальный «белый» удар на логотипе
  const hit = VAR === 'full' ? 34.55 : 11.0;
  const f = t >= hit && t < hit + .25 ? .18 * (1 - (t - hit) / .25) : 0;
  END.style.background = 'rgb(' + Math.round(25 + 230 * f) + ',' + Math.round(25 + 230 * f) + ',' + Math.round(25 + 200 * f) + ')';
}

/* ---------------------------------------------------------------- главный рендер */
window.__render = function (t) {
  const { act, tau, rewind } = actOf(t);
  const cam = camera(t, tau, act);
  gInt.style.display = '';
  renderInterior(t, tau, act, cam);
  renderLight(t, tau, act, cam);
  renderHud(t, tau, act, cam);
  renderExt(t);
  // перемотка: дрожание и белые полосы
  let sh = 0;
  if (rewind) {
    const u = (t - T.rewind[0]) / (T.rewind[1] - T.rewind[0]);
    sh = (hash(Math.floor(t * 30)) - .5) * 12 * Math.sin(Math.PI * cl(u));
  }
  svg.style.transform = 'translate(' + sh.toFixed(2) + 'px,' + (rewind ? (hash(Math.floor(t * 30) + 7) - .5) * 6 : 0).toFixed(2) + 'px)';
  drawRewindBars(t, rewind);
  renderPlates(t);
  renderWipe(t);
  renderSubs(t);
  renderEnd(t);
};
const bars = [0, 1].map(() => { const d = div('', null); d.style.cssText = 'position:absolute;left:0;right:0;height:.9vmin;background:rgba(255,255,255,.55);display:none;z-index:20'; return d; });
function drawRewindBars(t, rewind) {
  bars.forEach((b, i) => {
    if (!rewind) { b.style.display = 'none'; return; }
    b.style.display = ''; b.style.top = ((hash(Math.floor(t * 30) + i * 13) * .9 + .05) * H) + 'px';
    b.style.opacity = i ? .35 : .6;
  });
}
window.__ready = Promise.resolve();
})();
