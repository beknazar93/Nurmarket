/* 2026-10-30: движок роликов-фишек NurCRM №9–15 (1920x1080). Всё — функции времени: window.__render(t).
   Данные: window.SPEC (сценарий + озвучка с длительностями), window.FRAMES (кадры видеофрагментов), window.QR.
   Сцены: stage (персонажи Ad-Light + реплики), shots (реальные снимки/кадры стенда с рамками, зумом, подсветкой кликов),
   diagram (честные схемы: guard14, sync13, flow12), final (финальная плашка 4 с). Поверх — заставка 1,5 с, титры, субтитры, протирания. */
(function () {
'use strict';
const SP = window.SPEC, FR = window.FRAMES || {};
const W = 1920, H = 1080, NS = 'http://www.w3.org/2000/svg';
const Y = '#FFB208', D = '#191919';
const cl = (x, a = 0, b = 1) => Math.min(b, Math.max(a, x));
const lerp = (a, b, k) => a + (b - a) * k;
const E = { lin: u => u, out: u => 1 - Math.pow(1 - u, 3), in: u => u * u * u,
  io: u => (u < .5 ? 4 * u * u * u : 1 - Math.pow(-2 * u + 2, 3) / 2),
  back: u => { const c1 = 1.70158, c3 = c1 + 1; return 1 + c3 * Math.pow(u - 1, 3) + c1 * Math.pow(u - 1, 2); } };
const seg = (t, t0, d, f) => (f || E.out)(cl((t - t0) / Math.max(d, 1e-6)));
const pulse = (t, t0, d, fi = .2, fo = .2) => Math.min(seg(t, t0, fi, E.out), 1 - seg(t, t0 + d - fo, fo, E.in));
const hy = s => String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/\[\[(.+?)\]\]/g, '<span class="y">$1</span>');
function $(tag, cls, parent, html) {
  const e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html;
  (parent || document.getElementById('app')).appendChild(e); return e;
}
function mk(tag, attrs, parent) {
  const e = document.createElementNS(NS, tag);
  if (attrs) for (const k in attrs) e.setAttribute(k, attrs[k]);
  if (parent) parent.appendChild(e); return e;
}
function kf(t, keys) { // keys: [[t,v],...] -> значение с плавностью io
  if (!keys || !keys.length) return 0;
  if (t <= keys[0][0]) return keys[0][1];
  for (let i = 1; i < keys.length; i++) if (t <= keys[i][0]) {
    const a = keys[i - 1], b = keys[i]; return lerp(a[1], b[1], E.io(cl((t - a[0]) / Math.max(1e-6, b[0] - a[0]))));
  }
  return keys[keys.length - 1][1];
}
const src = id => document.getElementById('src-' + id);
function cloneOf(file, gid) {
  const o = src(file).querySelector('[id="' + gid + '"]');
  if (!o) throw new Error('нет группы ' + file + '#' + gid);
  const bb = o.getBBox(), c = o.cloneNode(true);
  [c, ...c.querySelectorAll('[id]')].forEach(e => { if (e.id) e.removeAttribute('id'); });
  return { el: c, bb };
}
function putCenter(parent, c, cx, cy, sc) {
  sc = sc || 1; const g = mk('g', { transform: 'translate(' + (cx - (c.bb.x + c.bb.width / 2) * sc) + ' ' + (cy - (c.bb.y + c.bb.height / 2) * sc) + ') scale(' + sc + ')' }, parent);
  g.appendChild(c.el); return g;
}

/* ---------------------------------------------------------------- тайминг сцен */
let T0 = 0;
SP.scenes.forEach(sc => { sc.start = T0; T0 += sc.dur; });
const TOTAL = T0;
const app = document.getElementById('app');
const layers = []; // {sc, el, update}
const ON = (cond) => (cond ? 'visible' : 'hidden');

/* ---------------------------------------------------------------- титры шагов */
function makeTitrs(sc, el) {
  const arr = (sc.titr || []).map(tt => {
    const d = $('div', 'titr', el, hy(tt.seq ? tt.seq[0] : tt.text)); d.style.opacity = 0; return { d, at: tt.at, dur: tt.dur || 2.6, tt };
  });
  return t => arr.forEach(a => {
    if (a.tt.seq) { const k = cl(Math.floor((t - a.at) / a.tt.step), 0, a.tt.seq.length - 1); if (a.d.dataset.k !== String(k)) { a.d.dataset.k = String(k); a.d.innerHTML = hy(a.tt.seq[k]); } }
    const p = pulse(t, a.at, a.dur, .16, .16); a.d.style.opacity = p; a.d.style.transform = 'translateY(' + ((1 - p) * 24) + 'px)';
  });
}

/* ---------------------------------------------------------------- STAGE */
function buildStage(sc, el) {
  const svg = mk('svg', { viewBox: '0 0 ' + W + ' ' + H, width: W, height: H }, el); svg.style.position = 'absolute'; svg.style.left = 0; svg.style.top = 0;
  const wall = sc.wall || '#FFF1DB';
  mk('rect', { x: 0, y: 0, width: W, height: 720, fill: wall }, svg);
  mk('rect', { x: 0, y: 720, width: W, height: 360, fill: sc.floor || '#E8D8BB' }, svg);
  mk('rect', { x: 0, y: 712, width: W, height: 16, fill: '#D3BE98' }, svg);
  const far = mk('g', null, svg);
  const decor = sc.decor || [['far-window', 330, 330, 1.05], ['far-sign', 1180, 150, 1.0], ['far-clock', 1560, 170, 1.1], ['far-poster', 1760, 400, 1.0]];
  decor.forEach(d => {
    const c = cloneOf('bg-wall', d[0]);
    [...c.el.querySelectorAll('text')].forEach(tx => { if (/БЕРЕКЕ/.test(tx.textContent)) tx.textContent = 'НУР'; });
    putCenter(far, c, d[1], d[2], d[3]);
  });
  const gBack = mk('g', null, svg), gCounter = mk('g', null, svg), gFront = mk('g', null, svg);
  if (sc.counter) {
    const c = cloneOf('bg-counter', 'counter'); putCenter(gCounter, c, sc.counter.x, sc.counter.y, sc.counter.s || 1.2);
    (sc.counter.extra || []).forEach(id => { const e = cloneOf('bg-counter', id); putCenter(gCounter, e, sc.counter.x + (e.bb.x + e.bb.width / 2 - c.bb.x - c.bb.width / 2) * (sc.counter.s || 1.2), sc.counter.y + (e.bb.y + e.bb.height / 2 - c.bb.y - c.bb.height / 2) * (sc.counter.s || 1.2), sc.counter.s || 1.2); });
  }
  const upd = [];
  // реквизит
  (sc.props || []).forEach(p => {
    const host = mk('g', null, p.z === 'back' ? gBack : gFront);
    let c;
    if (p.id === 'fridge') c = drawFridge(); else if (p.id === 'crate') c = drawCrate(); else if (p.id === 'laptop') c = drawLaptop(); else if (p.id === 'stack') c = drawStack(p.n); else c = cloneOf(p.file || 'props', p.id);
    const g = putCenter(host, c, 0, 0, 1);
    upd.push(t => {
      const x = p.path ? kf(t, p.path.map(k => [k[0], k[1]])) : p.x, y = p.path ? kf(t, p.path.map(k => [k[0], k[2]])) : p.y;
      const sc2 = p.s || 1, rot = p.rot ? kf(t, p.rot) : 0, shake = p.shake && t >= p.shake[0] && t <= p.shake[1] ? Math.sin(t * 60) * 2 : 0;
      host.setAttribute('transform', 'translate(' + (x + shake) + ' ' + y + ') rotate(' + rot + ') scale(' + sc2 + ')');
      const vis = (p.in == null || t >= p.in) && (p.out == null || t < p.out);
      host.setAttribute('visibility', vis ? 'visible' : 'hidden');
    });
  });
  // актёры
  const actors = {};
  (sc.actors || []).forEach((a, ai) => {
    const host = mk('g', null, a.z === 'back' ? gBack : gFront);
    const names = [...new Set((a.poses || [[0, 'stand']]).map(p => p[1]))];
    const pg = {};
    names.forEach(n => {
      const c = cloneOf(a.who, a.who + '-' + n);
      [...c.el.querySelectorAll('text')].forEach(tx => { if (/БЕРЕКЕ/.test(tx.textContent)) tx.textContent = 'НУР'; });
      const w = mk('g', null, host); w.appendChild(c.el); w.setAttribute('visibility', 'hidden'); pg[n] = w;
    });
    const o = { a, host, pg, x: t => (a.path ? kf(t, a.path) : a.x) };
    actors[a.id || a.who] = o;
    upd.push(t => {
      const poses = a.poses || [[0, 'stand']]; let cur = poses[0][1], t0 = poses[0][0];
      for (const p of poses) if (t >= p[0]) { cur = p[1]; t0 = p[0]; }
      for (const n in pg) pg[n].setAttribute('visibility', n === cur ? 'visible' : 'hidden');
      const sc2 = a.s || 1.0, x = o.x(t), g0 = a.y || 975;
      let dy = 0;
      if (cur === 'stand' || cur === 'sad') dy = Math.sin(t * 6.3 + ai) * 2.5;
      else if (cur === 'happy') dy = -Math.abs(Math.sin((t - t0) * 8)) * 16 * (1 - cl((t - t0) / 1.6));
      else if (cur === 'walk') dy = -Math.abs(Math.sin(t * 9 + ai)) * 7;
      else if (cur === 'angry') dy = Math.sin(t * 22) * 1.8;
      const flip = a.flip ? -1 : 1;
      host.setAttribute('transform', 'translate(' + (x - 200 * sc2 * flip) + ' ' + (g0 - 688 * sc2 + dy) + ') scale(' + (sc2 * flip) + ' ' + sc2 + ')');
      host.setAttribute('visibility', ((a.in == null || t >= a.in) && (a.out == null || t < a.out)) ? 'visible' : 'hidden');
    });
  });
  // пузыри реплик (всё, что говорят не за кадром)
  (sc.say || []).filter(l => l.who !== 'N' && (l.anchor || (l.actor && actors[l.actor])) && l.show !== false).forEach(l => {
    const o = l.anchor ? { a: { s: 1, y: l.anchor[1] + 700 }, x: () => l.anchor[0] } : actors[l.actor], sc2 = o.a.s || 1.0, g0 = o.a.y || 975;
    const b = $('div', 'bub' + (l.anchor ? ' call' : ''), el, hy(l.text)); b.style.opacity = 0;
    const bw = b.offsetWidth;
    const t0 = l.at - 0.05, dur = l.dur + 0.45;
    upd.push(t => {
      const p = pulse(t, t0, dur, .14, .2);
      b.style.opacity = p; if (p <= 0) return;
      let x = o.x(t); x = cl(x, 24 + bw / 2, W - 24 - bw / 2);
      b.style.left = x + 'px'; b.style.top = (g0 - 700 * sc2 - 12 - (l.lift || 0)) + 'px';
      b.style.transform = 'translate(-50%,-100%) scale(' + (.85 + .15 * p) + ')';
      const tail = b.querySelector('i'); if (tail) tail.style.left = (50 + (o.x(t) - x) / bw * 100) + '%';
    });
    b.insertAdjacentHTML('beforeend', '<i></i>');
  });
  upd.push(makeChips(sc, el));
  return t => upd.forEach(f => f(t));
}
function makeChips(sc, el) { // выноски-значки (клавиши, подписи)
  const arr = (sc.chips || []).map(c => {
    const d = $('div', 'chip', el, hy(c.text)); d.style.left = c.x + 'px'; d.style.top = c.y + 'px'; d.style.opacity = 0;
    if (c.bg) d.style.background = c.bg; if (c.fg) d.style.color = c.fg; if (c.size) d.style.fontSize = c.size + 'px';
    return { c, d };
  });
  return t => arr.forEach(({ c, d }) => { const p = pulse(t, c.at, c.dur || 1.6, .15, .15); d.style.opacity = p; d.style.transform = 'translate(-50%,-50%) scale(' + (.8 + .2 * p) + ')'; });
}
function drawFridge() {
  const g = mk('g'); const k = { fill: '#E3EEF4', stroke: D, 'stroke-width': 6, 'stroke-linejoin': 'round' };
  mk('rect', Object.assign({ x: -130, y: -300, width: 260, height: 600, rx: 20 }, k), g);
  mk('rect', { x: -108, y: -270, width: 216, height: 360, rx: 10, fill: '#BFE6F4', stroke: D, 'stroke-width': 5 }, g);
  const cols = ['#E5484D', '#2F8F5B', '#FFB208', '#2F5FD0', '#fff'];
  for (let r = 0; r < 3; r++) for (let c = 0; c < 4; c++) mk('rect', { x: -96 + c * 52, y: -250 + r * 115 + 54, width: 40, height: 44, rx: 6, fill: cols[(r + c) % 5], stroke: D, 'stroke-width': 4 }, g);
  [-165, -50, 65].forEach(y => mk('rect', { x: -108, y: y + 148 - 100 + 52, width: 216, height: 5, fill: D }, g));
  mk('rect', { x: 100, y: -200, width: 12, height: 150, rx: 6, fill: D }, g);
  mk('text', { x: 0, y: 200, 'text-anchor': 'middle', 'font-family': 'Montserrat', 'font-size': 34, 'font-weight': 800, fill: D }, g).textContent = 'ВИТРИНА';
  mk('rect', { x: -60, y: 225, width: 120, height: 26, rx: 6, fill: Y, stroke: D, 'stroke-width': 4 }, g);
  return { el: g, bb: { x: -130, y: -300, width: 260, height: 600 } };
}
function drawCrate() {
  const g = mk('g'); mk('rect', { x: -110, y: -50, width: 220, height: 100, rx: 8, fill: '#C79A5E', stroke: D, 'stroke-width': 6 }, g);
  [-20, 15].forEach(y => mk('rect', { x: -110, y: y, width: 220, height: 6, fill: D }, g));
  [-55, 0, 55].forEach(x => mk('circle', { cx: x, cy: -62, r: 26, fill: '#D6453D', stroke: D, 'stroke-width': 5 }, g));
  return { el: g, bb: { x: -110, y: -90, width: 220, height: 140 } };
}
function drawStack(n) { // 2026-10-30: стопка бумажных накладных (ролик 20)
  const g = mk('g'); n = n || 13;
  for (let i = 0; i < n; i++) mk('rect', { x: (i % 3) * 7 - 110, y: 40 - i * 19, width: 226, height: 30, rx: 3, fill: '#fff', stroke: D, 'stroke-width': 4 }, g);
  return { el: g, bb: { x: -110, y: 40 - (n - 1) * 19, width: 240, height: 30 + (n - 1) * 19 } };
}
function drawLaptop() {
  const g = mk('g'); mk('rect', { x: -90, y: -120, width: 180, height: 116, rx: 10, fill: '#222', stroke: D, 'stroke-width': 6 }, g);
  mk('rect', { x: -78, y: -108, width: 156, height: 92, rx: 4, fill: '#FFF7E3' }, g);
  mk('path', { d: 'M-120 -4 L120 -4 L100 18 L-100 18 Z', fill: '#BDBDBD', stroke: D, 'stroke-width': 6, 'stroke-linejoin': 'round' }, g);
  return { el: g, bb: { x: -120, y: -120, width: 240, height: 140 } };
}

/* ---------------------------------------------------------------- SHOTS */
const MAIN = [200, 26, 1520, 826];
function buildShots(sc, el) {
  el.style.background = 'radial-gradient(ellipse at 50% 38%, #2a2a2a 0%, #191919 70%)';
  const views = (sc.views || []).map(v => makeView(v, el));
  const tit = makeTitrs(sc, el), chips = makeChips(sc, el);
  return t => { views.forEach(v => v.update(t)); tit(t); chips(t); };
}
function makeView(v, parent) {
  const area = v.area || MAIN;
  const wrap = $('div', 'vwrap', parent); wrap.style.visibility = 'hidden';
  const box = $('div', 'vbox', wrap); const inner = $('div', 'vinner', box);
  let iw, ih, frames = null, imgEl = null, fi = null;
  const ready = [];
  if (v.clip) {
    fi = FR[v.clip + '|' + (v.from || 0) + '|' + v.len];
    frames = fi.files.map(f => { const im = new Image(); im.src = 'file:///' + f.replace(/\\/g, '/'); im.style.cssText = 'position:absolute;left:0;top:0;display:none'; inner.appendChild(im); ready.push(im.decode().catch(() => {})); return im; });
    iw = fi.w; ih = fi.h; frames.forEach(im => { im.style.width = iw + 'px'; im.style.height = ih + 'px'; });
  } else if (v.src) {
    imgEl = new Image(); imgEl.src = 'file:///' + SP.shotsDir.replace(/\\/g, '/') + '/' + v.src; imgEl.style.cssText = 'position:absolute;left:0;top:0';
    inner.appendChild(imgEl); ready.push(imgEl.decode().catch(() => {}));
    iw = v.w; ih = v.h; // естественный размер задаёт сборщик
    imgEl.style.width = iw + 'px'; imgEl.style.height = ih + 'px';
  } else if (v.placeholder) {
    iw = 1600; ih = 1000; const ph = $('div', 'ph', inner, '<div class="ph-logo"></div><div class="ph-t">NurCRM · Касса</div><div class="ph-s">' + hy(v.placeholder) + '</div>');
    ph.style.cssText = 'position:absolute;left:0;top:0;width:1600px;height:1000px';
    ph.querySelector('.ph-logo').appendChild(logoSvg());
  }
  inner.style.width = iw + 'px'; inner.style.height = ih + 'px';
  window.__ready = Promise.all([window.__ready || Promise.resolve(), ...ready]);
  // плашки (закрывают область) и метки
  (v.plates || []).forEach(p => { const d = $('div', 'plate', inner, hy(p.text || '')); d.style.cssText += ';left:' + p.rect[0] * iw + 'px;top:' + p.rect[1] * ih + 'px;width:' + p.rect[2] * iw + 'px;height:' + p.rect[3] * ih + 'px;font-size:' + (p.rect[3] * ih * .5) + 'px'; });
  const marks = (v.marks || []).map(m => { const d = $('div', 'mark', inner); d.style.cssText += ';left:' + m.rect[0] * iw + 'px;top:' + m.rect[1] * ih + 'px;width:' + m.rect[2] * iw + 'px;height:' + m.rect[3] * ih + 'px'; return { m, d }; });
  let cur = null;
  if (v.cursor) {
    cur = $('div', 'cursor', inner, '<svg viewBox="0 0 24 30" width="44" height="54"><path d="M2 2 L2 24 L8 19 L12 28 L16 26 L12 17 L20 17 Z" fill="#fff" stroke="#191919" stroke-width="2" stroke-linejoin="round"/></svg><span>' + hy(v.cursor.label || '') + '</span>');
  }
  const moves = [{ at: -1e9, dur: 0.001, crop: v.crop || [0, 0, 1, 1] }].concat(v.moves || []);
  function cropAt(t) {
    let prev = moves[0].crop;
    for (let i = 1; i < moves.length; i++) {
      const m = moves[i]; if (t < m.at) break;
      const p = E.io(cl((t - m.at) / (m.dur || .9))); const c = prev.map((x, k) => lerp(x, m.crop[k], p));
      if (p < 1) return c; prev = m.crop;
    }
    return prev;
  }
  const dev = v.frame || null; let stand = null;
  if (dev) { box.classList.add('dev-' + dev); stand = $('div', 'dstand dstand-' + dev, wrap); }
  const at = v.at || 0, dur = v.dur;
  function update(t) {
    const ph = t - at, vis = ph >= -0.001 && ph < dur;
    wrap.style.visibility = ON(vis); if (!vis) return;
    const c = cropAt(ph), asp = (c[2] * iw) / (c[3] * ih);
    const pad = dev === 'tablet' ? 28 : dev === 'phone' ? 20 : dev ? 14 : 0;
    const ax = area[0], ay = area[1], aw = area[2] - 2 * pad, ah = area[3] - 2 * pad - (dev === 'monitor' ? 50 : dev === 'laptop' ? 30 : 0);
    let bw = aw, bh = bw / asp; if (bh > ah) { bh = ah; bw = bh * asp; }
    const s = bw / (c[2] * iw);
    const slide = (v.slide || 0) * (1 - seg(ph, 0, .3)), z = 1.04 - .04 * seg(ph, 0, .3);
    const bx = ax + pad + (aw - bw) / 2 + slide, by = ay + pad + (ah - bh) / 2;
    Object.assign(box.style, { left: bx + 'px', top: by + 'px', width: bw + 'px', height: bh + 'px' });
    box.style.transform = 'scale(' + z + ')';
    inner.style.transform = 'translate(' + (-c[0] * iw * s) + 'px,' + (-c[1] * ih * s) + 'px) scale(' + s + ')';
    wrap.style.opacity = Math.min(seg(ph, 0, .2), 1 - seg(ph, dur - .2, .2, E.in));
    if (stand) { stand.style.left = (bx + bw / 2) + 'px'; stand.style.top = (by + bh + pad) + 'px'; }
    if (frames) { const k = Math.min(frames.length - 1, Math.max(0, Math.floor(ph * fi.fps * (v.speed || 1)))); frames.forEach((im, i) => { im.style.display = i === k ? 'block' : 'none'; }); }
    marks.forEach(({ m, d }) => {
      const mt = t - m.at, md = m.dur || (m.kind === 'fill' ? .9 : .68);
      let a = 0, sc2 = 1;
      if (mt >= 0 && mt < md) { a = m.kind === 'hold' ? 1 : Math.min(seg(mt, 0, .12), 1 - seg(mt, md - .16, .16, E.in)); sc2 = 1.08 - .08 * seg(mt, 0, .12); }
      d.style.opacity = a;
      const bwid = 3 / s;
      if (m.kind === 'fill') { d.style.background = 'rgba(255,178,8,' + (.3 * a) + ')'; d.style.border = bwid + 'px solid rgba(255,178,8,' + a * .9 + ')'; }
      else { d.style.background = 'transparent'; d.style.border = bwid + 'px solid ' + Y; }
      d.style.borderRadius = (8 / s) + 'px'; d.style.transform = 'scale(' + sc2 + ')';
    });
    if (cur) {
      const cp = v.cursor.path, ct = t - at;
      const fx = kf(ct, cp.map(k => [k[0], k[1]])), fy = kf(ct, cp.map(k => [k[0], k[2]]));
      cur.style.left = fx * iw + 'px'; cur.style.top = fy * ih + 'px'; cur.style.transform = 'scale(' + (1 / s) + ')';
      cur.style.opacity = pulse(ct, v.cursor.at || 0, v.cursor.dur || 4, .3, .3);
    }
  }
  return { update };
}
function logoSvg(sz) {
  const c = cloneOf('logo', 'logo-mark'); const s = mk('svg', { viewBox: '0 0 400 400', width: sz || 220, height: sz || 220 }); s.appendChild(c.el); return s;
}

/* ---------------------------------------------------------------- DIAGRAM */
function buildDiagram(sc, el) {
  el.style.background = 'radial-gradient(ellipse at 50% 38%, #2a2a2a 0%, #191919 70%)';
  const tit = makeTitrs(sc, el);
  const svg = mk('svg', { viewBox: '0 0 ' + W + ' ' + H, width: W, height: H }, el); svg.style.cssText = 'position:absolute;left:0;top:0';
  const f = { guard14: dGuard, sync13: dSync, flow12: dFlow, remind16: dRemind, boxes: dBoxes }[sc.kind](svg, sc);
  return t => { f(t); tit(t); };
}
const FONT = 'Montserrat, "Noto Sans", sans-serif';
function txt(parent, x, y, s, size, fill, weight, anchor) {
  const t = mk('text', { x, y, 'font-family': FONT, 'font-size': size, 'font-weight': weight || 700, fill: fill || '#fff', 'text-anchor': anchor || 'middle' }, parent); t.textContent = s; return t;
}
function pill(parent, cx, cy, w, h, label, bg, fg, size) {
  const g = mk('g', null, parent); mk('rect', { x: cx - w / 2, y: cy - h / 2, width: w, height: h, rx: h / 2, fill: bg }, g);
  const t = txt(g, cx, cy + (size || 34) * .36, label, size || 34, fg, 800); return { g, t };
}
function iconKassa(parent, cx, cy, s) { // монитор кассы + галочка
  const g = mk('g', { transform: 'translate(' + cx + ' ' + cy + ') scale(' + s + ')' }, parent);
  mk('rect', { x: -130, y: -100, width: 260, height: 170, rx: 16, fill: '#222', stroke: '#fff', 'stroke-width': 7 }, g);
  mk('rect', { x: -112, y: -82, width: 224, height: 134, rx: 6, fill: '#FFF7E3' }, g);
  [-60, -34, -8].forEach((y, i) => mk('rect', { x: -96, y, width: 120 - i * 14, height: 12, rx: 6, fill: '#C9C2B2' }, g));
  mk('rect', { x: 30, y: 6, width: 68, height: 34, rx: 8, fill: Y }, g);
  mk('path', { d: 'M-30 70 L-50 120 L50 120 L30 70 Z', fill: '#444' }, g); mk('rect', { x: -80, y: 118, width: 160, height: 12, rx: 6, fill: '#666' }, g);
  return g;
}
function iconSite(parent, cx, cy, s) { // окно браузера с глобусом
  const g = mk('g', { transform: 'translate(' + cx + ' ' + cy + ') scale(' + s + ')' }, parent);
  mk('rect', { x: -150, y: -105, width: 300, height: 210, rx: 18, fill: '#fff', stroke: '#fff', 'stroke-width': 6 }, g);
  mk('path', { d: 'M-150 -60 L150 -60 L150 -87 Q150 -105 132 -105 L-132 -105 Q-150 -105 -150 -87 Z', fill: Y }, g);
  [-128, -108, -88].forEach(x => mk('circle', { cx: x, cy: -82, r: 7, fill: D }, g));
  mk('circle', { cx: 0, cy: 12, r: 48, fill: 'none', stroke: D, 'stroke-width': 7 }, g);
  mk('ellipse', { cx: 0, cy: 12, rx: 20, ry: 48, fill: 'none', stroke: D, 'stroke-width': 6 }, g);
  mk('path', { d: 'M-48 12 L48 12 M-38 -14 L38 -14 M-38 38 L38 38', stroke: D, 'stroke-width': 6, fill: 'none' }, g);
  return g;
}
function arrow(parent, x1, y, x2, color) {
  const g = mk('g', null, parent); mk('line', { x1, y1: y, x2: x2 - 26, y2: y, stroke: color || Y, 'stroke-width': 10, 'stroke-linecap': 'round' }, g);
  mk('path', { d: 'M' + (x2 - 36) + ' ' + (y - 24) + ' L' + x2 + ' ' + y + ' L' + (x2 - 36) + ' ' + (y + 24), fill: 'none', stroke: color || Y, 'stroke-width': 10, 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }, g); return g;
}

/* 2026-10-30: общая схема из данных (ролики 18-20): boxes / arrows / pills / texts, каждый элемент — со своим «at» (появление). */
function dBoxes(svg, sc) {
  const els = [];
  const reg = (g, at) => { els.push({ g, at: at || 0 }); return g; };
  (sc.boxes || []).forEach(b => {
    const g = mk('g', null, svg);
    mk('rect', { x: b.x - b.w / 2, y: b.y - b.h / 2, width: b.w, height: b.h, rx: 26, fill: b.fill || '#fff', stroke: b.stroke || 'none', 'stroke-width': 6 }, g);
    const fg = b.fg || D; let ty = b.y - b.h / 2 + (b.tsize || 44) + 22;
    if (b.title) { txt(g, b.x, ty, b.title, b.tsize || 44, b.tfill || fg, 800); ty += (b.tsize || 44) * .5 + 30; }
    (b.lines || []).forEach((l, i) => { txt(g, b.x, ty + i * ((b.size || 34) + 12), l, b.size || 34, b.lfill || fg, 600); });
    reg(g, b.at);
  });
  (sc.arrows || []).forEach(a => reg(arrow(svg, a.x1, a.y, a.x2, a.color), a.at));
  (sc.pills || []).forEach(p => reg(pill(svg, p.x, p.y, p.w, p.h, p.text, p.bg || Y, p.fg || D, p.size || 40).g, p.at));
  (sc.texts || []).forEach(t => reg(txt(svg, t.x, t.y, t.text, t.size || 40, t.fill || '#fff', t.weight || 800, t.anchor), t.at));
  return t => els.forEach(e => { const p = seg(t, e.at, .4, E.back); e.g.style.opacity = cl(p * 1.3); e.g.style.transform = 'translateY(' + ((1 - Math.min(1, p)) * 16) + 'px)'; });
}
function dFlow(svg) { // 12: касса -> сайт; категории; продали последний -> «Нет в наличии»
  const gK = mk('g', null, svg), gS = mk('g', null, svg), gA = mk('g', null, svg);
  iconKassa(gK, 440, 420, 1.35); txt(gK, 440, 640, 'Касса', 44, '#fff', 800);
  iconSite(gS, 1480, 420, 1.35); txt(gS, 1480, 640, 'Сайт магазина', 44, '#fff', 800);
  arrow(gA, 700, 420, 1220, Y); const cap = txt(gA, 960, 372, 'цены и наличие', 38, '#fff', 700);
  const cats = ['Молочное', 'Хлеб', 'Напитки'].map((s, i) => pill(gS, 1250 + i * 230, 720, 210, 62, s, '#3a3a3a', '#fff', 30));
  const chipK = pill(gK, 440, 740, 470, 78, 'Адыгене 1,5 л · 1 шт', '#fff', D, 34);
  const chipS = pill(gS, 1480, 800, 560, 78, 'Адыгене 1,5 л — в наличии', '#2F8F5B', '#fff', 32);
  const sold = pill(gK, 440, 215, 420, 62, 'продали последний', Y, D, 30);
  const dot = mk('circle', { r: 17, fill: '#fff' }, gA);
  return t => {
    const a = seg(t, .1, .5); gK.style.opacity = a; gS.style.opacity = a; gA.style.opacity = a;
    cats.forEach((c, i) => { const p = seg(t, .9 + i * .25, .35, E.back); c.g.style.opacity = p; c.g.style.transform = 'translateY(' + ((1 - p) * 16) + 'px)'; });
    const showB = t > (sc_t.b0 || 3.3);
    chipK.g.style.opacity = seg(t, 3.2, .3); chipS.g.style.opacity = seg(t, 3.2, .3);
    sold.g.style.opacity = pulse(t, 4.0, 2.2, .2, .2);
    const sellT = 4.0; chipK.t.textContent = t >= sellT + .25 ? 'Адыгене 1,5 л · 0 шт' : 'Адыгене 1,5 л · 1 шт';
    const sc2 = seg(t, sellT + .3, .8, E.io);
    dot.setAttribute('cx', lerp(720, 1200, sc2)); dot.setAttribute('cy', 420); dot.style.opacity = t > sellT + .3 && sc2 < 1 ? 1 : 0;
    const gone = t >= sellT + 1.2; chipS.t.textContent = gone ? 'Адыгене 1,5 л — нет в наличии' : 'Адыгене 1,5 л — в наличии';
    chipS.g.firstChild.setAttribute('fill', gone ? '#6b6b6b' : '#2F8F5B');
    cap.style.opacity = t < 3.0 ? 1 : .4;
  };
}
const sc_t = {};
function dSync(svg) { // 13: продали на планшете -> остаток на компьютере
  const gT = mk('g', null, svg), gM = mk('g', null, svg), gA = mk('g', null, svg);
  const tab = mk('g', { transform: 'translate(430 420)' }, gT);
  mk('rect', { x: -190, y: -130, width: 380, height: 260, rx: 34, fill: '#222', stroke: '#fff', 'stroke-width': 8 }, tab);
  mk('rect', { x: -164, y: -104, width: 328, height: 208, rx: 12, fill: '#FFF7E3' }, tab);
  [-70, -40, -10].forEach((y, i) => mk('rect', { x: -140, y, width: 200 - i * 20, height: 14, rx: 7, fill: '#C9C2B2' }, tab));
  mk('rect', { x: 60, y: 40, width: 90, height: 40, rx: 10, fill: Y }, tab);
  txt(gT, 430, 640, 'Планшет', 44, '#fff', 800);
  iconKassa(gM, 1490, 420, 1.35); txt(gM, 1490, 640, 'Компьютер', 44, '#fff', 800);
  arrow(gA, 700, 420, 1220, '#fff'); txt(gA, 960, 372, 'синхронизация', 38, '#fff', 700);
  const pT = pill(gT, 430, 740, 380, 78, 'Сүт · 12 шт', '#fff', D, 36), pM = pill(gM, 1490, 740, 380, 78, 'Сүт · 12 шт', '#fff', D, 36);
  const sale = pill(gT, 430, 215, 300, 60, 'продажа −1', Y, D, 30);
  const dot = mk('circle', { r: 17, fill: Y }, gA);
  return t => {
    const a = seg(t, .1, .5); gT.style.opacity = a; gM.style.opacity = a; gA.style.opacity = a;
    sale.g.style.opacity = pulse(t, 1.0, 1.4, .15, .2);
    pT.t.textContent = t >= 1.3 ? 'Сүт · 11 шт' : 'Сүт · 12 шт';
    const mv = seg(t, 1.5, .7, E.io); dot.setAttribute('cx', lerp(720, 1200, mv)); dot.setAttribute('cy', 420); dot.style.opacity = t > 1.5 && mv < 1 ? 1 : 0;
    const done = t >= 2.3; pM.t.textContent = done ? 'Сүт · 11 шт' : 'Сүт · 12 шт';
    pM.g.firstChild.setAttribute('fill', done && t < 2.9 ? Y : '#fff'); pT.g.firstChild.setAttribute('fill', t >= 1.3 && t < 1.9 ? Y : '#fff');
  };
}
function dRemind(svg) { // 16: отметили должников -> WhatsApp с готовым текстом (схема, не снимок окна) -> «Отправить» нажимает владелец
  const g1 = mk('g', null, svg), g2 = mk('g', null, svg), g3 = mk('g', null, svg), gA = mk('g', null, svg), gB = mk('g', null, svg);
  const list = mk('g', { transform: 'translate(340 400)' }, g1);
  mk('rect', { x: -180, y: -170, width: 360, height: 340, rx: 24, fill: '#fff' }, list);
  ['Токтогул-ата', 'Бүбү-апа', 'Эрлан'].forEach((n, i) => {
    const y = -100 + i * 100;
    mk('rect', { x: -150, y: y - 26, width: 52, height: 52, rx: 10, fill: Y }, list);
    mk('path', { d: 'M-139 ' + y + ' L-128 ' + (y + 13) + ' L-109 ' + (y - 13), fill: 'none', stroke: D, 'stroke-width': 8, 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }, list);
    txt(list, -80, y + 12, n, 34, D, 800, 'start');
  });
  txt(g1, 340, 700, 'Отметили', 44, '#fff', 800);
  const bub = mk('g', { transform: 'translate(960 400)' }, g2);
  mk('rect', { x: -280, y: -175, width: 560, height: 350, rx: 28, fill: '#fff' }, bub);
  mk('path', { d: 'M-250 175 L-290 232 L-190 175 Z', fill: '#fff' }, bub);
  ['Здравствуйте, Токтогул-ата!', 'Это магазин «Нур». Напоминаем', 'о долге 118 000 сом (уже 41 дн.).', 'Пожалуйста, верните долг,', 'когда сможете. Спасибо!'].forEach((l, i) => txt(bub, -245, -90 + i * 52, l, 29, D, 600, 'start'));
  txt(g2, 960, 700, 'WhatsApp: готовый текст', 40, '#fff', 800);
  const btn = pill(g3, 1565, 400, 340, 112, 'Отправить', Y, D, 46);
  txt(g3, 1565, 700, 'Нажимаете вы', 44, '#fff', 800);
  txt(g3, 1565, 752, 'сама программа ничего не шлёт', 28, '#bdbdbd', 600);
  const a1 = arrow(gA, 545, 400, 665, Y), a2 = arrow(gB, 1255, 400, 1375, Y);
  return t => {
    g1.style.opacity = seg(t, .1, .4);
    a1.style.opacity = seg(t, .6, .3);
    const p2 = seg(t, 1.0, .45, E.back); g2.style.opacity = Math.min(1, p2); g2.style.transform = 'translateY(' + ((1 - p2) * 18) + 'px)';
    a2.style.opacity = seg(t, 1.9, .3);
    g3.style.opacity = seg(t, 2.2, .4);
    const pr = pulse(t, 2.85, .5, .1, .25);
    btn.g.firstChild.setAttribute('fill', pr > 0.05 ? '#fff' : Y);
    btn.g.style.transform = 'scale(' + (1 - .06 * pr) + ')'; btn.g.style.transformOrigin = '1565px 400px';
  };
}
function dGuard(svg) { // 14: Enter ×3 -> один чек; обрыв связи -> сверка с сервером -> дубля нет
  const g = mk('g', null, svg);
  const key = mk('g', { transform: 'translate(430 420)' }, g);
  const kb = mk('rect', { x: -170, y: -100, width: 340, height: 200, rx: 28, fill: '#E9E9E9', stroke: '#8a8a8a', 'stroke-width': 8 }, key);
  const kt = txt(key, 0, 22, 'Enter', 76, D, 800);
  const rec = mk('g', { transform: 'translate(1380 400)' }, g);
  mk('path', { d: 'M-170 -190 L170 -190 L170 190 L140 170 L110 190 L80 170 L50 190 L20 170 L-10 190 L-40 170 L-70 190 L-100 170 L-130 190 L-170 170 Z', fill: '#fff' }, rec);
  txt(rec, 0, -110, 'Чек № 128', 38, D, 800); txt(rec, 0, -50, '225 сом', 54, D, 800);
  const cnt = txt(rec, 0, 60, 'Продаж: 1', 44, '#2F8F5B', 800);
  const ok = txt(rec, 0, 130, 'повтор не принят', 28, '#777', 700);
  const hits = [0.7, 1.3, 1.9].map((t0, i) => pill(g, 430 + (i - 1) * 190, 640, 150, 56, '×' + (i + 1), '#3a3a3a', '#fff', 30));
  const arr = arrow(g, 640, 400, 1130, '#8a8a8a');
  // вторая часть
  const g2 = mk('g', null, svg);
  const wifi = mk('g', { transform: 'translate(430 400) scale(5.6)' }, g2);
  const stroke = { fill: 'none', stroke: '#fff', 'stroke-width': 1.6, 'stroke-linecap': 'round' };
  mk('path', Object.assign({ d: 'M2.2 9a15 15 0 0 1 19.6 0' }, stroke), wifi); mk('path', Object.assign({ d: 'M5.4 12.6a10.2 10.2 0 0 1 13.2 0' }, stroke), wifi);
  mk('path', Object.assign({ d: 'M8.7 16a5.3 5.3 0 0 1 6.6 0' }, stroke), wifi); mk('circle', { cx: 12, cy: 19.4, r: 1.3, fill: '#fff' }, wifi);
  mk('path', { d: 'M3.4 3.4 20.6 20.6', stroke: '#E5484D', 'stroke-width': 2.2, 'stroke-linecap': 'round' }, wifi);
  txt(g2, 430, 640, 'Связь оборвалась', 42, '#fff', 800);
  const srv = mk('g', { transform: 'translate(960 420)' }, g2);
  mk('rect', { x: -110, y: -120, width: 220, height: 240, rx: 18, fill: '#2a2a2a', stroke: '#fff', 'stroke-width': 7 }, srv);
  [-70, -10, 50].forEach(y => { mk('rect', { x: -84, y, width: 168, height: 38, rx: 8, fill: '#444' }, srv); mk('circle', { cx: 60, cy: y + 19, r: 7, fill: Y }, srv); });
  txt(g2, 960, 640, 'сервер', 42, '#fff', 800);
  const ver = pill(g2, 960, 740, 640, 70, 'касса сверилась с сервером', Y, D, 34);
  const chk = mk('g', { transform: 'translate(1480 420)' }, g2);
  mk('circle', { r: 130, fill: '#2F8F5B' }, chk); mk('path', { d: 'M-62 6 L-18 52 L66 -48', fill: 'none', stroke: '#fff', 'stroke-width': 24, 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }, chk);
  txt(g2, 1480, 640, 'дубля нет', 46, '#fff', 800);
  const arr2 = arrow(g2, 600, 420, 800, '#8a8a8a'), arr3 = arrow(g2, 1120, 420, 1300, Y);
  return t => {
    const A = pulse(t, 0, 3.6, .2, .3);
    g.style.opacity = A;
    kb.setAttribute('fill', [0.7, 1.3, 1.9].some(x => t >= x && t < x + .16) ? '#BDBDBD' : '#E9E9E9');
    key.setAttribute('transform', 'translate(430 ' + (420 + ([0.7, 1.3, 1.9].some(x => t >= x && t < x + .16) ? 12 : 0)) + ')');
    hits.forEach((h, i) => { h.g.style.opacity = t >= [0.7, 1.3, 1.9][i] ? 1 : 0; });
    rec.style.opacity = seg(t, .5, .3); ok.style.opacity = t >= 1.3 ? 1 : 0; arr.style.opacity = seg(t, .5, .3);
    const B = seg(t, 3.6, .4);
    g2.style.opacity = B;
    wifi.style.opacity = 1; ver.g.style.opacity = seg(t, 4.3, .3); arr2.style.opacity = seg(t, 4.2, .3); srv.style.opacity = seg(t, 4.2, .3);
    arr3.style.opacity = seg(t, 5.0, .3); chk.style.opacity = seg(t, 5.0, .3); chk.setAttribute('transform', 'translate(1480 420) scale(' + (.7 + .3 * seg(t, 5.0, .4, E.back)) + ')');
  };
}

/* ---------------------------------------------------------------- FINAL */
function buildFinal(sc, el) {
  el.style.background = 'radial-gradient(ellipse at 50% 30%, #2a2a2a 0%, #191919 70%)';
  const lg = $('div', 'f-logo', el); lg.appendChild(logoSvg());
  const l1 = $('div', 'f-l1', el, 'NurCRM — касса,'), l2 = $('div', 'f-l2', el, '[[которая знает]] ваш магазин'.replace(/\[\[(.+?)\]\]/, '<span class="y">$1</span>'));
  const sub = $('div', 'f-sub', el, 'Работает без интернета. Говорит на вашем языке. Считает вашу прибыль.');
  const plate = $('div', 'f-plate', el, 'app.nurcrm.kg');
  const qr = $('div', 'f-qr', el, window.QR);
  return t => {
    const a = seg(t, 0, .35, E.back); lg.style.opacity = cl(a * 1.4); lg.style.transform = 'translateX(-50%) scale(' + (.96 + .04 * Math.min(1, a)) + ')';
    [[l1, .3], [l2, .42]].forEach(([d, t0]) => { const p = seg(t, t0, .12); d.style.opacity = p; d.style.transform = 'translateY(' + ((1 - p) * 18) + 'px)'; });
    sub.style.opacity = seg(t, .9, .3); const p = seg(t, 1.3, .3); plate.style.opacity = p; qr.style.opacity = p;
    const fade = 1 - seg(t, sc.dur - .5, .5, E.in); el.style.opacity = fade;
  };
}

/* ---------------------------------------------------------------- сборка */
SP.scenes.forEach((sc, i) => {
  const el = $('div', 'scene', app); el.style.visibility = 'hidden';
  let up;
  const tit = (sc.type === 'stage') ? makeTitrs(sc, el) : null;
  if (sc.type === 'stage') { const f = buildStage(sc, el); up = t => { f(t); tit(t); }; }
  else if (sc.type === 'shots') up = buildShots(sc, el);
  else if (sc.type === 'diagram') up = buildDiagram(sc, el);
  else if (sc.type === 'final') up = buildFinal(sc, el);
  layers.push({ sc, el, up });
});
// заставка
const intro = $('div', 'intro', app);
const ilogo = $('div', 'i-logo', intro); ilogo.appendChild(logoSvg(320));
$('div', 'i-title', intro, hy(SP.title)); $('div', 'i-num', intro, ('0' + SP.num).slice(-2) + ' / ' + (SP.total || 20));
const wipe = $('div', 'wipe', app);
const sub = $('div', 'subs', app);
const subLines = [];
SP.scenes.forEach(sc => (sc.say || []).forEach(l => { if (l.who === 'N' && l.show !== false) subLines.push({ t0: sc.start + l.at, t1: sc.start + l.at + l.dur + .15, text: l.sub || l.text }); }));
const bounds = [1.5].concat(SP.scenes.slice(1).map(s => s.start));
window.__render = function (t) {
  layers.forEach(({ sc, el, up }) => {
    const vis = t >= sc.start - 1e-6 && t < sc.start + sc.dur - 1e-6;
    el.style.visibility = ON(vis);
    if (vis) { el.style.opacity = 1; up(t - sc.start); }
  });
  // заставка 1,5 с
  const iv = t < 1.5;
  intro.style.visibility = ON(iv);
  if (iv) { const p = seg(t, 0, .3); ilogo.style.transform = 'translate(-50%,0) scale(' + (.9 + .1 * p) + ')'; ilogo.style.opacity = cl(p * 1.5); intro.querySelector('.i-title').style.opacity = seg(t, .25, .35); intro.querySelector('.i-num').style.opacity = seg(t, .35, .3); }
  // протирание на границах
  let x = -101;
  for (const b of bounds) {
    if (t >= b - .12 && t < b) x = -100 + 100 * E.out((t - (b - .12)) / .12);
    else if (t >= b && t < b + .12) x = 100 * E.in((t - b) / .12);
  }
  wipe.style.transform = 'translateX(' + x + '%)'; wipe.style.display = x <= -100 || x >= 100 ? 'none' : 'block';
  const ln = subLines.find(l => t >= l.t0 && t < l.t1);
  sub.style.opacity = ln ? Math.min(1, (t - ln.t0) / .12, (ln.t1 - t) / .12) : 0; if (ln && sub.dataset.k !== String(ln.t0)) { sub.innerHTML = '<span>' + hy(ln.text) + '</span>'; sub.dataset.k = String(ln.t0); }
};
window.__ready = Promise.resolve(window.__ready).then(() => document.fonts.ready);
})();
