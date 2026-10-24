## v1.17.60 (тестовая версия)

**Тестовая версия — только для тестировщиков.** Обычные кассы её не получают, пока тестирование не закончено. Тестировщик: «Настройки → Обновления → Тестовые версии», ввести код тестировщика → «Проверить обновления» → «Обновить».

Новое окно оплаты, Маркетплейс как магазин приложений, красивый чек, массовое добавление товаров сканером, долг клиента как на сервере, Z-отчёт как X-отчёт, воронка с чатом, самообновляющаяся Сводка владельца и исправленные утечки памяти.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).

Обновление не меняет ваши настройки.

---

# Русский

### Новое

- **Новое окно оплаты.** Компактное, в две колонки, без прокрутки; клиент выбирается карточкой (до 6 строк). При долге крупно видны сумма чека и «Получить сейчас», ниже — «Чек · сейчас · в долг».
- **Маркетплейс как магазин приложений.** Витрина, страница «Подробнее» со слайдами, анимацией, блоком «Чем поможет» и ценой; темы и виды с превью; купленное помечено «Куплено», без цены.
- **Красивый чек-«бумага»** в истории чеков (касса и владелец); значок «Подробнее» и карточка товара.
- **Склад.** Массовое добавление товаров сканером: поле «Штрихкод — Enter», количество, «Очистить».
- **Поставщики.** Кнопка «Погасить весь долг»: оплачиваются все взносы сразу, а записи, похожие на двойные, не оплачиваются и подписаны.
- **Долг клиента** считается как на сервере; двойные записи помечены.
- **«Выручка (без долга)»** — одинаково во всех отчётах и на плитках.
- **Z-отчёт совпадает с X-отчётом**, в нём раскладка «в ящике должно быть». При открытии смены остаток берётся из последней закрытой. Если смена старше 22 часов — предупреждение.
- **Воронка (владелец).** Клик по клиенту открывает чат (WhatsApp, Instagram, Telegram); стрелки ◀ ▶ крупные.
- **Сводка владельца** обновляется сама раз в минуту. **Пустой счёт** нельзя выставить. **Дубль телефона** клиента виден сразу.
- **«Горячие клавиши» и «Импорт»** — в общем стиле окон. **История чеков** открывается по размеру окна кассы. **«Пополнение»** — без услуг.
- **Кто за кассой.** После входа имя кассира видно в меню, в «Внесении/изъятии» и в «Удалено из чека».
- **Окна не уходят под панель задач.**
- **Исправлены утечки памяти** (закрытые окна, миниатюры, темы).

### Было → стало

| Что | Было | Стало |
|---|---|---|
| Окно оплаты | Узкое, почти во всю высоту экрана | Компактное, две колонки |
| Оплата в долг | Не было крупной суммы «сейчас» | «Получить сейчас» и сумма чека |
| Маркетплейс | Список функций | Витрина, «Подробнее», слайды, «Куплено» |
| Долг клиента | В списке и внутри окна числа расходились | Как на сервере, двойные записи помечены |
| Z-отчёт | Свои цифры и строка «Корректировка по данным сервера» | Те же строки, что в X, «в ящике должно быть» |
| Клик по клиенту из Telegram в воронке | Открывался редактор | Чат клиента |
| Сводка владельца | Обновлялась вручную | Раз в минуту сама |
| «Горячие клавиши», «Импорт» | Свой вид | Общий стиль окон |

### Проверено перед выпуском (приёмка 24.10, тестовый аккаунт NBS)

- Деньги: наличные, безнал, долг с предоплатой и сроком, оплата долга, внесение, изъятие, X-отчёт и Z-отчёт — расхождение с сервером 0.
- Окно оплаты: размеры в норме, прокрутки нет, клавиши и фокус работают.
- Маркетплейс: витрина, «Подробнее», слайды, полный экран, «Как включить».
- Сводка владельца обновилась в фоне не позже чем через 49 секунд после продажи.
- Память выходит на плато, утечки не видно.

### Ещё не проверено вживую / известные ограничения

- Правки по результатам приёмки (общий долг клиента, «Куплено» вместо цены, История чеков по размеру окна, клик и стрелки в воронке, «(без долга)» на плитках владельца) внесены после неё и пока не проверены вживую повторно.
- Часть экранов проверена на тестовом стенде, а не вживую у кассира. Не проверены: окно оплаты на кыргызском и в одну колонку, настоящий экран 1024×600, печать, возврат (у тестового кассира нет права), Z после оплаты долга в программе владельца.
- Касса сразу после запуска занимает около 1,5 ГБ памяти.
- Ограничения сервера NurCRM (передано в ТЗ бэкенду, касса работает в обход): выручка сайта не включает предоплату долговых чеков со «Смешанной» оплатой; возврат долгового чека с безналом идёт как «Наличные»; остаток кассы при закрытой смене; пересчёт цены при смене закупочной.
- Название магазина в кассе и у владельца пока может отличаться.
- «Табель», «Прокат» и «Логи» при окне кассы 1024 на большом мониторе шире окна.
- Тексты на английском, турецком и узбекском переведены без носителя языка.

---

# Кыргызча

**Тесттик версия — тестировщиктер үчүн гана.** Кадимки кассалар аны тестирлөө бүткөнчө албайт. Тестировщик: «Жөндөөлөр → Жаңыртуулар → Тесттик версиялар», кодду киргизип → «Жаңыртууларды текшерүү» → «Жаңыртуу».

### Жаңы

- **Төлөмдүн жаңы терезеси.** Ыкчам, эки тилкелүү, прокруткасыз; кардар карточка менен тандалат. Карызда чектин суммасы жана «Азыр алуу» чоң көрүнөт.
- **Маркетплейс — колдонмолор дүкөнүндөй.** Витрина, слайддар жана анимациясы бар «Кененирээк» барагы, «Эмне жардам берет» жана баасы; темалар менен көрүнүштөр алдын ала көрүнөт; сатып алынганы «Сатып алынды» деп белгиленет.
- **Кооз кагаз чек** чектердин тарыхында; «Кененирээк» белгиси жана товардын карточкасы.
- **Кампа.** Товарларды сканер менен жапырт кошуу: «Штрихкод — Enter», саны, «Тазалоо».
- **Жеткирүүчүлөр.** «Бүт карызды төлөө»: бардык төлөмдөр бир жолу төлөнөт, кош жазууга окшош саптар төлөнбөйт.
- **Кардардын карызы** сервердегидей эсептелет; кош жазуулар белгиленет.
- **«Түшүм (карызсыз)»** бардык отчётто бирдей.
- **Z-отчёт X-отчётко дал келет**, ичинде «ящикте болушу керек» бөлүштүрүү. Смена ачылганда калдык акыркы жабылгандан алынат. Смена 22 сааттан эски болсо — эскертүү.
- **Воронка (ээси).** Кардарды чыкылдатсаңыз чат ачылат (WhatsApp, Instagram, Telegram); жебелер чоң.
- **Ээсинин Жыйынтыгы** мүнөтүнө бир жолу өзү жаңыланат. **Бош эсепти** чыгарууга болбойт. **Телефондун кайталанганы** дароо көрүнөт.
- **«Ысык баскычтар» жана «Импорт»** терезелердин жалпы стилинде. **Чектердин тарыхы** кассанын терезесине жараша. **«Толуктоо»** кызматтарсыз.
- **Кассада ким отурат.** Киргенден кийин кассирдин аты менюда, «Киргизүү/алуу» жана «Чектен өчүрүлгөндө» көрүнөт.
- **Терезелер тапшырмалар панелинин астына кирбейт.**
- **Эс тутумдун агып кетүүлөрү оңдолду** (жабылган терезелер, миниатюралар, темалар).

### Мурун → азыр

| Эмне | Мурун | Азыр |
|---|---|---|
| Төлөм терезеси | Тар, экрандын бийиктигинин дээрлик баарында | Ыкчам, эки тилке |
| Карыз менен төлөм | «Азыр» суммасы чоң эмес | «Азыр алуу» жана чектин суммасы |
| Маркетплейс | Функциялар тизмеси | Витрина, «Кененирээк», слайддар |
| Кардардын карызы | Тизмеде жана терезеде сандар бирдей эмес | Сервердегидей |
| Z-отчёт | Өз сандары жана «Сервер боюнча оңдоо» сабы | X менен бирдей саптар |
| Воронкада Telegram кардарын чыкылдатуу | Редактор ачылчу | Кардардын чаты |
| Ээсинин Жыйынтыгы | Колу менен жаңыртылат | Мүнөтүнө бир жолу өзү |

### Чыгаруудан мурун текшерилди (24.10 кабыл алуу, NBS тесттик аккаунту)

- Акча: накталай, накталайсыз, предоплата жана мөөнөтү менен карыз, карызды төлөө, киргизүү, алуу, X жана Z — серверден айырма 0.
- Төлөм терезеси, Маркетплейс жана ээсинин Жыйынтыгын жаңыртуу иштейт; эс тутум бирдей деңгээлге чыгат.

### Дагы тирүү текшерилген жок / белгилүү чектөөлөр

- Кабыл алуудан кийин киргизилген оңдоолор (кардардын жалпы карызы, баанын ордуна «Сатып алынды», чектердин тарыхы, воронка, ээсинин плиткаларындагы «(карызсыз)») кайра тирүү текшерилген жок.
- Кээ бир экрандар тесттик стендде текшерилди, кассирде эмес. Текшерилбеди: кыргызча жана бир тилкелүү төлөм терезеси, чыныгы 1024×600 экран, басып чыгаруу, кайтаруу.
- Иштеп баштаганда касса болжол менен 1,5 ГБ эс тутум алат.
- NurCRM сервердин чектөөлөрү (бэкендге ТЗда берилген, касса айланып өтөт): сайттын түшүмү «Аралаш» төлөмдөгү карыз чектердин предоплатасын камтыбайт; накталайсыз карыз чекти кайтаруу «Накталай» болуп өтөт; смена жабык болгондо кассанын калдыгы; сатып алуу баасы өзгөргөндө баа кайра эсептелиши.
- Дүкөндүн аты кассада жана ээсинде айырмаланышы мүмкүн.
- Кассанын терезеси 1024 болгондо чоң мониторда «Табель», «Прокат» жана «Логдор» терезеден кененирээк.
- Англисче, түркчө жана өзбекче тексттер ошол тилдин ээсисиз которулган.

---

# English

**Test version — for testers only.** Regular tills do not get it until testing is finished. Tester: “Settings → Updates → Test versions”, enter the tester code → “Check for updates” → “Update”.

### New

- **New payment window.** Compact, two columns, no scrolling; the client is picked with a card. With a debt, the receipt total and “Receive now” are shown large.
- **Marketplace like an app store.** A showcase, a “Details” page with slides, animation, “How it helps” and the price; themes and looks with previews; purchased items are marked “Purchased”.
- **A nicer paper-style receipt** in the receipt history; the “Details” badge and the product card.
- **Stock.** Bulk adding of products with a scanner: “Barcode — Enter”, quantity, “Clear”.
- **Suppliers.** “Pay off the whole debt”: all instalments are paid at once, entries that look like duplicates are not paid.
- **A client's debt** is counted as on the server; duplicate entries are marked.
- **“Revenue (without debt)”** is the same in every report.
- **The Z-report matches the X-report** and has the “should be in the drawer” breakdown. The opening balance of a shift comes from the last closed one. A warning for a shift older than 22 hours.
- **Funnel (owner).** Clicking a client opens a chat (WhatsApp, Instagram, Telegram); the arrows are larger.
- **The owner's Summary** refreshes itself every minute. An **empty invoice** cannot be issued. A **duplicate phone** is shown at once.
- **“Hotkeys” and “Import”** in the common window style. **Receipt history** follows the till window size. **“Restocking”** without services.
- **Who is at the till.** After login the cashier's name is shown in the menu, in “Deposit/Withdrawal” and in “Removed from receipt”.
- **Windows no longer go under the taskbar.**
- **Memory leaks fixed** (closed windows, thumbnails, themes).

### Before → now

| What | Before | Now |
|---|---|---|
| Payment window | Narrow, almost the full screen height | Compact, two columns |
| Payment on debt | No large “now” amount | “Receive now” and the receipt total |
| Marketplace | A list of features | Showcase, “Details”, slides, “Purchased” |
| A client's debt | Numbers differed in the list and in the window | As on the server |
| Z-report | Its own figures and a “Correction from server data” line | The same lines as X |
| Click on a Telegram client in the funnel | The editor opened | The client's chat |
| Owner's Summary | Refreshed by hand | Every minute by itself |

### Checked before release (acceptance 24.10, NBS test account)

- Money: cash, non-cash, debt with prepayment and due date, debt payment, deposit, withdrawal, X and Z — difference with the server 0.
- The payment window, the Marketplace and the background refresh of the owner's Summary work; memory levels off.

### Not yet checked live / known limitations

- Fixes made after the acceptance (one total debt of a client, “Purchased” instead of the price, receipt history by window size, the funnel, “(without debt)” on the owner's tiles) have not been re-checked live yet.
- Some screens were checked on a test stand, not live at a cashier. Not checked: the payment window in Kyrgyz and in one column, a real 1024×600 screen, printing, returns.
- Right after start the till takes about 1.5 GB of memory.
- NurCRM server limitations (passed to the backend spec, the till works around them): the website revenue does not include the prepayment of debt receipts with “Mixed” payment; a return of a debt receipt with non-cash goes as “Cash”; the till balance while the shift is closed; the price recalculation when the purchase price changes.
- The store name in the till and in the owner app may still differ.
- “Timesheet”, “Rental” and “Logs” are wider than the window when the till window is 1024 on a large monitor.
- The English, Turkish and Uzbek texts were translated without a native speaker.

---

# Türkçe

**Test sürümü — yalnızca test kullanıcıları için.** Normal kasalar test bitene kadar almaz. Test kullanıcısı: «Ayarlar → Güncellemeler → Test sürümleri», kodu girin → «Güncellemeleri kontrol et» → «Güncelle».

### Yeni

- **Yeni ödeme penceresi.** Kompakt, iki sütunlu, kaydırmasız; müşteri kartla seçilir. Borçta fiş tutarı ve «Şimdi al» büyük görünür.
- **Uygulama mağazası gibi Pazar yeri.** Vitrin, slayt ve animasyonlu «Ayrıntılı» sayfası, «Nasıl yardımcı olur» ve fiyat; önizlemeli temalar ve görünümler; satın alınanlar «Satın alındı» diye işaretlenir.
- **Güzel kâğıt fiş** fiş geçmişinde; «Ayrıntılı» simgesi ve ürün kartı.
- **Depo.** Ürünlerin tarayıcıyla toplu eklenmesi: «Barkod — Enter», adet, «Temizle».
- **Tedarikçiler.** «Tüm borcu öde»: tüm taksitler birden ödenir, çift kayıt gibi görünenler ödenmez.
- **Müşteri borcu** sunucudaki gibi hesaplanır; çift kayıtlar işaretlenir.
- **«Ciro (borçsuz)»** tüm raporlarda aynı.
- **Z raporu X raporuyla aynıdır**, içinde «çekmecede olması gereken» dökümü var. Vardiya açılırken bakiye son kapanan vardiyadan alınır. 22 saatten eski vardiya için uyarı.
- **Satış hunisi (sahip).** Müşteriye tıklamak sohbeti açar (WhatsApp, Instagram, Telegram); oklar büyük.
- **Sahibin Özeti** dakikada bir kendiliğinden yenilenir. **Boş fatura** kesilemez. **Tekrarlanan telefon** hemen görünür.
- **«Kısayol tuşları» ve «İçe aktar»** ortak pencere tarzında. **Fiş geçmişi** kasa penceresinin boyutuna göre. **«Stok yenileme»** hizmetsiz.
- **Kasada kim var.** Girişten sonra kasiyerin adı menüde, «Para girişi/çıkışı» ve «Fişten silinenler» içinde görünür.
- **Pencereler görev çubuğunun altına girmez.**
- **Bellek sızıntıları giderildi** (kapatılan pencereler, küçük resimler, temalar).

### Önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| Ödeme penceresi | Dar, ekran yüksekliğinin neredeyse tamamı | Kompakt, iki sütun |
| Borçlu ödeme | Büyük «şimdi» tutarı yoktu | «Şimdi al» ve fiş tutarı |
| Pazar yeri | İşlev listesi | Vitrin, «Ayrıntılı», slaytlar |
| Müşteri borcu | Listede ve pencerede rakamlar farklıydı | Sunucudaki gibi |
| Z raporu | Kendi rakamları ve «Sunucu verisine göre düzeltme» satırı | X ile aynı satırlar |
| Hunide Telegram müşterisine tıklama | Düzenleyici açılırdı | Müşterinin sohbeti |
| Sahibin Özeti | Elle yenilenirdi | Dakikada bir kendiliğinden |

### Yayından önce kontrol edildi (24.10 kabul, NBS test hesabı)

- Para: nakit, nakitsiz, ön ödemeli ve vadeli borç, borç ödeme, para girişi, çıkışı, X ve Z — sunucuyla fark 0.
- Ödeme penceresi, Pazar yeri ve sahip Özetinin arka planda yenilenmesi çalışıyor; bellek bir düzeyde duruyor.

### Henüz canlı kontrol edilmedi / bilinen sınırlamalar

- Kabulden sonra yapılan düzeltmeler (müşterinin tek borç tutarı, fiyat yerine «Satın alındı», fiş geçmişi, huni, sahip kutularında «(borçsuz)») henüz yeniden canlı kontrol edilmedi.
- Bazı ekranlar kasiyerde değil, test standında kontrol edildi. Kontrol edilmedi: Kırgızca ve tek sütunlu ödeme penceresi, gerçek 1024×600 ekran, yazdırma, iade.
- Başladıktan hemen sonra kasa yaklaşık 1,5 GB bellek kullanır.
- NurCRM sunucu sınırlamaları (arka uç şartnamesine iletildi, kasa dolaşır): site cirosu «Karma» ödemeli borçlu fişlerin ön ödemesini içermez; nakitsiz borçlu fişin iadesi «Nakit» olarak geçer; vardiya kapalıyken kasa bakiyesi; alış fiyatı değişince fiyatın yeniden hesaplanması.
- Mağaza adı kasada ve sahipte farklı olabilir.
- Kasa penceresi 1024 iken büyük monitörde «Puantaj», «Kiralama» ve «Günlükler» pencereden geniştir.
- İngilizce, Türkçe ve Özbekçe metinler ana dili konuşan biri olmadan çevrildi.

---

# O'zbekcha

**Test versiyasi — faqat testerlar uchun.** Oddiy kassalar sinov tugamaguncha uni olmaydi. Tester: «Sozlamalar → Yangilanishlar → Test versiyalari», kodni kiriting → «Yangilanishlarni tekshirish» → «Yangilash».

### Yangi

- **Yangi to'lov oynasi.** Ixcham, ikki ustunli, aylantirishsiz; mijoz karta orqali tanlanadi. Qarzda chek summasi va «Hozir olish» katta ko'rinadi.
- **Ilovalar do'koni kabi Marketplace.** Vitrina, slaydlar va animatsiyali «Batafsil» sahifasi, «Qanday yordam beradi» va narx; oldindan ko'rish bilan mavzular va ko'rinishlar; sotib olingani «Sotib olingan» deb belgilanadi.
- **Chiroyli qog'oz chek** cheklar tarixida; «Batafsil» belgisi va mahsulot kartasi.
- **Ombor.** Mahsulotlarni skaner bilan ommaviy qo'shish: «Shtrix-kod — Enter», soni, «Tozalash».
- **Yetkazib beruvchilar.** «Butun qarzni to'lash»: barcha badallar birdan to'lanadi, qo'sh yozuvga o'xshash qatorlar to'lanmaydi.
- **Mijoz qarzi** serverdagidek hisoblanadi; qo'sh yozuvlar belgilanadi.
- **«Tushum (qarzsiz)»** barcha hisobotlarda bir xil.
- **Z-hisobot X-hisobotga mos keladi**, unda «tortmada bo'lishi kerak» taqsimoti bor. Smena ochilganda qoldiq oxirgi yopilgan smenadan olinadi. Smena 22 soatdan eski bo'lsa — ogohlantirish.
- **Voronka (egasi).** Mijozga bosish chatni ochadi (WhatsApp, Instagram, Telegram); strelkalar katta.
- **Egasining Xulosasi** daqiqada bir marta o'zi yangilanadi. **Bo'sh hisobni** chiqarib bo'lmaydi. **Telefon takrori** darhol ko'rinadi.
- **«Tezkor tugmalar» va «Import»** oynalarning umumiy uslubida. **Cheklar tarixi** kassa oynasi o'lchamiga qarab. **«To'ldirish»** xizmatlarsiz.
- **Kassada kim turibdi.** Kirgandan keyin kassir ismi menyuda, «Kirim/chiqim» va «Chekdan o'chirilganlar»da ko'rinadi.
- **Oynalar vazifalar paneli ostiga kirmaydi.**
- **Xotira oqishlari tuzatildi** (yopilgan oynalar, kichik rasmlar, mavzular).

### Avval → hozir

| Nima | Avval | Hozir |
|---|---|---|
| To'lov oynasi | Tor, ekran balandligining deyarli hammasi | Ixcham, ikki ustun |
| Qarzga to'lov | Katta «hozir» summasi yo'q edi | «Hozir olish» va chek summasi |
| Marketplace | Funksiyalar ro'yxati | Vitrina, «Batafsil», slaydlar |
| Mijoz qarzi | Ro'yxatda va oynada raqamlar farq qilardi | Serverdagidek |
| Z-hisobot | O'z raqamlari va «Server ma'lumotiga ko'ra tuzatish» qatori | X bilan bir xil qatorlar |
| Voronkada Telegram mijoziga bosish | Muharrir ochilardi | Mijoz chati |
| Egasining Xulosasi | Qo'lda yangilanardi | Daqiqada bir marta o'zi |

### Chiqarishdan oldin tekshirildi (24.10 qabul, NBS test akkaunti)

- Pul: naqd, naqdsiz, oldindan to'lov va muddatli qarz, qarzni to'lash, kirim, chiqim, X va Z — serverdan farq 0.
- To'lov oynasi, Marketplace va egasi Xulosasining fonda yangilanishi ishlaydi; xotira bir darajada turadi.

### Hali jonli tekshirilmadi / ma'lum cheklovlar

- Qabuldan keyin kiritilgan tuzatishlar (mijozning yagona qarzi, narx o'rniga «Sotib olingan», cheklar tarixi, voronka, egasi plitkalarida «(qarzsiz)») hali qayta jonli tekshirilmadi.
- Ba'zi ekranlar kassirda emas, test stendida tekshirildi. Tekshirilmadi: qirg'izcha va bir ustunli to'lov oynasi, haqiqiy 1024×600 ekran, chop etish, qaytarish.
- Ishga tushgandan so'ng kassa taxminan 1,5 GB xotira oladi.
- NurCRM server cheklovlari (backend TZga berilgan, kassa aylanib o'tadi): sayt tushumi «Aralash» to'lovdagi qarz cheklarining oldindan to'lovini o'z ichiga olmaydi; naqdsiz qarz chekini qaytarish «Naqd» bo'lib o'tadi; smena yopiq bo'lganda kassa qoldig'i; xarid narxi o'zgarganda narxni qayta hisoblash.
- Do'kon nomi kassada va egasida farq qilishi mumkin.
- Kassa oynasi 1024 bo'lganda katta monitorda «Tabel», «Prokat» va «Loglar» oynadan kengroq.
- Inglizcha, turkcha va o'zbekcha matnlar ona tilida so'zlashuvchisiz tarjima qilingan.
