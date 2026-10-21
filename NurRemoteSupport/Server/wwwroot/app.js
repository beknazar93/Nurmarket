// Консоль оператора удалённой поддержки NurMarket (2026-09-29).
// Без сборщиков и библиотек. Протокол — NurRemoteSupport/PROTOCOL.md.
'use strict';

(() => {
  const $ = (id) => document.getElementById(id);
  const AUTH_KEY = 'nrs-auth';

  // ---------- состояние ----------
  const st = {
    auth: null,          // {token, name, login, expiresAt}
    ws: null,
    phase: 'idle',       // idle | joining | waiting | viewer | ended
    host: null,          // данные кассы из started
    viewOnly: false,
    actualSize: false,
    monitors: 1,
    endShown: false,
    // кадры
    frameTiles: [],
    drawChain: Promise.resolve(),
    gotFirstFrame: false,
    // статистика
    frames: 0, bytes: 0, rtt: null, hostStats: null,
    // ввод
    pressedKeys: new Set(),
    pressedButtons: new Set(),
    pendingMove: null, lastMoveAt: 0, moveTimer: 0,
    wheelAcc: 0,
    cursor: null,
  };

  const canvas = $('screen');
  const ctx2d = canvas.getContext('2d', { alpha: false, desynchronized: true });
  const stage = $('stage');

  // ---------- общие мелочи ----------
  let toastTimer = 0;
  function toast(text, ms = 4000) {
    const t = $('toast');
    t.textContent = text;
    t.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { t.hidden = true; }, ms);
  }

  function show(view) {
    for (const v of ['login', 'connect', 'waiting', 'viewer', 'journal']) $('view-' + v).hidden = v !== view;
    $('topbar').hidden = view === 'login' || view === 'viewer';
    document.querySelectorAll('.navbtn').forEach((b) => b.classList.toggle('active', b.dataset.nav === view));
    document.title = view === 'viewer' && st.host ? `${st.host.company || 'Касса'} · Поддержка` : 'NurMarket · Поддержка';
  }

  function loadAuth() {
    try {
      const a = JSON.parse(sessionStorage.getItem(AUTH_KEY) || 'null');
      if (a && a.token && new Date(a.expiresAt) > new Date()) return a;
    } catch { /* нет хранилища — войдём заново */ }
    return null;
  }
  function saveAuth(a) {
    st.auth = a;
    try { a ? sessionStorage.setItem(AUTH_KEY, JSON.stringify(a)) : sessionStorage.removeItem(AUTH_KEY); } catch { /* ничего */ }
  }

  async function api(path, opts = {}) {
    const headers = Object.assign({}, opts.headers || {});
    if (st.auth) headers.Authorization = 'Bearer ' + st.auth.token;
    const res = await fetch(path, Object.assign({}, opts, { headers }));
    if (res.status === 401 && path !== '/api/login') {
      logout('Время входа истекло — войдите снова.');
      throw new Error('unauthorized');
    }
    return res;
  }

  function logout(message) {
    closeSocket();
    saveAuth(null);
    show('login');
    $('login-error').textContent = message || '';
    $('login-password').value = '';
    setTimeout(() => $('login-login').focus(), 0);
  }

  // ---------- вход ----------
  $('login-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const btn = $('login-submit');
    btn.disabled = true;
    $('login-error').textContent = '';
    try {
      const res = await fetch('/api/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ login: $('login-login').value.trim(), password: $('login-password').value }),
      });
      if (res.status === 401) { $('login-error').textContent = 'Неверный логин или пароль.'; return; }
      if (res.status === 429) { $('login-error').textContent = 'Слишком много попыток. Подождите пару минут.'; return; }
      if (!res.ok) { $('login-error').textContent = 'Сервер ответил ошибкой (' + res.status + ').'; return; }
      const data = await res.json();
      saveAuth({ token: data.token, name: data.name, login: data.login, expiresAt: data.expiresAt });
      $('login-password').value = '';
      enterApp();
    } catch {
      $('login-error').textContent = 'Нет связи с сервером.';
    } finally {
      btn.disabled = false;
    }
  });

  $('logout').addEventListener('click', () => logout(''));

  function enterApp() {
    $('me-name').textContent = st.auth.name || st.auth.login;
    goConnect();
  }

  document.querySelectorAll('.navbtn').forEach((b) => b.addEventListener('click', () => {
    if (b.dataset.nav === 'journal') openJournal(); else goConnect();
  }));

  // ---------- код и PIN ----------
  const codeInput = $('join-code');
  const pinInput = $('join-pin');
  codeInput.addEventListener('input', () => {
    const d = codeInput.value.replace(/\D/g, '').slice(0, 9);
    codeInput.value = d.replace(/(\d{3})(?=\d)/g, '$1 ');
    if (d.length === 9 && document.activeElement === codeInput) pinInput.focus();
  });
  pinInput.addEventListener('input', () => { pinInput.value = pinInput.value.replace(/\D/g, '').slice(0, 4); });

  function goConnect(errorText) {
    st.phase = 'idle';
    show('connect');
    $('join-error').textContent = errorText || '';
    $('join-submit').disabled = false;
    setTimeout(() => (codeInput.value ? pinInput : codeInput).focus(), 0);
  }

  $('join-form').addEventListener('submit', (e) => {
    e.preventDefault();
    const code = codeInput.value.replace(/\D/g, '');
    const pin = pinInput.value.replace(/\D/g, '');
    if (code.length !== 9) { $('join-error').textContent = 'Код — 9 цифр.'; return; }
    if (pin.length !== 4) { $('join-error').textContent = 'PIN — 4 цифры.'; return; }
    $('join-error').textContent = '';
    $('join-submit').disabled = true;
    st.phase = 'joining';
    ensureSocket(() => send({ type: 'join', code, pin }));
  });

  $('waiting-cancel').addEventListener('click', () => {
    send({ type: 'end', reason: 'operator' });
    goConnect('Подключение отменено.');
  });

  const ERRORS = {
    'bad-pin': 'Неверный PIN.',
    'no-session': 'Сеанс с таким кодом не найден. Проверьте код или попросите кассира получить новый.',
    'locked': 'Слишком много неверных PIN — код заблокирован. Кассир должен получить новый код.',
    'busy': 'К этой кассе уже подключается или подключён другой оператор.',
    'declined': 'Кассир не разрешил подключение.',
    'expired': 'Код устарел (действует 15 минут). Попросите кассира получить новый.',
    'rate-limited': 'Слишком много попыток. Подождите минуту.',
    'bad-request': 'Сервер не понял запрос. Обновите страницу.',
    'not-joined': 'Сеанс уже завершён.',
  };

  const END_REASONS = {
    operator: 'Сеанс завершил оператор.',
    host: 'Кассир завершил сеанс.',
    timeout: 'Связь с кассой пропала (нет ответа 45 секунд).',
    error: 'Сеанс прерван из-за ошибки связи.',
    disconnect: 'Касса отключилась.',
    shutdown: 'Сервер поддержки перезапускается.',
    declined: 'Кассир не разрешил подключение.',
    'consent-timeout': 'Кассир не ответил на запрос.',
  };

  // ---------- WebSocket ----------
  function ensureSocket(onReady) {
    if (st.ws && st.ws.readyState === WebSocket.OPEN) { onReady(); return; }
    closeSocket();
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(`${proto}//${location.host}/ws/operator?token=${encodeURIComponent(st.auth.token)}`);
    ws.binaryType = 'arraybuffer';
    st.ws = ws;
    let opened = false;
    ws.onopen = () => { opened = true; onReady(); };
    ws.onmessage = (e) => {
      if (ws !== st.ws) return;
      if (typeof e.data === 'string') {
        let m;
        try { m = JSON.parse(e.data); } catch { return; }
        onText(m);
      } else {
        onBinary(e.data);
      }
    };
    ws.onclose = () => {
      if (ws !== st.ws) return;
      st.ws = null;
      onSocketClosed(opened);
    };
  }

  function closeSocket() {
    const ws = st.ws;
    st.ws = null;
    if (ws) try { ws.close(1000, 'bye'); } catch { /* уже закрыт */ }
  }

  function send(obj) {
    const ws = st.ws;
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
  }

  async function onSocketClosed(wasOpened) {
    if (st.phase === 'viewer') {
      if (!st.endShown) showEnded('Связь с сервером потеряна', 'Сеанс прерван. Коды одноразовые — попросите кассира получить новый код и подключитесь снова.');
      setLive(false);
      return;
    }
    if (st.phase === 'joining' || st.phase === 'waiting') {
      if (!wasOpened) {
        // Возможно, истёк токен
        try {
          const r = await api('/api/me');
          if (r.ok) goConnect('Не удалось подключиться к серверу поддержки. Проверьте интернет.');
        } catch { /* api() уже отправил на вход */ if (st.auth) goConnect('Нет связи с сервером.'); }
      } else {
        goConnect('Связь с сервером потеряна. Попробуйте снова.');
      }
    }
  }

  function onText(m) {
    switch (m.type) {
      case 'ping': send({ type: 'pong' }); break;
      case 'pong': if (typeof m.t === 'number') st.rtt = Math.round(performance.now() - m.t); break;
      case 'waiting-consent':
        st.phase = 'waiting';
        show('waiting');
        break;
      case 'error': {
        if (st.phase === 'joining' || st.phase === 'waiting') {
          let text = ERRORS[m.code] || ('Ошибка: ' + m.code);
          if (m.code === 'bad-pin' && typeof m.left === 'number') text += ` Осталось попыток: ${m.left}.`;
          goConnect(text);
          if (m.code === 'bad-pin') { pinInput.value = ''; pinInput.focus(); }
        }
        break;
      }
      case 'started':
        startViewer(m);
        break;
      case 'end':
        if (st.phase === 'viewer') {
          showEnded('Сеанс завершён', END_REASONS[m.reason] || ('Причина: ' + m.reason));
        } else if (st.phase === 'waiting' || st.phase === 'joining') {
          goConnect(END_REASONS[m.reason] || 'Подключение прервано.');
        }
        break;
      case 'screen': onScreen(m); break;
      case 'cursor': onCursor(m); break;
      case 'stats': st.hostStats = m; break;
      default: break; // неизвестное — пропускаем (протокол может расширяться)
    }
  }

  // ---------- просмотр ----------
  let statsTimer = 0, pingTimer = 0;

  function startViewer(m) {
    st.phase = 'viewer';
    st.endShown = false;
    st.host = Object.assign({ company: '', machine: '', version: '', screen: { w: 0, h: 0 } }, m.host || {});
    st.viewOnly = false;
    st.frameTiles = [];
    st.drawChain = Promise.resolve();
    st.gotFirstFrame = false;
    st.frames = 0; st.bytes = 0; st.rtt = null; st.hostStats = null;
    st.cursor = null;
    $('remote-cursor').hidden = true;
    $('vw-company').textContent = st.host.company || 'Касса';
    $('vw-machine').textContent = [st.host.machine, st.host.version && ('v' + st.host.version), 'оператор: ' + (m.operator || '')].filter(Boolean).join(' · ');
    $('tb-viewonly').setAttribute('aria-pressed', 'false');
    canvas.classList.remove('view-only');
    $('tb-quality').value = '';
    $('tb-monitor-wrap').hidden = true;
    $('ended').hidden = true;
    $('banner').hidden = true;
    $('stage-msg').hidden = false;
    $('stage-msg').textContent = 'Ждём первый кадр…';
    setLive(true);
    const w = st.host.screen && st.host.screen.w, h = st.host.screen && st.host.screen.h;
    if (w > 0 && h > 0) setCanvasSize(w, h);
    show('viewer');
    fit();
    canvas.focus();
    clearInterval(statsTimer); clearInterval(pingTimer);
    statsTimer = setInterval(updateStats, 1000);
    pingTimer = setInterval(() => send({ type: 'ping', t: performance.now() }), 2000);
    send({ type: 'ping', t: performance.now() });
  }

  function setLive(ok) { $('live-dot').classList.toggle('bad', !ok); }

  function stopViewerTimers() {
    clearInterval(statsTimer); clearInterval(pingTimer);
    statsTimer = pingTimer = 0;
  }

  function showEnded(title, text) {
    st.endShown = true;
    releaseAll();
    stopViewerTimers();
    setLive(false);
    $('ended-title').textContent = title;
    $('ended-text').textContent = text;
    $('ended').hidden = false;
    $('text-dialog').hidden = true;
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    setTimeout(() => $('ended-ok').focus(), 0);
  }

  $('ended-ok').addEventListener('click', () => {
    closeSocket();
    st.phase = 'idle';
    codeInput.value = '';
    pinInput.value = '';
    goConnect();
  });

  function setCanvasSize(w, h) {
    if (canvas.width !== w || canvas.height !== h) {
      canvas.width = w;
      canvas.height = h;
      ctx2d.fillStyle = '#000';
      ctx2d.fillRect(0, 0, w, h);
    }
    fit();
  }

  function onScreen(m) {
    const w = m.w | 0, h = m.h | 0;
    if (w > 0 && h > 0 && w <= 16384 && h <= 16384) setCanvasSize(w, h);
    st.monitors = Math.max(1, m.monitors | 0);
    const sel = $('tb-monitor');
    if (st.monitors > 1) {
      if (sel.options.length !== st.monitors) {
        sel.innerHTML = '';
        for (let i = 0; i < st.monitors; i++) {
          const o = document.createElement('option');
          o.value = String(i);
          o.textContent = String(i + 1);
          sel.appendChild(o);
        }
      }
      sel.value = String(m.monitor | 0);
      $('tb-monitor-wrap').hidden = false;
    } else {
      $('tb-monitor-wrap').hidden = true;
    }
  }

  function onBinary(buf) {
    if (st.phase !== 'viewer' || buf.byteLength < 13) return;
    st.bytes += buf.byteLength;
    const dv = new DataView(buf);
    const kind = dv.getUint8(0);
    const frame = dv.getUint32(1, true);
    if (kind === 1) {
      const x = dv.getUint16(5, true), y = dv.getUint16(7, true), w = dv.getUint16(9, true), h = dv.getUint16(11, true);
      const blob = new Blob([new Uint8Array(buf, 13)], { type: 'image/jpeg' });
      st.frameTiles.push(createImageBitmap(blob).then((bmp) => ({ bmp, x, y, w, h }), () => null));
    } else if (kind === 2) {
      const tiles = st.frameTiles;
      st.frameTiles = [];
      // Кадры рисуются строго по очереди; ack — только когда кадр целиком на экране.
      st.drawChain = st.drawChain
        .then(() => Promise.all(tiles))
        .then((list) => {
          for (const t of list) {
            if (!t) continue;
            ctx2d.drawImage(t.bmp, t.x, t.y, t.w || t.bmp.width, t.h || t.bmp.height);
            t.bmp.close();
          }
          if (!st.gotFirstFrame) { st.gotFirstFrame = true; $('stage-msg').hidden = true; }
          st.frames++;
          send({ type: 'ack', frame });
        })
        .catch(() => send({ type: 'ack', frame }));
    }
  }

  function onCursor(m) {
    st.cursor = { x: m.x | 0, y: m.y | 0 };
    placeCursor();
  }

  function placeCursor() {
    const el = $('remote-cursor');
    if (!st.cursor || !canvas.width) { el.hidden = true; return; }
    const r = canvas.getBoundingClientRect();
    const s = stage.getBoundingClientRect();
    const px = r.left - s.left + stage.scrollLeft + st.cursor.x * r.width / canvas.width;
    const py = r.top - s.top + stage.scrollTop + st.cursor.y * r.height / canvas.height;
    el.style.transform = `translate(${Math.round(px)}px, ${Math.round(py)}px)`;
    el.hidden = false;
  }

  function fit() {
    if (!canvas.width || !canvas.height) return;
    if (st.actualSize) {
      canvas.style.width = canvas.width + 'px';
      canvas.style.height = canvas.height + 'px';
    } else {
      const r = stage.getBoundingClientRect();
      const k = Math.min(r.width / canvas.width, r.height / canvas.height);
      if (!(k > 0)) return;
      canvas.style.width = Math.floor(canvas.width * k) + 'px';
      canvas.style.height = Math.floor(canvas.height * k) + 'px';
    }
    placeCursor();
  }
  window.addEventListener('resize', fit);
  if (window.ResizeObserver) new ResizeObserver(fit).observe(stage);
  stage.addEventListener('scroll', placeCursor);

  function updateStats() {
    const kbps = Math.round(st.bytes * 8 / 1000);
    const parts = [
      `${st.frames} к/с`,
      `${kbps} кбит/с`,
      st.rtt == null ? '— мс' : `${st.rtt} мс`,
    ];
    $('stats').textContent = parts.join(' · ');
    if (st.hostStats) $('stats').title = `Касса: ${st.hostStats.fps ?? '—'} к/с, ${st.hostStats.kbps ?? '—'} кбит/с. Задержка — до сервера поддержки.`;
    st.frames = 0;
    st.bytes = 0;
  }

  // ---------- ввод: мышь ----------
  function controlOn() { return st.phase === 'viewer' && !st.viewOnly && !st.endShown; }

  function toHost(e) {
    const r = canvas.getBoundingClientRect();
    let x = Math.floor((e.clientX - r.left) * canvas.width / r.width);
    let y = Math.floor((e.clientY - r.top) * canvas.height / r.height);
    x = Math.max(0, Math.min(canvas.width - 1, x));
    y = Math.max(0, Math.min(canvas.height - 1, y));
    return { x, y };
  }

  function flushMove() {
    clearTimeout(st.moveTimer);
    st.moveTimer = 0;
    if (!st.pendingMove) return;
    send({ type: 'mouse', action: 'move', x: st.pendingMove.x, y: st.pendingMove.y });
    st.pendingMove = null;
    st.lastMoveAt = performance.now();
  }

  canvas.addEventListener('mousemove', (e) => {
    if (!controlOn()) return;
    st.pendingMove = toHost(e);
    const wait = 16 - (performance.now() - st.lastMoveAt); // ~60 в секунду
    if (wait <= 0) flushMove();
    else if (!st.moveTimer) st.moveTimer = setTimeout(flushMove, wait);
  });

  canvas.addEventListener('mousedown', (e) => {
    e.preventDefault();
    canvas.focus();
    closeKeysMenu();
    if (!controlOn() || e.button > 2) return;
    st.pendingMove = toHost(e);
    flushMove();
    const p = toHost(e);
    st.pressedButtons.add(e.button);
    send({ type: 'mouse', action: 'down', x: p.x, y: p.y, button: e.button });
  });

  window.addEventListener('mouseup', (e) => {
    if (!st.pressedButtons.has(e.button)) return;
    st.pressedButtons.delete(e.button);
    if (st.phase !== 'viewer') return;
    flushMove();
    const p = toHost(e);
    send({ type: 'mouse', action: 'up', x: p.x, y: p.y, button: e.button });
  });

  canvas.addEventListener('contextmenu', (e) => e.preventDefault());
  canvas.addEventListener('auxclick', (e) => e.preventDefault());

  canvas.addEventListener('wheel', (e) => {
    e.preventDefault();
    if (!controlOn()) return;
    // В единицах Windows: 120 = щелчок вверх (см. PROTOCOL.md)
    const k = e.deltaMode === 1 ? 40 : e.deltaMode === 2 ? 360 : 1.2;
    st.wheelAcc += -e.deltaY * k;
    const delta = Math.trunc(st.wheelAcc);
    if (delta === 0) return;
    st.wheelAcc -= delta;
    const p = toHost(e);
    send({ type: 'mouse', action: 'wheel', x: p.x, y: p.y, delta: Math.max(-1200, Math.min(1200, delta)) });
  }, { passive: false });

  // ---------- ввод: клавиатура ----------
  function keyboardTarget() {
    if (st.phase !== 'viewer' || st.endShown || !$('text-dialog').hidden) return false;
    const a = document.activeElement;
    return a === canvas || a === document.body || a === stage;
  }

  const MODIFIERS = ['ControlLeft', 'ControlRight', 'ShiftLeft', 'ShiftRight', 'AltLeft', 'AltRight', 'MetaLeft', 'MetaRight'];
  function releaseModifiers() {
    for (const c of MODIFIERS) if (st.pressedKeys.has(c)) { st.pressedKeys.delete(c); send({ type: 'key', action: 'up', code: c, key: '' }); }
  }
  function releaseAll() {
    for (const c of Array.from(st.pressedKeys)) send({ type: 'key', action: 'up', code: c, key: '' });
    st.pressedKeys.clear();
    for (const b of Array.from(st.pressedButtons)) send({ type: 'mouse', action: 'up', x: 0, y: 0, button: b });
    st.pressedButtons.clear();
  }

  window.addEventListener('keydown', (e) => {
    if (!keyboardTarget()) return;
    // Ctrl+V — вставить буфер оператора как текст (событие paste ниже), клавиши не шлём
    if ((e.ctrlKey || e.metaKey) && !e.altKey && e.code === 'KeyV') return;
    e.preventDefault();
    if (!controlOn() || !e.code) return;
    st.pressedKeys.add(e.code);
    send({ type: 'key', action: 'down', code: e.code, key: e.key });
  }, true);

  window.addEventListener('keyup', (e) => {
    if (st.phase !== 'viewer') return;
    if (keyboardTarget()) e.preventDefault();
    if (!st.pressedKeys.has(e.code)) return;
    st.pressedKeys.delete(e.code);
    send({ type: 'key', action: 'up', code: e.code, key: e.key });
  }, true);

  document.addEventListener('paste', (e) => {
    if (!keyboardTarget() || !controlOn()) return;
    const text = e.clipboardData && e.clipboardData.getData('text');
    e.preventDefault();
    if (text) sendText(text);
  });

  function sendText(text) {
    releaseModifiers();
    const t = text.replace(/\r\n/g, '\n').slice(0, 10000);
    send({ type: 'text', text: t });
    toast(`Отправлено символов: ${t.length}`, 2000);
  }

  window.addEventListener('blur', () => { if (st.phase === 'viewer') releaseAll(); });
  document.addEventListener('visibilitychange', () => { if (document.hidden && st.phase === 'viewer') releaseAll(); });

  // ---------- панель ----------
  $('tb-viewonly').addEventListener('click', () => {
    st.viewOnly = !st.viewOnly;
    if (st.viewOnly) releaseAll();
    $('tb-viewonly').setAttribute('aria-pressed', String(st.viewOnly));
    canvas.classList.toggle('view-only', st.viewOnly);
    send({ type: 'view-only', value: st.viewOnly });
    toast(st.viewOnly ? 'Только просмотр: мышь и клавиатура не передаются.' : 'Управление включено.', 2500);
    canvas.focus();
  });

  $('tb-quality').addEventListener('change', (e) => {
    const v = parseInt(e.target.value, 10);
    if (v >= 10 && v <= 90) send({ type: 'quality', value: v });
    canvas.focus();
  });

  $('tb-monitor').addEventListener('change', (e) => {
    send({ type: 'monitor', index: parseInt(e.target.value, 10) || 0 });
    canvas.focus();
  });

  $('tb-scale').addEventListener('click', () => {
    st.actualSize = !st.actualSize;
    stage.classList.toggle('actual', st.actualSize);
    $('tb-scale-label').textContent = st.actualSize ? '100%' : 'Вписать';
    fit();
    canvas.focus();
  });

  $('tb-fullscreen').addEventListener('click', async () => {
    const el = $('view-viewer');
    try {
      if (document.fullscreenElement) {
        await document.exitFullscreen();
      } else {
        await el.requestFullscreen();
        // В Chrome/Edge во весь экран можно перехватить и Win, Alt+Tab, Esc
        if (navigator.keyboard && navigator.keyboard.lock) {
          navigator.keyboard.lock().then(() => toast('Во весь экран: чтобы выйти, удерживайте Esc.', 3500)).catch(() => {});
        }
      }
    } catch { toast('Браузер не разрешил полноэкранный режим.'); }
    canvas.focus();
  });
  document.addEventListener('fullscreenchange', () => {
    if (!document.fullscreenElement && navigator.keyboard && navigator.keyboard.unlock) navigator.keyboard.unlock();
    setTimeout(fit, 50);
  });

  // Сочетания клавиш
  function closeKeysMenu() { $('keys-menu').hidden = true; $('tb-keys').setAttribute('aria-expanded', 'false'); }
  $('tb-keys').addEventListener('click', (e) => {
    e.stopPropagation();
    const m = $('keys-menu');
    m.hidden = !m.hidden;
    $('tb-keys').setAttribute('aria-expanded', String(!m.hidden));
  });
  document.addEventListener('click', (e) => { if (!e.target.closest('.menu-wrap')) closeKeysMenu(); });
  $('keys-menu').addEventListener('click', (e) => {
    const b = e.target.closest('button');
    if (!b) return;
    closeKeysMenu();
    if (b.id === 'tb-cad') {
      toast('Ctrl+Alt+Del передать нельзя: Windows принимает его только с настоящей клавиатуры. Для диспетчера задач — Ctrl+Shift+Esc.', 7000);
      canvas.focus();
      return;
    }
    if (!controlOn()) { toast('Включён режим «Только просмотр».'); return; }
    const codes = b.dataset.combo.split(',');
    releaseAll();
    for (const c of codes) send({ type: 'key', action: 'down', code: c, key: '' });
    for (const c of codes.slice().reverse()) send({ type: 'key', action: 'up', code: c, key: '' });
    canvas.focus();
  });

  // Текст
  $('tb-text').addEventListener('click', () => {
    if (!controlOn()) { toast('Включён режим «Только просмотр».'); return; }
    releaseAll();
    $('text-dialog').hidden = false;
    $('text-input').value = '';
    setTimeout(() => $('text-input').focus(), 0);
  });
  $('text-cancel').addEventListener('click', () => { $('text-dialog').hidden = true; canvas.focus(); });
  $('text-form').addEventListener('submit', (e) => {
    e.preventDefault();
    const t = $('text-input').value;
    $('text-dialog').hidden = true;
    if (t) sendText(t);
    canvas.focus();
  });

  $('tb-end').addEventListener('click', () => {
    releaseAll();
    send({ type: 'end', reason: 'operator' });
    showEnded('Сеанс завершён', END_REASONS.operator);
  });

  // Интернет пропал/вернулся
  window.addEventListener('offline', () => {
    if (st.phase !== 'viewer') return;
    const b = $('banner');
    b.textContent = 'Нет интернета на этом компьютере. Ждём восстановления связи…';
    b.className = 'banner bad';
    b.hidden = false;
    setLive(false);
  });
  window.addEventListener('online', () => {
    $('banner').hidden = true;
    if (st.phase === 'viewer' && st.ws) setLive(true);
  });

  // Предупреждение при закрытии вкладки во время сеанса
  window.addEventListener('beforeunload', (e) => {
    if (st.phase === 'viewer' && !st.endShown) { e.preventDefault(); e.returnValue = ''; }
  });

  // ---------- журнал ----------
  const STATE_LABELS = { pending: ['Ждёт оператора', ''], 'awaiting-consent': ['Ждёт разрешения', 'pill-warn'], active: ['Идёт сеанс', 'pill-ok'] };
  const REASON_LABELS = {
    operator: ['Завершил оператор', 'pill-ok'],
    host: ['Завершил кассир', 'pill-ok'],
    timeout: ['Пропала связь', 'pill-warn'],
    error: ['Ошибка связи', 'pill-bad'],
    disconnect: ['Соединение оборвалось', 'pill-warn'],
    shutdown: ['Сервер перезапущен', 'pill-warn'],
    declined: ['Кассир отказал', 'pill-bad'],
    'consent-timeout': ['Кассир не ответил', 'pill-warn'],
    locked: ['Блокировка: неверный PIN', 'pill-bad'],
    expired: ['Код истёк', ''],
  };

  function fmtDate(s) {
    if (!s) return '—';
    const d = new Date(s);
    return isNaN(d) ? '—' : d.toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }
  function fmtDuration(sec) {
    sec = Math.max(0, Math.round(sec || 0));
    const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
    if (h) return `${h} ч ${m} мин`;
    if (m) return `${m} мин ${s} с`;
    return `${s} с`;
  }
  function cell(tr, text, cls) {
    const td = document.createElement('td');
    if (cls) {
      const span = document.createElement('span');
      span.className = 'pill ' + cls[1];
      span.textContent = cls[0];
      td.appendChild(span);
    } else {
      td.textContent = text == null || text === '' ? '—' : String(text);
    }
    tr.appendChild(td);
  }
  function emptyRow(tbody, cols, text) {
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = cols;
    td.className = 'empty';
    td.textContent = text;
    tr.appendChild(td);
    tbody.appendChild(tr);
  }

  async function openJournal() {
    show('journal');
    $('journal-error').textContent = '';
    try {
      const res = await api('/api/sessions?limit=200');
      if (!res.ok) throw new Error(String(res.status));
      const data = await res.json();
      const at = document.querySelector('#active-table tbody');
      at.innerHTML = '';
      if (!data.active.length) emptyRow(at, 7, 'Сейчас никого нет');
      for (const s of data.active) {
        const tr = document.createElement('tr');
        cell(tr, null, STATE_LABELS[s.state] || [s.state, '']);
        cell(tr, s.company); cell(tr, s.machine); cell(tr, s.version); cell(tr, s.hostIp);
        cell(tr, s.operatorName || s.operator);
        cell(tr, fmtDate(s.start || s.created));
        at.appendChild(tr);
      }
      const rt = document.querySelector('#recent-table tbody');
      rt.innerHTML = '';
      if (!data.recent.length) emptyRow(rt, 8, 'Журнал пока пуст');
      for (const s of data.recent) {
        const tr = document.createElement('tr');
        cell(tr, fmtDate(s.start || s.created));
        cell(tr, s.operatorName ? `${s.operatorName} (${s.operator})` : s.operator);
        cell(tr, s.company); cell(tr, s.machine); cell(tr, s.version); cell(tr, s.hostIp);
        cell(tr, s.start ? fmtDuration(s.durationSec) : '—');
        cell(tr, null, REASON_LABELS[s.reason] || [s.reason, '']);
        rt.appendChild(tr);
      }
    } catch (err) {
      if (err.message !== 'unauthorized') $('journal-error').textContent = 'Не удалось загрузить журнал.';
    }
  }
  $('journal-refresh').addEventListener('click', openJournal);

  // ---------- старт ----------
  st.auth = loadAuth();
  if (!st.auth) {
    logout('');
  } else {
    api('/api/me').then((r) => { if (r.ok) enterApp(); }).catch(() => {
      if (st.auth) { enterApp(); }
    });
  }
})();
