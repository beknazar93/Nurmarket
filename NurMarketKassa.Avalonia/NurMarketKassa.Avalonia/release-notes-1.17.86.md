## v1.17.86 (тестовая версия)

Весы Rongta и Штрих РС-200, остаток «приход в пути», карточки и счётчик штрихкодов, названия и тип штрихкода 07 для Rongta, продажа ниже закупки по праву, лента изменений и ETag прав, серийные номера и гарантия (Электроника), упаковки и уровни цен для строймаркета, слоты устройств, Ремонт и Trade-in в Маркетплейсе, мастер принтеров Xprinter, правка возвратов и журнал смены, новые сферы «Канцтовары» и «Электроника». Всё из 1.17.85 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.86.tar.gz`; Android — `nurmarket-kassa-android-1.17.86.apk`.

Тестовая версия: обновление видят только кассы с кодом тестировщика (Настройки → Обновления). Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Остаток «приход в пути».** Запасной путь полной загрузки каталога больше не перетирает остаток товара с «приходом в пути».
- **Карточки штрихкодов.** В программе владельца карточки штрихкодов больше не налезают друг на друга после смены масштаба.
- **Счётчик «нет штрихкода».** Сразу после генерации штрихкодов счётчик «нет штрихкода» показывает верное число.
- **Названия весовых товаров Rongta.** Названия печатаются без мусора (пробел, скобки); после обновления один раз нажмите «Отправить на весы».
- **Rongta — тип штрихкода 07.** Тип штрихкода 07 для Rongta включён по умолчанию; есть кнопка «Исправить» и инструкция.
- **Весы Rongta и Штрих РС-200.** Rongta тип 07 подключается по UDP 5001, названия товаров передаются в весы; Штрих РС-200 доработан по ТЗ от 12.11.
- **Продажа ниже закупки.** Скидка, после которой цена ниже закупочной, проводится только при праве «Продажа ниже закупки» (can_sell_below_cost).
- **Синхронизация и права.** Каталог обновляется по ленте изменений main/changes, права кэшируются по ETag; PLU весового товара берётся с сервера.
- **Серийные номера и гарантия.** Для сферы «Электроника»: серийные номера и гарантия в приёмке и продаже.
- **Строймаркет — упаковки и уровни цен.** Упаковки (sale_units) и уровни цен в продаже и приёмке.
- **Слоты устройств.** Если логин уже занят другой кассой, при входе касса спрашивает, отключить ли ту; старая касса получает понятное сообщение.
- **Ремонт и Trade-in в Маркетплейсе.** В Маркетплейсе появились модули «Ремонт» и «Trade-in».
- **Мастер принтеров Xprinter.** Мастер настройки принтера Xprinter и диагностика принтера.
- **Возвраты и журнал смены.** Исправлена правка возвратов поставщикам и клиентам; сверка журнала смены.
- **Внешний вид.** Новый блок состояния, обновлённые шапки окон и вкладки в Настройках.
- **Массовая загрузка — поле «Название».** В мастере массовой загрузки товаров появилось поле «Название» (не шире 440 px).
- **Новые сферы — Канцтовары и Электроника.** Для магазинов добавлены сферы «Канцтовары» и «Электроника».

---

# Кыргызча

### Эмнелер жаңы

- **Калдык «жолдогу кирим».** Каталогду толук жүктөөнүн запастагы жолу «жолдогу кириму» бар товардын калдыгын мындан ары өчүрбөйт.
- **Штрих-код карточкалары.** Ээсинин программасында масштаб өзгөргөндөн кийин штрих-код карточкалары бири-биринин үстүнө чыкпайт.
- **«Штрих-код жок» эсептегичи.** Штрих-коддорду жараткандан кийин эле «штрих-код жок» эсептегичи туура санды көрсөтөт.
- **Rongta таразалуучу товарлардын аталыштары.** Аталыштар керексиз белгилерсиз (боштук, жаасалар) басылат; жаңыртуудан кийин бир жолу «Таразага жөнөтүү» баскычын басыңыз.
- **Rongta — штрих-код түрү 07.** Rongta үчүн штрих-код түрү 07 демейки боюнча иштейт; «Оңдоо» баскычы жана нускама бар.
- **Rongta жана Штрих РС-200 таразалары.** Rongta 07-түрү UDP 5001 аркылуу туташат, товарлардын аталыштары таразага берилет; Штрих РС-200 12.11 ТТ боюнча жакшыртылды.
- **Сатып алуу баасынан төмөн сатуу.** Баа сатып алуу баасынан төмөн болуп калган жеңилдик «Сатып алуудан төмөн сатуу» (can_sell_below_cost) укугу болгондо гана өтөт.
- **Синхрондоштуруу жана укуктар.** Каталог main/changes өзгөрүүлөр лентасы боюнча жаңыланат, укуктар ETag боюнча кэште сакталат; таразалуучу товардын PLU серверден алынат.
- **Сериялык номерлер жана кепилдик.** «Электроника» тармагы үчүн: кабыл алууда жана сатууда сериялык номерлер жана кепилдик.
- **Курулуш дүкөнү — таңгактар жана баа деңгээли.** Сатууда жана кабыл алууда таңгактар (sale_units) жана баа деңгээлдери.
- **Түзмөк слоттору.** Логин башка кассада ээленген болсо, кирүүдө касса аны өчүрүүнү сурайт; эски касса түшүнүктүү билдирүү алат.
- **Маркетплейстеги «Оңдоо» жана Trade-in.** Маркетплейсте «Оңдоо» жана «Trade-in» модулдары пайда болду.
- **Xprinter принтерлеринин устасы.** Xprinter принтерин жөндөө устасы жана принтердин диагностикасы.
- **Кайтаруулар жана смена журналы.** Жеткирүүчүлөргө жана кардарларга кайтарууларды оңдоо оңдолду; смена журналын салыштыруу.
- **Сырткы көрүнүш.** Жаңы абал блогу, терезелердин жаңыланган башкы бөлүктөрү жана Жөндөөлөрдөгү өтмөктөр.
- **Жапырт жүктөө — «Аталышы» талаасы.** Товарларды жапырт жүктөө устасында «Аталышы» талаасы пайда болду (440 px'тен кененирээк эмес).
- **Жаңы тармактар — Канцтовар жана Электроника.** Дүкөндөр үчүн «Канцтовар» жана «Электроника» тармактары кошулду.

---

# English

### What's new

- **Stock with “goods in transit”.** The fallback path of the full catalog load no longer overwrites the stock of a product with “goods in transit”.
- **Barcode cards.** In the owner program, barcode cards no longer overlap after the scale is changed.
- **“No barcode” counter.** Right after barcodes are generated, the “no barcode” counter shows the correct number.
- **Rongta weighed product names.** Names are printed without junk characters (space, brackets); after the update press “Send to scale” once.
- **Rongta — barcode type 07.** Barcode type 07 for Rongta is on by default; there is a “Fix” button and an instruction.
- **Rongta and Shtrikh RS-200 scales.** Rongta type 07 connects over UDP 5001 and product names are sent to the scale; Shtrikh RS-200 was refined per the 12.11 specification.
- **Selling below cost.** A discount that brings the price below the purchase price goes through only with the “Sell below cost” permission (can_sell_below_cost).
- **Sync and permissions.** The catalog updates from the main/changes feed, permissions are cached by ETag; the PLU of a weighed product is taken from the server.
- **Serial numbers and warranty.** For the “Electronics” sphere: serial numbers and warranty in receiving and selling.
- **Building store — packs and price levels.** Packs (sale_units) and price levels in selling and receiving.
- **Device slots.** If the login is already used by another till, sign-in asks whether to disconnect it; the old till gets a clear message.
- **Repair and Trade-in in the Marketplace.** The Marketplace now has the “Repair” and “Trade-in” modules.
- **Xprinter printer wizard.** A setup wizard for Xprinter printers and printer diagnostics.
- **Returns and the shift log.** Editing of supplier and customer returns is fixed; shift log reconciliation.
- **Look and feel.** A new status block, refreshed window headers and tabs in Settings.
- **Bulk upload — the “Name” field.** The bulk product upload wizard now has a “Name” field (no wider than 440 px).
- **New spheres — Stationery and Electronics.** The “Stationery” and “Electronics” store spheres were added.

---

# Türkçe

### Yenilikler

- **«Yoldaki mal» stoku.** Kataloğun tam yüklenmesinin yedek yolu artık «yoldaki mal» olan ürünün stokunun üzerine yazmaz.
- **Barkod kartları.** Sahip programında ölçek değiştikten sonra barkod kartları artık üst üste binmez.
- **«Barkod yok» sayacı.** Barkodlar üretildikten hemen sonra «barkod yok» sayacı doğru sayıyı gösterir.
- **Rongta tartılan ürün adları.** Adlar gereksiz karakterler (boşluk, parantez) olmadan basılır; güncellemeden sonra bir kez «Teraziye gönder»e basın.
- **Rongta — barkod türü 07.** Rongta için barkod türü 07 varsayılan olarak açıktır; «Düzelt» düğmesi ve talimat vardır.
- **Rongta ve Shtrikh RS-200 terazileri.** Rongta tip 07 UDP 5001 üzerinden bağlanır, ürün adları teraziye gönderilir; Shtrikh RS-200 12.11 tarihli şartnameye göre geliştirildi.
- **Maliyetin altında satış.** Fiyatı alış fiyatının altına düşüren indirim yalnızca «Maliyetin altında satış» (can_sell_below_cost) yetkisiyle yapılır.
- **Eşitleme ve yetkiler.** Katalog main/changes değişiklik akışıyla güncellenir, yetkiler ETag ile önbelleğe alınır; tartılan ürünün PLU değeri sunucudan alınır.
- **Seri numaraları ve garanti.** «Elektronik» alanı için: mal kabulünde ve satışta seri numaraları ve garanti.
- **Yapı market — paketler ve fiyat seviyeleri.** Satışta ve mal kabulünde paketler (sale_units) ve fiyat seviyeleri.
- **Cihaz yuvaları.** Kullanıcı adı başka bir kasada kullanılıyorsa giriş sırasında diğerinin kapatılıp kapatılmayacağı sorulur; eski kasa anlaşılır bir mesaj alır.
- **Pazaryerinde Onarım ve Trade-in.** Pazaryerinde «Onarım» ve «Trade-in» modülleri eklendi.
- **Xprinter yazıcı sihirbazı.** Xprinter yazıcıları için kurulum sihirbazı ve yazıcı tanılama.
- **İadeler ve vardiya günlüğü.** Tedarikçi ve müşteri iadelerinin düzenlenmesi düzeltildi; vardiya günlüğü mutabakatı.
- **Görünüm.** Yeni durum bloğu, yenilenen pencere başlıkları ve Ayarlar'daki sekmeler.
- **Toplu yükleme — «Ad» alanı.** Toplu ürün yükleme sihirbazına «Ad» alanı eklendi (en çok 440 px genişlikte).
- **Yeni alanlar — Kırtasiye ve Elektronik.** Mağazalar için «Kırtasiye» ve «Elektronik» alanları eklendi.

---

# O'zbekcha

### Yangiliklar

- **«Yo'ldagi kirim» qoldig'i.** Katalogni to'liq yuklashning zaxira yo'li endi «yo'ldagi kirim»li tovar qoldig'ini o'chirib yubormaydi.
- **Shtrix-kod kartochkalari.** Ega dasturida masshtab o'zgargandan keyin shtrix-kod kartochkalari endi bir-birining ustiga chiqmaydi.
- **«Shtrix-kod yo'q» hisoblagichi.** Shtrix-kodlar yaratilgandan so'ng darhol «shtrix-kod yo'q» hisoblagichi to'g'ri sonni ko'rsatadi.
- **Rongta tortiladigan tovar nomlari.** Nomlar ortiqcha belgilarsiz (bo'shliq, qavslar) chop etiladi; yangilangandan keyin bir marta «Tarozilarga yuborish»ni bosing.
- **Rongta — shtrix-kod turi 07.** Rongta uchun shtrix-kod turi 07 odatiy bo'yicha yoqilgan; «Tuzatish» tugmasi va yo'riqnoma bor.
- **Rongta va Shtrix RS-200 tarozilari.** Rongta 07-tur UDP 5001 orqali ulanadi, tovar nomlari tarozilarga yuboriladi; Shtrix RS-200 12.11 TT bo'yicha takomillashtirildi.
- **Xarid narxidan past sotuv.** Narxni xarid narxidan pastga tushiradigan chegirma faqat «Xariddan past sotish» (can_sell_below_cost) huquqi bilan o'tadi.
- **Sinxronlash va huquqlar.** Katalog main/changes o'zgarishlar lentasi bo'yicha yangilanadi, huquqlar ETag bo'yicha keshlanadi; tarozida tortiladigan tovarning PLU serverdan olinadi.
- **Seriya raqamlari va kafolat.** «Elektronika» sohasi uchun: qabul va sotuvda seriya raqamlari va kafolat.
- **Qurilish do'koni — o'ramlar va narx darajalari.** Sotuv va qabulda o'ramlar (sale_units) va narx darajalari.
- **Qurilma slotlari.** Login boshqa kassada band bo'lsa, kirishda kassa uni o'chirishni so'raydi; eski kassa tushunarli xabar oladi.
- **Marketpleysda Ta'mirlash va Trade-in.** Marketpleysda «Ta'mirlash» va «Trade-in» modullari paydo bo'ldi.
- **Xprinter printer ustasi.** Xprinter printerlari uchun sozlash ustasi va printer diagnostikasi.
- **Qaytarishlar va smena jurnali.** Yetkazib beruvchilarga va mijozlarga qaytarishlarni tahrirlash tuzatildi; smena jurnalini solishtirish.
- **Ko'rinish.** Yangi holat bloki, yangilangan oyna sarlavhalari va Sozlamalardagi yorliqlar.
- **Ommaviy yuklash — «Nomi» maydoni.** Tovarlarni ommaviy yuklash ustasida «Nomi» maydoni paydo bo'ldi (440 px dan keng emas).
- **Yangi sohalar — Kantselyariya va Elektronika.** Do'konlar uchun «Kantselyariya» va «Elektronika» sohalari qo'shildi.
