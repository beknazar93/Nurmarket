/* 2026-10-29: актёры и реквизит ролика «Свет погас»: Айнура, ата, Жылдыз, Бакыт, Эрлан, апа, Нурлан + предметы. */
(function () {
'use strict';
const { THREE, scene, M, gBox, gSph, gCyl, gCap, gTor, gRB, mesh, grp, ctex, rrect, FONT, L, hash } = window.N3;
const { buildChar } = window.N3.Chars;
const Wd = window.N3.World;
const Y = 0xFFB208;

/* ---------- узоры ---------- */
const texScarfA = ctex(256, 256, (g, w, h) => {      // белый платок с жёлтым узором «кочкор мүйүз»
  g.fillStyle = '#FFFFFF'; g.fillRect(0, 0, w, h);
  g.strokeStyle = '#FFB208'; g.lineWidth = 7; g.lineCap = 'round';
  for (let r = 0; r < 4; r++) for (let c = 0; c < 8; c++) {
    const x = c * 32 + 16 + (r % 2) * 16, y = 30 + r * 52;
    g.beginPath(); g.arc(x - 8, y, 9, -2.6, 1.0); g.stroke(); g.beginPath(); g.arc(x + 8, y, 9, 2.1, 5.7); g.stroke();
  }
});
const texScarfR = ctex(256, 256, (g, w, h) => {      // красный платок в горошек
  g.fillStyle = '#C8352E'; g.fillRect(0, 0, w, h); g.fillStyle = '#FFFFFF';
  for (let r = 0; r < 8; r++) for (let c = 0; c < 8; c++) { g.beginPath(); g.arc(c * 32 + 16 + (r % 2) * 16, r * 32 + 16, 6, 0, 7); g.fill(); }
});
const texPlaid = ctex(256, 256, (g, w, h) => {       // клетчатый баул
  g.fillStyle = '#F4F0E8'; g.fillRect(0, 0, w, h);
  g.fillStyle = 'rgba(200,53,46,.85)'; for (let i = 0; i < 4; i++) { g.fillRect(i * 64 + 8, 0, 24, h); g.fillRect(0, i * 64 + 8, w, 24); }
  g.fillStyle = 'rgba(47,95,208,.8)'; for (let i = 0; i < 4; i++) { g.fillRect(i * 64 + 44, 0, 8, h); g.fillRect(0, i * 64 + 44, w, 8); }
}, {});
const texApron = ctex(256, 256, (g, w, h) => {
  g.fillStyle = '#FFB208'; g.fillRect(0, 0, w, h); g.fillStyle = '#191919'; g.font = '800 44px ' + FONT; g.textAlign = 'center'; g.textBaseline = 'middle'; g.fillText(L('БЕРЕКЕ', 'БЕРЕКЕ'), w / 2, h * .2);
  g.lineWidth = 8; g.strokeStyle = '#191919'; g.beginPath(); g.arc(w / 2, h * .5, 20, 0, 7); g.stroke();
});
const texStripes = ctex(64, 128, (g, w, h) => { g.fillStyle = '#FFD24A'; g.fillRect(0, 0, w, h); g.fillStyle = '#E8A412'; for (let i = 0; i < 6; i++) g.fillRect(0, i * 22 + 8, w, 9); });
const texKalpak = ctex(256, 128, (g, w, h) => {
  g.fillStyle = '#F6F2E8'; g.fillRect(0, 0, w, h); g.fillStyle = '#191919'; g.fillRect(0, h * .78, w, h * .22);
  g.strokeStyle = '#191919'; g.lineWidth = 6; for (let i = 0; i < 8; i++) { const x = i * 32 + 16; g.beginPath(); g.moveTo(x - 10, h * .75); g.quadraticCurveTo(x, h * .25, x + 10, h * .75); g.stroke(); }
});

/* ---------- сборка актёров ---------- */
const CAST = {};
function hairCap(P, color, o) {
  o = o || {};
  const cap = mesh(P.head, new THREE.SphereGeometry(.2, 24, 16, 0, Math.PI * 2, 0, o.th || 1.5), M(color, { r: .9 }), 0, .01, -.01, { r: [o.tilt == null ? -.35 : o.tilt, 0, 0] });
  return cap;
}

// Айнура
CAST.ainura = buildChar({
  name: 'ainura', scale: .93, skin: 0xF2C29B, top: 0x2F5FD0, sleeve: 0x2F5FD0, pants: 0x2F5FD0, shoes: 0x3b2a22, seed: 3,
  skirt: { color: 0x2F5FD0, len: .55, r0: .15, r1: .26 }, stride: 1.2,
  build(P, m) {
    const scarf = mesh(P.head, new THREE.SphereGeometry(.208, 28, 20, 0, Math.PI * 2, 0, 1.48), M(0xFFFFFF, { r: .85, tex: texScarfA, tag: 'scarfA' }), 0, .015, -.012, { r: [-.5, 0, 0] });
    mesh(P.head, gSph(.07, 12, 10), M(0xFFFFFF, { r: .85, tex: texScarfA, tag: 'scarfA' }), 0, -.1, -.19);    // узел сзади
    // фартук
    const ap = mesh(P.torso, gRB(.3, .4, .03, .012), M(0xFFFFFF, { r: .7, tex: texApron, tag: 'apron' }), 0, .26, .133, { r: [0, 0, 0] });
    [1, -1].forEach(sd => mesh(P.torso, gBox(.025, .14, .02), M(Y, { r: .7 }), sd * .12, .5, .115, { r: [0, 0, sd * .15], cast: false }));
  },
});
// Ата
CAST.ata = buildChar({
  name: 'ata', scale: .92, skin: 0xE2AE86, skinD: 0xC98F66, top: 0x8A5A3C, sleeve: 0x8A5A3C, sleeveFull: true, pants: 0x777B82, shoes: 0x2b2420, brow: 0xE6E2DA, seed: 7, stride: 1.05,
  build(P, m) {
    const hat = grp(P.head, 0, .09, 0);
    const lathe = new THREE.LatheGeometry([[.205, 0], [.218, .05], [.2, .13], [.16, .22], [.125, .29], [.09, .32], [0, .335]].map(p => new THREE.Vector2(p[0], p[1])), 28);
    mesh(hat, lathe, M(0xFFFFFF, { r: .95, tex: texKalpak, tag: 'kalpak' }), 0, 0, 0);
    mesh(hat, new THREE.CylinderGeometry(.222, .22, .075, 28, 1, true), M(0x191919, { r: .9, side: THREE.DoubleSide }), 0, .0, 0, { cast: false });
    mesh(P.head, gSph(.14, 16, 12), M(0xE9E5DE, { r: 1 }), 0, -.14, .045, { s: [1, .85, .85] });                         // борода
    mesh(P.head, gCap(.013, .09, 3, 8), M(0xE9E5DE, { r: 1 }), 0, -.055, .19, { r: [0, 0, Math.PI / 2], cast: false });   // усы
  },
});
// Жылдыз
CAST.jyldyz = buildChar({
  name: 'jyldyz', scale: .9, skin: 0xF0BE96, top: 0x3E8E5A, sleeve: 0x3E8E5A, sleeveFull: true, pants: 0x34405A, shoes: 0xF2F2F2, seed: 11, stride: 1.15,
  build(P, m) {
    hairCap(P, 0x2a1c18, { th: 1.7, tilt: -.25 });
    mesh(P.head, gCap(.12, .38, 4, 12), M(0x2a1c18, { r: .9 }), 0, -.2, -.1, { s: [1, 1, .8], cast: false });          // длинные волосы
    mesh(P.torso, gBox(.12, .46, .02), M(0xFFFFFF, { r: .8 }), 0, .27, .125, { cast: false });                                // футболка под кардиганом
  },
});
// Бакыт (6 лет)
CAST.bakyt = buildChar({
  name: 'bakyt', scale: .62, headScale: 1.28, skin: 0xF6CBA2, top: 0xFFD24A, sleeve: 0xFFD24A, pants: 0x2F5FD0, shoes: 0xC8352E, bareLegs: true, seed: 5, stride: .8,
  build(P, m) {
    hairCap(P, 0x2a1c18, { th: 1.45, tilt: -.1 });
    mesh(P.head, new THREE.ConeGeometry(.045, .12, 8), M(0x2a1c18, { r: .9 }), 0, .21, .03, { r: [.3, 0, 0], cast: false });   // хохолок
    mesh(P.torso, gCyl(.185, .185, .3, 20, 1), M(0xFFFFFF, { r: .85, tex: texStripes, tag: 'stripes' }), 0, .3, 0, { s: [1, 1, .76], cast: false });
    [1, -1].forEach(sd => mesh(P.head, gSph(.035, 8, 6), M(0xF29A8A, { r: .9 }), sd * .12, -.04, .14, { s: [1, .7, .5], cast: false })); // румянец
  },
});
// Эрлан
CAST.erlan = buildChar({
  name: 'erlan', scale: .96, skin: 0xF2C29B, top: 0x23232A, sleeve: 0x23232A, sleeveFull: true, pants: 0x2a2d36, shoes: 0xF4F4F4, seed: 13, stride: 1.3,
  build(P, m) {
    // капюшон худи сзади
    mesh(P.torso, gSph(.15, 14, 10), M(0x23232A, { r: .9 }), 0, .68, -.09, { s: [1.1, .8, .7], cast: false });
    // кепка козырьком назад
    const cap = grp(P.head, 0, .05, 0);
    mesh(cap, new THREE.SphereGeometry(.205, 24, 14, 0, Math.PI * 2, 0, 1.25), M(0x55565F, { r: .85 }), 0, 0, 0, { r: [-.15, 0, 0] });
    mesh(cap, gRB(.2, .02, .17, .01), M(0x55565F, { r: .85 }), 0, .035, -.25, { r: [-.15, 0, 0] });
    // наушники
    const hp = M(0x3EA7E8, { r: .5 });
    mesh(P.head, gTor(.2, .014, 6, 28, Math.PI), hp, 0, .01, 0, { r: [0, 0, 0], cast: false });
    [1, -1].forEach(sd => mesh(P.head, gCyl(.065, .065, .05, 18), hp, sd * .2, -.0, 0, { r: [0, 0, Math.PI / 2] }));
  },
});
// Бүбү-апа
CAST.apa = buildChar({
  name: 'apa', scale: .88, skin: 0xE8B58C, top: 0x7A4B32, sleeve: 0x7A4B32, sleeveFull: true, pants: 0x7C7F86, shoes: 0x2b2420, brow: 0x555555, seed: 17, stride: 1.0,
  skirt: { color: 0x7C7F86, len: .66, r0: .16, r1: .27 },
  build(P, m) {
    mesh(P.head, new THREE.SphereGeometry(.208, 28, 20, 0, Math.PI * 2, 0, 1.5), M(0xFFFFFF, { r: .9, tex: texScarfR, tag: 'scarfR' }), 0, .015, -.012, { r: [-.5, 0, 0] });
    mesh(P.head, gSph(.075, 12, 10), M(0xFFFFFF, { r: .9, tex: texScarfR, tag: 'scarfR' }), 0, -.11, -.18);
    const gm = M(0x3a3a3a, { r: .4 });
    [1, -1].forEach(sd => mesh(P.head, gTor(.048, .008, 6, 20), gm, sd * .075, .035, .186, { cast: false }));
    mesh(P.head, gBox(.04, .01, .01), gm, 0, .045, .19, { cast: false });
  },
});
// Нурлан-байке
CAST.nurlan = buildChar({
  name: 'nurlan', scale: .95, skin: 0xE4B088, top: 0xF4F4F4, sleeve: 0xF4F4F4, sleeveFull: true, pants: 0x3a3f4a, shoes: 0x2b2420, brow: 0x2a2a2a, seed: 19, stride: 1.2, torsoScale: [1.12, 1, .84],
  build(P, m) {
    const cap = grp(P.head, 0, .06, 0);
    mesh(cap, new THREE.SphereGeometry(.205, 24, 14, 0, Math.PI * 2, 0, 1.3), M(0x8A8E96, { r: .85 }), 0, 0, 0, { r: [.05, 0, 0] });
    mesh(cap, gRB(.2, .02, .12, .01), M(0x8A8E96, { r: .85 }), 0, .01, .23, { r: [.2, 0, 0] });
    mesh(P.head, gCap(.014, .1, 3, 8), M(0x1c1c1c, { r: .9 }), 0, -.05, .19, { r: [0, 0, Math.PI / 2], cast: false });
    // жилет
    const vest = M(0x2E8B57, { r: .85 });
    mesh(P.torso, gCap(.185, .22, 4, 14), vest, 0, .25, 0, { s: [1.12, 1, .84], cast: false });
    mesh(P.torso, gBox(.02, .42, .02), M(Y, { r: .7 }), 0, .27, .157, { cast: false });
    [1, -1].forEach(sd => mesh(P.torso, gBox(.035, .4, .02), M(Y, { r: .7 }), sd * .06, .28, .156, { r: [0, 0, sd * .05], cast: false }));
    mesh(P.torso, gSph(.2, 14, 10), M(0xF4F4F4, { r: .85 }), 0, .1, .02, { s: [1.0, .6, .8], cast: false });          // животик
  },
});
for (const k in CAST) CAST[k].name = k;
// Бүбү-апа снаружи (отдельная копия: заходит в дверь в К1)
CAST.apaExt = buildChar(Object.assign({}, { name: 'apa', scale: .88, skin: 0xE8B58C, top: 0x7A4B32, sleeve: 0x7A4B32, sleeveFull: true, pants: 0x7C7F86, shoes: 0x2b2420, brow: 0x555555, seed: 18, stride: 1.0, skirt: { color: 0x7C7F86, len: .66, r0: .16, r1: .27 },
  build(P, m) {
    mesh(P.head, new THREE.SphereGeometry(.208, 28, 20, 0, Math.PI * 2, 0, 1.5), M(0xFFFFFF, { r: .9, tex: texScarfR, tag: 'scarfR' }), 0, .015, -.012, { r: [-.5, 0, 0] });
    mesh(P.head, gSph(.075, 12, 10), M(0xFFFFFF, { r: .9, tex: texScarfR, tag: 'scarfR' }), 0, -.11, -.18);
  } }));

/* ---------- реквизит ---------- */
const PROPS = {};
const phoneScr = ctex(320, 640, () => {});
const phoneScrFl = ctex(64, 64, (g, w, h) => { g.fillStyle = '#EAF3FF'; g.fillRect(0, 0, w, h); });
function mkPhone(screenTex, emissive) {
  const g = new THREE.Group();
  mesh(g, gRB(.075, .155, .01, .008), M(0x1b1d22, { r: .35 }), 0, 0, 0);
  const scr = mesh(g, new THREE.PlaneGeometry(.066, .141), new THREE.MeshBasicMaterial({ map: screenTex }), 0, 0, .0062, { cast: false, recv: false });
  g.userData.scr = scr;
  return g;
}
PROPS.phoneScan = mkPhone(phoneScr);
PROPS.phoneFlash = (() => {
  const g = mkPhone(phoneScrFl);
  g.userData.scr.material.color = new THREE.Color(0x000000);                      // экран «выключен», светит камера-вспышка сзади
  const lamp = mesh(g, gSph(.012, 8, 6), new THREE.MeshBasicMaterial({ color: 0xFFFFFF }), 0, .068, -.008, { cast: false, recv: false });
  g.userData.lamp = lamp;
  return g;
})();
PROPS.flashlight = (() => {
  const g = new THREE.Group();
  mesh(g, gCyl(.025, .03, .17, 12), M(0x3a3f4a, { r: .4, m: .3 }), 0, 0, 0, { r: [Math.PI / 2, 0, 0] });
  mesh(g, gCyl(.04, .027, .05, 12), M(0x2a2d34, { r: .4, m: .3 }), 0, 0, .1, { r: [Math.PI / 2, 0, 0] });
  g.userData.lamp = mesh(g, gCyl(.038, .038, .006, 12), new THREE.MeshBasicMaterial({ color: 0xFFFFFF }), 0, 0, .127, { r: [Math.PI / 2, 0, 0], cast: false, recv: false });
  return g;
})();
PROPS.lepyoshka = (() => {
  const g = new THREE.Group();
  mesh(g, new THREE.CylinderGeometry(.15, .15, .035, 26), M(0xE7BE74, { r: .8 }), 0, 0, 0);
  mesh(g, gTor(.1, .012, 6, 24), M(0xCC9648, { r: .8 }), 0, .018, 0, { r: [Math.PI / 2, 0, 0], cast: false });
  mesh(g, gTor(.05, .01, 6, 20), M(0xCC9648, { r: .8 }), 0, .018, 0, { r: [Math.PI / 2, 0, 0], cast: false });
  g.rotation.order = 'YXZ';
  return g;
})();
PROPS.milk = (() => { const g = new THREE.Group(); mesh(g, gBox(.08, .18, .08), M(0xFFFFFF, { r: .6, tex: Wd.texMilk, tag: 'milk' }), 0, 0, 0); return g; })();
PROPS.seeds = (() => { const g = new THREE.Group(); mesh(g, gBox(.13, .2, .04), M(0xFFFFFF, { r: .6, tex: Wd.texSeeds, tag: 'seed' }), 0, 0, 0); return g; })();
PROPS.loaf = (() => { const g = new THREE.Group(); const m = mesh(g, gCap(.05, .2, 4, 10), M(0xD7973F, { r: .7 }), 0, 0, 0, { r: [0, 0, Math.PI / 2] }); m.scale.set(1, .85, 1); return g; })();
PROPS.bag = (() => {
  const g = new THREE.Group();
  mesh(g, gRB(.5, .36, .22, .06), M(0xFFFFFF, { r: .85, tex: texPlaid, tag: 'plaid' }), 0, -.1, 0);
  mesh(g, gTor(.15, .012, 6, 18, Math.PI), M(0x2a2a2a), 0, .08, 0, { r: [0, 0, 0], cast: false });
  return g;
})();
PROPS.calc = (() => {
  const g = new THREE.Group();
  mesh(g, gRB(.12, .02, .17, .01), M(0x30343c, { r: .5 }), 0, 0, 0);
  const sc = mesh(g, gBox(.1, .004, .03), M(0xCFE9CF, { r: .4, e: 0x86C98A, ei: .5 }), 0, .011, -.058, { cast: false });
  for (let i = 0; i < 4; i++) for (let j = 0; j < 4; j++) mesh(g, gBox(.02, .006, .02), M(j === 3 ? 0xFFB208 : 0xCDD2DA, { r: .5 }), -.039 + i * .026, .011, -.02 + j * .03, { cast: false });
  return g;
})();
PROPS.notebook = (() => {
  const g = new THREE.Group();
  mesh(g, gRB(.17, .015, .23, .008), M(0x2F5FD0, { r: .7 }), 0, 0, 0);
  mesh(g, gBox(.155, .012, .215), M(0xFAF6EA, { r: .9 }), 0, .012, 0, { cast: false });
  return g;
})();
PROPS.receipt = (() => {
  const g = new THREE.Group();
  const tex = ctex(128, 256, (c, w, h) => { c.fillStyle = '#FFFFFF'; c.fillRect(0, 0, w, h); c.fillStyle = '#191919'; c.font = '800 20px ' + FONT; c.textAlign = 'center'; c.fillText('NurCRM', w / 2, 30); c.font = '600 15px ' + FONT; [L('Нан', 'Нан') + '  30', L('Молоко', 'Сүт') + '  65', L('Семечки', 'Күн карама') + '  40'].forEach((s, i) => c.fillText(s, w / 2, 70 + i * 24)); c.fillRect(14, 160, w - 28, 3); c.font = '800 18px ' + FONT; c.fillText(L('ИТОГО', 'ЖАЛПЫ') + ' 135', w / 2, 190); });
  mesh(g, new THREE.PlaneGeometry(.07, .14), new THREE.MeshStandardMaterial({ map: tex, roughness: .9, side: THREE.DoubleSide }), 0, 0, 0, { cast: false });
  g.userData.tex = tex; return g;
})();
PROPS.money = (() => {
  const g = new THREE.Group();
  [0, 1, 2].forEach(i => mesh(g, gRB(.14, .006, .07, .002), M([0x6FAE7B, 0xB7D9BC, 0x8CC39A][i], { r: .8 }), 0, i * .006, 0, { r: [0, i * .15, 0], cast: false }));
  return g;
})();
PROPS.sweat = (() => { const g = new THREE.Group(); mesh(g, gSph(.012, 8, 6), M(0x9FD9FF, { r: .2, e: 0x5aa8e0, ei: .4 }), 0, 0, 0, { s: [1, 1.5, 1], cast: false }); return g; })();
for (const k in PROPS) { PROPS[k].visible = false; scene.add(PROPS[k]); }
Wd.phoneScr = phoneScr; Wd.phoneScrFl = phoneScrFl;
window.N3.Cast = CAST; window.N3.Props = PROPS;
})();
