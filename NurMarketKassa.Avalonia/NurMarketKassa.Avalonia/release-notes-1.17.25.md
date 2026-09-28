## v1.17.25 — тестовая версия

---

# Русский

### Новое

- **Оплата одним запросом.** Касса проводит продажу одним обращением к серверу NurCRM вместо 4–5. Сервер отвечает за 0,3–0,4 с (было 1,5–1,9 с). У каждой оплаты свой ключ: повтор после обрыва связи не создаёт второй чек. Офлайн-чеки досылаются так же — без риска дублей.
- **Смешанная оплата с разбивкой.** Сумма наличными и безналом уходит на сервер отдельно.
- **«Оплата долга» одной суммой.** Новый блок «Погасить одной суммой»: сервер сам раскладывает деньги по взносам, от старых к новым. Долг из 16 взносов закрывается за ~1 с (раньше около 15 с).
- **Весы TM-30F (Dahua) — прямая отправка из кассы.** Окно «Весы» → марка «TM-30F (Dahua)» → «Отправить на весы». Протокол сверен с записью обмена программы «Русский масштаб» с весами — совпадение байт в байт. Запасной путь — файл для их программы. Окно «Настройки весов TM-30F»: проверка связи, 15 форматов штрихкода, «Price point».
- **Поиск весов во всех подсетях.** Заводские подсети весов, своя подсеть или диапазон, временный адрес компьютера на время поиска (с подтверждением, адрес удаляется сам). Весы TM-30F узнаются по порту 4001.
- **Telegram-бот:** должники одним запросом к серверу (быстрее и без завышенных сумм), /ostatki с сервера, привязка клиента к Telegram хранится на сервере — напоминания работают с любой кассы.
- **Вид магазина** (продукты / одежда / услуги) берётся из настроек компании на сервере; смена вида в кассе сохраняется на сервер (для владельца).
- **Знак валюты Штрих-ПРИНТ:** кнопка «Отразить ↔», если на экране весов знак вышел зеркальным.
- **Отчёт смены с сервера.** «Детали смены», Z-отчёт и отчёт у владельца берут цифры с сервера: смешанная оплата раздельно, возвраты, внесения и изъятия, скидки, ожидаемая сумма. Сводка смены в Telegram — тоже.
- **Внесение входит в сумму смены.** Внесение уходит на сервер отдельным видом операции — сумма в кассе при закрытии смены больше не расходится на величину внесений.
- **Постоянный номер чека**, как на сайте (например 001125): в «Истории чеков», в окне «Возврат» и на чеке возврата; поиск чека по номеру в «Возврате».

### Исправлено: было → стало

| Что | Было | Стало |
|---|---|---|
| Первый чек после «Открыть смену» | Если касса запустилась без своей смены, чек уходил в офлайн-очередь при живой связи; «в долг» и смешанная для него были запрещены | Чек переносится на сервер перед оплатой |
| Сбой сервера NurCRM (502) при оплате | Кассир видел «error code: 502» | «Сервер временно не отвечает… деньги не списаны, чек сохранён». Перед этим касса проверяет, не прошла ли продажа, — без двойного чека |
| Rongta, способ «свой сервер» | В коде товара и отделе уходили нули — штрихкод этикетки был бы с нулевым кодом | Код = PLU, тип штрихкода 5, отдел 21 — как в файле с сайта |
| TM-30F: цена и чтение товара | Цена отправлялась бы в целых сомах (120 → 1,20), чтение — неверной командой | Цена в тиынах, чтение «!0U» — по живой записи обмена |

### Проверено

- Стресс-тест в кассе: 26 из 27 оплат старым путём (одну сорвал сбой сервера 502 — после исправления повтор прошёл), затем 11 из 11 новым быстрым путём (сервер 280–400 мс, номер чека приходит сразу).
- Новый адрес оплаты — 6 продаж на тестовом аккаунте: наличные, безнал, смешанная с разбивкой, в долг с предоплатой, скидки строки и чека, «Доп. услуга», консультант; повтор с тем же ключом — продажа одна.
- Погашение долга одной суммой на тестовом клиенте: 16 взносов за 1,22 с, повтор с тем же ключом деньги второй раз не проводит, переплата отклоняется.
- Драйвер TM-30F: три строки товаров совпали с записью обмена байт в байт; ответы весов разбираются верно.
- Цикл смены в кассе: закрытие → Z-отчёт с сервера (42 продажи, 2 534,50 сом = наличные 1 983,50 + безнал 551, расхождение 0) → открытие новой смены → 3 продажи.
- Внесение 10 сом на тестовой смене: ожидаемая сумма 905 → 915, изъятие вернуло 905. Возвраты за периоды сверены с сайтом (27.09: 5 на 195 сом; 01–28.09: 22 на 22 785,99).

### Не проверено (поэтому версия тестовая)

- Отправка на живые весы TM-30F из кассы (весы на другом компьютере); на весах сейчас формат штрихкода с суммой — для кассы нужно выбрать FFWWWWWNNNNNC (вес).
- Окно «Оплата долга» и выбор вида магазина — кнопками в кассе; бот — в настоящем Telegram.
- Окна «Возврат» и «История чеков» с новым номером — кнопками в кассе. В списке «Товары за смену» Z-отчёта сумма считается по истории кассы и может не совпадать с выручкой — отдельная задача.

---

# Кыргызча

### Жаңы

- **Бир суроо менен төлөм.** Касса сатууну NurCRM серверине 4–5 эмес, бир кайрылуу менен өткөрөт. Сервер 0,3–0,4 с жооп берет (мурун 1,5–1,9 с). Ар бир төлөмдүн өз ачкычы бар: байланыш үзүлүп кайталаганда экинчи чек түзүлбөйт. Офлайн чектер да ушундай жөнөтүлөт.
- **Аралаш төлөм бөлүнүп кетет:** накталай жана накталай эмес сумма серверге өзүнчө.
- **«Карызды төлөө» бир сумма менен.** Сервер акчаны төлөмдөргө эскиден жаңыга карай өзү бөлөт. 16 төлөмдөн турган карыз ~1 секундада жабылат (мурун ~15 с).
- **TM-30F (Dahua) таразалары — кассадан түз жөнөтүү.** «Таразалар» терезеси → «TM-30F (Dahua)» → «Таразага жөнөтүү». Протокол «Русский масштаб» программасынын жазылган алмашуусу менен байтка чейин дал келди.
- **Бардык подсеттерден таразаларды издөө:** таразалардын заводдук подсеттери, өз диапазонуңуз, издөө учурунда компьютерге убактылуу дарек (ырастоо менен).
- **Telegram-бот:** карыздар серверден бир суроо менен, /ostatki серверден, кардардын Telegram байланышы серверде сакталат.
- **Дүкөндүн түрү** компаниянын жөндөөлөрүнөн алынат; кассада өзгөртүү серверге сакталат (ээси үчүн).
- **Штрих-ПРИНТтин валюта белгиси:** «Күзгүдөй буруу ↔» баскычы.
- **Сменанын отчёту серверден:** «Сменанын чоо-жайы», Z-отчёт жана ээсинин отчёту — аралаш төлөм өзүнчө, кайтаруулар, салуу/алуу, арзандатуулар.
- **Салуу сменанын суммасына кирет** — сменаны жапканда айырма болбойт.
- **Чектин туруктуу номери** сайттагыдай: «Чектердин тарыхында», «Кайтарууда», номер боюнча издөө.

### Оңдолду: мурун → азыр

| Эмне | Мурун | Азыр |
|---|---|---|
| «Сменаны ачуудан» кийинки биринчи чек | Байланыш бар болсо да офлайн кезекке кетчү | Төлөмдөн мурун серверге өткөрүлөт |
| Төлөмдө NurCRM серверинин катасы (502) | «error code: 502» | Түшүнүктүү билдирүү; сатуу өткөнбү — алдын ала текшерилет, кош чек жок |
| Rongta, «өз сервер» жолу | Товардын коду нөл болуп кетчү | Код = PLU, штрих-код түрү 5, бөлүм 21 |
| TM-30F: баа жана окуу | Баа бүтүн сом менен кетмек, окуу туура эмес буйрук менен | Баа тыйын менен, окуу «!0U» |

### Текшерилди

- Кассада стресс-тест: эски жол менен 27ден 26 төлөм (бири сервердин 502 катасынан — оңдолгондон кийин кайталоо өттү), жаңы тез жол менен 11ден 11.
- Жаңы төлөм дареги тесттик аккаунтта 6 сатуу менен; бир ачкыч менен кайталоо — бир сатуу.
- Карызды бир сумма менен жабуу: 16 төлөм 1,22 с.
- TM-30F драйвери: үч сап жазылган алмашуу менен байтка чейин дал келди.

### Текшерилген жок (ошондуктан тесттик версия)

- Кассадан чыныгы TM-30F таразасына жөнөтүү; таразада азыр сумма форматы — кассага FFWWWWWNNNNNC (салмак) тандоо керек.
- «Карызды төлөө» терезеси жана дүкөн түрү — кассадагы баскычтар менен; бот — чыныгы Telegramда.
- Кассада сменанын цикли: жабуу → серверден Z-отчёт (42 сатуу, 2 534,50 сом, айырма 0) → жаңы смена → 3 сатуу — текшерилди. «Кайтаруу» терезеси жаңы номер менен — кассада баскычтар менен текшерилген жок.

---

# English

### New

- **Payment in one request.** The till makes a sale with one call to the NurCRM server instead of 4–5. The server answers in 0.3–0.4 s (was 1.5–1.9 s). Each payment has its own key: a retry after a lost connection does not create a second receipt. Offline receipts are sent the same way.
- **Mixed payment is split** into cash and card on the server.
- **“Debt payment” with one sum.** The server spreads the money over instalments, oldest first. A 16-instalment debt closes in ~1 s (was ~15 s).
- **TM-30F (Dahua) scales — direct upload from the till.** “Scales” window → “TM-30F (Dahua)” → “Send to scale”. The protocol matches a recorded exchange of the “Russian scale” program byte for byte. Fallback: a file for their program.
- **Scale search in all subnets:** factory scale subnets, your own subnet or range, a temporary PC address during the search (with confirmation, removed automatically).
- **Telegram bot:** debtors in one server request, /ostatki from the server, the client’s Telegram link is stored on the server.
- **Shop type** is taken from the company settings on the server; changing it in the till saves to the server (owner).
- **Shtrikh-PRINT currency sign:** a “Mirror ↔” button.
- **Shift report from the server:** “Shift details”, the Z report and the owner’s report — mixed payment split, returns, cash in/out, discounts, expected cash.
- **Cash deposits count in the shift total** — no more difference at shift close.
- **Permanent receipt number** as on the website: in “Receipt history”, “Return”, search by number.

### Fixed: before → now

| What | Before | Now |
|---|---|---|
| First receipt after “Open shift” | Went to the offline queue while online | Moved to the server before payment |
| NurCRM server failure (502) on payment | “error code: 502” | A clear message; the till first checks whether the sale went through — no duplicate |
| Rongta, “own server” method | Product code went as zeros | Code = PLU, barcode type 5, department 21 |
| TM-30F: price and reading | Price would go in whole som, reading with a wrong command | Price in tyiyn, reading “!0U” |

### Checked

- Stress test in the till: 26 of 27 payments on the old path (one failed on a server 502 — the retry passed after the fix), then 11 of 11 on the new fast path.
- The new payment address — 6 sales on the test account; a retry with the same key gives one sale.
- Debt payment with one sum: 16 instalments in 1.22 s.
- TM-30F driver: three lines match the recorded exchange byte for byte.

### Not checked (hence a test release)

- Sending to real TM-30F scales from the till; the scale currently uses a total-price barcode — choose FFWWWWWNNNNNC (weight) for the till.
- The “Debt payment” window and shop type — by buttons in the till; the bot — in real Telegram.
- Shift cycle in the till: close → Z report from the server (42 sales, 2,534.50 som, difference 0) → new shift → 3 sales — checked. The “Return” window with the new number was not checked by buttons.

---

# Türkçe

### Yeni

- **Tek istekle ödeme.** Kasa satışı NurCRM sunucusuna 4–5 yerine tek çağrıyla yapar. Sunucu 0,3–0,4 sn’de yanıt verir (önce 1,5–1,9 sn). Her ödemenin kendi anahtarı var: bağlantı kopup tekrarlanınca ikinci fiş oluşmaz. Çevrimdışı fişler de böyle gönderilir.
- **Karışık ödeme ayrılmış gider:** nakit ve kart ayrı.
- **«Borç ödeme» tek tutarla.** Sunucu parayı taksitlere eskiden yeniye kendisi dağıtır. 16 taksitlik borç ~1 sn’de kapanır (önce ~15 sn).
- **TM-30F (Dahua) teraziler — kasadan doğrudan gönderme.** «Teraziler» → «TM-30F (Dahua)» → «Teraziye gönder». Protokol, «Russian scale» programının kayıtlı alışverişiyle bayt bayt aynı.
- **Tüm alt ağlarda terazi arama:** terazilerin fabrika alt ağları, kendi aralığınız, arama sırasında bilgisayara geçici adres (onayla, kendiliğinden silinir).
- **Telegram botu:** borçlular tek sunucu isteğiyle, /ostatki sunucudan, müşterinin Telegram bağlantısı sunucuda.
- **Mağaza türü** sunucudaki şirket ayarlarından alınır; kasada değiştirmek sunucuya kaydedilir (sahip).
- **Shtrikh-PRINT para işareti:** «Aynala ↔» düğmesi.
- **Vardiya raporu sunucudan:** «Vardiya ayrıntıları», Z raporu ve sahip raporu — karışık ödeme ayrı, iadeler, para giriş/çıkış, indirimler.
- **Para girişi vardiya toplamına dahil** — kapanışta fark yok.
- **Kalıcı fiş numarası** sitedeki gibi: «Fiş geçmişi», «İade», numaraya göre arama.

### Düzeltildi: önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| «Vardiya aç» sonrası ilk fiş | Bağlantı varken çevrimdışı kuyruğa gidiyordu | Ödemeden önce sunucuya taşınır |
| Ödemede NurCRM sunucu hatası (502) | «error code: 502» | Anlaşılır mesaj; satışın geçip geçmediği önce kontrol edilir — çift fiş yok |
| Rongta, «kendi sunucu» yolu | Ürün kodu sıfır gidiyordu | Kod = PLU, barkod türü 5, bölüm 21 |
| TM-30F: fiyat ve okuma | Fiyat tam som gidecekti, okuma yanlış komutla | Fiyat tiyin, okuma «!0U» |

### Kontrol edildi

- Kasada stres testi: eski yolla 27’den 26 ödeme (biri sunucu 502 hatası — düzeltmeden sonra tekrar geçti), yeni hızlı yolla 11’den 11.
- Yeni ödeme adresi test hesabında 6 satışla; aynı anahtarla tekrar — tek satış.
- Borcu tek tutarla kapatma: 16 taksit 1,22 sn.
- TM-30F sürücüsü: üç satır kayıtla bayt bayt aynı.

### Kontrol edilmedi (bu yüzden test sürümü)

- Kasadan gerçek TM-30F teraziye gönderme; terazide şu an tutar barkodu var — kasa için FFWWWWWNNNNNC (ağırlık) seçilmeli.
- «Borç ödeme» penceresi ve mağaza türü — kasada düğmelerle; bot — gerçek Telegram’da.
- Kasada vardiya döngüsü: kapatma → sunucudan Z raporu (42 satış, 2.534,50 som, fark 0) → yeni vardiya → 3 satış — kontrol edildi. Yeni numaralı «İade» penceresi düğmelerle denenmedi.

---

# O'zbekcha

### Yangi

- **Bitta so'rov bilan to'lov.** Kassa savdoni NurCRM serveriga 4–5 emas, bitta murojaat bilan o'tkazadi. Server 0,3–0,4 s da javob beradi (oldin 1,5–1,9 s). Har bir to'lovning o'z kaliti bor: aloqa uzilib takrorlanganda ikkinchi chek yaratilmaydi. Oflayn cheklar ham shunday yuboriladi.
- **Aralash to'lov ajratilib ketadi:** naqd va karta alohida.
- **«Qarz to'lovi» bitta summa bilan.** Server pulni to'lovlarga eskidan yangiga qarab o'zi taqsimlaydi. 16 to'lovli qarz ~1 soniyada yopiladi (oldin ~15 s).
- **TM-30F (Dahua) tarozilari — kassadan to'g'ridan-to'g'ri yuborish.** «Tarozilar» → «TM-30F (Dahua)» → «Taroziga yuborish». Protokol «Russian scale» dasturining yozib olingan almashuvi bilan baytma-bayt mos.
- **Barcha quyi tarmoqlarda tarozi qidirish:** tarozilarning zavod quyi tarmoqlari, o'z diapazoningiz, qidiruv paytida kompyuterga vaqtinchalik manzil (tasdiq bilan, o'zi o'chiriladi).
- **Telegram-bot:** qarzdorlar serverdan bitta so'rov bilan, /ostatki serverdan, mijozning Telegram bog'lanishi serverda.
- **Do'kon turi** serverdagi kompaniya sozlamalaridan olinadi; kassada o'zgartirish serverga saqlanadi (egasi uchun).
- **Shtrix-PRINT valyuta belgisi:** «Ko'zgu ↔» tugmasi.
- **Smena hisoboti serverdan:** «Smena tafsilotlari», Z hisobot va egasi hisoboti — aralash to'lov alohida, qaytarishlar, kiritish/olish, chegirmalar.
- **Kiritish smena summasiga kiradi** — yopishda farq bo'lmaydi.
- **Doimiy chek raqami** saytdagidek: «Cheklar tarixi», «Qaytarish», raqam bo'yicha qidirish.

### Tuzatildi: oldin → hozir

| Nima | Oldin | Hozir |
|---|---|---|
| «Smenani ochish»dan keyingi birinchi chek | Aloqa bo'lsa ham oflayn navbatga ketardi | To'lovdan oldin serverga o'tkaziladi |
| To'lovda NurCRM server xatosi (502) | «error code: 502» | Tushunarli xabar; savdo o'tganmi — oldin tekshiriladi, ikki chek yo'q |
| Rongta, «o'z server» usuli | Mahsulot kodi nol bo'lib ketardi | Kod = PLU, shtrix-kod turi 5, bo'lim 21 |
| TM-30F: narx va o'qish | Narx butun so'mda ketardi, o'qish noto'g'ri buyruq bilan | Narx tiyinda, o'qish «!0U» |

### Tekshirildi

- Kassada stress-test: eski yo'l bilan 27 dan 26 to'lov (biri server 502 xatosi — tuzatishdan keyin takror o'tdi), yangi tez yo'l bilan 11 dan 11.
- Yangi to'lov manzili test akkauntda 6 savdo bilan; bitta kalit bilan takror — bitta savdo.
- Qarzni bitta summa bilan yopish: 16 to'lov 1,22 s.
- TM-30F drayveri: uchta satr yozib olingan almashuv bilan baytma-bayt mos.

### Tekshirilmadi (shuning uchun test versiyasi)

- Kassadan haqiqiy TM-30F taroziga yuborish; tarozida hozir summa formati — kassa uchun FFWWWWWNNNNNC (vazn) tanlash kerak.
- «Qarz to'lovi» oynasi va do'kon turi — kassadagi tugmalar bilan; bot — haqiqiy Telegramda.
- Kassada smena sikli: yopish → serverdan Z hisobot (42 savdo, 2 534,50 so'm, farq 0) → yangi smena → 3 savdo — tekshirildi. Yangi raqamli «Qaytarish» oynasi tugmalar bilan tekshirilmadi.
