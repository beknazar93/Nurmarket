## v1.17.35

Обновление для всех клиентов: исправлены экранная клавиатура и окно «Просмотр смены», в программе владельца в «Сводке» появился период «Спец. дата».

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца);
- `NurMarketOwner-owner-Setup.exe` — только программа владельца «NurMarket Владелец», например на домашний компьютер без кассы.

---

# Русский

### Новое

- **«Сводка» в программе владельца — период «Спец. дата».** Рядом с «Сегодня / Неделя / Месяц» выберите любые даты «с — по». Выручка, чеки, средний чек и прибыль считаются за эти дни. Сравнение идёт с таким же по длине периодом прямо перед ними, а график показывает выручку по дням периода.

### Исправлено: было → стало

| Что | Было | Стало |
|---|---|---|
| Экранная клавиатура | После нажатия кнопки «Клавиатура» набранное на экранной клавиатуре пропадало: кнопка забирала курсор из поля, и буквы уходили «в никуда» | Кнопка не забирает курсор из поля. Если поле не выбрано, курсор сам ставится в поиск товара. Касса, окно входа и карточка товара |
| Экранная клавиатура уже открыта, но свёрнута | Повторное нажатие ничего не показывало | Клавиатура поднимается поверх окон |
| Окно «Просмотр смены» | При длинном списке «Товары за смену» кнопки «Печать» и «Закрыть» уходили за нижний край экрана | Кнопки всегда внизу окна, список прокручивается, окно стало ниже. Блок «Смена» теперь в две колонки |

### Проверено

- Кнопка «Клавиатура» в кассе открывает экранную клавиатуру Windows, в журнале есть запись о запуске.
- «Просмотр смены» со сменой на 33 товара: окно целиком на экране, кнопки «Печать» и «Закрыть» видны, список прокручивается. Короткая смена показывается компактно.
- «Спец. дата» в «Сводке»: период 24.09–30.09 применяется, возврат на «Сегодня» работает.
- Касса и программа владельца запускаются, версия 1.17.35.

### Не проверено

- Ввод текста с экранной клавиатуры пальцем на сенсорном экране (проверен запуск клавиатуры, сам набор — нет).
- Полный регресс продаж, долгов и смены: в этой версии код продаж не менялся.

---

# Кыргызча

- **Ээсинин программасындагы «Жыйынтык» — «Башка дата» мезгили.** Каалаган күндөрдү «баштап — чейин» тандаңыз: түшүм, чектер жана пайда ошол күндөр үчүн эсептелет, алардан мурунку ушундай мезгил менен салыштырылат.
- **Экрандагы баскычтоп оңдолду.** Терилген текст мындан ары жоголбойт: баскыч курсорду талаадан албайт, талаа тандалбаса курсор товар издөөгө коюлат. Жыйылган баскычтоп терезелердин үстүнө чыгат.
- **«Сменаны көрүү» терезеси.** «Басып чыгаруу» жана «Жабуу» баскычтары дайыма көрүнөт — товарлардын узун тизмеси сыдырылат, терезе жыйнактуу болду.

---

# English

- **Owner app “Overview” — “Custom dates” period.** Pick any “from — to” dates: revenue, receipts and profit are calculated for those days and compared with the same-length period right before them.
- **On-screen keyboard fixed.** Typing is no longer lost: the button keeps the cursor in the field, and if no field is selected the cursor goes to product search. A minimized keyboard is brought to the front.
- **“View shift” window.** The “Print” and “Close” buttons are always visible — a long product list scrolls, and the window is more compact.

---

# Türkçe

- **Sahip programı «Özet» — «Özel tarih» dönemi.** İstediğiniz «başlangıç — bitiş» tarihlerini seçin: ciro, fişler ve kâr bu günler için hesaplanır ve hemen öncesindeki aynı uzunluktaki dönemle karşılaştırılır.
- **Ekran klavyesi düzeltildi.** Yazılanlar artık kaybolmuyor: düğme imleci alandan almıyor, alan seçili değilse imleç ürün aramasına gidiyor. Küçültülmüş klavye öne getiriliyor.
- **«Vardiyayı görüntüle» penceresi.** «Yazdır» ve «Kapat» düğmeleri her zaman görünür — uzun ürün listesi kaydırılır, pencere daha derli toplu.

---

# O‘zbekcha

- **Egasining dasturi «Umumiy ko‘rinish» — «Boshqa sana» davri.** Istalgan «dan — gacha» sanalarni tanlang: tushum, cheklar va foyda shu kunlar uchun hisoblanadi va ulardan oldingi xuddi shunday davr bilan solishtiriladi.
- **Ekran klaviaturasi tuzatildi.** Yozilganlar endi yo‘qolmaydi: tugma kursorni maydondan olmaydi, maydon tanlanmagan bo‘lsa kursor tovar qidiruviga qo‘yiladi. Yig‘ilgan klaviatura oldinga chiqariladi.
- **«Smenani ko‘rish» oynasi.** «Chop etish» va «Yopish» tugmalari doim ko‘rinadi — uzun tovarlar ro‘yxati aylantiriladi, oyna ixchamroq.
