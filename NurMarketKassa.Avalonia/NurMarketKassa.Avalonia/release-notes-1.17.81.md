## v1.17.81

Цена продажи в приёмке больше не меняется сама, весы «Штрих РС-200» и предупреждения о ценах выше 9999,99, новый вид главной кассы, удаление товара одним окном, печать этикеток и PDF на Linux и Android, экономия памяти. Всё из 1.17.79 и тестовой 1.17.80 (весы) сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.81.tar.gz`; macOS — `nurmarket-kassa-macos-arm64-1.17.81.zip` (Apple Silicon) и `nurmarket-kassa-macos-x64-1.17.81.zip` (Intel); Android — `nurmarket-kassa-android-1.17.81.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Цена продажи в приёмке.** Цена продажи при приёмке больше не меняется сама: закупка 40, продажа 60 — остаётся 60. Если сервер пересчитал цену по наценке, касса возвращает введённую вами цену и наценку и сообщает об этом.
- **Весы «Штрих РС-200».** Главный экран весов собирается автоматически (сценарий самообслуживания), для товаров без фото рисуются картинки-заглушки, есть полные настройки весов в окне с вкладками.
- **Цены выше 9999,99 на весах.** Для весов (Штрих-ПРИНТ, Rongta, Штрих РС-200) цена выше 9999,99 даёт понятное предупреждение вместо неверной цены на этикетке; для Rongta — предупреждение и рекомендуемая структура штрихкода без ограничения суммы; выгрузка, которую весы ещё не приняли, не перезаписывается.
- **Новый вид главной кассы.** Пилюля смены и компактная шапка, кнопка «Вернуть» после очистки чека, подсветка добавленной строки, на узком окне каталог открывается автосписком.
- **Удаление товара.** Удаление товара — одним окном с последствиями, архив — одним вопросом; аналитика прошлых периодов не теряет удалённый товар.
- **Колонка «№» в приёмке.** В таблицах приёмки появилась колонка «№».
- **Маркетплейс.** «Открыть на сайте», «Обновить покупки»; темы с сайта (когда сервер их включит).
- **Печать и PDF.** Печать этикеток и PDF работают на Linux и Android (через Skia и CUPS); на чеке есть кнопка «PDF».
- **Этикетки размеров.** Кнопка «Создать QR» для размеров и подтверждение перед печатью этикеток.
- **Память.** Освобождаются кэши картинок и история Советника; в журнале появилась строка «ПАМЯТЬ».
- **Быстрее запуск.** Касса запускается быстрее, сценарии работают без связи.
- **Исправлено по живой проверке.** Цена в накладной считается верно, изменение товара не теряется при обновлении ленты кассы.

---

# Кыргызча

### Эмнелер жаңы

- **Кабыл алуудагы сатуу баасы.** Кабыл алууда сатуу баасы эми өзү өзгөрбөйт: сатып алуу 40, сатуу 60 — 60 бойдон калат. Сервер баасын кошумча баа боюнча кайра эсептесе, касса сиз киргизген баа менен кошумча бааны кайтарып, бул тууралуу кабарлайт.
- **«Штрих РС-200» таразасы.** Таразанын башкы экраны автоматтык түрдө курулат (өзүн-өзү тейлөө сценарийи), сүрөтү жок товарларга толтургуч сүрөттөр тартылат, табулуу терезеде таразанын толук жөндөөлөрү бар.
- **Таразадагы 9999,99дан жогору баалар.** Тараза үчүн (Штрих-ПРИНТ, Rongta, Штрих РС-200) 9999,99дан жогору баа этикеткада туура эмес баанын ордуна түшүнүктүү эскертүү берет; Rongta үчүн эскертүү жана суммага чектөөсүз штрихкоддун сунушталган түзүлүшү; таразага али кабыл алынбаган чыгаруу кайра жазылбайт.
- **Башкы касса жаңы көрүнүшү.** Смена пилюлясы жана ыкчам баш, чек тазаланган соң «Кайтаруу» баскычы, кошулган сапты белгилөө, тар терезеде каталог автотизме болуп ачылат.
- **Товарды өчүрүү.** Товарды өчүрүү — кесепеттери менен бир терезеде, архив — бир суроо менен; өткөн мезгилдердин аналитикасы өчүрүлгөн товарды жоготпойт.
- **Кабыл алуудагы «№» мамычасы.** Кабыл алуу таблицаларында «№» мамычасы пайда болду.
- **Маркетплейс.** «Сайтта ачуу», «Сатып алууларды жаңыртуу»; сайттагы темалар (сервер иштеткенде).
- **Басып чыгаруу жана PDF.** Этикеткаларды басып чыгаруу жана PDF Linux менен Android'де иштейт (Skia жана CUPS аркылуу); чекте «PDF» баскычы бар.
- **Өлчөм этикеткалары.** Өлчөмдөр үчүн «QR түзүү» баскычы жана этикеткаларды басардан мурун ырастоо.
- **Эс тутум.** Сүрөт кэштери жана Кеңешчинин тарыхы бошотулат; журналда «ПАМЯТЬ» сабы пайда болду.
- **Ылдамыраак иштетүү.** Касса тезирээк ишке кирет, сценарийлер байланышсыз да иштейт.
- **Түз текшерүү боюнча оңдоолор.** Накладнойдогу баа туура эсептелет, товардын өзгөрүүсү кассанын лентасын жаңыртууда жоголбойт.

---

# English

### What's new

- **Selling price in receiving.** The selling price no longer changes by itself when receiving goods: purchase 40, sale 60 stays 60. If the server recalculated the price by markup, the till restores the price and markup you entered and tells you.
- **“Shtrikh RS-200” scales.** The scale's main screen is built automatically (self-service scenario), placeholder pictures are drawn for products without a photo, and there are full scale settings in a tabbed window.
- **Prices above 9999.99 on scales.** For scales (Shtrikh-PRINT, Rongta, Shtrikh RS-200) a price above 9999.99 gives a clear warning instead of a wrong price on the label; for Rongta there is a warning and a recommended barcode structure without the amount limit; an upload the scales have not accepted yet is not overwritten.
- **New look of the main till screen.** A shift pill and a compact header, a “Restore” button after clearing the receipt, a highlight on the added line, and on a narrow window the catalog opens as an automatic list.
- **Deleting a product.** Deleting a product is one window with the consequences, the archive is one question; analytics of past periods no longer loses a deleted product.
- **“No.” column in receiving.** A “No.” column appeared in the receiving tables.
- **Marketplace.** “Open on the site”, “Refresh purchases”; themes from the site (once the server enables them).
- **Printing and PDF.** Label printing and PDF work on Linux and Android (through Skia and CUPS); the receipt has a “PDF” button.
- **Size labels.** A “Create QR” button for sizes and a confirmation before printing labels.
- **Memory.** Image caches and the Advisor's history are freed; the log now has a “MEMORY” line.
- **Faster start.** The till starts faster, and scenarios work without a connection.
- **Fixed after live testing.** The price in an invoice is calculated correctly, and a product change is not lost when the till's feed refreshes.

---

# Türkçe

### Yenilikler

- **Mal kabulünde satış fiyatı.** Mal kabulünde satış fiyatı artık kendiliğinden değişmez: alış 40, satış 60 ise 60 kalır. Sunucu fiyatı kâr oranına göre yeniden hesaplarsa kasa girdiğiniz fiyatı ve kâr oranını geri yükler ve bunu bildirir.
- **«Shtrih RS-200» terazisi.** Tartının ana ekranı otomatik kurulur (self-servis senaryosu), fotoğrafsız ürünler için yer tutucu resimler çizilir, sekmeli pencerede tam terazi ayarları vardır.
- **Terazilerde 9999,99 üstü fiyatlar.** Teraziler (Shtrih-PRINT, Rongta, Shtrih RS-200) için 9999,99 üstü fiyat, etikette yanlış fiyat yerine anlaşılır bir uyarı verir; Rongta için uyarı ve tutar sınırı olmayan önerilen barkod yapısı vardır; terazinin henüz kabul etmediği yükleme üzerine yazılmaz.
- **Ana kasa ekranının yeni görünümü.** Vardiya hapı ve kompakt başlık, fiş temizlendikten sonra «Geri al» düğmesi, eklenen satırın vurgulanması, dar pencerede katalog otomatik liste olarak açılır.
- **Ürün silme.** Ürün silme sonuçlarıyla birlikte tek pencere, arşiv tek soru; geçmiş dönem analitiği silinen ürünü artık kaybetmez.
- **Mal kabulünde «No» sütunu.** Mal kabul tablolarında «No» sütunu eklendi.
- **Pazaryeri.** «Sitede aç», «Satın alımları yenile»; siteden temalar (sunucu etkinleştirince).
- **Yazdırma ve PDF.** Etiket yazdırma ve PDF Linux ve Android'de çalışır (Skia ve CUPS ile); fişte «PDF» düğmesi vardır.
- **Beden etiketleri.** Bedenler için «QR oluştur» düğmesi ve etiket basmadan önce onay.
- **Bellek.** Resim önbellekleri ve Danışman geçmişi boşaltılır; günlükte «ПАМЯТЬ» satırı belirdi.
- **Daha hızlı açılış.** Kasa daha hızlı açılır, senaryolar bağlantısız da çalışır.
- **Canlı testten sonra düzeltilenler.** Faturadaki fiyat doğru hesaplanır, kasa akışı yenilenirken ürün değişikliği kaybolmaz.

---

# O'zbekcha

### Yangiliklar

- **Qabuldagi sotuv narxi.** Qabulda sotuv narxi endi o'zi o'zgarmaydi: xarid 40, sotuv 60 — 60 bo'lib qoladi. Server narxni ustama bo'yicha qayta hisoblasa, kassa siz kiritgan narx va ustamani qaytaradi va bu haqda xabar beradi.
- **«Shtrix RS-200» tarozisi.** Tarozining bosh ekrani avtomatik quriladi (o'z-o'ziga xizmat ssenariysi), rasmsiz tovarlar uchun to'ldirgich rasmlar chiziladi, ichki oynali oynada tarozining to'liq sozlamalari bor.
- **Tarozilarda 9999,99 dan yuqori narxlar.** Tarozilar (Shtrix-PRINT, Rongta, Shtrix RS-200) uchun 9999,99 dan yuqori narx yorliqda noto'g'ri narx o'rniga tushunarli ogohlantirish beradi; Rongta uchun ogohlantirish va summa cheklovisiz shtrix-kodning tavsiya etilgan tuzilishi bor; tarozi hali qabul qilmagan yuklash ustiga yozilmaydi.
- **Asosiy kassa yangi ko'rinishi.** Smena tabletkasi va ixcham sarlavha, chek tozalangandan keyin «Qaytarish» tugmasi, qo'shilgan qatorni ajratib ko'rsatish, tor oynada katalog avtoro'yxat bo'lib ochiladi.
- **Tovarni o'chirish.** Tovarni o'chirish — oqibatlari bilan bitta oyna, arxiv — bitta savol; o'tgan davrlar analitikasi o'chirilgan tovarni yo'qotmaydi.
- **Qabuldagi «№» ustuni.** Qabul jadvallarida «№» ustuni paydo bo'ldi.
- **Marketpleys.** «Saytda ochish», «Xaridlarni yangilash»; saytdan mavzular (server yoqqanda).
- **Chop etish va PDF.** Yorliqlarni chop etish va PDF Linux va Android'da ishlaydi (Skia va CUPS orqali); chekda «PDF» tugmasi bor.
- **O'lcham yorliqlari.** O'lchamlar uchun «QR yaratish» tugmasi va yorliqlarni chop etishdan oldin tasdiq.
- **Xotira.** Rasm keshlari va Maslahatchi tarixi bo'shatiladi; jurnalda «ПАМЯТЬ» qatori paydo bo'ldi.
- **Tezroq ishga tushish.** Kassa tezroq ishga tushadi, ssenariylar aloqasiz ham ishlaydi.
- **Jonli tekshiruvdan keyin tuzatilganlar.** Yuk xatidagi narx to'g'ri hisoblanadi, kassa lentasi yangilanganda tovar o'zgarishi yo'qolmaydi.
