## v1.17.33

Срочное обновление для всех: программа владельца ставится на новых компьютерах. Вошли и изменения тестовой 1.17.32.

**Отдельный установщик программы владельца** — файл `NurMarketOwner-owner-Setup.exe` в этом релизе (на случай, если ярлык «NurMarket Владелец» после установки кассы не появился или владелец работает на другом компьютере без кассы).

---

# Русский

### Новое

- **Тема «Кыргыз»**: цвета флага — красный и золото на войлочном фоне шырдака. Кыргызские орнаменты, в каждом месте свой:
  - фон окон и полоса под шапкой — кочкор мүйүз;
  - окна и настройки — ит куйрук;
  - каталог — суу;
  - корзина — тумар;
  - плитки товаров — бадам;
  - кнопки — ромб.

  Экран покупателя тоже в национальном виде. В тёмном режиме — золото на бордовом.
- **Плитки «Финансов» нажимаются**, как в Z-отчёте. Выручка, возвраты, наличные, безнал, средний чек и чеки открываются подробно: чеки с товарами и итог за выбранный период.
- **Отдельный установщик программы владельца** (см. выше).

### Исправлено: было → стало

| Что | Было | Стало |
|---|---|---|
| Программа владельца на новых установках | Ярлык делался через Windows Script Host. Где тот отключён или его блокирует антивирус, ярлык молча не создавался, и программы владельца не было | Ярлык пишется системным способом Windows. Если при установке не получилось, касса создаёт его при первом запуске. Итог пишется в `Logs\owner-shortcut.log` |
| Финансы → «Возвраты» | Показывался журнал удалений строк из корзины до оплаты. Настоящие возвраты, особенно частичные, не показывались вовсе | Возвраты с сервера — полные и частичные, с номером чека, видом, составом и точной суммой. Двойной щелчок открывает чек |
| Голосовое управление | Слово «касса» искалось в любом месте фразы: разговоры у кассы добавляли товары | Команда — только отдельное слово «касса»/«каса» в начале фразы |
| Настройки на узком экране | Строки в 2–3 колонки сжимались, поля обрезались | Уже 760 точек строки встают столбиком |
| Окно «Весы» (ky/en/uz) | «Код в ШК», цена, «Ед.», фильтры категорий и «Показать» обрезались | Всё видно полностью на 5 языках |

### Проверено

- Ярлык программы владельца:
  - хук установщика (`--veloapp-install`) создаёт ярлыки на рабочем столе и в «Пуске» с ключом `--owner`;
  - после удаления отметки и ярлыков касса при запуске создала их заново.
- «Возвраты» за сентябрь на тестовом аккаунте: 24 записи на 136 297,99 — столько же отдаёт сервер. Частичные №126, 682, 725, 736 отмечены «Частичный возврат».
- Плитка «Возвраты»: окно подробностей открывается, итог совпадает с плиткой.
- Тема «Кыргыз»: касса, меню, настройки и программа владельца, светлый и тёмный режим — снимками.
- Окно «Весы» — снимками на ky/uz/en/tr.
- Сборка установлена на рабочий компьютер: касса и программа владельца запускаются.

### Не проверено

- Полный регресс продаж, возврата, долга и смены на этой сборке (выпуск срочный по просьбе владельца).
- Экран покупателя в теме «Кыргыз» на настоящем втором мониторе.
- Установка на чистый компьютер с отключённым Windows Script Host.

---

# Кыргызча

- **«Кыргыз» темасы**: желектин кызыл жана алтын түсү, ар бир жерде өз оюусу — кочкор мүйүз, ит куйрук, суу, тумар, бадам, ромб. Сатып алуучунун экраны да улуттук көрүнүштө.
- **«Каржынын» плиткалары басылат** — Z-отчёттогудай толук.
- **Ээсинин программасынын өзүнчө орнотуучусу** — `NurMarketOwner-owner-Setup.exe`.

| Эмне | Мурун | Азыр |
|---|---|---|
| Жаңы орнотууларда ээсинин программасы | Энбелги Windows Script Host аркылуу жасалчу; ал өчүрүлгөн же антивирус бөгөттөгөн жерде энбелги түзүлбөй калчу | Энбелги Windowsтун системалык жолу менен жазылат; орнотууда болбосо — касса ишке кирерде түзөт |
| Каржы → «Кайтаруулар» | Себеттен өчүрүүлөрдүн журналы көрүнчү, чыныгы (жарым-жартылай) кайтаруулар жок болчу | Сервердеги толук жана жарым-жартылай кайтаруулар — чектин номери, курамы жана так суммасы менен |
| Үн менен башкаруу | «касса» фразанын каалаган жеринен изделчү | Фразанын башындагы өзүнчө «касса» гана буйрук |
| Тар экрандагы жөндөөлөр, «Таразалар» терезеси | Саптар кысылып, тилкелер кесилчү | Бардыгы толук көрүнөт |

---

# English

- **“Kyrgyz” theme**: the red and gold of the flag, a different Kyrgyz ornament in each area; the customer display too.
- **Finance tiles are clickable** — detailed like the Z-report.
- **Separate owner app installer** — `NurMarketOwner-owner-Setup.exe`.

| What | Before | Now |
|---|---|---|
| Owner app on new installs | The shortcut was made via Windows Script Host; where it is disabled or blocked by antivirus, no shortcut appeared | Written with the native Windows API; if installation fails to make it, the till creates it at startup |
| Finance → “Returns” | Showed the cart deletion log; real (partial) returns were missing | Server returns, full and partial, with receipt number, items and exact amount |
| Voice control | “касса” was matched anywhere in a phrase | Only a separate “касса” at the start is a command |
| Narrow-screen settings, “Scales” window | Rows squeezed, columns cut | Everything fits |

---

# Türkçe

- **«Kırgız» teması**: bayrağın kırmızısı ve altını, her alanda farklı bir Kırgız süslemesi; müşteri ekranı da.
- **Finans kutucukları tıklanabilir** — Z raporundaki gibi ayrıntılı.
- **Ayrı sahip programı yükleyicisi** — `NurMarketOwner-owner-Setup.exe`.

| Ne | Önce | Şimdi |
|---|---|---|
| Yeni kurulumlarda sahip programı | Kısayol Windows Script Host ile yapılıyordu; kapalıysa veya antivirüs engellerse kısayol oluşmuyordu | Windows’un yerel yöntemiyle yazılır; kurulumda olmazsa kasa açılışta oluşturur |
| Finans → «İadeler» | Sepetten silme günlüğü gösteriliyordu, gerçek (kısmi) iadeler yoktu | Sunucudaki tam ve kısmi iadeler — fiş numarası, içerik ve kesin tutarla |
| Sesli kontrol | «касса» cümlenin her yerinde aranıyordu | Yalnızca baştaki ayrı «касса» komuttur |
| Dar ekranda ayarlar, «Teraziler» penceresi | Satırlar sıkışıyor, sütunlar kesiliyordu | Her şey sığıyor |

---

# O‘zbekcha

- **«Qirg‘iz» mavzusi**: bayroqning qizil va oltin ranglari, har joyda boshqa qirg‘iz naqshi; xaridor ekrani ham.
- **«Moliya» plitkalari bosiladi** — Z-hisobotdagidek batafsil.
- **Egasi dasturining alohida o‘rnatuvchisi** — `NurMarketOwner-owner-Setup.exe`.

| Nima | Oldin | Hozir |
|---|---|---|
| Yangi o‘rnatishlarda egasi dasturi | Yorliq Windows Script Host orqali yaratilardi; u o‘chirilgan yoki antivirus bloklagan joyda yorliq paydo bo‘lmasdi | Windows’ning tizim usuli bilan yoziladi; o‘rnatishda bo‘lmasa kassa ishga tushganda yaratadi |
| Moliya → «Qaytarishlar» | Savatdan o‘chirishlar jurnali ko‘rsatilardi, haqiqiy (qisman) qaytarishlar yo‘q edi | Serverdagi to‘liq va qisman qaytarishlar — chek raqami, tarkibi va aniq summasi bilan |
| Ovozli boshqaruv | «касса» iboraning istalgan joyida qidirilardi | Faqat boshidagi alohida «касса» buyruq |
| Tor ekrandagi sozlamalar, «Tarozilar» oynasi | Qatorlar siqilib, ustunlar kesilardi | Hammasi sig‘adi |
