## v1.17.72

Исправления после 1.17.71: смена и остаток, округление чека как на сервере, возврат, правка накладной, редактор сайта. Всё из 1.17.71 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.72.tar.gz`; macOS — `nurmarket-kassa-macos-arm64-1.17.72.zip` (Apple Silicon) и `nurmarket-kassa-macos-x64-1.17.72.zip` (Intel); Android — `nurmarket-kassa-android-1.17.72.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Открытие смены.** Сумма открытия не теряется, если сервер отвечает медленно. Если смена уже открыта, появляется понятное окно «Смена уже открыта».
- **Остаток в ящике.** При открытии «Остаток по системе» подставляется из последней закрытой смены, пока он загружается — виден индикатор. После закрытия смены шапка показывает пересчитанную сумму.
- **Остаток товара.** После продажи остаток больше не затирается: ни правкой карточки, ни импортом, ни размерами, ни перечитыванием с сервера, ни устаревшим ответом.
- **Округление чека как на сервере.** Строки не округляются, итог округляется один раз, поэтому сумма в кассе совпадает с серверной.
- **Возврат.** Сумма выдачи берётся из ответа сервера, «К выдаче (ожидаемо)» показывается до проведения, повторный возврат блокируется. В «Возвратах» добавлен столбец «Время».
- **Накладная.** Правка идёт через сервер: все поля, добавление и удаление позиций, администратору тоже. Есть журнал правок и отметка «кто и когда» у позиции; срока правки нет.
- **Редактор сайта.** Telegram-кнопки и «Помощник магазина» (ключ Gemini); у заказа — чип «Откуда заказ».
- **Настройки.** Верхние кнопки и вкладки обновлены, принтер этикеток можно сбросить, язык переключается прямо в открытых настройках.
- **Ввод.** Штрихкод, отсканированный в поле количества, не портит число.

---

# Кыргызча

### Эмнелер жаңы

- **Сменаны ачуу.** Сервер жай жооп бергенде ачылыш суммасы жоголбойт. Смена мурунтан эле ачык болсо, «Смена мурунтан эле ачык» деген түшүнүктүү терезе чыгат.
- **Суурмадагы калдык.** Ачууда «Система боюнча калдык» акыркы жабылган сменадан коюлат, ал жүктөлүп жатканда жүктөө көрсөткүчү көрүнөт. Сменаны жапкандан кийин баш жагы кайра эсептелген сумманы көрсөтөт.
- **Товардын калдыгы.** Сатуудан кийин калдык өчүрүлбөйт: карточканы оңдоо, импорт, өлчөмдөр, серверден кайра окуу жана эскирген жооп аны бузбайт.
- **Чектин тегиздөөсү сервердегидей.** Саптар тегизделбейт, жыйынтык бир жолу тегизделет, ошондуктан кассадагы сумма сервердикине дал келет.
- **Кайтаруу.** Берилүүчү сумма серверден келген жооптон алынат, «Берүүгө (күтүлүүдө)» өткөрүүгө чейин көрсөтүлөт, кайталап кайтарууга тыюу салынат. «Кайтаруулар» терезесинде «Убакыт» тилкеси кошулду.
- **Накладной.** Оңдоо сервер аркылуу өтөт: бардык талаалар, позицияларды кошуу жана өчүрүү, администратор үчүн да. Оңдоолор журналы жана позицияда «ким жана качан» белгиси бар; оңдоого мөөнөт жок.
- **Сайт редактору.** Telegram баскычтары жана «Дүкөн жардамчысы» (Gemini ачкычы); заказда «Заказ кайдан» чипи.
- **Жөндөөлөр.** Жогорку баскычтар жана өтмөктөр жаңыланды, энбелги принтерин кайра баштапкы абалга келтирүүгө болот, тил ачык жөндөөлөрдө эле которулат.
- **Киргизүү.** Сан талаасына сканерленген штрих-код санды бузбайт.

---

# English

### What's new

- **Opening a shift.** The opening amount is not lost when the server is slow. If a shift is already open, you see a clear “A shift is already open” window.
- **Drawer balance.** At opening the “System balance” is filled in from the last closed shift, with a loading indicator while it loads. After closing a shift the header shows the recalculated amount.
- **Product stock.** After a sale the stock is no longer overwritten: not by editing the card, an import, sizes, re-reading from the server or a stale server answer.
- **Receipt rounding as on the server.** Lines are not rounded, the total is rounded once, so the amount in the till matches the server's.
- **Returns.** The payout comes from the server's answer, “To pay out (expected)” is shown before posting, and a repeat return is blocked. A “Time” column is added to “Returns”.
- **Invoice.** Editing goes through the server: all fields, adding and removing items, administrators too. There is an edit log and a “who and when” mark on each item; there is no time limit for editing.
- **Site editor.** Telegram buttons and the “Store assistant” (Gemini key); an “Order source” chip on each order.
- **Settings.** The top buttons and tabs are refreshed, the label printer can be reset, and the language switches right in open settings.
- **Input.** A barcode scanned into the quantity field does not spoil the number.

---

# Türkçe

### Yenilikler

- **Vardiya açma.** Sunucu yavaş yanıt verse bile açılış tutarı kaybolmaz. Vardiya zaten açıksa anlaşılır bir «Vardiya zaten açık» penceresi çıkar.
- **Çekmece bakiyesi.** Açılışta «Sistem bakiyesi» son kapatılan vardiyadan doldurulur, yüklenirken yükleme göstergesi görünür. Vardiya kapatıldıktan sonra başlık yeniden hesaplanan tutarı gösterir.
- **Ürün stoku.** Satıştan sonra stok artık ezilmez: ne kart düzenlemesi, ne içe aktarma, ne bedenler, ne sunucudan yeniden okuma, ne de eski bir yanıt bozar.
- **Fiş yuvarlaması sunucudaki gibi.** Satırlar yuvarlanmaz, toplam bir kez yuvarlanır; böylece kasadaki tutar sunucununkiyle aynıdır.
- **İade.** Ödenecek tutar sunucu yanıtından alınır, «Verilecek (beklenen)» işlemeden önce gösterilir, tekrar iade engellenir. «İadeler» penceresine «Saat» sütunu eklendi.
- **İrsaliye.** Düzenleme sunucu üzerinden geçer: tüm alanlar, kalem ekleme ve silme, yöneticiler için de. Düzenleme günlüğü ve her kalemde «kim ve ne zaman» işareti var; düzenleme için süre sınırı yok.
- **Site düzenleyici.** Telegram düğmeleri ve «Mağaza yardımcısı» (Gemini anahtarı); siparişte «Sipariş nereden» etiketi.
- **Ayarlar.** Üst düğmeler ve sekmeler yenilendi, etiket yazıcısı sıfırlanabilir, dil açık ayarlarda doğrudan değişir.
- **Giriş.** Miktar alanına okutulan barkod sayıyı bozmaz.

---

# O'zbekcha

### Yangiliklar

- **Smenani ochish.** Server sekin javob bersa ham ochilish summasi yo'qolmaydi. Smena allaqachon ochiq bo'lsa, tushunarli «Smena allaqachon ochiq» oynasi chiqadi.
- **Tortmadagi qoldiq.** Ochishda «Tizim bo'yicha qoldiq» oxirgi yopilgan smenadan qo'yiladi, yuklanayotganda yuklash ko'rsatkichi ko'rinadi. Smena yopilgandan keyin sarlavha qayta hisoblangan summani ko'rsatadi.
- **Mahsulot qoldig'i.** Sotuvdan keyin qoldiq o'chib ketmaydi: kartani tahrirlash, import, o'lchamlar, serverdan qayta o'qish va eskirgan javob uni buzmaydi.
- **Chekni yaxlitlash serverdagidek.** Qatorlar yaxlitlanmaydi, jami bir marta yaxlitlanadi, shuning uchun kassadagi summa serverniki bilan mos keladi.
- **Qaytarish.** Beriladigan summa server javobidan olinadi, «Berish (kutilmoqda)» o'tkazishdan oldin ko'rsatiladi, takroriy qaytarish bloklanadi. «Qaytarishlar» oynasiga «Vaqt» ustuni qo'shildi.
- **Yuk xati.** Tahrirlash server orqali o'tadi: barcha maydonlar, pozitsiya qo'shish va o'chirish, administrator uchun ham. Tahrirlar jurnali va pozitsiyada «kim va qachon» belgisi bor; tahrirlash muddati yo'q.
- **Sayt muharriri.** Telegram tugmalari va «Do'kon yordamchisi» (Gemini kaliti); buyurtmada «Buyurtma qayerdan» belgisi.
- **Sozlamalar.** Yuqori tugmalar va yorliqlar yangilandi, yorliq printerini tashlash mumkin, til ochiq sozlamalarda to'g'ridan-to'g'ri almashadi.
- **Kiritish.** Miqdor maydoniga skanerlangan shtrix-kod sonni buzmaydi.
