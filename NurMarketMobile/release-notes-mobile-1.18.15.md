## NurCRM Mobile 1.18.15 — тестовая сборка

**Тестовая версия — только для тестировщиков.** Не ставьте её в рабочем магазине без договорённости с нами.

Файл: `nurmarket-mobile-1.18.15.apk` (Android 8.0 и новее). Ставится поверх 1.18.x — подпись та же, вход сохраняется; если уже стоит 1.18.14, приложение само предложит обновление (баннер «Доступна версия»). Пять быстрых нажатий на логотип открывают **режим тестировщика** — список проверок с отметками «Проверено» (пункты этой версии — про округление чека, итог после возвратов и ручную скидку).

### Русский

**Что нового по сравнению с 1.18.14**
- **Итог чека как на сервере.** Строки не округляются, итог округляется один раз до 0,01 (половина — вверх). Сумма на экране, в чеке и в оплате совпадает с серверной, в том числе для весовых товаров и упаковок.
- **Возврат: «Итог чека после возвратов».** На экране возврата чека, по которому уже были возвраты, показана сумма чека после возвратов с сервера.
- **Ручная скидка не больше суммы строки.** Итог строки больше не уходит в минус.

Остальное — как в 1.18.14.

**Что проверить** (подробные шаги — в режиме тестировщика)
1. Товар с «хвостом» в цене (33,333) в нескольких строках: итог округлён один раз и совпадает с сайтом.
2. Чек с прошлым возвратом: на экране возврата видна строка «Итог чека после возвратов».
3. Скидка на строку больше суммы строки: значение ограничивается суммой строки.

**Ограничения.** Кнопка «Провести старые чеки (PIN владельца)» пока не добавлена — ждёт ответа сервера. Живая проверка на телефонах продолжается — поэтому сборка **тестовая**. Тексты на кыргызском, узбекском и турецком переведены без носителя языка.

### Кыргызча

**1.18.14кө салыштырмалуу жаңы эмне бар**
- **Чектин жыйынтыгы сервердегидей.** Саптар тегизделбейт, жыйынтык бир жолу 0,01ге тегизделет (жарымы — жогору). Экрандагы, чектеги жана төлөмдөгү сумма сервердикине дал келет, салмактуу товарларда жана таңгактарда да.
- **Кайтаруу: «Кайтаруулардан кийинки чектин жыйынтыгы».** Мурун кайтаруулар болгон чектин кайтаруу экранында серверден алынган, кайтаруулардан кийинки чектин суммасы көрсөтүлөт.
- **Кол менен берилген арзандатуу сап суммасынан ашпайт.** Сап жыйынтыгы мындан ары минуска кетпейт.

Калганы 1.18.14төгүдөй.

**Эмнени текшерүү керек** (толук кадамдар — тестирөөчү режиминде)
1. Баасында «куйругу» бар (33,333) товар бир нече сапта: жыйынтык бир жолу тегизделет жана сайт менен дал келет.
2. Мурун кайтарылган чек: кайтаруу экранында «Кайтаруулардан кийинки чектин жыйынтыгы» сабы көрүнөт.
3. Сапка сап суммасынан чоң арзандатуу: маани сап суммасы менен чектелет.

**Чектөөлөр.** «Эски чектерди өткөрүү (ээсинин PIN)» баскычы азырынча кошула элек — сервердин жообун күтөт. Телефондордо жандуу текшерүү улантылууда — ошондуктан курама **сынак** үчүн. Кыргызча, өзбекче жана түркчө тексттер эне тилде сүйлөгөн адамсыз которулган.

### English

**What's new compared with 1.18.14**
- **Receipt total as on the server.** Lines are not rounded, the total is rounded once to 0.01 (half rounds up). The amount on the screen, in the receipt and in payment matches the server's, including weighed goods and packs.
- **Return: “Receipt total after returns”.** The return screen of a receipt that already had returns shows the receipt amount after returns, taken from the server.
- **A manual discount cannot exceed the line amount.** The line total no longer goes negative.

Everything else is as in 1.18.14.

**What to check** (detailed steps are in tester mode)
1. A product with a “tail” in the price (33.333) on several lines: the total is rounded once and matches the website.
2. A receipt with an earlier return: the return screen shows the “Receipt total after returns” line.
3. A line discount larger than the line amount: the value is limited to the line amount.

**Limits.** The “Accept old receipts (owner PIN)” button is not added yet — it waits for the server's answer. Live checking on phones continues — that is why the build is **a test one**. The Kyrgyz, Uzbek and Turkish texts were translated without a native speaker.

### Türkçe

**1.18.14'e göre yenilikler**
- **Fiş toplamı sunucudaki gibi.** Satırlar yuvarlanmaz, toplam bir kez 0,01'e yuvarlanır (yarım — yukarı). Ekrandaki, fişteki ve ödemedeki tutar sunucununkiyle aynıdır; tartılı ürünler ve paketler dahil.
- **İade: «İadelerden sonraki fiş toplamı».** Daha önce iadeleri olan fişin iade ekranında, sunucudan alınan iade sonrası fiş tutarı gösterilir.
- **Elle indirim satır tutarını aşamaz.** Satır toplamı artık eksiye düşmez.

Gerisi 1.18.14'teki gibi.

**Ne kontrol edilmeli** (ayrıntılı adımlar test uzmanı modunda)
1. Fiyatında «kuyruk» olan (33,333) ürün birkaç satırda: toplam bir kez yuvarlanır ve site ile aynıdır.
2. Daha önce iade edilmiş fiş: iade ekranında «İadelerden sonraki fiş toplamı» satırı görünür.
3. Satır tutarından büyük satır indirimi: değer satır tutarıyla sınırlanır.

**Sınırlar.** «Eski fişleri işle (sahibin PIN'i)» düğmesi henüz eklenmedi — sunucunun yanıtını bekliyor. Telefonlarda canlı kontrol sürüyor, bu yüzden sürüm **test sürümüdür**. Kırgızca, Özbekçe ve Türkçe metinler anadili konuşan biri olmadan çevrildi.

### O'zbekcha

**1.18.14 ga nisbatan yangiliklar**
- **Chek jami serverdagidek.** Qatorlar yaxlitlanmaydi, jami bir marta 0,01 gacha yaxlitlanadi (yarmi — yuqoriga). Ekrandagi, chekdagi va to'lovdagi summa serverniki bilan mos keladi, vaznli mahsulotlar va qadoqlarda ham.
- **Qaytarish: «Qaytarishlardan keyingi chek jami».** Avval qaytarishlar bo'lgan chekning qaytarish ekranida serverdan olingan, qaytarishlardan keyingi chek summasi ko'rsatiladi.
- **Qo'lda chegirma qator summasidan oshmaydi.** Qator jami endi minusga ketmaydi.

Qolgani 1.18.14 dagidek.

**Nimani tekshirish kerak** (batafsil qadamlar — sinovchi rejimida)
1. Narxida «dum» bor (33,333) mahsulot bir nechta qatorda: jami bir marta yaxlitlanadi va sayt bilan mos keladi.
2. Avval qaytarilgan chek: qaytarish ekranida «Qaytarishlardan keyingi chek jami» qatori ko'rinadi.
3. Qator summasidan katta qator chegirmasi: qiymat qator summasi bilan cheklanadi.

**Cheklovlar.** «Eski cheklarni o'tkazish (egasi PIN)» tugmasi hali qo'shilmagan — serverning javobini kutmoqda. Telefonlarda jonli tekshiruv davom etmoqda — shuning uchun yig'ma **sinov** uchun. Qirg'izcha, o'zbekcha va turkcha matnlar ona tilida so'zlashuvchisiz tarjima qilingan.
