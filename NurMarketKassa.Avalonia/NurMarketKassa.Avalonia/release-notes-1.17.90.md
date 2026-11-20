## v1.17.90 (тестовая версия)

Автозапчасти: импорт прайсов поставщика (в том числе старый .xls), исправления чека, один аккаунт для кассы и программы владельца, ценники. Всё из 1.17.89 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.90.tar.gz`; Android — `nurmarket-kassa-android-1.17.90.apk`.

Тестовая версия: обновление видят только кассы с кодом тестировщика (Настройки → Обновления). Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Автозапчасти.** импорт прайсов: В мастере «Массовая загрузка» новый профиль «Прайс поставщика (автозапчасти)»: колонки находятся по синонимам, группы и строки без цены пропускаются, остаток вида «10-20» читается диапазоном, повторная загрузка не плодит дубли, читается и старый .xls.
- **Чек.** возвраты: Строки возвратов в чеке не выходят за край, а сумма не рвётся по цифре.
- **Один аккаунт для кассы и владельца.** В кассу и в программу владельца можно входить одним аккаунтом владельца; общий ключ устройства создаётся атомарно.
- **Закрытие кассы установщиком.** Исправлена ошибка при закрытии кассы по сигналу установщика (ObjectDisposedException).
- **Штрихкоды и ценники.** Раскладка не дрожит при смене масштаба; в новом окне ценника в высоких окнах панель шириной 1560 px.

# Кыргызча

### Эмнелер жаңы

- **Автобөлүктөр.** прайс импорту: «Жапырт жүктөө» устасында жаңы профиль «Жеткирүүчүнүн прайси (автобөлүктөр)»: мамычалар синонимдери боюнча табылат, топтор жана баасы жок саптар өткөрүлөт, «10-20» түрүндөгү калдык диапазон катары окулат, кайра жүктөө кайталанмаларды жаратпайт, эски .xls да окулат.
- **Чек.** кайтаруулар: Чектеги кайтаруу саптары четтен чыкпайт, сумма цифра боюнча үзүлбөйт.
- **Касса жана ээси үчүн бир аккаунт.** Кассага да, ээсинин программасына да ээсинин бир аккаунту менен кирүүгө болот; түзмөктүн жалпы ачкычы атомардык түрдө түзүлөт.
- **Кассаны орноткуч жапканда.** Орноткучтун сигналы боюнча кассаны жапкандагы ката оңдолду (ObjectDisposedException).
- **Штрих-коддор жана баа белгилери.** Масштабды өзгөрткөндө жайгашуу титиреп калбайт; баа белгисинин жаңы терезесинде бийик терезелерде панель 1560 px.

# English

### What's new

- **Auto parts.** price list import: The “Bulk import” wizard has a new profile “Supplier price list (auto parts)”: columns are found by synonyms, groups and rows without a price are skipped, a stock like “10-20” is read as a range, re-uploading does not create duplicates, and the old .xls format is readable.
- **Receipt.** returns: Return lines on the receipt no longer go past the edge, and the total is no longer split in the middle of a number.
- **One account for the till and the owner.** You can sign in to both the till and the owner program with one owner account; the shared device key is created atomically.
- **Closing the till by the installer.** Fixed an error when the till is closed by the installer's signal (ObjectDisposedException).
- **Barcodes and price tags.** The layout no longer jitters when the scale changes; in the new price tag window the panel is 1560 px in tall windows.

# Türkçe

### Yenilikler

- **Oto yedek parça.** fiyat listesi içe aktarma: «Toplu yükleme» sihirbazında yeni «Tedarikçi fiyat listesi (oto yedek parça)» profili: sütunlar eş anlamlılarla bulunur, grup satırları ve fiyatsız satırlar atlanır, «10-20» gibi stok aralık olarak okunur, yeniden yükleme kopya oluşturmaz, eski .xls da okunur.
- **Fiş.** iadeler: Fişteki iade satırları kenardan taşmaz, tutar rakamın ortasından bölünmez.
- **Kasa ve sahip için tek hesap.** Hem kasaya hem sahip programına tek sahip hesabıyla girilebilir; ortak cihaz anahtarı atomik olarak oluşturulur.
- **Kurulum programı kasayı kapatırken.** Kasa kurulum programının sinyaliyle kapatılırken oluşan hata düzeltildi (ObjectDisposedException).
- **Barkodlar ve fiyat etiketleri.** Ölçek değişince yerleşim titremez; yeni etiket penceresinde yüksek pencerelerde panel 1560 px.

# O'zbekcha

### Yangiliklar

- **Avtoehtiyot qismlar.** narx-navo importi: «Ommaviy yuklash» ustasida yangi profil «Yetkazib beruvchi narx-navosi (avtoehtiyot qismlar)»: ustunlar sinonimlar bo'yicha topiladi, guruhlar va narxsiz qatorlar o'tkazib yuboriladi, «10-20» ko'rinishidagi qoldiq oraliq sifatida o'qiladi, qayta yuklash dublikat yaratmaydi, eski .xls ham o'qiladi.
- **Chek.** qaytarishlar: Chekdagi qaytarish qatorlari chetdan chiqmaydi, summa raqam o'rtasidan uzilmaydi.
- **Kassa va egasi uchun bitta akkaunt.** Kassaga ham, egasi dasturiga ham egasining bitta akkaunti bilan kirish mumkin; qurilmaning umumiy kaliti atomar yaratiladi.
- **O'rnatuvchi kassani yopganda.** O'rnatuvchi signali bo'yicha kassa yopilganda yuz beradigan xato tuzatildi (ObjectDisposedException).
- **Shtrix-kodlar va narx yorliqlari.** Masshtab o'zgarganda joylashuv titramaydi; narx yorlig'ining yangi oynasida baland oynalarda panel 1560 px.
