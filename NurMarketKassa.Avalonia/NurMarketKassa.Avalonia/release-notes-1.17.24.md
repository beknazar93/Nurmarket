## v1.17.24 — тестовая версия

---

# Русский

### Новое

- **Поиск весов в сети.** Настройки → Весы → «Поиск весов в сети…» (и в окне «Весы»). Касса за несколько секунд проверяет все адреса вашей сети и показывает устройства: адрес, производителя, открыты ли порты весов, и догадку — «Штрих-ПРИНТ», «похоже на TM-30F», «возможно Rongta». Кнопка «Использовать для…» записывает адрес в настройки нужных весов.
- **Окна настроек для каждых весов.** «Настройки весов выбранной марки…»:
  - **Rongta** — адрес и проверка связи, оба способа загрузки товаров, типы штрихкода со сверкой с кассой и кнопкой «Подобрать под кассу»;
  - **TM-30F** — адрес и проверка связи, какие значения ввести в меню весов (Spec), конструктор формата штрихкода;
  - **Штрих-ПРИНТ** — новые вкладки «Валюта» и «Макет этикетки».
- **«СОМ» вместо «РУБ» на этикетке Штрих-ПРИНТ.** Надпись «ЦЕНА, РУБ/КГ» зашита в весы, поэтому мастер делает свой формат этикетки: встроенная надпись убирается, на её место ставится свой текст «ЦЕНА, СОМ/КГ». Там же — рисование знака валюты («с», «сом») для этикетки и экрана весов и курс валюты.
- **Макет этикетки Штрих-ПРИНТ** (Формат 1–5): все элементы этикетки с координатами в мм, шрифтами, перетаскиванием на схеме и предупреждением о наложениях.
- **Весы TM-30F (JHScale)** в списке марок: касса готовит файл товаров для их программы.
- **Поиск товаров** в окне «Весы».
- **Количество в строке чека вводится с клавиатуры** во всех видах кассы (Табличная, Карточки, Минимал, Профи, 1С), как в «Классике».
- **Быстрые товары можно отключить:** Настройки → Экран → «Быстрые товары в пустом чеке».
- **Код тестера** (Настройки → Обновления): зелёная галочка «Код принят» или красный крестик «Код неверный»; после верного кода касса сама проверяет обновления.

### Исправлено: было → стало

| Что | Было | Стало |
|---|---|---|
| F1–F12 при открытом окне товаров | Другая клавиша не срабатывала — окно надо было закрыть и нажать снова | Другая клавиша сразу показывает свои товары; та же клавиша или Esc закрывают окно |
| Марка весов, адрес весов Штрих-М и выбор источника Rongta | После перезапуска кассы сбрасывались на «Штрих-М» и пустой адрес | Сохраняются |
| Значки в меню настроек, меню смены, заголовках страниц | Надписи начинались с разного места («Монитор» уезжал), значок «Сотрудники», «Аккаунт», «Кастомизация», «Монитор покупателя» прилипал к краю плашки | Ровный столбец значков, значки по центру плашек |
| Пароль весов Штрих-ПРИНТ и код товара при прямой отправке (из 1.17.23) | «30» уходил как «3000»; в штрихкод шёл номер ячейки | «0030»; в штрихкод идёт PLU товара |

### Проверено

- Сборка установлена на рабочем компьютере, касса и программа владельца запускаются.
- F1 → F2 → F2 в кассе: окно сменилось на товары F2 и закрылось повторным нажатием.
- Поиск весов на сети этого компьютера: 254 адреса за 3,7 с, найдено 2 устройства (роутер и ещё одно), весов среди них нет — определено верно.
- Команды «Валюты» и «Макета этикетки» сверены с драйвером Штрих-М на эмуляторе весов: 21 из 21 совпали байт в байт.
- Штрихкод с этикетки 2101003003901 разбирается кассой как «Алма», 0,390 кг = 23,40 сом.
- Окна Rongta, TM-30F, поиска и вкладки Штрих-ПРИНТ проверены снимками на 5 языках.

### Не проверено (поэтому версия тестовая)

- На настоящих весах Штрих-ПРИНТ, Rongta и TM-30F — весов в сети не было.
- Полный прогон продаж, долга, возврата и смены на этой сборке.
- Ввод количества в видах кассы кроме «Классики» и переключатель быстрых товаров — не проверены вживую.

---

# Кыргызча

### Жаңы

- **Тармактан таразаларды издөө.** Жөндөөлөр → Таразалар → «Тармактан таразаларды издөө…» (жана «Таразалар» терезесинде). Касса бир нече секундда тармактын бардык даректерин текшерип, түзмөктөрдү көрсөтөт: дарек, өндүрүүчү, тараза порттору ачыкпы, жана божомол — «Штрих-ПРИНТ», «TM-30Fке окшош», «Rongta болушу мүмкүн». «…үчүн колдонуу» баскычы даректи керектүү таразанын жөндөөсүнө жазат.
- **Ар бир тараза үчүн жөндөө терезелери.** «Тандалган марканын жөндөөлөрү…»:
  - **Rongta** — дарек жана байланышты текшерүү, товар жүктөөнүн эки жолу, штрих-код түрлөрү касса менен салыштыруу жана «Кассага ылайыкта» баскычы менен;
  - **TM-30F** — дарек жана байланышты текшерүү, тараза менюсуна кандай маанилер (Spec) киргизүү, штрих-код форматынын конструктору;
  - **Штрих-ПРИНТ** — жаңы «Валюта» жана «Этикетканын макети» өтмөктөрү.
- **Штрих-ПРИНТ этикеткасында «РУБ» ордуна «СОМ».** «ЦЕНА, РУБ/КГ» жазуусу таразага бекитилген, ошондуктан устат өз форматын жасайт: орнотулган жазуу алынып, ордуна «ЦЕНА, СОМ/КГ» деген өз текст коюлат. Ошол эле жерде — этикетка жана тараза экраны үчүн валюта белгисин («с», «сом») тартуу жана валюта курсу.
- **Штрих-ПРИНТ этикеткасынын макети** (Формат 1–5): этикетканын бардык элементтери мм координаттары, шрифттери менен, схемада сүйрөө жана үстү-үстүнө түшүү эскертүүсү.
- **TM-30F (JHScale) таразалары** маркалардын тизмесинде: касса алардын программасы үчүн товарлар файлын даярдайт.
- «Таразалар» терезесинде **товар издөө**.
- **Чектин сабындагы санды клавиатурадан киргизүү** бардык касса көрүнүштөрүндө («Классикадагыдай»).
- **Ыкчам товарларды өчүрсө болот:** Жөндөөлөр → Экран.
- **Тестер коду:** жашыл «Код кабыл алынды» же кызыл «Код туура эмес»; туура коддон кийин касса жаңыртууларды өзү текшерет.

### Оңдолду: мурун → азыр

| Эмне | Мурун | Азыр |
|---|---|---|
| Товарлар терезеси ачык турганда F1–F12 | Башка баскыч иштечү эмес — терезени жаап, кайра басуу керек болчу | Башка баскыч дароо өз товарларын көрсөтөт; ошол эле баскыч же Esc жабат |
| Тараза маркасы, Штрих-М дареги, Rongta булагы | Касса кайра ачылганда «Штрих-М» жана бош дарекке кайтчу | Сакталат |
| Жөндөөлөр менюсундагы, смена менюсундагы, барак аталыштарындагы белгилер | Жазуулар ар кайсы жерден башталчу, кээ бир белгилер плашканын четине жабышчу | Белгилер бир түз тилкеде, плашкалардын ортосунда |

### Текшерилди

- Жыйнак жумушчу компьютерге орнотулду, касса жана ээсинин программасы иштейт.
- Кассада F1 → F2 → F2: терезе F2 товарларына алмашып, кайра басканда жабылды.
- Бул компьютердин тармагында таразаларды издөө: 3,7 секундда 254 дарек, 2 түзмөк табылды, таразалар жок — туура аныкталды.
- «Валюта» жана «Этикетканын макети» буйруктары эмулятордо Штрих-М драйвери менен салыштырылды: 21ден 21и байтка чейин дал келди.
- 2101003003901 штрих-коду касса тарабынан «Алма», 0,390 кг = 23,40 сом деп окулат.

### Текшерилген жок (ошондуктан тесттик версия)

- Чыныгы Штрих-ПРИНТ, Rongta жана TM-30F таразаларында.
- Бул жыйнакта сатуу, карыз, кайтаруу жана сменанын толук текшерүүсү.
- «Классикадан» башка көрүнүштөрдө сан киргизүү жана ыкчам товарлар которгучу жандуу текшерилген жок.

---

# English

### New

- **Find scales on the network.** Settings → Scales → “Find scales on the network…” (also in the “Scales” window). The till checks every address of your network in a few seconds and lists devices: address, vendor, open scale ports and a guess — “Shtrikh-PRINT”, “looks like TM-30F”, “possibly Rongta”. “Use for…” saves the address into the right scale settings.
- **A settings window for each scale brand** (“Settings of the selected brand…”):
  - **Rongta** — address and connection check, both product upload methods, barcode types checked against the till with a “Match the till” button;
  - **TM-30F** — address and connection check, which Spec values to enter on the scale, a barcode format builder;
  - **Shtrikh-PRINT** — new “Currency” and “Label layout” tabs.
- **“SOM” instead of “RUB” on Shtrikh-PRINT labels.** The caption “ЦЕНА, РУБ/КГ” is built into the scale, so a wizard creates a custom label format: the built-in caption is hidden and your own text “ЦЕНА, СОМ/КГ” is placed there. Also: drawing the currency sign (“с”, “сом”) for the label and the scale display, and the exchange rate.
- **Shtrikh-PRINT label layout** (Format 1–5): every label element with mm coordinates, fonts, drag-and-drop on a diagram and overlap warnings.
- **TM-30F (JHScale) scales** in the brand list: the till prepares a product file for their software.
- **Product search** in the “Scales” window.
- **The quantity in a receipt line can be typed** in every till layout, as in “Classic”.
- **Quick products can be turned off:** Settings → Screen.
- **Tester code:** a green “Code accepted” or red “Wrong code”; after a correct code the till checks for updates by itself.

### Fixed: before → now

| What | Before | Now |
|---|---|---|
| F1–F12 with the products window open | Another key did nothing — you had to close the window and press again | Another key shows its products at once; the same key or Esc closes the window |
| Scale brand, Shtrikh-M address, Rongta source | Reset to “Shtrikh-M” and an empty address after restart | Saved |
| Icons in settings menu, shift menu, page headers | Captions started at different places, some icons stuck to the badge edge | One straight icon column, icons centred |

### Checked

- Build installed on the work PC; the till and owner app start.
- F1 → F2 → F2 in the till: the window switched to F2 products and closed on the second press.
- Scale search on this PC's network: 254 addresses in 3.7 s, 2 devices found, no scales — detected correctly.
- “Currency” and “Label layout” commands compared with the Shtrikh-M driver on a scale emulator: 21 of 21 byte-identical.
- Barcode 2101003003901 is read by the till as “Алма”, 0.390 kg = 23.40 som.

### Not checked (hence a test release)

- On real Shtrikh-PRINT, Rongta and TM-30F scales.
- A full run of sales, debt, returns and shifts on this build.
- Quantity typing in layouts other than “Classic” and the quick products switch were not checked live.

---

# Türkçe

### Yeni

- **Ağda terazi arama.** Ayarlar → Teraziler → «Ağda terazi ara…» (ve «Teraziler» penceresinde). Kasa birkaç saniyede ağınızdaki tüm adresleri kontrol eder ve cihazları listeler: adres, üretici, terazi portları açık mı ve tahmin — «Shtrikh-PRINT», «TM-30F’e benziyor», «muhtemelen Rongta». «Şunun için kullan…» adresi ilgili terazinin ayarlarına kaydeder.
- **Her terazi markası için ayar penceresi** («Seçili markanın ayarları…»):
  - **Rongta** — adres ve bağlantı kontrolü, iki ürün yükleme yolu, kasayla karşılaştırılan barkod türleri ve «Kasaya uydur» düğmesi;
  - **TM-30F** — adres ve bağlantı kontrolü, terazi menüsüne girilecek Spec değerleri, barkod biçimi oluşturucu;
  - **Shtrikh-PRINT** — yeni «Para birimi» ve «Etiket düzeni» sekmeleri.
- **Shtrikh-PRINT etiketinde «RUB» yerine «SOM».** «ЦЕНА, РУБ/КГ» yazısı teraziye gömülüdür; sihirbaz özel bir etiket biçimi oluşturur: yerleşik yazı gizlenir, yerine kendi metniniz «ЦЕНА, СОМ/КГ» konur. Ayrıca: etiket ve terazi ekranı için para işareti («с», «сом») çizimi ve döviz kuru.
- **Shtrikh-PRINT etiket düzeni** (Biçim 1–5): tüm etiket öğeleri mm koordinatları ve yazı tipleriyle, şemada sürükleme ve çakışma uyarısı.
- **TM-30F (JHScale) teraziler** marka listesinde: kasa, onların programı için ürün dosyası hazırlar.
- «Teraziler» penceresinde **ürün arama**.
- **Fiş satırındaki miktar** tüm kasa görünümlerinde klavyeden girilebilir («Klasik»teki gibi).
- **Hızlı ürünler kapatılabilir:** Ayarlar → Ekran.
- **Test kodu:** yeşil «Kod kabul edildi» veya kırmızı «Kod yanlış»; doğru koddan sonra kasa güncellemeleri kendisi kontrol eder.

### Düzeltildi: önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| Ürün penceresi açıkken F1–F12 | Başka tuş çalışmıyordu — pencereyi kapatıp yeniden basmak gerekiyordu | Başka tuş hemen kendi ürünlerini gösterir; aynı tuş veya Esc kapatır |
| Terazi markası, Shtrikh-M adresi, Rongta kaynağı | Yeniden başlatınca «Shtrikh-M» ve boş adrese dönüyordu | Kaydediliyor |
| Ayarlar menüsü, vardiya menüsü, sayfa başlıklarındaki simgeler | Yazılar farklı yerlerden başlıyordu, bazı simgeler kenara yapışıyordu | Düz bir simge sütunu, simgeler ortada |

### Kontrol edildi

- Derleme iş bilgisayarına kuruldu; kasa ve sahip programı açılıyor.
- Kasada F1 → F2 → F2: pencere F2 ürünlerine geçti ve ikinci basışta kapandı.
- Bu bilgisayarın ağında terazi arama: 3,7 sn’de 254 adres, 2 cihaz bulundu, terazi yok — doğru belirlendi.
- «Para birimi» ve «Etiket düzeni» komutları emülatörde Shtrikh-M sürücüsüyle karşılaştırıldı: 21/21 bayt bayt aynı.
- 2101003003901 barkodu kasada «Алма», 0,390 kg = 23,40 som olarak okunuyor.

### Kontrol edilmedi (bu yüzden test sürümü)

- Gerçek Shtrikh-PRINT, Rongta ve TM-30F terazilerinde.
- Bu derlemede satış, borç, iade ve vardiya tam turu.
- «Klasik» dışındaki görünümlerde miktar girişi ve hızlı ürün anahtarı canlı denenmedi.

---

# O'zbekcha

### Yangi

- **Tarmoqda tarozilarni qidirish.** Sozlamalar → Tarozilar → «Tarmoqda tarozilarni qidirish…» (va «Tarozilar» oynasida). Kassa bir necha soniyada tarmoqdagi barcha manzillarni tekshiradi va qurilmalarni ko'rsatadi: manzil, ishlab chiqaruvchi, tarozi portlari ochiqmi va taxmin — «Shtrix-PRINT», «TM-30F ga o'xshaydi», «Rongta bo'lishi mumkin». «…uchun ishlatish» tugmasi manzilni kerakli tarozi sozlamalariga yozadi.
- **Har bir tarozi markasi uchun sozlash oynasi** («Tanlangan marka sozlamalari…»):
  - **Rongta** — manzil va aloqani tekshirish, mahsulot yuklashning ikki usuli, kassa bilan solishtirilgan shtrix-kod turlari va «Kassaga moslash» tugmasi;
  - **TM-30F** — manzil va aloqani tekshirish, tarozi menyusiga qanday qiymatlar (Spec) kiritish, shtrix-kod formati konstruktori;
  - **Shtrix-PRINT** — yangi «Valyuta» va «Yorliq maketi» yorliqlari.
- **Shtrix-PRINT yorlig'ida «RUB» o'rniga «SOM».** «ЦЕНА, РУБ/КГ» yozuvi taroziga o'rnatilgan, shuning uchun usta o'z yorliq formatini yaratadi: o'rnatilgan yozuv yashiriladi, o'rniga o'z matningiz «ЦЕНА, СОМ/КГ» qo'yiladi. U yerda — yorliq va tarozi ekrani uchun valyuta belgisini («с», «сом») chizish va valyuta kursi.
- **Shtrix-PRINT yorliq maketi** (Format 1–5): yorliqning barcha elementlari mm koordinatalari va shriftlari bilan, sxemada sudrab joylash va ustma-ust tushish ogohlantirishi.
- **TM-30F (JHScale) tarozilari** markalar ro'yxatida: kassa ularning dasturi uchun mahsulotlar faylini tayyorlaydi.
- «Tarozilar» oynasida **mahsulot qidirish**.
- **Chek qatoridagi miqdorni** barcha kassa ko'rinishlarida klaviaturadan kiritish mumkin («Klassika»dagidek).
- **Tezkor mahsulotlarni o'chirish mumkin:** Sozlamalar → Ekran.
- **Tester kodi:** yashil «Kod qabul qilindi» yoki qizil «Kod noto'g'ri»; to'g'ri koddan keyin kassa yangilanishlarni o'zi tekshiradi.

### Tuzatildi: oldin → hozir

| Nima | Oldin | Hozir |
|---|---|---|
| Mahsulotlar oynasi ochiq bo'lganda F1–F12 | Boshqa tugma ishlamasdi — oynani yopib, qayta bosish kerak edi | Boshqa tugma darhol o'z mahsulotlarini ko'rsatadi; o'sha tugma yoki Esc yopadi |
| Tarozi markasi, Shtrix-M manzili, Rongta manbasi | Qayta ishga tushirilganda «Shtrix-M» va bo'sh manzilga qaytardi | Saqlanadi |
| Sozlamalar menyusi, smena menyusi, sahifa sarlavhalaridagi belgilar | Yozuvlar turli joydan boshlanardi, ba'zi belgilar chetga yopishardi | Belgilar bir tekis ustunda, o'rtada |

### Tekshirildi

- Yig'ma ish kompyuteriga o'rnatildi; kassa va egasi dasturi ishga tushadi.
- Kassada F1 → F2 → F2: oyna F2 mahsulotlariga almashdi va qayta bosilganda yopildi.
- Ushbu kompyuter tarmog'ida tarozi qidirish: 3,7 soniyada 254 manzil, 2 qurilma topildi, tarozi yo'q — to'g'ri aniqlandi.
- «Valyuta» va «Yorliq maketi» buyruqlari emulyatorda Shtrix-M drayveri bilan solishtirildi: 21 dan 21 tasi baytma-bayt mos.
- 2101003003901 shtrix-kodi kassada «Алма», 0,390 kg = 23,40 som deb o'qiladi.

### Tekshirilmadi (shuning uchun test versiyasi)

- Haqiqiy Shtrix-PRINT, Rongta va TM-30F tarozilarida.
- Ushbu yig'mada savdo, qarz, qaytarish va smenaning to'liq tekshiruvi.
- «Klassika»dan boshqa ko'rinishlarda miqdor kiritish va tezkor mahsulotlar tugmasi jonli tekshirilmadi.
