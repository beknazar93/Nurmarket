// ignore: unused_import
import 'package:intl/intl.dart' as intl;

import 'app_localizations.dart';

// ignore_for_file: type=lint

/// The translations for Turkish (`tr`).
class AppLocalizationsTr extends AppLocalizations {
  AppLocalizationsTr([String locale = 'tr']) : super(locale);

  @override
  String get som => 'som';

  @override
  String get cancel => 'İptal';

  @override
  String get done => 'Tamam';

  @override
  String get save => 'Kaydet';

  @override
  String get retry => 'Tekrar dene';

  @override
  String get all => 'Tümü';

  @override
  String get loginTagline => 'Kasadaki ve webdeki ile aynı giriş';

  @override
  String get loginLabel => 'Kullanıcı adı veya telefon';

  @override
  String get loginHint => 'e-posta veya telefon';

  @override
  String get passwordLabel => 'Şifre';

  @override
  String get signIn => 'Giriş yap';

  @override
  String get loginEmpty => 'Kullanıcı adı ve şifreyi girin.';

  @override
  String get loginWrong => 'Kullanıcı adı veya şifre yanlış.';

  @override
  String get chooseCashbox => 'Kasa seçin';

  @override
  String get cashbox => 'Kasa';

  @override
  String get tabHome => 'Ana sayfa';

  @override
  String get tabSale => 'Satış';

  @override
  String get tabScan => 'Tarayıcı';

  @override
  String get tabChecks => 'Fişler';

  @override
  String get tabStock => 'Depo';

  @override
  String get newCheck => 'Yeni fiş';

  @override
  String get scanTileSub => 'Barkoda göre fiyat ve stok';

  @override
  String get checksTileSub => 'Gönder, yazdır';

  @override
  String get stockTileSub => 'Stok ve mal kabul';

  @override
  String shiftOpenSince(String time) {
    return 'Vardiya açık · $time';
  }

  @override
  String get shiftClosed => 'Vardiya kapalı';

  @override
  String get mySalesForShift => 'Bu vardiyadaki satışlarım';

  @override
  String checksCount(int n) {
    return '$n fiş';
  }

  @override
  String get openShift => 'Vardiyayı aç';

  @override
  String get closeShift => 'Vardiyayı kapat';

  @override
  String get openingCashHint => 'Başlangıçta kasadaki nakit';

  @override
  String get openShiftFirst => 'Önce ana ekranda vardiyayı açın.';

  @override
  String get offlineWorking =>
      'Sunucu bağlantısı yok — satışlar telefonda saklanır ve kendiliğinden gönderilir.';

  @override
  String runningOut(int n) {
    return '$n ürün tükeniyor';
  }

  @override
  String get runningOutShort => 'Tükeniyor';

  @override
  String get outOfStock => 'Stokta yok';

  @override
  String get tellAdmin => 'Yöneticiye bildirin';

  @override
  String get searchProduct => 'Ürün veya barkod';

  @override
  String get favorites => 'Favoriler';

  @override
  String get nothingFound => 'Hiçbir şey bulunamadı';

  @override
  String get inCheck => 'Fişte';

  @override
  String positions(int n) {
    return '$n kalem';
  }

  @override
  String get swipeToRemove => 'Kaldırmak için satırı sola kaydırın';

  @override
  String payAmount(String sum) {
    return '$sum som öde';
  }

  @override
  String get orderDiscount => 'Fiş indirimi';

  @override
  String get discountPercentHint => 'Yüzde, ör. 10';

  @override
  String get extraService => 'Ek hizmet';

  @override
  String get clearCheck => 'Fişi temizle';

  @override
  String get price => 'Fiyat';

  @override
  String weightOf(String name) {
    return 'Ağırlık: $name';
  }

  @override
  String get kg => 'kilogram, ör. 0,5';

  @override
  String get lineDiscount => 'Ürün indirimi';

  @override
  String get sumHint => 'Som cinsinden tutar';

  @override
  String get discount => 'İndirim';

  @override
  String barcodeNotFound(String code) {
    return '$code barkodu katalogda yok.';
  }

  @override
  String get cameraUnavailable =>
      'Kamera kullanılamıyor. Telefon ayarlarından kamera izni verin veya barkodu elle girin.';

  @override
  String get scanHint => 'Barkoda doğrultun — ürün kendiliğinden eklenir';

  @override
  String scanReceiveHint(int n) {
    return 'Tarandı: $n';
  }

  @override
  String get toCheck => 'Fişe';

  @override
  String get checkPrice => 'Fiyat sor';

  @override
  String get receiving => 'Mal kabul';

  @override
  String doneCount(int n) {
    return 'Tamam · $n';
  }

  @override
  String inStock(String qty, String unit) {
    return 'depoda $qty $unit';
  }

  @override
  String get scanContinuous => 'Onaysız art arda tara';

  @override
  String checkN(int n) {
    return 'Fiş · $n';
  }

  @override
  String get addToCheck => 'Fişe ekle';

  @override
  String get addedAutomatically => 'Fişe eklendi';

  @override
  String get profileTitle => 'Profil';

  @override
  String get discountInPercent => 'Yüzde';

  @override
  String get discountInSom => 'Tutar, som';

  @override
  String discountResult(String sum) {
    return 'İndirimli toplam: $sum som';
  }

  @override
  String get clothingMode => 'Satışta beden ve renk';

  @override
  String get clothingModeSub => 'Giyim ve ayakkabı mağazası için';

  @override
  String get chooseSize => 'Beden ve renk';

  @override
  String get colorLabel => 'Renk';

  @override
  String get sizeLabel => 'Beden';

  @override
  String toCheckSize(String size) {
    return 'Fişe · $size';
  }

  @override
  String sizeLowStock(String qty) {
    return 'Bu bedenden $qty adet kaldı. Yine de eklensin mi?';
  }

  @override
  String get rentals => 'Kiralama';

  @override
  String get rentalNew => 'Yeni kiralama';

  @override
  String get rentalActive => 'Dışarıda';

  @override
  String get rentalOverdue => 'Gecikmiş';

  @override
  String get rentalReturn => 'İadeyi al';

  @override
  String get rentalReturned => 'İade alındı.';

  @override
  String get rentalCondOk => 'Sağlam';

  @override
  String get rentalCondDamaged => 'Hasarlı';

  @override
  String get rentalPenalty => 'Depozitodan ceza';

  @override
  String get rentalClient => 'Müşteri';

  @override
  String get rentalItem => 'Ürün';

  @override
  String get rentalUntil => 'Bitiş';

  @override
  String get rentalPerDay => 'Günlük fiyat';

  @override
  String rentalDays(int n) {
    return '$n gün';
  }

  @override
  String get rentalDeposit => 'Nakit depozito';

  @override
  String get rentalDocument => 'Belge depozito';

  @override
  String get rentalDocumentHint => 'Pasaport ID…, ad';

  @override
  String get rentalCreate => 'Ver ve fişe';

  @override
  String rentalCreated(int n) {
    return '#$n kiralama oluşturuldu — tutar fişte.';
  }

  @override
  String rentalLine(int n, String days) {
    return 'Kiralama #$n · $days gün';
  }

  @override
  String get chooseClient => 'Müşteri seç';

  @override
  String get chooseItem => 'Ürün seçin';

  @override
  String get marginPercent => 'Kâr, %';

  @override
  String get marginSum => 'Kâr, som';

  @override
  String get kindProduct => 'Ürün';

  @override
  String get kindClothes => 'Giyim';

  @override
  String get kindService => 'Hizmet';

  @override
  String get sizesLabel => 'Bedenler';

  @override
  String get colorsLabel => 'Renkler';

  @override
  String get addColor => 'Renk ekle';

  @override
  String get colorName => 'Renk adı';

  @override
  String get colorSizeGrid => 'Renk × beden — kaç adet';

  @override
  String variantsSummary(int n, String qty) {
    return '$n varyant · $qty adet';
  }

  @override
  String variantsSaved(int n) {
    return 'Kaydedilen varyant: $n';
  }

  @override
  String get durationLabel => 'Süre';

  @override
  String get mastersLabel => 'Ustalar · kendi fiyat ve yüzde';

  @override
  String get masterAdd => 'Usta ekle';

  @override
  String get masterName => 'Usta adı';

  @override
  String get masterPercent => '% ustaya';

  @override
  String get consumablesLabel => 'Depodan sarf malzemesi';

  @override
  String get consumableAdd => 'Sarf malzemesi ekle';

  @override
  String get onlineBooking => 'Sitede ve botta çevrimiçi randevu';

  @override
  String get serviceLocalNote =>
      'Ustalar, süre ve malzemeler sunucu API\'si olana kadar bu telefonda saklanır. Hizmetin kendisi NurCRM kataloğundadır.';

  @override
  String get saveService => 'Hizmeti kaydet';

  @override
  String get findInternet => 'İnternette bul';

  @override
  String get internetFound => 'NurCRM tabanında bulundu';

  @override
  String get barcodeMatched => 'barkod eşleşti · kesin';

  @override
  String get takeLabel => 'Al';

  @override
  String get categoryLabel => 'Kategori';

  @override
  String get brandLabel => 'Marka';

  @override
  String get fillProduct => 'Ürünü doldur';

  @override
  String get searchInBrowser => 'Tarayıcıda ara';

  @override
  String get priceNotTaken =>
      'İnternetten fiyat alınmaz — kendinizinkini girin.';

  @override
  String get bookingsTitle => 'Hizmetler · randevu';

  @override
  String get dayToday => 'Bugün';

  @override
  String get dayTomorrow => 'Yarın';

  @override
  String get allMasters => 'Tüm ustalar';

  @override
  String get bookNew => 'Randevu ver';

  @override
  String get bookCheckout => 'Hesapla';

  @override
  String get bookArrived => 'geldi';

  @override
  String get bookBooked => 'kayıtlı';

  @override
  String get bookPaid => 'ödendi';

  @override
  String get bookFree => 'boş';

  @override
  String get bookMarkArrived => 'Geldi olarak işaretle';

  @override
  String get bookRemind => 'WhatsApp\'tan hatırlat';

  @override
  String get bookCancel => 'Randevuyu iptal et';

  @override
  String get bookTime => 'Saat';

  @override
  String get bookClash => 'Ustanın bu saatte randevusu var.';

  @override
  String get bookNoMasters => 'Henüz usta yok — hizmet oluştururken ekleyin.';

  @override
  String get bookLocalNote =>
      'Randevular sunucu API\'si olana kadar bu telefonda saklanır. Ödeme normal NurCRM fişidir.';

  @override
  String bookReminder(String name, String when, String master) {
    return 'Merhaba $name! $when $master randevunuzu hatırlatırız.';
  }

  @override
  String get pricesTitle => 'Satış fiyatları';

  @override
  String pricesSub(int a, int b) {
    return 'alış değişti: $a · yeni: $b';
  }

  @override
  String get ruleLabel => 'Kural';

  @override
  String get roundTo => 'Yuvarla';

  @override
  String get applyToAll => 'Tüm satırlara';

  @override
  String costWas(String was, String now) {
    return 'alış $was → $now';
  }

  @override
  String get newItemShort => 'yeni';

  @override
  String get applyPrices => 'Fiyatları uygula';

  @override
  String postSummary(int a, String qty, int b, int n, String sum) {
    return 'Satır: $a · adet: $qty\nYeni ürün: $b\nDeğişen fiyat: $n\nAlış: $sum';
  }

  @override
  String get quotesTitle => 'Teklifler ve faturalar';

  @override
  String get quoteNew => 'Ticari teklif';

  @override
  String get invoiceNew => 'Fatura';

  @override
  String get quotesWaiting => 'Yanıt bekliyor';

  @override
  String get invoicesUnpaid => 'Ödenmedi';

  @override
  String get invoicesOverdue => 'Gecikmiş';

  @override
  String get filterAll => 'Tümü';

  @override
  String get filterQuotes => 'Teklifler';

  @override
  String get filterInvoices => 'Faturalar';

  @override
  String get stDraft => 'Taslak';

  @override
  String get stSent => 'Gönderildi';

  @override
  String get stAccepted => 'Kabul → fatura';

  @override
  String get stDeclined => 'Reddedildi';

  @override
  String get stIssued => 'Ödeme bekliyor';

  @override
  String get stPartial => 'Kısmen ödendi';

  @override
  String get stPaid => 'Ödendi';

  @override
  String validUntil(String date) {
    return '$date tarihine kadar geçerli';
  }

  @override
  String payUntil(String date) {
    return '$date tarihine kadar öde';
  }

  @override
  String get fromCart => 'Mevcut fişten';

  @override
  String get useWholesale => 'Toptan fiyatlar';

  @override
  String get innLabel => 'VKN';

  @override
  String get docSendPdf => 'PDF gönder';

  @override
  String get invoiceFromQuote => 'Tekliften fatura';

  @override
  String get takePayment => 'Ödeme al';

  @override
  String get sellFromDoc => 'Satışı yap';

  @override
  String get markDeclined => 'Müşteri reddetti';

  @override
  String get editDoc => 'Düzenle';

  @override
  String get deleteDoc => 'Belgeyi sil';

  @override
  String paidOf(String paid, String total) {
    return '$total içinden $paid ödendi';
  }

  @override
  String basedOnQuote(String number) {
    return '$number üzerinden';
  }

  @override
  String get quotesLocalNote =>
      'Teklifler ve faturalar PC\'deki sahip programı ve şirketin diğer telefonlarıyla ortaktır. Faturadan satış normal NurCRM fişidir.';

  @override
  String get addLine => 'Ürün ekle';

  @override
  String get docDiscount => 'İndirim, som';

  @override
  String get esfTitle => 'ÖKC ve e-fatura';

  @override
  String esfQueue(int n) {
    return 'Kuyrukta $n fiş — bağlantı gelince gider';
  }

  @override
  String get esfOnline => 'Kasa çevrimiçi, kuyruk boş';

  @override
  String get esfIssue => 'E-fatura düzenle';

  @override
  String get esfBuyerInn => 'Alıcı VKN';

  @override
  String get esfBuyerName => 'Alıcı';

  @override
  String get esfPayForm => 'Ödeme şekli';

  @override
  String get esfCash => 'Nakit';

  @override
  String get esfNonCash => 'Nakitsiz';

  @override
  String get esfContract => 'Sözleşme no ve tarih';

  @override
  String get esfSum => 'Tutar, som';

  @override
  String get esfDraftSaved => 'E-fatura taslağı kaydedildi';

  @override
  String get esfSendTax => 'Vergi dairesine gönder';

  @override
  String get esfDrafts => 'E-fatura taslakları';

  @override
  String get esfIncoming => 'Gelen e-faturalar';

  @override
  String get esfStub =>
      'Vergi dairesine gönderim ve gelen e-faturalar NurCRM sunucusu bağlandığında çalışacak. Şimdilik taslak hazırlayabilirsiniz.';

  @override
  String get smartCam => 'Akıllı kamera';

  @override
  String get byLook => 'Görünüşe göre';

  @override
  String get byLookHint =>
      'Barkodsuz ürünü çekin — YZ tanır ve katalogda bulur';

  @override
  String get recognizing => 'Tanınıyor…';

  @override
  String get recognizedNone => 'Tanınmadı — daha yakından ve ışıkta çekin';

  @override
  String get seenOnPhoto => 'Fotoğrafta';

  @override
  String get notInCatalog => 'katalogda yok';

  @override
  String get aiKeyNeeded =>
      'Akıllı kamera için YZ anahtarı (Gemini) gerekir — kasadaki Nur Danışman ile aynı. Buraya yapıştırabilirsiniz.';

  @override
  String get aiKeyLabel => 'Gemini anahtarı';

  @override
  String get smartShelf => 'Raf';

  @override
  String get smartInvoice => 'İrsaliye';

  @override
  String get smartLabels => 'Etiketler';

  @override
  String get smartHint => 'Telefonu düz tutun — kamera ürün ve etiket arar';

  @override
  String get takePhoto => 'Fotoğraf çek';

  @override
  String get morePhoto => 'Bir fotoğraf daha';

  @override
  String seenInBase(int n) {
    return 'Katalogda · $n';
  }

  @override
  String seenNew(int n) {
    return 'Yeni · $n';
  }

  @override
  String smartNext(int n) {
    return 'İleri — mal kabule · $n';
  }

  @override
  String get bulkImport => 'Toplu yükleme';

  @override
  String get bulkPickFile => 'Excel veya CSV dosyası seç';

  @override
  String get bulkPaste => 'Excel\'den kopyalananı yapıştır';

  @override
  String get bulkTemplate => 'Dosya şablonu';

  @override
  String get bulkHint =>
      'Sütunlar: Ad · Barkod · Satış fiyatı · Alış · Miktar · Birim · Kategori. İlk satır başlıklar. Aynı barkodlu ürün çoğaltılmaz: miktar eklenir, fiyatlar güncellenir.';

  @override
  String bulkSummary(int n, int a, int b) {
    return 'Satır: $n · yeni $a · mevcut $b';
  }

  @override
  String get bulkBad => 'atlandı: ad veya fiyat yok';

  @override
  String get bulkExists => 'mevcut — eklenecek';

  @override
  String bulkUpload(int n) {
    return '$n yükle';
  }

  @override
  String bulkDone(int n) {
    return 'Yüklendi: $n';
  }

  @override
  String get bulkNothing => 'Dosyada ürün satırı bulunamadı';

  @override
  String get notFoundTitle => 'Ürün katalogda yok';

  @override
  String notFoundAsk(String code) {
    return 'Barkod $code. Bu barkodla yeni ürün eklensin mi?';
  }

  @override
  String get addNewProduct => 'Ürün ekle';

  @override
  String get supplierLabel => 'Tedarikçi';

  @override
  String get supplierDiscount => 'Tedarikçi indirimi, %';

  @override
  String costWithDiscount(String sum) {
    return 'İndirimli alış: $sum';
  }

  @override
  String get supplierReceiptNote =>
      'Miktar tedarikçiden giriş olarak gelir — NurCRM kimden ve kaça alındığını gösterir.';

  @override
  String get retailPrice => 'Perakende fiyat';

  @override
  String get altBarcodesLabel => 'Ek barkodlar';

  @override
  String get altBarcodesHint =>
      'Aynı ürünün veya paketin başka barkodu: kolide kaç adet olduğunu yazın — koli taranınca hepsi eklenir.';

  @override
  String get altBarcodeAdd => 'Barkod ekle';

  @override
  String get packQty => 'Adet';

  @override
  String get packName => 'Paket';

  @override
  String get photosLabel => 'Ürün fotoğrafları';

  @override
  String get photoCamera => 'Kamera';

  @override
  String get photoGallery => 'Galeri';

  @override
  String get photoInternet => 'İnternetten';

  @override
  String get photosSearching => 'Fotoğraf aranıyor…';

  @override
  String get photoFirstMain => 'İlk fotoğraf ana fotoğraftır';

  @override
  String get descriptionLabel => 'Açıklama — site ve bot için';

  @override
  String get descriptionFind => 'İnternette açıklama bul';

  @override
  String get descriptionWriting => 'Açıklama yazılıyor…';

  @override
  String get lowStockPull => '↑ Yukarı çekin — tükenenler';

  @override
  String lowStockTitle(int n) {
    return 'Tükeniyor · $n';
  }

  @override
  String get lowStockEmpty => 'Her şey stokta';

  @override
  String get summaryTitle => 'Özet';

  @override
  String get periodToday => 'Bugün';

  @override
  String get periodWeek => 'Hafta';

  @override
  String get periodMonth => 'Ay';

  @override
  String get kpiRevenue => 'Ciro';

  @override
  String get kpiChecks => 'Fiş';

  @override
  String get kpiAvg => 'Ortalama fiş';

  @override
  String get kpiProfit => 'Kâr';

  @override
  String get revenueByDay => 'Günlere göre ciro';

  @override
  String get paymentMethods => 'Ödeme yöntemleri';

  @override
  String get topProducts => 'En çok satanlar';

  @override
  String get noSalesPeriod => 'Bu dönemde satış yok';

  @override
  String get abcTitle => 'ABC analizi';

  @override
  String get abcByRevenue => 'Ciroya göre';

  @override
  String get abcByProfit => 'Kâra göre';

  @override
  String get abcByQty => 'Adede göre';

  @override
  String get abcA => 'Mağazayı besler';

  @override
  String get abcB => 'Orta';

  @override
  String get abcC => 'Kuyruk';

  @override
  String abcGroupNote(int n, String share) {
    return '$n kalem · %$share';
  }

  @override
  String get paretoTitle => 'Pareto grafiği';

  @override
  String get paretoHint =>
      'Çubuklar — ürünün payı, çizgi — kümülatif pay. %80 işaretinin solundakiler A grubu.';

  @override
  String get advisorTitle => 'Nur Danışman';

  @override
  String get advisorOnline => 'Mağaza verileri bağlı';

  @override
  String get advisorAsk => 'Mağazanız hakkında sorun…';

  @override
  String get advisorThinking => 'Danışman düşünüyor…';

  @override
  String get advisorQ1 => 'Dün ne kadar sattık?';

  @override
  String get advisorQ2 => 'Kim borçlu?';

  @override
  String get advisorQ3 => 'Ne sipariş etmeliyim?';

  @override
  String get advisorQ4 => 'Hafta nasıl geçti?';

  @override
  String get advisorNew => 'Yeni sohbet';

  @override
  String get advisorNoKey =>
      'Sahip programında bir YZ anahtarı ekleyin: Nur Danışman → ⚙ → YZ modelleri ve anahtarlar';

  @override
  String get advisorStageData => 'Mağaza verileri toplanıyor…';

  @override
  String get advisorStageAsk => 'YZ\'ye soruluyor…';

  @override
  String advisorError(String error) {
    return 'Danışman yanıt vermedi: $error';
  }

  @override
  String get callTitle => 'Sesli görüşme';

  @override
  String get callConnecting => 'Bağlanıyor…';

  @override
  String get callPreparing => 'Mağaza verileri hazırlanıyor…';

  @override
  String get callListening => 'Dinliyorum…';

  @override
  String get callThinking => 'Düşünüyorum…';

  @override
  String get callSpeaking => 'Konuşuyorum… (kesmek için dokunun)';

  @override
  String get callEnded => 'Görüşme bitti';

  @override
  String get callEnd => 'Bitir';

  @override
  String get callMicOff => 'Mikrofon kapalı';

  @override
  String get callMicOn => 'Mikrofon açık';

  @override
  String get callSpeakerOn => 'Hoparlör';

  @override
  String get callSpeakerOff => 'Ahize';

  @override
  String callCost(int n) {
    return 'Görüşme en fazla $n dk sürer ve YZ anahtarının kotasını sohbetten daha hızlı harcar.';
  }

  @override
  String get callLimitSoon => 'Görüşme bir dakika içinde bitecek';

  @override
  String callEndedLimit(int n) {
    return 'Görüşme bitti: $n dk geçti. Tekrar arayabilirsiniz.';
  }

  @override
  String get callEndedFocus =>
      'Görüşme kesildi: gelen arama veya uygulama küçültüldü.';

  @override
  String callFailed(String error) {
    return 'Görüşme başarısız: $error';
  }

  @override
  String get callNoMic =>
      'Mikrofon erişimi yok. Telefon ayarlarında NurMarket için mikrofona izin verin.';

  @override
  String get callOpenSettings => 'Ayarları aç';

  @override
  String get callUnsupported => 'Sesli görüşme Android telefonda çalışır.';

  @override
  String get callTableReady => 'Rakamlı tablo görüşmeden sonra sohbette olacak';

  @override
  String get callClose => 'Kapat';

  @override
  String get callYou => 'Siz';

  @override
  String checkTab(int n) {
    return 'Fiş · $n';
  }

  @override
  String get deleteProduct => 'Ürünü sil';

  @override
  String deleteProductAsk(String name) {
    return '«$name» silinsin mi? Satışları ve raporları kalır.';
  }

  @override
  String get productDeleted => 'Ürün silindi';

  @override
  String smartUpload(int n) {
    return '$n ürünü depoya yükle';
  }

  @override
  String get smartToReceive => 'Tedarikçiyle mal kabule';

  @override
  String smartShots(int n) {
    return 'Çekim: $n';
  }

  @override
  String get smartPriceRule => 'Yenilerin fiyatı: alış +%30, 5\'e yuvarlı';

  @override
  String smartUploaded(int n) {
    return 'Depoya yüklendi: $n';
  }

  @override
  String smartPartial(int done, int total, int left) {
    return '$total satırdan $done işlendi, $left işlenmedi — «Yükle»ye tekrar basın: işlenenler ikinci kez eklenmez.';
  }

  @override
  String get smartNoLink =>
      'Sunucu ile bağlantı yok — hiçbir şey işlenmedi. Liste ekranda kaldı, internet gelince tekrarlayın.';

  @override
  String smartRowRejected(String reason) {
    return 'Reddedildi: $reason';
  }

  @override
  String get smartRowUnknown =>
      'Sunucudan yanıt yok — tekrarlayın: stok iki kez eklenmez.';

  @override
  String get smartRowLocked =>
      'Bu satır sunucuya zaten gönderildi — ürün ve ad değiştirilemez, yüklemeyi tekrarlayın.';

  @override
  String get smartPendingReceipt =>
      'Önceki tedarikçi girişi yanıtsız kaldı — önce «Tedarikçiyle kabul»den tekrarlayın, aksi halde stok iki kez eklenebilir.';

  @override
  String get smartPendingStock =>
      'Bazı satırlar için sunucudan yanıt yok — önce «Yükle»ye tekrar basın: stok iki kez eklenmez.';

  @override
  String get sellingPrice => 'Satış fiyatı';

  @override
  String get durationCustom => 'Özel…';

  @override
  String get minutesHint => 'dakika';

  @override
  String get photoLabel => 'Fotoğraf';

  @override
  String get shiftDetails => 'Vardiya raporu';

  @override
  String get shOpened => 'Açıldı';

  @override
  String get shClosed => 'Kapandı';

  @override
  String get shOpeningCash => 'Açılış nakit';

  @override
  String get shNonCash => 'Nakitsiz';

  @override
  String get shDebt => 'Veresiye';

  @override
  String get shReturns => 'İadeler';

  @override
  String get shIncome => 'Diğer gelir';

  @override
  String get shExpected => 'Kasada olması gereken';

  @override
  String get shCounted => 'Sayılan';

  @override
  String get shDifference => 'Fark';

  @override
  String orderReadyNotify(String name, String num, String sum) {
    return 'Merhaba $name! №$num siparişiniz teslime hazır. Ödenecek $sum som. Sizi bekliyoruz!';
  }

  @override
  String get customSize => 'Özel beden';

  @override
  String get pickSupplier => 'Tedarikçi seç';

  @override
  String get addProductsHint => 'Ürünü bulun ve eklemek için dokunun';

  @override
  String get removeAction => 'Kaldır';

  @override
  String get esfExportCsv => 'Excel\'e aktar (CSV)';

  @override
  String get esfExportAll => 'Tüm taslaklar Excel\'e (CSV)';

  @override
  String get esfTaxSoon =>
      'Vergi dairesine gönderim, NurCRM sunucusu ESF\'yi bağladığında çalışacak. Şimdilik Excel\'e aktarıp vergi sitesine yükleyin.';

  @override
  String get dateLabel => 'Tarih';

  @override
  String get supplierNoLow => 'Bu tedarikçinin azalan ürünü yok';

  @override
  String get supplierLowAdd => 'Tedarikçinin azalan ürünlerini ekle';

  @override
  String get inStockOnly => 'Stokta';

  @override
  String get allCategories => 'Tüm kategoriler';

  @override
  String get catalogPick => 'Katalogdan';

  @override
  String get themeAuto => 'Saate göre';

  @override
  String get darkFrom => 'Koyu başlangıç';

  @override
  String get darkTo => 'bitiş';

  @override
  String get leaveAsk => 'Çıkmak istediğinize emin misiniz?';

  @override
  String get leaveAskSub => 'Kaydedilmemiş değişiklikler kaybolacak.';

  @override
  String get leaveYes => 'Evet, çık';

  @override
  String get leaveNo => 'Hayır, yanlışlıkla bastım';

  @override
  String get aiSettings => 'Yapay zekâ ayarları';

  @override
  String get aiSettingsHint =>
      'Nur Danışman Gemini ile yanıtlar. Gemini\'nin ücretsiz limiti biterse Groq, ardından OpenRouter. Anahtarlar ücretsizdir; NurCRM\'de saklanır.';

  @override
  String get aiConnected => 'bağlı';

  @override
  String get aiNoKeyShort => 'anahtar yok';

  @override
  String get aiGetKey => 'Anahtar al';

  @override
  String get aiTest => 'Test et';

  @override
  String get aiTestOk => 'Çalışıyor ✓';

  @override
  String get aiKeysSaved => 'Anahtarlar kaydedildi';

  @override
  String get aiPasteKey => 'Yeni anahtarı yapıştırın';

  @override
  String get aiRemoveKey => 'Anahtarı kaldır';

  @override
  String get payQrTitle => 'Ödeme QR';

  @override
  String get payQrHint =>
      'Bankalarınızın QR kodlarını ekleyin. Müşteri fiş tutarlı QR\'ı tarayıp öder. Hangi kasiyerin hangi QR\'ı göreceğini seçin — en fazla 5 banka.';

  @override
  String get payQrAdd => 'QR ekle';

  @override
  String get payQrBank => 'Banka';

  @override
  String get payQrCode => 'Banka QR';

  @override
  String get payQrScan => 'Tara';

  @override
  String get payQrGallery => 'Galeriden (ekran görüntüsü)';

  @override
  String get payQrPaste => 'Bağlantıyı yapıştır';

  @override
  String get payQrDynamic => 'Fiş tutarı QR\'da';

  @override
  String get payQrDynamicSub =>
      'Müşteri tutarı yazmaz (ELQR). Banka kabul etmezse kapatın.';

  @override
  String get payQrStaticOnly => 'Bu QR ELQR değil — tutarsız gösterilir';

  @override
  String get payQrCashiers => 'Kime göster';

  @override
  String get payQrAllCashiers => 'Tüm kasiyerler';

  @override
  String get payQrNotRecognized => 'QR tanınmadı';

  @override
  String get payQrShowCustomer =>
      'Müşteriye gösterin. Ödemeden sonra Öde\'ye basın.';

  @override
  String get payQrNone =>
      'Ödeme QR\'ı yok — sahibi Yönetim → Ödeme QR\'dan ekler.';

  @override
  String get payQrNoneOwner => 'Henüz QR yok. QR ekle\'ye dokunun.';

  @override
  String get payQrSaved => 'QR kaydedildi';

  @override
  String get payQrDelete => 'QR sil';

  @override
  String get grpAi => 'Yapay zekâ ve analiz';

  @override
  String get grpStock => 'Ürünler ve depo';

  @override
  String get grpMoney => 'Satış ve para';

  @override
  String get grpClients => 'Müşteriler ve belgeler';

  @override
  String get grpTeam => 'Ekip';

  @override
  String get findSection => 'Bölüm bul';

  @override
  String get liveAuto => 'Otomatik';

  @override
  String get liveHintSale => 'Ürüne doğrultun ve «Tara»ya basın';

  @override
  String get liveHintShelf => 'Rafa doğrultun';

  @override
  String get liveHintInvoice => 'Faturanın tamamını çerçeveye alın';

  @override
  String get liveHintLabels => 'Etiketlere doğrultun';

  @override
  String get auditTitle => 'Denetim günlüğü';

  @override
  String get auditSub => 'Bu telefondaki tüm işlemler';

  @override
  String get auditSearch => 'Personel, ürün, fiş no';

  @override
  String get auditKind => 'İşlem';

  @override
  String get auditWhat => 'Ne yapıldı';

  @override
  String get auditSale => 'Satış';

  @override
  String get auditCart => 'Fişe';

  @override
  String get auditReturn => 'İade';

  @override
  String get auditPrice => 'Fiyat ve stok';

  @override
  String get auditProduct => 'Ürünler';

  @override
  String get auditSettings => 'Ayarlar';

  @override
  String get auditLogin => 'Giriş/çıkış';

  @override
  String get auditOpen => 'Ekran açtı';

  @override
  String get teachCamera => 'Akıllı kamerayı öğret: 3–5 fotoğraf + barkod';

  @override
  String get teachRight => '3\'te 1 · sağdan';

  @override
  String get teachLeft => '3\'te 2 · soldan';

  @override
  String get teachTop => '3\'te 3 · üstten';

  @override
  String get teachHint => 'Ürün tamamen çerçevede';

  @override
  String get teachDone => 'Kamera öğrendi';

  @override
  String teachShots(int n) {
    return 'Akıllı kamera için: $n fotoğraf';
  }

  @override
  String get scanShoot => 'Tara';

  @override
  String get scanBusy => 'Taranıyor…';

  @override
  String get scanNext => 'Sonraki ürün — yine «Tara»';

  @override
  String get scanChecking => 'Öğretilen ürünlerle karşılaştırılıyor…';

  @override
  String scanTook(String sec) {
    return '$sec sn';
  }

  @override
  String teachPhotoOf(int a, int b) {
    return 'Fotoğraf $a/$b';
  }

  @override
  String get teachAngleRight => 'sağdan';

  @override
  String get teachAngleLeft => 'soldan';

  @override
  String get teachAngleTop => 'üstten';

  @override
  String get teachAngleFront => 'önden etiket';

  @override
  String get teachAngleBack => 'arkadan';

  @override
  String get teachCanFinish => '«Tamam»a basın veya bir açı daha çekin';

  @override
  String get teachRetake => 'Yeniden çek';

  @override
  String get weighAim =>
      'Ürünü teraziye koyun, ekrana doğrultun ve «Tara»ya basın';

  @override
  String get weighAimDisplay =>
      'Terazi ekranı görünmüyor — ağırlık penceresine doğrultun, feneri kapatın ve «Tara»ya basın';

  @override
  String get aiErrNoNet =>
      'İnternet yok — bağlantıyı kontrol edip yine «Tara»ya basın';

  @override
  String get aiErrSlow =>
      'Yapay zekâ zamanında cevap vermedi (yavaş bağlantı) — tekrar deneyin';

  @override
  String get aiErrLimit =>
      'Ücretsiz yapay zekâ limiti doldu — bir dakika sonra deneyin';

  @override
  String get aiErrBusy => 'Yapay zekâ şu an yoğun — bir dakika sonra deneyin';

  @override
  String get aiErrOther => 'Yapay zekâ fotoğrafı tanıyamadı';

  @override
  String addAgain(String name) {
    return '«$name» zaten fişte. Bir tane daha eklensin mi?';
  }

  @override
  String get yesAdd => 'Ekle';

  @override
  String get shelfAddAsk =>
      'Raf fotoğrafı raftaki mevcut ürünleri gösterir. Bu miktarlar teslimat olarak stoka eklensin mi?';

  @override
  String isItProduct(String name) {
    return 'Bu «$name» mi?';
  }

  @override
  String notFoundOffline(String code) {
    return '$code kodu telefonda yok, sunucu cevap vermiyor. Bağlantıyı kontrol edin — ürün PC\'de zaten olabilir, ikincisini oluşturmayın.';
  }

  @override
  String get scaleLabelsTitle => 'Terazi etiketleri';

  @override
  String get scaleLabelsHint =>
      'Terazi etiketi barkodu bir önekle (20–29) başlar. Hangi öneklerin TUTAR, hangilerinin AĞIRLIK taşıdığını belirtin. Şirketin tüm telefonları için.';

  @override
  String get scaleAmountPrefixes => '«Tutar» önekleri';

  @override
  String get scaleWeightPrefixes => '«Ağırlık» önekleri';

  @override
  String get cameraShotFailed =>
      'Kamera fotoğraf çekemedi — yine «Tara»ya basın';

  @override
  String get saved => 'Kaydedildi';

  @override
  String get exactMatch => 'kesin';

  @override
  String get noAccess => 'Erişim yok';

  @override
  String get noAccessHint =>
      'Bu bölüm veya işlem hesabınıza kapalı. Sahibi açabilir: NurCRM → Çalışanlar → erişim.';

  @override
  String get clientOptional => 'Müşteri — bonus ve alışveriş geçmişi için';

  @override
  String get bonusRedeem => 'Bonusla öde';

  @override
  String get bonusNone => 'Kullanma';

  @override
  String bonusAll(String sum) {
    return 'Maksimum · $sum';
  }

  @override
  String bonusEarn(String sum) {
    return 'Kazanılacak bonus: $sum';
  }

  @override
  String bonusPaid(String sum) {
    return 'Bonusla: −$sum som';
  }

  @override
  String get removeClient => 'Müşteriyi kaldır';

  @override
  String get discountRemove => 'İndirimi kaldır';

  @override
  String get addedToCheck => 'Fişe eklendi';

  @override
  String get payment => 'Ödeme';

  @override
  String toPayPositions(int n) {
    return 'Ödenecek · $n kalem';
  }

  @override
  String get payMethod => 'Ödeme yöntemi';

  @override
  String get cash => 'Nakit';

  @override
  String get card => 'Kart';

  @override
  String get debt => 'Veresiye';

  @override
  String get mixed => 'Karma';

  @override
  String get client => 'Müşteri';

  @override
  String get received => 'Alınan';

  @override
  String get receivedNowDebt => 'Şimdi alınan (0 — tamamı borç)';

  @override
  String get exact => 'Tam tutar';

  @override
  String get change => 'Para üstü';

  @override
  String notEnough(String sum) {
    return '$sum som eksik';
  }

  @override
  String get receiptToCustomer => 'Müşteriye fiş';

  @override
  String get notNeeded => 'Gerekmez';

  @override
  String get doPay => 'Ödemeyi tamamla';

  @override
  String get check => 'Fiş';

  @override
  String get total => 'Toplam';

  @override
  String get paidOk => 'Ödeme tamamlandı';

  @override
  String get savedOffline => 'Fiş telefonda kaydedildi';

  @override
  String get savedOfflineHint =>
      'Bağlantı yok — fiş kendiliğinden gönderilecek, iki kez kaydedilmez.';

  @override
  String get receiptQr => 'QR kod';

  @override
  String get showQr => 'Müşteriye QR göster';

  @override
  String get qrPointCamera => 'Müşteri telefon kamerasını tutar';

  @override
  String get qrDownloadHint =>
      'Müşterinin kamerası fişi hemen PDF olarak indirir';

  @override
  String get qrTextHint =>
      'Sunucu fiş bağlantısı verene kadar kamera metin gösterir. PDF\'i aşağıdaki düğmeyle gönderin.';

  @override
  String get cashInOut => 'Para girişi / çıkışı';

  @override
  String get cashIn => 'Para girişi';

  @override
  String get cashOut => 'Para çıkışı';

  @override
  String get amount => 'Tutar';

  @override
  String get reason => 'Neden';

  @override
  String get reasonChange => 'Bozuk para';

  @override
  String get reasonCollection => 'Tahsilat';

  @override
  String get reasonExpense => 'Giderler';

  @override
  String get reasonOther => 'Diğer';

  @override
  String get cashOpDone => 'Vardiyaya kaydedildi';

  @override
  String get debts => 'Müşteri borçları';

  @override
  String get debtorsTotal => 'Toplam borç';

  @override
  String get payDebt => 'Borç ödemesi al';

  @override
  String get payDebtDone => 'Borç ödemesi kaydedildi';

  @override
  String get noDebts => 'Borçlu yok';

  @override
  String get heldChecks => 'Bekleyen fişler';

  @override
  String get holdCheck => 'Fişi beklet';

  @override
  String get holdNote => 'Kimin için veya not';

  @override
  String get heldEmpty => 'Bekleyen fiş yok';

  @override
  String get restoreCheck => 'Geri yükle';

  @override
  String get delete => 'Sil';

  @override
  String get checkBusy => 'Fişte ürün var — önce bekletin veya ödeyin';

  @override
  String get returnTitle => 'Fişten iade';

  @override
  String get returnAll => 'Tüm fişi iade et';

  @override
  String get returnSelected => 'Seçileni iade et';

  @override
  String get returnDone => 'İade tamamlandı';

  @override
  String get returnToCustomer => 'Müşteriye iade';

  @override
  String get showReceipt => 'Fişi görüntüle';

  @override
  String get sendPdf => 'PDF gönder';

  @override
  String get sendReceipt => 'Fişi gönder';

  @override
  String get newClient => 'Yeni müşteri';

  @override
  String get clientName => 'Ad';

  @override
  String get phone => 'Telefon';

  @override
  String get clientSearch => 'Ad veya telefon';

  @override
  String get myChecks => 'Fişlerim';

  @override
  String get thisShift => 'Bu vardiya';

  @override
  String unsentN(int n) {
    return 'Gönderilmemiş · $n';
  }

  @override
  String get checkSearch => 'Fiş numarası veya ürün';

  @override
  String get noChecks => 'Henüz fiş yok';

  @override
  String get checkNotSent => 'Fiş gönderilmedi';

  @override
  String get notAccepted => 'kabul edilmedi';

  @override
  String get waitsForNetwork => 'bağlantı bekliyor';

  @override
  String get returned => 'iade';

  @override
  String get offlineCheckHint =>
      'Bağlantı yok — fiş kaydedildi, kendiliğinden gönderilecek';

  @override
  String get sendToCustomer => 'Müşteriye gönder';

  @override
  String get sendAgain => 'Tekrar gönder';

  @override
  String get returnByAdmin => 'İade — yönetici izniyle';

  @override
  String get receiveByScanner => 'Tarayıcıyla mal kabul';

  @override
  String get audit => 'Sayım';

  @override
  String get findProduct => 'Ürün bul';

  @override
  String get pricesByAdmin => 'Fiyatları ve yeni ürünleri yönetici değiştirir';

  @override
  String get receiveNoSupplier => 'Tedarikçisiz ve fiyatsız';

  @override
  String get scanProduct => 'Ürünü tara';

  @override
  String get eachScanPlusOne => 'Her tarama — +1 adet';

  @override
  String get accepted => 'Kabul edildi';

  @override
  String goodsPieces(int n, String qty) {
    return '$n ürün · $qty adet';
  }

  @override
  String wasPieces(String qty) {
    return 'önce $qty';
  }

  @override
  String get unknownBarcode => 'Bilinmeyen barkod';

  @override
  String unknownBarcodeHint(String code) {
    return '$code · yönetici alıp ürünü ekleyecek';
  }

  @override
  String get postpone => 'Ertele';

  @override
  String postPieces(String qty) {
    return 'Kaydet · $qty adet';
  }

  @override
  String get receivedOk => 'Mal kabul kaydedildi.';

  @override
  String get receivedQueued =>
      'Mal kabul kaydedildi — bağlantı gelince gönderilecek.';

  @override
  String get allStock => 'Tüm depo';

  @override
  String get chooseWhatToCount => 'Neyi sayacağız';

  @override
  String checkedOf(int a, int b) {
    return '$b üründen $a kontrol edildi';
  }

  @override
  String get notChecked => 'Kontrol edilmedi';

  @override
  String get differences => 'Farklar';

  @override
  String byProgram(String qty, String unit) {
    return 'sistemde $qty $unit';
  }

  @override
  String get matches => 'eşleşti';

  @override
  String shortBy(String qty) {
    return '$qty eksik';
  }

  @override
  String extraBy(String qty) {
    return 'fazla $qty';
  }

  @override
  String get auditAdminApplies => 'Stoku yönetici kontrolden sonra günceller';

  @override
  String get sendToAdmin => 'Yöneticiye gönder';

  @override
  String get auditSent => 'Sayım yöneticiye gönderildi.';

  @override
  String get auditQueued => 'Sayım kaydedildi — bağlantı gelince gönderilecek.';

  @override
  String sinceChecks(String time, int n) {
    return '$time itibarıyla · $n fiş';
  }

  @override
  String get revenue => 'Ciro';

  @override
  String get shouldBeInDrawer => 'Kasada olması gereken';

  @override
  String get recountCash => 'Nakdi sayın';

  @override
  String get howMuchInDrawer => 'Kasada ne kadar nakit var';

  @override
  String cashDiff(String sum) {
    return 'Sistemle fark: $sum som';
  }

  @override
  String get cashDiffHint => 'Tutar tutmazsa yönetici farkı raporda görür';

  @override
  String closeShiftPending(int n) {
    return 'Önce $n fiş sunucuya gönderilmeli — bağlantı gerekli.';
  }

  @override
  String get shiftClosedOk => 'Vardiya kapatıldı';

  @override
  String get receiptPrinter => 'Fiş yazıcısı';

  @override
  String get prnSub => 'Bluetooth, Wi-Fi/LAN veya USB · 58/80 mm';

  @override
  String get prnNotSet => 'Ayarlanmadı';

  @override
  String get prnConnection => 'Bağlantı';

  @override
  String get prnOff => 'Yok';

  @override
  String get prnNetwork => 'Ağ (Wi-Fi/LAN)';

  @override
  String get prnUsb => 'USB (OTG)';

  @override
  String get prnSearch => 'Yazıcı bul';

  @override
  String get prnSearching => 'Aranıyor…';

  @override
  String get prnNothingFound =>
      'Hiçbir şey bulunamadı. Yazıcı açık ve yakında mı?';

  @override
  String get prnPairHint =>
      'Yazıcı listede yok mu? Açın ve «Bul»a dokunun — ya da telefonun Bluetooth ayarlarında eşleştirin (PIN genelde 0000 veya 1234).';

  @override
  String get prnUsbHint =>
      'Yazıcıyı OTG kablosuyla bağlayıp açın. Android izin isteyecek — «İzin ver»e dokunun.';

  @override
  String get prnBtSettings => 'Bluetooth ayarları';

  @override
  String get prnPaired => 'eşleşmiş';

  @override
  String get prnNotPaired => 'yakında, eşleşmemiş';

  @override
  String get prnIp => 'Yazıcı IP adresi';

  @override
  String get prnPort => 'Port';

  @override
  String get prnPaper => 'Kağıt genişliği';

  @override
  String prnPaperMm(int a, int b) {
    return '$a mm · $b karakter';
  }

  @override
  String get prnCodepage => 'Kiril kodlaması';

  @override
  String get prnCodeTable => 'ESC t tablosu';

  @override
  String get prnCodepageHint =>
      'Kiril harfleri yerine çince karakter veya «?» — kodlamayı ya da tablo numarasını değiştirin (CP866 — 17, bazen 6 veya 7; CP1251 — 46, bazen 73) ve tekrar deneyin.';

  @override
  String get prnAuto => 'Ödemeden hemen sonra fişi yazdır';

  @override
  String get prnTest => 'Test baskısı';

  @override
  String get prnTestSent => 'Test sayfası yazıcıya gönderildi';

  @override
  String get prnTestHint =>
      'Test sayfasına bakın: rakam satırı tüm genişliği doldurmalı, kiril harfleri okunmalı.';

  @override
  String get prnPrint => 'Fişi yazdır';

  @override
  String get prnSent => 'Fiş yazıcıya gönderildi';

  @override
  String get prnErrNotConfigured =>
      'Yazıcı ayarlanmadı: Profil → Fiş yazıcısı.';

  @override
  String get prnErrUnsupported => 'Bu yazdırma yöntemi bu cihazda yok.';

  @override
  String get prnErrBtOff => 'Bluetooth kapalı — açıp tekrar deneyin.';

  @override
  String get prnErrPermission =>
      '«Yakındaki cihazlar» (Bluetooth) izni yok. Android penceresinde veya uygulama ayarlarında izin verin.';

  @override
  String get prnErrNotFound =>
      'Yazıcı bulunamadı — açık olduğunu kontrol edip yeniden seçin.';

  @override
  String get prnErrBleOnly =>
      'Bu yazıcı yalnızca BLE ile çalışıyor — klasik Bluetooth (SPP) yazıcı gerekli.';

  @override
  String get prnErrTimeout =>
      'Yazıcı yanıt vermiyor. Açık, yakında ve kağıt var mı? Tekrar deneyin.';

  @override
  String get prnErrRefused =>
      'Ağ yazıcısı işi reddediyor — IP adresini ve portu kontrol edin (genelde 9100).';

  @override
  String get prnErrUsbDenied =>
      'USB yazıcıya erişim yok — Android penceresinde «İzin ver»e dokunun.';

  @override
  String get prnErrUsbNotFound =>
      'USB yazıcı bulunamadı — OTG kablosunu ve yazıcının gücünü kontrol edin.';

  @override
  String prnErrIo(String text) {
    return 'Yazdırma hatası: $text';
  }

  @override
  String get kgUnit => 'kg';

  @override
  String get weighShort => 'Terazi';

  @override
  String get weighTitle => 'Tartılı ürün';

  @override
  String get weighHint =>
      'Ürünü teraziye koyun. Kamerayı ürün ve terazi ekranı görünecek şekilde tutun.';

  @override
  String get weighReading => 'Ürün tanınıyor ve terazi okunuyor…';

  @override
  String get weighWaiting => 'Ağırlığın sabitlenmesi bekleniyor…';

  @override
  String get weighZero => 'Terazide 0 — ürünü koyun.';

  @override
  String get weighNoDisplay =>
      'Terazi ekranı görünmüyor — kamerayı ağırlık rakamlarına tutun.';

  @override
  String get weighNoAi =>
      'Akıllı kamera kullanılamıyor (bağlantı veya YZ anahtarı yok) — ürünü seçip ağırlığı elle girin.';

  @override
  String get weighConfirm => 'Ürünü ve ağırlığı kontrol edin';

  @override
  String weighRead(String name, String kg) {
    return 'Kamera okudu: $name · $kg kg';
  }

  @override
  String weighLine(String name, String kg, String price, String sum) {
    return '$name $kg kg × $price = $sum som';
  }

  @override
  String weighTooHeavy(String kg) {
    return 'Ağırlık $kg kg — 50 kg\'dan fazla. Yine de eklensin mi?';
  }

  @override
  String get weighNotStable => 'Ağırlık sabitlenmedi — değeri kontrol edin.';

  @override
  String get weighEditKg => 'Ağırlığı değiştir';

  @override
  String get weighOther => 'Başka ürün — listeden seç';

  @override
  String get linkWithKassa => 'Kasa ile bağlantı';

  @override
  String get linkWithKassaSub =>
      'Mağaza Wi-Fi\'ı üzerinden — bir sonraki güncellemede';

  @override
  String get language => 'Dil';

  @override
  String get darkTheme => 'Koyu tema';

  @override
  String get offlineMode => 'İnternetsiz çalışma';

  @override
  String waitingToSend(int n) {
    return 'Gönderilmeyi bekleyen: $n';
  }

  @override
  String get logout => 'Çıkış yap';

  @override
  String logoutPending(int n) {
    return 'Telefonda $n gönderilmemiş kayıt var. Çıkıştan sonra kalır ve bu hesabın bir sonraki girişinde gönderilir. Çıkılsın mı?';
  }

  @override
  String get sync => 'Senkronizasyon';

  @override
  String catalogUpdated(String time) {
    return 'Katalog güncellendi $time';
  }

  @override
  String get nothingToSend => 'Her şey gönderildi';

  @override
  String get sent => 'gönderildi';

  @override
  String get sendNow => 'Şimdi gönder';

  @override
  String get kindSale => 'Satış';

  @override
  String get admin => 'Yönetim';

  @override
  String get adminSub => 'Fiyatlar, vardiyalar, siparişler, müşteriler';

  @override
  String get revenueToday => 'Bugünkü ciro';

  @override
  String get checksToday => 'Bugünkü fişler';

  @override
  String get clientsOwe => 'Müşteri borcu';

  @override
  String get productPrice => 'Ürün — fiyat ve stok';

  @override
  String get shifts => 'Vardiyalar ve rapor';

  @override
  String get siteOrders => 'Site siparişleri';

  @override
  String get clientsBonuses => 'Müşteriler ve bonuslar';

  @override
  String get supplierOrder => 'Tedarikçi siparişi';

  @override
  String get deletedFromCheck => 'Fişten silinenler';

  @override
  String get nameOrBarcode => 'Ad veya barkod';

  @override
  String get salePrice => 'Satış fiyatı';

  @override
  String get purchasePrice => 'Alış fiyatı';

  @override
  String get wholesalePrice => 'Toptan fiyat';

  @override
  String get onStockLabel => 'Stokta';

  @override
  String get priceSaved => 'Fiyat kaydedildi.';

  @override
  String markup(String pct) {
    return 'Kâr $pct %';
  }

  @override
  String get belowCost => 'Fiyat alışın altında — zararına satış';

  @override
  String get shiftOpenNow => 'açık';

  @override
  String shiftClosedAt(String time) {
    return '$time kapandı';
  }

  @override
  String get cashierLabel => 'Kasiyer';

  @override
  String get orderNew => 'Yeni';

  @override
  String get orderPicking => 'Toplanıyor';

  @override
  String get orderReady => 'Hazır';

  @override
  String get orderDone => 'Teslim edildi';

  @override
  String get orderCanceled => 'İptal';

  @override
  String get pickOrder => 'Siparişi topla';

  @override
  String get pickScanHint => 'Ürünü tarayın — kendisi işaretlenir';

  @override
  String pickedOf(int a, int b) {
    return '$b / $a toplandı';
  }

  @override
  String get orderPicked => 'Sipariş toplandı';

  @override
  String get notInOrder => 'Bu ürün siparişte yok';

  @override
  String get notifyCustomer => 'Müşteriye bildir';

  @override
  String get handOver => 'Teslim et';

  @override
  String orderReadyMsg(String num, String sum, String code) {
    return '$num siparişi hazır! Ödenecek $sum som. Teslim kodu $code.';
  }

  @override
  String get statusSaved => 'Durum kaydedildi.';

  @override
  String get bonusBalance => 'Bonus bakiyesi';

  @override
  String get addBonus => 'Bonus ekle';

  @override
  String get bonusAdded => 'Bonuslar eklendi.';

  @override
  String get noPhone => 'numara yok';

  @override
  String get orderToSupplier => 'WhatsApp ile gönder';

  @override
  String get supplierName => 'Tedarikçi';

  @override
  String get supplierPhone => 'Tedarikçi telefonu';

  @override
  String get runningLowAdd => 'Azalan her şeyi ekle';

  @override
  String get orderQty => 'Sipariş miktarı';

  @override
  String get deleteReason => 'Neden çıkarıyorsunuz?';

  @override
  String get reasonChanged => 'Müşteri vazgeçti';

  @override
  String get reasonScanError => 'Tarama hatası';

  @override
  String get reasonNoMoney => 'Para yetmedi';

  @override
  String get reasonDefect => 'Kusurlu veya tarihi geçmiş';

  @override
  String get remove => 'Çıkar';

  @override
  String get noDeletions => 'Silme yok.';

  @override
  String get loading => 'Yükleniyor…';

  @override
  String get empty => 'Burada bir şey yok.';

  @override
  String get share => 'Paylaş';

  @override
  String get documentPdf => 'İrsaliye, satış fişi, fatura';

  @override
  String get docSalesReceipt => 'Satış fişi';

  @override
  String get docWaybill => 'İrsaliye';

  @override
  String get docInvoice => 'Fatura';

  @override
  String get staff => 'Personel ve yetkiler';

  @override
  String get rightSale => 'Satış ve ödeme';

  @override
  String get rightReturn => 'İade ve değişim';

  @override
  String get rightDiscount => 'İndirimler';

  @override
  String get rightEditPrice => 'Fişte fiyat değiştirme';

  @override
  String get rightDeleteLine => 'Fişten silme';

  @override
  String get rightStock => 'Depo, kabul, sayım';

  @override
  String get rightReports => 'Raporlar ve analiz';

  @override
  String get rightShifts => 'Vardiyalar';

  @override
  String get rightClients => 'Müşteriler';

  @override
  String get rightSettings => 'Ayarlar';

  @override
  String get rightsSaved => 'Yetkiler kaydedildi.';

  @override
  String get notifications => 'Bildirimler';

  @override
  String get notifyNewOrder => 'Yeni site siparişi';

  @override
  String get notifyRunningOut => 'Ürünler azalıyor';

  @override
  String notifyRunningOutSub(int n) {
    return '$n kalem · sipariş zamanı';
  }

  @override
  String get notifyBigDebt => 'Büyük müşteri borcu';

  @override
  String get notifyNotSent => 'Sunucuya gönderilmedi';

  @override
  String get noNotifications => 'Yeni bildirim yok.';

  @override
  String get newProductSub => 'Barkod, ad, fiyatlar';

  @override
  String get findByBarcode => 'Barkodla bul';

  @override
  String alreadyInStock(String name) {
    return 'Bu ürün zaten var: $name';
  }

  @override
  String get productCreated => 'Ürün oluşturuldu.';

  @override
  String get quantityLabel => 'Miktar';

  @override
  String get barcodeLabel => 'Barkod';

  @override
  String get retail => 'Perakende';

  @override
  String get wholesale => 'Toptan';

  @override
  String get wholesaleOn =>
      'Toptan satış: toptan fiyatı olan ürünler toptan fiyatla.';

  @override
  String get wholesaleNone => 'Bu fişteki ürünlerin toptan fiyatı yok.';

  @override
  String get wholesaleTag => 'toptan';

  @override
  String get cashJournal => 'Vardiyadaki giriş ve çıkışlar';

  @override
  String get noCashOps => 'Henüz giriş veya çıkış yok.';

  @override
  String get themeLight => 'Açık';

  @override
  String get themeDark => 'Koyu';

  @override
  String get themeSystem => 'Sistemdeki gibi';

  @override
  String get appearance => 'Görünüm';

  @override
  String get searchAll => 'Ürün, müşteri veya bölüm';

  @override
  String get sectionsTitle => 'Bölümler';

  @override
  String get productsTitle => 'Ürünler';

  @override
  String get clientsTitle => 'Müşteriler';

  @override
  String get openAction => 'Aç';

  @override
  String get returnAction => 'Geri al';

  @override
  String addedToCheckName(String name) {
    return 'Fişe eklendi: $name';
  }

  @override
  String get receiveWithPrices => 'Fiyatlı mal kabul';

  @override
  String get receiveWithPricesSub => 'Tedarikçi, alış, satış, yeni ürünler';

  @override
  String get noSupplier => 'Tedarikçisiz';

  @override
  String get newProduct => 'Yeni ürün';

  @override
  String barcodeNotInStock(String code) {
    return '$code barkodu depoda yok';
  }

  @override
  String get foundInBase => 'Ortak tabanda bulundu — kontrol edin';

  @override
  String get unitLabel => 'Birim';

  @override
  String get createAndAdd => 'Oluştur ve ekle';

  @override
  String markupRule(String pct) {
    return 'Satış fiyatı: alış +%$pct';
  }

  @override
  String get payNowCash => 'Hemen ödendi';

  @override
  String get onCredit => 'Veresiye';

  @override
  String purchaseSum(String sum) {
    return 'Alış $sum som';
  }

  @override
  String get postReceipt => 'Girişi işle';

  @override
  String receiptPosted(int n) {
    return 'Giriş işlendi: $n kalem';
  }

  @override
  String partlyFailed(String text) {
    return 'Başarısız: $text';
  }

  @override
  String get shiftsOpen => 'Açık';

  @override
  String get shiftsClosed => 'Kapalı';

  @override
  String get debtAccessTitle => 'Veresiye yetkisi';

  @override
  String get debtAccessHint =>
      'Hangi çalışanlar veresiye satabilir: izinli, yasak veya limitli — günlük fiş ve tutar. Kurallar şirketin tüm kasaları ve telefonları için ortaktır. Sahip ve yöneticiler sınırsızdır.';

  @override
  String get debtAllStaff => 'Tüm çalışanlar';

  @override
  String get debtSameAsAll => 'Herkes gibi';

  @override
  String get debtAllow => 'İzinli';

  @override
  String get debtDeny => 'Yasak';

  @override
  String get debtLimit => 'Limitli';

  @override
  String get debtReceiptsPerDay => 'Günlük veresiye fiş';

  @override
  String get debtSumPerDay => 'Günlük veresiye tutarı, som';

  @override
  String get debtNoLimit => 'sınırsız';

  @override
  String get debtAccessSaved =>
      'Kaydedildi — kurallar tüm kasa ve telefonlarda geçerli.';

  @override
  String get debtStaffNotLoaded =>
      'Çalışan listesi alınamadı — herkes için kural koyabilirsiniz.';

  @override
  String get debtDenied =>
      'Sahip size veresiye satışı yasakladı. Başka bir ödeme yöntemi seçin veya sahibi çağırın.';

  @override
  String debtLimitReceipts(int a, int b) {
    return 'Bugünkü veresiye limiti doldu: $a / $b fiş. Sahibe başvurun.';
  }

  @override
  String debtLimitSum(String left, String max, String sum) {
    return 'Bugünkü veresiye limiti aşıldı: $max som\'dan $left som kaldı, borç $sum som. Ödemenin bir kısmını şimdi alın veya sahibi çağırın.';
  }

  @override
  String debtReceiptsOf(int a, int b) {
    return 'fiş $a / $b';
  }

  @override
  String debtSumOf(String used, String max) {
    return 'borç $used / $max som';
  }

  @override
  String debtYourLimit(String text) {
    return 'Bugünkü veresiye limitiniz: $text';
  }

  @override
  String get promoTag => 'kampanya';

  @override
  String get promoNoManualDiscount =>
      'Bu ürün kampanyada — indirimi sunucu uygular; bu satıra elle indirim verilemez.';

  @override
  String get quoteSearch => 'Numara, müşteri veya telefon';

  @override
  String get quotesEmpty => 'Henüz teklif veya fatura yok';

  @override
  String get quotesEmptyHint =>
      '“Oluştur”a dokunun: müşteri ve ürünleri seçin — PDF WhatsApp\'a veya e-postaya gider.';

  @override
  String get quotesNoMatch =>
      'Hiçbir şey bulunamadı — aramayı veya filtreyi değiştirin.';

  @override
  String get createDoc => 'Oluştur';

  @override
  String get makeInvoice => 'Fatura oluştur';

  @override
  String get sendToClient => 'Gönder';

  @override
  String docWaText(String doc, String number, String sum, String until) {
    return '$doc No. $number, tutar $sum som. $until.';
  }

  @override
  String get docSubtotal => 'İndirimsiz tutar';

  @override
  String get discountShort => 'İndirim';

  @override
  String get paidLabel => 'Ödendi';

  @override
  String get leftToPay => 'Kalan ödeme';

  @override
  String get findClient => 'Müşteri bul';

  @override
  String get companyLabel => 'Şirket (Ltd., şahıs)';

  @override
  String get clientExists =>
      'Bu telefonla müşteri NurCRM\'de zaten var — o seçildi.';

  @override
  String get clientSaved => 'Müşteri NurCRM\'e kaydedildi';

  @override
  String get clientLocalOnly =>
      'NurCRM bağlantısı yok — müşteri yalnızca bu belgeye yazıldı.';

  @override
  String get clientNameRequired => 'Ad veya şirket girin';

  @override
  String get innDigits => 'VKN — yalnızca rakam, genelde 14';

  @override
  String get discountHint => 'Tutar veya yüzde, ör. %10';

  @override
  String get stExpired => 'Süresi doldu';

  @override
  String deleteDocAsk(String number) {
    return '$number silinsin mi?';
  }

  @override
  String get deleteDocAskSub =>
      'Belge sahip programından da kalıcı olarak silinir.';

  @override
  String get requisitesTitle => 'Fatura bilgileri';

  @override
  String get requisitesHint =>
      'Banka, hesap, BIC, telefon — PDF\'te mağaza adının altında yazdırılır.';

  @override
  String scannedAdded(String name) {
    return 'Eklendi: $name';
  }

  @override
  String invoiceCreated(String number) {
    return '$number faturası oluşturuldu';
  }

  @override
  String get historyLabel => 'Geçmiş';

  @override
  String get docLines => 'Ürünler ve hizmetler';

  @override
  String docFrom(String date) {
    return '$date tarihli';
  }

  @override
  String get clientNotChosen => 'Müşteri seçilmedi';

  @override
  String get linesEmptyHint =>
      'Ürün ekleyin: arama, tarayıcı veya mevcut fişten.';

  @override
  String get quotesSyncing => 'Ortak liste güncelleniyor…';

  @override
  String get quotesShared => 'Sahip programıyla ortak liste';

  @override
  String quotesSharedAt(String time) {
    return 'Ortak liste güncellendi: $time';
  }

  @override
  String get quotesOffline =>
      'Sunucu bağlantısı yok — telefondaki kopya gösteriliyor';

  @override
  String quotesNotSent(int n) {
    return 'Gönderilmedi: $n. Bağlantı olunca sahip programına gider — tekrar için dokunun.';
  }

  @override
  String get quotesForbidden =>
      'Şirketin ortak ayarlarına yetki yok — değişiklikler yalnızca bu telefonda. Sahip veya yönetici girişi gerekir.';

  @override
  String get notSentMark => 'gönderilmedi';

  @override
  String quoteChangedElsewhere(String number) {
    return '$number daha sonra başka yerde değiştirildi — son sürüm gösteriliyor';
  }

  @override
  String quoteRenumbered(String from, String to) {
    return '$from numarası dolu — belge artık $to';
  }

  @override
  String get payOutcomeUnknown =>
      'Sunucu yanıt vermedi — satış gerçekleşmiş olabilir. Hiçbir şeyi değiştirmeden tekrar «Öde»ye dokunun: tekrar ikinci kez işlenmez. Borç ve bonus kullanımı bağlantı gerektirir.';

  @override
  String get debtDueLabel => 'Son ödeme tarihi';

  @override
  String debtDueDays(int n) {
    return '$n gün';
  }

  @override
  String get debtDueNotSaved =>
      'Borç vadesi sunucuya kaydedilmedi — web sitesinden düzeltin (varsayılan 30 gün).';

  @override
  String returnAmount(String sum) {
    return 'İade tutarı: $sum som';
  }

  @override
  String returnGive(String what) {
    return 'Müşteriye verilecek: $what';
  }

  @override
  String returnInCash(String sum) {
    return 'nakit $sum som';
  }

  @override
  String returnToCard(String sum) {
    return 'karta/havale ile $sum som';
  }

  @override
  String returnDebtReduced(String sum) {
    return 'Müşterinin borcu $sum som azaldı — bu para verilmez.';
  }

  @override
  String returnDebtUnknown(String sum) {
    return 'Dikkat: müşterinin kalan borcu sunucudan alınamadı. Hesaba göre borç $sum som azaldı, ancak müşteri bu fişin borcunu zaten ödediyse kendisine $sum som\'a kadar iade edilmeli. Ödemeden önce müşterinin borcunu kontrol edin.';
  }

  @override
  String get returnViaBank =>
      'Kart/havale kısmı — kasadan değil, banka üzerinden iade edin.';

  @override
  String returnPaidInfo(String paid, String done) {
    return 'Fiş için ödenen: $paid som · iade edilen: $done som';
  }

  @override
  String returnedQty(String qty) {
    return 'iade edildi: $qty';
  }

  @override
  String get returnNothingLeft => 'Bu fişteki her şey zaten iade edildi.';

  @override
  String cashOutTooMuch(String sum) {
    return 'Çekilen tutar kasadakinden fazla ($sum som).';
  }

  @override
  String payDebtToDebt(String sum) {
    return 'Borca mahsuben: $sum som';
  }

  @override
  String get payDebtOverCard => 'Havale ile — borçtan fazla olamaz.';

  @override
  String receiveNotAccepted(String reason) {
    return 'Mal kabul sunucu tarafından kabul edilmedi: $reason. Kayıt «Eşitleme»de.';
  }

  @override
  String get auditApply => 'Sayımı işle';

  @override
  String get auditApplyHint =>
      'Stoklar sayılan miktarlara eşitlenir — PC kasasındaki sayım gibi. Bağlantı gerekir.';

  @override
  String auditApplyAsk(int n, int a) {
    return 'Kontrol edilen ürün: $n, fark: $a. Stoklar sayılan miktarlara eşitlenecek. İşlensin mi?';
  }

  @override
  String get auditApplied => 'Sayım işlendi — stoklar güncellendi.';

  @override
  String get auditApplyQueued =>
      'Sayım belgesi oluşturuldu — işleme bağlantı gelince gönderilecek.';

  @override
  String get auditOnlineOnly =>
      'Sayım yalnızca sunucu bağlantısıyla işlenir — sayım kaydedildi, sonra tekrar deneyin.';

  @override
  String auditContinue(int n) {
    return 'Sayıma devam et ($n ürün)';
  }

  @override
  String get countedCash => 'Sayılan';

  @override
  String get openingCashLabel => 'Vardiya başında';

  @override
  String closeShiftFailed(int n) {
    return 'Sunucu $n fişi kabul etmedi — vardiya raporuna girmeyecek. Tekrar gönderin veya «Eşitleme»de kaldırın.';
  }

  @override
  String get closeShiftAsk => 'Vardiya kapatılsın mı?';

  @override
  String get closeShiftAskSub =>
      'Kapatıldıktan sonra bu vardiyada satış yapılamaz.';

  @override
  String get xReport => 'X raporu';

  @override
  String get foreignShiftHint =>
      'Bu kasada başka bir kasiyerin vardiyası açık — ona satış yapılamaz. Kendi vardiyanızı açın.';

  @override
  String get loadMore => 'Daha fazla';

  @override
  String get dropRow => 'Kaldır';

  @override
  String get dropRowAsk =>
      'Fiş kuyruktan kaldırılsın mı? Sunucuya gitmez, ürün telefondaki stoğa döner. Para alındıysa müşteriye iade edin veya satışı yeniden yapın.';

  @override
  String get dropRowAskStock =>
      'Kayıt kuyruktan kaldırılsın mı? Sunucuya gitmez — ürün stoğunu elle kontrol edin.';

  @override
  String get otherAccount => 'başka hesap';

  @override
  String otherAccountHint(String email) {
    return '$email girişiyle kaydedildi — bu kullanıcı adıyla giriş yapılınca gönderilir.';
  }

  @override
  String pAStockUnclear(String name, String now, String before) {
    return '$name: önceki deneme sunucudan yanıt alamadı ve stok o zamandan beri değişti (şu an $now, eklemeden önce $before idi). İki katına çıkmasın diye tekrar EKLENMEDİ. Stoğu sayımla kontrol edin: eklendiyse satırı silin, eklenmediyse silip ürünü yeniden ekleyin.';
  }

  @override
  String get pAWeightLabel =>
      'Bu bir terazi etiketi (tek alışverişin ağırlığı) — mal kabulünde kullanılmaz. Ürün barkodunu tarayın veya adıyla bulun.';

  @override
  String get pANoServerCheck =>
      'Sunucuyla bağlantı yok — bu barkodlu ürünün zaten olup olmadığı kontrol edilemiyor. Bağlantı gelince tekrar deneyin.';

  @override
  String pALinesLeft(int n) {
    return 'Kabul edilmeyen satır: $n — listede kaldılar, düzeltip yeniden işleyin.';
  }

  @override
  String get pAPluLabel => 'Terazi için PLU';

  @override
  String get pAPluHint => 'boş — otomatik atanır';

  @override
  String get pAStockAdd => 'Stoğa ekle';

  @override
  String get pAStockSet => 'Stoğu ayarla';

  @override
  String get pAStockAddHint =>
      'Dosyadaki miktar, mal kabulü gibi stoğa eklenir.';

  @override
  String get pAStockSetHint =>
      'Stok dosyadaki miktara eşit olur (değiştirilir, eklenmez).';

  @override
  String pADupRow(int n) {
    return '$n. satırın tekrarı — atlanıyor';
  }

  @override
  String get pAUploaded => 'yüklendi';

  @override
  String get rightCashier => 'Kasiyer arayüzü';

  @override
  String get rightOrders => 'Site siparişleri';

  @override
  String get rightProcurement => 'Satın almalar';

  @override
  String get rightSuppliers => 'Tedarikçiler';

  @override
  String get rightSelfLocked =>
      'Kendi yetkilerinizi kapatamazsınız — bunu sahip veya başka bir yönetici yapar.';

  @override
  String get staffYou => 'bu sizsiniz';

  @override
  String get screenUnavailable =>
      'Ekran yeniden açıldı ve verileri kayboldu. Geri dönüp tekrar açın.';

  @override
  String get cancelOrder => 'Siparişi iptal et';

  @override
  String cancelOrderAsk(String number) {
    return '$number numaralı sipariş iptal edilsin mi? Müşteri onu teslim almayacak.';
  }

  @override
  String pickShortAsk(String got, String need) {
    return '$need üründen $got toplandı. Sipariş eksik toplanmış olarak işaretlensin mi?';
  }

  @override
  String pickupCodeLabel(String code) {
    return 'Teslim kodu: $code';
  }

  @override
  String get torchToggle => 'El feneri';

  @override
  String get payTransferQr => 'Havale / QR';

  @override
  String get reportLoadFailed =>
      'Rapor yüklenmedi. Bağlantıyı kontrol edip «Tekrar dene»ye basın.';

  @override
  String get rentalLatePerDay => 'Gecikilen gün başına ceza';

  @override
  String rentalLateHint(int n, String rate, String sum) {
    return 'Gecikme $n gün × $rate = $sum som (kiralama koşullarına göre)';
  }

  @override
  String rentalBackDeposit(String sum) {
    return 'Depozitoyu iade edin: $sum som';
  }

  @override
  String rentalWithheld(String sum) {
    return 'tutulan $sum som';
  }

  @override
  String rentalBackDocument(String doc) {
    return 'Belgeyi iade edin: $doc';
  }

  @override
  String rentalExcess(String sum) {
    return 'Depozitoyu aşan ceza: $sum som — fişe eklenecek';
  }

  @override
  String rentalPenaltyLine(int n) {
    return 'Kiralama #$n cezası';
  }

  @override
  String rentalPenaltyInCart(String sum) {
    return 'İade alındı. $sum som ceza fişe eklendi — ödemeyi alın.';
  }

  @override
  String get waybillDoc => 'İrsaliye';

  @override
  String get waybillsFilter => 'İrsaliyeler';

  @override
  String get docPayMarkOnly =>
      'Bu yalnızca belgede bir işarettir: para vardiya kasasına ve müşteri borcu NurCRM\'e yazılmaz. Parayı kasadan geçirmek için \"Satışı yap\"ı kullanın.';

  @override
  String get docPayMarkOk => 'Ödemeyi işaretle';

  @override
  String get docSellCartBusy =>
      'Fişte zaten ürünler var. Belgenin ürünleriyle değiştirilsin mi?';

  @override
  String get docSellReplace => 'Değiştir';

  @override
  String get docSentToSale => 'Satışa aktarıldı';

  @override
  String docSellMissing(String names) {
    return 'Telefon kataloğunda yok — elle ekleyin: $names';
  }

  @override
  String docVatIncl(String p) {
    return 'KDV dahil $p%';
  }

  @override
  String get prnQr => 'Fişte QR kod';

  @override
  String get prnQrHint =>
      'Mali fiş doğrulama veya e-fiş. Yazıcı QR yerine fazladan karakter basıyorsa kapatın.';

  @override
  String get prnDrawer => 'Yazıcı üzerinden para çekmecesi';

  @override
  String get prnDrawerHint =>
      'Nakit satıştan sonra açılır (çekmece yazıcıya kabloyla bağlı).';

  @override
  String prnDrawerPin(int n) {
    return 'Pin $n';
  }

  @override
  String get prnDrawerTest => 'Çekmeceyi aç';

  @override
  String get prnDrawerSent =>
      'Çekmece komutu gönderildi. Açılmadıysa diğer pini seçin.';

  @override
  String get stepPhoto => 'Fotoğraf';

  @override
  String get stepBarcode => 'Barkod';

  @override
  String get bindTitle =>
      'Fotoğraflar kaydedildi ✓ — şimdi ürünün barkodunu tarayın';

  @override
  String get bindSub => 'Üründe barkod yoksa «Atla»ya basın';

  @override
  String get bindSkip => 'Atla';

  @override
  String get bindManual => 'Elle gir';

  @override
  String get bindManualHint => 'Barkodun altındaki rakamlar';

  @override
  String get bindChecking => 'Barkod kontrol ediliyor…';

  @override
  String bindWillLink(String code, String name) {
    return '$code barkodu «$name» ürününe bağlanacak';
  }

  @override
  String bindWillLinkNew(String code) {
    return '$code barkodu yeni ürüne bağlanacak';
  }

  @override
  String bindAlreadyOwn(String code, String name) {
    return '$code barkodu zaten «$name» ürününe bağlı — değiştirmeye gerek yok';
  }

  @override
  String get bindTakenTitle => 'Barkod başka bir üründe';

  @override
  String bindTakenAsk(String code, String name) {
    return '$code barkodu zaten «$name» ürününe bağlı.\n«Değiştir» — barkod bu ürüne geçer.\n«Bırak» — «$name» üründe kalır, bu ürün barkodsuz olur.';
  }

  @override
  String bindTakenAskNew(String code, String name) {
    return '$code barkodu zaten «$name» ürününe bağlı — bu ürün depoda zaten olabilir.\n«Bırak» — barkod «$name» üründe kalır, yeni ürün barkodsuz olur.';
  }

  @override
  String get bindReplace => 'Değiştir';

  @override
  String get bindKeep => 'Bırak';

  @override
  String get bindRescan => 'Yeniden tara';

  @override
  String bindLinked(String code, String name) {
    return '$code barkodu «$name» ürününe bağlandı';
  }

  @override
  String get teachNext =>
      'Ürünü 3–5 açıdan çekin, sonra «Tamam» — sıradaki adım barkod';

  @override
  String get teachNextNoCode => 'Ürünü 3–5 açıdan çekin, sonra «Tamam»a basın';

  @override
  String get liveNextSale =>
      'Bulunan ürünler aşağıda görünür — fişe eklemek için birine dokunun';

  @override
  String get liveNextList =>
      'Çekimden sonra kamera bulduklarını listeler — listeyi düzeltebilirsiniz';

  @override
  String get scanCodesHint =>
      'Çerçeveyi barkoda doğrultun — kod kendiliğinden kaydedilir. Sonra «Tamam»a basın';

  @override
  String get weighNext => 'Kamera ürünü tanır, ağırlığı okur ve fişe ekler';

  @override
  String get weighManualBtn => 'Kamerasız — ürünü seçip ağırlığı girin';

  @override
  String get peEdit => 'Ürünü düzenle';

  @override
  String get peName => 'Ad';

  @override
  String get peMinQty => 'Asgari stok';

  @override
  String get peMinQtyHint => 'Bunun altında ürün \"Azalıyor\" listesine girer';

  @override
  String get peStock => 'Stok';

  @override
  String get peStockHint =>
      'Stok yalnızca belgeyle değişir — gerekçeli giriş, zayiat veya sayım, bilgisayardaki gibi. Böylece tutarsızlık olmaz.';

  @override
  String get peIncome => 'Giriş';

  @override
  String get peWriteOff => 'Zayiat';

  @override
  String get peRecount => 'Sayım';

  @override
  String get peQtyIn => 'Kaç adet geldi';

  @override
  String get peQtyOut => 'Kaç adet düşülecek';

  @override
  String get peQtyFact => 'Gerçekte kaç adet';

  @override
  String get peReason => 'Gerekçe';

  @override
  String get peReasonNeed => 'Gerekçeyi girin.';

  @override
  String get peReasonExpired => 'Son kullanma tarihi geçti';

  @override
  String get peReasonDefect => 'Kusurlu';

  @override
  String get peReasonSpoiled => 'Bozuldu';

  @override
  String get peReasonTheft => 'Hırsızlık';

  @override
  String get peReasonOther => 'Diğer';

  @override
  String get peReasonSupplier => 'Tedarik';

  @override
  String get peReasonCount => 'Yeniden sayım';

  @override
  String get peApply => 'İşle';

  @override
  String peStockDone(String from, String to) {
    return 'Stok: $from → $to';
  }

  @override
  String peStockSame(String qty) {
    return 'Stok zaten $qty — hiçbir şey değişmedi.';
  }

  @override
  String get peStockQueued =>
      'Belge oluşturuldu — bağlantı gelince kendiliğinden işlenecek.';

  @override
  String get peStockOffline =>
      'Sunucuyla bağlantı yok — stok alınamadı, hiçbir şey değişmedi.';

  @override
  String peStockBelowZero(String qty) {
    return 'Stoktan fazla düşülemez (mevcut $qty).';
  }

  @override
  String get peSaved => 'Ürün kaydedildi.';

  @override
  String get peNoChanges => 'Değişiklik yok.';

  @override
  String pePriceRestored(String price) {
    return 'Sunucu satış fiyatını kâr marjına göre yeniden hesapladı — girdiğiniz $price fiyatı geri yüklendi.';
  }

  @override
  String get pePriceLocked =>
      'Fiyatları \"Fiyat değiştirme\" yetkisi olan çalışan değiştirir.';

  @override
  String get peLoadFailed =>
      'Ürün kartı sunucudan yüklenemedi — düzenleme bağlantı varken yapılabilir.';

  @override
  String get peNameNeed => 'Adı girin.';

  @override
  String peBarcodeTaken(String code, String name) {
    return '$code barkodu zaten \"$name\" ürününde.';
  }

  @override
  String peBarcodeTwice(String code) {
    return '$code barkodu iki kez girilmiş.';
  }

  @override
  String get peScan => 'Tara';

  @override
  String get bcGenTooltip => 'Barkod oluştur';

  @override
  String get bcGenReplace => 'Mevcut barkod yenisiyle değiştirilsin mi?';

  @override
  String get bcGenDo => 'Oluştur';

  @override
  String pePackQty(String qty) {
    return 'pakette $qty';
  }

  @override
  String get peNoAccess =>
      'Ürünü \"Depo\" yetkisi olan çalışan veya yönetici düzenleyebilir.';
}
