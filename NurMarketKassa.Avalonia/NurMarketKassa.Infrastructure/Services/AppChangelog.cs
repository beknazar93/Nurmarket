namespace NurMarketKassa.Services;

/// <summary>Краткое описание изменений ТЕКУЩЕЙ версии — показывается кассиру один раз
/// в окне "Что нового" после того, как касса обновилась (см. UserPreferences.LastSeenAppVersion),
/// и используется как текст релиза при публикации на GitHub.
/// ВАЖНО: перед каждым релизом содержимое <see cref="Latest"/> (и переводов) полностью ЗАМЕНЯЕТСЯ
/// на изменения именно этой версии — не дописывается поверх старых пунктов. История прошлых версий
/// не нужна здесь: она уже есть в самих релизах на GitHub, а этот список — только "что нового прямо
/// сейчас". 2026-09-07: список показывается на языке интерфейса (ru/ky/en/tr/uz, см.
/// <see cref="LatestForCurrentLanguage"/>) — раньше при кыргызском интерфейсе заголовок был
/// кыргызским, а сами пункты русскими.</summary>
public static class AppChangelog
{
    public static readonly string[] Latest =
    [
        "Исправлено: продажи уходили в чужую кассу. Если у кассы не было своей открытой смены, она молча брала первую открытую смену компании — например, смену «Касса 2», открытую на сайте. Продажи попадали туда и не появлялись ни в «Продажах», ни в итогах этой кассы, а когда сервер отказывал, оплата не проходила и товар оставался в чеке. Теперь касса работает только в своей смене. Если смена этого же кассира открыта на другой кассе, касса переходит на ту кассу целиком — название в шапке, продажи и итоги смены совпадают",
        "Откат на прошлую версию снова работает, и в списке версий больше нет повторов. Раньше касса брала версию не из её собственного релиза и пыталась скачать пакет оттуда, где его нет",
        "Приёмка на складе переделана по образцу «Массового сканирования» сайта. Скан сначала ищется на своём складе, затем в общей базе CRM (товар узнаётся по штрихкоду, даже если у магазина его ещё нет), и только потом считается новым — тогда достаточно вписать название. У каждой строки обязательны цена закупки и цена продажи, их можно поправить прямо в таблице. С поставщиком приход проводится документом «Закупки», как на сайте: оплачено сразу или в долг поставщику. Без поставщика остаток и цены пишутся прямо в товар. Недостающие товары создаются сами",
        "В карточке товара на складе появилась «История закупок»: когда, сколько и почём привозили товар, поставщик и кто принял. Туда попадают приёмки кассы и закупки, сделанные на сайте",
        "На складе появился раздел «Перемещение» — документы о перевозке товара между точками: номер, откуда и куда, перевозчик, трек-номер, вес партии и ответственный. Статус меняется кнопками: создано → в пути → принято или отменено, каждое изменение записывается с датой и сотрудником. К документу прикладываются накладные и фотографии. Журнал ищет по номеру и по товарам внутри, фильтруется по статусу и выгружается в Excel, Word и CSV. Вес партии вписывается руками",
        "В приёмке и перемещении работает живой поиск: с двух букв под полем появляются подходящие товары, а кнопка «Выбрать из списка» открывает каталог. Сканер работает как раньше",
        "Горячие клавиши F1–F12 открывают окно с товарами своей клавиши: выбрали товар — он ушёл в чек, каталог остался как был",
        "Услуги получили отдельную вкладку «Услуги» и метку на плитке. Услуга продаётся без остатка: касса больше не требует количество на складе, и «+» в строке чека не упирается в ноль",
        "Единицы измерения берутся с сервера: товар в метрах, литрах или упаковках больше не превращается в «шт» ни в каталоге, ни в чеке",
        "В Настройках → Экран появился ползунок «Размер карточек товара»: крупнее — удобнее на сенсорном экране, мельче — больше товаров на экране. Шапку и чек он не меняет",
        "Скан на складе больше не путает вкладки: в «Приёмке» товар встаёт в приёмку, а не в ревизию, в «Ревизии» — в ревизию, а не в списание",
        "Исправлено: скидка в процентах на строку чека не доходила до сервера — касса показывала сумму со скидкой, сервер считал без неё, и оплата останавливалась с сообщением «Сумма чека изменилась при переносе на сервер». Теперь процент пересчитывается в сумму и уходит вместе со строкой",
        "Аналитика сверяется с сервером: продажи, удалённые или отменённые на сайте, больше не остаются в ABC, «Финансах» и прогнозах. Отменённые продажи в историю больше не попадают вовсе",
        "«Финансы → Смены»: нажатие на смену открывает её отчёт — начальная сумма, продажи по способам оплаты, долги, возвраты, расходы, печать Z-отчёта и список проданных за смену товаров. У закрытых смен убрана кнопка «Открыть смену», которая читалась как «открыть закрытую»",
        "Выбор клиента при оплате — обычный список: инициалы, имя, телефон, поиск сверху; «Новый клиент» раскрывается отдельно",
        "«История списаний» стала отчётом: себестоимость и сумма по каждой строке, внизу — итог, сколько и на сколько списано, и разбивка по причинам. Себестоимость запоминается на момент списания",
        "В «Настройки → Аккаунт» добавлена карточка «Вход в кассу»: логин, кассир, роль, касса и смена. Пароль касса не хранит — иначе его видел бы любой за кассой; сменить его можно на сайте NurCRM. Имя кассира теперь берётся из имени и фамилии, как на сайте, а не пустое",
        "Новая карточка товара на складе сама подставляет название из общей базы товаров NurCRM по штрихкоду и сразу предупреждает, если такой штрихкод уже есть на складе",
        "Обновления выходят сначала как тестовые: касса клиента на «Проверить обновления» отвечает, что новая версия в тестировании и обновиться можно будет после его завершения. Тестировщики включают тестовый канал кодом в «Настройки → Обновления»",
        "Окно подтверждения отката больше не уходит за край экрана: описание версии прокручивается, вопрос и кнопки всегда видны",
        "В отчёте закрытой смены нажимается каждая плитка: «Продажи», «Наличные», «Безналичные», «Долг», «Возвраты», «Списания», «Расход», «Оплата долгов», «Скидки». Открывается подробный отчёт: чеки с товарами, время, способ оплаты и итог",
        "Работает Esc: закрывает окна и диалоги так же, как их кнопка «Закрыть» или «Отмена». В «Финансах», «Продажах» и «Клиентах» он сначала закрывает открытый чек или карточку, на главном экране закрывает меню и возвращает из поиска к сканеру. Если на складе остались непроведённые строки, касса сначала переспросит",
        "Касса больше не упирается в ограничение сервера на частоту запросов. «Финансы» и «Продажи» загружают чеки спокойнее и не скачивают уже загруженные повторно. Если сервер просит подождать, касса ждёт и повторяет запрос сама: оплата не падает с ошибкой «Запрос был проигнорирован»",
        "Остаток кассы в шапке и в меню обновляется после каждой оплаты, а не только при запуске",
        "Исправлено: в меню и в «Аккаунте» пропадало имя кассира (показывалось «Кассир —»)",
        "Пока загружаются «Финансы», «Продажи», «Клиенты», ABC-анализ и отчёт смены, видна полоска загрузки",
        "Чеки возврата и изъятия, а также повторная печать чека печатаются в фоне: медленный принтер больше не подвешивает кассу",
        "Модуль голосового замка снова скачивается. В окне ошибки длинная надпись на кнопке больше не обрезается",
        "Аналитика сверена с сервером до копейки. «Безнал» больше не включает продажи в долг. Полностью возвращённый чек не считается ни в выручке, ни в наличных, ни в числе чеков. «Скидки» в «Продажах» учитывают и скидки на строку. Время чеков в «Финансах» и «Истории» больше не показывается как 00:00",
        "ABC-анализ: товар, который сам пересекает границу 80 %, остаётся в группе A. Раньше товар с четвертью выручки мог попасть в группу C. Цвета способов оплаты в полосе и в круговой диаграмме теперь совпадают",
        "Остаток кассы обновляется и после возврата. Окно «Просмотр смены» стало шире: крупные суммы не обрезаются",
    ];

    public static readonly string[] LatestKy =
    [
        "Оңдолду: сатуулар башка кассага кетип жаткан. Кассанын өз ачык сменасы жок болсо, ал компаниянын биринчи ачык сменасын үнсүз алчу — мисалы, сайтта ачылган «Касса 2» сменасын. Сатуулар ошол жакка түшүп, бул кассанын «Сатуулар» бөлүмүндө да, жыйынтыгында да көрүнчү эмес, ал эми сервер баш тартканда төлөм өтпөй, товар чекте калчу. Эми касса өз сменасында гана иштейт. Ошол эле кассирдин сменасы башка кассада ачык болсо, касса толугу менен ошол кассага өтөт",
        "Мурунку версияга кайтуу кайра иштейт, версиялар тизмесинде кайталоолор жок",
        "Кампадагы кабыл алуу сайттагы «Массалык сканерлөө» үлгүсү менен кайра жасалды. Скан адегенде өз кампадан, андан кийин CRM жалпы базасынан изделет, анан гана жаңы деп эсептелет — атын жазуу жетиштүү. Ар бир сапта сатып алуу жана сатуу баасы милдеттүү. Жеткирүүчү менен кириш сайттагыдай «Сатып алуулар» документи менен өтөт, жеткирүүчүсүз калдык жана баалар товарга жазылат. Жетишпеген товарлар өзү түзүлөт",
        "Кампадагы товар карточкасында «Сатып алуулар тарыхы» пайда болду: качан, канча жана канчадан алынганы, жеткирүүчү жана ким кабыл алганы",
        "Кампада «Жылышуу» бөлүмү пайда болду: номери, кайдан-кайда, ташуучу, трек-номер, салмагы жана жооптуу адам. Статус баскычтар менен өзгөрөт, ар бир өзгөрүү жазылат. Журнал Excel, Word жана CSV га чыгарылат. Партиянын салмагы кол менен жазылат",
        "Кабыл алууда жана жылышууда жандуу издөө жана «Тизмеден тандоо» иштейт",
        "F1–F12 ыкчам баскычтары өз товарлары менен терезе ачат",
        "Кызматтардын өзүнчө «Кызматтар» өтмөгү жана белгиси бар. Кызмат калдыксыз сатылат — касса кампадагы санды талап кылбайт",
        "Өлчөө бирдиктери серверден алынат: метр, литр же таңгактагы товар «шт» болуп калбайт",
        "Жөндөөлөр → Экранда «Товар карточкаларынын өлчөмү» жылдыргычы пайда болду",
        "Кампадагы скан өтмөктөрдү чаташтырбайт: «Кабыл алууда» товар кабыл алууга түшөт",
        "Оңдолду: чек сабындагы пайыздык арзандатуу серверге жетчү эмес, төлөм сумманы салыштырууда токтоп калчу",
        "Аналитика сервер менен салыштырылат: сайтта өчүрүлгөн же жокко чыгарылган сатуулар ABC жана отчётторго кирбейт",
        "«Финансы → Сменалар»: сменаны бассаңыз анын отчёту ачылат (төлөмдөр, карыздар, Z-отчёт, сатылган товарлар). Жабылган сменалардан «Сменаны ачуу» баскычы алынды",
        "Төлөмдө кардарды тандоо — кадимки тизме: аты, телефону, издөө",
        "«Эсептен чыгаруулар тарыхы» отчёт болду: өздүк нарк, сумма жана жыйынтык",
        "«Жөндөөлөр → Аккаунт»: кассага ким кирди — логин, кассир, ролу, касса, смена. Сырсөз сакталбайт",
        "Жаңы товар карточкасы штрихкод боюнча NurCRM жалпы базасынан атын өзү коёт",
        "Жаңыртуулар адегенде тесттик болуп чыгат: кардарга версия текшерүүдө экени айтылат, тестерлер код менен алышат",
        "Кайтууну ырастоо терезеси экрандан чыкпайт",
        "Жабылган сменанын отчётунда ар бир плитка басылат: «Сатуулар», «Накталай», «Накталай эмес», «Карыз», «Кайтаруулар», «Эсептен чыгаруулар», «Чыгаша», «Карыз төлөө», «Арзандатуулар». Толук отчёт ачылат: товарлары менен чектер, убакыт, төлөм ыкмасы жана жыйынтык",
        "Esc иштейт: терезелерди жана диалогдорду алардын «Жабуу» же «Жокко чыгаруу» баскычы сыяктуу жабат. «Финансы», «Сатуулар» жана «Кардарлар» бөлүмүндө адегенде ачык чекти же карточканы жабат, башкы экранда менюну жабат жана издөөдөн сканерге кайтарат. Кампада өткөрүлө элек саптар калса, касса адегенде сурайт",
        "Касса сервердин суроо-талаптардын жыштыгына болгон чегине такалбайт. «Финансы» жана «Сатуулар» чектерди жайыраак жүктөп, жүктөлгөндөрүн кайра жүктөбөйт. Сервер күтүүнү сураса, касса күтүп, суроону өзү кайталайт: төлөм «Суроо четке кагылды» катасы менен токтобойт",
        "Шапкадагы жана менюдагы кассанын калдыгы ар бир төлөмдөн кийин жаңыланат, ишке киргенде гана эмес",
        "Оңдолду: менюда жана «Аккаунтта» кассирдин аты жоголуп калчу («Кассир —»)",
        "«Финансы», «Сатуулар», «Кардарлар», ABC-анализ жана смена отчёту жүктөлүп жатканда жүктөө тилкеси көрүнөт",
        "Кайтаруу жана алып чыгуу чектери, ошондой эле чекти кайра басып чыгаруу фондо жүрөт: жай принтер кассаны токтотпойт",
        "Үн кулпусунун модулу кайра жүктөлөт. Ката терезесиндеги баскычтын узун жазуусу кесилбейт",
        "Аналитика сервер менен тыйынына чейин салыштырылды. «Накталай эмес» карызга сатууларды камтыбайт. Толук кайтарылган чек түшүмгө да, накталайга да, чектердин санына да кирбейт. «Сатуулардагы» «Арзандатуулар» сап боюнча арзандатууну да эсептейт. «Финансы» жана «Тарых» бөлүмүндө чектердин убактысы 00:00 болуп көрүнбөйт",
        "ABC-анализ: 80 % чегин өзү кесип өткөн товар A тобунда калат. Мурун түшүмдүн төрттөн бирин берген товар C тобуна түшүп калчу. Тилкедеги жана тегерек диаграммадагы төлөм ыкмаларынын түстөрү дал келет",
        "Кассанын калдыгы кайтаруудан кийин да жаңыланат. «Сменаны көрүү» терезеси кеңейди: чоң суммалар кесилбейт",
    ];

    public static readonly string[] LatestEn =
    [
        "Fixed: sales were going to another register. When the till had no open shift of its own, it silently took the company's first open shift - for example the \"Register 2\" shift opened on the website. Sales landed there and did not show in this till's Sales or shift totals, and when the server refused, payment failed and the goods stayed in the receipt. The till now works only in its own shift. If the same cashier has a shift open on another register, the till switches to that register entirely, so the header, sales and shift totals match",
        "Rolling back to a previous version works again, and the version list no longer shows duplicates",
        "Warehouse receiving has been rebuilt after the website's Mass scan. A scan is looked up in your own stock, then in the shared CRM base (a product is recognised by its barcode even if the shop does not have it yet), and only then treated as new - just type its name. Every line needs a purchase price and a sale price, editable right in the table. With a supplier the receipt is posted as a website Purchase document, paid now or owed to the supplier; without one, stock and prices are written straight into the product. Missing products are created automatically",
        "The product card in the warehouse now has a Purchase history: when, how many and at what price the product was bought, the supplier and who received it",
        "The warehouse got a Transfers section: number, from and to, carrier, tracking number, batch weight and person responsible. The status changes by buttons and every change is recorded. The journal exports to Excel, Word and CSV. Batch weight is typed by hand",
        "Live search and Pick from list work in Receiving and Transfers",
        "The F1-F12 hotkeys open a window with that key's products",
        "Services have their own Services tab and tile badge. A service sells without stock - the till no longer asks for a warehouse quantity",
        "Units come from the server: a product sold in metres, litres or packs no longer turns into \"pcs\"",
        "Settings -> Screen has a new Product tile size slider",
        "Scans in the warehouse no longer mix up tabs: in Receiving the product goes into the receiving list",
        "Fixed: a percentage discount on a receipt line did not reach the server, and payment stopped at the amount check",
        "Analytics is reconciled with the server: sales deleted or cancelled on the website no longer stay in ABC and reports",
        "Finance -> Shifts: tapping a shift opens its report (payments, debts, Z-report, products sold). Closed shifts no longer show Open shift",
        "Choosing a client at payment is now a plain list with initials, name, phone and search",
        "Write-off history became a report: unit cost, amount per line and a total at the bottom",
        "Settings -> Account shows who is signed in: login, cashier, role, register and shift. The password is not stored",
        "A new product card fills in the name from the NurCRM shared product base by barcode",
        "Updates are released as test versions first: customers are told the version is under testing, testers get it with a code",
        "The rollback confirmation no longer runs off the screen",
        "Every tile in a closed shift report can now be clicked: Sales, Cash, Non-cash, Debt, Returns, Write-offs, Expenses, Debt payments, Discounts. It opens a detailed report with receipts and their items, time, payment method and the total",
        "Esc now works: it closes windows and dialogs the same way their Close or Cancel button does. In Finance, Sales and Clients it first closes the open receipt or card; on the main screen it closes the menu and returns from search to the scanner. If the warehouse has unposted lines, the POS asks first",
        "The POS no longer runs into the server's request rate limit. Finance and Sales load receipts more gently and do not download already loaded ones again. When the server asks to wait, the POS waits and retries on its own, so payment no longer fails with \"Request was throttled\"",
        "The cash balance in the header and menu updates after every payment, not only at startup",
        "Fixed: the cashier name disappeared from the menu and the Account page (it showed \"Cashier —\")",
        "A loading bar is shown while Finance, Sales, Clients, ABC analysis and the shift report are loading",
        "Return and withdrawal receipts and receipt reprints are printed in the background, so a slow printer no longer freezes the POS",
        "The voice lock module downloads again. A long button label in the error window is no longer cut off",
        "Analytics reconciled with the server to the cent. Non-cash no longer includes debt sales. A fully returned receipt is not counted in revenue, cash or the receipt count. Discounts in Sales now include line discounts. Receipt times in Finance and History no longer show as 00:00",
        "ABC analysis: an item that itself crosses the 80% line stays in group A. Previously an item with a quarter of revenue could land in group C. Payment method colors in the bar and the pie chart now match",
        "The cash balance also updates after a return. The shift view window is wider, so large sums are no longer cut off",
    ];

    public static readonly string[] LatestTr =
    [
        "Düzeltildi: satışlar başka kasaya gidiyordu. Kasanın kendi açık vardiyası yoksa şirketin ilk açık vardiyasını sessizce alıyordu - örneğin web sitesinde açılan \"Kasa 2\" vardiyasını. Satışlar oraya düşüyor, bu kasanın Satışlar bölümünde ve vardiya toplamında görünmüyordu. Artık kasa yalnızca kendi vardiyasında çalışır. Aynı kasiyerin vardiyası başka kasada açıksa kasa tamamen o kasaya geçer",
        "Önceki sürüme dönüş yeniden çalışıyor, sürüm listesinde tekrar yok",
        "Depo mal kabulü web sitesindeki Toplu tarama örneğine göre yeniden yapıldı: önce kendi depoda, sonra CRM ortak tabanında aranır, ancak sonra yeni sayılır. Her satırda alış ve satış fiyatı zorunludur. Tedarikçiyle kabul web sitesindeki Alım belgesi olarak geçer, tedarikçisiz stok ve fiyatlar doğrudan ürüne yazılır. Eksik ürünler kendiliğinden oluşturulur",
        "Depodaki ürün kartında Alım geçmişi var: ne zaman, ne kadar, kaça alındı, tedarikçi ve teslim alan",
        "Depoya Transfer bölümü eklendi: numara, nereden-nereye, taşıyıcı, takip numarası, ağırlık ve sorumlu. Durum düğmelerle değişir, günlük Excel, Word ve CSV olarak aktarılır. Parti ağırlığı elle yazılır",
        "Mal kabulde ve transferde canlı arama ve Listeden seç çalışıyor",
        "F1-F12 kısayolları kendi ürünlerinin penceresini açar",
        "Hizmetlerin ayrı Hizmetler sekmesi ve etiketi var. Hizmet stoksuz satılır",
        "Birimler sunucudan alınır: metre, litre veya paket ürün \"adet\" olmaz",
        "Ayarlar -> Ekran'da Ürün kartı boyutu kaydırıcısı var",
        "Depodaki tarama sekmeleri karıştırmıyor",
        "Düzeltildi: fiş satırındaki yüzde indirimi sunucuya ulaşmıyordu ve ödeme tutar kontrolünde duruyordu",
        "Analiz sunucuyla eşleştirilir: sitede silinen veya iptal edilen satışlar raporlarda kalmaz",
        "Finans -> Vardiyalar: vardiyaya dokununca raporu açılır. Kapalı vardiyalarda Vardiya aç düğmesi kaldırıldı",
        "Ödemede müşteri seçimi artık düz bir liste",
        "Zayiat geçmişi rapor oldu: birim maliyet, tutar ve toplam",
        "Ayarlar -> Hesap: giriş yapan kullanıcı, rol, kasa ve vardiya. Şifre saklanmaz",
        "Yeni ürün kartı barkoda göre NurCRM ortak tabanından adı doldurur",
        "Güncellemeler önce test sürümü olarak çıkar: müşteriye sürümün testte olduğu söylenir, test edenler kodla alır",
        "Geri dönüş onay penceresi artık ekrandan taşmıyor",
        "Kapanan vardiya raporundaki her kutucuğa tıklanabilir: Satışlar, Nakit, Nakit dışı, Borç, İadeler, Zayiatlar, Gider, Borç ödemeleri, İndirimler. Fişler ve ürünleri, saat, ödeme yöntemi ve toplamla ayrıntılı rapor açılır",
        "Esc çalışıyor: pencereleri ve diyalogları Kapat veya İptal düğmesi gibi kapatır. Finans, Satışlar ve Müşteriler'de önce açık fişi veya kartı kapatır; ana ekranda menüyü kapatır ve aramadan tarayıcıya döndürür. Depoda kaydedilmemiş satırlar varsa kasa önce sorar",
        "Kasa artık sunucunun istek sıklığı sınırına takılmıyor. Finans ve Satışlar fişleri daha sakin yükler ve yüklenmiş olanları tekrar indirmez. Sunucu beklemeyi isterse kasa bekler ve isteği kendisi tekrarlar: ödeme \"İstek yok sayıldı\" hatasıyla durmaz",
        "Başlıktaki ve menüdeki kasa bakiyesi yalnızca açılışta değil, her ödemeden sonra güncellenir",
        "Düzeltildi: kasiyer adı menüden ve Hesap sayfasından kayboluyordu (\"Kasiyer —\" görünüyordu)",
        "Finans, Satışlar, Müşteriler, ABC analizi ve vardiya raporu yüklenirken yükleme çubuğu görünür",
        "İade ve çekim fişleri ile fişin yeniden yazdırılması arka planda yapılır: yavaş yazıcı artık kasayı dondurmaz",
        "Ses kilidi modülü yeniden indiriliyor. Hata penceresindeki uzun düğme yazısı artık kesilmiyor",
        "Analitik sunucuyla kuruşu kuruşuna karşılaştırıldı. Nakit dışı artık borçlu satışları içermiyor. Tamamen iade edilen fiş ciroya, nakde ve fiş sayısına girmiyor. Satışlar'daki İndirimler satır indirimlerini de kapsıyor. Finans ve Geçmiş'te fiş saatleri artık 00:00 görünmüyor",
        "ABC analizi: %80 sınırını kendisi geçen ürün A grubunda kalır. Önceden cironun dörtte birini getiren bir ürün C grubuna düşebiliyordu. Çubuk ve pasta grafikteki ödeme yöntemi renkleri artık aynı",
        "Kasa bakiyesi iadeden sonra da güncellenir. Vardiya görüntüleme penceresi genişledi, büyük tutarlar kesilmiyor",
    ];

    public static readonly string[] LatestUz =
    [
        "Tuzatildi: sotuvlar boshqa kassaga ketayotgan edi. Kassaning o'z ochiq smenasi bo'lmasa, u kompaniyaning birinchi ochiq smenasini jimgina olardi - masalan, saytda ochilgan \"Kassa 2\" smenasini. Sotuvlar o'sha yerga tushib, bu kassaning Sotuvlar bo'limida ham, smena yakunida ham ko'rinmasdi. Endi kassa faqat o'z smenasida ishlaydi. Xuddi shu kassirning smenasi boshqa kassada ochiq bo'lsa, kassa to'liq o'sha kassaga o'tadi",
        "Oldingi versiyaga qaytish yana ishlaydi, versiyalar ro'yxatida takrorlar yo'q",
        "Ombordagi qabul qilish saytdagi Ommaviy skanerlash namunasida qayta qilindi: avval o'z omborda, keyin CRM umumiy bazasida qidiriladi, shundan keyingina yangi hisoblanadi. Har bir qatorda xarid va sotuv narxi majburiy. Yetkazib beruvchi bilan kirim saytdagi Xaridlar hujjati sifatida o'tadi, yetkazib beruvchisiz qoldiq va narxlar mahsulotga yoziladi. Yetishmagan mahsulotlar o'zi yaratiladi",
        "Ombordagi mahsulot kartasida Xaridlar tarixi bor: qachon, qancha va necha puldan olingani, yetkazib beruvchi va kim qabul qilgani",
        "Omborga Ko'chirish bo'limi qo'shildi: raqam, qayerdan-qayerga, tashuvchi, kuzatuv raqami, og'irlik va mas'ul. Holat tugmalar bilan o'zgaradi, jurnal Excel, Word va CSV ga chiqariladi. Partiya og'irligi qo'lda yoziladi",
        "Qabul qilishda va ko'chirishda jonli qidiruv va Ro'yxatdan tanlash ishlaydi",
        "F1-F12 tezkor tugmalari o'z mahsulotlari oynasini ochadi",
        "Xizmatlar alohida Xizmatlar bo'limi va belgisiga ega. Xizmat qoldiqsiz sotiladi",
        "O'lchov birliklari serverdan olinadi: metr, litr yoki qadoqdagi mahsulot \"dona\" bo'lib qolmaydi",
        "Sozlamalar -> Ekranda Mahsulot kartochkasi o'lchami slayderi paydo bo'ldi",
        "Ombordagi skan bo'limlarni chalkashtirmaydi",
        "Tuzatildi: chek qatoridagi foizli chegirma serverga yetib bormasdi va to'lov summani tekshirishda to'xtardi",
        "Tahlil server bilan solishtiriladi: saytda o'chirilgan yoki bekor qilingan sotuvlar hisobotlarda qolmaydi",
        "Moliya -> Smenalar: smenani bossangiz uning hisoboti ochiladi. Yopilgan smenalarda Smenani ochish tugmasi olib tashlandi",
        "To'lovda mijozni tanlash endi oddiy ro'yxat",
        "Hisobdan chiqarishlar tarixi hisobotga aylandi: tannarx, summa va jami",
        "Sozlamalar -> Akkaunt: kim kirgan, rol, kassa va smena. Parol saqlanmaydi",
        "Yangi mahsulot kartasi shtrix-kod bo'yicha NurCRM umumiy bazasidan nomni to'ldiradi",
        "Yangilanishlar avval test versiyasi sifatida chiqadi: mijozga versiya sinovda ekani aytiladi, sinovchilar uni kod bilan oladi",
        "Qaytishni tasdiqlash oynasi endi ekrandan chiqib ketmaydi",
        "Yopilgan smena hisobotidagi har bir plitka bosiladi: Sotuvlar, Naqd, Naqdsiz, Qarz, Qaytarishlar, Hisobdan chiqarishlar, Xarajat, Qarz to'lovlari, Chegirmalar. Mahsulotlari bilan cheklar, vaqt, to'lov usuli va jami ko'rsatilgan batafsil hisobot ochiladi",
        "Esc ishlaydi: oynalar va dialoglarni ularning Yopish yoki Bekor qilish tugmasi kabi yopadi. Moliya, Sotuvlar va Mijozlarda avval ochiq chek yoki kartani yopadi; asosiy ekranda menyuni yopadi va qidiruvdan skanerga qaytaradi. Omborda o'tkazilmagan qatorlar qolsa, kassa avval so'raydi",
        "Kassa endi serverning so'rovlar chastotasi chegarasiga urilmaydi. Moliya va Sotuvlar cheklarni sekinroq yuklaydi va yuklanganlarini qayta yuklamaydi. Server kutishni so'rasa, kassa kutadi va so'rovni o'zi takrorlaydi: to'lov \"So'rov rad etildi\" xatosi bilan to'xtamaydi",
        "Sarlavha va menyudagi kassa qoldig'i faqat ishga tushganda emas, har bir to'lovdan keyin yangilanadi",
        "Tuzatildi: menyu va Hisob sahifasida kassir ismi yo'qolib qolardi (\"Kassir —\" ko'rinardi)",
        "Moliya, Sotuvlar, Mijozlar, ABC tahlili va smena hisoboti yuklanayotganda yuklash chizig'i ko'rinadi",
        "Qaytarish va chiqim cheklari hamda chekni qayta chop etish fonda bajariladi: sekin printer endi kassani qotirmaydi",
        "Ovozli qulf moduli yana yuklab olinadi. Xato oynasidagi uzun tugma yozuvi endi kesilmaydi",
        "Tahlil server bilan tiyinigacha solishtirildi. Naqdsiz endi qarzga sotuvlarni o'z ichiga olmaydi. To'liq qaytarilgan chek tushumga ham, naqdga ham, cheklar soniga ham kirmaydi. Sotuvlardagi Chegirmalar qator chegirmalarini ham hisobga oladi. Moliya va Tarixda cheklar vaqti endi 00:00 ko'rinmaydi",
        "ABC tahlili: 80 % chegarasini o'zi kesib o'tgan mahsulot A guruhida qoladi. Avval tushumning chorak qismini bergan mahsulot C guruhiga tushib qolishi mumkin edi. Chiziq va doiraviy diagrammadagi to'lov usullari ranglari endi bir xil",
        "Kassa qoldig'i qaytarishdan keyin ham yangilanadi. Smenani ko'rish oynasi kengaydi: katta summalar kesilmaydi",
    ];

    /// <summary>Список на языке интерфейса (2026-09-07).</summary>
    public static string[] LatestForCurrentLanguage() =>
        UserPreferences.Instance.Language switch
        {
            AppLanguage.Kyrgyz => LatestKy,
            AppLanguage.English => LatestEn,
            AppLanguage.Turkish => LatestTr,
            AppLanguage.Uzbek => LatestUz,
            _ => Latest,
        };

    public static string LatestAsBulletedText() =>
        string.Join("\n", System.Linq.Enumerable.Select(LatestForCurrentLanguage(), line => "• " + line));
}
