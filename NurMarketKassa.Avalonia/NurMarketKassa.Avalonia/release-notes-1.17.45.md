## v1.17.45

Исправления по списку магазина: опт, скидка в убыток, предоплата долга в чеке, прокат в отчёте смены, склад, выход из аккаунта при сбое сервера.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).

Обновление не меняет ваши настройки. После обновления касса один раз заново загрузит каталог с сервера, чтобы записать оптовые цены.

---

# Русский

### Новое

- **Тумблер «Розничный / Оптовый»** над чеком. «Оптовый» — товары с оптовой ценой в этом чеке и всё, что отсканируете дальше, идут по опту. Новый чек снова розничный, чтобы следующего покупателя не пробить по опту по ошибке.
- **Галочка «Опт» у товара** в чеке — опт на отдельный товар.
- **«Продажа в убыток»**: если скидка делает товар дешевле закупки, появляется окно с кнопкой «Я знаю что делаю». Без неё скидка не применяется, «Отмена» возвращает прежнюю скидку.
- **Прокат в отчёте закрытия смены**: выдано, возвращено, залоги, удержано, штрафы, на руках и просрочено. На экране и в печати.
- **Склад: кнопка «Обновить»** — товары и остатки заново с сервера.

### Было → стало

| Что | Было | Стало |
|---|---|---|
| Оптовая цена | Терялась после перезапуска кассы и после каждой продажи: кнопка «Опт» не появлялась, цена не менялась | Хранится в кассе, опт работает всегда |
| Режим скидки | Каждый раз открывался «Процент» | Открывается последний выбранный: процент или сом |
| Чек «в долг» с предоплатой | «ВНЕСЕНО: 300» — непонятно, наличными или безналом | «ВНЕСЕНО НАЛИЧНЫМИ» или «ВНЕСЕНО БЕЗНАЛОМ» |
| Новый весовой товар | Поле PLU сдвигало цены вниз, строка обрезалась | PLU в одной строке с «Весовой», цены на месте |
| Сбой сервера NurCRM | Если в это время истекал вход, касса выходила из аккаунта | Касса остаётся в аккаунте и работает дальше |
| Остаток после продажи | Уменьшался только через 1–2 минуты | Уменьшается сразу после оплаты |

### Проверено (04.10, тестовый аккаунт)

- Тумблер «Оптовый»: «Адыгене 1л» встал по оптовой цене 45 вместо 50, галочка снимается — снова 50.
- Скидка 10 сом при закупке 40: окно «Продажа в убыток» (убыток 7,50). «Отмена» вернула итог 42,50, «Я знаю что делаю» применила скидку (32,50).
- Окно скидки после выбора «сом» открылось снова в «сом».
- Продажа: остаток «Адыгене 1л» сразу 98 вместо 99; тумблер после продажи вернулся в «Розничный».
- Отчёт смены 02.10: «Выдано 7, залог +3000, под документ 2, возвращено 5, штрафы 400, на руках 3, просрочено 2».
- Склад: «Обновить» загрузил каталог с сервера. Новый товар: PLU в одной строке, цены на месте.
- Текст чека «в долг»: «ВНЕСЕНО БЕЗНАЛОМ 30 / В ДОЛГ 70» и «ВНЕСЕНО НАЛИЧНЫМИ».
- Выход из аккаунта при сбое сервера — исправлен и проверен по коду, сбой сервера вживую не воспроизводился. Возврат, оплата долга и работа без интернета в этот раз не перепроверялись.

---

# Кыргызча

- **Жаңы:** чектин үстүндө «Чекене / Дүң» которгуч жана товардагы «Дүң» белгиси. Дүң баа кайра жүргүзгөндөн жана сатуудан кийин жоголбойт.
- **Жаңы:** арзандатуу товарды сатып алуу баасынан арзан кылса — «Эмне кылып жатканымды билем» баскычы бар эскертүү; ырастабасаңыз арзандатуу колдонулбайт.
- **Жаңы:** сменаны жабуу отчётунда «Сменадагы прокат»; кампада «Жаңылоо» баскычы.
- **Оңдолду:** арзандатуу терезеси режимди эстейт; «карызга» чекте алдын ала төлөм накталай же накталай эмес экени басылат; салмактуу товардын PLU талаасы бааларды түртпөйт; сервер иштебей калганда касса аккаунттан чыкпайт; калдык сатуудан кийин дароо азаят.

---

# English

- **New:** a “Retail / Wholesale” switch above the receipt and a “Wholesale” tick on each item. The wholesale price is no longer lost after a restart or a sale.
- **New:** a discount that sells an item below cost shows a warning with an “I know what I'm doing” button; without it the discount is not applied.
- **New:** “Rentals this shift” in the shift closing report; a “Refresh” button in the warehouse.
- **Fixed:** the discount window remembers percent/som; a credit receipt shows whether the prepayment was cash or non-cash; the PLU field no longer pushes prices down; the till stays logged in when the server is down; stock goes down right after a sale.

---

# Türkçe

- **Yeni:** fişin üstünde «Perakende / Toptan» anahtarı ve üründe «Toptan» işareti. Toptan fiyat yeniden başlatmadan ve satıştan sonra kaybolmuyor.
- **Yeni:** indirim ürünü alış fiyatının altına düşürürse «Ne yaptığımı biliyorum» düğmeli uyarı; onay olmadan indirim uygulanmaz.
- **Yeni:** vardiya kapanış raporunda «Vardiyadaki kiralamalar»; depoda «Yenile» düğmesi.
- **Düzeltildi:** indirim penceresi yüzde/som modunu hatırlar; veresiye fişinde ön ödemenin nakit mi nakitsiz mi olduğu yazar; PLU alanı fiyatları itmiyor; sunucu çalışmazken kasa hesaptan çıkmıyor; stok satıştan hemen sonra azalır.

---

# O'zbekcha

- **Yangi:** chek ustida «Chakana / Ulgurji» tugmasi va mahsulotda «Ulgurji» belgisi. Ulgurji narx qayta ishga tushirish va sotuvdan keyin yo'qolmaydi.
- **Yangi:** chegirma mahsulotni xarid narxidan arzonga tushirsa — «Nima qilayotganimni bilaman» tugmali ogohlantirish; tasdiqsiz chegirma qo'llanmaydi.
- **Yangi:** smena yopilishi hisobotida «Smenadagi prokat»; omborda «Yangilash» tugmasi.
- **Tuzatildi:** chegirma oynasi foiz/so'm rejimini eslab qoladi; «qarzga» chekda oldindan to'lov naqd yoki naqdsiz ekani yoziladi; PLU maydoni narxlarni surmaydi; server ishlamaganda kassa akkauntdan chiqmaydi; qoldiq sotuvdan keyin darhol kamayadi.
