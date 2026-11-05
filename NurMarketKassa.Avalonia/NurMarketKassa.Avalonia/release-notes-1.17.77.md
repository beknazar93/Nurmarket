## v1.17.77

Исправлена загрузка каталога при чистой установке и смене аккаунта (срочный фикс); исправления по независимому аудиту: признаки сервера при сбое связи, шаг округления весовых (весы TM-30F), PIN старшего, оповещения, смена компании, Android. Всё из 1.17.76 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.77.tar.gz`; macOS — `nurmarket-kassa-macos-arm64-1.17.77.zip` (Apple Silicon) и `nurmarket-kassa-macos-x64-1.17.77.zip` (Intel); Android — `nurmarket-kassa-android-1.17.77.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Загрузка каталога.** Исправлена загрузка каталога при чистой установке и смене аккаунта: весовые товары, штрихкоды и PLU больше не теряются (касса сама перезагрузит неполный каталог один раз).
- **Исправлено по независимому аудиту.** Признаки сервера не пропадают при сбое связи (оплата в долг не дублируется); шаг округления весовых берётся только по серверной единице (весы TM-30F); PIN старшего не обходится ошибкой сервера; оповещения не теряются; смена компании очищает отложенные записи.
- **Android.** Excel в программе владельца, накладная из файла, плашки не перехватывают фокус.

---

# Кыргызча

### Эмнелер жаңы

- **Каталогду жүктөө.** Таза орнотууда жана аккаунтту алмаштырууда каталогду жүктөө оңдолду: салмактуу товарлар, штрихкоддор жана PLU жоголбойт (касса толук эмес каталогду бир жолу өзү кайра жүктөйт).
- **Көз карандысыз аудит боюнча оңдоолор.** Байланыш үзүлгөндө сервердин белгилери жоголбойт (карызга төлөм кайталанбайт); салмактуу саптарды тегеректөө кадамы сервердин бирдиги боюнча гана алынат (TM-30F тараза); улуунун PIN-коду сервер катасы менен айланып өтпөйт; эскертмелер жоголбойт; компанияны алмаштырууда кечиктирилген жазуулар тазаланат.
- **Android.** Ээсинин программасында Excel, файлдан накладной, плашкалар фокусту тартып албайт.

---

# English

### What's new

- **Catalog loading.** Fixed catalog loading on a clean install and when switching accounts: weighed products, barcodes and PLU are no longer lost (the till reloads an incomplete catalog once by itself).
- **Fixes from the independent audit.** Server flags are not lost when the connection drops (a debt payment is not duplicated); the weighed-line rounding step comes only from the server unit (TM-30F scales); the senior PIN cannot be bypassed by a server error; notifications are not lost; switching company clears pending records.
- **Android.** Owner-program Excel, invoice from a file; banners no longer steal focus.

---

# Türkçe

### Yenilikler

- **Katalog yükleme.** Temiz kurulumda ve hesap değiştirirken katalog yükleme düzeltildi: tartılı ürünler, barkodlar ve PLU artık kaybolmaz (kasa eksik kataloğu bir kez kendisi yeniden yükler).
- **Bağımsız denetimden düzeltmeler.** Bağlantı koptuğunda sunucu bayrakları kaybolmaz (borçlu ödeme tekrarlanmaz); tartılı satır yuvarlama adımı yalnızca sunucu birimine göre alınır (TM-30F terazi); yönetici PIN'i sunucu hatasıyla atlanamaz; bildirimler kaybolmaz; şirket değiştirmek bekleyen kayıtları temizler.
- **Android.** Sahip programında Excel, dosyadan irsaliye; uyarı şeritleri odağı çalmaz.

---

# O'zbekcha

### Yangiliklar

- **Katalogni yuklash.** Toza o'rnatishda va akkauntni almashtirishda katalogni yuklash tuzatildi: tarozi tovarlar, shtrix-kodlar va PLU endi yo'qolmaydi (kassa to'liq bo'lmagan katalogni bir marta o'zi qayta yuklaydi).
- **Mustaqil auditdan tuzatishlar.** Aloqa uzilganda server belgilari yo'qolmaydi (qarzga to'lov takrorlanmaydi); tarozi qatorlarini yaxlitlash qadami faqat server birligi bo'yicha olinadi (TM-30F tarozi); katta PIN server xatosi bilan aylanib o'tilmaydi; ogohlantirishlar yo'qolmaydi; kompaniyani almashtirish kechiktirilgan yozuvlarni tozalaydi.
- **Android.** Egasi dasturida Excel, fayldan yuk xati; plashkalar fokusni tortib olmaydi.
