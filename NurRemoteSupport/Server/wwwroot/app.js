// Консоль оператора удалённой поддержки NurMarket (2026-09-29).
// Без сборщиков и библиотек. Протокол — NurRemoteSupport/PROTOCOL.md.
// 2026-10-27 (W1): 5 языков (ru, ky, en, tr, uz), светлая/тёмная тема, билет WebSocket вместо токена в адресе, выход и смена пароля,
// «Только просмотр» от кассира (started.viewOnly), экран защищён Windows (screen-paused), касание: тап, долгое нажатие, щипок.
'use strict';

(() => {
  const $ = (id) => document.getElementById(id);
  const AUTH_KEY = 'nrs-auth';

  // ---------- переводы: [ru, ky, en, tr, uz] ----------
  const LANGS = ['ru', 'ky', 'en', 'tr', 'uz'];
  const LANG_NAMES = { ru: 'Русский', ky: 'Кыргызча', en: 'English', tr: 'Türkçe', uz: "O'zbekcha" };
  const LANG_LOCALE = { ru: 'ru-RU', ky: 'ky-KG', en: 'en-GB', tr: 'tr-TR', uz: 'uz-UZ' };
  const T = {
    pageTitle: ['NurCRM · Поддержка', 'NurCRM · Колдоо', 'NurCRM · Support', 'NurCRM · Destek', 'NurCRM · Yordam'],
    brandSupport: ['Поддержка', 'Колдоо', 'Support', 'Destek', 'Yordam'],
    navConnect: ['Подключение', 'Туташуу', 'Connect', 'Bağlantı', 'Ulanish'],
    navJournal: ['Журнал', 'Журнал', 'Log', 'Günlük', 'Jurnal'],
    logout: ['Выйти', 'Чыгуу', 'Sign out', 'Çıkış', 'Chiqish'],
    changePw: ['Сменить пароль', 'Сырсөздү алмаштыруу', 'Change password', 'Şifreyi değiştir', 'Parolni almashtirish'],
    themeTitle: ['Тема: светлая / тёмная', 'Тема: ачык / караңгы', 'Theme: light / dark', 'Tema: açık / koyu', "Mavzu: yorug' / qorong'i"],
    langLabel: ['Язык', 'Тил', 'Language', 'Dil', 'Til'],
    loginTitle: ['Поддержка NurCRM', 'NurCRM колдоосу', 'NurCRM Support', 'NurCRM Destek', 'NurCRM yordami'],
    loginSub: ['Вход для сотрудников', 'Кызматкерлер үчүн кирүү', 'Staff sign-in', 'Personel girişi', 'Xodimlar uchun kirish'],
    loginLabel: ['Логин', 'Логин', 'Login', 'Kullanıcı adı', 'Login'],
    passwordLabel: ['Пароль', 'Сырсөз', 'Password', 'Şifre', 'Parol'],
    signIn: ['Войти', 'Кирүү', 'Sign in', 'Giriş', 'Kirish'],
    connTitle: ['Подключение к кассе', 'Кассага туташуу', 'Connect to a till', 'Kasaya bağlan', 'Kassaga ulanish'],
    connIntro: ['Попросите кассира открыть в кассе «Тех. поддержка» и продиктовать код и PIN.',
      'Кассирден кассада «Тех. колдоо» бөлүмүн ачып, код менен PIN\'ди айтып берүүсүн сураңыз.',
      'Ask the cashier to open “Support” in the till and read out the code and PIN.',
      'Kasiyerden kasada «Destek» bölümünü açıp kodu ve PIN\'i okumasını isteyin.',
      'Kassirdan kassada «Texnik yordam»ni ochib, kod va PIN\'ni aytishini so\'rang.'],
    codeLabel: ['Код сеанса', 'Сеанстын коду', 'Session code', 'Oturum kodu', 'Seans kodi'],
    connect: ['Подключиться', 'Туташуу', 'Connect', 'Bağlan', 'Ulanish'],
    connHint: ['Кассир увидит ваше имя и должен нажать «Разрешить». Код и PIN одноразовые и действуют 15 минут.',
      'Кассир сиздин атыңызды көрөт жана «Уруксат берүү» баскычын басышы керек. Код менен PIN бир жолу колдонулат, 15 мүнөт жарайт.',
      'The cashier will see your name and must press “Allow”. The code and PIN are single-use and valid for 15 minutes.',
      'Kasiyer adınızı görür ve «İzin ver»e basmalıdır. Kod ve PIN tek kullanımlıktır, 15 dakika geçerlidir.',
      'Kassir ismingizni ko\'radi va «Ruxsat berish»ni bosishi kerak. Kod va PIN bir martalik, 15 daqiqa amal qiladi.'],
    waitTitle: ['Ожидаем разрешения кассира…', 'Кассирдин уруксатын күтүп жатабыз…', 'Waiting for the cashier\'s permission…', 'Kasiyerin izni bekleniyor…', 'Kassir ruxsatini kutyapmiz…'],
    waitText: ['На экране кассы появился запрос. Кассир должен нажать «Разрешить».',
      'Кассанын экранында суроо чыкты. Кассир «Уруксат берүү» басышы керек.',
      'A request appeared on the till screen. The cashier must press “Allow”.',
      'Kasa ekranında bir istek göründü. Kasiyer «İzin ver»e basmalı.',
      'Kassa ekranida so\'rov paydo bo\'ldi. Kassir «Ruxsat berish»ni bosishi kerak.'],
    cancel: ['Отменить', 'Жокко чыгаруу', 'Cancel', 'İptal', 'Bekor qilish'],
    cancelBtn: ['Отмена', 'Жок', 'Cancel', 'İptal', 'Bekor'],
    liveTitle: ['Сеанс идёт', 'Сеанс жүрүп жатат', 'Session is live', 'Oturum sürüyor', 'Seans davom etmoqda'],
    cashierViewOnly: ['Кассир разрешил только просмотр', 'Кассир көрүүгө гана уруксат берди', 'The cashier allowed view only', 'Kasiyer yalnızca izlemeye izin verdi', 'Kassir faqat ko\'rishga ruxsat berdi'],
    viewOnly: ['Только просмотр', 'Көрүү гана', 'View only', 'Yalnızca izle', 'Faqat ko\'rish'],
    viewOnlyTitle: ['Только смотреть, без мыши и клавиатуры', 'Чычкансыз жана баскычтопсуз гана көрүү', 'Watch only, no mouse or keyboard', 'Yalnızca izle, fare ve klavye yok', 'Faqat ko\'rish, sichqoncha va klaviaturasiz'],
    quality: ['Качество', 'Сапат', 'Quality', 'Kalite', 'Sifat'],
    qualityTitle: ['Качество картинки', 'Сүрөттүн сапаты', 'Picture quality', 'Görüntü kalitesi', 'Rasm sifati'],
    qAuto: ['авто', 'авто', 'auto', 'otomatik', 'avto'],
    qLow: ['низкое', 'төмөн', 'low', 'düşük', 'past'],
    qMid: ['среднее', 'орто', 'medium', 'orta', 'o\'rta'],
    qHigh: ['высокое', 'жогору', 'high', 'yüksek', 'yuqori'],
    qMax: ['максимум', 'максимум', 'maximum', 'maksimum', 'maksimal'],
    monitor: ['Монитор', 'Монитор', 'Monitor', 'Monitör', 'Monitor'],
    monitorTitle: ['Монитор кассы', 'Кассанын монитору', 'Till monitor', 'Kasa monitörü', 'Kassa monitori'],
    keys: ['Клавиши', 'Баскычтар', 'Keys', 'Tuşlar', 'Tugmalar'],
    keysTitle: ['Сочетания клавиш, которые браузер не пропускает', 'Браузер өткөрбөгөн баскычтар айкалышы', 'Key combinations the browser does not pass through',
      'Tarayıcının geçirmediği tuş kombinasyonları', 'Brauzer o\'tkazmaydigan tugmalar birikmasi'],
    keyTaskMgr: ['Ctrl+Shift+Esc — диспетчер задач', 'Ctrl+Shift+Esc — тапшырмалар диспетчери', 'Ctrl+Shift+Esc — task manager', 'Ctrl+Shift+Esc — görev yöneticisi', 'Ctrl+Shift+Esc — vazifalar dispetcheri'],
    keyWin: ['Win (Пуск)', 'Win (Старт)', 'Win (Start)', 'Win (Başlat)', 'Win (Start)'],
    keyRun: ['Win+R — выполнить', 'Win+R — аткаруу', 'Win+R — run', 'Win+R — çalıştır', 'Win+R — bajarish'],
    keyDesktop: ['Win+D — рабочий стол', 'Win+D — иш столу', 'Win+D — desktop', 'Win+D — masaüstü', 'Win+D — ish stoli'],
    keyClose: ['Alt+F4 — закрыть окно', 'Alt+F4 — терезени жабуу', 'Alt+F4 — close window', 'Alt+F4 — pencereyi kapat', 'Alt+F4 — oynani yopish'],
    keyCad: ['Ctrl+Alt+Del — не поддерживается', 'Ctrl+Alt+Del — колдоого алынбайт', 'Ctrl+Alt+Del — not supported', 'Ctrl+Alt+Del — desteklenmiyor', 'Ctrl+Alt+Del — qo\'llab-quvvatlanmaydi'],
    text: ['Текст', 'Текст', 'Text', 'Metin', 'Matn'],
    textTitle: ['Напечатать или вставить текст на кассе', 'Кассада текст терүү же коюу', 'Type or paste text on the till', 'Kasada metin yaz veya yapıştır', 'Kassada matn yozish yoki qo\'yish'],
    fit: ['Вписать', 'Батыруу', 'Fit', 'Sığdır', 'Sig\'dirish'],
    scaleTitle: ['Вписать в окно / реальный размер', 'Терезеге батыруу / чыныгы өлчөм', 'Fit to window / actual size', 'Pencereye sığdır / gerçek boyut', 'Oynaga sig\'dirish / haqiqiy o\'lcham'],
    fullscreen: ['Во весь экран', 'Толук экран', 'Full screen', 'Tam ekran', 'To\'liq ekran'],
    fullscreenTitle: ['Во весь экран', 'Толук экранга', 'Full screen', 'Tam ekran', 'To\'liq ekran'],
    statsTitle: ['Кадров в секунду · скорость · задержка до сервера', 'Секундасына кадр · ылдамдык · серверге чейинки кечигүү', 'Frames per second · speed · delay to the server',
      'Saniyede kare · hız · sunucuya gecikme', 'Soniyasiga kadr · tezlik · serverga kechikish'],
    hostStatsTitle: ['Касса: {f} к/с, {k} кбит/с. Задержка — до сервера поддержки.', 'Касса: {f} к/с, {k} кбит/с. Кечигүү — колдоо серверине чейин.',
      'Till: {f} fps, {k} kbit/s. The delay is to the support server.', 'Kasa: {f} fps, {k} kbit/s. Gecikme destek sunucusuna kadardır.', 'Kassa: {f} k/s, {k} kbit/s. Kechikish — yordam serverigacha.'],
    fpsU: ['к/с', 'к/с', 'fps', 'fps', 'k/s'],
    kbpsU: ['кбит/с', 'кбит/с', 'kbit/s', 'kbit/s', 'kbit/s'],
    msU: ['мс', 'мс', 'ms', 'ms', 'ms'],
    end: ['Завершить', 'Аяктоо', 'End', 'Bitir', 'Tugatish'],
    screenAria: ['Экран кассы', 'Кассанын экраны', 'Till screen', 'Kasa ekranı', 'Kassa ekrani'],
    firstFrame: ['Ждём первый кадр…', 'Биринчи кадрды күтүп жатабыз…', 'Waiting for the first frame…', 'İlk kare bekleniyor…', 'Birinchi kadrni kutyapmiz…'],
    endedTitle: ['Сеанс завершён', 'Сеанс аяктады', 'Session ended', 'Oturum bitti', 'Seans tugadi'],
    newConn: ['Новое подключение', 'Жаңы туташуу', 'New connection', 'Yeni bağlantı', 'Yangi ulanish'],
    textDlgTitle: ['Текст на кассу', 'Кассага текст', 'Text to the till', 'Kasaya metin', 'Kassaga matn'],
    textDlgHint: ['Текст будет напечатан там, где сейчас курсор на кассе. Кириллица поддерживается.', 'Текст кассада курсор турган жерге терилет. Кириллица колдоого алынат.',
      'The text is typed where the cursor is on the till. Cyrillic is supported.', 'Metin kasada imlecin olduğu yere yazılır. Kiril desteklenir.', 'Matn kassada kursor turgan joyga yoziladi. Kirill qo\'llab-quvvatlanadi.'],
    textPh: ['Введите или вставьте текст', 'Текст киргизиңиз же коюңуз', 'Type or paste text', 'Metin yazın veya yapıştırın', 'Matn kiriting yoki qo\'ying'],
    send: ['Отправить', 'Жөнөтүү', 'Send', 'Gönder', 'Yuborish'],
    journalTitle: ['Журнал сеансов', 'Сеанстар журналы', 'Session log', 'Oturum günlüğü', 'Seanslar jurnali'],
    refresh: ['Обновить', 'Жаңыртуу', 'Refresh', 'Yenile', 'Yangilash'],
    jNow: ['Сейчас', 'Азыр', 'Now', 'Şimdi', 'Hozir'],
    jRecent: ['Последние сеансы', 'Акыркы сеанстар', 'Recent sessions', 'Son oturumlar', 'So\'nggi seanslar'],
    thState: ['Состояние', 'Абалы', 'State', 'Durum', 'Holat'],
    thCompany: ['Компания', 'Компания', 'Company', 'Şirket', 'Kompaniya'],
    thTill: ['Касса', 'Касса', 'Till', 'Kasa', 'Kassa'],
    thVersion: ['Версия', 'Версия', 'Version', 'Sürüm', 'Versiya'],
    thTillIp: ['IP кассы', 'Кассанын IP', 'Till IP', 'Kasa IP', 'Kassa IP'],
    thOperator: ['Оператор', 'Оператор', 'Operator', 'Operatör', 'Operator'],
    thStart: ['Начало', 'Башталышы', 'Start', 'Başlangıç', 'Boshlanishi'],
    thDuration: ['Длительность', 'Узактыгы', 'Duration', 'Süre', 'Davomiyligi'],
    thResult: ['Итог', 'Жыйынтык', 'Result', 'Sonuç', 'Natija'],
    jEmptyNow: ['Сейчас никого нет', 'Азыр эч ким жок', 'Nobody right now', 'Şu an kimse yok', 'Hozir hech kim yo\'q'],
    jEmptyRecent: ['Журнал пока пуст', 'Журнал азырынча бош', 'The log is empty', 'Günlük henüz boş', 'Jurnal hozircha bo\'sh'],
    jLoadFail: ['Не удалось загрузить журнал.', 'Журналды жүктөө мүмкүн болгон жок.', 'Could not load the log.', 'Günlük yüklenemedi.', 'Jurnalni yuklab bo\'lmadi.'],
    stPending: ['Ждёт оператора', 'Операторду күтөт', 'Waiting for an operator', 'Operatör bekliyor', 'Operator kutmoqda'],
    stAwait: ['Ждёт разрешения', 'Уруксат күтөт', 'Awaiting consent', 'İzin bekliyor', 'Ruxsat kutmoqda'],
    stActive: ['Идёт сеанс', 'Сеанс жүрүп жатат', 'Session live', 'Oturum sürüyor', 'Seans davom etmoqda'],
    rOperator: ['Завершил оператор', 'Оператор аяктады', 'Ended by operator', 'Operatör bitirdi', 'Operator tugatdi'],
    rHost: ['Завершил кассир', 'Кассир аяктады', 'Ended by cashier', 'Kasiyer bitirdi', 'Kassir tugatdi'],
    rTimeout: ['Пропала связь', 'Байланыш үзүлдү', 'Connection lost', 'Bağlantı koptu', 'Aloqa uzildi'],
    rError: ['Ошибка связи', 'Байланыш катасы', 'Connection error', 'Bağlantı hatası', 'Aloqa xatosi'],
    rDisconnect: ['Соединение оборвалось', 'Туташуу үзүлдү', 'Disconnected', 'Bağlantı kesildi', 'Ulanish uzildi'],
    rShutdown: ['Сервер перезапущен', 'Сервер кайра иштетилди', 'Server restarted', 'Sunucu yeniden başlatıldı', 'Server qayta ishga tushirildi'],
    rDeclined: ['Кассир отказал', 'Кассир баш тартты', 'Cashier declined', 'Kasiyer reddetti', 'Kassir rad etdi'],
    rConsentTimeout: ['Кассир не ответил', 'Кассир жооп берген жок', 'No answer from cashier', 'Kasiyer yanıt vermedi', 'Kassir javob bermadi'],
    rLocked: ['Блокировка: неверный PIN', 'Бөгөт: PIN туура эмес', 'Locked: wrong PIN', 'Kilit: yanlış PIN', 'Bloklandi: PIN noto\'g\'ri'],
    rExpired: ['Код истёк', 'Код эскирди', 'Code expired', 'Kodun süresi doldu', 'Kod eskirdi'],
    rIdle: ['Простой', 'Иш-аракет жок', 'Idle timeout', 'Hareketsizlik', 'Harakatsizlik'],
    rMax: ['Предел времени', 'Убакыт чеги', 'Time limit', 'Süre sınırı', 'Vaqt chegarasi'],
    rRevoked: ['Вход отозван', 'Кирүү жокко чыгарылды', 'Sign-in revoked', 'Giriş iptal edildi', 'Kirish bekor qilindi'],
    rLogout: ['Оператор вышел', 'Оператор чыкты', 'Operator signed out', 'Operatör çıkış yaptı', 'Operator chiqdi'],
    unitH: ['ч', 'саат', 'h', 'sa', 'soat'],
    unitM: ['мин', 'мүн', 'min', 'dk', 'daq'],
    unitS: ['с', 'сек', 's', 'sn', 's'],
    errBadLogin: ['Неверный логин или пароль.', 'Логин же сырсөз туура эмес.', 'Wrong login or password.', 'Kullanıcı adı veya şifre yanlış.', 'Login yoki parol noto\'g\'ri.'],
    errTooMany: ['Слишком много попыток. Подождите пару минут.', 'Өтө көп аракет. Бир нече мүнөт күтүңүз.', 'Too many attempts. Wait a couple of minutes.',
      'Çok fazla deneme. Birkaç dakika bekleyin.', 'Juda ko\'p urinish. Bir necha daqiqa kuting.'],
    errServerStatus: ['Сервер ответил ошибкой ({n}).', 'Сервер ката менен жооп берди ({n}).', 'The server returned an error ({n}).', 'Sunucu hata döndürdü ({n}).', 'Server xato qaytardi ({n}).'],
    errNoConn: ['Нет связи с сервером.', 'Сервер менен байланыш жок.', 'No connection to the server.', 'Sunucuyla bağlantı yok.', 'Server bilan aloqa yo\'q.'],
    serverDown: ['Сервер недоступен. Проверьте интернет или попробуйте позже.', 'Сервер жеткиликсиз. Интернетти текшериңиз же кийинчерээк аракет кылыңыз.',
      'The server is unavailable. Check your internet or try again later.', 'Sunucuya ulaşılamıyor. İnternetinizi kontrol edin veya daha sonra deneyin.',
      'Server mavjud emas. Internetni tekshiring yoki keyinroq urinib ko\'ring.'],
    relogin: ['Время входа истекло — войдите снова.', 'Кирүү мөөнөтү бүттү — кайра кириңиз.', 'Your sign-in expired — sign in again.', 'Oturum süresi doldu — yeniden giriş yapın.', 'Kirish muddati tugadi — qayta kiring.'],
    errCode9: ['Код — 9 цифр.', 'Код — 9 сан.', 'The code is 9 digits.', 'Kod 9 haneli.', 'Kod — 9 raqam.'],
    errPin4: ['PIN — 4 цифры.', 'PIN — 4 сан.', 'The PIN is 4 digits.', 'PIN 4 haneli.', 'PIN — 4 raqam.'],
    cancelled: ['Подключение отменено.', 'Туташуу токтотулду.', 'Connection cancelled.', 'Bağlantı iptal edildi.', 'Ulanish bekor qilindi.'],
    eBadPin: ['Неверный PIN.', 'PIN туура эмес.', 'Wrong PIN.', 'Yanlış PIN.', 'PIN noto\'g\'ri.'],
    eBadPinLeft: ['Неверный PIN. Осталось попыток: {n}.', 'PIN туура эмес. Калган аракет: {n}.', 'Wrong PIN. Attempts left: {n}.', 'Yanlış PIN. Kalan deneme: {n}.', "PIN noto'g'ri. Qolgan urinish: {n}."],
    eNoSession: ['Сеанс с таким кодом не найден. Проверьте код или попросите кассира получить новый.', 'Мындай кодлуу сеанс табылган жок. Кодду текшериңиз же кассирден жаңысын алууну сураңыз.',
      'No session with this code. Check the code or ask the cashier to get a new one.', 'Bu kodla oturum bulunamadı. Kodu kontrol edin veya kasiyerden yenisini almasını isteyin.',
      'Bunday kodli seans topilmadi. Kodni tekshiring yoki kassirdan yangisini olishni so\'rang.'],
    eLocked: ['Слишком много неверных PIN — код заблокирован. Кассир должен получить новый код.', 'PIN өтө көп жолу туура эмес — код бөгөттөлдү. Кассир жаңы код алышы керек.',
      'Too many wrong PINs — the code is locked. The cashier must get a new code.', 'Çok fazla yanlış PIN — kod kilitlendi. Kasiyer yeni kod almalı.',
      'PIN juda ko\'p marta noto\'g\'ri — kod bloklandi. Kassir yangi kod olishi kerak.'],
    eBusy: ['К этой кассе уже подключается или подключён другой оператор.', 'Бул кассага башка оператор туташууда же туташкан.', 'Another operator is already connecting or connected to this till.',
      'Bu kasaya başka bir operatör bağlanıyor veya bağlı.', 'Bu kassaga boshqa operator ulanmoqda yoki ulangan.'],
    eDeclined: ['Кассир не разрешил подключение.', 'Кассир туташууга уруксат берген жок.', 'The cashier did not allow the connection.', 'Kasiyer bağlantıya izin vermedi.', 'Kassir ulanishga ruxsat bermadi.'],
    eExpired: ['Код устарел (действует 15 минут). Попросите кассира получить новый.', 'Код эскирди (15 мүнөт жарайт). Кассирден жаңысын алууну сураңыз.',
      'The code expired (valid for 15 minutes). Ask the cashier for a new one.', 'Kodun süresi doldu (15 dakika geçerli). Kasiyerden yenisini isteyin.', 'Kod eskirdi (15 daqiqa amal qiladi). Kassirdan yangisini so\'rang.'],
    eRate: ['Слишком много попыток. Подождите минуту.', 'Өтө көп аракет. Бир мүнөт күтүңүз.', 'Too many attempts. Wait a minute.', 'Çok fazla deneme. Bir dakika bekleyin.', 'Juda ko\'p urinish. Bir daqiqa kuting.'],
    eBadReq: ['Сервер не понял запрос. Обновите страницу.', 'Сервер суроону түшүнгөн жок. Баракты жаңыртыңыз.', 'The server did not understand the request. Reload the page.',
      'Sunucu isteği anlamadı. Sayfayı yenileyin.', 'Server so\'rovni tushunmadi. Sahifani yangilang.'],
    eNotJoined: ['Сеанс уже завершён.', 'Сеанс буга чейин аякталган.', 'The session has already ended.', 'Oturum zaten bitti.', 'Seans allaqachon tugagan.'],
    eGeneric: ['Ошибка: {x}', 'Ката: {x}', 'Error: {x}', 'Hata: {x}', 'Xato: {x}'],
    endOperator: ['Сеанс завершил оператор.', 'Сеансты оператор аяктады.', 'The operator ended the session.', 'Oturumu operatör sonlandırdı.', 'Seansni operator tugatdi.'],
    endHost: ['Кассир завершил сеанс.', 'Кассир сеансты аяктады.', 'The cashier ended the session.', 'Kasiyer oturumu sonlandırdı.', 'Kassir seansni tugatdi.'],
    endTimeout: ['Связь с кассой пропала (нет ответа 45 секунд).', 'Касса менен байланыш үзүлдү (45 секунд жооп жок).', 'Lost contact with the till (no answer for 45 seconds).',
      'Kasayla bağlantı koptu (45 saniye yanıt yok).', 'Kassa bilan aloqa uzildi (45 soniya javob yo\'q).'],
    endError: ['Сеанс прерван из-за ошибки связи.', 'Сеанс байланыш катасынан улам үзүлдү.', 'The session was interrupted by a connection error.', 'Oturum bağlantı hatası nedeniyle kesildi.', 'Seans aloqa xatosi tufayli uzildi.'],
    endDisconnect: ['Касса отключилась.', 'Касса өчүп калды.', 'The till disconnected.', 'Kasa bağlantıyı kesti.', 'Kassa uzildi.'],
    endShutdown: ['Сервер поддержки перезапускается.', 'Колдоо сервери кайра иштетилүүдө.', 'The support server is restarting.', 'Destek sunucusu yeniden başlıyor.', 'Yordam serveri qayta ishga tushmoqda.'],
    endConsentTimeout: ['Кассир не ответил на запрос.', 'Кассир суроого жооп берген жок.', 'The cashier did not answer the request.', 'Kasiyer isteğe yanıt vermedi.', 'Kassir so\'rovga javob bermadi.'],
    endIdle: ['Сеанс завершён: оператор долго ничего не делал.', 'Сеанс аяктады: оператор көпкө чейин эч нерсе кылган жок.', 'The session ended: no operator activity for a long time.',
      'Oturum bitti: operatör uzun süre işlem yapmadı.', 'Seans tugadi: operator uzoq vaqt hech narsa qilmadi.'],
    endMax: ['Сеанс достиг предела времени и завершён.', 'Сеанс убакыт чегине жетип аяктады.', 'The session reached its time limit and was ended.',
      'Oturum süre sınırına ulaştı ve sonlandırıldı.', 'Seans vaqt chegarasiga yetib tugatildi.'],
    endRevoked: ['Ваш вход отозван (сменён пароль или удалён доступ). Войдите снова.', 'Кирүүңүз жокко чыгарылды (сырсөз алмашты же мүмкүнчүлүк өчүрүлдү). Кайра кириңиз.',
      'Your sign-in was revoked (password changed or access removed). Sign in again.', 'Girişiniz iptal edildi (şifre değişti veya erişim kaldırıldı). Yeniden giriş yapın.',
      'Kirishingiz bekor qilindi (parol o\'zgargan yoki ruxsat olib tashlangan). Qayta kiring.'],
    endLogout: ['Вы вышли из программы.', 'Сиз чыктыңыз.', 'You signed out.', 'Çıkış yaptınız.', 'Siz chiqdingiz.'],
    endOther: ['Причина: {x}', 'Себеби: {x}', 'Reason: {x}', 'Neden: {x}', 'Sababi: {x}'],
    connLostTitle: ['Связь с сервером потеряна', 'Сервер менен байланыш үзүлдү', 'Connection to the server lost', 'Sunucu bağlantısı koptu', 'Server bilan aloqa uzildi'],
    connLostText: ['Сеанс прерван. Коды одноразовые — попросите кассира получить новый код и подключитесь снова.', 'Сеанс үзүлдү. Коддор бир жолу колдонулат — кассирден жаңы код алууну сураңыз да, кайра туташыңыз.',
      'The session was interrupted. Codes are single-use — ask the cashier for a new code and connect again.', 'Oturum kesildi. Kodlar tek kullanımlıktır — kasiyerden yeni kod isteyin ve yeniden bağlanın.',
      'Seans uzildi. Kodlar bir martalik — kassirdan yangi kod so\'rang va qayta ulaning.'],
    connFail: ['Не удалось подключиться к серверу поддержки. Проверьте интернет.', 'Колдоо серверине туташуу мүмкүн болгон жок. Интернетти текшериңиз.',
      'Could not connect to the support server. Check your internet.', 'Destek sunucusuna bağlanılamadı. İnternetinizi kontrol edin.', 'Yordam serveriga ulanib bo\'lmadi. Internetni tekshiring.'],
    connLost2: ['Связь с сервером потеряна. Попробуйте снова.', 'Сервер менен байланыш үзүлдү. Кайра аракет кылыңыз.', 'Connection to the server lost. Try again.',
      'Sunucu bağlantısı koptu. Tekrar deneyin.', 'Server bilan aloqa uzildi. Qayta urinib ko\'ring.'],
    connInterrupted: ['Подключение прервано.', 'Туташуу үзүлдү.', 'Connection interrupted.', 'Bağlantı kesildi.', 'Ulanish uzildi.'],
    offlineBanner: ['Нет интернета на этом компьютере. Ждём восстановления связи…', 'Бул компьютерде интернет жок. Байланыш калыбына келишин күтөбүз…',
      'No internet on this computer. Waiting for the connection to return…', 'Bu bilgisayarda internet yok. Bağlantının dönmesi bekleniyor…', 'Bu kompyuterda internet yo\'q. Aloqa tiklanishini kutyapmiz…'],
    caTitle: ['Кассир работает — ваше управление приостановлено', 'Кассир иштеп жатат — сиздин башкаруу токтотулду', 'The cashier is working — your control is paused',
      'Kasiyer çalışıyor — kontrolünüz durduruldu', 'Kassir ishlayapti — boshqaruvingiz to\'xtatildi'],
    caHint: ['Ввод включится сам через несколько секунд после того, как кассир закончит.', 'Кассир бүткөндөн бир нече секунддан кийин киргизүү өзү күйөт.',
      'Input resumes by itself a few seconds after the cashier stops.', 'Kasiyer bitirdikten birkaç saniye sonra giriş kendiliğinden açılır.', 'Kassir tugatgandan bir necha soniya o\'tgach kiritish o\'zi yoqiladi.'],
    pausedTitle: ['Экран защищён Windows (UAC или блокировка) — сеанс продолжается', 'Экранды Windows коргоодо (UAC же кулпулоо) — сеанс уланууда',
      'The screen is protected by Windows (UAC or lock) — the session continues', 'Ekran Windows tarafından korunuyor (UAC veya kilit) — oturum sürüyor',
      'Ekran Windows tomonidan himoyalangan (UAC yoki qulf) — seans davom etmoqda'],
    pausedHint: ['Картинка вернётся сама, когда кассир ответит на запрос Windows или разблокирует экран.', 'Кассир Windows суроосуна жооп бергенде же экранды ачканда сүрөт өзү кайтат.',
      'The picture returns by itself once the cashier answers the Windows prompt or unlocks the screen.', 'Kasiyer Windows istemini yanıtlayınca veya ekranı açınca görüntü kendiliğinden döner.',
      'Kassir Windows so\'roviga javob berganda yoki ekranni ochganda tasvir o\'zi qaytadi.'],
    voOnToast: ['Только просмотр: мышь и клавиатура не передаются.', 'Көрүү гана: чычкан менен баскычтоп берилбейт.', 'View only: mouse and keyboard are not sent.', 'Yalnızca izle: fare ve klavye iletilmiyor.', 'Faqat ko\'rish: sichqoncha va klaviatura uzatilmaydi.'],
    ctrlOn: ['Управление включено.', 'Башкаруу күйгүзүлдү.', 'Control is on.', 'Kontrol açık.', 'Boshqaruv yoqildi.'],
    viewOnlyOn: ['Включён режим «Только просмотр».', '«Көрүү гана» режими күйгүзүлгөн.', '“View only” mode is on.', '«Yalnızca izle» modu açık.', '«Faqat ko\'rish» rejimi yoqilgan.'],
    voByCashier: ['Кассир разрешил только просмотр — управление недоступно.', 'Кассир көрүүгө гана уруксат берди — башкаруу жеткиликсиз.', 'The cashier allowed view only — control is unavailable.',
      'Kasiyer yalnızca izlemeye izin verdi — kontrol kullanılamaz.', 'Kassir faqat ko\'rishga ruxsat berdi — boshqaruv mavjud emas.'],
    fsLock: ['Во весь экран: чтобы выйти, удерживайте Esc.', 'Толук экран: чыгуу үчүн Esc басып туруңуз.', 'Full screen: hold Esc to exit.', 'Tam ekran: çıkmak için Esc\'e basılı tutun.', 'To\'liq ekran: chiqish uchun Esc ni bosib turing.'],
    fsDenied: ['Браузер не разрешил полноэкранный режим.', 'Браузер толук экранга уруксат берген жок.', 'The browser did not allow full screen.', 'Tarayıcı tam ekrana izin vermedi.', 'Brauzer to\'liq ekranga ruxsat bermadi.'],
    cadToast: ['Ctrl+Alt+Del передать нельзя: Windows принимает его только с настоящей клавиатуры. Для диспетчера задач — Ctrl+Shift+Esc.',
      'Ctrl+Alt+Del берүүгө болбойт: Windows аны чыныгы баскычтоптон гана кабыл алат. Тапшырмалар диспетчери үчүн — Ctrl+Shift+Esc.',
      'Ctrl+Alt+Del cannot be sent: Windows accepts it only from a real keyboard. For the task manager use Ctrl+Shift+Esc.',
      'Ctrl+Alt+Del gönderilemez: Windows bunu yalnızca gerçek klavyeden kabul eder. Görev yöneticisi için Ctrl+Shift+Esc.',
      'Ctrl+Alt+Del yuborib bo\'lmaydi: Windows uni faqat haqiqiy klaviaturadan qabul qiladi. Vazifalar dispetcheri uchun — Ctrl+Shift+Esc.'],
    sentChars: ['Отправлено символов: {n}', 'Жөнөтүлгөн белгилер: {n}', 'Characters sent: {n}', 'Gönderilen karakter: {n}', 'Yuborilgan belgilar: {n}'],
    tillDefault: ['Касса', 'Касса', 'Till', 'Kasa', 'Kassa'],
    operatorLbl: ['оператор', 'оператор', 'operator', 'operatör', 'operator'],
    pwTitle: ['Смена пароля', 'Сырсөздү алмаштыруу', 'Change password', 'Şifre değiştirme', 'Parolni almashtirish'],
    pwOld: ['Текущий пароль', 'Азыркы сырсөз', 'Current password', 'Mevcut şifre', 'Joriy parol'],
    pwNew: ['Новый пароль', 'Жаңы сырсөз', 'New password', 'Yeni şifre', 'Yangi parol'],
    pwRepeat: ['Новый пароль ещё раз', 'Жаңы сырсөздү кайра', 'New password again', 'Yeni şifre (tekrar)', 'Yangi parol yana'],
    pwSave: ['Сменить', 'Алмаштыруу', 'Change', 'Değiştir', 'Almashtirish'],
    pwMismatch: ['Новые пароли не совпадают.', 'Жаңы сырсөздөр дал келген жок.', 'The new passwords do not match.', 'Yeni şifreler uyuşmuyor.', 'Yangi parollar mos kelmadi.'],
    pwWeak: ['Новый пароль слишком короткий (не меньше {n} символов).', 'Жаңы сырсөз өтө кыска (кеминде {n} белги).', 'The new password is too short (at least {n} characters).',
      'Yeni şifre çok kısa (en az {n} karakter).', 'Yangi parol juda qisqa (kamida {n} belgi).'],
    pwSame: ['Новый пароль совпадает со старым.', 'Жаңы сырсөз эскиси менен бирдей.', 'The new password is the same as the old one.', 'Yeni şifre eskisiyle aynı.', 'Yangi parol eskisi bilan bir xil.'],
    pwBadOld: ['Текущий пароль неверный.', 'Азыркы сырсөз туура эмес.', 'The current password is wrong.', 'Mevcut şifre yanlış.', 'Joriy parol noto\'g\'ri.'],
    pwDone: ['Пароль изменён. Другие входы с этим логином закрыты.', 'Сырсөз алмашты. Бул логин менен башка кирүүлөр жабылды.', 'Password changed. Other sign-ins with this login were closed.',
      'Şifre değiştirildi. Bu kullanıcı adıyla diğer girişler kapatıldı.', 'Parol o\'zgartirildi. Ushbu login bilan boshqa kirishlar yopildi.'],
    pwNotSupported: ['Сервер ещё не поддерживает смену пароля.', 'Сервер сырсөз алмаштырууну азырынча колдобойт.', 'The server does not support password change yet.',
      'Sunucu henüz şifre değiştirmeyi desteklemiyor.', 'Server hali parol almashtirishni qo\'llab-quvvatlamaydi.'],
  };
  const MIN_PW = 10; // на сервере Relay:MinPasswordLength; если сервер скажет другое — возьмём его min

  function pickLang() {
    try { const s = localStorage.getItem('nrs-lang'); if (LANGS.includes(s)) return s; } catch { /* нет хранилища */ }
    const l = String(navigator.language || 'ru').slice(0, 2).toLowerCase();
    return LANGS.includes(l) ? l : 'ru';
  }
  let lang = pickLang();
  function t(key, params) {
    const row = T[key];
    let s = row ? (row[LANGS.indexOf(lang)] || row[0]) : key;
    if (params) for (const k of Object.keys(params)) s = s.split('{' + k + '}').join(String(params[k]));
    return s;
  }

  // Ошибка в поле хранится ключом перевода — при смене языка пересчитывается
  function setErr(id, key, params) {
    const el = $(id);
    if (!key) { el.textContent = ''; delete el.dataset.k; delete el.dataset.p; return; }
    el.dataset.k = key;
    el.dataset.p = params ? JSON.stringify(params) : '';
    el.textContent = t(key, params);
  }

  // ---------- состояние ----------
  const st = {
    auth: null,          // {token, name, login, expiresAt}
    ws: null,
    wsGen: 0,            // номер попытки подключения (отмена устаревших)
    phase: 'idle',       // idle | joining | waiting | viewer | ended
    view: 'login',
    host: null,          // данные кассы из started
    viewOnly: false,
    cashierViewOnly: false, // 2026-10-27: кассир выбрал «Только просмотр» (started.viewOnly / view-only cashier) — ввод у оператора выключен
    clientActive: false, // 2026-10-26: приоритет кассира — кассир работает сам, касса не исполняет ввод оператора (client-active)
    paused: false, pausedAt: 0, // 2026-10-27: экран кассы защищён Windows (screen-paused)
    actualSize: false,
    zoom: 1,             // 2026-10-27: масштаб пальцами поверх «вписать»
    monitors: 1,
    endShown: false,
    ended: null,         // {title:ключ, text:ключ|{key,params}|строка} — чтобы перевести при смене языка
    journal: null,
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
    lastPoint: null,     // 2026-10-27: последняя известная точка мыши на экране кассы (для отпускания кнопок)
    lastTouchAt: 0,
    wheelAcc: 0,
    cursor: null,
  };

  const canvas = $('screen');
  const ctx2d = canvas.getContext('2d', { alpha: false, desynchronized: true });
  const stage = $('stage');

  // ---------- общие мелочи ----------
  let toastTimer = 0;
  function toast(text, ms = 4000) {
    const el = $('toast');
    el.textContent = text;
    el.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { el.hidden = true; }, ms);
  }

  function show(view) {
    st.view = view;
    for (const v of ['login', 'connect', 'waiting', 'viewer', 'journal']) $('view-' + v).hidden = v !== view;
    $('topbar').hidden = view === 'login' || view === 'viewer';
    document.querySelectorAll('.navbtn').forEach((b) => b.classList.toggle('active', b.dataset.nav === view));
    updateDocTitle();
  }
  function updateDocTitle() {
    document.title = st.view === 'viewer' && st.host ? `${st.host.company || t('tillDefault')} · ${t('brandSupport')}` : t('pageTitle');
  }

  // ---------- язык и тема ----------
  function prefSave(k, v) { try { localStorage.setItem(k, v); } catch { /* ничего */ } }
  function prefLoad(k) { try { return localStorage.getItem(k); } catch { return null; } }

  function applyStatic() {
    document.documentElement.lang = lang;
    document.querySelectorAll('[data-i18n]').forEach((el) => { el.textContent = t(el.dataset.i18n); });
    document.querySelectorAll('[data-i18n-title]').forEach((el) => { el.title = t(el.dataset.i18nTitle); });
    document.querySelectorAll('[data-i18n-aria]').forEach((el) => { el.setAttribute('aria-label', t(el.dataset.i18nAria)); });
    document.querySelectorAll('[data-i18n-ph]').forEach((el) => { el.placeholder = t(el.dataset.i18nPh); });
    document.querySelectorAll('.error[data-k]').forEach((el) => { el.textContent = t(el.dataset.k, el.dataset.p ? JSON.parse(el.dataset.p) : undefined); });
    document.querySelectorAll('#login-langs .lang-chip').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.lang === lang)));
    $('lang-select-top').value = lang;
    $('tb-scale-label').textContent = st.actualSize ? '100%' : t('fit');
    updateDocTitle();
    if (st.host) $('vw-machine').textContent = machineLine();
    if (st.ended) renderEnded();
    if (st.clientActive) fillPlate('client-active', 'caTitle', 'caHint');
    if (st.paused) fillPlate('screen-paused', 'pausedTitle', 'pausedHint');
    if (st.view === 'viewer') updateStats();
    if (st.view === 'journal' && st.journal) renderJournal();
  }
  function setLang(l) {
    if (!LANGS.includes(l)) return;
    lang = l;
    prefSave('nrs-lang', l);
    applyStatic();
  }
  function buildLangUi() {
    const box = $('login-langs');
    const sel = $('lang-select-top');
    for (const l of LANGS) {
      const b = document.createElement('button');
      b.type = 'button';
      b.className = 'lang-chip';
      b.dataset.lang = l;
      b.textContent = l.toUpperCase();
      b.title = LANG_NAMES[l];
      b.addEventListener('click', () => setLang(l));
      box.appendChild(b);
      const o = document.createElement('option');
      o.value = l;
      o.textContent = LANG_NAMES[l];
      sel.appendChild(o);
    }
    sel.addEventListener('change', () => setLang(sel.value));
  }
  function effectiveTheme() {
    return document.documentElement.dataset.theme || (window.matchMedia && matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
  }
  function applyTheme() {
    const v = prefLoad('nrs-theme');
    if (v === 'light' || v === 'dark') document.documentElement.dataset.theme = v; else delete document.documentElement.dataset.theme;
  }
  document.querySelectorAll('.theme-btn').forEach((b) => b.addEventListener('click', () => {
    const next = effectiveTheme() === 'dark' ? 'light' : 'dark';
    prefSave('nrs-theme', next);
    applyTheme();
  }));

  // ---------- вход ----------
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
      logout('relogin');
      throw new Error('unauthorized');
    }
    return res;
  }

  function logout(messageKey) {
    closeSocket();
    stopViewerTimers();
    st.phase = 'idle';
    saveAuth(null);
    show('login');
    setErr('login-error', messageKey || '');
    $('login-password').value = '';
    $('login-submit').disabled = false;
    $('pw-dialog').hidden = true;
    setTimeout(() => $('login-login').focus(), 0);
  }

  $('login-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const btn = $('login-submit');
    btn.disabled = true;
    setErr('login-error', '');
    try {
      const res = await fetch('/api/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ login: $('login-login').value.trim(), password: $('login-password').value }),
      });
      if (res.status === 401) { setErr('login-error', 'errBadLogin'); return; }
      if (res.status === 429) { setErr('login-error', 'errTooMany'); return; }
      if (res.status >= 500) { setErr('login-error', 'serverDown'); return; }
      if (!res.ok) { setErr('login-error', 'errServerStatus', { n: res.status }); return; }
      const data = await res.json();
      saveAuth({ token: data.token, name: data.name, login: data.login, expiresAt: data.expiresAt });
      $('login-password').value = '';
      enterApp();
    } catch {
      setErr('login-error', 'serverDown');
    } finally {
      btn.disabled = false;
    }
  });

  // 2026-10-27 (С-4): выход отзывает токен на сервере (если сервер умеет — иначе просто выходим)
  $('logout').addEventListener('click', async () => {
    try { if (st.auth) await fetch('/api/logout', { method: 'POST', headers: { Authorization: 'Bearer ' + st.auth.token } }); } catch { /* всё равно выходим */ }
    logout('');
  });

  function enterApp() {
    $('me-name').textContent = st.auth.name || st.auth.login;
    goConnect();
  }

  document.querySelectorAll('.navbtn').forEach((b) => b.addEventListener('click', () => {
    if (b.dataset.nav === 'journal') openJournal(); else goConnect();
  }));

  // ---------- смена пароля (С-5) ----------
  $('change-pw').addEventListener('click', () => {
    $('pw-form').reset();
    setErr('pw-error', '');
    $('pw-dialog').hidden = false;
    setTimeout(() => $('pw-old').focus(), 0);
  });
  $('pw-cancel').addEventListener('click', () => { $('pw-dialog').hidden = true; });
  $('pw-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const oldPw = $('pw-old').value, newPw = $('pw-new').value;
    setErr('pw-error', '');
    if (newPw !== $('pw-new2').value) { setErr('pw-error', 'pwMismatch'); return; }
    if (newPw.length < MIN_PW) { setErr('pw-error', 'pwWeak', { n: MIN_PW }); return; }
    if (newPw === oldPw) { setErr('pw-error', 'pwSame'); return; }
    const btn = $('pw-submit');
    btn.disabled = true;
    try {
      const res = await api('/api/password', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ old: oldPw, new: newPw }) });
      if (res.ok) {
        const d = await res.json();
        // Прежние токены после смены недействительны — берём свежий из ответа
        saveAuth({ token: d.token, name: d.name || st.auth.name, login: d.login || st.auth.login, expiresAt: d.expiresAt || st.auth.expiresAt });
        closeSocket();
        $('pw-dialog').hidden = true;
        toast(t('pwDone'), 5000);
        return;
      }
      let body = {};
      try { body = await res.json(); } catch { /* без тела */ }
      if (res.status === 403) setErr('pw-error', 'pwBadOld');
      else if (res.status === 429) setErr('pw-error', 'errTooMany');
      else if (res.status === 400 && body.error === 'weak-password') setErr('pw-error', 'pwWeak', { n: body.min || MIN_PW });
      else if (res.status === 400 && body.error === 'same-password') setErr('pw-error', 'pwSame');
      else if (res.status === 404 || res.status === 405) setErr('pw-error', 'pwNotSupported');
      else setErr('pw-error', 'errServerStatus', { n: res.status });
    } catch (err) {
      if (err.message !== 'unauthorized') setErr('pw-error', 'serverDown');
    } finally {
      btn.disabled = false;
    }
  });

  // ---------- код и PIN ----------
  const codeInput = $('join-code');
  const pinInput = $('join-pin');

  // 2026-10-27 (В-8): поле принимает любую вставку — «Код: 123 456 789», «123456789 PIN: 5093», 13 цифр подряд (код + PIN).
  // Лишнее убирается, цифры раскладываются на код и PIN. maxlength у полей нет — он обрезал вставку до «123 45».
  function parseCodePin(text) {
    const s = String(text);
    const m = /pin/i.exec(s);
    if (m) {
      return { code: s.slice(0, m.index).replace(/\D/g, '').slice(0, 9), pin: s.slice(m.index + 3).replace(/\D/g, '').slice(0, 4), word: true };
    }
    const d = s.replace(/\D/g, '');
    return { code: d.slice(0, 9), pin: d.slice(9, 13), word: false, all: d };
  }
  function fmtCode(d) { return d.replace(/(\d{3})(?=\d)/g, '$1 '); }

  codeInput.addEventListener('input', () => {
    const r = parseCodePin(codeInput.value);
    codeInput.value = fmtCode(r.code);
    if (r.pin.length > 0) pinInput.value = r.pin;
    if (r.code.length === 9 && document.activeElement === codeInput) pinInput.focus();
  });
  pinInput.addEventListener('input', () => {
    const r = parseCodePin(pinInput.value);
    if (r.word) {
      if (r.code.length === 9) codeInput.value = fmtCode(r.code);
      pinInput.value = r.pin;
    } else if (r.all.length >= 13) {
      codeInput.value = fmtCode(r.code);
      pinInput.value = r.pin;
    } else {
      pinInput.value = r.all.slice(0, 4);
    }
  });

  function goConnect(errKey, errParams) {
    st.phase = 'idle';
    show('connect');
    setErr('join-error', errKey || '', errParams);
    $('join-submit').disabled = false;
    setTimeout(() => (codeInput.value ? pinInput : codeInput).focus(), 0);
  }

  $('join-form').addEventListener('submit', (e) => {
    e.preventDefault();
    const code = codeInput.value.replace(/\D/g, '');
    const pin = pinInput.value.replace(/\D/g, '');
    if (code.length !== 9) { setErr('join-error', 'errCode9'); return; }
    if (pin.length !== 4) { setErr('join-error', 'errPin4'); return; }
    setErr('join-error', '');
    $('join-submit').disabled = true;
    st.phase = 'joining';
    ensureSocket(() => send({ type: 'join', code, pin }));
  });

  $('waiting-cancel').addEventListener('click', () => {
    send({ type: 'end', reason: 'operator' });
    goConnect('cancelled');
  });

  const ERRORS = {
    'bad-pin': 'eBadPin', 'no-session': 'eNoSession', 'locked': 'eLocked', 'busy': 'eBusy', 'declined': 'eDeclined', 'expired': 'eExpired',
    'rate-limited': 'eRate', 'bad-request': 'eBadReq', 'not-joined': 'eNotJoined',
  };

  const END_REASONS = {
    operator: 'endOperator', host: 'endHost', timeout: 'endTimeout', error: 'endError', disconnect: 'endDisconnect', shutdown: 'endShutdown',
    declined: 'endDeclined', 'consent-timeout': 'endConsentTimeout', idle: 'endIdle', max: 'endMax', revoked: 'endRevoked', logout: 'endLogout',
  };
  T.endDeclined = T.eDeclined;
  function endKey(reason) { return END_REASONS[reason] || null; }

  // ---------- WebSocket ----------
  // 2026-10-27 (С-4): сначала одноразовый билет (POST /api/ws-ticket) — токен в адрес не попадает; сервер без билетов — запасной ?token=.
  async function ensureSocket(onReady) {
    if (st.ws && st.ws.readyState === WebSocket.OPEN) { onReady(); return; }
    closeSocket();
    const gen = ++st.wsGen;
    let query = '';
    try {
      const r = await api('/api/ws-ticket', { method: 'POST' });
      if (r.ok) query = 'ticket=' + encodeURIComponent((await r.json()).ticket);
    } catch (err) {
      if (err.message === 'unauthorized') return;
      if (gen === st.wsGen) goConnect('serverDown');
      return;
    }
    if (gen !== st.wsGen) return;
    if (!query) query = 'token=' + encodeURIComponent(st.auth.token);
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(`${proto}//${location.host}/ws/operator?${query}`);
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
    st.wsGen++;
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
      if (!st.endShown) showEnded('connLostTitle', 'connLostText');
      setLive(false);
      return;
    }
    if (st.phase === 'joining' || st.phase === 'waiting') {
      if (!wasOpened) {
        // Не открылось: токен истёк (401), сервер лежит (502/сеть) или он просто недоступен
        try {
          const r = await api('/api/me');
          goConnect(r.ok ? 'connFail' : 'serverDown');
        } catch (err) {
          if (err.message !== 'unauthorized' && st.auth) goConnect('serverDown');
        }
      } else {
        goConnect('connLost2');
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
        if (m.code === 'unauthorized') { logout('relogin'); break; } // вход отозван (смена пароля, выход в другом окне)
        if (st.phase === 'joining' || st.phase === 'waiting') {
          const key = ERRORS[m.code];
          if (m.code === 'bad-pin') {
            if (typeof m.left === 'number') goConnect('eBadPinLeft', { n: m.left }); else goConnect('eBadPin');
            pinInput.value = '';
            pinInput.focus();
          } else if (key) {
            goConnect(key);
          } else {
            goConnect('eGeneric', { x: String(m.code) });
          }
        }
        break;
      }
      case 'started':
        startViewer(m);
        break;
      case 'end':
        if (st.phase === 'viewer') {
          const k = endKey(m.reason);
          showEnded('endedTitle', k ? k : { key: 'endOther', params: { x: String(m.reason) } });
        } else if (st.phase === 'waiting' || st.phase === 'joining') {
          const k = endKey(m.reason);
          goConnect(k || 'connInterrupted');
        }
        break;
      case 'screen': onScreen(m); break;
      case 'cursor': onCursor(m); break;
      case 'stats': st.hostStats = m; break;
      case 'client-active': setClientActive(m.active); break; // 2026-10-26: необязательное; старая касса его не шлёт
      case 'screen-paused': setPaused(true); break;           // 2026-10-27: UAC / блокировка Windows — кадров нет, сеанс жив
      case 'view-only':
        // 2026-10-27 (В-2): сервер не пересылает ввод при «Только просмотр» кассира и сообщает оператору об этом
        if (m.value === true && m.cashier === true) setCashierViewOnly(true, true);
        break;
      default: break; // неизвестное — пропускаем (протокол может расширяться)
    }
  }

  // ---------- просмотр ----------
  let statsTimer = 0, pingTimer = 0;

  function machineLine() {
    const h = st.host;
    return [h.machine, h.version && ('v' + h.version), t('operatorLbl') + ': ' + (h.operator || '')].filter(Boolean).join(' · ');
  }

  function startViewer(m) {
    st.phase = 'viewer';
    st.endShown = false;
    st.ended = null;
    st.host = Object.assign({ company: '', machine: '', version: '', screen: { w: 0, h: 0 } }, m.host || {});
    st.host.operator = m.operator || '';
    st.viewOnly = false;
    st.cashierViewOnly = false;
    st.clientActive = false;
    st.paused = false;
    st.zoom = 1;
    st.actualSize = false;
    stage.classList.remove('actual');
    $('client-active').hidden = true;
    $('screen-paused').hidden = true;
    st.frameTiles = [];
    st.drawChain = Promise.resolve();
    st.gotFirstFrame = false;
    st.frames = 0; st.bytes = 0; st.rtt = null; st.hostStats = null;
    st.cursor = null;
    st.lastPoint = null;
    $('remote-cursor').hidden = true;
    $('vw-company').textContent = st.host.company || t('tillDefault');
    $('vw-machine').textContent = machineLine();
    $('tb-scale-label').textContent = t('fit');
    $('tb-quality').value = '';
    $('tb-monitor-wrap').hidden = true;
    $('ended').hidden = true;
    $('banner').hidden = true;
    $('stage-msg').hidden = false;
    $('stage-msg').textContent = t('firstFrame');
    syncViewOnlyUi();
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
    // 2026-10-27 (В-1): кассир выбрал «Только просмотр» — значок и выключенный ввод с первого кадра. Без поля — как раньше.
    if (m.viewOnly === true) setCashierViewOnly(true, false);
  }

  function setLive(ok) { $('live-dot').classList.toggle('bad', !ok); }

  function stopViewerTimers() {
    clearInterval(statsTimer); clearInterval(pingTimer);
    statsTimer = pingTimer = 0;
  }

  // 2026-10-27: «Только просмотр» от кассира. Кнопка оператора блокируется (логика cashierViewOnly из прежнего operator.js)
  function setCashierViewOnly(on, notify) {
    if (st.cashierViewOnly === on) return;
    st.cashierViewOnly = on;
    if (on) {
      st.viewOnly = true;
      clearTimeout(st.moveTimer);
      st.moveTimer = 0;
      st.pendingMove = null;
      releaseAll();
      $('text-dialog').hidden = true;
      if (notify) toast(t('voByCashier'), 4000);
    }
    syncViewOnlyUi();
  }
  function syncViewOnlyUi() {
    $('vo-badge').hidden = !st.cashierViewOnly;
    $('tb-viewonly').setAttribute('aria-pressed', String(st.viewOnly));
    $('tb-viewonly').disabled = st.cashierViewOnly;
    $('tb-text').disabled = st.cashierViewOnly;
    $('tb-keys').disabled = st.cashierViewOnly;
    canvas.classList.toggle('view-only', st.viewOnly);
    if (st.cashierViewOnly) closeKeysMenu();
  }

  function renderEnded() {
    const e = st.ended;
    $('ended-title').textContent = t(e.title);
    $('ended-text').textContent = typeof e.text === 'string' ? t(e.text) : t(e.text.key, e.text.params);
  }
  function showEnded(titleKey, text) {
    st.endShown = true;
    st.ended = { title: titleKey, text };
    $('client-active').hidden = true;
    $('screen-paused').hidden = true;
    releaseAll();
    stopViewerTimers();
    setLive(false);
    renderEnded();
    $('ended').hidden = false;
    $('text-dialog').hidden = true;
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    setTimeout(() => $('ended-ok').focus(), 0);
  }

  $('ended-ok').addEventListener('click', () => {
    closeSocket();
    st.phase = 'idle';
    st.ended = null;
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
    setPaused(false); // новый размер/монитор — значит экран снова доступен
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
          for (const tile of list) {
            if (!tile) continue;
            ctx2d.drawImage(tile.bmp, tile.x, tile.y, tile.w || tile.bmp.width, tile.h || tile.bmp.height);
            tile.bmp.close();
          }
          if (!st.gotFirstFrame) { st.gotFirstFrame = true; $('stage-msg').hidden = true; }
          // Кадр после «экран защищён» — картинка вернулась (кадры, отправленные до паузы, ещё могут догонять — 400 мс не считаем)
          if (st.paused && performance.now() - st.pausedAt > 400 && list.some(Boolean)) setPaused(false);
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
    stage.classList.toggle('actual', st.actualSize || st.zoom > 1);
    if (st.actualSize) {
      canvas.style.width = canvas.width + 'px';
      canvas.style.height = canvas.height + 'px';
    } else {
      const r = stage.getBoundingClientRect();
      const k = Math.min(r.width / canvas.width, r.height / canvas.height) * st.zoom;
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
      `${st.frames} ${t('fpsU')}`,
      `${kbps} ${t('kbpsU')}`,
      st.rtt == null ? `— ${t('msU')}` : `${st.rtt} ${t('msU')}`,
    ];
    $('stats').textContent = parts.join(' · ');
    $('stats').title = st.hostStats ? t('hostStatsTitle', { f: st.hostStats.fps ?? '—', k: st.hostStats.kbps ?? '—' }) : t('statsTitle');
    st.frames = 0;
    st.bytes = 0;
  }

  // ---------- ввод: мышь ----------
  // 2026-10-26: пока кассир работает сам (client-active), ввод оператора не отправляется — касса его всё равно не исполнит.
  function controlOn() { return st.phase === 'viewer' && !st.viewOnly && !st.endShown && !st.clientActive; }

  // Подпись над плашкой: ключи заголовка и подсказки
  function fillPlate(id, titleKey, hintKey) {
    const box = $(id);
    box.querySelector('b').textContent = t(titleKey);
    box.querySelector('span').textContent = t(hintKey);
  }

  function setClientActive(on) {
    on = on === true;
    if (st.clientActive === on) return;
    st.clientActive = on;
    const box = $('client-active');
    if (on) {
      fillPlate('client-active', 'caTitle', 'caHint');
      clearTimeout(st.moveTimer);
      st.moveTimer = 0;
      st.pendingMove = null;
      releaseAll();
    }
    box.hidden = !on || st.phase !== 'viewer' || st.endShown;
  }

  // 2026-10-27: экран кассы защищён Windows (UAC, блокировка) — сеанс жив, картинка вернётся сама
  function setPaused(on) {
    if (st.paused === on) return;
    st.paused = on;
    if (on) { st.pausedAt = performance.now(); fillPlate('screen-paused', 'pausedTitle', 'pausedHint'); }
    $('screen-paused').hidden = !on || st.phase !== 'viewer' || st.endShown;
  }

  function toHost(e) {
    const r = canvas.getBoundingClientRect();
    let x = Math.floor((e.clientX - r.left) * canvas.width / r.width);
    let y = Math.floor((e.clientY - r.top) * canvas.height / r.height);
    x = Math.max(0, Math.min(canvas.width - 1, x));
    y = Math.max(0, Math.min(canvas.height - 1, y));
    st.lastPoint = { x, y };
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

  // Мышь, синтезированная браузером после касания, игнорируем — касания обрабатываются ниже сами
  function fromTouch() { return performance.now() - st.lastTouchAt < 800; }

  canvas.addEventListener('mousemove', (e) => {
    if (fromTouch() || !controlOn()) return;
    st.pendingMove = toHost(e);
    const wait = 16 - (performance.now() - st.lastMoveAt); // ~60 в секунду
    if (wait <= 0) flushMove();
    else if (!st.moveTimer) st.moveTimer = setTimeout(flushMove, wait);
  });

  canvas.addEventListener('mousedown', (e) => {
    e.preventDefault();
    if (fromTouch()) return;
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

  // ---------- ввод: касание (2026-10-27) ----------
  // Тап — левый щелчок, долгое нажатие (550 мс) — правый щелчок, два пальца — масштаб (щипок), палец при увеличении — прокрутка.
  const touch = { sx: 0, sy: 0, moved: false, long: false, timer: 0, pinch: null };
  function cancelLong() { clearTimeout(touch.timer); touch.timer = 0; }
  function tapAt(p, button) {
    send({ type: 'mouse', action: 'move', x: p.x, y: p.y });
    send({ type: 'mouse', action: 'down', x: p.x, y: p.y, button });
    send({ type: 'mouse', action: 'up', x: p.x, y: p.y, button });
  }
  const dist = (a, b) => Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY);

  canvas.addEventListener('touchstart', (e) => {
    st.lastTouchAt = performance.now();
    closeKeysMenu();
    if (e.touches.length === 2) {
      cancelLong();
      touch.pinch = { d: dist(e.touches[0], e.touches[1]) || 1, zoom: st.zoom };
      return;
    }
    if (e.touches.length !== 1) return;
    const tc = e.touches[0];
    touch.sx = tc.clientX; touch.sy = tc.clientY; touch.moved = false; touch.long = false; touch.pinch = null;
    cancelLong();
    touch.timer = setTimeout(() => {
      touch.timer = 0;
      if (touch.moved || !controlOn()) return;
      touch.long = true;
      tapAt(toHost({ clientX: touch.sx, clientY: touch.sy }), 2);
    }, 550);
  }, { passive: true });

  canvas.addEventListener('touchmove', (e) => {
    st.lastTouchAt = performance.now();
    if (touch.pinch && e.touches.length === 2) {
      e.preventDefault();
      const cx = (stage.scrollLeft + stage.clientWidth / 2) / Math.max(1, canvas.offsetWidth);
      const cy = (stage.scrollTop + stage.clientHeight / 2) / Math.max(1, canvas.offsetHeight);
      st.zoom = Math.max(1, Math.min(6, touch.pinch.zoom * dist(e.touches[0], e.touches[1]) / touch.pinch.d));
      fit();
      stage.scrollLeft = cx * canvas.offsetWidth - stage.clientWidth / 2;
      stage.scrollTop = cy * canvas.offsetHeight - stage.clientHeight / 2;
      return;
    }
    if (e.touches.length === 1 && Math.hypot(e.touches[0].clientX - touch.sx, e.touches[0].clientY - touch.sy) > 10) {
      touch.moved = true;
      cancelLong();
    }
  }, { passive: false });

  function touchEnd(e) {
    st.lastTouchAt = performance.now();
    cancelLong();
    if (touch.pinch) {
      if (e.touches.length === 0) touch.pinch = null;
      return;
    }
    const wasTap = !touch.moved && !touch.long && e.type === 'touchend' && e.changedTouches.length === 1;
    if (wasTap && controlOn()) tapAt(toHost(e.changedTouches[0]), 0);
    if (e.cancelable && !touch.moved) e.preventDefault(); // без синтетических mousedown/click
  }
  canvas.addEventListener('touchend', touchEnd, { passive: false });
  canvas.addEventListener('touchcancel', touchEnd, { passive: false });

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
    // 2026-10-27: кнопки мыши отпускаются в последней известной точке, а не в (0,0) — иначе на кассе «отпускание» уходит в угол экрана
    const p = st.lastPoint || { x: 0, y: 0 };
    for (const b of Array.from(st.pressedButtons)) send({ type: 'mouse', action: 'up', x: p.x, y: p.y, button: b });
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

  // Почему ввод сейчас недоступен (для подсказки)
  function blockedText() {
    return st.clientActive ? t('caTitle') : st.cashierViewOnly ? t('voByCashier') : t('viewOnlyOn');
  }

  function sendText(text) {
    // 2026-10-27: диалог мог открыться раньше, чем кассир начал работать / выбрал «просмотр» — тогда ничего не отправляем и не обещаем
    if (!controlOn()) { toast(blockedText()); return; }
    releaseModifiers();
    const s = text.replace(/\r\n/g, '\n').slice(0, 10000);
    send({ type: 'text', text: s });
    toast(t('sentChars', { n: s.length }), 2000);
  }

  window.addEventListener('blur', () => { if (st.phase === 'viewer') releaseAll(); });
  document.addEventListener('visibilitychange', () => { if (document.hidden && st.phase === 'viewer') releaseAll(); });

  // ---------- панель ----------
  $('tb-viewonly').addEventListener('click', () => {
    if (st.cashierViewOnly) { toast(t('voByCashier')); return; }
    st.viewOnly = !st.viewOnly;
    if (st.viewOnly) releaseAll();
    syncViewOnlyUi();
    send({ type: 'view-only', value: st.viewOnly });
    toast(st.viewOnly ? t('voOnToast') : t('ctrlOn'), 2500);
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
    // «Вписать» всегда сбрасывает и масштаб пальцами
    if (st.zoom > 1) { st.zoom = 1; fit(); canvas.focus(); return; }
    st.actualSize = !st.actualSize;
    stage.classList.toggle('actual', st.actualSize);
    $('tb-scale-label').textContent = st.actualSize ? '100%' : t('fit');
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
          navigator.keyboard.lock().then(() => toast(t('fsLock'), 3500)).catch(() => {});
        }
      }
    } catch { toast(t('fsDenied')); }
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
      toast(t('cadToast'), 7000);
      canvas.focus();
      return;
    }
    if (!controlOn()) { toast(blockedText()); return; }
    const codes = b.dataset.combo.split(',');
    releaseAll();
    for (const c of codes) send({ type: 'key', action: 'down', code: c, key: '' });
    for (const c of codes.slice().reverse()) send({ type: 'key', action: 'up', code: c, key: '' });
    canvas.focus();
  });

  // Текст
  $('tb-text').addEventListener('click', () => {
    if (!controlOn()) { toast(blockedText()); return; }
    releaseAll();
    $('text-dialog').hidden = false;
    $('text-input').value = '';
    setTimeout(() => $('text-input').focus(), 0);
  });
  $('text-cancel').addEventListener('click', () => { $('text-dialog').hidden = true; canvas.focus(); });
  $('text-form').addEventListener('submit', (e) => {
    e.preventDefault();
    const v = $('text-input').value;
    $('text-dialog').hidden = true;
    if (v) sendText(v);
    canvas.focus();
  });

  $('tb-end').addEventListener('click', () => {
    releaseAll();
    send({ type: 'end', reason: 'operator' });
    showEnded('endedTitle', 'endOperator');
  });

  // Интернет пропал/вернулся
  window.addEventListener('offline', () => {
    if (st.phase !== 'viewer') return;
    const b = $('banner');
    b.textContent = t('offlineBanner');
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
  const STATE_LABELS = { pending: ['stPending', ''], 'awaiting-consent': ['stAwait', 'pill-warn'], active: ['stActive', 'pill-ok'] };
  const REASON_LABELS = {
    operator: ['rOperator', 'pill-ok'],
    host: ['rHost', 'pill-ok'],
    timeout: ['rTimeout', 'pill-warn'],
    error: ['rError', 'pill-bad'],
    disconnect: ['rDisconnect', 'pill-warn'],
    shutdown: ['rShutdown', 'pill-warn'],
    declined: ['rDeclined', 'pill-bad'],
    'consent-timeout': ['rConsentTimeout', 'pill-warn'],
    locked: ['rLocked', 'pill-bad'],
    expired: ['rExpired', ''],
    idle: ['rIdle', 'pill-warn'],
    max: ['rMax', 'pill-warn'],
    revoked: ['rRevoked', 'pill-bad'],
    logout: ['rLogout', ''],
  };

  function fmtDate(s) {
    if (!s) return '—';
    const d = new Date(s);
    return isNaN(d) ? '—' : d.toLocaleString(LANG_LOCALE[lang], { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }
  function fmtDuration(sec) {
    sec = Math.max(0, Math.round(sec || 0));
    const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
    if (h) return `${h} ${t('unitH')} ${m} ${t('unitM')}`;
    if (m) return `${m} ${t('unitM')} ${s} ${t('unitS')}`;
    return `${s} ${t('unitS')}`;
  }
  function cell(tr, text, pill) {
    const td = document.createElement('td');
    if (pill) {
      const span = document.createElement('span');
      span.className = 'pill ' + pill[1];
      span.textContent = pill[0];
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
  function labelPill(map, key) {
    const e = map[key];
    return e ? [t(e[0]), e[1]] : [String(key ?? '—'), ''];
  }

  function renderJournal() {
    const data = st.journal;
    if (!data) return;
    const at = document.querySelector('#active-table tbody');
    at.innerHTML = '';
    if (!data.active.length) emptyRow(at, 7, t('jEmptyNow'));
    for (const s of data.active) {
      const tr = document.createElement('tr');
      cell(tr, null, labelPill(STATE_LABELS, s.state));
      cell(tr, s.company); cell(tr, s.machine); cell(tr, s.version); cell(tr, s.hostIp);
      cell(tr, s.operatorName || s.operator);
      cell(tr, fmtDate(s.start || s.created));
      at.appendChild(tr);
    }
    const rt = document.querySelector('#recent-table tbody');
    rt.innerHTML = '';
    if (!data.recent.length) emptyRow(rt, 8, t('jEmptyRecent'));
    for (const s of data.recent) {
      const tr = document.createElement('tr');
      cell(tr, fmtDate(s.start || s.created));
      cell(tr, s.operatorName ? `${s.operatorName} (${s.operator})` : s.operator);
      cell(tr, s.company); cell(tr, s.machine); cell(tr, s.version); cell(tr, s.hostIp);
      cell(tr, s.start ? fmtDuration(s.durationSec) : '—');
      cell(tr, null, labelPill(REASON_LABELS, s.reason));
      rt.appendChild(tr);
    }
  }

  async function openJournal() {
    show('journal');
    setErr('journal-error', '');
    try {
      const res = await api('/api/sessions?limit=200');
      if (!res.ok) throw new Error(String(res.status));
      st.journal = await res.json();
      renderJournal();
    } catch (err) {
      if (err.message !== 'unauthorized') setErr('journal-error', 'jLoadFail');
    }
  }
  $('journal-refresh').addEventListener('click', openJournal);

  // ---------- старт ----------
  // 2026-10-27: форма входа видна сразу (не белый экран), пока идёт проверка токена; при любом сбое /api/me — вход с понятным сообщением
  applyTheme();
  buildLangUi();
  applyStatic();
  async function boot() {
    st.auth = loadAuth();
    if (!st.auth) { logout(''); return; }
    const ctl = new AbortController();
    const timer = setTimeout(() => ctl.abort(), 8000);
    try {
      const r = await fetch('/api/me', { headers: { Authorization: 'Bearer ' + st.auth.token }, signal: ctl.signal });
      if (r.ok) enterApp();
      else if (r.status === 401) logout('relogin');
      else { show('login'); setErr('login-error', 'serverDown'); }
    } catch {
      show('login');
      setErr('login-error', 'serverDown');
    } finally {
      clearTimeout(timer);
    }
  }
  boot();
})();
