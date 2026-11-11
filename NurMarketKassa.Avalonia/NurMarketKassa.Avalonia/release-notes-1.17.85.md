## v1.17.85

Срочное исправление: в «Закупки → Приём товара» (версии 1.17.82–1.17.84) количество строк обнулялось, а наценка и цены портились при вводе. Кроме того — ввод и скан в приёмке, возвраты, окна. Всё из 1.17.84 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.85.tar.gz`; macOS — `nurmarket-kassa-macos-arm64-1.17.85.zip` (Apple Silicon) и `nurmarket-kassa-macos-x64-1.17.85.zip` (Intel); Android — `nurmarket-kassa-android-1.17.85.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Приём товара: исправлена ошибка.** Исправлена ошибка версий 1.17.82–1.17.84 в «Закупки → Приём товара»: количество строк обнулялось после отрисовки, а наценка, цена продажи и закупка портились при вводе.
- **Приёмка: ввод и скан.** В ячейках число выделяется при входе, пустое поле допустимо, Esc отменяет ввод; у нового товара можно создать штрихкод; у товара склада можно принять упаковкой (с подтверждением); точный скан — штучные коды «20…» и «27…» не считаются весовыми; ячейки цены защищены, пределы цен сервера учитываются.
- **Повторный скан и черновик.** Повторный скан открывает окно количества (по настройке); черновик неоконченной приёмки сохраняется.
- **Возвраты.** Исправлена ошибка «sale_item_id»; фильтры и поиск по товару в возвратах; кнопки «Возврат» в карточке клиента, в «Долгах» и «Продажах», пункт «Возврат» в программе владельца; правка и аннулирование возврата — когда это поддержит сервер.
- **Окна.** Затемнение окон без размытия; окно можно свернуть.
- **Правка штрихкода.** Правка штрихкода товара не теряется: касса сверяет карточку товара по id.
- **Филиал и неоднозначный штрихкод.** Запросы приёмки учитывают филиал; если штрихкод неоднозначный, касса просит выбрать товар.

---

# Кыргызча

### Эмнелер жаңы

- **Товар кабыл алуу: ката оңдолду.** 1.17.82–1.17.84 версияларындагы «Сатып алуулар → Товар кабыл алуу» катасы оңдолду: сап саны чийилгенден кийин нөлгө түшүп, кошумча баа, сатуу жана сатып алуу баасы киргизүүдө бузулуп жаткан.
- **Кабыл алуу: киргизүү жана сканерлөө.** Уячаларда сан кирүүдө бөлүнүп көрсөтүлөт, бош талаа уруксат, Esc киргизүүнү жокко чыгарат; жаңы товарга штрихкод түзсө болот; кампа товарын таңгак менен ырастоо аркылуу кабыл алса болот; так сканерлөө — «20…» жана «27…» даана коддору салмактуу деп эсептелбейт; баа уячалары корголгон, сервердин баа чектери эске алынат.
- **Кайра сканерлөө жана долбоор.** Кайра сканерлөө санын терезесин ачат (жөндөө боюнча); аягына чыкпаган кабыл алуунун долбоору сакталат.
- **Кайтаруулар.** «sale_item_id» катасы оңдолду; кайтаруулардагы чыпкалар жана товар боюнча издөө; кардар карточкасында, «Карыздарда» жана «Сатууларда» «Кайтаруу» баскычтары, ээсинин программасында «Кайтаруу» пункту; кайтарууну оңдоо жана жокко чыгаруу — сервер колдогондо.
- **Терезелер.** Терезелерди караңгылатуу бүдөмүк эмес; терезени жыйыштырууга болот.
- **Штрихкодду оңдоо.** Товардын штрихкодун оңдоо жоголбойт: касса товардын карточкасын id боюнча салыштырат.
- **Филиал жана так эмес штрихкод.** Кабыл алуу суроолору филиалды эске алат; штрихкод так эмес болсо, касса товарды тандоону сурайт.

---

# English

### What's new

- **Receiving goods: bug fixed.** Fixed a bug of versions 1.17.82–1.17.84 in “Purchases → Receiving goods”: line quantities were reset to zero after redraw, and the markup, selling price and purchase price were corrupted while typing.
- **Receiving: typing and scanning.** In cells the number is selected on entry, an empty field is allowed, Esc cancels typing; a new product can get a generated barcode; a warehouse product can be received by the pack (with confirmation); exact scan — piece codes “20…” and “27…” are not treated as weighed; price cells are protected and the server's price limits are respected.
- **Repeat scan and draft.** A repeat scan opens the quantity window (by setting); the draft of an unfinished receiving is kept.
- **Returns.** Fixed the “sale_item_id” error; filters and product search in returns; “Return” buttons in the client card, in “Debts” and “Sales”, a “Return” item in the owner program; editing and voiding a return — once the server supports it.
- **Windows.** Window dimming without blur; a window can be minimized.
- **Barcode editing.** A product barcode edit is not lost: the till checks the product card by id.
- **Branch and ambiguous barcode.** Receiving requests take the branch into account; if a barcode is ambiguous, the till asks you to pick the product.

---

# Türkçe

### Yenilikler

- **Mal kabulü: hata düzeltildi.** 1.17.82–1.17.84 sürümlerinin «Satın alma → Mal kabulü» hatası düzeltildi: satır miktarları yeniden çizimden sonra sıfırlanıyor, kâr oranı, satış ve alış fiyatı yazarken bozuluyordu.
- **Mal kabulü: giriş ve tarama.** Hücrelerde sayı girişte seçilir, boş alana izin verilir, Esc girişi iptal eder; yeni ürüne barkod üretilebilir; depo ürünü paketle (onayla) kabul edilebilir; kesin tarama — «20…» ve «27…» adet kodları tartılı sayılmaz; fiyat hücreleri korunur, sunucunun fiyat sınırları dikkate alınır.
- **Tekrar tarama ve taslak.** Tekrar tarama miktar penceresini açar (ayara göre); bitmemiş mal kabulünün taslağı saklanır.
- **İadeler.** «sale_item_id» hatası düzeltildi; iadelerde filtreler ve ürüne göre arama; müşteri kartında, «Borçlar»da ve «Satışlar»da «İade» düğmeleri, sahip programında «İade» öğesi; iadeyi düzenleme ve iptal etme — sunucu desteklediğinde.
- **Pencereler.** Pencere karartması bulanıklık olmadan; pencere küçültülebilir.
- **Barkod düzenleme.** Ürün barkodu düzenlemesi kaybolmaz: kasa ürün kartını id'ye göre karşılaştırır.
- **Şube ve belirsiz barkod.** Mal kabulü istekleri şubeyi dikkate alır; barkod belirsizse kasa ürünü seçmenizi ister.

---

# O'zbekcha

### Yangiliklar

- **Tovar qabuli: xato tuzatildi.** 1.17.82–1.17.84 versiyalaridagi «Xaridlar → Tovar qabuli» xatosi tuzatildi: qator miqdori qayta chizilgandan keyin nolga tushar, ustama, sotuv va xarid narxi kiritishda buzilar edi.
- **Qabul: kiritish va skanerlash.** Kataklarda son kirishda ajratib ko'rsatiladi, bo'sh maydon mumkin, Esc kiritishni bekor qiladi; yangi tovarga shtrix-kod yaratish mumkin; ombor tovarini o'ram bilan (tasdiq bilan) qabul qilish mumkin; aniq skanerlash — «20…» va «27…» dona kodlari tarozi deb hisoblanmaydi; narx kataklari himoyalangan, serverning narx chegaralari hisobga olinadi.
- **Takroriy skanerlash va qoralama.** Takroriy skanerlash miqdor oynasini ochadi (sozlama bo'yicha); tugallanmagan qabulning qoralamasi saqlanadi.
- **Qaytarishlar.** «sale_item_id» xatosi tuzatildi; qaytarishlarda filtrlar va tovar bo'yicha qidiruv; mijoz kartochkasida, «Qarzlar» va «Sotuvlar»da «Qaytarish» tugmalari, egasi dasturida «Qaytarish» bandi; qaytarishni tahrirlash va bekor qilish — server qo'llab-quvvatlaganda.
- **Oynalar.** Oynalarni xiralashtirish xiralashtirishsiz; oynani yig'ish mumkin.
- **Shtrix-kodni tahrirlash.** Tovar shtrix-kodini tahrirlash yo'qolmaydi: kassa tovar kartochkasini id bo'yicha solishtiradi.
- **Filial va noaniq shtrix-kod.** Qabul so'rovlari filialni hisobga oladi; shtrix-kod noaniq bo'lsa, kassa tovarni tanlashni so'raydi.
