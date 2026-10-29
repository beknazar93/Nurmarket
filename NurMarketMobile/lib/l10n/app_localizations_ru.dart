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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n чека',
      many: '$n чеков',
      few: '$n чека',
      one: '$n чек',
    );
    return '$_temp0';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: 'Заканчиваются $n товара',
      many: 'Заканчиваются $n товаров',
      few: 'Заканчиваются $n товара',
      one: 'Заканчивается $n товар',
    );
    return '$_temp0';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n позиции',
      many: '$n позиций',
      few: '$n позиции',
      one: '$n позиция',
    );
    return '$_temp0';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n варианта · $qty шт',
      many: '$n вариантов · $qty шт',
      few: '$n варианта · $qty шт',
      one: '$n вариант · $qty шт',
    );
    return '$_temp0';
  }

  @override
  String variantsSaved(int n) {
    return 'Сохранено вариантов: $n';
  }

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
  String get dayToday => 'Сегодня';

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
  String get chartBestDay => 'Лучший день';

  @override
  String get chartWorstDay => 'Самый слабый день';

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
  String get advisorOnline => 'Отвечает по данным вашего магазина';

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
      'Ключа ИИ нет. Добавьте его в «Настройка ИИ» → «Ключи Gemini» на этом телефоне (или в программе владельца: Нур Советник → ⚙ → Модели ИИ и ключи)';

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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: 'Загрузить $n товара на склад',
      many: 'Загрузить $n товаров на склад',
      few: 'Загрузить $n товара на склад',
      one: 'Загрузить $n товар на склад',
    );
    return '$_temp0';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: 'К оплате · $n позиции',
      many: 'К оплате · $n позиций',
      few: 'К оплате · $n позиции',
      one: 'К оплате · $n позиция',
    );
    return '$_temp0';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n товара · $qty шт',
      many: '$n товаров · $qty шт',
      few: '$n товара · $qty шт',
      one: '$n товар · $qty шт',
    );
    return '$_temp0';
  }

  @override
  String wasPieces(String qty) {
    return 'было $qty';
  }

  @override
  String get unknownBarcode => 'Неизвестный штрихкод';

  @override
  String unknownBarcodeHint(String code) {
    return '$code · не найден ни в каталоге, ни в справочнике NurCRM. Заведите товар — код никуда не отправляется';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: 'с $time · $n чека',
      many: 'с $time · $n чеков',
      few: 'с $time · $n чека',
      one: 'с $time · $n чек',
    );
    return '$_temp0';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: 'Сначала должны уйти на сервер $n чека — нужна связь.',
      many: 'Сначала должны уйти на сервер $n чеков — нужна связь.',
      few: 'Сначала должны уйти на сервер $n чека — нужна связь.',
      one: 'Сначала должен уйти на сервер $n чек — нужна связь.',
    );
    return '$_temp0';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other:
          'На телефоне $n неотправленные записи. После выхода они останутся и уйдут при следующем входе этого же аккаунта. Выйти?',
      many:
          'На телефоне $n неотправленных записей. После выхода они останутся и уйдут при следующем входе этого же аккаунта. Выйти?',
      few:
          'На телефоне $n неотправленные записи. После выхода они останутся и уйдут при следующем входе этого же аккаунта. Выйти?',
      one:
          'На телефоне $n неотправленная запись. После выхода она останется и уйдёт при следующем входе этого же аккаунта. Выйти?',
    );
    return '$_temp0';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n позиции · пора заказать',
      many: '$n позиций · пора заказать',
      few: '$n позиции · пора заказать',
      one: '$n позиция · пора заказать',
    );
    return '$_temp0';
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
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: 'Продолжить пересчёт ($n товара)',
      many: 'Продолжить пересчёт ($n товаров)',
      few: 'Продолжить пересчёт ($n товара)',
      one: 'Продолжить пересчёт ($n товар)',
    );
    return '$_temp0';
  }

  @override
  String get countedCash => 'Пересчитано';

  @override
  String get openingCashLabel => 'На начало смены';

  @override
  String closeShiftFailed(int n) {
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other:
          'Сервер не принял $n чека — они не попадут в отчёт смены. Отправьте их снова или снимите в «Синхронизации».',
      many:
          'Сервер не принял $n чеков — они не попадут в отчёт смены. Отправьте их снова или снимите в «Синхронизации».',
      few:
          'Сервер не принял $n чека — они не попадут в отчёт смены. Отправьте их снова или снимите в «Синхронизации».',
      one:
          'Сервер не принял $n чек — он не попадёт в отчёт смены. Отправьте его снова или снимите в «Синхронизации».',
    );
    return '$_temp0';
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
  String get bcGenTooltip => 'Сгенерировать штрихкод';

  @override
  String get bcGenReplace => 'Заменить текущий штрихкод новым?';

  @override
  String get bcGenDo => 'Сгенерировать';

  @override
  String pePackQty(String qty) {
    return 'в упаковке $qty';
  }

  @override
  String get peNoAccess =>
      'Править товар может сотрудник с правом «Склад» или администратор.';

  @override
  String get smartNoLink =>
      'Нет связи с сервером — ничего не проведено. Список остался на экране, повторите, когда появится интернет.';

  @override
  String smartPartial(int done, int total, int left) {
    return 'Проведено $done из $total, не проведено $left — нажмите «Загрузить» ещё раз: проведённое не прибавится повторно.';
  }

  @override
  String get smartPendingReceipt =>
      'Прошлый приход поставщику остался без ответа — сначала повторите его через «В приёмку с поставщиком», иначе остаток может прибавиться дважды.';

  @override
  String get smartPendingStock =>
      'Для части строк нет ответа сервера — сначала нажмите «Загрузить» ещё раз: так остаток не задвоится.';

  @override
  String get smartRowLocked =>
      'Строка уже отправлена на сервер — товар и название менять нельзя, повторите отправку.';

  @override
  String smartRowRejected(String reason) {
    return 'Отклонено: $reason';
  }

  @override
  String get smartRowUnknown =>
      'Нет ответа сервера — повторите: остаток не задвоится.';

  @override
  String payDebtLeft(String sum) {
    return 'Останется долга: $sum';
  }

  @override
  String get debtReceiptTitle => 'Квитанция об оплате долга';

  @override
  String get returnsTitle => 'Возвраты';

  @override
  String get shDebtPayments => 'Оплата долгов';

  @override
  String get shDebtPrepay => 'Предоплата по долгу';

  @override
  String get exitPressAgain => 'Нажмите ещё раз, чтобы выйти';

  @override
  String get createProductWithCode => 'Создать товар с этим кодом';

  @override
  String get printLabel => 'Напечатать этикетку';

  @override
  String get openingCashField => 'Сумма в кассе на начало смены';

  @override
  String get advisorGreeting =>
      'Здравствуйте! Я Нур Советник. Спросите о продажах, долгах или остатках — отвечу по данным вашего магазина.';

  @override
  String get a11yBack => 'Назад';

  @override
  String get a11yShowPassword => 'Показать пароль';

  @override
  String get a11yHidePassword => 'Скрыть пароль';

  @override
  String get a11yScanner => 'Сканер';

  @override
  String get a11yCallAi => 'Позвонить ИИ';

  @override
  String get stockNone => 'нет';

  @override
  String get cartEmptyHint =>
      'Добавьте товар: найдите, отсканируйте или нажмите плитку';

  @override
  String get toProductsBtn => 'К товарам';

  @override
  String get closeBtn => 'Закрыть';

  @override
  String clientDebtLine(String sum) {
    return 'Долг: $sum сом';
  }

  @override
  String get apiNoToken => 'Сервер не вернул токен.';

  @override
  String get apiSessionExpired => 'Сессия истекла — войдите снова.';

  @override
  String get apiTimeout => 'Сервер не ответил вовремя.';

  @override
  String get apiNoConnection => 'Нет связи с сервером.';

  @override
  String apiServerError(String code) {
    return 'Ошибка сервера $code.';
  }

  @override
  String get apiNetworkError => 'Ошибка сети.';

  @override
  String get adminNoLink => 'нет связи';

  @override
  String printLabelAsk(String name, String code) {
    return 'Напечатать этикетку для «$name»? Штрихкод $code';
  }

  @override
  String get notNow => 'Не сейчас';

  @override
  String get bcGenTaken => 'Этот код уже занят на сервере — выдан новый.';

  @override
  String get tapePrint => 'Печать';

  @override
  String get tapeSend => 'Отправить';

  @override
  String get zReport => 'Z-итог смены';

  @override
  String get xReportNote => 'X-отчёт — промежуточный: смена не закрыта.';

  @override
  String get shSalesBlock => 'Продажи';

  @override
  String get shCashBlock => 'Деньги в кассе';

  @override
  String get shPrepayIncluded => 'в наличных/безнале — предоплата по долгу';

  @override
  String get shReturnsUnknown => 'Возвраты смены не загружены — нет связи.';

  @override
  String shReturnsPartial(int n) {
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n возврата',
      many: '$n возвратов',
      few: '$n возврата',
      one: '$n возврат',
    );
    return 'Частично: $_temp0 без кассира в ответе сервера — не учтено. Сверьте на сайте.';
  }

  @override
  String returnsCountN(int n) {
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n возврата',
      many: '$n возвратов',
      few: '$n возврата',
      one: '$n возврат',
    );
    return '$_temp0';
  }

  @override
  String get shReturnsCash => 'Возвраты наличными';

  @override
  String get shCalcRow => 'Расчёт по строкам';

  @override
  String shMismatch(String sum) {
    return 'Расчёт по строкам расходится с сервером на $sum сом — полагайтесь на число сервера.';
  }

  @override
  String get shNoSplit =>
      'Разбивка по возвратам и оплате долгов недоступна — «Должно быть в кассе» взято с сервера.';

  @override
  String get shPartial =>
      'Часть данных не загрузилась (нет связи) — обновите экран.';

  @override
  String get debtReceiptBefore => 'Долг до оплаты';

  @override
  String get debtReceiptMethod => 'Способ оплаты';

  @override
  String get debtReceiptAccepted => 'Принято';

  @override
  String get debtReceiptCleared => 'Долг погашен полностью.';

  @override
  String debtReceiptAcceptedLine(String sum, String left) {
    return 'Принято $sum, остаток $left';
  }

  @override
  String get returnReasonTitle => 'Причина возврата (необязательно)';

  @override
  String get returnReasonLabelShort => 'Причина';

  @override
  String get returnReasonDefect => 'Брак';

  @override
  String get returnReasonNotFit => 'Не подошёл';

  @override
  String get returnReasonCashier => 'Ошибка кассира';

  @override
  String get listCachedNote =>
      'Нет связи — показан последний сохранённый список.';

  @override
  String get checksLoadFailed => 'Не удалось загрузить чеки';

  @override
  String get debtsLoadFailed => 'Не удалось загрузить долги';

  @override
  String get callMinimize => 'Свернуть';

  @override
  String get callAiListens => 'ИИ слушает';

  @override
  String get callInterrupted => 'Перебили — слушаю';

  @override
  String get callExtend => 'Ещё 10 минут';

  @override
  String get callExtended => 'Звонок продлён на 10 минут';

  @override
  String get callNoLink => 'Нет связи с ИИ';

  @override
  String get callNoLinkWork => 'Нет связи с ИИ, работайте на экране';

  @override
  String callNoLinkEnds(int n) {
    return 'Завершится через $n с, если связь не вернётся';
  }

  @override
  String get callNoLinkHelp => 'Корзина, поиск и склад работают без ИИ.';

  @override
  String get callSlowServer => 'Сервер отвечает медленно…';

  @override
  String get callStillLoading => 'Данные ещё грузятся — скажу, когда придут';

  @override
  String get callSearching => 'Ищу…';

  @override
  String get callSpeakingShort => 'Говорю…';

  @override
  String get callHush => 'Замолчи';

  @override
  String get callOpenFull =>
      'Звонок с Нур Советником — развернуть; удержание — «Замолчи»';

  @override
  String aiSays(String text) {
    return 'ИИ: $text';
  }

  @override
  String get aiUndo => 'Отменить';

  @override
  String get aiCantUndo => 'отменить нельзя';

  @override
  String get aiPickTitle => 'Какой вариант?';

  @override
  String get aiAmbiguousHint => 'Скажите номер или нажмите';

  @override
  String get aiNotThat => 'Не то';

  @override
  String aiYesNumber(String num) {
    return 'Да, $num';
  }

  @override
  String get aiNoRepeat => 'Нет, скажу снова';

  @override
  String aiConfirmDo(String what) {
    return 'Сделать: $what';
  }

  @override
  String get aiDo => 'Сделать';

  @override
  String get aiDont => 'Не надо';

  @override
  String get aiOrSayYes => 'или скажите «да»';

  @override
  String get aiModeAskMoney => 'Спрашивать перед деньгами';

  @override
  String get aiWhatPay => 'оплата';

  @override
  String get aiWhatReturn => 'возврат';

  @override
  String get aiWhatReceive => 'проведение прихода';

  @override
  String get aiWhatWriteOff => 'списание';

  @override
  String get aiWhatCount => 'ревизия';

  @override
  String get aiWhatCloseShift => 'закрытие смены';

  @override
  String get aiWhatSalary => 'выплата сотруднику';

  @override
  String get aiWhatDebt => 'погашение долга';

  @override
  String get aiWhatDelete => 'удаление товара';

  @override
  String get aiWhatPrice => 'смена цены';

  @override
  String get aiWhatCash => 'внесение или изъятие из кассы';

  @override
  String get aiWhatOther => 'действие';

  @override
  String get tblBigger => 'Крупнее';

  @override
  String get tblSmaller => 'Мельче';

  @override
  String get tblToChat => 'В чат';

  @override
  String get tblPdf => 'PDF';

  @override
  String get tblShare => 'Поделиться';

  @override
  String get tblPin => 'Закрепить';

  @override
  String get tblPinned => 'Закреплено';

  @override
  String tblMore(int n) {
    return 'Показать ещё $n';
  }

  @override
  String tblMoreLeft(int n) {
    return 'осталось $n';
  }

  @override
  String get tblAllShown => 'Все строки показаны';

  @override
  String tblSource(String source, String time) {
    return 'данные программы · $source · $time';
  }

  @override
  String get tblEmpty => 'Пока пусто';

  @override
  String tblEmptyFilter(String q) {
    return 'По запросу «$q» ничего не нашлось';
  }

  @override
  String get tblClearFilter => 'Снять фильтр';

  @override
  String get tblOpenObject => 'Открыть';

  @override
  String tblRowsN(int n) {
    String _temp0 = intl.Intl.pluralLogic(
      n,
      locale: localeName,
      other: '$n строки',
      many: '$n строк',
      few: '$n строки',
      one: '$n строка',
    );
    return '$_temp0';
  }

  @override
  String get tblPrev => 'Предыдущая таблица';

  @override
  String get tblNext => 'Следующая таблица';

  @override
  String get tblCollapse => 'Свернуть панель';

  @override
  String get tblClose => 'Закрыть таблицу';

  @override
  String get tblSentChat => 'Таблица добавлена в чат';

  @override
  String get tblPdfFailed => 'Не удалось сделать PDF';

  @override
  String get vcCamera => 'Камера';

  @override
  String get vcCameraOn => 'Камера вкл.';

  @override
  String get vcCameraOff => 'Камера выкл.';

  @override
  String get vcSwitchCamera => 'Другая камера';

  @override
  String get vcTorch => 'Фонарик';

  @override
  String get vcRecording => 'Камера';

  @override
  String get vcAiSees => 'ИИ видит';

  @override
  String get vcEconomy => 'Экономия трафика';

  @override
  String get vcEconomyHint => '1 кадр в 3 секунды и только когда вы говорите';

  @override
  String get vcStarting => 'Включаю камеру…';

  @override
  String get vcSearching => 'Ищу в каталоге…';

  @override
  String get vcWhatIsIt => 'Что это?';

  @override
  String get vcInvoice => 'Накладная';

  @override
  String get vcShelf => 'Полка';

  @override
  String get vcPriceTag => 'Ценник';

  @override
  String get wizAddProduct => 'Добавить товар';

  @override
  String get wizTitle => 'Добавление товара';

  @override
  String get wizSlotFront => 'Спереди';

  @override
  String get wizSlotBack => 'Сзади';

  @override
  String get wizSlotSide => 'Сбоку';

  @override
  String get wizSlotCode => 'Штрихкод';

  @override
  String wizShootSide(String side) {
    return 'Снять: $side';
  }

  @override
  String get wizCreate => 'Создать товар';

  @override
  String wizDraft(String name, String price) {
    return '«$name» · $price сом';
  }

  @override
  String get wizHint => 'Скажите «готово» или нажмите «Снять»';

  @override
  String wizDuplicate(String name) {
    return 'Уже есть: «$name»';
  }

  @override
  String wizPhotoAlt(String side) {
    return 'Снимок: $side';
  }

  @override
  String get wizEnterCode => 'Ввести штрихкод';

  @override
  String get wizEnterCodeHint => 'Отсканируйте сканером или введите цифры';

  @override
  String get vcCardProduct => 'Товар';

  @override
  String get vcNotFound =>
      'В каталоге не нашёл. Поднесите ближе или покажите штрихкод';

  @override
  String get vcExact => 'точно';

  @override
  String get vcByBarcode => 'штрихкод';

  @override
  String get vcByPhoto => 'по фото';

  @override
  String get vcLooksLike => 'Похоже на:';

  @override
  String get vcPickHint => 'нажмите, чтобы выбрать';

  @override
  String vcPrice(String sum) {
    return 'Цена: $sum сом';
  }

  @override
  String vcStock(String qty) {
    return 'Остаток: $qty';
  }

  @override
  String vcInvoiceSummary(int n, int known, int fresh) {
    return 'Строк: $n · в каталоге: $known · новых: $fresh';
  }

  @override
  String vcOpenReceive(int n) {
    return 'Открыть приход ($n)';
  }

  @override
  String get vcReceiveEndsCall =>
      'Звонок завершится, разговор сохранится в чате';

  @override
  String get vcNewItemsHint =>
      'Новых товаров нет в каталоге — заведите их через «Склад → Накладная»';

  @override
  String get vcNoProcure => 'Приход проводит администратор';

  @override
  String get vcShelfHint => 'ИИ считает на глаз — перепроверьте перед ревизией';

  @override
  String get vcShelfCols => 'видно · в программе';

  @override
  String get vcShelfThing => 'товар';

  @override
  String get vcOnTag => 'На ценнике';

  @override
  String get vcInProgram => 'В программе';

  @override
  String vcTagMismatch(String tag, String sum) {
    return 'На ценнике $tag сом, в программе $sum сом — не совпадает';
  }

  @override
  String get vcTagOk => 'Цена на ценнике совпадает с программой';

  @override
  String get vcTagFixHint => 'Цену здесь не правим — через карточку товара';

  @override
  String vcMoreLines(int n) {
    return 'ещё $n строк';
  }

  @override
  String get vcNewTag => 'новый';

  @override
  String get vcConsentTitle => 'Включить камеру в разговоре?';

  @override
  String get vcConsentBody =>
      'Пока камера включена, кадры (примерно 1 в секунду) уходят в Google Gemini по ключу ИИ вашего магазина. Они не сохраняются ни на телефоне, ни в NurCRM — в чате останется только текст разговора. На бесплатном ключе Google может использовать данные для улучшения своих моделей. Не показывайте лица покупателей, банковские карты и чужие документы.';

  @override
  String get vcConsentPtSends =>
      'Пока камера включена, кадры (примерно 1 в секунду) уходят в Google Gemini по ключу ИИ вашего магазина.';

  @override
  String get vcConsentPtSaved =>
      'Они не сохраняются ни на телефоне, ни в NurCRM — в чате останется только текст разговора.';

  @override
  String get vcConsentPtFree =>
      'На бесплатном ключе Google может использовать данные для улучшения своих моделей.';

  @override
  String get vcConsentPtWarn =>
      'Не показывайте лица покупателей, банковские карты и чужие документы.';

  @override
  String get vcConsentAgree => 'Включить камеру';

  @override
  String get vcConsentLater => 'Не сейчас';

  @override
  String vcCost(int n) {
    return 'Видеозвонок длится до $n мин и расходует лимит ключа ИИ в несколько раз быстрее голосового';
  }

  @override
  String get aiAccTitle => 'Доступ к ИИ';

  @override
  String get aiAccSection => 'Кому разрешён ИИ';

  @override
  String aiAccLimitInfo(int n) {
    return 'Бесплатный лимит ИИ (Google Gemini) общий на весь проект: запросов в минуту и в день мало. Поэтому ИИ — Нур Советник, звонок, умная камера, накладная по фото, весы по виду — могут использовать не больше $n сотрудников магазина. Выберите, кому разрешить.';
  }

  @override
  String aiAccCounter(int a, int b) {
    return '$a из $b';
  }

  @override
  String aiAccLimitReached(int n) {
    return 'Можно выбрать не больше $n сотрудников. Чтобы дать доступ другому, сначала снимите галочку с кого-то из выбранных.';
  }

  @override
  String aiAccDenied(int n, String names) {
    return 'ИИ доступен только $n сотрудникам магазина: $names. Обратитесь к владельцу.';
  }

  @override
  String get aiAccDeniedNone =>
      'ИИ в магазине сейчас выключен для всех сотрудников. Обратитесь к владельцу.';

  @override
  String get aiAccRecommend =>
      'Рекомендуем ограничить ИИ двумя сотрудниками (общий бесплатный лимит)';

  @override
  String get aiAccConfigure => 'Настроить';

  @override
  String get aiAccSaved => 'Доступ к ИИ сохранён';

  @override
  String get aiAccOwnerTag => 'владелец';

  @override
  String get aiAccYouTag => 'вы';

  @override
  String get aiAccStaffNotLoaded =>
      'Список сотрудников не получен (нет связи?). Показаны только уже выбранные.';

  @override
  String get aiAccNobody =>
      'Никто не выбран — ИИ будет выключен для всех сотрудников.';

  @override
  String get aiAccManagerOnly => 'Список меняет владелец или администратор.';

  @override
  String get aiAccNoticeTitle => 'ИИ — общий лимит';

  @override
  String aiAccNoticeBody(int n) {
    return 'ИИ магазина работает на общем бесплатном лимите Google, поэтому право на него есть не больше чем у $n сотрудников — у вас оно есть. Не запускайте длинные звонки и лишние запросы без нужды.';
  }

  @override
  String get aiAccGotIt => 'Понятно';

  @override
  String get aiAccMineYes => 'У вас есть доступ к ИИ';

  @override
  String get aiAccMineNo => 'ИИ вам недоступен — обратитесь к владельцу';

  @override
  String get aiAccMineOpen => 'ИИ доступен всем сотрудникам (лимит не задан)';

  @override
  String get aiAccNotSet =>
      'Не задано — ИИ у всех сотрудников (владельцу и администратору — всегда)';

  @override
  String get assistantTitle => 'Помощник';

  @override
  String get assistantSub => 'режим голосового управления';

  @override
  String get aiModeDo => 'Выполнять по просьбе';

  @override
  String get aiModeDoHint =>
      'Открывает экраны, читает, показывает таблицы и делает, что вы попросили — сразу, с итогом вслух и кнопкой «Отменить».';

  @override
  String get aiModeAskHint =>
      'Как по просьбе, но перед оплатой, возвратом, приходом, списанием, ревизией, закрытием смены, выплатой, долгом, удалением и сменой цены — карточка «Сделать».';

  @override
  String get aiModeWatch => 'Только смотреть';

  @override
  String get aiModeWatchHint =>
      'Только экраны, чтение и таблицы. На просьбу что-то сделать ответит: «В этом режиме я не меняю данные — сделайте на экране или смените режим».';

  @override
  String get aiModeDefault => 'по умолчанию';

  @override
  String get aiModeSofterLocked => 'Мягче, чем задал владелец — недоступно';

  @override
  String get aiModeOwnerSet => 'Владелец задал для всех на этом телефоне:';

  @override
  String get aiModeStricterSelf => 'Для себя можно выбрать строже';

  @override
  String get aiModeNoVoice => 'Голосом режим не меняется — только здесь';

  @override
  String get aiModeAskWhatTitle => 'Перед чем спросит';

  @override
  String get aiModeAskNote =>
      'Карточка «Сделать: … сумма …» и одна кнопка — или голосом «да». Двойных подтверждений нет.';

  @override
  String get aiModeFooter => 'Права помощника — по роли пользователя';

  @override
  String get aiModeSaved => 'Режим сохранён';

  @override
  String get aiLogTitle => 'Действия ИИ';

  @override
  String get aiLogSubAll => 'все записи телефона';

  @override
  String get aiLogMoneyOnly => 'Только деньги';

  @override
  String get aiLogUndone => 'Отменённые';

  @override
  String get aiLogByDate => 'Фильтр по дате';

  @override
  String get dayYesterday => 'Вчера';

  @override
  String aiLogUndoneAt(String time) {
    return 'отменено $time';
  }

  @override
  String get aiLogCantUndoWriteOff => 'нельзя отменить · можно списать';

  @override
  String get aiUndoRequest => 'отмена — запросом владельцу';

  @override
  String get aiLogUndoGone => 'отмена была только в звонке';

  @override
  String aiLogUndoneToast(String what) {
    return 'Отменено: $what';
  }

  @override
  String get aiLogUndoConflict => 'Уже менялось после ИИ — проверьте на экране';

  @override
  String aiLogUndoFailed(String what) {
    return 'Не удалось отменить: $what. Проверьте связь и экран.';
  }

  @override
  String get aiLogUndoShift =>
      'Отменить нельзя: смена уже закрыта или в кассе меньше денег';

  @override
  String aiLogUndoPartial(int done, int total) {
    return 'Вернули $done из $total; остальное менялось после ИИ или не вышло';
  }

  @override
  String get aiLogEmpty => 'ИИ ещё ничего не делал на этом телефоне';

  @override
  String get aiLogEmptyHint =>
      'Позвоните Нур Советнику и скажите, что сделать — здесь появится каждая запись с кнопкой «Отменить»';

  @override
  String get aiLogCall => 'Позвонить советнику';

  @override
  String get aiLogMineOnly => 'Видны только ваши действия';

  @override
  String get aiLogFooter =>
      'Хранится на телефоне, до 2000 записей. Без ключей ИИ, звука и телефонов клиентов.';

  @override
  String get aiGroupNav => 'Экраны';

  @override
  String get aiGroupCart => 'Чек и заказы';

  @override
  String get aiGroupMoney => 'Деньги';

  @override
  String get aiGroupProduct => 'Товары';

  @override
  String get aiGroupTask => 'Задачи';

  @override
  String get aiGroupSalary => 'Зарплата';

  @override
  String get auditAi => 'ИИ';

  @override
  String get stfTasksTitle => 'Задачи';

  @override
  String stfTasksSub(int n, String late) {
    return 'Задач: $n · просрочено: $late';
  }

  @override
  String get stfMyTasksTitle => 'Мои задачи';

  @override
  String stfMySub(String name, String today, String late) {
    return '$name · на сегодня: $today · просрочено: $late';
  }

  @override
  String get stfFilterOverdue => 'Просрочено';

  @override
  String get stfFilterDone => 'Сделано';

  @override
  String get stfStNew => 'новая';

  @override
  String get stfStWork => 'в работе';

  @override
  String get stfStDone => 'сделано';

  @override
  String get stfOverdue => 'просрочена';

  @override
  String get stfNoDue => 'без срока';

  @override
  String get stfDue => 'Срок';

  @override
  String get stfRemind => 'Напомнить';

  @override
  String get stfUrgent => 'Срочная задача';

  @override
  String get stfNewTask => 'Новая задача';

  @override
  String get stfTaskField => 'Что сделать';

  @override
  String get stfTaskHint => 'Например: пересчитать полку с водой';

  @override
  String get stfAssignee => 'Исполнитель';

  @override
  String get stfAssigneeHint => 'Выберите сотрудника';

  @override
  String get stfCreate => 'Поставить задачу';

  @override
  String get stfNeedTitle => 'Напишите, что нужно сделать';

  @override
  String get stfNeedAssignee => 'Выберите исполнителя';

  @override
  String get stfTaskCreated => 'Задача поставлена';

  @override
  String get stfToWork => 'В работу';

  @override
  String get stfMarkDone => 'Сделано';

  @override
  String get stfReopen => 'Вернуть в работу';

  @override
  String get stfEditDue => 'Изменить срок';

  @override
  String get stfEditAssignee => 'Передать другому';

  @override
  String get stfDelete => 'Удалить';

  @override
  String stfDeleteAsk(String title) {
    return 'Удалить задачу «$title»?';
  }

  @override
  String get stfNoTasks => 'Задач нет';

  @override
  String get stfLoadFail => 'Не удалось загрузить задачи';

  @override
  String get stfPriorityHint =>
      'Срочность и напоминание пока видны только в приложении; на сайте у задачи может появиться служебная пометка — до обновления сервера';

  @override
  String get stfNoBackground =>
      'Фоновых уведомлений нет — напоминание покажется при открытии приложения';

  @override
  String stfOverduePlate(int n, String title) {
    return 'Просрочено: $n — «$title»';
  }

  @override
  String stfRemindPlate(String title) {
    return 'Напоминание: «$title»';
  }

  @override
  String stfTodayPlate(String today, String late) {
    return 'Задач на сегодня: $today · просрочено: $late';
  }

  @override
  String get stfSalaryTitle => 'Зарплата';

  @override
  String stfSalarySub(int n) {
    return 'сотрудников: $n';
  }

  @override
  String get stfPeriodMonth => 'Этот месяц';

  @override
  String get stfPeriodLast => 'Прошлый месяц';

  @override
  String get stfPeriodWeek => '7 дней';

  @override
  String get stfAccrued => 'Начислено';

  @override
  String get stfPaid => 'Выплачено';

  @override
  String get stfLeft => 'Осталось';

  @override
  String get stfColEmployee => 'Сотрудник';

  @override
  String get stfChecking => 'Проверяем выплаты…';

  @override
  String get stfPartialPlate =>
      'Проверено частично — сумма к выплате сама не подставляется.';

  @override
  String get stfTrustTimeout =>
      'Проверка прошлых выплат не успела завершиться — проверьте на сайте перед выплатой.';

  @override
  String get stfTrustJournal =>
      'Журнал выплат на телефоне не читается — выплаты закрыты.';

  @override
  String get stfTrustNotChecked =>
      'Не удалось проверить прошлые выплаты — проверьте на сайте перед выплатой.';

  @override
  String stfTrustUnassigned(String sum, int n) {
    return 'Есть расходы «Зарплата» без привязки к сотруднику: $sum сом ($n шт.) — проверьте на сайте.';
  }

  @override
  String get stfTrustPartial =>
      'Выплаты без статьи «Зарплата» или не все расходы видны — проверьте на сайте перед выплатой.';

  @override
  String get stfTrustPartialNotes =>
      'Часть выплат лишь частично попала в период — она не вычтена, проверьте.';

  @override
  String get stfTrustByName =>
      'Часть выплат найдена только по имени в названии расхода — проверьте на сайте перед выплатой.';

  @override
  String stfPartialNote(String sum, String period) {
    return 'Выплата $sum сом за период $period лишь частично попадает в этот период — не вычтена';
  }

  @override
  String get stfSalaryEmpty =>
      'Начислений за период нет: нет продаж или ни у кого не настроена схема оплаты.';

  @override
  String get stfSalaryDenied =>
      'Нет доступа к зарплате: у этого аккаунта нет права на аналитику сотрудников на сайте.';

  @override
  String get stfSalaryFail => 'Не удалось загрузить зарплату';

  @override
  String get stfSalaryHint =>
      'Начислено — расчёт сервера, как на компьютере и на сайте. «Выплачено» — расходы «Зарплата» за этот период.';

  @override
  String get stfLockedTag => 'выплата без ответа';

  @override
  String get stfPayBtn => 'Выплатить';

  @override
  String stfPayTitle(String name) {
    return 'Выплата: $name';
  }

  @override
  String get stfKindSalary => 'Зарплата';

  @override
  String get stfKindAdvance => 'Аванс';

  @override
  String get stfPayAmount => 'Сумма';

  @override
  String get stfPayEnterAmount => 'Введите сумму сами';

  @override
  String get stfPayBox => 'Касса';

  @override
  String get stfPayComment => 'Комментарий (необязательно)';

  @override
  String get stfPayNeedAmount => 'Введите сумму выплаты';

  @override
  String get stfPayNoCash =>
      'Не нашла кассу компании для выплаты — выплатите на сайте или в программе владельца.';

  @override
  String get stfSalesBoxWarn =>
      'Это касса продаж филиала — расход уменьшит её остаток.';

  @override
  String stfPayConfirm(String sum, String name, String box) {
    return 'Выплатить $sum сом сотруднику $name из кассы «$box»?';
  }

  @override
  String stfPayDone(String sum, String name) {
    return 'Выплачено $sum сом: $name';
  }

  @override
  String get stfPayFailed => 'Выплата не прошла.';

  @override
  String stfPayUnknown(String sum, String box) {
    return 'Ответ сервера не пришёл — выплата $sum сом (касса «$box») могла пройти. Повторять нельзя: откройте сотрудника и нажмите «Проверить на сервере».';
  }

  @override
  String stfPayLock(String sum, String when, String box) {
    return 'Прошлая выплата $sum сом ($when, касса «$box») осталась без ответа сервера. Новая выплата этому сотруднику закрыта, пока вы её не проверите.';
  }

  @override
  String get stfPayLockShort =>
      'Прошлая выплата этому сотруднику осталась без ответа сервера — новая закрыта, пока вы её не проверите.';

  @override
  String get stfPayDup =>
      'Такая же выплата только что уже прошла — повтор не проводится.';

  @override
  String get stfPayJournal =>
      'Журнал выплат на телефоне не читается — без него нет защиты от двойной выплаты, выплата не отправлена.';

  @override
  String get stfLockCheck => 'Проверить на сервере';

  @override
  String get stfLockCheckShort => 'Проверить';

  @override
  String get stfLockYes => 'Выплата ЕСТЬ на сайте';

  @override
  String get stfLockNo => 'Выплаты НЕТ на сайте';

  @override
  String get stfLockYesAsk => 'Выплата есть на сайте?';

  @override
  String get stfLockNoAsk => 'Выплаты нет на сайте?';

  @override
  String get stfLockResolveHint =>
      'Сверьте расход «Зарплата» на сайте NurCRM. Если нажать неверно, выплата будет учтена неправильно или пройдёт дважды.';

  @override
  String get stfLockFound => 'Выплата найдена на сервере и учтена.';

  @override
  String get stfLockNotFound =>
      'На сервере выплату не нашла — замок остаётся, решите сами после проверки на сайте.';

  @override
  String get stfConfirm => 'Подтвердить';

  @override
  String get stfRecentPayouts => 'Недавние выплаты с этого телефона';

  @override
  String get stfCancelBtn => 'Запросить отмену';

  @override
  String stfCancelAsk(String sum) {
    return 'Запросить отмену выплаты $sum сом?';
  }

  @override
  String get stfCancelHint =>
      'Выплату отменяет только владелец или бухгалтер на сайте NurCRM — с телефона уходит запрос.';

  @override
  String get stfCancelSend => 'Отправить запрос';

  @override
  String get stfCancelSent => 'Запрос на отмену отправлен владельцу.';

  @override
  String get stfSchemeBtn => 'Схема оплаты';

  @override
  String stfSchemeTitle(String name) {
    return 'Схема оплаты: $name';
  }

  @override
  String get stfSchemeSaved => 'Схема оплаты сохранена';

  @override
  String get stfSchemeSalary => 'Оклад';

  @override
  String get stfSchemePercent => 'Процент от продаж';

  @override
  String get stfSchemeSalaryPercent => 'Оклад + процент от продаж';

  @override
  String get stfSchemePerItem => 'За каждый проданный товар';

  @override
  String get stfSchemeSalaryItem => 'Оклад + за каждый товар';

  @override
  String get stfSchemePercentItem => 'Процент + за каждый товар';

  @override
  String get stfNoScheme => 'схема не настроена';

  @override
  String get stfMonthly => 'Оклад в месяц';

  @override
  String get stfPercent => 'Процент от продаж';

  @override
  String get stfPerItem => 'За каждый проданный товар';

  @override
  String get stfBonus => 'Начислено за продажи';

  @override
  String get gkOpen => 'Ключи Gemini (несколько аккаунтов)';

  @override
  String get gkTitle => 'Ключи Gemini';

  @override
  String get gkHint =>
      'Ключи используются по порядку: первый рабочий. Когда у ключа кончился лимит, ИИ сам берёт следующий. Список тот же, что на кассе и в программе владельца.';

  @override
  String get gkWarn =>
      'Правила Google могут запрещать обходить лимиты несколькими аккаунтами или проектами — есть риск блокировки ключей. Бесплатный уровень Google может использовать запросы для улучшения своих продуктов. Решаете вы.';

  @override
  String get gkViewOnly =>
      'Список ключей меняют владелец и администратор. Вам он показан без ключей целиком — ИИ работает по этому списку.';

  @override
  String gkWorkingCount(int a, int b) {
    return 'Рабочих ключей: $a из $b';
  }

  @override
  String get gkEmpty =>
      'Ключей нет. Добавьте первый — его выдаёт Google AI Studio.';

  @override
  String get gkAdd => 'Добавить ключ';

  @override
  String get gkAddBtn => 'Добавить';

  @override
  String get gkPasteKey => 'Вставьте ключ из Google AI Studio';

  @override
  String get gkLabelHint => 'Метка (необязательно)';

  @override
  String gkLabelDefault(int n) {
    return 'Аккаунт $n';
  }

  @override
  String get gkMax => 'Не больше 10 ключей.';

  @override
  String get gkDuplicate => 'Такой ключ уже есть в списке.';

  @override
  String get gkShow => 'Показать';

  @override
  String get gkHide => 'Скрыть';

  @override
  String get gkEnabled => 'Включён';

  @override
  String get gkMoveUp => 'Выше';

  @override
  String get gkMoveDown => 'Ниже';

  @override
  String get gkDelete => 'Удалить';

  @override
  String gkDeleteAsk(String name, String tail) {
    return 'Удалить ключ «$name» (…$tail)?';
  }

  @override
  String get gkStateWorking => 'рабочий';

  @override
  String gkStateExhausted(String time) {
    return 'исчерпан до $time';
  }

  @override
  String get gkStateInvalid => 'недействителен';

  @override
  String get gkStateDisabled => 'выключен';

  @override
  String get gkStateUnchecked => 'не проверен';

  @override
  String get gkCheckOk => 'Ключ рабочий ✓';

  @override
  String gkCheckFail(String err) {
    return 'Не удалось проверить: $err';
  }

  @override
  String get aiKeysSavedPhoneOnly =>
      'Сохранено на телефоне, но в облако не ушло — касса и программа владельца пока не увидят. Повторите позже.';

  @override
  String get klTitle => 'Связь с кассой';

  @override
  String get klProfileOff => 'Не подключена';

  @override
  String klKassaName(String name) {
    return 'Касса «$name»';
  }

  @override
  String get klScanQr => 'Сканировать QR на кассе';

  @override
  String get klScanQrHint =>
      'На компьютере с кассой откройте окно «Подключить телефон» и наведите камеру на QR-код. Телефон и касса должны быть в одной Wi-Fi сети, касса — запущена.';

  @override
  String get klScanQrTitle => 'Наведите камеру на QR-код на экране кассы';

  @override
  String get klNotKassaQr => 'Это не QR-код кассы NurMarket';

  @override
  String get klBadQr =>
      'Не удалось прочитать QR-код. Откройте на кассе окно «Подключить телефон» и сканируйте заново.';

  @override
  String get klQrVersion =>
      'Эта касса новее приложения. Обновите NurMarket на телефоне.';

  @override
  String get klManual => 'Ввести ссылку вручную';

  @override
  String get klManualHint => 'Вставьте текст QR-кода (nurmarket-link:…)';

  @override
  String get klConnect => 'Подключить';

  @override
  String klConnected(String name) {
    return 'Касса «$name» подключена';
  }

  @override
  String get klConnectedPlain => 'Касса подключена';

  @override
  String get klStUnpaired => 'Касса не подключена';

  @override
  String get klStUnknown => 'Связь не проверена';

  @override
  String get klStOnline => 'На связи';

  @override
  String get klStNotFound => 'Касса не найдена в этой сети';

  @override
  String get klStRevoked => 'Телефон отключён на кассе';

  @override
  String get klStBadTime => 'Проверьте время на телефоне';

  @override
  String get klStDisabled => 'Приём с телефонов выключен на кассе';

  @override
  String get klStRePair => 'Связь устарела — сканируйте QR на кассе заново';

  @override
  String get klChecking => 'Проверка связи…';

  @override
  String get klShiftOpen => 'Смена на кассе: открыта';

  @override
  String get klShiftClosed => 'Смена на кассе: закрыта';

  @override
  String get klCheckNow => 'Проверить связь';

  @override
  String get klDisconnect => 'Отключить';

  @override
  String klDisconnectAsk(String name) {
    return 'Отключить телефон от кассы «$name»? Подключить снова можно по QR-коду. На самой кассе телефон останется в списке — удалите его и там.';
  }

  @override
  String get klDisconnected => 'Телефон отключён от кассы';

  @override
  String get klScannerMode => 'Сканер для кассы';

  @override
  String get klScannerHint =>
      'Наведите на штрихкод — товар сразу попадёт в чек на кассе';

  @override
  String get klSetupAsk => 'Связь с кассой не настроена';

  @override
  String get klSetup => 'Настроить';

  @override
  String get klScSending => 'Отправка…';

  @override
  String get klScAdded => 'Добавлен';

  @override
  String get klScNotFound => 'Не найден';

  @override
  String get klScTillInactive => 'Окно кассы не открыто';

  @override
  String get klScQueued => 'Нет связи — в очереди';

  @override
  String get klScQueueFull => 'Очередь заполнена — код не принят';

  @override
  String get klScExpired => 'Не отправлен: касса не ответила';

  @override
  String get klScFailed => 'Касса не приняла код';

  @override
  String get klLastCode => 'Последний код';

  @override
  String klCounter(int n) {
    return 'Добавлено в чек: $n';
  }

  @override
  String klQueueLeft(int n) {
    return 'В очереди: $n';
  }

  @override
  String get globalSource => 'из справочника NurCRM';

  @override
  String globalFoundAsk(String name) {
    return 'В справочнике NurCRM есть: $name';
  }

  @override
  String get globalCatalogTitle => 'Справочник товаров';

  @override
  String globalCatalogStatus(String count, String date, String added) {
    return 'Справочник: $count товаров, обновлён $date, добавлено: $added';
  }

  @override
  String get globalCatalogOff => 'Справочник недоступен';

  @override
  String globalBulkFilled(int n) {
    return 'Названий подставлено из справочника NurCRM: $n';
  }

  @override
  String advisorQuota(String time) {
    return 'Лимит ИИ исчерпан до $time (общий с кассой и ботом). Можно добавить второй ключ из другого аккаунта Google.';
  }

  @override
  String get advisorQuotaLater =>
      'Лимит ИИ исчерпан (общий с кассой и ботом). Попробуйте позже или добавьте второй ключ из другого аккаунта Google.';

  @override
  String advisorKeyRejected(String time) {
    return 'Google отклонил ключ, повтор после $time. Проверьте ключ в «Настройка ИИ» → «Ключи Gemini».';
  }

  @override
  String get advisorTimeout =>
      'ИИ долго не отвечает. Проверьте интернет и нажмите «Повторить».';

  @override
  String gkLastError(String text) {
    return 'Последняя ошибка: $text';
  }

  @override
  String get aiAccOwnerNote =>
      'Вас нет в списке, но владельцу и администратору ИИ доступен всегда.';

  @override
  String get pePriceCheckTitle => 'Проверьте цену';

  @override
  String pePriceUpAsk(String now, String was, String times) {
    return 'Цена $now вместо $was — сохранить?\nЭто в $times раз больше прежней.';
  }

  @override
  String pePriceDownAsk(String now, String was, String times) {
    return 'Цена $now вместо $was — сохранить?\nЭто в $times раз меньше прежней.';
  }

  @override
  String peBelowCostAsk(String now, String cost) {
    return 'Цена $now ниже закупки $cost — продажа в убыток. Сохранить?';
  }

  @override
  String peMarkupAsk(String now, String cost, String pct) {
    return 'Цена $now при закупке $cost: наценка $pct %. Нет ли ошибки в цифрах? Сохранить?';
  }

  @override
  String get peFixPrice => 'Исправить';

  @override
  String get peSaveAsIs => 'Сохранить как есть';

  @override
  String get klSearching => 'Ищу кассу в сети Wi-Fi…';

  @override
  String get klHelpNotFound =>
      'Касса не отвечает. Проверьте: 1) касса запущена; 2) в настройках кассы включено «Принимать сканы с телефонов» (по умолчанию оно выключено); 3) телефон в той же Wi-Fi сети, что и компьютер с кассой, а не на мобильном интернете.';

  @override
  String get klHelpDisabled =>
      'На кассе выключен приём с телефонов. Включите «Принимать сканы с телефонов» в настройках кассы и нажмите «Проверить связь».';

  @override
  String get klHelpRePair =>
      'Касса не узнаёт этот телефон (его отключили или QR устарел). Нажмите «Сканировать QR заново» и наведите камеру на QR в окне «Подключить телефон» на кассе.';

  @override
  String get klHelpBadTime =>
      'Время на телефоне и на кассе расходится больше чем на 10 минут. Включите автоматическую дату и время на телефоне (и проверьте время на кассе), затем нажмите «Проверить связь».';

  @override
  String get klRescanQr => 'Сканировать QR заново';

  @override
  String get klScRefused =>
      'Код не отправлен — сначала восстановите связь с кассой';

  @override
  String get aiAccDraftNote =>
      'Список ещё не сохранён: сейчас ИИ доступен всем сотрудникам. Отметьте до 2 человек и нажмите «Сохранить» — у остальных ИИ выключится. Владельцу и администратору ИИ доступен всегда.';

  @override
  String get aiAccAlways =>
      'Владельцу и администратору ИИ доступен всегда, независимо от списка.';

  @override
  String get invTitle => 'Загрузить накладную';

  @override
  String get invSub => 'Excel, CSV или XML от поставщика';

  @override
  String get invHint =>
      'Выберите файл накладной от поставщика: Excel (.xlsx), CSV или XML (электронная накладная, счёт-фактура). Строки попадут в приход — вы проверите количество и цены и проведёте его как обычно.';

  @override
  String get invPick => 'Выбрать файл накладной';

  @override
  String get invPhotoHint =>
      'PDF и фото накладной читает «Умная камера» (нужен ключ ИИ)';

  @override
  String get invPhotoBtn => 'Накладная по фото';

  @override
  String get invReading => 'Читаем накладную…';

  @override
  String invFileName(String name) {
    return 'Файл: $name';
  }

  @override
  String get invOtherFile => 'Другой файл';

  @override
  String invDocLine(String num, String date) {
    return 'Накладная № $num от $date';
  }

  @override
  String invDocNum(String num) {
    return 'Накладная № $num';
  }

  @override
  String invDocDate(String date) {
    return 'Накладная от $date';
  }

  @override
  String get invDocNone => 'Номер и дата в файле не найдены';

  @override
  String invSupplierFile(String name) {
    return 'В файле: $name';
  }

  @override
  String invSupplierFileInn(String name, String inn) {
    return 'В файле: $name, ИНН $inn';
  }

  @override
  String invSupplierInnOnly(String inn) {
    return 'В файле ИНН: $inn';
  }

  @override
  String get invSupplierNoneInFile => 'В файле поставщик не указан';

  @override
  String get invSupByInn => 'Найден по ИНН';

  @override
  String get invSupByName => 'Найден по названию';

  @override
  String get invSupNotFound =>
      'Такого поставщика нет — выберите из списка или создайте';

  @override
  String get invSupCreate => 'Создать поставщика';

  @override
  String invSupCreated(String name) {
    return 'Поставщик «$name» создан';
  }

  @override
  String invSupFail(String text) {
    return 'Не удалось создать поставщика: $text';
  }

  @override
  String get invSupOffline => 'Нет связи: список поставщиков не загружен';

  @override
  String get invSupKept => 'Приход остался на прежнем поставщике';

  @override
  String get invDupPosted => 'Эта накладная уже загружалась и проведена';

  @override
  String get invDupPending =>
      'Эта накладная уже добавлена в приход, который ещё не проведён';

  @override
  String get invDupAnyway => 'Всё равно добавить';

  @override
  String invTotalLines(String sum) {
    return 'По строкам: $sum';
  }

  @override
  String invTotalFile(String sum) {
    return 'В файле: $sum';
  }

  @override
  String get invTotalOk => 'Итоги сходятся';

  @override
  String invTotalBad(String diff) {
    return 'Итоги не сходятся: разница $diff. Проверьте количество и цены.';
  }

  @override
  String get invTotalNone => 'Итог в файле не найден';

  @override
  String get invTotalVatHint =>
      'С НДС итоги сходятся — включите «Закупка с НДС»';

  @override
  String get invVat => 'Закупка с НДС';

  @override
  String get invVatSub => 'По умолчанию закупка берётся без НДС';

  @override
  String invCurrency(String cur) {
    return 'Валюта накладной: $cur. Вы работаете в сомах — проверьте цены: программа их не пересчитывает.';
  }

  @override
  String get invGenCodes => 'Новым товарам без штрихкода дать свой код';

  @override
  String get invGenCodesSub => 'Иначе они будут без штрихкода';

  @override
  String invSummary(int n, String stock, String fresh, String bad) {
    return 'Строк: $n · на складе: $stock · новых: $fresh · не распознано: $bad';
  }

  @override
  String get invStBarcode => 'На складе · по штрихкоду';

  @override
  String get invStArticle => 'На складе · по артикулу';

  @override
  String get invStName => 'На складе · по названию';

  @override
  String invStFuzzy(String name) {
    return 'Похоже на: $name';
  }

  @override
  String get invStCatalog => 'Новый · есть в справочнике NurCRM';

  @override
  String get invStManual => 'Новый · заполните вручную';

  @override
  String get invStBad => 'Не распознана';

  @override
  String get invBadNoName => 'Нет названия, штрихкода и артикула';

  @override
  String get invBadNoQty => 'Нет количества';

  @override
  String invUseCandidate(String name) {
    return 'Это «$name»';
  }

  @override
  String get invAsNew => 'Новый товар';

  @override
  String invSrcRow(int n) {
    return 'Строка файла: $n';
  }

  @override
  String invArticle(String code) {
    return 'Артикул: $code';
  }

  @override
  String get invCostMissing => 'Укажите закупку';

  @override
  String get invPriceMissing => 'Укажите цену продажи';

  @override
  String get invRemoveRow => 'Убрать строку';

  @override
  String invAdd(int n) {
    return 'Добавить в приход · $n';
  }

  @override
  String get invAddFix => 'Исправьте отмеченные строки';

  @override
  String get invAdding => 'Готовим строки…';

  @override
  String invMerged(int n) {
    return 'Повторы товара в накладной объединены: $n';
  }

  @override
  String get invErrTooBig => 'Файл больше 10 МБ — такую накладную не открыть';

  @override
  String get invErrEmpty => 'Файл пустой';

  @override
  String get invErrPdf =>
      'Это PDF или фото. Их читает «Умная камера» (кнопка ниже)';

  @override
  String get invErrXls =>
      'Старый формат Excel (.xls) не читается — сохраните файл как .xlsx или .csv';

  @override
  String get invErrBroken => 'Файл повреждён или это не накладная';

  @override
  String get invErrNoTable =>
      'В файле не нашли таблицу товаров: нужны столбцы «Наименование», «Количество» и «Цена»';

  @override
  String get invErrUnsafe =>
      'XML содержит DTD или внешние сущности — такой файл не открывается из соображений безопасности';

  @override
  String get invErrTooMany => 'Слишком много строк (больше 5000)';

  @override
  String get invErrFormat =>
      'Неподдерживаемый файл: нужен .xlsx, .csv или .xml';

  @override
  String get tsTitle => 'Режим тестировщика';

  @override
  String tsBuildLine(String version, String build) {
    return 'Версия $version (сборка $build)';
  }

  @override
  String get tsTestBuild => 'тестовая сборка';

  @override
  String get tsDevice => 'Устройство';

  @override
  String tsDeviceId(String id) {
    return 'ID устройства: $id';
  }

  @override
  String get tsDeviceNameTitle => 'Название устройства';

  @override
  String get tsDeviceNameHint => 'Например: телефон кассира';

  @override
  String get tsRename => 'Назвать устройство';

  @override
  String tsScreenLine(String w, String h, String scale, String lang) {
    return 'Экран $w×$h dp, шрифт ×$scale, язык $lang';
  }

  @override
  String tsSummary(String done, String total, String issues) {
    return 'Проверено $done из $total, проблем: $issues';
  }

  @override
  String get tsVersionFilter => 'Версия';

  @override
  String tsVersionCurrent(String v) {
    return '$v (текущая)';
  }

  @override
  String tsAreaProgress(String done, String total) {
    return '$done из $total';
  }

  @override
  String get tsChanged => 'Что изменилось';

  @override
  String get tsHowTo => 'Как проверить';

  @override
  String get tsSevMoney => 'Деньги';

  @override
  String get tsSevImportant => 'Важное';

  @override
  String get tsSevNormal => 'Обычное';

  @override
  String get tsMarkOk => 'Проверено';

  @override
  String get tsMarkIssue => 'Есть проблема';

  @override
  String get tsNotChecked => 'Не проверено';

  @override
  String tsCheckedAt(String when) {
    return 'Проверено $when';
  }

  @override
  String tsIssueAt(String when) {
    return 'Проблема $when';
  }

  @override
  String get tsNoComment => 'Без комментария';

  @override
  String get tsUndoHint => 'Нажмите ещё раз, чтобы снять отметку';

  @override
  String get tsCountNone => 'Пока не проверено ни на одном устройстве';

  @override
  String get tsCountLocalOk => 'Проверено: 1 устройство (это)';

  @override
  String get tsCountLocalIssue => 'Проблема: 1 устройство (это)';

  @override
  String tsCountShared(String ok, String issue) {
    return 'Проверено на устройствах: $ok · проблема на устройствах: $issue';
  }

  @override
  String get tsSyncOff => 'Общий счётчик недоступен (нужны права или сервер)';

  @override
  String get tsRefresh => 'Обновить счётчики';

  @override
  String get tsCopyReport => 'Скопировать отчёт';

  @override
  String get tsShareReport => 'Отправить отчёт';

  @override
  String get tsCopied => 'Отчёт скопирован — вставьте его в сообщение';

  @override
  String get tsIssueTitle => 'Что не так?';

  @override
  String get tsIssueHint => 'Опишите проблему';

  @override
  String get tsAttachShot => 'Прикрепить скриншот';

  @override
  String get tsShotAttached => 'Скриншот прикреплён';

  @override
  String get tsShotRemove => 'Убрать скриншот';

  @override
  String get tsEditComment => 'Изменить комментарий';

  @override
  String get tsEmpty => 'Для этой версии нет пунктов';

  @override
  String get tsCodeTitle => 'Код тестировщика';

  @override
  String get tsCodeHint => 'Введите код';

  @override
  String get tsCodeOn => 'Режим тестировщика включён: нажмите на логотип 5 раз';

  @override
  String get tsCodeBad => 'Код не подходит';

  @override
  String tsTapMore(int n) {
    return 'Режим тестировщика: осталось нажатий — $n';
  }

  @override
  String get pdNoRights =>
      'Удалять товары может сотрудник с правом «Склад» или администратор.';

  @override
  String get pdGone => 'Товара уже нет на сервере — убрал его и с телефона.';

  @override
  String pdRefused(String msg) {
    return 'Сервер не разрешил удалить товар: $msg. Если он уже в продажах или документах, спишите остаток вместо удаления.';
  }

  @override
  String get pdOffline =>
      'Нет связи с сервером — товар НЕ удалён. Повторите, когда сеть появится.';

  @override
  String pdFailed(String msg) {
    return 'Товар не удалён: $msg';
  }

  @override
  String pdQueueBlock(int n) {
    return 'С этим товаром есть неотправленные операции: $n. Дождитесь отправки и повторите.';
  }

  @override
  String get pdAuditFail => 'Не удалён';

  @override
  String pdAsk(String name) {
    return 'Удалить «$name»? Товар исчезнет из каталога NurCRM, с кассы и из мобилки. Отменить это нельзя.';
  }

  @override
  String pdStockWarn(String qty, String unit) {
    return 'На складе $qty $unit. Остаток пропадёт вместе с товаром. Если товар не нужен, лучше сначала списать остаток.';
  }

  @override
  String get pdTypeName => 'Для подтверждения введите название товара:';

  @override
  String get pdTypeHint => 'Регистр не важен';

  @override
  String get ctTitle => 'Умная камера';

  @override
  String get ctAngleFront => 'спереди';

  @override
  String get ctAngleBack => 'сзади';

  @override
  String get ctAngleSide => 'сбоку';

  @override
  String ctCount(int n, int a) {
    return 'Эталонов: $n из $a';
  }

  @override
  String get ctNone => 'Не обучена: по фото товар пока не узнаётся точно';

  @override
  String get ctTrained => 'Обучена: при продаже товар узнаётся по этим снимкам';

  @override
  String get ctTrain => 'Обучить камеру';

  @override
  String get ctReset => 'Сбросить эталоны';

  @override
  String get ctResetAsk =>
      'Убрать все эталонные снимки этого товара с телефона? Фото в NurCRM останутся.';

  @override
  String get ctSaved => 'Эталон сохранён';

  @override
  String get ctRemoved => 'Эталон убран';

  @override
  String ctShotHint(String angle) {
    return 'Товар целиком в рамке, $angle';
  }

  @override
  String get ctBarcodeNone => 'Штрихкода нет — отсканируйте его';

  @override
  String ctBarcodeOk(String code) {
    return 'Штрихкод: $code';
  }

  @override
  String get ctNote =>
      'Снимки хранятся на этом телефоне. «Обучить камеру» — полный курс до 5 снимков и штрихкод; он ещё добавляет фото товара в NurCRM.';

  @override
  String ctSlot(int n) {
    return 'Фото $n';
  }

  @override
  String ctSlotAdd(int n, String angle) {
    return 'Фото $n: снять $angle';
  }

  @override
  String ctSlotRetake(int n) {
    return 'Фото $n: переснять';
  }

  @override
  String get ctRemove => 'Убрать';

  @override
  String get camPickTitle => 'Похоже на — выберите, если это он';

  @override
  String get camFromGallery => 'Из галереи';

  @override
  String get camFromFile => 'Из файла';

  @override
  String get camInvoiceSrcHint =>
      'Накладную можно сфотографировать, выбрать из галереи или из файла: фото, PDF, Excel, CSV, XML.';

  @override
  String camPageOf(int n, int a) {
    return 'Фото $n из $a';
  }

  @override
  String get camFileNoProcure =>
      'Загрузка файла накладной — с правом «Закупки». Сфотографируйте накладную — строки появятся здесь.';

  @override
  String get camFileTooBig =>
      'Файл больше 8 МБ — выберите меньше или сфотографируйте накладную.';

  @override
  String get camFileBadType =>
      'Такой файл не подходит. Нужны Excel (xlsx), CSV, XML, TXT, PDF или фото.';

  @override
  String get camFileReadFail => 'Не удалось прочитать файл.';

  @override
  String recvFoundGlobal(String name) {
    return 'Найдено в справочнике NurCRM: «$name»';
  }

  @override
  String get recvAddProduct => 'Завести товар';

  @override
  String recvLooksLike(String name) {
    return 'Похоже на ваш «$name»';
  }

  @override
  String get recvBindCode => 'Привязать штрихкод';

  @override
  String recvTypo(String name, String stored, String code) {
    return 'Опечатка в штрихкоде карточки «$name»: записано $stored, верный $code';
  }

  @override
  String get recvFixCode => 'Исправить';

  @override
  String recvBound(String name) {
    return 'Штрихкод привязан к «$name»';
  }

  @override
  String get recvFixed => 'Штрихкод исправлен';

  @override
  String get recvNoRight =>
      'Завести, привязать или исправить может сотрудник с правом «Склад».';

  @override
  String recvSearching(String code) {
    return 'Ищу $code на сервере и в справочнике…';
  }

  @override
  String get pkTitle => 'Упаковки / поштучная продажа';

  @override
  String get pkSwitch => 'Продавать поштучно из упаковки';

  @override
  String get pkSwitchHint =>
      'Например, пачка сигарет или упаковка воды 6 шт: можно продать и упаковкой, и штукой. Остаток ведётся в упаковках.';

  @override
  String get pkName => 'Название упаковки';

  @override
  String get pkNameHint => 'Пачка, блок, коробка';

  @override
  String get pkQty => 'Штук в упаковке';

  @override
  String pkPackPrice(String price) {
    return 'Цена упаковки: $price сом (это цена продажи выше)';
  }

  @override
  String get pkPiecePrice => 'Цена штуки';

  @override
  String get pkAuto => 'Авто';

  @override
  String get pkAutoTip => 'Посчитать цену штуки заново: цена упаковки ÷ штук';

  @override
  String pkMarkup(int n) {
    return 'Наценка штуки: $n %';
  }

  @override
  String pkBelowCost(String cost) {
    return 'Штука дешевле закупки ($cost сом)';
  }

  @override
  String get pkQtyBad => 'Штук в упаковке — целое число, не меньше 2';

  @override
  String get pkPieceBad => 'Укажите цену штуки';

  @override
  String pkMismatch(String qty, String piece, String sum, String pack) {
    return '$qty шт × $piece сом = $sum сом, а упаковка стоит $pack сом — расхождение больше 5 %. Сохранить так?';
  }

  @override
  String get pkMismatchTitle => 'Цены штуки и упаковки расходятся';

  @override
  String get pkWeightBlocked =>
      'Весовой товар (кг) нельзя продавать поштучно из упаковки.';

  @override
  String get pkMulti =>
      'У товара несколько упаковок — их правят на сайте или кассе.';

  @override
  String get pkQuick => 'Быстро';

  @override
  String get pkWordPack => 'Пачка';

  @override
  String get pkWordPackage => 'Упаковка';

  @override
  String get pkWordCrate => 'Ящик';

  @override
  String get pkWordBlock => 'Блок';

  @override
  String get pkWordBox => 'Коробка';

  @override
  String get pkPcs => 'шт';

  @override
  String get pkChoiceTitle => 'Как продать?';

  @override
  String get pkChoicePiece => 'Штука';

  @override
  String pkChoiceStock(String stock) {
    return 'Остаток: $stock';
  }

  @override
  String pkLinePiece(String name) {
    return 'шт. из «$name»';
  }

  @override
  String get pkQtyInPacks =>
      'Количество вводится в упаковках: 12 — это 12 пачек, штуки продаются из них долями.';

  @override
  String get pkPackShort => 'уп.';

  @override
  String get pkNoPackId =>
      'Упаковка ещё не сохранена на сервере — сначала обновите каталог.';

  @override
  String get camNotThis => 'Не тот товар';

  @override
  String get camNotThisDone =>
      'Запомненное название забыто — выберите товар заново.';

  @override
  String get camAliasesTitle => 'Запомненные названия:';

  @override
  String get camAliasForget => 'Забыть название';

  @override
  String get invDupPostedNow =>
      'Эта накладная уже проведена раньше (на этом телефоне или на кассе). Если провести приход ещё раз, остаток и долг поставщику удвоятся.';

  @override
  String get invSupOtherDoc =>
      'В этом приходе уже есть накладная другого поставщика. Сначала проведите её, потом загрузите эту.';

  @override
  String get invSupConflictTitle => 'Чей это приход?';

  @override
  String invSupConflictBody(String doc, String keep) {
    return 'Накладная: $doc. В приходе выбран: $keep. Приход и долг запишутся на одного поставщика — выберите.';
  }

  @override
  String invSupConflictKeep(String name) {
    return 'Оставить: $name';
  }

  @override
  String invSupConflictDoc(String name) {
    return 'Из накладной: $name';
  }

  @override
  String recvCostAvg(int n) {
    return 'Закупка пересчитана как средняя по количеству у позиций: $n';
  }

  @override
  String invPackWarn(String unit, String name, String pu) {
    return 'В накладной «$unit» — это упаковка, а товар «$name» считается в «$pu». Выберите, как принять: иначе упаковки попадут в остаток как штуки по цене упаковки.';
  }

  @override
  String get invPackSize => 'Штук в упаковке';

  @override
  String get invPackToPieces => 'Пересчитать в штуки';

  @override
  String get invPackAsIs => 'Принять как есть';

  @override
  String get invPackBad =>
      'Укажите, сколько штук в упаковке: целое число от 2.';

  @override
  String invPackDonePieces(int n, String qty, String cost) {
    return '1 упак. = $n шт → $qty шт по $cost сом';
  }

  @override
  String invPackDoneAsIs(String qty, String unit, String cost) {
    return 'Принято как есть: остаток вырастет на $qty $unit, закупка $cost сом за $unit';
  }

  @override
  String get invPackChange => 'Изменить';

  @override
  String get pieceSaleMode => 'Продавать поштучно из упаковки';

  @override
  String get pieceSaleModeSub =>
      'Выключите, если сервер списывает целые пачки вместо штук. Тогда такой товар продаётся только упаковкой.';

  @override
  String get pieceSaleTesterWarn =>
      'Тест: поштучная продажа из упаковки ещё не подтверждена сервером. После оплаты проверьте остаток товара.';

  @override
  String pieceStockMismatch(String list) {
    return 'Остаток на сервере после продажи штуки не сошёлся: $list. Проверьте остаток товара и сообщите администратору.';
  }

  @override
  String get klScStale => 'Код устарел — отсканируйте снова';

  @override
  String get klScBusy => 'Идёт оплата — сканируйте после неё';

  @override
  String invCurrencyOk(String cur) {
    return 'Цены в $cur, а приход ведётся в сомах. Добавить как есть (1 $cur = 1 сом) — я проверил суммы';
  }

  @override
  String get invGrossOnly =>
      'В файле только цены с НДС. Закупка пересчитана без НДС по ставке строки (нет ставки — взята цена с НДС). Проверьте закупку.';

  @override
  String invUnitCheck(String inv, String item) {
    return 'Проверьте · единица: в накладной «$inv», у товара «$item»';
  }

  @override
  String get empAdd => 'Новый сотрудник';

  @override
  String get empCard => 'Карточка сотрудника';

  @override
  String get empSectionData => 'Данные сотрудника';

  @override
  String get empSectionRights => 'Доступы';

  @override
  String get empFirstName => 'Имя';

  @override
  String get empLastName => 'Фамилия';

  @override
  String get empMiddleName => 'Отчество';

  @override
  String get empEmail => 'Почта (логин для входа)';

  @override
  String get empPhone => 'Телефон';

  @override
  String get empTrack => 'Номер сотрудника';

  @override
  String get empRole => 'Должность';

  @override
  String get empBirth => 'Дата рождения';

  @override
  String get empHire => 'Дата найма';

  @override
  String get empAddress => 'Адрес';

  @override
  String get empComment => 'Комментарий';

  @override
  String get empClearDate => 'Очистить дату';

  @override
  String get empRequired => 'Заполните поле';

  @override
  String get empEmailBad => 'Введите почту полностью, например aibek@mail.kg';

  @override
  String get empPhoneBad => 'Телефон: от 9 до 15 цифр, можно с «+»';

  @override
  String get empSaved => 'Сотрудник сохранён.';

  @override
  String get empCreated => 'Сотрудник добавлен.';

  @override
  String get empNothingChanged => 'Ничего не изменено.';

  @override
  String get empSearch => 'Поиск: имя, почта, телефон';

  @override
  String get empFilterActive => 'Работают';

  @override
  String get empFilterDismissed => 'Уволены';

  @override
  String get empDismissedTag => 'уволен';

  @override
  String empCount(int n) {
    return 'Сотрудников: $n';
  }

  @override
  String get empNothingFound => 'Никого не найдено.';

  @override
  String get empDismiss => 'Уволить';

  @override
  String get empRestore => 'Восстановить';

  @override
  String empDismissAsk(String name) {
    return 'Уволить $name? Он больше не сможет войти в программу. Продажи и смены сохранятся.';
  }

  @override
  String empRestoreAsk(String name) {
    return 'Восстановить $name? Он снова сможет войти в программу.';
  }

  @override
  String get empDismissedDone => 'Сотрудник уволен.';

  @override
  String get empRestoredDone => 'Сотрудник восстановлен.';

  @override
  String get empDismissStill =>
      'Сервер принял запрос, но сотрудник остался в списке. Удалите его на сайте app.nurcrm.kg.';

  @override
  String get empResetPwd => 'Сбросить пароль';

  @override
  String empResetAsk(String name) {
    return 'Сбросить пароль $name? Старый пароль перестанет работать, новый покажем один раз.';
  }

  @override
  String get empPasswordTitle => 'Пароль сотрудника';

  @override
  String empPasswordNote(String email) {
    return 'Логин: $email. Пароль показывается один раз — передайте его сотруднику сейчас.';
  }

  @override
  String get empCopy => 'Копировать пароль';

  @override
  String get empCopied => 'Скопировано.';

  @override
  String get empFeatureMissing => 'Сервер пока не поддерживает это действие.';

  @override
  String get empListDenied =>
      'Нет права «Сотрудники» — список недоступен. Обратитесь к владельцу.';

  @override
  String get empErrBad => 'Сервер не принял данные. Проверьте поля.';

  @override
  String get empErrEmail => 'Эта почта уже занята или записана неверно.';

  @override
  String get empErr403 => 'Нет права на это действие. Обратитесь к владельцу.';

  @override
  String get empErr404 => 'Сотрудник не найден на сервере — обновите список.';

  @override
  String get empErr409 =>
      'Такой сотрудник уже есть или данные успели измениться. Обновите список.';

  @override
  String get empErr429 =>
      'Слишком много запросов. Подождите минуту и повторите.';

  @override
  String get empErr5xx =>
      'Ошибка сервера. Данные не изменены — повторите позже.';

  @override
  String get rightUsePos => 'Работа на кассе';

  @override
  String get rightSalaryView => 'Зарплата: смотреть';

  @override
  String get rightSalaryEdit => 'Зарплата: выплачивать';

  @override
  String get rightEmployeesView => 'Сотрудники: смотреть';

  @override
  String get rightEmployeesEdit => 'Сотрудники: править';

  @override
  String get rightsReadOnly =>
      'Доступы можно только смотреть: правит их владелец или тот, у кого открыты «Настройки».';

  @override
  String get unitNamePcs => 'Штука';

  @override
  String get unitNameKg => 'Килограмм';

  @override
  String get unitNameG => 'Грамм';

  @override
  String get unitNameL => 'Литр';

  @override
  String get unitNameMl => 'Миллилитр';

  @override
  String get unitNameM => 'Метр';

  @override
  String get unitNamePack => 'Упаковка';

  @override
  String get unitNamePair => 'Пара';

  @override
  String get unitNameSet => 'Комплект';

  @override
  String get unitCustom => 'Своя';

  @override
  String get unitCustomTitle => 'Своя единица';

  @override
  String get unitCustomHint => 'Например: рулон, бутылка';

  @override
  String unitHintFrac(String name) {
    return '$name: можно дробное количество, например 0,5';
  }

  @override
  String unitHintWhole(String name) {
    return '$name: только целое количество';
  }

  @override
  String get unitHintCustom => 'Своя единица: количество можно дробное';

  @override
  String nuQtyWhole(String unit, String qty) {
    return 'Для «$unit» количество должно быть целым (сейчас $qty). Выберите кг, л, м… или впишите целое число.';
  }

  @override
  String get nuQtyNegative => 'Количество не может быть меньше нуля.';

  @override
  String get nuPriceNeed => 'Укажите цену продажи';

  @override
  String get bkHintUnits =>
      'Единица: шт, кг, г, л, мл, м, упак, пара, компл (понимаются и «штука», «литр», pcs, kg, l, дана, dona…). Цена и количество — можно дробные: 12,75; 0,5.';

  @override
  String get bkErrName => 'нет названия';

  @override
  String get bkErrPrice => 'укажите цену продажи';

  @override
  String bkErrUnit(String unit) {
    return 'единица «$unit» не распознана — выберите шт, кг, л…';
  }

  @override
  String bkErrQtyWhole(String qty, String unit) {
    return 'количество $qty должно быть целым для «$unit»';
  }

  @override
  String get bkErrQtyNeg => 'количество меньше нуля';

  @override
  String get bkWarnQty => 'количество не указано — остаток 0';

  @override
  String get bkSetUnit => 'Установить единицу';

  @override
  String get bkSetPrice => 'Установить цену';

  @override
  String get bkSetQty => 'Установить количество';

  @override
  String get bkSelectAll => 'Выбрать все';

  @override
  String get bkSelectBad => 'С ошибками';

  @override
  String get bkSelectNone => 'Снять выбор';

  @override
  String bkSelectedN(int n) {
    return 'Выбрано строк: $n';
  }

  @override
  String get bkSelectFirst =>
      'Отметьте строки галочками, чтобы применить ко многим сразу.';

  @override
  String bkApplyTo(int n) {
    return 'Применить к $n';
  }

  @override
  String get bkEditRow => 'Строка товара';

  @override
  String bkResult(int a, int b) {
    return 'Готово $a, с ошибками $b';
  }

  @override
  String get barcodeDigitsHint => 'Только цифры, от 4 до 14';

  @override
  String get barcodeBadLen => 'Штрихкод — от 4 до 14 цифр, без букв';

  @override
  String get infK => 'тыс';

  @override
  String get infM => 'млн';

  @override
  String get infB => 'млрд';

  @override
  String get infNoData => 'Нет данных за период';

  @override
  String get infNoDataHint => 'Выберите другой период или проведите продажи';

  @override
  String infUpdatedAt(String time) {
    return 'Обновлено $time';
  }

  @override
  String get infCompare => 'Сравнить с прошлым периодом';

  @override
  String get infPeriodCustom => 'Свой период';

  @override
  String get infVsYesterday => 'к вчера';

  @override
  String get infVsPrevWeek => 'к прошлой неделе';

  @override
  String get infVsPrevMonth => 'к прошлому месяцу';

  @override
  String get infVsPrevPeriod => 'к прошлому периоду';

  @override
  String infDeltaUpSem(String pct, String vs) {
    return 'Выше на $pct % $vs';
  }

  @override
  String infDeltaDownSem(String pct, String vs) {
    return 'Ниже на $pct % $vs';
  }

  @override
  String infDeltaSameSem(String vs) {
    return 'Без изменений $vs';
  }

  @override
  String get infDeltaNew => 'новое';

  @override
  String get infOther => 'Прочее';

  @override
  String get infShare => 'Доля';

  @override
  String infDescEmpty(String name) {
    return '$name: нет данных';
  }

  @override
  String infDescOne(String name, String label, String value, String unit) {
    return '$name: $label — $value $unit';
  }

  @override
  String infDescBars(
    String name,
    String max,
    String unit,
    String maxLabel,
    String min,
    String minLabel,
    String total,
  ) {
    return '$name: максимум $max $unit ($maxLabel), минимум $min $unit ($minLabel), всего $total $unit';
  }

  @override
  String get infHowCalc => 'Как посчитано';

  @override
  String get infHowRevenue =>
      'Выручка — сумма чеков со статусом «оплачен» и «частичный возврат» за период, как в программе на компьютере и на сайте. Чеки в долг и отменённые сюда не входят.';

  @override
  String get infHowChecks =>
      'Чеки — число оплаченных чеков за период (с частичным возвратом тоже).';

  @override
  String get infHowAvg => 'Средний чек = выручка ÷ число чеков за период.';

  @override
  String get infHowProfit =>
      'Прибыль = выручка − закупочная стоимость проданных товаров (валовая прибыль сервера). Расходы магазина сюда не входят.';

  @override
  String get infHowShiftCash =>
      'Должно быть в кассе = наличные на начало + наличные продажи + оплата долгов наличными + внесения − возвраты наличными − изъятия. Число даёт сервер.';

  @override
  String get infHowDebts =>
      'Долги — сумма, которую клиенты должны магазину сейчас (список должников сервера).';

  @override
  String get infHowLow =>
      'Заканчивается — товары (не услуги), у которых остаток меньше 10 или не больше минимального остатка, заданного для товара.';

  @override
  String get infHowPayments =>
      'Способы оплаты — сколько выручки пришло наличными, картой, переводом и в долг; проценты считаются от суммы всех способов.';

  @override
  String get infHowTop =>
      'Топ товаров — товары с наибольшей выручкой за период; полоса — доля от лучшего товара.';

  @override
  String get scanModeLabel => 'Режим';

  @override
  String get scanModeTitle => 'Режим сканера';

  @override
  String scanModeA11y(String mode) {
    return 'Режим сканера: $mode. Нажмите, чтобы выбрать';
  }

  @override
  String get scanModeSaleSub => 'Сканируйте — товар сразу в чек';

  @override
  String get scanModeLookSub => 'Узнать товар по фото, без штрихкода';

  @override
  String get scanModeWeighSub => 'Весовой товар: вес с табло весов';

  @override
  String get scanModeReceiveSub => 'Принять товар на склад';

  @override
  String get scanModeKassaSub => 'Штрихкоды уходят на кассу по Wi-Fi';

  @override
  String get infNoDelta => '— н/д';

  @override
  String get infNoCompare => '— сравнения нет';

  @override
  String get infCompareShort => 'Сравнить';

  @override
  String get infLoadFailedTitle => 'Не удалось загрузить';

  @override
  String infLoadFailedAt(String time) {
    return 'Нет связи с сервером. Показаны данные на $time.';
  }

  @override
  String get infLoadFailedNoData => 'Нет связи с сервером.';

  @override
  String get infTapHint => 'Нажмите на столбец — подробности';

  @override
  String get infTapRingHint => 'Нажмите на сектор — подробности';

  @override
  String get infPrevBar => 'Прошлый период';

  @override
  String infOfPeriod(String pct) {
    return '$pct % периода';
  }

  @override
  String get infHoursNote =>
      'Выручка по часам: сервер пока не отдаёт разбивку по часам — раздел появится позже.';

  @override
  String get infExpensesNote =>
      'Расходы магазина в отчёт сервера не входят — показаны выручка, себестоимость и прибыль.';

  @override
  String get infCost => 'Себестоимость';

  @override
  String infMargin(String pct) {
    return 'Рентабельность $pct %';
  }

  @override
  String get infRevenueCostProfit => 'Выручка, себестоимость и прибыль';

  @override
  String get infDebtConcTitle => 'Кто должен больше всех';

  @override
  String infDebtConcLine(String pct) {
    return 'Три крупнейших должника — $pct % долга';
  }

  @override
  String get infDebtAgeNote =>
      'Давность долга: сервер не присылает даты долгов — показана концентрация по должникам.';

  @override
  String get infStockTitle => 'Остатки на складе';

  @override
  String get infStockOut => 'Нет в наличии';

  @override
  String get infStockLow => 'Заканчивается';

  @override
  String get infStockOk => 'Хватает';

  @override
  String get infPaidWith => 'Чем платили';

  @override
  String get infTotalWord => 'Всего';

  @override
  String get infShiftNoSales => 'В смене пока нет продаж';

  @override
  String get infHowTitle => 'Как это посчитано';

  @override
  String get infRangePick => 'Выбрать даты';
}
