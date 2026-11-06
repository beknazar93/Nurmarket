/* 2026-10-29: персонажи «из примитивов» (капсулы/сферы/цилиндры) + простой скелет из групп и анимация как функция времени.
   Лицо смотрит в +z локальной системы; левая рука персонажа — на +x. Углы рук: [вперёд, в сторону, сгиб локтя]. */
(function () {
'use strict';
const { THREE, scene, M, gBox, gSph, gCyl, gCap, gTor, gRB, mesh, grp, ctex, rrect, FONT, L, E, cl, lerp, seg, hash, kf } = window.N3;
const DEG = Math.PI / 180;
const SKIN_SHADE = c => c;

/* ------------------------------------------------ позы (таблицы) */
// руки: [Lf,La,Lb, Rf,Ra,Rb, качание L, качание R]
const ARMS = {
  rest:      [.06, .14, .12, .06, .14, .12, 1, 1],
  front:     [.5, .1, 1.1, .5, .1, 1.1, 0, 0],
  counter:   [.75, .2, .7, .75, .2, .7, 0, 0],
  rToCounter:[.15, .12, .15, .95, .18, .65, 0, 0],
  holdR:     [.06, .14, .12, .6, .05, 1.2, 1, 0],
  holdL:     [.6, .05, 1.2, .06, .14, .12, 0, 1],
  breadOut:  [.06, .14, .12, 1.15, .05, .35, 1, 0],
  breadOutL: [1.15, .05, .35, .06, .14, .12, 0, 1],
  phoneChest:[.5, .1, 1.05, 1.0, .1, 1.25, 0, 0],
  phoneAim:  [.55, .12, 1.0, 1.28, .0, .45, 0, 0],
  phoneDown: [.3, .1, .6, .4, .1, .9, 0, 0],
  flashUp:   [.06, .14, .12, 1.45, .1, .55, 1, 0],
  flashLow:  [.06, .14, .12, 1.0, .08, .75, 1, 0],
  flashRL:   [1.0, .08, .75, .06, .14, .12, 0, 1],
  shrug:     [.35, .75, 1.35, .35, .75, 1.35, 0, 0],
  cheer:     [2.7, .3, .15, 2.7, .3, .15, 0, 0],
  waveR:     [.06, .14, .12, 2.5, .45, .7, 1, 0],
  fistsUp:   [1.7, .25, 1.0, 1.7, .25, 1.0, 0, 0],
  hips:      [.0, .75, 1.7, .0, .75, 1.7, 0, 0],
  pointR:    [.06, .14, .12, 1.5, .12, .1, 1, 0],
  eyesHide:  [1.9, .3, 2.2, 1.9, .3, 2.2, 0, 0],
  headHand:  [.06, .14, .12, 2.3, .4, 2.0, 1, 0],
  calcTap:   [.8, .15, .9, .85, .15, .95, 0, 0],
  squeeze:   [.2, .1, .5, .2, .1, .5, 0, 0],
  bagR:      [.06, .14, .12, .06, .12, .1, 1, 0],
  handsOut:  [.9, .35, .6, .9, .35, .6, 0, 0],
  clap:      [1.0, .05, 1.4, 1.0, .05, 1.4, 0, 0],
};
// голова: [поворот (yaw), наклон вниз (pitch), крен]
const HEADS = { fwd: [0, 0, 0], l: [.45, 0, 0], r: [-.45, 0, 0], down: [0, .35, 0], up: [0, -.25, 0], tilt: [0, .05, .18], back: [-.6, 0, 0] };
// корпус: [наклон вперёд, поворот, крен]
const LEANS = { n: [0, 0, 0], fwd: [.2, 0, 0], fwd2: [.55, 0, 0], back: [-.12, 0, 0], tw: [.05, .25, 0], twn: [.05, -.25, 0] };
// лицо: [улыбка, рот открыт, брови (злость +), брови вверх, прищур]
const FACES = {
  neutral: [.2, 0, 0, 0, 0], happy: [1, .25, -.1, .3, .3], laugh: [1, .8, -.15, .4, .6], angry: [-.7, .05, .95, -.1, .35], sad: [-.8, 0, -.8, .25, .1],
  surprise: [0, .9, -.2, .9, 0], focus: [.25, 0, .2, -.05, .25], worried: [-.3, .05, -.5, .45, 0], squint: [-.2, 0, .3, -.2, .75], calm: [.5, 0, -.05, .1, .1],
};

function mix(a, b, u) { return a.map((v, i) => v + (b[i] - v) * u); }
// последовательность поз: keys = [[tEnd, 'имя', dur], ...]; первая поза — исходная
function seq(keys, t, table) {
  const get = k => (typeof k === 'string' ? table[k] : k);
  let last = get(keys[0][1]);
  for (let i = 1; i < keys.length; i++) {
    const [te, name, d] = keys[i], ts = te - d, tg = get(name);
    if (t < ts) return last;
    if (t < te) return mix(last, tg, E.inOutS((t - ts) / Math.max(1e-4, d)));
    last = tg;
  }
  return last;
}

/* ------------------------------------------------ материалы лица */
const mWhite = M(0xFFFFFF, { r: .4 }), mBlack = M(0x15110f, { r: .3 });
const mMouth = M(0x6B1D1D, { r: .6 }), mTeeth = M(0xFFFFFF, { r: .5 });

function buildChar(spec) {
  const s = spec.scale || 1;
  const skin = M(spec.skin || 0xF2C29B, { r: .75 });
  const skinD = M(spec.skinD || 0xDDA77F, { r: .75 });
  const root = new THREE.Group(); scene.add(root);
  const body = grp(root); body.scale.setScalar(s);
  const P = { root, body, spec, name: spec.name };
  // ---- таз и ноги
  const pelvis = grp(body, 0, .82, 0); P.pelvis = pelvis;
  const pantsM = M(spec.pants || 0x3a3f4a, { r: .85 }), shoeM = M(spec.shoes || 0x222222, { r: .6 });
  const legs = [];
  [1, -1].forEach(sd => {
    const hip = grp(pelvis, sd * .09, 0, 0);
    mesh(hip, gCap(.078, .26, 4, 12), pantsM, 0, -.19, 0);
    const knee = grp(hip, 0, -.38, 0);
    const bareShin = spec.bareLegs ? skin : pantsM;
    mesh(knee, gCap(.066, .26, 4, 12), bareShin, 0, -.19, 0);
    const foot = grp(knee, 0, -.38, 0);
    mesh(foot, gRB(.125, .075, .27, .03), shoeM, 0, -.02, .05);
    legs.push({ hip, knee, foot });
  });
  P.legs = legs; // [0] — левая (+x), [1] — правая (−x)
  if (spec.skirt) {
    const sk = spec.skirt; // {color, len, r0, r1}
    mesh(pelvis, new THREE.CylinderGeometry(sk.r0 || .17, sk.r1 || .27, sk.len, 22, 1, true), M(sk.color, { r: .9, side: THREE.DoubleSide }), 0, -sk.len / 2 + .02, 0, { cast: true });
  }
  // ---- торс
  const torso = grp(pelvis, 0, 0, 0); P.torso = torso;
  const shirtM = M(spec.top || 0x2F5FD0, { r: .85 });
  const tz = spec.torsoScale || [1, 1, .74];
  mesh(torso, gCap(.17, .3, 5, 16), shirtM, 0, .29, 0, { s: tz });
  mesh(torso, gCap(.14, .14, 4, 12), pantsM, 0, .02, 0, { s: [1, 1, .8] });   // низ (пояс)
  // шея и голова
  mesh(torso, gCyl(.05, .055, .14, 10), skin, 0, .66, 0);
  const head = grp(torso, 0, .84, 0); P.head = head;
  const hs = spec.headScale || 1;
  head.scale.setScalar(hs);
  mesh(head, gSph(.19, 26, 20), skin, 0, 0, 0, { s: [1, 1.04, .98] });
  [1, -1].forEach(sd => mesh(head, gSph(.036, 10, 8), skin, sd * .185, -.005, 0, { s: [.6, 1, .9] }));
  mesh(head, gSph(.028, 10, 8), skinD, 0, -.015, .185);   // нос
  // глаза
  const eyeMat = new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: .35, emissive: 0xffffff, emissiveIntensity: 0 });
  P.eyeMat = eyeMat;
  const eyes = [];
  [1, -1].forEach(sd => {
    const eg = grp(head, sd * .075, .035, .165);
    const sc = mesh(eg, gSph(.044, 14, 10), eyeMat, 0, 0, 0, { s: [1, 1.15, .55], cast: false });
    const pu = mesh(eg, gSph(.024, 10, 8), mBlack, 0, 0, .014, { s: [1, 1, .6], cast: false });
    const brow = mesh(head, gRB(.085, .018, .014, .006), M(spec.brow || 0x2a1d17, { r: .8 }), sd * .075, .108, .176, { cast: false });
    eyes.push({ eg, sc, pu, brow, sd });
  });
  P.eyes = eyes;
  const mouthG = grp(head, 0, -.07, .176); P.mouthG = mouthG;
  const smileM = M(0x7A2A25, { r: .6 });
  const smile = mesh(mouthG, gTor(.045, .0095, 6, 18, Math.PI), smileM, 0, .0, 0, { cast: false }); P.smile = smile;
  const open = mesh(mouthG, gSph(.032, 12, 8), mMouth, 0, -.005, -.006, { cast: false, s: [1.2, .1, .6] }); P.open = open;
  // ---- руки
  const arms = [];
  const sleeveM = M(spec.sleeve || spec.top || 0x2F5FD0, { r: .85 });
  [1, -1].forEach(sd => {
    const sh = grp(torso, sd * .215, .53, 0);
    mesh(sh, gSph(.062, 12, 10), sleeveM, 0, 0, 0);
    mesh(sh, gCap(.05, .2, 4, 12), sleeveM, 0, -.15, 0);
    const el = grp(sh, 0, -.3, 0);
    mesh(el, gSph(.047, 10, 8), spec.sleeveFull ? sleeveM : skin, 0, 0, 0);
    mesh(el, gCap(.043, .17, 4, 12), spec.sleeveFull ? sleeveM : skin, 0, -.13, 0);
    const hand = grp(el, 0, -.285, 0);
    mesh(hand, gSph(.055, 12, 10), skin, 0, 0, 0, { s: [1, 1.1, .85] });
    arms.push({ sh, el, hand });
  });
  P.arms = arms; // [0] — левая (+x), [1] — правая (−x)
  if (spec.build) spec.build(P, { skin, skinD, shirtM, pantsM, shoeM, sleeveM });
  // ---- состояние анимации
  P.keys = { pos: [[0, 0, 0, 0]], arms: [[0, 'rest', 0]], head: [[0, 'fwd', 0]], lean: [[0, 'n', 0]], face: [[0, 'neutral', 0]] };
  P.fx = [];
  P.seed = spec.seed || 1;
  P.dist = null;
  P.walkStride = spec.stride || 1.25;
  P.look = [0, 0];
  P.speak = [];      // интервалы [t0, t1] — шевелим ртом
  P.mouthTalk = 0;
  return P;
}

// накопленный путь по таблице (для шага ходьбы, одинаково работает вперёд и в перемотке)
function prepDist(P, T1) {
  const step = 1 / 60, n = Math.ceil(T1 / step) + 2, d = new Float32Array(n);
  let prev = kf(0, P.keys.pos), acc = 0;
  for (let i = 1; i < n; i++) {
    const c = kf(i * step, P.keys.pos);
    acc += Math.hypot(c[0] - prev[0], c[1] - prev[1]); d[i] = acc; prev = c;
  }
  P.dist = d; P.distStep = step;
}
function distAt(P, t) {
  const f = cl(t / P.distStep, 0, P.dist.length - 2), i = Math.floor(f);
  return lerp(P.dist[i], P.dist[i + 1], f - i);
}
function speedAt(P, t) {
  const a = distAt(P, t - .06), b = distAt(P, t + .06);
  return Math.abs(b - a) / .12;
}
const FXI = { Lf: 0, La: 1, Lb: 2, Rf: 3, Ra: 4, Rb: 5, hy: 0, hp: 1, hr: 2, lf: 0, lt: 1, ls: 2, sm: 0, op: 1, br: 2, bu: 3, sq: 4 };

function blinkAmt(t, seed) {
  const per = 3.1 + hash(seed) * 1.6, ph = ((t + hash(seed + 5) * per) % per);
  return ph < .14 ? Math.sin(Math.PI * ph / .14) : 0;
}

// кадр персонажа
function poseChar(P, t, env) {
  const [x, z, hd] = kf(t, P.keys.pos);
  P.root.position.set(x, 0, z);
  P.root.rotation.y = hd * DEG;
  const sp = speedAt(P, t), w = E.sm(cl((sp - .12) / .55));
  const ph = distAt(P, t) / P.walkStride * Math.PI * 2;
  let arms = seq(P.keys.arms, t, ARMS).slice(), head = seq(P.keys.head, t, HEADS).slice(), lean = seq(P.keys.lean, t, LEANS).slice(), face = seq(P.keys.face, t, FACES).slice();
  // дополнительные движения
  P.fx.forEach(f => {
    if (t < f.t0 || t > f.t1) return;
    const inn = f.in == null ? .15 : f.in, out = f.out == null ? .15 : f.out;
    const wgt = Math.min(1, (t - f.t0) / Math.max(1e-3, inn), (f.t1 - t) / Math.max(1e-3, out));
    const r = f.fn(t - f.t0, t);
    for (const k in r) {
      const v = r[k] * E.sm(cl(wgt));
      if (/^[LR][fab]$/.test(k)) arms[FXI[k]] += v; else if (k[0] === 'h') head[FXI[k]] += v; else if (k[0] === 'l') lean[FXI[k]] += v; else face[FXI[k]] += v;
    }
  });
  // ходьба
  const bob = w * .035 * Math.abs(Math.sin(ph));
  const idle = (1 - w) * (.008 * Math.sin(t * 1.7 + P.seed * 4));
  P.pelvis.position.y = .82 + bob + idle;
  P.pelvis.rotation.set(0, 0, w * .03 * Math.sin(ph));
  P.torso.rotation.set(-0 + lean[0] + w * .05, lean[1] + w * .07 * Math.sin(ph), lean[2] + (1 - w) * .01 * Math.sin(t * 1.3 + P.seed));
  const legAmp = (P.spec.skirt ? .34 : .62) * w;
  P.legs.forEach((lg, i) => {
    const sd = i === 0 ? 1 : -1, a = ph + (i === 0 ? 0 : Math.PI);
    lg.hip.rotation.x = -legAmp * Math.sin(a) * 1.0;
    lg.knee.rotation.x = w * (P.spec.skirt ? .45 : .85) * Math.max(0, Math.sin(a + 1.15)) ;
    lg.foot.rotation.x = -(lg.hip.rotation.x + lg.knee.rotation.x) * .8;
    lg.hip.rotation.z = -sd * .02;
  });
  // руки
  P.arms.forEach((ar, i) => {
    const o = i * 3, sd = i === 0 ? 1 : -1, sw = arms[6 + i] * w;
    const swing = sw * .55 * Math.sin(ph + (i === 0 ? Math.PI : 0));
    const f = arms[o] + swing, a = arms[o + 1], b = arms[o + 2] + sw * .22 * (1 + Math.sin(ph + (i === 0 ? Math.PI : 0)));
    ar.sh.rotation.set(-f, 0, sd * a);
    ar.el.rotation.set(-b, 0, 0);
  });
  // голова
  P.head.rotation.set(head[1] + idleHead(P, t, 'p') , head[0] + idleHead(P, t, 'y'), head[2]);
  // лицо
  const talk = P.speak.some(([a, b]) => t >= a && t < b) ? (.35 + .55 * Math.abs(Math.sin(t * 13 + P.seed * 3)) * (.6 + .4 * Math.sin(t * 5.3))) : 0;
  const sm = face[0], op = Math.max(face[1], talk), bl = blinkAmt(t, P.seed * 17) * (1 - face[4] * .0);
  P.smile.scale.set(1, Math.abs(sm) < .06 ? .06 * Math.sign(sm || 1) * -1 : -sm, 1);
  P.smile.position.y = sm >= 0 ? .0 : -.02;
  P.smile.visible = op < .55 || Math.abs(sm) > .5;
  P.open.scale.set(1.15, .12 + op * 1.0, .6 + op * .3); P.open.visible = op > .06;
  P.open.position.y = -.015 - op * .012;
  P.eyes.forEach(e => {
    const sq = 1 - face[4] * .55, eyeClosed = envClosed(P, t, env) ;
    const k = Math.max(.08, (1 - Math.max(bl, eyeClosed)) * sq);
    e.sc.scale.set(1, 1.15 * k, .55); e.pu.scale.set(1, k, .6); e.pu.visible = k > .25;
    e.pu.position.x = P.look[0] * .012; e.pu.position.y = P.look[1] * .012;
    e.brow.rotation.z = e.sd * face[2] * .5;
    e.brow.position.y = .108 + face[3] * .022 - face[4] * .012;
  });
}
function idleHead(P, t, k) { return k === 'y' ? .06 * Math.sin(t * .8 + P.seed * 2) : .02 * Math.sin(t * 1.1 + P.seed); }
function envClosed(P, t, env) { return P.closedEyes ? P.closedEyes(t) : 0; }

window.N3.Chars = { buildChar, poseChar, prepDist, ARMS, HEADS, LEANS, FACES, seq, blinkAmt, DEG };
})();
