## v1.16.64

---

# Русский

- Исправлено главное: массовый импорт товаров из файла ОБНУЛЯЛ остаток у уже заведённых товаров, а если в файле не было колонки «Цена» — ещё и цену, после чего товар пробивался за 0 сом. Теперь поля, которых нет в файле, не отправляются вовсе и на сервере остаются прежними
- В предпросмотре импорта для существующих товаров теперь видно «остаток 40 → 60 (замена, не приход)» — импорт заменяет остаток, а не прибавляет к нему, и раньше это было ниоткуда не видно
- Новое: на экране покупателя можно показывать платёжный QR с уже вписанной суммой чека — покупателю не нужно набирать её вручную в приложении банка. Включается в «Настройки → Операции», по умолчанию выключено
- Новое: весы ШТРИХ-ПРИНТ можно подключить напрямую сетевым кабелем, без сервера — в окне «Весы» появились галочка «Напрямую по кабелю», поля IP/порт/пароль и кнопка «Проверить» со звуковым сигналом
- Новое: раз в сутки автоматически создаётся резервная копия локальной базы (хранятся 7 последних). В базе лежит очередь непроведённых офлайн-продаж, история списаний и бонусы клиентов — всего этого больше нигде нет
- Исправлено: касса предлагала «обновиться» на версию СТАРЕЕ установленной, то есть откатиться назад. Защита от этого была написана, но сравнивала версию не той сборки и не срабатывала ни разу
- Исправлено: в разделе «Обновления» вместо английского текста об ошибке теперь понятное объяснение, если касса запущена не из установленной копии
- Исправлено: раскладка «1С» в тёмной теме была нечитаемой — кнопки количества и действий рисовались белым по белому
- Исправлено: сообщения об успехе («Сохранено», «Добавлено сотрудников…») на некоторых темах сливались с фоном и были не видны
- Исправлено: при выгрузке офлайн-очереди касса отправляла серверу идентификатор смены, открытой без интернета — сервер его не принимает, а отказ считался окончательным, и чеки уходили в «ошибки» без возврата в очередь
- Ключ доступа к серверу обновлений убран из программы — раньше он был вшит внутрь и мог быть извлечён из файла программы
- Список изменений в разделе «Обновления» больше не растягивает страницу: у него ограничена высота и появилась прокрутка, поэтому «Откат на прошлую версию» и «Диагностика» остаются на виду
- Исправлено: при скачивании пакета голосового управления окно на ЛЮБУЮ ошибку писало «Проверьте интернет», хотя связь была в порядке — теперь показывается настоящая причина (файла нет на сервере, нет места на диске, оборвалось соединение)
- Исправлено: скачанный пакет распознавания речи (113 МБ) сохранялся в папку, которую обновление кассы заменяет целиком — после каждого автообновления он исчезал и его приходилось качать заново. Теперь он лежит рядом с локальной базой и переживает обновления
- Голосовое управление заработало: ссылки на пакеты распознавания речи вели на релиз, которого не существует, и обе отдавали ошибку 404 — скачать модель не мог никто. Теперь пакеты берутся с официального сайта Vosk. Кыргызская модель тоже доступна — раньше её не было нигде
- Новое: телеграм-бот владельца. При закрытии смены вам в Telegram приходит сводка — выручка, число чеков, средний чек, наличные и безналичные, внесения и изъятия, недостача или излишек. Работает из магазина без своего сервера. Подключается в «Настройки → Операции», по умолчанию выключено
- Исправлено: чек не проводился («Оплата не прошла. Проверьте параметры скидки»), если на чек стояла скидка в ПРОЦЕНТАХ и одновременно списывались бонусы клиента. Сервер хранит скидку в двух разных полях и принимает только одно из них за раз, а бонусы уходят суммой — теперь касса сначала гасит второе поле
- Исправлено (тихая потеря денег): скидка с ПРЕДЫДУЩЕГО чека оставалась на серверной корзине и применялась к следующему. Касса показывала кассиру полную сумму, а продажа записывалась со старой скидкой — недобор, которого никто не видел. Теперь чужая скидка снимается перед каждым чеком
- Исправлено: если свернуть кассу на рабочий стол, вернуть её было нечем — ярлык каждый раз отвечал «Касса уже запущена, проверьте панель задач». Теперь нажатие на ярлык возвращает кассу на экран, даже если она свёрнута или ушла в трей
- Исправлено: при продаже пачками или поштучно касса упиралась в «Недостаточно остатка» с единственной кнопкой «Закрыть» и не предлагала пополнить склад, хотя такая возможность есть. Теперь она спрашивает «Продолжить и пополнить склад?» — как и при обычной продаже
- Исправлено: пополнение склада при продаже брало текущий остаток из локального кэша (он обновляется раз в 2 минуты) и отправляло на сервер итоговое число. Если за это время продала другая касса, её продажи возвращались в остаток. Теперь остаток перечитывается с сервера, а без связи пополнение отменяется

---

# Кыргызча

- Эң негизгиси оңдолду: файлдан товарларды топтоп жүктөө мурунтан бар товарлардын калдыгын НӨЛДӨП салчу, ал эми файлда «Баасы» тилкеси жок болсо — баасын дагы, ошондон кийин товар 0 сомго өтчү. Эми файлда жок талаалар таптакыр жөнөтүлбөйт
- Жүктөөнү алдын ала кароодо мурунтан бар товарлар үчүн «калдык 40 → 60 (алмаштыруу, кириш эмес)» деп көрүнөт — жүктөө калдыкты кошпойт, алмаштырат
- Жаңы: сатып алуучунун экранында суммасы мурунтан жазылган төлөм QR-кодун көрсөтүүгө болот — сатып алуучу аны банк тиркемесинде кол менен теришинин кажети жок. «Жөндөөлөр → Операциялар» бөлүмүнөн күйгүзүлөт, демейки боюнча өчүк
- Жаңы: ШТРИХ-ПРИНТ таразаларын тармак кабели менен түз туташтырууга болот, серверсиз — «Тараза» терезесинде «Кабель аркылуу түз» белгиси, IP/порт/сырсөз талаалары жана үн берүүчү «Текшерүү» баскычы пайда болду
- Жаңы: күнүнө бир жолу жергиликтүү базанын камдык көчүрмөсү автоматтык түрдө жасалат (акыркы 7 сакталат). Базада өткөрүлбөгөн офлайн сатуулар, эсептен чыгаруу тарыхы жана кардарлардын бонустары бар — мунун баары башка эч жерде жок
- Оңдолду: касса орнотулгандан ЭСКИ версияга «жаңыртууну» сунуштачу, башкача айтканда артка кайтарууну. Мындан коргоо жазылган, бирок туура эмес чогултманын версиясын салыштырып, эч качан иштечү эмес
- Оңдолду: «Жаңыртуулар» бөлүмүндө англисче ката текстинин ордуна эми түшүнүктүү түшүндүрмө берилет
- Оңдолду: караңгы темада «1С» жайгашуусу окулбай калчу — сан жана аракет баскычтары ак фондо ак түстө тартылчу
- Оңдолду: ийгилик тууралуу билдирүүлөр («Сакталды», «Кызматкерлер кошулду…») кээ бир темаларда фон менен кошулуп, көрүнбөй калчу
- Оңдолду: офлайн кезегин жүктөөдө касса серверге интернетсиз ачылган сменанын идентификаторун жөнөтчү — сервер аны кабыл албайт, ал эми баш тартуу акыркы деп эсептелип, чектер кезекке кайтпай «каталарга» кетчү
- Жаңыртуу серверине кирүү ачкычы программадан алынып салынды — мурун ал ичине тигилген жана программанын файлынан алынышы мүмкүн эле
- «Жаңыртуулар» бөлүмүндөгү өзгөрүүлөр тизмеси эми баракты созбойт: бийиктиги чектелип, жылдыруу пайда болду, ошондуктан «Мурунку версияга кайтуу» жана «Диагностика» көрүнүп турат
- Оңдолду: үн башкаруу пакетин жүктөөдө терезе КАНДАЙ БОЛБОСУН катага «Интернетти текшериңиз» деп жазчу, байланыш жакшы болсо да — эми чыныгы себеп көрсөтүлөт (файл серверде жок, дискте орун жок, туташуу үзүлдү)
- Оңдолду: жүктөлгөн кеп таануу пакети (113 МБ) кассанын жаңыртуусу толугу менен алмаштырган папкага сакталчу — ар бир авто-жаңыртуудан кийин жоголуп, кайра жүктөөгө туура келчү. Эми ал жергиликтүү базанын жанында жатат жана жаңыртууларды аман өтөт
- Үн башкаруу иштей баштады: кеп таануу пакеттеринин шилтемелери жок релизге алып барчу жана экөө тең 404 катасын кайтарчу — моделди эч ким жүктөй алчу эмес. Эми пакеттер Vosk расмий сайтынан алынат. Кыргыз модели да жеткиликтүү — мурун ал эч жерде жок болчу
- Жаңы: ээсинин телеграм-боту. Смена жабылганда Telegram'га жыйынтык келет — түшкөн акча, чектердин саны, орточо чек, накталай жана накталай эмес, салуулар жана алуулар, жетишпегени же ашыкчасы. Дүкөндөн өз серверисиз иштейт. «Жөндөөлөр → Операциялар» бөлүмүнөн туташтырылат, демейки боюнча өчүк
- Оңдолду: суммага арзандатуусу бар чек өтпөй калчу — касса «Төлөм өтпөдү. Арзандатуу параметрлерин текшериңиз» деп жазчу, арзандатуу туура болсо да. Арзандатуу серверге эки жолу жөнөтүлчү: адегенде чек өткөрүлгөндө, анан төлөөдө ошол эле маани менен — экинчисин сервер көтөрө албай калчу. Эми кайталап жөнөтүлбөйт
- Оңдолду: чекке ПАЙЫЗДЫК арзандатуу коюлуп, ошол эле учурда кардардын бонустары эсептен чыгарылса, чек өтпөй калчу («Төлөм өтпөдү. Арзандатуу параметрлерин текшериңиз»). Сервер арзандатууну эки башка талаада сактайт жана бир эле убакта бирөөсүн гана кабыл алат, бонустар болсо сумма менен кетет — эми касса адегенде экинчи талааны өчүрөт
- Оңдолду (акчанын тынч жоголушу): МУРУНКУ чектин арзандатуусу сервердеги себетте калып, кийинки чекке колдонулчу. Касса кассирге толук сумманы көрсөтчү, ал эми сатуу эски арзандатуу менен жазылчу — эч ким көрбөгөн кем чыгуу. Эми ар бир чектин алдында бөтөн арзандатуу алынып салынат
- Оңдолду: кассаны иш столуна жыйнап койсоң, аны кайтарууга эч нерсе жок болчу — жарлык ар дайым «Касса иштеп жатат, тапшырмалар панелин текшериңиз» деп жооп берчү. Эми жарлыкты басканда касса экранга кайтат, жыйналган же трейге кеткен болсо да
- Оңдолду: пачка менен же даана сатууда касса «Калдык жетишсиз» деген жалгыз «Жабуу» баскычтуу терезеге такалып, складды толуктоону сунуштачу эмес, ал мүмкүнчүлүк бар болсо да. Эми «Улантып, складды толуктайсызбы?» деп сурайт — кадимки сатуудагыдай
- Оңдолду: сатуу учурунда складды толуктоо учурдагы калдыкты жергиликтүү кэштен алчу (ал 2 мүнөттө бир жаңырат) жана серверге жыйынтык санды жөнөтчү. Ошол убакта башка касса сатса, анын сатуулары калдыкка кайтып келчү. Эми калдык серверден кайра окулат, байланыш жок болсо толуктоо жокко чыгарылат

---

# English

- Most important fix: bulk product import used to RESET the stock of products that already existed, and if the file had no "Price" column, their price as well - after which the product rang up at 0 som. Fields missing from the file are now not sent at all and keep their server-side values
- The import preview now shows "stock 40 -> 60 (replace, not add)" for existing products - import replaces the stock rather than adding to it, and previously there was no way to see that
- New: the customer display can show a payment QR with the receipt amount already encoded - the customer no longer types it into their banking app by hand. Enable it in Settings -> Operations; off by default
- New: SHTRIKH-PRINT scales can be connected directly with a network cable, without the server - the Scales window now has a "Direct over cable" checkbox, IP/port/password fields and a "Test" button that makes the scales beep
- New: a backup of the local database is created automatically once a day (the 7 most recent are kept). The database holds the queue of unsubmitted offline sales, the write-off history and customer loyalty points - none of which exist anywhere else
- Fixed: the app offered to "update" to a version OLDER than the installed one, i.e. to roll back. The protection against this had been written, but compared the version of the wrong assembly and never actually worked
- Fixed: the Updates section now explains in plain language that the app is running from an uninstalled copy, instead of showing an English error
- Fixed: the "1C" layout was unreadable in the dark theme - the quantity and action buttons were drawn white on white
- Fixed: success messages ("Saved", "Employees added...") blended into the background and were invisible on some themes
- Fixed: when replaying the offline queue the app sent the server the identifier of a shift opened without internet - the server rejects it, that rejection counted as final, and receipts went to "failed" with no way back into the queue
- The update server access key has been removed from the program - it used to be embedded inside and could be extracted from the program file
- The change list in the Updates section no longer stretches the page: its height is capped and it scrolls, so "Roll back to a previous version" and "Diagnostics" stay visible
- Fixed: when downloading the voice-control package, the window said "Check your internet" for ANY failure even when the connection was fine - it now shows the real reason (file missing on the server, no disk space, connection dropped)
- Fixed: the downloaded speech-recognition package (113 MB) was stored in a folder that the app update replaces entirely - it disappeared after every auto-update and had to be downloaded again. It now lives next to the local database and survives updates
- Voice control now works: the speech-recognition package links pointed at a release that does not exist and both returned 404 - nobody could download a model. The packages are now taken from the official Vosk site. The Kyrgyz model is available too - previously it existed nowhere
- New: owner's Telegram bot. When a shift closes you get a summary in Telegram - revenue, receipt count, average receipt, cash and cashless, deposits and withdrawals, shortage or surplus. Works from the shop without your own server. Set it up in Settings -> Operations; off by default
- Fixed: a receipt with a fixed-sum discount would not go through - the app said "Payment failed. Check the discount settings" even though the discount was fine. The discount was sent to the server twice: first when the receipt was transferred, then again at payment with the same value - and the server could not take the second one. The repeat is no longer sent
- Fixed: a receipt would not go through ("Payment failed. Check the discount settings") when a PERCENTAGE discount was applied to the receipt and customer loyalty points were redeemed at the same time. The server keeps the discount in two separate fields and accepts only one of them at a time, while points are sent as a sum - the app now clears the other field first
- Fixed (silent loss of money): a discount from the PREVIOUS receipt stayed on the server-side cart and was applied to the next one. The app showed the cashier the full amount while the sale was recorded with the old discount - an undercharge nobody could see. Any leftover discount is now cleared before each receipt
- Fixed: if the app was minimised to the desktop there was no way to bring it back - the shortcut kept answering "The app is already running, check the taskbar". Clicking the shortcut now brings the app back on screen, even when minimised or sent to the tray
- Fixed: when selling by pack or by piece the app hit an "Insufficient stock" message with a single "Close" button and never offered to replenish, even though that feature exists. It now asks "Continue and replenish?" - the same as in a regular sale
- Fixed: replenishing stock during a sale took the current quantity from the local cache (refreshed every 2 minutes) and sent the resulting total to the server. If another till sold in the meantime, its sales were returned to the stock. The quantity is now re-read from the server, and without a connection the replenishment is canceled

---

# Türkçe

- En onemli duzeltme: toplu urun ice aktarma, halihazirda kayitli urunlerin stogunu SIFIRLIYORDU, dosyada "Fiyat" sutunu yoksa fiyatini da - ardindan urun 0 som olarak okutuluyordu. Dosyada bulunmayan alanlar artik hic gonderilmiyor
- Ice aktarma onizlemesinde mevcut urunler icin "stok 40 -> 60 (degistirme, ekleme degil)" gorunuyor - ice aktarma stogu degistirir, uzerine eklemez
- Yeni: musteri ekraninda tutari onceden islenmis bir odeme QR kodu gosterilebilir - musterinin tutari banka uygulamasina elle girmesi gerekmez. Ayarlar -> Islemler bolumunden acilir, varsayilan olarak kapali
- Yeni: SHTRIKH-PRINT teraziler sunucu olmadan dogrudan ag kablosuyla baglanabilir - "Terazi" penceresine "Kablo ile dogrudan" onay kutusu, IP/port/sifre alanlari ve teraziyi otturen "Test" dugmesi eklendi
- Yeni: yerel veritabaninin yedegi gunde bir kez otomatik olusturuluyor (en son 7 kopya saklanir). Veritabaninda gonderilmemis cevrimdisi satislar, dusum gecmisi ve musteri puanlari bulunur - bunlarin hicbiri baska yerde yoktur
- Duzeltildi: uygulama, kurulu olandan DAHA ESKI bir surume "guncelleme" oneriyordu, yani geri almayi. Buna karsi koruma yazilmisti ama yanlis derlemenin surumunu karsilastiriyor ve hic calismiyordu
- Duzeltildi: Guncellemeler bolumu, Ingilizce hata metni yerine uygulamanin kurulu olmayan bir kopyadan calistigini anlasilir bicimde acikliyor
- Duzeltildi: koyu temada "1C" duzeni okunamiyordu - miktar ve islem dugmeleri beyaz uzerine beyaz ciziliyordu
- Duzeltildi: basari mesajlari ("Kaydedildi", "Personel eklendi...") bazi temalarda arka plana karisip gorunmez oluyordu
- Duzeltildi: cevrimdisi kuyruk aktarilirken uygulama, internetsiz acilan vardiyanin kimligini sunucuya gonderiyordu - sunucu bunu kabul etmez, bu ret kesin sayiliyor ve fisler kuyruga donmeden "hata"ya dusuyordu
- Guncelleme sunucusu erisim anahtari programdan kaldirildi - daha once icine gomuluydu ve program dosyasindan cikarilabilirdi
- Guncellemeler bolumundeki degisiklik listesi artik sayfayi uzatmiyor: yuksekligi sinirlandi ve kaydirma eklendi, boylece "Onceki surume don" ve "Tani" gorunur kaliyor
- Duzeltildi: sesli kontrol paketi indirilirken pencere, baglanti sorunsuzken bile HER hata icin "Internetinizi kontrol edin" yaziyordu - artik gercek nedeni gosteriyor (dosya sunucuda yok, diskte yer yok, baglanti koptu)
- Duzeltildi: indirilen konusma tanima paketi (113 MB), uygulama guncellemesinin tamamen degistirdigi bir klasore kaydediliyordu - her otomatik guncellemeden sonra kayboluyor ve yeniden indirilmesi gerekiyordu. Artik yerel veritabaninin yaninda duruyor ve guncellemeleri atlatiyor
- Sesli kontrol artik calisiyor: konusma tanima paketi baglantilari var olmayan bir surume isaret ediyordu ve ikisi de 404 donduruyordu - kimse model indiremiyordu. Paketler artik resmi Vosk sitesinden aliniyor. Kirgizca model de mevcut - onceden hicbir yerde yoktu
- Yeni: isletme sahibinin Telegram botu. Vardiya kapandiginda Telegram'a ozet gelir - ciro, fis sayisi, ortalama fis, nakit ve nakit disi, girisler ve cikislar, eksik veya fazla. Magazadan kendi sunucunuz olmadan calisir. Ayarlar -> Islemler bolumunden kurulur, varsayilan olarak kapali
- Duzeltildi: tutar indirimi olan fis gecmiyordu - uygulama indirimde sorun olmamasina ragmen "Odeme basarisiz. Indirim ayarlarini kontrol edin" yaziyordu. Indirim sunucuya iki kez gonderiliyordu: once fis aktarilirken, sonra odeme sirasinda ayni degerle - sunucu ikincisini kaldiramiyordu. Artik tekrar gonderilmiyor
- Duzeltildi: fise YUZDE indirim uygulanip ayni anda musteri puanlari kullanildiginda fis gecmiyordu ("Odeme basarisiz. Indirim ayarlarini kontrol edin"). Sunucu indirimi iki ayri alanda tutuyor ve ayni anda yalnizca birini kabul ediyor, puanlar ise tutar olarak gonderiliyor - uygulama artik once diger alani sifirliyor
- Duzeltildi (sessiz para kaybi): ONCEKI fisin indirimi sunucudaki sepette kaliyor ve bir sonraki fise uygulaniyordu. Uygulama kasiyere tam tutari gosterirken satis eski indirimle kaydediliyordu - kimsenin goremedigi bir eksik tahsilat. Artik her fisten once kalan indirim temizleniyor
- Duzeltildi: uygulama masaustune kucultuldugunde geri getirmenin yolu yoktu - kisayol her seferinde "Uygulama zaten calisiyor, gorev cubugunu kontrol edin" diyordu. Artik kisayola tiklamak uygulamayi ekrana geri getiriyor, kucultulmus veya tepsiye gonderilmis olsa bile
- Duzeltildi: paket veya adet satisinda uygulama tek "Kapat" dugmeli "Stok yetersiz" mesajina takiliyor ve bu ozellik var olmasina ragmen stok eklemeyi onermiyordu. Artik "Devam edip stok eklensin mi?" diye soruyor - normal satista oldugu gibi
- Duzeltildi: satis sirasinda stok ekleme, mevcut miktari yerel onbellekten aliyordu (2 dakikada bir yenilenir) ve sonucu sunucuya gonderiyordu. Bu sirada baska bir kasa satis yaptiysa, o satislar stoka geri donuyordu. Miktar artik sunucudan yeniden okunuyor, baglanti yoksa islem iptal ediliyor

---

# O'zbekcha

- Eng muhim tuzatish: mahsulotlarni ommaviy import qilish allaqachon kiritilgan mahsulotlar qoldigini NOLGA TUSHIRARDI, faylda "Narx" ustuni bolmasa - narxini ham, shundan song mahsulot 0 somga otardi. Endi faylda yoq maydonlar umuman yuborilmaydi
- Import korinishida mavjud mahsulotlar uchun "qoldiq 40 -> 60 (almashtirish, kirim emas)" korinadi - import qoldiqni qoshmaydi, almashtiradi
- Yangi: xaridor ekranida summasi oldindan kiritilgan tolov QR-kodini korsatish mumkin - xaridor uni bank ilovasida qolda terishi shart emas. "Sozlamalar -> Operatsiyalar" bolimidan yoqiladi, sukut boyicha ochiq
- Yangi: SHTRIKH-PRINT tarozilarini serversiz, togridan-togri tarmoq kabeli bilan ulash mumkin - "Tarozi" oynasida "Kabel orqali togridan-togri" belgisi, IP/port/parol maydonlari va ovoz beruvchi "Tekshirish" tugmasi paydo boldi
- Yangi: mahalliy malumotlar bazasining zaxira nusxasi kuniga bir marta avtomatik yaratiladi (oxirgi 7 tasi saqlanadi). Bazada yuborilmagan oflayn savdolar, hisobdan chiqarish tarixi va mijozlar bonuslari bor - bularning hech biri boshqa joyda yoq
- Tuzatildi: dastur ornatilganidan ESKIROQ versiyaga "yangilanishni" taklif qilardi, yani orqaga qaytishni. Bunga qarshi himoya yozilgan edi, lekin notogri yigmaning versiyasini solishtirib, hech qachon ishlamagan
- Tuzatildi: "Yangilanishlar" bolimi inglizcha xato matni orniga dastur ornatilmagan nusxadan ishga tushirilganini tushunarli tilda tushuntiradi
- Tuzatildi: qorongi mavzuda "1C" joylashuvi oqib bolmasdi - miqdor va amal tugmalari oq fonda oq rangda chizilardi
- Tuzatildi: muvaffaqiyat xabarlari ("Saqlandi", "Xodimlar qoshildi...") bazi mavzularda fon bilan qoshilib, korinmay qolardi
- Tuzatildi: oflayn navbatni yuborishda dastur serverga internetsiz ochilgan smena identifikatorini yuborardi - server uni qabul qilmaydi, rad etish yakuniy hisoblanib, cheklar navbatga qaytmasdan "xatolar"ga tushardi
- Yangilanish serveriga kirish kaliti dasturdan olib tashlandi - avval u ichiga joylashtirilgan va dastur faylidan chiqarib olinishi mumkin edi
- "Yangilanishlar" bolimidagi ozgarishlar royxati endi sahifani cho'zmaydi: balandligi cheklandi va aylantirish paydo boldi, shuning uchun "Oldingi versiyaga qaytish" va "Diagnostika" ko'rinib turadi
- Tuzatildi: ovozli boshqaruv paketini yuklab olishda oyna aloqa yaxshi bolsa ham HAR QANDAY xato uchun "Internetni tekshiring" deb yozardi - endi asl sabab korsatiladi (fayl serverda yoq, diskda joy yoq, ulanish uzildi)
- Tuzatildi: yuklab olingan nutqni tanish paketi (113 MB) dastur yangilanishi butunlay almashtiradigan papkaga saqlanardi - har bir avto-yangilanishdan keyin yoqolib, qaytadan yuklash kerak bolardi. Endi u mahalliy baza yonida turadi va yangilanishlardan omon qoladi
- Ovozli boshqaruv ishlay boshladi: nutqni tanish paketlari havolalari mavjud bolmagan relizga olib borardi va ikkalasi ham 404 qaytarardi - hech kim modelni yuklab ololmasdi. Paketlar endi rasmiy Vosk saytidan olinadi. Qirgizcha model ham mavjud - avval u hech qayerda yoq edi
- Yangi: egasining Telegram boti. Smena yopilganda Telegramga xulosa keladi - tushum, cheklar soni, ortacha chek, naqd va naqdsiz, kiritishlar va yechib olishlar, kamomad yoki ortiqcha. Dokondan oz serveringizsiz ishlaydi. "Sozlamalar -> Operatsiyalar" bolimidan ulanadi, sukut boyicha ochiq
- Tuzatildi: summaga chegirmasi bor chek otmasdi - dastur chegirmada muammo bolmasa ham "Tolov otmadi. Chegirma parametrlarini tekshiring" deb yozardi. Chegirma serverga ikki marta yuborilardi: avval chek otkazilganda, keyin tolovda osha qiymat bilan - serverni ikkinchisi kotara olmasdi. Endi qayta yuborilmaydi
- Tuzatildi: chekka FOIZLI chegirma qoyilib, ayni paytda mijoz bonuslari hisobdan chiqarilganda chek otmasdi ("Tolov otmadi. Chegirma parametrlarini tekshiring"). Server chegirmani ikki alohida maydonda saqlaydi va bir vaqtda faqat bittasini qabul qiladi, bonuslar esa summa bilan yuboriladi - dastur endi avval ikkinchi maydonni tozalaydi
- Tuzatildi (pulning sokin yoqolishi): OLDINGI chekning chegirmasi serverdagi savatda qolib, keyingi chekka qollanardi. Dastur kassirga toliq summani korsatardi, sotuv esa eski chegirma bilan yozilardi - hech kim kormaydigan kam tushum. Endi har chekdan oldin begona chegirma olib tashlanadi
- Tuzatildi: dastur ish stoliga kichraytirilganda uni qaytarishning iloji yoq edi - yorliq har safar "Dastur allaqachon ishlayapti, vazifalar panelini tekshiring" deb javob berardi. Endi yorliqni bosish dasturni ekranga qaytaradi, kichraytirilgan yoki tryega yuborilgan bolsa ham
- Tuzatildi: paket yoki dona bilan sotishda dastur bitta "Yopish" tugmali "Qoldiq yetarli emas" xabariga taqalib, ombor toldirishni taklif qilmasdi, garchi bunday imkoniyat bor bolsa ham. Endi "Davom etib, omborni toldirasizmi?" deb soraydi - oddiy sotuvdagidek
- Tuzatildi: sotuv paytida omborni toldirish joriy qoldiqni mahalliy keshdan olardi (u 2 daqiqada bir yangilanadi) va natijani serverga yuborardi. Shu vaqtda boshqa kassa sotgan bolsa, uning sotuvlari qoldiqqa qaytardi. Endi qoldiq serverdan qayta oqiladi, aloqa bolmasa toldirish bekor qilinadi
