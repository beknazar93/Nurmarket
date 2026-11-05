## v1.17.76

Безопасность данных и денег, антифрод и PIN старшего (выключено по умолчанию), электронный чек в Telegram, просрочка при продаже, автозаказ v2, «Пересчёт дня», сверка выписки, дизайн и скорость. Всё из 1.17.75 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.76.tar.gz`; macOS — `nurmarket-kassa-macos-arm64-1.17.76.zip` (Apple Silicon) и `nurmarket-kassa-macos-x64-1.17.76.zip` (Intel); Android — `nurmarket-kassa-android-1.17.76.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Безопасные данные.** Проверочные тесты работают в отдельной папке и не трогают данные кассы; файлы с деньгами и настройками пишутся без риска потери.
- **Защита денег и время сервера.** Возвраты, внесения, изъятия, оплата долга и перемещения уходят с ключом повтора: после обрыва связи та же операция не проведётся дважды. Время продажи берётся по часам сервера — сбитая дата на ПК или телефоне не портит отчёты.
- **Подозрительные операции и PIN старшего.** Выключено по умолчанию — включает владелец. Журнал крупных удалений строк, скидок, возвратов без чека и недостач, баллы кассиров, сообщения владельцу в Telegram (ночью — утром); PIN старшего на скидку, возврат, ручную цену и закрытие смены с недостачей.
- **Электронный чек в Telegram.** Только по согласию клиента: он привязывает телефон к боту магазина, и касса присылает чек с балансом бонусов; на экране покупателя — QR на бота. Ссылка на бумажном чеке по умолчанию выключена, по «/stop» чеки прекращаются.
- **Просрочка при продаже.** Касса предупреждает «Товар просрочен. Продать всё равно?» или пускает только с PIN владельца — режим выбирает владелец, по умолчанию выключено.
- **Закупки и цены.** Автозаказ v2: потерянный спрос, кратность упаковки, минимальная партия, срок поставки; анализ ABC + XYZ (насколько ровен спрос); подсказки цены после прихода («Поднять цену с 85 до 95?») и целевая наценка по категориям; доп. расходы прихода (доставка, грузчики) входят в себестоимость товара.
- **«Пересчёт дня» и сверка выписки.** «Пересчёт дня» — каждый день 20–30 позиций вместо полной ревизии (сначала подозрительные, потом по классам ABC); сверка банковской выписки (MBank, Optima: Excel/CSV) с безналом смен и оплатами поставщикам.
- **Программа владельца экономнее.** Закрытые разделы больше не остаются в памяти спрятанными окнами — программа владельца занимает меньше памяти при долгой работе.
- **Дизайн.** Удаление строки чека, смешанная оплата, плотность списков, тёмная тема и экраны инвестиций стали аккуратнее и читаемее.
- **Скорость.** Миниатюры товаров в 8 раз легче — списки открываются быстрее и занимают меньше места; в журнале появились замеры скорости запросов для поддержки. Плюс мелкие исправления.
- **Весы: единица суммы в штрихкоде.** Единица суммы в штрихкоде этикетки (тыйыны или сомы) выбирается на кассе — в мастере «Настроить по этикетке» и в окнах весов; защита от нелепого веса при скане.
- **Внешние сервисы.** Советник, Telegram и проверка обновлений: сроки ожидания ответа, без дублей сообщений.
- **Возврат долгового чека.** Исправлено двойное вычитание «Касса:» при возврате долгового чека (сервер NurCRM теперь сам снимает наличные): старые местные поправки больше не применяются.
- **Приход, чек и ошибки сервера.** Удаление прихода (с причиной); филиал и устройство в чеке; ключи повтора для проката, бонусов и перемещений; понятные сообщения об ошибках сервера и неверном PIN.
- **Округление весовых как на сервере.** Весовые строки округляются как на сервере — вниз до шага весов.
- **Android.** Сканер штрихкодов, кнопка «Назад», экранная клавиатура, выбор фото, QR и выгрузка в Excel работают надёжнее.

---

# Кыргызча

### Эмнелер жаңы

- **Коопсуз маалыматтар.** Текшерүү тесттери өзүнчө папкада иштейт жана кассанын маалыматтарына тийбейт; акча жана жөндөөлөр файлдары жоголуу коркунучусуз жазылат.
- **Акчаны коргоо жана сервердин убактысы.** Кайтаруулар, киргизүүлөр, чыгаруулар, карыз төлөө жана жылдыруулар кайталоо ачкычы менен кетет: байланыш үзүлгөндөн кийин ошол эле амал эки жолу өтпөйт. Сатуу убактысы сервердин саты боюнча алынат — компьютердеги же телефондогу бузулган дата отчётторду бузбайт.
- **Шектүү операциялар жана улуунун PIN-коду.** Демейки боюнча өчүк — ээси иштетет. Чоң сап өчүрүүлөрдүн, арзандатуулардын, чексиз кайтаруулардын жана жетишпөөлөрдүн журналы, кассирлердин баллдары, ээсине Telegram аркылуу билдирүүлөр (түнкүсүн — эртең менен); арзандатууга, кайтарууга, кол менен бааға жана жетишпөөсү бар сменаны жабууга улуунун PIN-коду.
- **Telegram'да электрондук чек.** Кардардын макулдугу менен гана: ал телефонун дүкөндүн ботуна байлайт, касса чекти бонус балансы менен жөнөтөт; кардардын экранында ботко QR. Кагаз чектеги шилтеме демейки боюнча өчүк, «/stop» менен чектер токтойт.
- **Сатууда мөөнөтү бүткөн товар.** Касса «Товардун мөөнөтү бүткөн. Баары бир сатасызбы?» деп эскертет же ээсинин PIN-коду менен гана өткөрөт — режимди ээси тандайт, демейки боюнча өчүк.
- **Сатып алуу жана баалар.** Автобуйрутма v2: жоголгон суроо-талап, таңгак эселиги, минималдуу партия, жеткирүү мөөнөтү; ABC + XYZ талдоосу (суроо-талап канчалык туруктуу); товар кабыл алуудан кийинки баа кеңештери («Баасын 85тен 95ке көтөрөбүзбү?») жана категория боюнча максаттуу кошумча баа; кабыл алуунун кошумча чыгымдары (жеткирүү, жүк ташуучулар) товардын өздүк наркына кирет.
- **«Күндүн кайра эсеби» жана көчүрмөнү салыштыруу.** «Күндүн кайра эсеби» — толук ревизиянын ордуна күн сайын 20–30 позиция (адегенде шектүүлөр, анан ABC класстары боюнча); банк көчүрмөсүн (MBank, Optima: Excel/CSV) смендердин накталайсыз түшүмү жана жеткирүүчүлөргө төлөмдөр менен салыштыруу.
- **Ээсинин программасы үнөмдүүрөөк.** Жабылган бөлүмдөр жашырылган терезе болуп эсте калбайт — ээсинин программасы узак иштегенде аз эс тутумду ээлейт.
- **Дизайн.** Чек сабын өчүрүү, аралаш төлөм, тизмелердин тыгыздыгы, караңгы тема жана инвестиция экрандары тыкандуу жана окууга ыңгайлуураак болду.
- **Ылдамдык.** Товар миниатюралары 8 эсе жеңил — тизмелер тезирээк ачылат жана аз орун ээлейт; колдоо үчүн журналда суроо ылдамдыгынын өлчөмдөрү пайда болду. Кошумча майда оңдоолор.
- **Тараза: штрихкоддогу сумманын бирдиги.** Этикетка штрихкодундагы сумманын бирдиги (тыйын же сом) кассада тандалат — «Этикетка боюнча жөндөө» устасында жана тараза терезелеринде; сканерлегенде акылга сыйбаган салмактан коргоо.
- **Тышкы кызматтар.** Кеңешчи, Telegram жана жаңыртууларды текшерүү: жоопту күтүү мөөнөттөрү, билдирүүлөр кайталанбайт.
- **Карыз чегин кайтаруу.** Карыз чегин кайтарууда «Касса:» эки жолу кемиген ката оңдолду (NurCRM сервери накталайды өзү алат): эски жергиликтүү оңдоолор колдонулбайт.
- **Киреше, чек жана сервердин каталары.** Кирешени өчүрүү (себеби менен); чекте филиал жана түзмөк; прокат, бонустар жана жылдыруулар үчүн кайталоо ачкычтары; сервердин каталары жана PIN туура эместиги тууралуу түшүнүктүү билдирүүлөр.
- **Салмактуу саптарды сервердей тегеректөө.** Салмактуу саптар сервердегидей тегеректелет — тараза кадамына чейин төмөн карай.
- **Android.** Штрихкод сканери, «Артка» баскычы, экрандык баскычтоп, сүрөт тандоо, QR жана Excelге чыгаруу ишенимдүүрөөк иштейт.

---

# English

### What's new

- **Safe data.** Verification tests run in a separate folder and never touch the till's data; money and settings files are written without risk of loss.
- **Money protection and server time.** Returns, deposits, withdrawals, debt payments and transfers are sent with a retry key: after a dropped connection the same operation is never posted twice. The sale time comes from the server clock, so a wrong date on the PC or phone no longer spoils reports.
- **Suspicious operations and senior PIN.** Off by default — the owner turns it on. A log of large line removals, discounts, returns without a receipt and shortages, cashier scores, Telegram messages to the owner (at night — held until morning); a senior PIN for discounts, returns, manual prices and closing a shift with a shortage.
- **Electronic receipt in Telegram.** Only with the customer's consent: they link their phone to the shop's bot and the till sends the receipt with the bonus balance; the customer display shows a QR to the bot. The link on the paper receipt is off by default, “/stop” ends the receipts.
- **Expired goods at the sale.** The till warns “The product is expired. Sell anyway?” or lets it through only with the owner's PIN — the owner picks the mode, off by default.
- **Purchasing and prices.** Auto-order v2: lost demand, pack multiples, minimum batch, supplier lead time; ABC + XYZ analysis (how steady demand is); price hints after receiving (“Raise the price from 85 to 95?”) and a target markup per category; extra receiving costs (delivery, loaders) go into the product cost.
- **“Day recount” and statement reconciliation.** “Day recount” — 20–30 items a day instead of a full stock count (suspicious ones first, then by ABC class); reconciliation of a bank statement (MBank, Optima: Excel/CSV) with shifts' non-cash sales and supplier payments.
- **Leaner owner program.** Closed sections no longer stay in memory as hidden windows — the owner program uses less memory over a long session.
- **Design.** Line removal, mixed payment, list density, the dark theme and the investment screens are tidier and easier to read.
- **Speed.** Product thumbnails are 8 times lighter — lists open faster and take less space; the log now records request timings for support. Plus small fixes.
- **Scales: amount unit in the barcode.** The unit of the amount in the label barcode (tyiyn or som) is chosen on the till — in the “Set up from a label” wizard and in the scale windows; protection against an absurd weight when scanning.
- **External services.** Advisor, Telegram and update checks: response timeouts, no duplicate messages.
- **Debt receipt return.** Fixed the double subtraction of “Till:” when returning a debt receipt (the NurCRM server now takes the cash itself): old local adjustments are no longer applied.
- **Goods receipt, branch and server errors.** Deleting a goods receipt (with a reason); branch and device on the receipt; retry keys for rental, bonuses and transfers; clear messages for server errors and a wrong PIN.
- **Weighed lines rounded like the server.** Weighed lines are rounded like on the server — down to the scale step.
- **Android.** The barcode scanner, the “Back” button, the on-screen keyboard, photo picking, QR and Excel export work more reliably.

---

# Türkçe

### Yenilikler

- **Güvenli veri.** Doğrulama testleri ayrı bir klasörde çalışır ve kasa verilerine dokunmaz; para ve ayar dosyaları kayıp riski olmadan yazılır.
- **Para koruması ve sunucu saati.** İadeler, para girişleri, çıkışları, borç ödemeleri ve transferler yeniden deneme anahtarıyla gider: bağlantı koptuktan sonra aynı işlem iki kez işlenmez. Satış zamanı sunucu saatinden alınır — bilgisayardaki veya telefondaki yanlış tarih raporları bozmaz.
- **Şüpheli işlemler ve yönetici PIN'i.** Varsayılan olarak kapalı — sahibi açar. Büyük satır silmelerin, indirimlerin, fişsiz iadelerin ve eksiklerin günlüğü, kasiyer puanları, sahibine Telegram mesajları (geceleri — sabaha bırakılır); indirim, iade, elle fiyat ve eksikli vardiya kapatma için yönetici PIN'i.
- **Telegram'da elektronik fiş.** Yalnızca müşterinin onayıyla: telefonunu mağaza botuna bağlar, kasa fişi bonus bakiyesiyle gönderir; müşteri ekranında bota QR görünür. Kağıt fişteki bağlantı varsayılan olarak kapalıdır, «/stop» ile fişler durur.
- **Satışta son kullanma tarihi.** Kasa «Ürünün tarihi geçmiş. Yine de satılsın mı?» diye uyarır veya yalnızca sahibin PIN'iyle geçirir — modu sahibi seçer, varsayılan olarak kapalı.
- **Satın alma ve fiyatlar.** Otomatik sipariş v2: kaybedilen talep, paket katı, asgari parti, tedarik süresi; ABC + XYZ analizi (talep ne kadar düzenli); mal kabulünden sonra fiyat önerileri («Fiyat 85'ten 95'e çıkarılsın mı?») ve kategori başına hedef kâr oranı; mal kabulünün ek masrafları (nakliye, hamal) ürün maliyetine girer.
- **«Günün sayımı» ve ekstre uzlaştırma.** «Günün sayımı» — tam sayım yerine günde 20–30 kalem (önce şüpheliler, sonra ABC sınıfına göre); banka ekstresinin (MBank, Optima: Excel/CSV) vardiyaların nakitsiz satışları ve tedarikçi ödemeleriyle uzlaştırılması.
- **Daha hafif sahip programı.** Kapatılan bölümler artık gizli pencere olarak bellekte kalmaz — sahip programı uzun çalışmada daha az bellek kullanır.
- **Tasarım.** Satır silme, karma ödeme, liste yoğunluğu, koyu tema ve yatırım ekranları daha derli toplu ve okunaklı.
- **Hız.** Ürün küçük resimleri 8 kat daha hafif — listeler daha hızlı açılır ve daha az yer kaplar; günlükte destek için istek süresi ölçümleri var. Ayrıca küçük düzeltmeler.
- **Terazi: barkoddaki tutar birimi.** Etiket barkodundaki tutar birimi (tıyın veya som) kasada seçilir — «Etiketten ayarla» sihirbazında ve terazi pencerelerinde; taramada saçma ağırlığa karşı koruma.
- **Harici hizmetler.** Danışman, Telegram ve güncelleme kontrolü: yanıt bekleme süreleri, tekrarlanan mesaj yok.
- **Borç fişi iadesi.** Borç fişi iadesinde «Kasa:» değerinin iki kez düşülmesi düzeltildi (NurCRM sunucusu artık nakdi kendisi düşer): eski yerel düzeltmeler artık uygulanmaz.
- **Mal kabulü, şube ve sunucu hataları.** Mal kabulünü silme (nedenle); fişte şube ve cihaz; kiralama, bonus ve transferler için yeniden deneme anahtarları; sunucu hataları ve yanlış PIN için anlaşılır mesajlar.
- **Tartılı satırlar sunucu gibi yuvarlanır.** Tartılı satırlar sunucudaki gibi yuvarlanır — terazi adımına kadar aşağı.
- **Android.** Barkod tarayıcı, «Geri» düğmesi, ekran klavyesi, fotoğraf seçimi, QR ve Excel dışa aktarma daha güvenilir çalışır.

---

# O'zbekcha

### Yangiliklar

- **Xavfsiz ma'lumotlar.** Tekshiruv testlari alohida papkada ishlaydi va kassa ma'lumotlariga tegmaydi; pul va sozlama fayllari yo'qolish xavfisiz yoziladi.
- **Pulni himoya va server vaqti.** Qaytarish, kiritish, chiqarish, qarz to'lovi va ko'chirishlar takrorlash kaliti bilan yuboriladi: aloqa uzilgandan keyin bir xil amal ikki marta o'tmaydi. Sotuv vaqti server soati bo'yicha olinadi — kompyuter yoki telefondagi noto'g'ri sana hisobotlarni buzmaydi.
- **Shubhali amallar va katta PIN.** Odatda o'chiq — egasi yoqadi. Katta qator o'chirishlar, chegirmalar, cheksiz qaytarishlar va kamomadlar jurnali, kassirlar ballari, egasiga Telegram xabarlari (tunda — ertalabgacha kutadi); chegirma, qaytarish, qo'lda narx va kamomadli smenani yopish uchun katta PIN.
- **Telegram'da elektron chek.** Faqat mijoz roziligi bilan: u telefonini do'kon botiga bog'laydi, kassa chekni bonus balansi bilan yuboradi; mijoz ekranida botga QR. Qog'oz chekdagi havola odatda o'chiq, «/stop» bilan cheklar to'xtaydi.
- **Sotuvda muddati o'tgan tovar.** Kassa «Tovarning muddati o'tgan. Baribir sotilsinmi?» deb ogohlantiradi yoki faqat egasining PIN-kodi bilan o'tkazadi — rejimni egasi tanlaydi, odatda o'chiq.
- **Xaridlar va narxlar.** Avtobuyurtma v2: yo'qolgan talab, o'ram karraligi, minimal partiya, yetkazib berish muddati; ABC + XYZ tahlili (talab qanchalik barqaror); tovar qabul qilingandan keyingi narx maslahatlari («Narxni 85 dan 95 ga ko'tarilsinmi?») va kategoriya bo'yicha maqsadli ustama; qabulning qo'shimcha xarajatlari (yetkazish, yuk tashuvchilar) tovar tannarxiga kiradi.
- **«Kun qayta hisobi» va ko'chirmani solishtirish.** «Kun qayta hisobi» — to'liq reviziya o'rniga har kuni 20–30 pozitsiya (avval shubhalilar, keyin ABC sinfi bo'yicha); bank ko'chirmasini (MBank, Optima: Excel/CSV) smenalarning naqdsiz tushumi va yetkazib beruvchilarga to'lovlar bilan solishtirish.
- **Egasi dasturi tejamkorroq.** Yopilgan bo'limlar endi yashirin oyna sifatida xotirada qolmaydi — egasi dasturi uzoq ishlaganda kamroq xotira egallaydi.
- **Dizayn.** Qatorni o'chirish, aralash to'lov, ro'yxat zichligi, qorong'i mavzu va investitsiya ekranlari ixchamroq va o'qishga qulayroq bo'ldi.
- **Tezlik.** Tovar miniatyuralari 8 baravar yengil — ro'yxatlar tezroq ochiladi va kamroq joy egallaydi; jurnalda yordam uchun so'rov tezligi o'lchovlari paydo bo'ldi. Qo'shimcha mayda tuzatishlar.
- **Tarozi: shtrix-koddagi summa birligi.** Yorliq shtrix-kodidagi summa birligi (tiyin yoki so'm) kassada tanlanadi — «Yorliq bo'yicha sozlash» ustasida va tarozi oynalarida; skanerlashda bema'ni vazndan himoya.
- **Tashqi xizmatlar.** Maslahatchi, Telegram va yangilanishlarni tekshirish: javob kutish muddatlari, xabarlar takrorlanmaydi.
- **Qarz cheki qaytarilishi.** Qarz chekini qaytarishda «Kassa:» ikki marta ayrilishi tuzatildi (NurCRM serveri endi naqdni o'zi yechadi): eski mahalliy tuzatishlar endi qo'llanilmaydi.
- **Kirim, filial va server xatolari.** Kirimni o'chirish (sabab bilan); chekda filial va qurilma; ijara, bonus va ko'chirishlar uchun takrorlash kalitlari; server xatolari va noto'g'ri PIN haqida tushunarli xabarlar.
- **Tarozi qatorlari server kabi yaxlitlanadi.** Tarozi qatorlari serverdagidek yaxlitlanadi — tarozi qadamigacha pastga.
- **Android.** Shtrix-kod skaneri, «Orqaga» tugmasi, ekran klaviaturasi, rasm tanlash, QR va Excelga yuklash ishonchliroq ishlaydi.
