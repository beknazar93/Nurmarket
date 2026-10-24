// ignore: unused_import
import 'package:intl/intl.dart' as intl;

import 'app_localizations.dart';

// ignore_for_file: type=lint

/// The translations for Russian (`ru`).
class AppLocalizationsRu extends AppLocalizations {
  AppLocalizationsRu([String locale = 'ru']) : super(locale);

  @override
  String get som => 'сом';

  @override
  String get cancel => 'Отмена';

  @override
  String get done => 'Готово';

  @override
  String get save => 'Сохранить';

  @override
  String get retry => 'Повторить';

  @override
  String get all => 'Все';

  @override
  String get loginTagline => 'Тот же вход, что на кассе и в вебе';

  @override
  String get loginLabel => 'Логин или телефон';

  @override
  String get loginHint => 'почта или телефон';

  @override
  String get passwordLabel => 'Пароль';

  @override
  String get signIn => 'Войти';

  @override
  String get loginEmpty => 'Введите логин и пароль.';

  @override
  String get loginWrong => 'Неверный логин или пароль.';

  @override
  String get chooseCashbox => 'Выберите кассу';

  @override
  String get cashbox => 'Касса';

  @override
  String get tabHome => 'Главная';

  @override
  String get tabSale => 'Продажа';

  @override
  String get tabScan => 'Сканер';

  @override
  String get tabChecks => 'Чеки';

  @override
  String get tabStock => 'Склад';

  @override
  String get newCheck => 'Новый чек';

  @override
  String get scanTileSub => 'Цена и остаток по штрихкоду';

  @override
  String get checksTileSub => 'Отправить, напечатать';

  @override
  String get stockTileSub => 'Остатки и приёмка';

  @override
  String shiftOpenSince(String time) {
    return 'Смена открыта · $time';
  }

  @override
  String get shiftClosed => 'Смена закрыта';

  @override
  String get mySalesForShift => 'Мои продажи за смену';

  @override
  String checksCount(int n) {
    return '$n чеков';
  }

  @override
  String get openShift => 'Открыть смену';

  @override
  String get closeShift => 'Закрыть смену';

  @override
  String get openingCashHint => 'Сколько денег в кассе на начало';

  @override
  String get openShiftFirst => 'Сначала откройте смену на главной.';

  @override
  String get offlineWorking =>
      'Нет связи с сервером — продажи сохраняются на телефоне и уйдут сами.';

  @override
  String runningOut(int n) {
    return 'Заканчиваются $n товаров';
  }

  @override
  String get runningOutShort => 'Заканчиваются';

  @override
  String get outOfStock => 'Нет в наличии';

  @override
  String get tellAdmin => 'Сообщите администратору';

  @override
  String get searchProduct => 'Товар или штрихкод';

  @override
  String get favorites => 'Избранное';

  @override
  String get nothingFound => 'Ничего не найдено';

  @override
  String get inCheck => 'В чеке';

  @override
  String positions(int n) {
    return '$n позиций';
  }

  @override
  String get swipeToRemove => 'Смахните строку влево, чтобы убрать';

  @override
  String payAmount(String sum) {
    return 'Оплатить $sum сом';
  }

  @override
  String get orderDiscount => 'Скидка на чек';

  @override
  String get discountPercentHint => 'Процент, например 10';

  @override
  String get extraService => 'Доп. услуга';

  @override
  String get clearCheck => 'Очистить чек';

  @override
  String get price => 'Цена';

  @override
  String weightOf(String name) {
    return 'Вес: $name';
  }

  @override
  String get kg => 'килограммы, например 0,5';

  @override
  String get lineDiscount => 'Скидка на товар';

  @override
  String get sumHint => 'Сумма в сомах';

  @override
  String get discount => 'Скидка';

  @override
  String barcodeNotFound(String code) {
    return 'Штрихкод $code не найден в каталоге.';
  }

  @override
  String get cameraUnavailable =>
      'Камера недоступна. Разрешите доступ к камере в настройках телефона или введите штрихкод вручную.';

  @override
  String get scanHint => 'Наведите на штрихкод — товар добавится сам';

  @override
  String scanReceiveHint(int n) {
    return 'Отсканировано: $n';
  }

  @override
  String get toCheck => 'В чек';

  @override
  String get checkPrice => 'Узнать цену';

  @override
  String get receiving => 'Приёмка';

  @override
  String doneCount(int n) {
    return 'Готово · $n';
  }

  @override
  String inStock(String qty, String unit) {
    return 'на складе $qty $unit';
  }

  @override
  String get scanContinuous => 'Сканировать подряд, без подтверждения';

  @override
  String checkN(int n) {
    return 'Чек · $n';
  }

  @override
  String get addToCheck => 'Добавить в чек';

  @override
  String get addedAutomatically => 'Добавлено в чек';

  @override
  String get profileTitle => 'Профиль';

  @override
  String get discountInPercent => 'Процент';

  @override
  String get discountInSom => 'Сумма, сом';

  @override
  String discountResult(String sum) {
    return 'Итого со скидкой: $sum сом';
  }

  @override
  String get clothingMode => 'Размеры и цвета при продаже';

  @override
  String get clothingModeSub => 'Для магазина одежды и обуви';

  @override
  String get chooseSize => 'Размер и цвет';

  @override
  String get colorLabel => 'Цвет';

  @override
  String get sizeLabel => 'Размер';

  @override
  String toCheckSize(String size) {
    return 'В чек · $size';
  }

  @override
  String sizeLowStock(String qty) {
    return 'Остаток этого размера — $qty шт. Всё равно добавить?';
  }

  @override
  String get rentals => 'Прокат';

  @override
  String get rentalNew => 'Новый прокат';

  @override
  String get rentalActive => 'На руках';

  @override
  String get rentalOverdue => 'Просрочен';

  @override
  String get rentalReturn => 'Принять возврат';

  @override
  String get rentalReturned => 'Возврат принят.';

  @override
  String get rentalCondOk => 'Целое';

  @override
  String get rentalCondDamaged => 'Повреждено';

  @override
  String get rentalPenalty => 'Штраф из залога';

  @override
  String get rentalClient => 'Клиент';

  @override
  String get rentalItem => 'Вещь';

  @override
  String get rentalUntil => 'До даты';

  @override
  String get rentalPerDay => 'Цена за сутки';

  @override
  String rentalDays(int n) {
    return '$n сут.';
  }

  @override
  String get rentalDeposit => 'Залог деньгами';

  @override
  String get rentalDocument => 'Залог документом';

  @override
  String get rentalDocumentHint => 'Паспорт ID…, ФИО';

  @override
  String get rentalCreate => 'Выдать и в чек';

  @override
  String rentalCreated(int n) {
    return 'Прокат №$n оформлен — стоимость в чеке.';
  }

  @override
  String rentalLine(int n, String days) {
    return 'Прокат №$n · $days сут.';
  }

  @override
  String get chooseClient => 'Выбрать клиента';

  @override
  String get chooseItem => 'Выберите вещь';

  @override
  String get marginPercent => 'Наценка, %';

  @override
  String get marginSum => 'Наценка, сом';

  @override
  String get kindProduct => 'Товар';

  @override
  String get kindClothes => 'Одежда';

  @override
  String get kindService => 'Услуга';

  @override
  String get sizesLabel => 'Размеры';

  @override
  String get colorsLabel => 'Цвета';

  @override
  String get addColor => 'Добавить цвет';

  @override
  String get colorName => 'Название цвета';

  @override
  String get colorSizeGrid => 'Цвет × размер — сколько штук';

  @override
  String variantsSummary(int n, String qty) {
    return '$n вариантов · $qty шт';
  }

  @override
  String variantsSaved(int n) {
    return 'Сохранено вариантов: $n';
  }

  @override
  String get durationLabel => 'Длительность';

  @override
  String get mastersLabel => 'Мастера · своя цена и процент';

  @override
  String get masterAdd => 'Добавить мастера';

  @override
  String get masterName => 'Имя мастера';

  @override
  String get masterPercent => '% мастеру';

  @override
  String get consumablesLabel => 'Расходники со склада';

  @override
  String get consumableAdd => 'Добавить расходник';

  @override
  String get onlineBooking => 'Онлайн-запись на сайте и в боте';

  @override
  String get serviceLocalNote =>
      'Мастера, длительность и расходники хранятся на этом телефоне, пока у сервера нет API услуг. Сама услуга — в каталоге NurCRM.';

  @override
  String get saveService => 'Сохранить услугу';

  @override
  String get findInternet => 'Найти в интернете';

  @override
  String get internetFound => 'Найдено в базе NurCRM';

  @override
  String get barcodeMatched => 'штрихкод совпал · точно';

  @override
  String get takeLabel => 'Взять';

  @override
  String get categoryLabel => 'Категория';

  @override
  String get brandLabel => 'Бренд';

  @override
  String get fillProduct => 'Заполнить товар';

  @override
  String get searchInBrowser => 'Искать в браузере';

  @override
  String get priceNotTaken => 'Цена из интернета не берётся — укажите свою.';

  @override
  String get bookingsTitle => 'Услуги · запись';

  @override
  String get dayToday => 'Сегодня';

  @override
  String get dayTomorrow => 'Завтра';

  @override
  String get allMasters => 'Все мастера';

  @override
  String get bookNew => 'Записать';

  @override
  String get bookCheckout => 'Рассчитать';

  @override
  String get bookArrived => 'пришёл';

  @override
  String get bookBooked => 'записан';

  @override
  String get bookPaid => 'оплачено';

  @override
  String get bookFree => 'свободно';

  @override
  String get bookMarkArrived => 'Отметить «пришёл»';

  @override
  String get bookRemind => 'Напомнить в WhatsApp';

  @override
  String get bookCancel => 'Отменить запись';

  @override
  String get bookTime => 'Время';

  @override
  String get bookClash => 'У мастера в это время уже есть запись.';

  @override
  String get bookNoMasters =>
      'Мастеров пока нет — добавьте их при создании услуги («Новая позиция» → «Услуга»).';

  @override
  String get bookLocalNote =>
      'Записи хранятся на этом телефоне, пока у сервера нет API записи. Оплата услуг — обычный чек NurCRM.';

  @override
  String bookReminder(String name, String when, String master) {
    return 'Здравствуйте, $name! Напоминаем о записи $when к мастеру $master.';
  }

  @override
  String get pricesTitle => 'Цены продажи';

  @override
  String pricesSub(int a, int b) {
    return 'закупка изменилась: $a · новых: $b';
  }

  @override
  String get ruleLabel => 'Правило';

  @override
  String get roundTo => 'Округлять до';

  @override
  String get applyToAll => 'Ко всем строкам, не только изменённым';

  @override
  String costWas(String was, String now) {
    return 'закупка $was → $now';
  }

  @override
  String get newItemShort => 'новый';

  @override
  String get applyPrices => 'Применить цены';

  @override
  String postSummary(int a, String qty, int b, int n, String sum) {
    return 'Позиций: $a · штук: $qty\nНовых товаров: $b\nЦен продажи изменится: $n\nЗакупка: $sum';
  }

  @override
  String get quotesTitle => 'КП и счета';

  @override
  String get quoteNew => 'Коммерческое предложение';

  @override
  String get invoiceNew => 'Счёт на оплату';

  @override
  String get quotesWaiting => 'Ждут ответа';

  @override
  String get invoicesUnpaid => 'Не оплачены';

  @override
  String get invoicesOverdue => 'Просрочено';

  @override
  String get filterAll => 'Все';

  @override
  String get filterQuotes => 'КП';

  @override
  String get filterInvoices => 'Счета';

  @override
  String get stDraft => 'Черновик';

  @override
  String get stSent => 'Отправлено';

  @override
  String get stAccepted => 'Принято → счёт';

  @override
  String get stDeclined => 'Отклонено';

  @override
  String get stIssued => 'Ждёт оплаты';

  @override
  String get stPartial => 'Оплачен частично';

  @override
  String get stPaid => 'Оплачен';

  @override
  String validUntil(String date) {
    return 'Действует до $date';
  }

  @override
  String payUntil(String date) {
    return 'Оплатить до $date';
  }

  @override
  String get fromCart => 'Из текущего чека';

  @override
  String get useWholesale => 'Оптовые цены';

  @override
  String get innLabel => 'ИНН';

  @override
  String get docSendPdf => 'Отправить PDF';

  @override
  String get invoiceFromQuote => 'Выставить счёт по КП';

  @override
  String get takePayment => 'Принять оплату';

  @override
  String get sellFromDoc => 'Провести продажу';

  @override
  String get markDeclined => 'Клиент отказался';

  @override
  String get editDoc => 'Изменить';

  @override
  String get deleteDoc => 'Удалить документ';

  @override
  String paidOf(String paid, String total) {
    return 'оплачено $paid из $total';
  }

  @override
  String basedOnQuote(String number) {
    return 'по $number';
  }

  @override
  String get quotesLocalNote =>
      'КП и счета общие с программой владельца на ПК и другими телефонами компании. Продажа по счёту — обычный чек NurCRM.';

  @override
  String get addLine => 'Добавить товар';

  @override
  String get docDiscount => 'Скидка, сом';

  @override
  String get esfTitle => 'ККМ и ЭСФ';

  @override
  String esfQueue(int n) {
    return 'Чеков в очереди: $n — уйдут, когда появится связь';
  }

  @override
  String get esfOnline => 'Касса на связи, очередь пуста';

  @override
  String get esfIssue => 'Выписать ЭСФ';

  @override
  String get esfBuyerInn => 'ИНН покупателя';

  @override
  String get esfBuyerName => 'Покупатель';

  @override
  String get esfPayForm => 'Форма оплаты';

  @override
  String get esfCash => 'Наличная';

  @override
  String get esfNonCash => 'Безналичная';

  @override
  String get esfContract => 'Договор № и дата';

  @override
  String get esfSum => 'Сумма, сом';

  @override
  String get esfDraftSaved => 'Черновик ЭСФ сохранён';

  @override
  String get esfSendTax => 'Отправить в налоговую';

  @override
  String get esfDrafts => 'Черновики ЭСФ';

  @override
  String get esfIncoming => 'Входящие ЭСФ от поставщиков';

  @override
  String get esfStub =>
      'Отправка в налоговую (ЭСФ ГНС КР) и входящие ЭСФ заработают, когда сервер NurCRM подключит налоговую. Сейчас можно готовить черновики — данные покупателя и суммы сохранятся.';

  @override
  String get smartCam => 'Умная камера';

  @override
  String get byLook => 'По виду';

  @override
  String get byLookHint =>
      'Снимите товар без штрихкода — ИИ узнает его и найдёт в каталоге';

  @override
  String get recognizing => 'Узнаю товар…';

  @override
  String get recognizedNone => 'Не узнал товар — снимите ближе и при свете';

  @override
  String get seenOnPhoto => 'На снимке';

  @override
  String get notInCatalog => 'нет в каталоге';

  @override
  String get aiKeyNeeded =>
      'Для умной камеры нужен ключ ИИ (Gemini) — тот же, что у Нур Советника на кассе. Его можно вставить здесь.';

  @override
  String get aiKeyLabel => 'Ключ Gemini';

  @override
  String get smartShelf => 'Полка';

  @override
  String get smartInvoice => 'Накладная';

  @override
  String get smartLabels => 'Этикетки';

  @override
  String get smartHint =>
      'Держите телефон ровно — камера ищет товары и ценники';

  @override
  String get takePhoto => 'Сделать снимок';

  @override
  String get morePhoto => 'Ещё снимок';

  @override
  String seenInBase(int n) {
    return 'Есть в базе · $n';
  }

  @override
  String seenNew(int n) {
    return 'Новых · $n';
  }

  @override
  String smartNext(int n) {
    return 'Далее — в приёмку · $n';
  }

  @override
  String get bulkImport => 'Массовая загрузка';

  @override
  String get bulkPickFile => 'Выбрать файл Excel или CSV';

  @override
  String get bulkPaste => 'Вставить скопированное из Excel';

  @override
  String get bulkTemplate => 'Шаблон файла';

  @override
  String get bulkHint =>
      'Столбцы: Название · Штрихкод · Цена продажи · Закупка · Количество · Единица · Категория. Первая строка — заголовки (можно в любом порядке). Товар с тем же штрихкодом не дублируется: прибавится количество и обновятся цены.';

  @override
  String bulkSummary(int n, int a, int b) {
    return 'Строк: $n · новых $a · уже есть $b';
  }

  @override
  String get bulkBad => 'пропущено: нет названия или цены';

  @override
  String get bulkExists => 'уже есть — прибавится';

  @override
  String bulkUpload(int n) {
    return 'Загрузить $n';
  }

  @override
  String bulkDone(int n) {
    return 'Загружено: $n';
  }

  @override
  String get bulkNothing => 'В файле не нашлось строк с товарами';

  @override
  String get notFoundTitle => 'Товара нет в каталоге';

  @override
  String notFoundAsk(String code) {
    return 'Штрихкод $code. Добавить новый товар с этим штрихкодом?';
  }

  @override
  String get addNewProduct => 'Добавить товар';

  @override
  String get supplierLabel => 'Поставщик';

  @override
  String get supplierDiscount => 'Скидка поставщика, %';

  @override
  String costWithDiscount(String sum) {
    return 'Закупка со скидкой: $sum';
  }

  @override
  String get supplierReceiptNote =>
      'Количество придёт приходом от поставщика — в NurCRM видно, у кого и почём купили.';

  @override
  String get retailPrice => 'Розничная цена';

  @override
  String get altBarcodesLabel => 'Доп. штрихкоды';

  @override
  String get altBarcodesHint =>
      'Другой штрихкод того же товара или упаковки: у коробки укажите, сколько в ней штук — скан коробки добавит их все.';

  @override
  String get altBarcodeAdd => 'Добавить штрихкод';

  @override
  String get packQty => 'Штук';

  @override
  String get packName => 'Упаковка';

  @override
  String get photosLabel => 'Фото товара';

  @override
  String get photoCamera => 'Камера';

  @override
  String get photoGallery => 'Галерея';

  @override
  String get photoInternet => 'Из интернета';

  @override
  String get photosSearching => 'Ищу фото…';

  @override
  String get photoFirstMain => 'Первое фото — главное';

  @override
  String get descriptionLabel => 'Описание — для сайта и бота';

  @override
  String get descriptionFind => 'Найти описание в интернете';

  @override
  String get descriptionWriting => 'Пишу описание…';

  @override
  String get lowStockPull => '↑ Потяните вверх — что заканчивается';

  @override
  String lowStockTitle(int n) {
    return 'Заканчивается · $n';
  }

  @override
  String get lowStockEmpty => 'Всего хватает — остатки в норме';

  @override
  String get summaryTitle => 'Сводка';

  @override
  String get periodToday => 'Сегодня';

  @override
  String get periodWeek => 'Неделя';

  @override
  String get periodMonth => 'Месяц';

  @override
  String get kpiRevenue => 'Выручка';

  @override
  String get kpiChecks => 'Чеков';

  @override
  String get kpiAvg => 'Средний чек';

  @override
  String get kpiProfit => 'Прибыль';

  @override
  String get revenueByDay => 'Выручка по дням';

  @override
  String get paymentMethods => 'Способы оплаты';

  @override
  String get topProducts => 'Лучшие товары';

  @override
  String get noSalesPeriod => 'За этот период продаж нет';

  @override
  String get abcTitle => 'ABC-анализ';

  @override
  String get abcByRevenue => 'По выручке';

  @override
  String get abcByProfit => 'По прибыли';

  @override
  String get abcByQty => 'По штукам';

  @override
  String get abcA => 'Кормят магазин';

  @override
  String get abcB => 'Середина';

  @override
  String get abcC => 'Хвост';

  @override
  String abcGroupNote(int n, String share) {
    return '$n поз. · $share %';
  }

  @override
  String get paretoTitle => 'Диаграмма Парето';

  @override
  String get paretoHint =>
      'Столбцы — вклад товара, линия — накопленная доля. Товары левее отметки 80 % — группа A.';

  @override
  String get advisorTitle => 'Нур Советник';

  @override
  String get advisorOnline => 'Данные магазина подключены';

  @override
  String get advisorAsk => 'Спросите о своём магазине…';

  @override
  String get advisorThinking => 'Советник думает…';

  @override
  String get advisorQ1 => 'Сколько продали вчера?';

  @override
  String get advisorQ2 => 'Кто должен?';

  @override
  String get advisorQ3 => 'Что заказать?';

  @override
  String get advisorQ4 => 'Как прошла неделя?';

  @override
  String get advisorNew => 'Новый разговор';

  @override
  String get advisorNoKey =>
      'Добавьте ключ ИИ в программе владельца: Нур Советник → ⚙ → Модели ИИ и ключи';

  @override
  String get advisorStageData => 'Собираю данные магазина…';

  @override
  String get advisorStageAsk => 'Спрашиваю ИИ…';

  @override
  String advisorError(String error) {
    return 'Советник не ответил: $error';
  }

  @override
  String get callTitle => 'Разговор';

  @override
  String get callConnecting => 'Подключаюсь…';

  @override
  String get callPreparing => 'Готовлю данные магазина…';

  @override
  String get callListening => 'Слушаю…';

  @override
  String get callThinking => 'Думаю…';

  @override
  String get callSpeaking => 'Говорю… (нажмите, чтобы перебить)';

  @override
  String get callEnded => 'Звонок завершён';

  @override
  String get callEnd => 'Завершить';

  @override
  String get callMicOff => 'Микрофон выкл.';

  @override
  String get callMicOn => 'Микрофон вкл.';

  @override
  String get callSpeakerOn => 'Громкая связь';

  @override
  String get callSpeakerOff => 'Без громкой связи';

  @override
  String callCost(int n) {
    return 'Звонок длится до $n мин и расходует лимит ключа ИИ быстрее, чем чат.';
  }

  @override
  String get callLimitSoon => 'Через минуту звонок завершится';

  @override
  String callEndedLimit(int n) {
    return 'Звонок завершён: прошло $n мин. Можно позвонить снова.';
  }

  @override
  String get callEndedFocus =>
      'Звонок прерван: входящий звонок или приложение свёрнуто.';

  @override
  String callFailed(String error) {
    return 'Звонок не удался: $error';
  }

  @override
  String get callNoMic =>
      'Нет доступа к микрофону. Разрешите микрофон для NurMarket в настройках телефона.';

  @override
  String get callOpenSettings => 'Открыть настройки';

  @override
  String get callUnsupported =>
      'Голосовой звонок работает на телефоне Android.';

  @override
  String get callTableReady => 'Таблица с цифрами будет в чате после звонка';

  @override
  String get callClose => 'Закрыть';

  @override
  String get callYou => 'Вы';

  @override
  String checkTab(int n) {
    return 'Чек · $n';
  }

  @override
  String get deleteProduct => 'Удалить товар';

  @override
  String deleteProductAsk(String name) {
    return 'Удалить «$name»? Продажи и отчёты по нему останутся.';
  }

  @override
  String get productDeleted => 'Товар удалён';

  @override
  String smartUpload(int n) {
    return 'Загрузить $n товаров на склад';
  }

  @override
  String get smartToReceive => 'В приёмку с поставщиком';

  @override
  String smartShots(int n) {
    return 'Снимков: $n';
  }

  @override
  String get smartPriceRule => 'Цена продажи новых: +30 % к закупке, до 5 сом';

  @override
  String smartUploaded(int n) {
    return 'Загружено на склад: $n';
  }

  @override
  String get sellingPrice => 'Продажная цена';

  @override
  String get durationCustom => 'Своя…';

  @override
  String get minutesHint => 'минут';

  @override
  String get photoLabel => 'Фото';

  @override
  String get shiftDetails => 'Отчёт смены';

  @override
  String get shOpened => 'Открыта';

  @override
  String get shClosed => 'Закрыта';

  @override
  String get shOpeningCash => 'В кассе на начало';

  @override
  String get shNonCash => 'Безнал';

  @override
  String get shDebt => 'В долг';

  @override
  String get shReturns => 'Возвраты';

  @override
  String get shIncome => 'Прочий приход';

  @override
  String get shExpected => 'Должно быть в кассе';

  @override
  String get shCounted => 'Пересчитано';

  @override
  String get shDifference => 'Разница';

  @override
  String orderReadyNotify(String name, String num, String sum) {
    return 'Здравствуйте, $name! Ваш заказ №$num готов к выдаче. К оплате $sum сом. Ждём вас!';
  }

  @override
  String get customSize => 'Свой размер';

  @override
  String get pickSupplier => 'Выбрать поставщика';

  @override
  String get addProductsHint => 'Найдите товар и нажмите, чтобы добавить';

  @override
  String get removeAction => 'Убрать';

  @override
  String get esfExportCsv => 'Выгрузить в Excel (CSV)';

  @override
  String get esfExportAll => 'Все черновики в Excel (CSV)';

  @override
  String get esfTaxSoon =>
      'Отправка в налоговую заработает, когда сервер NurCRM подключит ЭСФ ГНС. Пока выгрузите в Excel и загрузите на сайте ГНС.';

  @override
  String get dateLabel => 'Дата';

  @override
  String get supplierNoLow => 'У этого поставщика нет заканчивающихся товаров';

  @override
  String get supplierLowAdd => 'Добавить заканчивающиеся товары поставщика';

  @override
  String get inStockOnly => 'В наличии';

  @override
  String get allCategories => 'Все категории';

  @override
  String get catalogPick => 'Из каталога';

  @override
  String get themeAuto => 'По времени';

  @override
  String get darkFrom => 'Тёмная с';

  @override
  String get darkTo => 'до';

  @override
  String get leaveAsk => 'Вы точно хотите выйти?';

  @override
  String get leaveAskSub => 'Несохранённые изменения пропадут.';

  @override
  String get leaveYes => 'Да, конечно';

  @override
  String get leaveNo => 'Нет, случайно нажал';

  @override
  String get aiSettings => 'Настройка ИИ';

  @override
  String get aiSettingsHint =>
      'Нур Советник отвечает через Gemini. Если у Gemini кончился бесплатный лимит — через Groq, затем OpenRouter. Ключи бесплатные; сохраняются в NurCRM и работают на телефоне, кассе и в программе владельца.';

  @override
  String get aiConnected => 'подключён';

  @override
  String get aiNoKeyShort => 'нет ключа';

  @override
  String get aiGetKey => 'Получить ключ';

  @override
  String get aiTest => 'Проверить';

  @override
  String get aiTestOk => 'Работает ✓';

  @override
  String get aiKeysSaved => 'Ключи сохранены';

  @override
  String get aiPasteKey => 'Вставьте новый ключ';

  @override
  String get aiRemoveKey => 'Убрать ключ';

  @override
  String get payQrTitle => 'QR для оплаты';

  @override
  String get payQrHint =>
      'Добавьте QR своих банков (MBank, Optima и др.). Покупатель сканирует QR с суммой чека и платит в приложении банка. Укажите, каким кассирам какой QR показывать — у кассира на выбор до 5 банков. Список общий для всех телефонов магазина.';

  @override
  String get payQrAdd => 'Добавить QR';

  @override
  String get payQrBank => 'Банк';

  @override
  String get payQrCode => 'QR банка';

  @override
  String get payQrScan => 'Сканировать';

  @override
  String get payQrGallery => 'Из галереи (скриншот)';

  @override
  String get payQrPaste => 'Вставить ссылку';

  @override
  String get payQrDynamic => 'Сумма чека в QR';

  @override
  String get payQrDynamicSub =>
      'Покупателю не нужно набирать сумму (стандарт ELQR, как на кассе; проверено на MBank). Если банк покупателя не примет — выключите.';

  @override
  String get payQrStaticOnly =>
      'Этот QR без стандарта ELQR — сумма не вписывается, покажется как есть';

  @override
  String get payQrCashiers => 'Кому показывать';

  @override
  String get payQrAllCashiers => 'Всем кассирам';

  @override
  String get payQrNotRecognized => 'QR не распознан';

  @override
  String get payQrShowCustomer =>
      'Покажите покупателю (нажмите — крупно). После оплаты нажмите «Оплатить».';

  @override
  String get payQrNone =>
      'QR для оплаты не добавлены — владелец добавляет в «Управление → QR для оплаты».';

  @override
  String get payQrNoneOwner => 'Пока ни одного QR. Нажмите «Добавить QR».';

  @override
  String get payQrSaved => 'QR сохранены — у кассиров появятся сразу';

  @override
  String get payQrDelete => 'Удалить QR';

  @override
  String get grpAi => 'ИИ и аналитика';

  @override
  String get grpStock => 'Товары и склад';

  @override
  String get grpMoney => 'Продажи и деньги';

  @override
  String get grpClients => 'Клиенты и документы';

  @override
  String get grpTeam => 'Сотрудники';

  @override
  String get findSection => 'Найти раздел';

  @override
  String get liveAuto => 'Авто';

  @override
  String get liveHintSale => 'Наведите на товар и нажмите «Сканировать»';

  @override
  String get liveHintShelf => 'Наведите на полку с товарами';

  @override
  String get liveHintInvoice => 'Наведите на накладную целиком';

  @override
  String get liveHintLabels => 'Наведите на ценники и этикетки';

  @override
  String get auditTitle => 'Журнал аудита';

  @override
  String get auditSub => 'Все действия на этом телефоне';

  @override
  String get auditSearch => 'Сотрудник, товар, номер чека';

  @override
  String get auditKind => 'Действие';

  @override
  String get auditWhat => 'Что сделано';

  @override
  String get auditSale => 'Продажа';

  @override
  String get auditCart => 'В чек';

  @override
  String get auditReturn => 'Возврат';

  @override
  String get auditPrice => 'Цены и остаток';

  @override
  String get auditProduct => 'Товары';

  @override
  String get auditSettings => 'Настройки';

  @override
  String get auditLogin => 'Вход и выход';

  @override
  String get auditOpen => 'Открыл экран';

  @override
  String get teachCamera => 'Научить умную камеру: 3–5 фото + штрихкод';

  @override
  String get teachRight => '1 из 3 · товар справа';

  @override
  String get teachLeft => '2 из 3 · товар слева';

  @override
  String get teachTop => '3 из 3 · товар сверху';

  @override
  String get teachHint => 'Товар целиком в рамке, на светлом фоне';

  @override
  String get teachDone =>
      'Камера обучена — при продаже найдёт этот товар точно';

  @override
  String teachShots(int n) {
    return 'Для умной камеры: $n фото';
  }

  @override
  String get scanShoot => 'Сканировать';

  @override
  String get scanBusy => 'Сканирую…';

  @override
  String get scanNext => 'Следующий товар — снова «Сканировать»';

  @override
  String get scanChecking => 'Сверяю с обученными товарами…';

  @override
  String scanTook(String sec) {
    return 'за $sec с';
  }

  @override
  String teachPhotoOf(int a, int b) {
    return 'Фото $a из $b';
  }

  @override
  String get teachAngleRight => 'товар справа';

  @override
  String get teachAngleLeft => 'товар слева';

  @override
  String get teachAngleTop => 'товар сверху';

  @override
  String get teachAngleFront => 'этикетка спереди';

  @override
  String get teachAngleBack => 'товар сзади';

  @override
  String get teachCanFinish => 'Можно нажать «Готово» или снять ещё ракурс';

  @override
  String get teachRetake => 'Переснять';

  @override
  String get weighAim =>
      'Положите товар на весы, наведите на табло и нажмите «Сканировать»';

  @override
  String get weighAimDisplay =>
      'Табло весов не видно — наведите камеру на окно веса, выключите фонарик и нажмите «Сканировать»';

  @override
  String get aiErrNoNet =>
      'Нет интернета — проверьте связь и нажмите «Сканировать» ещё раз';

  @override
  String get aiErrSlow =>
      'ИИ не ответил вовремя (медленная связь) — попробуйте ещё раз';

  @override
  String get aiErrLimit =>
      'Кончился бесплатный лимит запросов ИИ — попробуйте через минуту';

  @override
  String get aiErrBusy => 'ИИ сейчас перегружен — попробуйте через минуту';

  @override
  String get aiErrOther => 'ИИ не смог распознать снимок';

  @override
  String addAgain(String name) {
    return '«$name» уже в чеке. Добавить ещё?';
  }

  @override
  String get yesAdd => 'Добавить';

  @override
  String get shelfAddAsk =>
      'Снимок полки — это товар, который уже стоит на полке. Прибавить эти количества к остатку как поставку?';

  @override
  String isItProduct(String name) {
    return 'Это «$name»?';
  }

  @override
  String notFoundOffline(String code) {
    return 'Код $code не найден на телефоне, а сервер не отвечает. Проверьте связь — товар может быть уже заведён на ПК, не создавайте второй.';
  }

  @override
  String get scaleLabelsTitle => 'Этикетки весов';

  @override
  String get scaleLabelsHint =>
      'Штрихкод этикетки весов начинается с префикса (20–29). Укажите, какие префиксы несут СУММУ, а какие — ВЕС. Для всех телефонов компании.';

  @override
  String get scaleAmountPrefixes => 'Префиксы «сумма»';

  @override
  String get scaleWeightPrefixes => 'Префиксы «вес»';

  @override
  String get cameraShotFailed =>
      'Камера не сделала снимок — нажмите «Сканировать» ещё раз';

  @override
  String get saved => 'Сохранено';

  @override
  String get exactMatch => 'точно';

  @override
  String get noAccess => 'Нет доступа';

  @override
  String get noAccessHint =>
      'Этот раздел или действие закрыты для вашей учётной записи. Доступ включает владелец: NurCRM → Сотрудники → доступы.';

  @override
  String get clientOptional => 'Клиент — для бонусов и истории покупок';

  @override
  String get bonusRedeem => 'Списать бонусами';

  @override
  String get bonusNone => 'Не списывать';

  @override
  String bonusAll(String sum) {
    return 'Максимум · $sum';
  }

  @override
  String bonusEarn(String sum) {
    return 'Начислится бонусов: $sum';
  }

  @override
  String bonusPaid(String sum) {
    return 'Бонусами: −$sum сом';
  }

  @override
  String get removeClient => 'Убрать клиента';

  @override
  String get discountRemove => 'Убрать скидку';

  @override
  String get addedToCheck => 'Добавлено в чек';

  @override
  String get payment => 'Оплата';

  @override
  String toPayPositions(int n) {
    return 'К оплате · $n позиций';
  }

  @override
  String get payMethod => 'Способ оплаты';

  @override
  String get cash => 'Наличные';

  @override
  String get card => 'Карта';

  @override
  String get debt => 'В долг';

  @override
  String get mixed => 'Смешанная';

  @override
  String get client => 'Клиент';

  @override
  String get received => 'Получено';

  @override
  String get receivedNowDebt => 'Получено сейчас (0 — весь долг)';

  @override
  String get exact => 'Без сдачи';

  @override
  String get change => 'Сдача';

  @override
  String notEnough(String sum) {
    return 'Не хватает $sum сом';
  }

  @override
  String get receiptToCustomer => 'Чек покупателю';

  @override
  String get notNeeded => 'Не нужен';

  @override
  String get doPay => 'Провести оплату';

  @override
  String get check => 'Чек';

  @override
  String get total => 'Итого';

  @override
  String get paidOk => 'Оплата проведена';

  @override
  String get savedOffline => 'Чек сохранён на телефоне';

  @override
  String get savedOfflineHint =>
      'Нет связи — чек уйдёт на сервер сам, второй раз не проведётся.';

  @override
  String get receiptQr => 'QR-код';

  @override
  String get showQr => 'Показать QR покупателю';

  @override
  String get qrPointCamera => 'Покупатель наводит камеру телефона';

  @override
  String get qrDownloadHint => 'Камера покупателя сразу скачает чек PDF-файлом';

  @override
  String get qrTextHint =>
      'Пока сервер не выдаёт ссылку на чек, камера покажет его текстом. PDF-файл клиенту — кнопкой ниже.';

  @override
  String get cashInOut => 'Внесение / изъятие';

  @override
  String get cashIn => 'Внесение';

  @override
  String get cashOut => 'Изъятие';

  @override
  String get amount => 'Сумма';

  @override
  String get reason => 'Причина';

  @override
  String get reasonChange => 'Размен';

  @override
  String get reasonCollection => 'Инкассация';

  @override
  String get reasonExpense => 'Расходы';

  @override
  String get reasonOther => 'Другое';

  @override
  String get cashOpDone => 'Записано в смену';

  @override
  String get debts => 'Долги клиентов';

  @override
  String get debtorsTotal => 'Всего долгов';

  @override
  String get payDebt => 'Принять оплату долга';

  @override
  String get payDebtDone => 'Оплата долга проведена';

  @override
  String get noDebts => 'Должников нет';

  @override
  String get heldChecks => 'Отложенные чеки';

  @override
  String get holdCheck => 'Отложить чек';

  @override
  String get holdNote => 'Для кого или заметка';

  @override
  String get heldEmpty => 'Отложенных чеков нет';

  @override
  String get restoreCheck => 'Вернуть в чек';

  @override
  String get delete => 'Удалить';

  @override
  String get checkBusy =>
      'В чеке уже есть товары — сначала отложите или оплатите его';

  @override
  String get returnTitle => 'Возврат по чеку';

  @override
  String get returnAll => 'Вернуть весь чек';

  @override
  String get returnSelected => 'Вернуть выбранное';

  @override
  String get returnDone => 'Возврат проведён';

  @override
  String get returnToCustomer => 'Вернуть покупателю';

  @override
  String get showReceipt => 'Посмотреть чек';

  @override
  String get sendPdf => 'Отправить PDF';

  @override
  String get sendReceipt => 'Отправить чек';

  @override
  String get newClient => 'Новый клиент';

  @override
  String get clientName => 'Имя';

  @override
  String get phone => 'Телефон';

  @override
  String get clientSearch => 'Имя или телефон';

  @override
  String get myChecks => 'Мои чеки';

  @override
  String get thisShift => 'Эта смена';

  @override
  String unsentN(int n) {
    return 'Не отправленные · $n';
  }

  @override
  String get checkSearch => 'Номер чека или товар';

  @override
  String get noChecks => 'Чеков пока нет';

  @override
  String get checkNotSent => 'Чек не отправлен';

  @override
  String get notAccepted => 'не принят';

  @override
  String get waitsForNetwork => 'ждёт связи';

  @override
  String get returned => 'возврат';

  @override
  String get offlineCheckHint =>
      'Нет связи — чек сохранён и уйдёт на сервер сам';

  @override
  String get sendToCustomer => 'Отправить покупателю';

  @override
  String get sendAgain => 'Отправить ещё раз';

  @override
  String get returnByAdmin => 'Возврат — с разрешения администратора';

  @override
  String get receiveByScanner => 'Приёмка сканером';

  @override
  String get audit => 'Ревизия';

  @override
  String get findProduct => 'Найти товар';

  @override
  String get pricesByAdmin => 'Цены и новые товары меняет администратор';

  @override
  String get receiveNoSupplier => 'Без поставщика и без цен';

  @override
  String get scanProduct => 'Сканировать товар';

  @override
  String get eachScanPlusOne => 'Каждый скан — +1 штука';

  @override
  String get accepted => 'Принято';

  @override
  String goodsPieces(int n, String qty) {
    return '$n товаров · $qty шт';
  }

  @override
  String wasPieces(String qty) {
    return 'было $qty';
  }

  @override
  String get unknownBarcode => 'Неизвестный штрихкод';

  @override
  String unknownBarcodeHint(String code) {
    return '$code · администратор получит его и заведёт товар';
  }

  @override
  String get postpone => 'Отложить';

  @override
  String postPieces(String qty) {
    return 'Провести · $qty шт';
  }

  @override
  String get receivedOk => 'Приёмка проведена.';

  @override
  String get receivedQueued => 'Приёмка сохранена — уйдёт при связи.';

  @override
  String get allStock => 'Весь склад';

  @override
  String get chooseWhatToCount => 'Что пересчитать';

  @override
  String checkedOf(int a, int b) {
    return 'Проверено $a из $b';
  }

  @override
  String get notChecked => 'Не проверены';

  @override
  String get differences => 'Расхождения';

  @override
  String byProgram(String qty, String unit) {
    return 'по программе $qty $unit';
  }

  @override
  String get matches => 'совпало';

  @override
  String shortBy(String qty) {
    return 'не хватает $qty';
  }

  @override
  String extraBy(String qty) {
    return 'лишние $qty';
  }

  @override
  String get auditAdminApplies =>
      'Остатки поменяет администратор после проверки';

  @override
  String get sendToAdmin => 'Отправить администратору';

  @override
  String get auditSent => 'Ревизия отправлена администратору.';

  @override
  String get auditQueued => 'Ревизия сохранена — уйдёт при связи.';

  @override
  String sinceChecks(String time, int n) {
    return 'с $time · $n чеков';
  }

  @override
  String get revenue => 'Выручка';

  @override
  String get shouldBeInDrawer => 'Должно быть в кассе';

  @override
  String get recountCash => 'Пересчитайте наличные';

  @override
  String get howMuchInDrawer => 'Сколько денег в кассе';

  @override
  String cashDiff(String sum) {
    return 'Разница с программой: $sum сом';
  }

  @override
  String get cashDiffHint =>
      'Если сумма не сходится, администратор увидит разницу в отчёте';

  @override
  String closeShiftPending(int n) {
    return 'Сначала должны уйти на сервер $n чеков — нужна связь.';
  }

  @override
  String get shiftClosedOk => 'Смена закрыта';

  @override
  String get receiptPrinter => 'Принтер чеков';

  @override
  String get prnSub => 'Bluetooth, Wi-Fi/LAN или USB · 58/80 мм';

  @override
  String get prnNotSet => 'Не настроен';

  @override
  String get prnConnection => 'Подключение';

  @override
  String get prnOff => 'Нет';

  @override
  String get prnNetwork => 'Сеть (Wi-Fi/LAN)';

  @override
  String get prnUsb => 'USB (OTG)';

  @override
  String get prnSearch => 'Найти принтеры';

  @override
  String get prnSearching => 'Ищу…';

  @override
  String get prnNothingFound => 'Ничего не найдено. Принтер включён и рядом?';

  @override
  String get prnPairHint =>
      'Принтера нет в списке? Включите его и нажмите «Найти» — или сопрягите в настройках Bluetooth телефона (PIN обычно 0000 или 1234).';

  @override
  String get prnUsbHint =>
      'Подключите принтер кабелем OTG и включите его. Android спросит разрешение — нажмите «Разрешить».';

  @override
  String get prnBtSettings => 'Настройки Bluetooth';

  @override
  String get prnPaired => 'сопряжён';

  @override
  String get prnNotPaired => 'рядом, не сопряжён';

  @override
  String get prnIp => 'IP-адрес принтера';

  @override
  String get prnPort => 'Порт';

  @override
  String get prnPaper => 'Ширина ленты';

  @override
  String prnPaperMm(int a, int b) {
    return '$a мм · $b знаков';
  }

  @override
  String get prnCodepage => 'Кодировка кириллицы';

  @override
  String get prnCodeTable => 'Таблица ESC t';

  @override
  String get prnCodepageHint =>
      'Вместо русских букв иероглифы или «?» — смените кодировку или номер таблицы (CP866 — 17, у некоторых 6 или 7; CP1251 — 46, у некоторых 73) и проверьте ещё раз.';

  @override
  String get prnAuto => 'Печатать чек сразу после оплаты';

  @override
  String get prnTest => 'Проверить печать';

  @override
  String get prnTestSent => 'Пробная страница отправлена на принтер';

  @override
  String get prnTestHint =>
      'Посмотрите пробную страницу: строка цифр должна занять всю ширину, русские буквы — читаться.';

  @override
  String get prnPrint => 'Печать чека';

  @override
  String get prnSent => 'Чек отправлен на принтер';

  @override
  String get prnErrNotConfigured =>
      'Принтер не настроен: Профиль → Принтер чеков.';

  @override
  String get prnErrUnsupported =>
      'На этом устройстве такой способ печати недоступен.';

  @override
  String get prnErrBtOff => 'Bluetooth выключен — включите его и повторите.';

  @override
  String get prnErrPermission =>
      'Нет разрешения «Устройства поблизости» (Bluetooth). Разрешите в окне Android или в настройках приложения.';

  @override
  String get prnErrNotFound =>
      'Принтер не найден — проверьте, что он включён, и выберите его заново.';

  @override
  String get prnErrBleOnly =>
      'Этот принтер работает только через BLE — нужен принтер с обычным Bluetooth (SPP).';

  @override
  String get prnErrTimeout =>
      'Принтер не отвечает. Он включён, рядом, есть бумага? Повторите.';

  @override
  String get prnErrRefused =>
      'Принтер в сети не принимает печать — проверьте IP-адрес и порт (обычно 9100).';

  @override
  String get prnErrUsbDenied =>
      'Нет доступа к USB-принтеру — нажмите «Разрешить» в окне Android.';

  @override
  String get prnErrUsbNotFound =>
      'USB-принтер не найден — проверьте кабель OTG и питание принтера.';

  @override
  String prnErrIo(String text) {
    return 'Ошибка печати: $text';
  }

  @override
  String get kgUnit => 'кг';

  @override
  String get weighShort => 'Весы';

  @override
  String get weighTitle => 'Весовой товар';

  @override
  String get weighHint =>
      'Положите товар на весы. Наведите камеру, чтобы были видны товар и табло весов.';

  @override
  String get weighReading => 'Узнаю товар и читаю табло весов…';

  @override
  String get weighWaiting => 'Жду, пока вес установится…';

  @override
  String get weighZero => 'На весах 0 — положите товар.';

  @override
  String get weighNoDisplay =>
      'Табло весов не видно — наведите камеру на цифры веса.';

  @override
  String get weighNoAi =>
      'Умная камера недоступна (нет связи или ключа ИИ) — выберите товар и введите вес вручную.';

  @override
  String get weighConfirm => 'Проверьте товар и вес';

  @override
  String weighRead(String name, String kg) {
    return 'Камера прочитала: $name · $kg кг';
  }

  @override
  String weighLine(String name, String kg, String price, String sum) {
    return '$name $kg кг × $price = $sum сом';
  }

  @override
  String weighTooHeavy(String kg) {
    return 'Вес $kg кг — больше 50 кг. Точно добавить?';
  }

  @override
  String get weighNotStable => 'Вес не установился — проверьте показание.';

  @override
  String get weighEditKg => 'Изменить вес';

  @override
  String get weighOther => 'Другой товар — выбрать из списка';

  @override
  String get linkWithKassa => 'Связь с кассой';

  @override
  String get linkWithKassaSub => 'По Wi-Fi магазина — в следующем обновлении';

  @override
  String get language => 'Язык';

  @override
  String get darkTheme => 'Тёмная тема';

  @override
  String get offlineMode => 'Работа без интернета';

  @override
  String waitingToSend(int n) {
    return 'Ждут отправки: $n';
  }

  @override
  String get logout => 'Выйти из аккаунта';

  @override
  String logoutPending(int n) {
    return 'На телефоне $n неотправленных записей. После выхода они останутся и уйдут при следующем входе этого же аккаунта. Выйти?';
  }

  @override
  String get sync => 'Синхронизация';

  @override
  String catalogUpdated(String time) {
    return 'Каталог обновлён $time';
  }

  @override
  String get nothingToSend => 'Всё отправлено';

  @override
  String get sent => 'отправлено';

  @override
  String get sendNow => 'Отправить сейчас';

  @override
  String get kindSale => 'Продажа';

  @override
  String get admin => 'Управление';

  @override
  String get adminSub => 'Цены, смены, заказы, клиенты';

  @override
  String get revenueToday => 'Выручка сегодня';

  @override
  String get checksToday => 'Чеков сегодня';

  @override
  String get clientsOwe => 'Клиенты должны';

  @override
  String get productPrice => 'Товар — цена и остаток';

  @override
  String get shifts => 'Смены и отчёт';

  @override
  String get siteOrders => 'Заказы с сайта';

  @override
  String get clientsBonuses => 'Клиенты и бонусы';

  @override
  String get supplierOrder => 'Заказ поставщику';

  @override
  String get deletedFromCheck => 'Удалено из чека';

  @override
  String get nameOrBarcode => 'Название или штрихкод';

  @override
  String get salePrice => 'Цена продажи';

  @override
  String get purchasePrice => 'Закупка';

  @override
  String get wholesalePrice => 'Оптовая цена';

  @override
  String get onStockLabel => 'На складе';

  @override
  String get priceSaved => 'Цена сохранена.';

  @override
  String markup(String pct) {
    return 'Наценка $pct %';
  }

  @override
  String get belowCost => 'Цена ниже закупки — продажа в убыток';

  @override
  String get shiftOpenNow => 'открыта';

  @override
  String shiftClosedAt(String time) {
    return 'закрыта $time';
  }

  @override
  String get cashierLabel => 'Кассир';

  @override
  String get orderNew => 'Новый';

  @override
  String get orderPicking => 'Собираем';

  @override
  String get orderReady => 'Готов к выдаче';

  @override
  String get orderDone => 'Выдан';

  @override
  String get orderCanceled => 'Отменён';

  @override
  String get pickOrder => 'Собрать заказ';

  @override
  String get pickScanHint => 'Сканируйте товар — он сам отметится';

  @override
  String pickedOf(int a, int b) {
    return 'Собрано $a из $b';
  }

  @override
  String get orderPicked => 'Заказ собран';

  @override
  String get notInOrder => 'Этого товара нет в заказе';

  @override
  String get notifyCustomer => 'Сообщить покупателю';

  @override
  String get handOver => 'Выдать';

  @override
  String orderReadyMsg(String num, String sum, String code) {
    return 'Заказ $num готов! К оплате $sum сом. Код выдачи $code.';
  }

  @override
  String get statusSaved => 'Статус сохранён.';

  @override
  String get bonusBalance => 'Бонусов на счёте';

  @override
  String get addBonus => 'Начислить бонусы';

  @override
  String get bonusAdded => 'Бонусы начислены.';

  @override
  String get noPhone => 'нет номера';

  @override
  String get orderToSupplier => 'Отправить в WhatsApp';

  @override
  String get supplierName => 'Поставщик';

  @override
  String get supplierPhone => 'Телефон поставщика';

  @override
  String get runningLowAdd => 'Добавить всё, что заканчивается';

  @override
  String get orderQty => 'Сколько заказать';

  @override
  String get deleteReason => 'Почему убираете?';

  @override
  String get reasonChanged => 'Покупатель передумал';

  @override
  String get reasonScanError => 'Ошибка сканирования';

  @override
  String get reasonNoMoney => 'Не хватило денег';

  @override
  String get reasonDefect => 'Брак или просрочка';

  @override
  String get remove => 'Убрать';

  @override
  String get noDeletions => 'Удалений не было.';

  @override
  String get loading => 'Загрузка…';

  @override
  String get empty => 'Здесь пусто.';

  @override
  String get share => 'Поделиться';

  @override
  String get documentPdf => 'Накладная, товарный чек, счёт';

  @override
  String get docSalesReceipt => 'Товарный чек';

  @override
  String get docWaybill => 'Накладная';

  @override
  String get docInvoice => 'Счёт на оплату';

  @override
  String get staff => 'Сотрудники и права';

  @override
  String get rightSale => 'Продажа и оплата';

  @override
  String get rightReturn => 'Возврат и обмен';

  @override
  String get rightDiscount => 'Скидки';

  @override
  String get rightEditPrice => 'Менять цену в чеке';

  @override
  String get rightDeleteLine => 'Удалять из чека';

  @override
  String get rightStock => 'Склад, приёмка и ревизия';

  @override
  String get rightReports => 'Отчёты и аналитика';

  @override
  String get rightShifts => 'Смены';

  @override
  String get rightClients => 'Клиенты';

  @override
  String get rightSettings => 'Настройки';

  @override
  String get rightsSaved => 'Права сохранены.';

  @override
  String get notifications => 'Уведомления';

  @override
  String get notifyNewOrder => 'Новый заказ с сайта';

  @override
  String get notifyRunningOut => 'Заканчиваются товары';

  @override
  String notifyRunningOutSub(int n) {
    return '$n позиций · пора заказать';
  }

  @override
  String get notifyBigDebt => 'Большой долг клиента';

  @override
  String get notifyNotSent => 'Не отправлены на сервер';

  @override
  String get noNotifications => 'Новых уведомлений нет.';

  @override
  String get newProductSub => 'Штрихкод, название, цены';

  @override
  String get findByBarcode => 'Найти по штрихкоду';

  @override
  String alreadyInStock(String name) {
    return 'Такой товар уже есть: $name';
  }

  @override
  String get productCreated => 'Товар создан.';

  @override
  String get quantityLabel => 'Количество';

  @override
  String get barcodeLabel => 'Штрихкод';

  @override
  String get retail => 'Розница';

  @override
  String get wholesale => 'Опт';

  @override
  String get wholesaleOn =>
      'Оптовая продажа: товары с оптовой ценой — по опту.';

  @override
  String get wholesaleNone =>
      'У товаров этого чека нет оптовой цены — задайте её в «Товар — цена и остаток».';

  @override
  String get wholesaleTag => 'опт';

  @override
  String get cashJournal => 'Внесения и изъятия за смену';

  @override
  String get noCashOps => 'Внесений и изъятий не было.';

  @override
  String get themeLight => 'Светлая';

  @override
  String get themeDark => 'Тёмная';

  @override
  String get themeSystem => 'Как в системе';

  @override
  String get appearance => 'Оформление';

  @override
  String get searchAll => 'Товар, клиент или раздел';

  @override
  String get sectionsTitle => 'Разделы';

  @override
  String get productsTitle => 'Товары';

  @override
  String get clientsTitle => 'Клиенты';

  @override
  String get openAction => 'Открыть';

  @override
  String get returnAction => 'Вернуть';

  @override
  String addedToCheckName(String name) {
    return 'Добавлено в чек: $name';
  }

  @override
  String get receiveWithPrices => 'Приёмка с ценами';

  @override
  String get receiveWithPricesSub =>
      'Поставщик, закупка, продажа, новые товары';

  @override
  String get noSupplier => 'Без поставщика';

  @override
  String get newProduct => 'Новый товар';

  @override
  String barcodeNotInStock(String code) {
    return 'Штрихкод $code не найден на складе';
  }

  @override
  String get foundInBase => 'Найдено в общей базе по штрихкоду — проверьте';

  @override
  String get unitLabel => 'Единица';

  @override
  String get createAndAdd => 'Создать и добавить';

  @override
  String markupRule(String pct) {
    return 'Цены продажи: +$pct % к закупке, до 5 сом';
  }

  @override
  String get payNowCash => 'Оплачено сразу';

  @override
  String get onCredit => 'В долг поставщику';

  @override
  String purchaseSum(String sum) {
    return 'Закупка $sum сом';
  }

  @override
  String get postReceipt => 'Провести приход';

  @override
  String receiptPosted(int n) {
    return 'Приход проведён: $n поз.';
  }

  @override
  String partlyFailed(String text) {
    return 'Не прошло: $text';
  }

  @override
  String get shiftsOpen => 'Открытые';

  @override
  String get shiftsClosed => 'Закрытые';

  @override
  String get debtAccessTitle => 'Доступ к долгу';

  @override
  String get debtAccessHint =>
      'Кому из сотрудников можно продавать в долг: можно, нельзя или с лимитом — сколько чеков и на какую сумму в день. Правила общие для всех касс и телефонов компании. Владелец и администратор ограничений не имеют.';

  @override
  String get debtAllStaff => 'Все сотрудники';

  @override
  String get debtSameAsAll => 'Как у всех';

  @override
  String get debtAllow => 'Можно';

  @override
  String get debtDeny => 'Нельзя';

  @override
  String get debtLimit => 'С лимитом';

  @override
  String get debtReceiptsPerDay => 'Чеков в долг в день';

  @override
  String get debtSumPerDay => 'Сумма долга в день, сом';

  @override
  String get debtNoLimit => 'без огр.';

  @override
  String get debtAccessSaved =>
      'Сохранено — правила уже действуют на всех кассах и телефонах.';

  @override
  String get debtStaffNotLoaded =>
      'Список сотрудников не получен (нет связи?) — можно задать правило для всех.';

  @override
  String get debtDenied =>
      'Владелец запретил вам продажу в долг. Выберите другой способ оплаты или позовите владельца.';

  @override
  String debtLimitReceipts(int a, int b) {
    return 'Лимит продаж в долг на сегодня исчерпан: $a из $b чек. Обратитесь к владельцу.';
  }

  @override
  String debtLimitSum(String left, String max, String sum) {
    return 'Превышен лимит долга на сегодня: можно ещё $left сом из $max сом, а в долг $sum сом. Возьмите часть оплаты сейчас или позовите владельца.';
  }

  @override
  String debtReceiptsOf(int a, int b) {
    return 'чеков $a из $b';
  }

  @override
  String debtSumOf(String used, String max) {
    return 'долг $used из $max сом';
  }

  @override
  String debtYourLimit(String text) {
    return 'Ваш лимит в долг на сегодня: $text';
  }

  @override
  String get promoTag => 'акция';

  @override
  String get promoNoManualDiscount =>
      'На товар действует акция — скидку по ней считает сервер, ручную скидку на эту строку дать нельзя.';

  @override
  String get quoteSearch => 'Номер, клиент или телефон';

  @override
  String get quotesEmpty => 'Пока нет КП и счетов';

  @override
  String get quotesEmptyHint =>
      'Нажмите «Создать»: выберите клиента и товары — PDF отправится в WhatsApp или на почту.';

  @override
  String get quotesNoMatch => 'Ничего не найдено — измените поиск или фильтр.';

  @override
  String get createDoc => 'Создать';

  @override
  String get makeInvoice => 'Сделать счёт';

  @override
  String get sendToClient => 'Отправить';

  @override
  String docWaText(String doc, String number, String sum, String until) {
    return '$doc № $number на сумму $sum сом. $until.';
  }

  @override
  String get docSubtotal => 'Сумма без скидки';

  @override
  String get discountShort => 'Скидка';

  @override
  String get paidLabel => 'Оплачено';

  @override
  String get leftToPay => 'Осталось оплатить';

  @override
  String get findClient => 'Найти клиента';

  @override
  String get companyLabel => 'Компания (ОсОО, ИП)';

  @override
  String get clientExists =>
      'Клиент с этим телефоном уже есть в NurCRM — выбран он.';

  @override
  String get clientSaved => 'Клиент сохранён в NurCRM';

  @override
  String get clientLocalOnly =>
      'Нет связи с NurCRM — клиент записан только в этот документ.';

  @override
  String get clientNameRequired => 'Введите имя или компанию';

  @override
  String get innDigits => 'ИНН — только цифры, обычно 14';

  @override
  String get discountHint => 'Сумма или процент, например 10%';

  @override
  String get stExpired => 'Срок истёк';

  @override
  String deleteDocAsk(String number) {
    return 'Удалить $number?';
  }

  @override
  String get deleteDocAskSub =>
      'Документ удалится и в программе владельца, без возврата.';

  @override
  String get requisitesTitle => 'Реквизиты для счёта';

  @override
  String get requisitesHint =>
      'Банк, расчётный счёт, БИК, телефон — печатаются в PDF под названием магазина.';

  @override
  String scannedAdded(String name) {
    return 'Добавлено: $name';
  }

  @override
  String invoiceCreated(String number) {
    return 'Создан счёт $number';
  }

  @override
  String get historyLabel => 'История';

  @override
  String get docLines => 'Товары и услуги';

  @override
  String docFrom(String date) {
    return 'от $date';
  }

  @override
  String get clientNotChosen => 'Клиент не выбран';

  @override
  String get linesEmptyHint =>
      'Добавьте товары: поиск, сканер или из текущего чека.';

  @override
  String get quotesSyncing => 'Обновление общего списка…';

  @override
  String get quotesShared => 'Общий список с программой владельца';

  @override
  String quotesSharedAt(String time) {
    return 'Общий список обновлён: $time';
  }

  @override
  String get quotesOffline => 'Нет связи с сервером — показана копия телефона';

  @override
  String quotesNotSent(int n) {
    return 'Не отправлено: $n. Уйдёт в программу владельца при связи — нажмите, чтобы повторить.';
  }

  @override
  String get quotesForbidden =>
      'Нет прав на общие настройки компании — изменения только на этом телефоне. Нужен вход владельца или администратора.';

  @override
  String get notSentMark => 'не отправлено';

  @override
  String quoteChangedElsewhere(String number) {
    return '$number позже изменили в другом месте — показана последняя версия';
  }

  @override
  String quoteRenumbered(String from, String to) {
    return 'Номер $from уже занят — документ теперь $to';
  }

  @override
  String get payOutcomeUnknown =>
      'Сервер не ответил — продажа могла пройти. Нажмите «Оплатить» ещё раз, ничего не меняя: повтор второй раз не проведётся. Без связи долг и списание бонусов не оформляются.';

  @override
  String get debtDueLabel => 'Вернуть до';

  @override
  String debtDueDays(int n) {
    return '$n дн.';
  }

  @override
  String get debtDueNotSaved =>
      'Срок долга не записан на сервере — поправьте его на сайте (по умолчанию 30 дней).';

  @override
  String returnAmount(String sum) {
    return 'Сумма возврата: $sum сом';
  }

  @override
  String returnGive(String what) {
    return 'Выдать покупателю: $what';
  }

  @override
  String returnInCash(String sum) {
    return 'наличными $sum сом';
  }

  @override
  String returnToCard(String sum) {
    return 'на карту/перевод $sum сом';
  }

  @override
  String returnDebtReduced(String sum) {
    return 'Долг клиента уменьшен на $sum сом — эти деньги не выдаются.';
  }

  @override
  String returnDebtUnknown(String sum) {
    return 'Внимание: остаток долга клиента с сервера не получен. По расчёту долг уменьшен на $sum сом, но если клиент уже погасил долг по этому чеку — ему нужно вернуть до $sum сом. Проверьте долг клиента перед выдачей.';
  }

  @override
  String get returnViaBank =>
      'Часть на карту/перевод — вернуть через банк, не из кассы.';

  @override
  String returnPaidInfo(String paid, String done) {
    return 'Оплачено по чеку: $paid сом · уже возвращено: $done сом';
  }

  @override
  String returnedQty(String qty) {
    return 'уже возвращено: $qty';
  }

  @override
  String get returnNothingLeft => 'По этому чеку всё уже возвращено.';

  @override
  String cashOutTooMuch(String sum) {
    return 'Изъятие больше, чем в кассе ($sum сом).';
  }

  @override
  String payDebtToDebt(String sum) {
    return 'В счёт долга: $sum сом';
  }

  @override
  String get payDebtOverCard => 'Переводом — не больше долга.';

  @override
  String receiveNotAccepted(String reason) {
    return 'Приход не принят сервером: $reason. Запись — в «Синхронизации».';
  }

  @override
  String get auditApply => 'Провести ревизию';

  @override
  String get auditApplyHint =>
      'Остатки станут равны пересчитанным — как ревизия на кассе ПК. Нужна связь.';

  @override
  String auditApplyAsk(int n, int a) {
    return 'Проверено товаров: $n, расхождений: $a. Остатки станут равны пересчитанным. Провести?';
  }

  @override
  String get auditApplied => 'Ревизия проведена — остатки обновлены.';

  @override
  String get auditApplyQueued =>
      'Акт ревизии создан — проведение уйдёт при связи.';

  @override
  String get auditOnlineOnly =>
      'Ревизия проводится только при связи с сервером — пересчёт сохранён, повторите позже.';

  @override
  String auditContinue(int n) {
    return 'Продолжить пересчёт ($n товаров)';
  }

  @override
  String get countedCash => 'Пересчитано';

  @override
  String get openingCashLabel => 'На начало смены';

  @override
  String closeShiftFailed(int n) {
    return 'Сервер не принял $n чеков — они не попадут в отчёт смены. Отправьте их снова или снимите в «Синхронизации».';
  }

  @override
  String get closeShiftAsk => 'Закрыть смену?';

  @override
  String get closeShiftAskSub => 'После закрытия продавать в эту смену нельзя.';

  @override
  String get xReport => 'X-отчёт';

  @override
  String get foreignShiftHint =>
      'На этой кассе открыта смена другого кассира — продавать в неё нельзя. Откройте свою смену.';

  @override
  String get loadMore => 'Ещё';

  @override
  String get dropRow => 'Снять';

  @override
  String get dropRowAsk =>
      'Снять чек из очереди? Он не уйдёт на сервер, товар вернётся в остаток телефона. Если деньги взяты — верните их покупателю или пробейте чек заново.';

  @override
  String get dropRowAskStock =>
      'Снять запись из очереди? Она не уйдёт на сервер — проверьте остатки товара вручную.';

  @override
  String get otherAccount => 'другой аккаунт';

  @override
  String otherAccountHint(String email) {
    return 'Записано под входом $email — уйдёт после входа этим логином.';
  }

  @override
  String pAStockUnclear(String name, String now, String before) {
    return '$name: прошлая попытка осталась без ответа сервера, а остаток с тех пор изменился (сейчас $now, до прибавки было $before). Повторно НЕ прибавлено, чтобы не задвоить. Проверьте остаток ревизией: если прибавилось — удалите строку, если нет — удалите её и добавьте товар заново.';
  }

  @override
  String get pAWeightLabel =>
      'Это этикетка весов (вес одной покупки) — в приёмку её не берём. Отсканируйте штрихкод товара или найдите его по названию.';

  @override
  String get pANoServerCheck =>
      'Нет связи с сервером — не проверить, есть ли уже товар с этим штрихкодом. Повторите, когда появится связь.';

  @override
  String pALinesLeft(int n) {
    return 'Не принято строк: $n — они остались в списке, поправьте и проведите снова.';
  }

  @override
  String get pAPluLabel => 'PLU для весов';

  @override
  String get pAPluHint => 'пусто — назначится сам';

  @override
  String get pAStockAdd => 'Прибавить к остатку';

  @override
  String get pAStockSet => 'Задать остаток';

  @override
  String get pAStockAddHint =>
      'Количество из файла прибавится к остатку, как приход.';

  @override
  String get pAStockSetHint =>
      'Остаток станет равен количеству из файла (замена, не приход).';

  @override
  String pADupRow(int n) {
    return 'Повтор строки $n — пропускается';
  }

  @override
  String get pAUploaded => 'загружено';

  @override
  String get rightCashier => 'Интерфейс кассира';

  @override
  String get rightOrders => 'Заказы с сайта';

  @override
  String get rightProcurement => 'Закупки';

  @override
  String get rightSuppliers => 'Поставщики';

  @override
  String get rightSelfLocked =>
      'Свои права выключить нельзя — это делает владелец или другой администратор.';

  @override
  String get staffYou => 'это вы';

  @override
  String get screenUnavailable =>
      'Экран открылся заново, и его данные не сохранились. Вернитесь назад и откройте его ещё раз.';

  @override
  String get cancelOrder => 'Отменить заказ';

  @override
  String cancelOrderAsk(String number) {
    return 'Отменить заказ №$number? Покупатель его не получит.';
  }

  @override
  String pickShortAsk(String got, String need) {
    return 'Собрано $got из $need. Отметить заказ собранным с недобором?';
  }

  @override
  String pickupCodeLabel(String code) {
    return 'Код выдачи: $code';
  }

  @override
  String get torchToggle => 'Фонарик';

  @override
  String get payTransferQr => 'Перевод / QR';

  @override
  String get reportLoadFailed =>
      'Отчёт не загрузился. Проверьте связь и нажмите «Повторить».';

  @override
  String get rentalLatePerDay => 'Пеня за сутки просрочки';

  @override
  String rentalLateHint(int n, String rate, String sum) {
    return 'Просрочка $n сут. × $rate = $sum сом (по условиям проката)';
  }

  @override
  String rentalBackDeposit(String sum) {
    return 'Вернуть клиенту залог: $sum сом';
  }

  @override
  String rentalWithheld(String sum) {
    return 'удержано $sum сом';
  }

  @override
  String rentalBackDocument(String doc) {
    return 'Вернуть клиенту документ: $doc';
  }

  @override
  String rentalExcess(String sum) {
    return 'Штраф сверх залога: $sum сом — добавится в чек';
  }

  @override
  String rentalPenaltyLine(int n) {
    return 'Штраф по прокату №$n';
  }

  @override
  String rentalPenaltyInCart(String sum) {
    return 'Возврат принят. Штраф $sum сом добавлен в чек — проведите оплату.';
  }

  @override
  String get waybillDoc => 'Накладная';

  @override
  String get waybillsFilter => 'Накладные';

  @override
  String get docPayMarkOnly =>
      'Это только отметка в документе: деньги в кассу смены и долг клиента в NurCRM не записываются. Чтобы провести деньги через кассу, используйте «Провести продажу».';

  @override
  String get docPayMarkOk => 'Отметить оплату';

  @override
  String get docSellCartBusy =>
      'В чеке уже есть товары. Заменить их товарами документа?';

  @override
  String get docSellReplace => 'Заменить';

  @override
  String get docSentToSale => 'Передан в продажу';

  @override
  String docSellMissing(String names) {
    return 'Нет в каталоге телефона — добавьте вручную: $names';
  }

  @override
  String docVatIncl(String p) {
    return 'В т.ч. НДС $p%';
  }

  @override
  String get prnQr => 'QR-код на чеке';

  @override
  String get prnQrHint =>
      'Проверка фискального чека или электронный чек. Если принтер печатает вместо QR лишние знаки — выключите.';

  @override
  String get prnDrawer => 'Денежный ящик через принтер';

  @override
  String get prnDrawerHint =>
      'Открывается после продажи за наличные (ящик подключён к принтеру кабелем).';

  @override
  String prnDrawerPin(int n) {
    return 'Контакт $n';
  }

  @override
  String get prnDrawerTest => 'Открыть ящик';

  @override
  String get prnDrawerSent =>
      'Команда ящику отправлена. Не открылся — выберите другой контакт.';

  @override
  String get stepPhoto => 'Фото';

  @override
  String get stepBarcode => 'Штрихкод';

  @override
  String get bindTitle =>
      'Фото сохранены ✓ — теперь отсканируйте штрихкод товара';

  @override
  String get bindSub => 'Если на товаре нет штрихкода — нажмите «Пропустить»';

  @override
  String get bindSkip => 'Пропустить';

  @override
  String get bindManual => 'Ввести вручную';

  @override
  String get bindManualHint => 'Цифры под штрихкодом';

  @override
  String get bindChecking => 'Проверяю штрихкод…';

  @override
  String bindWillLink(String code, String name) {
    return 'Штрихкод $code будет привязан к «$name»';
  }

  @override
  String bindWillLinkNew(String code) {
    return 'Штрихкод $code будет привязан к новому товару';
  }

  @override
  String bindAlreadyOwn(String code, String name) {
    return 'Штрихкод $code уже привязан к «$name» — менять ничего не нужно';
  }

  @override
  String get bindTakenTitle => 'Штрихкод уже у другого товара';

  @override
  String bindTakenAsk(String code, String name) {
    return 'Штрихкод $code уже привязан к «$name».\n«Заменить» — штрихкод перейдёт к этому товару.\n«Оставить» — останется у «$name», этот товар будет без штрихкода.';
  }

  @override
  String bindTakenAskNew(String code, String name) {
    return 'Штрихкод $code уже привязан к «$name» — возможно, этот товар уже есть на складе.\n«Оставить» — штрихкод останется у «$name», новый товар будет без штрихкода.';
  }

  @override
  String get bindReplace => 'Заменить';

  @override
  String get bindKeep => 'Оставить';

  @override
  String get bindRescan => 'Сканировать заново';

  @override
  String bindLinked(String code, String name) {
    return 'Штрихкод $code привязан к «$name»';
  }

  @override
  String get teachNext =>
      'Снимите товар с 3–5 сторон, затем «Готово» — следующим шагом штрихкод';

  @override
  String get teachNextNoCode =>
      'Снимите товар с 3–5 сторон, затем нажмите «Готово»';

  @override
  String get liveNextSale =>
      'Найденные товары появятся ниже — нажмите товар, и он ляжет в чек';

  @override
  String get liveNextList =>
      'После снимка камера покажет список найденного — его можно поправить';

  @override
  String get scanCodesHint =>
      'Наведите рамку на штрихкод — код запишется сам. Затем нажмите «Готово»';

  @override
  String get weighNext => 'Камера узнает товар, прочитает вес и добавит в чек';

  @override
  String get weighManualBtn => 'Без камеры — выбрать товар и ввести вес';

  @override
  String get peEdit => 'Редактировать товар';

  @override
  String get peName => 'Название';

  @override
  String get peMinQty => 'Минимальный остаток';

  @override
  String get peMinQtyHint => 'Меньше — товар попадёт в «Заканчивается»';

  @override
  String get peStock => 'Остаток';

  @override
  String get peStockHint =>
      'Остаток меняется только документом — приходом, списанием или ревизией с причиной, как на ПК. Так не бывает расхождений.';

  @override
  String get peIncome => 'Приход';

  @override
  String get peWriteOff => 'Списание';

  @override
  String get peRecount => 'Ревизия';

  @override
  String get peQtyIn => 'Сколько пришло';

  @override
  String get peQtyOut => 'Сколько списать';

  @override
  String get peQtyFact => 'Сколько на самом деле';

  @override
  String get peReason => 'Причина';

  @override
  String get peReasonNeed => 'Укажите причину.';

  @override
  String get peReasonExpired => 'Просрочка';

  @override
  String get peReasonDefect => 'Брак';

  @override
  String get peReasonSpoiled => 'Порча';

  @override
  String get peReasonTheft => 'Кража';

  @override
  String get peReasonOther => 'Другое';

  @override
  String get peReasonSupplier => 'Поставка';

  @override
  String get peReasonCount => 'Пересчёт';

  @override
  String get peApply => 'Провести';

  @override
  String peStockDone(String from, String to) {
    return 'Остаток: $from → $to';
  }

  @override
  String peStockSame(String qty) {
    return 'Остаток уже $qty — ничего не изменено.';
  }

  @override
  String get peStockQueued => 'Документ создан — проведётся сам при связи.';

  @override
  String get peStockOffline =>
      'Нет связи с сервером — остаток не получен, ничего не изменено.';

  @override
  String peStockBelowZero(String qty) {
    return 'Списать больше остатка нельзя (есть $qty).';
  }

  @override
  String get peSaved => 'Товар сохранён.';

  @override
  String get peNoChanges => 'Изменений нет.';

  @override
  String pePriceRestored(String price) {
    return 'Сервер пересчитал цену продажи по наценке — введённая цена $price возвращена.';
  }

  @override
  String get pePriceLocked =>
      'Цены меняет сотрудник с правом «Изменение цены».';

  @override
  String get peLoadFailed =>
      'Карточка товара не загружена с сервера — править можно при связи.';

  @override
  String get peNameNeed => 'Введите название.';

  @override
  String peBarcodeTaken(String code, String name) {
    return 'Штрихкод $code уже у товара «$name».';
  }

  @override
  String peBarcodeTwice(String code) {
    return 'Штрихкод $code указан дважды.';
  }

  @override
  String get peScan => 'Сканировать';

  @override
  String pePackQty(String qty) {
    return 'в упаковке $qty';
  }

  @override
  String get peNoAccess =>
      'Править товар может сотрудник с правом «Склад» или администратор.';
}
