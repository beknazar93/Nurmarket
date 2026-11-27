import 'package:flutter/widgets.dart';

import '../../core/util/ui.dart' show L10nX;
import 'marketplace_data.dart';

/// 2026-11-27 (Маркетплейс мобилки): надписи раздела на 5 языках — свой файл, а не .arb (в .arb в это время пишут другие исполнители; конфликты слияния ни к чему).
/// Порядок аргументов — как у `context.t5`: ru, ky, uz, en, tr. Тексты «что делает» функций сюда не входят — они в assets/marketplace/content.json (из кассы).
class Ms {
  Ms._();

  static String title(BuildContext c) => c.t5('Маркетплейс', 'Маркетплейс', 'Marketplace', 'Marketplace', 'Marketplace');
  static String tabFeatures(BuildContext c) => c.t5('Функции', 'Функциялар', 'Funksiyalar', 'Features', 'Özellikler');
  static String tabTariffs(BuildContext c) => c.t5('Тарифы', 'Тарифтер', 'Tariflar', 'Plans', 'Tarifeler');
  static String searchHint(BuildContext c) => c.t5('Найти функцию', 'Функцияны табуу', 'Funksiyani topish', 'Find a feature', 'Özellik ara');
  static String fAll(BuildContext c) => c.t5('Все', 'Баары', 'Hammasi', 'All', 'Tümü');
  static String fOn(BuildContext c) => c.t5('Подключённые', 'Күйгүзүлгөн', 'Yoqilgan', 'Enabled', 'Açık');
  static String fAvail(BuildContext c) => c.t5('Доступные', 'Жеткиликтүү', 'Mavjud', 'Available', 'Uygun');
  static String statusOn(BuildContext c) => c.t5('Подключён', 'Күйгүзүлгөн', 'Yoqilgan', 'Enabled', 'Etkin');
  static String statusAvail(BuildContext c) => c.t5('Доступен', 'Жеткиликтүү', 'Mavjud', 'Available', 'Uygun');
  static String statusSoon(BuildContext c) => c.t5('Скоро', 'Жакында', 'Tez orada', 'Coming soon', 'Yakında');
  static String includedSphere(BuildContext c) => c.t5('Входит в вашу сферу', 'Сиздин чөйрөгө кирет', 'Sohangizga kiradi', 'Included in your sphere', 'Alanınıza dahil');
  static String trialUntil(BuildContext c, DateTime d) {
    final s = '${d.day.toString().padLeft(2, '0')}.${d.month.toString().padLeft(2, '0')}.${d.year}';
    return c.t5('Проба до $s', 'Сыноо $s чейин', 'Sinov $s gacha', 'Trial until $s', 'Deneme: $s tarihine kadar');
  }

  static String trialPeriod(BuildContext c) => c.t5('Пробный период', 'Сыноо мөөнөтү', 'Sinov muddati', 'Trial period', 'Deneme süresi');

  /// Цена: «200 сом/мес» (валюта сервера KGS → «сом»; другая — как есть).
  static String priceMonth(BuildContext c, double v, String currency) {
    final n = _num(v);
    final cur = currency == 'KGS' ? c.t5('сом', 'сом', "so'm", 'som', 'som') : currency;
    final per = c.t5('/мес', '/ай', '/oy', '/mo', '/ay');
    return '$n $cur$per';
  }

  static String priceYear(BuildContext c, double v, String currency) {
    final cur = currency == 'KGS' ? c.t5('сом', 'сом', "so'm", 'som', 'som') : currency;
    return '${_num(v)} $cur${c.t5('/год', '/жыл', '/yil', '/yr', '/yıl')}';
  }

  static String money(BuildContext c, double v, String currency) {
    final cur = currency == 'KGS' ? c.t5('сом', 'сом', "so'm", 'som', 'som') : currency;
    return '${_num(v)} $cur';
  }

  static String _num(double v) {
    final t = v == v.roundToDouble() ? v.toInt().toString() : v.toStringAsFixed(2);
    final neg = t.startsWith('-');
    final body = neg ? t.substring(1) : t;
    final parts = body.split('.');
    final buf = StringBuffer();
    for (var i = 0; i < parts[0].length; i++) {
      if (i > 0 && (parts[0].length - i) % 3 == 0) buf.write(' ');
      buf.write(parts[0][i]);
    }
    return '${neg ? '-' : ''}$buf${parts.length > 1 ? ',${parts[1]}' : ''}';
  }

  static String priceTbd(BuildContext c) => c.t5('Цена уточняется', 'Баасы тактала элек', 'Narx aniqlanmoqda', 'Price to be confirmed', 'Fiyat belirleniyor');
  static String yourSphere(BuildContext c) => c.t5('ваша сфера', 'сиздин чөйрө', 'sizning sohangiz', 'your sphere', 'alanınız');
  static String offline(BuildContext c) => c.t5(
    'Нет связи — показаны сохранённые данные. Включать и выключать функции можно, когда появится интернет.',
    'Байланыш жок — сакталган маалымат көрсөтүлдү. Функцияларды интернет пайда болгондо күйгүзүп-өчүрсө болот.',
    "Aloqa yo'q — saqlangan ma'lumotlar ko'rsatilmoqda. Funksiyalarni internet paydo bo'lganda yoqish va o'chirish mumkin.",
    'No connection — saved data is shown. You can turn features on and off when you are online.',
    'Bağlantı yok — kayıtlı veriler gösteriliyor. Özellikleri internet geldiğinde açıp kapatabilirsiniz.',
  );
  static String stateUnknown(BuildContext c) => c.t5(
    'Состояние функций не загрузилось — потяните вниз, чтобы обновить.',
    'Функциялардын абалы жүктөлгөн жок — жаңыртуу үчүн ылдый тартыңыз.',
    "Funksiyalar holati yuklanmadi — yangilash uchun pastga torting.",
    'The state of the features did not load — pull down to refresh.',
    'Özelliklerin durumu yüklenemedi — yenilemek için aşağı çekin.',
  );
  static String loadFail(BuildContext c) => c.t5(
    'Не удалось загрузить Маркетплейс. Проверьте интернет и повторите.',
    'Маркетплейсти жүктөө мүмкүн болбоду. Интернетти текшерип, кайра аракет кылыңыз.',
    "Marketplace'ni yuklab bo'lmadi. Internetni tekshirib, qayta urinib ko'ring.",
    'Could not load the Marketplace. Check the internet and try again.',
    "Marketplace yüklenemedi. İnterneti kontrol edip tekrar deneyin.",
  );
  static String retry(BuildContext c) => c.t5('Повторить', 'Кайталоо', 'Qayta urinish', 'Retry', 'Tekrar dene');
  static String nothingFound(BuildContext c) => c.t5('Ничего не найдено', 'Эч нерсе табылган жок', 'Hech narsa topilmadi', 'Nothing found', 'Bir şey bulunamadı');

  // страница функции
  static String whatItDoes(BuildContext c) => c.t5('Что делает', 'Эмне кылат', 'Nima qiladi', 'What it does', 'Ne yapar');
  static String gains(BuildContext c) => c.t5('Что получите', 'Эмне аласыз', 'Nima olasiz', 'What you get', 'Ne kazanırsınız');
  static String howItWorks(BuildContext c) => c.t5('Как это работает', 'Кантип иштейт', 'Qanday ishlaydi', 'How it works', 'Nasıl çalışır');
  static String howToEnable(BuildContext c) => c.t5('Как включить', 'Кантип күйгүзүү', 'Qanday yoqish', 'How to turn it on', 'Nasıl açılır');
  static String forWhom(BuildContext c) => c.t5('Для кого', 'Кимдер үчүн', 'Kimlar uchun', 'Who it is for', 'Kimler için');
  static String priceTitle(BuildContext c) => c.t5('Цена', 'Баасы', 'Narxi', 'Price', 'Fiyat');
  static String stepsNote(BuildContext c) => c.t5(
    'Шаги описаны для кассы; включение действует на всю компанию.',
    'Кадамдар кассага жазылган; күйгүзүү бүт компанияга таасир этет.',
    "Qadamlar kassa uchun yozilgan; yoqish butun kompaniyaga ta'sir qiladi.",
    'Steps are written for the till; turning a feature on applies to the whole company.',
    'Adımlar kasa için yazılmıştır; açma işlemi tüm şirket için geçerlidir.',
  );
  static String noDescription(BuildContext c) => c.t5(
    'Функция из набора модулей: включается и выключается здесь.',
    'Модулдар топтомунан функция: ушул жерден күйгүзүлөт жана өчүрүлөт.',
    "Modullar to'plamidan funksiya: shu yerda yoqiladi va o'chiriladi.",
    'A feature from the module set: turn it on and off here.',
    'Modül setinden bir özellik: burada açılıp kapatılır.',
  );
  static String priceTermsPaid(BuildContext c, String price) => c.t5(
    '$price, цена с сервера. Включается и выключается здесь, разовой платы нет.',
    '$price, баа серверден. Ушул жерден күйгүзүлөт жана өчүрүлөт, бир жолку төлөм жок.',
    "$price, narx serverdan. Shu yerda yoqiladi va o'chiriladi, bir martalik to'lov yo'q.",
    '$price, the price comes from the server. Turn it on and off here, no one-time fee.',
    '$price, fiyat sunucudan gelir. Burada açılıp kapatılır, tek seferlik ücret yok.',
  );
  static String priceTermsIncluded(BuildContext c) => c.t5(
    'Входит в вашу сферу — без доплаты. Функцию можно выключить здесь, а потом включить снова.',
    'Сиздин чөйрөгө кирет — кошумча төлөмсүз. Функцияны ушул жерде өчүрүп, кийин кайра күйгүзсө болот.',
    "Sohangizga kiradi — qo'shimcha to'lovsiz. Funksiyani shu yerda o'chirib, keyin qayta yoqish mumkin.",
    'Included in your sphere — no extra charge. You can turn the feature off here and on again later.',
    'Alanınıza dahil — ek ücret yok. Özelliği burada kapatıp daha sonra tekrar açabilirsiniz.',
  );
  static String priceTermsUnknown(BuildContext c) => c.t5(
    'Цену задаёт сервер (в месяц) — она появится здесь, как только загрузится. Разовой платы нет.',
    'Баасын сервер коёт (айына) — жүктөлгөндө бул жерде пайда болот. Бир жолку төлөм жок.',
    "Narxni server belgilaydi (oyiga) — yuklangach shu yerda paydo bo'ladi. Bir martalik to'lov yo'q.",
    'The price is set by the server (per month) — it appears here once loaded. No one-time fee.',
    'Fiyatı sunucu belirler (aylık) — yüklenince burada görünür. Tek seferlik ücret yok.',
  );
  static String linkedNote(BuildContext c) => c.t5(
    'Привязано к модулю «Ремонт»: работает, пока он включён, отдельной платы нет. Страница статуса — на вашей онлайн-витрине.',
    '«Оңдоо» модулуна байланган: ал күйгүзүлгөндө иштейт, өзүнчө төлөм жок. Абал барагы — онлайн-витринаңызда.',
    "«Ta'mir» moduliga bog'langan: u yoqilganda ishlaydi, alohida to'lov yo'q. Holat sahifasi onlayn vitrinangizda.",
    'Tied to the “Repair” module: works while it is on, no separate fee. The status page is on your online storefront.',
    '«Tamir» modülüne bağlı: o açıkken çalışır, ayrı ücret yok. Durum sayfası çevrimiçi vitrininizde.',
  );
  static String soonNote(BuildContext c) => c.t5(
    'Скоро: ждём сервер NurCRM. Работает только при включённом модуле «Ремонт» и онлайн-витрине.',
    'Жакында: NurCRM серверин күтөбүз. «Оңдоо» модулу жана онлайн-витрина күйгүзүлгөндө гана иштейт.',
    "Tez orada: NurCRM serverini kutmoqdamiz. Faqat «Ta'mir» moduli va onlayn vitrina yoqilganda ishlaydi.",
    'Coming soon: waiting for the NurCRM server. Works only with the “Repair” module turned on and an online storefront.',
    'Yakında: NurCRM sunucusunu bekliyoruz. Yalnızca «Tamir» modülü ve çevrimiçi vitrin açıkken çalışır.',
  );
  static String need(BuildContext c) => c.t5('Что нужно', 'Эмне керек', 'Nima kerak', 'What you need', 'Neler gerekir');
  static String included(BuildContext c) => c.t5('Входит в программу', 'Программага кирет', 'Dasturga kiradi', 'Included in the program', 'Programa dahil');
  static String availableNow(BuildContext c) => c.t5('Доступно', 'Жеткиликтүү', 'Mavjud', 'Available', 'Kullanılabilir');
  static String supportService(BuildContext c) => c.t5('Цену называет поддержка', 'Баасын колдоо айтат', "Narxni qo'llab-quvvatlash aytadi", 'Support names the price', 'Fiyatı destek söyler');
  static String termsBuiltin(BuildContext c) => c.t5('Входит в программу без доплаты.', 'Программага кошумча төлөмсүз кирет.', "Dasturga qo'shimcha to'lovsiz kiradi.", 'Included in the program at no extra charge.', 'Programa ek ücret olmadan dahildir.');
  static String termsService(BuildContext c) => c.t5('Цену и срок называет поддержка до начала работы.', 'Баасын жана мөөнөтүн иш башталганга чейин колдоо айтат.', "Narx va muddatni ish boshlanishidan oldin qo'llab-quvvatlash aytadi.", 'Support names the price and the term before the work starts.', 'Fiyatı ve süreyi iş başlamadan önce destek söyler.');
  static String termsCapabilityOn(BuildContext c) => c.t5('Функция доступна вашей компании; включается в настройках бота магазина.', 'Функция компанияңыз үчүн жеткиликтүү; дүкөндүн боту орнотууларынан күйгүзүлөт.', "Funksiya kompaniyangiz uchun mavjud; do'kon boti sozlamalarida yoqiladi.", "The feature is available to your company; turn it on in the shop bot's settings.", 'Özellik şirketiniz için kullanılabilir; mağaza botunun ayarlarından açılır.');
  static String termsCapabilitySoon(BuildContext c) => c.t5('Скоро: функцию открывает сервер NurCRM — она появится здесь сама.', 'Жакында: функцияны NurCRM сервери ачат — ал бул жерде өзү пайда болот.', "Tez orada: funksiyani NurCRM serveri ochadi — u shu yerda o'zi paydo bo'ladi.", 'Coming soon: the NurCRM server opens the feature — it will appear here by itself.', 'Yakında: özelliği NurCRM sunucusu açar — burada kendiliğinden görünecek.');
  static String termsFiscal(BuildContext c) => c.t5('Режим включает NurCRM по заявке; цену называет NurCRM.', 'Режимди NurCRM өтүнмө боюнча күйгүзөт; баасын NurCRM айтат.', "Rejimni NurCRM ariza bo'yicha yoqadi; narxni NurCRM aytadi.", 'NurCRM turns the mode on on request and names the price.', 'Modu NurCRM talep üzerine açar; fiyatı NurCRM söyler.');
  static String demoStep(BuildContext c, int i, int n) => c.t5('Шаг $i из $n', 'Кадам $i / $n', 'Qadam $i / $n', 'Step $i of $n', 'Adım $i / $n');
  static String demoPlay(BuildContext c) => c.t5('Запустить показ', 'Көрсөтүүнү баштоо', "Namoyishni boshlash", 'Play', 'Oynat');
  static String demoPause(BuildContext c) => c.t5('Остановить показ', 'Көрсөтүүнү токтотуу', "Namoyishni to'xtatish", 'Pause', 'Duraklat');
  static String demoNote(BuildContext c) => c.t5(
    'Учебный пример: имена и суммы выдуманные.',
    'Окуу үлгүсү: ысымдар жана суммалар ойдон чыгарылган.',
    "O'quv misoli: ismlar va summalar o'ylab topilgan.",
    'A sample for learning: names and amounts are made up.',
    'Öğretici örnek: isimler ve tutarlar uydurmadır.',
  );

  // действия
  static String enable(BuildContext c) => c.t5('Включить', 'Күйгүзүү', 'Yoqish', 'Turn on', 'Aç');
  static String disable(BuildContext c) => c.t5('Выключить', 'Өчүрүү', "O'chirish", 'Turn off', 'Kapat');
  static String tryDays(BuildContext c, int n) => c.t5('Попробовать $n дн.', '$n күн сынап көрүү', '$n kun sinab ko\'rish', 'Try for $n days', '$n gün dene');
  static String cancel(BuildContext c) => c.t5('Отмена', 'Жокко чыгаруу', 'Bekor qilish', 'Cancel', 'İptal');
  static String confirmEnableTitle(BuildContext c, String name) => c.t5('Включить «$name»?', '«$name» күйгүзөбүзбү?', '«$name» yoqilsinmi?', 'Turn on “$name”?', '«$name» açılsın mı?');
  static String confirmDisableTitle(BuildContext c, String name) => c.t5('Выключить «$name»?', '«$name» өчүрөбүзбү?', "«$name» o'chirilsinmi?", 'Turn off “$name”?', '«$name» kapatılsın mı?');
  static String confirmDisableBody(BuildContext c) => c.t5(
    'Функция перестанет работать в кассе и программе. Включить её снова можно в любой момент.',
    'Функция кассада жана программада иштебей калат. Аны каалаган убакта кайра күйгүзсөңүз болот.',
    "Funksiya kassa va dasturda ishlamay qoladi. Uni istalgan vaqtda qayta yoqish mumkin.",
    'The feature will stop working in the till and the program. You can turn it on again at any time.',
    'Özellik kasada ve programda çalışmayı durdurur. İstediğiniz zaman tekrar açabilirsiniz.',
  );
  static String doneOn(BuildContext c, String name) => c.t5('«$name» включено', '«$name» күйгүзүлдү', '«$name» yoqildi', '“$name” turned on', '«$name» açıldı');
  static String doneOff(BuildContext c, String name) => c.t5('«$name» выключено', '«$name» өчүрүлдү', "«$name» o'chirildi", '“$name” turned off', '«$name» kapatıldı');
  static String doneTrial(BuildContext c, String name) => c.t5('Проба «$name» началась', '«$name» сыноосу башталды', '«$name» sinovi boshlandi', 'Trial of “$name” started', '«$name» denemesi başladı');

  /// Понятный текст итога действия (те же формулировки, что у кассы, CompanyModules.Describe).
  static String describe(BuildContext c, MpChangeResult r) {
    switch (r.status) {
      case MpChange.forbidden:
        return c.t5(
          'Модули меняет только владелец или администратор компании.',
          'Модулдарды компаниянын ээси же администратору гана өзгөртөт.',
          "Modullarni faqat kompaniya egasi yoki administrator o'zgartira oladi.",
          'Only the company owner or an administrator can change modules.',
          'Modülleri yalnızca şirket sahibi veya yönetici değiştirebilir.',
        );
      case MpChange.noConnection:
        return c.t5(
          'Нет связи с сервером — повторите, когда появится интернет.',
          'Сервер менен байланыш жок — интернет пайда болгондо кайра аракет кылыңыз.',
          "Server bilan aloqa yo'q — internet paydo bo'lganda qayta urinib ko'ring.",
          'No connection to the server — try again when you are online.',
          'Sunucuyla bağlantı yok — internet geldiğinde tekrar deneyin.',
        );
      case MpChange.unknownModule:
        return c.t5(
          'Сервер не знает эту функцию — обновите приложение или обратитесь в поддержку.',
          'Сервер бул функцияны билбейт — колдонмону жаңыртыңыз же колдоо кызматына кайрылыңыз.',
          "Server bu funksiyani bilmaydi — ilovani yangilang yoki qo'llab-quvvatlashga murojaat qiling.",
          'The server does not know this feature — update the app or contact support.',
          'Sunucu bu özelliği tanımıyor — uygulamayı güncelleyin veya destekle iletişime geçin.',
        );
      case MpChange.conflict:
        return c.t5(
          'Модули только что изменили в другом месте — список обновлён, повторите действие.',
          'Модулдар дагы бир жерде эми өзгөртүлдү — тизме жаңыртылды, аракетти кайталаңыз.',
          "Modullar hozirgina boshqa joyda o'zgartirildi — ro'yxat yangilandi, amalni takrorlang.",
          'The modules were just changed elsewhere — the list is refreshed, repeat the action.',
          'Modüller az önce başka yerde değiştirildi — liste yenilendi, işlemi tekrarlayın.',
        );
      case MpChange.trialUsed:
        return c.t5(
          'Пробный период этой функции уже использован. Её можно включить обычным порядком — цена на странице.',
          'Бул функциянын сыноо мөөнөтү мурда колдонулган. Аны кадимки тартипте күйгүзсө болот — баасы беттен көрүнөт.',
          "Bu funksiyaning sinov muddati allaqachon ishlatilgan. Uni odatdagidek yoqish mumkin — narxi sahifada.",
          'The trial period of this feature has already been used. You can turn it on in the usual way — the price is on the page.',
          'Bu özelliğin deneme süresi daha önce kullanıldı. Olağan şekilde açılabilir — fiyat sayfada.',
        );
      case MpChange.alreadyActive:
        return c.t5('Функция уже включена — пробный период не нужен.', 'Функция мурунтан эле күйгүзүлгөн — сыноо мөөнөтү керек эмес.', "Funksiya allaqachon yoqilgan — sinov kerak emas.", 'The feature is already on — no trial is needed.', 'Özellik zaten açık — deneme gerekmez.');
      case MpChange.unsupported:
        return c.t5('Этот сервер пока не умеет менять функции.', 'Бул сервер функцияларды азырынча өзгөртө албайт.', "Bu server funksiyalarni hali o'zgartira olmaydi.", 'This server cannot change features yet.', 'Bu sunucu henüz özellikleri değiştiremiyor.');
      case MpChange.ok:
      case MpChange.failed:
        return (r.detail != null && r.detail!.trim().isNotEmpty)
            ? r.detail!
            : c.t5('Не удалось изменить функцию. Повторите позже.', 'Функцияны өзгөртүү мүмкүн болбоду. Кийинчерээк аракет кылыңыз.', "Funksiyani o'zgartirib bo'lmadi. Keyinroq urinib ko'ring.", 'Could not change the feature. Try again later.', 'Özellik değiştirilemedi. Daha sonra tekrar deneyin.');
    }
  }

  // тарифы и «Своё»
  static String yourPlan(BuildContext c) => c.t5('Ваш тариф', 'Сиздин тариф', 'Sizning tarifingiz', 'Your plan', 'Tarifeniz');
  static String trialDaysLabel(BuildContext c, int n) => c.t5('Пробный период $n дн.', 'Сыноо мөөнөтү $n күн', 'Sinov muddati $n kun', '$n-day trial', '$n gün deneme');
  static String yearOffer(BuildContext c, String price, int paid) => c.t5(
    'Год — $price: $paid месяцев вместо 12',
    'Жыл — $price: 12 ордуна $paid ай',
    "Yil — $price: 12 oy o'rniga $paid oy",
    'Year — $price: $paid months instead of 12',
    'Yıl — $price: 12 yerine $paid ay',
  );
  static String customTitle(BuildContext c) => c.t5('Тариф «Своё»', '«Өзүмдүкү» тарифи', "«O'zimniki» tarifi", '“Custom” plan', '«Özel» tarife');
  static String customHint(BuildContext c) => c.t5(
    'Соберите тариф из нужных функций — цену посчитает сервер. Отметьте, что добавить или убрать, и отправьте заявку.',
    'Керектүү функциялардан тариф түзүңүз — баасын сервер эсептейт. Эмнени кошууну же алып салууну белгилеп, арыз жөнөтүңүз.',
    "Kerakli funksiyalardan tarif tuzing — narxni server hisoblaydi. Nimani qo'shish yoki olib tashlashni belgilang va ariza yuboring.",
    'Build a plan from the features you need — the server works out the price. Tick what to add or remove and send a request.',
    'İhtiyacınız olan özelliklerden bir tarife oluşturun — fiyatı sunucu hesaplar. Neyin ekleneceğini veya çıkarılacağını işaretleyip talep gönderin.',
  );
  static String packsTitle(BuildContext c) => c.t5('Пакеты', 'Пакеттер', 'Paketlar', 'Packs', 'Paketler');
  static String calcBtn(BuildContext c) => c.t5('Рассчитать цену', 'Баасын эсептөө', 'Narxni hisoblash', 'Calculate the price', 'Fiyatı hesapla');
  static String sendBtn(BuildContext c) => c.t5('Отправить заявку', 'Арыз жөнөтүү', 'Ariza yuborish', 'Send the request', 'Talep gönder');
  static String periodMonth(BuildContext c) => c.t5('В месяц', 'Айына', 'Oyiga', 'Monthly', 'Aylık');
  static String periodYear(BuildContext c) => c.t5('В год', 'Жылына', 'Yiliga', 'Yearly', 'Yıllık');
  static String totalMonth(BuildContext c) => c.t5('Итого в месяц', 'Айына жыйынтыгы', 'Oyiga jami', 'Total per month', 'Aylık toplam');
  static String totalYear(BuildContext c) => c.t5('Итого за год', 'Жылына жыйынтыгы', 'Yiliga jami', 'Total per year', 'Yıllık toplam');
  static String baseLine(BuildContext c) => c.t5('Основа тарифа', 'Тариф негизи', 'Tarif asosi', 'Plan base', 'Tarife tabanı');
  static String extraLine(BuildContext c) => c.t5('Дополнительно', 'Кошумча', "Qo'shimcha", 'Extra', 'Ek');
  static String nothingSelected(BuildContext c) => c.t5('Отметьте, что добавить или убрать.', 'Эмнени кошуу же алып салууну белгилеңиз.', "Nimani qo'shish yoki olib tashlashni belgilang.", 'Tick what to add or remove.', 'Neyin ekleneceğini veya çıkarılacağını işaretleyin.');
  static String ownerOnly(BuildContext c) => c.t5(
    'Заявку на «Своё» отправляет только владелец компании.',
    '«Өзүмдүкү» тарифине арызды компаниянын ээси гана жөнөтөт.',
    "«O'zimniki» tarifiga arizani faqat kompaniya egasi yuboradi.",
    'Only the company owner can send the “Custom” request.',
    '«Özel» talebini yalnızca şirket sahibi gönderebilir.',
  );
  static String requestSent(BuildContext c) => c.t5(
    'Заявка отправлена — менеджер свяжется с вами.',
    'Арыз жөнөтүлдү — менеджер сиз менен байланышат.',
    "Ariza yuborildi — menejer siz bilan bog'lanadi.",
    'The request is sent — a manager will contact you.',
    'Talep gönderildi — bir yönetici sizinle iletişime geçecek.',
  );
  static String requestFail(BuildContext c) => c.t5('Не удалось. Проверьте связь и повторите.', 'Ишке ашкан жок. Байланышты текшерип, кайра аракет кылыңыз.', "Bajarilmadi. Aloqani tekshirib, qayta urinib ko'ring.", 'Failed. Check the connection and try again.', 'Olmadı. Bağlantıyı kontrol edip tekrar deneyin.');
  static String openPay(BuildContext c) => c.t5('Перейти к оплате', 'Төлөөгө өтүү', "To'lovga o'tish", 'Go to payment', 'Ödemeye git');
  static String inPlan(BuildContext c) => c.t5('Уже в тарифе', 'Тарифте бар', 'Tarifda bor', 'Already in the plan', 'Zaten tarifte');
  static String openTariffs(BuildContext c) => c.t5('Тарифы и «Своё»', 'Тарифтер жана «Өзүмдүкү»', "Tariflar va «O'zimniki»", 'Plans and “Custom”', 'Tarifeler ve «Özel»');

  /// Название группы без записи в контенте: код с заглавной буквы.
  static String groupFallback(String code) => code.isEmpty ? '' : code[0].toUpperCase() + code.substring(1).replaceAll('_', ' ');
}
