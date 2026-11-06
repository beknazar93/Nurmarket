/* 2026-10-29: мир ролика «Свет погас» (3D): магазин «Береке» внутри и улица снаружи.
   Координаты: метры, y вверх, фасад на z=+6 (камера снаружи смотрит в -z), проход вдоль оси X, касса справа (x≈3.3…4.3). */
(function () {
'use strict';
const { THREE, scene, M, gBox, gSph, gCyl, gCap, gTor, gRB, mesh, grp, ctex, rrect, FONT, L, lerp, cl, hash } = window.N3;
const Y = 0xFFB208, INK = 0x191919;
const Wd = {};   // всё, что нужно сюжету
window.N3.World = Wd;

/* ------------------------------------------------ текстуры */
const texFloor = ctex(256, 256, (g, w, h) => {
  const a = '#EBD9B4', b = '#DCC08E';
  for (let i = 0; i < 2; i++) for (let j = 0; j < 2; j++) { g.fillStyle = (i + j) % 2 ? b : a; g.fillRect(i * w / 2, j * h / 2, w / 2, h / 2); }
  g.strokeStyle = 'rgba(120,90,50,.28)'; g.lineWidth = 3; g.strokeRect(1.5, 1.5, w - 3, h - 3); g.beginPath(); g.moveTo(w / 2, 0); g.lineTo(w / 2, h); g.moveTo(0, h / 2); g.lineTo(w, h / 2); g.stroke();
}, { repeat: [16, 7.5] });
const texRoad = ctex(256, 256, (g, w, h) => {
  g.fillStyle = '#3B3E49'; g.fillRect(0, 0, w, h);
  for (let i = 0; i < 400; i++) { g.fillStyle = 'rgba(' + (hash(i) > .5 ? '255,255,255' : '0,0,0') + ',.05)'; g.fillRect(hash(i + 3) * w, hash(i + 9) * h, 3, 3); }
}, { repeat: [24, 8] });
const texSide = ctex(256, 256, (g, w, h) => {
  g.fillStyle = '#B7AEA2'; g.fillRect(0, 0, w, h); g.strokeStyle = 'rgba(70,60,50,.35)'; g.lineWidth = 4; g.strokeRect(0, 0, w, h);
  g.beginPath(); g.moveTo(w / 2, 0); g.lineTo(w / 2, h); g.stroke();
}, { repeat: [40, 3] });
const texWindow = ctex(1024, 512, (g, w, h) => {
  const gr = g.createLinearGradient(0, 0, 0, h); gr.addColorStop(0, '#2C3F78'); gr.addColorStop(.45, '#7F6FA3'); gr.addColorStop(.72, '#F0A06A'); gr.addColorStop(1, '#F8CF86');
  g.fillStyle = gr; g.fillRect(0, 0, w, h);
  const ridge = (base, amp, col, seed, snow) => {
    g.fillStyle = col; g.beginPath(); g.moveTo(0, h);
    const pts = [];
    for (let x = 0; x <= w; x += 32) { const y = base - amp * (.5 + .5 * Math.sin(x * .012 + seed)) - amp * .6 * hash(x + seed * 7); pts.push([x, y]); g.lineTo(x, y); }
    g.lineTo(w, h); g.closePath(); g.fill();
    if (snow) { g.fillStyle = 'rgba(255,255,255,.75)'; pts.forEach(([x, y]) => { if (y < base - amp * .85) { g.beginPath(); g.moveTo(x - 20, y + 22); g.lineTo(x, y); g.lineTo(x + 20, y + 22); g.closePath(); g.fill(); } }); }
  };
  ridge(330, 170, '#6B6A92', 1.3, true); ridge(400, 110, '#4A4E78', 4.1, false); ridge(470, 60, '#2F3452', 2.2, false);
}, {});
const texMilk = ctex(128, 256, (g, w, h) => {
  g.fillStyle = '#FFFFFF'; g.fillRect(0, 0, w, h); g.fillStyle = '#2F5FD0'; g.fillRect(0, h * .42, w, h * .34);
  g.fillStyle = '#fff'; g.font = '800 ' + (w * .46) + 'px ' + FONT; g.textAlign = 'center'; g.textBaseline = 'middle'; g.fillText('СҮТ', w / 2, h * .59);
  g.fillStyle = '#FFB208'; g.fillRect(0, h * .88, w, h * .05);
});
const texSeeds = ctex(160, 256, (g, w, h) => {
  g.fillStyle = '#1E1E1E'; g.fillRect(0, 0, w, h); g.fillStyle = '#FFB208'; g.fillRect(8, 8, w - 16, h * .5);
  g.fillStyle = '#191919'; g.font = '800 ' + (w * .17) + 'px ' + FONT; g.textAlign = 'center'; g.textBaseline = 'middle'; g.fillText(L('СЕМЕЧКИ', 'КҮН КАРАМА'), w / 2, h * .26);
  g.fillStyle = '#fff'; g.beginPath(); g.ellipse(w / 2, h * .74, w * .26, h * .12, -.4, 0, Math.PI * 2); g.fill();
});
const texEggs = ctex(128, 96, (g, w, h) => { g.fillStyle = '#E9DEC6'; g.fillRect(0, 0, w, h); g.fillStyle = '#6E4B2A'; g.font = '800 26px ' + FONT; g.textAlign = 'center'; g.fillText(L('ЯЙЦА', 'ЖУМУРТКА'), w / 2, h * .6); });
function labelTex(txt, price) {
  return ctex(256, 112, (g, w, h) => {
    g.fillStyle = '#FFFFFF'; rrect(g, 4, 4, w - 8, h - 8, 14); g.fill(); g.fillStyle = '#FFB208'; g.fillRect(4, h - 30, w - 8, 26);
    g.fillStyle = '#191919'; g.font = '800 40px ' + FONT; g.textAlign = 'left'; g.textBaseline = 'middle'; g.fillText(txt, 16, 42);
    g.textAlign = 'right'; g.fillText(price, w - 16, 42);
  });
}

/* ------------------------------------------------ вспомогательное */
function wbox(x0, x1, y0, y1, z0, z1, mat, o) {
  o = o || {};
  const m = mesh(scene, gBox(x1 - x0, y1 - y0, z1 - z0), mat, (x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2, { cast: !!o.cast, recv: o.recv !== false });
  return m;
}
const matWall = M(0xF3E6CF, { r: .95 }), matPanel = M(0x7DB7C4, { r: .9 }), matWood = M(0xD7A56A, { r: .8 }), matWoodD = M(0xB07A44, { r: .8 });
const matFacade = M(0xE9B98A, { r: .95 });
const matCeil = new THREE.MeshStandardMaterial({ color: 0xFAF3E4, roughness: 1, emissive: 0xFFF1D8, emissiveIntensity: .3 });
Wd.ceilMat = matCeil;

/* ------------------------------------------------ пол, потолок, стены интерьера */
const floor = mesh(scene, gBox(19.5, .1, 9), M(0xFFFFFF, { r: .9, tex: texFloor, tag: 'floor' }), -2.25, -.05, 1.5, { cast: false });
wbox(-12, 7.5, 3.2, 3.3, -3, 6, matCeil, { cast: false });                       // потолок
wbox(-12, 7.5, 0, 3.2, -3.2, -3, matWall);                                         // задняя стена
wbox(-12, 7.5, 0, 1.0, -3, -2.97, matPanel);                                       // панель
wbox(7.5, 7.7, 0, 3.2, -3.2, 6.15, matWall);                                       // правая стена
wbox(-12.2, -12, 0, 3.2, -3.2, 6.15, matWall);                                     // левая стена
// плинтус
wbox(-12, 7.5, 0, .08, -3, -2.95, M(0xFFFFFF, { r: .8 }), { cast: false });
// окно в задней стене (x 1.2…4.4, y 1.05…2.55): вырез делаем накладной рамой поверх «картины» гор
const winTex = new THREE.MeshBasicMaterial({ map: texWindow, color: 0xffffff });
mesh(scene, new THREE.PlaneGeometry(3.0, 1.4), winTex, 2.8, 1.8, -2.965, { cast: false, recv: false });
Wd.winMat = winTex;
const matFrame = M(0xFFFFFF, { r: .6 });
[[2.8, 2.575, 3.3, .12, .1], [2.8, 1.025, 3.3, .12, .1], [1.2, 1.8, .12, 1.5, .1], [4.4, 1.8, .12, 1.5, .1], [2.8, 1.8, .06, 1.4, .06], [2.8, 1.8, 3.0, .06, .06]]
  .forEach(([x, y, w, h, d]) => mesh(scene, gBox(w, h, d), matFrame, x, y, -2.93, { cast: false }));
mesh(scene, gBox(3.5, .08, .3), matFrame, 2.8, .98, -2.82, { cast: false });
// комната-подсобка (проём x 5.55…6.45 в задней стене)
wbox(5.55, 6.45, 2.1, 3.2, -3.2, -3, matWall);
{ const dark = new THREE.MeshBasicMaterial({ color: 0x0d0a08, side: THREE.BackSide }); mesh(scene, gBox(1.6, 2.4, 1.8), dark, 6.0, 1.2, -3.9, { cast: false, recv: false }); }
[[5.5, 1.05, .1, 2.1, .12], [6.5, 1.05, .1, 2.1, .12], [6.0, 2.15, 1.1, .1, .12]].forEach(([x, y, w, h, d]) => mesh(scene, gBox(w, h, d), matWoodD, x, y, -2.93, { cast: false }));
// открытая дверь подсобки
{ const leaf = grp(scene, 6.45, 0, -2.95); const lm = mesh(leaf, gRB(.86, 2.05, .05, .02), M(0xB07A44, { r: .7 }), -.43, 1.03, 0); leaf.rotation.y = -1.6; leaf.position.x = 5.55; lm.position.x = .43; }
// выключатель + часы + ёршик
{
  const sw = mesh(scene, gRB(.09, .14, .03, .012), M(0xFFFFFF, { r: .5 }), 5.05, 1.35, -2.955);
  mesh(scene, gRB(.03, .06, .02, .008), M(0x444444), 5.05, 1.35, -2.935, { cast: false });
  Wd.switchPos = new THREE.Vector3(5.05, 1.35, -2.9);
  const clock = grp(scene, 4.95, 2.45, -2.93);
  mesh(clock, gCyl(.2, .2, .05, 28), M(0xFFFFFF, { r: .4 }), 0, 0, 0, { r: [Math.PI / 2, 0, 0], cast: false });
  mesh(clock, gTor(.2, .02, 6, 28), M(0x191919), 0, 0, .0, { cast: false });
  mesh(clock, gBox(.015, .12, .01), M(0x191919), 0, .05, .03, { cast: false }); mesh(clock, gBox(.012, .08, .01), M(0x191919), .03, .0, .03, { cast: false, r: [0, 0, -1.2] });
}

/* ------------------------------------------------ фасад (стена z=6) с проёмами */
{
  const z0 = 5.85, z1 = 6.15, top = 3.7;
  wbox(-12, -9.6, 0, top, z0, z1, matFacade);          // слева от двери
  wbox(-9.6, -8.4, 2.2, top, z0, z1, matFacade);       // над дверью
  wbox(-8.4, -6.5, 0, top, z0, z1, matFacade);         // между
  wbox(-6.5, -1.5, 0, .8, z0, z1, matFacade);          // под окном
  wbox(-6.5, -1.5, 2.3, top, z0, z1, matFacade);       // над окном
  wbox(-1.5, 7.7, 0, top, z0, z1, matFacade);          // справа
  wbox(-12.3, 7.8, top, top + .18, z0 - .05, z1 + .25, M(0xD9A06E, { r: .9 }));  // карниз
  // цоколь
  wbox(-12, 7.7, 0, .35, z1, z1 + .04, M(0xA8957F, { r: 1 }));
  // стекло окна и двери
  const glass = M(0xBFE3FF, { r: .05, tag: 'glass' }); glass.transparent = true; glass.opacity = .1; glass.depthWrite = false;
  mesh(scene, new THREE.PlaneGeometry(5, 1.5), glass, -4, 1.55, 6.0, { cast: false, recv: false });
  mesh(scene, new THREE.PlaneGeometry(1.2, 2.2), glass, -9, 1.1, 6.0, { cast: false, recv: false });
  const fr = M(0x3D3D3D, { r: .5 });
  [[-4, .8, 5.06, .06, .08], [-4, 2.3, 5.06, .06, .08]].forEach(() => 0);
  mesh(scene, gBox(5.1, .07, .1), fr, -4, .8, 6.02, { cast: false }); mesh(scene, gBox(5.1, .07, .1), fr, -4, 2.3, 6.02, { cast: false });
  mesh(scene, gBox(.07, 1.5, .1), fr, -6.5, 1.55, 6.02, { cast: false }); mesh(scene, gBox(.07, 1.5, .1), fr, -1.5, 1.55, 6.02, { cast: false });
  mesh(scene, gBox(.06, 1.5, .08), fr, -4, 1.55, 6.02, { cast: false });
  mesh(scene, gBox(1.28, .07, .1), fr, -9, 2.2, 6.02, { cast: false }); mesh(scene, gBox(.07, 2.2, .1), fr, -9.6, 1.1, 6.02, { cast: false }); mesh(scene, gBox(.07, 2.2, .1), fr, -8.4, 1.1, 6.02, { cast: false });
  mesh(scene, gBox(.04, .5, .06), M(0xC9C9C9, { m: .8, r: .3 }), -8.55, 1.05, 6.12, { cast: false });
  // табличка «ОТКРЫТО» на двери
  const texOpen = ctex(256, 96, (g, w, h) => { g.fillStyle = '#2E8B57'; rrect(g, 2, 2, w - 4, h - 4, 14); g.fill(); g.fillStyle = '#fff'; g.font = '800 44px ' + FONT; g.textAlign = 'center'; g.textBaseline = 'middle'; g.fillText(L('ОТКРЫТО', 'АЧЫК'), w / 2, h / 2 + 2); });
  mesh(scene, new THREE.PlaneGeometry(.5, .19), new THREE.MeshBasicMaterial({ map: texOpen }), -9, 1.5, 6.07, { cast: false, recv: false });
  // вывеска «БЕРЕКЕ»
  const SW = 5.6, SH = 1.0;
  const letters = ['Б', 'Е', 'Р', 'Е', 'К', 'Е'];
  const signTex = ctex(1792, 320, () => {});
  Wd.drawSign = (flick) => {
    const g = signTex._g, w = 1792, h = 320;
    g.clearRect(0, 0, w, h);
    g.fillStyle = '#FFB208'; rrect(g, 6, 6, w - 12, h - 12, 46); g.fill();
    g.lineWidth = 12; g.strokeStyle = '#191919'; rrect(g, 24, 24, w - 48, h - 48, 34); g.stroke();
    // узор «кочкор мүйүз» по краям
    g.strokeStyle = '#191919'; g.lineWidth = 9; g.lineCap = 'round';
    [[130, 1], [w - 130, -1]].forEach(([cx, s]) => { g.beginPath(); g.arc(cx, h / 2, 46, Math.PI * (s > 0 ? .25 : .75), Math.PI * (s > 0 ? 1.75 : 2.25), s < 0); g.stroke(); g.beginPath(); g.arc(cx + s * 24, h / 2, 20, 0, 2 * Math.PI); g.stroke(); });
    g.font = '800 210px ' + FONT; g.textBaseline = 'middle'; g.textAlign = 'left';
    const tw = letters.map(c => g.measureText(c).width), gap = 14, total = tw.reduce((a, b) => a + b, 0) + gap * 5;
    let x = (w - total) / 2;
    letters.forEach((c, i) => { g.fillStyle = i === 3 ? 'rgba(25,25,25,' + flick.toFixed(2) + ')' : '#191919'; g.fillText(c, x, h / 2 + 12); x += tw[i] + gap; });
    signTex.needsUpdate = true;
  };
  Wd.drawSign(1);
  const signMat = new THREE.MeshStandardMaterial({ map: signTex, roughness: .6, emissive: 0xFFB208, emissiveMap: signTex, emissiveIntensity: .7 });
  const sg = grp(scene, -4, 2.95, 6.2);
  mesh(sg, gRB(SW + .2, SH + .2, .14, .05), M(0x2b2b2b, { r: .6 }), 0, 0, -.02);
  mesh(sg, new THREE.PlaneGeometry(SW, SH), signMat, 0, 0, .06, { cast: false });
  Wd.signMat = signMat;
  // «ВЫХОД» над дверью (внутри) — светится в темноте
  const texExit = ctex(256, 96, (g, w, h) => { g.fillStyle = '#1E8E4E'; rrect(g, 2, 2, w - 4, h - 4, 12); g.fill(); g.fillStyle = '#fff'; g.font = '800 36px ' + FONT; g.textAlign = 'center'; g.textBaseline = 'middle'; g.fillText(L('ЧЫГУУ · ВЫХОД', 'ЧЫГУУ · ВЫХОД'), w / 2, h / 2 + 2); });
  const exitMat = new THREE.MeshBasicMaterial({ map: texExit });
  const ex = mesh(scene, new THREE.PlaneGeometry(.8, .3), exitMat, -9, 2.55, 5.8, { cast: false, recv: false }); ex.rotation.y = Math.PI;
}

/* ------------------------------------------------ улица */
const matRoad = M(0xFFFFFF, { r: 1, tex: texRoad, tag: 'road' });
mesh(scene, gBox(80, .1, 16), matRoad, -3, -.07, 16.3, { cast: false });
mesh(scene, gBox(80, .12, 2.1), M(0xFFFFFF, { r: 1, tex: texSide, tag: 'side' }), -3, -.02, 7.2, { cast: false });
mesh(scene, gBox(80, .18, .22), M(0x9C9A95, { r: 1 }), -3, .0, 8.35, { cast: false });
for (let i = -8; i < 8; i++) mesh(scene, gBox(1.6, .02, .16), M(0xEFE9D8, { r: 1 }), i * 4.4, .0, 13.8, { cast: false });
// соседние здания
const neigh = [[-18, 12, 5.2, 0xC9D6E8], [-30, 12, 4.4, 0xE3C79B], [14, 13, 4.8, 0xD7E0C6], [27, 12, 5.6, 0xE8B9B0]];
neigh.forEach(([cx, w, h, col], i) => {
  mesh(scene, gBox(w, h, 9), M(col, { r: .95 }), cx, h / 2, 1.5, {});
  for (let k = 0; k < 3; k++) mesh(scene, gBox(1.2, 1.0, .06), M(0x2c3550, { r: .4 }), cx - w / 3 + k * w / 3, 2.1, 6.03, { cast: false });
});
mesh(scene, gBox(1.2, 1.0, .06), M(0x2c3550, { r: .4 }), 2.5, 2.1, 6.03, { cast: false });
// горы вдали
{
  const mm = M(0x58628E, { r: 1, flat: true }), sn = M(0xE8EEF8, { r: 1, flat: true });
  for (let i = 0; i < 9; i++) {
    const h = 20 + hash(i * 3 + 1) * 16, r = 16 + hash(i * 5 + 2) * 8, x = -60 + i * 17 + hash(i) * 6;
    mesh(scene, new THREE.ConeGeometry(r, h, 7), mm, x, h / 2 - 1, -70 - hash(i + 5) * 12, { cast: false, recv: false });
    mesh(scene, new THREE.ConeGeometry(r * .32, h * .3, 7), sn, x, h * .85 - 1, -70 - hash(i + 5) * 12, { cast: false, recv: false });
  }
}
// уличные фонари
Wd.streetLamps = [];
[[-14.5, 8.0], [9.5, 8.0]].forEach(([x, z]) => {
  mesh(scene, gCyl(.05, .07, 4.2, 10), M(0x3a3a3a, { r: .6 }), x, 2.1, z);
  mesh(scene, gBox(.8, .06, .06), M(0x3a3a3a), x + .35, 4.2, z, {});
  const hd = mesh(scene, gRB(.34, .12, .2, .04), M(0xFFE9B0, { e: 0xFFD27A, ei: 1.6, r: .5 }), x + .65, 4.12, z, { cast: false });
  Wd.streetLamps.push(hd);
});
// машина (проезжает в К1)
{
  const car = grp(scene, 0, 0, 11.4); Wd.car = car;
  const body = M(0xE8E8EC, { r: .4, m: .2 }), dk = M(0x2A2F3A, { r: .3 });
  mesh(car, gRB(4.2, .6, 1.8, .22), body, 0, .6, 0);
  mesh(car, gRB(2.2, .55, 1.6, .22), dk, -.15, 1.12, 0);
  [[-1.3, .72], [1.3, .72], [-1.3, -.72], [1.3, -.72]].forEach(([x, z]) => { mesh(car, gCyl(.33, .33, .26, 16), M(0x1b1b1b), x, .33, z, { r: [Math.PI / 2, 0, 0] }); });
  mesh(car, gBox(.06, .14, .3), M(0xFFF3C8, { e: 0xFFF0B0, ei: 2 }), 2.08, .62, .55, { cast: false }); mesh(car, gBox(.06, .14, .3), M(0xFFF3C8, { e: 0xFFF0B0, ei: 2 }), 2.08, .62, -.55, { cast: false });
  mesh(car, gBox(.06, .12, .3), M(0xC8201E, { e: 0xFF2020, ei: 1.2 }), -2.08, .62, .55, { cast: false }); mesh(car, gBox(.06, .12, .3), M(0xC8201E, { e: 0xFF2020, ei: 1.2 }), -2.08, .62, -.55, { cast: false });
}
// скамейка и урна у входа
mesh(scene, gRB(1.6, .1, .45, .03), matWoodD, 4.8, .5, 7.1); mesh(scene, gRB(1.6, .5, .08, .03), matWoodD, 4.8, .78, 7.3); mesh(scene, gBox(.08, .5, .4), M(0x333333), 4.1, .25, 7.1); mesh(scene, gBox(.08, .5, .4), M(0x333333), 5.5, .25, 7.1);

/* ------------------------------------------------ лампы (подвесные) */
Wd.lamps = [];
[[0.6, 0.0], [3.5, 0.6], [-4.6, 0.8]].forEach(([x, z], i) => {
  const g = grp(scene, x, 0, z);
  mesh(g, gCyl(.012, .012, .7, 6), M(0x222222), 0, 2.85, 0, { cast: false });
  mesh(g, gCyl(.07, .36, .3, 20), M(Y, { r: .45, m: .1 }), 0, 2.5, 0, { cast: false });
  const bulbMat = new THREE.MeshStandardMaterial({ color: 0xFFF4D0, emissive: 0xFFEBB0, emissiveIntensity: 1.6, roughness: .5 });
  mesh(g, gSph(.09, 14, 10), bulbMat, 0, 2.33, 0, { cast: false });
  const cone = new THREE.Mesh(new THREE.ConeGeometry(.78, 2.3, 24, 1, true), new THREE.MeshBasicMaterial({ color: 0xFFE8B0, transparent: true, opacity: .06, depthWrite: false, blending: THREE.AdditiveBlending, side: THREE.DoubleSide }));
  cone.position.set(0, 1.18, 0); cone.renderOrder = 3; g.add(cone);
  Wd.lamps.push({ g, bulbMat, cone, x, z });
});

/* ------------------------------------------------ товарные инстансы */
const INST = {};
function inst(type, geoFn, mat) { INST[type] = { geoFn, mat, list: [], cols: [] }; }
{
  const gLoaf = new THREE.CapsuleGeometry(.045, .2, 4, 10); gLoaf.rotateZ(Math.PI / 2); gLoaf.scale(1, .85, 1); gLoaf.translate(0, .04, 0);
  const gDisc = new THREE.CylinderGeometry(.115, .115, .03, 20); gDisc.translate(0, .015, 0);
  const gMilk = new THREE.BoxGeometry(.075, .17, .075); gMilk.translate(0, .085, 0);
  const gSeed = new THREE.BoxGeometry(.12, .2, .035); gSeed.translate(0, .1, 0);
  const gWater = new THREE.CylinderGeometry(.034, .034, .24, 12); gWater.translate(0, .12, 0);
  const gApple = new THREE.SphereGeometry(.045, 10, 8); gApple.translate(0, .045, 0);
  const gEgg = new THREE.BoxGeometry(.2, .06, .15); gEgg.translate(0, .03, 0);
  inst('loaf', () => gLoaf, M(0xD7973F, { r: .7 }));
  inst('disc', () => gDisc, M(0xE7BE74, { r: .8 }));
  inst('milk', () => gMilk, M(0xFFFFFF, { r: .6, tex: texMilk, tag: 'milk' }));
  inst('seed', () => gSeed, M(0xFFFFFF, { r: .6, tex: texSeeds, tag: 'seed' }));
  inst('water', () => gWater, M(0x8FD3F0, { r: .25 }));
  inst('apple', () => gApple, M(0xFFFFFF, { r: .5 }));
  inst('egg', () => gEgg, M(0xFFFFFF, { r: .8, tex: texEggs, tag: 'egg' }));
}
const _m4 = new THREE.Matrix4(), _q = new THREE.Quaternion(), _e = new THREE.Euler(), _p = new THREE.Vector3(), _s = new THREE.Vector3(1, 1, 1);
function addItem(type, x, y, z, ry, sc, col) {
  _e.set(0, ry || 0, 0); _q.setFromEuler(_e); _p.set(x, y, z); _s.set(sc || 1, sc || 1, sc || 1);
  _m4.compose(_p, _q, _s); INST[type].list.push(_m4.clone()); INST[type].cols.push(col == null ? 0xFFFFFF : col);
}
function buildInst() {
  for (const k in INST) {
    const d = INST[k]; if (!d.list.length) continue;
    const im = new THREE.InstancedMesh(d.geoFn(), d.mat, d.list.length);
    d.list.forEach((m, i) => { im.setMatrixAt(i, m); im.setColorAt(i, new THREE.Color(d.cols[i])); });
    im.castShadow = false; im.receiveShadow = true; scene.add(im);
  }
}
const APPLE = [0xD8332B, 0xE05A2B, 0x8FBF3A, 0xD8332B, 0xF0C23B];

/* ------------------------------------------------ стеллажи */
function shelfUnit(cx, cz, w, d, h, faceZ, boards, seed, double) {
  const g = grp(scene, cx, 0, cz);
  mesh(g, gBox(w, .08, d), matWoodD, 0, .04, 0);                         // цоколь
  const nb = boards;
  for (let b = 0; b < nb; b++) { const y = .3 + b * ((h - .5) / (nb - 1)); mesh(g, gBox(w, .04, d), matWood, 0, y, 0); }
  [-w / 2 + .02, w / 2 - .02].forEach(x => mesh(g, gBox(.04, h, d), matWoodD, x, h / 2, 0));
  [0.25, 0.5, 0.75].forEach(f => mesh(g, gBox(.025, h, d), matWoodD, -w / 2 + f * w, h / 2, 0, { cast: false }));
  if (!double) mesh(g, gBox(w, h, .03), M(0xC89257, { r: .9 }), 0, h / 2, -faceZ * (d / 2 - .015), { cast: false });
  // товары
  const sides = double ? [1, -1] : [faceZ];
  sides.forEach((fz, si) => {
    for (let b = 0; b < nb; b++) {
      const y = .3 + b * ((h - .5) / (nb - 1)) + .02;
      let x = -w / 2 + .14, n = 0;
      while (x < w / 2 - .12) {
        const r = hash(seed + b * 31 + n * 7 + si * 13), z0 = cz + fz * (d / 2 - .11);
        const kind = Math.floor(r * 7);
        if (kind === 0) { addItem('loaf', cx + x, y, z0, 0, 1); addItem('loaf', cx + x, y + .075, z0, 0, 1); addItem('loaf', cx + x + .02, y, z0 - fz * .12, 0, 1); x += .26; }
        else if (kind === 1) { for (let k = 0; k < 4; k++) addItem('disc', cx + x, y + k * .032, z0 - fz * .01, k * .5, 1); addItem('disc', cx + x, y, z0 - fz * .12, 1, 1); x += .26; }
        else if (kind === 2) { for (let k = 0; k < 3; k++) for (let j = 0; j < 2; j++) addItem('milk', cx + x + k * .085, y, z0 - fz * j * .09, 0, 1); x += .3; }
        else if (kind === 3) { for (let k = 0; k < 3; k++) addItem('seed', cx + x + k * .14, y, z0 - fz * k * .02, 0, 1); x += .44; }
        else if (kind === 4) { for (let k = 0; k < 4; k++) addItem('water', cx + x + k * .08, y, z0, 0, 1); for (let k = 0; k < 4; k++) addItem('water', cx + x + k * .08, y, z0 - fz * .09, 0, 1); x += .36; }
        else if (kind === 5) { for (let k = 0; k < 3; k++) for (let j = 0; j < 2; j++) addItem('apple', cx + x + k * .1, y, z0 - fz * j * .1, 0, 1, APPLE[(k + j + n) % 5]); x += .34; }
        else { addItem('egg', cx + x + .05, y, z0 - fz * .04, 0, 1); addItem('egg', cx + x + .05, y + .06, z0 - fz * .04, 0, 1); x += .3; }
        n++;
      }
      // ценник
      if (b < nb - 1 || true) {
        const lt = [labelTex(L('Нан', 'Нан'), '30 с'), labelTex(L('Молоко', 'Сүт'), '65 с'), labelTex(L('Семечки', 'Күн карама'), '40 с')][(b + seed) % 3];
        const pl = mesh(g, new THREE.PlaneGeometry(.2, .0875), new THREE.MeshBasicMaterial({ map: lt }), -w * .28 + (b % 2) * w * .5, y - .06, fz * (d / 2 + .004), { cast: false, recv: false });
        if (fz < 0) pl.rotation.y = Math.PI;
      }
    }
  });
  return g;
}
shelfUnit(-4.5, -2.72, 10.2, .5, 2.15, 1, 5, 11, false);
shelfUnit(-5.6, 3.5, 3.0, .5, 1.25, -1, 3, 41, true);
// остров с товарами у прохода (по заднему плану, слева)
shelfUnit(-6.2, -0.9, 3.4, .6, 1.5, 1, 4, 71, true);

// ящик яблок на входе
{
  const g = grp(scene, -7.6, 0, 4.4);
  mesh(g, gBox(.8, .3, .55), matWoodD, 0, .15, 0);
  for (let i = 0; i < 12; i++) addItem('apple', -7.6 - .3 + (i % 4) * .2, .3, 4.4 - .14 + Math.floor(i / 4) * .14, 0, 1.15, APPLE[i % 5]);
}
// баннер «АКЦИЯ»
{
  const tex = ctex(768, 192, (g, w, h) => {
    g.fillStyle = '#E53935'; rrect(g, 4, 4, w - 8, h - 8, 24); g.fill(); g.fillStyle = '#FFE066'; g.font = '800 120px ' + FONT; g.textAlign = 'center'; g.textBaseline = 'middle'; g.fillText(L('АКЦИЯ', 'АКЦИЯ'), w / 2, h / 2 + 6);
  });
  const bn = mesh(scene, new THREE.PlaneGeometry(1.8, .45), new THREE.MeshBasicMaterial({ map: tex, side: THREE.DoubleSide }), -5.4, 2.55, 1.9, { cast: false, recv: false });
  mesh(scene, gCyl(.008, .008, .5, 4), M(0x222222), -6.2, 2.9, 1.9, { cast: false }); mesh(scene, gCyl(.008, .008, .5, 4), M(0x222222), -4.6, 2.9, 1.9, { cast: false });
}

/* ------------------------------------------------ прилавок и касса */
const CT = { x: 3.75, z: -0.2, front: 3.3, top: 1.0 };
Wd.CT = CT;
{
  const g = grp(scene, CT.x, 0, CT.z);
  mesh(g, gRB(.9, .98, 2.7, .05), M(0xFFF3DC, { r: .8 }), 0, .49, 0);
  mesh(g, gRB(.04, .8, 2.5, .02), M(Y, { r: .7 }), -.46, .5, 0, { cast: false });
  mesh(g, gRB(1.05, .07, 2.85, .03), M(0x8A5A3C, { r: .6 }), 0, 1.015, 0);
  // кассовый аппарат (старая касса)
  const reg = grp(scene, 3.85, 1.05, -1.0);
  mesh(reg, gRB(.38, .14, .34, .04), M(0x4A4F5A, { r: .5 }), 0, .07, 0);
  const keys = M(0xCDD2DA, { r: .5 });
  for (let i = 0; i < 4; i++) for (let j = 0; j < 3; j++) mesh(reg, gBox(.05, .02, .045), keys, -.12 + i * .08, .15, .05 + j * .06 - .07, { cast: false });
  const stand = mesh(reg, gBox(.04, .2, .04), M(0x30343c), 0, .24, -.1);
  const scrTex = ctex(256, 128, () => {});
  Wd.regTex = scrTex;
  Wd.drawReg = (mode) => {
    const g2 = scrTex._g, w = 256, h = 128;
    g2.fillStyle = mode === 'off' ? '#0b0f0b' : '#CFE9CF'; g2.fillRect(0, 0, w, h);
    if (mode !== 'off') { g2.fillStyle = '#17361c'; g2.font = '800 ' + (mode === 'price' ? 52 : 40) + 'px ' + FONT; g2.textAlign = 'right'; g2.textBaseline = 'middle'; g2.fillText(mode === 'price' ? '30.00' : '0.00', w - 14, h / 2 + 4); g2.font = '700 22px ' + FONT; g2.textAlign = 'left'; g2.fillText(mode === 'price' ? L('Нан', 'Нан') : 'KASSA', 12, 24); }
    scrTex.needsUpdate = true;
  };
  Wd.drawReg('on');
  const scr = mesh(reg, new THREE.PlaneGeometry(.3, .15), new THREE.MeshBasicMaterial({ map: scrTex }), 0, .38, -.075, { cast: false, recv: false });
  scr.rotation.x = -.35; mesh(reg, gRB(.34, .19, .04, .02), M(0x2A2D33), 0, .38, -.1, { r: [-.35, 0, 0] });
  Wd.regScr = scr;
  // терминал
  mesh(scene, gRB(.12, .035, .2, .015), M(0x20242A, { r: .4 }), 3.85, 1.07, -.5);
  // коробка семечек на прилавке
  mesh(scene, gRB(.34, .22, .24, .03), M(0xFFFFFF, { r: .6, tex: texSeeds, tag: 'seedbox' }), 4.15, 1.17, 0.85);
}

/* ------------------------------------------------ свет сцены */
const hemi = new THREE.HemisphereLight(0xFFF1D8, 0xD8BE98, 1.0);
scene.add(hemi);
const key = new THREE.DirectionalLight(0xFFE8C8, 2.0);
key.position.set(-2.5, 7.5, 10); key.target.position.set(2.2, 0, 0.2);
key.castShadow = true; key.shadow.mapSize.set(1024, 1024);
const sc = key.shadow.camera; sc.left = -7; sc.right = 7; sc.top = 6; sc.bottom = -6; sc.near = 2; sc.far = 30;
key.shadow.bias = -0.0004; key.shadow.normalBias = .03; key.shadow.radius = 4;
scene.add(key); scene.add(key.target);
const moon = new THREE.DirectionalLight(0x7FA0FF, 0.0);
moon.position.set(3, 4, -9); moon.target.position.set(2.5, 0, 0.5); scene.add(moon); scene.add(moon.target);
const lampSpots = Wd.lamps.map(l => {
  const s = new THREE.SpotLight(0xFFE2B0, 30, 0, 1.0, .7, 1.6);
  s.position.set(l.x, 2.45, l.z); s.target.position.set(l.x + .15, 0, l.z); s.castShadow = false;
  scene.add(s); scene.add(s.target); return s;
});
const flash = new THREE.SpotLight(0xDCEBFF, 0, 0, .3, .55, 1.3);
flash.castShadow = true; flash.shadow.mapSize.set(1024, 1024); flash.shadow.bias = -.0005; flash.shadow.normalBias = .02; flash.shadow.camera.near = .3; flash.shadow.camera.far = 14;
scene.add(flash); scene.add(flash.target);
const phoneP = new THREE.PointLight(0xFFB84A, 0, 5, 1.7); scene.add(phoneP);
const phoneS = new THREE.SpotLight(0xFFC060, 0, 0, .9, .8, 1.3); scene.add(phoneS); scene.add(phoneS.target);
Wd.lights = { hemi, key, moon, lampSpots, flash, phoneP, phoneS };
// небо для кадров снаружи
const skyTex = ctex(8, 512, (g, w, h) => {
  const gr = g.createLinearGradient(0, 0, 0, h); gr.addColorStop(0, '#232D5C'); gr.addColorStop(.5, '#6E6AA0'); gr.addColorStop(.8, '#F2A06A'); gr.addColorStop(1, '#F8D190');
  g.fillStyle = gr; g.fillRect(0, 0, w, h);
});
scene.background = skyTex;
Wd.fogDay = new THREE.Fog(0xC9A9A0, 70, 220);
scene.fog = Wd.fogDay;
Wd.buildInst = buildInst;
Wd.addItem = addItem;
Wd.matWood = matWood; Wd.texMilk = texMilk; Wd.texSeeds = texSeeds;
})();
