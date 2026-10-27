// ignore: unused_import
import 'package:intl/intl.dart' as intl;

import 'app_localizations.dart';

// ignore_for_file: type=lint

/// The translations for Uzbek (`uz`).
class AppLocalizationsUz extends AppLocalizations {
  AppLocalizationsUz([String locale = 'uz']) : super(locale);

  @override
  String get som => 'so\'m';

  @override
  String get cancel => 'Bekor qilish';

  @override
  String get done => 'Tayyor';

  @override
  String get save => 'Saqlash';

  @override
  String get retry => 'Qayta urinish';

  @override
  String get all => 'Hammasi';

  @override
  String get loginTagline => 'Kassa va vebdagi kabi kirish';

  @override
  String get loginLabel => 'Login yoki telefon';

  @override
  String get loginHint => 'pochta yoki telefon';

  @override
  String get passwordLabel => 'Parol';

  @override
  String get signIn => 'Kirish';

  @override
  String get loginEmpty => 'Login va parolni kiriting.';

  @override
  String get loginWrong => 'Login yoki parol noto\'g\'ri.';

  @override
  String get chooseCashbox => 'Kassani tanlang';

  @override
  String get cashbox => 'Kassa';

  @override
  String get tabHome => 'Bosh sahifa';

  @override
  String get tabSale => 'Sotuv';

  @override
  String get tabScan => 'Skaner';

  @override
  String get tabChecks => 'Cheklar';

  @override
  String get tabStock => 'Ombor';

  @override
  String get newCheck => 'Yangi chek';

  @override
  String get scanTileSub => 'Shtrix-kod bo\'yicha narx va qoldiq';

  @override
  String get checksTileSub => 'Yuborish, chop etish';

  @override
  String get stockTileSub => 'Qoldiqlar va qabul';

  @override
  String shiftOpenSince(String time) {
    return 'Smena ochiq · $time';
  }

  @override
  String get shiftClosed => 'Smena yopiq';

  @override
  String get mySalesForShift => 'Smenadagi sotuvlarim';

  @override
  String checksCount(int n) {
    return '$n ta chek';
  }

  @override
  String get openShift => 'Smenani ochish';

  @override
  String get closeShift => 'Smenani yopish';

  @override
  String get openingCashHint => 'Boshida kassada qancha pul';

  @override
  String get openShiftFirst => 'Avval bosh sahifada smenani oching.';

  @override
  String get offlineWorking =>
      'Server bilan aloqa yo\'q — sotuvlar telefonda saqlanadi va o\'zi yuboriladi.';

  @override
  String runningOut(int n) {
    return '$n ta mahsulot tugayapti';
  }

  @override
  String get runningOutShort => 'Tugayapti';

  @override
  String get outOfStock => 'Mavjud emas';

  @override
  String get tellAdmin => 'Administratorga xabar bering';

  @override
  String get searchProduct => 'Mahsulot yoki shtrix-kod';

  @override
  String get favorites => 'Saralangan';

  @override
  String get nothingFound => 'Hech narsa topilmadi';

  @override
  String get inCheck => 'Chekda';

  @override
  String positions(int n) {
    return '$n ta pozitsiya';
  }

  @override
  String get swipeToRemove => 'Olib tashlash uchun qatorni chapga suring';

  @override
  String payAmount(String sum) {
    return '$sum so\'m to\'lash';
  }

  @override
  String get orderDiscount => 'Chekka chegirma';

  @override
  String get discountPercentHint => 'Foiz, masalan 10';

  @override
  String get extraService => 'Qo\'shimcha xizmat';

  @override
  String get clearCheck => 'Chekni tozalash';

  @override
  String get price => 'Narx';

  @override
  String weightOf(String name) {
    return 'Og\'irligi: $name';
  }

  @override
  String get kg => 'kilogramm, masalan 0,5';

  @override
  String get lineDiscount => 'Mahsulotga chegirma';

  @override
  String get sumHint => 'So\'mdagi summa';

  @override
  String get discount => 'Chegirma';

  @override
  String barcodeNotFound(String code) {
    return '$code shtrix-kodi katalogda yo\'q.';
  }

  @override
  String get cameraUnavailable =>
      'Kamera mavjud emas. Telefon sozlamalarida kameraga ruxsat bering yoki shtrix-kodni qo\'lda kiriting.';

  @override
  String get scanHint => 'Shtrix-kodga qarating — mahsulot o\'zi qo\'shiladi';

  @override
  String scanReceiveHint(int n) {
    return 'Skanerlandi: $n';
  }

  @override
  String get toCheck => 'Chekka';

  @override
  String get checkPrice => 'Narxni bilish';

  @override
  String get receiving => 'Qabul';

  @override
  String doneCount(int n) {
    return 'Tayyor · $n';
  }

  @override
  String inStock(String qty, String unit) {
    return 'omborda $qty $unit';
  }

  @override
  String get scanContinuous => 'Tasdiqlamasdan ketma-ket skanerlash';

  @override
  String checkN(int n) {
    return 'Chek · $n';
  }

  @override
  String get addToCheck => 'Chekka qo\'shish';

  @override
  String get addedAutomatically => 'Chekka qo\'shildi';

  @override
  String get profileTitle => 'Profil';

  @override
  String get discountInPercent => 'Foiz';

  @override
  String get discountInSom => 'Summa, so\'m';

  @override
  String discountResult(String sum) {
    return 'Chegirma bilan: $sum so\'m';
  }

  @override
  String get clothingMode => 'Sotuvda o\'lcham va ranglar';

  @override
  String get clothingModeSub => 'Kiyim va poyabzal do\'koni uchun';

  @override
  String get chooseSize => 'O\'lcham va rang';

  @override
  String get colorLabel => 'Rang';

  @override
  String get sizeLabel => 'O\'lcham';

  @override
  String toCheckSize(String size) {
    return 'Chekka · $size';
  }

  @override
  String sizeLowStock(String qty) {
    return 'Bu o\'lchamdan $qty dona qoldi. Baribir qo\'shilsinmi?';
  }

  @override
  String get rentals => 'Prokat';

  @override
  String get rentalNew => 'Yangi prokat';

  @override
  String get rentalActive => 'Qo\'lda';

  @override
  String get rentalOverdue => 'Kechikkan';

  @override
  String get rentalReturn => 'Qaytarishni qabul qilish';

  @override
  String get rentalReturned => 'Qaytarish qabul qilindi.';

  @override
  String get rentalCondOk => 'Butun';

  @override
  String get rentalCondDamaged => 'Shikastlangan';

  @override
  String get rentalPenalty => 'Garovdan jarima';

  @override
  String get rentalClient => 'Mijoz';

  @override
  String get rentalItem => 'Buyum';

  @override
  String get rentalUntil => 'Sanagacha';

  @override
  String get rentalPerDay => 'Sutkalik narx';

  @override
  String rentalDays(int n) {
    return '$n sutka';
  }

  @override
  String get rentalDeposit => 'Pul garov';

  @override
  String get rentalDocument => 'Hujjat garov';

  @override
  String get rentalDocumentHint => 'Pasport ID…, FIO';

  @override
  String get rentalCreate => 'Berish va chekka';

  @override
  String rentalCreated(int n) {
    return '№$n prokat rasmiylashtirildi — narxi chekda.';
  }

  @override
  String rentalLine(int n, String days) {
    return 'Prokat №$n · $days sut.';
  }

  @override
  String get chooseClient => 'Mijozni tanlash';

  @override
  String get chooseItem => 'Buyumni tanlang';

  @override
  String get marginPercent => 'Ustama, %';

  @override
  String get marginSum => 'Ustama, so\'m';

  @override
  String get kindProduct => 'Mahsulot';

  @override
  String get kindClothes => 'Kiyim';

  @override
  String get kindService => 'Xizmat';

  @override
  String get sizesLabel => 'O\'lchamlar';

  @override
  String get colorsLabel => 'Ranglar';

  @override
  String get addColor => 'Rang qo\'shish';

  @override
  String get colorName => 'Rang nomi';

  @override
  String get colorSizeGrid => 'Rang × o\'lcham — necha dona';

  @override
  String variantsSummary(int n, String qty) {
    return '$n variant · $qty dona';
  }

  @override
  String variantsSaved(int n) {
    return 'Saqlangan variantlar: $n';
  }

  @override
  String get saveService => 'Xizmatni saqlash';

  @override
  String get findInternet => 'Internetdan topish';

  @override
  String get internetFound => 'NurCRM bazasidan topildi';

  @override
  String get barcodeMatched => 'shtrix-kod mos · aniq';

  @override
  String get takeLabel => 'Olish';

  @override
  String get categoryLabel => 'Kategoriya';

  @override
  String get brandLabel => 'Brend';

  @override
  String get fillProduct => 'Mahsulotni to\'ldirish';

  @override
  String get searchInBrowser => 'Brauzerda qidirish';

  @override
  String get priceNotTaken =>
      'Internetdan narx olinmaydi — o\'zingiznikini kiriting.';

  @override
  String get dayToday => 'Bugun';

  @override
  String get pricesTitle => 'Sotuv narxlari';

  @override
  String pricesSub(int a, int b) {
    return 'xarid o\'zgardi: $a · yangi: $b';
  }

  @override
  String get ruleLabel => 'Qoida';

  @override
  String get roundTo => 'Yaxlitlash';

  @override
  String get applyToAll => 'Barcha qatorlarga';

  @override
  String costWas(String was, String now) {
    return 'xarid $was → $now';
  }

  @override
  String get newItemShort => 'yangi';

  @override
  String get applyPrices => 'Narxlarni qo\'llash';

  @override
  String postSummary(int a, String qty, int b, int n, String sum) {
    return 'Pozitsiya: $a · dona: $qty\nYangi mahsulot: $b\nNarxi o\'zgaradi: $n\nXarid: $sum';
  }

  @override
  String get quotesTitle => 'KP va hisoblar';

  @override
  String get quoteNew => 'Tijorat taklifi';

  @override
  String get invoiceNew => 'To\'lov hisobi';

  @override
  String get quotesWaiting => 'Javob kutilmoqda';

  @override
  String get invoicesUnpaid => 'To\'lanmagan';

  @override
  String get invoicesOverdue => 'Muddati o\'tgan';

  @override
  String get filterAll => 'Hammasi';

  @override
  String get filterQuotes => 'KP';

  @override
  String get filterInvoices => 'Hisoblar';

  @override
  String get stDraft => 'Qoralama';

  @override
  String get stSent => 'Yuborildi';

  @override
  String get stAccepted => 'Qabul → hisob';

  @override
  String get stDeclined => 'Rad etildi';

  @override
  String get stIssued => 'To\'lov kutilmoqda';

  @override
  String get stPartial => 'Qisman to\'langan';

  @override
  String get stPaid => 'To\'langan';

  @override
  String validUntil(String date) {
    return '$date gacha amal qiladi';
  }

  @override
  String payUntil(String date) {
    return '$date gacha to\'lash';
  }

  @override
  String get fromCart => 'Joriy chekdan';

  @override
  String get useWholesale => 'Ulgurji narxlar';

  @override
  String get innLabel => 'INN';

  @override
  String get docSendPdf => 'PDF yuborish';

  @override
  String get invoiceFromQuote => 'KP bo\'yicha hisob';

  @override
  String get takePayment => 'To\'lov qabul qilish';

  @override
  String get sellFromDoc => 'Sotuvni o\'tkazish';

  @override
  String get markDeclined => 'Mijoz rad etdi';

  @override
  String get editDoc => 'O\'zgartirish';

  @override
  String get deleteDoc => 'Hujjatni o\'chirish';

  @override
  String paidOf(String paid, String total) {
    return '$total dan $paid to\'langan';
  }

  @override
  String basedOnQuote(String number) {
    return '$number bo\'yicha';
  }

  @override
  String get quotesLocalNote =>
      'KP va hisoblar kompyuterdagi egasi dasturi va kompaniyaning boshqa telefonlari bilan umumiy. Hisob bo\'yicha sotuv — oddiy NurCRM cheki.';

  @override
  String get addLine => 'Mahsulot qo\'shish';

  @override
  String get docDiscount => 'Chegirma, so\'m';

  @override
  String get esfTitle => 'KKM va ESF';

  @override
  String esfQueue(int n) {
    return 'Navbatda $n chek — aloqa bo\'lganda ketadi';
  }

  @override
  String get esfOnline => 'Kassa aloqada, navbat bo\'sh';

  @override
  String get esfIssue => 'ESF yozish';

  @override
  String get esfBuyerInn => 'Xaridor INN';

  @override
  String get esfBuyerName => 'Xaridor';

  @override
  String get esfPayForm => 'To\'lov shakli';

  @override
  String get esfCash => 'Naqd';

  @override
  String get esfNonCash => 'Naqdsiz';

  @override
  String get esfContract => 'Shartnoma № va sanasi';

  @override
  String get esfSum => 'Summa, so\'m';

  @override
  String get esfDraftSaved => 'ESF qoralamasi saqlandi';

  @override
  String get esfSendTax => 'Soliqqa yuborish';

  @override
  String get esfDrafts => 'ESF qoralamalari';

  @override
  String get esfIncoming => 'Kiruvchi ESF';

  @override
  String get esfStub =>
      'Soliqqa yuborish va kiruvchi ESF NurCRM serveri soliqni ulaganda ishlaydi. Hozircha qoralama tayyorlash mumkin.';

  @override
  String get smartCam => 'Aqlli kamera';

  @override
  String get byLook => 'Ko\'rinishi bo\'yicha';

  @override
  String get byLookHint =>
      'Shtrix-kodsiz mahsulotni suratga oling — SI uni taniydi va katalogdan topadi';

  @override
  String get recognizing => 'Tanilmoqda…';

  @override
  String get recognizedNone =>
      'Tanilmadi — yaqinroq va yorug\'da suratga oling';

  @override
  String get seenOnPhoto => 'Suratda';

  @override
  String get notInCatalog => 'katalogda yo\'q';

  @override
  String get aiKeyNeeded =>
      'Aqlli kamera uchun SI kaliti (Gemini) kerak — kassadagi Nur Maslahatchi bilan bir xil. Uni shu yerga qo\'yish mumkin.';

  @override
  String get aiKeyLabel => 'Gemini kaliti';

  @override
  String get smartShelf => 'Javon';

  @override
  String get smartInvoice => 'Yuk xati';

  @override
  String get smartLabels => 'Yorliqlar';

  @override
  String get smartHint =>
      'Telefonni tekis tuting — kamera mahsulot va narxlarni qidiradi';

  @override
  String get takePhoto => 'Suratga olish';

  @override
  String get morePhoto => 'Yana surat';

  @override
  String seenInBase(int n) {
    return 'Bazada bor · $n';
  }

  @override
  String seenNew(int n) {
    return 'Yangi · $n';
  }

  @override
  String smartNext(int n) {
    return 'Keyingi — qabulga · $n';
  }

  @override
  String get bulkImport => 'Ommaviy yuklash';

  @override
  String get bulkPickFile => 'Excel yoki CSV faylni tanlash';

  @override
  String get bulkPaste => 'Excel\'dan nusxalanganini qo\'yish';

  @override
  String get bulkTemplate => 'Fayl namunasi';

  @override
  String get bulkHint =>
      'Ustunlar: Nomi · Shtrix-kod · Sotuv narxi · Xarid · Miqdor · Birlik · Kategoriya. Birinchi qator — sarlavhalar. Bir xil shtrix-kodli mahsulot takrorlanmaydi: miqdor qo\'shiladi, narxlar yangilanadi.';

  @override
  String bulkSummary(int n, int a, int b) {
    return 'Qator: $n · yangi $a · bor $b';
  }

  @override
  String get bulkBad => 'o\'tkazildi: nomi yoki narxi yo\'q';

  @override
  String get bulkExists => 'bor — qo\'shiladi';

  @override
  String bulkUpload(int n) {
    return '$n ta yuklash';
  }

  @override
  String bulkDone(int n) {
    return 'Yuklandi: $n';
  }

  @override
  String get bulkNothing => 'Faylda mahsulot qatorlari topilmadi';

  @override
  String get notFoundTitle => 'Mahsulot katalogda yo\'q';

  @override
  String notFoundAsk(String code) {
    return 'Shtrix-kod $code. Shu shtrix-kod bilan yangi mahsulot qo\'shilsinmi?';
  }

  @override
  String get addNewProduct => 'Mahsulot qo\'shish';

  @override
  String get supplierLabel => 'Yetkazib beruvchi';

  @override
  String get supplierDiscount => 'Yetkazib beruvchi chegirmasi, %';

  @override
  String costWithDiscount(String sum) {
    return 'Chegirmali xarid: $sum';
  }

  @override
  String get supplierReceiptNote =>
      'Miqdor yetkazib beruvchidan kirim bo\'lib keladi — NurCRM\'da kimdan va qanchaga olingani ko\'rinadi.';

  @override
  String get retailPrice => 'Chakana narx';

  @override
  String get altBarcodesLabel => 'Qo\'shimcha shtrix-kodlar';

  @override
  String get altBarcodesHint =>
      'Shu mahsulot yoki qadoqning boshqa shtrix-kodi: qutida nechta borligini yozing — quti skanerlansa hammasi qo\'shiladi.';

  @override
  String get altBarcodeAdd => 'Shtrix-kod qo\'shish';

  @override
  String get packQty => 'Dona';

  @override
  String get packName => 'Qadoq';

  @override
  String get photosLabel => 'Mahsulot rasmlari';

  @override
  String get photoCamera => 'Kamera';

  @override
  String get photoGallery => 'Galereya';

  @override
  String get photoInternet => 'Internetdan';

  @override
  String get photosSearching => 'Rasm qidirilmoqda…';

  @override
  String get photoFirstMain => 'Birinchi rasm — asosiy';

  @override
  String get descriptionLabel => 'Tavsif — sayt va bot uchun';

  @override
  String get descriptionFind => 'Internetdan tavsif topish';

  @override
  String get descriptionWriting => 'Tavsif yozilmoqda…';

  @override
  String get lowStockPull => '↑ Yuqoriga torting — nima tugayapti';

  @override
  String lowStockTitle(int n) {
    return 'Tugayapti · $n';
  }

  @override
  String get lowStockEmpty => 'Hammasi yetarli';

  @override
  String get summaryTitle => 'Xulosa';

  @override
  String get periodToday => 'Bugun';

  @override
  String get periodWeek => 'Hafta';

  @override
  String get periodMonth => 'Oy';

  @override
  String get kpiRevenue => 'Tushum';

  @override
  String get kpiChecks => 'Cheklar';

  @override
  String get kpiAvg => 'O\'rtacha chek';

  @override
  String get kpiProfit => 'Foyda';

  @override
  String get revenueByDay => 'Kunlar bo\'yicha tushum';

  @override
  String get paymentMethods => 'To\'lov usullari';

  @override
  String get chartBestDay => 'Eng yaxshi kun';

  @override
  String get chartWorstDay => 'Eng sust kun';

  @override
  String get topProducts => 'Eng yaxshi mahsulotlar';

  @override
  String get noSalesPeriod => 'Bu davrda sotuv yo\'q';

  @override
  String get abcTitle => 'ABC tahlil';

  @override
  String get abcByRevenue => 'Tushum bo\'yicha';

  @override
  String get abcByProfit => 'Foyda bo\'yicha';

  @override
  String get abcByQty => 'Dona bo\'yicha';

  @override
  String get abcA => 'Do\'konni boqadi';

  @override
  String get abcB => 'O\'rta';

  @override
  String get abcC => 'Dum';

  @override
  String abcGroupNote(int n, String share) {
    return '$n ta · $share %';
  }

  @override
  String get paretoTitle => 'Pareto diagrammasi';

  @override
  String get paretoHint =>
      'Ustunlar — mahsulot ulushi, chiziq — yig\'ma ulush. 80 % belgisidan chapdagilar — A guruhi.';

  @override
  String get advisorTitle => 'Nur Maslahatchi';

  @override
  String get advisorOnline => 'Do\'koningiz ma\'lumotlari asosida javob beradi';

  @override
  String get advisorAsk => 'Do\'koningiz haqida so\'rang…';

  @override
  String get advisorThinking => 'Maslahatchi o\'ylamoqda…';

  @override
  String get advisorQ1 => 'Kecha qancha sotdik?';

  @override
  String get advisorQ2 => 'Kim qarzdor?';

  @override
  String get advisorQ3 => 'Nima buyurtma qilish kerak?';

  @override
  String get advisorQ4 => 'Hafta qanday o\'tdi?';

  @override
  String get advisorNew => 'Yangi suhbat';

  @override
  String get advisorNoKey =>
      'SI kaliti yo\'q. Uni shu telefonda «SI sozlamalari» → «Gemini kalitlari» bo\'limida qo\'shing (yoki egasi dasturida: Nur Maslahatchi → ⚙ → SI modellari va kalitlari)';

  @override
  String get advisorStageData => 'Do\'kon ma\'lumotlari yig\'ilmoqda…';

  @override
  String get advisorStageAsk => 'SIdan so\'ralmoqda…';

  @override
  String advisorError(String error) {
    return 'Maslahatchi javob bermadi: $error';
  }

  @override
  String get callTitle => 'Suhbat';

  @override
  String get callConnecting => 'Ulanmoqda…';

  @override
  String get callPreparing => 'Do\'kon ma\'lumotlari tayyorlanmoqda…';

  @override
  String get callListening => 'Tinglayapman…';

  @override
  String get callThinking => 'O\'ylayapman…';

  @override
  String get callSpeaking => 'Gapiryapman… (to\'xtatish uchun bosing)';

  @override
  String get callEnded => 'Qo\'ng\'iroq tugadi';

  @override
  String get callEnd => 'Tugatish';

  @override
  String get callMicOff => 'Mikrofon o\'chiq';

  @override
  String get callMicOn => 'Mikrofon yoqiq';

  @override
  String get callSpeakerOn => 'Baland ovoz';

  @override
  String get callSpeakerOff => 'Quloqchin';

  @override
  String callCost(int n) {
    return 'Qo\'ng\'iroq $n daqiqagacha davom etadi va SI kaliti limitini chatdan tezroq sarflaydi.';
  }

  @override
  String get callLimitSoon => 'Bir daqiqadan so\'ng qo\'ng\'iroq tugaydi';

  @override
  String callEndedLimit(int n) {
    return 'Qo\'ng\'iroq tugadi: $n daqiqa o\'tdi. Yana qo\'ng\'iroq qilish mumkin.';
  }

  @override
  String get callEndedFocus =>
      'Qo\'ng\'iroq uzildi: kiruvchi qo\'ng\'iroq yoki ilova yig\'ildi.';

  @override
  String callFailed(String error) {
    return 'Qo\'ng\'iroq amalga oshmadi: $error';
  }

  @override
  String get callNoMic =>
      'Mikrofonga ruxsat yo\'q. Telefon sozlamalarida NurMarket uchun mikrofonga ruxsat bering.';

  @override
  String get callOpenSettings => 'Sozlamalarni ochish';

  @override
  String get callUnsupported =>
      'Ovozli qo\'ng\'iroq Android telefonida ishlaydi.';

  @override
  String get callTableReady =>
      'Raqamli jadval qo\'ng\'iroqdan keyin chatda bo\'ladi';

  @override
  String get callClose => 'Yopish';

  @override
  String get callYou => 'Siz';

  @override
  String checkTab(int n) {
    return 'Chek · $n';
  }

  @override
  String get deleteProduct => 'Mahsulotni o\'chirish';

  @override
  String deleteProductAsk(String name) {
    return '«$name» o\'chirilsinmi? Sotuvlar va hisobotlar qoladi.';
  }

  @override
  String get productDeleted => 'Mahsulot o\'chirildi';

  @override
  String smartUpload(int n) {
    return '$n ta mahsulotni omborga yuklash';
  }

  @override
  String get smartToReceive => 'Yetkazib beruvchi bilan qabulga';

  @override
  String smartShots(int n) {
    return 'Suratlar: $n';
  }

  @override
  String get smartPriceRule => 'Yangilar narxi: xarid +30 %, 5 gacha';

  @override
  String smartUploaded(int n) {
    return 'Omborga yuklandi: $n';
  }

  @override
  String get sellingPrice => 'Sotuv narxi';

  @override
  String get photoLabel => 'Rasm';

  @override
  String get shiftDetails => 'Smena hisoboti';

  @override
  String get shOpened => 'Ochilgan';

  @override
  String get shClosed => 'Yopilgan';

  @override
  String get shOpeningCash => 'Boshida kassada';

  @override
  String get shNonCash => 'Naqd emas';

  @override
  String get shDebt => 'Qarzga';

  @override
  String get shReturns => 'Qaytarishlar';

  @override
  String get shIncome => 'Boshqa kirim';

  @override
  String get shExpected => 'Kassada bo\'lishi kerak';

  @override
  String get shCounted => 'Sanalgan';

  @override
  String get shDifference => 'Farq';

  @override
  String orderReadyNotify(String name, String num, String sum) {
    return 'Assalomu alaykum, $name! №$num buyurtmangiz tayyor. To\'lov $sum so\'m. Sizni kutamiz!';
  }

  @override
  String get customSize => 'Boshqa o\'lcham';

  @override
  String get pickSupplier => 'Yetkazib beruvchini tanlash';

  @override
  String get addProductsHint => 'Mahsulotni toping va qo\'shish uchun bosing';

  @override
  String get removeAction => 'Olib tashlash';

  @override
  String get esfExportCsv => 'Excel\'ga yuklash (CSV)';

  @override
  String get esfExportAll => 'Barcha qoralamalar Excel\'ga (CSV)';

  @override
  String get esfTaxSoon =>
      'Soliqqa yuborish NurCRM serveri ESFni ulaganda ishlaydi. Hozircha Excel\'ga yuklab, soliq saytiga yuklang.';

  @override
  String get dateLabel => 'Sana';

  @override
  String get supplierNoLow =>
      'Bu yetkazib beruvchining tugayotgan mahsuloti yo\'q';

  @override
  String get supplierLowAdd =>
      'Yetkazib beruvchining tugayotgan mahsulotlarini qo\'shish';

  @override
  String get inStockOnly => 'Mavjud';

  @override
  String get allCategories => 'Barcha toifalar';

  @override
  String get catalogPick => 'Katalogdan';

  @override
  String get themeAuto => 'Vaqt bo\'yicha';

  @override
  String get darkFrom => 'Qorong\'i boshlanishi';

  @override
  String get darkTo => 'gacha';

  @override
  String get leaveAsk => 'Rostdan chiqmoqchimisiz?';

  @override
  String get leaveAskSub => 'Saqlanmagan o\'zgarishlar yo\'qoladi.';

  @override
  String get leaveYes => 'Ha, albatta';

  @override
  String get leaveNo => 'Yo\'q, tasodifan bosdim';

  @override
  String get aiSettings => 'SI sozlamalari';

  @override
  String get aiSettingsHint =>
      'Nur Maslahatchi Gemini orqali javob beradi. Gemini limiti tugasa — Groq, keyin OpenRouter. Kalitlar bepul; NurCRMda saqlanadi.';

  @override
  String get aiConnected => 'ulangan';

  @override
  String get aiNoKeyShort => 'kalit yo\'q';

  @override
  String get aiGetKey => 'Kalit olish';

  @override
  String get aiTest => 'Tekshirish';

  @override
  String get aiTestOk => 'Ishlayapti ✓';

  @override
  String get aiKeysSaved => 'Kalitlar saqlandi';

  @override
  String get aiPasteKey => 'Yangi kalitni qo\'ying';

  @override
  String get aiRemoveKey => 'Kalitni olib tashlash';

  @override
  String get payQrTitle => 'To\'lov QR';

  @override
  String get payQrHint =>
      'Banklaringiz QR kodlarini qo\'shing. Xaridor chek summasi bilan QRni skanerlab to\'laydi. Qaysi kassirga qaysi QR ko\'rinishini tanlang — 5 tagacha bank.';

  @override
  String get payQrAdd => 'QR qo\'shish';

  @override
  String get payQrBank => 'Bank';

  @override
  String get payQrCode => 'Bank QR';

  @override
  String get payQrScan => 'Skanerlash';

  @override
  String get payQrGallery => 'Galereyadan (skrinshot)';

  @override
  String get payQrPaste => 'Havolani qo\'yish';

  @override
  String get payQrDynamic => 'Chek summasi QRda';

  @override
  String get payQrDynamicSub =>
      'Xaridor summani kiritmaydi (ELQR). Bank qabul qilmasa — o\'chiring.';

  @override
  String get payQrStaticOnly => 'Bu QR ELQR emas — summasiz ko\'rsatiladi';

  @override
  String get payQrCashiers => 'Kimga ko\'rsatish';

  @override
  String get payQrAllCashiers => 'Barcha kassirlarga';

  @override
  String get payQrNotRecognized => 'QR aniqlanmadi';

  @override
  String get payQrShowCustomer =>
      'Xaridorga ko\'rsating. To\'lovdan keyin «To\'lash»ni bosing.';

  @override
  String get payQrNone =>
      'To\'lov QR yo\'q — egasi «Boshqaruv → To\'lov QR»da qo\'shadi.';

  @override
  String get payQrNoneOwner => 'Hozircha QR yo\'q. «QR qo\'shish»ni bosing.';

  @override
  String get payQrSaved => 'QR saqlandi';

  @override
  String get payQrDelete => 'QRni o\'chirish';

  @override
  String get grpAi => 'SI va tahlil';

  @override
  String get grpStock => 'Mahsulotlar va ombor';

  @override
  String get grpMoney => 'Savdo va pul';

  @override
  String get grpClients => 'Mijozlar va hujjatlar';

  @override
  String get grpTeam => 'Xodimlar';

  @override
  String get findSection => 'Bo\'limni topish';

  @override
  String get liveAuto => 'Avto';

  @override
  String get liveHintSale => 'Mahsulotga qarating va «Skanerlash»ni bosing';

  @override
  String get liveHintShelf => 'Javonga qarating';

  @override
  String get liveHintInvoice => 'Nakladnoyni to\'liq ramkaga oling';

  @override
  String get liveHintLabels => 'Narx yorliqlariga qarating';

  @override
  String get auditTitle => 'Audit jurnali';

  @override
  String get auditSub => 'Bu telefondagi barcha amallar';

  @override
  String get auditSearch => 'Xodim, mahsulot, chek raqami';

  @override
  String get auditKind => 'Amal';

  @override
  String get auditWhat => 'Nima qilindi';

  @override
  String get auditSale => 'Sotuv';

  @override
  String get auditCart => 'Chekka';

  @override
  String get auditReturn => 'Qaytarish';

  @override
  String get auditPrice => 'Narx va qoldiq';

  @override
  String get auditProduct => 'Mahsulotlar';

  @override
  String get auditSettings => 'Sozlamalar';

  @override
  String get auditLogin => 'Kirish/chiqish';

  @override
  String get auditOpen => 'Ekran ochdi';

  @override
  String get teachCamera => 'Aqlli kamerani o\'rgatish: 3–5 rasm + shtrixkod';

  @override
  String get teachRight => '3 dan 1 · o\'ngdan';

  @override
  String get teachLeft => '3 dan 2 · chapdan';

  @override
  String get teachTop => '3 dan 3 · yuqoridan';

  @override
  String get teachHint => 'Mahsulot to\'liq ramkada';

  @override
  String get teachDone => 'Kamera o\'rgatildi';

  @override
  String teachShots(int n) {
    return 'Aqlli kamera uchun: $n rasm';
  }

  @override
  String get scanShoot => 'Skanerlash';

  @override
  String get scanBusy => 'Skanerlanmoqda…';

  @override
  String get scanNext => 'Keyingi mahsulot — yana «Skanerlash»';

  @override
  String get scanChecking => 'O\'rgatilgan mahsulotlar bilan solishtirilmoqda…';

  @override
  String scanTook(String sec) {
    return '$sec soniyada';
  }

  @override
  String teachPhotoOf(int a, int b) {
    return '$b ta rasmdan $a';
  }

  @override
  String get teachAngleRight => 'o\'ngdan';

  @override
  String get teachAngleLeft => 'chapdan';

  @override
  String get teachAngleTop => 'yuqoridan';

  @override
  String get teachAngleFront => 'oldidan yorliq';

  @override
  String get teachAngleBack => 'orqasidan';

  @override
  String get teachCanFinish => '«Tayyor»ni bosing yoki yana bir tomondan oling';

  @override
  String get teachRetake => 'Qayta olish';

  @override
  String get weighAim =>
      'Mahsulotni taroziga qo\'ying, tabloga qarating va «Skanerlash»ni bosing';

  @override
  String get weighAimDisplay =>
      'Tarozi tablosi ko\'rinmayapti — og\'irlik oynasiga qarating, chiroqni o\'chiring va «Skanerlash»ni bosing';

  @override
  String get aiErrNoNet =>
      'Internet yo\'q — aloqani tekshirib, yana «Skanerlash»ni bosing';

  @override
  String get aiErrSlow =>
      'Sun\'iy intellekt o\'z vaqtida javob bermadi (aloqa sekin) — qayta urinib ko\'ring';

  @override
  String get aiErrLimit =>
      'Sun\'iy intellektning bepul limiti tugadi — bir daqiqadan keyin urinib ko\'ring';

  @override
  String get aiErrBusy =>
      'Sun\'iy intellekt hozir band — bir daqiqadan keyin urinib ko\'ring';

  @override
  String get aiErrOther => 'Sun\'iy intellekt suratni taniy olmadi';

  @override
  String addAgain(String name) {
    return '«$name» chekda bor. Yana qo\'shilsinmi?';
  }

  @override
  String get yesAdd => 'Qo\'shish';

  @override
  String get shelfAddAsk =>
      'Javon surati — javonda turgan mahsulot. Bu miqdorlar qoldiqqa yetkazib berish sifatida qo\'shilsinmi?';

  @override
  String isItProduct(String name) {
    return 'Bu «$name»mi?';
  }

  @override
  String notFoundOffline(String code) {
    return '$code kodi telefonda yo\'q, server javob bermayapti. Aloqani tekshiring — mahsulot kompyuterda allaqachon bo\'lishi mumkin, ikkinchisini yaratmang.';
  }

  @override
  String get scaleLabelsTitle => 'Tarozi yorliqlari';

  @override
  String get scaleLabelsHint =>
      'Tarozi yorlig\'i shtrix-kodi prefiks bilan boshlanadi (20–29). Qaysi prefikslar SUMMA, qaysilari OG\'IRLIK ekanini ko\'rsating. Kompaniyaning barcha telefonlari uchun.';

  @override
  String get scaleAmountPrefixes => '«Summa» prefikslari';

  @override
  String get scaleWeightPrefixes => '«Og\'irlik» prefikslari';

  @override
  String get cameraShotFailed =>
      'Kamera suratga olmadi — yana «Skanerlash»ni bosing';

  @override
  String get saved => 'Saqlandi';

  @override
  String get exactMatch => 'aniq';

  @override
  String get noAccess => 'Ruxsat yo\'q';

  @override
  String get noAccessHint =>
      'Bu bo\'lim yoki amal hisobingiz uchun yopiq. Egasi ochadi: NurCRM → Xodimlar → ruxsatlar.';

  @override
  String get clientOptional => 'Mijoz — bonus va xaridlar tarixi uchun';

  @override
  String get bonusRedeem => 'Bonus bilan to\'lash';

  @override
  String get bonusNone => 'Ishlatmaslik';

  @override
  String bonusAll(String sum) {
    return 'Maksimum · $sum';
  }

  @override
  String bonusEarn(String sum) {
    return 'Beriladigan bonus: $sum';
  }

  @override
  String bonusPaid(String sum) {
    return 'Bonus bilan: −$sum so\'m';
  }

  @override
  String get removeClient => 'Mijozni olib tashlash';

  @override
  String get discountRemove => 'Chegirmani olib tashlash';

  @override
  String get addedToCheck => 'Chekka qo\'shildi';

  @override
  String get payment => 'To\'lov';

  @override
  String toPayPositions(int n) {
    return 'To\'lovga · $n ta pozitsiya';
  }

  @override
  String get payMethod => 'To\'lov usuli';

  @override
  String get cash => 'Naqd';

  @override
  String get card => 'Karta';

  @override
  String get debt => 'Qarzga';

  @override
  String get mixed => 'Aralash';

  @override
  String get client => 'Mijoz';

  @override
  String get received => 'Olindi';

  @override
  String get receivedNowDebt => 'Hozir olindi (0 — hammasi qarz)';

  @override
  String get exact => 'Qaytimsiz';

  @override
  String get change => 'Qaytim';

  @override
  String notEnough(String sum) {
    return '$sum so\'m yetmaydi';
  }

  @override
  String get receiptToCustomer => 'Xaridorga chek';

  @override
  String get notNeeded => 'Kerak emas';

  @override
  String get doPay => 'To\'lovni o\'tkazish';

  @override
  String get check => 'Chek';

  @override
  String get total => 'Jami';

  @override
  String get paidOk => 'To\'lov o\'tdi';

  @override
  String get savedOffline => 'Chek telefonda saqlandi';

  @override
  String get savedOfflineHint =>
      'Aloqa yo\'q — chek serverga o\'zi ketadi, ikki marta o\'tmaydi.';

  @override
  String get receiptQr => 'QR-kod';

  @override
  String get showQr => 'Xaridorga QR ko\'rsatish';

  @override
  String get qrPointCamera => 'Xaridor telefon kamerasini qaratadi';

  @override
  String get qrDownloadHint =>
      'Xaridor kamerasi chekni darhol PDF fayl qilib yuklab oladi';

  @override
  String get qrTextHint =>
      'Server chekka havola bermaguncha kamera uni matn sifatida ko\'rsatadi. PDF faylni mijozga — pastdagi tugma bilan.';

  @override
  String get cashInOut => 'Kiritish / olish';

  @override
  String get cashIn => 'Kiritish';

  @override
  String get cashOut => 'Olish';

  @override
  String get amount => 'Summa';

  @override
  String get reason => 'Sabab';

  @override
  String get reasonChange => 'Mayda pul';

  @override
  String get reasonCollection => 'Inkassatsiya';

  @override
  String get reasonExpense => 'Xarajatlar';

  @override
  String get reasonOther => 'Boshqa';

  @override
  String get cashOpDone => 'Smenaga yozildi';

  @override
  String get debts => 'Mijozlar qarzlari';

  @override
  String get debtorsTotal => 'Jami qarzlar';

  @override
  String get payDebt => 'Qarz to\'lovini qabul qilish';

  @override
  String get payDebtDone => 'Qarz to\'lovi o\'tkazildi';

  @override
  String get noDebts => 'Qarzdorlar yo\'q';

  @override
  String get heldChecks => 'Kechiktirilgan cheklar';

  @override
  String get holdCheck => 'Chekni kechiktirish';

  @override
  String get holdNote => 'Kim uchun yoki eslatma';

  @override
  String get heldEmpty => 'Kechiktirilgan cheklar yo\'q';

  @override
  String get restoreCheck => 'Chekka qaytarish';

  @override
  String get delete => 'O\'chirish';

  @override
  String get checkBusy => 'Chekda tovar bor — avval kechiktiring yoki to\'lang';

  @override
  String get returnTitle => 'Chek bo\'yicha qaytarish';

  @override
  String get returnAll => 'Butun chekni qaytarish';

  @override
  String get returnSelected => 'Tanlanganini qaytarish';

  @override
  String get returnDone => 'Qaytarish o\'tkazildi';

  @override
  String get returnToCustomer => 'Xaridorga qaytarish';

  @override
  String get showReceipt => 'Chekni ko\'rish';

  @override
  String get sendPdf => 'PDF yuborish';

  @override
  String get sendReceipt => 'Chekni yuborish';

  @override
  String get newClient => 'Yangi mijoz';

  @override
  String get clientName => 'Ism';

  @override
  String get phone => 'Telefon';

  @override
  String get clientSearch => 'Ism yoki telefon';

  @override
  String get myChecks => 'Mening cheklarim';

  @override
  String get thisShift => 'Shu smena';

  @override
  String unsentN(int n) {
    return 'Yuborilmagan · $n';
  }

  @override
  String get checkSearch => 'Chek raqami yoki mahsulot';

  @override
  String get noChecks => 'Hozircha cheklar yo\'q';

  @override
  String get checkNotSent => 'Chek yuborilmagan';

  @override
  String get notAccepted => 'qabul qilinmadi';

  @override
  String get waitsForNetwork => 'aloqa kutmoqda';

  @override
  String get returned => 'qaytarildi';

  @override
  String get offlineCheckHint =>
      'Aloqa yo\'q — chek saqlandi va serverga o\'zi ketadi';

  @override
  String get sendToCustomer => 'Xaridorga yuborish';

  @override
  String get sendAgain => 'Qayta yuborish';

  @override
  String get returnByAdmin => 'Qaytarish — administrator ruxsati bilan';

  @override
  String get receiveByScanner => 'Skaner bilan qabul';

  @override
  String get audit => 'Inventarizatsiya';

  @override
  String get findProduct => 'Mahsulot topish';

  @override
  String get pricesByAdmin =>
      'Narxlar va yangi mahsulotlarni administrator o\'zgartiradi';

  @override
  String get receiveNoSupplier => 'Yetkazib beruvchisiz va narxsiz';

  @override
  String get scanProduct => 'Mahsulotni skanerlash';

  @override
  String get eachScanPlusOne => 'Har bir skan — +1 dona';

  @override
  String get accepted => 'Qabul qilindi';

  @override
  String goodsPieces(int n, String qty) {
    return '$n ta mahsulot · $qty dona';
  }

  @override
  String wasPieces(String qty) {
    return 'edi $qty';
  }

  @override
  String get unknownBarcode => 'Noma\'lum shtrix-kod';

  @override
  String unknownBarcodeHint(String code) {
    return '$code · katalogda ham, NurCRM ma\'lumotnomasida ham yo\'q. Mahsulotni qo\'shing — kod hech qayerga yuborilmaydi';
  }

  @override
  String get postpone => 'Keyinga qoldirish';

  @override
  String postPieces(String qty) {
    return 'O\'tkazish · $qty dona';
  }

  @override
  String get receivedOk => 'Qabul o\'tkazildi.';

  @override
  String get receivedQueued => 'Qabul saqlandi — aloqa bo\'lganda yuboriladi.';

  @override
  String get allStock => 'Butun ombor';

  @override
  String get chooseWhatToCount => 'Nimani sanash';

  @override
  String checkedOf(int a, int b) {
    return '$b tadan $a tasi tekshirildi';
  }

  @override
  String get notChecked => 'Tekshirilmagan';

  @override
  String get differences => 'Farqlar';

  @override
  String byProgram(String qty, String unit) {
    return 'dastur bo\'yicha $qty $unit';
  }

  @override
  String get matches => 'mos keldi';

  @override
  String shortBy(String qty) {
    return '$qty yetmaydi';
  }

  @override
  String extraBy(String qty) {
    return 'ortiqcha $qty';
  }

  @override
  String get auditAdminApplies =>
      'Qoldiqlarni administrator tekshiruvdan keyin o\'zgartiradi';

  @override
  String get sendToAdmin => 'Administratorga yuborish';

  @override
  String get auditSent => 'Inventarizatsiya administratorga yuborildi.';

  @override
  String get auditQueued =>
      'Inventarizatsiya saqlandi — aloqa bo\'lganda yuboriladi.';

  @override
  String sinceChecks(String time, int n) {
    return '$time dan · $n ta chek';
  }

  @override
  String get revenue => 'Tushum';

  @override
  String get shouldBeInDrawer => 'Kassada bo\'lishi kerak';

  @override
  String get recountCash => 'Naqd pulni qayta sanang';

  @override
  String get howMuchInDrawer => 'Kassada qancha pul';

  @override
  String cashDiff(String sum) {
    return 'Dastur bilan farq: $sum so\'m';
  }

  @override
  String get cashDiffHint =>
      'Summa mos kelmasa, administrator farqni hisobotda ko\'radi';

  @override
  String closeShiftPending(int n) {
    return 'Avval $n ta chek serverga yuborilishi kerak — aloqa kerak.';
  }

  @override
  String get shiftClosedOk => 'Smena yopildi';

  @override
  String get receiptPrinter => 'Chek printeri';

  @override
  String get prnSub => 'Bluetooth, Wi-Fi/LAN yoki USB · 58/80 mm';

  @override
  String get prnNotSet => 'Sozlanmagan';

  @override
  String get prnConnection => 'Ulanish';

  @override
  String get prnOff => 'Yo\'q';

  @override
  String get prnNetwork => 'Tarmoq (Wi-Fi/LAN)';

  @override
  String get prnUsb => 'USB (OTG)';

  @override
  String get prnSearch => 'Printerlarni topish';

  @override
  String get prnSearching => 'Qidirilmoqda…';

  @override
  String get prnNothingFound =>
      'Hech narsa topilmadi. Printer yoqilganmi, yaqinmi?';

  @override
  String get prnPairHint =>
      'Printer ro\'yxatda yo\'qmi? Uni yoqing va «Topish»ni bosing — yoki telefonning Bluetooth sozlamalarida ulang (PIN odatda 0000 yoki 1234).';

  @override
  String get prnUsbHint =>
      'Printerni OTG kabeli bilan ulang va yoqing. Android ruxsat so\'raydi — «Ruxsat berish»ni bosing.';

  @override
  String get prnBtSettings => 'Bluetooth sozlamalari';

  @override
  String get prnPaired => 'ulangan';

  @override
  String get prnNotPaired => 'yaqinda, ulanmagan';

  @override
  String get prnIp => 'Printer IP manzili';

  @override
  String get prnPort => 'Port';

  @override
  String get prnPaper => 'Lenta kengligi';

  @override
  String prnPaperMm(int a, int b) {
    return '$a mm · $b belgi';
  }

  @override
  String get prnCodepage => 'Kirill kodlashi';

  @override
  String get prnCodeTable => 'ESC t jadvali';

  @override
  String get prnCodepageHint =>
      'Kirill harflari o\'rniga ieroglif yoki «?» — kodlashni yoki jadval raqamini almashtiring (CP866 — 17, ba\'zan 6 yoki 7; CP1251 — 46, ba\'zan 73) va qayta tekshiring.';

  @override
  String get prnAuto => 'To\'lovdan keyin chekni darhol chop etish';

  @override
  String get prnTest => 'Chop etishni tekshirish';

  @override
  String get prnTestSent => 'Sinov sahifasi printerga yuborildi';

  @override
  String get prnTestHint =>
      'Sinov sahifasini ko\'ring: raqamlar qatori butun kenglikni egallashi, kirill harflari o\'qilishi kerak.';

  @override
  String get prnPrint => 'Chekni chop etish';

  @override
  String get prnSent => 'Chek printerga yuborildi';

  @override
  String get prnErrNotConfigured =>
      'Printer sozlanmagan: Profil → Chek printeri.';

  @override
  String get prnErrUnsupported => 'Bu qurilmada bunday chop etish mavjud emas.';

  @override
  String get prnErrBtOff =>
      'Bluetooth o\'chiq — yoqing va qayta urinib ko\'ring.';

  @override
  String get prnErrPermission =>
      '«Yaqin atrofdagi qurilmalar» (Bluetooth) ruxsati yo\'q. Android oynasida yoki ilova sozlamalarida ruxsat bering.';

  @override
  String get prnErrNotFound =>
      'Printer topilmadi — yoqilganini tekshiring va qayta tanlang.';

  @override
  String get prnErrBleOnly =>
      'Bu printer faqat BLE orqali ishlaydi — oddiy Bluetooth (SPP) printer kerak.';

  @override
  String get prnErrTimeout =>
      'Printer javob bermayapti. Yoqilganmi, yaqinmi, qog\'oz bormi? Qayta urinib ko\'ring.';

  @override
  String get prnErrRefused =>
      'Tarmoq printeri qabul qilmayapti — IP manzil va portni tekshiring (odatda 9100).';

  @override
  String get prnErrUsbDenied =>
      'USB printerga ruxsat yo\'q — Android oynasida «Ruxsat berish»ni bosing.';

  @override
  String get prnErrUsbNotFound =>
      'USB printer topilmadi — OTG kabeli va printer quvvatini tekshiring.';

  @override
  String prnErrIo(String text) {
    return 'Chop etish xatosi: $text';
  }

  @override
  String get kgUnit => 'kg';

  @override
  String get weighShort => 'Tarozi';

  @override
  String get weighTitle => 'Tortiladigan mahsulot';

  @override
  String get weighHint =>
      'Mahsulotni taroziga qo\'ying. Kamerani mahsulot va tarozi tablosi ko\'rinadigan qilib tuting.';

  @override
  String get weighReading =>
      'Mahsulotni aniqlab, tarozi tablosini o\'qiyapman…';

  @override
  String get weighWaiting => 'Og\'irlik barqarorlashishini kutyapman…';

  @override
  String get weighZero => 'Tarozida 0 — mahsulotni qo\'ying.';

  @override
  String get weighNoDisplay =>
      'Tarozi tablosi ko\'rinmayapti — kamerani og\'irlik raqamlariga qarating.';

  @override
  String get weighNoAi =>
      'Aqlli kamera mavjud emas (aloqa yoki SI kaliti yo\'q) — mahsulotni tanlang va og\'irlikni qo\'lda kiriting.';

  @override
  String get weighConfirm => 'Mahsulot va og\'irlikni tekshiring';

  @override
  String weighRead(String name, String kg) {
    return 'Kamera o\'qidi: $name · $kg kg';
  }

  @override
  String weighLine(String name, String kg, String price, String sum) {
    return '$name $kg kg × $price = $sum so\'m';
  }

  @override
  String weighTooHeavy(String kg) {
    return 'Og\'irlik $kg kg — 50 kg dan ortiq. Baribir qo\'shilsinmi?';
  }

  @override
  String get weighNotStable =>
      'Og\'irlik barqarorlashmadi — ko\'rsatkichni tekshiring.';

  @override
  String get weighEditKg => 'Og\'irlikni o\'zgartirish';

  @override
  String get weighOther => 'Boshqa mahsulot — ro\'yxatdan tanlash';

  @override
  String get linkWithKassa => 'Kassa bilan aloqa';

  @override
  String get linkWithKassaSub => 'Do\'kon Wi-Fi orqali — keyingi yangilanishda';

  @override
  String get language => 'Til';

  @override
  String get darkTheme => 'Qorong\'i mavzu';

  @override
  String get offlineMode => 'Internetsiz ishlash';

  @override
  String waitingToSend(int n) {
    return 'Yuborishni kutmoqda: $n';
  }

  @override
  String get logout => 'Hisobdan chiqish';

  @override
  String logoutPending(int n) {
    return 'Telefonda $n ta yuborilmagan yozuv bor. Chiqgandan keyin ular qoladi va shu hisob qayta kirganda yuboriladi. Chiqasizmi?';
  }

  @override
  String get sync => 'Sinxronlash';

  @override
  String catalogUpdated(String time) {
    return 'Katalog yangilandi $time';
  }

  @override
  String get nothingToSend => 'Hammasi yuborildi';

  @override
  String get sent => 'yuborildi';

  @override
  String get sendNow => 'Hozir yuborish';

  @override
  String get kindSale => 'Sotuv';

  @override
  String get admin => 'Boshqaruv';

  @override
  String get adminSub => 'Narxlar, smenalar, buyurtmalar, mijozlar';

  @override
  String get revenueToday => 'Bugungi tushum';

  @override
  String get checksToday => 'Bugungi cheklar';

  @override
  String get clientsOwe => 'Mijozlar qarzi';

  @override
  String get productPrice => 'Mahsulot — narx va qoldiq';

  @override
  String get shifts => 'Smenalar va hisobot';

  @override
  String get siteOrders => 'Saytdan buyurtmalar';

  @override
  String get clientsBonuses => 'Mijozlar va bonuslar';

  @override
  String get supplierOrder => 'Yetkazib beruvchiga buyurtma';

  @override
  String get deletedFromCheck => 'Chekdan o\'chirilganlar';

  @override
  String get nameOrBarcode => 'Nom yoki shtrix-kod';

  @override
  String get salePrice => 'Sotuv narxi';

  @override
  String get purchasePrice => 'Xarid narxi';

  @override
  String get wholesalePrice => 'Ulgurji narx';

  @override
  String get onStockLabel => 'Omborda';

  @override
  String get priceSaved => 'Narx saqlandi.';

  @override
  String markup(String pct) {
    return 'Ustama $pct %';
  }

  @override
  String get belowCost => 'Narx xariddan past — zararga sotuv';

  @override
  String get shiftOpenNow => 'ochiq';

  @override
  String shiftClosedAt(String time) {
    return '$time da yopildi';
  }

  @override
  String get cashierLabel => 'Kassir';

  @override
  String get orderNew => 'Yangi';

  @override
  String get orderPicking => 'Yig\'ilmoqda';

  @override
  String get orderReady => 'Tayyor';

  @override
  String get orderDone => 'Berildi';

  @override
  String get orderCanceled => 'Bekor qilindi';

  @override
  String get pickOrder => 'Yig\'ish';

  @override
  String get pickScanHint => 'Mahsulotni skanerlang — o\'zi belgilanadi';

  @override
  String pickedOf(int a, int b) {
    return '$b dan $a yig\'ildi';
  }

  @override
  String get orderPicked => 'Buyurtma yig\'ildi';

  @override
  String get notInOrder => 'Bu mahsulot buyurtmada yo\'q';

  @override
  String get notifyCustomer => 'Xaridorga xabar berish';

  @override
  String get handOver => 'Berish';

  @override
  String orderReadyMsg(String num, String sum, String code) {
    return '$num buyurtma tayyor! To\'lov $sum so\'m. Berish kodi $code.';
  }

  @override
  String get statusSaved => 'Holat saqlandi.';

  @override
  String get bonusBalance => 'Bonus balansi';

  @override
  String get addBonus => 'Bonus qo\'shish';

  @override
  String get bonusAdded => 'Bonuslar qo\'shildi.';

  @override
  String get noPhone => 'raqam yo\'q';

  @override
  String get orderToSupplier => 'WhatsApp\'ga yuborish';

  @override
  String get supplierName => 'Yetkazib beruvchi';

  @override
  String get supplierPhone => 'Yetkazib beruvchi telefoni';

  @override
  String get runningLowAdd => 'Tugayotganlarning hammasini qo\'shish';

  @override
  String get orderQty => 'Buyurtma miqdori';

  @override
  String get deleteReason => 'Nega olib tashlaysiz?';

  @override
  String get reasonChanged => 'Xaridor fikridan qaytdi';

  @override
  String get reasonScanError => 'Skanerlash xatosi';

  @override
  String get reasonNoMoney => 'Pul yetmadi';

  @override
  String get reasonDefect => 'Nuqson yoki muddati o\'tgan';

  @override
  String get remove => 'Olib tashlash';

  @override
  String get noDeletions => 'O\'chirish bo\'lmagan.';

  @override
  String get loading => 'Yuklanmoqda…';

  @override
  String get empty => 'Bu yerda bo\'sh.';

  @override
  String get share => 'Ulashish';

  @override
  String get documentPdf => 'Yuk xati, tovar cheki, hisob';

  @override
  String get docSalesReceipt => 'Tovar cheki';

  @override
  String get docWaybill => 'Yuk xati';

  @override
  String get docInvoice => 'To\'lov hisobi';

  @override
  String get staff => 'Xodimlar va huquqlar';

  @override
  String get rightSale => 'Sotuv va to\'lov';

  @override
  String get rightReturn => 'Qaytarish va almashtirish';

  @override
  String get rightDiscount => 'Chegirmalar';

  @override
  String get rightEditPrice => 'Chekda narxni o\'zgartirish';

  @override
  String get rightDeleteLine => 'Chekdan o\'chirish';

  @override
  String get rightStock => 'Ombor, qabul, reviziya';

  @override
  String get rightReports => 'Hisobotlar va tahlil';

  @override
  String get rightShifts => 'Smenalar';

  @override
  String get rightClients => 'Mijozlar';

  @override
  String get rightSettings => 'Sozlamalar';

  @override
  String get rightsSaved => 'Huquqlar saqlandi.';

  @override
  String get notifications => 'Bildirishnomalar';

  @override
  String get notifyNewOrder => 'Saytdan yangi buyurtma';

  @override
  String get notifyRunningOut => 'Mahsulotlar tugamoqda';

  @override
  String notifyRunningOutSub(int n) {
    return '$n ta · buyurtma vaqti';
  }

  @override
  String get notifyBigDebt => 'Mijozning katta qarzi';

  @override
  String get notifyNotSent => 'Serverga yuborilmagan';

  @override
  String get noNotifications => 'Yangi bildirishnoma yo\'q.';

  @override
  String get newProductSub => 'Shtrix-kod, nom, narxlar';

  @override
  String get findByBarcode => 'Shtrix-kod bo\'yicha topish';

  @override
  String alreadyInStock(String name) {
    return 'Bu mahsulot bor: $name';
  }

  @override
  String get productCreated => 'Mahsulot yaratildi.';

  @override
  String get quantityLabel => 'Miqdor';

  @override
  String get barcodeLabel => 'Shtrix-kod';

  @override
  String get retail => 'Chakana';

  @override
  String get wholesale => 'Ulgurji';

  @override
  String get wholesaleOn =>
      'Ulgurji savdo: ulgurji narxi bor mahsulotlar ulgurji narxda.';

  @override
  String get wholesaleNone =>
      'Bu chekdagi mahsulotlarning ulgurji narxi yo\'q.';

  @override
  String get wholesaleTag => 'ulgurji';

  @override
  String get cashJournal => 'Smenadagi kirim va chiqimlar';

  @override
  String get noCashOps => 'Kirim va chiqim bo\'lmagan.';

  @override
  String get themeLight => 'Yorug\'';

  @override
  String get themeDark => 'Qorong\'i';

  @override
  String get themeSystem => 'Tizimdagidek';

  @override
  String get appearance => 'Ko\'rinish';

  @override
  String get searchAll => 'Mahsulot, mijoz yoki bo\'lim';

  @override
  String get sectionsTitle => 'Bo\'limlar';

  @override
  String get productsTitle => 'Mahsulotlar';

  @override
  String get clientsTitle => 'Mijozlar';

  @override
  String get openAction => 'Ochish';

  @override
  String get returnAction => 'Qaytarish';

  @override
  String addedToCheckName(String name) {
    return 'Chekka qo\'shildi: $name';
  }

  @override
  String get receiveWithPrices => 'Narxlar bilan qabul';

  @override
  String get receiveWithPricesSub =>
      'Yetkazib beruvchi, xarid, sotuv, yangi mahsulotlar';

  @override
  String get noSupplier => 'Yetkazib beruvchisiz';

  @override
  String get newProduct => 'Yangi mahsulot';

  @override
  String barcodeNotInStock(String code) {
    return '$code shtrix-kodi omborda yo\'q';
  }

  @override
  String get foundInBase => 'Umumiy bazadan topildi — tekshiring';

  @override
  String get unitLabel => 'Birlik';

  @override
  String get createAndAdd => 'Yaratish va qo\'shish';

  @override
  String markupRule(String pct) {
    return 'Sotuv narxi: xarid +$pct %';
  }

  @override
  String get payNowCash => 'Darhol to\'landi';

  @override
  String get onCredit => 'Qarzga';

  @override
  String purchaseSum(String sum) {
    return 'Xarid $sum so\'m';
  }

  @override
  String get postReceipt => 'Kirimni o\'tkazish';

  @override
  String receiptPosted(int n) {
    return 'Kirim o\'tkazildi: $n ta';
  }

  @override
  String partlyFailed(String text) {
    return 'O\'tmadi: $text';
  }

  @override
  String get shiftsOpen => 'Ochiq';

  @override
  String get shiftsClosed => 'Yopiq';

  @override
  String get debtAccessTitle => 'Qarzga ruxsat';

  @override
  String get debtAccessHint =>
      'Qaysi xodimlar qarzga sota oladi: mumkin, mumkin emas yoki limit bilan — kuniga chek va summa. Qoidalar kompaniyaning barcha kassa va telefonlari uchun umumiy. Egasi va administratorga cheklov yo\'q.';

  @override
  String get debtAllStaff => 'Barcha xodimlar';

  @override
  String get debtSameAsAll => 'Hammadek';

  @override
  String get debtAllow => 'Mumkin';

  @override
  String get debtDeny => 'Mumkin emas';

  @override
  String get debtLimit => 'Limit bilan';

  @override
  String get debtReceiptsPerDay => 'Kuniga qarz chek';

  @override
  String get debtSumPerDay => 'Kuniga qarz summasi, so\'m';

  @override
  String get debtNoLimit => 'cheksiz';

  @override
  String get debtAccessSaved =>
      'Saqlandi — qoidalar barcha kassa va telefonlarda amal qiladi.';

  @override
  String get debtStaffNotLoaded =>
      'Xodimlar ro\'yxati olinmadi — hamma uchun qoida qo\'yish mumkin.';

  @override
  String get debtDenied =>
      'Egasi sizga qarzga sotishni taqiqlagan. Boshqa to\'lov usulini tanlang yoki egasini chaqiring.';

  @override
  String debtLimitReceipts(int a, int b) {
    return 'Bugungi qarzga sotish limiti tugadi: $a / $b chek. Egasiga murojaat qiling.';
  }

  @override
  String debtLimitSum(String left, String max, String sum) {
    return 'Bugungi qarz limiti oshdi: $max so\'mdan yana $left so\'m mumkin, qarz $sum so\'m. To\'lovning bir qismini hozir oling yoki egasini chaqiring.';
  }

  @override
  String debtReceiptsOf(int a, int b) {
    return 'chek $a / $b';
  }

  @override
  String debtSumOf(String used, String max) {
    return 'qarz $used / $max so\'m';
  }

  @override
  String debtYourLimit(String text) {
    return 'Bugungi qarz limitingiz: $text';
  }

  @override
  String get promoTag => 'aksiya';

  @override
  String get promoNoManualDiscount =>
      'Mahsulotga aksiya amal qiladi — chegirmani server hisoblaydi, bu qatorga qo\'lda chegirma berib bo\'lmaydi.';

  @override
  String get quoteSearch => 'Raqam, mijoz yoki telefon';

  @override
  String get quotesEmpty => 'Hozircha KP va hisoblar yo\'q';

  @override
  String get quotesEmptyHint =>
      '«Yaratish»ni bosing: mijoz va mahsulotlarni tanlang — PDF WhatsApp yoki pochtaga yuboriladi.';

  @override
  String get quotesNoMatch =>
      'Hech narsa topilmadi — qidiruv yoki filtrni o\'zgartiring.';

  @override
  String get createDoc => 'Yaratish';

  @override
  String get makeInvoice => 'Hisob yaratish';

  @override
  String get sendToClient => 'Yuborish';

  @override
  String docWaText(String doc, String number, String sum, String until) {
    return '$doc № $number, summasi $sum so\'m. $until.';
  }

  @override
  String get docSubtotal => 'Chegirmasiz summa';

  @override
  String get discountShort => 'Chegirma';

  @override
  String get paidLabel => 'To\'langan';

  @override
  String get leftToPay => 'To\'lash qoldi';

  @override
  String get findClient => 'Mijozni topish';

  @override
  String get companyLabel => 'Kompaniya (MChJ, YaTT)';

  @override
  String get clientExists => 'Bu telefonli mijoz NurCRMda bor — u tanlandi.';

  @override
  String get clientSaved => 'Mijoz NurCRMga saqlandi';

  @override
  String get clientLocalOnly =>
      'NurCRM bilan aloqa yo\'q — mijoz faqat shu hujjatga yozildi.';

  @override
  String get clientNameRequired => 'Ism yoki kompaniyani kiriting';

  @override
  String get innDigits => 'INN — faqat raqamlar, odatda 14';

  @override
  String get discountHint => 'Summa yoki foiz, masalan 10%';

  @override
  String get stExpired => 'Muddati tugadi';

  @override
  String deleteDocAsk(String number) {
    return '$number o\'chirilsinmi?';
  }

  @override
  String get deleteDocAskSub =>
      'Hujjat egasi dasturidan ham butunlay o\'chiriladi.';

  @override
  String get requisitesTitle => 'Hisob rekvizitlari';

  @override
  String get requisitesHint =>
      'Bank, hisob raqami, BIK, telefon — PDFda do\'kon nomi ostida chiqadi.';

  @override
  String scannedAdded(String name) {
    return 'Qo\'shildi: $name';
  }

  @override
  String invoiceCreated(String number) {
    return '$number hisobi yaratildi';
  }

  @override
  String get historyLabel => 'Tarix';

  @override
  String get docLines => 'Mahsulotlar va xizmatlar';

  @override
  String docFrom(String date) {
    return '$date sanali';
  }

  @override
  String get clientNotChosen => 'Mijoz tanlanmagan';

  @override
  String get linesEmptyHint =>
      'Mahsulot qo\'shing: qidiruv, skaner yoki joriy chekdan.';

  @override
  String get quotesSyncing => 'Umumiy ro\'yxat yangilanmoqda…';

  @override
  String get quotesShared => 'Egasi dasturi bilan umumiy ro\'yxat';

  @override
  String quotesSharedAt(String time) {
    return 'Umumiy ro\'yxat yangilandi: $time';
  }

  @override
  String get quotesOffline =>
      'Server bilan aloqa yo\'q — telefondagi nusxa ko\'rsatilmoqda';

  @override
  String quotesNotSent(int n) {
    return 'Yuborilmadi: $n. Aloqa bo\'lganda egasi dasturiga ketadi — qayta urinish uchun bosing.';
  }

  @override
  String get quotesForbidden =>
      'Kompaniyaning umumiy sozlamalariga huquq yo\'q — o\'zgarishlar faqat shu telefonda. Egasi yoki administrator kirishi kerak.';

  @override
  String get notSentMark => 'yuborilmagan';

  @override
  String quoteChangedElsewhere(String number) {
    return '$number keyinroq boshqa joyda o\'zgartirildi — oxirgi versiya ko\'rsatildi';
  }

  @override
  String quoteRenumbered(String from, String to) {
    return '$from raqami band — hujjat endi $to';
  }

  @override
  String get payOutcomeUnknown =>
      'Server javob bermadi — sotuv o\'tgan bo\'lishi mumkin. Hech narsani o\'zgartirmay «To\'lash»ni yana bosing: takror ikkinchi marta o\'tmaydi. Aloqasiz qarz va bonus yechish rasmiylashtirilmaydi.';

  @override
  String get debtDueLabel => 'Qaytarish muddati';

  @override
  String debtDueDays(int n) {
    return '$n kun';
  }

  @override
  String get debtDueNotSaved =>
      'Qarz muddati serverga yozilmadi — saytda tuzating (odatiy 30 kun).';

  @override
  String returnAmount(String sum) {
    return 'Qaytarish summasi: $sum so\'m';
  }

  @override
  String returnGive(String what) {
    return 'Xaridorga berish: $what';
  }

  @override
  String returnInCash(String sum) {
    return 'naqd $sum so\'m';
  }

  @override
  String returnToCard(String sum) {
    return 'kartaga/o\'tkazma bilan $sum so\'m';
  }

  @override
  String returnDebtReduced(String sum) {
    return 'Mijozning qarzi $sum so\'mga kamaydi — bu pul berilmaydi.';
  }

  @override
  String returnDebtUnknown(String sum) {
    return 'Diqqat: mijoz qarzining qoldig\'i serverdan olinmadi. Hisob bo\'yicha qarz $sum so\'mga kamaydi, lekin mijoz bu chek bo\'yicha qarzini to\'lab bo\'lgan bo\'lsa — unga $sum so\'mgacha qaytarish kerak. Berishdan oldin mijoz qarzini tekshiring.';
  }

  @override
  String get returnViaBank =>
      'Karta/o\'tkazma qismi — kassadan emas, bank orqali qaytaring.';

  @override
  String returnPaidInfo(String paid, String done) {
    return 'Chek bo\'yicha to\'langan: $paid so\'m · qaytarilgan: $done so\'m';
  }

  @override
  String returnedQty(String qty) {
    return 'qaytarilgan: $qty';
  }

  @override
  String get returnNothingLeft => 'Bu chek bo\'yicha hammasi qaytarilgan.';

  @override
  String cashOutTooMuch(String sum) {
    return 'Olinadigan summa kassadagidan ko\'p ($sum so\'m).';
  }

  @override
  String payDebtToDebt(String sum) {
    return 'Qarz hisobiga: $sum so\'m';
  }

  @override
  String get payDebtOverCard => 'O\'tkazma bilan — qarzdan ko\'p emas.';

  @override
  String receiveNotAccepted(String reason) {
    return 'Kirim server tomonidan qabul qilinmadi: $reason. Yozuv — «Sinxronlash»da.';
  }

  @override
  String get auditApply => 'Inventarizatsiyani o\'tkazish';

  @override
  String get auditApplyHint =>
      'Qoldiqlar sanalganiga teng bo\'ladi — kompyuterdagi kassadagi inventarizatsiya kabi. Aloqa kerak.';

  @override
  String auditApplyAsk(int n, int a) {
    return 'Tekshirilgan mahsulot: $n, farq: $a. Qoldiqlar sanalganiga teng bo\'ladi. O\'tkazilsinmi?';
  }

  @override
  String get auditApplied =>
      'Inventarizatsiya o\'tkazildi — qoldiqlar yangilandi.';

  @override
  String get auditApplyQueued =>
      'Inventarizatsiya akti yaratildi — o\'tkazish aloqa bo\'lganda yuboriladi.';

  @override
  String get auditOnlineOnly =>
      'Inventarizatsiya faqat server bilan aloqa bo\'lganda o\'tkaziladi — sanash saqlandi, keyinroq qaytaring.';

  @override
  String auditContinue(int n) {
    return 'Sanashni davom ettirish ($n ta mahsulot)';
  }

  @override
  String get countedCash => 'Sanaldi';

  @override
  String get openingCashLabel => 'Smena boshida';

  @override
  String closeShiftFailed(int n) {
    return 'Server $n ta chekni qabul qilmadi — ular smena hisobotiga kirmaydi. Qayta yuboring yoki «Sinxronlash»da olib tashlang.';
  }

  @override
  String get closeShiftAsk => 'Smena yopilsinmi?';

  @override
  String get closeShiftAskSub =>
      'Yopilgandan keyin bu smenada sotib bo\'lmaydi.';

  @override
  String get xReport => 'X-hisobot';

  @override
  String get foreignShiftHint =>
      'Bu kassada boshqa kassirning smenasi ochiq — unga sotib bo\'lmaydi. O\'z smenangizni oching.';

  @override
  String get loadMore => 'Yana';

  @override
  String get dropRow => 'Olib tashlash';

  @override
  String get dropRowAsk =>
      'Chek navbatdan olib tashlansinmi? U serverga ketmaydi, mahsulot telefondagi qoldiqqa qaytadi. Pul olingan bo\'lsa — xaridorga qaytaring yoki chekni qayta uring.';

  @override
  String get dropRowAskStock =>
      'Yozuv navbatdan olib tashlansinmi? U serverga ketmaydi — mahsulot qoldig\'ini qo\'lda tekshiring.';

  @override
  String get otherAccount => 'boshqa hisob';

  @override
  String otherAccountHint(String email) {
    return '$email kirishi bilan yozilgan — shu login bilan kirilgandan keyin yuboriladi.';
  }

  @override
  String pAStockUnclear(String name, String now, String before) {
    return '$name: oldingi urinish serverdan javobsiz qoldi, qoldiq esa o\'shandan beri o\'zgardi (hozir $now, qo\'shishdan oldin $before edi). Ikki baravar bo\'lmasligi uchun qayta QO\'SHILMADI. Qoldiqni inventarizatsiya bilan tekshiring: qo\'shilgan bo\'lsa — qatorni o\'chiring, qo\'shilmagan bo\'lsa — o\'chirib, mahsulotni qayta qo\'shing.';
  }

  @override
  String get pAWeightLabel =>
      'Bu tarozi yorlig\'i (bitta xaridning og\'irligi) — qabulga olinmaydi. Mahsulot shtrix-kodini skanerlang yoki nomi bo\'yicha toping.';

  @override
  String get pANoServerCheck =>
      'Server bilan aloqa yo\'q — bu shtrix-kodli mahsulot bor-yo\'qligini tekshirib bo\'lmaydi. Aloqa paydo bo\'lganda qayta urining.';

  @override
  String pALinesLeft(int n) {
    return 'Qabul qilinmagan qatorlar: $n — ular ro\'yxatda qoldi, tuzatib qayta o\'tkazing.';
  }

  @override
  String get pAPluLabel => 'Tarozi uchun PLU';

  @override
  String get pAPluHint => 'bo\'sh — o\'zi beriladi';

  @override
  String get pAStockAdd => 'Qoldiqqa qo\'shish';

  @override
  String get pAStockSet => 'Qoldiqni belgilash';

  @override
  String get pAStockAddHint =>
      'Fayldagi miqdor kirim kabi qoldiqqa qo\'shiladi.';

  @override
  String get pAStockSetHint =>
      'Qoldiq fayldagi miqdorga teng bo\'ladi (almashtirish, kirim emas).';

  @override
  String pADupRow(int n) {
    return '$n-qatorning takrori — o\'tkazib yuboriladi';
  }

  @override
  String get pAUploaded => 'yuklandi';

  @override
  String get rightCashier => 'Kassir interfeysi';

  @override
  String get rightOrders => 'Saytdan buyurtmalar';

  @override
  String get rightProcurement => 'Xaridlar';

  @override
  String get rightSuppliers => 'Yetkazib beruvchilar';

  @override
  String get rightSelfLocked =>
      'O\'z huquqlaringizni o\'chirib bo\'lmaydi — buni egasi yoki boshqa administrator qiladi.';

  @override
  String get staffYou => 'bu siz';

  @override
  String get screenUnavailable =>
      'Ekran qayta ochildi va uning ma\'lumotlari saqlanmadi. Orqaga qayting va uni yana oching.';

  @override
  String get cancelOrder => 'Buyurtmani bekor qilish';

  @override
  String cancelOrderAsk(String number) {
    return '№$number buyurtma bekor qilinsinmi? Xaridor uni olmaydi.';
  }

  @override
  String pickShortAsk(String got, String need) {
    return '$need tadan $got ta yig\'ildi. Buyurtma to\'liq emas yig\'ilgan deb belgilansinmi?';
  }

  @override
  String pickupCodeLabel(String code) {
    return 'Berish kodi: $code';
  }

  @override
  String get torchToggle => 'Fonar';

  @override
  String get payTransferQr => 'O\'tkazma / QR';

  @override
  String get reportLoadFailed =>
      'Hisobot yuklanmadi. Aloqani tekshiring va «Qayta urinish»ni bosing.';

  @override
  String get rentalLatePerDay => 'Kechikkan har sutka uchun penya';

  @override
  String rentalLateHint(int n, String rate, String sum) {
    return 'Kechikish $n sutka × $rate = $sum so\'m (prokat shartlari bo\'yicha)';
  }

  @override
  String rentalBackDeposit(String sum) {
    return 'Garovni qaytaring: $sum so\'m';
  }

  @override
  String rentalWithheld(String sum) {
    return 'ushlab qolindi $sum so\'m';
  }

  @override
  String rentalBackDocument(String doc) {
    return 'Hujjatni qaytaring: $doc';
  }

  @override
  String rentalExcess(String sum) {
    return 'Garovdan ortiq jarima: $sum so\'m — chekka qo\'shiladi';
  }

  @override
  String rentalPenaltyLine(int n) {
    return 'Prokat №$n bo\'yicha jarima';
  }

  @override
  String rentalPenaltyInCart(String sum) {
    return 'Qaytarish qabul qilindi. $sum so\'m jarima chekka qo\'shildi — to\'lovni o\'tkazing.';
  }

  @override
  String get waybillDoc => 'Yuk xati';

  @override
  String get waybillsFilter => 'Yuk xatlari';

  @override
  String get docPayMarkOnly =>
      'Bu faqat hujjatdagi belgi: pul smena kassasiga va mijoz qarzi NurCRMga yozilmaydi. Pulni kassa orqali o\'tkazish uchun \"Sotuvni o\'tkazish\"dan foydalaning.';

  @override
  String get docPayMarkOk => 'To\'lovni belgilash';

  @override
  String get docSellCartBusy =>
      'Chekda mahsulotlar bor. Ularni hujjat mahsulotlari bilan almashtiraymi?';

  @override
  String get docSellReplace => 'Almashtirish';

  @override
  String get docSentToSale => 'Sotuvga berildi';

  @override
  String docSellMissing(String names) {
    return 'Telefon katalogida yo\'q — qo\'lda qo\'shing: $names';
  }

  @override
  String docVatIncl(String p) {
    return 'Shu jumladan QQS $p%';
  }

  @override
  String get prnQr => 'Chekda QR-kod';

  @override
  String get prnQrHint =>
      'Fiskal chekni tekshirish yoki elektron chek. Printer QR o\'rniga ortiqcha belgilar bossa — o\'chiring.';

  @override
  String get prnDrawer => 'Printer orqali pul qutisi';

  @override
  String get prnDrawerHint =>
      'Naqd sotuvdan keyin ochiladi (quti printerga kabel bilan ulangan).';

  @override
  String prnDrawerPin(int n) {
    return 'Kontakt $n';
  }

  @override
  String get prnDrawerTest => 'Qutini ochish';

  @override
  String get prnDrawerSent =>
      'Qutiga buyruq yuborildi. Ochilmasa — boshqa kontaktni tanlang.';

  @override
  String get stepPhoto => 'Rasm';

  @override
  String get stepBarcode => 'Shtrix-kod';

  @override
  String get bindTitle =>
      'Rasmlar saqlandi ✓ — endi mahsulot shtrix-kodini skanerlang';

  @override
  String get bindSub =>
      'Mahsulotda shtrix-kod bo\'lmasa — «O\'tkazib yuborish»ni bosing';

  @override
  String get bindSkip => 'O\'tkazib yuborish';

  @override
  String get bindManual => 'Qo\'lda kiritish';

  @override
  String get bindManualHint => 'Shtrix-kod ostidagi raqamlar';

  @override
  String get bindChecking => 'Shtrix-kod tekshirilmoqda…';

  @override
  String bindWillLink(String code, String name) {
    return '$code shtrix-kodi «$name» mahsulotiga bog\'lanadi';
  }

  @override
  String bindWillLinkNew(String code) {
    return '$code shtrix-kodi yangi mahsulotga bog\'lanadi';
  }

  @override
  String bindAlreadyOwn(String code, String name) {
    return '$code shtrix-kodi allaqachon «$name» ga bog\'langan — hech narsani o\'zgartirish shart emas';
  }

  @override
  String get bindTakenTitle => 'Shtrix-kod boshqa mahsulotda bor';

  @override
  String bindTakenAsk(String code, String name) {
    return '$code shtrix-kodi allaqachon «$name» ga bog\'langan.\n«Almashtirish» — shtrix-kod shu mahsulotga o\'tadi.\n«Qoldirish» — «$name» da qoladi, bu mahsulot shtrix-kodsiz bo\'ladi.';
  }

  @override
  String bindTakenAskNew(String code, String name) {
    return '$code shtrix-kodi allaqachon «$name» ga bog\'langan — balki bu mahsulot omborda bordir.\n«Qoldirish» — shtrix-kod «$name» da qoladi, yangi mahsulot shtrix-kodsiz bo\'ladi.';
  }

  @override
  String get bindReplace => 'Almashtirish';

  @override
  String get bindKeep => 'Qoldirish';

  @override
  String get bindRescan => 'Qayta skanerlash';

  @override
  String bindLinked(String code, String name) {
    return '$code shtrix-kodi «$name» ga bog\'landi';
  }

  @override
  String get teachNext =>
      'Mahsulotni 3–5 tomondan oling, so\'ng «Tayyor» — keyingi qadam shtrix-kod';

  @override
  String get teachNextNoCode =>
      'Mahsulotni 3–5 tomondan oling, so\'ng «Tayyor»ni bosing';

  @override
  String get liveNextSale =>
      'Topilgan mahsulotlar pastda chiqadi — chekka qo\'shish uchun bosing';

  @override
  String get liveNextList =>
      'Suratdan keyin kamera topilganlar ro\'yxatini ko\'rsatadi — uni tuzatish mumkin';

  @override
  String get scanCodesHint =>
      'Ramkani shtrix-kodga qarating — kod o\'zi yoziladi. So\'ng «Tayyor»ni bosing';

  @override
  String get weighNext =>
      'Kamera mahsulotni taniydi, og\'irlikni o\'qiydi va chekka qo\'shadi';

  @override
  String get weighManualBtn =>
      'Kamerasiz — mahsulotni tanlab, og\'irlikni kiritish';

  @override
  String get peEdit => 'Mahsulotni tahrirlash';

  @override
  String get peName => 'Nomi';

  @override
  String get peMinQty => 'Minimal qoldiq';

  @override
  String get peMinQtyHint =>
      'Bundan kam bo\'lsa — mahsulot \"Tugayapti\" ro\'yxatiga tushadi';

  @override
  String get peStock => 'Qoldiq';

  @override
  String get peStockHint =>
      'Qoldiq faqat hujjat bilan o\'zgaradi — sababli kirim, hisobdan chiqarish yoki inventarizatsiya, kompyuterdagidek. Shunda farq bo\'lmaydi.';

  @override
  String get peIncome => 'Kirim';

  @override
  String get peWriteOff => 'Hisobdan chiqarish';

  @override
  String get peRecount => 'Inventarizatsiya';

  @override
  String get peQtyIn => 'Qancha keldi';

  @override
  String get peQtyOut => 'Qanchasini hisobdan chiqarish';

  @override
  String get peQtyFact => 'Aslida qancha';

  @override
  String get peReason => 'Sabab';

  @override
  String get peReasonNeed => 'Sababni kiriting.';

  @override
  String get peReasonExpired => 'Muddati o\'tgan';

  @override
  String get peReasonDefect => 'Nuqsonli';

  @override
  String get peReasonSpoiled => 'Buzilgan';

  @override
  String get peReasonTheft => 'O\'g\'irlik';

  @override
  String get peReasonOther => 'Boshqa';

  @override
  String get peReasonSupplier => 'Yetkazib berish';

  @override
  String get peReasonCount => 'Qayta sanash';

  @override
  String get peApply => 'O\'tkazish';

  @override
  String peStockDone(String from, String to) {
    return 'Qoldiq: $from → $to';
  }

  @override
  String peStockSame(String qty) {
    return 'Qoldiq allaqachon $qty — hech narsa o\'zgarmadi.';
  }

  @override
  String get peStockQueued =>
      'Hujjat yaratildi — aloqa bo\'lganda o\'zi o\'tkaziladi.';

  @override
  String get peStockOffline =>
      'Server bilan aloqa yo\'q — qoldiq olinmadi, hech narsa o\'zgarmadi.';

  @override
  String peStockBelowZero(String qty) {
    return 'Qoldiqdan ko\'p hisobdan chiqarib bo\'lmaydi (bor $qty).';
  }

  @override
  String get peSaved => 'Mahsulot saqlandi.';

  @override
  String get peNoChanges => 'O\'zgarish yo\'q.';

  @override
  String pePriceRestored(String price) {
    return 'Server sotuv narxini ustama bo\'yicha qayta hisobladi — kiritilgan $price narxi qaytarildi.';
  }

  @override
  String get pePriceLocked =>
      'Narxlarni \"Narxni o\'zgartirish\" huquqi bor xodim o\'zgartiradi.';

  @override
  String get peLoadFailed =>
      'Mahsulot kartasi serverdan yuklanmadi — aloqa bo\'lganda tahrirlash mumkin.';

  @override
  String get peNameNeed => 'Nomini kiriting.';

  @override
  String peBarcodeTaken(String code, String name) {
    return '$code shtrix-kodi allaqachon \"$name\" mahsulotida.';
  }

  @override
  String peBarcodeTwice(String code) {
    return '$code shtrix-kodi ikki marta kiritilgan.';
  }

  @override
  String get peScan => 'Skanerlash';

  @override
  String get bcGenTooltip => 'Shtrix-kod yaratish';

  @override
  String get bcGenReplace => 'Joriy shtrix-kod yangisiga almashtirilsinmi?';

  @override
  String get bcGenDo => 'Yaratish';

  @override
  String pePackQty(String qty) {
    return 'qadoqda $qty';
  }

  @override
  String get peNoAccess =>
      'Mahsulotni \"Ombor\" huquqi bor xodim yoki administrator tahrirlay oladi.';

  @override
  String get smartNoLink =>
      'Server bilan aloqa yo‘q — hech narsa o‘tkazilmadi. Ro‘yxat ekranda qoldi, internet paydo bo‘lganda takrorlang.';

  @override
  String smartPartial(int done, int total, int left) {
    return '$total tadan $done tasi o‘tkazildi, $left tasi o‘tkazilmadi — «Yuklash» tugmasini yana bosing: o‘tkazilgani qayta qo‘shilmaydi.';
  }

  @override
  String get smartPendingReceipt =>
      'Oldingi yetkazib beruvchiga kirim javobsiz qoldi — avval uni «Yetkazib beruvchi bilan qabul»da takrorlang, aks holda qoldiq ikki marta qo‘shilishi mumkin.';

  @override
  String get smartPendingStock =>
      'Ba’zi qatorlar uchun server javob bermadi — avval «Yuklash»ni yana bosing: qoldiq ikki marta qo‘shilmaydi.';

  @override
  String get smartRowLocked =>
      'Qator serverga yuborilgan — tovar va nomni o‘zgartirib bo‘lmaydi, qayta yuboring.';

  @override
  String smartRowRejected(String reason) {
    return 'Rad etildi: $reason';
  }

  @override
  String get smartRowUnknown =>
      'Server javob bermadi — takrorlang: qoldiq ikki marta qo‘shilmaydi.';

  @override
  String payDebtLeft(String sum) {
    return 'Qoladigan qarz: $sum';
  }

  @override
  String get debtReceiptTitle => 'Qarz to\'langani haqida kvitansiya';

  @override
  String get returnsTitle => 'Qaytarishlar';

  @override
  String get shDebtPayments => 'Qarzlarni to\'lash';

  @override
  String get shDebtPrepay => 'Qarz bo\'yicha oldindan to\'lov';

  @override
  String get exitPressAgain => 'Chiqish uchun yana bir marta bosing';

  @override
  String get createProductWithCode => 'Shu kod bilan mahsulot yaratish';

  @override
  String get printLabel => 'Yorliq chop etish';

  @override
  String get openingCashField => 'Smena boshida kassadagi summa';

  @override
  String get advisorGreeting =>
      'Assalomu alaykum! Men Nur Maslahatchiman. Sotuvlar, qarzlar yoki qoldiqlar haqida so\'rang — do\'koningiz ma\'lumotlari bo\'yicha javob beraman.';

  @override
  String get a11yBack => 'Orqaga';

  @override
  String get a11yShowPassword => 'Parolni ko\'rsatish';

  @override
  String get a11yHidePassword => 'Parolni yashirish';

  @override
  String get a11yScanner => 'Skaner';

  @override
  String get a11yCallAi => 'SIga qo\'ng\'iroq qilish';

  @override
  String get stockNone => 'yo\'q';

  @override
  String get cartEmptyHint =>
      'Mahsulot qo\'shing: qidiring, skanerlang yoki kartani bosing';

  @override
  String get toProductsBtn => 'Mahsulotlarga';

  @override
  String get closeBtn => 'Yopish';

  @override
  String clientDebtLine(String sum) {
    return 'Qarz: $sum so\'m';
  }

  @override
  String get apiNoToken => 'Server token qaytarmadi.';

  @override
  String get apiSessionExpired => 'Sessiya tugadi — qaytadan kiring.';

  @override
  String get apiTimeout => 'Server o\'z vaqtida javob bermadi.';

  @override
  String get apiNoConnection => 'Server bilan aloqa yo\'q.';

  @override
  String apiServerError(String code) {
    return 'Server xatosi $code.';
  }

  @override
  String get apiNetworkError => 'Tarmoq xatosi.';

  @override
  String get adminNoLink => 'aloqa yo\'q';

  @override
  String printLabelAsk(String name, String code) {
    return '«$name» uchun yorliq chop etilsinmi? Shtrix-kod $code';
  }

  @override
  String get notNow => 'Hozir emas';

  @override
  String get bcGenTaken => 'Bu kod serverda band — yangisi berildi.';

  @override
  String get tapePrint => 'Chop etish';

  @override
  String get tapeSend => 'Yuborish';

  @override
  String get zReport => 'Smena Z-hisoboti';

  @override
  String get xReportNote => 'X-hisobot oraliq: smena yopilmagan.';

  @override
  String get shSalesBlock => 'Sotuvlar';

  @override
  String get shCashBlock => 'Kassadagi pul';

  @override
  String get shPrepayIncluded =>
      'naqd/naqdsizda — qarz bo\'yicha oldindan to\'lov kiritilgan';

  @override
  String get shReturnsUnknown =>
      'Smena qaytarishlari yuklanmadi — aloqa yo\'q.';

  @override
  String shReturnsPartial(int n) {
    return 'Qisman: serverning javobida kassiri yo\'q $n ta qaytarish hisobga olinmadi. Saytda tekshiring.';
  }

  @override
  String returnsCountN(int n) {
    return '$n ta qaytarish';
  }

  @override
  String get shReturnsCash => 'Naqd qaytarishlar';

  @override
  String get shCalcRow => 'Qatorlar bo\'yicha hisob';

  @override
  String shMismatch(String sum) {
    return 'Qatorlar yig\'indisi serverdan $sum so\'mga farq qiladi — server raqamiga tayaning.';
  }

  @override
  String get shNoSplit =>
      'Qaytarish va qarz to\'lovlari bo\'yicha taqsimot yo\'q — «Kassada bo\'lishi kerak» serverdan olingan.';

  @override
  String get shPartial =>
      'Ba\'zi ma\'lumotlar yuklanmadi (aloqa yo\'q) — ekranni yangilang.';

  @override
  String get debtReceiptBefore => 'To\'lovdan oldingi qarz';

  @override
  String get debtReceiptMethod => 'To\'lov usuli';

  @override
  String get debtReceiptAccepted => 'Qabul qilindi';

  @override
  String get debtReceiptCleared => 'Qarz to\'liq to\'landi.';

  @override
  String debtReceiptAcceptedLine(String sum, String left) {
    return 'Qabul qilindi $sum, qoldiq $left';
  }

  @override
  String get returnReasonTitle => 'Qaytarish sababi (ixtiyoriy)';

  @override
  String get returnReasonLabelShort => 'Sabab';

  @override
  String get returnReasonDefect => 'Nuqson';

  @override
  String get returnReasonNotFit => 'Mos kelmadi';

  @override
  String get returnReasonCashier => 'Kassir xatosi';

  @override
  String get listCachedNote =>
      'Aloqa yo\'q — oxirgi saqlangan ro\'yxat ko\'rsatilmoqda.';

  @override
  String get checksLoadFailed => 'Cheklarni yuklab bo\'lmadi';

  @override
  String get debtsLoadFailed => 'Qarzlarni yuklab bo\'lmadi';

  @override
  String get callMinimize => 'Yig\'ish';

  @override
  String get callAiListens => 'SI tinglayapti';

  @override
  String get callInterrupted => 'To\'xtatdingiz — tinglayapman';

  @override
  String get callExtend => 'Yana 10 daqiqa';

  @override
  String get callExtended => 'Qo\'ng\'iroq 10 daqiqaga uzaytirildi';

  @override
  String get callNoLink => 'SI bilan aloqa yo\'q';

  @override
  String get callNoLinkWork => 'SI bilan aloqa yo\'q, ekranda ishlang';

  @override
  String callNoLinkEnds(int n) {
    return 'Aloqa qaytmasa, $n soniyadan keyin tugaydi';
  }

  @override
  String get callNoLinkHelp => 'Savat, qidiruv va ombor SIsiz ishlaydi.';

  @override
  String get callSlowServer => 'Server sekin javob bermoqda…';

  @override
  String get callStillLoading =>
      'Ma\'lumotlar hali yuklanmoqda — kelganda aytaman';

  @override
  String get callSearching => 'Qidiryapman…';

  @override
  String get callSpeakingShort => 'Gapiryapman…';

  @override
  String get callHush => 'Jim';

  @override
  String get callOpenFull =>
      'Nur Maslahatchi bilan suhbat — ochish; ushlab turish — «Jim»';

  @override
  String aiSays(String text) {
    return 'SI: $text';
  }

  @override
  String get aiUndo => 'Bekor qilish';

  @override
  String get aiCantUndo => 'bekor qilib bo\'lmaydi';

  @override
  String get aiPickTitle => 'Qaysi biri?';

  @override
  String get aiAmbiguousHint => 'Raqamni ayting yoki bosing';

  @override
  String get aiNotThat => 'Bu emas';

  @override
  String aiYesNumber(String num) {
    return 'Ha, $num';
  }

  @override
  String get aiNoRepeat => 'Yo\'q, qayta aytaman';

  @override
  String aiConfirmDo(String what) {
    return 'Bajarish: $what';
  }

  @override
  String get aiDo => 'Bajarish';

  @override
  String get aiDont => 'Kerak emas';

  @override
  String get aiOrSayYes => 'yoki «ha» deb ayting';

  @override
  String get aiModeAskMoney => 'Pul oldidan so\'rash';

  @override
  String get aiWhatPay => 'to\'lov';

  @override
  String get aiWhatReturn => 'qaytarish';

  @override
  String get aiWhatReceive => 'kirimni o\'tkazish';

  @override
  String get aiWhatWriteOff => 'hisobdan chiqarish';

  @override
  String get aiWhatCount => 'reviziya';

  @override
  String get aiWhatCloseShift => 'smenani yopish';

  @override
  String get aiWhatSalary => 'xodimga to\'lov';

  @override
  String get aiWhatDebt => 'qarzni to\'lash';

  @override
  String get aiWhatDelete => 'tovarni o\'chirish';

  @override
  String get aiWhatPrice => 'narxni o\'zgartirish';

  @override
  String get aiWhatCash => 'kassaga kirim yoki chiqim';

  @override
  String get aiWhatOther => 'amal';

  @override
  String get tblBigger => 'Kattaroq';

  @override
  String get tblSmaller => 'Kichikroq';

  @override
  String get tblToChat => 'Chatga';

  @override
  String get tblPdf => 'PDF';

  @override
  String get tblShare => 'Ulashish';

  @override
  String get tblPin => 'Mahkamlash';

  @override
  String get tblPinned => 'Mahkamlangan';

  @override
  String tblMore(int n) {
    return 'Yana $n ta ko\'rsatish';
  }

  @override
  String tblMoreLeft(int n) {
    return '$n ta qoldi';
  }

  @override
  String get tblAllShown => 'Barcha qatorlar ko\'rsatilgan';

  @override
  String tblSource(String source, String time) {
    return 'dastur ma\'lumoti · $source · $time';
  }

  @override
  String get tblEmpty => 'Hozircha bo\'sh';

  @override
  String tblEmptyFilter(String q) {
    return '«$q» bo\'yicha hech narsa topilmadi';
  }

  @override
  String get tblClearFilter => 'Filtrni olib tashlash';

  @override
  String get tblOpenObject => 'Ochish';

  @override
  String tblRowsN(int n) {
    return '$n ta qator';
  }

  @override
  String get tblPrev => 'Oldingi jadval';

  @override
  String get tblNext => 'Keyingi jadval';

  @override
  String get tblCollapse => 'Panelni yig\'ish';

  @override
  String get tblClose => 'Jadvalni yopish';

  @override
  String get tblSentChat => 'Jadval chatga qo\'shildi';

  @override
  String get tblPdfFailed => 'PDF yaratib bo\'lmadi';

  @override
  String get vcCamera => 'Kamera';

  @override
  String get vcCameraOn => 'Kamera yoniq';

  @override
  String get vcCameraOff => 'Kamera o\'chiq';

  @override
  String get vcSwitchCamera => 'Boshqa kamera';

  @override
  String get vcTorch => 'Chiroq';

  @override
  String get vcRecording => 'Kamera';

  @override
  String get vcAiSees => 'SI ko\'ryapti';

  @override
  String get vcEconomy => 'Trafik tejash';

  @override
  String get vcEconomyHint => '3 soniyada 1 kadr va faqat siz gapirganda';

  @override
  String get vcStarting => 'Kamerani yoqyapman…';

  @override
  String get vcSearching => 'Katalogdan qidiryapman…';

  @override
  String get vcWhatIsIt => 'Bu nima?';

  @override
  String get vcInvoice => 'Nakladnoy';

  @override
  String get vcShelf => 'Javon';

  @override
  String get vcPriceTag => 'Narx yorlig\'i';

  @override
  String get wizAddProduct => 'Tovar qo\'shish';

  @override
  String get wizTitle => 'Tovar qo\'shilmoqda';

  @override
  String get wizSlotFront => 'Old';

  @override
  String get wizSlotBack => 'Orqa';

  @override
  String get wizSlotSide => 'Yon';

  @override
  String get wizSlotCode => 'Shtrixkod';

  @override
  String wizShootSide(String side) {
    return 'Olish: $side';
  }

  @override
  String get wizCreate => 'Tovar yaratish';

  @override
  String wizDraft(String name, String price) {
    return '«$name» · $price so\'m';
  }

  @override
  String get wizHint => '«Tayyor» deng yoki Olish tugmasini bosing';

  @override
  String wizDuplicate(String name) {
    return 'Allaqachon bor: «$name»';
  }

  @override
  String wizPhotoAlt(String side) {
    return 'Surat: $side';
  }

  @override
  String get wizEnterCode => 'Shtrixkod kiritish';

  @override
  String get wizEnterCodeHint =>
      'Skaner bilan o\'qiting yoki raqamlarni kiriting';

  @override
  String get vcCardProduct => 'Tovar';

  @override
  String get vcNotFound =>
      'Katalogdan topilmadi. Yaqinroq tuting yoki shtrix-kodni ko\'rsating';

  @override
  String get vcExact => 'aniq';

  @override
  String get vcByBarcode => 'shtrix-kod';

  @override
  String get vcByPhoto => 'rasm bo\'yicha';

  @override
  String get vcLooksLike => 'O\'xshaydi:';

  @override
  String get vcPickHint => 'tanlash uchun bosing';

  @override
  String vcPrice(String sum) {
    return 'Narxi: $sum so\'m';
  }

  @override
  String vcStock(String qty) {
    return 'Qoldiq: $qty';
  }

  @override
  String vcInvoiceSummary(int n, int known, int fresh) {
    return 'Qatorlar: $n · katalogda: $known · yangi: $fresh';
  }

  @override
  String vcOpenReceive(int n) {
    return 'Kirimni ochish ($n)';
  }

  @override
  String get vcReceiveEndsCall =>
      'Qo\'ng\'iroq tugaydi, suhbat chatda saqlanadi';

  @override
  String get vcNewItemsHint =>
      'Yangi tovarlar katalogda yo\'q — «Ombor → Nakladnoy» orqali qo\'shing';

  @override
  String get vcNoProcure => 'Kirimni administrator o\'tkazadi';

  @override
  String get vcShelfHint =>
      'SI ko\'z bilan chamalab sanaydi — reviziyadan oldin qayta tekshiring';

  @override
  String get vcShelfCols => 'ko\'rinadi · dasturda';

  @override
  String get vcShelfThing => 'tovar';

  @override
  String get vcOnTag => 'Narx yorlig\'ida';

  @override
  String get vcInProgram => 'Dasturda';

  @override
  String vcTagMismatch(String tag, String sum) {
    return 'Narx yorlig\'ida $tag so\'m, dasturda $sum so\'m — mos kelmaydi';
  }

  @override
  String get vcTagOk => 'Yorliqdagi narx dasturga mos';

  @override
  String get vcTagFixHint =>
      'Narx bu yerda o\'zgartirilmaydi — tovar kartasi orqali';

  @override
  String vcMoreLines(int n) {
    return 'yana $n ta qator';
  }

  @override
  String get vcNewTag => 'yangi';

  @override
  String get vcConsentTitle => 'Suhbatda kamerani yoqamizmi?';

  @override
  String get vcConsentBody =>
      'Kamera yoniq bo\'lganda kadrlar (soniyasiga taxminan 1 ta) do\'koningizning SI kaliti bilan Google Gemini\'ga ketadi. Ular telefonda ham, NurCRM\'da ham saqlanmaydi — chatda faqat suhbat matni qoladi. Bepul kalitda Google ma\'lumotlardan o\'z modellarini yaxshilash uchun foydalanishi mumkin. Xaridorlarning yuzini, bank kartalarini va begona hujjatlarni ko\'rsatmang.';

  @override
  String get vcConsentPtSends =>
      'Kamera yoniq bo\'lganda kadrlar (soniyasiga taxminan 1 ta) do\'koningizning SI kaliti bilan Google Gemini\'ga ketadi.';

  @override
  String get vcConsentPtSaved =>
      'Ular telefonda ham, NurCRM\'da ham saqlanmaydi — chatda faqat suhbat matni qoladi.';

  @override
  String get vcConsentPtFree =>
      'Bepul kalitda Google ma\'lumotlardan o\'z modellarini yaxshilash uchun foydalanishi mumkin.';

  @override
  String get vcConsentPtWarn =>
      'Xaridorlarning yuzini, bank kartalarini va begona hujjatlarni ko\'rsatmang.';

  @override
  String get vcConsentAgree => 'Kamerani yoqish';

  @override
  String get vcConsentLater => 'Hozir emas';

  @override
  String vcCost(int n) {
    return 'Video qo\'ng\'iroq $n daqiqagacha davom etadi va SI kaliti limitini ovozlidan bir necha barobar tez sarflaydi';
  }

  @override
  String get aiAccTitle => 'SI dan foydalanish';

  @override
  String get aiAccSection => 'SI dan kim foydalanishi mumkin';

  @override
  String aiAccLimitInfo(int n) {
    return 'Bepul SI limiti (Google Gemini) butun loyiha uchun umumiy: daqiqasiga va kuniga so\'rovlar kam. Shuning uchun SI dan — Nur Maslahatchi, qo\'ng\'iroq, aqlli kamera, rasm bo\'yicha nakladnoy, ko\'rinishi bo\'yicha tarozi — do\'konning ko\'pi bilan $n xodimi foydalana oladi. Kimga ruxsat berishni tanlang.';
  }

  @override
  String aiAccCounter(int a, int b) {
    return '$b tadan $a';
  }

  @override
  String aiAccLimitReached(int n) {
    return 'Ko\'pi bilan $n xodimni tanlash mumkin. Boshqasiga ruxsat berish uchun avval tanlanganlardan birining belgisini olib tashlang.';
  }

  @override
  String aiAccDenied(int n, String names) {
    return 'SI faqat do\'konning $n xodimiga ochiq: $names. Egasiga murojaat qiling.';
  }

  @override
  String get aiAccDeniedNone =>
      'Hozir do\'konda SI barcha xodimlar uchun o\'chirilgan. Egasiga murojaat qiling.';

  @override
  String get aiAccRecommend =>
      'SI ni ikki xodim bilan cheklashni tavsiya qilamiz (umumiy bepul limit)';

  @override
  String get aiAccConfigure => 'Sozlash';

  @override
  String get aiAccSaved => 'SI dan foydalanish saqlandi';

  @override
  String get aiAccOwnerTag => 'egasi';

  @override
  String get aiAccYouTag => 'siz';

  @override
  String get aiAccStaffNotLoaded =>
      'Xodimlar ro\'yxati olinmadi (aloqa yo\'qmi?). Faqat oldin tanlanganlar ko\'rsatildi.';

  @override
  String get aiAccNobody =>
      'Hech kim tanlanmadi — SI barcha xodimlar uchun o\'chiriladi.';

  @override
  String get aiAccManagerOnly =>
      'Ro\'yxatni egasi yoki administrator o\'zgartiradi.';

  @override
  String get aiAccNoticeTitle => 'SI — umumiy limit';

  @override
  String aiAccNoticeBody(int n) {
    return 'Do\'konning SI si Google\'ning umumiy bepul limitida ishlaydi, shuning uchun unga ko\'pi bilan $n xodimning huquqi bor — sizda bor. Uzoq qo\'ng\'iroqlar va keraksiz so\'rovlarni bekorga boshlamang.';
  }

  @override
  String get aiAccGotIt => 'Tushunarli';

  @override
  String get aiAccMineYes => 'Sizda SI dan foydalanish huquqi bor';

  @override
  String get aiAccMineNo =>
      'SI siz uchun mavjud emas — egasiga murojaat qiling';

  @override
  String get aiAccMineOpen => 'SI barcha xodimlarga ochiq (limit qo\'yilmagan)';

  @override
  String get aiAccNotSet =>
      'Belgilanmagan — SI barcha xodimlarda (egasi va administratorda — doim)';

  @override
  String get assistantTitle => 'Yordamchi';

  @override
  String get assistantSub => 'ovozli boshqaruv rejimi';

  @override
  String get aiModeDo => 'So\'rov bo\'yicha bajarish';

  @override
  String get aiModeDoHint =>
      'Ekranlarni ochadi, o\'qiydi, jadvallarni ko\'rsatadi va so\'raganingizni darhol bajaradi — natijani ovoz chiqarib aytadi, «Bekor qilish» tugmasi bilan.';

  @override
  String get aiModeAskHint =>
      'So\'rov bo\'yicha kabi, lekin to\'lov, qaytarish, kirim, hisobdan chiqarish, reviziya, smenani yopish, to\'lab berish, qarz, o\'chirish va narx o\'zgartirishdan oldin «Bajarish» kartasi chiqadi.';

  @override
  String get aiModeWatch => 'Faqat ko\'rish';

  @override
  String get aiModeWatchHint =>
      'Faqat ekranlar, o\'qish va jadvallar. Biror narsa qilishni so\'rasangiz: «Bu rejimda ma\'lumotni o\'zgartirmayman — ekranda bajaring yoki rejimni almashtiring» deb javob beradi.';

  @override
  String get aiModeDefault => 'odatiy';

  @override
  String get aiModeSofterLocked =>
      'Egasi belgilagandan yumshoqroq — mavjud emas';

  @override
  String get aiModeOwnerSet => 'Egasi shu telefondagi hamma uchun belgiladi:';

  @override
  String get aiModeStricterSelf =>
      'O\'zingiz uchun qattiqroq rejimni tanlashingiz mumkin';

  @override
  String get aiModeNoVoice => 'Rejim ovoz bilan o\'zgarmaydi — faqat shu yerda';

  @override
  String get aiModeAskWhatTitle => 'Nimadan oldin so\'raydi';

  @override
  String get aiModeAskNote =>
      '«Bajarish: … summa …» kartasi va bitta tugma — yoki ovoz bilan «ha». Ikki marta tasdiqlash yo\'q.';

  @override
  String get aiModeFooter =>
      'Yordamchi huquqlari — foydalanuvchi roli bo\'yicha';

  @override
  String get aiModeSaved => 'Rejim saqlandi';

  @override
  String get aiLogTitle => 'SI harakatlari';

  @override
  String get aiLogSubAll => 'telefondagi barcha yozuvlar';

  @override
  String get aiLogMoneyOnly => 'Faqat pul';

  @override
  String get aiLogUndone => 'Bekor qilinganlar';

  @override
  String get aiLogByDate => 'Sana bo\'yicha filtr';

  @override
  String get dayYesterday => 'Kecha';

  @override
  String aiLogUndoneAt(String time) {
    return 'bekor qilindi $time';
  }

  @override
  String get aiLogCantUndoWriteOff =>
      'bekor qilib bo\'lmaydi · hisobdan chiqarish mumkin';

  @override
  String get aiUndoRequest => 'bekor qilish — egasiga so\'rov bilan';

  @override
  String get aiLogUndoGone => 'bekor qilish faqat qo\'ng\'iroqda bo\'lgan';

  @override
  String aiLogUndoneToast(String what) {
    return 'Bekor qilindi: $what';
  }

  @override
  String get aiLogUndoConflict =>
      'SI dan keyin o\'zgargan — ekranda tekshiring';

  @override
  String aiLogUndoFailed(String what) {
    return 'Bekor qilib bo\'lmadi: $what. Aloqa va ekranni tekshiring.';
  }

  @override
  String get aiLogUndoShift =>
      'Bekor qilib bo\'lmaydi: smena yopilgan yoki kassada pul kam';

  @override
  String aiLogUndoPartial(int done, int total) {
    return '$total tadan $done tasi qaytarildi; qolganlari SI dan keyin o\'zgargan yoki bo\'lmadi';
  }

  @override
  String get aiLogEmpty => 'SI bu telefonda hali hech narsa qilmagan';

  @override
  String get aiLogEmptyHint =>
      'Nur Maslahatchiga qo\'ng\'iroq qilib, nima qilishni ayting — har bir yozuv «Bekor qilish» tugmasi bilan shu yerda paydo bo\'ladi';

  @override
  String get aiLogCall => 'Maslahatchiga qo\'ng\'iroq';

  @override
  String get aiLogMineOnly => 'Faqat o\'zingizning amallaringiz ko\'rinadi';

  @override
  String get aiLogFooter =>
      'Telefonda saqlanadi, 2000 tagacha yozuv. SI kalitlari, ovoz va mijoz telefonlarisiz.';

  @override
  String get aiGroupNav => 'Ekranlar';

  @override
  String get aiGroupCart => 'Chek va buyurtmalar';

  @override
  String get aiGroupMoney => 'Pul';

  @override
  String get aiGroupProduct => 'Tovarlar';

  @override
  String get aiGroupTask => 'Vazifalar';

  @override
  String get aiGroupSalary => 'Maosh';

  @override
  String get auditAi => 'SI';

  @override
  String get stfTasksTitle => 'Vazifalar';

  @override
  String stfTasksSub(int n, String late) {
    return 'Vazifalar: $n ta · muddati o\'tgan: $late ta';
  }

  @override
  String get stfMyTasksTitle => 'Mening vazifalarim';

  @override
  String stfMySub(String name, String today, String late) {
    return '$name · bugun uchun: $today ta · muddati o\'tgan: $late ta';
  }

  @override
  String get stfFilterOverdue => 'Muddati o\'tgan';

  @override
  String get stfFilterDone => 'Bajarilgan';

  @override
  String get stfStNew => 'yangi';

  @override
  String get stfStWork => 'jarayonda';

  @override
  String get stfStDone => 'bajarildi';

  @override
  String get stfOverdue => 'muddati o\'tdi';

  @override
  String get stfNoDue => 'muddatsiz';

  @override
  String get stfDue => 'Muddati';

  @override
  String get stfRemind => 'Eslatish';

  @override
  String get stfUrgent => 'Shoshilinch vazifa';

  @override
  String get stfNewTask => 'Yangi vazifa';

  @override
  String get stfTaskField => 'Nima qilish kerak';

  @override
  String get stfTaskHint => 'Masalan: suv javonini qayta sanang';

  @override
  String get stfAssignee => 'Ijrochi';

  @override
  String get stfAssigneeHint => 'Xodimni tanlang';

  @override
  String get stfCreate => 'Vazifa qo\'yish';

  @override
  String get stfNeedTitle => 'Nima qilish kerakligini yozing';

  @override
  String get stfNeedAssignee => 'Ijrochini tanlang';

  @override
  String get stfTaskCreated => 'Vazifa qo\'yildi';

  @override
  String get stfToWork => 'Ishga olish';

  @override
  String get stfMarkDone => 'Bajarildi';

  @override
  String get stfReopen => 'Qayta ochish';

  @override
  String get stfEditDue => 'Muddatni o\'zgartirish';

  @override
  String get stfEditAssignee => 'Boshqasiga berish';

  @override
  String get stfDelete => 'O\'chirish';

  @override
  String stfDeleteAsk(String title) {
    return '«$title» vazifasini o\'chiramizmi?';
  }

  @override
  String get stfNoTasks => 'Vazifalar yo\'q';

  @override
  String get stfLoadFail => 'Vazifalarni yuklab bo\'lmadi';

  @override
  String get stfPriorityHint =>
      'Shoshilinchlik va eslatma hozircha faqat ilovada ko\'rinadi; server yangilanguncha saytda topshiriqda xizmat belgisi chiqishi mumkin';

  @override
  String get stfNoBackground =>
      'Fon bildirishnomalari yo\'q — eslatma ilova ochilganda ko\'rinadi';

  @override
  String stfOverduePlate(int n, String title) {
    return 'Muddati o\'tgan: $n ta — «$title»';
  }

  @override
  String stfRemindPlate(String title) {
    return 'Eslatma: «$title»';
  }

  @override
  String stfTodayPlate(String today, String late) {
    return 'Bugungi vazifalar: $today ta · muddati o\'tgan: $late ta';
  }

  @override
  String get stfSalaryTitle => 'Maosh';

  @override
  String stfSalarySub(int n) {
    return 'xodimlar: $n ta';
  }

  @override
  String get stfPeriodMonth => 'Shu oy';

  @override
  String get stfPeriodLast => 'O\'tgan oy';

  @override
  String get stfPeriodWeek => '7 kun';

  @override
  String get stfAccrued => 'Hisoblandi';

  @override
  String get stfPaid => 'To\'landi';

  @override
  String get stfLeft => 'Qoldi';

  @override
  String get stfColEmployee => 'Xodim';

  @override
  String get stfChecking => 'To\'lovlar tekshirilmoqda…';

  @override
  String get stfPartialPlate =>
      'Qisman tekshirildi — to\'lov summasi o\'zi qo\'yilmaydi.';

  @override
  String get stfTrustTimeout =>
      'O\'tgan to\'lovlarni tekshirish o\'z vaqtida tugamadi — to\'lashdan oldin saytda tekshiring.';

  @override
  String get stfTrustJournal =>
      'Telefondagi to\'lovlar jurnali o\'qilmaydi — to\'lovlar yopiq.';

  @override
  String get stfTrustNotChecked =>
      'O\'tgan to\'lovlarni tekshirib bo\'lmadi — to\'lashdan oldin saytda tekshiring.';

  @override
  String stfTrustUnassigned(String sum, int n) {
    return 'Xodimga bog\'lanmagan «Maosh» xarajatlari bor: $sum so\'m ($n ta) — saytda tekshiring.';
  }

  @override
  String get stfTrustPartial =>
      '«Maosh» moddasiz to\'lovlar yoki barcha xarajatlar ko\'rinmaydi — to\'lashdan oldin saytda tekshiring.';

  @override
  String get stfTrustPartialNotes =>
      'To\'lovlarning bir qismi davrga faqat qisman kirdi — ayrilmadi, tekshiring.';

  @override
  String get stfTrustByName =>
      'To\'lovlarning bir qismi faqat xarajat nomidagi ism bo\'yicha topildi — to\'lashdan oldin saytda tekshiring.';

  @override
  String stfPartialNote(String sum, String period) {
    return '$period davri uchun $sum so\'m to\'lov bu davrga faqat qisman kiradi — ayrilmadi';
  }

  @override
  String get stfSalaryEmpty =>
      'Davr uchun hisob-kitob yo\'q: sotuv yo\'q yoki hech kimda to\'lov sxemasi sozlanmagan.';

  @override
  String get stfSalaryDenied =>
      'Maoshga ruxsat yo\'q: bu akkauntda saytdagi xodimlar tahlili huquqi yo\'q.';

  @override
  String get stfSalaryFail => 'Maoshni yuklab bo\'lmadi';

  @override
  String get stfSalaryHint =>
      'Hisoblangan — server hisobi, kompyuter va saytdagidek. «To\'landi» — shu davrdagi «Maosh» xarajatlari.';

  @override
  String get stfLockedTag => 'javobsiz to\'lov';

  @override
  String get stfPayBtn => 'To\'lash';

  @override
  String stfPayTitle(String name) {
    return 'To\'lov: $name';
  }

  @override
  String get stfKindSalary => 'Maosh';

  @override
  String get stfKindAdvance => 'Avans';

  @override
  String get stfPayAmount => 'Summa';

  @override
  String get stfPayEnterAmount => 'Summani o\'zingiz kiriting';

  @override
  String get stfPayBox => 'Kassa';

  @override
  String get stfPayComment => 'Izoh (majburiy emas)';

  @override
  String get stfPayNeedAmount => 'To\'lov summasini kiriting';

  @override
  String get stfPayNoCash =>
      'To\'lov uchun kompaniya kassasi topilmadi — saytda yoki egasi dasturida to\'lang.';

  @override
  String get stfSalesBoxWarn =>
      'Bu filialning sotuv kassasi — xarajat uning qoldig\'ini kamaytiradi.';

  @override
  String stfPayConfirm(String sum, String name, String box) {
    return '$name xodimiga «$box» kassasidan $sum so\'m to\'lansinmi?';
  }

  @override
  String stfPayDone(String sum, String name) {
    return 'To\'landi $sum so\'m: $name';
  }

  @override
  String get stfPayFailed => 'To\'lov o\'tmadi.';

  @override
  String stfPayUnknown(String sum, String box) {
    return 'Serverdan javob kelmadi — $sum so\'m to\'lov («$box» kassasi) o\'tgan bo\'lishi mumkin. Takrorlab bo\'lmaydi: xodimni ochib «Serverda tekshirish»ni bosing.';
  }

  @override
  String stfPayLock(String sum, String when, String box) {
    return 'Oldingi $sum so\'m to\'lov ($when, «$box» kassasi) serverdan javobsiz qoldi. Siz tekshirmaguncha bu xodimga yangi to\'lov yopiq.';
  }

  @override
  String get stfPayLockShort =>
      'Bu xodimga oldingi to\'lov serverdan javobsiz qoldi — siz tekshirmaguncha yangisi yopiq.';

  @override
  String get stfPayDup =>
      'Xuddi shunday to\'lov hozirgina o\'tdi — takrorlanmaydi.';

  @override
  String get stfPayJournal =>
      'Telefondagi to\'lovlar jurnali o\'qilmaydi — usiz ikki marta to\'lashdan himoya yo\'q, to\'lov yuborilmadi.';

  @override
  String get stfLockCheck => 'Serverda tekshirish';

  @override
  String get stfLockCheckShort => 'Tekshirish';

  @override
  String get stfLockYes => 'To\'lov saytda BOR';

  @override
  String get stfLockNo => 'To\'lov saytda YO\'Q';

  @override
  String get stfLockYesAsk => 'To\'lov saytda bormi?';

  @override
  String get stfLockNoAsk => 'To\'lov saytda yo\'qmi?';

  @override
  String get stfLockResolveHint =>
      'NurCRM saytidagi «Maosh» xarajatini tekshiring. Noto\'g\'ri bossangiz, to\'lov noto\'g\'ri hisoblanadi yoki ikki marta o\'tadi.';

  @override
  String get stfLockFound => 'To\'lov serverda topildi va hisobga olindi.';

  @override
  String get stfLockNotFound =>
      'Serverda to\'lov topilmadi — qulf qoladi, saytda tekshirgandan keyin o\'zingiz hal qiling.';

  @override
  String get stfConfirm => 'Tasdiqlash';

  @override
  String get stfRecentPayouts => 'Shu telefondan so\'nggi to\'lovlar';

  @override
  String get stfCancelBtn => 'Bekor qilishni so\'rash';

  @override
  String stfCancelAsk(String sum) {
    return '$sum so\'m to\'lovni bekor qilishni so\'raymizmi?';
  }

  @override
  String get stfCancelHint =>
      'To\'lovni faqat egasi yoki buxgalter NurCRM saytida bekor qiladi — telefondan so\'rov yuboriladi.';

  @override
  String get stfCancelSend => 'So\'rov yuborish';

  @override
  String get stfCancelSent => 'Bekor qilish so\'rovi egasiga yuborildi.';

  @override
  String get stfSchemeBtn => 'To\'lov sxemasi';

  @override
  String stfSchemeTitle(String name) {
    return 'To\'lov sxemasi: $name';
  }

  @override
  String get stfSchemeSaved => 'To\'lov sxemasi saqlandi';

  @override
  String get stfSchemeSalary => 'Maosh';

  @override
  String get stfSchemePercent => 'Sotuvdan foiz';

  @override
  String get stfSchemeSalaryPercent => 'Maosh + sotuvdan foiz';

  @override
  String get stfSchemePerItem => 'Har bir sotilgan mahsulot uchun';

  @override
  String get stfSchemeSalaryItem => 'Maosh + har bir mahsulot uchun';

  @override
  String get stfSchemePercentItem => 'Foiz + har bir mahsulot uchun';

  @override
  String get stfNoScheme => 'sxema sozlanmagan';

  @override
  String get stfMonthly => 'Oylik maosh';

  @override
  String get stfPercent => 'Sotuvdan foiz';

  @override
  String get stfPerItem => 'Har bir sotilgan mahsulot uchun';

  @override
  String get stfBonus => 'Sotuv uchun hisoblandi';

  @override
  String get gkOpen => 'Gemini kalitlari (bir nechta akkaunt)';

  @override
  String get gkTitle => 'Gemini kalitlari';

  @override
  String get gkHint =>
      'Kalitlar navbat bilan ishlatiladi: birinchi ishlaydigani. Kalitning limiti tugasa, SI keyingisini o\'zi oladi. Ro\'yxat kassa va egasi dasturidagi bilan bir xil.';

  @override
  String get gkWarn =>
      'Google qoidalari limitlarni bir nechta akkaunt yoki loyiha bilan aylanib o\'tishni taqiqlashi mumkin — kalitlar bloklanishi xavfi bor. Google bepul darajasi so\'rovlarni o\'z mahsulotlarini yaxshilash uchun ishlatishi mumkin. Qaror sizda.';

  @override
  String get gkViewOnly =>
      'Kalitlar ro\'yxatini egasi va administrator o\'zgartiradi. Sizga kalitlar to\'liq ko\'rsatilmaydi — SI shu ro\'yxat bilan ishlaydi.';

  @override
  String gkWorkingCount(int a, int b) {
    return 'Ishlaydigan kalitlar: $a / $b';
  }

  @override
  String get gkEmpty =>
      'Kalit yo\'q. Birinchisini qo\'shing — uni Google AI Studio beradi.';

  @override
  String get gkAdd => 'Kalit qo\'shish';

  @override
  String get gkAddBtn => 'Qo\'shish';

  @override
  String get gkPasteKey => 'Google AI Studio kalitini qo\'ying';

  @override
  String get gkLabelHint => 'Belgi (ixtiyoriy)';

  @override
  String gkLabelDefault(int n) {
    return 'Akkaunt $n';
  }

  @override
  String get gkMax => '10 tadan ko\'p emas.';

  @override
  String get gkDuplicate => 'Bunday kalit ro\'yxatda bor.';

  @override
  String get gkShow => 'Ko\'rsatish';

  @override
  String get gkHide => 'Yashirish';

  @override
  String get gkEnabled => 'Yoqilgan';

  @override
  String get gkMoveUp => 'Yuqoriga';

  @override
  String get gkMoveDown => 'Pastga';

  @override
  String get gkDelete => 'O\'chirish';

  @override
  String gkDeleteAsk(String name, String tail) {
    return '«$name» kalitini (…$tail) o\'chiramizmi?';
  }

  @override
  String get gkStateWorking => 'ishlaydi';

  @override
  String gkStateExhausted(String time) {
    return '$time gacha tugagan';
  }

  @override
  String get gkStateInvalid => 'yaroqsiz';

  @override
  String get gkStateDisabled => 'o\'chiq';

  @override
  String get gkStateUnchecked => 'tekshirilmagan';

  @override
  String get gkCheckOk => 'Kalit ishlaydi ✓';

  @override
  String gkCheckFail(String err) {
    return 'Tekshirib bo\'lmadi: $err';
  }

  @override
  String get aiKeysSavedPhoneOnly =>
      'Telefonda saqlandi, lekin bulutga ketmadi — kassa va egasi dasturi hozircha ko\'rmaydi. Keyinroq qayta urinib ko\'ring.';

  @override
  String get klTitle => 'Kassa bilan aloqa';

  @override
  String get klProfileOff => 'Ulanmagan';

  @override
  String klKassaName(String name) {
    return '«$name» kassasi';
  }

  @override
  String get klScanQr => 'Kassadagi QR-kodni skanerlash';

  @override
  String get klScanQrHint =>
      'Kassa kompyuterida «Telefonni ulash» oynasini oching va kamerani QR-kodga qarating. Telefon va kassa bir Wi-Fi tarmog\'ida bo\'lishi, kassa esa ishlab turishi kerak.';

  @override
  String get klScanQrTitle => 'Kamerani kassa ekranidagi QR-kodga qarating';

  @override
  String get klNotKassaQr => 'Bu NurMarket kassasining QR-kodi emas';

  @override
  String get klBadQr =>
      'QR-kodni o\'qib bo\'lmadi. Kassada «Telefonni ulash» oynasini ochib, qayta skanerlang.';

  @override
  String get klQrVersion =>
      'Bu kassa ilovadan yangiroq. Telefondagi NurMarket\'ni yangilang.';

  @override
  String get klManual => 'Havolani qo\'lda kiritish';

  @override
  String get klManualHint => 'QR-kod matnini qo\'ying (nurmarket-link:…)';

  @override
  String get klConnect => 'Ulash';

  @override
  String klConnected(String name) {
    return '«$name» kassasi ulandi';
  }

  @override
  String get klConnectedPlain => 'Kassa ulandi';

  @override
  String get klStUnpaired => 'Kassa ulanmagan';

  @override
  String get klStUnknown => 'Aloqa tekshirilmagan';

  @override
  String get klStOnline => 'Aloqada';

  @override
  String get klStNotFound => 'Kassa bu tarmoqda topilmadi';

  @override
  String get klStRevoked => 'Telefon kassada o\'chirilgan';

  @override
  String get klStBadTime => 'Telefondagi vaqtni tekshiring';

  @override
  String get klStDisabled => 'Kassada telefonlardan qabul qilish o\'chirilgan';

  @override
  String get klStRePair =>
      'Aloqa eskirgan — kassadagi QR-kodni qayta skanerlang';

  @override
  String get klChecking => 'Aloqa tekshirilmoqda…';

  @override
  String get klShiftOpen => 'Kassadagi smena: ochiq';

  @override
  String get klShiftClosed => 'Kassadagi smena: yopiq';

  @override
  String get klCheckNow => 'Aloqani tekshirish';

  @override
  String get klDisconnect => 'Uzish';

  @override
  String klDisconnectAsk(String name) {
    return 'Telefonni «$name» kassasidan uzamizmi? QR-kod bilan qayta ulash mumkin. Kassaning o\'zida telefon ro\'yxatda qoladi — uni o\'sha yerdan ham olib tashlang.';
  }

  @override
  String get klDisconnected => 'Telefon kassadan uzildi';

  @override
  String get klScannerMode => 'Kassa uchun skaner';

  @override
  String get klScannerHint =>
      'Shtrix-kodga qarating — mahsulot to\'g\'ridan-to\'g\'ri kassadagi chekka tushadi';

  @override
  String get klSetupAsk => 'Kassa bilan aloqa sozlanmagan';

  @override
  String get klSetup => 'Sozlash';

  @override
  String get klScSending => 'Yuborilmoqda…';

  @override
  String get klScAdded => 'Qo\'shildi';

  @override
  String get klScNotFound => 'Topilmadi';

  @override
  String get klScTillInactive => 'Kassa oynasi ochiq emas';

  @override
  String get klScQueued => 'Aloqa yo\'q — navbatda';

  @override
  String get klScQueueFull => 'Navbat to\'ldi — kod qabul qilinmadi';

  @override
  String get klScExpired => 'Yuborilmadi: kassa javob bermadi';

  @override
  String get klScFailed => 'Kassa kodni qabul qilmadi';

  @override
  String get klLastCode => 'Oxirgi kod';

  @override
  String klCounter(int n) {
    return 'Chekka qo\'shildi: $n';
  }

  @override
  String klQueueLeft(int n) {
    return 'Navbatda: $n';
  }

  @override
  String get globalSource => 'NurCRM ma\'lumotnomasidan';

  @override
  String globalFoundAsk(String name) {
    return 'NurCRM ma\'lumotnomasida bor: $name';
  }

  @override
  String get globalCatalogTitle => 'Mahsulotlar ma\'lumotnomasi';

  @override
  String globalCatalogStatus(String count, String date, String added) {
    return 'Ma\'lumotnoma: $count mahsulot, yangilangan $date, qo\'shildi: $added';
  }

  @override
  String get globalCatalogOff => 'Ma\'lumotnoma mavjud emas';

  @override
  String globalBulkFilled(int n) {
    return 'NurCRM ma\'lumotnomasidan qo\'yilgan nomlar: $n';
  }

  @override
  String advisorQuota(String time) {
    return 'SI limiti soat $time gacha tugadi (kassa va bot bilan umumiy). Boshqa Google akkauntidan ikkinchi kalit qo\'shishingiz mumkin.';
  }

  @override
  String get advisorQuotaLater =>
      'SI limiti tugadi (kassa va bot bilan umumiy). Keyinroq urinib ko\'ring yoki boshqa Google akkauntidan ikkinchi kalit qo\'shing.';

  @override
  String advisorKeyRejected(String time) {
    return 'Google kalitni rad etdi, $time dan keyin qayta uriniladi. Kalitni «SI sozlamalari» → «Gemini kalitlari» bo\'limida tekshiring.';
  }

  @override
  String get advisorTimeout =>
      'SI uzoq vaqt javob bermayapti. Internetni tekshirib, «Qayta urinish»ni bosing.';

  @override
  String gkLastError(String text) {
    return 'Oxirgi xato: $text';
  }

  @override
  String get aiAccOwnerNote =>
      'Siz ro\'yxatda yo\'qsiz, lekin egasi va administrator SI dan doim foydalana oladi.';

  @override
  String get pePriceCheckTitle => 'Narxni tekshiring';

  @override
  String pePriceUpAsk(String now, String was, String times) {
    return 'Narx $was o\'rniga $now — saqlansinmi?\nAvvalgisidan $times baravar ko\'p.';
  }

  @override
  String pePriceDownAsk(String now, String was, String times) {
    return 'Narx $was o\'rniga $now — saqlansinmi?\nAvvalgisidan $times baravar kam.';
  }

  @override
  String peBelowCostAsk(String now, String cost) {
    return 'Narx $now xarid narxi $cost dan past — zararga sotish. Saqlansinmi?';
  }

  @override
  String peMarkupAsk(String now, String cost, String pct) {
    return 'Narx $now, xarid $cost: ustama $pct %. Raqamlarda xato yo\'qmi? Saqlansinmi?';
  }

  @override
  String get peFixPrice => 'Tuzatish';

  @override
  String get peSaveAsIs => 'Shunday saqlash';

  @override
  String get klSearching => 'Kassa Wi-Fi tarmog\'ida qidirilmoqda…';

  @override
  String get klHelpNotFound =>
      'Kassa javob bermayapti. Tekshiring: 1) kassa ishlayaptimi; 2) kassa sozlamalarida «Telefonlardan skanerlashni qabul qilish» yoqilganmi (odatda o\'chiq); 3) telefon kassali kompyuter bilan bir Wi-Fi tarmog\'idami, mobil internetda emas.';

  @override
  String get klHelpDisabled =>
      'Kassada telefonlardan qabul o\'chiq. Kassa sozlamalarida «Telefonlardan skanerlashni qabul qilish»ni yoqib, «Aloqani tekshirish»ni bosing.';

  @override
  String get klHelpRePair =>
      'Kassa bu telefonni tanimayapti (u uzilgan yoki QR eskirgan). «QR-kodni qayta skanerlash»ni bosib, kameraini kassadagi «Telefonni ulash» oynasidagi QR-kodga qarating.';

  @override
  String get klHelpBadTime =>
      'Telefon va kassa vaqti o\'rtasida 10 daqiqadan ortiq farq bor. Telefonda sana va vaqtni avtomatik qiling (kassadagi vaqtni ham tekshiring), keyin «Aloqani tekshirish»ni bosing.';

  @override
  String get klRescanQr => 'QR-kodni qayta skanerlash';

  @override
  String get klScRefused =>
      'Kod yuborilmadi — avval kassa bilan aloqani tiklang';

  @override
  String get aiAccDraftNote =>
      'Ro\'yxat hali saqlanmagan: hozir SI barcha xodimlarga ochiq. 2 tagacha odamni belgilab, «Saqlash»ni bosing — qolganlarda SI o\'chadi. Egasi va administrator SI dan doim foydalana oladi.';

  @override
  String get aiAccAlways =>
      'Egasi va administrator ro\'yxatdan qat\'i nazar SI dan doim foydalana oladi.';

  @override
  String get invTitle => 'Nakladnoyni yuklash';

  @override
  String get invSub => 'Yetkazib beruvchidan Excel, CSV yoki XML';

  @override
  String get invHint =>
      'Yetkazib beruvchining nakladnoy faylini tanlang: Excel (.xlsx), CSV yoki XML (elektron nakladnoy, hisob-faktura). Satrlar kirimga tushadi — miqdor va narxlarni tekshirib, odatdagidek o\'tkazasiz.';

  @override
  String get invPick => 'Nakladnoy faylini tanlash';

  @override
  String get invPhotoHint =>
      'PDF va nakladnoy suratini «Aqlli kamera» o\'qiydi (AI kaliti kerak)';

  @override
  String get invPhotoBtn => 'Surat bo\'yicha nakladnoy';

  @override
  String get invReading => 'Nakladnoy o\'qilmoqda…';

  @override
  String invFileName(String name) {
    return 'Fayl: $name';
  }

  @override
  String get invOtherFile => 'Boshqa fayl';

  @override
  String invDocLine(String num, String date) {
    return '$date dagi nakladnoy № $num';
  }

  @override
  String invDocNum(String num) {
    return 'Nakladnoy № $num';
  }

  @override
  String invDocDate(String date) {
    return '$date dagi nakladnoy';
  }

  @override
  String get invDocNone => 'Faylda raqam va sana topilmadi';

  @override
  String invSupplierFile(String name) {
    return 'Faylda: $name';
  }

  @override
  String invSupplierFileInn(String name, String inn) {
    return 'Faylda: $name, STIR $inn';
  }

  @override
  String invSupplierInnOnly(String inn) {
    return 'Fayldagi STIR: $inn';
  }

  @override
  String get invSupplierNoneInFile =>
      'Faylda yetkazib beruvchi ko\'rsatilmagan';

  @override
  String get invSupByInn => 'STIR bo\'yicha topildi';

  @override
  String get invSupByName => 'Nomi bo\'yicha topildi';

  @override
  String get invSupNotFound =>
      'Bunday yetkazib beruvchi yo\'q — ro\'yxatdan tanlang yoki yarating';

  @override
  String get invSupCreate => 'Yetkazib beruvchi yaratish';

  @override
  String invSupCreated(String name) {
    return '«$name» yetkazib beruvchisi yaratildi';
  }

  @override
  String invSupFail(String text) {
    return 'Yetkazib beruvchi yaratilmadi: $text';
  }

  @override
  String get invSupOffline =>
      'Aloqa yo\'q: yetkazib beruvchilar ro\'yxati yuklanmadi';

  @override
  String get invSupKept => 'Kirim avvalgi yetkazib beruvchida qoldi';

  @override
  String get invDupPosted => 'Bu nakladnoy avval yuklangan va o\'tkazilgan';

  @override
  String get invDupPending =>
      'Bu nakladnoy hali o\'tkazilmagan kirimga qo\'shilgan';

  @override
  String get invDupAnyway => 'Baribir qo\'shish';

  @override
  String invTotalLines(String sum) {
    return 'Satrlar bo\'yicha: $sum';
  }

  @override
  String invTotalFile(String sum) {
    return 'Faylda: $sum';
  }

  @override
  String get invTotalOk => 'Jamilar mos';

  @override
  String invTotalBad(String diff) {
    return 'Jamilar mos emas: farq $diff. Miqdor va narxlarni tekshiring.';
  }

  @override
  String get invTotalNone => 'Faylda jami topilmadi';

  @override
  String get invTotalVatHint =>
      'QQS bilan jamilar mos — «QQS bilan xarid»ni yoqing';

  @override
  String get invVat => 'QQS bilan xarid';

  @override
  String get invVatSub => 'Odatda xarid QQS\'siz olinadi';

  @override
  String invCurrency(String cur) {
    return 'Nakladnoy valyutasi: $cur. Siz so\'mda ishlaysiz — narxlarni tekshiring: dastur qayta hisoblamaydi.';
  }

  @override
  String get invGenCodes => 'Shtrix-kodsiz yangi tovarlarga o\'z kodini berish';

  @override
  String get invGenCodesSub => 'Aks holda ular shtrix-kodsiz bo\'ladi';

  @override
  String invSummary(int n, String stock, String fresh, String bad) {
    return 'Satrlar: $n · omborda: $stock · yangi: $fresh · taniylmadi: $bad';
  }

  @override
  String get invStBarcode => 'Omborda · shtrix-kod bo\'yicha';

  @override
  String get invStArticle => 'Omborda · artikul bo\'yicha';

  @override
  String get invStName => 'Omborda · nomi bo\'yicha';

  @override
  String invStFuzzy(String name) {
    return 'O\'xshash: $name';
  }

  @override
  String get invStCatalog => 'Yangi · NurCRM ma\'lumotnomasida bor';

  @override
  String get invStManual => 'Yangi · qo\'lda to\'ldiring';

  @override
  String get invStBad => 'Taniylmadi';

  @override
  String get invBadNoName => 'Nomi, shtrix-kodi va artikuli yo\'q';

  @override
  String get invBadNoQty => 'Miqdori yo\'q';

  @override
  String invUseCandidate(String name) {
    return 'Bu «$name»';
  }

  @override
  String get invAsNew => 'Yangi tovar';

  @override
  String invSrcRow(int n) {
    return 'Fayl satri: $n';
  }

  @override
  String invArticle(String code) {
    return 'Artikul: $code';
  }

  @override
  String get invCostMissing => 'Xarid narxini kiriting';

  @override
  String get invPriceMissing => 'Sotuv narxini kiriting';

  @override
  String get invRemoveRow => 'Satrni olib tashlash';

  @override
  String invAdd(int n) {
    return 'Kirimga qo\'shish · $n';
  }

  @override
  String get invAddFix => 'Belgilangan satrlarni tuzating';

  @override
  String get invAdding => 'Satrlar tayyorlanmoqda…';

  @override
  String invMerged(int n) {
    return 'Nakladnoydagi takroriy tovarlar birlashtirildi: $n';
  }

  @override
  String get invErrTooBig => 'Fayl 10 MB dan katta — ochib bo\'lmaydi';

  @override
  String get invErrEmpty => 'Fayl bo\'sh';

  @override
  String get invErrPdf =>
      'Bu PDF yoki surat. Ularni «Aqlli kamera» o\'qiydi (pastdagi tugma)';

  @override
  String get invErrXls =>
      'Eski Excel formati (.xls) o\'qilmaydi — faylni .xlsx yoki .csv qilib saqlang';

  @override
  String get invErrBroken => 'Fayl buzilgan yoki bu nakladnoy emas';

  @override
  String get invErrNoTable =>
      'Tovarlar jadvali topilmadi: «Nomi», «Miqdori» va «Narxi» ustunlari kerak';

  @override
  String get invErrUnsafe =>
      'XML da DTD yoki tashqi obyektlar bor — xavfsizlik uchun bunday fayl ochilmaydi';

  @override
  String get invErrTooMany => 'Satrlar juda ko\'p (5000 dan ortiq)';

  @override
  String get invErrFormat =>
      'Qo\'llab-quvvatlanmaydigan fayl: .xlsx, .csv yoki .xml kerak';

  @override
  String get tsTitle => 'Sinovchi rejimi';

  @override
  String tsBuildLine(String version, String build) {
    return 'Versiya $version (yig\'ma $build)';
  }

  @override
  String get tsTestBuild => 'sinov yig\'masi';

  @override
  String get tsDevice => 'Qurilma';

  @override
  String tsDeviceId(String id) {
    return 'Qurilma ID: $id';
  }

  @override
  String get tsDeviceNameTitle => 'Qurilma nomi';

  @override
  String get tsDeviceNameHint => 'Masalan: kassirning telefoni';

  @override
  String get tsRename => 'Qurilmaga nom berish';

  @override
  String tsScreenLine(String w, String h, String scale, String lang) {
    return 'Ekran $w×$h dp, shrift ×$scale, til $lang';
  }

  @override
  String tsSummary(String done, String total, String issues) {
    return 'Tekshirildi $done / $total, muammo: $issues';
  }

  @override
  String get tsVersionFilter => 'Versiya';

  @override
  String tsVersionCurrent(String v) {
    return '$v (joriy)';
  }

  @override
  String tsAreaProgress(String done, String total) {
    return '$done / $total';
  }

  @override
  String get tsChanged => 'Nima o\'zgardi';

  @override
  String get tsHowTo => 'Qanday tekshirish';

  @override
  String get tsSevMoney => 'Pul';

  @override
  String get tsSevImportant => 'Muhim';

  @override
  String get tsSevNormal => 'Oddiy';

  @override
  String get tsMarkOk => 'Tekshirildi';

  @override
  String get tsMarkIssue => 'Muammo bor';

  @override
  String get tsNotChecked => 'Tekshirilmagan';

  @override
  String tsCheckedAt(String when) {
    return 'Tekshirildi $when';
  }

  @override
  String tsIssueAt(String when) {
    return 'Muammo $when';
  }

  @override
  String get tsNoComment => 'Izohsiz';

  @override
  String get tsUndoHint => 'Belgini olib tashlash uchun yana bosing';

  @override
  String get tsCountNone => 'Hozircha hech bir qurilmada tekshirilmagan';

  @override
  String get tsCountLocalOk => 'Tekshirildi: 1 qurilma (shu)';

  @override
  String get tsCountLocalIssue => 'Muammo: 1 qurilma (shu)';

  @override
  String tsCountShared(String ok, String issue) {
    return 'Tekshirilgan qurilmalar: $ok · muammoli qurilmalar: $issue';
  }

  @override
  String get tsSyncOff =>
      'Umumiy hisoblagich mavjud emas (huquq yoki server kerak)';

  @override
  String get tsRefresh => 'Hisoblagichlarni yangilash';

  @override
  String get tsCopyReport => 'Hisobotni nusxalash';

  @override
  String get tsShareReport => 'Hisobotni yuborish';

  @override
  String get tsCopied => 'Hisobot nusxalandi — xabarga qo\'ying';

  @override
  String get tsIssueTitle => 'Nima noto\'g\'ri?';

  @override
  String get tsIssueHint => 'Muammoni tasvirlang';

  @override
  String get tsAttachShot => 'Skrinshot biriktirish';

  @override
  String get tsShotAttached => 'Skrinshot biriktirildi';

  @override
  String get tsShotRemove => 'Skrinshotni olib tashlash';

  @override
  String get tsEditComment => 'Izohni o\'zgartirish';

  @override
  String get tsEmpty => 'Bu versiya uchun bandlar yo\'q';

  @override
  String get tsCodeTitle => 'Sinovchi kodi';

  @override
  String get tsCodeHint => 'Kodni kiriting';

  @override
  String get tsCodeOn => 'Sinovchi rejimi yoqildi: logotipni 5 marta bosing';

  @override
  String get tsCodeBad => 'Kod mos kelmaydi';

  @override
  String tsTapMore(int n) {
    return 'Sinovchi rejimi: qolgan bosishlar — $n';
  }

  @override
  String get pdNoRights =>
      'Mahsulotlarni «Ombor» huquqiga ega xodim yoki administrator o\'chira oladi.';

  @override
  String get pdGone =>
      'Mahsulot serverda allaqachon yo\'q — telefondan ham olib tashladim.';

  @override
  String pdRefused(String msg) {
    return 'Server mahsulotni o\'chirishga ruxsat bermadi: $msg. Agar u sotuvlar yoki hujjatlarda bo\'lsa, o\'chirish o\'rniga qoldiqni hisobdan chiqaring.';
  }

  @override
  String get pdOffline =>
      'Server bilan aloqa yo\'q — mahsulot o\'chirilmadi. Tarmoq paydo bo\'lganda qayta urinib ko\'ring.';

  @override
  String pdFailed(String msg) {
    return 'Mahsulot o\'chirilmadi: $msg';
  }

  @override
  String pdQueueBlock(int n) {
    return 'Bu mahsulot bo\'yicha yuborilmagan amallar bor: $n. Yuborilishini kutib, qayta urinib ko\'ring.';
  }

  @override
  String get pdAuditFail => 'O\'chirilmadi';

  @override
  String pdAsk(String name) {
    return '«$name» o\'chirilsinmi? Mahsulot NurCRM katalogidan, kassadan va ilovadan yo\'qoladi. Buni bekor qilib bo\'lmaydi.';
  }

  @override
  String pdStockWarn(String qty, String unit) {
    return 'Omborda $qty $unit bor. Qoldiq mahsulot bilan birga yo\'qoladi. Mahsulot kerak bo\'lmasa, avval qoldiqni hisobdan chiqaring.';
  }

  @override
  String get pdTypeName => 'Tasdiqlash uchun mahsulot nomini yozing:';

  @override
  String get pdTypeHint => 'Katta-kichik harf muhim emas';

  @override
  String get ctTitle => 'Aqlli kamera';

  @override
  String get ctAngleFront => 'oldidan';

  @override
  String get ctAngleBack => 'orqasidan';

  @override
  String get ctAngleSide => 'yonidan';

  @override
  String ctCount(int n, int a) {
    return 'Namunalar: $n / $a';
  }

  @override
  String get ctNone =>
      'O\'rgatilmagan: mahsulot rasm bo\'yicha hali aniq tanilmaydi';

  @override
  String get ctTrained =>
      'O\'rgatilgan: sotuvda mahsulot shu rasmlar bo\'yicha taniladi';

  @override
  String get ctTrain => 'Kamerani o\'rgatish';

  @override
  String get ctReset => 'Namunalarni tozalash';

  @override
  String get ctResetAsk =>
      'Bu mahsulotning barcha namuna rasmlari telefondan olib tashlansinmi? NurCRM\'dagi rasmlar qoladi.';

  @override
  String get ctSaved => 'Namuna saqlandi';

  @override
  String get ctRemoved => 'Namuna olib tashlandi';

  @override
  String ctShotHint(String angle) {
    return 'Mahsulot butunlay ramkada, $angle';
  }

  @override
  String get ctBarcodeNone => 'Shtrix-kod yo\'q — skanerlang';

  @override
  String ctBarcodeOk(String code) {
    return 'Shtrix-kod: $code';
  }

  @override
  String get ctNote =>
      'Rasmlar shu telefonda saqlanadi. «Kamerani o\'rgatish» — 5 tagacha rasm va shtrix-koddan iborat to\'liq kurs; u mahsulot rasmini NurCRM\'ga ham qo\'shadi.';

  @override
  String ctSlot(int n) {
    return 'Rasm $n';
  }

  @override
  String ctSlotAdd(int n, String angle) {
    return 'Rasm $n: $angle olish';
  }

  @override
  String ctSlotRetake(int n) {
    return 'Rasm $n: qayta olish';
  }

  @override
  String get ctRemove => 'Olib tashlash';

  @override
  String get camPickTitle => 'O\'xshash — shu bo\'lsa, tanlang';

  @override
  String get camFromGallery => 'Galereyadan';

  @override
  String get camFromFile => 'Fayldan';

  @override
  String get camInvoiceSrcHint =>
      'Hisob-fakturani suratga olish, galereya yoki fayldan tanlash mumkin: rasm, PDF, Excel, CSV, XML.';

  @override
  String camPageOf(int n, int a) {
    return 'Rasm $n / $a';
  }

  @override
  String get camFileNoProcure =>
      'Hisob-faktura faylini yuklash «Xaridlar» huquqini talab qiladi. Hisob-fakturani suratga oling — qatorlar shu yerda chiqadi.';

  @override
  String get camFileTooBig =>
      'Fayl 8 MB dan katta — kichikrog\'ini tanlang yoki hisob-fakturani suratga oling.';

  @override
  String get camFileBadType =>
      'Bunday fayl mos emas. Excel (xlsx), CSV, XML, TXT, PDF yoki rasm kerak.';

  @override
  String get camFileReadFail => 'Faylni o\'qib bo\'lmadi.';

  @override
  String recvFoundGlobal(String name) {
    return 'NurCRM ma\'lumotnomasida topildi: «$name»';
  }

  @override
  String get recvAddProduct => 'Mahsulot qo\'shish';

  @override
  String recvLooksLike(String name) {
    return 'Sizning «$name» ga o\'xshaydi';
  }

  @override
  String get recvBindCode => 'Shtrix-kodni bog\'lash';

  @override
  String recvTypo(String name, String stored, String code) {
    return '«$name» kartasidagi shtrix-kodda xato: yozilgan $stored, to\'g\'risi $code';
  }

  @override
  String get recvFixCode => 'To\'g\'rilash';

  @override
  String recvBound(String name) {
    return 'Shtrix-kod «$name» ga bog\'landi';
  }

  @override
  String get recvFixed => 'Shtrix-kod to\'g\'rilandi';

  @override
  String get recvNoRight =>
      'Qo\'shish, bog\'lash yoki to\'g\'rilash uchun «Ombor» huquqi kerak.';

  @override
  String recvSearching(String code) {
    return '$code serverda va ma\'lumotnomada qidirilmoqda…';
  }

  @override
  String get pkTitle => 'Qadoqlar / donalab sotish';

  @override
  String get pkSwitch => 'Qadoqdan donalab sotish';

  @override
  String get pkSwitchHint =>
      'Masalan, sigaret pachkasi yoki 6 talik suv qadog\'i: qadoq bilan ham, dona bilan ham sotish mumkin. Qoldiq qadoqlarda yuritiladi.';

  @override
  String get pkName => 'Qadoq nomi';

  @override
  String get pkNameHint => 'Pachka, blok, quti';

  @override
  String get pkQty => 'Qadoqdagi dona';

  @override
  String pkPackPrice(String price) {
    return 'Qadoq narxi: $price so\'m (yuqoridagi sotuv narxi)';
  }

  @override
  String get pkPiecePrice => 'Dona narxi';

  @override
  String get pkAuto => 'Avto';

  @override
  String get pkAutoTip => 'Qayta hisoblash: qadoq narxi ÷ dona';

  @override
  String pkMarkup(int n) {
    return 'Dona ustamasi: $n %';
  }

  @override
  String pkBelowCost(String cost) {
    return 'Dona xarid narxidan arzon ($cost so\'m)';
  }

  @override
  String get pkQtyBad => 'Qadoqdagi dona — kamida 2 bo\'lgan butun son';

  @override
  String get pkPieceBad => 'Dona narxini kiriting';

  @override
  String pkMismatch(String qty, String piece, String sum, String pack) {
    return '$qty dona × $piece so\'m = $sum so\'m, qadoq esa $pack so\'m — farq 5 %dan ko\'p. Shunday saqlansinmi?';
  }

  @override
  String get pkMismatchTitle => 'Dona va qadoq narxlari mos emas';

  @override
  String get pkWeightBlocked =>
      'Tortiladigan tovarni (kg) qadoqdan donalab sotib bo\'lmaydi.';

  @override
  String get pkMulti =>
      'Tovarda bir nechta qadoq bor — ularni saytda yoki kassada tahrirlang.';

  @override
  String get pkQuick => 'Tez';

  @override
  String get pkWordPack => 'Pachka';

  @override
  String get pkWordPackage => 'Qadoq';

  @override
  String get pkWordCrate => 'Yashik';

  @override
  String get pkWordBlock => 'Blok';

  @override
  String get pkWordBox => 'Quti';

  @override
  String get pkPcs => 'dona';

  @override
  String get pkChoiceTitle => 'Qanday sotiladi?';

  @override
  String get pkChoicePiece => 'Dona';

  @override
  String pkChoiceStock(String stock) {
    return 'Qoldiq: $stock';
  }

  @override
  String pkLinePiece(String name) {
    return '«$name»dan dona';
  }

  @override
  String get pkQtyInPacks =>
      'Miqdor qadoqlarda kiritiladi: 12 — 12 ta pachka, donalar ulardan sotiladi.';

  @override
  String get pkPackShort => 'qadoq';

  @override
  String get pkNoPackId =>
      'Qadoq serverda hali saqlanmagan — avval katalogni yangilang.';

  @override
  String get camNotThis => 'Bu boshqa tovar';

  @override
  String get camNotThisDone =>
      'Eslab qolingan nom o\'chirildi — tovarni qayta tanlang.';

  @override
  String get camAliasesTitle => 'Eslab qolingan nomlar:';

  @override
  String get camAliasForget => 'Nomni unutish';

  @override
  String get invDupPostedNow =>
      'Bu nakladnoy avval o\'tkazilgan (shu telefonda yoki kassada). Kirimni yana o\'tkazsangiz, qoldiq va yetkazib beruvchiga qarz ikki baravar bo\'ladi.';

  @override
  String get invSupOtherDoc =>
      'Bu kirimda boshqa yetkazib beruvchining nakladnoyi bor. Avval uni o\'tkazing, keyin buni yuklang.';

  @override
  String get invSupConflictTitle => 'Bu kirim kimniki?';

  @override
  String invSupConflictBody(String doc, String keep) {
    return 'Nakladnoy: $doc. Kirimda tanlangan: $keep. Kirim va qarz bitta yetkazib beruvchiga yoziladi — tanlang.';
  }

  @override
  String invSupConflictKeep(String name) {
    return 'Qoldirish: $name';
  }

  @override
  String invSupConflictDoc(String name) {
    return 'Nakladnoydan: $name';
  }

  @override
  String recvCostAvg(int n) {
    return 'Xarid narxi miqdor bo\'yicha o\'rtacha qilib qayta hisoblandi, pozitsiya: $n';
  }

  @override
  String invPackWarn(String unit, String name, String pu) {
    return 'Nakladnoyda «$unit» — bu qadoq, «$name» tovari esa «$pu» bilan hisoblanadi. Qanday qabul qilishni tanlang: aks holda qadoqlar qoldiqqa qadoq narxida dona bo\'lib tushadi.';
  }

  @override
  String get invPackSize => 'Qadoqdagi dona';

  @override
  String get invPackToPieces => 'Donaga aylantirish';

  @override
  String get invPackAsIs => 'Shundayligicha qabul qilish';

  @override
  String get invPackBad =>
      'Qadoqda nechta dona borligini kiriting: 2 dan kam bo\'lmagan butun son.';

  @override
  String invPackDonePieces(int n, String qty, String cost) {
    return '1 qadoq = $n dona → $qty dona, $cost so\'m';
  }

  @override
  String invPackDoneAsIs(String qty, String unit, String cost) {
    return 'Shundayligicha qabul qilindi: qoldiq $qty $unit ga oshadi, xarid $cost so\'m / $unit';
  }

  @override
  String get invPackChange => 'O\'zgartirish';

  @override
  String get pieceSaleMode => 'Qadoqdan donalab sotish';

  @override
  String get pieceSaleModeSub =>
      'Server dona o\'rniga butun pachkani yechsa, o\'chiring. Unda bunday tovar faqat qadoq bilan sotiladi.';

  @override
  String get pieceSaleTesterWarn =>
      'Sinov: qadoqdan donalab sotish serverda hali tasdiqlanmagan. To\'lovdan keyin tovar qoldig\'ini tekshiring.';

  @override
  String pieceStockMismatch(String list) {
    return 'Dona sotilgandan keyin serverdagi qoldiq mos kelmadi: $list. Tovar qoldig\'ini tekshirib, administratorga xabar bering.';
  }

  @override
  String get klScStale => 'Kod eskirdi — qayta skanerlang';

  @override
  String get klScBusy => 'To\'lov davom etmoqda — undan keyin skanerlang';

  @override
  String invCurrencyOk(String cur) {
    return 'Narxlar $cur da, kirim esa so\'mda yuritiladi. Shundayligicha qo\'shish (1 $cur = 1 so\'m) — summalarni tekshirdim';
  }

  @override
  String get invGrossOnly =>
      'Faylda faqat QQS bilan narxlar bor. Xarid narxi qator stavkasi bo\'yicha QQS\'siz hisoblandi (stavka yo\'q bo\'lsa — QQS bilan narx olindi). Tekshiring.';

  @override
  String invUnitCheck(String inv, String item) {
    return 'Tekshiring · birlik: nakladnoyda «$inv», tovarda «$item»';
  }
}
