## v1.17.75

Возврат и деньги на медленном сервере, накладная на возврат поставщику, инвестиции, удаление и архив товаров, умное сопоставление накладных, поиск фото пачками, спокойная Android-касса. Версия 1.17.74 была тестовой и клиентам не выходила — её изменения входят сюда. Всё из 1.17.73 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.75.tar.gz`; macOS — `nurmarket-kassa-macos-arm64-1.17.75.zip` (Apple Silicon) и `nurmarket-kassa-macos-x64-1.17.75.zip` (Intel); Android — `nurmarket-kassa-android-1.17.75.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Возврат.** «Касса:» в шапке уменьшается сразу после возврата наличными; в чеке видно «возвращено 1 из 2»; «Последний чек» печатается сразу.
- **Деньги на медленном сервере.** Возврат, обмен, оплата долга, выплата зарплаты и перемещение не держат окно дольше 8 секунд; повторно ту же операцию нажать нельзя, пока сервер не ответил.
- **Надёжнее.** Продажа «в долг» уходит в офлайн-долг, только если запрос точно не отправился; склад при обрыве связи не прибавляет остаток второй раз.
- **Заставка запуска.** Если сервер отвечает долго, через 15 с объясняет это и даёт кнопку «Выйти».
- **Закрытие программы.** Установщик или выключение Windows больше не зависают на вопросе «Смена не закрыта»; смена остаётся открытой.
- **X-отчёт и история чеков.** «Корректировка» только при настоящем расхождении; без связи в истории настоящие номера и суммы со скидкой.
- **Удаление товара «как в 1С».** Остаток не пропадает: если списан не полностью, товар остаётся, а касса пишет, сколько осталось. Архив товаров грузится быстрее, есть «Отменить загрузку» и поиск.
- **Накладная на возврат поставщику.** Печать, PDF, Word, Excel: номер с сервера, поставщик, приход-основание, суммы, сумма прописью, места для подписей.
- **Инвестиции и окупаемость.** Вложения считаются по датам, один срок окупаемости везде, верная ведомость выплат при нескольких инвесторах.
- **Программа владельца.** Не спрашивает про «несохранённые правки» после переключения фильтра; экранный диктор читает кнопки; при медленном сервере видно «Ждём ответ сервера… N с».
- **Нур Советник и ИИ-звонок.** Понимает «кыргызча жооп бер», «отвечай по-русски»; звонок не переходит сам на другой язык.
- **Накладная: умное сопоставление.** Сокращения («Мол.»), «1л» = «1 литр» = «1000 мл», латиница и кириллица; похожие товары списком до 5; касса запоминает выбор у поставщика; «Подобрать ИИ» — только с подтверждением.
- **Накладная по фото и рукописная.** ИИ опирается на названия склада, неуверенные строки подсвечены «проверьте по фото».
- **Поиск фото пачками.** Нур Советник ищет фото по 20 или 50 товаров, помнит прогресс, неуверенные совпадения отдельно с пометкой «проверьте».
- **Android.** Файлы (PDF, Word, Excel) сохраняются в выбранное место; уход в фон и камера не закрывают программу; чек по Bluetooth не печатается дважды.

---

# Кыргызча

### Эмнелер жаңы

- **Кайтаруу.** Баш жактагы «Касса:» накталай кайтаруудан кийин дароо азаят; чекте «1 / 2 кайтарылды» көрүнөт; «Акыркы чек» дароо басылат.
- **Жай серверде акча.** Кайтаруу, алмаштыруу, карыз төлөө, эмгек акы төлөө жана жылдыруу терезени 8 секундадан ашык карманбайт; сервер жооп бербегиче ошол эле амалды экинчи жолу басууга болбойт.
- **Ишенимдүүрөөк.** «Карызга» сатуу сурам так жөнөтүлбөсө гана оффлайн-карызга кетет; байланыш үзүлгөндө кампа калдыкты экинчи жолу кошпойт.
- **Иштетүү заставкасы.** Сервер узак жооп берсе, 15 секунддан кийин муну түшүндүрүп, «Чыгуу» баскычын берет.
- **Программаны жабуу.** Орнотуучу же Windows өчүрүлгөндө «Смена жабылган жок» деген суроодо илинбейт; смена ачык калат.
- **X-отчёт жана чектер тарыхы.** «Оңдоо» чыныгы айырма болгондо гана; байланышсыз тарыхта чыныгы номерлер жана арзандатуу менен суммалар.
- **Товарды «1С сыяктуу» өчүрүү.** Калдык жоголбойт: толук эсептен чыгарылбаса, товар калат, касса канча калганын жазат. Товарлардын архиви тезирээк жүктөлөт, «Жүктөөнү токтотуу» жана издөө бар.
- **Жеткирүүчүгө кайтаруу накладнойу.** Басып чыгаруу, PDF, Word, Excel: серверден номер, жеткирүүчү, негиз киреше, суммалар, сумма жазуу менен, кол коюу жайлары.
- **Инвестициялар жана кайтарым.** Салымдар даталар боюнча эсептелет, бардык жерде бир кайтарым мөөнөтү, бир нече инвестор болгондо төлөм ведомосту туура.
- **Ээсинин программасы.** Чыпканы которгондон кийин «сакталбаган оңдоолор» деп сурабайт; экран окугуч баскычтарды окуйт; жай серверде «Серверди күтүп жатабыз… N с» көрүнөт.
- **Нур Кеңешчи жана ЖИ чалуусу.** «Кыргызча жооп бер», «орусча жооп бер» буйруктарын түшүнөт; чалуу өзү башка тилге өтпөйт.
- **Накладной: акылдуу салыштыруу.** Кыскартуулар («Мол.»), «1л» = «1 литр» = «1000 мл», латын жана кирилл; окшош товарлар 5ке чейин тизме менен; касса жеткирүүчү боюнча тандоону эстейт; «ЖИ менен тандоо» — ырастоо менен гана.
- **Сүрөт жана кол менен жазылган накладной.** ЖИ кампанын аталыштарына таянат, ишенимсиз саптар «сүрөт боюнча текшериңиз» деп белгиленет.
- **Сүрөттү пачкалап издөө.** Нур Кеңешчи 20 же 50 товардын сүрөтүн издейт, жүрүшүн эстейт, ишенимсиз дал келүүлөр өзүнчө «текшериңиз» белгиси менен.
- **Android.** Файлдар (PDF, Word, Excel) тандалган жерге сакталат; фонго чыгуу жана камера программаны жаппайт; Bluetooth аркылуу чек эки жолу басылбайт.

---

# English

### What's new

- **Returns.** The “Till:” header goes down right after a cash return; a receipt shows “returned 1 of 2”; “Last receipt” prints at once.
- **Money on a slow server.** Return, exchange, debt payment, salary payout and transfer no longer hold the window for more than 8 seconds; the same operation cannot be pressed again until the server answers.
- **More reliable.** A credit sale goes to the offline debt queue only if the request was definitely not sent; the warehouse does not add stock twice after a connection drop.
- **Startup splash.** If the server answers slowly, after 15 s it explains this and offers an “Exit” button.
- **Closing the program.** An installer or Windows shutdown no longer hangs on “Shift not closed”; the shift stays open.
- **X-report and receipt history.** “Adjustment” appears only for a real discrepancy; without a connection the history shows real receipt numbers and discounted amounts.
- **Deleting a product “as in 1C”.** Stock is not lost: if it was not written off completely, the product stays and the till says how much is left. The product archive loads faster, with “Cancel loading” and search.
- **Return-to-supplier waybill.** Print, PDF, Word, Excel: server number, supplier, delivery it is based on, amounts, amount in words, places for signatures.
- **Investments and payback.** Investments are counted by date, one payback period everywhere, a correct payout statement with several investors.
- **Owner program.** No “unsaved changes” question after switching a filter; the screen reader reads the buttons; with a slow server “Waiting for the server… N s” is shown.
- **Nur Advisor and the AI call.** Understands “answer in Kyrgyz”, “speak Russian”; the call does not switch language by itself.
- **Invoice: smarter matching.** Abbreviations (“Mol.”), “1l” = “1 litre” = “1000 ml”, Latin and Cyrillic; similar items as a list of up to 5; the till remembers your choice per supplier; “Match with AI” only with your confirmation.
- **Photo and handwritten invoices.** The AI leans on your stock names, uncertain lines are marked “check against the photo”.
- **Photo search in batches.** Nur Advisor searches photos for 20 or 50 products, remembers progress, uncertain matches are shown separately with a “check” mark.
- **Android.** Files (PDF, Word, Excel) are saved to the chosen place; going to the background or opening the camera does not close the program; a Bluetooth receipt is not printed twice.

---

# Türkçe

### Yenilikler

- **İade.** Üstteki «Kasa:» nakit iadeden hemen sonra azalır; fişte «iade edilen 1 / 2» görünür; «Son fiş» hemen yazdırılır.
- **Yavaş sunucuda para işlemleri.** İade, değişim, borç ödemesi, maaş ödemesi ve transfer pencereyi 8 saniyeden fazla tutmaz; sunucu yanıtlayana kadar aynı işleme tekrar basılamaz.
- **Daha güvenilir.** Veresiye satış, istek kesinlikle gönderilmediyse çevrimdışı borca gider; bağlantı kopunca depo stoğu ikinci kez eklemez.
- **Açılış ekranı.** Sunucu yavaş yanıt verirse 15 sn sonra bunu açıklar ve «Çıkış» düğmesi sunar.
- **Programı kapatma.** Yükleyici veya Windows kapanışı artık «Vardiya kapatılmadı» sorusunda takılmaz; vardiya açık kalır.
- **X-raporu ve fiş geçmişi.** «Düzeltme» yalnızca gerçek fark olduğunda çıkar; bağlantısız geçmişte gerçek fiş numaraları ve indirimli tutarlar görünür.
- **Ürünü «1C gibi» silme.** Stok kaybolmaz: tam düşülmediyse ürün kalır, kasa ne kadar kaldığını yazar. Ürün arşivi daha hızlı yüklenir, «Yüklemeyi iptal et» ve arama vardır.
- **Tedarikçiye iade irsaliyesi.** Yazdır, PDF, Word, Excel: sunucudan numara, tedarikçi, dayanak giriş, tutarlar, yazıyla tutar, imza yerleri.
- **Yatırımlar ve geri dönüş.** Yatırımlar tarihe göre hesaplanır, her yerde tek geri dönüş süresi, birden çok yatırımcıda doğru ödeme çizelgesi.
- **İşletme sahibi programı.** Filtre değişince «kaydedilmemiş değişiklik» diye sormaz; ekran okuyucu düğmeleri okur; yavaş sunucuda «Sunucu yanıtı bekleniyor… N sn» görünür.
- **Nur Danışman ve YZ araması.** «Kırgızca cevap ver», «Rusça cevap ver» komutlarını anlar; arama kendiliğinden başka dile geçmez.
- **Fatura: akıllı eşleştirme.** Kısaltmalar («Mol.»), «1l» = «1 litre» = «1000 ml», Latin ve Kiril; benzer ürünler 5'e kadar liste halinde; kasa tedarikçi başına seçiminizi hatırlar; «YZ ile eşleştir» yalnızca onayınızla.
- **Fotoğraflı ve el yazısı fatura.** YZ deponuzdaki adlara dayanır, emin olunmayan satırlar «fotoğrafa göre kontrol edin» diye işaretlenir.
- **Fotoğrafları gruplar halinde arama.** Nur Danışman 20 veya 50 ürünün fotoğrafını arar, ilerlemeyi hatırlar, emin olunmayan eşleşmeler ayrı ve «kontrol edin» işaretiyle gösterilir.
- **Android.** Dosyalar (PDF, Word, Excel) seçilen yere kaydedilir; arka plana geçmek veya kamera programı kapatmaz; Bluetooth fişi iki kez basılmaz.

---

# O'zbekcha

### Yangiliklar

- **Qaytarish.** Yuqoridagi «Kassa:» naqd qaytarishdan keyin darrov kamayadi; chekda «qaytarildi 1 / 2» ko'rinadi; «Oxirgi chek» darrov chop etiladi.
- **Sekin serverda pul amallari.** Qaytarish, almashtirish, qarz to'lovi, ish haqi to'lovi va ko'chirish oynani 8 soniyadan ortiq ushlab turmaydi; server javob bermaguncha o'sha amalni ikkinchi marta bosib bo'lmaydi.
- **Ishonchliroq.** «Qarzga» sotuv so'rov aniq yuborilmagan bo'lsagina oflayn-qarzga ketadi; aloqa uzilganda ombor qoldiqni ikki marta qo'shmaydi.
- **Ishga tushirish ekrani.** Server sekin javob bersa, 15 soniyadan keyin buni tushuntiradi va «Chiqish» tugmasini beradi.
- **Dasturni yopish.** O'rnatuvchi yoki Windows o'chirilganda «Smena yopilmagan» savolida qotib qolmaydi; smena ochiq qoladi.
- **X-hisobot va cheklar tarixi.** «Tuzatish» faqat haqiqiy farq bo'lganda chiqadi; aloqasiz tarixda haqiqiy chek raqamlari va chegirmali summalar ko'rinadi.
- **Mahsulotni «1C'dagidek» o'chirish.** Qoldiq yo'qolmaydi: to'liq hisobdan chiqarilmasa, mahsulot qoladi, kassa qancha qolganini yozadi. Mahsulotlar arxivi tezroq yuklanadi, «Yuklashni bekor qilish» va qidiruv bor.
- **Yetkazib beruvchiga qaytarish yuk xati.** Chop etish, PDF, Word, Excel: serverdagi raqam, yetkazib beruvchi, asos bo'lgan kirim, summalar, summa yozuvda, imzo joylari.
- **Investitsiyalar va o'zini oqlash.** Sarmoyalar sana bo'yicha hisoblanadi, hamma joyda bitta oqlanish muddati, bir nechta investorda to'lovlar qaydnomasi to'g'ri.
- **Egasining dasturi.** Filtrni almashtirgandan keyin «saqlanmagan o'zgarishlar» deb so'ramaydi; ekran o'quvchi tugmalarni o'qiydi; sekin serverda «Server javobi kutilmoqda… N s» ko'rinadi.
- **Nur Maslahatchi va SI qo'ng'irog'i.** «Qirg'izcha javob ber», «ruscha javob ber» buyruqlarini tushunadi; qo'ng'iroq o'zi boshqa tilga o'tmaydi.
- **Yuk xati: aqlli solishtirish.** Qisqartmalar («Mol.»), «1l» = «1 litr» = «1000 ml», lotin va kirill; o'xshash mahsulotlar 5 tagacha ro'yxat bilan; kassa yetkazib beruvchi bo'yicha tanlovni eslab qoladi; «SI bilan tanlash» faqat tasdiq bilan.
- **Rasm va qo'lda yozilgan yuk xati.** SI omboringizdagi nomlarga tayanadi, ishonchsiz qatorlar «rasm bo'yicha tekshiring» deb belgilanadi.
- **Rasmlarni guruhlab qidirish.** Nur Maslahatchi 20 yoki 50 ta mahsulot rasmini qidiradi, jarayonni eslab qoladi, ishonchsiz mos kelishlar alohida «tekshiring» belgisi bilan chiqadi.
- **Android.** Fayllar (PDF, Word, Excel) tanlangan joyga saqlanadi; fonga o'tish yoki kamera dasturni yopmaydi; Bluetooth orqali chek ikki marta chop etilmaydi.
