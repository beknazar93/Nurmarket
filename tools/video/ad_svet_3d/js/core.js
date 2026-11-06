/* 2026-10-29: 3D-версия ролика «Свет погас» (Three.js). Ядро: утилиты, рендерер, материалы, геометрии, текстуры.
   Вся сцена — чистая функция времени: window.__render(t). Состояния между кадрами нет. */
(function () {
'use strict';
const S = window.SCENE, TL = window.TIMELINE, THREE = window.THREE;
const W = S.W, H = S.H, PORTRAIT = H > W;
const LANG = S.lang === 'ky' ? 'ky' : 'ru';
const VAR = PORTRAIT ? 'short' : 'full';
const V = TL[VAR];
const VM = Math.min(W, H) / 100;
const L = (ru, ky) => (LANG === 'ky' ? ky : ru);

/* ---------- утилиты ---------- */
const cl = (x, a = 0, b = 1) => Math.min(b, Math.max(a, x));
const lerp = (a, b, k) => a + (b - a) * k;
const E = {
  lin: u => u, sm: u => u * u * (3 - 2 * u), inS: u => 1 - Math.cos(u * Math.PI / 2), outS: u => Math.sin(u * Math.PI / 2),
  inOutS: u => -(Math.cos(Math.PI * u) - 1) / 2, out3: u => 1 - Math.pow(1 - u, 3), in3: u => u * u * u,
  inOut3: u => (u < .5 ? 4 * u * u * u : 1 - Math.pow(-2 * u + 2, 3) / 2),
  back: u => { const c1 = 1.70158, c3 = c1 + 1; return 1 + c3 * Math.pow(u - 1, 3) + c1 * Math.pow(u - 1, 2); },
  hold: u => (u >= 1 ? 1 : 0),
};
const seg = (t, t0, d, f) => (f || E.inOutS)(cl((t - t0) / d));
const pulse = (t, t0, d, fi = .25, fo = .25) => Math.min(seg(t, t0, fi, E.out3), 1 - seg(t, t0 + d - fo, fo, E.in3));
const hash = n => { const x = Math.sin(n * 127.1 + 311.7) * 43758.5453; return x - Math.floor(x); };
// ключевые кадры: keys = [[t, v1, v2, ..., ease?]]; ease стоит в «конечном» ключе сегмента
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

/* ---------- рендерер ---------- */
const canvas = document.getElementById('gl');
canvas.width = W; canvas.height = H; canvas.style.width = W + 'px'; canvas.style.height = H + 'px';
const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, powerPreference: 'high-performance', preserveDrawingBuffer: false });
renderer.setPixelRatio(1);
renderer.setSize(W, H, false);
renderer.shadowMap.enabled = true;
renderer.shadowMap.type = THREE.PCFSoftShadowMap;
renderer.toneMapping = THREE.NeutralToneMapping;
renderer.toneMappingExposure = 1.0;
const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(40, W / H, 0.05, 120);

/* ---------- материалы и геометрии (с кэшем) ---------- */
const MC = {};
function M(color, o) {
  o = o || {};
  const key = color + '|' + (o.r == null ? .85 : o.r) + '|' + (o.m || 0) + '|' + (o.e || 0) + '|' + (o.ei || 1) + '|' + (o.flat ? 1 : 0) + '|' + (o.side || 0) + '|' + (o.tag || '');
  if (o.noCache || !MC[key]) {
    const m = new THREE.MeshStandardMaterial({ color, roughness: o.r == null ? .85 : o.r, metalness: o.m || 0, emissive: o.e || 0, emissiveIntensity: o.ei == null ? 1 : o.ei, flatShading: !!o.flat, side: o.side || 0 });
    if (o.tex) m.map = o.tex;
    if (o.noCache) return m;
    MC[key] = m;
  }
  return MC[key];
}
const GC = {};
const geo = (k, f) => GC[k] || (GC[k] = f());
const gBox = (w, h, d) => geo('b' + w + ',' + h + ',' + d, () => new THREE.BoxGeometry(w, h, d));
const gSph = (r, ws = 18, hs = 14) => geo('s' + r + ',' + ws + ',' + hs, () => new THREE.SphereGeometry(r, ws, hs));
const gCyl = (rt, rb, h, s = 20) => geo('c' + rt + ',' + rb + ',' + h + ',' + s, () => new THREE.CylinderGeometry(rt, rb, h, s));
const gCap = (r, len, cs = 5, rs = 14) => geo('p' + r + ',' + len + ',' + cs + ',' + rs, () => new THREE.CapsuleGeometry(r, len, cs, rs));
const gTor = (R, r, rs = 8, ts = 24, arc = Math.PI * 2) => geo('t' + R + ',' + r + ',' + rs + ',' + ts + ',' + arc, () => new THREE.TorusGeometry(R, r, rs, ts, arc));
// скруглённый параллелепипед («пластилин»): ExtrudeGeometry со скосом
function gRB(w, h, d, r) {
  r = r == null ? Math.min(w, h, d) * .18 : r;
  return geo('r' + w + ',' + h + ',' + d + ',' + r, () => {
    const bv = Math.min(r, w / 2 - .002, h / 2 - .002, d / 2 - .002);
    const ww = w - 2 * bv, hh = h - 2 * bv, rr = Math.max(.0005, bv * .6);
    const x = -ww / 2, y = -hh / 2;
    const s = new THREE.Shape();
    s.moveTo(x + rr, y); s.lineTo(x + ww - rr, y); s.quadraticCurveTo(x + ww, y, x + ww, y + rr); s.lineTo(x + ww, y + hh - rr);
    s.quadraticCurveTo(x + ww, y + hh, x + ww - rr, y + hh); s.lineTo(x + rr, y + hh); s.quadraticCurveTo(x, y + hh, x, y + hh - rr);
    s.lineTo(x, y + rr); s.quadraticCurveTo(x, y, x + rr, y);
    const g = new THREE.ExtrudeGeometry(s, { depth: Math.max(.001, d - 2 * bv), bevelEnabled: true, bevelThickness: bv, bevelSize: bv, bevelSegments: 2, curveSegments: 3 });
    g.translate(0, 0, -(d - 2 * bv) / 2);
    g.computeVertexNormals();
    return g;
  });
}
function mesh(parent, g, m, x, y, z, o) {
  o = o || {};
  const e = new THREE.Mesh(g, m);
  e.position.set(x || 0, y || 0, z || 0);
  if (o.s != null) { if (typeof o.s === 'number') e.scale.set(o.s, o.s, o.s); else e.scale.set(o.s[0], o.s[1], o.s[2]); }
  if (o.r) e.rotation.set(o.r[0], o.r[1], o.r[2]);
  e.castShadow = o.cast !== false; e.receiveShadow = o.recv !== false;
  if (parent) parent.add(e);
  return e;
}
function grp(parent, x, y, z) {
  const g = new THREE.Group(); g.position.set(x || 0, y || 0, z || 0); if (parent) parent.add(g); return g;
}
// canvas-текстура
function ctex(w, h, draw, o) {
  o = o || {};
  const c = document.createElement('canvas'); c.width = w; c.height = h;
  const g = c.getContext('2d');
  draw(g, w, h);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace; t.anisotropy = 8;
  if (o.repeat) { t.wrapS = t.wrapT = THREE.RepeatWrapping; t.repeat.set(o.repeat[0], o.repeat[1]); }
  t._c = c; t._g = g;
  return t;
}
function rrect(g, x, y, w, h, r) {
  g.beginPath(); g.moveTo(x + r, y); g.arcTo(x + w, y, x + w, y + h, r); g.arcTo(x + w, y + h, x, y + h, r); g.arcTo(x, y + h, x, y, r); g.arcTo(x, y, x + w, y, r); g.closePath();
}
const FONT = 'Montserrat,"Noto Sans",Arial,sans-serif';

window.N3 = { S, TL, THREE, W, H, PORTRAIT, LANG, VAR, V, VM, L, cl, lerp, E, seg, pulse, hash, kf, canvas, renderer, scene, camera,
  M, gBox, gSph, gCyl, gCap, gTor, gRB, mesh, grp, ctex, rrect, FONT };
})();
