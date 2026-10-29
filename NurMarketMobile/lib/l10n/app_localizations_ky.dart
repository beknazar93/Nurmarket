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
  String get rentals => 'Ижара';

  @override
  String get rentalNew => 'Жаңы ижара';

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
    return '№$n ижара тариздолду — баасы чекте.';
  }

  @override
  String rentalLine(int n, String days) {
    return 'Ижара №$n · $days сут.';
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
  String get dayToday => 'Бүгүн';

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
  String get chartBestDay => 'Эң мыкты күн';

  @override
  String get chartWorstDay => 'Эң начар күн';

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
  String get advisorOnline => 'Дүкөнүңүздүн маалыматтары боюнча жооп берет';

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
  String get advisorNoKey =>
      'ИИ ачкычы жок. Аны ушул телефондо «ИИ жөндөөсү» → «Gemini ачкычтары» бөлүмүнө кошуңуз (же ээсинин программасында: Нур Кеңешчи → ⚙ → ИИ моделдери жана ачкычтар)';

  @override
  String get advisorStageData => 'Дүкөндүн маалыматтарын чогултуп жатам…';

  @override
  String get advisorStageAsk => 'ИИден сурап жатам…';

  @override
  String advisorError(String error) {
    return 'Кеңешчи жооп берген жок: $error';
  }

  @override
  String get callTitle => 'Сүйлөшүү';

  @override
  String get callConnecting => 'Туташып жатам…';

  @override
  String get callPreparing => 'Дүкөндүн маалыматын даярдап жатам…';

  @override
  String get callListening => 'Угуп жатам…';

  @override
  String get callThinking => 'Ойлонуп жатам…';

  @override
  String get callSpeaking => 'Сүйлөп жатам… (бөлүү үчүн басыңыз)';

  @override
  String get callEnded => 'Чалуу аяктады';

  @override
  String get callEnd => 'Бүтүрүү';

  @override
  String get callMicOff => 'Микрофон өчүк';

  @override
  String get callMicOn => 'Микрофон күйүк';

  @override
  String get callSpeakerOn => 'Катуу байланыш';

  @override
  String get callSpeakerOff => 'Катуу байланышсыз';

  @override
  String callCost(int n) {
    return 'Чалуу $n мүнөткө чейин созулат жана ИИ ачкычынын лимитин чатка караганда тезирээк коротот.';
  }

  @override
  String get callLimitSoon => 'Бир мүнөттөн кийин чалуу аяктайт';

  @override
  String callEndedLimit(int n) {
    return 'Чалуу аяктады: $n мүнөт өттү. Кайра чалууга болот.';
  }

  @override
  String get callEndedFocus =>
      'Чалуу үзүлдү: кирүүчү чалуу же тиркеме жыйналды.';

  @override
  String callFailed(String error) {
    return 'Чалуу ишке ашкан жок: $error';
  }

  @override
  String get callNoMic =>
      'Микрофонго уруксат жок. Телефондун жөндөөлөрүндө NurMarket үчүн микрофонго уруксат бериңиз.';

  @override
  String get callOpenSettings => 'Жөндөөлөрдү ачуу';

  @override
  String get callUnsupported => 'Үн менен чалуу Android телефонунда иштейт.';

  @override
  String get callTableReady => 'Сандары бар таблица чалуудан кийин чатта болот';

  @override
  String get callClose => 'Жабуу';

  @override
  String get callYou => 'Сиз';

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
  String get liveHintSale => 'Товарга багыттап, «Сканерлөө» басыңыз';

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
  String get teachCamera => 'Акылдуу камераны үйрөтүү: 3–5 сүрөт + штрихкод';

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
  String get scanShoot => 'Сканерлөө';

  @override
  String get scanBusy => 'Сканерлеп жатам…';

  @override
  String get scanNext => 'Кийинки товар — кайра «Сканерлөө»';

  @override
  String get scanChecking => 'Үйрөтүлгөн товарлар менен салыштырып жатам…';

  @override
  String scanTook(String sec) {
    return '$sec сек';
  }

  @override
  String teachPhotoOf(int a, int b) {
    return '$b сүрөттөн $a';
  }

  @override
  String get teachAngleRight => 'товар оң жактан';

  @override
  String get teachAngleLeft => 'товар сол жактан';

  @override
  String get teachAngleTop => 'товар үстүнөн';

  @override
  String get teachAngleFront => 'алдындагы этикетка';

  @override
  String get teachAngleBack => 'товар артынан';

  @override
  String get teachCanFinish => '«Даяр» басыңыз же дагы бир тараптан тартыңыз';

  @override
  String get teachRetake => 'Кайра тартуу';

  @override
  String get weighAim =>
      'Товарды таразага коюп, таблого багыттап, «Сканерлөө» басыңыз';

  @override
  String get weighAimDisplay =>
      'Тараза таблосу көрүнбөйт — камераны салмак терезесине багыттап, фонарикти өчүрүп, «Сканерлөө» басыңыз';

  @override
  String get aiErrNoNet =>
      'Интернет жок — байланышты текшерип, кайра «Сканерлөө» басыңыз';

  @override
  String get aiErrSlow =>
      'ИИ убагында жооп берген жок (байланыш жай) — кайра аракет кылыңыз';

  @override
  String get aiErrLimit =>
      'ИИнин акысыз лимити бүттү — бир мүнөттөн кийин аракет кылыңыз';

  @override
  String get aiErrBusy =>
      'ИИ азыр ашыкча жүктөлгөн — бир мүнөттөн кийин аракет кылыңыз';

  @override
  String get aiErrOther => 'ИИ сүрөттү тааный алган жок';

  @override
  String addAgain(String name) {
    return '«$name» чекте бар. Дагы кошобузбу?';
  }

  @override
  String get yesAdd => 'Кошуу';

  @override
  String get shelfAddAsk =>
      'Текченин сүрөтү — текчеде турган товар. Бул сандарды калдыкка жеткирүү катары кошобузбу?';

  @override
  String isItProduct(String name) {
    return 'Бул «$name»бу?';
  }

  @override
  String notFoundOffline(String code) {
    return '$code коду телефондо жок, сервер жооп бербей жатат. Байланышты текшериңиз — товар ПКда түзүлгөн болушу мүмкүн, экинчисин түзбөңүз.';
  }

  @override
  String get scaleLabelsTitle => 'Тараза этикеткалары';

  @override
  String get scaleLabelsHint =>
      'Тараза этикеткасынын штрихкоду префикс менен башталат (20–29). Кайсы префикстер СУММАНЫ, кайсылары САЛМАКТЫ көрсөтөрүн жазыңыз. Компаниянын бардык телефондору үчүн.';

  @override
  String get scaleAmountPrefixes => '«Сумма» префикстери';

  @override
  String get scaleWeightPrefixes => '«Салмак» префикстери';

  @override
  String get cameraShotFailed =>
      'Камера сүрөт тарткан жок — кайра «Сканерлөө» басыңыз';

  @override
  String get saved => 'Сакталды';

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
    return '$code · каталогдо да, NurCRM справочнигинде да жок. Товарды кошуңуз — код эч жакка жөнөтүлбөйт';
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

  @override
  String get payOutcomeUnknown =>
      'Сервер жооп берген жок — сатуу өтүп кетиши мүмкүн. Эч нерсени өзгөртпөй «Төлөө» дагы басыңыз: кайталоо экинчи жолу өтпөйт. Байланышсыз карыз жана бонус кемитүү жасалбайт.';

  @override
  String get debtDueLabel => 'Кайтаруу мөөнөтү';

  @override
  String debtDueDays(int n) {
    return '$n күн';
  }

  @override
  String get debtDueNotSaved =>
      'Карыздын мөөнөтү серверге жазылган жок — сайттан оңдоңуз (демейки 30 күн).';

  @override
  String returnAmount(String sum) {
    return 'Кайтаруу суммасы: $sum сом';
  }

  @override
  String returnGive(String what) {
    return 'Сатып алуучуга берүү: $what';
  }

  @override
  String returnInCash(String sum) {
    return 'накталай $sum сом';
  }

  @override
  String returnToCard(String sum) {
    return 'картага/которуу менен $sum сом';
  }

  @override
  String returnDebtReduced(String sum) {
    return 'Кардардын карызы $sum сомго азайды — бул акча берилбейт.';
  }

  @override
  String returnDebtUnknown(String sum) {
    return 'Көңүл буруңуз: кардардын карызынын калдыгы серверден алынган жок. Эсеп боюнча карыз $sum сомго азайды, бирок кардар бул чек боюнча карызын төлөп койгон болсо — ага $sum сомго чейин кайтаруу керек. Берүүдөн мурун кардардын карызын текшериңиз.';
  }

  @override
  String get returnViaBank =>
      'Картага/которуу бөлүгү — кассадан эмес, банк аркылуу кайтарыңыз.';

  @override
  String returnPaidInfo(String paid, String done) {
    return 'Чек боюнча төлөндү: $paid сом · кайтарылды: $done сом';
  }

  @override
  String returnedQty(String qty) {
    return 'кайтарылды: $qty';
  }

  @override
  String get returnNothingLeft => 'Бул чек боюнча баары кайтарылган.';

  @override
  String cashOutTooMuch(String sum) {
    return 'Алуу кассадагыдан көп ($sum сом).';
  }

  @override
  String payDebtToDebt(String sum) {
    return 'Карыздын эсебине: $sum сом';
  }

  @override
  String get payDebtOverCard => 'Которуу менен — карыздан ашпасын.';

  @override
  String receiveNotAccepted(String reason) {
    return 'Кириш сервер тарабынан кабыл алынган жок: $reason. Жазуу — «Синхрондоштурууда».';
  }

  @override
  String get auditApply => 'Ревизияны өткөрүү';

  @override
  String get auditApplyHint =>
      'Калдыктар саналгандай болот — ПКдагы кассадагы ревизиядай. Байланыш керек.';

  @override
  String auditApplyAsk(int n, int a) {
    return 'Текшерилген товар: $n, айырма: $a. Калдыктар саналгандай болот. Өткөрөбүзбү?';
  }

  @override
  String get auditApplied => 'Ревизия өттү — калдыктар жаңыланды.';

  @override
  String get auditApplyQueued =>
      'Ревизия акты түзүлдү — өткөрүү байланыш болгондо кетет.';

  @override
  String get auditOnlineOnly =>
      'Ревизия сервер менен байланыш болгондо гана өтөт — саноо сакталды, кийин кайталаңыз.';

  @override
  String auditContinue(int n) {
    return 'Саноону улантуу ($n товар)';
  }

  @override
  String get countedCash => 'Саналды';

  @override
  String get openingCashLabel => 'Сменанын башында';

  @override
  String closeShiftFailed(int n) {
    return 'Сервер $n чекти кабыл алган жок — алар сменанын отчетуна кирбейт. Кайра жөнөтүңүз же «Синхрондоштурууда» алып салыңыз.';
  }

  @override
  String get closeShiftAsk => 'Сменаны жабабызбы?';

  @override
  String get closeShiftAskSub =>
      'Жабылгандан кийин бул сменада сатууга болбойт.';

  @override
  String get xReport => 'X-отчет';

  @override
  String get foreignShiftHint =>
      'Бул кассада башка кассирдин сменасы ачык — ага сатууга болбойт. Өз сменаңызды ачыңыз.';

  @override
  String get loadMore => 'Дагы';

  @override
  String get dropRow => 'Алып салуу';

  @override
  String get dropRowAsk =>
      'Чекти кезектен алып салабызбы? Ал серверге кетпейт, товар телефондогу калдыкка кайтат. Акча алынган болсо — сатып алуучуга кайтарыңыз же чекти кайра басыңыз.';

  @override
  String get dropRowAskStock =>
      'Жазууну кезектен алып салабызбы? Ал серверге кетпейт — товардын калдыгын кол менен текшериңиз.';

  @override
  String get otherAccount => 'башка аккаунт';

  @override
  String otherAccountHint(String email) {
    return '$email кирүүсү менен жазылган — ушул логин менен киргенде кетет.';
  }

  @override
  String pAStockUnclear(String name, String now, String before) {
    return '$name: мурунку аракет сервердин жообусуз калды, ал эми калдык андан бери өзгөрдү (азыр $now, кошууга чейин $before болчу). Эки эсе болбосун деп кайра КОШУЛГАН ЖОК. Калдыкты ревизия менен текшериңиз: кошулган болсо — сапты өчүрүңүз, кошулбаса — өчүрүп, товарды кайра кошуңуз.';
  }

  @override
  String get pAWeightLabel =>
      'Бул тараза этикеткасы (бир сатып алуунун салмагы) — кабыл алууга кирбейт. Товардын штрихкодун сканерлеңиз же аты боюнча табыңыз.';

  @override
  String get pANoServerCheck =>
      'Сервер менен байланыш жок — бул штрихкоддуу товар бар-жогун текшерүүгө болбойт. Байланыш келгенде кайталаңыз.';

  @override
  String pALinesLeft(int n) {
    return 'Кабыл алынбаган саптар: $n — алар тизмеде калды, оңдоп кайра өткөрүңүз.';
  }

  @override
  String get pAPluLabel => 'Тараза үчүн PLU';

  @override
  String get pAPluHint => 'бош — өзү дайындалат';

  @override
  String get pAStockAdd => 'Калдыкка кошуу';

  @override
  String get pAStockSet => 'Калдыкты коюу';

  @override
  String get pAStockAddHint =>
      'Файлдагы сан кабыл алуудагыдай калдыкка кошулат.';

  @override
  String get pAStockSetHint =>
      'Калдык файлдагы санга барабар болот (алмаштыруу, кошуу эмес).';

  @override
  String pADupRow(int n) {
    return '$n-саптын кайталанышы — өткөрүлүп жиберилет';
  }

  @override
  String get pAUploaded => 'жүктөлдү';

  @override
  String get rightCashier => 'Кассир интерфейси';

  @override
  String get rightOrders => 'Сайттан заказдар';

  @override
  String get rightProcurement => 'Сатып алуулар';

  @override
  String get rightSuppliers => 'Жеткирүүчүлөр';

  @override
  String get rightSelfLocked =>
      'Өз укуктарыңызды өчүрүүгө болбойт — муну ээси же башка администратор жасайт.';

  @override
  String get staffYou => 'бул сиз';

  @override
  String get screenUnavailable =>
      'Экран кайра ачылып, анын маалыматы сакталган жок. Артка кайтып, аны кайра ачыңыз.';

  @override
  String get cancelOrder => 'Заказды жокко чыгаруу';

  @override
  String cancelOrderAsk(String number) {
    return '№$number заказды жокко чыгарабызбы? Сатып алуучу аны албайт.';
  }

  @override
  String pickShortAsk(String got, String need) {
    return '$need ичинен $got чогултулду. Заказды толук эмес чогултулду деп белгилейбизби?';
  }

  @override
  String pickupCodeLabel(String code) {
    return 'Берүү коду: $code';
  }

  @override
  String get torchToggle => 'Фонарь';

  @override
  String get payTransferQr => 'Которуу / QR';

  @override
  String get reportLoadFailed =>
      'Отчет жүктөлгөн жок. Байланышты текшерип, «Кайталоо» баскычын басыңыз.';

  @override
  String get rentalLatePerDay => 'Кечиктирилген ар бир сутка үчүн айып';

  @override
  String rentalLateHint(int n, String rate, String sum) {
    return 'Кечиктирүү $n сутка × $rate = $sum сом (ижаранын шарты боюнча)';
  }

  @override
  String rentalBackDeposit(String sum) {
    return 'Кардарга күрөөнү кайтаруу: $sum сом';
  }

  @override
  String rentalWithheld(String sum) {
    return 'кармалды $sum сом';
  }

  @override
  String rentalBackDocument(String doc) {
    return 'Кардарга документти кайтаруу: $doc';
  }

  @override
  String rentalExcess(String sum) {
    return 'Күрөөдөн ашкан айып: $sum сом — чекке кошулат';
  }

  @override
  String rentalPenaltyLine(int n) {
    return '№$n ижара боюнча айып';
  }

  @override
  String rentalPenaltyInCart(String sum) {
    return 'Кайтарым кабыл алынды. $sum сом айып чекке кошулду — төлөмдү өткөрүңүз.';
  }

  @override
  String get waybillDoc => 'Накладной';

  @override
  String get waybillsFilter => 'Накладнойлор';

  @override
  String get docPayMarkOnly =>
      'Бул документтеги белги гана: акча сменанын кассасына жана кардардын карызы NurCRMге жазылбайт. Акчаны касса аркылуу өткөрүү үчүн «Сатууну өткөрүү» колдонуңуз.';

  @override
  String get docPayMarkOk => 'Төлөмдү белгилөө';

  @override
  String get docSellCartBusy =>
      'Чекте товарлар бар. Аларды документтин товарлары менен алмаштырабызбы?';

  @override
  String get docSellReplace => 'Алмаштыруу';

  @override
  String get docSentToSale => 'Сатууга берилди';

  @override
  String docSellMissing(String names) {
    return 'Телефондун каталогунда жок — кол менен кошуңуз: $names';
  }

  @override
  String docVatIncl(String p) {
    return 'Анын ичинде КНС $p%';
  }

  @override
  String get prnQr => 'Чекте QR-код';

  @override
  String get prnQrHint =>
      'Фискалдык чекти текшерүү же электрондук чек. Принтер QR ордуна башка белгилерди басса — өчүрүңүз.';

  @override
  String get prnDrawer => 'Принтер аркылуу акча кутусу';

  @override
  String get prnDrawerHint =>
      'Накталай сатуудан кийин ачылат (куту принтерге кабель менен туташкан).';

  @override
  String prnDrawerPin(int n) {
    return 'Контакт $n';
  }

  @override
  String get prnDrawerTest => 'Кутуну ачуу';

  @override
  String get prnDrawerSent =>
      'Кутуга буйрук жөнөтүлдү. Ачылбаса — башка контактты тандаңыз.';

  @override
  String get stepPhoto => 'Сүрөт';

  @override
  String get stepBarcode => 'Штрихкод';

  @override
  String get bindTitle =>
      'Сүрөттөр сакталды ✓ — эми товардын штрихкодун сканерлеңиз';

  @override
  String get bindSub =>
      'Товардо штрихкод жок болсо — «Өткөрүп жиберүү» басыңыз';

  @override
  String get bindSkip => 'Өткөрүп жиберүү';

  @override
  String get bindManual => 'Кол менен киргизүү';

  @override
  String get bindManualHint => 'Штрихкоддун астындагы сандар';

  @override
  String get bindChecking => 'Штрихкодду текшерип жатам…';

  @override
  String bindWillLink(String code, String name) {
    return '$code штрихкоду «$name» товарына байланат';
  }

  @override
  String bindWillLinkNew(String code) {
    return '$code штрихкоду жаңы товарга байланат';
  }

  @override
  String bindAlreadyOwn(String code, String name) {
    return '$code штрихкоду «$name» товарына мурунтан байланган — эч нерсе өзгөртүүнүн кереги жок';
  }

  @override
  String get bindTakenTitle => 'Штрихкод башка товарда бар';

  @override
  String bindTakenAsk(String code, String name) {
    return '$code штрихкоду «$name» товарына байланган.\n«Алмаштыруу» — штрихкод бул товарга өтөт.\n«Калтыруу» — «$name» товарында калат, бул товар штрихкодсуз болот.';
  }

  @override
  String bindTakenAskNew(String code, String name) {
    return '$code штрихкоду «$name» товарына байланган — балким, бул товар кампада мурунтан бар.\n«Калтыруу» — штрихкод «$name» товарында калат, жаңы товар штрихкодсуз болот.';
  }

  @override
  String get bindReplace => 'Алмаштыруу';

  @override
  String get bindKeep => 'Калтыруу';

  @override
  String get bindRescan => 'Кайра сканерлөө';

  @override
  String bindLinked(String code, String name) {
    return '$code штрихкоду «$name» товарына байланды';
  }

  @override
  String get teachNext =>
      'Товарды 3–5 тараптан тартып, «Даяр» басыңыз — кийинки кадам штрихкод';

  @override
  String get teachNextNoCode => 'Товарды 3–5 тараптан тартып, «Даяр» басыңыз';

  @override
  String get liveNextSale =>
      'Табылган товарлар төмөндө чыгат — товарды басыңыз, ал чекке түшөт';

  @override
  String get liveNextList =>
      'Тартылгандан кийин камера табылгандардын тизмесин көрсөтөт — аны оңдосо болот';

  @override
  String get scanCodesHint =>
      'Рамканы штрихкодго багыттаңыз — код өзү жазылат. Анан «Даяр» басыңыз';

  @override
  String get weighNext => 'Камера товарды таанып, салмакты окуп, чекке кошот';

  @override
  String get weighManualBtn => 'Камерасыз — товарды тандап, салмакты киргизүү';

  @override
  String get peEdit => 'Товарды оңдоо';

  @override
  String get peName => 'Аталышы';

  @override
  String get peMinQty => 'Минималдуу калдык';

  @override
  String get peMinQtyHint =>
      'Мындан аз болсо — товар «Түгөнүп баратат» тизмесине түшөт';

  @override
  String get peStock => 'Калдык';

  @override
  String get peStockHint =>
      'Калдык документ менен гана өзгөрөт — себеби көрсөтүлгөн киреше, эсептен чыгаруу же ревизия, ПКдагыдай. Ошондо айырма болбойт.';

  @override
  String get peIncome => 'Киреше';

  @override
  String get peWriteOff => 'Эсептен чыгаруу';

  @override
  String get peRecount => 'Ревизия';

  @override
  String get peQtyIn => 'Канча келди';

  @override
  String get peQtyOut => 'Канчаны эсептен чыгаруу';

  @override
  String get peQtyFact => 'Чындыгында канча';

  @override
  String get peReason => 'Себеби';

  @override
  String get peReasonNeed => 'Себебин көрсөтүңүз.';

  @override
  String get peReasonExpired => 'Мөөнөтү өттү';

  @override
  String get peReasonDefect => 'Бузук';

  @override
  String get peReasonSpoiled => 'Бузулду';

  @override
  String get peReasonTheft => 'Уурдалды';

  @override
  String get peReasonOther => 'Башка';

  @override
  String get peReasonSupplier => 'Жеткирүү';

  @override
  String get peReasonCount => 'Кайра саноо';

  @override
  String get peApply => 'Өткөрүү';

  @override
  String peStockDone(String from, String to) {
    return 'Калдык: $from → $to';
  }

  @override
  String peStockSame(String qty) {
    return 'Калдык мурунтан $qty — эч нерсе өзгөргөн жок.';
  }

  @override
  String get peStockQueued =>
      'Документ түзүлдү — байланыш болгондо өзү өткөрүлөт.';

  @override
  String get peStockOffline =>
      'Сервер менен байланыш жок — калдык алынган жок, эч нерсе өзгөргөн жок.';

  @override
  String peStockBelowZero(String qty) {
    return 'Калдыктан көп эсептен чыгарууга болбойт (бар $qty).';
  }

  @override
  String get peSaved => 'Товар сакталды.';

  @override
  String get peNoChanges => 'Өзгөртүү жок.';

  @override
  String pePriceRestored(String price) {
    return 'Сервер сатуу баасын үстөк менен кайра эсептеди — киргизилген $price баасы кайтарылды.';
  }

  @override
  String get pePriceLocked =>
      'Бааны «Бааны өзгөртүү» укугу бар кызматкер өзгөртөт.';

  @override
  String get peLoadFailed =>
      'Товардын карточкасы серверден жүктөлгөн жок — байланыш болгондо оңдосо болот.';

  @override
  String get peNameNeed => 'Аталышын жазыңыз.';

  @override
  String peBarcodeTaken(String code, String name) {
    return '$code штрихкоду «$name» товарында бар.';
  }

  @override
  String peBarcodeTwice(String code) {
    return '$code штрихкоду эки жолу жазылган.';
  }

  @override
  String get peScan => 'Сканерлөө';

  @override
  String get bcGenTooltip => 'Штрихкод түзүү';

  @override
  String get bcGenReplace => 'Азыркы штрихкод жаңысына алмаштырылсынбы?';

  @override
  String get bcGenDo => 'Түзүү';

  @override
  String pePackQty(String qty) {
    return 'таңгакта $qty';
  }

  @override
  String get peNoAccess =>
      'Товарды «Кампа» укугу бар кызматкер же администратор оңдой алат.';

  @override
  String get smartNoLink =>
      'Сервер менен байланыш жок — эч нерсе өткөрүлгөн жок. Тизме экранда калды, интернет чыкканда кайталаңыз.';

  @override
  String smartPartial(int done, int total, int left) {
    return '$total ичинен $done өткөрүлдү, $left өткөрүлгөн жок — «Жүктөө» баскычын дагы басыңыз: өткөрүлгөнү кайра кошулбайт.';
  }

  @override
  String get smartPendingReceipt =>
      'Мурунку жөнөтүүчүгө кирешенин жообу келген жок — адегенде «Жөнөтүүчү менен кабыл алууга» өтүп кайталаңыз, болбосо калдык эки жолу кошулушу мүмкүн.';

  @override
  String get smartPendingStock =>
      'Айрым саптар үчүн сервер жооп берген жок — адегенде «Жүктөө» баскычын дагы басыңыз: калдык эки эселенбейт.';

  @override
  String get smartRowLocked =>
      'Саптагы маалымат серверге жөнөтүлгөн — товарды жана атын өзгөртүүгө болбойт, кайра жөнөтүңүз.';

  @override
  String smartRowRejected(String reason) {
    return 'Четке кагылды: $reason';
  }

  @override
  String get smartRowUnknown =>
      'Сервер жооп берген жок — кайталаңыз: калдык эки эселенбейт.';

  @override
  String payDebtLeft(String sum) {
    return 'Калды карыз: $sum';
  }

  @override
  String get debtReceiptTitle => 'Карызды төлөгөндүгү жөнүндө квитанция';

  @override
  String get returnsTitle => 'Кайтаруулар';

  @override
  String get shDebtPayments => 'Карыздарды төлөө';

  @override
  String get shDebtPrepay => 'Карыз боюнча алдын ала төлөм';

  @override
  String get exitPressAgain => 'Чыгуу үчүн дагы бир жолу басыңыз';

  @override
  String get createProductWithCode => 'Бул код менен товар түзүү';

  @override
  String get printLabel => 'Этикетка басып чыгаруу';

  @override
  String get openingCashField => 'Сменанын башында кассадагы сумма';

  @override
  String get advisorGreeting =>
      'Саламатсызбы! Мен Нур Кеңешчимин. Сатуулар, карыздар же калдыктар жөнүндө сураңыз — дүкөнүңүздүн маалыматтары боюнча жооп берем.';

  @override
  String get a11yBack => 'Артка';

  @override
  String get a11yShowPassword => 'Сырсөздү көрсөтүү';

  @override
  String get a11yHidePassword => 'Сырсөздү жашыруу';

  @override
  String get a11yScanner => 'Сканер';

  @override
  String get a11yCallAi => 'ИИге чалуу';

  @override
  String get stockNone => 'жок';

  @override
  String get cartEmptyHint =>
      'Товар кошуңуз: издеңиз, сканерлеңиз же плитканы басыңыз';

  @override
  String get toProductsBtn => 'Товарларга';

  @override
  String get closeBtn => 'Жабуу';

  @override
  String clientDebtLine(String sum) {
    return 'Карыз: $sum сом';
  }

  @override
  String get apiNoToken => 'Сервер токенди кайтарган жок.';

  @override
  String get apiSessionExpired => 'Сессиянын мөөнөтү бүттү — кайра кириңиз.';

  @override
  String get apiTimeout => 'Сервер убагында жооп берген жок.';

  @override
  String get apiNoConnection => 'Сервер менен байланыш жок.';

  @override
  String apiServerError(String code) {
    return 'Сервер катасы $code.';
  }

  @override
  String get apiNetworkError => 'Тармак катасы.';

  @override
  String get adminNoLink => 'байланыш жок';

  @override
  String printLabelAsk(String name, String code) {
    return '«$name» үчүн этикетка басылып чыксынбы? Штрихкод $code';
  }

  @override
  String get notNow => 'Азыр эмес';

  @override
  String get bcGenTaken => 'Бул код серверде бош эмес — жаңысы берилди.';

  @override
  String get tapePrint => 'Басып чыгаруу';

  @override
  String get tapeSend => 'Жөнөтүү';

  @override
  String get zReport => 'Смена Z-жыйынтыгы';

  @override
  String get xReportNote => 'X-отчет — аралык: смена жабыла элек.';

  @override
  String get shSalesBlock => 'Сатуулар';

  @override
  String get shCashBlock => 'Кассадагы акча';

  @override
  String get shPrepayIncluded =>
      'накталайда/накталайсызда — карыз боюнча алдын ала төлөм';

  @override
  String get shReturnsUnknown =>
      'Смена кайтарууларын жүктөө мүмкүн болгон жок — байланыш жок.';

  @override
  String shReturnsPartial(int n) {
    return 'Жарым-жартылай: сервердин жообунда кассири жок $n кайтаруу эсепке алынган жок. Сайттан салыштырыңыз.';
  }

  @override
  String returnsCountN(int n) {
    return '$n кайтаруу';
  }

  @override
  String get shReturnsCash => 'Накталай кайтаруулар';

  @override
  String get shCalcRow => 'Саптар боюнча эсеп';

  @override
  String shMismatch(String sum) {
    return 'Саптар боюнча эсеп серверден $sum сомго айырмаланат — сервердин санына ишениңиз.';
  }

  @override
  String get shNoSplit =>
      'Кайтаруулар жана карыз төлөмдөрү боюнча бөлүштүрүү жеткиликсиз — «Кассада болушу керек» серверден алынды.';

  @override
  String get shPartial =>
      'Айрым маалыматтар жүктөлгөн жок (байланыш жок) — экранды жаңылаңыз.';

  @override
  String get debtReceiptBefore => 'Төлөөгө чейинки карыз';

  @override
  String get debtReceiptMethod => 'Төлөө ыкмасы';

  @override
  String get debtReceiptAccepted => 'Кабыл алынды';

  @override
  String get debtReceiptCleared => 'Карыз толугу менен төлөндү.';

  @override
  String debtReceiptAcceptedLine(String sum, String left) {
    return 'Кабыл алынды $sum, калдык $left';
  }

  @override
  String get returnReasonTitle => 'Кайтаруу себеби (милдеттүү эмес)';

  @override
  String get returnReasonLabelShort => 'Себеби';

  @override
  String get returnReasonDefect => 'Бузук';

  @override
  String get returnReasonNotFit => 'Туура келген жок';

  @override
  String get returnReasonCashier => 'Кассирдин катасы';

  @override
  String get listCachedNote =>
      'Байланыш жок — акыркы сакталган тизме көрсөтүлдү.';

  @override
  String get checksLoadFailed => 'Чектерди жүктөө мүмкүн болгон жок';

  @override
  String get debtsLoadFailed => 'Карыздарды жүктөө мүмкүн болгон жок';

  @override
  String get callMinimize => 'Жыйыштыруу';

  @override
  String get callAiListens => 'ИИ угуп жатат';

  @override
  String get callInterrupted => 'Бөлдүңүз — угуп жатам';

  @override
  String get callExtend => 'Дагы 10 мүнөт';

  @override
  String get callExtended => 'Сүйлөшүү 10 мүнөткө узартылды';

  @override
  String get callNoLink => 'ИИ менен байланыш жок';

  @override
  String get callNoLinkWork => 'ИИ менен байланыш жок, экрандан иштеңиз';

  @override
  String callNoLinkEnds(int n) {
    return 'Байланыш келбесе, $n с кийин аякталат';
  }

  @override
  String get callNoLinkHelp => 'Себет, издөө жана кампа ИИсиз иштейт.';

  @override
  String get callSlowServer => 'Сервер жай жооп берүүдө…';

  @override
  String get callStillLoading => 'Маалымат дагы жүктөлүүдө — келгенде айтам';

  @override
  String get callSearching => 'Издеп жатам…';

  @override
  String get callSpeakingShort => 'Айтып жатам…';

  @override
  String get callHush => 'Токто';

  @override
  String get callOpenFull =>
      'Нур Кеңешчи менен сүйлөшүү — ачуу; кармап туруу — «Токто»';

  @override
  String aiSays(String text) {
    return 'ИИ: $text';
  }

  @override
  String get aiUndo => 'Жокко чыгаруу';

  @override
  String get aiCantUndo => 'жокко чыгарууга болбойт';

  @override
  String get aiPickTitle => 'Кайсы вариант?';

  @override
  String get aiAmbiguousHint => 'Номерин айтыңыз же басыңыз';

  @override
  String get aiNotThat => 'Ал эмес';

  @override
  String aiYesNumber(String num) {
    return 'Ооба, $num';
  }

  @override
  String get aiNoRepeat => 'Жок, кайра айтам';

  @override
  String aiConfirmDo(String what) {
    return 'Аткаруу: $what';
  }

  @override
  String get aiDo => 'Аткаруу';

  @override
  String get aiDont => 'Кереги жок';

  @override
  String get aiOrSayYes => 'же «ооба» деп айтыңыз';

  @override
  String get aiModeAskMoney => 'Акчадан мурун сураш';

  @override
  String get aiWhatPay => 'төлөм';

  @override
  String get aiWhatReturn => 'кайтаруу';

  @override
  String get aiWhatReceive => 'кирешени киргизүү';

  @override
  String get aiWhatWriteOff => 'эсептен чыгаруу';

  @override
  String get aiWhatCount => 'ревизия';

  @override
  String get aiWhatCloseShift => 'сменаны жабуу';

  @override
  String get aiWhatSalary => 'кызматкерге төлөө';

  @override
  String get aiWhatDebt => 'карызды төлөө';

  @override
  String get aiWhatDelete => 'товарды өчүрүү';

  @override
  String get aiWhatPrice => 'баасын өзгөртүү';

  @override
  String get aiWhatCash => 'кассага салуу же алуу';

  @override
  String get aiWhatOther => 'аракет';

  @override
  String get tblBigger => 'Чоңураак';

  @override
  String get tblSmaller => 'Кичирээк';

  @override
  String get tblToChat => 'Чатка';

  @override
  String get tblPdf => 'PDF';

  @override
  String get tblShare => 'Бөлүшүү';

  @override
  String get tblPin => 'Бекитүү';

  @override
  String get tblPinned => 'Бекитилген';

  @override
  String tblMore(int n) {
    return 'Дагы $n көрсөтүү';
  }

  @override
  String tblMoreLeft(int n) {
    return '$n калды';
  }

  @override
  String get tblAllShown => 'Бардык саптар көрсөтүлдү';

  @override
  String tblSource(String source, String time) {
    return 'программанын маалыматы · $source · $time';
  }

  @override
  String get tblEmpty => 'Азырынча бош';

  @override
  String tblEmptyFilter(String q) {
    return '«$q» боюнча эч нерсе табылган жок';
  }

  @override
  String get tblClearFilter => 'Чыпканы алуу';

  @override
  String get tblOpenObject => 'Ачуу';

  @override
  String tblRowsN(int n) {
    return '$n сап';
  }

  @override
  String get tblPrev => 'Мурунку таблица';

  @override
  String get tblNext => 'Кийинки таблица';

  @override
  String get tblCollapse => 'Панелди жыйыштыруу';

  @override
  String get tblClose => 'Таблицаны жабуу';

  @override
  String get tblSentChat => 'Таблица чатка кошулду';

  @override
  String get tblPdfFailed => 'PDF жасалган жок';

  @override
  String get vcCamera => 'Камера';

  @override
  String get vcCameraOn => 'Камера күйүк';

  @override
  String get vcCameraOff => 'Камера өчүк';

  @override
  String get vcSwitchCamera => 'Башка камера';

  @override
  String get vcTorch => 'Фонарик';

  @override
  String get vcRecording => 'Камера';

  @override
  String get vcAiSees => 'ИИ көрүп жатат';

  @override
  String get vcEconomy => 'Трафикти үнөмдөө';

  @override
  String get vcEconomyHint => '3 секундада 1 кадр жана сиз сүйлөгөндө гана';

  @override
  String get vcStarting => 'Камераны күйгүзүп жатам…';

  @override
  String get vcSearching => 'Каталогдон издеп жатам…';

  @override
  String get vcWhatIsIt => 'Бул эмне?';

  @override
  String get vcInvoice => 'Накладная';

  @override
  String get vcShelf => 'Текче';

  @override
  String get vcPriceTag => 'Баа белгиси';

  @override
  String get wizAddProduct => 'Товар кошуу';

  @override
  String get wizTitle => 'Товар кошуу';

  @override
  String get wizSlotFront => 'Алды';

  @override
  String get wizSlotBack => 'Арты';

  @override
  String get wizSlotSide => 'Капталы';

  @override
  String get wizSlotCode => 'Штрихкод';

  @override
  String wizShootSide(String side) {
    return 'Тартуу: $side';
  }

  @override
  String get wizCreate => 'Товар түзүү';

  @override
  String wizDraft(String name, String price) {
    return '«$name» · $price сом';
  }

  @override
  String get wizHint => '«Даяр» деңиз же «Тартуу» басыңыз';

  @override
  String wizDuplicate(String name) {
    return 'Мурунтан бар: «$name»';
  }

  @override
  String wizPhotoAlt(String side) {
    return 'Сүрөт: $side';
  }

  @override
  String get wizEnterCode => 'Штрихкодду киргизүү';

  @override
  String get wizEnterCodeHint =>
      'Сканер менен окутуңуз же цифраларды киргизиңиз';

  @override
  String get vcCardProduct => 'Товар';

  @override
  String get vcNotFound =>
      'Каталогдон табылган жок. Жакындатыңыз же штрихкодду көрсөтүңүз';

  @override
  String get vcExact => 'так';

  @override
  String get vcByBarcode => 'штрихкод';

  @override
  String get vcByPhoto => 'сүрөт боюнча';

  @override
  String get vcLooksLike => 'Окшош:';

  @override
  String get vcPickHint => 'тандоо үчүн басыңыз';

  @override
  String vcPrice(String sum) {
    return 'Баасы: $sum сом';
  }

  @override
  String vcStock(String qty) {
    return 'Калдык: $qty';
  }

  @override
  String vcInvoiceSummary(int n, int known, int fresh) {
    return 'Саптар: $n · каталогто: $known · жаңы: $fresh';
  }

  @override
  String vcOpenReceive(int n) {
    return 'Кирешени ачуу ($n)';
  }

  @override
  String get vcReceiveEndsCall => 'Сүйлөшүү аяктайт, маек чатта сакталат';

  @override
  String get vcNewItemsHint =>
      'Жаңы товарлар каталогто жок — «Кампа → Накладная» аркылуу кошуңуз';

  @override
  String get vcNoProcure => 'Кирешени администратор өткөрөт';

  @override
  String get vcShelfHint =>
      'ИИ көз чамасы менен санайт — ревизиядан мурун кайра текшериңиз';

  @override
  String get vcShelfCols => 'көрүнүп турат · программада';

  @override
  String get vcShelfThing => 'товар';

  @override
  String get vcOnTag => 'Баа белгисинде';

  @override
  String get vcInProgram => 'Программада';

  @override
  String vcTagMismatch(String tag, String sum) {
    return 'Баа белгисинде $tag сом, программада $sum сом — дал келбейт';
  }

  @override
  String get vcTagOk => 'Баа белгисиндеги баа программага дал келет';

  @override
  String get vcTagFixHint =>
      'Баасын бул жерде оңдобойбуз — товар картасы аркылуу';

  @override
  String vcMoreLines(int n) {
    return 'дагы $n сап';
  }

  @override
  String get vcNewTag => 'жаңы';

  @override
  String get vcConsentTitle => 'Сүйлөшүүдө камераны күйгүзөбүзбү?';

  @override
  String get vcConsentBody =>
      'Камера күйүк турганда кадрлар (секундасына болжол менен 1) дүкөнүңүздүн ИИ ачкычы менен Google Gemini\'ге кетет. Алар телефондо да, NurCRM\'де да сакталбайт — чатта сүйлөшүүнүн тексти гана калат. Акысыз ачкычта Google маалыматты өз моделдерин жакшыртуу үчүн колдонушу мүмкүн. Сатып алуучулардын жүзүн, банк карталарын жана бөтөн документтерди көрсөтпөңүз.';

  @override
  String get vcConsentPtSends =>
      'Камера күйүк турганда кадрлар (секундасына болжол менен 1) дүкөнүңүздүн ИИ ачкычы менен Google Gemini\'ге кетет.';

  @override
  String get vcConsentPtSaved =>
      'Алар телефондо да, NurCRM\'де да сакталбайт — чатта сүйлөшүүнүн тексти гана калат.';

  @override
  String get vcConsentPtFree =>
      'Акысыз ачкычта Google маалыматты өз моделдерин жакшыртуу үчүн колдонушу мүмкүн.';

  @override
  String get vcConsentPtWarn =>
      'Сатып алуучулардын жүзүн, банк карталарын жана бөтөн документтерди көрсөтпөңүз.';

  @override
  String get vcConsentAgree => 'Камераны күйгүзүү';

  @override
  String get vcConsentLater => 'Азыр эмес';

  @override
  String vcCost(int n) {
    return 'Видеосүйлөшүү $n мүнөткө чейин созулат жана ИИ ачкычынын лимитин үндүккө караганда бир нече эсе тез сарптайт';
  }

  @override
  String get aiAccTitle => 'ИИге жеткиликтүүлүк';

  @override
  String get aiAccSection => 'ИИни кимдер колдоно алат';

  @override
  String aiAccLimitInfo(int n) {
    return 'ИИнин акысыз лимити (Google Gemini) бүт долбоор үчүн жалпы: мүнөтүнө жана күнүнө суроолор аз. Ошондуктан ИИни — Нур Кеңешчи, чалуу, акылдуу камера, сүрөт боюнча накладная, көрүнүшү боюнча таразы — дүкөндүн $n кызматкеринен ашыгы колдоно албайт. Кимге уруксат берээрин тандаңыз.';
  }

  @override
  String aiAccCounter(int a, int b) {
    return '$a / $b';
  }

  @override
  String aiAccLimitReached(int n) {
    return '$n кызматкерден көп тандоого болбойт. Башкага уруксат берүү үчүн адегенде тандалгандардын бирин алып салыңыз.';
  }

  @override
  String aiAccDenied(int n, String names) {
    return 'ИИ дүкөндүн $n кызматкерине гана жеткиликтүү: $names. Ээсине кайрылыңыз.';
  }

  @override
  String get aiAccDeniedNone =>
      'Азыр дүкөндө ИИ бардык кызматкерлерге өчүрүлгөн. Ээсине кайрылыңыз.';

  @override
  String get aiAccRecommend =>
      'ИИни эки кызматкер менен чектөөнү сунуштайбыз (жалпы акысыз лимит)';

  @override
  String get aiAccConfigure => 'Жөндөө';

  @override
  String get aiAccSaved => 'ИИге жеткиликтүүлүк сакталды';

  @override
  String get aiAccOwnerTag => 'ээси';

  @override
  String get aiAccYouTag => 'сиз';

  @override
  String get aiAccStaffNotLoaded =>
      'Кызматкерлердин тизмеси алынган жок (байланыш жокпу?). Мурда тандалгандар гана көрсөтүлдү.';

  @override
  String get aiAccNobody =>
      'Эч ким тандалган жок — ИИ бардык кызматкерлерге өчүрүлөт.';

  @override
  String get aiAccManagerOnly => 'Тизмени ээси же администратор өзгөртөт.';

  @override
  String get aiAccNoticeTitle => 'ИИ — жалпы лимит';

  @override
  String aiAccNoticeBody(int n) {
    return 'Дүкөндүн ИИси Google\'дун жалпы акысыз лимитинде иштейт, ошондуктан ага уруксат $n кызматкерден ашыгында жок — сизде бар. Узак чалууларды жана керексиз суроолорду жөн жерден жасабаңыз.';
  }

  @override
  String get aiAccGotIt => 'Түшүнүктүү';

  @override
  String get aiAccMineYes => 'Сизде ИИге уруксат бар';

  @override
  String get aiAccMineNo => 'ИИ сизге жеткиликсиз — ээсине кайрылыңыз';

  @override
  String get aiAccMineOpen =>
      'ИИ бардык кызматкерлерге жеткиликтүү (лимит коюлган эмес)';

  @override
  String get aiAccNotSet =>
      'Коюлган эмес — ИИ бардык кызматкерлерде (ээсине жана администраторго — дайыма)';

  @override
  String get assistantTitle => 'Жардамчы';

  @override
  String get assistantSub => 'үн менен башкаруу режими';

  @override
  String get aiModeDo => 'Өтүнүч боюнча аткаруу';

  @override
  String get aiModeDoHint =>
      'Экрандарды ачат, окуйт, таблицаларды көрсөтөт жана сиз сураганды дароо жасайт — жыйынтыгын үн чыгарып айтат, «Жокко чыгаруу» баскычы менен.';

  @override
  String get aiModeAskHint =>
      'Өтүнүч боюнча сыяктуу эле, бирок төлөм, кайтаруу, киреше, эсептен чыгаруу, ревизия, сменаны жабуу, акы төлөө, карыз, өчүрүү жана баа өзгөртүүдөн мурун «Аткаруу» карточкасы чыгат.';

  @override
  String get aiModeWatch => 'Көрүү гана';

  @override
  String get aiModeWatchHint =>
      'Экрандар, окуу жана таблицалар гана. Бир нерсе жасоону суранганда: «Бул режимде маалыматты өзгөртпөйм — экранда жасаңыз же режимди алмаштырыңыз» деп жооп берет.';

  @override
  String get aiModeDefault => 'демейки';

  @override
  String get aiModeSofterLocked => 'Ээси койгондон жумшагыраак — жеткиликсиз';

  @override
  String get aiModeOwnerSet => 'Ээси бул телефондогу баарына койгон:';

  @override
  String get aiModeStricterSelf => 'Өзүңүз үчүн катуураагын тандасаңыз болот';

  @override
  String get aiModeNoVoice => 'Режим үн менен өзгөрбөйт — ушул жерде гана';

  @override
  String get aiModeAskWhatTitle => 'Эмнеден мурун сурайт';

  @override
  String get aiModeAskNote =>
      '«Аткаруу: … сумма …» карточкасы жана бир баскыч — же үн менен «ооба». Эки жолу ырастоо жок.';

  @override
  String get aiModeFooter => 'Жардамчынын укуктары — колдонуучунун ролу боюнча';

  @override
  String get aiModeSaved => 'Режим сакталды';

  @override
  String get aiLogTitle => 'ИИ аракеттери';

  @override
  String get aiLogSubAll => 'телефондогу бардык жазуулар';

  @override
  String get aiLogMoneyOnly => 'Акча гана';

  @override
  String get aiLogUndone => 'Жокко чыгарылгандар';

  @override
  String get aiLogByDate => 'Датасы боюнча чыпка';

  @override
  String get dayYesterday => 'Кечээ';

  @override
  String aiLogUndoneAt(String time) {
    return 'жокко чыгарылды $time';
  }

  @override
  String get aiLogCantUndoWriteOff =>
      'жокко чыгарууга болбойт · эсептен чыгарса болот';

  @override
  String get aiUndoRequest => 'жокко чыгаруу — ээсине өтүнүч менен';

  @override
  String get aiLogUndoGone => 'жокко чыгаруу чалууда гана болгон';

  @override
  String aiLogUndoneToast(String what) {
    return 'Жокко чыгарылды: $what';
  }

  @override
  String get aiLogUndoConflict => 'ИИден кийин өзгөргөн — экрандан текшериңиз';

  @override
  String aiLogUndoFailed(String what) {
    return 'Жокко чыгаруу мүмкүн болгон жок: $what. Байланышты жана экранды текшериңиз.';
  }

  @override
  String get aiLogUndoShift =>
      'Жокко чыгаруу мүмкүн эмес: смена жабылган же кассада акча аз';

  @override
  String aiLogUndoPartial(int done, int total) {
    return '$total ичинен $done кайтарылды; калганы ИИден кийин өзгөргөн же болгон жок';
  }

  @override
  String get aiLogEmpty => 'ИИ бул телефондо азырынча эч нерсе жасаган жок';

  @override
  String get aiLogEmptyHint =>
      'Нур Кеңешчиге чалып, эмне кылуу керектигин айтыңыз — ар бир жазуу «Жокко чыгаруу» баскычы менен ушул жерде пайда болот';

  @override
  String get aiLogCall => 'Кеңешчиге чалуу';

  @override
  String get aiLogMineOnly => 'Сиздин аракеттериңиз гана көрүнөт';

  @override
  String get aiLogFooter =>
      'Телефондо сакталат, 2000 жазууга чейин. ИИ ачкычтары, үн жана кардар телефондору жок.';

  @override
  String get aiGroupNav => 'Экрандар';

  @override
  String get aiGroupCart => 'Чек жана буйрутмалар';

  @override
  String get aiGroupMoney => 'Акча';

  @override
  String get aiGroupProduct => 'Товарлар';

  @override
  String get aiGroupTask => 'Тапшырмалар';

  @override
  String get aiGroupSalary => 'Эмгек акы';

  @override
  String get auditAi => 'ИИ';

  @override
  String get stfTasksTitle => 'Тапшырмалар';

  @override
  String stfTasksSub(int n, String late) {
    return 'Тапшырма: $n · мөөнөтү өткөн: $late';
  }

  @override
  String get stfMyTasksTitle => 'Менин тапшырмаларым';

  @override
  String stfMySub(String name, String today, String late) {
    return '$name · бүгүнкү: $today · мөөнөтү өткөн: $late';
  }

  @override
  String get stfFilterOverdue => 'Мөөнөтү өткөн';

  @override
  String get stfFilterDone => 'Аткарылган';

  @override
  String get stfStNew => 'жаңы';

  @override
  String get stfStWork => 'иште';

  @override
  String get stfStDone => 'аткарылды';

  @override
  String get stfOverdue => 'мөөнөтү өттү';

  @override
  String get stfNoDue => 'мөөнөтсүз';

  @override
  String get stfDue => 'Мөөнөтү';

  @override
  String get stfRemind => 'Эскертүү';

  @override
  String get stfUrgent => 'Шашылыш тапшырма';

  @override
  String get stfNewTask => 'Жаңы тапшырма';

  @override
  String get stfTaskField => 'Эмне кылуу керек';

  @override
  String get stfTaskHint => 'Мисалы: суусундуктар текчесин кайра санаңыз';

  @override
  String get stfAssignee => 'Аткаруучу';

  @override
  String get stfAssigneeHint => 'Кызматкерди тандаңыз';

  @override
  String get stfCreate => 'Тапшырма коюу';

  @override
  String get stfNeedTitle => 'Эмне кылуу керектигин жазыңыз';

  @override
  String get stfNeedAssignee => 'Аткаруучуну тандаңыз';

  @override
  String get stfTaskCreated => 'Тапшырма коюлду';

  @override
  String get stfToWork => 'Ишке алуу';

  @override
  String get stfMarkDone => 'Аткарылды';

  @override
  String get stfReopen => 'Кайра ишке алуу';

  @override
  String get stfEditDue => 'Мөөнөттү өзгөртүү';

  @override
  String get stfEditAssignee => 'Башкага өткөрүү';

  @override
  String get stfDelete => 'Өчүрүү';

  @override
  String stfDeleteAsk(String title) {
    return '«$title» тапшырмасын өчүрөбүзбү?';
  }

  @override
  String get stfNoTasks => 'Тапшырма жок';

  @override
  String get stfLoadFail => 'Тапшырмаларды жүктөө мүмкүн болгон жок';

  @override
  String get stfPriorityHint =>
      'Шашылыштык жана эскертүү азырынча тиркемеде гана көрүнөт; сайттагы тапшырмада кызматтык белги чыгышы мүмкүн — сервер жаңырганга чейин';

  @override
  String get stfNoBackground =>
      'Фондук эскертмелер жок — эскертүү колдонмону ачканда көрүнөт';

  @override
  String stfOverduePlate(int n, String title) {
    return 'Мөөнөтү өткөн: $n — «$title»';
  }

  @override
  String stfRemindPlate(String title) {
    return 'Эскертүү: «$title»';
  }

  @override
  String stfTodayPlate(String today, String late) {
    return 'Бүгүнкү тапшырма: $today · мөөнөтү өткөн: $late';
  }

  @override
  String get stfSalaryTitle => 'Эмгек акы';

  @override
  String stfSalarySub(int n) {
    return 'кызматкерлер: $n';
  }

  @override
  String get stfPeriodMonth => 'Ушул ай';

  @override
  String get stfPeriodLast => 'Өткөн ай';

  @override
  String get stfPeriodWeek => '7 күн';

  @override
  String get stfAccrued => 'Эсептелди';

  @override
  String get stfPaid => 'Төлөндү';

  @override
  String get stfLeft => 'Калды';

  @override
  String get stfColEmployee => 'Кызматкер';

  @override
  String get stfChecking => 'Төлөмдөр текшерилүүдө…';

  @override
  String get stfPartialPlate =>
      'Жарым-жартылай текшерилди — төлөө суммасы өзү коюлбайт.';

  @override
  String get stfTrustTimeout =>
      'Мурунку төлөмдөрдү текшерүү өз убагында бүтпөдү — төлөөдөн мурун сайттан текшериңиз.';

  @override
  String get stfTrustJournal =>
      'Телефондогу төлөмдөр журналы окулбайт — төлөмдөр жабык.';

  @override
  String get stfTrustNotChecked =>
      'Мурунку төлөмдөрдү текшерүү мүмкүн болгон жок — төлөөдөн мурун сайттан текшериңиз.';

  @override
  String stfTrustUnassigned(String sum, int n) {
    return 'Кызматкерге байланбаган «Эмгек акы» чыгымдары бар: $sum сом ($n) — сайттан текшериңиз.';
  }

  @override
  String get stfTrustPartial =>
      '«Эмгек акы» беренесиз төлөмдөр же бардык чыгымдар көрүнбөйт — төлөөдөн мурун сайттан текшериңиз.';

  @override
  String get stfTrustPartialNotes =>
      'Төлөмдөрдүн бир бөлүгү мезгилге жарым-жартылай гана кирди — алынган жок, текшериңиз.';

  @override
  String get stfTrustByName =>
      'Төлөмдөрдүн бир бөлүгү чыгымдын атындагы ат боюнча гана табылды — төлөөдөн мурун сайттан текшериңиз.';

  @override
  String stfPartialNote(String sum, String period) {
    return '$period мезгили үчүн $sum сом төлөм бул мезгилге жарым-жартылай гана кирет — алынган жок';
  }

  @override
  String get stfSalaryEmpty =>
      'Мезгил үчүн эсептөө жок: сатуу жок же эч кимде төлөм схемасы жок.';

  @override
  String get stfSalaryDenied =>
      'Эмгек акыга уруксат жок: бул аккаунттун сайтта кызматкерлердин аналитикасына укугу жок.';

  @override
  String get stfSalaryFail => 'Эмгек акыны жүктөө мүмкүн болгон жок';

  @override
  String get stfSalaryHint =>
      'Эсептелген — сервердин эсеби, компьютерде жана сайттагыдай. «Төлөндү» — ушул мезгилдеги «Эмгек акы» чыгымдары.';

  @override
  String get stfLockedTag => 'жоопсуз төлөм';

  @override
  String get stfPayBtn => 'Төлөө';

  @override
  String stfPayTitle(String name) {
    return 'Төлөм: $name';
  }

  @override
  String get stfKindSalary => 'Эмгек акы';

  @override
  String get stfKindAdvance => 'Аванс';

  @override
  String get stfPayAmount => 'Сумма';

  @override
  String get stfPayEnterAmount => 'Сумманы өзүңүз киргизиңиз';

  @override
  String get stfPayBox => 'Касса';

  @override
  String get stfPayComment => 'Комментарий (милдеттүү эмес)';

  @override
  String get stfPayNeedAmount => 'Төлөм суммасын киргизиңиз';

  @override
  String get stfPayNoCash =>
      'Төлөм үчүн компаниянын кассасы табылган жок — сайтта же ээсинин программасында төлөңүз.';

  @override
  String get stfSalesBoxWarn =>
      'Бул филиалдын сатуу кассасы — чыгым анын калдыгын азайтат.';

  @override
  String stfPayConfirm(String sum, String name, String box) {
    return '$name кызматкерине «$box» кассасынан $sum сом төлөйбүзбү?';
  }

  @override
  String stfPayDone(String sum, String name) {
    return 'Төлөндү $sum сом: $name';
  }

  @override
  String get stfPayFailed => 'Төлөм өткөн жок.';

  @override
  String stfPayUnknown(String sum, String box) {
    return 'Сервердин жообу келген жок — $sum сом төлөм («$box» кассасы) өтүп кетиши мүмкүн. Кайталоого болбойт: кызматкерди ачып «Серверден текшерүү» басыңыз.';
  }

  @override
  String stfPayLock(String sum, String when, String box) {
    return 'Мурунку $sum сом төлөм ($when, «$box» кассасы) сервердин жообусуз калды. Сиз аны текшергенче бул кызматкерге жаңы төлөм жабык.';
  }

  @override
  String get stfPayLockShort =>
      'Бул кызматкерге мурунку төлөм сервердин жообусуз калды — сиз текшергенче жаңысы жабык.';

  @override
  String get stfPayDup => 'Дал ушундай төлөм азыр эле өттү — кайталанбайт.';

  @override
  String get stfPayJournal =>
      'Телефондогу төлөмдөр журналы окулбайт — аныксыз кош төлөмдөн коргоо жок, төлөм жөнөтүлгөн жок.';

  @override
  String get stfLockCheck => 'Серверден текшерүү';

  @override
  String get stfLockCheckShort => 'Текшерүү';

  @override
  String get stfLockYes => 'Төлөм сайтта БАР';

  @override
  String get stfLockNo => 'Төлөм сайтта ЖОК';

  @override
  String get stfLockYesAsk => 'Төлөм сайтта барбы?';

  @override
  String get stfLockNoAsk => 'Төлөм сайтта жокпу?';

  @override
  String get stfLockResolveHint =>
      'NurCRM сайтындагы «Эмгек акы» чыгымын салыштырыңыз. Туура эмес бассаңыз, төлөм туура эмес эсепке алынат же эки жолу өтөт.';

  @override
  String get stfLockFound => 'Төлөм серверден табылды жана эсепке алынды.';

  @override
  String get stfLockNotFound =>
      'Серверден төлөм табылган жок — кулпу калат, сайттан текшергенден кийин өзүңүз чечиңиз.';

  @override
  String get stfConfirm => 'Ырастоо';

  @override
  String get stfRecentPayouts => 'Ушул телефондон акыркы төлөмдөр';

  @override
  String get stfCancelBtn => 'Жокко чыгарууну сурануу';

  @override
  String stfCancelAsk(String sum) {
    return '$sum сом төлөмдү жокко чыгарууну сурайбызбы?';
  }

  @override
  String get stfCancelHint =>
      'Төлөмдү NurCRM сайтында ээси же бухгалтер гана жокко чыгарат — телефондон өтүнүч жөнөтүлөт.';

  @override
  String get stfCancelSend => 'Өтүнүч жөнөтүү';

  @override
  String get stfCancelSent => 'Жокко чыгаруу өтүнүчү ээсине жөнөтүлдү.';

  @override
  String get stfSchemeBtn => 'Төлөм схемасы';

  @override
  String stfSchemeTitle(String name) {
    return 'Төлөм схемасы: $name';
  }

  @override
  String get stfSchemeSaved => 'Төлөм схемасы сакталды';

  @override
  String get stfSchemeSalary => 'Айлык';

  @override
  String get stfSchemePercent => 'Сатуудан пайыз';

  @override
  String get stfSchemeSalaryPercent => 'Айлык + сатуудан пайыз';

  @override
  String get stfSchemePerItem => 'Ар бир сатылган товар үчүн';

  @override
  String get stfSchemeSalaryItem => 'Айлык + ар бир товар үчүн';

  @override
  String get stfSchemePercentItem => 'Пайыз + ар бир товар үчүн';

  @override
  String get stfNoScheme => 'схема жөндөлгөн эмес';

  @override
  String get stfMonthly => 'Айына айлык';

  @override
  String get stfPercent => 'Сатуудан пайыз';

  @override
  String get stfPerItem => 'Ар бир сатылган товар үчүн';

  @override
  String get stfBonus => 'Сатуу үчүн эсептелди';

  @override
  String get gkOpen => 'Gemini ачкычтары (бир нече аккаунт)';

  @override
  String get gkTitle => 'Gemini ачкычтары';

  @override
  String get gkHint =>
      'Ачкычтар иретине менен колдонулат: биринчи иштегени. Ачкычтын лимити бүтсө, ИИ кийинкисин өзү алат. Тизме кассадагы жана ээсинин программасындагы менен бирдей.';

  @override
  String get gkWarn =>
      'Google эрежелери лимиттерди бир нече аккаунт же долбоор менен айланып өтүүнү тыюу салышы мүмкүн — ачкычтар бөгөттөлүшү ыктымал. Google бекер деңгээли суроолорду өз өнүмдөрүн жакшыртууга колдониши мүмкүн. Чечим сизде.';

  @override
  String get gkViewOnly =>
      'Ачкыч тизмесин ээси жана администратор өзгөртөт. Сизге ачкычтар толук көрсөтүлбөйт — ИИ ушул тизме менен иштейт.';

  @override
  String gkWorkingCount(int a, int b) {
    return 'Иштеген ачкычтар: $a / $b';
  }

  @override
  String get gkEmpty =>
      'Ачкыч жок. Биринчисин кошуңуз — аны Google AI Studio берет.';

  @override
  String get gkAdd => 'Ачкыч кошуу';

  @override
  String get gkAddBtn => 'Кошуу';

  @override
  String get gkPasteKey => 'Google AI Studio ачкычын чаптаңыз';

  @override
  String get gkLabelHint => 'Белги (милдеттүү эмес)';

  @override
  String gkLabelDefault(int n) {
    return 'Аккаунт $n';
  }

  @override
  String get gkMax => '10 ачкычтан көп болбойт.';

  @override
  String get gkDuplicate => 'Мындай ачкыч тизмеде бар.';

  @override
  String get gkShow => 'Көрсөтүү';

  @override
  String get gkHide => 'Жашыруу';

  @override
  String get gkEnabled => 'Күйгүзүлгөн';

  @override
  String get gkMoveUp => 'Жогору';

  @override
  String get gkMoveDown => 'Төмөн';

  @override
  String get gkDelete => 'Өчүрүү';

  @override
  String gkDeleteAsk(String name, String tail) {
    return '«$name» ачкычын (…$tail) өчүрөбүзбү?';
  }

  @override
  String get gkStateWorking => 'иштейт';

  @override
  String gkStateExhausted(String time) {
    return '$time чейин түгөндү';
  }

  @override
  String get gkStateInvalid => 'жараксыз';

  @override
  String get gkStateDisabled => 'өчүк';

  @override
  String get gkStateUnchecked => 'текшерилген эмес';

  @override
  String get gkCheckOk => 'Ачкыч иштейт ✓';

  @override
  String gkCheckFail(String err) {
    return 'Текшерүүгө болбоду: $err';
  }

  @override
  String get aiKeysSavedPhoneOnly =>
      'Телефондо сакталды, бирок булутка кеткен жок — касса жана ээсинин программасы азырынча көрбөйт. Кийинчерээк кайталаңыз.';

  @override
  String get klTitle => 'Кассага туташуу';

  @override
  String get klProfileOff => 'Туташкан эмес';

  @override
  String klKassaName(String name) {
    return '«$name» кассасы';
  }

  @override
  String get klScanQr => 'Кассадагы QR кодду скандоо';

  @override
  String get klScanQrHint =>
      'Кассасы бар компьютерде «Телефонду туташтыруу» терезесин ачып, камераны QR кодго багыттаңыз. Телефон менен касса бир Wi-Fi тармагында болушу керек, касса иштеп турушу керек.';

  @override
  String get klScanQrTitle =>
      'Камераны кассанын экранындагы QR кодго багыттаңыз';

  @override
  String get klNotKassaQr => 'Бул NurMarket кассасынын QR коду эмес';

  @override
  String get klBadQr =>
      'QR кодду окуу мүмкүн болгон жок. Кассада «Телефонду туташтыруу» терезесин ачып, кайра скандаңыз.';

  @override
  String get klQrVersion =>
      'Бул касса тиркемеден жаңыраак. Телефондогу NurMarket\'ти жаңыртыңыз.';

  @override
  String get klManual => 'Шилтемени кол менен киргизүү';

  @override
  String get klManualHint => 'QR коддун текстин чаптаңыз (nurmarket-link:…)';

  @override
  String get klConnect => 'Туташтыруу';

  @override
  String klConnected(String name) {
    return '«$name» кассасы туташты';
  }

  @override
  String get klConnectedPlain => 'Касса туташты';

  @override
  String get klStUnpaired => 'Касса туташкан эмес';

  @override
  String get klStUnknown => 'Байланыш текшерилген жок';

  @override
  String get klStOnline => 'Байланышта';

  @override
  String get klStNotFound => 'Касса бул тармакта табылган жок';

  @override
  String get klStRevoked => 'Телефон кассада өчүрүлгөн';

  @override
  String get klStBadTime => 'Телефондогу убакытты текшериңиз';

  @override
  String get klStDisabled => 'Кассада телефондордон кабыл алуу өчүк';

  @override
  String get klStRePair =>
      'Байланыш эскирди — кассадагы QR кодду кайра скандаңыз';

  @override
  String get klChecking => 'Байланыш текшерилүүдө…';

  @override
  String get klShiftOpen => 'Кассадагы смена: ачык';

  @override
  String get klShiftClosed => 'Кассадагы смена: жабык';

  @override
  String get klCheckNow => 'Байланышты текшерүү';

  @override
  String get klDisconnect => 'Ажыратуу';

  @override
  String klDisconnectAsk(String name) {
    return 'Телефонду «$name» кассасынан ажыратабызбы? Кайра QR код менен туташтырса болот. Кассанын өзүндө телефон тизмеде калат — аны ошол жактан да өчүрүңүз.';
  }

  @override
  String get klDisconnected => 'Телефон кассадан ажыратылды';

  @override
  String get klScannerMode => 'Касса үчүн сканер';

  @override
  String get klScannerHint =>
      'Штрихкодго багыттаңыз — товар кассадагы чекке түз түшөт';

  @override
  String get klSetupAsk => 'Кассага туташуу жөндөлгөн эмес';

  @override
  String get klSetup => 'Жөндөө';

  @override
  String get klScSending => 'Жөнөтүлүүдө…';

  @override
  String get klScAdded => 'Кошулду';

  @override
  String get klScNotFound => 'Табылган жок';

  @override
  String get klScTillInactive => 'Кассанын терезеси ачык эмес';

  @override
  String get klScQueued => 'Байланыш жок — кезекте';

  @override
  String get klScQueueFull => 'Кезек толду — код кабыл алынган жок';

  @override
  String get klScExpired => 'Жөнөтүлгөн жок: касса жооп берген жок';

  @override
  String get klScFailed => 'Касса кодду кабыл албады';

  @override
  String get klLastCode => 'Акыркы код';

  @override
  String klCounter(int n) {
    return 'Чекке кошулду: $n';
  }

  @override
  String klQueueLeft(int n) {
    return 'Кезекте: $n';
  }

  @override
  String get globalSource => 'NurCRM маалымдамасынан';

  @override
  String globalFoundAsk(String name) {
    return 'NurCRM маалымдамасында бар: $name';
  }

  @override
  String get globalCatalogTitle => 'Товарлар маалымдамасы';

  @override
  String globalCatalogStatus(String count, String date, String added) {
    return 'Маалымдама: $count товар, жаңыртылды $date, кошулду: $added';
  }

  @override
  String get globalCatalogOff => 'Маалымдама жеткиликсиз';

  @override
  String globalBulkFilled(int n) {
    return 'NurCRM маалымдамасынан аталыштар коюлду: $n';
  }

  @override
  String advisorQuota(String time) {
    return 'ИИ лимити $time чейин түгөндү (касса жана бот менен жалпы). Башка Google аккаунтунан экинчи ачкыч кошсоңуз болот.';
  }

  @override
  String get advisorQuotaLater =>
      'ИИ лимити түгөндү (касса жана бот менен жалпы). Кийинчерээк аракет кылыңыз же башка Google аккаунтунан экинчи ачкыч кошуңуз.';

  @override
  String advisorKeyRejected(String time) {
    return 'Google ачкычты четке какты, $time кийин кайра аракет болот. Ачкычты «ИИ жөндөөсү» → «Gemini ачкычтары» бөлүмүндө текшериңиз.';
  }

  @override
  String get advisorTimeout =>
      'ИИ узак убакыт жооп бербей жатат. Интернетти текшерип, «Кайталоо» баскычын басыңыз.';

  @override
  String gkLastError(String text) {
    return 'Акыркы ката: $text';
  }

  @override
  String get aiAccOwnerNote =>
      'Сиз тизмеде жоксуз, бирок ээсине жана администраторго ИИ дайыма жеткиликтүү.';

  @override
  String get pePriceCheckTitle => 'Бааны текшериңиз';

  @override
  String pePriceUpAsk(String now, String was, String times) {
    return 'Баа $was эмес, $now — сактайсызбы?\nМурдагыдан $times эсе көп.';
  }

  @override
  String pePriceDownAsk(String now, String was, String times) {
    return 'Баа $was эмес, $now — сактайсызбы?\nМурдагыдан $times эсе аз.';
  }

  @override
  String peBelowCostAsk(String now, String cost) {
    return 'Баа $now сатып алуудан $cost төмөн — зыянга сатуу. Сактайсызбы?';
  }

  @override
  String peMarkupAsk(String now, String cost, String pct) {
    return 'Баа $now, сатып алуу $cost: үстөк $pct %. Сандарда ката жокпу? Сактайсызбы?';
  }

  @override
  String get peFixPrice => 'Оңдоо';

  @override
  String get peSaveAsIs => 'Ушундай сактоо';

  @override
  String get klSearching => 'Кассаны Wi-Fi тармагында издеп жатам…';

  @override
  String get klHelpNotFound =>
      'Касса жооп бербей жатат. Текшериңиз: 1) касса иштеп жатабы; 2) кассанын жөндөөлөрүндө «Телефондордон сканерлөөнү кабыл алуу» күйгүзүлгөнбү (демейки боюнча өчүк); 3) телефон кассасы бар компьютер менен бир Wi-Fi тармагындабы, мобилдик интернетте эмес.';

  @override
  String get klHelpDisabled =>
      'Кассада телефондордон кабыл алуу өчүк. Кассанын жөндөөлөрүндө «Телефондордон сканерлөөнү кабыл алуу» күйгүзүп, «Байланышты текшерүү» баскычын басыңыз.';

  @override
  String get klHelpRePair =>
      'Касса бул телефонду таныбай жатат (ал ажыратылган же QR эскирген). «QR кайра скандоо» баскычын басып, кассадагы «Телефонду туташтыруу» терезесиндеги QR кодго камераны багыттаңыз.';

  @override
  String get klHelpBadTime =>
      'Телефондогу жана кассадагы убакыт 10 мүнөттөн ашык айырмаланат. Телефондо датаны жана убакытты автоматтык кылыңыз (кассадагы убакытты да текшериңиз), анан «Байланышты текшерүү» баскычын басыңыз.';

  @override
  String get klRescanQr => 'QR кодду кайра скандоо';

  @override
  String get klScRefused =>
      'Код жөнөтүлгөн жок — адегенде кассага байланышты калыбына келтириңиз';

  @override
  String get aiAccDraftNote =>
      'Тизме али сакталган жок: азыр ИИ бардык кызматкерлерге жеткиликтүү. 2 кишиге чейин белгилеп, «Сактоо» баскычын басыңыз — калгандарда ИИ өчөт. Ээсине жана администраторго ИИ дайыма жеткиликтүү.';

  @override
  String get aiAccAlways =>
      'Ээсине жана администраторго ИИ тизмеге карабай дайыма жеткиликтүү.';

  @override
  String get invTitle => 'Накладнойду жүктөө';

  @override
  String get invSub => 'Жеткирүүчүдөн Excel, CSV же XML';

  @override
  String get invHint =>
      'Жеткирүүчүнүн накладной файлын тандаңыз: Excel (.xlsx), CSV же XML (электрондук накладной, эсеп-фактура). Саптар кирешеге түшөт — санын жана баасын текшерип, адаттагыдай өткөрөсүз.';

  @override
  String get invPick => 'Накладной файлын тандоо';

  @override
  String get invPhotoHint =>
      'PDF жана накладнойдун сүрөтүн «Акылдуу камера» окуйт (ИИ ачкычы керек)';

  @override
  String get invPhotoBtn => 'Сүрөт боюнча накладной';

  @override
  String get invReading => 'Накладной окулууда…';

  @override
  String invFileName(String name) {
    return 'Файл: $name';
  }

  @override
  String get invOtherFile => 'Башка файл';

  @override
  String invDocLine(String num, String date) {
    return '$date күнүндөгү накладной № $num';
  }

  @override
  String invDocNum(String num) {
    return 'Накладной № $num';
  }

  @override
  String invDocDate(String date) {
    return '$date күнүндөгү накладной';
  }

  @override
  String get invDocNone => 'Файлда номери жана датасы табылган жок';

  @override
  String invSupplierFile(String name) {
    return 'Файлда: $name';
  }

  @override
  String invSupplierFileInn(String name, String inn) {
    return 'Файлда: $name, ИНН $inn';
  }

  @override
  String invSupplierInnOnly(String inn) {
    return 'Файлда ИНН: $inn';
  }

  @override
  String get invSupplierNoneInFile => 'Файлда жеткирүүчү көрсөтүлгөн эмес';

  @override
  String get invSupByInn => 'ИНН боюнча табылды';

  @override
  String get invSupByName => 'Аталышы боюнча табылды';

  @override
  String get invSupNotFound =>
      'Мындай жеткирүүчү жок — тизмеден тандаңыз же түзүңүз';

  @override
  String get invSupCreate => 'Жеткирүүчү түзүү';

  @override
  String invSupCreated(String name) {
    return '«$name» жеткирүүчүсү түзүлдү';
  }

  @override
  String invSupFail(String text) {
    return 'Жеткирүүчү түзүлгөн жок: $text';
  }

  @override
  String get invSupOffline =>
      'Байланыш жок: жеткирүүчүлөр тизмеси жүктөлгөн жок';

  @override
  String get invSupKept => 'Кирешенин жеткирүүчүсү мурункусу бойдон калды';

  @override
  String get invDupPosted => 'Бул накладной буга чейин жүктөлүп, өткөрүлгөн';

  @override
  String get invDupPending =>
      'Бул накладной али өткөрүлө элек кирешеге кошулган';

  @override
  String get invDupAnyway => 'Баары бир кошуу';

  @override
  String invTotalLines(String sum) {
    return 'Саптар боюнча: $sum';
  }

  @override
  String invTotalFile(String sum) {
    return 'Файлда: $sum';
  }

  @override
  String get invTotalOk => 'Жыйынтыктар дал келет';

  @override
  String invTotalBad(String diff) {
    return 'Жыйынтыктар дал келбейт: айырма $diff. Санын жана баасын текшериңиз.';
  }

  @override
  String get invTotalNone => 'Файлда жыйынтык табылган жок';

  @override
  String get invTotalVatHint =>
      'НДС менен жыйынтыктар дал келет — «НДС менен сатып алуу» күйгүзүңүз';

  @override
  String get invVat => 'НДС менен сатып алуу';

  @override
  String get invVatSub => 'Адатта сатып алуу НДСсиз алынат';

  @override
  String invCurrency(String cur) {
    return 'Накладнойдун валютасы: $cur. Сиз сом менен иштейсиз — баасын текшериңиз: программа кайра эсептебейт.';
  }

  @override
  String get invGenCodes => 'Штрихкоду жок жаңы товарларга өз кодун берүү';

  @override
  String get invGenCodesSub => 'Болбосо алар штрихкодсуз болот';

  @override
  String invSummary(int n, String stock, String fresh, String bad) {
    return 'Саптар: $n · кампада: $stock · жаңы: $fresh · таанылган жок: $bad';
  }

  @override
  String get invStBarcode => 'Кампада · штрихкод боюнча';

  @override
  String get invStArticle => 'Кампада · артикул боюнча';

  @override
  String get invStName => 'Кампада · аталышы боюнча';

  @override
  String invStFuzzy(String name) {
    return 'Окшош: $name';
  }

  @override
  String get invStCatalog => 'Жаңы · NurCRM маалымдамасында бар';

  @override
  String get invStManual => 'Жаңы · өзүңүз толтуруңуз';

  @override
  String get invStBad => 'Таанылган жок';

  @override
  String get invBadNoName => 'Аталышы, штрихкоду жана артикулу жок';

  @override
  String get invBadNoQty => 'Саны жок';

  @override
  String invUseCandidate(String name) {
    return 'Бул «$name»';
  }

  @override
  String get invAsNew => 'Жаңы товар';

  @override
  String invSrcRow(int n) {
    return 'Файлдын сабы: $n';
  }

  @override
  String invArticle(String code) {
    return 'Артикул: $code';
  }

  @override
  String get invCostMissing => 'Сатып алуу баасын көрсөтүңүз';

  @override
  String get invPriceMissing => 'Сатуу баасын көрсөтүңүз';

  @override
  String get invRemoveRow => 'Сапты алып салуу';

  @override
  String invAdd(int n) {
    return 'Кирешеге кошуу · $n';
  }

  @override
  String get invAddFix => 'Белгиленген саптарды оңдоңуз';

  @override
  String get invAdding => 'Саптар даярдалууда…';

  @override
  String invMerged(int n) {
    return 'Накладнойдогу кайталанган товарлар бириктирилди: $n';
  }

  @override
  String get invErrTooBig =>
      'Файл 10 МБдан чоң — мындай накладнойду ачуу мүмкүн эмес';

  @override
  String get invErrEmpty => 'Файл бош';

  @override
  String get invErrPdf =>
      'Бул PDF же сүрөт. Аларды «Акылдуу камера» окуйт (төмөнкү баскыч)';

  @override
  String get invErrXls =>
      'Эски Excel форматы (.xls) окулбайт — файлды .xlsx же .csv катары сактаңыз';

  @override
  String get invErrBroken => 'Файл бузулган же бул накладной эмес';

  @override
  String get invErrNoTable =>
      'Файлдан товарлар таблицасы табылган жок: «Аталышы», «Саны» жана «Баасы» мамычалары керек';

  @override
  String get invErrUnsafe =>
      'XML DTD же тышкы объекттерди камтыйт — коопсуздук үчүн мындай файл ачылбайт';

  @override
  String get invErrTooMany => 'Саптар өтө көп (5000 ден ашык)';

  @override
  String get invErrFormat =>
      'Колдоого алынбаган файл: .xlsx, .csv же .xml керек';

  @override
  String get tsTitle => 'Тестирлөөчү режими';

  @override
  String tsBuildLine(String version, String build) {
    return 'Версия $version (курулуш $build)';
  }

  @override
  String get tsTestBuild => 'сыноо курулушу';

  @override
  String get tsDevice => 'Түзмөк';

  @override
  String tsDeviceId(String id) {
    return 'Түзмөк ID: $id';
  }

  @override
  String get tsDeviceNameTitle => 'Түзмөктүн аталышы';

  @override
  String get tsDeviceNameHint => 'Мисалы: кассирдин телефону';

  @override
  String get tsRename => 'Түзмөккө ат коюу';

  @override
  String tsScreenLine(String w, String h, String scale, String lang) {
    return 'Экран $w×$h dp, арип ×$scale, тил $lang';
  }

  @override
  String tsSummary(String done, String total, String issues) {
    return 'Текшерилди $done/$total, көйгөй: $issues';
  }

  @override
  String get tsVersionFilter => 'Версия';

  @override
  String tsVersionCurrent(String v) {
    return '$v (азыркы)';
  }

  @override
  String tsAreaProgress(String done, String total) {
    return '$done/$total';
  }

  @override
  String get tsChanged => 'Эмне өзгөрдү';

  @override
  String get tsHowTo => 'Кантип текшерүү';

  @override
  String get tsSevMoney => 'Акча';

  @override
  String get tsSevImportant => 'Маанилүү';

  @override
  String get tsSevNormal => 'Кадимки';

  @override
  String get tsMarkOk => 'Текшерилди';

  @override
  String get tsMarkIssue => 'Көйгөй бар';

  @override
  String get tsNotChecked => 'Текшерилген жок';

  @override
  String tsCheckedAt(String when) {
    return 'Текшерилди $when';
  }

  @override
  String tsIssueAt(String when) {
    return 'Көйгөй $when';
  }

  @override
  String get tsNoComment => 'Комментарийсиз';

  @override
  String get tsUndoHint => 'Белгини алып салуу үчүн дагы бир жолу басыңыз';

  @override
  String get tsCountNone => 'Азырынча эч бир түзмөктө текшерилген жок';

  @override
  String get tsCountLocalOk => 'Текшерилди: 1 түзмөк (бул)';

  @override
  String get tsCountLocalIssue => 'Көйгөй: 1 түзмөк (бул)';

  @override
  String tsCountShared(String ok, String issue) {
    return 'Текшерилген түзмөктөр: $ok · көйгөйлүү түзмөктөр: $issue';
  }

  @override
  String get tsSyncOff => 'Жалпы эсептегич жеткиликсиз (укук же сервер керек)';

  @override
  String get tsRefresh => 'Эсептегичтерди жаңыртуу';

  @override
  String get tsCopyReport => 'Отчётту көчүрүү';

  @override
  String get tsShareReport => 'Отчётту жөнөтүү';

  @override
  String get tsCopied => 'Отчёт көчүрүлдү — билдирүүгө чаптаңыз';

  @override
  String get tsIssueTitle => 'Эмне туура эмес?';

  @override
  String get tsIssueHint => 'Көйгөйдү сүрөттөңүз';

  @override
  String get tsAttachShot => 'Скриншот тиркөө';

  @override
  String get tsShotAttached => 'Скриншот тиркелди';

  @override
  String get tsShotRemove => 'Скриншотту алып салуу';

  @override
  String get tsEditComment => 'Комментарийди өзгөртүү';

  @override
  String get tsEmpty => 'Бул версия үчүн пункттар жок';

  @override
  String get tsCodeTitle => 'Тестирлөөчү коду';

  @override
  String get tsCodeHint => 'Кодду киргизиңиз';

  @override
  String get tsCodeOn =>
      'Тестирлөөчү режими күйгүзүлдү: логотипти 5 жолу басыңыз';

  @override
  String get tsCodeBad => 'Код туура эмес';

  @override
  String tsTapMore(int n) {
    return 'Тестирлөөчү режими: калган басуулар — $n';
  }

  @override
  String get pdNoRights =>
      'Товарларды «Кампа» укугу бар кызматкер же администратор өчүрө алат.';

  @override
  String get pdGone => 'Товар серверде жок — телефондон да алып салдым.';

  @override
  String pdRefused(String msg) {
    return 'Сервер товарды өчүрүүгө уруксат берген жок: $msg. Товар сатууларда же документтерде болсо, өчүрүүнүн ордуна калдыкты списаниеге чыгарыңыз.';
  }

  @override
  String get pdOffline =>
      'Сервер менен байланыш жок — товар өчүрүлгөн жок. Тармак пайда болгондо кайталаңыз.';

  @override
  String pdFailed(String msg) {
    return 'Товар өчүрүлгөн жок: $msg';
  }

  @override
  String pdQueueBlock(int n) {
    return 'Бул товар боюнча жиберилбеген операциялар бар: $n. Жиберилгенин күтүп, кайра аракет кылыңыз.';
  }

  @override
  String get pdAuditFail => 'Өчүрүлгөн жок';

  @override
  String pdAsk(String name) {
    return '«$name» өчүрүлсүнбү? Товар NurCRM каталогунан, кассадан жана мобилкадан жоголот. Муну артка кайтаруу мүмкүн эмес.';
  }

  @override
  String pdStockWarn(String qty, String unit) {
    return 'Кампада $qty $unit бар. Калдык товар менен кошо жоголот. Товар керек болбосо, адегенде калдыкты списаниеге чыгарыңыз.';
  }

  @override
  String get pdTypeName => 'Ырастоо үчүн товардын атын жазыңыз:';

  @override
  String get pdTypeHint => 'Баш/кичине тамга маанилүү эмес';

  @override
  String get ctTitle => 'Акылдуу камера';

  @override
  String get ctAngleFront => 'алдынан';

  @override
  String get ctAngleBack => 'артынан';

  @override
  String get ctAngleSide => 'капталынан';

  @override
  String ctCount(int n, int a) {
    return 'Эталон: $n / $a';
  }

  @override
  String get ctNone =>
      'Үйрөтүлгөн эмес: сүрөт боюнча товар азырынча так таанылбайт';

  @override
  String get ctTrained =>
      'Үйрөтүлгөн: сатууда товар ушул сүрөттөр боюнча таанылат';

  @override
  String get ctTrain => 'Камераны үйрөтүү';

  @override
  String get ctReset => 'Эталондорду өчүрүү';

  @override
  String get ctResetAsk =>
      'Бул товардын бардык эталон сүрөттөрүн телефондон алып салуу керекпи? NurCRM\'деги сүрөттөр калат.';

  @override
  String get ctSaved => 'Эталон сакталды';

  @override
  String get ctRemoved => 'Эталон алынды';

  @override
  String ctShotHint(String angle) {
    return 'Товар толугу менен рамкада, $angle';
  }

  @override
  String get ctBarcodeNone => 'Штрихкод жок — сканерлеңиз';

  @override
  String ctBarcodeOk(String code) {
    return 'Штрихкод: $code';
  }

  @override
  String get ctNote =>
      'Сүрөттөр ушул телефондо сакталат. «Камераны үйрөтүү» — 5 сүрөткө чейин жана штрихкод; ал дагы товардын сүрөтүн NurCRM\'ге кошот.';

  @override
  String ctSlot(int n) {
    return 'Сүрөт $n';
  }

  @override
  String ctSlotAdd(int n, String angle) {
    return 'Сүрөт $n: $angle тартуу';
  }

  @override
  String ctSlotRetake(int n) {
    return 'Сүрөт $n: кайра тартуу';
  }

  @override
  String get ctRemove => 'Алып салуу';

  @override
  String get camPickTitle => 'Окшош — ушул болсо, тандаңыз';

  @override
  String get camFromGallery => 'Галереядан';

  @override
  String get camFromFile => 'Файлдан';

  @override
  String get camInvoiceSrcHint =>
      'Накладнойду сүрөткө тартса, галереядан же файлдан тандаса болот: сүрөт, PDF, Excel, CSV, XML.';

  @override
  String camPageOf(int n, int a) {
    return 'Сүрөт $n / $a';
  }

  @override
  String get camFileNoProcure =>
      'Накладной файлын жүктөө — «Сатып алуу» укугу менен. Накладнойду сүрөткө тартыңыз — саптар ушул жерде чыгат.';

  @override
  String get camFileTooBig =>
      'Файл 8 МБдан чоң — кичирээк тандаңыз же накладнойду сүрөткө тартыңыз.';

  @override
  String get camFileBadType =>
      'Мындай файл жараксыз. Excel (xlsx), CSV, XML, TXT, PDF же сүрөт керек.';

  @override
  String get camFileReadFail => 'Файлды окуу мүмкүн болгон жок.';

  @override
  String recvFoundGlobal(String name) {
    return 'NurCRM справочнигинен табылды: «$name»';
  }

  @override
  String get recvAddProduct => 'Товар кошуу';

  @override
  String recvLooksLike(String name) {
    return 'Сиздин «$name» окшайт';
  }

  @override
  String get recvBindCode => 'Штрихкодду байлоо';

  @override
  String recvTypo(String name, String stored, String code) {
    return '«$name» карточкасындагы штрихкодто ката: $stored жазылган, туурасы $code';
  }

  @override
  String get recvFixCode => 'Оңдоо';

  @override
  String recvBound(String name) {
    return 'Штрихкод «$name» менен байланды';
  }

  @override
  String get recvFixed => 'Штрихкод оңдолду';

  @override
  String get recvNoRight => 'Кошуу, байлоо же оңдоо үчүн «Кампа» укугу керек.';

  @override
  String recvSearching(String code) {
    return '$code серверден жана справочниктен издеп жатам…';
  }

  @override
  String get pkTitle => 'Таңгактар / даана сатуу';

  @override
  String get pkSwitch => 'Таңгактан даана-даана сатуу';

  @override
  String get pkSwitchHint =>
      'Мисалы, сигарет пачкасы же 6 даана суу таңгагы: таңгак менен да, даана менен да сатса болот. Калдык таңгак менен жүрөт.';

  @override
  String get pkName => 'Таңгактын аталышы';

  @override
  String get pkNameHint => 'Пачка, блок, куту';

  @override
  String get pkQty => 'Таңгактагы даана';

  @override
  String pkPackPrice(String price) {
    return 'Таңгактын баасы: $price сом (бул жогорудагы сатуу баасы)';
  }

  @override
  String get pkPiecePrice => 'Даананын баасы';

  @override
  String get pkAuto => 'Авто';

  @override
  String get pkAutoTip => 'Даананын баасын кайра эсептөө: таңгак баасы ÷ даана';

  @override
  String pkMarkup(int n) {
    return 'Даананын үстөмөгү: $n %';
  }

  @override
  String pkBelowCost(String cost) {
    return 'Даана сатып алуудан арзан ($cost сом)';
  }

  @override
  String get pkQtyBad => 'Таңгактагы даана — 2ден кем эмес бүтүн сан';

  @override
  String get pkPieceBad => 'Даананын баасын көрсөтүңүз';

  @override
  String pkMismatch(String qty, String piece, String sum, String pack) {
    return '$qty дана × $piece сом = $sum сом, ал эми таңгак $pack сом — айырма 5 %тен ашык. Ушундай сактайсызбы?';
  }

  @override
  String get pkMismatchTitle => 'Даана менен таңгактын баасы дал келбейт';

  @override
  String get pkWeightBlocked =>
      'Таразалуучу товарды (кг) таңгактан даана сатууга болбойт.';

  @override
  String get pkMulti =>
      'Товарда бир нече таңгак бар — аларды сайттан же кассадан оңдоңуз.';

  @override
  String get pkQuick => 'Тез';

  @override
  String get pkWordPack => 'Пачка';

  @override
  String get pkWordPackage => 'Таңгак';

  @override
  String get pkWordCrate => 'Ящик';

  @override
  String get pkWordBlock => 'Блок';

  @override
  String get pkWordBox => 'Куту';

  @override
  String get pkPcs => 'даана';

  @override
  String get pkChoiceTitle => 'Кантип сатасыз?';

  @override
  String get pkChoicePiece => 'Даана';

  @override
  String pkChoiceStock(String stock) {
    return 'Калдык: $stock';
  }

  @override
  String pkLinePiece(String name) {
    return '«$name» ичинен даана';
  }

  @override
  String get pkQtyInPacks =>
      'Саны таңгакта киргизилет: 12 — бул 12 пачка, даанасы алардан сатылат.';

  @override
  String get pkPackShort => 'таңгак';

  @override
  String get pkNoPackId =>
      'Таңгак серверде азырынча сакталган эмес — адегенде каталогду жаңыртыңыз.';

  @override
  String get camNotThis => 'Бул башка товар';

  @override
  String get camNotThisDone =>
      'Эсте калган ат унутулду — товарды кайра тандаңыз.';

  @override
  String get camAliasesTitle => 'Эсте калган аттар:';

  @override
  String get camAliasForget => 'Атты унутуу';

  @override
  String get invDupPostedNow =>
      'Бул накладной мурда эле өткөрүлгөн (бул телефондо же кассада). Кирешени дагы бир жолу өткөрсөңүз, калдык жана жеткирүүчүгө карыз эки эсе болот.';

  @override
  String get invSupOtherDoc =>
      'Бул кирешеде башка жеткирүүчүнүн накладнойу бар. Адегенде аны өткөрүңүз, андан кийин муну жүктөңүз.';

  @override
  String get invSupConflictTitle => 'Бул кимдин кирешеси?';

  @override
  String invSupConflictBody(String doc, String keep) {
    return 'Накладной: $doc. Кирешеде тандалган: $keep. Киреше жана карыз бир жеткирүүчүгө жазылат — тандаңыз.';
  }

  @override
  String invSupConflictKeep(String name) {
    return 'Калтыруу: $name';
  }

  @override
  String invSupConflictDoc(String name) {
    return 'Накладнойдон: $name';
  }

  @override
  String recvCostAvg(int n) {
    return 'Сатып алуу баасы саны боюнча орточо болуп кайра эсептелди, позиция: $n';
  }

  @override
  String invPackWarn(String unit, String name, String pu) {
    return 'Накладнойда «$unit» — бул таңгак, ал эми «$name» товары «$pu» менен эсептелет. Кандай кабыл алууну тандаңыз: болбосо таңгактар калдыкка таңгак баасы менен даана болуп түшөт.';
  }

  @override
  String get invPackSize => 'Таңгактагы даана';

  @override
  String get invPackToPieces => 'Даанага кайра эсептөө';

  @override
  String get invPackAsIs => 'Өзүндөй кабыл алуу';

  @override
  String get invPackBad =>
      'Таңгакта канча даана экенин жазыңыз: 2ден кем эмес бүтүн сан.';

  @override
  String invPackDonePieces(int n, String qty, String cost) {
    return '1 таңгак = $n даана → $qty даана, $cost сом';
  }

  @override
  String invPackDoneAsIs(String qty, String unit, String cost) {
    return 'Өзүндөй кабыл алынды: калдык $qty $unit көбөйөт, сатып алуу $cost сом / $unit';
  }

  @override
  String get invPackChange => 'Өзгөртүү';

  @override
  String get pieceSaleMode => 'Таңгактан даана сатуу';

  @override
  String get pieceSaleModeSub =>
      'Сервер даананын ордуна бүт пачканы алып салса, өчүрүңүз. Анда мындай товар таңгак менен гана сатылат.';

  @override
  String get pieceSaleTesterWarn =>
      'Сынак: таңгактан даана сатуу сервер тарабынан дагы ырасталган жок. Төлөгөндөн кийин товардын калдыгын текшериңиз.';

  @override
  String pieceStockMismatch(String list) {
    return 'Даана сатылгандан кийин сервердеги калдык дал келген жок: $list. Товардын калдыгын текшерип, администраторго билдириңиз.';
  }

  @override
  String get klScStale => 'Код эскирди — кайра сканерлеңиз';

  @override
  String get klScBusy => 'Төлөм жүрүп жатат — андан кийин сканерлеңиз';

  @override
  String invCurrencyOk(String cur) {
    return 'Баалар $cur менен, ал эми киреше сом менен жүрөт. Өзүндөй кошуу (1 $cur = 1 сом) — сумманы текшердим';
  }

  @override
  String get invGrossOnly =>
      'Файлда ндс менен баалар гана бар. Сатып алуу баасы сабактын ставкасы боюнча ндссиз эсептелди (ставка жок болсо — ндс менен баа алынды). Текшериңиз.';

  @override
  String invUnitCheck(String inv, String item) {
    return 'Текшериңиз · бирдик: накладнойда «$inv», товарда «$item»';
  }

  @override
  String get empAdd => 'Жаңы кызматкер';

  @override
  String get empCard => 'Кызматкердин картасы';

  @override
  String get empSectionData => 'Кызматкердин маалыматы';

  @override
  String get empSectionRights => 'Жеткиликтүүлүк';

  @override
  String get empFirstName => 'Аты';

  @override
  String get empLastName => 'Фамилиясы';

  @override
  String get empMiddleName => 'Атасынын аты';

  @override
  String get empEmail => 'Почта (кирүү логини)';

  @override
  String get empPhone => 'Телефон';

  @override
  String get empTrack => 'Кызматкердин номери';

  @override
  String get empRole => 'Кызматы';

  @override
  String get empBirth => 'Туулган күнү';

  @override
  String get empHire => 'Жумушка алынган күнү';

  @override
  String get empAddress => 'Дареги';

  @override
  String get empComment => 'Комментарий';

  @override
  String get empClearDate => 'Датаны тазалоо';

  @override
  String get empRequired => 'Талааны толтуруңуз';

  @override
  String get empEmailBad => 'Почтаны толук жазыңыз, мисалы aibek@mail.kg';

  @override
  String get empPhoneBad => 'Телефон: 9дан 15ке чейин сан, «+» болушу мүмкүн';

  @override
  String get empSaved => 'Кызматкер сакталды.';

  @override
  String get empCreated => 'Кызматкер кошулду.';

  @override
  String get empNothingChanged => 'Эч нерсе өзгөргөн жок.';

  @override
  String get empSearch => 'Издөө: аты, почта, телефон';

  @override
  String get empFilterActive => 'Иштейт';

  @override
  String get empFilterDismissed => 'Бошотулган';

  @override
  String get empDismissedTag => 'бошотулган';

  @override
  String empCount(int n) {
    return 'Кызматкерлер: $n';
  }

  @override
  String get empNothingFound => 'Эч ким табылган жок.';

  @override
  String get empDismiss => 'Бошотуу';

  @override
  String get empRestore => 'Калыбына келтирүү';

  @override
  String empDismissAsk(String name) {
    return '$name бошотулсунбу? Ал программага кире албайт. Сатуулар жана сменалар сакталат.';
  }

  @override
  String empRestoreAsk(String name) {
    return '$name калыбына келтирилсинби? Ал кайра программага кире алат.';
  }

  @override
  String get empDismissedDone => 'Кызматкер бошотулду.';

  @override
  String get empRestoredDone => 'Кызматкер калыбына келтирилди.';

  @override
  String get empDismissStill =>
      'Сервер өтүнүчтү кабыл алды, бирок кызматкер тизмеде калды. Аны app.nurcrm.kg сайтынан өчүрүңүз.';

  @override
  String get empResetPwd => 'Сырсөздү калыбына келтирүү';

  @override
  String empResetAsk(String name) {
    return '$name сырсөзү калыбына келтирилсинби? Эски сырсөз иштебей калат, жаңысын бир жолу көрсөтөбүз.';
  }

  @override
  String get empPasswordTitle => 'Кызматкердин сырсөзү';

  @override
  String empPasswordNote(String email) {
    return 'Логин: $email. Сырсөз бир гана жолу көрсөтүлөт — азыр кызматкерге бериңиз.';
  }

  @override
  String get empCopy => 'Сырсөздү көчүрүү';

  @override
  String get empCopied => 'Көчүрүлдү.';

  @override
  String get empFeatureMissing => 'Сервер бул аракетти азырынча колдобойт.';

  @override
  String get empListDenied =>
      '«Кызматкерлер» укугу жок — тизме жеткиликсиз. Ээсине кайрылыңыз.';

  @override
  String get empErrBad =>
      'Сервер маалыматты кабыл албады. Талааларды текшериңиз.';

  @override
  String get empErrEmail => 'Бул почта бош эмес же туура эмес жазылган.';

  @override
  String get empErr403 => 'Бул аракетке укук жок. Ээсине кайрылыңыз.';

  @override
  String get empErr404 =>
      'Кызматкер серверде табылган жок — тизмени жаңыртыңыз.';

  @override
  String get empErr409 =>
      'Мындай кызматкер бар же маалымат өзгөрүп калды. Тизмени жаңыртыңыз.';

  @override
  String get empErr429 => 'Өтүнүч өтө көп. Бир мүнөт күтүп, кайталаңыз.';

  @override
  String get empErr5xx =>
      'Сервер катасы. Маалымат өзгөргөн жок — кийинчерээк кайталаңыз.';

  @override
  String get rightUsePos => 'Кассада иштөө';

  @override
  String get rightSalaryView => 'Эмгек акы: көрүү';

  @override
  String get rightSalaryEdit => 'Эмгек акы: төлөө';

  @override
  String get rightEmployeesView => 'Кызматкерлер: көрүү';

  @override
  String get rightEmployeesEdit => 'Кызматкерлер: оңдоо';

  @override
  String get rightsReadOnly =>
      'Жеткиликтүүлүктү көрүүгө гана болот: аны ээси же «Жөндөөлөр» ачык адам оңдойт.';

  @override
  String get unitNamePcs => 'Даана';

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
  String get unitNamePack => 'Таңгак';

  @override
  String get unitNamePair => 'Жуп';

  @override
  String get unitNameSet => 'Комплект';

  @override
  String get unitCustom => 'Башка';

  @override
  String get unitCustomTitle => 'Өз бирдигиңиз';

  @override
  String get unitCustomHint => 'Мисалы: рулон, бөтөлкө';

  @override
  String unitHintFrac(String name) {
    return '$name: бөлчөк сан болот, мисалы 0,5';
  }

  @override
  String unitHintWhole(String name) {
    return '$name: бүтүн сан гана';
  }

  @override
  String get unitHintCustom => 'Өз бирдик: сан бөлчөк болушу мүмкүн';

  @override
  String nuQtyWhole(String unit, String qty) {
    return '«$unit» үчүн сан бүтүн болушу керек (азыр $qty). кг, л, м… тандаңыз же бүтүн сан жазыңыз.';
  }

  @override
  String get nuQtyNegative => 'Саны нөлдөн кичине болбошу керек.';

  @override
  String get nuPriceNeed => 'Сатуу баасын жазыңыз';

  @override
  String get bkHintUnits =>
      'Бирдик: шт, кг, г, л, мл, м, упак, пара, компл («штука», «литр», pcs, kg, l, дана, dona… да түшүнүлөт). Баа жана саны бөлчөк болушу мүмкүн: 12,75; 0,5.';

  @override
  String get bkErrName => 'аталышы жок';

  @override
  String get bkErrPrice => 'сатуу баасын жазыңыз';

  @override
  String bkErrUnit(String unit) {
    return '«$unit» бирдиги таанылган жок — шт, кг, л… тандаңыз';
  }

  @override
  String bkErrQtyWhole(String qty, String unit) {
    return '«$unit» үчүн $qty саны бүтүн болушу керек';
  }

  @override
  String get bkErrQtyNeg => 'саны нөлдөн кичине';

  @override
  String get bkWarnQty => 'саны жазылган эмес — калдык 0';

  @override
  String get bkSetUnit => 'Бирдикти коюу';

  @override
  String get bkSetPrice => 'Баа коюу';

  @override
  String get bkSetQty => 'Санды коюу';

  @override
  String get bkSelectAll => 'Баарын тандоо';

  @override
  String get bkSelectBad => 'Каталуулар';

  @override
  String get bkSelectNone => 'Тандоону алуу';

  @override
  String bkSelectedN(int n) {
    return 'Тандалган сап: $n';
  }

  @override
  String get bkSelectFirst =>
      'Бир нече сапка бирден колдонуу үчүн саптарды белгилеңиз.';

  @override
  String bkApplyTo(int n) {
    return '$n сапка колдонуу';
  }

  @override
  String get bkEditRow => 'Товар сабы';

  @override
  String bkResult(int a, int b) {
    return 'Даяр $a, каталар менен $b';
  }

  @override
  String get barcodeDigitsHint => 'Цифралар гана, 4төн 14кө чейин';

  @override
  String get barcodeBadLen => 'Штрихкод — 4төн 14кө чейин сан, тамгасыз';

  @override
  String get infK => 'миң';

  @override
  String get infM => 'млн';

  @override
  String get infB => 'млрд';

  @override
  String get infNoData => 'Мөөнөттө маалымат жок';

  @override
  String get infNoDataHint => 'Башка мөөнөттү тандаңыз же сатуу жүргүзүңүз';

  @override
  String infUpdatedAt(String time) {
    return 'Жаңыртылды $time';
  }

  @override
  String get infCompare => 'Мурунку мөөнөт менен салыштыруу';

  @override
  String get infPeriodCustom => 'Өз мөөнөтү';

  @override
  String get infVsYesterday => 'кечээге салыштырмалуу';

  @override
  String get infVsPrevWeek => 'өткөн жумага салыштырмалуу';

  @override
  String get infVsPrevMonth => 'өткөн айга салыштырмалуу';

  @override
  String get infVsPrevPeriod => 'мурунку мөөнөткө салыштырмалуу';

  @override
  String infDeltaUpSem(String pct, String vs) {
    return '$pct % жогору, $vs';
  }

  @override
  String infDeltaDownSem(String pct, String vs) {
    return '$pct % төмөн, $vs';
  }

  @override
  String infDeltaSameSem(String vs) {
    return 'Өзгөрүү жок, $vs';
  }

  @override
  String get infDeltaNew => 'жаңы';

  @override
  String get infOther => 'Башка';

  @override
  String get infShare => 'Үлүшү';

  @override
  String infDescEmpty(String name) {
    return '$name: маалымат жок';
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
    return '$name: эң көп $max $unit ($maxLabel), эң аз $min $unit ($minLabel), бардыгы $total $unit';
  }

  @override
  String get infHowCalc => 'Кантип эсептелген';

  @override
  String get infHowRevenue =>
      'Түшүм — мөөнөттөгү «төлөндү» жана «жарым-жартылай кайтарылды» статусундагы чектердин суммасы, компьютердеги программадагыдай. Карызга жана жокко чыгарылган чектер кирбейт.';

  @override
  String get infHowChecks =>
      'Чектер — мөөнөттөгү төлөнгөн чектердин саны (жарым-жартылай кайтарылгандар да).';

  @override
  String get infHowAvg => 'Орточо чек = түшүм ÷ мөөнөттөгү чектер саны.';

  @override
  String get infHowProfit =>
      'Пайда = түшүм − сатылган товарлардын сатып алуу баасы (сервердин жалпы пайдасы). Дүкөндүн чыгымдары кирбейт.';

  @override
  String get infHowShiftCash =>
      'Кассада болушу керек = башталышындагы нак акча + нак сатуулар + карызды нак төлөө + киргизүүлөр − нак кайтаруулар − алуулар. Санды сервер берет.';

  @override
  String get infHowDebts =>
      'Карыздар — кардарлар дүкөнгө азыр милдеттүү болгон сумма (серверден карыздарлардын тизмеси).';

  @override
  String get infHowLow =>
      'Түгөнүп жатат — калдыгы 10дон аз же товар үчүн коюлган минималдуу калдыктан көп эмес товарлар (кызматтар эмес).';

  @override
  String get infHowPayments =>
      'Төлөм ыкмалары — түшүмдүн канчасы накта, картага, которуу жана карызга келди; пайыздар бардык ыкмалардын суммасынан эсептелет.';

  @override
  String get infHowTop =>
      'Топ товарлар — мөөнөттө эң көп түшүм берген товарлар; тилке — эң мыкты товардын үлүшү.';

  @override
  String get scanModeLabel => 'Режим';

  @override
  String get scanModeTitle => 'Сканер режими';

  @override
  String scanModeA11y(String mode) {
    return 'Сканер режими: $mode. Тандоо үчүн басыңыз';
  }

  @override
  String get scanModeSaleSub => 'Скандаңыз — товар түз чекке түшөт';

  @override
  String get scanModeLookSub => 'Штрихкодсуз, сүрөт боюнча товарды таануу';

  @override
  String get scanModeWeighSub => 'Тараза товары: салмагы таразанын экранынан';

  @override
  String get scanModeReceiveSub => 'Товарды кампага кабыл алуу';

  @override
  String get scanModeKassaSub => 'Штрихкоддор Wi-Fi аркылуу кассага кетет';

  @override
  String get infNoDelta => '— жок';

  @override
  String get infNoCompare => '— салыштыруу жок';

  @override
  String get infCompareShort => 'Салыштыруу';

  @override
  String get infLoadFailedTitle => 'Жүктөлгөн жок';

  @override
  String infLoadFailedAt(String time) {
    return 'Сервер менен байланыш жок. $time боюнча маалымат көрсөтүлдү.';
  }

  @override
  String get infLoadFailedNoData => 'Сервер менен байланыш жок.';

  @override
  String get infTapHint => 'Тилкени басыңыз — майда-чүйдөсү';

  @override
  String get infTapRingHint => 'Секторду басыңыз — майда-чүйдөсү';

  @override
  String get infPrevBar => 'Мурунку мөөнөт';

  @override
  String infOfPeriod(String pct) {
    return 'мөөнөттүн $pct %';
  }

  @override
  String get infHoursNote =>
      'Саат боюнча түшүм: сервер саат боюнча бөлүштүрүүнү азырынча бербейт — бөлүм кийин чыгат.';

  @override
  String get infExpensesNote =>
      'Дүкөндүн чыгымдары сервер отчетуна кирбейт — түшүм, өздүк нарк жана пайда көрсөтүлдү.';

  @override
  String get infCost => 'Өздүк нарк';

  @override
  String infMargin(String pct) {
    return 'Рентабельдүүлүк $pct %';
  }

  @override
  String get infRevenueCostProfit => 'Түшүм, өздүк нарк жана пайда';

  @override
  String get infDebtConcTitle => 'Эң көп карыздар';

  @override
  String infDebtConcLine(String pct) {
    return 'Эң чоң үч карызкор — карыздын $pct %';
  }

  @override
  String get infDebtAgeNote =>
      'Карыздын мөөнөтү: сервер карыз күндөрүн жөнөтпөйт — карызкорлор боюнча топтолуу көрсөтүлдү.';

  @override
  String get infStockTitle => 'Кампадагы калдыктар';

  @override
  String get infStockOut => 'Жок';

  @override
  String get infStockLow => 'Түгөнүп баратат';

  @override
  String get infStockOk => 'Жетиштүү';

  @override
  String get infPaidWith => 'Эмне менен төлөндү';

  @override
  String get infTotalWord => 'Бардыгы';

  @override
  String get infShiftNoSales => 'Сменада азырынча сатуу жок';

  @override
  String get infHowTitle => 'Бул кантип эсептелген';

  @override
  String get infRangePick => 'Күндөрдү тандоо';
}
