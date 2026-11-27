## v1.17.96

Смешанная оплата работает без связи (чек встаёт в очередь и уходит на сервер с разбивкой наличные/безнал, без дублей); вход кассира по QR-бейджу; 7 исправлений в редакторе ценников и штрихкодов; расход в «Просмотре смены» у владельца по данным сервера и мелкие исправления. Всё из 1.17.95 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.96.tar.gz`; Android — `nurmarket-kassa-android-1.17.96.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Смешанная оплата без связи.** Если сервер не отвечает, смешанная оплата больше не отказывает — чек встаёт в очередь и потом уходит на сервер с разбивкой на наличные и безнал, без дублей. Один быстрый сбой фонового запроса больше не переводит кассу в «Автономно».
- **Вход по QR-бейджу.** Владелец выпускает кассиру QR-бейдж в «Настройки → Сотрудники → QR для входа» (срок действия и PIN по желанию, отзыв одной кнопкой). Кассир подносит бейдж к сканеру на экране входа — касса открывается от его имени. Есть защита от перебора; в первый раз на каждой кассе пароль нужен один раз.
- **Редактор ценников и штрихкодов.** Правки редактора сразу доходят до «Печати»; кнопка «Штрих-код» выбирает штрихкод, а не убирает его; новое поле «Толщина штриха» и подсказка, если штрихкод слишком тонкий; галочки в «Печати» не ломают свой шаблон; при смене размера элементы растягиваются; поля X/Y/ширина/высота показывают настоящее значение; у QR лишние настройки отключены.
- **Исправления.** «Просмотр смены» в программе владельца показывает расход по данным сервера; экран покупателя «Классика» без пустой заглушки; в «Тех поддержке» одна главная кнопка; в окне «Возврат» колонка «Товар» и шире дата; подтверждения внесения и изъятия не зависают при работе с Диктором.

# Кыргызча

### Эмнелер жаңы

- **Байланышсыз аралаш төлөм.** Сервер жооп бербесе, аралаш төлөм мындан ары баш тартпайт — чек кезекке турат да, кийин серверге накталай жана накталай эмес бөлүктөрү менен, кайталанбай жөнөтүлөт. Фондук сурамдын бир тез катасы кассаны «Автономдуу» режимге которбойт.
- **QR-бейдж менен кирүү.** Ээси кассирге «Жөндөөлөр → Кызматкерлер → Кирүү үчүн QR» бөлүмүндө QR-бейдж чыгарат (мөөнөтү жана PIN каалоо боюнча, бир баскыч менен жокко чыгаруу). Кассир бейджди кирүү экранында сканерге тийгизет — касса анын атынан ачылат. Тандап табуудан коргоо бар; ар бир кассада биринчи жолу сырсөз бир жолу керек.
- **Баа белгилеринин жана штрихкоддордун редактору.** Редактордогу өзгөртүүлөр дароо «Басып чыгарууга» жетет; «Штрих-код» баскычы штрихкодду тандайт, алып салбайт; жаңы «Сызык калыңдыгы» талаасы жана штрихкод өтө ичке болсо кеңеш; «Басып чыгаруудагы» белгилер өз шаблонуңузду бузбайт; өлчөм өзгөргөндө элементтер чоюлат; X/Y/туурасы/бийиктиги талаалары чыныгы маанини көрсөтөт; QR үчүн ашыкча жөндөөлөр өчүрүлгөн.
- **Оңдоолор.** Ээсинин программасындагы «Сменаны көрүү» чыгымды сервердин маалыматы боюнча көрсөтөт; «Классика» сатып алуучу экраны бош тосмосуз; «Техколдоодо» бир гана негизги баскыч; «Кайтаруу» терезесинде «Товар» мамычасы жана кененирээк дата; акча салууну жана алууну ырастоо Диктор менен иштегенде катып калбайт.

# English

### What's new

- **Mixed payment offline.** When the server does not respond, mixed payment no longer fails — the receipt is queued and later sent to the server split into cash and non-cash, without duplicates. A single quick error on a background request no longer switches the till to “Offline”.
- **Sign-in with a QR badge.** The owner issues a cashier a QR badge in “Settings → Employees → Sign-in QR” (optional expiry and PIN, revoke with one button). The cashier holds the badge to the scanner on the login screen and the till opens under their name. There is brute-force protection; the first time on each till the password is needed once.
- **Price tag and barcode editor.** Editor changes reach “Print” right away; the “Barcode” button selects the barcode instead of removing it; a new “Bar thickness” field and a hint when the barcode is too thin; checkboxes in “Print” no longer break your own template; elements stretch when the size changes; the X/Y/width/height fields show the real value; extra settings are disabled for QR.
- **Fixes.** “Shift details” in the owner's program shows expenses from the server's data; the “Classic” customer screen has no empty placeholder; “Tech support” has a single main button; the “Return” window has a “Product” column and a wider date; cash-in and cash-out confirmations no longer hang with Narrator.

# Türkçe

### Yenilikler

- **Bağlantısız karışık ödeme.** Sunucu yanıt vermediğinde karışık ödeme artık reddedilmez — fiş kuyruğa alınır ve sonra nakit ve nakit dışı olarak ayrılmış şekilde, tekrarsız sunucuya gönderilir. Arka plan isteğindeki tek bir hızlı hata kasayı artık «Çevrimdışı» moda geçirmez.
- **QR rozetle giriş.** Sahip, kasiyere «Ayarlar → Çalışanlar → Giriş QR'ı» bölümünden QR rozet verir (isteğe bağlı süre ve PIN, tek düğmeyle iptal). Kasiyer rozeti giriş ekranında tarayıcıya okutur ve kasa onun adına açılır. Deneme-yanılmaya karşı koruma var; her kasada ilk seferde şifre bir kez gerekir.
- **Etiket ve barkod düzenleyicisi.** Düzenleyicideki değişiklikler hemen «Yazdır»a ulaşır; «Barkod» düğmesi barkodu kaldırmaz, seçer; yeni «Çizgi kalınlığı» alanı ve barkod çok inceyse ipucu; «Yazdır»daki onay kutuları kendi şablonunuzu bozmaz; boyut değişince öğeler uzar; X/Y/genişlik/yükseklik alanları gerçek değeri gösterir; QR için gereksiz ayarlar kapalı.
- **Düzeltmeler.** Sahibin programındaki «Vardiya ayrıntıları» gideri sunucu verilerine göre gösterir; «Klasik» müşteri ekranında boş yer tutucu yok; «Teknik destek»te tek ana düğme var; «İade» penceresinde «Ürün» sütunu ve daha geniş tarih var; para girişi ve çıkışı onayları Ekran Okuyucu ile donmaz.

# O'zbekcha

### Yangiliklar

- **Aloqasiz aralash to'lov.** Server javob bermasa, aralash to'lov endi rad etilmaydi — chek navbatga turadi va keyin serverga naqd va naqdsiz qismlarga ajratilgan holda, takrorlanmasdan yuboriladi. Fon so'rovidagi bitta tez xato kassani endi «Avtonom» rejimga o'tkazmaydi.
- **QR-beyj bilan kirish.** Ega kassirga «Sozlamalar → Xodimlar → Kirish uchun QR» bo'limida QR-beyj chiqaradi (muddat va PIN ixtiyoriy, bitta tugma bilan bekor qilish). Kassir beyjni kirish ekranida skanerga tutadi — kassa uning nomidan ochiladi. Tanlab topishdan himoya bor; har bir kassada birinchi marta parol bir marta kerak.
- **Narx yorliqlari va shtrix-kodlar muharriri.** Muharrirdagi o'zgarishlar darhol «Chop etish»ga yetadi; «Shtrix-kod» tugmasi shtrix-kodni olib tashlamaydi, tanlaydi; yangi «Chiziq qalinligi» maydoni va shtrix-kod juda ingichka bo'lsa maslahat; «Chop etish»dagi belgilar o'z shabloningizni buzmaydi; o'lcham o'zgarganda elementlar cho'ziladi; X/Y/kenglik/balandlik maydonlari haqiqiy qiymatni ko'rsatadi; QR uchun ortiqcha sozlamalar o'chirilgan.
- **Tuzatishlar.** Egasining dasturidagi «Smenani ko'rish» xarajatni server ma'lumotlari bo'yicha ko'rsatadi; «Klassika» xaridor ekranida bo'sh joy egallovchi yo'q; «Texnik yordam»da bitta asosiy tugma; «Qaytarish» oynasida «Tovar» ustuni va kengroq sana; pul kiritish va olishni tasdiqlash Diktor bilan ishlaganda qotib qolmaydi.
