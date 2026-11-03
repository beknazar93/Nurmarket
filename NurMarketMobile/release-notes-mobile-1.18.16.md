## NurCRM Mobile 1.18.16 — тестовая сборка

**Тестовая версия — только для тестировщиков.** Не ставьте её в рабочем магазине без договорённости с нами.

Файл: `nurmarket-mobile-1.18.16.apk` (Android 8.0 и новее). Ставится поверх 1.18.x — подпись та же, вход сохраняется; если уже стоит 1.18.15, приложение само предложит обновление (баннер «Доступна версия»). Пять быстрых нажатий на логотип открывают **режим тестировщика** — список проверок с отметками «Проверено» (пункты этой версии — про итог чека по правилу сервера, заморозку чека во время оплаты, повторную оплату после падения и обновление).

### Русский

**Что нового по сравнению с 1.18.15**
- **Итог чека по правилу сервера.** Подытог = ВНИЗ(сумма весовых товаров до копейки) + ВВЕРХ(сумма остальных); скидка чека в процентах считается от округлённого подытога. Примеры: 0,375 кг × 13 и 0,343 кг × 66 дают 27,51; 22,638 → 22,63; 17,00 со скидкой 2,5 % → 16,57. На чеке видно «Сумма без скидки − Скидка = ИТОГО».
- **Во время оплаты чек заморожен.** Скан и поиск не добавляют товар, пока идёт оплата; количество, скидки и удаление строк недоступны.
- **После падения приложения повторная оплата не создаёт вторую продажу.** Если приложение закрылось или упало во время оплаты, повторная оплата того же чека возвращает прежнюю продажу по ключу оплаты.
- **Обновление: только стабильные выпуски.** В обычном режиме предлагаются стабильные выпуски, тестовые — в режиме тестировщика. Скачанный файл сверяется по размеру и, если GitHub прислал контрольную сумму, по SHA-256; при несовпадении он удаляется и установка не открывается.

Остальное — как в 1.18.15.

**Что проверить** (подробные шаги — в режиме тестировщика)
1. Весовые 0,375 кг × 13 и 0,343 кг × 66: итог 27,51. Обычный товар 17,00 со скидкой чека 2,5 %: 16,57 и строки «Сумма без скидки», «Скидка», «ИТОГО».
2. Нажмите «Оплатить» и, пока идёт оплата, отсканируйте другой штрихкод: товар в чек не попадает.
3. Нажмите «Оплатить», сразу закройте приложение из списка недавних, откройте снова и оплатите тот же чек: продажа одна (или ни одной), не две.
4. Обновление: в обычном режиме тестовый выпуск не предлагается; «Обновить» → скачивание → системная установка; при обрыве загрузки — понятное сообщение, недокачанный файл удалён.

**Ограничения.** Если GitHub не прислал контрольную сумму, проверяется только размер файла. Живая проверка на телефонах продолжается — поэтому сборка **тестовая**. Тексты на кыргызском, узбекском и турецком переведены без носителя языка.

### Кыргызча

**1.18.15ке салыштырмалуу жаңы эмне бар**
- **Чектин жыйынтыгы сервердин эрежеси боюнча.** Жыйынтык алды = ТӨМӨН(салмактуу товарлардын суммасы тыйынга чейин) + ЖОГОРУ(калгандарынын суммасы); чектин пайыздык арзандатуусу тегизделген жыйынтык алдыдан эсептелет. Мисалдар: 13төн 0,375 кг жана 66дан 0,343 кг 27,51 берет; 22,638 → 22,63; 17,00 жана 2,5 % арзандатуу → 16,57. Чекте «Арзандатуусуз сумма − Арзандатуу = ЖЫЙЫНТЫК» көрүнөт.
- **Төлөм учурунда чек тоңдурулган.** Төлөм жүрүп жатканда сканерлөө жана издөө товар кошпойт; сан, арзандатуулар жана саптарды өчүрүү жеткиликсиз.
- **Тиркеме кулагандан кийин кайра төлөө экинчи сатууну түзбөйт.** Төлөм учурунда тиркеме жабылса же кулап калса, ошол эле чекти кайра төлөө төлөм ачкычы боюнча мурунку сатууну кайтарат.
- **Жаңыртуу: туруктуу чыгарылыштар гана.** Кадимки режимде туруктуу чыгарылыштар сунушталат, сынак чыгарылыштар — тестирөөчү режиминде. Жүктөлгөн файл өлчөмү боюнча, ал эми GitHub текшерүү суммасын жөнөтсө, SHA-256 боюнча салыштырылат; дал келбесе, ал өчүрүлөт жана орнотуу ачылбайт.

Калганы 1.18.15тегидей.

**Эмнени текшерүү керек** (толук кадамдар — тестирөөчү режиминде)
1. Салмактуу 13төн 0,375 кг жана 66дан 0,343 кг: жыйынтык 27,51. 17,00 кадимки товар, чекке 2,5 % арзандатуу: 16,57 жана «Арзандатуусуз сумма», «Арзандатуу», «ЖЫЙЫНТЫК» саптары.
2. «Төлөө» басып, төлөм жүрүп жатканда башка штрих-кодду сканерлеңиз: товар чекке түшпөйт.
3. «Төлөө» басып, дароо тиркемени акыркылар тизмесинен жабыңыз, кайра ачып, ошол эле чекти төлөңүз: сатуу бир (же такыр жок), экөө эмес.
4. Жаңыртуу: кадимки режимде сынак чыгарылыш сунушталбайт; «Жаңыртуу» → жүктөө → системалык орнотуу; жүктөө үзүлсө — түшүнүктүү билдирүү, жарым-жартылай файл өчүрүлөт.

**Чектөөлөр.** GitHub текшерүү суммасын жөнөтпөсө, файлдын өлчөмү гана текшерилет. Телефондордо жандуу текшерүү улантылууда — ошондуктан курама **сынак** үчүн. Кыргызча, өзбекче жана түркчө тексттер эне тилде сүйлөгөн адамсыз которулган.

### English

**What's new compared with 1.18.15**
- **Receipt total by the server's rule.** Subtotal = ROUND DOWN(sum of weighed goods to the cent) + ROUND UP(sum of the rest); a percent receipt discount is taken from the rounded subtotal. Examples: 0.375 kg × 13 and 0.343 kg × 66 give 27.51; 22.638 → 22.63; 17.00 with a 2.5 % discount → 16.57. The receipt shows “Amount without discount − Discount = TOTAL”.
- **The receipt is frozen during payment.** A scan or search does not add a product while a payment is in progress; quantity, discounts and deleting lines are unavailable.
- **After an app crash, paying again does not create a second sale.** If the app closes or crashes during payment, paying the same receipt again returns the earlier sale by the payment key.
- **Update: stable releases only.** In the normal mode stable releases are offered, test ones — in tester mode. The downloaded file is checked by size and, if GitHub sent a checksum, by SHA-256; on a mismatch it is deleted and the installer does not open.

Everything else is as in 1.18.15.

**What to check** (detailed steps are in tester mode)
1. Weighed 0.375 kg × 13 and 0.343 kg × 66: the total is 27.51. A regular product 17.00 with a 2.5 % receipt discount: 16.57 and the lines “Amount without discount”, “Discount”, “TOTAL”.
2. Tap “Pay” and, while the payment is in progress, scan another barcode: the product does not get into the receipt.
3. Tap “Pay”, at once close the app from the recent apps list, open it again and pay the same receipt: there is one sale (or none), not two.
4. Update: in the normal mode the test release is not offered; “Update” → download → the system installer; if the download is interrupted — a clear message and the partial file is deleted.

**Limits.** If GitHub did not send a checksum, only the file size is checked. Live checking on phones continues — that is why the build is **a test one**. The Kyrgyz, Uzbek and Turkish texts were translated without a native speaker.

### Türkçe

**1.18.15'e göre yenilikler**
- **Fiş toplamı sunucunun kuralına göre.** Ara toplam = AŞAĞI(tartılı ürünlerin toplamı kuruşa kadar) + YUKARI(diğerlerinin toplamı); yüzdelik fiş indirimi yuvarlanmış ara toplamdan hesaplanır. Örnekler: 13 somdan 0,375 kg ve 66 somdan 0,343 kg 27,51 verir; 22,638 → 22,63; %2,5 indirimli 17,00 → 16,57. Fişte «İndirimsiz tutar − İndirim = TOPLAM» görünür.
- **Ödeme sırasında fiş dondurulur.** Ödeme sürerken tarama ve arama ürün eklemez; miktar, indirimler ve satır silme kullanılamaz.
- **Uygulama çöktükten sonra tekrar ödeme ikinci bir satış oluşturmaz.** Ödeme sırasında uygulama kapanır veya çökerse, aynı fişi tekrar ödemek ödeme anahtarıyla önceki satışı döndürür.
- **Güncelleme: yalnızca kararlı sürümler.** Normal modda kararlı sürümler önerilir, test sürümleri — test uzmanı modunda. İndirilen dosya boyutuna göre ve GitHub bir sağlama toplamı gönderdiyse SHA-256'ya göre denetlenir; uyuşmazlıkta silinir ve kurulum açılmaz.

Gerisi 1.18.15'teki gibi.

**Ne kontrol edilmeli** (ayrıntılı adımlar test uzmanı modunda)
1. Tartılı 13 somdan 0,375 kg ve 66 somdan 0,343 kg: toplam 27,51. %2,5 fiş indirimli 17,00 sıradan ürün: 16,57 ve «İndirimsiz tutar», «İndirim», «TOPLAM» satırları.
2. «Öde»ye dokunun ve ödeme sürerken başka bir barkod okutun: ürün fişe girmez.
3. «Öde»ye dokunup hemen uygulamayı son uygulamalar listesinden kapatın, yeniden açıp aynı fişi ödeyin: tek satış var (veya hiç yok), iki değil.
4. Güncelleme: normal modda test sürümü önerilmez; «Güncelle» → indirme → sistem kurulumu; indirme kesilirse — anlaşılır mesaj, yarım dosya silinir.

**Sınırlar.** GitHub sağlama toplamı göndermediyse yalnızca dosya boyutu denetlenir. Telefonlarda canlı kontrol sürüyor, bu yüzden sürüm **test sürümüdür**. Kırgızca, Özbekçe ve Türkçe metinler anadili konuşan biri olmadan çevrildi.

### O'zbekcha

**1.18.15 ga nisbatan yangiliklar**
- **Chek jami server qoidasi bo'yicha.** Oraliq jami = PASTGA(vaznli mahsulotlar summasi tiyingacha) + YUQORIGA(qolganlari summasi); chekning foizli chegirmasi yaxlitlangan oraliq jamidan hisoblanadi. Misollar: 13 so'mdan 0,375 kg va 66 so'mdan 0,343 kg 27,51 beradi; 22,638 → 22,63; 2,5 % chegirmali 17,00 → 16,57. Chekda «Chegirmasiz summa − Chegirma = JAMI» ko'rinadi.
- **To'lov paytida chek muzlatilgan.** To'lov ketayotganda skanerlash va qidiruv mahsulot qo'shmaydi; miqdor, chegirmalar va qatorlarni o'chirish mavjud emas.
- **Ilova qulagandan keyin qayta to'lash ikkinchi sotuvni yaratmaydi.** To'lov paytida ilova yopilsa yoki qulab tushsa, xuddi shu chekni qayta to'lash to'lov kaliti bo'yicha avvalgi sotuvni qaytaradi.
- **Yangilash: faqat barqaror versiyalar.** Oddiy rejimda barqaror versiyalar taklif qilinadi, sinov versiyalari — sinovchi rejimida. Yuklangan fayl hajmi bo'yicha, GitHub nazorat summasini yuborgan bo'lsa, SHA-256 bo'yicha solishtiriladi; mos kelmasa o'chiriladi va o'rnatish ochilmaydi.

Qolgani 1.18.15 dagidek.

**Nimani tekshirish kerak** (batafsil qadamlar — sinovchi rejimida)
1. Vaznli 13 so'mdan 0,375 kg va 66 so'mdan 0,343 kg: jami 27,51. 2,5 % chek chegirmasi bilan 17,00 oddiy mahsulot: 16,57 va «Chegirmasiz summa», «Chegirma», «JAMI» qatorlari.
2. «To'lash»ni bosing va to'lov ketayotganda boshqa shtrix-kodni skanerlang: mahsulot chekka tushmaydi.
3. «To'lash»ni bosib, darhol ilovani so'nggilar ro'yxatidan yoping, qayta ochib, xuddi shu chekni to'lang: sotuv bitta (yoki umuman yo'q), ikkita emas.
4. Yangilash: oddiy rejimda sinov versiyasi taklif qilinmaydi; «Yangilash» → yuklash → tizim o'rnatishi; yuklash uzilsa — tushunarli xabar, chala fayl o'chiriladi.

**Cheklovlar.** GitHub nazorat summasini yubormasa, faqat fayl hajmi tekshiriladi. Telefonlarda jonli tekshiruv davom etmoqda — shuning uchun yig'ma **sinov** uchun. Qirg'izcha, o'zbekcha va turkcha matnlar ona tilida so'zlashuvchisiz tarjima qilingan.
