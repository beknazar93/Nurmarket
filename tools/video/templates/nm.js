/* 2026-10-29: общая библиотека видео-шаблонов NurMarket.
   Время кадра задаёт сборщик: window.__render(t) вызывается для каждого кадра (t в секундах от начала сцены).
   Страница ничего не «проигрывает» сама — все состояния считаются из t, поэтому кадры точные.
   Данные сцены: window.SCENE = {params, duration, lang, portrait, W, H}. */
(function () {
  const S = window.SCENE || { params: {}, duration: 5, lang: 'ru' };
  const P = S.params || {};
  const clamp = (x, a = 0, b = 1) => Math.min(b, Math.max(a, x));
  const ease = {
    lin: (t) => t,
    out: (t) => 1 - Math.pow(1 - t, 3),
    in: (t) => t * t * t,
    inOut: (t) => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2),
    back: (t) => { const c1 = 1.70158, c3 = c1 + 1; return 1 + c3 * Math.pow(t - 1, 3) + c1 * Math.pow(t - 1, 2); },
  };
  // «40%» = доля длительности сцены, число = секунды
  const T = (v, def) => {
    if (v == null) return def;
    if (typeof v === 'string' && v.trim().endsWith('%')) return (parseFloat(v) / 100) * S.duration;
    return +v;
  };
  // прогресс отрезка [at, at+dur] с функцией плавности
  const seg = (t, at, dur, fn) => (fn || ease.out)(clamp((t - at) / Math.max(dur, 1e-6)));
  // 0 -> 1 на входе, 1 -> 0 на выходе
  const pulse = (t, at, dur, fin = 0.35, fout = 0.35) =>
    Math.min(seg(t, at, fin, ease.out), 1 - seg(t, at + dur - fout, fout, ease.in));
  const el = (tag, cls, parent, text) => {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    (parent || document.getElementById('root') || document.body).appendChild(e);
    return e;
  };
  const lerp = (a, b, k) => a + (b - a) * k;
  const toURL = (p) => (/^(file|https?|data):/i.test(p) ? p : 'file:///' + String(p).replace(/\\/g, '/'));
  const loadImg = (src) => new Promise((res) => {
    const im = new Image();
    im.onload = () => res(im); im.onerror = () => res(im);
    im.src = toURL(src);
  });
  document.documentElement.classList.add(S.portrait ? 'portrait' : 'landscape');

  /* ---- плашка-титр (lower third) ---- */
  function titr(text, opts) {
    const o = Object.assign({ pos: 'top' }, opts || {});
    const box = el('div', 'nm-titr', document.body);
    const bar = el('i', null, box);
    el('span', null, box, text);
    Object.assign(box.style, {
      position: 'absolute', left: 'calc(var(--u)*4)', maxWidth: 'calc(100vw - var(--u)*8)',
      [o.pos === 'bottom' ? 'bottom' : 'top']: o.pos === 'bottom' ? 'calc(var(--u)*' + (S.portrait ? 17 : 13) + ')' : 'calc(var(--u)*' + (S.portrait ? 6 : 4) + ')',
      display: 'flex', alignItems: 'stretch', background: 'rgba(25,25,25,.92)', color: '#fff',
      borderRadius: 'calc(var(--u)*1.2)', overflow: 'hidden', zIndex: 20,
      font: '700 calc(var(--u)*3) "Montserrat","Noto Sans",sans-serif',
      boxShadow: '0 calc(var(--u)*.6) calc(var(--u)*2.4) rgba(0,0,0,.35)',
    });
    Object.assign(bar.style, { width: 'calc(var(--u)*1.1)', background: '#FFB208', flex: 'none' });
    box.querySelector('span').style.padding = 'calc(var(--u)*1.7) calc(var(--u)*2.6)';
    return {
      set(t, at, dur) {
        const p = pulse(t, at, dur, 0.45, 0.35);
        box.style.opacity = p;
        box.style.transform = `translateX(${(1 - p) * -6}vmin)`;
      },
    };
  }

  window.NM = { S, P, clamp, ease, T, seg, pulse, el, lerp, toURL, loadImg, titr };
  window.__ready = new Promise((r) => {
    const go = () => (document.fonts && document.fonts.ready ? document.fonts.ready : Promise.resolve()).then(r);
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', go); else go();
  });
})();
