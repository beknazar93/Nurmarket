## v1.17.94

Печать этикетки открывает «Штрихкоды» с выбранным товаром; в «Товарах» вернулись галочки выбора; в «Штрихкодах» вид «Список / Плитки»; в «Приёмке» «Убрать» и Ctrl+Z; остаток в минусе показывается красным «Минус −N шт.»; «Смета» и «Прокат» слушаются сервера; у функций Маркетплейса свои иконки и описания. Всё из 1.17.93 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.94.tar.gz`; Android — `nurmarket-kassa-android-1.17.94.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Печать этикетки.** Пункт «Печать этикетки» в меню «⋯» у товара теперь открывает окно «Штрихкоды» с выбранным товаром, а не печатает молча.
- **Товары — выбор галочками.** В «Товарах» вернулись галочки выбора: в строках, «выбрать все», счётчик и панель «Ценники и штрихкоды»; в плитках галочка в углу.
- **Штрихкоды — Список и Плитки.** В «Штрихкодах» два вида — «Список» и «Плитки»: список быстро работает даже на 15 000 товаров, выбор и число копий общие для обоих видов.
- **Приёмка.** Кнопка «Убрать» убирает последнюю строку; Ctrl+Z возвращает убранное сразу пачкой.
- **Остаток в минусе.** Остаток ниже нуля показывается красным: «Минус −N шт.» (плитка, список, упаковка, карточка, «Склад → Товары»); исправлено «0 шт.» вместо минуса. После продажи остаток уходит ниже нуля только при включённой настройке «Продавать без остатка».
- **Смета, Прокат и Маркетплейс.** «Смета» и «Прокат» больше не показываются по старому признаку, если сервер сказал «выключено». В Маркетплейсе у каждой из 20 функций своя иконка и описание: что делает, как включить, для кого.

# Кыргызча

### Эмнелер жаңы

- **Этикетка басуу.** Товардын «⋯» менюсундагы «Этикетка басуу» пункту эми үнсүз баспастан, тандалган товар менен «Штрихкоддор» терезесин ачат.
- **Товарлар — белги менен тандоо.** «Товарларда» тандоо белгилери кайтты: сабактарда, «баарын тандоо», эсептегич жана «Баа этикеткалары жана штрихкоддор» панели; плиткаларда белги бурчта.
- **Штрихкоддор — Тизме жана Плиткалар.** «Штрихкоддордо» эки көрүнүш бар — «Тизме» жана «Плиткалар»: тизме 15 000 товарда да тез иштейт, тандоо жана нускалардын саны эки көрүнүштө бирдей.
- **Кабыл алуу.** «Алып салуу» баскычы акыркы сапты алып салат; Ctrl+Z алынганды бир жолу кайтарат.
- **Минустагы калдык.** Нөлдөн төмөн калдык кызыл түстө көрсөтүлөт: «Минус −N даана» (плитка, тизме, таңгак, карточка, «Кампа → Товарлар»); минустун ордуна «0 даана» көрүнгөнү оңдолду. Сатуудан кийин калдык нөлдөн төмөн «Калдыксыз сатуу» жөндөөсү күйгүзүлгөндө гана түшөт.
- **Смета, Ижара жана Маркетплейс.** «Смета» менен «Ижара» сервер «өчүрүлгөн» деп айтса, эски белги боюнча көрүнбөйт. Маркетплейсте 20 функциянын ар биринин өз иконкасы жана сүрөттөмөсү бар: эмне кылат, кантип күйгүзүлөт, кимге.

# English

### What's new

- **Print label.** The “Print label” item in a product’s “⋯” menu now opens “Barcodes” with that product selected instead of printing silently.
- **Products — tick-box selection.** Tick boxes are back in “Products”: in rows, “select all”, a counter and the “Price tags and barcodes” panel; in tiles the tick box is in the corner.
- **Barcodes — List and Tiles.** “Barcodes” has two views, “List” and “Tiles”: the list stays fast even with 15,000 products, and the selection and copy counts are shared by both views.
- **Receiving.** The “Remove” button removes the last line; Ctrl+Z brings the removed lines back in one batch.
- **Negative stock.** Stock below zero is shown in red as “Minus −N pcs” (tile, list, pack, card, “Warehouse → Products”); fixed “0 pcs” being shown instead of the minus. After a sale the stock goes below zero only when “Sell without stock” is on.
- **Estimate, Rental and Marketplace.** “Estimate” and “Rental” are no longer shown by the old flag when the server says “off”. In the Marketplace each of the 20 features has its own icon and a description: what it does, how to turn it on, who it is for.

# Türkçe

### Yenilikler

- **Etiket yazdır.** Ürünün “⋯” menüsündeki “Etiket yazdır” öğesi artık sessizce yazdırmak yerine seçili ürünle “Barkodlar” penceresini açar.
- **Ürünler — onay kutusuyla seçim.** “Ürünler”de seçim onay kutuları geri geldi: satırlarda, “tümünü seç”, sayaç ve “Fiyat etiketleri ve barkodlar” paneli; kutucuklarda onay kutusu köşede.
- **Barkodlar — Liste ve Kutucuklar.** “Barkodlar”da iki görünüm var: “Liste” ve “Kutucuklar”; liste 15.000 üründe bile hızlı çalışır, seçim ve kopya sayısı iki görünümde ortaktır.
- **Mal kabul.** “Kaldır” düğmesi son satırı kaldırır; Ctrl+Z kaldırılanları tek seferde geri getirir.
- **Eksi stok.** Sıfırın altındaki stok kırmızı renkle “Eksi −N adet” olarak gösterilir (kutucuk, liste, paket, kart, “Depo → Ürünler”); eksi yerine “0 adet” görünmesi düzeltildi. Satıştan sonra stok yalnızca “Stoksuz sat” açıkken sıfırın altına düşer.
- **Teklif, Kiralama ve Pazar yeri.** Sunucu “kapalı” dediğinde “Teklif” ve “Kiralama” artık eski işarete göre gösterilmez. Pazar yerinde 20 işlevin her birinin kendi simgesi ve açıklaması var: ne yapar, nasıl açılır, kimler için.

# O'zbekcha

### Yangiliklar

- **Yorliq chop etish.** Tovarning «⋯» menyusidagi «Yorliq chop etish» bandi endi indamay chop etmasdan, tanlangan tovar bilan «Shtrix-kodlar» oynasini ochadi.
- **Tovarlar — belgi bilan tanlash.** «Tovarlar»da tanlash belgilari qaytdi: qatorlarda, «hammasini tanlash», hisoblagich va «Narx yorliqlari va shtrix-kodlar» paneli; plitkalarda belgi burchakda.
- **Shtrix-kodlar — Ro'yxat va Plitkalar.** «Shtrix-kodlar»da ikki ko'rinish bor — «Ro'yxat» va «Plitkalar»: ro'yxat 15 000 tovarda ham tez ishlaydi, tanlash va nusxalar soni ikkala ko'rinishda umumiy.
- **Qabul qilish.** «Olib tashlash» tugmasi oxirgi qatorni olib tashlaydi; Ctrl+Z olib tashlanganini birdaniga qaytaradi.
- **Minusdagi qoldiq.** Noldan past qoldiq qizil rangda ko'rsatiladi: «Minus −N dona» (plitka, ro'yxat, qadoq, karta, «Ombor → Tovarlar»); minus o'rniga «0 dona» ko'rinishi tuzatildi. Sotuvdan keyin qoldiq faqat «Qoldiqsiz sotish» yoqilgan bo'lsa noldan pastga tushadi.
- **Smeta, Ijara va Marketplace.** Server «o'chirilgan» desa, «Smeta» va «Ijara» eski belgi bo'yicha ko'rinmaydi. Marketplace'da 20 funksiyaning har birining o'z ikonkasi va tavsifi bor: nima qiladi, qanday yoqiladi, kim uchun.
