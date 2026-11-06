/* 2026-10-29: сюжет «Свет погас» в 3D: хореография, реквизит, свет, камеры, кадр __render(t).
   Две «временные линии»: акт А («как раньше», время tau, есть перемотка) и акт Б («с NurCRM», абсолютное время t).
   Тайминги взяты из 2D-версии (tools/video/ad_svet/svet.js) и timeline.json — звук общий (premix). */
(function () {
'use strict';
const N = window.N3;
const { THREE, scene, camera, renderer, W, H, PORTRAIT, VAR, V, LANG, L, VM, cl, lerp, E, seg, pulse, hash, kf, M, mesh, grp, ctex, rrect, FONT, TL } = N;
const Wd = N.World, CAST = N.Cast, PR = N.Props, { poseChar, prepDist, DEG } = N.Chars;

/* ------------------------------------------------ тайминги */
const T = VAR === 'full'
  ? { clickA: 7.4, click: 20.4, glow: 21.05, hud: 22.9, pik0: 24.0, piks: [25.2, 25.8, 26.4], lightOn: 31.0, rewind: [18.0, 19.1], swap: 19.24, end: 34.0, sentA: 32.3, sentB: 34.0 }
  : { click: 1.5, glow: 3.0, hud: 4.9, pik0: 6.0, piks: [7.2, 7.8, 8.4], lightOn: 1e9, rewind: null, swap: 0, end: 10.8 };
const NAMES = ['ainura', 'ata', 'jyldyz', 'bakyt', 'erlan', 'apa', 'nurlan'];

/* ------------------------------------------------ хореография (позиции: x, z, курс°) */
const QP = { ata: [2.9, 0.05, 56], apa: [1.85, 0.0, 42], jyldyz: [0.95, -0.1, 40], bakyt: [1.32, 0.6, 50], erlan: [-0.05, 0.05, 38] };
const AI = [4.5, -0.25, -42];
const LIN = E.lin;
function K(o) { return Object.assign({ pos: [[0, 0, 0, 0]], arms: [[0, 'rest', 0]], head: [[0, 'fwd', 0]], lean: [[0, 'n', 0]], face: [[0, 'neutral', 0]], fx: [] }, o); }
const osc = (f, a, amp) => Math.sin(f * a * 6.2832) * amp;

function trackA() {
  const A = {};
  A.ainura = K({
    pos: [[0, ...AI]],
    arms: [[0, 'counter', 0], [3.3, 'counter', .01], [3.65, 'breadOut', .3], [5.15, 'breadOut', .01], [5.6, 'counter', .4], [7.45, 'counter', .01], [7.7, 'front', .2], [8.9, 'front', .01], [9.15, 'counter', .3],
      [9.65, 'calcTap', .4], [13.0, 'calcTap', .01], [13.35, 'headHand', .4], [14.3, 'headHand', .01], [14.7, 'calcTap', .4], [16.5, 'calcTap', .01], [16.7, 'counter', .3], [16.9, 'counter', .01], [17.25, 'shrug', .35], [17.9, 'shrug', .01], [18.3, 'counter', .4]],
    head: [[0, 'fwd', 0], [3.2, 'fwd', .01], [3.6, 'down', .3], [5.0, 'down', .01], [5.5, 'fwd', .3], [7.8, 'fwd', .01], [8.1, 'l', .3], [8.5, 'l', .01], [8.9, 'r', .35], [9.2, 'r', .01], [9.7, 'down', .35], [12.6, 'down', .01], [13.0, 'l', .3], [13.3, 'l', .01], [13.6, 'down', .3], [16.4, 'down', .01], [16.8, 'r', .3], [17.9, 'r', .01], [18.4, 'fwd', .4]],
    lean: [[0, 'n', 0], [8.9, 'n', .01], [9.2, 'fwd2', .3], [9.55, 'fwd2', .01], [9.9, 'fwd', .3]],
    face: [[0, 'calm', 0], [3.3, 'happy', .3], [5.3, 'calm', .3], [7.5, 'calm', .01], [7.75, 'surprise', .15], [8.8, 'surprise', .01], [9.1, 'worried', .3], [9.9, 'squint', .2], [12.2, 'squint', .01], [12.6, 'worried', .3], [16.6, 'worried', .01], [16.8, 'sad', .3]],
    fx: [{ t0: 10.1, t1: 14.9, fn: (u) => ({ Rb: osc(u, 2.3, .07), Lb: osc(u, 2.3, -.05) }) }, { t0: 10.15, t1: 10.7, fn: (u) => ({ hr: Math.sin(Math.PI * u / .5) * .2, lf: Math.sin(Math.PI * u / .5) * .1 }) },
      { t0: 9.15, t1: 9.65, fn: (u) => ({ Rf: osc(u, 3, .35), Lf: osc(u, 3, -.35) }) }],
  });
  A.ata = K({
    pos: [[0, ...QP.ata]],
    arms: [[0, 'holdR', 0], [3.4, 'holdR', .01], [3.95, 'breadOut', .5], [5.3, 'breadOut', .01], [5.8, 'holdR', .5], [14.0, 'holdR', .01], [14.3, 'raiseR', .3], [15.1, 'raiseR', .01], [15.5, 'holdR', .3], [16.15, 'holdR', .01], [16.6, 'breadOut', .4], [17.0, 'rest', .3]],
    head: [[0, 'fwd', 0], [12.9, 'fwd', .01], [13.2, 'tilt', .2]],
    face: [[0, 'calm', 0], [3.4, 'happy', .3], [5.4, 'calm', .3], [7.6, 'calm', .01], [7.8, 'surprise', .15], [9.0, 'worried', .5], [12.9, 'worried', .01], [13.2, 'angry', .25], [16.1, 'angry', .01], [16.4, 'sad', .3]],
    pos2: null,
  });
  A.ata.pos = [[0, ...QP.ata], [16.95, ...QP.ata], [19.4, 0.2, 0.2, -90, E.inOutS]];
  A.ata.pos = [[0, ...QP.ata], [16.95, ...QP.ata, LIN], [17.1, 2.4, 0.2, -90, E.inOutS], [19.4, 0.1, 0.25, -90, E.sm]];
  A.apa = K({
    pos: [[0, ...QP.apa], [16.4, ...QP.apa], [16.55, 1.85, .0, -90, E.inOutS], [19.4, -0.3, 0.4, -90, E.sm]],
    arms: [[0, 'bagR', 0], [15.9, 'bagR', .01], [16.0, 'bagR', .01]],
    head: [[0, 'fwd', 0], [13.15, 'fwd', .01], [13.3, 'tilt', .2]],
    face: [[0, 'calm', 0], [7.8, 'calm', .01], [8.0, 'surprise', .15], [9.0, 'worried', .5], [13.1, 'worried', .01], [13.3, 'sad', .25]],
    fx: [{ t0: 15.9, t1: 16.45, fn: (u) => ({ hy: Math.sin(Math.PI * u / .5) * -.4, hr: Math.sin(Math.PI * u / .5) * -.2 }) }],
  });
  A.jyldyz = K({
    pos: [[0, ...QP.jyldyz], [16.05, ...QP.jyldyz], [16.2, 0.9, -.1, -90, E.inOutS], [19.2, -1.9, 0.0, -90, E.sm]],
    arms: [[0, 'holdR', 0]],
    face: [[0, 'calm', 0], [7.8, 'calm', .01], [8.0, 'surprise', .15], [9.0, 'worried', .5], [13.7, 'worried', .01], [13.9, 'angry', .25]],
    fx: [{ t0: 14.9, t1: 15.8, fn: () => ({}) }],
  });
  A.bakyt = K({
    pos: [[0, ...QP.bakyt], [16.05, ...QP.bakyt], [16.2, 1.3, .55, -90, E.inOutS], [19.2, -1.6, .5, -90, E.sm]],
    arms: [[0, 'front', 0]],
    face: [[0, 'happy', 0], [2.2, 'happy', .01], [3.8, 'laugh', .1], [7.7, 'laugh', .01], [8.0, 'surprise', .15], [9.0, 'worried', .5], [13.75, 'worried', .01], [14.0, 'sad', .25]],
    fx: [{ t0: 13.75, t1: 15.3, fn: (u) => ({ hr: osc(u, 2, .06) }) }, { t0: 2.2, t1: 3.7, fn: (u) => ({ hr: osc(u, 2, .08) }) }],
  });
  A.erlan = K({
    pos: [[0, ...QP.erlan], [15.75, ...QP.erlan], [15.95, -.05, .05, -90, E.inOutS], [18.6, -4.2, .15, -90, E.sm]],
    arms: [[0, 'holdL', 0], [7.65, 'holdL', .01], [8.45, 'flashUp', .6], [15.7, 'flashUp', .01], [16.0, 'holdL', .3]],
    head: [[0, 'fwd', 0], [7.8, 'fwd', .01], [8.2, 'up', .3], [8.8, 'fwd', .3]],
    face: [[0, 'neutral', 0], [7.6, 'neutral', .01], [7.85, 'surprise', .15], [9.2, 'worried', .5], [12.3, 'worried', .01], [12.5, 'angry', .25]],
    fx: [],
  });
  A.nurlan = K({
    pos: [[0, 6.0, -3.9, 0], [15.1, 6.0, -3.9, 0, LIN], [15.3, 6.0, -3.2, 0, LIN], [15.95, 5.4, -1.2, -120, E.inOutS], [18.9, 5.3, -1.1, -120, E.sm]],
    arms: [[0, 'rest', 0], [15.0, 'rest', .01], [15.6, 'flashLow', .4], [18.9, 'flashLow', .01]],
    head: [[0, 'fwd', 0], [16.0, 'fwd', .01], [16.4, 'l', .3]],
    face: [[0, 'neutral', 0], [15.1, 'worried', .15], [16.7, 'worried', .01], [16.95, 'surprise', .15]],
  });
  return A;
}

function trackB() {
  const B = {};
  const piks = T.piks, FULL = VAR === 'full';
  const t20 = FULL ? 0 : 1.5 - T.click;
  B.ainura = K({
    pos: [[0, ...AI]],
    arms: FULL
      ? [[0, 'counter', 0], [20.5, 'counter', .01], [20.95, 'rToCounter', .25], [21.35, 'phoneChest', .35], [22.4, 'phoneChest', .01], [22.9, 'phoneAim', .45], [24.45, 'phoneAim', .01], [24.9, 'phoneChest', .4], [28.1, 'phoneChest', .01], [28.5, 'rToCounter', .3], [29.0, 'phoneChest', .35], [31.9, 'phoneChest', .01], [32.3, 'counter', .4]]
      : [[0, 'counter', 0], [1.7, 'counter', .01], [2.9, 'rToCounter', .25], [3.3, 'phoneChest', .35], [4.4, 'phoneChest', .01], [4.9, 'phoneAim', .45], [6.5, 'phoneAim', .01], [7.0, 'phoneChest', .4]],
    head: FULL ? [[0, 'fwd', 0], [20.8, 'fwd', .01], [21.1, 'down', .3], [23.0, 'down', .01], [24.5, 'l', .3], [29.0, 'l', .01], [29.5, 'tilt', .4]]
      : [[0, 'fwd', 0], [3.0, 'down', .3], [5.0, 'down', .01], [6.5, 'l', .3]],
    face: FULL ? [[0, 'calm', 0], [20.4, 'calm', .01], [20.9, 'calm', .3], [24.6, 'happy', .4], [29.0, 'happy', .01], [29.0, 'laugh', .2]]
      : [[0, 'calm', 0], [3.0, 'calm', .3], [6.6, 'happy', .4], [9.8, 'laugh', .3]],
  });
  B.ata = K({
    pos: FULL ? [[0, ...QP.ata], [31.9, ...QP.ata], [32.0, ...QP.ata, LIN], [32.25, 2.4, 0.2, -90, E.inOutS], [35.4, -0.7, 0.2, -90, E.sm]] : [[0, ...QP.ata]],
    arms: FULL
      ? [[0, 'holdR', 0], [22.2, 'holdR', .01], [22.7, 'breadOut', .5], [24.3, 'breadOut', .01], [24.7, 'holdR', .4], [28.0, 'holdR', .01], [28.4, 'breadOut', .3], [29.3, 'holdR', .3]]
      : [[0, 'holdR', 0], [4.2, 'holdR', .01], [4.7, 'breadOut', .5], [6.4, 'breadOut', .01], [6.8, 'holdR', .4]],
    head: [[0, 'fwd', 0]],
    face: FULL ? [[0, 'calm', 0], [22.1, 'calm', .01], [22.7, 'surprise', .3], [24.85, 'surprise', .01], [25.1, 'happy', .3]] : [[0, 'calm', 0], [4.5, 'surprise', .3], [6.45, 'happy', .3]],
  });
  B.apa = K({
    pos: FULL ? [[0, ...QP.apa], [piks[2] - .2, ...QP.apa], [piks[2] + .3, 2.0, .0, 66, E.inOutS], [27.9, 2.0, 0, 66], [28.0, 2.0, 0, 66, LIN], [28.85, 3.0, .15, 60, E.inOutS], [32.4, 3.0, .15, 60], [32.5, 3.0, .15, 60, LIN], [32.85, 2.7, .3, -90, E.inOutS], [35.4, -.8, .4, -90, E.sm]]
      : [[0, ...QP.apa], [piks[2] - .2, ...QP.apa], [piks[2] + .3, 2.0, 0, 66, E.inOutS]],
    arms: FULL ? [[0, 'bagR', 0], [piks[2] - .25, 'bagR', .01], [piks[2] + .1, 'holdL', .3], [29.0, 'holdL', .01], [29.4, 'clap', .3], [29.9, 'holdL', .3], [32.4, 'holdL', .01], [32.7, 'waveR', .3], [33.9, 'waveR', .01]]
      : [[0, 'bagR', 0], [piks[2] - .25, 'bagR', .01], [piks[2] + .1, 'holdL', .3], [10.1, 'holdL', .01], [10.5, 'clap', .3]],
    face: FULL ? [[0, 'calm', 0], [20.8, 'surprise', .2], [22.0, 'worried', .3], [24.85, 'worried', .01], [piks[2] + .1, 'happy', .3], [29.0, 'laugh', .3]] : [[0, 'calm', 0], [2.2, 'surprise', .2], [piks[2] + .1, 'happy', .3], [9.7, 'laugh', .3]],
    fx: [],
  });
  B.jyldyz = K({
    pos: FULL ? [[0, ...QP.jyldyz], [piks[1] - .25, ...QP.jyldyz], [piks[1] + .3, 1.15, -.1, 62, E.inOutS], [32.25, 1.15, -.1, 62], [32.4, 1.15, -.1, 62, LIN], [32.65, 0.9, .0, -90, E.inOutS], [35.4, -2.2, 0.1, -90, E.sm]] : [[0, ...QP.jyldyz]],
    arms: [[0, 'holdR', 0]],
    face: FULL ? [[0, 'calm', 0], [20.8, 'surprise', .2], [22.0, 'worried', .3], [piks[1] + .1, 'happy', .3]] : [[0, 'calm', 0]],
  });
  if (!FULL) delete B.jyldyz;
  B.bakyt = K({
    pos: FULL ? [[0, ...QP.bakyt], [piks[0] - .2, ...QP.bakyt], [piks[0] + .35, 1.55, .6, 74, E.inOutS], [32.25, 1.55, .6, 74], [32.4, 1.55, .6, 74, LIN], [32.65, 1.3, .55, -90, E.inOutS], [35.4, -1.8, .5, -90, E.sm]]
      : [[0, ...QP.bakyt], [piks[0] - .2, ...QP.bakyt], [piks[0] + .35, 1.55, .6, 74, E.inOutS]],
    arms: FULL ? [[0, 'front', 0], [31.0, 'front', .01], [31.15, 'eyesHide', .1], [31.9, 'eyesHide', .01], [32.2, 'front', .3]] : [[0, 'front', 0]],
    face: FULL ? [[0, 'happy', 0], [20.8, 'surprise', .2], [22.0, 'worried', .3], [piks[0] + .1, 'laugh', .2], [31.0, 'laugh', .01], [31.45, 'laugh', .1]] : [[0, 'happy', 0], [2.2, 'surprise', .2], [piks[0] + .1, 'laugh', .2]],
    fx: [],
  });
  B.erlan = K({
    pos: FULL ? [[0, ...QP.erlan], [piks[1] - .2, ...QP.erlan], [piks[1] + .35, .25, .05, 58, E.inOutS], [32.25, .25, .05, 58], [32.4, .25, .05, 58, LIN], [32.65, 0.0, .1, -90, E.inOutS], [35.4, -3.2, .15, -90, E.sm]]
      : [[0, ...QP.erlan], [piks[1] - .2, ...QP.erlan], [piks[1] + .35, .25, .05, 58, E.inOutS]],
    arms: FULL ? [[0, 'holdL', 0], [piks[1] + .5, 'holdL', .01], [piks[1] + .8, 'rest', .3], [27.0, 'rest', .01], [27.4, 'holdL', .3]] : [[0, 'holdL', 0]],
    head: FULL ? [[0, 'fwd', 0], [21.5, 'up', .3], [22.2, 'fwd', .3]] : [[0, 'fwd', 0], [2.2, 'up', .3], [3.0, 'fwd', .3]],
    face: FULL ? [[0, 'neutral', 0], [20.75, 'angry', .2], [22.0, 'angry', .01], [25.0, 'surprise', .3], [26.2, 'happy', .3]] : [[0, 'neutral', 0], [1.9, 'angry', .2], [7.5, 'surprise', .3], [8.5, 'happy', .3]],
  });
  B.nurlan = FULL ? K({
    pos: [[0, 6.0, -3.9, 0], [29.4, 6.0, -3.9, 0, LIN], [29.6, 6.0, -3.2, 0, LIN], [30.35, 5.3, -1.2, -115, E.inOutS], [32.0, 5.3, -1.2, -115], [35.4, 5.3, -1.2, -115]],
    arms: [[0, 'rest', 0], [29.3, 'rest', .01], [29.9, 'flashLow', .4], [30.9, 'flashLow', .01], [31.3, 'bagR', .3]],
    head: [[0, 'fwd', 0], [30.4, 'fwd', .01], [30.8, 'l', .3]],
    face: [[0, 'neutral', 0], [29.4, 'happy', .15], [31.4, 'happy', .01], [31.6, 'laugh', .15]],
  }) : null;
  if (!FULL) delete B.nurlan;
  return B;
}
const TRK = { A: VAR === 'full' ? trackA() : null, B: trackB() };
// недостающие позы
N.Chars.ARMS.raiseR = [.06, .14, .12, 2.25, .25, .6, 1, 0];
// подготовка путей и речи
const WHO = { ER: 'erlan', AI: 'ainura', AT: 'ata', NU: 'nurlan', AP: 'apa', BA: 'bakyt' };
const SPEAK = {};
V.voice.forEach(v => { const n = WHO[v.who]; if (!n) return; (SPEAK[n] = SPEAK[n] || []).push([v.at + .03, v.at + (v.dur || 1.5) - .05]); });
['A', 'B'].forEach(a => { const s = TRK[a]; if (!s) return; for (const n in s) { const P = CAST[n]; P.keys = s[n]; prepDist(P, 40); s[n].dist = P.dist; s[n].distStep = P.distStep; } });

/* ------------------------------------------------ улица: апа заходит в магазин */
function poseExt(t) {
  const P = CAST.apaExt;
  const on = VAR === 'full' && t < 1.45;
  P.root.visible = on;
  if (!on) return;
  P.keys = { pos: [[0, 3.6, 7.3, -90], [1.15, -8.9, 6.9, -90, E.inOutS], [1.4, -9.0, 6.0, -90, LIN]], arms: [[0, 'bagR', 0]], head: [[0, 'fwd', 0]], lean: [[0, 'n', 0]], face: [[0, 'calm', 0]], fx: [] };
  if (!P._d) { P._d = true; prepDist(P, 3); P._keys = P.keys; P._dist = P.dist; }
  P.dist = P._dist; P.speak = []; P.fx = [];
  P.keys.pos = [[0, 3.6, 7.3, 90], [0.9, -8.0, 7.2, 90, E.inOutS], [1.1, -9.0, 7.0, 180, E.inOutS], [1.45, -9.0, 5.7, 180, LIN]];
  poseChar(P, t);
}
(function () { const P = CAST.apaExt; P.keys = { pos: [[0, 3.6, 7.3, 90], [0.9, -8.0, 7.2, 90, E.inOutS], [1.1, -9.0, 7.0, 180, E.inOutS], [1.45, -9.0, 5.7, 180, LIN]], arms: [[0, 'bagR', 0]], head: [[0, 'fwd', 0]], lean: [[0, 'n', 0]], face: [[0, 'calm', 0]], fx: [] }; prepDist(P, 3); P._d = true; P._dist = P.dist; })();

/* ------------------------------------------------ экран телефона, касса */
const pcv = Wd.phoneScr, pg = pcv._g;
let lastPhoneKey = '';
function drawPhone(ctt, mode, info) {
  const key = mode + '|' + (info.k || '');
  if (key === lastPhoneKey && !info.anim) return;
  lastPhoneKey = key;
  const w = 320, h = 640;
  pg.fillStyle = '#191919'; pg.fillRect(0, 0, w, h);
  if (mode === 'off') { pcv.needsUpdate = true; return; }
  // шапка
  pg.fillStyle = '#FFB208'; pg.fillRect(0, 0, w, 74);
  pg.fillStyle = '#191919'; pg.font = '800 32px ' + FONT; pg.textAlign = 'left'; pg.textBaseline = 'middle'; pg.fillText('NurCRM', 18, 38);
  pg.font = '700 20px ' + FONT; pg.textAlign = 'right'; pg.fillText(L('офлайн', 'офлайн'), w - 16, 38);
  if (mode === 'sent') {
    pg.fillStyle = '#fff'; pg.font = '800 34px ' + FONT; pg.textAlign = 'center'; pg.fillText(L('Чеки отправлены', 'Чектер жөнөтүлдү'), w / 2, 190);
    pg.fillStyle = '#7FD38C'; pg.font = '800 110px ' + FONT; pg.fillText('✓', w / 2, 320);
    pg.fillStyle = '#FFB208'; pg.font = '800 70px ' + FONT; pg.fillText(info.n + '/5', w / 2, 450);
    pcv.needsUpdate = true; return;
  }
  // видоискатель
  const vx = 20, vy = 94, vw = w - 40, vh = 330;
  pg.fillStyle = '#2b2118'; rrect(pg, vx, vy, vw, vh, 18); pg.fill();
  if (info.bread) {
    pg.fillStyle = '#E7BE74'; pg.beginPath(); pg.ellipse(vx + vw / 2, vy + vh / 2, 100, 90, 0, 0, 7); pg.fill();
    pg.strokeStyle = '#CC9648'; pg.lineWidth = 8; pg.beginPath(); pg.ellipse(vx + vw / 2, vy + vh / 2, 62, 56, 0, 0, 7); pg.stroke();
  }
  const half = info.half || 140, cx = vx + vw / 2, cy = vy + vh / 2, ll = 36;
  pg.strokeStyle = '#FFB208'; pg.lineWidth = 8; pg.lineCap = 'round'; pg.lineJoin = 'round';
  [[-1, -1], [1, -1], [1, 1], [-1, 1]].forEach(([sx, sy]) => { const x = cx + sx * half, y = cy + sy * half * .8; pg.beginPath(); pg.moveTo(x, y - sy * ll); pg.lineTo(x, y); pg.lineTo(x - sx * ll, y); pg.stroke(); });
  if (info.line != null) { pg.fillStyle = '#E53935'; pg.fillRect(cx - half, cy - half * .8 + info.line * half * 1.6, half * 2, 5); }
  if (info.flash) { pg.fillStyle = 'rgba(255,255,255,' + info.flash + ')'; pg.fillRect(vx, vy, vw, vh); }
  // список
  pg.textAlign = 'left'; pg.font = '700 26px ' + FONT;
  const rows = info.rows || [];
  pg.fillStyle = '#2a2a2a'; rrect(pg, 20, 444, w - 40, 170, 14); pg.fill();
  rows.slice(-4).forEach((r, i) => { pg.fillStyle = '#fff'; pg.textAlign = 'left'; pg.fillText(r[0], 34, 474 + i * 36); pg.fillStyle = '#FFB208'; pg.textAlign = 'right'; pg.fillText(r[1], w - 34, 474 + i * 36); });
  pcv.needsUpdate = true;
}

/* ------------------------------------------------ перенос реквизита (держит рука, лежит на столе, плавные «подхваты») */
const _v = new THREE.Vector3(), _q = new THREE.Quaternion(), _e = new THREE.Euler(), _o = new THREE.Vector3();
function handWorld(actor, side, off) {
  const P = CAST[actor], h = P.arms[side === 'L' ? 0 : 1].hand;
  const v = new THREE.Vector3(); h.getWorldPosition(v);
  if (off) { const q = new THREE.Quaternion(); P.root.getWorldQuaternion(q); v.add(new THREE.Vector3(off[0], off[1], off[2]).multiplyScalar(P.spec.scale || 1).applyQuaternion(q)); }
  return v;
}
function targetOf(w, aim) {
  if (w.hand) {
    const p = handWorld(w.actor, w.hand, w.off);
    const P = CAST[w.actor], q = new THREE.Quaternion(); P.root.getWorldQuaternion(q);
    let rq;
    if (w.aim && aim) { const m = new THREE.Matrix4().lookAt(aim, p, new THREE.Vector3(0, 1, 0)); rq = new THREE.Quaternion().setFromRotationMatrix(m); rq.multiply(new THREE.Quaternion().setFromEuler(new THREE.Euler(0, Math.PI, 0))); rq.set(rq.x, rq.y, rq.z, rq.w); }
    else rq = q.clone().multiply(new THREE.Quaternion().setFromEuler(new THREE.Euler(...(w.rot || [0, 0, 0]))));
    return { p, q: rq, s: w.s || 1 };
  }
  return { p: new THREE.Vector3(...w.pos), q: new THREE.Quaternion().setFromEuler(new THREE.Euler(...(w.rot || [0, 0, 0]))), s: w.s || 1 };
}
function carry(prop, wins, t, aim) {
  let i = -1;
  for (let k = 0; k < wins.length; k++) if (wins[k].t0 <= t) i = k;
  if (i < 0 || wins[i].hide) { prop.visible = false; return; }
  prop.visible = true;
  const w = wins[i];
  const cur = targetOf(w, aim);
  let p = cur.p, q = cur.q, s = cur.s;
  if (i > 0 && !wins[i - 1].hide && w.tw && t - w.t0 < w.tw) {
    const u = E.inOutS(cl((t - w.t0) / w.tw)), pr = targetOf(wins[i - 1], aim);
    p = pr.p.clone().lerp(cur.p, u); q = pr.q.clone().slerp(cur.q, u); s = lerp(pr.s, cur.s, u);
    p.y += Math.sin(Math.PI * u) * (w.arc || 0);
  }
  if (w.t0 === t) { /* без анимации */ }
  prop.position.copy(p); prop.quaternion.copy(q); prop.scale.setScalar(s);
}
const FL = Math.PI / 2;
// окна переноса: A (по tau) и B (по t)
const CARRY = { A: {}, B: {} };
CARRY.A.lepyoshka = [{ t0: 0, hand: 'R', actor: 'ata', off: [0, -.02, .05], rot: [0, 0, 0] }, { t0: 16.58, pos: [3.15, 1.06, 0.1], rot: [0, .3, 0], tw: .3, arc: .12 }];
CARRY.A.milk = [{ t0: 0, hand: 'R', actor: 'bakyt', off: [-.03, .0, .05], rot: [0, 0, 0] }];
CARRY.A.seeds = [{ t0: 0, hand: 'L', actor: 'erlan', off: [0, -.04, .02], rot: [0, 0, -.3] }];
CARRY.A.bag = [{ t0: 0, hand: 'R', actor: 'apa', off: [0, -.2, 0], rot: [0, 1.57, 0] }];
CARRY.A.calc = [{ t0: 0, hide: 1 }, { t0: 9.6, pos: [3.95, 1.045, -.12], rot: [0, .3, 0], tw: .01 }];
CARRY.A.notebook = [{ t0: 0, hide: 1 }, { t0: 9.5, pos: [3.95, 1.05, .35], rot: [0, -.25, 0], tw: .01 }];
CARRY.A.phoneFlash = [{ t0: 0, hide: 1 }, { t0: 8.4, hand: 'R', actor: 'erlan', off: [0, 0.0, .03], aim: 1, tw: .3 }, { t0: 15.9, hide: 1 }];
CARRY.A.flashlight = [{ t0: 0, hide: 1 }, { t0: 15.3, hand: 'R', actor: 'nurlan', off: [0, 0, .02], aim: 1 }, { t0: 18.95, hide: 1 }];
CARRY.A.phoneScan = [{ t0: 0, pos: [3.85, 1.04, .3], rot: [-FL, .25, 0] }];
CARRY.B.lepyoshka = [{ t0: 0, hand: 'R', actor: 'ata', off: [0, -.02, .05], rot: [0, 0, 0] }];
CARRY.B.milk = [{ t0: 0, hand: 'R', actor: 'bakyt', off: [-.03, 0, .05] }];
CARRY.B.seeds = [{ t0: 0, hand: 'L', actor: 'erlan', off: [0, -.04, .02], rot: [0, 0, -.3] }];
CARRY.B.bag = [{ t0: 0, hand: 'R', actor: 'apa', off: [0, -.2, 0], rot: [0, 1.57, 0] }];
CARRY.B.loaf = [{ t0: 0, hide: 1 }];
CARRY.B.money = [{ t0: 0, hide: 1 }];
CARRY.B.receipt = [{ t0: 0, hide: 1 }];
CARRY.B.calc = [{ t0: 0, hide: 1 }]; CARRY.B.notebook = [{ t0: 0, hide: 1 }]; CARRY.B.phoneFlash = [{ t0: 0, hide: 1 }];
CARRY.B.flashlight = VAR === 'full' ? [{ t0: 0, hide: 1 }, { t0: 29.9, hand: 'R', actor: 'nurlan', off: [0, 0, .02], aim: 1 }, { t0: 30.95, hide: 1 }] : [{ t0: 0, hide: 1 }];
const PHONE_HAND = { hand: 'R', actor: 'ainura', off: [-.02, .1, .1], rot: [-.35, -.3, 0] };
const PHONE_AIM = { hand: 'R', actor: 'ainura', off: [-.02, .1, .1], rot: [-.35, -.3, 0] };
if (VAR === 'full') {
  CARRY.B.phoneScan = [{ t0: 0, pos: [3.85, 1.04, .3], rot: [-FL, .25, 0] }, Object.assign({ t0: 20.95, tw: .4, arc: .08 }, PHONE_HAND), { t0: 32.2, pos: [3.9, 1.12, .12], rot: [-.55, -.22, 0], tw: .35, arc: .05 }];
  CARRY.B.loaf = [{ t0: 0, hide: 1 }, { t0: 25.9, hand: 'L', actor: 'apa', off: [0, -.02, .04], rot: [0, 1.57, 0] }];
  CARRY.B.money = [{ t0: 0, hide: 1 }, { t0: 28.15, hand: 'L', actor: 'ata', off: [0, -.02, .04], rot: [0, 0, 0] }, { t0: 28.4, hand: 'R', actor: 'ainura', off: [0, -.03, .05], rot: [0, 0, 0], tw: .4, arc: .1 }, { t0: 28.85, hide: 1 }];
  CARRY.B.receipt = [{ t0: 0, hide: 1 }, { t0: 28.55, pos: [3.78, 1.17, .05], rot: [-.1, .2, 0], s: .6 }, { t0: 28.6, hand: 'L', actor: 'ainura', off: [0, -.02, .05], rot: [0, 0, 0], tw: .3 }, { t0: 28.9, hand: 'R', actor: 'ata', off: [0, 0, .05], rot: [0, 0, 0], tw: .4, arc: .1 }, { t0: 29.7, hide: 1 }];
  CARRY.B.lepyoshka = [{ t0: 0, hand: 'R', actor: 'ata', off: [0, -.02, .05], rot: [0, 0, 0] }, { t0: 22.7, pos: [3.46, 1.2, .12], rot: [.35, 0, .15], tw: .4 }, { t0: 25.9, hand: 'R', actor: 'ata', off: [0, -.02, .05], rot: [0, 0, 0], tw: .5 }];
  CARRY.B.phoneScan.splice(2, 0, { t0: 22.7, pos: [3.9, 1.22, .02], rot: [-.12, -.62, 0], tw: .4 }, { t0: 25.9, hand: 'R', actor: 'ainura', off: [-.02, .1, .1], rot: [-.35, -.3, 0], tw: .4 });
  CARRY.B.bag = [{ t0: 0, hand: 'R', actor: 'apa', off: [0, -.2, 0], rot: [0, 1.57, 0] }, { t0: 27.9, pos: [2.6, .13, .75], rot: [0, .6, 0], tw: .4 }, { t0: 32.5, hand: 'R', actor: 'apa', off: [0, -.2, 0], rot: [0, 1.57, 0], tw: .4 }];
} else {
  CARRY.B.phoneScan = [{ t0: 0, pos: [3.85, 1.04, .3], rot: [-FL, .25, 0] }, Object.assign({ t0: 2.9, tw: .4, arc: .08 }, PHONE_HAND), { t0: 4.7, pos: [3.9, 1.22, .02], rot: [-.12, -.62, 0], tw: .4 }, { t0: 7.0, hand: 'R', actor: 'ainura', off: [-.02, .1, .1], rot: [-.35, -.3, 0], tw: .4 }];
  CARRY.B.lepyoshka = [{ t0: 0, hand: 'R', actor: 'ata', off: [0, -.02, .05], rot: [0, 0, 0] }, { t0: 4.7, pos: [3.46, 1.2, .12], rot: [.35, 0, .15], tw: .4 }, { t0: 7.0, hand: 'R', actor: 'ata', off: [0, -.02, .05], rot: [0, 0, 0], tw: .5 }];
  CARRY.B.loaf = [{ t0: 0, hide: 1 }, { t0: 7.5, hand: 'L', actor: 'apa', off: [0, -.02, .04], rot: [0, 1.57, 0] }];
}

/* ------------------------------------------------ камера */
function spl(keys, t) {
  const n = keys.length, d = keys[0].length - 1;
  if (t <= keys[0][0]) return keys[0].slice(1);
  if (t >= keys[n - 1][0]) return keys[n - 1].slice(1);
  let i = 0; while (i < n - 2 && t > keys[i + 1][0]) i++;
  const a = keys[i], b = keys[i + 1], h = b[0] - a[0], u = (t - a[0]) / h, u2 = u * u, u3 = u2 * u;
  const out = [];
  for (let j = 1; j <= d; j++) {
    const m0 = i > 0 ? (b[j] - keys[i - 1][j]) / (b[0] - keys[i - 1][0]) : (b[j] - a[j]) / h;
    const m1 = i < n - 2 ? (keys[i + 2][j] - a[j]) / (keys[i + 2][0] - a[0]) : (b[j] - a[j]) / h;
    out.push((2 * u3 - 3 * u2 + 1) * a[j] + (u3 - 2 * u2 + u) * h * m0 + (-2 * u3 + 3 * u2) * b[j] + (u3 - u2) * h * m1);
  }
  return out;
}
// shots: [{t0, t1, keys:[[t, px,py,pz, lx,ly,lz, fov]]}]
const CAMS = {
  full: {
    A: [
      { t0: 0, t1: 1.15, keys: [[0, -3.6, 1.7, 14.8, -4.6, 1.9, 6, 38], [1.15, -4.2, 1.7, 13.9, -4.8, 1.85, 6, 36]] },
      { t0: 1.15, t1: 7.4, keys: [[1.15, -4.2, 1.7, 13.9, -4.8, 1.85, 6, 36], [1.55, -8.7, 1.6, 8.4, -9.0, 1.5, 4.0, 46], [1.95, -9.0, 1.6, 5.2, -6.5, 1.4, 2.2, 54], [2.5, -7.6, 1.6, 2.7, -2.4, 1.35, 0.9, 54], [3.0, -5.0, 1.55, 2.3, 0.6, 1.3, 0.2, 50],
        [3.6, -2.2, 1.5, 3.2, 2.4, 1.25, 0.0, 46], [4.4, 0.0, 1.5, 4.5, 3.0, 1.2, -0.1, 41], [7.4, 0.5, 1.5, 4.7, 3.1, 1.2, -0.1, 38]] },
      { t0: 7.4, t1: 8.9, keys: [[7.4, 0.5, 1.5, 4.7, 3.1, 1.2, -0.1, 38], [8.9, 0.8, 1.5, 4.6, 3.2, 1.2, -0.1, 36]] },
      { t0: 8.9, t1: 12.9, keys: [[8.9, 3.2, 1.58, 2.7, 4.4, 1.42, -0.25, 32], [12.9, 3.5, 1.55, 2.4, 4.45, 1.38, -0.25, 25]] },
      { t0: 12.9, t1: 13.15, keys: [[12.9, 1.7, 1.55, 2.2, 2.75, 1.5, 0.05, 30], [13.15, 1.7, 1.55, 2.2, 2.75, 1.5, 0.05, 30]] },
      { t0: 13.15, t1: 13.7, keys: [[13.15, 2.9, 1.45, 2.2, 1.85, 1.42, 0.05, 30], [13.7, 2.95, 1.45, 2.2, 1.85, 1.42, 0.05, 28]] },
      { t0: 13.7, t1: 14.05, keys: [[13.7, 2.1, 1.2, 2.5, 1.1, 1.1, 0.25, 34], [14.05, 2.1, 1.2, 2.5, 1.1, 1.1, 0.25, 32]] },
      { t0: 14.05, t1: 14.85, keys: [[14.05, 3.8, 1.6, 1.9, 2.75, 1.65, 0.05, 28], [14.85, 3.9, 1.6, 1.9, 2.75, 1.65, 0.05, 26]] },
      { t0: 14.85, t1: 15.25, keys: [[14.85, 0.4, 1.5, 4.6, 1.5, 1.45, 0, 38], [15.25, 0.5, 1.5, 4.6, 1.5, 1.45, 0, 37]] },
      { t0: 15.25, t1: 20, keys: [[15.25, 1.0, 1.75, 5.5, 1.9, 1.15, 0, 46], [18.0, -0.3, 1.8, 5.55, 1.2, 1.15, 0, 48]] },
    ],
    B: [
      { t0: 19.2, t1: 22.4, keys: [[19.2, 0.5, 1.5, 4.7, 3.1, 1.2, -0.1, 38], [20.4, 0.55, 1.5, 4.7, 3.15, 1.2, -0.1, 38], [21.0, 1.0, 1.5, 4.3, 3.4, 1.25, -0.2, 36], [22.4, 1.5, 1.5, 3.5, 3.9, 1.3, -0.2, 33]] },
      { t0: 22.4, t1: 25.3, keys: [[22.4, 2.6, 1.5, 1.6, 3.5, 1.22, 0.0, 30], [25.3, 2.8, 1.5, 1.5, 3.55, 1.2, 0.0, 28]] },
      { t0: 25.3, t1: 28.1, keys: [[25.3, 0.7, 1.45, 4.4, 1.0, 1.28, 0.15, 40], [28.1, 1.9, 1.45, 4.2, 2.0, 1.28, 0.1, 40]] },
      { t0: 28.1, t1: 30.1, keys: [[28.1, 2.3, 1.55, 2.5, 3.7, 1.15, -0.05, 33], [30.1, 2.5, 1.55, 2.4, 3.8, 1.15, -0.05, 30]] },
      { t0: 30.1, t1: 32.0, keys: [[30.1, -2.4, 1.7, 5.3, 3.6, 1.2, -0.3, 50], [32.0, -1.9, 1.7, 5.3, 3.5, 1.2, -0.3, 50]] },
      { t0: 32.0, t1: 36, keys: [[32.0, 0.6, 1.55, 4.7, 0.6, 1.25, 0.0, 44], [34.2, -2.6, 1.55, 4.5, -2.0, 1.25, 0.0, 42]] },
    ],
  },
  short: {
    A: null,
    B: [
      { t0: 0, t1: 2.9, keys: [[0, 1.9, 1.55, 3.6, 3.5, 1.3, -0.2, 50], [2.9, 2.3, 1.55, 3.3, 3.6, 1.3, -0.2, 46]] },
      { t0: 2.9, t1: 6.0, keys: [[2.9, 2.2, 1.55, 3.2, 3.65, 1.22, 0.05, 56], [6.0, 2.5, 1.55, 3.0, 3.6, 1.2, 0.05, 50]] },
      { t0: 6.0, t1: 12, keys: [[6.0, 1.4, 1.5, 3.7, 1.9, 1.3, 0.2, 54], [10.8, 2.4, 1.5, 3.6, 2.4, 1.3, 0.2, 52]] },
    ],
  },
};
function camAt(x, act) {
  const set = CAMS[VAR][act] || CAMS[VAR].B;
  let sh = set[set.length - 1];
  for (const s of set) if (x >= s.t0 && x < s.t1) { sh = s; break; }
  if (x < set[0].t0) sh = set[0];
  return spl(sh.keys, x);
}

/* ------------------------------------------------ вспомогательные спрайты и лучи */
const glowTex = ctex(128, 128, (g, w, h) => { const gr = g.createRadialGradient(64, 64, 0, 64, 64, 64); gr.addColorStop(0, 'rgba(255,214,120,1)'); gr.addColorStop(.35, 'rgba(255,170,40,.45)'); gr.addColorStop(1, 'rgba(255,150,0,0)'); g.fillStyle = gr; g.fillRect(0, 0, w, h); });
const phoneGlow = new THREE.Sprite(new THREE.SpriteMaterial({ map: glowTex, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, opacity: 0 }));
phoneGlow.scale.set(1.1, 1.1, 1); scene.add(phoneGlow);
const dustTex = ctex(64, 64, (g, w, h) => { const gr = g.createRadialGradient(32, 32, 0, 32, 32, 32); gr.addColorStop(0, 'rgba(240,235,225,.9)'); gr.addColorStop(1, 'rgba(240,235,225,0)'); g.fillStyle = gr; g.fillRect(0, 0, w, h); });
const dusts = [0, 1, 2, 3, 4].map(() => { const s = new THREE.Sprite(new THREE.SpriteMaterial({ map: dustTex, transparent: true, depthWrite: false, opacity: 0 })); scene.add(s); return s; });
const clickTex = ctex(128, 128, (g, w, h) => { g.strokeStyle = '#FFB208'; g.lineWidth = 8; g.lineCap = 'round'; for (let i = 0; i < 8; i++) { const a = i * Math.PI / 4; g.beginPath(); g.moveTo(64 + Math.cos(a) * 30, 64 + Math.sin(a) * 30); g.lineTo(64 + Math.cos(a) * 56, 64 + Math.sin(a) * 56); g.stroke(); } });
const clickFx = new THREE.Sprite(new THREE.SpriteMaterial({ map: clickTex, transparent: true, depthWrite: false, depthTest: false, opacity: 0 })); clickFx.scale.set(.5, .5, 1); clickFx.position.set(5.05, 1.35, -2.8); scene.add(clickFx);
function mkBeam(color) {
  const g = new THREE.ConeGeometry(1, 1, 28, 1, true); g.translate(0, -.5, 0); g.rotateX(-Math.PI / 2);   // вершина в начале, ось +z
  const pos = g.attributes.position, col = new Float32Array(pos.count * 3);
  for (let i = 0; i < pos.count; i++) { const k = 1 - cl(pos.getZ(i)) * .55; col[i * 3] = k; col[i * 3 + 1] = k; col[i * 3 + 2] = k; }
  g.setAttribute('color', new THREE.BufferAttribute(col, 3));
  const m = new THREE.Mesh(g, new THREE.MeshBasicMaterial({ color, vertexColors: true, transparent: true, opacity: 0, blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide }));
  m.renderOrder = 4; m.frustumCulled = false; scene.add(m); return m;
}
const beam = mkBeam(0xCFE2FF);

/* ------------------------------------------------ главный кадр */
function actOf(t) {
  if (VAR === 'short') return { act: 'B', tau: t };
  if (t < T.rewind[0]) return { act: 'A', tau: t };
  if (t < T.swap) { const u = E.inOutS(cl((t - T.rewind[0]) / (T.rewind[1] - T.rewind[0]))); return { act: 'A', tau: lerp(T.rewind[0], 3.2, u), rewind: true }; }
  return { act: 'B', tau: t };
}
function lightLevel(t, tau, act) {   // 1 — лампы горят; 0 — тьма (с двухкадровым «щелчком»)
  if (VAR === 'short') return t < T.click ? 1 : (t < T.click + .034 ? .4 : 0);
  if (act === 'A') { if (tau < T.clickA) return 1; if (tau < T.clickA + .034) return .4; return tau < 18.99 ? 0 : 1; }
  if (t < T.click) return 1; if (t < T.click + .034) return .4; if (t < T.lightOn) return 0; if (t < T.lightOn + .067) return .6; return 1;
}
const Lg = Wd.lights;
const headV = (n) => { const v = new THREE.Vector3(); CAST[n].head.getWorldPosition(v); return v; };
const darkBG = new THREE.Color(0x070a14), dayFog = Wd.fogDay;
const subsInfo = { rows: [], k: '' };
let lastReg = '';
let SHAKE = 0;

window.__render = function (t) {
  const { act, tau, rewind } = actOf(t);
  const ctt = act === 'A' ? tau : t;
  const lvl = lightLevel(t, tau, act), dark = 1 - lvl;
  // --- персонажи
  const set = act === 'A' ? TRK.A : TRK.B;
  NAMES.forEach(n => {
    const P = CAST[n], k = set && set[n];
    if (!k) { P.root.visible = false; return; }
    P.keys = k; P.fx = k.fx || []; P.speak = SPEAK[n] || []; P.dist = k.dist; P.distStep = k.distStep;
    poseChar(P, ctt);
    P.root.visible = n === 'nurlan' ? P.root.position.z > -3.1 : true;
  });
  const P_ap = CAST.apa; if (VAR === 'full' && ctt < 1.5 && act === 'A') P_ap.root.visible = false;
  poseExt(act === 'A' ? ctt : 99);
  scene.updateMatrixWorld(true);
  // --- мерцание буквы вывески
  if (VAR === 'full' && t < 2.4) Wd.drawSign(Math.sin(t * 17) > .55 || (t > .7 && t < .86) ? 0.15 : 1);
  // --- машина
  Wd.car.visible = VAR === 'full' && t < 2.3;
  Wd.car.position.x = lerp(14, -16, seg(t, .55, 1.5, E.lin));
  // --- экран кассы
  let regMode = 'on';
  if (act === 'A') regMode = (tau >= 4.45 && tau < 6.0) ? 'price' : (tau < T.clickA ? 'on' : (tau < 18.99 ? 'off' : 'on')); else regMode = lvl < .5 && t < (T.lightOn) ? 'off' : 'on';
  if (VAR === 'short' && t >= T.click) regMode = 'off';
  if (regMode !== lastReg) { Wd.drawReg(regMode); lastReg = regMode; }
  // --- свет
  const lampL = lvl;
  Lg.hemi.intensity = lerp(.32, 1.7, lampL); Lg.hemi.color.setHex(lampL > .5 ? 0xFFF1D8 : 0x2a3f7a); Lg.hemi.groundColor.setHex(lampL > .5 ? 0xD8BE98 : 0x0c0f1c);
  Lg.key.intensity = 3.2 * lampL; Wd.ceilMat.emissiveIntensity = .3 * lampL; Lg.moon.intensity = lerp(2.2, 0, lampL);
  Lg.lampSpots.forEach(s => { s.intensity = 48 * lampL; });
  Wd.lamps.forEach(l => { l.bulbMat.emissiveIntensity = 1.6 * lampL; l.cone.material.opacity = .07 * lampL; });
  Wd.winMat.color.setRGB(lerp(.28, 1, lampL), lerp(.36, 1, lampL), lerp(.7, 1, lampL));
  const out = VAR === 'full' && t < 2.0;
  scene.fog = out ? Wd.fogDay : null;
  // --- лучи: фонарик (акт А) и телефон (акт Б)
  let flashI = 0, phoneUp = 0, flashSrc = null, flashTgt = null;
  if (act === 'A' && tau >= 8.6 && tau < 18.95) {
    const srcName = tau < 15.9 ? 'erlan' : 'nurlan';
    const fade = Math.min(seg(tau, 8.6, .4), 1 - seg(tau, 18.4, .55));
    flashI = fade;
    const prop = tau < 15.9 ? PR.phoneFlash : PR.flashlight;
    flashSrc = new THREE.Vector3(); prop.getWorldPosition(flashSrc);
    let tn = 'ainura';
    if (tau >= 12.9 && tau < 13.15) tn = 'ata'; else if (tau >= 13.15 && tau < 13.7) tn = 'apa'; else if (tau >= 13.7 && tau < 14.05) tn = 'bakyt'; else if (tau >= 14.05 && tau < 14.85) tn = 'ata';
    flashTgt = headV(tn);
    const jx = (hash(Math.floor(tau * 10)) - .5) * .1, jy = (hash(Math.floor(tau * 10) + 50) - .5) * .08;
    flashTgt.x += jx; flashTgt.y += jy - .02;
    if (tau >= 15.9) flashTgt.y -= .15;
    flashSrc.add(new THREE.Vector3(0, 0, 0));
  }
  Lg.flash.intensity = flashI * 75;
  if (flashSrc) { Lg.flash.position.copy(flashSrc); Lg.flash.target.position.copy(flashTgt); Lg.flash.angle = tau < 15.9 ? .26 : .34; Lg.flash.color.setHex(tau < 15.9 ? 0xDCEBFF : 0xFFF3D8); }
  const bi = flashSrc ? flashI * (dark > .5 ? 1 : 0) : 0;
  beam.visible = bi > .01;
  if (flashSrc && beam.visible) {
    const len = flashSrc.distanceTo(flashTgt);
    beam.position.copy(flashSrc); beam.lookAt(flashTgt);
    const r = Math.tan(Lg.flash.angle) * len * .75; beam.scale.set(r, r, len * 1.04);
    beam.material.opacity = .07 * bi; beam.material.color.setHex(tau < 15.9 ? 0xCFE2FF : 0xFFEFD0);
  }
  // свет телефона
  const gT = T.glow;
  let phoneW = null;
  if (act === 'B' && ctt >= gT && lvl < .5) {
    phoneUp = seg(ctt, gT, .32, E.out3);
    phoneW = new THREE.Vector3(); PR.phoneScan.getWorldPosition(phoneW);
  } else if (act === 'B' && ctt >= gT && ctt < (VAR === 'full' ? T.lightOn - .2 : 99)) {
    phoneUp = 0;
  }
  const breath = 1 + .04 * Math.sin(2 * Math.PI * ctt / 2.4);
  const lampOnPhone = act === 'B' && ctt >= gT && lvl < .5 ? 1 : 0;
  const fadeEnd = VAR === 'full' ? 1 - seg(ctt, T.lightOn - .2, .2) : 1;
  Lg.phoneP.intensity = 6.5 * phoneUp * breath * fadeEnd * (lampOnPhone ? 1 : 0);
  Lg.phoneS.intensity = 26 * phoneUp * breath * fadeEnd * (lampOnPhone ? 1 : 0);
  if (phoneW) {
    Lg.phoneP.position.copy(phoneW).add(new THREE.Vector3(-.12, .05, .35));
    Lg.phoneS.position.copy(phoneW).add(new THREE.Vector3(-.1, .0, .15));
    const wide = (VAR === 'full' ? seg(ctt, 24.7, 1.0) + seg(ctt, 27.8, 1.2) : seg(ctt, 6.8, 1.0));
    Lg.phoneS.target.position.set(phoneW.x - 2.0 - wide * .5, phoneW.y - .3, phoneW.z + .5); Lg.phoneS.angle = 0.75 + wide * .15;
  }
  phoneGlow.material.opacity = (phoneW ? .75 * phoneUp * fadeEnd : 0);
  if (phoneW) phoneGlow.position.copy(phoneW).add(new THREE.Vector3(0, .02, .08));
  // --- реквизит
  const aimPt = flashTgt;
  Object.keys(PR).forEach(name => {
    if (name === 'sweat') return;
    const wins = (act === 'A' ? CARRY.A : CARRY.B)[name];
    if (!wins) { PR[name].visible = false; return; }
    carry(PR[name], wins, ctt, aimPt);
  });
  // фонарь-ручка: лампочка телефона-фонарика/фонарика светится
  // --- экран телефона Айнуры
  PR.phoneScan.userData.scr.visible = true;
  (function () {
    let mode = 'off', info = { k: '' };
    if (act === 'B' && ctt >= gT) {
      const rows = [];
      const t0s = [T.pik0, ...T.piks];
      const names = [[L('Лепёшка', 'Нан'), '30 с'], [L('Молоко', 'Сүт'), '65 с'], [L('Семечки', 'Күн карама'), '40 с'], [L('Хлеб', 'Нан (батон)'), '35 с']];
      t0s.forEach((tt, i) => { if (ctt >= tt) rows.push(names[i]); });
      mode = 'cam';
      const aiming = ctt >= T.hud;
      info = { rows, bread: aiming && ctt < T.pik0 + 4, half: aiming ? lerp(150, 85, seg(ctt, T.hud, T.pik0 - T.hud)) : 150, line: aiming && ctt < T.pik0 ? (.5 + .5 * Math.sin(ctt * 9)) : null, flash: (ctt >= T.pik0 && ctt < T.pik0 + .12) ? .6 * (1 - (ctt - T.pik0) / .12) : 0, anim: 1 };
      T.piks.forEach(p => { if (ctt >= p && ctt < p + .1) info.flash = .5 * (1 - (ctt - p) / .1); });
      info.k = rows.length + '|' + Math.round((info.half || 0) / 3) + '|' + (info.line != null ? Math.round(info.line * 30) : 'n') + '|' + (info.flash ? Math.round(info.flash * 10) : 0) + '|' + (info.bread ? 1 : 0);
      if (VAR === 'full' && ctt >= T.sentA && ctt < T.sentB) { mode = 'sent'; info = { n: Math.min(5, Math.floor(1 + (ctt - T.sentA) * 3.2)), k: 'n' + Math.min(5, Math.floor(1 + (ctt - T.sentA) * 3.2)) }; }
    } else if (act === 'B' && lvl > .5 && ctt < T.click) { mode = 'off'; }
    if (VAR === 'full' && act === 'B' && ctt >= T.lightOn && !(ctt >= T.sentA && ctt < T.sentB) && ctt < T.sentA) { mode = 'cam'; info = { rows: [[L('Лепёшка', 'Нан'), '30 с'], [L('Молоко', 'Сүт'), '65 с'], [L('Семечки', 'Күн карама'), '40 с'], [L('Хлеб', 'Нан (батон)'), '35 с']], k: 'done', half: 85 }; }
    drawPhone(ctt, mode, info);
  })();
  PR.phoneScan.userData.scr.material.color.setScalar(mode_bright(act, ctt, lvl));
  // лампа телефона-фонарика/фонарика всегда ярко-белая
  // --- пыль и пот (К5)
  if (VAR === 'full' && act === 'A' && tau >= 10.2 && tau < 11.1) {
    const u = (tau - 10.2) / .9, base = new THREE.Vector3(3.95, 1.1, .35);
    dusts.forEach((s, i) => { const a = i * 1.26 + .4; s.position.set(base.x + Math.cos(a) * u * .35 * (.6 + .15 * i), base.y + u * .25 + Math.sin(a) * .06, base.z + Math.sin(a) * u * .2); s.scale.setScalar((.06 + .22 * u) * (.7 + .1 * i)); s.material.opacity = (1 - u) * .8; s.visible = true; });
  } else dusts.forEach(s => { s.visible = false; });
  if (VAR === 'full' && act === 'A' && tau >= 10.4 && tau < 11.6) {
    const hp = headV('ainura'), u = (tau - 10.4) / 1.2;
    PR.sweat.visible = true; PR.sweat.position.set(hp.x + .1 - .03 * u, hp.y + .1 - .18 * u, hp.z + .17 + .02 * u); PR.sweat.scale.setScalar(1 + .3 * u);
  } else PR.sweat.visible = false;
  // --- щелчок выключателя
  let clk = 0;
  const clicks = VAR === 'full' ? (act === 'A' ? [T.clickA] : [T.click, T.lightOn]) : [T.click];
  clicks.forEach(c => { if (ctt >= c && ctt < c + .35) clk = Math.max(clk, 1 - (ctt - c) / .35); });
  clickFx.material.opacity = clk; clickFx.scale.setScalar(.35 + (1 - clk) * .5);
  // --- глаза в темноте
  NAMES.forEach(n => {
    const P = CAST[n]; if (!P.root.visible) return;
    const hp = headV(n);
    let lit = 0;
    if (flashSrc && flashI > 0) {
      const d = flashTgt.clone().sub(flashSrc).normalize(), v = hp.clone().sub(flashSrc), dist = v.length();
      const ang = Math.acos(cl(d.dot(v.normalize()), -1, 1));
      lit = Math.max(lit, cl(1 - (ang - Lg.flash.angle * .7) / (Lg.flash.angle * .6)) * flashI);
    }
    if (phoneW) lit = Math.max(lit, cl(1.3 - hp.distanceTo(phoneW) / 2.6) * phoneUp * fadeEnd);
    const fadeIn = dark > .5 ? cl((ctt - (act === 'A' ? T.clickA : (VAR === 'full' && ctt > T.lightOn - 1 ? 0 : T.click)) - .07) / .12) : 0;
    P.eyeMat.emissiveIntensity = dark * 1.9 * (1 - lit) * (dark > .5 ? 1 : 0);
  });
  // --- камера
  const c = camAt(ctt, act);
  let shk = 0;
  clicks.forEach(cc => { if (ctt >= cc && ctt < cc + .22) shk = Math.max(shk, (1 - (ctt - cc) / .22)); });
  let sh = [0, 0];
  if (rewind) { const u = (t - T.rewind[0]) / (T.rewind[1] - T.rewind[0]); const a = Math.sin(Math.PI * cl(u)); sh = [(hash(Math.floor(t * 30)) - .5) * .09 * a, (hash(Math.floor(t * 30) + 7) - .5) * .05 * a]; }
  camera.position.set(c[0] + sh[0] + (hash(Math.floor(t * 40)) - .5) * .02 * shk, c[1] + sh[1] + (hash(Math.floor(t * 40) + 3) - .5) * .02 * shk, c[2]);
  camera.lookAt(c[3], c[4], c[5]);
  camera.fov = c[6]; camera.updateProjectionMatrix();
  if (PORTRAIT) camera.fov = c[6];
  renderer.render(scene, camera);
  window.N3.frame = { t, act, tau, ctt, lvl, rewind: !!rewind, cam: c, phoneW: phoneW && phoneW.clone(), breadW: (function () { const v = new THREE.Vector3(); PR.lepyoshka.getWorldPosition(v); return v; })() };
  window.N3.UI && window.N3.UI.update(t, window.N3.frame);
};
function mode_bright(act, ctt, lvl) { return act === 'B' && ctt >= T.glow ? 1 : (lvl > .5 ? .55 : .0); }
window.N3.T = T; window.N3.TRK = TRK;
window.__ready = Promise.resolve();
// строим инстансы товаров и первый кадр
Wd.buildInst();
})();
