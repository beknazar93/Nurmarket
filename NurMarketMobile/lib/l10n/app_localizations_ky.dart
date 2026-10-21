// ignore: unused_import
import 'package:intl/intl.dart' as intl;

import 'app_localizations.dart';

// ignore_for_file: type=lint

/// The translations for Kirghiz Kyrgyz (`ky`).
class AppLocalizationsKy extends AppLocalizations {
  AppLocalizationsKy([String locale = 'ky']) : super(locale);

  @override
  String get som => 'сом';

  @override
  String get cancel => 'Жокко чыгаруу';

  @override
  String get done => 'Даяр';

  @override
  String get save => 'Сактоо';

  @override
  String get retry => 'Кайталоо';

  @override
  String get all => 'Баары';

  @override
  String get loginTagline => 'Кассадагы жана вебдеги кирүү';

  @override
  String get loginLabel => 'Логин же телефон';

  @override
  String get loginHint => 'почта же телефон';

  @override
  String get passwordLabel => 'Сырсөз';

  @override
  String get signIn => 'Кирүү';

  @override
  String get loginEmpty => 'Логин менен сырсөздү киргизиңиз.';

  @override
  String get loginWrong => 'Логин же сырсөз туура эмес.';

  @override
  String get chooseCashbox => 'Кассаны тандаңыз';

  @override
  String get cashbox => 'Касса';

  @override
  String get tabHome => 'Башкы';

  @override
  String get tabSale => 'Сатуу';

  @override
  String get tabScan => 'Сканер';

  @override
  String get tabChecks => 'Чектер';

  @override
  String get tabStock => 'Кампа';

  @override
  String get newCheck => 'Жаңы чек';

  @override
  String get scanTileSub => 'Штрихкод боюнча баа жана калдык';

  @override
  String get checksTileSub => 'Жөнөтүү, басып чыгаруу';

  @override
  String get stockTileSub => 'Калдыктар жана кабыл алуу';

  @override
  String shiftOpenSince(String time) {
    return 'Смена ачык · $time';
  }

  @override
  String get shiftClosed => 'Смена жабык';

  @override
  String get mySalesForShift => 'Сменадагы менин сатууларым';

  @override
  String checksCount(int n) {
    return '$n чек';
  }

  @override
  String get openShift => 'Сменаны ачуу';

  @override
  String get closeShift => 'Сменаны жабуу';

  @override
  String get openingCashHint => 'Башында кассада канча акча';

  @override
  String get openShiftFirst => 'Адегенде башкы бетте сменаны ачыңыз.';

  @override
  String get offlineWorking =>
      'Сервер менен байланыш жок — сатуулар телефондо сакталат жана өзү жөнөтүлөт.';

  @override
  String runningOut(int n) {
    return '$n товар түгөнүп баратат';
  }

  @override
  String get runningOutShort => 'Түгөнүп баратат';

  @override
  String get outOfStock => 'Жок';

  @override
  String get tellAdmin => 'Администраторго билдириңиз';

  @override
  String get searchProduct => 'Товар же штрихкод';

  @override
  String get favorites => 'Тандалгандар';

  @override
  String get nothingFound => 'Эч нерсе табылган жок';

  @override
  String get inCheck => 'Чекте';

  @override
  String positions(int n) {
    return '$n позиция';
  }

  @override
  String get swipeToRemove => 'Алып салуу үчүн сапты солго сүрүңүз';

  @override
  String payAmount(String sum) {
    return '$sum сом төлөө';
  }

  @override
  String get orderDiscount => 'Чекке арзандатуу';

  @override
  String get discountPercentHint => 'Пайыз, мисалы 10';

  @override
  String get extraService => 'Кош. кызмат';

  @override
  String get clearCheck => 'Чекти тазалоо';

  @override
  String get price => 'Баасы';

  @override
  String weightOf(String name) {
    return 'Салмагы: $name';
  }

  @override
  String get kg => 'килограмм, мисалы 0,5';

  @override
  String get lineDiscount => 'Товарга арзандатуу';

  @override
  String get sumHint => 'Сомдогу сумма';

  @override
  String get discount => 'Арзандатуу';

  @override
  String barcodeNotFound(String code) {
    return '$code штрихкоду каталогдо жок.';
  }

  @override
  String get cameraUnavailable =>
      'Камера жеткиликсиз. Телефондун жөндөөлөрүндө камерага уруксат бериңиз же штрихкодду кол менен киргизиңиз.';

  @override
  String get scanHint => 'Штрихкодго багыттаңыз — товар өзү кошулат';

  @override
  String scanReceiveHint(int n) {
    return 'Сканерленди: $n';
  }

  @override
  String get toCheck => 'Чекке';

  @override
  String get checkPrice => 'Бааны билүү';

  @override
  String get receiving => 'Кабыл алуу';

  @override
  String doneCount(int n) {
    return 'Даяр · $n';
  }

  @override
  String inStock(String qty, String unit) {
    return 'кампада $qty $unit';
  }

  @override
  String get scanContinuous => 'Ырастоосуз катары менен сканерлөө';

  @override
  String checkN(int n) {
    return 'Чек · $n';
  }

  @override
  String get addToCheck => 'Чекке кошуу';

  @override
  String get addedAutomatically => 'Чекке кошулду';

  @override
  String get profileTitle => 'Профиль';

  @override
  String get discountInPercent => 'Пайыз';

  @override
  String get discountInSom => 'Сумма, сом';

  @override
  String discountResult(String sum) {
    return 'Арзандатуу менен: $sum сом';
  }

  @override
  String get clothingMode => 'Сатууда өлчөмдөр жана түстөр';

  @override
  String get clothingModeSub => 'Кийим жана бут кийим дүкөнү үчүн';

  @override
  String get chooseSize => 'Өлчөм жана түс';

  @override
  String get colorLabel => 'Түс';

  @override
  String get sizeLabel => 'Өлчөм';

  @override
  String toCheckSize(String size) {
    return 'Чекке · $size';
  }

  @override
  String sizeLowStock(String qty) {
    return 'Бул өлчөмдүн калдыгы — $qty даана. Баары бир кошулсунбу?';
  }

  @override
  String get rentals => 'Прокат';

  @override
  String get rentalNew => 'Жаңы прокат';

  @override
  String get rentalActive => 'Колдо';

  @override
  String get rentalOverdue => 'Мөөнөтү өттү';

  @override
  String get rentalReturn => 'Кайтарымды кабыл алуу';

  @override
  String get rentalReturned => 'Кайтарым кабыл алынды.';

  @override
  String get rentalCondOk => 'Бүтүн';

  @override
  String get rentalCondDamaged => 'Бузулган';

  @override
  String get rentalPenalty => 'Күрөөдөн айып';

  @override
  String get rentalClient => 'Кардар';

  @override
  String get rentalItem => 'Буюм';

  @override
  String get rentalUntil => 'Күнгө чейин';

  @override
  String get rentalPerDay => 'Суткасына баа';

  @override
  String rentalDays(int n) {
    return '$n сутка';
  }

  @override
  String get rentalDeposit => 'Акчалай күрөө';

  @override
  String get rentalDocument => 'Документ менен күрөө';

  @override
  String get rentalDocumentHint => 'Паспорт ID…, аты-жөнү';

  @override
  String get rentalCreate => 'Берүү жана чекке';

  @override
  String rentalCreated(int n) {
    return '№$n прокат тариздолду — баасы чекте.';
  }

  @override
  String rentalLine(int n, String days) {
    return 'Прокат №$n · $days сут.';
  }

  @override
  String get chooseClient => 'Кардарды тандоо';

  @override
  String get chooseItem => 'Буюмду тандаңыз';

  @override
  String get marginPercent => 'Үстөк, %';

  @override
  String get marginSum => 'Үстөк, сом';

  @override
  String get kindProduct => 'Товар';

  @override
  String get kindClothes => 'Кийим';

  @override
  String get kindService => 'Кызмат';

  @override
  String get sizesLabel => 'Өлчөмдөр';

  @override
  String get colorsLabel => 'Түстөр';

  @override
  String get addColor => 'Түс кошуу';

  @override
  String get colorName => 'Түстүн аталышы';

  @override
  String get colorSizeGrid => 'Түс × өлчөм — канча даана';

  @override
  String variantsSummary(int n, String qty) {
    return '$n вариант · $qty даана';
  }

  @override
  String variantsSaved(int n) {
    return 'Сакталды: $n вариант';
  }

  @override
  String get durationLabel => 'Узактыгы';

  @override
  String get mastersLabel => 'Чеберлер · өз баасы жана пайызы';

  @override
  String get masterAdd => 'Чебер кошуу';

  @override
  String get masterName => 'Чебердин аты';

  @override
  String get masterPercent => '% чеберге';

  @override
  String get consumablesLabel => 'Кампадагы материалдар';

  @override
  String get consumableAdd => 'Материал кошуу';

  @override
  String get onlineBooking => 'Сайтта жана ботто онлайн жазылуу';

  @override
  String get serviceLocalNote =>
      'Чеберлер, узактык жана материалдар ушул телефондо сакталат, сервердин API\'си болгонго чейин. Кызмат — NurCRM каталогунда.';

  @override
  String get saveService => 'Кызматты сактоо';

  @override
  String get findInternet => 'Интернеттен издөө';

  @override
  String get internetFound => 'NurCRM базасынан табылды';

  @override
  String get barcodeMatched => 'штрихкод дал келди · так';

  @override
  String get takeLabel => 'Алуу';

  @override
  String get categoryLabel => 'Категория';

  @override
  String get brandLabel => 'Бренд';

  @override
  String get fillProduct => 'Товарды толтуруу';

  @override
  String get searchInBrowser => 'Браузерден издөө';

  @override
  String get priceNotTaken => 'Интернеттен баа алынбайт — өзүңүздүкүн жазыңыз.';

  @override
  String get bookingsTitle => 'Кызматтар · жазылуу';

  @override
  String get dayToday => 'Бүгүн';

  @override
  String get dayTomorrow => 'Эртең';

  @override
  String get allMasters => 'Бардык чеберлер';

  @override
  String get bookNew => 'Жазуу';

  @override
  String get bookCheckout => 'Эсептешүү';

  @override
  String get bookArrived => 'келди';

  @override
  String get bookBooked => 'жазылды';

  @override
  String get bookPaid => 'төлөндү';

  @override
  String get bookFree => 'бош';

  @override
  String get bookMarkArrived => '«Келди» деп белгилөө';

  @override
  String get bookRemind => 'WhatsApp\'та эскертүү';

  @override
  String get bookCancel => 'Жазылууну жокко чыгаруу';

  @override
  String get bookTime => 'Убакыт';

  @override
  String get bookClash => 'Чеберде бул убакытта жазылуу бар.';

  @override
  String get bookNoMasters => 'Чеберлер азырынча жок — кызмат түзүүдө кошуңуз.';

  @override
  String get bookLocalNote =>
      'Жазылуулар ушул телефондо сакталат, сервердин API\'си болгонго чейин. Кызмат төлөмү — кадимки NurCRM чеги.';

  @override
  String bookReminder(String name, String when, String master) {
    return 'Саламатсызбы, $name! $when $master чеберине жазылууңузду эскертебиз.';
  }

  @override
  String get pricesTitle => 'Сатуу баалары';

  @override
  String pricesSub(int a, int b) {
    return 'сатып алуу өзгөрдү: $a · жаңы: $b';
  }

  @override
  String get ruleLabel => 'Эреже';

  @override
  String get roundTo => 'Тегеректөө';

  @override
  String get applyToAll => 'Бардык саптарга';

  @override
  String costWas(String was, String now) {
    return 'сатып алуу $was → $now';
  }

  @override
  String get newItemShort => 'жаңы';

  @override
  String get applyPrices => 'Бааларды колдонуу';

  @override
  String postSummary(int a, String qty, int b, int n, String sum) {
    return 'Позиция: $a · даана: $qty\nЖаңы товар: $b\nБаасы өзгөрөт: $n\nСатып алуу: $sum';
  }

  @override
  String get quotesTitle => 'КП жана эсептер';

  @override
  String get quoteNew => 'Коммерциялык сунуш';

  @override
  String get invoiceNew => 'Төлөм эсеби';

  @override
  String get quotesWaiting => 'Жооп күтүүдө';

  @override
  String get invoicesUnpaid => 'Төлөнгөн эмес';

  @override
  String get invoicesOverdue => 'Мөөнөтү өттү';

  @override
  String get filterAll => 'Баары';

  @override
  String get filterQuotes => 'КП';

  @override
  String get filterInvoices => 'Эсептер';

  @override
  String get stDraft => 'Долбоор';

  @override
  String get stSent => 'Жөнөтүлдү';

  @override
  String get stAccepted => 'Кабыл алынды → эсеп';

  @override
  String get stDeclined => 'Четке кагылды';

  @override
  String get stIssued => 'Төлөмдү күтөт';

  @override
  String get stPartial => 'Жарым-жартылай төлөндү';

  @override
  String get stPaid => 'Төлөндү';

  @override
  String validUntil(String date) {
    return '$date чейин жарактуу';
  }

  @override
  String payUntil(String date) {
    return '$date чейин төлөө';
  }

  @override
  String get fromCart => 'Учурдагы чектен';

  @override
  String get useWholesale => 'Дүң баалар';

  @override
  String get innLabel => 'ИНН';

  @override
  String get docSendPdf => 'PDF жөнөтүү';

  @override
  String get invoiceFromQuote => 'КП боюнча эсеп чыгаруу';

  @override
  String get takePayment => 'Төлөм кабыл алуу';

  @override
  String get sellFromDoc => 'Сатууну өткөрүү';

  @override
  String get markDeclined => 'Кардар баш тартты';

  @override
  String get editDoc => 'Өзгөртүү';

  @override
  String get deleteDoc => 'Документти өчүрүү';

  @override
  String paidOf(String paid, String total) {
    return '$total ичинен $paid төлөндү';
  }

  @override
  String basedOnQuote(String number) {
    return '$number боюнча';
  }

  @override
  String get quotesLocalNote =>
      'КП жана эсептер ПКдагы ээсинин программасы жана компаниянын башка телефондору менен жалпы. Эсеп боюнча сатуу — кадимки NurCRM чеги.';

  @override
  String get addLine => 'Товар кошуу';

  @override
  String get docDiscount => 'Арзандатуу, сом';

  @override
  String get esfTitle => 'ККМ жана ЭСФ';

  @override
  String esfQueue(int n) {
    return 'Кезекте $n чек — байланыш пайда болгондо кетет';
  }

  @override
  String get esfOnline => 'Касса байланышта, кезек бош';

  @override
  String get esfIssue => 'ЭСФ жазуу';

  @override
  String get esfBuyerInn => 'Сатып алуучунун ИНН';

  @override
  String get esfBuyerName => 'Сатып алуучу';

  @override
  String get esfPayForm => 'Төлөм формасы';

  @override
  String get esfCash => 'Накталай';

  @override
  String get esfNonCash => 'Накталай эмес';

  @override
  String get esfContract => 'Келишим № жана күнү';

  @override
  String get esfSum => 'Сумма, сом';

  @override
  String get esfDraftSaved => 'ЭСФ долбоору сакталды';

  @override
  String get esfSendTax => 'Салыкка жөнөтүү';

  @override
  String get esfDrafts => 'ЭСФ долбоорлору';

  @override
  String get esfIncoming => 'Жеткирүүчүлөрдөн келген ЭСФ';

  @override
  String get esfStub =>
      'Салыкка жөнөтүү жана келген ЭСФ NurCRM сервери салыкты туташтырганда иштейт. Азыр долбоор даярдасаңыз болот.';

  @override
  String get smartCam => 'Акылдуу камера';

  @override
  String get byLook => 'Көрүнүшү боюнча';

  @override
  String get byLookHint =>
      'Штрихкодсуз товарды тартыңыз — ИИ аны таанып, каталогдон табат';

  @override
  String get recognizing => 'Товарды таанып жатам…';

  @override
  String get recognizedNone =>
      'Таанылган жок — жакыныраак жана жарыкта тартыңыз';

  @override
  String get seenOnPhoto => 'Сүрөттө';

  @override
  String get notInCatalog => 'каталогдо жок';

  @override
  String get aiKeyNeeded =>
      'Акылдуу камера үчүн ИИ ачкычы (Gemini) керек — кассадагы Нур Кеңешчиникиндей. Бул жерге коюуга болот.';

  @override
  String get aiKeyLabel => 'Gemini ачкычы';

  @override
  String get smartShelf => 'Текче';

  @override
  String get smartInvoice => 'Жүк кагаз';

  @override
  String get smartLabels => 'Этикеткалар';

  @override
  String get smartHint =>
      'Телефонду түз кармаңыз — камера товарларды жана бааларды издейт';

  @override
  String get takePhoto => 'Сүрөткө тартуу';

  @override
  String get morePhoto => 'Дагы сүрөт';

  @override
  String seenInBase(int n) {
    return 'Базада бар · $n';
  }

  @override
  String seenNew(int n) {
    return 'Жаңы · $n';
  }

  @override
  String smartNext(int n) {
    return 'Андан ары — кабыл алууга · $n';
  }

  @override
  String get bulkImport => 'Массалык жүктөө';

  @override
  String get bulkPickFile => 'Excel же CSV файлын тандоо';

  @override
  String get bulkPaste => 'Excel\'ден көчүрүлгөндү коюу';

  @override
  String get bulkTemplate => 'Файл үлгүсү';

  @override
  String get bulkHint =>
      'Мамычалар: Аталышы · Штрихкод · Сатуу баасы · Сатып алуу · Саны · Бирдик · Категория. Биринчи сап — аталыштар. Ошол эле штрихкоддуу товар кайталанбайт: саны кошулат, баалары жаңырат.';

  @override
  String bulkSummary(int n, int a, int b) {
    return 'Сап: $n · жаңы $a · бар $b';
  }

  @override
  String get bulkBad => 'өткөрүлдү: аталышы же баасы жок';

  @override
  String get bulkExists => 'бар — кошулат';

  @override
  String bulkUpload(int n) {
    return '$n жүктөө';
  }

  @override
  String bulkDone(int n) {
    return 'Жүктөлдү: $n';
  }

  @override
  String get bulkNothing => 'Файлда товар саптары табылган жок';

  @override
  String get notFoundTitle => 'Товар каталогдо жок';

  @override
  String notFoundAsk(String code) {
    return 'Штрихкод $code. Ушул штрихкод менен жаңы товар кошобузбу?';
  }

  @override
  String get addNewProduct => 'Товар кошуу';

  @override
  String get supplierLabel => 'Жеткирүүчү';

  @override
  String get supplierDiscount => 'Жеткирүүчүнүн арзандатуусу, %';

  @override
  String costWithDiscount(String sum) {
    return 'Арзандатуу менен сатып алуу: $sum';
  }

  @override
  String get supplierReceiptNote =>
      'Саны жеткирүүчүдөн кириш менен келет — NurCRM\'де кимден канчага алынганы көрүнөт.';

  @override
  String get retailPrice => 'Чекене баа';

  @override
  String get altBarcodesLabel => 'Кошумча штрихкоддор';

  @override
  String get altBarcodesHint =>
      'Ошол эле товардын же таңгактын башка штрихкоду: кутуда канча даана экенин көрсөтүңүз.';

  @override
  String get altBarcodeAdd => 'Штрихкод кошуу';

  @override
  String get packQty => 'Даана';

  @override
  String get packName => 'Таңгак';

  @override
  String get photosLabel => 'Товардын сүрөтү';

  @override
  String get photoCamera => 'Камера';

  @override
  String get photoGallery => 'Галерея';

  @override
  String get photoInternet => 'Интернеттен';

  @override
  String get photosSearching => 'Сүрөт издеп жатам…';

  @override
  String get photoFirstMain => 'Биринчи сүрөт — негизги';

  @override
  String get descriptionLabel => 'Сүрөттөмө — сайт жана бот үчүн';

  @override
  String get descriptionFind => 'Сүрөттөмөнү интернеттен табуу';

  @override
  String get descriptionWriting => 'Сүрөттөмө жазып жатам…';

  @override
  String get lowStockPull => '↑ Өйдө тартыңыз — эмне түгөнүп баратат';

  @override
  String lowStockTitle(int n) {
    return 'Түгөнүп баратат · $n';
  }

  @override
  String get lowStockEmpty => 'Баары жетиштүү — калдыктар нормада';

  @override
  String get summaryTitle => 'Жыйынтык';

  @override
  String get periodToday => 'Бүгүн';

  @override
  String get periodWeek => 'Апта';

  @override
  String get periodMonth => 'Ай';

  @override
  String get kpiRevenue => 'Түшүм';

  @override
  String get kpiChecks => 'Чектер';

  @override
  String get kpiAvg => 'Орточо чек';

  @override
  String get kpiProfit => 'Пайда';

  @override
  String get revenueByDay => 'Күндөр боюнча түшүм';

  @override
  String get paymentMethods => 'Төлөм ыкмалары';

  @override
  String get topProducts => 'Мыкты товарлар';

  @override
  String get noSalesPeriod => 'Бул мезгилде сатуу жок';

  @override
  String get abcTitle => 'ABC-анализ';

  @override
  String get abcByRevenue => 'Түшүм боюнча';

  @override
  String get abcByProfit => 'Пайда боюнча';

  @override
  String get abcByQty => 'Даана боюнча';

  @override
  String get abcA => 'Дүкөндү багат';

  @override
  String get abcB => 'Орто';

  @override
  String get abcC => 'Куйрук';

  @override
  String abcGroupNote(int n, String share) {
    return '$n поз. · $share %';
  }

  @override
  String get paretoTitle => 'Парето диаграммасы';

  @override
  String get paretoHint =>
      'Мамычалар — товардын салымы, сызык — топтолгон үлүш. 80 % белгисинен солдогулар — A тобу.';

  @override
  String get advisorTitle => 'Нур Кеңешчи';

  @override
  String get advisorOnline => 'Дүкөндүн маалыматтары туташкан';

  @override
  String get advisorAsk => 'Дүкөнүңүз жөнүндө сураңыз…';

  @override
  String get advisorThinking => 'Кеңешчи ойлонуп жатат…';

  @override
  String get advisorQ1 => 'Кечээ канча саттык?';

  @override
  String get advisorQ2 => 'Ким карыз?';

  @override
  String get advisorQ3 => 'Эмне заказ кылуу керек?';

  @override
  String get advisorQ4 => 'Апта кандай өттү?';

  @override
  String get advisorNew => 'Жаңы сүйлөшүү';

  @override
  String checkTab(int n) {
    return 'Чек · $n';
  }

  @override
  String get deleteProduct => 'Товарды өчүрүү';

  @override
  String deleteProductAsk(String name) {
    return '«$name» өчүрүлсүнбү? Сатуулар жана отчеттор калат.';
  }

  @override
  String get productDeleted => 'Товар өчүрүлдү';

  @override
  String smartUpload(int n) {
    return '$n товарды кампага жүктөө';
  }

  @override
  String get smartToReceive => 'Жеткирүүчү менен кабыл алууга';

  @override
  String smartShots(int n) {
    return 'Сүрөт: $n';
  }

  @override
  String get smartPriceRule =>
      'Жаңылардын баасы: сатып алууга +30 %, 5 сомго чейин';

  @override
  String smartUploaded(int n) {
    return 'Кампага жүктөлдү: $n';
  }

  @override
  String get sellingPrice => 'Сатуу баасы';

  @override
  String get durationCustom => 'Башка…';

  @override
  String get minutesHint => 'мүнөт';

  @override
  String get photoLabel => 'Сүрөт';

  @override
  String get shiftDetails => 'Смена отчету';

  @override
  String get shOpened => 'Ачылды';

  @override
  String get shClosed => 'Жабылды';

  @override
  String get shOpeningCash => 'Башында кассада';

  @override
  String get shNonCash => 'Накталай эмес';

  @override
  String get shDebt => 'Карызга';

  @override
  String get shReturns => 'Кайтаруулар';

  @override
  String get shIncome => 'Башка киреше';

  @override
  String get shExpected => 'Кассада болушу керек';

  @override
  String get shCounted => 'Саналды';

  @override
  String get shDifference => 'Айырма';

  @override
  String orderReadyNotify(String name, String num, String sum) {
    return 'Саламатсызбы, $name! №$num заказыңыз берүүгө даяр. Төлөөгө $sum сом. Күтөбүз!';
  }

  @override
  String get customSize => 'Өз өлчөмү';

  @override
  String get pickSupplier => 'Жеткирүүчүнү тандоо';

  @override
  String get addProductsHint => 'Товарды таап, кошуу үчүн басыңыз';

  @override
  String get removeAction => 'Алып салуу';

  @override
  String get esfExportCsv => 'Excel\'ге түшүрүү (CSV)';

  @override
  String get esfExportAll => 'Бардык долбоорлор Excel\'ге (CSV)';

  @override
  String get esfTaxSoon =>
      'Салыкка жөнөтүү NurCRM серверине ЭСФ кошулганда иштейт. Азырынча Excel\'ге түшүрүп, ГНС сайтына жүктөңүз.';

  @override
  String get dateLabel => 'Күнү';

  @override
  String get supplierNoLow =>
      'Бул жеткирүүчүнүн түгөнүп бараткан товарлары жок';

  @override
  String get supplierLowAdd =>
      'Жеткирүүчүнүн түгөнүп бараткан товарларын кошуу';

  @override
  String get inStockOnly => 'Бар';

  @override
  String get allCategories => 'Бардык категориялар';

  @override
  String get catalogPick => 'Каталогдон';

  @override
  String get themeAuto => 'Убакыт боюнча';

  @override
  String get darkFrom => 'Караңгы саат';

  @override
  String get darkTo => 'чейин';

  @override
  String get leaveAsk => 'Чын эле чыккыңыз келеби?';

  @override
  String get leaveAskSub => 'Сакталбаган өзгөртүүлөр жоголот.';

  @override
  String get leaveYes => 'Ооба, албетте';

  @override
  String get leaveNo => 'Жок, кокустан бастым';

  @override
  String get aiSettings => 'ИИ жөндөөсү';

  @override
  String get aiSettingsHint =>
      'Нур Кеңешчи Gemini аркылуу жооп берет. Gemini лимити бүтсө — Groq, андан кийин OpenRouter. Ачкычтар акысыз; NurCRMде сакталат.';

  @override
  String get aiConnected => 'туташкан';

  @override
  String get aiNoKeyShort => 'ачкыч жок';

  @override
  String get aiGetKey => 'Ачкыч алуу';

  @override
  String get aiTest => 'Текшерүү';

  @override
  String get aiTestOk => 'Иштейт ✓';

  @override
  String get aiKeysSaved => 'Ачкычтар сакталды';

  @override
  String get aiPasteKey => 'Жаңы ачкычты чаптаңыз';

  @override
  String get aiRemoveKey => 'Ачкычты алып салуу';

  @override
  String get payQrTitle => 'Төлөм үчүн QR';

  @override
  String get payQrHint =>
      'Банктарыңыздын QR кодун кошуңуз. Сатып алуучу чектин суммасы менен QRди сканерлеп төлөйт. Кайсы кассирге кайсы QR көрүнөрүн тандаңыз — 5 банкка чейин.';

  @override
  String get payQrAdd => 'QR кошуу';

  @override
  String get payQrBank => 'Банк';

  @override
  String get payQrCode => 'Банктын QR';

  @override
  String get payQrScan => 'Сканерлөө';

  @override
  String get payQrGallery => 'Галереядан (скриншот)';

  @override
  String get payQrPaste => 'Шилтемени чаптоо';

  @override
  String get payQrDynamic => 'Чектин суммасы QRде';

  @override
  String get payQrDynamicSub =>
      'Сатып алуучу сумманы тербейт (ELQR). Банк кабыл албаса — өчүрүңүз.';

  @override
  String get payQrStaticOnly => 'Бул QR ELQR эмес — сумма жазылбайт';

  @override
  String get payQrCashiers => 'Кимге көрсөтүү';

  @override
  String get payQrAllCashiers => 'Бардык кассирлерге';

  @override
  String get payQrNotRecognized => 'QR таанылган жок';

  @override
  String get payQrShowCustomer =>
      'Сатып алуучуга көрсөтүңүз. Төлөгөндөн кийин «Төлөө» басыңыз.';

  @override
  String get payQrNone =>
      'Төлөм QR кошула элек — ээси «Башкаруу → Төлөм үчүн QR» бөлүмүндө кошот.';

  @override
  String get payQrNoneOwner => 'Азырынча QR жок. «QR кошуу» басыңыз.';

  @override
  String get payQrSaved => 'QR сакталды';

  @override
  String get payQrDelete => 'QR өчүрүү';

  @override
  String get grpAi => 'ИИ жана аналитика';

  @override
  String get grpStock => 'Товарлар жана кампа';

  @override
  String get grpMoney => 'Сатуу жана акча';

  @override
  String get grpClients => 'Кардарлар жана документтер';

  @override
  String get grpTeam => 'Кызматкерлер';

  @override
  String get findSection => 'Бөлүмдү табуу';

  @override
  String get liveAuto => 'Авто';

  @override
  String get liveHintSale =>
      'Товарга багыттаңыз — 2 секунддан кийин өзү тартат';

  @override
  String get liveHintShelf => 'Текчеге багыттаңыз';

  @override
  String get liveHintInvoice => 'Накладнойго толук багыттаңыз';

  @override
  String get liveHintLabels => 'Баа белгилерине багыттаңыз';

  @override
  String get auditTitle => 'Аудит журналы';

  @override
  String get auditSub => 'Бул телефондогу бардык аракеттер';

  @override
  String get auditSearch => 'Кызматкер, товар, чек номери';

  @override
  String get auditKind => 'Аракет';

  @override
  String get auditWhat => 'Эмне жасалды';

  @override
  String get auditSale => 'Сатуу';

  @override
  String get auditCart => 'Чекке';

  @override
  String get auditReturn => 'Кайтаруу';

  @override
  String get auditPrice => 'Баалар жана калдык';

  @override
  String get auditProduct => 'Товарлар';

  @override
  String get auditSettings => 'Жөндөөлөр';

  @override
  String get auditLogin => 'Кирүү жана чыгуу';

  @override
  String get auditOpen => 'Экран ачты';

  @override
  String get teachCamera => 'Акылдуу камераны үйрөтүү: 3 сүрөт + штрихкод';

  @override
  String get teachRight => '3төн 1 · товар оң жактан';

  @override
  String get teachLeft => '3төн 2 · товар сол жактан';

  @override
  String get teachTop => '3төн 3 · товар үстүнөн';

  @override
  String get teachHint => 'Товар толугу менен рамкада';

  @override
  String get teachDone => 'Камера үйрөтүлдү';

  @override
  String teachShots(int n) {
    return 'Акылдуу камера үчүн: $n сүрөт';
  }

  @override
  String get exactMatch => 'так';

  @override
  String get noAccess => 'Кирүү укугу жок';

  @override
  String get noAccessHint =>
      'Бул бөлүм же аракет сиздин эсебиңиз үчүн жабык. Кирүүнү ээси ачат: NurCRM → Кызматкерлер → кирүү укуктары.';

  @override
  String get clientOptional => 'Кардар — бонустар жана сатып алуулар үчүн';

  @override
  String get bonusRedeem => 'Бонус менен чегерүү';

  @override
  String get bonusNone => 'Чегербөө';

  @override
  String bonusAll(String sum) {
    return 'Максимум · $sum';
  }

  @override
  String bonusEarn(String sum) {
    return 'Бонус кошулат: $sum';
  }

  @override
  String bonusPaid(String sum) {
    return 'Бонус менен: −$sum сом';
  }

  @override
  String get removeClient => 'Кардарды алып салуу';

  @override
  String get discountRemove => 'Арзандатууну алып салуу';

  @override
  String get addedToCheck => 'Чекке кошулду';

  @override
  String get payment => 'Төлөм';

  @override
  String toPayPositions(int n) {
    return 'Төлөөгө · $n позиция';
  }

  @override
  String get payMethod => 'Төлөм ыкмасы';

  @override
  String get cash => 'Накталай';

  @override
  String get card => 'Карта';

  @override
  String get debt => 'Карызга';

  @override
  String get mixed => 'Аралаш';

  @override
  String get client => 'Кардар';

  @override
  String get received => 'Алынды';

  @override
  String get receivedNowDebt => 'Азыр алынды (0 — баары карыз)';

  @override
  String get exact => 'Кайтарымсыз';

  @override
  String get change => 'Кайтарым';

  @override
  String notEnough(String sum) {
    return '$sum сом жетишпейт';
  }

  @override
  String get receiptToCustomer => 'Сатып алуучуга чек';

  @override
  String get notNeeded => 'Керек эмес';

  @override
  String get doPay => 'Төлөмдү өткөрүү';

  @override
  String get check => 'Чек';

  @override
  String get total => 'Жыйынтык';

  @override
  String get paidOk => 'Төлөм өттү';

  @override
  String get savedOffline => 'Чек телефондо сакталды';

  @override
  String get savedOfflineHint =>
      'Байланыш жок — чек серверге өзү кетет, экинчи жолу өтпөйт.';

  @override
  String get receiptQr => 'QR-код';

  @override
  String get showQr => 'Сатып алуучуга QR көрсөтүү';

  @override
  String get qrPointCamera => 'Сатып алуучу телефондун камерасын багыттайт';

  @override
  String get qrDownloadHint =>
      'Сатып алуучунун камерасы чекти дароо PDF файл кылып жүктөп алат';

  @override
  String get qrTextHint =>
      'Сервер чекке шилтеме бере электе камера аны текст катары көрсөтөт. PDF файлды кардарга — төмөнкү баскыч менен.';

  @override
  String get cashInOut => 'Салуу / алуу';

  @override
  String get cashIn => 'Салуу';

  @override
  String get cashOut => 'Алуу';

  @override
  String get amount => 'Сумма';

  @override
  String get reason => 'Себеп';

  @override
  String get reasonChange => 'Майда акча';

  @override
  String get reasonCollection => 'Инкассация';

  @override
  String get reasonExpense => 'Чыгымдар';

  @override
  String get reasonOther => 'Башка';

  @override
  String get cashOpDone => 'Сменага жазылды';

  @override
  String get debts => 'Кардарлардын карыздары';

  @override
  String get debtorsTotal => 'Бардык карыздар';

  @override
  String get payDebt => 'Карыз төлөмүн кабыл алуу';

  @override
  String get payDebtDone => 'Карыз төлөмү өттү';

  @override
  String get noDebts => 'Карыздарлар жок';

  @override
  String get heldChecks => 'Кийинкиге калтырылган чектер';

  @override
  String get holdCheck => 'Чекти кийинкиге калтыруу';

  @override
  String get holdNote => 'Ким үчүн же эскертме';

  @override
  String get heldEmpty => 'Кийинкиге калтырылган чек жок';

  @override
  String get restoreCheck => 'Чекке кайтаруу';

  @override
  String get delete => 'Өчүрүү';

  @override
  String get checkBusy => 'Чекте товар бар — алгач аны калтырыңыз же төлөңүз';

  @override
  String get returnTitle => 'Чек боюнча кайтаруу';

  @override
  String get returnAll => 'Бүт чекти кайтаруу';

  @override
  String get returnSelected => 'Тандалганды кайтаруу';

  @override
  String get returnDone => 'Кайтаруу өттү';

  @override
  String get returnToCustomer => 'Сатып алуучуга кайтаруу';

  @override
  String get showReceipt => 'Чекти көрүү';

  @override
  String get sendPdf => 'PDF жөнөтүү';

  @override
  String get sendReceipt => 'Чекти жөнөтүү';

  @override
  String get newClient => 'Жаңы кардар';

  @override
  String get clientName => 'Аты';

  @override
  String get phone => 'Телефон';

  @override
  String get clientSearch => 'Аты же телефону';

  @override
  String get myChecks => 'Менин чектерим';

  @override
  String get thisShift => 'Бул смена';

  @override
  String unsentN(int n) {
    return 'Жөнөтүлбөгөн · $n';
  }

  @override
  String get checkSearch => 'Чектин номери же товар';

  @override
  String get noChecks => 'Азырынча чек жок';

  @override
  String get checkNotSent => 'Чек жөнөтүлгөн жок';

  @override
  String get notAccepted => 'кабыл алынган жок';

  @override
  String get waitsForNetwork => 'байланыш күтүүдө';

  @override
  String get returned => 'кайтарылды';

  @override
  String get offlineCheckHint =>
      'Байланыш жок — чек сакталды жана серверге өзү кетет';

  @override
  String get sendToCustomer => 'Сатып алуучуга жөнөтүү';

  @override
  String get sendAgain => 'Дагы жөнөтүү';

  @override
  String get returnByAdmin => 'Кайтаруу — администратордун уруксаты менен';

  @override
  String get receiveByScanner => 'Сканер менен кабыл алуу';

  @override
  String get audit => 'Ревизия';

  @override
  String get findProduct => 'Товар издөө';

  @override
  String get pricesByAdmin =>
      'Бааларды жана жаңы товарларды администратор өзгөртөт';

  @override
  String get receiveNoSupplier => 'Жеткирүүчүсүз жана баасыз';

  @override
  String get scanProduct => 'Товарды сканерлөө';

  @override
  String get eachScanPlusOne => 'Ар бир скан — +1 даана';

  @override
  String get accepted => 'Кабыл алынды';

  @override
  String goodsPieces(int n, String qty) {
    return '$n товар · $qty даана';
  }

  @override
  String wasPieces(String qty) {
    return 'болгон $qty';
  }

  @override
  String get unknownBarcode => 'Белгисиз штрихкод';

  @override
  String unknownBarcodeHint(String code) {
    return '$code · администратор алып, товарды кошот';
  }

  @override
  String get postpone => 'Калтыруу';

  @override
  String postPieces(String qty) {
    return 'Өткөрүү · $qty даана';
  }

  @override
  String get receivedOk => 'Кабыл алуу өттү.';

  @override
  String get receivedQueued => 'Кабыл алуу сакталды — байланыш болгондо кетет.';

  @override
  String get allStock => 'Бүт кампа';

  @override
  String get chooseWhatToCount => 'Эмнени кайра эсептөө';

  @override
  String checkedOf(int a, int b) {
    return '$b ичинен $a текшерилди';
  }

  @override
  String get notChecked => 'Текшерилген жок';

  @override
  String get differences => 'Айырмачылыктар';

  @override
  String byProgram(String qty, String unit) {
    return 'программа боюнча $qty $unit';
  }

  @override
  String get matches => 'дал келди';

  @override
  String shortBy(String qty) {
    return '$qty жетишпейт';
  }

  @override
  String extraBy(String qty) {
    return 'ашыкча $qty';
  }

  @override
  String get auditAdminApplies =>
      'Калдыктарды администратор текшергенден кийин өзгөртөт';

  @override
  String get sendToAdmin => 'Администраторго жөнөтүү';

  @override
  String get auditSent => 'Ревизия администраторго жөнөтүлдү.';

  @override
  String get auditQueued => 'Ревизия сакталды — байланыш болгондо кетет.';

  @override
  String sinceChecks(String time, int n) {
    return '$time баштап · $n чек';
  }

  @override
  String get revenue => 'Түшкөн акча';

  @override
  String get shouldBeInDrawer => 'Кассада болушу керек';

  @override
  String get recountCash => 'Накталайды кайра санаңыз';

  @override
  String get howMuchInDrawer => 'Кассада канча акча';

  @override
  String cashDiff(String sum) {
    return 'Программа менен айырма: $sum сом';
  }

  @override
  String get cashDiffHint =>
      'Сумма дал келбесе, администратор айырманы отчеттон көрөт';

  @override
  String closeShiftPending(int n) {
    return 'Адегенде $n чек серверге кетиши керек — байланыш керек.';
  }

  @override
  String get shiftClosedOk => 'Смена жабылды';

  @override
  String get receiptPrinter => 'Чек принтери';

  @override
  String get prnSub => 'Bluetooth, Wi-Fi/LAN же USB · 58/80 мм';

  @override
  String get prnNotSet => 'Орнотулган эмес';

  @override
  String get prnConnection => 'Туташуу';

  @override
  String get prnOff => 'Жок';

  @override
  String get prnNetwork => 'Тармак (Wi-Fi/LAN)';

  @override
  String get prnUsb => 'USB (OTG)';

  @override
  String get prnSearch => 'Принтерлерди табуу';

  @override
  String get prnSearching => 'Издеп жатам…';

  @override
  String get prnNothingFound =>
      'Эч нерсе табылган жок. Принтер күйүкпү, жакынбы?';

  @override
  String get prnPairHint =>
      'Принтер тизмеде жокпу? Аны күйгүзүп «Табуу» басыңыз — же телефондун Bluetooth жөндөөлөрүндө жупташтырыңыз (PIN адатта 0000 же 1234).';

  @override
  String get prnUsbHint =>
      'Принтерди OTG кабели менен туташтырып, күйгүзүңүз. Android уруксат сурайт — «Уруксат берүү» басыңыз.';

  @override
  String get prnBtSettings => 'Bluetooth жөндөөлөрү';

  @override
  String get prnPaired => 'жупташкан';

  @override
  String get prnNotPaired => 'жакын, жупташкан эмес';

  @override
  String get prnIp => 'Принтердин IP дареги';

  @override
  String get prnPort => 'Порт';

  @override
  String get prnPaper => 'Тасманын туурасы';

  @override
  String prnPaperMm(int a, int b) {
    return '$a мм · $b белги';
  }

  @override
  String get prnCodepage => 'Кириллица коддоосу';

  @override
  String get prnCodeTable => 'ESC t таблицасы';

  @override
  String get prnCodepageHint =>
      'Орус тамгалардын ордуна иероглиф же «?» чыкса — коддоону же таблицанын номерин алмаштырыңыз (CP866 — 17, кээде 6 же 7; CP1251 — 46, кээде 73) жана кайра текшериңиз.';

  @override
  String get prnAuto => 'Төлөмдөн кийин чекти дароо басып чыгаруу';

  @override
  String get prnTest => 'Басып чыгарууну текшерүү';

  @override
  String get prnTestSent => 'Сыноо барагы принтерге жөнөтүлдү';

  @override
  String get prnTestHint =>
      'Сыноо барагын караңыз: сандар сабы бүт туурасын ээлеши, орус тамгалар окулушу керек.';

  @override
  String get prnPrint => 'Чекти басып чыгаруу';

  @override
  String get prnSent => 'Чек принтерге жөнөтүлдү';

  @override
  String get prnErrNotConfigured =>
      'Принтер орнотулган эмес: Профиль → Чек принтери.';

  @override
  String get prnErrUnsupported => 'Бул түзмөктө мындай басып чыгаруу жок.';

  @override
  String get prnErrBtOff => 'Bluetooth өчүк — күйгүзүп, кайталаңыз.';

  @override
  String get prnErrPermission =>
      '«Жакынкы түзмөктөр» (Bluetooth) уруксаты жок. Android терезесинде же колдонмо жөндөөлөрүндө уруксат бериңиз.';

  @override
  String get prnErrNotFound =>
      'Принтер табылган жок — күйүк экенин текшерип, кайра тандаңыз.';

  @override
  String get prnErrBleOnly =>
      'Бул принтер BLE аркылуу гана иштейт — кадимки Bluetooth (SPP) принтер керек.';

  @override
  String get prnErrTimeout =>
      'Принтер жооп бербейт. Күйүкпү, жакынбы, кагаз барбы? Кайталаңыз.';

  @override
  String get prnErrRefused =>
      'Тармактагы принтер кабыл албай жатат — IP даректи жана портту текшериңиз (адатта 9100).';

  @override
  String get prnErrUsbDenied =>
      'USB принтерге кирүү жок — Android терезесинде «Уруксат берүү» басыңыз.';

  @override
  String get prnErrUsbNotFound =>
      'USB принтер табылган жок — OTG кабелин жана принтердин кубатын текшериңиз.';

  @override
  String prnErrIo(String text) {
    return 'Басып чыгаруу катасы: $text';
  }

  @override
  String get kgUnit => 'кг';

  @override
  String get weighShort => 'Тараза';

  @override
  String get weighTitle => 'Салмактуу товар';

  @override
  String get weighHint =>
      'Товарды таразага коюңуз. Камераны товар жана тараза табло көрүнгөндөй багыттаңыз.';

  @override
  String get weighReading => 'Товарды таанып, тараза таблосун окуп жатам…';

  @override
  String get weighWaiting => 'Салмак туруктанганча күтүп жатам…';

  @override
  String get weighZero => 'Таразада 0 — товарды коюңуз.';

  @override
  String get weighNoDisplay =>
      'Тараза таблосу көрүнбөйт — камераны салмак сандарына багыттаңыз.';

  @override
  String get weighNoAi =>
      'Акылдуу камера жеткиликсиз (байланыш же ИИ ачкычы жок) — товарды тандап, салмагын кол менен киргизиңиз.';

  @override
  String get weighConfirm => 'Товарды жана салмакты текшериңиз';

  @override
  String weighRead(String name, String kg) {
    return 'Камера окуду: $name · $kg кг';
  }

  @override
  String weighLine(String name, String kg, String price, String sum) {
    return '$name $kg кг × $price = $sum сом';
  }

  @override
  String weighTooHeavy(String kg) {
    return 'Салмагы $kg кг — 50 кг дан ашык. Чын эле кошосузбу?';
  }

  @override
  String get weighNotStable =>
      'Салмак туруктанган жок — көрсөткүчтү текшериңиз.';

  @override
  String get weighEditKg => 'Салмакты өзгөртүү';

  @override
  String get weighOther => 'Башка товар — тизмеден тандоо';

  @override
  String get linkWithKassa => 'Касса менен байланыш';

  @override
  String get linkWithKassaSub => 'Дүкөндүн Wi-Fi аркылуу — кийинки жаңыртууда';

  @override
  String get language => 'Тил';

  @override
  String get darkTheme => 'Караңгы тема';

  @override
  String get offlineMode => 'Интернетсиз иштөө';

  @override
  String waitingToSend(int n) {
    return 'Жөнөтүүнү күтүүдө: $n';
  }

  @override
  String get logout => 'Аккаунттан чыгуу';

  @override
  String logoutPending(int n) {
    return 'Телефондо $n жөнөтүлбөгөн жазуу бар. Чыккандан кийин алар калат жана ушул аккаунт кайра киргенде кетет. Чыгасызбы?';
  }

  @override
  String get sync => 'Шайкештирүү';

  @override
  String catalogUpdated(String time) {
    return 'Каталог жаңыртылды $time';
  }

  @override
  String get nothingToSend => 'Баары жөнөтүлдү';

  @override
  String get sent => 'жөнөтүлдү';

  @override
  String get sendNow => 'Азыр жөнөтүү';

  @override
  String get kindSale => 'Сатуу';

  @override
  String get admin => 'Башкаруу';

  @override
  String get adminSub => 'Баалар, сменалар, заказдар, кардарлар';

  @override
  String get revenueToday => 'Бүгүнкү түшүм';

  @override
  String get checksToday => 'Бүгүнкү чектер';

  @override
  String get clientsOwe => 'Кардарлар карыз';

  @override
  String get productPrice => 'Товар — баа жана калдык';

  @override
  String get shifts => 'Сменалар жана отчет';

  @override
  String get siteOrders => 'Сайттан заказдар';

  @override
  String get clientsBonuses => 'Кардарлар жана бонустар';

  @override
  String get supplierOrder => 'Жеткирүүчүгө заказ';

  @override
  String get deletedFromCheck => 'Чектен өчүрүлгөн';

  @override
  String get nameOrBarcode => 'Аталышы же штрихкод';

  @override
  String get salePrice => 'Сатуу баасы';

  @override
  String get purchasePrice => 'Сатып алуу';

  @override
  String get wholesalePrice => 'Дүң баа';

  @override
  String get onStockLabel => 'Кампада';

  @override
  String get priceSaved => 'Баа сакталды.';

  @override
  String markup(String pct) {
    return 'Үстөк $pct %';
  }

  @override
  String get belowCost => 'Баа сатып алуудан төмөн — зыянга сатуу';

  @override
  String get shiftOpenNow => 'ачык';

  @override
  String shiftClosedAt(String time) {
    return '$time жабылды';
  }

  @override
  String get cashierLabel => 'Кассир';

  @override
  String get orderNew => 'Жаңы';

  @override
  String get orderPicking => 'Чогултуудабыз';

  @override
  String get orderReady => 'Берүүгө даяр';

  @override
  String get orderDone => 'Берилди';

  @override
  String get orderCanceled => 'Жокко чыгарылды';

  @override
  String get pickOrder => 'Заказды чогултуу';

  @override
  String get pickScanHint => 'Товарды сканерлеңиз — өзү белгиленет';

  @override
  String pickedOf(int a, int b) {
    return '$b ичинен $a чогултулду';
  }

  @override
  String get orderPicked => 'Заказ чогултулду';

  @override
  String get notInOrder => 'Бул товар заказда жок';

  @override
  String get notifyCustomer => 'Сатып алуучуга кабарлоо';

  @override
  String get handOver => 'Берүү';

  @override
  String orderReadyMsg(String num, String sum, String code) {
    return '$num заказы даяр! Төлөөгө $sum сом. Берүү коду $code.';
  }

  @override
  String get statusSaved => 'Абалы сакталды.';

  @override
  String get bonusBalance => 'Эсептеги бонустар';

  @override
  String get addBonus => 'Бонус кошуу';

  @override
  String get bonusAdded => 'Бонустар кошулду.';

  @override
  String get noPhone => 'номер жок';

  @override
  String get orderToSupplier => 'WhatsApp\'ка жөнөтүү';

  @override
  String get supplierName => 'Жеткирүүчү';

  @override
  String get supplierPhone => 'Жеткирүүчүнүн телефону';

  @override
  String get runningLowAdd => 'Түгөнүп жаткандардын баарын кошуу';

  @override
  String get orderQty => 'Канча заказ кылуу';

  @override
  String get deleteReason => 'Эмне үчүн алып саласыз?';

  @override
  String get reasonChanged => 'Сатып алуучу айнып калды';

  @override
  String get reasonScanError => 'Скандоо катасы';

  @override
  String get reasonNoMoney => 'Акча жетпей калды';

  @override
  String get reasonDefect => 'Бузук же мөөнөтү өткөн';

  @override
  String get remove => 'Алып салуу';

  @override
  String get noDeletions => 'Өчүрүү болгон жок.';

  @override
  String get loading => 'Жүктөлүүдө…';

  @override
  String get empty => 'Бул жерде бош.';

  @override
  String get share => 'Бөлүшүү';

  @override
  String get documentPdf => 'Жүк кагаз, товардык чек, эсеп';

  @override
  String get docSalesReceipt => 'Товардык чек';

  @override
  String get docWaybill => 'Жүк кагаз';

  @override
  String get docInvoice => 'Төлөм эсеби';

  @override
  String get staff => 'Кызматкерлер жана укуктар';

  @override
  String get rightSale => 'Сатуу жана төлөм';

  @override
  String get rightReturn => 'Кайтаруу жана алмашуу';

  @override
  String get rightDiscount => 'Арзандатуулар';

  @override
  String get rightEditPrice => 'Чекте бааны өзгөртүү';

  @override
  String get rightDeleteLine => 'Чектен өчүрүү';

  @override
  String get rightStock => 'Кампа, кабыл алуу, ревизия';

  @override
  String get rightReports => 'Отчеттор жана аналитика';

  @override
  String get rightShifts => 'Сменалар';

  @override
  String get rightClients => 'Кардарлар';

  @override
  String get rightSettings => 'Жөндөөлөр';

  @override
  String get rightsSaved => 'Укуктар сакталды.';

  @override
  String get notifications => 'Билдирмелер';

  @override
  String get notifyNewOrder => 'Сайттан жаңы заказ';

  @override
  String get notifyRunningOut => 'Товарлар түгөнүүдө';

  @override
  String notifyRunningOutSub(int n) {
    return '$n позиция · заказ кылуу убагы';
  }

  @override
  String get notifyBigDebt => 'Кардардын чоң карызы';

  @override
  String get notifyNotSent => 'Серверге жөнөтүлгөн жок';

  @override
  String get noNotifications => 'Жаңы билдирме жок.';

  @override
  String get newProductSub => 'Штрихкод, аталыш, баалар';

  @override
  String get findByBarcode => 'Штрихкод боюнча табуу';

  @override
  String alreadyInStock(String name) {
    return 'Мындай товар бар: $name';
  }

  @override
  String get productCreated => 'Товар түзүлдү.';

  @override
  String get quantityLabel => 'Саны';

  @override
  String get barcodeLabel => 'Штрихкод';

  @override
  String get retail => 'Чекене';

  @override
  String get wholesale => 'Дүң';

  @override
  String get wholesaleOn => 'Дүң сатуу: дүң баасы бар товарлар — дүң баада.';

  @override
  String get wholesaleNone => 'Бул чектеги товарлардын дүң баасы жок.';

  @override
  String get wholesaleTag => 'дүң';

  @override
  String get cashJournal => 'Сменадагы салуулар жана алуулар';

  @override
  String get noCashOps => 'Салуу жана алуу болгон жок.';

  @override
  String get themeLight => 'Жарык';

  @override
  String get themeDark => 'Караңгы';

  @override
  String get themeSystem => 'Системадагыдай';

  @override
  String get appearance => 'Көрүнүш';

  @override
  String get searchAll => 'Товар, кардар же бөлүм';

  @override
  String get sectionsTitle => 'Бөлүмдөр';

  @override
  String get productsTitle => 'Товарлар';

  @override
  String get clientsTitle => 'Кардарлар';

  @override
  String get openAction => 'Ачуу';

  @override
  String get returnAction => 'Кайтаруу';

  @override
  String addedToCheckName(String name) {
    return 'Чекке кошулду: $name';
  }

  @override
  String get receiveWithPrices => 'Баалар менен кабыл алуу';

  @override
  String get receiveWithPricesSub =>
      'Жеткирүүчү, сатып алуу, сатуу, жаңы товарлар';

  @override
  String get noSupplier => 'Жеткирүүчүсүз';

  @override
  String get newProduct => 'Жаңы товар';

  @override
  String barcodeNotInStock(String code) {
    return '$code штрихкоду кампада табылган жок';
  }

  @override
  String get foundInBase => 'Жалпы базадан табылды — текшериңиз';

  @override
  String get unitLabel => 'Бирдик';

  @override
  String get createAndAdd => 'Түзүп кошуу';

  @override
  String markupRule(String pct) {
    return 'Сатуу баасы: сатып алууга +$pct %';
  }

  @override
  String get payNowCash => 'Дароо төлөндү';

  @override
  String get onCredit => 'Жеткирүүчүгө карызга';

  @override
  String purchaseSum(String sum) {
    return 'Сатып алуу $sum сом';
  }

  @override
  String get postReceipt => 'Кирешени өткөрүү';

  @override
  String receiptPosted(int n) {
    return 'Кириш өткөрүлдү: $n поз.';
  }

  @override
  String partlyFailed(String text) {
    return 'Өткөн жок: $text';
  }

  @override
  String get shiftsOpen => 'Ачык';

  @override
  String get shiftsClosed => 'Жабык';

  @override
  String get debtAccessTitle => 'Карызга уруксат';

  @override
  String get debtAccessHint =>
      'Кайсы кызматкер карызга сата алат: болот, болбойт же лимит менен — күнүнө канча чек жана канча сом. Эрежелер компаниянын бардык кассалары жана телефондору үчүн жалпы. Ээси менен администраторго чек жок.';

  @override
  String get debtAllStaff => 'Бардык кызматкерлер';

  @override
  String get debtSameAsAll => 'Баарындай';

  @override
  String get debtAllow => 'Болот';

  @override
  String get debtDeny => 'Болбойт';

  @override
  String get debtLimit => 'Лимит менен';

  @override
  String get debtReceiptsPerDay => 'Күнүнө карыз чек';

  @override
  String get debtSumPerDay => 'Күнүнө карыз суммасы, сом';

  @override
  String get debtNoLimit => 'чексиз';

  @override
  String get debtAccessSaved =>
      'Сакталды — эрежелер бардык кассаларда жана телефондордо иштейт.';

  @override
  String get debtStaffNotLoaded =>
      'Кызматкерлердин тизмеси алынган жок — баары үчүн эреже коюуга болот.';

  @override
  String get debtDenied =>
      'Ээси сизге карызга сатууга тыюу салган. Башка төлөм ыкмасын тандаңыз же ээсин чакырыңыз.';

  @override
  String debtLimitReceipts(int a, int b) {
    return 'Бүгүнкү карызга сатуу лимити бүттү: $a / $b чек. Ээсине кайрылыңыз.';
  }

  @override
  String debtLimitSum(String left, String max, String sum) {
    return 'Бүгүнкү карыз лимитинен ашты: дагы $left сом болот ($max сомдон), карызга $sum сом. Төлөмдүн бир бөлүгүн азыр алыңыз же ээсин чакырыңыз.';
  }

  @override
  String debtReceiptsOf(int a, int b) {
    return 'чек $a / $b';
  }

  @override
  String debtSumOf(String used, String max) {
    return 'карыз $used / $max сом';
  }

  @override
  String debtYourLimit(String text) {
    return 'Бүгүнкү карыз лимитиңиз: $text';
  }

  @override
  String get promoTag => 'акция';

  @override
  String get promoNoManualDiscount =>
      'Товарга акция иштейт — арзандатууну сервер эсептейт, бул сапка кол менен арзандатуу берүүгө болбойт.';

  @override
  String get quoteSearch => 'Номер, кардар же телефон';

  @override
  String get quotesEmpty => 'Азырынча КП жана эсептер жок';

  @override
  String get quotesEmptyHint =>
      '«Түзүү» басыңыз: кардарды жана товарларды тандаңыз — PDF WhatsApp же почта аркылуу жөнөтүлөт.';

  @override
  String get quotesNoMatch =>
      'Эч нерсе табылган жок — издөөнү же чыпканы өзгөртүңүз.';

  @override
  String get createDoc => 'Түзүү';

  @override
  String get makeInvoice => 'Эсеп түзүү';

  @override
  String get sendToClient => 'Жөнөтүү';

  @override
  String docWaText(String doc, String number, String sum, String until) {
    return '$doc № $number, суммасы $sum сом. $until.';
  }

  @override
  String get docSubtotal => 'Арзандатуусуз сумма';

  @override
  String get discountShort => 'Арзандатуу';

  @override
  String get paidLabel => 'Төлөндү';

  @override
  String get leftToPay => 'Төлөнө турган калдык';

  @override
  String get findClient => 'Кардарды табуу';

  @override
  String get companyLabel => 'Компания (ЖЧК, ЖИ)';

  @override
  String get clientExists =>
      'Бул телефон менен кардар NurCRMде бар — ал тандалды.';

  @override
  String get clientSaved => 'Кардар NurCRMге сакталды';

  @override
  String get clientLocalOnly =>
      'NurCRM менен байланыш жок — кардар ушул документке гана жазылды.';

  @override
  String get clientNameRequired => 'Атын же компанияны жазыңыз';

  @override
  String get innDigits => 'ИНН — сандар гана, адатта 14';

  @override
  String get discountHint => 'Сумма же пайыз, мисалы 10%';

  @override
  String get stExpired => 'Мөөнөтү бүттү';

  @override
  String deleteDocAsk(String number) {
    return '$number өчүрүлсүнбү?';
  }

  @override
  String get deleteDocAskSub =>
      'Документ ээсинин программасынан да биротоло өчөт.';

  @override
  String get requisitesTitle => 'Эсеп үчүн реквизиттер';

  @override
  String get requisitesHint =>
      'Банк, эсептешүү эсеби, БИК, телефон — PDFте дүкөндүн аталышынын астында чыгат.';

  @override
  String scannedAdded(String name) {
    return 'Кошулду: $name';
  }

  @override
  String invoiceCreated(String number) {
    return '$number эсеби түзүлдү';
  }

  @override
  String get historyLabel => 'Тарых';

  @override
  String get docLines => 'Товарлар жана кызматтар';

  @override
  String docFrom(String date) {
    return '$date';
  }

  @override
  String get clientNotChosen => 'Кардар тандалган жок';

  @override
  String get linesEmptyHint =>
      'Товар кошуңуз: издөө, сканер же учурдагы чектен.';

  @override
  String get quotesSyncing => 'Жалпы тизме жаңыланууда…';

  @override
  String get quotesShared => 'Ээсинин программасы менен жалпы тизме';

  @override
  String quotesSharedAt(String time) {
    return 'Жалпы тизме жаңыланды: $time';
  }

  @override
  String get quotesOffline =>
      'Сервер менен байланыш жок — телефондогу көчүрмө көрсөтүлдү';

  @override
  String quotesNotSent(int n) {
    return 'Жөнөтүлгөн жок: $n. Байланыш болгондо ээсинин программасына кетет — кайталоо үчүн басыңыз.';
  }

  @override
  String get quotesForbidden =>
      'Компаниянын жалпы жөндөөлөрүнө укук жок — өзгөртүүлөр ушул телефондо гана. Ээсинин же администратордун кирүүсү керек.';

  @override
  String get notSentMark => 'жөнөтүлгөн жок';

  @override
  String quoteChangedElsewhere(String number) {
    return '$number кийин башка жерде өзгөртүлдү — акыркы версиясы көрсөтүлдү';
  }

  @override
  String quoteRenumbered(String from, String to) {
    return '$from номери бош эмес — документ эми $to';
  }
}
