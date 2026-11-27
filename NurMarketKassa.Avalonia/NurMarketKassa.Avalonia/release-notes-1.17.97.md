## v1.17.97

Каталог разделён на категории линиями; смена кассира и вход по скану QR-бейджа; исправлено подключение телефона-сканера (QR в тёмной теме) и смена компании («Access denied»); ошибка «Печать на A4» исправлена, у ценников появилась рамка. Всё из 1.17.96 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.97.tar.gz`; Android — `nurmarket-kassa-android-1.17.97.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Каталог по категориям.** Плитки и список в каталоге разделены на группы — над каждой полоса с названием категории, числом товаров и линией, каждая категория с новой строки. Выключается в «Настройки → Экран» (галочка «Разделять каталог по категориям»). При поиске по тексту и в магазине без категорий всё как раньше.
- **Смена кассира по QR-бейджу.** Бейдж, поднесённый к сканеру в окне «Сменить кассира» или на экране входа, сразу входит от имени кассира (если он хоть раз входил паролем на этой кассе). Строка бейджа больше не попадает в поля, поиск и чек; на открытой кассе касса спросит «Сменить кассира на …?».
- **Подключение телефона-сканера.** В тёмной теме QR кассы рисовался наоборот и телефон его не читал — исправлено (и у QR чека, Telegram и бейджа). QR стал короче и крупнее; если телефон не подключается, окно подсказывает, как разрешить кассу в брандмауэре Windows.
- **Смена компании.** Исправлена ошибка «Данные прежней компании заняты другой программой … Access denied» — программа владельца больше не держит базу кассы открытой, перенос данных стал надёжнее, в окне «Неотправленные данные» видно, что именно мешает.
- **Ценники.** Исправлена ошибка «Произошла ошибка в программе» в окне «Печать на A4». Новая рамка для ценников: толщина, скругление, цвет, пунктир — на этикетке в редакторе и на листе A4; редактор предупреждает, если элемент задевает рамку.

# Кыргызча

### Эмнелер жаңы

- **Каталог категориялар боюнча.** Каталогдогу плиткалар жана тизме топторго бөлүнөт — ар биринин үстүндө категориянын аталышы, товарлардын саны жана сызык бар, ар бир категория жаңы саптан башталат. «Жөндөөлөр → Экран» бөлүмүндө өчүрүлөт («Каталогду категориялар боюнча бөлүү» белгиси). Текст боюнча издөөдө жана категориясыз дүкөндө мурдагыдай.
- **QR-бейдж менен кассирди алмаштыруу.** «Кассирди алмаштыруу» терезесинде же кирүү экранында сканерге тийгизилген бейдж кассирдин атынан дароо кирет (ал бул кассада жок дегенде бир жолу сырсөз менен кирсе). Бейдждин сабы талааларга, издөөгө жана чекке түшпөйт; ачык кассада касса «Кассирди … алмаштырабы?» деп сурайт.
- **Телефон-сканерди туташтыруу.** Караңгы темада кассанын QR коду тескери тартылып, телефон окуй албай жаткан — оңдолду (чектин, Telegram жана бейджди QR кодуна да). QR кыскарып, чоңойду; телефон туташпаса, терезе кассаны Windows брандмауэринде кантип уруксаттоону көрсөтөт.
- **Компанияны алмаштыруу.** «Мурдагы компаниянын маалыматтары башка программа тарабынан алынган … Access denied» катасы оңдолду — ээсинин программасы кассанын базасын мындан ары ачык кармабайт, маалыматтарды которуу ишенимдүү болду, «Жөнөтүлбөгөн маалыматтар» терезесинде эмне тоскоол болуп жатканы көрүнөт.
- **Баа белгилери.** «Басып чыгаруу A4» терезесиндеги «Программада ката кетти» катасы оңдолду. Баа белгилери үчүн жаңы алкак: калыңдык, тегеректөө, түс, пунктир — редактордогу этикеткада жана A4 барагында; элемент алкакка тийсе, редактор эскертет.

# English

### What's new

- **Catalog by category.** Tiles and the list in the catalog are split into groups — above each one a strip with the category name, product count and a line, each category starting on a new row. Turn it off in “Settings → Screen” (“Split catalog by category”). In text search and in stores without categories it works as before.
- **Switch cashier by QR badge.** A badge held to the scanner in the “Switch cashier” window or on the login screen signs in as that cashier right away (if they have signed in with a password on this till at least once). The badge string no longer lands in fields, search or the receipt; on an open till it asks “Switch cashier to …?”.
- **Connecting the phone scanner.** In the dark theme the till's QR was drawn inverted and the phone could not read it — fixed (also for the receipt, Telegram and badge QR). The QR is shorter and larger; if the phone does not connect, the window explains how to allow the till in the Windows firewall.
- **Switching company.** Fixed the “Previous company data is in use by another program … Access denied” error — the owner's program no longer keeps the till database open, data move is more reliable, and the “Unsent data” window shows what exactly is in the way.
- **Price tags.** Fixed the “An error occurred in the program” error in the “Print on A4” window. New frame for price tags: thickness, rounding, colour, dashed — on the label in the editor and on the A4 sheet; the editor warns if an element touches the frame.

# Türkçe

### Yenilikler

- **Kategoriye göre katalog.** Katalogdaki kutucuklar ve liste gruplara ayrılır — her grubun üstünde kategori adı, ürün sayısı ve bir çizgi bulunur, her kategori yeni satırdan başlar. «Ayarlar → Ekran» bölümünden kapatılır («Kataloğu kategorilere ayır»). Metin aramasında ve kategorisiz mağazada eskisi gibidir.
- **QR rozetle kasiyer değiştirme.** «Kasiyeri değiştir» penceresinde veya giriş ekranında tarayıcıya okutulan rozet hemen o kasiyer adına girer (bu kasada en az bir kez şifreyle girmişse). Rozet satırı artık alanlara, aramaya ve fişe düşmez; açık kasada kasa «Kasiyer … olarak değiştirilsin mi?» diye sorar.
- **Telefon tarayıcıyı bağlama.** Koyu temada kasanın QR kodu ters çiziliyordu ve telefon okuyamıyordu — düzeltildi (fiş, Telegram ve rozet QR'ı da). QR kısaldı ve büyüdü; telefon bağlanmazsa pencere kasayı Windows güvenlik duvarında nasıl izinli yapacağınızı gösterir.
- **Şirket değiştirme.** «Önceki şirketin verileri başka bir program tarafından kullanılıyor … Access denied» hatası düzeltildi — sahibin programı artık kasa veritabanını açık tutmaz, veri taşıma daha güvenilir, «Gönderilmemiş veriler» penceresinde neyin engellediği görünür.
- **Etiketler.** «A4'e yazdır» penceresindeki «Programda hata oluştu» hatası düzeltildi. Etiketler için yeni çerçeve: kalınlık, yuvarlama, renk, kesikli — düzenleyicideki etikette ve A4 sayfasında; öğe çerçeveye değerse düzenleyici uyarır.

# O'zbekcha

### Yangiliklar

- **Katalog kategoriyalar bo'yicha.** Katalogdagi plitkalar va ro'yxat guruhlarga bo'linadi — har birining tepasida kategoriya nomi, tovarlar soni va chiziq bor, har bir kategoriya yangi qatordan boshlanadi. «Sozlamalar → Ekran» bo'limida o'chiriladi («Katalogni kategoriyalar bo'yicha ajratish» belgisi). Matn bo'yicha qidiruvda va kategoriyasiz do'konda avvalgidek.
- **QR-beyj bilan kassirni almashtirish.** «Kassirni almashtirish» oynasida yoki kirish ekranida skanerga tutilgan beyj darhol kassir nomidan kiradi (agar u bu kassada kamida bir marta parol bilan kirgan bo'lsa). Beyj qatori endi maydonlarga, qidiruvga va chekka tushmaydi; ochiq kassada kassa «Kassirni … ga almashtirasizmi?» deb so'raydi.
- **Telefon-skanerni ulash.** Qorong'i mavzuda kassaning QR kodi teskari chizilib, telefon uni o'qiy olmasdi — tuzatildi (chek, Telegram va beyj QR kodi ham). QR qisqa va katta bo'ldi; telefon ulanmasa, oyna kassani Windows brandmauerida qanday ruxsat etishni ko'rsatadi.
- **Kompaniyani almashtirish.** «Oldingi kompaniya ma'lumotlari boshqa dastur tomonidan band … Access denied» xatosi tuzatildi — egasining dasturi endi kassa bazasini ochiq tutmaydi, ma'lumotlarni ko'chirish ishonchliroq bo'ldi, «Yuborilmagan ma'lumotlar» oynasida nima xalaqit berayotgani ko'rinadi.
- **Narx yorliqlari.** «A4 ga chop etish» oynasidagi «Dasturda xatolik yuz berdi» xatosi tuzatildi. Narx yorliqlari uchun yangi ramka: qalinlik, yumaloqlash, rang, punktir — muharrirdagi yorliqda va A4 varag'ida; element ramkaga tegsa, muharrir ogohlantiradi.
