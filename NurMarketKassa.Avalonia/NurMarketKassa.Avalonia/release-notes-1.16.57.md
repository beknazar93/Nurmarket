## v1.16.57

---

# 🇰🇬 Русский

### Важное: массовый импорт товаров больше не обнуляет склад

- Загрузка прайса поставщика по уже существующим товарам **обнуляла у них остаток**, а если в файле не было колонки «Цена» — ещё и цену, после чего товар пробивался на кассе за 0 сом. Причина: касса не отличала «такой колонки в файле нет» от «в ячейке ноль» и отправляла ноль на сервер как новое значение. Теперь поля, которых не было в файле, вообще не отправляются — на сервере они остаются прежними.
- В предпросмотре импорта для существующих товаров теперь видно **«остаток 40 → 60 (замена, не приход)»**. Импорт заменяет остаток, а не прибавляет к нему, и раньше это было ниоткуда не видно.

### Оплата

- **QR с суммой чека.** На экране покупателя можно показывать платёжный QR, в который уже вписана сумма — покупателю не нужно набирать её вручную в приложении банка. Реквизиты берутся из вашего же загруженного QR. Включается в «Настройки → Операции».
  По умолчанию выключено: формат сверен с QR MBank, на приложениях других банков не проверялся. Перед включением отсканируйте пробный чек двумя-тремя банковскими приложениями. Оплату кассир по-прежнему подтверждает вручную.

### Весы

- **Прямая работа с весами ШТРИХ-ПРИНТ по сетевому кабелю**, без участия сервера. В окне «Весы» появилась галочка «Напрямую по кабелю», поля IP/порт/пароль и кнопка «Проверить» — она опознаёт весы и подаёт звуковой сигнал.
  По умолчанию выключено, выгрузка через сервер работает как раньше. Порт в протоколе весов не задан — посмотрите его в системном меню самих весов.

### Надёжность

- **Ежедневная резервная копия локальной базы**, 7 последних копий в `%AppData%\NurMarketKassa\data\backups`. В базе лежит очередь непроведённых офлайн-продаж, история списаний и бонусы клиентов — всё это есть только на этом компьютере.
- Исправлено: при выгрузке офлайн-очереди касса отправляла серверу идентификатор смены, открытой без интернета. Сервер его не принимает, а отказ считался окончательным — чеки уходили в «ошибки» без возврата в очередь.

### Внешний вид

- Исправлено: **раскладка «1С» в тёмной теме была нечитаемой** — кнопки количества и действий рисовались белым по белому.
- Исправлено: сообщения об успехе («Сохранено», «Добавлено сотрудников…») сливались с фоном на некоторых темах.
- Исправлено: в разделе «Обновления» вместо английского текста об ошибке теперь понятное объяснение, если касса запущена не из установленной копии.

### Безопасность

- Ключ доступа к серверу обновлений убран из программы — раньше он был вшит внутрь и мог быть извлечён из файла программы.

---

# 🇰🇬 Кыргызча

### Маанилүү: товарларды топтоп жүктөө эми калдыкты нөлдөгөн жок

- Жеткирүүчүнүн прайсын мурунтан бар товарлар боюнча жүктөө **алардын калдыгын нөлдөп** салчу, ал эми файлда «Баасы» тилкеси жок болсо — баасын дагы, ошондон кийин товар кассада 0 сомго өтчү. Себеби: касса «файлда мындай тилке жок» менен «уячада нөл» дегенди айырмалачу эмес. Эми файлда болбогон талаалар серверге таптакыр жөнөтүлбөйт.
- Жүктөөнү алдын ала кароодо мурунтан бар товарлар үчүн **«калдык 40 → 60 (алмаштыруу, кириш эмес)»** деп көрүнөт. Жүктөө калдыкты кошпойт, алмаштырат.

### Төлөм

- **Чектин суммасы жазылган QR.** Сатып алуучунун экранында суммасы мурунтан жазылган төлөм QR-кодун көрсөтүүгө болот — сатып алуучу аны банк тиркемесинде кол менен терүүнүн кажети жок. Реквизиттер сиз жүктөгөн QR-ден алынат. «Жөндөөлөр → Операциялар» бөлүмүнөн күйгүзүлөт.
  Демейки боюнча өчүк: формат MBank QR-и менен текшерилген, башка банктардын тиркемелеринде текшерилген эмес. Күйгүзөрдөн мурун сынам чекти эки-үч банк тиркемеси менен скандаңыз. Төлөмдү кассир мурункудай кол менен ырастайт.

### Тараза

- **ШТРИХ-ПРИНТ таразалары менен тармак кабели аркылуу түз иштөө**, серверсиз. «Тараза» терезесинде «Кабель аркылуу түз» белгиси, IP/порт/сырсөз талаалары жана «Текшерүү» баскычы пайда болду — ал таразаны таанып, үн берет.
  Демейки боюнча өчүк, сервер аркылуу жүктөө мурункудай иштейт. Порт таразанын протоколунда көрсөтүлгөн эмес — аны таразанын системалык менюсунан караңыз.

### Ишенимдүүлүк

- **Жергиликтүү базанын күнүмдүк камдык көчүрмөсү**, акыркы 7 көчүрмө `%AppData%\NurMarketKassa\data\backups` папкасында. Базада өткөрүлбөгөн офлайн сатуулардын кезеги, эсептен чыгаруу тарыхы жана кардарлардын бонустары бар — мунун баары ушул компьютерде гана.
- Оңдолду: офлайн кезегин жүктөөдө касса серверге интернетсиз ачылган сменанын идентификаторун жөнөтчү. Сервер аны кабыл албайт, ал эми баш тартуу акыркы деп эсептелчү — чектер кезекке кайтарылбай «каталарга» кетчү.

### Сырткы көрүнүш

- Оңдолду: **караңгы темада «1С» жайгашуусу окулбай калчу** — сан жана аракет баскычтары ак түстө ак фондо тартылчу.
- Оңдолду: ийгилик тууралуу билдирүүлөр («Сакталды», «Кызматкерлер кошулду…») кээ бир темаларда фон менен кошулуп кетчү.
- Оңдолду: «Жаңыртуулар» бөлүмүндө англисче ката текстинин ордуна эми түшүнүктүү түшүндүрмө берилет.

### Коопсуздук

- Жаңыртуу серверине кирүү ачкычы программадан алынып салынды — мурун ал ичине тигилген жана программанын файлынан алынышы мүмкүн эле.

---

# 🇬🇧 English

### Important: bulk product import no longer zeroes out your stock

- Importing a supplier price list over products that already existed **reset their stock to zero**, and if the file had no "Price" column, their price as well — after which the product rang up at 0 som. The cause: the app could not tell "this column is absent from the file" from "the cell contains zero", and sent the zero to the server as the new value. Fields missing from the file are now not sent at all and keep their server-side values.
- The import preview now shows **"stock 40 → 60 (replace, not add)"** for existing products. Import replaces the stock rather than adding to it, and previously there was no way to see that.

### Payments

- **QR code with the receipt amount.** The customer display can now show a payment QR with the amount already encoded — the customer no longer types it into their banking app by hand. The payee details are taken from the QR image you already uploaded. Enable it in Settings → Operations.
  Off by default: the format was verified against MBank QR codes and has not been tested with other banks' apps. Scan a test receipt with two or three banking apps before enabling. The cashier still confirms payment manually.

### Scales

- **Direct communication with SHTRIKH-PRINT scales over a network cable**, without the server. The Scales window now has a "Direct over cable" checkbox, IP/port/password fields and a "Test" button that identifies the scales and makes them beep.
  Off by default; uploading via the server works as before. The port is not specified in the scales protocol — check it in the scales' own system menu.

### Reliability

- **Daily backup of the local database**, keeping the 7 most recent copies in `%AppData%\NurMarketKassa\data\backups`. The database holds the queue of unsubmitted offline sales, the write-off history and customer loyalty points — none of which exist anywhere else.
- Fixed: when replaying the offline queue, the app sent the server the identifier of a shift opened without internet. The server rejects it, and that rejection counted as final — receipts went to "failed" with no way back into the queue.

### Appearance

- Fixed: **the "1C" layout was unreadable in the dark theme** — the quantity and action buttons were drawn white on white.
- Fixed: success messages ("Saved", "Employees added…") blended into the background on some themes.
- Fixed: the Updates section now explains in plain language that the app is running from an uninstalled copy, instead of showing an English error.

### Security

- The update server access key has been removed from the program. It used to be embedded inside and could be extracted from the program file.

---

# 🇹🇷 Türkçe

### Önemli: toplu ürün içe aktarma artık stoğu sıfırlamıyor

- Tedarikçi fiyat listesini mevcut ürünler üzerine yüklemek **stoklarını sıfırlıyordu**, dosyada "Fiyat" sütunu yoksa fiyatı da — ardından ürün kasada 0 som olarak okutuluyordu. Nedeni: uygulama "bu sütun dosyada yok" ile "hücrede sıfır var" arasındaki farkı ayırt edemiyor ve sıfırı yeni değer olarak sunucuya gönderiyordu. Dosyada bulunmayan alanlar artık hiç gönderilmiyor.
- İçe aktarma önizlemesinde mevcut ürünler için artık **"stok 40 → 60 (değiştirme, ekleme değil)"** görünüyor. İçe aktarma stoğu değiştirir, üzerine eklemez.

### Ödeme

- **Fiş tutarı yazılı QR kodu.** Müşteri ekranında tutarı önceden işlenmiş bir ödeme QR kodu gösterilebilir — müşterinin tutarı banka uygulamasına elle girmesi gerekmez. Alıcı bilgileri sizin yüklediğiniz QR görselinden alınır. Ayarlar → İşlemler bölümünden açılır.
  Varsayılan olarak kapalı: biçim MBank QR kodlarıyla doğrulandı, diğer bankaların uygulamalarında test edilmedi. Açmadan önce deneme fişini iki üç banka uygulamasıyla tarayın. Ödemeyi kasiyer yine elle onaylar.

### Terazi

- **SHTRIKH-PRINT terazileriyle ağ kablosu üzerinden doğrudan çalışma**, sunucu olmadan. "Terazi" penceresine "Kablo ile doğrudan" onay kutusu, IP/port/şifre alanları ve teraziyi tanıyıp sesli uyarı veren "Test" düğmesi eklendi.
  Varsayılan olarak kapalı; sunucu üzerinden aktarım eskisi gibi çalışır. Port, terazi protokolünde belirtilmemiştir — teraziye ait sistem menüsünden bakın.

### Güvenilirlik

- **Yerel veritabanının günlük yedeği**, en son 7 kopya `%AppData%\NurMarketKassa\data\backups` klasöründe. Veritabanında gönderilmemiş çevrimdışı satış kuyruğu, düşüm geçmişi ve müşteri puanları bulunur — bunların hiçbiri başka yerde yoktur.
- Düzeltildi: çevrimdışı kuyruk aktarılırken uygulama, internetsiz açılan vardiyanın kimliğini sunucuya gönderiyordu. Sunucu bunu kabul etmez ve bu ret kesin sayılıyordu — fişler kuyruğa dönmeden "hata"ya düşüyordu.

### Görünüm

- Düzeltildi: **koyu temada "1C" düzeni okunamıyordu** — miktar ve işlem düğmeleri beyaz üzerine beyaz çiziliyordu.
- Düzeltildi: başarı mesajları ("Kaydedildi", "Personel eklendi…") bazı temalarda arka plana karışıyordu.
- Düzeltildi: Güncellemeler bölümü, İngilizce hata metni yerine uygulamanın kurulu olmayan bir kopyadan çalıştığını anlaşılır biçimde açıklıyor.

### Güvenlik

- Güncelleme sunucusu erişim anahtarı programdan kaldırıldı. Daha önce içine gömülüydü ve program dosyasından çıkarılabilirdi.

---

# 🇺🇿 O'zbekcha

### Muhim: mahsulotlarni ommaviy import qilish endi omborni nolga tushirmaydi

- Yetkazib beruvchi narxnomasini mavjud mahsulotlar ustiga yuklash **ularning qoldig'ini nolga tushirardi**, faylda "Narx" ustuni bo'lmasa — narxini ham, shundan so'ng mahsulot kassada 0 so'mga o'tardi. Sababi: dastur "bunday ustun faylda yo'q" bilan "katakda nol" o'rtasidagi farqni ajrata olmasdi. Endi faylda bo'lmagan maydonlar serverga umuman yuborilmaydi.
- Import ko'rinishida mavjud mahsulotlar uchun **"qoldiq 40 → 60 (almashtirish, kirim emas)"** ko'rinadi. Import qoldiqni qo'shmaydi, almashtiradi.

### To'lov

- **Chek summasi yozilgan QR.** Xaridor ekranida summasi oldindan kiritilgan to'lov QR-kodini ko'rsatish mumkin — xaridor uni bank ilovasida qo'lda terishi shart emas. Rekvizitlar siz yuklagan QR rasmidan olinadi. "Sozlamalar → Operatsiyalar" bo'limidan yoqiladi.
  Sukut bo'yicha o'chiq: format MBank QR kodlari bilan tekshirilgan, boshqa banklar ilovalarida sinalmagan. Yoqishdan oldin sinov chekini ikki-uchta bank ilovasi bilan skanerlang. To'lovni kassir avvalgidek qo'lda tasdiqlaydi.

### Tarozi

- **SHTRIKH-PRINT tarozilari bilan tarmoq kabeli orqali to'g'ridan-to'g'ri ishlash**, serversiz. "Tarozi" oynasida "Kabel orqali to'g'ridan-to'g'ri" belgisi, IP/port/parol maydonlari va tarozini tanib, ovoz beradigan "Tekshirish" tugmasi paydo bo'ldi.
  Sukut bo'yicha o'chiq; server orqali yuklash avvalgidek ishlaydi. Port tarozi protokolida ko'rsatilmagan — uni tarozining tizim menyusidan qarang.

### Ishonchlilik

- **Mahalliy ma'lumotlar bazasining kunlik zaxira nusxasi**, oxirgi 7 nusxa `%AppData%\NurMarketKassa\data\backups` papkasida. Bazada yuborilmagan oflayn savdolar navbati, hisobdan chiqarish tarixi va mijozlar bonuslari saqlanadi — bularning hech biri boshqa joyda yo'q.
- Tuzatildi: oflayn navbatni yuborishda dastur serverga internetsiz ochilgan smena identifikatorini yuborardi. Server uni qabul qilmaydi, rad etish esa yakuniy hisoblanardi — cheklar navbatga qaytmasdan "xatolar"ga tushardi.

### Tashqi ko'rinish

- Tuzatildi: **qorong'i mavzuda "1C" joylashuvi o'qib bo'lmasdi** — miqdor va amal tugmalari oq fonda oq rangda chizilardi.
- Tuzatildi: muvaffaqiyat xabarlari ("Saqlandi", "Xodimlar qo'shildi…") ba'zi mavzularda fon bilan qo'shilib ketardi.
- Tuzatildi: "Yangilanishlar" bo'limi inglizcha xato matni o'rniga dastur o'rnatilmagan nusxadan ishga tushirilganini tushunarli tilda tushuntiradi.

### Xavfsizlik

- Yangilanish serveriga kirish kaliti dasturdan olib tashlandi. Avval u ichiga joylashtirilgan va dastur faylidan chiqarib olinishi mumkin edi.
