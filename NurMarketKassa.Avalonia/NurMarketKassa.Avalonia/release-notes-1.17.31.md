## v1.17.31

Изменения с прошлой версии для всех (1.17.30).

---

# Русский

### Исправлено: было → стало

| Что | Было | Стало |
|---|---|---|
| Товары в чеке после входа и после продажи | Иногда добавлялись сами: Enter от сканера нажимал плитку или кнопку, на которой остался фокус | Enter сканера не нажимает плитки и кнопки кассы |
| Окна на маленьких и квадратных экранах | Часть окон выходила за край экрана вместе с кнопками | Каждое окно ужимается до экрана кассы и встаёт по центру |
| Весы Rongta | Только через программу RLS1000 | Касса пишет товары в весы сама по сети (порт 4001), RLS1000 не нужна |
| Отправка на весы: «код в ШК … откроет …» | Вся отправка останавливалась | Касса сама подбирает код, который откроет именно этот товар, и пишет, что заменила |
| Этикетки весов, отправленных из программы владельца | Касса на том же компьютере не находила товар («Товар с кодом 00193 не найден») | Находит |
| Префикс весов, которые печатают сумму | Если у кассы для префикса стоял «вес», сумма читалась как вес | Касса знает, что эти весы печатают сумму |
| PLU нового весового товара | «Число весовых + 1» могло быть занято — сервер отказывал, товар не сохранялся | Берётся следующий свободный номер |
| «+ Новый чек» и сразу скан | Создавался ещё один лишний чек | Товар попадает в новый чек |
| Сервер не отвечает при оплате | Касса долго крутила ожидание | Через ~2 секунды продажа уходит в очередь и досылается сама, без повторов |
| «Свой сервер» Rongta ждёт программу весов | 90 секунд серой кнопки без отмены | Кнопка «Остановить»; смена способа прерывает ожидание |

### Новое

- **Весы Rongta напрямую** (Настройки → Весы → Rongta → «Напрямую по сети (без RLS1000)»): названия на кириллице, цена, срок годности, код в штрих-коде — прямо в весы.
- **PLU = сайт.** Товарам без PLU касса назначает PLU на сайте; PLU меняется прямо в окне «Весы» — и на сайте тоже.
- **«Кнопки весов…»** — сетка кнопок: нажмите кнопку и выберите товар. **«Лист кнопок»** — печать на A4 или Word: какая кнопка какой товар.
- **Окно «Весы»:** только весовые товары; фильтр «Показать» — весовые на сайте / все в кг / без PLU / с кнопкой; поиск по названию, PLU и коду.
- **«Инструкция подключения»** у каждых весов в Настройки → Весы — пошаговый план с анимациями (Rongta, Штрих-ПРИНТ, TM-30F, AI весы).
- **Штрих-ПРИНТ:** постоянный PLU за товаром, клавиши весов переходят за товаром, «Обновить с сервера».
- **Касса:** доп. штрих-код варианта — отдельной строкой; «Напечатать чек» запоминается.
- **Программа владельца:** «Заказы с сайта» и «Настройки сайта».

![Rongta IP](https://raw.githubusercontent.com/beknazar93/Nurmarket/v1.17.31/NurMarketKassa.Avalonia/NurMarketKassa.Avalonia/Assets/kb/guide-rongta-ip.gif)

![Scales window](https://raw.githubusercontent.com/beknazar93/Nurmarket/v1.17.31/NurMarketKassa.Avalonia/NurMarketKassa.Avalonia/Assets/kb/guide-kassa-send.gif)

### Проверено

- 9 продаж подряд (наличные и безнал) и продажа в долг на тестовом аккаунте — выручка и число чеков совпали с сервером (1103,00 / 21).
- Весы Rongta RLS1100 владельца: 6 серий этикеток — цена 12,34, штрих-код 20 34567 00290 8, названия «Тест», «Сыр 1кг», «Хлеб 5 Шт.», «Мясо»; отправлено 80 весовых товаров напрямую.
- Кодировщик названий на C# совпал с проверенным на этикетках байт в байт; запись PLU на сайт — на тестовом товаре (200; занятый номер — 400).
- «Лист кнопок» в Word — таблица «Кнопка · PLU · Товар · Цена · Код».
- Раньше в этой же сборке (30.09): сбой сервера — 1,8 с до очереди, 2 чека дошли ровно по разу; «+ Новый чек» + скан; доп. штрих-код; Z-отчёт; настройки клиента до/после — изменились только служебные поля.

### Не проверено

- Возврат, внесение/изъятие и закрытие смены на этой сборке (не менялись с 1.17.30).
- Печать «Листа кнопок» на бумаге A4; окна «Кнопки весов» и «Инструкция подключения» на маленьком экране.
- Раскладка кнопок на самих весах Rongta: у этих весов кнопка вызывает ячейку PLU с тем же номером — касса ставит товару PLU = номер кнопки.
- Штрих-ПРИНТ и TM-30F на живых весах в этой версии.

### Важно при этом обновлении

- Название и адрес магазина и настройки кассы обновление не меняет. Способ отправки Rongta остаётся прежним — «Напрямую по сети» включается в Настройки → Весы.

---

# Кыргызча

### Оңдолду: мурун → азыр

| Эмне | Мурун | Азыр |
|---|---|---|
| Кирүүдөн жана сатуудан кийин чектеги товарлар | Кээде өзүнөн-өзү кошулчу: сканердин Enter'и фокус калган плитканы же баскычты басчу | Сканердин Enter'и кассанын плиткаларын жана баскычтарын баспайт |
| Кичине жана чарчы экрандардагы терезелер | Кээ бир терезелер баскычтары менен экрандын четинен чыгып кетчү | Ар бир терезе кассанын экранына батат жана ортого турат |
| Rongta таразасы | RLS1000 программасы аркылуу гана | Касса товарларды таразага тармак аркылуу өзү жазат (4001 порт), RLS1000 керек эмес |
| Таразага жөнөтүү: «ШКдагы код … ачат» | Бүт жөнөтүү токтоп калчу | Касса ушул товарды ача турган кодду өзү тандайт жана алмаштырганын жазат |
| Ээсинин программасынан жөнөтүлгөн таразанын этикеткалары | Ошол эле компьютердеги касса товарды таппачу | Табат |
| Сумма басуучу таразанын префикси | Кассада префикс үчүн «салмак» турса, сумма салмак катары окулчу | Касса бул тараза сумма басарын билет |
| Жаңы салмактуу товардын PLU'су | «Салмактуулардын саны + 1» бош эмес болсо — сервер баш тартчу, товар сакталчу эмес | Кийинки бош номер алынат |
| «+ Жаңы чек» жана дароо скан | Дагы бир ашыкча чек ачылчу | Товар жаңы чекке түшөт |
| Төлөмдө сервер жооп бербесе | Касса узак күтчү | ~2 секунддан кийин сатуу кезекке кетип, өзү жөнөтүлөт |
| Rongta «өз сервери» тараза программасын күтөт | 90 секунд боз баскыч, жокко чыгаруусуз | «Токтотуу» баскычы; жолду өзгөртсө күтүү токтойт |

### Жаңы

- **Rongta таразасы түз** (Жөндөөлөр → Таразалар → Rongta → «Тармак аркылуу түз (RLS1000'сиз)»): кириллица аталыштар, баа, жарактуулук мөөнөтү, штрих-коддогу код — түз таразага.
- **PLU = сайт.** PLU'су жок товарларга касса сайтта PLU берет; PLU «Таразалар» терезесинде өзгөрөт — сайтта да.
- **«Тараза баскычтары…»** — баскычтын торчосу: баскычты басып товарды тандаңыз. **«Баскычтар барагы»** — A4 же Word.
- **«Таразалар» терезеси:** салмактуу товарлар гана; «Көрсөтүү» чыпкасы; аталыш, PLU жана код боюнча издөө.
- **«Туташтыруу нускамасы»** ар бир таразада — анимациялуу кадамдар (Rongta, Штрих-ПРИНТ, TM-30F, AI).
- **Штрих-ПРИНТ:** товардын туруктуу PLU'су, баскычтар товар менен көчөт, «Серверден жаңыртуу».
- **Касса:** варианттын кошумча штрих-коду — өзүнчө сап; «Чекти басып чыгаруу» эстелет.
- **Ээсинин программасы:** «Сайттан заказдар» жана «Сайттын жөндөөлөрү».

![Rongta IP](https://raw.githubusercontent.com/beknazar93/Nurmarket/v1.17.31/NurMarketKassa.Avalonia/NurMarketKassa.Avalonia/Assets/kb/guide-rongta-ip.ky.gif)

![Scales window](https://raw.githubusercontent.com/beknazar93/Nurmarket/v1.17.31/NurMarketKassa.Avalonia/NurMarketKassa.Avalonia/Assets/kb/guide-kassa-send.ky.gif)

### Текшерилди

- Тесттик аккаунтта 9 сатуу (накталай жана накталай эмес) жана карызга сатуу — киреше жана чектердин саны сервер менен дал келди (1103,00 / 21).
- Ээсинин Rongta RLS1100 таразасы: этикеткалардын 6 сериясы; 80 салмактуу товар түз жөнөтүлдү.
- C#'тагы аталыш коддоочу этикеткаларда текшерилген менен байт-байт дал келди; сайтка PLU жазуу тесттик товарда текшерилди.
- «Баскычтар барагы» Word'до — «Баскыч · PLU · Товар · Баа · Код» таблицасы.

### Текшерилген жок

- Ушул жыйындыда кайтаруу, акча салуу/алуу жана сменаны жабуу (1.17.30дан бери өзгөргөн жок).
- «Баскычтар барагын» A4 кагазга басып чыгаруу.
- Штрих-ПРИНТ жана TM-30F жандуу таразада ушул версияда.

### Бул жаңыртууда маанилүү

- Дүкөндүн аталышы, дареги жана кассанын жөндөөлөрүн жаңыртуу өзгөртпөйт. Rongta'нын жөнөтүү жолу мурункудай калат — «Тармак аркылуу түз» Жөндөөлөр → Таразалар ичинде күйгүзүлөт.

---

# English

### Fixed: before → now

| What | Before | Now |
|---|---|---|
| Goods in the receipt after sign-in and after a sale | Sometimes added by themselves: the scanner's Enter pressed a focused tile or button | The scanner's Enter no longer presses till tiles and buttons |
| Windows on small and square screens | Some windows went past the screen edge together with their buttons | Every window fits the till screen and is centred |
| Rongta scales | Only through RLS1000 | The till writes goods to the scale itself over the network (port 4001), RLS1000 not needed |
| Sending to a scale: “barcode code … opens …” | The whole upload stopped | The till picks a code that opens exactly this product and says what it replaced |
| Labels of a scale loaded from the owner app | The till on the same computer did not find the product | Found |
| Prefix of a scale that prints the amount | If the till had “weight” for that prefix, the amount was read as weight | The till knows this scale prints the amount |
| PLU of a new weighed product | “Weighed count + 1” could be taken — the server refused and the product was not saved | The next free number is used |
| “+ New receipt” and an immediate scan | One more extra receipt was created | The product goes to the new receipt |
| Server not answering at payment | The till waited for a long time | After ~2 seconds the sale goes to the queue and is sent later by itself |
| Rongta “own server” waiting for the scale software | 90 seconds of a grey button with no cancel | A “Stop” button; changing the method stops the wait |

### New

- **Rongta directly** (Settings → Scales → Rongta → “Directly over the network (no RLS1000)”): Cyrillic names, price, shelf life, barcode code — straight into the scale.
- **PLU = website.** Goods without a PLU get one on the website from the till; a PLU changed in the “Scales” window changes on the website too.
- **“Scale keys…”** — a grid of keys: press a key and choose a product. **“Key sheet”** — A4 print or Word.
- **“Scales” window:** weighed goods only; “Show” filter; search by name, PLU and code.
- **“Connection guide”** for every scale — steps with animations (Rongta, Shtrih-PRINT, TM-30F, AI).
- **Shtrih-PRINT:** fixed PLU per product, keys follow the product, “Refresh from server”.
- **Till:** a variant's extra barcode is a separate line; “Print receipt” is remembered.
- **Owner app:** “Website orders” and “Website settings”.

![Rongta IP](https://raw.githubusercontent.com/beknazar93/Nurmarket/v1.17.31/NurMarketKassa.Avalonia/NurMarketKassa.Avalonia/Assets/kb/guide-rongta-ip.en.gif)

![Scales window](https://raw.githubusercontent.com/beknazar93/Nurmarket/v1.17.31/NurMarketKassa.Avalonia/NurMarketKassa.Avalonia/Assets/kb/guide-kassa-send.en.gif)

### Checked

- 9 sales in a row (cash and non-cash) and a debt sale on the test account — revenue and receipt count match the server (1103.00 / 21).
- The owner's Rongta RLS1100: 6 label series; 80 weighed goods sent directly.
- The C# name encoder matches the label-verified one byte for byte; writing a PLU to the website checked on a test product.
- “Key sheet” in Word — a “Key · PLU · Product · Price · Code” table.

### Not checked

- Return, cash in/out and shift closing on this build (unchanged since 1.17.30).
- Printing the “Key sheet” on A4 paper.
- Shtrih-PRINT and TM-30F on live scales in this version.

### Important with this update

- The update does not change the store name, address or till settings. The Rongta upload method stays as it was — “Directly over the network” is turned on in Settings → Scales.

---

# Türkçe

### Düzeltildi: önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| Girişten ve satıştan sonra fişteki ürünler | Bazen kendiliğinden ekleniyordu: tarayıcının Enter'ı odaktaki karo veya düğmeye basıyordu | Tarayıcının Enter'ı kasa karo ve düğmelerine basmıyor |
| Küçük ve kare ekranlarda pencereler | Bazı pencereler düğmeleriyle birlikte ekran kenarından taşıyordu | Her pencere kasa ekranına sığar ve ortalanır |
| Rongta tartılar | Yalnızca RLS1000 ile | Kasa ürünleri tartıya ağ üzerinden kendisi yazar (port 4001), RLS1000 gerekmez |
| Tartıya gönderim: «barkod kodu … açar» | Tüm gönderim duruyordu | Kasa tam bu ürünü açacak kodu seçer ve neyi değiştirdiğini yazar |
| Sahip programından yüklenen tartı etiketleri | Aynı bilgisayardaki kasa ürünü bulmuyordu | Buluyor |
| Tutar basan tartının öneki | Kasada önek «ağırlık» ise tutar ağırlık olarak okunuyordu | Kasa bu tartının tutar bastığını biliyor |
| Yeni tartılı ürünün PLU'su | «Tartılı sayısı + 1» doluysa sunucu reddediyordu, ürün kaydedilmiyordu | Sonraki boş numara alınıyor |
| «+ Yeni fiş» ve hemen okutma | Fazladan bir fiş daha açılıyordu | Ürün yeni fişe düşüyor |
| Ödemede sunucu yanıt vermezse | Kasa uzun süre bekliyordu | ~2 saniye sonra satış kuyruğa gider, kendiliğinden gönderilir |
| Rongta «kendi sunucu» tartı programını bekler | 90 saniye gri düğme, iptal yok | «Durdur» düğmesi; yöntem değişince bekleme durur |

### Yeni

- **Rongta doğrudan** (Ayarlar → Tartılar → Rongta → «Doğrudan ağ üzerinden (RLS1000'siz)»): Kiril adlar, fiyat, raf ömrü, barkod kodu — doğrudan tartıya.
- **PLU = site.** PLU'su olmayan ürünlere kasa sitede PLU verir; «Tartı» penceresinde değişen PLU sitede de değişir.
- **«Tartı tuşları…»** — tuş ızgarası: tuşa basıp ürün seçin. **«Tuş listesi»** — A4 veya Word.
- **«Tartı» penceresi:** yalnızca tartılı ürünler; «Göster» filtresi; ad, PLU ve kodla arama.
- **«Bağlantı kılavuzu»** her tartıda — animasyonlu adımlar (Rongta, Shtrih-PRINT, TM-30F, AI).
- **Shtrih-PRINT:** ürüne sabit PLU, tuşlar ürünü takip eder, «Sunucudan yenile».
- **Kasa:** varyantın ek barkodu ayrı satır; «Fişi yazdır» hatırlanıyor.
- **Sahip programı:** «Site siparişleri» ve «Site ayarları».

![Rongta IP](https://raw.githubusercontent.com/beknazar93/Nurmarket/v1.17.31/NurMarketKassa.Avalonia/NurMarketKassa.Avalonia/Assets/kb/guide-rongta-ip.tr.gif)

![Scales window](https://raw.githubusercontent.com/beknazar93/Nurmarket/v1.17.31/NurMarketKassa.Avalonia/NurMarketKassa.Avalonia/Assets/kb/guide-kassa-send.tr.gif)

### Denendi

- Test hesabında art arda 9 satış (nakit ve kart) ve veresiye satış — ciro ve fiş sayısı sunucuyla aynı (1103,00 / 21).
- Sahibin Rongta RLS1100 tartısı: 6 etiket serisi; 80 tartılı ürün doğrudan gönderildi.
- C# ad kodlayıcı etiketlerde denenenle bayt bayt aynı; siteye PLU yazma test ürününde denendi.
- Word'de «Tuş listesi» — «Tuş · PLU · Ürün · Fiyat · Kod» tablosu.

### Denenmedi

- Bu derlemede iade, para giriş/çıkış ve vardiya kapatma (1.17.30'dan beri değişmedi).
- «Tuş listesi»nin A4 kâğıda basılması.
- Bu sürümde canlı Shtrih-PRINT ve TM-30F tartılar.

### Bu güncellemede önemli

- Güncelleme mağaza adını, adresini ve kasa ayarlarını değiştirmez. Rongta gönderim yolu olduğu gibi kalır — «Doğrudan ağ üzerinden» Ayarlar → Tartılar'da açılır.

---

# O‘zbekcha

### Tuzatildi: oldin → hozir

| Nima | Oldin | Hozir |
|---|---|---|
| Kirish va sotuvdan keyin chekdagi tovarlar | Ba’zan o‘z-o‘zidan qo‘shilardi: skanerning Enter'i fokusdagi plitka yoki tugmani bosardi | Skanerning Enter'i kassa plitkalari va tugmalarini bosmaydi |
| Kichik va kvadrat ekranlardagi oynalar | Ba’zi oynalar tugmalari bilan ekran chetidan chiqib ketardi | Har bir oyna kassa ekraniga sig‘adi va o‘rtaga turadi |
| Rongta tarozilari | Faqat RLS1000 orqali | Kassa tovarlarni taroziga tarmoq orqali o‘zi yozadi (4001 port), RLS1000 kerak emas |
| Taroziga yuborish: «ShKdagi kod … ochadi» | Butun yuborish to‘xtardi | Kassa aynan shu tovarni ochadigan kodni o‘zi tanlaydi va nimani almashtirganini yozadi |
| Egasi dasturidan yuklangan tarozi yorliqlari | Shu kompyuterdagi kassa tovarni topmasdi | Topadi |
| Summa bosadigan tarozi prefiksi | Kassada prefiks uchun «vazn» bo‘lsa, summa vazn deb o‘qilardi | Kassa bu tarozi summa bosishini biladi |
| Yangi vaznli tovar PLU'si | «Vaznlilar soni + 1» band bo‘lsa, server rad etardi, tovar saqlanmasdi | Keyingi bo‘sh raqam olinadi |
| «+ Yangi chek» va darhol skan | Yana bitta ortiqcha chek ochilardi | Tovar yangi chekka tushadi |
| To‘lovda server javob bermasa | Kassa uzoq kutardi | ~2 soniyadan keyin sotuv navbatga ketadi va o‘zi yuboriladi |
| Rongta «o‘z server» tarozi dasturini kutadi | 90 soniya kulrang tugma, bekor qilish yo‘q | «To‘xtatish» tugmasi; usul o‘zgarsa kutish to‘xtaydi |

### Yangi

- **Rongta to‘g‘ridan-to‘g‘ri** (Sozlamalar → Tarozilar → Rongta → «To‘g‘ridan-to‘g‘ri tarmoq orqali (RLS1000'siz)»): kirill nomlar, narx, yaroqlilik muddati, shtrix-koddagi kod — to‘g‘ridan-to‘g‘ri taroziga.
- **PLU = sayt.** PLU'si yo‘q tovarlarga kassa saytda PLU beradi; «Tarozi» oynasida o‘zgargan PLU saytda ham o‘zgaradi.
- **«Tarozi tugmalari…»** — tugmalar to‘ri: tugmani bosing va tovarni tanlang. **«Tugmalar varag‘i»** — A4 yoki Word.
- **«Tarozi» oynasi:** faqat vaznli tovarlar; «Ko‘rsatish» filtri; nom, PLU va kod bo‘yicha qidiruv.
- **«Ulanish yo‘riqnomasi»** har bir tarozida — animatsiyali qadamlar (Rongta, Shtrix-PRINT, TM-30F, AI).
- **Shtrix-PRINT:** tovarga doimiy PLU, tugmalar tovar bilan ko‘chadi, «Serverdan yangilash».
- **Kassa:** variantning qo‘shimcha shtrix-kodi — alohida qator; «Chekni chop etish» eslab qolinadi.
- **Egasi dasturi:** «Saytdan buyurtmalar» va «Sayt sozlamalari».

![Rongta IP](https://raw.githubusercontent.com/beknazar93/Nurmarket/v1.17.31/NurMarketKassa.Avalonia/NurMarketKassa.Avalonia/Assets/kb/guide-rongta-ip.uz.gif)

![Scales window](https://raw.githubusercontent.com/beknazar93/Nurmarket/v1.17.31/NurMarketKassa.Avalonia/NurMarketKassa.Avalonia/Assets/kb/guide-kassa-send.uz.gif)

### Tekshirildi

- Test akkauntida ketma-ket 9 ta sotuv (naqd va naqdsiz) va qarzga sotuv — tushum va cheklar soni server bilan bir xil (1103,00 / 21).
- Egasining Rongta RLS1100 tarozisi: 6 seriya yorliq; 80 ta vaznli tovar to‘g‘ridan-to‘g‘ri yuborildi.
- C# dagi nom kodlovchisi yorliqlarda tekshirilgani bilan bayt-bayt bir xil; saytga PLU yozish test tovarida tekshirildi.
- Word'dagi «Tugmalar varag‘i» — «Tugma · PLU · Tovar · Narx · Kod» jadvali.

### Tekshirilmadi

- Bu yig‘mada qaytarish, pul kiritish/olish va smenani yopish (1.17.30 dan beri o‘zgarmagan).
- «Tugmalar varag‘i»ni A4 qog‘ozga chop etish.
- Bu versiyada jonli Shtrix-PRINT va TM-30F tarozilari.

### Bu yangilanishda muhim

- Yangilanish do‘kon nomi, manzili va kassa sozlamalarini o‘zgartirmaydi. Rongta yuborish usuli avvalgidek qoladi — «To‘g‘ridan-to‘g‘ri tarmoq orqali» Sozlamalar → Tarozilar bo‘limida yoqiladi.
