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
  String get durationLabel => 'Davomiyligi';

  @override
  String get mastersLabel => 'Ustalar · o\'z narxi va foizi';

  @override
  String get masterAdd => 'Usta qo\'shish';

  @override
  String get masterName => 'Usta ismi';

  @override
  String get masterPercent => '% ustaga';

  @override
  String get consumablesLabel => 'Ombordan materiallar';

  @override
  String get consumableAdd => 'Material qo\'shish';

  @override
  String get onlineBooking => 'Saytda va botda onlayn yozilish';

  @override
  String get serviceLocalNote =>
      'Ustalar, davomiylik va materiallar server API\'si bo\'lguncha shu telefonda saqlanadi. Xizmat — NurCRM katalogida.';

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
  String get bookingsTitle => 'Xizmatlar · yozilish';

  @override
  String get dayToday => 'Bugun';

  @override
  String get dayTomorrow => 'Ertaga';

  @override
  String get allMasters => 'Barcha ustalar';

  @override
  String get bookNew => 'Yozish';

  @override
  String get bookCheckout => 'Hisob-kitob';

  @override
  String get bookArrived => 'keldi';

  @override
  String get bookBooked => 'yozilgan';

  @override
  String get bookPaid => 'to\'landi';

  @override
  String get bookFree => 'bo\'sh';

  @override
  String get bookMarkArrived => '«Keldi» deb belgilash';

  @override
  String get bookRemind => 'WhatsApp\'da eslatish';

  @override
  String get bookCancel => 'Yozilishni bekor qilish';

  @override
  String get bookTime => 'Vaqt';

  @override
  String get bookClash => 'Ustada bu vaqtda yozilish bor.';

  @override
  String get bookNoMasters =>
      'Ustalar hozircha yo\'q — xizmat yaratishda qo\'shing.';

  @override
  String get bookLocalNote =>
      'Yozilishlar server API\'si bo\'lguncha shu telefonda saqlanadi. To\'lov — oddiy NurCRM cheki.';

  @override
  String bookReminder(String name, String when, String master) {
    return 'Assalomu alaykum, $name! $when $master ustaga yozilganingizni eslatamiz.';
  }

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
  String get advisorOnline => 'Do\'kon ma\'lumotlari ulangan';

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
  String get durationCustom => 'Boshqa…';

  @override
  String get minutesHint => 'daqiqa';

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
  String get liveHintSale =>
      'Mahsulotga qarating — 2 soniyadan keyin o\'zi oladi';

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
  String get teachCamera => 'Aqlli kamerani o\'rgatish: 3 rasm + shtrixkod';

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
    return '$code · administrator oladi va mahsulotni qo\'shadi';
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
}
