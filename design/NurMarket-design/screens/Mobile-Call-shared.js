/* NurMarket Mobile — звонок с Нур Советником. Общий скрипт макетов Mobile-Call-*.html (26.10.2026).
   Панель сверху — только для просмотра макета: тема, телефон (390×844 / 360×740), шрифт 1.0/1.3, длинные подписи (ky),
   показ ключей l10n, состояния экрана. Параметры адреса: ?theme=dark&size=360&big=1&ky=1&keys=1#состояние */
(function(){
  /* Значки: контурные (Material Icons Rounded по смыслу, здесь — упрощённые контуры 24×24) и заливочные (MDI). */
  const STROKE = {
    mic:'<rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5 11a7 7 0 0 0 14 0M12 18v3M8 21h8"/>',
    'mic-off':'<rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5 11a7 7 0 0 0 14 0M12 18v3M8 21h8M4 4l16 16"/>',
    speaker:'<path d="M4 10v4h3l4 4V6L7 10H4z"/><path d="M15 9a4 4 0 0 1 0 6M17.5 6.5a8 8 0 0 1 0 11"/>',
    earpiece:'<path d="M4 10v4h3l4 4V6L7 10H4z"/>',
    camera:'<path d="M4 8h3l2-3h6l2 3h3v11H4z"/><circle cx="12" cy="13" r="3.5"/>',
    'camera-flip':'<path d="M4 8h3l2-3h6l2 3h3v11H4z"/><path d="M9.3 13.8a3 3 0 0 1 4.6-2.6M14.7 12.2a3 3 0 0 1-4.6 2.6"/><path d="M13.6 9.6l.6 1.9-1.9.5M10.4 16.4l-.6-1.9 1.9-.5"/>',
    torch:'<path d="M8 2h8v4l-2 3v13h-4V9L8 6z"/><path d="M12 12v3"/>',
    'chevron-down':'<path d="M6 9l6 6 6-6"/>', 'chevron-up':'<path d="M6 15l6-6 6 6"/>', 'chevron-left':'<path d="M15 6l-6 6 6 6"/>', 'chevron-right':'<path d="M9 6l6 6-6 6"/>',
    close:'<path d="M6 6l12 12M18 6 6 18"/>', check:'<path d="M5 12.5l4.5 4.5L19 7.5"/>',
    undo:'<path d="M9 14l-4-4 4-4"/><path d="M5 10h9a5 5 0 0 1 0 10h-4"/>',
    lock:'<rect x="5" y="11" width="14" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/>',
    alert:'<path d="M12 3l10 18H2z"/><path d="M12 10v5M12 18.5v.01"/>',
    table:'<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 9h18M3 14h18M9 4v16"/>',
    chat:'<path d="M4 5h16v11H9l-5 4z"/>',
    pdf:'<path d="M6 2h8l5 5v15H6z"/><path d="M14 2v5h5M9 17v-5h2a1.5 1.5 0 0 1 0 3H9"/>',
    share:'<circle cx="18" cy="5" r="2.5"/><circle cx="6" cy="12" r="2.5"/><circle cx="18" cy="19" r="2.5"/><path d="M8.2 10.8l7.6-4.6M8.2 13.2l7.6 4.6"/>',
    pin:'<path d="M9 3h6l-1 6 3 3H7l3-3zM12 12v9"/>',
    'text-size':'<path d="M3 18L8 6l5 12M5 14h6"/><path d="M15 18l3-7 3 7M16.2 15.5h3.6"/>',
    cart:'<path d="M3 4h2l2.4 11h11.2L21 7H6"/><circle cx="9" cy="19" r="1.6"/><circle cx="17" cy="19" r="1.6"/>',
    account:'<circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/>',
    cash:'<rect x="3" y="6" width="18" height="12" rx="2"/><circle cx="12" cy="12" r="2.5"/><path d="M7 12h.01M17 12h.01"/>',
    calendar:'<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M3 10h18M8 3v4M16 3v4"/>',
    flag:'<path d="M5 21V4h11l-1.5 3 1.5 3H5"/>',
    tasks:'<rect x="5" y="4" width="14" height="17" rx="2"/><path d="M9 4V2h6v2M9 13l2 2 4-4"/>',
    send:'<path d="M3 11l18-8-8 18-2-8z"/>',
    plus:'<path d="M12 5v14M5 12h14"/>', minus:'<path d="M5 12h14"/>',
    image:'<rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="9" cy="10" r="2"/><path d="m21 17-5-5-9 8"/>',
    compass:'<circle cx="12" cy="12" r="9"/><path d="M15 9l-2 6-4 2 2-6z"/>',
    eye:'<path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>',
    bolt:'<path d="M13 2L4 14h7l-1 8 9-12h-7z"/>',
    shield:'<path d="M12 2l8 3v6c0 5-3.5 9-8 11-4.5-2-8-6-8-11V5z"/><path d="M12 16h.01M10 10a2 2 0 1 1 3 1.7c-.7.4-1 .8-1 1.3"/>',
    tune:'<path d="M4 7h10M18 7h2M4 17h4M12 17h8"/><circle cx="16" cy="7" r="2"/><circle cx="10" cy="17" r="2"/>',
    home:'<path d="M3 11l9-8 9 8v10H3z"/>',
    stop:'<rect x="6" y="6" width="12" height="12" rx="2"/>',
    sparkle:'<path d="M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8z"/><path d="M19 17l.7 1.8 1.8.7-1.8.7L19 22l-.7-1.8-1.8-.7 1.8-.7z"/>',
    cloud:'<path d="M7 18a4 4 0 0 1-.5-8A6 6 0 0 1 18 9a4.5 4.5 0 0 1 0 9z"/>',
    'card-off':'<rect x="3" y="6" width="18" height="12" rx="2"/><path d="M3 10h18M4 4l16 16"/>',
    face:'<circle cx="12" cy="12" r="9"/><path d="M9 10h.01M15 10h.01M8.5 15a5 5 0 0 0 7 0"/>',
    expand:'<path d="M4 14v6h6M20 10V4h-6M4 20l6-6M20 4l-6 6"/>',
    menu:'<path d="M4 7h16M4 12h16M4 17h16"/>',
    scan:'<path d="M4 8V5a1 1 0 0 1 1-1h3M16 4h3a1 1 0 0 1 1 1v3M20 16v3a1 1 0 0 1-1 1h-3M8 20H5a1 1 0 0 1-1-1v-3M7 12h10"/>',
    'arrow-up':'<path d="M12 19V5M6 11l6-6 6 6"/>', 'arrow-down':'<path d="M12 5v14M6 13l6 6 6-6"/>',
    filter:'<path d="M4 5h16l-6 7v6l-4 2v-8z"/>',
    trash:'<path d="M5 7h14M9 7V4h6v3M7 7l1 13h8l1-13M10 11v6M14 11v6"/>',
    'wifi-off':'<path d="M2 8.5a16 16 0 0 1 20 0M5 12a11 11 0 0 1 14 0M8.5 15.5a6 6 0 0 1 7 0M12 19h.01M3 3l18 18"/>',
    info:'<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8v.01"/>',
    clock:'<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3.5 2"/>',
    search:'<circle cx="10.5" cy="10.5" r="6.5"/><path d="M20 20l-4.5-4.5"/>',
    package:'<path d="M12 3l8 4.5v9L12 21l-8-4.5v-9z"/><path d="M4 7.5l8 4.5 8-4.5M12 12v9"/>',
    history:'<path d="M3 12a9 9 0 1 0 3-6.7M3 4v5h5"/><path d="M12 7v5l3 2"/>',
    more:'<circle cx="5" cy="12" r="1.6"/><circle cx="12" cy="12" r="1.6"/><circle cx="19" cy="12" r="1.6"/>',
    hand:'<path d="M8 13V5.5a1.5 1.5 0 0 1 3 0V11M11 11V4.5a1.5 1.5 0 0 1 3 0V11M14 11V6a1.5 1.5 0 0 1 3 0v8a6 6 0 0 1-12 0v-3.5a1.5 1.5 0 0 1 3 0V13"/>',
    battery:'<rect x="3" y="7" width="16" height="10" rx="2"/><path d="M21 10v4M7 11v2"/>'
  };
  const FILL = {
    'phone-hangup':'M12,9C10.4,9 8.85,9.25 7.4,9.72V12.82C7.4,13.22 7.17,13.56 6.84,13.72C5.86,14.21 4.97,14.84 4.17,15.57C4,15.75 3.75,15.86 3.5,15.86C3.2,15.86 2.95,15.74 2.77,15.56L0.29,13.08C0.11,12.9 0,12.65 0,12.38C0,12.1 0.11,11.85 0.29,11.67C3.34,8.77 7.46,7 12,7C16.54,7 20.66,8.77 23.71,11.67C23.89,11.85 24,12.1 24,12.38C24,12.65 23.89,12.9 23.71,13.08L21.23,15.56C21.05,15.74 20.8,15.86 20.5,15.86C20.25,15.86 20,15.75 19.82,15.57C19.03,14.84 18.14,14.21 17.16,13.72C16.83,13.56 16.6,13.22 16.6,12.82V9.72C15.15,9.25 13.6,9 12,9Z',
    phone:'M6.62,10.79C8.06,13.62 10.38,15.94 13.21,17.38L15.41,15.18C15.69,14.9 16.08,14.82 16.43,14.93C17.55,15.3 18.75,15.5 20,15.5A1,1 0 0,1 21,16.5V20A1,1 0 0,1 20,21A17,17 0 0,1 3,4A1,1 0 0,1 4,3H7.5A1,1 0 0,1 8.5,4C8.5,5.25 8.7,6.45 9.07,7.57C9.18,7.92 9.1,8.31 8.82,8.59L6.62,10.79Z',
    'check-circle':'M12 2C6.5 2 2 6.5 2 12S6.5 22 12 22 22 17.5 22 12 17.5 2 12 2M12 20C7.59 20 4 16.41 4 12S7.59 4 12 4 20 7.59 20 12 16.41 20 12 20M16.59 7.58L10 14.17L7.41 11.59L6 13L10 17L18 9L16.59 7.58Z',
    'mdi-eq':'M3,9H7V15H3V9M9,5H13V19H9V5M15,11H19V13H15V11Z'
  };
  function icon(name, size){ size = size || 20;
    if (FILL[name]) return '<svg class="ic" width="'+size+'" height="'+size+'" viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="'+FILL[name]+'"/></svg>';
    const d = STROKE[name] || STROKE.info;
    return '<svg class="ic" width="'+size+'" height="'+size+'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">'+d+'</svg>';
  }

  /* Кыргызский — только чтобы проверить длинные подписи (на 30–40 % длиннее). Переводы для кода делает Sonnet
     (существующие ключи call* — из tool/gen_arb.py). */
  const KY = {
    'Разговор':'Сүйлөшүү','Нур Советник':'Нур Кеңешчи','Вы':'Сиз','Подключаюсь…':'Туташып жатам…','Готовлю данные магазина…':'Дүкөндүн маалыматын даярдап жатам…',
    'Слушаю…':'Угуп жатам…','Думаю…':'Ойлонуп жатам…','Ищу…':'Издеп жатам…','Говорю… (нажмите, чтобы перебить)':'Сүйлөп жатам… (бөлүү үчүн басыңыз)','Говорю…':'Сүйлөп жатам…',
    'Звонок завершён':'Чалуу аяктады','Завершить':'Бүтүрүү','Закрыть':'Жабуу','Микрофон выкл.':'Микрофон өчүк','Микрофон вкл.':'Микрофон күйүк','Громкая связь':'Катуу байланыш','Без громкой связи':'Катуу байланышсыз',
    'Через минуту звонок завершится':'Бир мүнөттөн кийин чалуу аяктайт','Ещё 10 минут':'Дагы 10 мүнөт','Повторить':'Кайталоо','Открыть настройки':'Жөндөөлөрдү ачуу',
    'Свернуть':'Жыйыштыруу','Замолчи':'Токто','ИИ слушает':'ИИ угуп жатат','Перебили — слушаю':'Бөлдүңүз — угуп жатам','Отменить':'Жокко чыгаруу','отменить нельзя':'жокко чыгарууга болбойт',
    'Нет связи с ИИ, работайте на экране':'ИИ менен байланыш жок, экранда иштеңиз','Сервер отвечает медленно…':'Сервер жай жооп берүүдө…','Данные ещё грузятся — скажу, когда придут':'Маалымат жүктөлүп жатат — келгенде айтам',
    'Камера':'Камера','Камера вкл.':'Камера күйүк','Камера выкл.':'Камера өчүк','Другая камера':'Башка камера','Фонарик':'Чырак','ИИ видит':'ИИ көрөт','Пауза — ИИ не видит':'Тыныгуу — ИИ көрбөйт',
    'Экономия трафика':'Трафикти үнөмдөө','Что это?':'Бул эмне?','Накладная':'Накладной','Полка':'Текче','Ценник':'Баа белгиси','Снимаю чётко — держите ровно…':'Так тартып жатам — түз кармаңыз…','Ищу в каталоге…':'Каталогдон издеп жатам…',
    'Включить камеру в разговоре?':'Сүйлөшүүдө камераны күйгүзөсүзбү?','Включить камеру':'Камераны күйгүзүү','Не сейчас':'Азыр эмес','Открыть приход':'Кирешени ачуу','Звонок завершится, разговор сохранится в чате':'Чалуу аяктайт, сүйлөшүү чатта сакталат',
    'Остаток':'Калдык','Цена':'Баа','точно':'так','Похоже на:':'Окшош:','В каталоге не нашёл. Поднесите ближе или покажите штрихкод':'Каталогдон табылган жок. Жакыныраак алып келиңиз же штрих-кодду көрсөтүңүз',
    'ИИ считает на глаз — перепроверьте перед ревизией':'ИИ көз менен эсептейт — ревизиядан мурун кайра текшериңиз','На ценнике':'Баа белгисинде','В программе':'Программада','не совпадает':'дал келбейт','совпадает':'дал келет',
    'Крупнее':'Чоңураак','В чат':'Чатка','PDF':'PDF','Поделиться':'Бөлүшүү','Закрепить':'Бекитүү','Закреплено':'Бекитилди','Показать ещё 20':'Дагы 20 көрсөтүү','Итого':'Жалпы','Пока пусто':'Азырынча бош','Снять фильтр':'Чыпканы алып салуу',
    'Скажите номер или нажмите':'Номерин айтыңыз же басыңыз','Не то':'Ал эмес','Сделать':'Жасоо','Не надо':'Кереги жок','или скажите «да»':'же «ооба» деп айтыңыз','Да, 120':'Ооба, 120','Нет, скажу снова':'Жок, кайра айтам',
    'Списать те же товары':'Ошол эле товарларды эсептен чыгаруу','Оставить':'Калтыруу',
    'Действия ИИ':'ИИ аракеттери','Все':'Баары','Только деньги':'Акча гана','Отменённые':'Жокко чыгарылгандар','Сегодня':'Бүгүн','Вчера':'Кечээ','отменено':'жокко чыгарылды',
    'Помощник':'Жардамчы','Выполнять по просьбе':'Өтүнүч боюнча аткаруу','Только смотреть':'Көрүү гана','Спрашивать перед деньгами':'Акчадан мурун суроо','по умолчанию':'демейки',
    'Голосом режим не меняется — только здесь':'Режим үн менен өзгөрбөйт — ушул жерде гана','Владелец задал для всех на этом телефоне:':'Ээси бул телефондогу баарына койгон:','Для себя можно выбрать строже':'Өзүңүз үчүн катуураагын тандасаңыз болот',
    'Задачи':'Тапшырмалар','Мои задачи':'Менин тапшырмаларым','Новая задача':'Жаңы тапшырма','новая':'жаңы','в работе':'аткарылууда','сделано':'аткарылды','просрочена':'мөөнөтү өттү','до':'чейин',
    'Задач на сегодня':'Бүгүнкү тапшырмалар','просрочено':'мөөнөтү өткөн','Зарплата':'Эмгек акы','Начислено':'Эсептелди','Выплачено':'Төлөндү','Осталось':'Калды','Выплатить':'Төлөп берүү','Сотрудник':'Кызматкер','Схема':'Схема',
    'проверено частично — сумма к выплате сама не подставляется':'жарым-жартылай текшерилди — төлөм суммасы автоматтык коюлбайт','Октябрь':'Октябрь','Сентябрь':'Сентябрь',
    'Главная':'Башкы','Продажа':'Сатуу','Сканер':'Сканер','Склад':'Кампа','Ещё':'Дагы','Оплатить':'Төлөө','Мало':'Аз','Все товары':'Бардык товарлар','Поиск товара':'Товар издөө','Товар':'Товар','Приёмка':'Кабыл алуу','Провести':'Өткөрүү',
    'Профиль':'Профиль','Хранится на телефоне, до 2000 записей. Без ключей ИИ, звука и телефонов клиентов.':'Телефондо сакталат, 2000 жазууга чейин. ИИ ачкычтары, үн жана кардар телефондору жок.',
    'Видны только ваши действия':'Сиздин аракеттериңиз гана көрүнөт','Звонок длится до 10 мин и расходует лимит ключа ИИ быстрее, чем чат.':'Чалуу 10 мүнөткө чейин созулат жана ИИ ачкычынын лимитин чатка караганда тезирээк коротот.',
    'Видеозвонок расходует лимит ключа ИИ в несколько раз быстрее голосового':'Видео чалуу ИИ ачкычынын лимитин үн чалуудан бир нече эсе тезирээк коротот'
  };

  const Q = (s, r) => (r || document).querySelector(s);
  const QA = (s, r) => Array.from((r || document).querySelectorAll(s));
  const fmt = n => String(n).replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
  const som = n => fmt(n) + ' сом';

  function applyKy(on){
    QA('[data-ky], [data-key]').forEach(el => {
      if (el.children.length) return;
      const ru = el.dataset.ru != null ? el.dataset.ru : el.textContent.trim();
      if (el.dataset.ru == null) el.dataset.ru = ru;
      const m = ru.match(/^(.*?)(\s*[(·:]\s*[\d\s%]*\)?)?$/); const base = m[1], tail = m[2] || '';
      const ky = KY[base] || KY[ru]; if (!ky) return;
      el.textContent = on ? (KY[ru] ? KY[ru] : ky + tail) : ru;
    });
  }

  /* Панель макета. opts: {title, states:[{id,label}], onState(id), sizes:['390','360','land']} */
  function toolbar(frame, opts){
    const bar = document.createElement('div'); bar.className = 'tb';
    const states = opts.states || [];
    const sizes = opts.sizes || ['390','360'];
    const sizeLbl = { '390':'390×844', '360':'360×740', 'land':'844×390' };
    bar.innerHTML =
      '<a href="Mobile-Call-Index.html">← Макеты</a><b>'+opts.title+'</b>'+
      '<div class="grp"><span>Тема</span><button data-t="light" class="on">Светлая</button><button data-t="dark">Тёмная</button></div>'+
      '<div class="grp"><span>Телефон</span>'+sizes.map((s,i)=>'<button data-s="'+s+'"'+(i===0?' class="on"':'')+'>'+sizeLbl[s]+'</button>').join('')+'</div>'+
      '<div class="grp"><span>Шрифт</span><button data-k="1" class="on">1.0</button><button data-k="1.3">1.3</button></div>'+
      '<div class="grp"><button data-ky>ky — длинные подписи</button><button data-keys>ключи l10n</button></div>'+
      (states.length ? '<div class="grp"><span>Состояние</span>'+states.map((s,i)=>'<button data-st="'+s.id+'"'+(i===0?' class="on"':'')+'>'+s.label+'</button>').join('')+'</div>' : '');
    document.body.insertBefore(bar, document.body.firstChild);
    const sw = (sel, btn) => { QA(sel, bar).forEach(b => b.classList.toggle('on', b === btn)); };
    const setSize = s => { frame.classList.toggle('s360', s === '360'); frame.classList.toggle('land', s === 'land'); if (opts.onState && frame.dataset.state) opts.onState(frame.dataset.state); };
    bar.addEventListener('click', e => {
      const b = e.target.closest('button'); if (!b) return;
      if (b.dataset.t){ frame.dataset.theme = b.dataset.t; sw('[data-t]', b); }
      else if (b.dataset.s){ setSize(b.dataset.s); sw('[data-s]', b); }
      else if (b.dataset.k){ frame.classList.toggle('big', b.dataset.k === '1.3'); sw('[data-k]', b); }
      else if ('ky' in b.dataset){ b.classList.toggle('on'); frame.dataset.ky = b.classList.contains('on') ? '1' : ''; applyKy(!!frame.dataset.ky); }
      else if ('keys' in b.dataset){ b.classList.toggle('on'); document.body.classList.toggle('show-keys', b.classList.contains('on')); }
      else if (b.dataset.st){ sw('[data-st]', b); setState(frame, b.dataset.st, opts.onState); }
    });
    const qs = new URLSearchParams(location.search);
    if (qs.get('theme') === 'dark'){ frame.dataset.theme = 'dark'; sw('[data-t]', Q('[data-t="dark"]', bar)); }
    if (qs.get('size') && sizes.includes(qs.get('size'))){ setSize(qs.get('size')); sw('[data-s]', Q('[data-s="'+qs.get('size')+'"]', bar)); }
    if (qs.get('big')){ frame.classList.add('big'); sw('[data-k]', Q('[data-k="1.3"]', bar)); }
    if (qs.get('ky')){ frame.dataset.ky = '1'; Q('[data-ky]', bar).classList.add('on'); }
    if (qs.get('keys')){ document.body.classList.add('show-keys'); Q('[data-keys]', bar).classList.add('on'); }
    const first = decodeURIComponent((location.hash || '').slice(1));
    const init = states.find(s => s.id === first) ? first : (states[0] && states[0].id);
    if (init){ QA('[data-st]', bar).forEach(x => x.classList.toggle('on', x.dataset.st === init)); setState(frame, init, opts.onState); }
    else if (opts.onState) opts.onState();
  }
  function setState(frame, id, cb){
    frame.dataset.state = id;
    if (cb) cb(id);
    if (frame.dataset.ky) applyKy(true);
  }

  /* ---------- куски телефона ---------- */
  function statusBar(o){ o = o || {};
    return '<div class="sb"><span class="num">'+(o.time || '14:05')+'</span><span class="r">'+(o.cam ? '<i class="cam-dot" title="камера/микрофон используются"></i>' : '')+
      '<svg width="16" height="12" viewBox="0 0 16 12" aria-hidden="true"><path fill="currentColor" d="M1 11h2V7H1zM5 11h2V4H5zM9 11h2V2H9zM13 11h2V0h-2z"/></svg>'+
      '<svg width="22" height="12" viewBox="0 0 22 12" aria-hidden="true"><rect x=".75" y=".75" width="18" height="10.5" rx="2" fill="none" stroke="currentColor" stroke-width="1.5"/><rect x="2.5" y="2.5" width="12" height="7" fill="currentColor"/><rect x="20" y="4" width="1.5" height="4" fill="currentColor"/></svg></span></div>';
  }
  const gesture = () => '<div class="gest"><i></i></div>';
  /* Место под мини-плашку сверху: раздел получает отступ 64 dp (SafeArea-padding оверлея), чтобы плашка не закрывала заголовок. */
  const pillSpace = () => '<div class="pill-space" style="flex:0 0 64px"></div>';
  function nav(active){
    const items = [['Главная','home'],['Продажа','cart'],['Сканер','scan'],['Склад','package'],['Ещё','menu']];
    return '<nav class="nav">'+items.map(([t,i]) => '<a class="'+(t===active?'on':'')+'" href="#"><span class="pill-i">'+icon(i,22)+'</span><span data-ky>'+t+'</span></a>').join('')+'</nav>';
  }
  function hdr(title, sub, right, back){
    return '<div class="hdr">'+(back ? '<button class="ib" aria-label="Назад">'+icon('chevron-left',24)+'</button>' : '')+
      '<div class="tt"><b data-ky>'+title+'</b>'+(sub ? '<span data-ky>'+sub+'</span>' : '')+'</div>'+(right || '')+'</div>';
  }
  const ph = (size) => '<span class="ph" style="width:'+size+'px;height:'+size+'px">'+icon('image',20)+'</span>';

  /* Мини-плашка звонка. o: {state:'listening|thinking|speaking|noconn|micoff', text, pos:'top|bottom', time, level} */
  function pill(o){ o = o || {};
    const st = o.state || 'listening';
    const text = o.text || { listening:'Слушаю…', thinking:'Ищу…', speaking:'Говорю…', noconn:'Нет связи с ИИ', micoff:'Микрофон выкл.' }[st];
    const lv = st === 'listening' || st === 'speaking' ? '<span class="lv live" aria-hidden="true"><i></i><i></i><i></i><i></i></span>' : (st === 'thinking' ? '<span class="dots" style="height:18px"><i></i><i></i><i></i></span>' : '');
    return '<div class="pill '+(o.pos || 'top')+' '+st+'" role="button" aria-label="Звонок с Нур Советником — развернуть; удержание — «Замолчи»">'+
      '<i class="dot '+st+'"></i><span class="st" data-ky data-key="'+({listening:'callListening',thinking:'callSearching',speaking:'callSpeakingShort',noconn:'callNoLink',micoff:'callMicOff'}[st])+'">'+text+'</span>'+lv+
      '<span class="tm num">'+(o.time || '03:41')+'</span><span class="sep"></span>'+
      '<button class="ib" aria-label="Микрофон">'+icon(st === 'micoff' ? 'mic-off' : 'mic', 22)+'</button>'+
      '<button class="ib end" aria-label="Завершить звонок">'+icon('phone-hangup', 22)+'</button></div>';
  }
  /* Строка итога действия ИИ. o: {text, sub, undo:true|false, kind:'ok|lock|bad|warn', btn} */
  function actLine(o){
    const ic = { ok:'check-circle', lock:'lock', bad:'alert', warn:'clock' }[o.kind || 'ok'];
    return '<div class="act '+(o.kind || 'ok')+'" style="'+(o.style || '')+'">'+icon(ic, 22)+'<div class="tx"><span data-key="'+(o.key || 'aiActDone')+'"><b>ИИ:</b> '+o.text+'</span>'+(o.sub ? '<small>'+o.sub+'</small>' : '')+'</div>'+
      (o.undo ? '<button class="btn sm" data-ky data-key="aiUndo">'+(o.btn || 'Отменить')+'</button>' : '')+(o.life === false ? '' : '<i class="life"></i>')+'</div>';
  }

  /* ---------- разделы под плашкой ---------- */
  const PRODUCTS = [
    ['Кока-Кола 1 л', 95, 12, 'шт'], ['Сок Gracio яблочный 1 л', 120, 2, 'шт'], ['Mars 50 г', 39, 48, 'шт'], ['Хлеб белый «Нарын»', 35, 0, 'шт'],
    ['Молоко «Эльвест» 2,5 % 1 л', 72, 3, 'шт'], ['Макароны «Макфа» спагетти 450 г', 85, 50, 'шт'], ['Масло «Шоро» подсолнечное 1 л', 165, 1, 'шт'], ['Сахар-песок 1 кг', 92, 20, 'кг']
  ];
  function section(kind, o){ o = o || {};
    if (kind === 'sale'){
      const lines = [['Кока-Кола 1 л', 2, 95], ['Mars 50 г', 1, 39], ['Сок Gracio яблочный 1 л', 1, 120]];
      const sum = lines.reduce((s, l) => s + l[1] * l[2], 0);
      return hdr('Продажа', 'Чек № 1043 · смена с 08:55') + '<div class="body"><div class="search">'+icon('search',20)+'<span class="sp" data-ky>Поиск товара</span>'+icon('scan',20)+'</div>'+
        '<div class="chips"><span class="chip on" data-ky>Все</span><span class="chip">Напитки</span><span class="chip">Сладкое</span><span class="chip">Хлеб</span></div>'+
        '<div class="list">'+lines.map(l => '<div class="item">'+ph(44)+'<div class="t"><b>'+l[0]+'</b><span class="num">'+l[1]+' × '+som(l[2])+'</span></div><div class="step"><button class="ib" aria-label="Меньше">'+icon('minus',18)+'</button><span class="q num">'+l[1]+'</span><button class="ib" aria-label="Больше">'+icon('plus',18)+'</button></div></div>').join('')+'</div>'+
        '<div class="total"><div class="sum"><span data-ky>Итого</span><b class="num">'+som(sum)+'</b></div><button class="btn cta" style="flex:0 0 58%"><span data-ky>Оплатить</span></button></div></div>' + nav('Продажа');
    }
    if (kind === 'stock'){
      const low = PRODUCTS.filter(p => p[2] <= 3);
      return hdr('Склад', 'NBS · 156 товаров') + '<div class="body"><div class="search">'+icon('search',20)+'<span class="sp" data-ky>Поиск товара</span>'+icon('scan',20)+'</div>'+
        '<div class="chips"><span class="chip" data-ky>Все товары</span><span class="chip on">'+icon('filter',16)+'<span data-ky>Мало</span> · '+low.length+'</span><span class="chip">Нет в наличии</span><span class="chip">Напитки</span></div>'+
        '<div class="list">'+low.map(p => '<div class="item">'+ph(44)+'<div class="t"><b>'+p[0]+'</b><span><span class="tag '+(p[2]===0?'bad':'warn')+'">'+(p[2]===0?'нет в наличии':'мало · '+p[2]+' '+p[3])+'</span></span></div><span class="pr num">'+som(p[1])+'</span></div>').join('')+'</div>'+
        '</div>' + nav('Склад');
    }
    if (kind === 'product'){
      return hdr('Товар', 'Напитки · 5449000000996', '<button class="ib" aria-label="Ещё">'+icon('more',22)+'</button>', true) + '<div class="body">'+
        '<div class="card"><div class="row">'+ph(72)+'<div class="sp" style="display:flex;flex-direction:column;gap:4px"><b style="font-size:calc(17px*var(--k));font-weight:800;line-height:1.2">Кока-Кола 1 л</b><span class="hint">Напитки · ПЭТ · поставщик «Кока-Кола Бишкек»</span></div></div>'+
        '<div class="kpis"><div class="kpi"><small data-ky>Цена</small><b class="num" style="color:var(--price)">95 сом</b></div><div class="kpi"><small>Закупка</small><b class="num">70 сом</b></div><div class="kpi"><small data-ky>Остаток</small><b class="num">12 шт</b></div></div></div>'+
        '<div class="card" style="gap:0;padding:0 14px"><div class="kv"><span>Наценка</span><b class="num">35,7 %</b></div><div class="kv"><span>Мин. остаток</span><b class="num">10 шт</b></div><div class="kv"><span>Продано за 30 дней</span><b class="num">184 шт</b></div><div class="kv" style="border:0"><span>Штрихкод</span><b class="num">5449000000996</b></div></div>'+
        '<div class="ctrl"><button class="btn"><span class="ell">Ценник</span></button><button class="btn"><span class="ell">Изменить</span></button><button class="btn cta dark" style="flex:1.4"><span class="ell">В чек</span></button></div>'+
        '</div>' + nav('Склад');
    }
    if (kind === 'receive'){
      const lines = [['Кока-Кола 1 л', 24, 70], ['Fanta 1 л', 24, 68], ['Sprite 1 л', 12, 68], ['BonAqua 0,5 л', 48, 22], ['Кока-Кола 0,5 л', 48, 45]];
      return hdr('Приёмка', 'Поставщик «Кока-Кола Бишкек» · № 4512', null, true) + '<div class="body">'+
        '<div class="search">'+icon('scan',20)+'<span class="sp">Сканируйте или введите штрихкод</span></div>'+
        '<div class="list">'+lines.map(l => '<div class="item"><div class="t"><b>'+l[0]+'</b><span class="num">'+l[1]+' шт × '+som(l[2])+' · наценка 35 %</span></div><span class="pr num">'+som(l[1]*l[2])+'</span></div>').join('')+'<div class="hint c">ещё 3 строки</div></div>'+
        '<div class="total"><div class="sum"><span>8 строк · 174 шт</span><b class="num">12 400 сом</b></div><button class="btn cta '+(o.posted ? 'dis' : '')+'" style="flex:0 0 50%"><span data-ky>'+(o.posted ? 'Проведено' : 'Провести')+'</span></button></div></div>' + nav('Склад');
    }
    if (kind === 'home'){
      return '<div class="hdr"><div class="tt"><b>Бекназар</b><span>NBS · владелец · смена с 08:55</span></div><button class="ib" aria-label="Профиль">'+icon('account',24)+'</button></div><div class="body">'+
        '<div class="kpis"><div class="kpi"><small>Выручка сегодня</small><b class="num">48 300</b></div><div class="kpi"><small>Чеков</small><b class="num">61</b></div><div class="kpi"><small>Долги</small><b class="num neg">−12 500</b></div></div>'+
        (o.extra || '')+
        '<div class="card" style="gap:6px"><b style="font-size:calc(14px*var(--k))">Требует внимания</b><div class="kv" style="padding:0"><span>Заканчиваются</span><b>4 товара</b></div><div class="kv" style="padding:0;border:0"><span>Просроченные долги</span><b class="neg">3 клиента</b></div></div>'+
        '</div>' + nav('Главная');
    }
    return '';
  }

  window.addEventListener('error', e => { const f = document.getElementById('frame');
    if (f) f.insertAdjacentHTML('afterbegin', '<pre style="color:#B91C1C;padding:20px;white-space:pre-wrap;font:13px monospace">Ошибка макета: ' + e.message + ' (' + (e.filename || '').split('/').pop() + ':' + e.lineno + ')</pre>'); });
  window.MC = { icon, toolbar, setState, applyKy, statusBar, gesture, pillSpace, nav, hdr, ph, pill, actLine, section, fmt, som, PRODUCTS, Q, QA };
})();
