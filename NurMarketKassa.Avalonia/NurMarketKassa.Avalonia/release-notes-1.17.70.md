## v1.17.70

Накладная прихода и редактор закупок, весовые товары в приёмке, документы перемещения, работа без интернета, новое окно оплаты и многое другое.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.70.tar.gz`; macOS — `nurmarket-kassa-macos-arm64-1.17.70.zip` (Apple Silicon) и `nurmarket-kassa-macos-x64-1.17.70.zip` (Intel); Android — `nurmarket-kassa-android-1.17.70.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Накладная прихода.** Новая форма и редактор: правку принятой накладной можно разрешить на 24 часа, 48 часов или всегда. Закупки получают номера ПР-…, все закупки — в реестре «Покупки», нового поставщика можно создать прямо в окне «Оприходовать». Правка накладной надёжна и при обрыве связи.
- **Весовые товары в приёмке.** У нового товара есть выбор единицы (шт, кг, л, м, уп): «кг» создаёт весовой товар, количество можно принимать дробным. Этикетка весов находит товар и ставит вес. Количество и цена закупки не округляются молча, остаток весового товара показывается до 3 знаков.
- **Нур Советник.** Закупка по фото накладной со сверкой с итогом (НДС, скидка, число позиций), подсвеченные подозрительные строки, настройка того, что Советнику разрешено, списки склада в ответах.
- **Склад → Перемещение → «Документы».** Кнопка «Создать документ» формирует накладную на внутреннее перемещение, акт приёма-передачи и транспортный документ; при «Отправить» накладная создаётся сама.
- **Склад.** Сортировка по заголовкам столбцов, вкладка «Поступления» (от кого, когда и по какой накладной пришёл товар), каталог до 15 тысяч товаров и быстрая массовая загрузка, понятное окно вместо ошибки при удалении товара с историей.
- **Окно оплаты по-новому.** Слева итог, сдача и клиент, справа способы оплаты в ряд; клавиши F1–F7. Окна и диалоги перетаскиваются за шапку и не выходят за экран.
- **Работа без интернета.** Продажа «в долг», возврат и оплата долга ждут в очереди и уходят сами; чек записывается на диск до отправки и не теряется при сбое питания; двойная продажа исключена. Вместо «нет связи» при перегрузке сервера — «Сервер занят, повтор через N с».
- **Синхронизация.** Цены и остатки доходят до других касс за 1–2 секунды.
- **Платные функции из Маркетплейса** включаются с сервера без ключа; серийные ключи работают как раньше.
- **Тех поддержка** не блокирует кассу; при истёкшей сессии — одно окно «Сессия истекла. Войдите снова».
- **Долги и клиенты.** Окно «Долги клиентов» открывается без подвисания, оплата долга показывает ход выполнения.
- **Воронка.** Кнопка «Открыть» открывает переписку с клиентом (Telegram или WhatsApp), у сделки есть «Карточка».
- **Программа владельца.** Сводка по периодам без «чужих» цифр, симулятор окупаемости инвестора, закрытые окна не занимают память.
- **Linux и macOS.** Печать чеков на Linux, автозапуск и ярлыки; касса для Mac (Apple Silicon и Intel).
- **Название.** Программа называется NurCRM («Нур СРМ»); меньше нагрузки на процессор и батарею, установка легче на 66 МБ.

---

# Кыргызча

### Эмнелер жаңы

- **Кирүү накладнойу.** Жаңы форма жана редактор: кабыл алынган накладнойду оңдоого 24 саатка, 48 саатка же ар дайым уруксат берсе болот. Сатып алуулар ПР-… номерин алат, бардык сатып алуулар «Сатып алуулар» реестринде, жаңы жеткирүүчүнү түз эле «Кабыл алуу» терезесинде түзсө болот. Байланыш үзүлгөндө да накладнойду оңдоо ишенимдүү.
- **Кабыл алууда салмактуу товарлар.** Жаңы товарда бирдик тандоо бар (шт, кг, л, м, уп): «кг» салмактуу товар түзөт, саны бөлчөк болушу мүмкүн. Таразанын этикеткасы товарды табып, салмакты коёт. Саны жана сатып алуу баасы унчукпай тегизделбейт, салмактуу товардын калдыгы 3 цифрага чейин көрсөтүлөт.
- **Нур Кеңешчи.** Накладнойдун сүрөтү боюнча сатып алуу жыйынтык менен салыштырылат (КНС, арзандатуу, позиция саны), шектүү саптар белгиленет, Кеңешчиге эмнеге уруксат берилгенин тууралоо, жоопторда кампа тизмелери.
- **Кампа → Жылдыруу → «Документтер».** «Документ түзүү» баскычы ички жылдыруу накладнойун, кабыл алуу-өткөрүп берүү актын жана транспорттук документти түзөт; «Жөнөтүү» басылганда накладной өзү түзүлөт.
- **Кампа.** Тилкелердин аталышы боюнча иреттөө, «Түшүүлөр» өтмөгү (товар кимден, качан, кайсы накладной менен келген), 15 миң товарга чейинки каталог жана тез жапырт жүктөө, тарыхы бар товарды өчүрүүдө ката ордуна түшүнүктүү терезе.
- **Төлөм терезеси жаңыча.** Сол жакта жыйынтык, кайтарым жана кардар, оң жакта төлөм ыкмалары катар менен; F1–F7 баскычтары. Терезелер жана диалогдор баш жагынан сүйрөлөт жана экрандан чыкпайт.
- **Интернетсиз иштөө.** «Карызга» сатуу, кайтарып алуу жана карызды төлөө кезекте күтүп, өзү жөнөтүлөт; чек жөнөтүүдөн мурун дискке жазылат жана электр өчкөндө жоголбойт; кош сатуу болбойт. Сервер жүктөлгөндө «байланыш жок» ордуна «Сервер бош эмес, N с кийин кайталайт».
- **Синхрондоштуруу.** Баалар жана калдыктар башка кассаларга 1–2 секундада жетет.
- **Маркетплейстен акы төлөнүүчү функциялар** ачкычсыз сервер аркылуу күйгүзүлөт; сериялык ачкычтар мурункудай иштейт.
- **Тех колдоо** кассаны бөгөттөбөйт; сессия бүткөндө — бир терезе «Сессия бүттү. Кайра кириңиз».
- **Карыздар жана кардарлар.** «Кардарлардын карыздары» терезеси катпай ачылат, карызды төлөөдө жүрүш көрүнөт.
- **Воронка.** «Ачуу» баскычы кардар менен жазышууну ачат (Telegram же WhatsApp), келишимде «Карточка» бар.
- **Ээсинин программасы.** Жыйынтык башка мезгилдин сандарын көрсөтпөйт, инвестордун кайтарым симулятору, жабылган терезелер эс тутумду ээлебейт.
- **Linux жана macOS.** Linux'та чек басуу, автоишке киргизүү жана ярлыктар; Mac үчүн касса (Apple Silicon жана Intel).
- **Аталышы.** Программа NurCRM («Нур СРМ») деп аталат; процессорго жана батареяга жүк азайды, орнотуу 66 МБ жеңилирээк.

---

# English

### What's new

- **Goods receipt invoice.** A new form and editor: editing an accepted invoice can be allowed for 24 hours, 48 hours or always. Purchases get numbers like ПР-…, all purchases are in the “Purchases” register, and a new supplier can be created right in the “Receive” window. Invoice editing stays reliable even if the connection drops.
- **Weighed goods on receiving.** A new product has a unit picker (pcs, kg, l, m, pack): “kg” creates a weighed product and the quantity can be fractional. A scale label finds the product and sets the weight. Quantity and purchase price are not rounded silently, and the stock of a weighed product is shown with up to 3 decimals.
- **Nur Advisor.** Purchase from an invoice photo with a cross-check against the invoice total (VAT, discount, item count), suspicious lines highlighted, a setting for what the Advisor may do, and warehouse lists in its answers.
- **Warehouse → Transfer → “Documents”.** The “Create document” button builds the internal transfer waybill, the handover certificate and the transport document; on “Send” the waybill is created automatically.
- **Warehouse.** Sorting by column headers, the “Receipts” tab (from whom, when and on which invoice a product arrived), a catalogue of up to 15 thousand products with fast bulk upload, and a clear window instead of an error when deleting a product that has history.
- **New payment window.** The total, change and client on the left, payment methods in a row on the right; keys F1–F7. Windows and dialogs can be dragged by the header and stay on screen.
- **Working offline.** A sale “on credit”, a return and a debt payment wait in a queue and go out by themselves; a receipt is saved to disk before it is sent and is not lost on a power failure; a double sale is ruled out. When the server is busy you see “Server busy, retrying in N s” instead of “no connection”.
- **Sync.** Prices and stock reach the other tills in 1–2 seconds.
- **Paid Marketplace features** are switched on from the server without a key; serial keys work as before.
- **Tech support** no longer blocks the till; when the session expires you get one window, “Session expired. Sign in again”.
- **Debts and clients.** The “Customer debts” window opens without freezing, and paying a debt shows progress.
- **Funnel.** The “Open” button opens the conversation with the client (Telegram or WhatsApp); a deal has a “Details” button.
- **Owner program.** The summary no longer shows numbers from another period, an investor payback simulator, and closed windows no longer stay in memory.
- **Linux and macOS.** Receipt printing on Linux, autostart and shortcuts; the till for Mac (Apple Silicon and Intel).
- **Name.** The program is called NurCRM; lower CPU and battery load, and the install is 66 MB lighter.

---

# Türkçe

### Yenilikler

- **Mal giriş irsaliyesi.** Yeni form ve düzenleyici: kabul edilmiş irsaliyenin düzenlenmesine 24 saat, 48 saat veya her zaman izin verilebilir. Alımlar ПР-… numarası alır, tüm alımlar «Alımlar» kaydındadır, yeni tedarikçi doğrudan «Mal girişi» penceresinde oluşturulabilir. Bağlantı kesilse bile irsaliye düzenleme güvenilirdir.
- **Kabulde tartılı ürünler.** Yeni üründe birim seçimi var (adet, kg, l, m, paket): «kg» tartılı ürün oluşturur, miktar ondalık olabilir. Terazi etiketi ürünü bulur ve ağırlığı yazar. Miktar ve alış fiyatı sessizce yuvarlanmaz, tartılı ürün stoku 3 basamağa kadar gösterilir.
- **Nur Danışman.** Fatura fotoğrafından alım, fatura toplamıyla karşılaştırılır (KDV, indirim, kalem sayısı), şüpheli satırlar işaretlenir, Danışman'a nelere izin verildiği ayarlanır, yanıtlarda depo listeleri bulunur.
- **Depo → Transfer → «Belgeler».** «Belge oluştur» düğmesi iç transfer irsaliyesini, teslim tutanağını ve taşıma belgesini hazırlar; «Gönder» ile irsaliye otomatik oluşur.
- **Depo.** Sütun başlıklarına göre sıralama, «Girişler» sekmesi (ürün kimden, ne zaman, hangi irsaliyeyle geldi), 15 bine kadar ürünlük katalog ve hızlı toplu yükleme, geçmişi olan ürün silinirken hata yerine anlaşılır pencere.
- **Yeni ödeme penceresi.** Solda toplam, para üstü ve müşteri, sağda ödeme yöntemleri yan yana; F1–F7 tuşları. Pencereler ve diyaloglar başlıktan sürüklenir ve ekrandan çıkmaz.
- **İnternetsiz çalışma.** «Veresiye» satış, iade ve borç ödemesi sırada bekler ve kendiliğinden gönderilir; fiş gönderilmeden önce diske yazılır ve elektrik kesintisinde kaybolmaz; çift satış olmaz. Sunucu yoğunken «bağlantı yok» yerine «Sunucu meşgul, N sn sonra tekrar» görünür.
- **Eşitleme.** Fiyat ve stok diğer kasalara 1–2 saniyede ulaşır.
- **Pazaryerinin ücretli özellikleri** anahtarsız, sunucudan açılır; seri anahtarlar eskisi gibi çalışır.
- **Teknik destek** kasayı kilitlemez; oturum bitince tek pencere: «Oturum bitti. Yeniden giriş yapın».
- **Borçlar ve müşteriler.** «Müşteri borçları» penceresi donmadan açılır, borç ödemesinde ilerleme görünür.
- **Huni.** «Aç» düğmesi müşteriyle yazışmayı açar (Telegram veya WhatsApp), anlaşmada «Kart» düğmesi vardır.
- **Sahip programı.** Özet başka dönemin rakamlarını göstermez, yatırımcı geri dönüş simülatörü, kapatılan pencereler bellekte kalmaz.
- **Linux ve macOS.** Linux'ta fiş yazdırma, otomatik başlatma ve kısayollar; Mac için kasa (Apple Silicon ve Intel).
- **Ad.** Program NurCRM adını taşır; işlemci ve pil yükü azaldı, kurulum 66 MB hafifledi.

---

# O'zbekcha

### Yangiliklar

- **Kirim yuk xati.** Yangi shakl va tahrirlagich: qabul qilingan yuk xatini tahrirlashga 24 soat, 48 soat yoki doim ruxsat berish mumkin. Xaridlar ПР-… raqamini oladi, barcha xaridlar «Xaridlar» reestrida, yangi yetkazib beruvchini to'g'ridan-to'g'ri «Kirim qilish» oynasida yaratish mumkin. Aloqa uzilganda ham yuk xatini tahrirlash ishonchli.
- **Qabulda vaznli mahsulotlar.** Yangi mahsulotda birlik tanlovi bor (dona, kg, l, m, qadoq): «kg» vaznli mahsulot yaratadi, miqdor kasr bo'lishi mumkin. Tarozi yorlig'i mahsulotni topadi va vaznni qo'yadi. Miqdor va xarid narxi jimgina yaxlitlanmaydi, vaznli mahsulot qoldig'i 3 xonagacha ko'rsatiladi.
- **Nur Maslahatchi.** Yuk xati suratidan xarid jami bilan solishtiriladi (QQS, chegirma, pozitsiyalar soni), shubhali qatorlar belgilanadi, Maslahatchiga nimaga ruxsat berilganini sozlash, javoblarda ombor ro'yxatlari.
- **Ombor → Ko'chirish → «Hujjatlar».** «Hujjat yaratish» tugmasi ichki ko'chirish yuk xatini, qabul-topshirish dalolatnomasini va transport hujjatini yaratadi; «Jo'natish» bosilganda yuk xati o'zi yaratiladi.
- **Ombor.** Ustun sarlavhalari bo'yicha saralash, «Kirimlar» yorlig'i (mahsulot kimdan, qachon, qaysi yuk xati bilan kelgan), 15 mingtagacha mahsulotli katalog va tez ommaviy yuklash, tarixi bor mahsulotni o'chirishda xato o'rniga tushunarli oyna.
- **To'lov oynasi yangicha.** Chapda jami, qaytim va mijoz, o'ngda to'lov usullari qatorda; F1–F7 tugmalari. Oynalar va dialoglar sarlavhadan suriladi va ekrandan chiqmaydi.
- **Internetsiz ishlash.** «Nasiya» sotuv, qaytarish va qarzni to'lash navbatda kutadi va o'zi jo'natiladi; chek jo'natishdan oldin diskka yoziladi va elektr o'chganda yo'qolmaydi; ikki marta sotuv bo'lmaydi. Server band bo'lganda «aloqa yo'q» o'rniga «Server band, N s dan keyin takrorlaydi».
- **Sinxronlash.** Narx va qoldiqlar boshqa kassalarga 1–2 soniyada yetadi.
- **Marketplace pullik funksiyalari** kalitsiz, serverdan yoqiladi; seriya kalitlari avvalgidek ishlaydi.
- **Tex yordam** kassani bloklamaydi; seans tugaganda — bitta oyna «Seans tugadi. Qayta kiring».
- **Qarzlar va mijozlar.** «Mijozlar qarzi» oynasi qotmasdan ochiladi, qarzni to'lashda jarayon ko'rinadi.
- **Voronka.** «Ochish» tugmasi mijoz bilan yozishmani ochadi (Telegram yoki WhatsApp), bitimda «Karta» tugmasi bor.
- **Egasi dasturi.** Jami boshqa davr raqamlarini ko'rsatmaydi, investor qoplash simulyatori, yopilgan oynalar xotirada qolmaydi.
- **Linux va macOS.** Linuxda chek chop etish, avtoyuklash va yorliqlar; Mac uchun kassa (Apple Silicon va Intel).
- **Nom.** Dastur NurCRM deb ataladi; protsessor va batareyaga yuk kamaydi, o'rnatish 66 MB yengilroq.
