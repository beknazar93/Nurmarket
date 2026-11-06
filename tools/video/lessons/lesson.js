/* 2026-10-30: движок видеоуроков NurCRM (1920x1080). Всё — функция времени t (сек от начала ролика): window.__render(t).
   Данные: window.SPEC (собирает gen_lesson.py из сценария JSON). Координаты меток/курсора/зума — доли кадра (0..1),
   поэтому не зависят от масштаба снимка. Типы сцен: intro, goal, step (кадры + рамки + курсор + зум + титр), final. */
(function () {
  const SP = window.SPEC, W = 1920, H = 1080;
  const cl = (x, a = 0, b = 1) => Math.min(b, Math.max(a, x));
  const E = {
    out: t => 1 - Math.pow(1 - t, 3),
    inOut: t => (t < .5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2),
    back: t => { const c1 = 1.70158, c3 = c1 + 1; return 1 + c3 * Math.pow(t - 1, 3) + c1 * Math.pow(t - 1, 2); },
  };
  const seg = (t, at, dur, fn) => (fn || E.out)(cl((t - at) / Math.max(dur, 1e-6)));
  const pulse = (t, a, b, fi, fo) => Math.min(seg(t, a, fi || .15), 1 - seg(t, b - (fo || .2), fo || .2, x => x));
  const $ = (tag, cls, parent, html) => { const e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; (parent || app).appendChild(e); return e; };
  const hy = s => String(s).replace(/\*\*(.+?)\*\*/g, '<b>$1</b>');
  const app = document.getElementById('app');

  function logoSvg(sz) {
    const src = document.querySelector('#svgsrc svg');
    const s = src.cloneNode(true); s.removeAttribute('id'); s.setAttribute('width', sz); s.setAttribute('height', sz); return s;
  }

  /* ---------- слой кадров (тело урока) ---------- */
  const body = $('div', 'layer'); body.style.display = 'none';
  const frames = [];   // плоский список всех кадров по порядку
  SP.steps.forEach(st => st.frames.forEach(f => {
    f.step = st;
    f.el = $('img', 'fr', body); f.el.src = f.src; f.el.decoding = 'sync';
    f.b = Math.min(W / f.w, H / f.h);
    frames.push(f);
  }));
  const boxes = [], rings = [];
  SP.steps.forEach(st => { (st.marks || []).forEach(m => { m.el = $('div', 'box', body); boxes.push(m); }); });
  SP.steps.forEach(st => { (st.clicks || []).forEach(c => { c.el = $('div', 'ring', body); rings.push(c); }); });
  const cur = $('div', 'cur', body, '<svg viewBox="0 0 24 24" width="42" height="42"><path d="M3 2 L3 19 L8 14.6 L11.4 22 L14.4 20.7 L11 13.4 L17.5 13.2 Z" fill="#191919" stroke="#fff" stroke-width="1.6" stroke-linejoin="round"/></svg>');
  const sch = $('div', 'sch', body);
  const titr = $('div', 'titr', app), kbd = $('div', 'kbd', app), hint = $('div', 'hint', app), sub = $('div', 'subs', app);

  const allZ = []; SP.steps.forEach(st => (st.zooms || []).forEach(z => allZ.push(z)));

  function zoomAt(t) {                 // -> {k, cx, cy, s}
    let best = null;
    for (const z of allZ) {
      const k = Math.min(seg(t, z.t0, .45, E.inOut), 1 - seg(t, z.t1 - .45, .45, E.inOut));
      if (k > 0 && (!best || k > best.k)) best = { k, z };
    }
    return best;
  }
  function viewOf(f, t) {              // раскладка кадра f в момент t: {s (px экрана на px кадра), x0, y0}
    const zz = zoomAt(t);
    let z = 1, cx = .5, cy = .5;
    if (zz) { z = 1 + (zz.z.scale - 1) * zz.k; cx = .5 + (zz.z.cx - .5) * zz.k; cy = .5 + (zz.z.cy - .5) * zz.k; }
    const s = f.b * z, vw = W / s, vh = H / s;
    let px = cx * f.w, py = cy * f.h;
    px = f.w * s >= W ? cl(px, vw / 2, f.w - vw / 2) : f.w / 2;
    py = f.h * s >= H ? cl(py, vh / 2, f.h - vh / 2) : f.h / 2;
    return { s, x0: W / 2 - px * s, y0: H / 2 - py * s };
  }
  const P = (f, v, fx, fy) => [v.x0 + fx * f.w * v.s, v.y0 + fy * f.h * v.s];

  function frameAt(t) {                // верхний видимый кадр
    let r = null;
    for (const f of frames) if (t >= f.t0 && t < f.t1) r = f;
    return r;
  }

  /* ---------- схема (подписанная, не снимок) ---------- */
  function buildSchema(st) {
    const s = st.schema, d = $('div', 'sch', body); d.style.display = 'none';
    $('div', 'sch-t', d, hy(s.title || ''));
    $('div', 'sch-tag', d, s.tag || 'СХЕМА · не снимок экрана');
    (s.boxes || []).forEach(b => { const e = $('div', 'sch-b ' + (b.c || ''), d, hy(b.text)); Object.assign(e.style, { left: b.x + 'px', top: b.y + 'px', width: b.w + 'px', height: b.h + 'px', fontSize: (b.fs || 34) + 'px' }); });
    (s.arrows || []).forEach(a => {
      const dx = a.x2 - a.x1, dy = a.y2 - a.y1, len = Math.hypot(dx, dy) - 14;
      const e = $('div', 'sch-a', d); Object.assign(e.style, { left: a.x1 + 'px', top: a.y1 - 3 + 'px', width: len + 'px', transform: 'rotate(' + Math.atan2(dy, dx) + 'rad)' });
    });
    (s.notes || []).forEach(n => { const e = $('div', 'sch-n', d, hy(n.text)); Object.assign(e.style, { left: n.x + 'px', top: n.y + 'px' }); });
    st.schEl = d;
  }
  SP.steps.forEach(st => { if (st.schema) buildSchema(st); });

  /* ---------- заставка / цель / финал ---------- */
  const intro = $('div', 'scene'), goal = $('div', 'scene'), fin = $('div', 'scene');
  const il = $('div', 'i-logo', intro); il.appendChild(logoSvg(340));
  const isub = $('div', 'i-sub', intro, hy(SP.subtitle));
  const iplate = $('div', 'i-plate', intro, hy(SP.label));
  const gimg = $('img', 'fr', goal); const gdim = $('div', 'g-dim', goal); const gtext = $('div', 'g-text', goal, '<span>' + hy(SP.goalText) + '</span>');
  const fl = $('div', 'f-logo', fin); fl.appendChild(logoSvg(210));
  const fl1 = $('div', 'f-l1', fin, hy(SP.finalTitle || 'Готово')), fl2 = $('div', 'f-l2', fin, hy(SP.outcome));
  const fnext = $('div', 'f-next', fin, 'Что дальше: <b>' + hy(SP.next) + '</b>');
  const fplate = $('div', 'f-plate', fin, 'app.nurcrm.kg'), fqr = $('div', 'f-qr', fin, window.QR);
  $('div', 'f-ver', fin, hy(SP.versionLine));
  if (SP.goalFrame) { gimg.src = SP.goalFrame.src; gimg.decoding = 'sync'; }

  function layout(img, w, h, el) { const b = Math.min(W / w, H / h); el.style.width = w + 'px'; el.style.height = h + 'px'; el.style.transform = 'translate(' + (W - w * b) / 2 + 'px,' + (H - h * b) / 2 + 'px) scale(' + b + ')'; el.style.display = 'block'; }
  if (SP.goalFrame) layout(gimg, SP.goalFrame.w, SP.goalFrame.h, gimg);
  frames.forEach(f => { f.el.style.width = f.w + 'px'; f.el.style.height = f.h + 'px'; });

  const I = SP.intro, G = SP.goal, F = SP.final;

  window.__render = function (t) {
    const inIntro = t < I.dur, inGoal = t >= G.start && t < G.start + G.dur, inFin = t >= F.start;
    const inBody = !inIntro && !inGoal && !inFin;
    intro.style.display = inIntro ? 'block' : 'none'; goal.style.display = inGoal ? 'block' : 'none'; fin.style.display = inFin ? 'block' : 'none';
    body.style.display = inBody ? 'block' : 'none';
    titr.style.opacity = 0; kbd.style.opacity = 0; hint.style.opacity = 0;
    // субтитры (на заставке и финале тоже, если там есть голос)
    const sb = SP.subs.find(x => t >= x.t0 && t < x.t1);
    if (sb) { if (sub.dataset.k !== String(sb.t0)) { sub.innerHTML = '<span>' + hy(sb.text) + '</span>'; sub.dataset.k = String(sb.t0); } sub.style.opacity = Math.min(1, (t - sb.t0) / .12, (sb.t1 - t) / .12); }
    else sub.style.opacity = 0;
    if (inIntro) {
      const p = seg(t, 0, .35, E.back); il.style.opacity = cl(p * 1.4); il.style.transform = 'translateX(-50%) scale(' + (.88 + .12 * Math.min(1, p)) + ')';
      isub.style.opacity = seg(t, .5, .4);
      const w = seg(t, .55, .24, E.out); iplate.style.transform = 'translateX(' + (-100 + 100 * w) + '%)';
      iplate.style.opacity = w > 0 ? 1 : 0;
      intro.style.opacity = 1 - seg(t, I.dur - .3, .3, x => x) * 0;   // без плавного выхода: переход встык
      return;
    }
    if (inGoal) {
      const l = t - G.start; gdim.style.opacity = seg(l, 0, .4);
      gtext.style.opacity = seg(l, .2, .4); gtext.style.transform = 'translateY(' + (1 - seg(l, .2, .4)) * 16 + 'px)';
      goal.style.opacity = 1;
      return;
    }
    if (inFin) {
      const l = t - F.start;
      const a = seg(l, 0, .4, E.back); fl.style.opacity = cl(a * 1.4); fl.style.transform = 'translateX(-50%) scale(' + (.9 + .1 * Math.min(1, a)) + ')';
      [[fl1, .25], [fl2, .4], [fnext, .65]].forEach(([d, t0]) => { const q = seg(l, t0, .3); d.style.opacity = q; d.style.transform = 'translateY(' + (1 - q) * 16 + 'px)'; });
      const q = seg(l, 1.0, .3); fplate.style.opacity = q; fqr.style.opacity = q;
      fin.style.opacity = 1 - seg(l, F.dur - .4, .4, x => x);
      return;
    }
    // ---------- тело ----------
    const top = frameAt(t);
    const st = SP.steps.find(s => t >= s.start && t < s.start + s.dur);
    let showSch = st && st.schEl && t >= st.schT0 && t < st.schT1;
    SP.steps.forEach(s => { if (s.schEl) s.schEl.style.display = (s === st && showSch) ? 'block' : 'none'; });
    frames.forEach(f => {
      const vis = t >= f.t0 && t < f.t1 && !(showSch && f.step === st);
      f.el.style.display = vis ? 'block' : 'none';
      if (!vis) return;
      const v = viewOf(f, t);
      f.el.style.transform = 'translate(' + v.x0 + 'px,' + v.y0 + 'px) scale(' + v.s + ')';
      f.el.style.opacity = f === top ? seg(t, f.t0, .12, x => x) : 1;
      f.el.style.boxShadow = '';
    });
    // рамки
    boxes.forEach(m => {
      const on = !showSch && top && m.f === top.id && t >= m.t0 - .01 && t < m.t1;
      if (!on) { m.el.style.opacity = 0; return; }
      const f = top, v = viewOf(f, t), [x1, y1] = P(f, v, m.r[0], m.r[1]), [x2, y2] = P(f, v, m.r[0] + m.r[2], m.r[1] + m.r[3]);
      const pad = 6;
      Object.assign(m.el.style, { left: x1 - pad + 'px', top: y1 - pad + 'px', width: x2 - x1 + 2 * pad + 'px', height: y2 - y1 + 2 * pad + 'px', opacity: Math.min(seg(t, m.t0, .16), 1 - seg(t, m.t1 - .2, .2, x => x)) });
    });
    // курсор и кольца клика
    let cp = null;
    const kfs = SP.cursor;
    if (!showSch && top && kfs.length) {
      const first = kfs[0], last = kfs[kfs.length - 1];
      if (t >= first.t - .01 && t < last.t + 1.6) {
        let a = kfs[0], b = kfs[0];
        for (let i = 0; i < kfs.length; i++) { if (kfs[i].t <= t) a = kfs[i]; if (kfs[i].t > t) { b = kfs[i]; break; } if (i === kfs.length - 1) b = kfs[i]; }
        // курсор ЕДЕТ к b за 0,55 с до b.t, до этого стоит на a
        const k = b.t > a.t ? E.inOut(cl((t - (b.t - .55)) / .55)) : 1;
        const fx = a.fx + (b.fx - a.fx) * (b === a ? 0 : k), fy = a.fy + (b.fy - a.fy) * (b === a ? 0 : k);
        const v = viewOf(top, t); cp = P(top, v, fx, fy);
      }
    }
    if (cp) { cur.style.opacity = Math.min(1, seg(t, kfs[0].t - .4, .3)) * (1 - seg(t, kfs[kfs.length - 1].t + 1.2, .4, x => x)); cur.style.transform = 'translate(' + (cp[0] - 4) + 'px,' + (cp[1] - 3) + 'px) scale(1.5)'; cur.style.transformOrigin = '0 0'; } else cur.style.opacity = 0;
    rings.forEach(c => {
      const l = t - c.t;
      if (showSch || !top || l < 0 || l > .34) { c.el.style.opacity = 0; return; }
      const v = viewOf(top, t), [x, y] = P(top, v, c.fx, c.fy), p = l / .34;
      Object.assign(c.el.style, { left: x + 'px', top: y + 'px', opacity: 1 - p * .9, transform: 'scale(' + (.7 + 1.1 * E.out(p)) + ')' });
    });
    if (st) {
      const l = t - st.start;
      titr.innerHTML = 'Шаг ' + st.n + ' из ' + SP.steps.length + ' · ' + hy(st.title);
      const p = seg(l, .05, .3);
      titr.style.opacity = p * (1 - seg(l, st.dur - .2, .2, x => x)); titr.style.transform = 'translateX(' + (1 - p) * -40 + 'px)';
      (st.kbd || []).forEach(k => { if (t >= k.t0 && t < k.t1) { kbd.textContent = k.text; kbd.style.opacity = Math.min(seg(t, k.t0, .15), 1 - seg(t, k.t1 - .2, .2, x => x)); } });
      if (st.hint && t >= st.hint.t0 && t < st.hint.t1) {
        if (hint.dataset.k !== st.hint.text) { hint.innerHTML = '<i>Если не получилось</i>' + hy(st.hint.text); hint.dataset.k = st.hint.text; }
        hint.style.opacity = Math.min(seg(t, st.hint.t0, .3), 1 - seg(t, st.hint.t1 - .3, .3, x => x));
      }
    }
  };
  window.__ready = Promise.all(frames.map(f => f.el.decode ? f.el.decode().catch(() => 0) : 0).concat(SP.goalFrame ? [gimg.decode().catch(() => 0)] : [])).then(() => document.fonts.ready);
})();
