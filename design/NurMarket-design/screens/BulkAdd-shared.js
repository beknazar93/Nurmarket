/* NurMarket Kassa — «Массовое добавление». Общий скрипт макетов BulkAdd-*.html (26.10.2026).
   Значки — пути из Services/MarketplaceIcons.cs (без изменений). Панель сверху — только для просмотра макета:
   тема, размер окна, длинные подписи (ky), показ ключей Tr.T, состояния экрана. */
(function(){
  const ICONS = {
    "barcode-scan":"M4,6H6V18H4V6M7,6H8V18H7V6M9,6H12V18H9V6M13,6H14V18H13V6M16,6H18V18H16V6M19,6H20V18H19V6M2,4V8H0V4A2,2 0 0,1 2,2H6V4H2M22,2A2,2 0 0,1 24,4V8H22V4H18V2H22M2,16V20H6V22H2A2,2 0 0,1 0,20V16H2M22,20V16H24V20A2,2 0 0,1 22,22H18V20H22Z",
    "barcode":"M2,6H4V18H2V6M5,6H6V18H5V6M7,6H10V18H7V6M11,6H12V18H11V6M14,6H16V18H14V6M17,6H20V18H17V6M21,6H22V18H21V6Z",
    "package-variant-closed":"M21,16.5C21,16.88 20.79,17.21 20.47,17.38L12.57,21.82C12.41,21.94 12.21,22 12,22C11.79,22 11.59,21.94 11.43,21.82L3.53,17.38C3.21,17.21 3,16.88 3,16.5V7.5C3,7.12 3.21,6.79 3.53,6.62L11.43,2.18C11.59,2.06 11.79,2 12,2C12.21,2 12.41,2.06 12.57,2.18L20.47,6.62C20.79,6.79 21,7.12 21,7.5V16.5M12,4.15L10.11,5.22L16,8.61L17.96,7.5L12,4.15M6.04,7.5L12,10.85L13.96,9.75L8.08,6.35L6.04,7.5M5,15.91L11,19.29V12.58L5,9.21V15.91M19,15.91V9.21L13,12.58V19.29L19,15.91Z",
    "database-arrow-right-outline":"M20 13.09V7C20 4.79 16.42 3 12 3S4 4.79 4 7V17C4 19.21 7.59 21 12 21C12.46 21 12.9 21 13.33 20.94C13.12 20.33 13 19.68 13 19L13 18.95C12.68 19 12.35 19 12 19C8.13 19 6 17.5 6 17V14.77C7.61 15.55 9.72 16 12 16C12.65 16 13.27 15.96 13.88 15.89C14.93 14.16 16.83 13 19 13C19.34 13 19.67 13.04 20 13.09M18 12.45C16.7 13.4 14.42 14 12 14S7.3 13.4 6 12.45V9.64C7.47 10.47 9.61 11 12 11S16.53 10.47 18 9.64V12.45M12 9C8.13 9 6 7.5 6 7S8.13 5 12 5 18 6.5 18 7 15.87 9 12 9M20 22V20H16V18H20V16L23 19L20 22Z",
    "magnify-close":"M9,2A7,7 0 0,1 16,9C16,10.5 15.5,12 14.61,13.19L15.41,14H16L22,20L20,22L14,16V15.41L13.19,14.61C12,15.5 10.5,16 9,16A7,7 0 0,1 2,9A7,7 0 0,1 9,2M11.12,5.46L9,7.59L6.88,5.46L5.46,6.88L7.59,9L5.46,11.12L6.88,12.54L9,10.41L11.12,12.54L12.54,11.12L10.41,9L12.54,6.88L11.12,5.46Z",
    "magnify":"M9.5,3A6.5,6.5 0 0,1 16,9.5C16,11.11 15.41,12.59 14.44,13.73L14.71,14H15.5L20.5,19L19,20.5L14,15.5V14.71L13.73,14.44C12.59,15.41 11.11,16 9.5,16A6.5,6.5 0 0,1 3,9.5A6.5,6.5 0 0,1 9.5,3M9.5,5C7,5 5,7 5,9.5C5,12 7,14 9.5,14C12,14 14,12 14,9.5C14,7 12,5 9.5,5Z",
    "file-excel-outline":"M14 2H6C4.89 2 4 2.9 4 4V20C4 21.11 4.89 22 6 22H18C19.11 22 20 21.11 20 20V8L14 2M18 20H6V4H13V9H18V20M12.9 14.5L15.8 19H14L12 15.6L10 19H8.2L11.1 14.5L8.2 10H10L12 13.4L14 10H15.8L12.9 14.5Z",
    "file-document-outline":"M6,2A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2H6M6,4H13V9H18V20H6V4M8,12V14H16V12H8M8,16V18H13V16H8Z",
    "file-export-outline":"M14 2H6C4.9 2 4 2.9 4 4V20C4 21.1 4.9 22 6 22H18C19.1 22 20 21.1 20 20V8L14 2M18 20H6V4H13V9H18V20M16 11V18.1L13.9 16L11.1 18.8L8.3 16L11.1 13.2L8.9 11H16Z",
    "file-check-outline":"M23.5 17L18.5 22L15 18.5L16.5 17L18.5 19L22 15.5L23.5 17M13.09 20H6V4H13V9H18V13.09C18.33 13.04 18.66 13 19 13S19.67 13.04 20 13.09V8L14 2H6C4.89 2 4 2.9 4 4V20C4 21.11 4.89 22 6 22H13.81C13.46 21.39 13.21 20.72 13.09 20Z",
    "check-circle-outline":"M12 2C6.5 2 2 6.5 2 12S6.5 22 12 22 22 17.5 22 12 17.5 2 12 2M12 20C7.59 20 4 16.41 4 12S7.59 4 12 4 20 7.59 20 12 16.41 20 12 20M16.59 7.58L10 14.17L7.41 11.59L6 13L10 17L18 9L16.59 7.58Z",
    "check":"M21,7L9,19L3.5,13.5L4.91,12.09L9,16.17L19.59,5.59L21,7Z",
    "information-outline":"M11,9H13V7H11M12,20C7.59,20 4,16.41 4,12C4,7.59 7.59,4 12,4C16.41,4 20,7.59 20,12C20,16.41 16.41,20 12,20M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M11,17H13V11H11V17Z",
    "history":"M13.5,8H12V13L16.28,15.54L17,14.33L13.5,12.25V8M13,3A9,9 0 0,0 4,12H1L4.96,16.03L9,12H6A7,7 0 0,1 13,5A7,7 0 0,1 20,12A7,7 0 0,1 13,19C11.07,19 9.32,18.21 8.06,16.94L6.64,18.36C8.27,20 10.5,21 13,21A9,9 0 0,0 22,12A9,9 0 0,0 13,3",
    "close":"M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z",
    "wifi-off":"M2.28,3L1,4.27L2.47,5.74C2.04,6 1.61,6.29 1.2,6.6L3,9C3.53,8.6 4.08,8.25 4.66,7.93L6.89,10.16C6.15,10.5 5.44,10.91 4.8,11.4L6.6,13.8C7.38,13.22 8.26,12.77 9.2,12.47L11.75,15C10.5,15.07 9.34,15.5 8.4,16.2L12,21L14.46,17.73L17.74,21L19,19.72M12,3C9.85,3 7.8,3.38 5.9,4.07L8.29,6.47C9.5,6.16 10.72,6 12,6C15.38,6 18.5,7.11 21,9L22.8,6.6C19.79,4.34 16.06,3 12,3M12,9C11.62,9 11.25,9 10.88,9.05L14.07,12.25C15.29,12.53 16.43,13.07 17.4,13.8L19.2,11.4C17.2,9.89 14.7,9 12,9Z",
    "chevron-left":"M15.41,16.58L10.83,12L15.41,7.41L14,6L8,12L14,18L15.41,16.58Z",
    "chevron-right":"M8.59,16.58L13.17,12L8.59,7.41L10,6L16,12L10,18L8.59,16.58Z",
    "chevron-down":"M7.41,8.58L12,13.17L16.59,8.58L18,10L12,16L6,10L7.41,8.58Z", /* нет в MarketplaceIcons — это chevron-right, повёрнутый; Sonnet: RenderTransform rotate(90) */
    "percent":"M18.5,3.5L3.5,18.5L5.5,20.5L20.5,5.5M7,4A3,3 0 0,0 4,7A3,3 0 0,0 7,10A3,3 0 0,0 10,7A3,3 0 0,0 7,4M17,14A3,3 0 0,0 14,17A3,3 0 0,0 17,20A3,3 0 0,0 20,17A3,3 0 0,0 17,14Z",
    "clock-outline":"M12,20A8,8 0 0,0 20,12A8,8 0 0,0 12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20M12,2A10,10 0 0,1 22,12A10,10 0 0,1 12,22C6.47,22 2,17.5 2,12A10,10 0 0,1 12,2M12.5,7V12.25L17,14.92L16.25,16.15L11,13V7H12.5Z",
    "server-outline":"M2 4.6V9.4C2 10.3 2.5 11 3.2 11H20.9C21.5 11 22.1 10.3 22.1 9.4V4.6C22 3.7 21.5 3 20.8 3H3.2C2.5 3 2 3.7 2 4.6M10 8V6H9V8H10M5 8H7V6H5V8M20 9H4V5H20V9M2 14.6V19.4C2 20.3 2.5 21 3.2 21H20.9C21.5 21 22.1 20.3 22.1 19.4V14.6C22.1 13.7 21.6 13 20.9 13H3.2C2.5 13 2 13.7 2 14.6M10 18V16H9V18H10M5 18H7V16H5V18M20 19H4V15H20V19Z",
    "checkbox-multiple-marked-outline":"M20,16V10H22V16A2,2 0 0,1 20,18H8C6.89,18 6,17.1 6,16V4C6,2.89 6.89,2 8,2H16V4H8V16H20M10.91,7.08L14,10.17L20.59,3.58L22,5L14,13L9.5,8.5L10.91,7.08M16,20V22H4A2,2 0 0,1 2,20V7H4V20H16Z",
    "arrow-expand":"M10,21V19H6.41L10.91,14.5L9.5,13.09L5,17.59V14H3V21H10M14.5,10.91L19,6.41V10H21V3H14V5H17.59L13.09,9.5L14.5,10.91Z"
  };
  function icon(name, size){ size = size || 18; const d = ICONS[name] || ICONS["information-outline"];
    return '<svg class="ic" width="'+size+'" height="'+size+'" viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="'+d+'"/></svg>'; }

  /* Кыргызский — только чтобы проверить длинные подписи (на 30–40 % длиннее). Переводы для кода делает Sonnet. */
  const KY = {
    "Массовое сканирование":"Массалык сканерлөө", "Импорт из Excel":"Excel'ден импорттоо", "Импорт из CSV":"CSV'ден импорттоо",
    "Сканер":"Сканер", "Провести приход":"Товарды кабыл алуу", "Провожу…":"Өткөрүп жатам…", "Выбрать из списка":"Тизмеден тандоо", "Очистить":"Тазалоо",
    "Добавить":"Кошуу", "Наведите сканер или введите штрихкод":"Сканерди багыттаңыз же штрих-кодду киргизиңиз",
    "Сканируйте штрихкоды. Повторный скан увеличивает количество.":"Штрих-коддорду сканерлеңиз. Кайталап сканерлөө санын көбөйтөт.",
    "Есть на складе":"Кампада бар", "Есть в базе CRM":"CRM базасында бар", "Неизвестный штрихкод":"Белгисиз штрих-код", "Пока пусто":"Азырынча бош",
    "Наценка для всех":"Баарына үстөк баа", "Применить":"Колдонуу", "Повторить ошибочные":"Каталууларды кайталоо", "Проверить снова":"Кайра текшерүү",
    "проверяю в базе…":"базадан текшерип жатам…", "не проверено (нет связи)":"текшерилген жок (байланыш жок)", "из базы CRM":"CRM базасынан",
    "Название":"Аталышы", "Единица":"Бирдиги", "Категория":"Категориясы", "Закупка":"Сатып алуу", "Продажа":"Сатуу", "Наценка %":"Үстөк баа %",
    "Продолжить":"Улантуу", "Удалить":"Өчүрүү", "Отмена":"Жокко чыгаруу", "Приходы в фоне":"Фондогу кирешелер",
    "Приход идёт в фоне — можно сканировать дальше":"Кабыл алуу фондо жүрүүдө — сканерлөөнү уланта берсеңиз болот",
    "Файл":"Файл", "Столбцы":"Мамычалар", "Проверка":"Текшерүү", "Выбрать файл…":"Файл тандоо…", "Скачать шаблон":"Үлгүнү жүктөп алуу", "Вставить из буфера":"Буферден коюу",
    "Далее":"Андан ары", "Назад":"Артка", "Наше поле":"Биздин талаа", "Столбец файла":"Файлдын мамычасы", "Пример (строка 1)":"Мисал (1-сап)",
    "Скачать строки с ошибками":"Каталуу саптарды жүктөп алуу", "Все":"Баары", "Приход к товарам склада":"Кампадагы товарларга кабыл алуу", "Новые":"Жаңылар", "Ошибки":"Каталар",
    "Что будет":"Эмне болот", "Ошибка":"Ката", "Штрихкод":"Штрих-код", "Количество":"Саны", "Новый импорт":"Жаңы импорт", "можно закрыть окно":"терезени жапсаңыз болот",
    "Сложить количество":"Санын кошуу", "Брать первую строку":"Биринчи сапты алуу", "Наценка для строк без цены продажи":"Сатуу баасы жок саптар үчүн үстөк баа",
    "Дубли штрихкода в файле":"Файлдагы кайталанган штрих-коддор", "Старая загрузка (замена остатка)":"Эски жүктөө (калдыкты алмаштыруу)",
    "Изменить и загрузить":"Өзгөртүп жүктөө", "Оставить как есть":"Ошол бойдон калтыруу", "Да, Windows-1251":"Ооба, Windows-1251", "Перейти":"Өтүү", "Сбросить":"Баштапкы абалга"
  };

  const Q = (s, r) => (r || document).querySelector(s);
  const QA = (s, r) => Array.from((r || document).querySelectorAll(s));

  function applyKy(on){
    QA('[data-ky], .seg-btn .long, .seg-btn .short, [data-key]').forEach(el => {
      if (el.children.length && !el.classList.contains('kyok')) return;
      const ru = el.dataset.ru != null ? el.dataset.ru : el.textContent.trim();
      if (el.dataset.ru == null) el.dataset.ru = ru;
      const m = ru.match(/^(.*?)(\s*\(\d[\d\s]*\))?$/); const base = m[1], tail = m[2] || '';
      const ky = KY[base]; if (!ky) return;
      el.textContent = on ? ky + tail : ru;
    });
    QA('input[placeholder]').forEach(i => { const ru = i.dataset.ru || i.placeholder; i.dataset.ru = ru; if (KY[ru]) i.placeholder = on ? KY[ru] : ru; });
  }

  /* Панель макета. opts: {title, states:[{id,label}], onState(id), back} */
  function toolbar(frame, opts){
    const bar = document.createElement('div'); bar.className = 'tb';
    const states = opts.states || [];
    bar.innerHTML =
      '<a href="BulkAdd-Index.html">← Макеты</a><b>'+opts.title+'</b>'+
      '<div class="grp"><span>Тема</span><button data-t="light" class="on">Светлая</button><button data-t="dark">Тёмная</button></div>'+
      '<div class="grp"><span>Окно</span><button data-s="1296" class="on">1296×879</button><button data-s="1024">1024×600</button></div>'+
      '<div class="grp"><button data-ky>ky — длинные подписи</button><button data-keys>ключи Tr.T</button></div>'+
      (states.length ? '<div class="grp"><span>Состояние</span>'+states.map((s,i)=>'<button data-st="'+s.id+'"'+(i===0?' class="on"':'')+'>'+s.label+'</button>').join('')+'</div>' : '');
    document.body.insertBefore(bar, document.body.firstChild);
    const sw = (sel, btn) => { QA(sel, bar).forEach(b => b.classList.toggle('on', b === btn)); };
    bar.addEventListener('click', e => {
      const b = e.target.closest('button'); if (!b) return;
      if (b.dataset.t){ frame.dataset.theme = b.dataset.t; sw('[data-t]', b); }
      else if (b.dataset.s){ frame.classList.toggle('s1024', b.dataset.s === '1024'); sw('[data-s]', b); }
      else if ('ky' in b.dataset){ b.classList.toggle('on'); frame.dataset.ky = b.classList.contains('on') ? '1' : ''; applyKy(!!frame.dataset.ky); }
      else if ('keys' in b.dataset){ b.classList.toggle('on'); document.body.classList.toggle('show-keys', b.classList.contains('on')); }
      else if (b.dataset.st){ sw('[data-st]', b); setState(frame, b.dataset.st, opts.onState); }
    });
    const qs = new URLSearchParams(location.search);   /* ?theme=dark&size=1024&ky=1 — для снимков */
    if (qs.get('theme') === 'dark'){ frame.dataset.theme = 'dark'; sw('[data-t]', Q('[data-t="dark"]', bar)); }
    if (qs.get('size') === '1024'){ frame.classList.add('s1024'); sw('[data-s]', Q('[data-s="1024"]', bar)); }
    if (qs.get('ky')){ frame.dataset.ky = '1'; Q('[data-ky]', bar).classList.add('on'); }
    if (qs.get('keys')){ document.body.classList.add('show-keys'); Q('[data-keys]', bar).classList.add('on'); }
    const first = (location.hash || '').slice(1);
    const init = states.find(s => s.id === first) ? first : (states[0] && states[0].id);
    if (init){ QA('[data-st]', bar).forEach(x => x.classList.toggle('on', x.dataset.st === init)); setState(frame, init, opts.onState); }
  }
  function setState(frame, id, cb){
    frame.dataset.state = id;
    if (cb) cb(id);
    QA('[data-states]', frame).forEach(el => { el.style.display = el.dataset.states.split(/\s+/).includes(id) ? '' : 'none'; });
    if (frame.dataset.ky) applyKy(true);
  }

  /* Шапка окна «Склад» — вкладки TabControl (контекст, не часть макета вкладки). */
  function chrome(){
    const tabs = ['Товары','Обзор','Приёмка','Массовое добавление','Ревизия','Списание','Перемещение','Журнал'];
    return '<div class="chrome"><span class="wt">Склад</span>'+tabs.map(t=>'<span class="t'+(t==='Массовое добавление'?' on':'')+'">'+t+'</span>').join('')+
      '<span class="sp"></span><span class="x">'+icon('close',18)+'</span></div>';
  }
  /* Сегмент режимов: sec-segment / sec-seg. mode: scan | excel | csv. На 1024 — короткие подписи. */
  function segment(mode){
    const b = (id, ic, long, short, key) => '<button class="seg-btn'+(mode===id?' on':'')+'" data-key="'+key+'">'+icon(ic,18)+'<span class="long kyok">'+long+'</span><span class="short kyok">'+short+'</span></button>';
    return '<div class="seg" role="radiogroup">'+
      b('scan','barcode-scan','Массовое сканирование','Сканер','bulk.modeScan')+
      b('excel','file-excel-outline','Импорт из Excel','Excel','bulk.modeExcel')+
      b('csv','file-document-outline','Импорт из CSV','CSV','bulk.modeCsv')+'</div>';
  }
  /* Шаги мастера. step 1..4 (4 = загрузка/итог), fname — имя файла справа. */
  function steps(step, fname){
    const s = (n, label, key) => '<span class="step'+(n===step?' on':n<step?' done':'')+'"><i>'+(n<step?icon('check',14):n)+'</i><span data-key="'+key+'">'+label+'</span></span>';
    return '<div class="steps">'+s(1,'Файл','imp.step1')+s(2,'Столбцы','imp.step2')+s(3,'Проверка','imp.step3')+
      (fname ? '<span class="fname">'+icon('file-excel-outline',16)+fname+'</span>' : '')+'</div>';
  }
  function fmt(n){ return String(n).replace(/\B(?=(\d{3})+(?!\d))/g, ' '); }

  window.addEventListener('error', e => { const f = document.getElementById('frame');
    if (f) f.insertAdjacentHTML('afterbegin', '<pre style="color:#B91C1C;padding:20px;white-space:pre-wrap;font:13px monospace">Ошибка макета: ' + e.message + ' (' + (e.filename || '').split('/').pop() + ':' + e.lineno + ':' + e.colno + ')</pre>'); });
  window.BA = { icon, toolbar, setState, chrome, segment, steps, fmt, applyKy, Q, QA };
})();
