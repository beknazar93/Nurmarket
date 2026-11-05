## NurCRM Mobile 1.18.17

Файл: `nurmarket-mobile-1.18.17.apk` (Android 8.0 и новее). Ставится поверх 1.18.x — подпись та же, вход сохраняется.

### Русский

**Что нового по сравнению с 1.18.16**
- **Чек по Bluetooth не печатается дважды.** Если принтер долго подключается, телефон больше не ставит второй чек вдогонку: ждёт ответ Android, а при потере связи говорит «проверьте, напечатался ли чек». На экране «Оплачено» после неудачной печати остаётся красная плашка «Чек не напечатан» с кнопкой «Повторить печать». В настройках принтера Bluetooth сразу показаны сопряжённые устройства (единственный принтер выбирается сам); на Android 10–11 с выключенным «Местоположением» поиск подсказывает его включить.
- **Весовая этикетка находит только весовой товар** (как на кассе): штучная «Жвачка» с артикулом 12 больше не подменяет «Картофель» с PLU 12. «Код товара» хранится отдельно от артикула. Этикетка «по сумме» у товара без цены — вес вписывается вручную. Неизвестный PLU: вместо «Добавить товар со штрихкодом этикетки» — «Выбрать товар и записать ему PLU». Одна и та же этикетка в кадре не ложится в чек сама второй раз (спрашивает); один сигнал вместо двух; если раскладка этикеток не задана и она читается двумя способами — выбор из двух товаров.
- **Умная камера:** окно «Узнаю товар…» показывает секунды, через 6 с — «ИИ думает дольше обычного…», есть кнопка «Отмена». В продаже штрихкод читается прямо со снимка (товар находится сразу, без ИИ); при уверенном ответе ИИ тяжёлая вторая сверка не запускается. Индекс сопоставления строится в отдельном потоке и не сбрасывается после каждой продажи. Кассиру без ключа ИИ — «ИИ не настроен — попросите владельца».
- **Скорость:** касание плитки не перечитывает список товаров; окно «Заканчивается» открывается сразу даже на большом складе; весы по камере видят все весовые товары, а не первые 3000 по алфавиту. «Добавить товар» для неизвестного штрихкода — после второго одинакового прочтения.
- **Чек и сдача — по ответу сервера;** в чеке появилась строка «Округление весовых».
- **База:** схема 10 — колонка «код товара» (заполняется при ближайшей полной загрузке каталога).
- **Во время оплаты чек нельзя отложить или очистить.** Команды ИИ («отложи чек», «очисти», «верни отложенный») и ручное «Вернуть отложенный» теперь ждут конца оплаты. ИИ честно отвечает «чек сейчас оплачивается» и не пишет в журнал «Удалено из чека» то, чего не было.
- **«Остаток по системе» при открытии смены** — как в кассе: подсказка суммы из последней закрытой смены; если вводите 0 при деньгах в ящике, приложение спросит. Повтор после обрыва связи («смена уже открыта») сам обновляет главную.
- **Закупочная цена — до 4 знаков** (0,1234 не превращается в 0,12 при правке цены на телефоне).
- **Приход без поставщика не прибавляется дважды:** метка начатой прибавки хранится на телефоне 24 часа и переживает закрытие экрана.
- **Резервная копия Android отключена** (`allowBackup=false`): база с очередью чеков не попадает в облако Google.
- **Сборка выпуска без ключа подписи теперь останавливается** с понятным сообщением (раньше молча подписывалась отладочным ключом).
- **Язык ИИ задан, а не угадывается.** В звонке, видеозвонке и чате ИИ говорит на языке приложения (русский или кыргызский) и не уходит на узбекский или турецкий. Язык меняется только настройкой или явной командой: «кыргызча жооп бер», «кыргызча сүйлө», «отвечай по-кыргызски», «переведи на кыргызский», «говори по-русски», «орусча сүйлө». Команда «…жооп бер / сүйлө / говори / отвечай» меняет только язык ИИ, «переведи / переключи / смени» — язык приложения и ИИ. Звонок при этом не обрывается.
- **Языковой пакет.** Английский, турецкий и узбекский — платные: без пакета «Язык» в списках языков они закрыты («Языковой пакет не подключён»), ИИ отвечает только по-русски и по-кыргызски, а сохранённый платный язык при запуске возвращается на русский. С подключённым пакетом всё работает как раньше.

### Кыргызча

**1.18.16 менен салыштырганда эмне жаңы**
- **Bluetooth аркылуу чек эки жолу басылбайт.** Принтер узак туташса, телефон кошумча экинчи чекти койбойт; байланыш үзүлсө — «чек басылганын текшериңиз». «Төлөндү» экранында басуу ишке ашпаса, «Чек басылган жок» плашкасы жана «Кайра басуу» баскычы калат. Принтер жөндөөлөрүндө Bluetooth жупташкан түзмөктөрдү дароо көрсөтөт.
- **Таразанын этикеткасы салмактуу товарды гана табат** (кассадагыдай); белгисиз PLU үчүн «Товарды тандап, ага PLU жазуу». Бир эле этикетка чекке өзү экинчи жолу түшпөйт (сурайт); бир сигнал.
- **Акылдуу камера:** «Товарды таанып жатам…» терезесинде секундалар жана «Баш тартуу» баскычы бар; сатууда штрихкод сүрөттөн түз окулат, ИИ керек эмес. Индекс өзүнчө агымда курулат.
- **ылдамдык:** плитка басылганда тизме кайра окулбайт; «Түгөнүп жатат» терезеси дароо ачылат. Чек жана кайтарым — сервердин жообу боюнча, чекте «Салмактуулардын тегеректелиши» саптары чыкты.
- **Төлөм учурунда чекти кое туруу же тазалоо мүмкүн эмес.** ИИ буйруктары («чекти кое тур», «тазала») жана кол менен «Кечиктирилгенди кайтаруу» төлөм бүткөнчө күтөт. ИИ чынчыл жооп берет: «чек азыр төлөнүүдө».
- **Смена ачууда «Системадагы калдык»** — кассадагыдай: акыркы жабылган сменанын суммасы көрсөтүлөт; кассада акча бар болуп, 0 киргизсеңиз, колдонмо сурайт.
- **Сатып алуу баасы — 4 белгиге чейин** (0,1234 телефондогу баа оңдолгондо 0,12 болуп калбайт).
- **Жеткирүүчүсүз кирим эки жолу кошулбайт:** кошуунун белгиси телефондо 24 саат сакталат.
- **Android резервдик көчүрмөсү өчүрүлдү:** чектер кезегинин базасы Google булутуна түшпөйт.
- **Кол тамгасыз чыгарылыш куруу эми токтойт** (мурун сыноо ачкычы менен тымызын кол коюлчу).
- **ИИ тили бекитилген, божомолдонбойт.** Чалууда, видеочалууда жана чатта ИИ колдонмонун тилинде (орусча же кыргызча) сүйлөйт, өзбекчеге же түркчөгө өтпөйт. Тил жөндөө менен же ачык буйрук менен гана өзгөрөт: «кыргызча жооп бер», «кыргызча сүйлө», «орусча сүйлө», «кыргызчага өт».
- **Тил топтому.** Англисче, түркчө жана өзбекче — акы төлөнүүчү: топтомсуз тилдер тизмесинде жабык («Тил топтому туташтырылган эмес»), ИИ орусча жана кыргызча гана жооп берет.

### English

**What's new since 1.18.16**
- **A Bluetooth receipt is no longer printed twice.** If the printer connects slowly the phone no longer queues a second receipt behind the first; when the link drops it says “check whether the receipt printed”. After a failed print the “Paid” screen keeps a red “Receipt not printed” banner with “Print again”. In printer settings paired Bluetooth devices are shown at once.
- **A scale label finds only weighed products** (like the till): a piece-priced “Gum” with article 12 no longer replaces “Potatoes” with PLU 12. An unknown PLU offers “Pick a product and assign this PLU” instead of “Add a product with the label barcode”. The same label in view is not added to the receipt a second time on its own (it asks); one beep instead of two.
- **Smart camera:** the “Recognising…” window shows seconds and a Cancel button; in a sale the barcode is read straight from the shot (no AI call); the index is built off the main thread.
- **Speed:** tapping a tile no longer re-reads the product list; “Running low” opens at once. The receipt and change follow the server response, and the receipt has a “Weighed rounding” line.
- **The cart cannot be held or cleared while it is being paid.** AI commands (“hold the cart”, “clear it”, “bring back the held one”) and the manual “restore held cart” now wait for the payment to finish. The AI answers honestly (“the cart is being paid right now”) and no longer logs deletions that did not happen.
- **“System balance” when opening a shift**, like the till: the amount of the last closed shift is shown; entering 0 while the drawer holds money asks for confirmation.
- **Purchase price keeps up to 4 decimals** (0.1234 no longer becomes 0.12 when a price is edited on the phone).
- **Receiving without a supplier is not added twice:** the mark of a started increase is kept on the phone for 24 hours.
- **Android backup is off** (`allowBackup=false`): the receipt queue database does not go to the Google cloud.
- **A release build without the signing key now stops** with a clear message (it used to be silently signed with the debug key).
- **The AI language is set, not guessed.** In calls, video calls and chat the AI speaks the app language (Russian or Kyrgyz) and does not drift into Uzbek or Turkish. The language changes only by the setting or an explicit command (“answer in Kyrgyz”, “speak Russian”, “switch to Kyrgyz”).
- **Language pack.** English, Turkish and Uzbek are paid: without the “Language” pack they are locked in the language lists (“Language pack is not connected”) and the AI answers only in Russian and Kyrgyz.

### Türkçe

**1.18.16'ya göre yenilikler**
- **Bluetooth fişi iki kez basılmaz.** Yazıcı geç bağlanırsa telefon ikinci fişi kuyruğa koymaz; bağlantı kopunca «fişin basılıp basılmadığını kontrol edin» der. Başarısız yazdırmadan sonra «Ödendi» ekranında «Fiş yazdırılmadı» uyarısı ve «Tekrar yazdır» düğmesi kalır.
- **Terazi etiketi yalnız tartılan ürünü bulur** (kasadaki gibi); bilinmeyen PLU için «Ürün seçip bu PLU'yu ata». Aynı etiket kendiliğinden ikinci kez eklenmez (sorar); tek ses.
- **Akıllı kamera:** «Ürün tanınıyor…» penceresinde saniyeler ve İptal düğmesi var; satışta barkod doğrudan fotoğraftan okunur; dizin ayrı iş parçacığında kurulur.
- **Hız:** karoya dokunmak listeyi yeniden okumaz; «Azalanlar» hemen açılır. Fiş ve para üstü sunucu yanıtına göre, fişte «Tartılı yuvarlama» satırı var.
- **Ödeme sırasında fiş askıya alınamaz veya temizlenemez.** YZ komutları ve elle «Askıdakini getir» ödeme bitene kadar bekler; YZ dürüstçe «fiş şu anda ödeniyor» der.
- **Vardiya açarken «Sistem bakiyesi»** kasadaki gibi: son kapanan vardiyanın tutarı gösterilir; çekmecede para varken 0 girilirse onay istenir.
- **Alış fiyatı 4 basamağa kadar** korunur.
- **Tedarikçisiz giriş iki kez eklenmez:** başlayan artışın işareti telefonda 24 saat saklanır.
- **Android yedeği kapatıldı** (`allowBackup=false`).
- **İmza anahtarsız sürüm derlemesi artık durur** (eskiden sessizce hata ayıklama anahtarıyla imzalanırdı).
- **YZ dili belirlenir, tahmin edilmez.** Aramalarda ve sohbette YZ uygulama dilinde (Rusça veya Kırgızca) konuşur; Özbekçe veya Türkçeye geçmez. Dil yalnızca ayarla veya açık komutla değişir.
- **Dil paketi.** İngilizce, Türkçe ve Özbekçe ücretlidir: «Dil» paketi olmadan listelerde kilitlidir («Dil paketi bağlı değil»), YZ yalnızca Rusça ve Kırgızca yanıt verir.

### O'zbekcha

**1.18.16 dan keyingi yangiliklar**
- **Bluetooth chek ikki marta chop etilmaydi.** Printer sekin ulansa, telefon ikkinchi chekni qo'shmaydi; aloqa uzilsa «chek chop etilganini tekshiring» deydi. «To'landi» ekranida chop etilmasa, «Chek chop etilmadi» belgisi va «Qayta chop etish» tugmasi qoladi.
- **Tarozi yorlig'i faqat tarozida tortiladigan tovarni topadi** (kassadagidek); noma'lum PLU uchun «Tovarni tanlab, unga PLU yozish». Bir xil yorliq o'zi ikkinchi marta qo'shilmaydi (so'raydi); bitta signal.
- **Aqlli kamera:** «Tovar aniqlanmoqda…» oynasida soniyalar va «Bekor qilish» tugmasi bor; sotuvda shtrix-kod to'g'ridan-to'g'ri suratdan o'qiladi; indeks alohida oqimda quriladi.
- **Tezlik:** plitkaga tegish ro'yxatni qayta o'qimaydi; «Tugayapti» darrov ochiladi. Chek va qaytim server javobi bo'yicha, chekda «Tortiladigan tovarlarni yaxlitlash» qatori bor.
- **To'lov paytida chekni kechiktirib yoki tozalab bo'lmaydi.** AI buyruqlari va qo'lda «Kechiktirilganni qaytarish» to'lov tugashini kutadi; AI rostini aytadi: «chek hozir to'lanmoqda».
- **Smena ochishda «Tizim qoldig'i»** kassadagidek: oxirgi yopilgan smena summasi ko'rsatiladi; qutida pul bo'lib 0 kiritilsa, tasdiq so'raladi.
- **Xarid narxi 4 belgigacha** saqlanadi.
- **Yetkazib beruvchisiz kirim ikki marta qo'shilmaydi:** boshlangan qo'shimcha belgisi telefonda 24 soat saqlanadi.
- **Android zaxira nusxasi o'chirildi** (`allowBackup=false`).
- **Imzo kalitisiz chiqarish yig'ish endi to'xtaydi** (avval jimgina sinov kaliti bilan imzolanardi).
- **AI tili belgilangan, taxmin qilinmaydi.** Qo'ng'iroq va chatda AI ilova tilida (ruscha yoki qirg'izcha) gapiradi, o'zbekcha yoki turkchaga o'tmaydi. Til faqat sozlama yoki aniq buyruq bilan o'zgaradi.
- **Til paketi.** Inglizcha, turkcha va o'zbekcha pullik: «Til» paketisiz ro'yxatda yopiq («Til paketi ulanmagan»), AI faqat ruscha va qirg'izcha javob beradi.
