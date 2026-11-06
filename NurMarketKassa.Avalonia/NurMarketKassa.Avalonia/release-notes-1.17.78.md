## v1.17.78

Этикетки размеров с QR, весы «Штрих РС-200», Склад с колонкой «Код / штрихкод», уточнения возврата долгового чека, устойчивость после смены компании и потери связи. Всё из 1.17.77 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.78.tar.gz`; macOS — `nurmarket-kassa-macos-arm64-1.17.78.zip` (Apple Silicon) и `nurmarket-kassa-macos-x64-1.17.78.zip` (Intel); Android — `nurmarket-kassa-android-1.17.78.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Этикетки размеров.** Кнопка «Создать QR» и формат QR на этикетках размеров; перед печатью этикеток размеров касса просит подтверждение и предупреждает про чековый принтер.
- **Весы «Штрих РС-200».** Поддержка весов Штрих РС-200: обмен файлами через общую папку, мастер подключения, штрихкод «Код-Вес» и «Код-Стоимость».
- **Экран не «прыгает».** Левая панель владельца и вкладки больше не «прыгают» при нажатии, плитки каталога не растут при клавиатурном фокусе.
- **Склад: код и штрихкод.** Колонка «Код / штрихкод» в Складе; карточки дочитываются, новый товар из ленты появляется сразу, скан неизвестного кода не затирает данные, правка товара отправляет только изменённые поля.
- **Возврат долгового чека.** Сообщение «Возврат уже проведён», предупреждение «Сервер пересчитал долг клиента иначе» и учёт такого возврата при закрытии смены.
- **Надёжность.** Новая база после смены компании больше не теряет столбцы; поиск по названию в приёмке работает без связи; фоновые окна не накладываются на «Сессия истекла»; после возврата связи каталог не перекачивается целиком.
- **PIN владельца при запуске.** Теперь это настройка и по умолчанию выключена.

---

# Кыргызча

### Эмнелер жаңы

- **Өлчөм этикеткалары.** Өлчөм этикеткаларында «QR түзүү» баскычы жана QR форматы; өлчөм этикеткаларын басардан мурун касса ырастоо сурайт жана чек принтери тууралуу эскертет.
- **«Штрих РС-200» таразасы.** Штрих РС-200 таразасын колдоо: жалпы папка аркылуу файл алмашуу, туташтыруу устасы, «Код-Салмак» жана «Код-Сумма» штрихкоду.
- **Экран «секирбейт».** Ээсинин сол панели жана өтмөктөр басканда «секирбейт», каталог плиткалары баскычтоп фокусунда чоңойбойт.
- **Кампа: код жана штрихкод.** Кампада «Код / штрихкод» мамычасы; карточкалар толукталат, лентадагы жаңы товар дароо чыгат, белгисиз кодду сканерлөө маалыматты өчүрбөйт, товарды оңдоодо өзгөргөн талаалар гана жөнөтүлөт.
- **Карыз чегин кайтаруу.** «Кайтаруу өткөрүлгөн» билдирүүсү, «Сервер кардардын карызын башкача кайра эсептеди» эскертүүсү жана мындай кайтарууну сменаны жабууда эсепке алуу.
- **Ишенимдүүлүк.** Компанияны алмаштыргандан кийинки жаңы база мамычаларды жоготпойт; кабыл алууда аталыш боюнча издөө байланышсыз да иштейт; фон терезелери «Сессия бүттү» терезесин жаппайт; байланыш кайтканда каталог толугу менен кайра жүктөлбөйт.
- **Ишке киргизүүдө ээсинин PIN-коду.** Эми бул жөндөө жана демейки боюнча өчүк.

---

# English

### What's new

- **Size labels.** A “Create QR” button and a QR format on size labels; before printing size labels the till asks for confirmation and warns about the receipt printer.
- **“Shtrikh RS-200” scales.** Support for Shtrikh RS-200 scales: file exchange through a shared folder, a connection wizard, “Code-Weight” and “Code-Amount” barcodes.
- **The screen no longer jumps.** The owner's left panel and the tabs no longer “jump” when clicked, and catalog tiles no longer grow on keyboard focus.
- **Warehouse: code and barcode.** A “Code / barcode” column in the Warehouse; cards are read in full, a new product from the feed appears at once, scanning an unknown code does not overwrite data, editing a product sends only the changed fields.
- **Returning a debt receipt.** A “Return already done” message, a “The server recalculated the client's debt differently” warning, and such a return counted when the shift is closed.
- **Reliability.** A new database after switching company no longer loses columns; name search in receiving works offline; background windows no longer stack on “Session expired”; after the connection returns the catalog is not downloaded in full again.
- **Owner PIN at start.** This is now a setting, off by default.

---

# Türkçe

### Yenilikler

- **Beden etiketleri.** Beden etiketlerinde «QR oluştur» düğmesi ve QR biçimi; beden etiketlerini basmadan önce kasa onay ister ve fiş yazıcısı konusunda uyarır.
- **«Shtrih RS-200» terazisi.** Shtrih RS-200 terazi desteği: ortak klasör üzerinden dosya alışverişi, bağlantı sihirbazı, «Kod-Ağırlık» ve «Kod-Tutar» barkodu.
- **Ekran artık «zıplamaz».** Sahibin sol paneli ve sekmeler tıklanınca artık «zıplamaz», katalog kutucukları klavye odağında büyümez.
- **Depo: kod ve barkod.** Depoda «Kod / barkod» sütunu; kartlar eksiksiz okunur, akıştaki yeni ürün hemen görünür, bilinmeyen kodun taranması verileri ezmez, ürün düzenlemede yalnızca değişen alanlar gönderilir.
- **Borç fişinin iadesi.** «İade zaten yapıldı» mesajı, «Sunucu müşteri borcunu farklı hesapladı» uyarısı ve böyle bir iadenin vardiya kapanışında hesaba katılması.
- **Güvenilirlik.** Şirket değiştirdikten sonraki yeni veritabanı artık sütun kaybetmez; mal kabulünde ada göre arama bağlantısız da çalışır; arka plan pencereleri «Oturum doldu» penceresinin üstüne binmez; bağlantı gelince katalog baştan indirilmez.
- **Açılışta sahip PIN'i.** Artık bir ayardır ve varsayılan olarak kapalıdır.

---

# O'zbekcha

### Yangiliklar

- **O'lcham yorliqlari.** O'lcham yorliqlarida «QR yaratish» tugmasi va QR formati; o'lcham yorliqlarini chop etishdan oldin kassa tasdiq so'raydi va chek printeri haqida ogohlantiradi.
- **«Shtrix RS-200» tarozisi.** Shtrix RS-200 tarozisini qo'llab-quvvatlash: umumiy papka orqali fayl almashish, ulash ustasi, «Kod-Vazn» va «Kod-Summa» shtrix-kodi.
- **Ekran «sakramaydi».** Egasining chap paneli va ichki oynalar bosilganda «sakramaydi», katalog plitkalari klaviatura fokusida kattalashmaydi.
- **Ombor: kod va shtrix-kod.** Omborda «Kod / shtrix-kod» ustuni; kartochkalar to'liq o'qiladi, lentadagi yangi tovar darhol chiqadi, noma'lum kodni skanerlash ma'lumotni o'chirmaydi, tovarni tahrirlashda faqat o'zgargan maydonlar yuboriladi.
- **Qarz chekini qaytarish.** «Qaytarish allaqachon o'tkazilgan» xabari, «Server mijoz qarzini boshqacha qayta hisobladi» ogohlantirishi va bunday qaytarishni smenani yopishda hisobga olish.
- **Ishonchlilik.** Kompaniyani almashtirgandan keyingi yangi baza ustunlarni yo'qotmaydi; qabulda nom bo'yicha qidiruv aloqasiz ham ishlaydi; fon oynalari «Sessiya tugadi» oynasi ustiga chiqmaydi; aloqa qaytgach katalog butunlay qayta yuklanmaydi.
- **Ishga tushirishda egasining PIN-kodi.** Endi bu sozlama bo'lib, odatda o'chiq.
