## v1.17.15

---

# Русский

- Исправлено: оплата не проходила с ошибкой «Смена не открыта», хотя смена была открыта — касса садилась на чужую смену или на смену этого кассира на другой кассе. Теперь касса работает только в своей смене; если ваша смена открыта на другой кассе, касса переходит на неё или прямо говорит, где её закрыть
- Исправлено: касса иногда переставала печатать чеки до перезапуска — когда принтер «зависал» (кончилась бумага, открыта крышка, уснул), печать ждала бесконечно. Теперь ожидание ограничено, следующий чек печатается сразу
- Печать через принтер Windows: касса проверяет, что чек действительно ушёл на принтер. Если принтер выключен или без бумаги, кассир сразу видит «чек не напечатан», а чек не выйдет сам через час. Режим «Работать автономно» и пауза очереди снимаются автоматически
- Внесение и изъятие денег — отдельная кнопка в меню под сменой; операции уходят на сервер и видны в итогах смены
- Приёмка на складе: товары можно сканировать подряд без паузы, прямо в таблице править название, количество и цены; сканер работает, даже когда курсор стоит в поле
- Быстрое сканирование в чек: товары, отсканированные подряд, больше не теряются
- Продажи: чек открывается одним нажатием, в списке видны товары каждого чека
- Массовая загрузка товаров из файла: каждая колонка проверяется до загрузки, при перегрузке сервера загрузка повторяется сама, без дублей
- Продажи без интернета надёжнее досылаются на сервер — без повторов и потерь; весовой товар защищён от случайного ввода огромного веса или суммы

---

# Кыргызча

- Оңдолду: смена ачык турса да төлөм «Смена ачылган эмес» деген ката менен өтпөй калчу — касса башка кассирдин сменасына же ушул кассирдин башка кассадагы сменасына отуруп калчу. Эми касса өз сменаңызда гана иштейт; сменаңыз башка кассада ачык болсо, касса ага өтөт же аны кайда жабуу керектигин ачык айтат
- Оңдолду: касса кээде кайра күйгүзгөнгө чейин чек чыгарбай калчу — принтер «катып» калганда (кагаз бүтсө, капкагы ачык калса, уктап калса) басып чыгаруу чексиз күтчү. Эми күтүү чектелген, кийинки чек дароо басылат
- Windows принтери аркылуу басуу: касса чек чындап принтерге жеткенин текшерет. Принтер өчүк же кагазы жок болсо, кассир дароо «чек басылган жок» деп көрөт, чек бир сааттан кийин өзү чыгып калбайт. «Автономдуу иштөө» режими жана кезектин тындыруусу автоматтык түрдө алынат
- Акча салуу жана алуу — менюда сменанын астында өзүнчө баскыч; операциялар серверге жөнөтүлүп, сменанын жыйынтыгында көрүнөт
- Кампага кабыл алуу: товарларды тыныгуусуз удаа сканерлесе болот, таблицанын ичинде аталышын, санын жана бааларын оңдосо болот; курсор талаада турса да сканер иштейт
- Чекке тез сканерлөө: удаа сканерленген товарлар мындан ары жоголбойт
- Сатуулар: чек бир басуу менен ачылат, тизмеде ар бир чектин товарлары көрүнөт
- Товарларды файлдан массалык жүктөө: жүктөөдөн мурун ар бир мамыча текшерилет, сервер ашыкча жүктөлсө жүктөө өзү кайталанат, кайталанган товарларсыз
- Интернетсиз сатуулар серверге ишенимдүүрөөк жөнөтүлөт — кайталоосуз жана жоготуусуз; салмактуу товар кокустан өтө чоң салмак же сумма киргизүүдөн корголгон

---

# English

- Fixed: payment failed with “Shift is not open” even though the shift was open — the register attached itself to another cashier's shift or to this cashier's shift on another register. Now the register works only in your own shift; if your shift is open on another register, it switches to it or tells you exactly where to close it
- Fixed: the register sometimes stopped printing receipts until restart — when the printer froze (out of paper, cover open, asleep), printing waited forever. Now the wait is limited and the next receipt prints right away
- Printing through a Windows printer: the register checks that the receipt really reached the printer. If the printer is off or out of paper, the cashier immediately sees “receipt not printed”, and the receipt will not come out on its own an hour later. “Use printer offline” and a paused queue are cleared automatically
- Cash in and cash out — a separate button in the menu under the shift; operations are sent to the server and appear in the shift totals
- Warehouse receiving: scan items one after another without pauses and edit name, quantity and prices right in the table; the scanner works even when the cursor is in a field
- Fast scanning into the receipt: items scanned in a row are no longer lost
- Sales: a receipt opens with one tap, and the list shows the items of each receipt
- Bulk product import from a file: every column is checked before upload, upload retries on its own when the server is overloaded, no duplicates
- Sales made without internet are sent to the server more reliably — no duplicates or losses; weighed items are protected from accidentally entering a huge weight or amount

---

# Türkçe

- Düzeltildi: vardiya açık olmasına rağmen ödeme “Vardiya açık değil” hatasıyla geçmiyordu — kasa başka bir kasiyerin vardiyasına ya da bu kasiyerin başka kasadaki vardiyasına bağlanıyordu. Artık kasa yalnızca kendi vardiyanızda çalışır; vardiyanız başka bir kasada açıksa ona geçer ya da nerede kapatacağınızı açıkça söyler
- Düzeltildi: kasa bazen yeniden başlatılana kadar fiş yazdırmayı bırakıyordu — yazıcı takıldığında (kağıt bitti, kapak açık, uyku modu) yazdırma sonsuza kadar bekliyordu. Artık bekleme sınırlı, sonraki fiş hemen yazdırılır
- Windows yazıcısıyla yazdırma: kasa fişin gerçekten yazıcıya ulaştığını kontrol eder. Yazıcı kapalıysa veya kağıdı yoksa kasiyer hemen “fiş yazdırılmadı” uyarısını görür ve fiş bir saat sonra kendiliğinden çıkmaz. “Yazıcıyı çevrimdışı kullan” modu ve duraklatılmış kuyruk otomatik kaldırılır
- Para girişi ve çıkışı — menüde vardiyanın altında ayrı bir düğme; işlemler sunucuya gönderilir ve vardiya toplamlarında görünür
- Depo mal kabulü: ürünleri ara vermeden art arda okutabilir, adı, miktarı ve fiyatları doğrudan tabloda düzeltebilirsiniz; imleç bir alandayken de barkod okuyucu çalışır
- Fişe hızlı okutma: art arda okutulan ürünler artık kaybolmuyor
- Satışlar: fiş tek dokunuşla açılır, listede her fişin ürünleri görünür
- Dosyadan toplu ürün yükleme: yüklemeden önce her sütun kontrol edilir, sunucu yoğunsa yükleme kendiliğinden tekrarlanır, mükerrer kayıt olmaz
- İnternetsiz yapılan satışlar sunucuya daha güvenilir şekilde gönderilir — tekrar ve kayıp olmadan; tartılı ürünlerde yanlışlıkla çok büyük ağırlık veya tutar girilmesine karşı koruma var

---

# O'zbekcha

- Tuzatildi: smena ochiq bo'lsa ham to'lov «Smena ochilmagan» xatosi bilan o'tmayotgan edi — kassa boshqa kassirning smenasiga yoki shu kassirning boshqa kassadagi smenasiga ulanib qolardi. Endi kassa faqat o'z smenangizda ishlaydi; smenangiz boshqa kassada ochiq bo'lsa, kassa unga o'tadi yoki uni qayerda yopish kerakligini aniq aytadi
- Tuzatildi: kassa ba'zan qayta ishga tushirilguncha chek chop etmay qo'yardi — printer qotib qolganda (qog'oz tugasa, qopqog'i ochiq qolsa, uyqu rejimida) chop etish cheksiz kutardi. Endi kutish cheklangan, keyingi chek darhol chop etiladi
- Windows printeri orqali chop etish: kassa chek haqiqatan printerga yetganini tekshiradi. Printer o'chiq yoki qog'ozi yo'q bo'lsa, kassir darhol «chek chop etilmadi» xabarini ko'radi, chek bir soatdan keyin o'zi chiqib qolmaydi. «Printerdan avtonom foydalanish» rejimi va navbat pauzasi avtomatik olib tashlanadi
- Pul kiritish va olish — menyuda smena ostida alohida tugma; amallar serverga yuboriladi va smena yakunlarida ko'rinadi
- Omborga qabul qilish: mahsulotlarni to'xtovsiz ketma-ket skanerlash, nomi, soni va narxlarini to'g'ridan-to'g'ri jadvalda tuzatish mumkin; kursor maydonda turganda ham skaner ishlaydi
- Chekka tez skanerlash: ketma-ket skanerlangan mahsulotlar endi yo'qolmaydi
- Sotuvlar: chek bir bosishda ochiladi, ro'yxatda har bir chekdagi mahsulotlar ko'rinadi
- Fayldan mahsulotlarni ommaviy yuklash: yuklashdan oldin har bir ustun tekshiriladi, server band bo'lsa yuklash o'zi qayta urinadi, takrorlarsiz
- Internetsiz qilingan sotuvlar serverga ishonchliroq yuboriladi — takror va yo'qotishlarsiz; tortiladigan mahsulot tasodifan juda katta og'irlik yoki summa kiritishdan himoyalangan
