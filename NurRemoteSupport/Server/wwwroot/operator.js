'use strict';
// 2026-10-16: страница оператора NurMarket: вход по коду и PIN → ждём «Разрешить» кассира → экран кассы на canvas,
// мышь и клавиатура в координатах экрана кассы. Протокол — NurRemoteSupport/PROTOCOL.md (v1).
(function () {
  const $ = (id) => document.getElementById(id);
  const canvas = $('screen');
  const ctx = canvas.getContext('2d', { alpha: false });
  const stage = $('stage');
  const cursorEl = $('cursor');

  const ERRORS = {
    'not-found': 'Код не найден или уже не действует. Проверьте цифры — кассир видит код в окне «Тех. поддержка».',
    'wrong-pin': 'Неверный PIN.',
    'locked': 'Код заблокирован: несколько раз ввели неверный PIN. Попросите кассира получить новый код.',
    'busy': 'К этой кассе уже подключаются или идёт сеанс. Попробуйте через минуту.',
    'rate-limit': 'Слишком много попыток с этого адреса.',
    'bad-key': 'Неверный ключ оператора.',
    'bad-request': 'Сервер не понял запрос. Обновите страницу.',
    'server-busy': 'Сервер поддержки перегружен. Попробуйте позже.',
  };
  const ENDS = {
    declined: 'Кассир отказал в доступе.',
    'consent-timeout': 'Кассир не ответил на запрос. Попросите его нажать «Разрешить» и подключитесь снова.',
    host: 'Кассир завершил сеанс.',
    operator: 'Сеанс завершён.',
    timeout: 'Сеанс завершён: долго не было активности.',
    disconnect: 'Связь с кассой прервалась.',
    server: 'Сервер поддержки перезапускается. Подключитесь снова через минуту.',
  };
  const COMBOS = {
    win: ['MetaLeft'], alttab: ['AltLeft', 'Tab'], altf4: ['AltLeft', 'F4'], taskmgr: ['ControlLeft', 'ShiftLeft', 'Escape'],
    windesk: ['MetaLeft', 'KeyD'], winr: ['MetaLeft', 'KeyR'], esc: ['Escape'],
  };

  let ws = null;
  let phase = 'idle'; // idle | connecting | waiting | session
  let W = 0, H = 0;
  let viewOnly = false, cashierViewOnly = false;
  let endText = '', endKind = 'err';
  let pending = [], chain = Promise.resolve();
  let lastPos = null, moveQueued = false, wheelAcc = 0, wheelQueued = false, lastCursor = null;
  let frames = 0, bytes = 0;
  const keysDown = new Set(), buttonsDown = new Set();

  // ---------- общее ----------

  function load(k) { try { return localStorage.getItem(k) || ''; } catch (e) { return ''; } }
  function store(k, v) { try { if (v) localStorage.setItem(k, v); else localStorage.removeItem(k); } catch (e) { /* хранилище недоступно */ } }
  function send(obj) { if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj)); }
  function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }
  function describe(m) { return [m.company, m.machine, m.version ? 'v' + m.version : ''].filter(Boolean).join(' · '); }

  function show(view) {
    for (const v of ['joinView', 'waitView', 'screenView']) $(v).hidden = v !== view;
    $('tools').hidden = view !== 'screenView';
    document.body.classList.toggle('in-session', view === 'screenView');
  }
  function setConn(text, kind) { const c = $('conn'); c.textContent = text; c.className = 'pill ' + (kind || ''); }
  function setMsg(text, kind) { const m = $('msg'); m.textContent = text || ''; m.className = 'msg ' + (kind || ''); }

  // ---------- вход ----------

  $('name').value = load('nr.name');
  const savedKey = load('nr.key');
  if (savedKey) { $('key').value = savedKey; $('rememberKey').checked = true; }
  const q = new URLSearchParams(location.search);
  if (q.get('code')) $('code').value = q.get('code').replace(/\D/g, '').slice(0, 9).replace(/(\d{3})(?=\d)/g, '$1 ');

  fetch('api/info', { cache: 'no-store' }).then((r) => r.json()).then((info) => {
    if (info.keyRequired) $('keyRow').hidden = false;
    if (info.pinLength) $('pin').placeholder = info.pinLength + ' цифры';
  }).catch(() => { /* сервер недоступен — покажет попытка входа */ });

  $('code').addEventListener('input', (e) => {
    const d = e.target.value.replace(/\D/g, '').slice(0, 9);
    e.target.value = d.replace(/(\d{3})(?=\d)/g, '$1 ');
  });
  $('pin').addEventListener('input', (e) => { e.target.value = e.target.value.replace(/\D/g, '').slice(0, 8); });

  $('joinForm').addEventListener('submit', (e) => {
    e.preventDefault();
    const code = $('code').value.replace(/\D/g, '');
    const pin = $('pin').value.replace(/\D/g, '');
    const name = $('name').value.trim();
    if (code.length !== 9) { setMsg('Код кассы — 9 цифр.', 'err'); $('code').focus(); return; }
    if (!pin) { setMsg('Введите PIN.', 'err'); $('pin').focus(); return; }
    if (!name) { setMsg('Введите своё имя — его увидит кассир.', 'err'); $('name').focus(); return; }
    store('nr.name', name);
    const key = $('key').value;
    store('nr.key', $('rememberKey').checked ? key : '');
    connect({ type: 'join', code, pin, name, key: key || undefined });
  });

  function connect(join) {
    if (ws) { const old = ws; ws = null; try { old.close(); } catch (e) { /* уже закрыт */ } }
    endText = ''; endKind = 'err'; phase = 'connecting';
    setMsg('Подключаюсь…', ''); setConn('Подключение…', 'warn'); $('btnJoin').disabled = true;
    const url = new URL('ws/operator', location.href);
    url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    url.search = ''; url.hash = '';
    const sock = new WebSocket(url.href);
    sock.binaryType = 'arraybuffer';
    ws = sock;
    sock.onopen = () => sock.send(JSON.stringify(join));
    sock.onmessage = (ev) => { if (sock === ws) { if (typeof ev.data === 'string') onText(ev.data); else onBinary(ev.data); } };
    sock.onclose = () => {
      if (sock !== ws) return;
      const text = endText || (phase === 'session' ? 'Связь с сервером поддержки прервалась.'
        : phase === 'connecting' ? 'Не удалось связаться с сервером поддержки. Проверьте интернет.' : 'Связь с сервером поддержки прервалась.');
      finish(text, endText ? endKind : 'err');
    };
  }

  function finish(text, kind) {
    keysDown.clear(); buttonsDown.clear(); pending = []; lastCursor = null;
    phase = 'idle'; ws = null;
    // 2026-10-17: проверка в браузере — после конца сеанса открытый «Вставить текст» оставался поверх формы входа,
    // а на canvas — экран прежней кассы (мелькал при подключении к другой кассе того же размера). Закрыть и стереть.
    if ($('textDialog').open) $('textDialog').close();
    W = 0; H = 0; canvas.width = 16; canvas.height = 9;
    show('joinView');
    $('sessionInfo').hidden = true; $('viewOnlyBadge').hidden = true; cursorEl.hidden = true; $('stats').textContent = '';
    document.body.classList.remove('view-only');
    setConn('Нет связи', ''); $('btnJoin').disabled = false; $('pin').value = '';
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    setMsg(text, kind);
    $('pin').focus();
  }

  // ---------- сообщения сервера ----------

  function onText(s) {
    let m;
    try { m = JSON.parse(s); } catch (e) { return; }
    switch (m.type) {
      case 'ping': send({ type: 'pong' }); break;
      case 'error': {
        let t = ERRORS[m.code] || ('Сервер поддержки ответил ошибкой: ' + m.code);
        if (m.code === 'wrong-pin' && m.attemptsLeft != null) t += ' Осталось попыток: ' + m.attemptsLeft + '.';
        if (m.code === 'rate-limit' && m.retryAfter) t += ' Подождите ' + Math.max(1, Math.ceil(m.retryAfter / 60)) + ' мин.';
        endText = t; endKind = 'err';
        break;
      }
      case 'waiting':
        phase = 'waiting';
        setConn('Ждём кассира', 'warn');
        $('waitInfo').textContent = describe(m) || 'Касса найдена';
        show('waitView');
        break;
      case 'started': startSession(m); break;
      case 'screen': setScreen(m.w, m.h); break;
      case 'cursor': lastCursor = { x: m.x, y: m.y }; placeCursor(); break;
      case 'view-only': cashierViewOnly = !!m.cashier; setViewOnly(!!m.value); break;
      case 'end':
        endText = m.reason === 'operator' && phase === 'waiting' ? 'Запрос отменён.' : (ENDS[m.reason] || 'Сеанс завершён.');
        endKind = m.reason === 'operator' ? 'ok' : 'err';
        break;
    }
  }

  function startSession(m) {
    phase = 'session';
    frames = 0; bytes = 0; pending = []; chain = Promise.resolve();
    cashierViewOnly = !!m.viewOnly;
    setViewOnly(!!m.viewOnly);
    $('sessionInfo').textContent = describe(m);
    $('sessionInfo').hidden = !$('sessionInfo').textContent;
    setConn('Сеанс идёт', 'ok');
    show('screenView');
    if (m.screen && m.screen.w > 0 && m.screen.h > 0) setScreen(m.screen.w, m.screen.h);
    const quality = +$('quality').value;
    if (quality !== 60) send({ type: 'quality', value: quality });
    canvas.focus();
  }

  function setViewOnly(v) {
    viewOnly = v;
    $('viewOnlyBadge').hidden = !v;
    $('viewOnlyBadge').title = cashierViewOnly ? 'Кассир разрешил только просмотр' : 'Мышь и клавиатура кассе не передаются';
    $('btnViewOnly').classList.toggle('on', v);
    $('btnViewOnly').disabled = cashierViewOnly;
    $('btnText').disabled = v;
    $('combo').disabled = v;
    document.body.classList.toggle('view-only', v);
    if (v) releaseAll();
  }

  function setScreen(w, h) {
    if (!(w > 0 && h > 0)) return;
    if (w !== W || h !== H) {
      W = w; H = h;
      canvas.width = w; canvas.height = h;
      ctx.fillStyle = '#000'; ctx.fillRect(0, 0, w, h);
    }
    fit();
  }

  function fit() {
    if (!W || !H || phase !== 'session') return;
    const r = stage.getBoundingClientRect();
    const k = Math.min(r.width / W, r.height / H);
    canvas.style.width = Math.max(1, Math.floor(W * k)) + 'px';
    canvas.style.height = Math.max(1, Math.floor(H * k)) + 'px';
    placeCursor();
  }
  window.addEventListener('resize', fit);
  // 2026-10-17: панель сверху меняет высоту и без изменения окна (появился значок «Только просмотр», перенос строки
  // из-за счётчика кадров) — тогда низ экрана кассы обрезался. Вписывать заново при любом изменении области экрана.
  if (window.ResizeObserver) new ResizeObserver(() => fit()).observe(stage);

  function placeCursor() {
    if (!lastCursor || phase !== 'session' || !W) { cursorEl.hidden = true; return; }
    const k = canvas.clientWidth / W;
    cursorEl.hidden = false;
    cursorEl.style.transform = 'translate(' + (canvas.offsetLeft + lastCursor.x * k) + 'px,' + (canvas.offsetTop + lastCursor.y * k) + 'px)';
  }

  // Кадр кассы: [тип 1|2][u32 номер][u16 x][u16 y][u16 w][u16 h][JPEG] (little-endian). Тип 1 — кусок экрана,
  // тип 2 — конец кадра: после отрисовки всех кусков — «ack» с номером (касса держит не больше 2 неподтверждённых кадров).
  function onBinary(buf) {
    bytes += buf.byteLength;
    if (phase !== 'session' || buf.byteLength < 13) return;
    const dv = new DataView(buf);
    const kind = dv.getUint8(0), frame = dv.getUint32(1, true);
    if (kind === 1) {
      const x = dv.getUint16(5, true), y = dv.getUint16(7, true);
      const blob = new Blob([new Uint8Array(buf, 13)], { type: 'image/jpeg' });
      pending.push(createImageBitmap(blob).then((b) => ({ b, x, y }), () => null));
    } else if (kind === 2) {
      const tiles = pending;
      pending = [];
      chain = chain
        .then(() => Promise.all(tiles))
        .then((list) => { for (const t of list) if (t) { ctx.drawImage(t.b, t.x, t.y); t.b.close(); } })
        .catch(() => { /* битый кусок — пропускаем, кадр всё равно подтверждаем */ })
        .then(() => { frames++; send({ type: 'ack', frame }); });
    }
  }

  // ---------- мышь ----------

  function canInput() { return phase === 'session' && !viewOnly && W > 0; }
  function posOf(e) {
    const r = canvas.getBoundingClientRect();
    return {
      x: clamp(Math.floor((e.clientX - r.left) * W / r.width), 0, W - 1),
      y: clamp(Math.floor((e.clientY - r.top) * H / r.height), 0, H - 1),
    };
  }
  function button(b) { return b === 2 ? 2 : b === 1 ? 1 : 0; } // 0 — левая, 1 — средняя, 2 — правая

  canvas.addEventListener('mousemove', (e) => {
    if (!canInput()) return;
    lastPos = posOf(e);
    if (!moveQueued) { moveQueued = true; requestAnimationFrame(flushMove); }
  });
  function flushMove() {
    moveQueued = false;
    if (lastPos && canInput()) send({ type: 'mouse', action: 'move', x: lastPos.x, y: lastPos.y });
  }
  canvas.addEventListener('mousedown', (e) => {
    canvas.focus();
    if (!canInput()) return;
    e.preventDefault();
    // 2026-10-17: боковые кнопки мыши («Назад»/«Вперёд», button 3/4) уходили на кассу левым кликом — пропускаем.
    if (e.button > 2) return;
    lastPos = posOf(e);
    const b = button(e.button);
    buttonsDown.add(b);
    send({ type: 'mouse', action: 'down', x: lastPos.x, y: lastPos.y, button: b });
  });
  window.addEventListener('mouseup', (e) => {
    // 2026-10-17: боковая кнопка не отпускает левую; в сеансе браузер не уходит «Назад» (страница закрыла бы сеанс).
    if (e.button > 2) { if (phase === 'session') e.preventDefault(); return; }
    const b = button(e.button);
    if (!buttonsDown.has(b)) return;
    buttonsDown.delete(b);
    if (phase !== 'session' || !W) return;
    const p = posOf(e);
    send({ type: 'mouse', action: 'up', x: p.x, y: p.y, button: b });
  });
  canvas.addEventListener('contextmenu', (e) => e.preventDefault());
  canvas.addEventListener('wheel', (e) => {
    e.preventDefault();
    if (!canInput()) return;
    lastPos = posOf(e);
    // Windows: +120 — колесо от себя (вверх); в браузере deltaY > 0 — вниз.
    const k = e.deltaMode === 1 ? 40 : e.deltaMode === 2 ? 360 : 1.2;
    wheelAcc += -e.deltaY * k;
    if (!wheelQueued) { wheelQueued = true; requestAnimationFrame(flushWheel); }
  }, { passive: false });
  function flushWheel() {
    wheelQueued = false;
    const d = clamp(Math.trunc(wheelAcc), -1200, 1200);
    if (!d || !lastPos || !canInput()) { if (!canInput()) wheelAcc = 0; return; }
    wheelAcc -= d;
    send({ type: 'mouse', action: 'wheel', x: lastPos.x, y: lastPos.y, delta: d });
  }

  // ---------- клавиатура ----------

  canvas.addEventListener('keydown', (e) => {
    if (phase !== 'session') return;
    e.preventDefault();
    if (!canInput()) return;
    keysDown.add(e.code);
    send({ type: 'key', action: 'down', code: e.code, key: e.key });
  });
  canvas.addEventListener('keyup', (e) => {
    if (phase !== 'session') return;
    e.preventDefault();
    if (!keysDown.has(e.code)) return;
    keysDown.delete(e.code);
    send({ type: 'key', action: 'up', code: e.code, key: e.key });
  });
  // Ушли из окна или из экрана кассы — отпустить всё, что держали (иначе на кассе «залипнет» Ctrl или кнопка мыши).
  function releaseAll() {
    for (const code of keysDown) send({ type: 'key', action: 'up', code });
    keysDown.clear();
    if (lastPos) for (const b of buttonsDown) send({ type: 'mouse', action: 'up', x: lastPos.x, y: lastPos.y, button: b });
    buttonsDown.clear();
  }
  canvas.addEventListener('blur', releaseAll);
  window.addEventListener('blur', releaseAll);
  document.addEventListener('visibilitychange', () => { if (document.hidden) releaseAll(); });

  function sendCombo(codes) {
    if (!canInput()) return;
    for (const c of codes) send({ type: 'key', action: 'down', code: c });
    for (const c of codes.slice().reverse()) send({ type: 'key', action: 'up', code: c });
  }

  // ---------- панель ----------

  $('quality').addEventListener('change', (e) => { send({ type: 'quality', value: +e.target.value }); canvas.focus(); });
  $('combo').addEventListener('change', (e) => {
    const codes = COMBOS[e.target.value];
    e.target.value = '';
    if (codes) sendCombo(codes);
    canvas.focus();
  });
  $('btnViewOnly').addEventListener('click', () => { if (!cashierViewOnly) send({ type: 'view-only', value: !viewOnly }); canvas.focus(); });
  $('btnText').addEventListener('click', () => { if (canInput()) { $('textInput').value = ''; $('textDialog').showModal(); } });
  $('textCancel').addEventListener('click', () => $('textDialog').close());
  $('textSend').addEventListener('click', () => {
    const text = $('textInput').value;
    $('textDialog').close();
    if (text && canInput()) send({ type: 'text', text: text.slice(0, 10000) });
    canvas.focus();
  });
  $('textDialog').addEventListener('close', () => { if (phase === 'session') canvas.focus(); });

  $('btnFull').addEventListener('click', () => {
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    else document.documentElement.requestFullscreen().catch(() => {});
  });
  document.addEventListener('fullscreenchange', () => {
    const full = !!document.fullscreenElement;
    $('btnFull').textContent = full ? 'Обычный режим' : 'Во весь экран';
    // В полноэкранном режиме Chrome/Edge отдают странице Esc, Win, Alt+Tab — они уходят на кассу.
    try {
      if (navigator.keyboard) { if (full) navigator.keyboard.lock().catch(() => {}); else navigator.keyboard.unlock(); }
    } catch (e) { /* браузер не умеет — не страшно */ }
    setTimeout(fit, 50);
    canvas.focus();
  });

  function endSession() {
    if (!ws) return;
    endText = ENDS.operator; endKind = 'ok';
    send({ type: 'end' });
    const sock = ws;
    setTimeout(() => { if (ws === sock) { try { sock.close(); } catch (e) { /* уже закрыт */ } } }, 2000);
  }
  $('btnEnd').addEventListener('click', endSession);
  $('btnCancel').addEventListener('click', () => { endSession(); endText = 'Запрос отменён.'; endKind = 'ok'; });
  window.addEventListener('pagehide', () => { if (phase === 'session' || phase === 'waiting') send({ type: 'end' }); });

  setInterval(() => {
    if (phase === 'session') $('stats').textContent = frames + ' кадр/с · ' + Math.round(bytes / 1024) + ' КБ/с';
    frames = 0; bytes = 0;
  }, 1000);
})();
