## v1.17.63 (тестовая версия)

**Тестовая версия — только для тестировщиков.** Обычные кассы её не получают, пока тестирование не закончено. Тестировщик: «Настройки → Обновления → Тестовые версии», ввести код тестировщика → «Проверить обновления» → «Обновить».

Итоговая сборка за 27.10: исправления оплаты, цен, смен, возвратов и отчётов, сканера, окон и тёмной темы; программа владельца; загрузка электронных накладных, встроенный справочник товаров NurCRM, «Телефон как сканер».

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).

Обновление не меняет ваши настройки.

---

# Русский

### Новое

- **Электронные накладные.** Склад → Приёмка: загрузка накладных из Excel, CSV, XML, ТОРГ-12 и CommerceML; многостраничные накладные, защита от повторной загрузки, подтверждение похожих товаров.
- **Нур Советник.** Разделы открываются без нейросети, ответ строится из фактов — быстрее.
- **Окно оплаты по центру экрана**; настройка «прижимать к низу» возвращает прежний вид.
- **Темы сайта.** Фильтр «Для вашего магазина», предупреждение «тема не для вашей сферы», «Применить без картинок». **Склад → Загрузить фото:** окно предпросмотра (главное / в галерею).
- **Встроенный справочник товаров NurCRM** (128 276 товаров): подстановка названия, а где они есть в справочнике — бренда и категории (бренд есть примерно у 5 % записей, категория — у 16 %; если их нет, в окне «Новый товар» сказано: «бренда/категории в справочнике нет — заполните вручную»); обновляется с сервера по мере появления новых.
- **«Телефон как сканер».** Скан камерой мобилки попадает в чек кассы по Wi-Fi, по QR-сопряжению; подпись ответов, защита от повторов, отключение телефонов в настройках. Выключено по умолчанию: Настройки → Печать.
- **Цены.** Цены чека сверяются с сервером перед оплатой («Цена изменилась: 205 → 195»); лента изменений каталога обновляет цену на плитке за 8–10 с, если сервер её отдаёт; строгая проверка цены сервером (заголовок `X-NurMarket-Strict-Price`, ответ 409 обрабатывается).
- **Программа владельца.** Общие настройки идут через общий документ кассы (название магазина, коды сотрудников, причина удаления доходят до касс); история списаний и ревизий всех касс; плашка вместо окна «Товар сохранён»; имена кнопок для диктора; единый порог «мало».
- **Окна.** «Полный экран» применяется к кассе, программе владельца, окну входа и окнам разделов; масштаб 1024×600 не ниже 80 %; вид «1С»: Enter, Num+, Num−.
- **Тёмная тема.** Читаемые надписи кнопок (контраст не ниже 4,5:1), значки в кнопках.
- **Права** на «Горячие клавиши» и «Логи и ошибки»; предупреждение при закрытии настроек с несохранёнными правками.
- **Отчёты.** X-ОТЧЁТ и Z-ОТЧЁТ на 5 языках.
- **Убрано голосовое управление кассой** — теперь есть ИИ-ассистент.

### Исправлено: было → стало

| Было | Стало |
| --- | --- |
| Enter проводил продажу при неактивной кнопке «Оплатить» | Не проводит |
| Экран покупателя и QR показывали сумму без скидки и бонусов | Сумма после скидки и бонусов |
| После обрыва связи «в долг» и «смешанная» продажа могла пройти дважды | Защита от двойной продажи |
| Предоплата долга безналом на старом пути уходила как наличные | Уходит как безнал |
| Довнесение предоплаты долга терялось при перезапуске | Переживает перезапуск |
| Скан клавиатурным сканером при открытом окне оплаты мог попасть не туда | Оплату не проводит и в «Получено» не попадает; код встаёт в очередь: после оплаты товар в новом чеке, при отмене оплаты — в том же |
| Суммы «1.000», «1e3», «(100)» принимались | Дают ошибку |
| Быстрые купюры; «+» у дробного количества | Купюры 10/20; «+» работает с дробным |
| Офлайн-чек терял клиента | Клиент сохраняется |
| Склад после прихода откатывал цену | Цена не откатывается |
| Возврат со скидкой на чек мог быть больше оплаченного | Не больше оплаченного |
| Внесения/изъятия закрытой смены могли не дойти до сервера | Досылаются |
| Закрытие смены не ждало очередь чеков; закрытая «в очередь» смена могла ожить | Закрытие ждёт очередь; смена не оживает |
| Z-отчёт без ответа сервера считался без внесений и возвратов | Считается с внесениями и возвратами |
| Копия чека после возврата и смешанной оплаты | Берётся по журналу кассы |
| Имена PDF чеков могли совпасть; история «Эта смена» брала не тот период | Имена уникальны; период смены |
| Старый буфер сканера съедал Enter; подтверждения реагировали на скан | Enter не теряется; подтверждения защищены |
| Знаки / + * _ % $ и GS в Code128/DataMatrix читались неверно | Читаются верно |
| Надписи кнопок в тёмной теме плохо читались | Контраст не ниже 4,5:1 (на стенде; вживую не проверено) |
| Общие настройки владельца не доходили до касс | Идут через общий документ кассы (вживую не проверено) |
| Скидка «0,5», «0,50» сом применялась как 50 сом, «2,5 %» — как 25 % (предпросмотр показывал верно) | Предпросмотр и применяемое значение считает одна функция; «0,5», «12,5 %», «12.5» верно, «1.000», «1e3», «(10)» — ошибка |
| Программа владельца с «Полным экраном» после «Свернуть» и возврата с панели задач становилась обычным окном | Полный экран возвращается (так же касса и окно входа) |
| Вернуть можно было только всю строку («клей × 2» — только оба) | Поле «Вернуть, шт.» у строки: часть по количеству (штучный — шаг 1, весовой — 0,1); сумма — доля строки со скидкой на чек, не больше оплаченного |
| «Цена изменилась…» гасла за 4 с одновременно с открытием окна оплаты | Текст стоит в окне оплаты над кнопками, пока окно открыто |
| «1,5» у штучного товара молча не применялось и открывало два окна | Одно понятное сообщение в чеке, поле возвращается к прежнему количеству |
| В окне оплаты подсказка «Введите сумму, полученную наличными.» стояла дважды | Одна |
| Для диктора: ячейки таблиц, кнопки прокрутки, поля без имени, кнопка поставщика после выбора | Имена добавлены (проверить диктором на NBS) |
| Возможное ложное «оплата уже прошла» | Защита (жетон чека) |
| Настройки владельца и кассы затирали друг друга | Сливаются по ключам, с резервной копией |
| Перед закрытием смены чеки могли остаться неотправленными; старая смена не продолжалась | Чеки досылаются; старую смену можно продолжить, с подсказкой об изъятии/внесении |
| Весовая этикетка «по сумме» пересчитывалась; штука из пачки получала цену пачки | Не пересчитывается; цена пачки штуке не передаётся |

### Проверено перед выпуском

- Сборка Windows и Linux (Portable) без ошибок, проверка компиляции Android; публикация Windows (Release).
- Проверки на отдельных стендах по потокам работ (оплата, цены, смены, сканер, накладные, телефон, темы сайта, Советник).
- Живая приёмка на тестовом аккаунте NBS (27.10): подтверждены деньги кассы (наличные, безнал, смешанная, долг с предоплатой, Enter без суммы), сверка цен перед оплатой, цена владелец → касса за ≈6 с, полный экран кассы и владельца при запуске, приход без отката цены. Найденные ошибки (скидка «0,5»/«2,5 %», полный экран владельца после «Свернуть», частичный возврат, подсказки) исправлены после неё; исправленное проверено на стендах, повторная живая проверка — следующим проходом. Вечерняя живая приёмка 27.10 прошла частично (отчёт — в docs/Отчеты).

### Ещё не проверено вживую / известные ограничения

- Живой приёмкой на NBS **не проверены**: цикл смены (X-ОТЧЁТ, внесение/изъятие, Z-ОТЧЁТ, повторное открытие), тёмная тема, загрузка накладных, «Телефон как сканер», справочник в окне «Новый товар», общие настройки владельца → касса, обрыв связи при оплате. Эти пункты проверены только на стендах.
- Возврат чека со скидкой на чек: касса возвращает не больше оплаченного, но сервер NurCRM записывает документ возврата без скидки (на 4,00 вместо 3,60) — такой возврат помечается в «Некорректных чеках» (нужна доработка сервера).
- «Телефон как сканер» выключен по умолчанию; нужна мобилка с поддержкой связи.
- Лента изменений каталога и строгая проверка цены работают, только когда сервер их выложил; пока сервер не выложил, касса тихо работает по-старому.
- Тексты на английском, турецком и узбекском переведены без носителя языка.

---

# Кыргызча

**Тесттик версия — тестировщиктер үчүн гана.**

- **Электрондук накладныйлар.** Кампа → Кабыл алуу: накладныйларды Excel, CSV, XML, ТОРГ-12 жана CommerceML форматтарынан жүктөө.
- **NurCRM товарларынын орнотулган маалымдамасы** (128 276 товар): аталышы, ал эми маалымдамада бар болсо — бренди жана категориясы коюлат (бренд болжол менен 5 %, категория — 16 % жазууда бар; жок болсо «Жаңы товар» терезеси «кол менен толтуруңуз» дейт); жаңылары чыккан сайын серверден жаңырат.
- **«Телефон сканер катары».** Мобилка камерасы менен сканерлөө Wi-Fi аркылуу, QR жупташтыруу менен кассанын чегине түшөт. Демейки боюнча өчүк: Жөндөөлөр → Басуу.
- **Баалар.** Чектин баалары төлөөдөн мурун сервер менен салыштырылат («Баа өзгөрдү: 205 → 195»); сервер каталогдун өзгөрүү лентасын берсе, плиткадагы баа 8–10 с ичинде жаңырат; бааны сервердин катуу текшерүүсү.
- **Ээсинин программасы.** Жалпы жөндөөлөр кассанын жалпы документи аркылуу өтөт (дүкөндүн аты, кызматкер коддору, өчүрүү себеби кассаларга жетет); бардык кассалардын эсептен чыгаруу жана ревизия тарыхы; «Товар сакталды» терезесинин ордуна тилкече; диктор үчүн баскыч аттары; «аз» үчүн бирдиктүү чек.
- **Терезелер.** «Толук экран» кассага, ээсинин программасына, кирүү терезесине жана бөлүм терезелерине колдонулат; 1024×600 масштабы 80 %дан төмөн эмес; «1С» көрүнүшү: Enter, Num+, Num−.
- **Караңгы тема.** Баскыч жазуулары окулуучу (контраст 4,5:1ден кем эмес), баскычтардагы белгилер.
- **Укуктар:** «Ысык баскычтар» жана «Журналдар жана каталар»; сакталбаган өзгөртүүлөрү бар жөндөөлөрдү жабууда эскертүү.
- **Отчёттор.** X-ОТЧЕТ жана Z-ОТЧЕТ 5 тилде.
- **Кассанын үн менен башкаруусу алынды** — эми ИИ-жардамчы бар.
- **Кошумча.** Нур Кеңешчи бөлүмдөрдү нейротармаксыз ачат, жооп фактылардан түзүлөт; төлөм терезеси экрандын ортосунда («ылдыйга кысуу» жөндөөсү мурункуну кайтарат); накладныйлар көп барактуу, кайталап жүктөөдөн коргоо, окшош товарларды ырастоо; сайт темалары: «Сиздин дүкөн үчүн» чыпкасы, эскертүү, «Сүрөтсүз колдонуу»; Кампа → Сүрөт жүктөө: алдын ала көрүү; «жалган төлөм өттү» билдирүүсүнөн коргоо; ээсинин жана кассанын жөндөөлөрү ачкычтар боюнча бириктирилет; смена: жабуудан мурун чектер жөнөтүлөт, эски сменаны улантса болот; «сумма боюнча» этикетка кайра эсептелбейт, пачкадан даана пачканын баасын албайт.
- **Оңдоолор.** Төлөм: «Төлөө» баскычы жигердүү болбосо, Enter сатууну өткөрбөйт; сатып алуучунун экраны жана QR арзандатуудан жана бонустардан кийинки сумманы көрсөтөт; байланыш үзүлгөндөн кийин «карызга» жана «аралаш» төлөм менен кош сатуудан коргоо; карыздын алдын ала төлөмү накталайсыз накталай болуп кетпейт жана кайра иштетүүдөн кийин сакталат; төлөм учурундагы сканерлөөлөр кезекке турат (товар төлөмдөн кийин жаңы чекке, төлөм жокко чыкса — ошол чекке кошулат); «1.000», «1e3», «(100)» суммалары ката берет; офлайн чек кардарды жоготпойт; кампа кирешеден кийин бааны кайтарбайт; арзандатуу менен чекти кайтаруу төлөнгөндөн ашпайт; жабылган сменанын киргизүү жана алуу операциялары кайра жөнөтүлөт; жабуу чектер кезегин күтөт; сервер жооп бербесе, Z киргизүүлөр жана кайтаруулар менен эсептелет; Enter баскычын эски сканер буфери жебейт.

**Чектөөлөр.** «Телефон сканер катары» демейки боюнча өчүк. Тирүү кабыл алуу NBS сынак аккаунтунда аткарылууда.

---

# English

**Test version — for testers only.**

- **Electronic invoices.** Stock → Receiving: loading invoices from Excel, CSV, XML, TORG-12 and CommerceML.
- **Built-in NurCRM product directory** (128,276 products): the name is filled in, and the brand and category where the directory has them (a brand is present in about 5% of entries and a category in 16%; if missing, the “New product” window says “fill in by hand”); updated from the server as new ones appear.
- **“Phone as a scanner”.** A scan with the mobile app camera goes into the till receipt over Wi-Fi, by QR pairing. Off by default: Settings → Printing.
- **Prices.** Receipt prices are checked against the server before payment (“Price changed: 205 → 195”); the catalog change feed updates a tile price within 8–10 s if the server provides it; strict server-side price check.
- **Owner’s program.** Common settings go through the shared till document (store name, employee codes, deletion reason reach the tills); write-off and stocktake history of all tills; a banner instead of the “Product saved” window; button names for the screen reader; one common “low” threshold.
- **Windows.** “Full screen” applies to the till, the owner’s program, the login window and section windows; 1024×600 scale not below 80%; the “1C” view: Enter, Num+, Num−.
- **Dark theme.** Readable button labels (contrast at least 4.5:1), icons in buttons.
- **Permissions** for “Hotkeys” and “Logs and errors”; a warning when closing settings with unsaved changes.
- **Reports.** X-REPORT and Z-REPORT in 5 languages.
- **Voice control of the till removed** — there is now an AI assistant.
- **Also.** Nur Advisor opens sections without the neural network and builds answers from facts; the payment window is centered (the “dock to the bottom” setting brings back the old look); invoices: multi-page, protection against loading twice, confirmation of similar products; site themes: the “For your store” filter, a warning, “Apply without pictures”; Stock → Upload photo: preview window; protection against a false “payment already went through”; owner and till settings merged by keys with a backup; shifts: receipts re-sent before closing, an old shift can be continued; a weighed label “by amount” is not recalculated, a single piece from a pack does not get the pack price; “Phone as a scanner”: signed replies, protection against repeats, phones can be disabled.
- **Fixes.** Payment: Enter does not complete the sale while “Pay” is inactive; the customer screen and QR show the amount after discount and bonuses; protection against a double sale “on credit” and with mixed payment after a connection drop; a cashless debt prepayment no longer goes out as cash and the top-up survives a restart; scans during payment go into a queue (the item is added after payment to the new receipt, or to the same one if payment is canceled); amounts “1.000”, “1e3”, “(100)” give an error; an offline receipt does not lose the customer; stock after receiving no longer rolls the price back; a return with a receipt discount is not more than what was paid; cash-in and cash-out of a closed shift are re-sent; closing waits for the receipt queue; Z without a server reply is counted with cash-in and returns; an old scanner buffer does not swallow Enter.

**Limitations.** “Phone as a scanner” is off by default. The live acceptance on the NBS test account is in progress.

---

# Türkçe

**Test sürümü — yalnızca test kullanıcıları için.**

- **Elektronik irsaliyeler.** Depo → Mal kabul: irsaliyelerin Excel, CSV, XML, TORG-12 ve CommerceML'den yüklenmesi.
- **Yerleşik NurCRM ürün rehberi** (128 276 ürün): ad, rehberde varsa marka ve kategori otomatik doldurulur (marka kayıtların yaklaşık %5'inde, kategori %16'sında var; yoksa «Yeni ürün» penceresi «elle doldurun» der); yenileri çıktıkça sunucudan güncellenir.
- **«Telefon tarayıcı olarak».** Mobil uygulama kamerasıyla tarama, Wi-Fi üzerinden QR eşleştirmesiyle kasa fişine düşer. Varsayılan olarak kapalı: Ayarlar → Yazdırma.
- **Fiyatlar.** Fiş fiyatları ödemeden önce sunucuyla karşılaştırılır («Fiyat değişti: 205 → 195»); sunucu katalog değişiklik akışını verirse kutucuktaki fiyat 8–10 sn içinde güncellenir; sunucuda sıkı fiyat denetimi.
- **Sahip programı.** Ortak ayarlar kasanın ortak belgesinden geçer (mağaza adı, çalışan kodları, silme nedeni kasalara ulaşır); tüm kasaların silme ve sayım geçmişi; «Ürün kaydedildi» penceresi yerine şerit; ekran okuyucu için düğme adları; ortak «az» eşiği.
- **Pencereler.** «Tam ekran» kasaya, sahip programına, giriş penceresine ve bölüm pencerelerine uygulanır; 1024×600 ölçeği %80'in altına inmez; «1C» görünümü: Enter, Num+, Num−.
- **Koyu tema.** Okunaklı düğme yazıları (kontrast en az 4,5:1), düğmelerdeki simgeler.
- **Yetkiler:** «Kısayol tuşları» ve «Günlükler ve hatalar»; kaydedilmemiş değişikliklerle ayarları kapatırken uyarı.
- **Raporlar.** X-RAPORU ve Z-RAPORU 5 dilde.
- **Kasanın sesli kontrolü kaldırıldı** — artık bir yapay zekâ asistanı var.
- **Ayrıca.** Nur Danışman bölümleri yapay zekâ olmadan açar, yanıtı gerçeklerden kurar; ödeme penceresi ekranın ortasında («alta yasla» ayarı eski görünümü getirir); irsaliyeler: çok sayfalı, tekrar yüklemeye karşı koruma, benzer ürün onayı; site temaları: «Mağazanız için» filtresi, uyarı, «Resimsiz uygula»; Depo → Fotoğraf yükle: önizleme; yanlış «ödeme zaten geçti» iletisine karşı koruma; sahip ve kasa ayarları anahtarlara göre birleştirilir, yedekle; vardiya: kapatmadan önce fişler yeniden gönderilir, eski vardiya devam ettirilebilir; «tutara göre» etiket yeniden hesaplanmaz, paketten tek parça paket fiyatını almaz; «Telefon tarayıcı olarak»: imzalı yanıtlar, tekrara karşı koruma, telefonları kapatma.
- **Düzeltmeler.** Ödeme: «Öde» etkin değilken Enter satışı tamamlamaz; müşteri ekranı ve QR indirim ve bonuslardan sonraki tutarı gösterir; bağlantı kopmasından sonra «veresiye» ve karma ödemeyle çifte satışa karşı koruma; nakitsiz borç ön ödemesi nakit gibi gitmez ve yeniden başlatmadan sonra korunur; ödeme sırasındaki taramalar sıraya girer (ürün ödemeden sonra yeni fişe, ödeme iptal edilirse aynı fişe eklenir); «1.000», «1e3», «(100)» tutarları hata verir; çevrimdışı fiş müşteriyi kaybetmez; depo mal kabulden sonra fiyatı geri almaz; indirimli fiş iadesi ödenenden fazla olmaz; kapalı vardiyanın para girişi ve çıkışı yeniden gönderilir; kapatma fiş kuyruğunu bekler; sunucu yanıt vermezse Z, giriş ve iadelerle hesaplanır; eski tarayıcı arabelleği Enter'ı yemez.

**Sınırlamalar.** «Telefon tarayıcı olarak» varsayılan olarak kapalı. NBS test hesabında canlı kabul sürüyor.

---

# O'zbekcha

**Test versiyasi — faqat testerlar uchun.**

- **Elektron nakladnoylar.** Ombor → Qabul: nakladnoylarni Excel, CSV, XML, TORG-12 va CommerceML'dan yuklash.
- **NurCRM tovarlarining o'rnatilgan ma'lumotnomasi** (128 276 tovar): nomi, ma'lumotnomada bo'lsa — brendi va toifasi qo'yiladi (brend yozuvlarning taxminan 5 %ida, toifa 16 %ida bor; yo'q bo'lsa «Yangi tovar» oynasi «qo'lda to'ldiring» deydi); yangilari paydo bo'lishi bilan serverdan yangilanadi.
- **«Telefon skaner sifatida».** Mobil ilova kamerasi bilan skan Wi-Fi orqali, QR juftlash bilan kassa chekiga tushadi. Odatda o'chiq: Sozlamalar → Chop etish.
- **Narxlar.** Chek narxlari to'lovdan oldin server bilan solishtiriladi («Narx o'zgardi: 205 → 195»); server katalog o'zgarishlari lentasini bersa, plitkadagi narx 8–10 s ichida yangilanadi; narxni serverda qat'iy tekshirish.
- **Egasi dasturi.** Umumiy sozlamalar kassaning umumiy hujjati orqali o'tadi (do'kon nomi, xodim kodlari, o'chirish sababi kassalarga yetadi); barcha kassalarning hisobdan chiqarish va taftish tarixi; «Tovar saqlandi» oynasi o'rniga lavha; diktor uchun tugma nomlari; yagona «kam» chegarasi.
- **Oynalar.** «To'liq ekran» kassaga, egasi dasturiga, kirish oynasiga va bo'lim oynalariga qo'llanadi; 1024×600 masshtabi 80 %dan past emas; «1C» ko'rinishi: Enter, Num+, Num−.
- **Qorong'i mavzu.** Tugma yozuvlari o'qiladigan (kontrast kamida 4,5:1), tugmalardagi belgilar.
- **Huquqlar:** «Tezkor tugmalar» va «Jurnallar va xatolar»; saqlanmagan o'zgarishlar bilan sozlamalarni yopishda ogohlantirish.
- **Hisobotlar.** X-HISOBOT va Z-HISOBOT 5 tilda.
- **Kassaning ovozli boshqaruvi olib tashlandi** — endi SI yordamchisi bor.
- **Qo'shimcha.** Nur Maslahatchi bo'limlarni neyrotarmoqsiz ochadi, javobni faktlardan tuzadi; to'lov oynasi ekran o'rtasida («pastga siqish» sozlamasi avvalgisini qaytaradi); nakladnoylar: ko'p sahifali, qayta yuklashdan himoya, o'xshash tovarlarni tasdiqlash; sayt mavzulari: «Sizning do'koningiz uchun» filtri, ogohlantirish, «Rasmsiz qo'llash»; Ombor → Rasm yuklash: oldindan ko'rish; «to'lov allaqachon o'tdi» yolg'on xabaridan himoya; egasi va kassa sozlamalari kalitlar bo'yicha birlashtiriladi, zaxira bilan; smena: yopishdan oldin cheklar qayta yuboriladi, eski smenani davom ettirish mumkin; «summa bo'yicha» yorliq qayta hisoblanmaydi, qadoqdan dona qadoq narxini olmaydi; «Telefon skaner sifatida»: javoblarga imzo, takrorlardan himoya, telefonlarni o'chirish.
- **Tuzatishlar.** To'lov: «To'lash» faol bo'lmasa, Enter sotuvni o'tkazmaydi; xaridor ekrani va QR chegirma va bonuslardan keyingi summani ko'rsatadi; aloqa uzilgandan keyin «qarzga» va aralash to'lov bilan ikki marta sotishdan himoya; qarzning naqdsiz oldindan to'lovi naqd bo'lib ketmaydi va qayta ishga tushirishdan keyin saqlanadi; to'lov paytidagi skanlar navbatga turadi (mahsulot to'lovdan keyin yangi chekka, to'lov bekor qilinsa shu chekka qo'shiladi); «1.000», «1e3», «(100)» summalari xato beradi; oflayn chek mijozni yo'qotmaydi; ombor kirimdan keyin narxni qaytarmaydi; chegirmali chekni qaytarish to'langandan oshmaydi; yopilgan smenaning kiritish va olish amallari qayta yuboriladi; yopish cheklar navbatini kutadi; server javob bermasa, Z kiritish va qaytarishlar bilan hisoblanadi; eski skaner buferi Enter'ni yemaydi.

**Cheklovlar.** «Telefon skaner sifatida» odatda o'chiq. NBS sinov akkauntida jonli qabul davom etmoqda.
