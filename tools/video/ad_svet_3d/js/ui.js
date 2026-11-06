/* 2026-10-29: плоский слой поверх 3D: титры-плашки, субтитры, жёлтое протирание, цена над лепёшкой, список «пик», видоискатель,
   «Чеки отправлены», финальная карточка. Бренд — NurCRM, ссылка только app.nurcrm.kg (без GitHub). */
(function () {
'use strict';
const N = window.N3;
const { THREE, W, H, PORTRAIT, VAR, V, LANG, L, VM, cl, lerp, E, seg, pulse, hash, camera, TL } = N;
const UI = document.getElementById('ui');
function div(cls, parent, html) { const e = document.createElement('div'); if (cls) e.className = cls; if (html != null) e.innerHTML = html; (parent || UI).appendChild(e); return e; }
const T = () => N.T;

/* ---- плашки-титры ---- */
const PL = [];
function addPlate(t0, d, ru, ky, o) {
  const p = div('plate', null, '<i></i><b></b>');
  p.querySelector('b').innerHTML = LANG === 'ky' ? ky : ru;
  p.style.fontSize = (PORTRAIT ? 8.6 : 6.0) * VM + 'px';
  p.style.top = (PORTRAIT ? .1 : .07) * H + 'px';
  p.style.opacity = 0;
  PL.push(Object.assign({ el: p, t0, d }, o || {}));
}
const PIK = '<span class="w">Пик.</span> <span class="w">Пик.</span> <span class="w">Пик.</span>';
function buildPlates(Tm) {
  if (VAR === 'full') {
    addPlate(3.3, 1.35, 'КАК <em>РАНЬШЕ</em>', 'МУРДА <em>КАНДАЙ</em> БОЛЧУ');
    addPlate(9.15, 2.6, 'Касса <em>умерла</em>', 'Касса <em>өлдү</em>');
    addPlate(12.15, 2.7, 'Лепёшка — <em>30? 35?</em>', 'Нан — <em>30? 35?</em>');
    addPlate(15.25, 2.7, 'Люди <em>ушли</em>', 'Адамдар <em>кетти</em>');
    addPlate(19.5, 1.25, 'С <em>NURCRM</em>', 'NURCRM <em>МЕНЕН</em>');
    addPlate(22.25, 2.5, 'Телефон <em>= касса</em>', 'Телефон <em>= касса</em>');
    addPlate(28.25, 1.75, 'Чек. Сдача. <em>Дальше</em>', 'Чек. Кайтарым. <em>Андан ары</em>');
    addPlate(32.15, 1.8, 'Тот же вечер — <em>без паники</em>', 'Ошол эле кеч — <em>паникасыз</em>');
    addPlate(25.0, 2.9, PIK, PIK, { words: Tm.piks });
  } else {
    addPlate(0.35, 2.6, 'Свет <em>погас?</em>', 'Жарык <em>өчтүбү?</em>');
    addPlate(3.6, 3.0, 'Телефон <em>= касса</em>', 'Телефон <em>= касса</em>');
    addPlate(7.0, 3.5, PIK, PIK, { words: Tm.piks });
  }
}
const subsEl = div('', null); subsEl.id = 'subs';
const wipe = div('', null); wipe.id = 'wipe'; wipe.style.transform = 'translateX(-101%)';
const flashEl = div('', null); flashEl.id = 'flash'; flashEl.style.opacity = 0;
const vig = div('', null); vig.id = 'vig';
const chip = div('chip', null, ''); chip.style.opacity = 0;
const panel = div('panel', null, ''); panel.style.opacity = 0;
const sent = div('chip', null, ''); sent.style.opacity = 0;
// видоискатель (угловые скобки) — SVG
const NS = 'http://www.w3.org/2000/svg';
const vfSvg = document.createElementNS(NS, 'svg'); vfSvg.setAttribute('width', W); vfSvg.setAttribute('height', H); vfSvg.style.cssText = 'position:absolute;left:0;top:0;opacity:0';
UI.appendChild(vfSvg);
const vfP = [0, 1, 2, 3].map(() => { const p = document.createElementNS(NS, 'path'); p.setAttribute('fill', 'none'); p.setAttribute('stroke', '#FFB208'); p.setAttribute('stroke-width', 5 * VM / 1.0 * .35); p.setAttribute('stroke-linecap', 'round'); p.setAttribute('stroke-linejoin', 'round'); vfSvg.appendChild(p); return p; });

/* ---- финальная карточка ---- */
const END = div('', null); END.id = 'end'; END.style.display = 'none';
const logo = document.createElement('img'); logo.src = window.LOGO; logo.style.cssText = 'position:absolute;border-radius:50%'; END.appendChild(logo);
const t1 = div('t1', END, LANG === 'ky' ? 'NurCRM —' : 'NurCRM — касса,');
const t2 = div('t2', END, LANG === 'ky' ? 'дүкөнүңүздү <span class="y">билген</span> касса' : '<span class="y">которая знает</span> ваш магазин');
const sub = div('sub', END, L('Работает без интернета. Говорит на вашем языке. Считает вашу прибыль.', 'Интернетсиз иштейт. Сиздин тилде сүйлөйт. Пайдаңызды эсептейт.'));
const links = div('links', END, L('Установить: app.nurcrm.kg', 'Орнотуу: app.nurcrm.kg'));
const qr = div('qr', END, window.QR.app + '<div>app.nurcrm.kg</div>');
(function () {
  const u = VM, qs = (PORTRAIT ? 24 : 17) * u;
  qr.querySelector('svg').setAttribute('width', qs); qr.querySelector('svg').setAttribute('height', qs); qr.style.fontSize = (PORTRAIT ? 2.8 : 1.9) * u + 'px';
  if (!PORTRAIT) {
    logo.style.width = logo.style.height = 21 * u + 'px'; logo.style.left = (W - 21 * u) / 2 + 'px'; logo.style.top = 2 * u + 'px';
    t1.style.top = '24.5vmin'; t1.style.fontSize = 8.6 * u + 'px'; t2.style.top = '35.5vmin'; t2.style.fontSize = 8.6 * u + 'px';
    sub.style.top = '48.5vmin'; sub.style.fontSize = 3.3 * u + 'px'; sub.style.fontWeight = 600; sub.style.color = '#d8d8d8';
    links.style.fontSize = 3.4 * u + 'px'; links.style.padding = '2.4vmin 3.6vmin'; links.style.top = '70vmin'; links.style.left = '10vmin'; links.style.transform = 'none';
    qr.style.position = 'absolute'; qr.style.left = (W - qs - 12 * u) + 'px'; qr.style.top = '58vmin';
  } else {
    logo.style.width = logo.style.height = 30 * u + 'px'; logo.style.left = (W - 30 * u) / 2 + 'px'; logo.style.top = 9 * u + 'px';
    t1.style.top = 46 * u + 'px'; t1.style.fontSize = 8.9 * u + 'px'; t2.style.top = 57 * u + 'px'; t2.style.fontSize = 8.9 * u + 'px'; t2.style.padding = '0 3vmin';
    sub.style.top = 80 * u + 'px'; sub.style.fontSize = 3.9 * u + 'px'; sub.style.fontWeight = 600; sub.style.color = '#d8d8d8'; sub.style.padding = '0 8vmin';
    links.style.fontSize = 4.6 * u + 'px'; links.style.padding = '2.4vmin 3vmin'; links.style.left = '5vmin'; links.style.right = '5vmin'; links.style.transform = 'none'; links.style.top = 100 * u + 'px';
    qr.style.position = 'absolute'; qr.style.left = (W - qs) / 2 + 'px'; qr.style.top = 123 * u + 'px';
  }
})();

const WIPES = VAR === 'full' ? [3.0, 19.0, 34.0] : [10.8];
const VOICE = V.voice;
let built = false;
const _v = new THREE.Vector3();
function proj(p) { _v.copy(p).project(camera); return [(_v.x * .5 + .5) * W, (-_v.y * .5 + .5) * H, _v.z]; }

function update(t, fr) {
  const Tm = N.T;
  if (!built) { buildPlates(Tm); built = true; }
  PL.forEach(p => {
    const u = pulse(t, p.t0, p.d, .3, .28);
    p.el.style.opacity = u; p.el.style.transform = 'translate(-50%,' + ((1 - u) * -2.2 * VM) + 'px)';
    if (p.words) [...p.el.querySelectorAll('.w')].forEach((w, i) => {
      const a = cl((t - p.words[i]) / .12);
      w.style.display = 'inline-block'; w.style.opacity = a; w.style.transform = 'scale(' + (.7 + .3 * E.back(a)).toFixed(3) + ')';
      w.style.color = a > 0 && t - p.words[i] < .35 ? '#FFB208' : '#fff';
    });
  });
  let x = -101;
  WIPES.forEach(w => { const u = (t - w) / .5; if (u >= 0 && u <= 1) x = lerp(-101, 101, E.inOutS(u)); });
  wipe.style.transform = 'translateX(' + x + '%)';
  // субтитры
  let cur = null;
  for (const v of VOICE) if (t >= v.at - .04 && t < v.at + (v.dur || 1.5) + .25) cur = v;
  if (!cur || (PORTRAIT && t >= Tm.end + .2)) subsEl.style.opacity = 0; else {
    const who = TL.who[cur.who][LANG], txt = (LANG === 'ky' ? cur.ky : cur.ru) || cur.ru;
    subsEl.innerHTML = (who ? '<span class="who">' + who + '</span>' : '') + txt.replace(/NurMarket/g, 'NurCRM');
    subsEl.style.fontSize = (PORTRAIT ? 4.5 : 3.9) * VM + 'px'; subsEl.style.bottom = (PORTRAIT ? .125 : .055) * H + 'px'; subsEl.style.maxWidth = (PORTRAIT ? 90 : 78) + '%';
    subsEl.style.opacity = Math.min(cl((t - cur.at + .04) / .12), cl((cur.at + (cur.dur || 1.5) + .25 - t) / .15));
  }
  // вспышка «свет включился»
  let fl = 0;
  if (VAR === 'full' && t >= Tm.lightOn && t < Tm.lightOn + .3) fl = t < Tm.lightOn + .07 ? .45 : .45 * (1 - (t - Tm.lightOn - .07) / .23);
  flashEl.style.opacity = fl;
  // цена над лепёшкой и видоискатель (акт Б)
  const ctt = fr.ctt, isB = fr.act === 'B';
  const hudStart = Tm.hud, hudEnd = Tm.pik0 + .25;
  vfSvg.style.opacity = 0;
  if (isB && ctt >= hudStart && ctt < hudEnd + .25) {
    const [bx, by] = proj(fr.breadW), pd = Math.hypot(fr.cam[0] - fr.breadW.x, fr.cam[2] - fr.breadW.z);
    const k = H / (2 * Math.tan(fr.cam[6] * Math.PI / 360) * pd);   // пикселей на метр
    const u = seg(ctt, hudStart, Tm.pik0 - hudStart, E.inOutS), half = lerp(.3, .17, u) * k, l = .07 * k;
    vfSvg.style.opacity = Math.min(seg(ctt, hudStart, .2), 1 - seg(ctt, hudEnd, .25));
    const co = [[-1, -1], [1, -1], [1, 1], [-1, 1]];
    vfP.forEach((p, i) => { const [sx, sy] = co[i], px = bx + sx * half, py = by + sy * half; p.setAttribute('d', 'M' + px + ' ' + (py - sy * l) + ' V' + py + ' H' + (px - sx * l)); p.setAttribute('stroke-width', Math.max(3, .012 * k)); });
  }
  if (isB && ctt >= Tm.pik0 && ctt < Tm.pik0 + 1.9) {
    const [bx, by] = proj(fr.breadW), uu = ctt - Tm.pik0;
    chip.innerHTML = L('Лепёшка — 30 с', 'Нан — 30 с'); chip.style.fontSize = (PORTRAIT ? 4.4 : 3.0) * VM + 'px';
    chip.style.left = Math.max(2 * VM, Math.min(W - chip.offsetWidth - 2 * VM, bx - 30)) + 'px'; chip.style.top = (by - (PORTRAIT ? 160 : 110)) + 'px';
    const p = E.back(cl(uu / .25)), o = 1 - seg(ctt, Tm.pik0 + 1.5, .4);
    chip.style.opacity = Math.min(1, uu / .08) * o; chip.style.transform = 'translate(0,-100%) scale(' + (.6 + .4 * p).toFixed(3) + ')';
  } else chip.style.opacity = 0;
  const endPan = VAR === 'full' ? 29.2 : 10.4;
  if (isB && ctt >= Tm.pik0 + 1.9 && ctt < endPan) {
    const rows = [[L('Лепёшка', 'Нан'), '30 с'], [L('Молоко', 'Сүт'), '65 с'], [L('Семечки', 'Күн карама'), '40 с'], [L('Хлеб', 'Нан (батон)'), '35 с']];
    const n = [...Tm.piks].filter(q => ctt >= q).length + 1;
    panel.innerHTML = rows.slice(0, n).map(r => '<div class="row"><span>' + r[0] + '</span><span>' + r[1] + '</span></div>').join('');
    panel.style.fontSize = (PORTRAIT ? 3.8 : 2.4) * VM + 'px';
    panel.style.left = (PORTRAIT ? W * .5 - 8 * VM : W - 27 * VM) + 'px'; panel.style.top = (PORTRAIT ? H * .2 : 17 * VM) + 'px'; panel.style.minWidth = (PORTRAIT ? 30 : 17) * VM + 'px';
    const ent = seg(ctt, Tm.pik0 + 1.9, .3, E.out3), ext = 1 - seg(ctt, endPan - .3, .3);
    panel.style.opacity = Math.min(ent, ext); panel.style.transform = 'translateY(' + (1 - ent) * 12 + 'px)';
  } else panel.style.opacity = 0;
  if (VAR === 'full' && isB && ctt >= 32.3 && ctt < 34.0) {
    sent.style.fontSize = 3.0 * VM + 'px'; sent.innerHTML = '✓ ' + L('Чеки отправлены ', 'Чектер жөнөтүлдү ') + Math.min(5, Math.floor(1 + (ctt - 32.3) * 3.2)) + '/5';
    sent.style.left = (W - 56 * VM) + 'px'; sent.style.top = (H - 20 * VM) + 'px'; sent.style.opacity = Math.min(seg(ctt, 32.3, .3, E.out3), 1 - seg(ctt, 33.7, .3));
  } else sent.style.opacity = 0;
  // финал
  const t0 = Tm.end + .24;
  if (t < t0) END.style.display = 'none'; else {
    END.style.display = '';
    const a = t - t0, lp = E.out3(cl(a / .5));
    logo.style.opacity = lp; logo.style.transform = 'scale(' + (.96 + .04 * lp + (a > .25 && a < .55 ? .05 * Math.sin(Math.PI * (a - .25) / .3) : 0)).toFixed(4) + ')';
    const l1 = cl((a - .45) / .22), l2 = cl((a - .57) / .22), l3 = cl((a - .95) / .4);
    t1.style.opacity = l1; t1.style.transform = 'translateY(' + (1 - E.out3(l1)) * 3 * VM + 'px)';
    t2.style.opacity = l2; t2.style.transform = 'translateY(' + (1 - E.out3(l2)) * 3 * VM + 'px)';
    sub.style.opacity = l3;
    const lt = VAR === 'full' ? 38.0 : 12.9, qt = VAR === 'full' ? 38.45 : 13.5;
    const lk = E.out3(cl((t - lt) / .4)), qk = cl((t - qt) / .3);
    links.style.opacity = lk; links.style.transform = 'translateY(' + (1 - lk) * 14 * VM + 'px)';
    qr.style.opacity = qk; qr.style.transform = 'scale(' + (.85 + .15 * E.back(qk)).toFixed(3) + ')';
    const hit = VAR === 'full' ? 34.55 : 11.0, f = t >= hit && t < hit + .25 ? .18 * (1 - (t - hit) / .25) : 0;
    END.style.background = 'rgb(' + Math.round(25 + 230 * f) + ',' + Math.round(25 + 230 * f) + ',' + Math.round(25 + 200 * f) + ')';
  }
}
N.UI = { update };
})();
