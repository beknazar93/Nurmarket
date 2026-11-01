## NurCRM Mobile 1.18.14 — тестовая сборка

**Тестовая версия — только для тестировщиков.** Не ставьте её в рабочем магазине без договорённости с нами.

Файл: `nurmarket-mobile-1.18.14.apk` (Android 8.0 и новее). Ставится поверх 1.18.x — подпись та же, вход сохраняется. Пять быстрых нажатий на логотип открывают **режим тестировщика** — список проверок с отметками «Проверено» (пункты этой версии — про обновление через GitHub, возврат и оплату).

### Русский

**Что нового по сравнению с 1.18.13**
- **Обновление через GitHub.** Когда выходит новая версия, на главной и в профиле появляется баннер «Доступна версия X». Нажмите «Обновить» — приложение скачает файл и предложит установить. В профиле есть строка «Проверить обновления». «Позже» прячет баннер на сутки.
- **Возврат учитывает уже возвращённое.** «Можно вернуть» берётся с сервера, поэтому нельзя вернуть больше, чем осталось.
- **Оплата не прерывается кнопкой «Назад».** Пока идёт оплата, экран не закрывается; чек следующего покупателя не очищается по ошибке.
- **Защита от повторной продажи после сбоя.** Ключ оплаты хранится, пока чек не очищен и черновик не записан; повтор того же чека возвращает прежнюю продажу, а не создаёт новую.
- **Бонусы с повтором.** Списание и начисление бонусов при обрыве связи встают в очередь и досылаются сами, без двойного списания.
- **Быстрее очередь.** Большая очередь неотправленных чеков считается быстро и не тормозит экран «Синхронизация».
- **Предупреждение о старых чеках.** Если в очереди есть чеки старше 5 суток, на экране «Синхронизация» красная плашка; чеки старше 7 суток сервер не принимает — показывается понятная причина.
- **Ограничения в поле «Получено».** Штрихкод в поле не войдёт (до 10 цифр до запятой и до 3 после); если «Получено» намного больше итога, перед оплатой спросят «Сдача N сом — верно?».

Остальное — как в 1.18.13.

**Что проверить** (подробные шаги — в режиме тестировщика)
1. Профиль → «Проверить обновления» при актуальной версии: «Установлена последняя версия». Когда выложена более новая — баннер «Доступна версия», «Обновить», скачивание, системная установка; вход и очередь на месте.
2. Возврат чека, по которому уже был частичный возврат: можно вернуть только остаток.
3. Во время оплаты нажмите «Назад» — экран остаётся.
4. Отключите связь, проведите продажу с бонусами, верните связь: бонусы ушли один раз.
5. В поле «Получено» отсканируйте штрихкод — цифры не вводятся; введите сумму в 20 раз больше итога — вопрос про сдачу.

**Ограничения.** Кнопка «Провести старые чеки (PIN владельца)» пока не добавлена — ждёт ответа сервера. Живая проверка на телефонах продолжается — поэтому сборка **тестовая**. Тексты на кыргызском, узбекском и турецком переведены без носителя языка.

### Кыргызча

**1.18.13кө салыштырмалуу жаңы эмне бар**
- **GitHub аркылуу жаңыртуу.** Жаңы версия чыкканда башкы экранда жана профилде «X версиясы жеткиликтүү» баннери пайда болот. «Жаңыртуу» басыңыз — тиркеме файлды жүктөп, орнотууну сунуштайт. Профилде «Жаңыртууларды текшерүү» сабы бар. «Кийинчерээк» баннерди бир күнгө жашырат.
- **Кайтаруу мурун кайтарылганды эсепке алат.** «Кайтарууга болот» серверден алынат, ошондуктан калгандан ашык кайтарууга болбойт.
- **Төлөм «Артка» баскычы менен үзүлбөйт.** Төлөм жүрүп жатканда экран жабылбайт; кийинки сатып алуучунун чеги жаңылыштык менен тазаланбайт.
- **Сбоодон кийин кайра сатуудан коргоо.** Төлөм ачкычы чек тазаланып, черновик жазылмайынча сакталат; ошол эле чекти кайталоо жаңысын түзбөйт, мурунку сатууну кайтарат.
- **Бонустар кайталоо менен.** Байланыш үзүлгөндө бонусту чыгымдоо жана эсептөө кезекке коюлуп, өзү жөнөтүлөт, эки эсе чыгымдалбайт.
- **Кезек тезирээк.** Жөнөтүлбөгөн чектердин чоң кезеги тез эсептелет жана «Синхрондоштуруу» экранын жайлатпайт.
- **Эски чектер тууралуу эскертүү.** Кезекте 5 суткадан эски чектер болсо, «Синхрондоштуруу» экранында кызыл белги чыгат; 7 суткадан эски чектерди сервер кабыл албайт — түшүнүктүү себеби көрсөтүлөт.
- **«Алынды» талаасындагы чектөөлөр.** Штрих-код талаага кирбейт (үтүргө чейин 10 цифрага, андан кийин 3кө чейин); «Алынды» жыйынтыктан бир кыйла көп болсо, төлөмдөн мурун «Кайтарым N сом — туурабы?» деп сурашат.

Калганы 1.18.13төгүдөй.

**Эмнени текшерүү керек** (толук кадамдар — тестирөөчү режиминде)
1. Профиль → «Жаңыртууларды текшерүү», версия акыркы болгондо: «Акыркы версия орнотулган». Жаңыраак чыкканда — «Версия жеткиликтүү» баннери, «Жаңыртуу», жүктөө, системалык орнотуу; кирүү жана кезек сакталат.
2. Мурун жарым-жартылай кайтарылган чекти кайтаруу: калганын гана кайтарууга болот.
3. Төлөм учурунда «Артка» басыңыз — экран калат.
4. Байланышты өчүрүп, бонус менен сатууну өткөрүңүз, байланышты кайтарыңыз: бонустар бир жолу гана кетти.
5. «Алынды» талаасында штрих-кодду сканерлеңиз — цифралар киргизилбейт; жыйынтыктан 20 эсе чоң сумманы киргизиңиз — кайтарым тууралуу суроо.

**Чектөөлөр.** «Эски чектерди өткөрүү (ээсинин PIN)» баскычы азырынча кошула элек — сервердин жообун күтөт. Телефондордо жандуу текшерүү улантылууда — ошондуктан курама **сынак** үчүн. Кыргызча, өзбекче жана түркчө тексттер эне тилде сүйлөгөн адамсыз которулган.

### English

**What's new compared with 1.18.13**
- **Update through GitHub.** When a new version is out, a banner “Version X is available” appears on the home screen and in the profile. Tap “Update” — the app downloads the file and offers to install it. The profile has a “Check for updates” row. “Later” hides the banner for a day.
- **A return accounts for what was already returned.** “Can return” comes from the server, so you cannot return more than is left.
- **Payment is not interrupted by the “Back” button.** While a payment is in progress the screen does not close; the next customer's receipt is not cleared by mistake.
- **Protection against a repeat sale after a failure.** The payment key is kept until the receipt is cleared and the draft is saved; repeating the same receipt returns the earlier sale instead of creating a new one.
- **Bonuses with retry.** Spending and earning bonuses on a dropped connection wait in a queue and are sent on their own, without double spending.
- **Faster queue.** A big queue of unsent receipts is counted quickly and does not slow down the “Sync” screen.
- **Warning about old receipts.** If the queue holds receipts older than 5 days, the “Sync” screen shows a red banner; the server does not accept receipts older than 7 days — a clear reason is shown.
- **Limits in the “Received” field.** A barcode cannot get into the field (up to 10 digits before the decimal point and 3 after); if “Received” is far larger than the total, you are asked “Change N som — correct?” before paying.

Everything else is as in 1.18.13.

**What to check** (detailed steps are in tester mode)
1. Profile → “Check for updates” on the latest version: “The latest version is installed”. When a newer one is published — the “Version available” banner, “Update”, download, the system install; sign-in and queue are intact.
2. Return a receipt that already had a partial return: only the rest can be returned.
3. Press “Back” during a payment — the screen stays.
4. Turn off the connection, make a sale with bonuses, restore the connection: the bonuses were sent once.
5. Scan a barcode into the “Received” field — no digits are entered; type an amount 20 times the total — a question about the change.

**Limits.** The “Accept old receipts (owner PIN)” button is not added yet — it waits for the server's answer. Live checking on phones continues — that is why the build is **a test one**. The Kyrgyz, Uzbek and Turkish texts were translated without a native speaker.

### Türkçe

**1.18.13'e göre yenilikler**
- **GitHub üzerinden güncelleme.** Yeni sürüm çıktığında ana ekranda ve profilde «X sürümü mevcut» başlığı görünür. «Güncelle»ye dokunun — uygulama dosyayı indirir ve kurmayı önerir. Profilde «Güncellemeleri denetle» satırı vardır. «Sonra» başlığı bir gün gizler.
- **İade, önceden iade edileni hesaba katar.** «İade edilebilir» miktar sunucudan gelir, bu yüzden kalandan fazlası iade edilemez.
- **Ödeme «Geri» düğmesiyle kesilmez.** Ödeme sürerken ekran kapanmaz; sıradaki müşterinin fişi yanlışlıkla temizlenmez.
- **Arızadan sonra tekrar satışa karşı koruma.** Ödeme anahtarı, fiş temizlenip taslak yazılana kadar saklanır; aynı fişi tekrarlamak yeni satış oluşturmaz, önceki satışı döndürür.
- **Tekrar denemeli bonuslar.** Bağlantı koptuğunda bonus harcama ve ekleme sıraya girer ve kendiliğinden gönderilir, çift harcama olmaz.
- **Daha hızlı sıra.** Gönderilmemiş fişlerin büyük sırası hızlı sayılır ve «Eşitleme» ekranını yavaşlatmaz.
- **Eski fişler için uyarı.** Sırada 5 günden eski fiş varsa «Eşitleme» ekranında kırmızı bir uyarı çıkar; 7 günden eski fişleri sunucu kabul etmez — anlaşılır neden gösterilir.
- **«Alınan» alanındaki sınırlar.** Barkod alana giremez (virgülden önce en çok 10, sonra 3 hane); «Alınan» toplamdan çok fazlaysa ödemeden önce «Para üstü N som — doğru mu?» diye sorulur.

Gerisi 1.18.13'teki gibi.

**Ne kontrol edilmeli** (ayrıntılı adımlar test uzmanı modunda)
1. Profil → «Güncellemeleri denetle», sürüm güncelken: «En son sürüm yüklü». Daha yeni sürüm yayınlandığında — «Sürüm mevcut» başlığı, «Güncelle», indirme, sistem kurulumu; giriş ve sıra yerinde.
2. Daha önce kısmen iade edilmiş bir fişi iade edin: yalnızca kalan iade edilebilir.
3. Ödeme sırasında «Geri»ye basın — ekran kalır.
4. Bağlantıyı kapatın, bonuslu satış yapın, bağlantıyı açın: bonuslar bir kez gitti.
5. «Alınan» alanında barkod okutun — rakamlar girilmez; toplamın 20 katı tutar yazın — para üstü sorusu.

**Sınırlar.** «Eski fişleri işle (sahibin PIN'i)» düğmesi henüz eklenmedi — sunucunun yanıtını bekliyor. Telefonlarda canlı kontrol sürüyor, bu yüzden sürüm **test sürümüdür**. Kırgızca, Özbekçe ve Türkçe metinler anadili konuşan biri olmadan çevrildi.

### O'zbekcha

**1.18.13 ga nisbatan yangiliklar**
- **GitHub orqali yangilash.** Yangi versiya chiqqanda bosh ekranda va profilda «X versiyasi mavjud» bannerini ko'rasiz. «Yangilash»ni bosing — ilova faylni yuklab, o'rnatishni taklif qiladi. Profilda «Yangilanishlarni tekshirish» qatori bor. «Keyinroq» bannerni bir kunga yashiradi.
- **Qaytarish avval qaytarilganini hisobga oladi.** «Qaytarish mumkin» serverdan olinadi, shuning uchun qolganidan ko'p qaytarib bo'lmaydi.
- **To'lov «Orqaga» tugmasi bilan uzilmaydi.** To'lov ketayotganda ekran yopilmaydi; keyingi xaridorning cheki xato bilan tozalanmaydi.
- **Nosozlikdan keyin takroriy sotuvdan himoya.** To'lov kaliti chek tozalanib, qoralama yozilmaguncha saqlanadi; xuddi shu chekni takrorlash yangi sotuv yaratmaydi, avvalgisini qaytaradi.
- **Takrorlanadigan bonuslar.** Aloqa uzilganda bonusni sarflash va hisoblash navbatga qo'yilib, o'zi yuboriladi, ikki marta sarflanmaydi.
- **Navbat tezroq.** Yuborilmagan cheklarning katta navbati tez sanaladi va «Sinxronlash» ekranini sekinlashtirmaydi.
- **Eski cheklar haqida ogohlantirish.** Navbatda 5 kundan eski cheklar bo'lsa, «Sinxronlash» ekranida qizil belgi chiqadi; 7 kundan eski cheklarni server qabul qilmaydi — tushunarli sabab ko'rsatiladi.
- **«Qabul qilindi» maydonidagi cheklovlar.** Shtrix-kod maydonga kirmaydi (verguldan oldin 10 ta, keyin 3 ta raqamgacha); «Qabul qilindi» jamidan ancha ko'p bo'lsa, to'lovdan oldin «Qaytim N so'm — to'g'rimi?» deb so'raladi.

Qolgani 1.18.13 dagidek.

**Nimani tekshirish kerak** (batafsil qadamlar — sinovchi rejimida)
1. Profil → «Yangilanishlarni tekshirish», versiya yangi bo'lganda: «Oxirgi versiya o'rnatilgan». Yangiroq chiqqanda — «Versiya mavjud» banneri, «Yangilash», yuklash, tizim o'rnatishi; kirish va navbat joyida.
2. Avval qisman qaytarilgan chekni qaytaring: faqat qolganini qaytarish mumkin.
3. To'lov paytida «Orqaga»ni bosing — ekran qoladi.
4. Aloqani o'chiring, bonusli sotuv o'tkazing, aloqani qaytaring: bonuslar bir marta ketdi.
5. «Qabul qilindi» maydonida shtrix-kodni skanerlang — raqamlar kiritilmaydi; jamidan 20 baravar katta summa kiriting — qaytim haqida savol.

**Cheklovlar.** «Eski cheklarni o'tkazish (egasi PIN)» tugmasi hali qo'shilmagan — serverning javobini kutmoqda. Telefonlarda jonli tekshiruv davom etmoqda — shuning uchun yig'ma **sinov** uchun. Qirg'izcha, o'zbekcha va turkcha matnlar ona tilida so'zlashuvchisiz tarjima qilingan.
