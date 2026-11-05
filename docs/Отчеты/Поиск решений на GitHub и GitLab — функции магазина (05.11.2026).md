# Поиск с (05.11.2026)

**Задача:** найти в открытых проектах функции, которые автоматизируют или улучшат NurCRM Kassa (касса, склад, накладные, долги, смены, владелец, Нур Советник, Telegram, 5 языков).
**Как искали:** GitHub API без токена (звёзды, дата последнего push, лицензия — на 05.11.2026), GitLab API, документация проектов (Odoo, Medusa, Saleor, Invoice Ninja, Loyverse), веб-поиск по КР (ELQR, MBank, ККМ ГНС). Чужой код не клонировали и не запускали — хватило README, документации и списков модулей.
**Сверка с нашим кодом:** перед тем как писать «нет у нас», проверено Grep'ом по `NurMarketKassa.Avalonia/` (что уже есть — раздел 1).

Обозначения: польза для магазина **1–5**; трудоёмкость **S** (до 2 дней), **M** (неделя), **L** (больше недели или нужен внешний партнёр). «Умар» — нужна доработка сервера NurCRM.
Лицензии: **<span style="color:#c00">красным</span>** — код копировать в закрытый продукт нельзя, только идея. MIT / BSD / Apache-2.0 — можно брать код с сохранением уведомления об авторстве (но всё равно лучше писать своё на C#).

---

## 1. Что у нас уже есть (не дублировать)

| Функция | Где у нас | Чем хуже, чем у лучших |
|---|---|---|
| Рекомендуемый заказ поставщику + отправка в WhatsApp | `PurchasesWindow.Recommend.cs`, `Purchases/PurchasePlanner.cs` (`PlannerSettings(PeriodDays=30, CoverDays=14, SafetyDays=3)`) | Простая средняя продаж: нет учёта дней «без остатка», кратности упаковки, срока поставки, редких товаров, нет утренней авто-проверки |
| ABC-анализ, сезонность | `AbcAnalysisWindow`, `Views/Analytics/SeasonalityView.cs` | Нет XYZ (стабильность спроса) |
| Ревизия по сканеру | `WarehouseWindow.Revision.cs`, `RevisionFinishDialog.cs` | Только полная; нет циклической, нет причин расхождений, нет порога с подтверждением |
| Сроки годности | `ProductExpiryIndex.cs` (с сервера `expiring_products`), метки на складе и плитках | Одна дата на товар, нет партий; на продаже не предупреждает |
| Контроль удалений из чека с причиной, слепой пересчёт кассы | `CartRemovalReasonDialog`, `CartRemovalLogStore`, `CloseShiftDialog` (`BlindCashCount`) | Журнал локальный на ПК кассы, нет сводки «подозрительный кассир», нет оповещений по правилам |
| Telegram-бот владельца, утренний отчёт, оповещения о сбоях | `TelegramBotService`, `TelegramReportBuilder`, `OwnerMorningReport` | Оповещения только технические; правил «сообщи, если…» нет |
| Лояльность, бонусы (частично на сервере), комплекты, опт | `ClientLoyaltyStore`, `ServerLoyalty`, `BundleItemPickerDialog` | Нет акций по правилам, купонов, сертификатов |
| QR ELQR с суммой (MBank) | `Host/PaymentQrs.cs`, `ElqrQrBuilder` | Оплата не подтверждается автоматически |
| Отправка чека WhatsApp/Telegram | `Services/ReceiptActionBar.cs` | Вручную, кассир перетаскивает PDF |
| Напоминания должникам по расписанию | `DebtReminderPlanner.cs` | Только считает «кому пора», отправляет владелец руками |
| Перемещения в филиалы, журнал правок прихода, «Некорректные чеки», поиск фото (Open Food Facts) | `StockTransferDialog.Branches.cs`, `ReceiptEditAuditLog.cs`, `IrregularReceiptsWindow`, `ProductPhotoFinder.cs` | — |

---

## 2. Топ-15 «сделать первыми»

| # | Функция | Подсмотрели у | Польза | Труд | Нужен Умар? |
|---|---|---|---|---|---|
| 1 | **Предупреждение о просрочке при продаже** + «списать просроченное» одной кнопкой | OCA `pos_product_expiry`, Odoo `product_expiry`, Grocy | 5 | S | Нет |
| 2 | **Антифрод кассира: сводка исключений + оповещения в Telegram** | Exception-based reporting (DTiQ, 3xLOGIC — коммерческие), OCA `pos_order_remove_line`/`pos_user_restriction`, NexoPOS «activity tracking» | 5 | M | Желательно (журнал на сервере) |
| 3 | **Автозаказ v2**: потерянный спрос, кратность, срок поставки, редкие товары, утренняя проверка | Odoo reordering rules + lead times, ERPNext Auto Material Request, Loyverse low-stock, statsforecast (Croston/TSB) | 5 | M | Нет |
| 4 | **Стенд-заглушка NurCRM для тестов + CI сборки** (тесты без боевой базы) | WireMock.Net, Verify, Avalonia.Headless, GitHub Actions | 5 (для качества выпусков) | S–M | Нет |
| 5 | **Оповещения «если… — сообщи» и отчёты по расписанию в Telegram** | Frappe Notification / Auto Email Report, Loyverse daily low stock | 4 | S | Нет |
| 6 | **XYZ к ABC** (матрица 9 клеток) | adib0073/Importance_Forecastability_Segmentation, ERPNext/OCA `stock_demand_estimate` | 4 | S | Нет |
| 7 | **Подсказки цены**: целевая наценка, «красивое» округление, закупка подорожала → новая цена | Odoo pricelists (`price_round`, `price_surcharge`), ERPNext Pricing Rule | 4 | S | Нет |
| 8 | **Электронный чек через Telegram-бот магазина / QR на чеке** | OSPOS (email/SMS чеки), Odoo «QR code on ticket», Loyverse email receipts, aiogram | 4 | M | Для страницы чека — да; через бот — нет |
| 9 | **Циклическая инвентаризация + причины расхождений + порог с подтверждением** | OCA `stock_cycle_count`, `stock_inventory_discrepancy`, `stock_change_qty_reason`; Loyverse partial count; InvenTree stocktake | 4 | M | Нет (причину — в примечание) |
| 10 | **Акции по правилам и промокоды** («2+1», скидка на категорию по часам/дням, от суммы чека, лимит использований) | Medusa Promotion module, Saleor promotions/vouchers, Bagisto cart rules, Odoo loyalty, ERPNext Pricing Rule | 5 | L | Да (сверка скидки и округления сервера) |
| 11 | **Себестоимость с доставкой** (landed cost: доставку/расходы прихода разнести на товары) | ERPNext Landed Cost Voucher, Odoo `stock_landed_costs` | 4 | M | Нет (новая закупочная цена строки) |
| 12 | **Права кассира по действиям + PIN старшего** (скидка > X %, возврат, смена цены, ящик без продажи) | Odoo Basic/Advanced rights, OCA `pos_user_restriction`, NexoPOS roles | 4 | M | Желательно (роль в профиле) |
| 13 | **Сверка банковской выписки** (MBank/Optima Excel → безнал смен, оплаты поставщикам) | Invoice Ninja bank rules, Akaunting reconciliation, ERPNext Bank Reconciliation Tool | 4 | M | Нет (хранить итог — лучше на сервере) |
| 14 | **Партии со сроками при приходе, FEFO** | Odoo lots/expiry, Dolibarr lots, OpenBoxes, POS Awesome batch | 5 | L | Да |
| 15 | **QR-оплата с автоподтверждением** (ELQR через банк) | Odoo «QR code payments» и платёжные терминалы; в КР открытого кода нет | 5 | L | Банк / МПЦ |

### Подробно

**1. Предупреждение о просрочке на кассе** (5 / S, без сервера)
- У них: OCA `pos_product_expiry` (Odoo) не даёт продать партию с истёкшим сроком или предупреждает; Grocy показывает «скоро истекает» и списывает по принципу «первым истекает — первым уходит».
- У нас уже есть: `ProductExpiryIndex` (дата, `days_left`, `Expired` с сервера), метка на плитке (`CatalogProductTileVm.ExpiryBadgeText`). Не хватает: при скане/добавлении в чек просроченного — окно «Товар просрочен (12.09). Продать всё равно? / Убрать» (по настройке: предупреждать / запретить без PIN владельца); в Складе фильтр «просрочено» → кнопка «Списать» (через существующий акт списания) и строка в Telegram «просрочено 7 товаров на 3 400 сом».
- Данные: всё есть на сервере (`analytics/market/?tab=products` → `expiring_products`).

**2. Антифрод кассира** (5 / M)
- У них: коммерческие системы «exception-based reporting» ищут в журнале кассы: много отмен/удалений после пробития, возвраты без исходного чека, ручные скидки, продажи ниже закупки, открытие ящика без продажи, возврат вскоре после продажи тем же кассиром, «круглые» суммы безнала. В открытых — только кусочки (Odoo/OCA ограничения кассира, NexoPOS журнал действий). Готового открытого модуля не нашли.
- У нас: журнал «Удалено из чека» с причинами (локально), слепой пересчёт с первой попыткой в журнале, «Некорректные чеки», `TelegramBotService.BuildAlert`.
- Как сделать: раздел владельца «Подозрительные операции» — правила с порогами (удаление строки > N сом после пробития; возврат > N или без чека; скидка > X %; цена ниже закупки; недостача при закрытии смены > N; > K удалений за смену) → балл кассира за неделю и список событий с переходом в чек. Каждое срабатывание — сообщение в Telegram владельцу (тихие часы как в `DebtReminderPlanner`).
- Сервер: возвраты, скидки (`discount_total`), смены, расхождения — уже есть. Журнал удалений сейчас живёт на ПК кассира → для нескольких касс нужен у Умара приём событий (или временно — пересылка через `app-settings` / бот). Пункт в ТЗ бэкенду.

**3. Автозаказ v2** (5 / M, без сервера)
- У них: Odoo — правила min/max по прогнозному остатку + срок поставки; отчёт «пополнение» для ручной проверки. ERPNext — уровень и количество перезаказа на склад, ежедневная задача создаёт заявку и шлёт письмо. Loyverse — ежедневное уведомление о низком остатке. statsforecast (Apache-2.0) — методы Croston/TSB для товаров, которые продаются редко и неровно.
- У нас: `PurchasePlanner` = средняя за период × (запас + страховой). Добавить: (а) не считать дни, когда товара не было (иначе заказ занижается — «потерянный спрос»); (б) кратность упаковки и минимальная партия поставщика (поле в карточке поставщика/товара; «Упаковка» уже есть); (в) срок поставки поставщика в днях (локально у поставщика); (г) для редких товаров — TSB (формула на 30 строк C#, код Python не нужен); (д) учёт дня недели; (е) утром — «пора заказать у 3 поставщиков на 48 000 сом» в Сводке и Telegram, кнопка «Сформировать заказы».
- Данные: продажи по строкам (`SalesHistoryBackfill`), `minimum_quantity` с сервера, остатки — всё есть.

**4. Тесты без боевой базы + CI** (5 / S–M, без сервера)
- Боль: правило владельца «никогда `dotnet test`» — тесты (`NurMarketKassa.Tests`) ходят в боевую базу. Папки `.github` нет — сборку никто автоматически не проверяет.
- У них: крупные проекты гоняют тесты на каждом коммите (Odoo, ERPNext, Saleor — GitHub Actions).
- Как сделать: WireMock.Net (Apache-2.0, 1,7 тыс. ★) поднимает поддельный `app.nurcrm.kg` на localhost с записанными ответами (товары, продажа, возврат, смена, ошибки 403/500/медленный ответ). Verify (MIT) — снимки текста чека, X/Z, этикеток: любое изменение видно диффом. Avalonia.Headless (MIT) — окна без экрана. CI: сборка + эти тесты на GitHub Actions (или локальный скрипт перед выпуском). Хук `block-dotnet-test.py` можно ослабить только для проекта тестов-заглушек — решение владельца.
- По правилу 4 — новые пакеты только с разрешения владельца.

**5. Оповещения по правилам и отчёты по расписанию** (4 / S)
- У них: Frappe (MIT) — «Notification» (условие на документ → сообщение) и «Auto Email Report» (отчёт по расписанию). Loyverse — ежедневный «мало на складе».
- У нас: утренний отчёт и технические оповещения. Добавить настройку «Сообщать в Telegram, если…»: смена закрыта с недостачей > N; возврат > N; выручка к 18:00 ниже плана на X %; товар A-группы закончился; новый долг > N; просрочка. И вечерний итог смены каждой кассы. Перевести `/неделя`, `/топ`, `/abc` с локальной истории на сервер (открытая задача из AI_HANDOFF §9).

**6. XYZ-анализ** (4 / S)
- У них: метод «важность × предсказуемость»: X — коэффициент вариации недельных продаж < 10–25 %, Y — до 50 %, Z — выше. Матрица ABC×XYZ: AX держать всегда, CZ — под заказ/вывести.
- У нас: вкладка в `AbcAnalysisWindow` по тем же продажам; итог передать в автозаказ (страховой запас для AX/BX выше, для CZ — 0) и Нур Советнику.

**7. Подсказки цены** (4 / S)
- У них: Odoo — правило «цена = закупка + наценка, округлить до …9»; ERPNext — Pricing Rule по количеству.
- У нас: в Сводке уже есть «закупка дороже цены». Добавить: при приходе, если закупка выросла — «Поднять цену с 85 до 95 (наценка 30 %)?» одним списком; целевая наценка на категорию; «красивое» округление (до 5/10 сом); итог — пакетная правка цен + печать ценников (`BulkPriceTagPrintDialog` уже есть).

**8. Электронный чек** (4 / M)
- У них: OSPOS — чек по email/SMS; Odoo — QR на чеке → страница чека/счёта; Loyverse — чек по email.
- У нас: ручная отправка. Вариант без сервера: Telegram-бот магазина (бот уже есть) — QR «Получать чеки и бонусы в Telegram» на чеке/экране покупателя → покупатель жмёт Start с номером телефона → касса сама шлёт чек клиенту с этим телефоном, баланс бонусов, напоминание о долге. Вариант с Умаром: публичная страница чека на витрине `market.nurcrm.kg/r/<токен>` и QR на каждом чеке (нужно и для будущей фискализации).

**9. Циклическая инвентаризация** (4 / M)
- У них (AGPL — только идея): OCA `stock_cycle_count` — правила «каждый день N позиций, A-товары раз в неделю, C — раз в квартал»; `stock_inventory_discrepancy` — расхождение выше порога требует подтверждения менеджера; `stock_change_qty_reason` — причина каждой корректировки. Loyverse — частичный пересчёт сканером.
- У нас: полная ревизия. Добавить «Пересчёт дня» (20–30 позиций по ABC и по «подозрительным»: отрицательный остаток, давно не считали, частые удаления), причина расхождения из списка (порча, кража, пересорт, ошибка прихода), порог — PIN владельца. Причину писать в примечание документа на сервере (поле уже есть).

**10. Акции по правилам и промокоды** (5 / L)
- У них: Medusa (MIT) — типы «standard» и «buy-get», условия по товару/группе клиентов/сумме, кампании с бюджетом и лимитом использований; Saleor (BSD-3) — акции на каталог (цена видна до корзины) и на заказ (от суммы, подарок), не складываются; Odoo (LGPL) — купоны, «купи X — получи Y», купон на следующую покупку.
- У нас: только ручная скидка (`OrderDiscountDialog`) и бонусы. Правила хранить в `app-settings` (как `PaymentQrs`, общие с телефоном), применять на кассе как скидку строки. Риск: округление и итог «ровно как на сервере» — нужно согласовать с Умаром, как сервер принимает скидку строки и учитывает её в аналитике; иначе расхождение Z-отчёта. Начать с одного типа: «скидка % на категорию/товар в период и часы».

**11. Себестоимость с доставкой** (4 / M)
- У них: ERPNext Landed Cost Voucher, Odoo landed costs — доставка, грузчики, таможня распределяются на строки прихода по сумме/весу/количеству.
- У нас: в приходе нет (в коде не нашёл). Поле «Доп. расходы: 3 000 сом, распределить по сумме» → новая закупочная цена строк (до 4 знаков уже поддерживается) → верная прибыль и наценка.

**12. Права кассира по действиям** (4 / M)
- У них: Odoo — Basic/Advanced права, вход по PIN/бейджу, блокировка экрана; OCA `pos_user_restriction`; NexoPOS — роли и разрешения.
- У нас: права документов закупки (`PermissionService`), PIN владельца для старых чеков. Добавить список действий с порогом: скидка > X %, возврат, ручная цена, удаление строки после пробития, открыть ящик, закрыть смену с недостачей — «спросить PIN старшего». Каждое подтверждение — в журнал антифрода (п. 2). Роль сотрудника лучше хранить на сервере (Умар), временно — в `app-settings`.

**13. Сверка банковской выписки** (4 / M)
- У них: Invoice Ninja (ELv2) — правила «входящие → счёт/оплата, исходящие → расход», по порядку, авто-превращение; номер счёта в описании ищется сам. Akaunting (BSL) — сверка, проведённые операции нельзя править. ERPNext — Bank Reconciliation Tool.
- У нас: сверка денег прибыли/кассы есть (`ProfitCashReconcileWindow`), выписки нет. Импорт Excel/CSV выписки MBank/Optima → сопоставить: безнал/QR смены по дню и сумме, оплаты поставщикам по сумме и ИНН/названию, остальное — «расход?» с правилом «запомнить». Итог: «за октябрь безнал по кассе 412 300, по банку 409 800, разница 2 500 — 3 чека».

**14. Партии и FEFO** (5 / L, Умар)
- У них: Odoo — партии со сроками, стратегия «первым истекает — первым уходит»; Dolibarr, OpenBoxes — партии, отчёт «истекает».
- У нас: одна дата на товар с сервера. Нужно у Умара: партия (дата прихода, срок, остаток) у строки прихода; продажа списывает с ранней партии. На кассе — ввод срока при приходе (накладная по фото уже может читать срок), отчёт «что истекает по партиям».

**15. QR с автоподтверждением** (5 / L, банк)
- Открытого кода для ELQR/MBank/Optima не нашли; публичного API MBank Business по QR в выдаче нет. ELQR ведёт МПЦ («Элкарт»), 20 банков. Путь — договор с банком-эквайером (уведомление об оплате по ID QR) или О!Банк API (уже в «больших направлениях»). Пока — кнопка «Проверить оплату» невозможна без банка; можно только запоминать сумму/время QR для последующей сверки выпиской (п. 13).

---

## 3. Остальные находки

| Функция | Проект (ссылка) | Лицензия | Польза | Труд | Как у нас |
|---|---|---|---|---|---|
| Подарочные сертификаты и депозит клиента (eWallet) | Odoo loyalty, OSPOS gift cards, Saleor gift cards | LGPL / MIT / BSD | 3 | M | Баланс на сервере — Умар; как бонусы |
| Прайс-листы по группам клиентов, скидка от количества | Odoo pricelists, ERPNext Pricing Rule | <span style="color:#c00">LGPL / GPL</span> | 3 | M | Опт есть; добавить группу клиента → цена |
| Рецептуры / производство (пекарня, кафе, фасовка) | Loyverse production, InvenTree BOM | закрытый / MIT | 3 | L | Комплекты есть; списание сырья — Умар |
| Эскалация напоминаний должникам (1-е, 2-е, 3-е) + автоотправка ботом | Invoice Ninja reminders | <span style="color:#c00">ELv2</span> | 3 | S | `DebtReminderPlanner` есть; через бот магазина — без кликов |
| Купоны сканом штрихкода (скидочные метки) | Odoo discount tags | LGPL | 3 | S | Часть п. 10 |
| Реферальные коды клиентов | POS Awesome | <span style="color:#c00">GPL-3</span> | 2 | M | После промокодов |
| Телеграм-витрина/заказ для покупателя (каталог, корзина) | AiogramShopBot (MIT, 249 ★) | MIT | 3 | M | Есть сайт и «Заказы с сайта»; бот — ссылка на витрину |
| Продажа голосом/текстом в Telegram для ларька без ПК («продал 2 хлеба», долг) | kirana-ai-bot | <span style="color:#c00">нет лицензии</span> | 3 | M | Нур Советник + бот уже понимают команды |
| Офлайн-распознавание накладной без ключа ИИ | docTR (6,4 тыс. ★), PaddleOCR (92 тыс. ★) | Apache-2.0 | 3 | L | Модели сотни МБ; запасной путь при отказе Gemini |
| Автозаполнение карточки по штрихкоду из открытой базы | Open Food Facts (данные ODbL), Grocy barcode lookup | данные ODbL; код <span style="color:#c00">AGPL</span> | 3 | S | Уже ищем фото там; добавить название/вес/состав по EAN |
| Анализ фото товара (этикетка, вес, бренд) | Robotoff | <span style="color:#c00">AGPL</span> | 2 | M | Делаем Gemini — только идея |
| Резервирование под заказ и выбор склада списания | Saleor allocation strategy, Medusa stock locations | BSD / MIT | 2 | L | Филиалы есть; резерв — Умар |
| Аудит оборудования (весы, принтеры: дата проверки, поверка весов) | Snipe-IT (15 тыс. ★) | <span style="color:#c00">AGPL</span> | 2 | S | Напоминание «поверка весов до …» в настройках оборудования |
| Блок сканера на время окна оплаты | OCA `pos_barcode_block` | <span style="color:#c00">AGPL</span> | 2 | S | Проверить, что скан в оплате не добавляет товар |
| Электронные ценники | Odoo electronic shelf labels | LGPL | 1 | L | Нет оборудования в КР |
| Учёт электронных компонентов | Part-DB | <span style="color:#c00">AGPL</span> | 1 | — | Не для магазина |
| Кухонный экран | Chromis Kitchen Screen, Loyverse KDS | <span style="color:#c00">нет/GPL</span> | 1 | M | Только если кафе |
| Маркетплейс приложений на кассе, ЕГАИС | Эвотор `integration-library`, `egais-api` | <span style="color:#c00">лицензия не указана</span> | 2 | — | Свой Маркетплейс уже есть |

---

## 4. Чего у нас нет совсем

1. **Фискализация ГНС (ККМ онлайн).** У конкурента МойСклад есть приложения для КР: NewCas (чеки в налоговую, офлайн-режим, X/Z), Yaros (eKassa/Yakassa); у НУР Телеком — «О!Касса». Открытого кода нет. Это обязательная для многих магазинов функция — главный риск потерять клиентов.
2. **Автоподтверждение QR-оплаты** — нужен банк (п. 15).
3. **Акции по правилам, промокоды, подарочные сертификаты, депозит клиента** (п. 10, раздел 3).
4. **Партии со сроками и FEFO** (п. 14).
5. **XYZ-анализ** (п. 6).
6. **Себестоимость с доставкой** (п. 11).
7. **Сверка банковской выписки** (п. 13).
8. **Права кассира по действиям** — отдельного списка прав в коде не нашёл, есть только права документов закупки и PIN владельца (п. 12).
9. **Сводка подозрительных операций** — журналы есть, анализа и оповещений нет (п. 2).
10. **Тесты на поддельном сервере и CI** — нет `.github/`, тесты ходят в боевую базу (п. 4).
11. **Рецептуры/производство** (раздел 3).

---

## 5. Опасно по лицензии

Нельзя копировать код в закрытую кассу — только читать документацию и вдохновляться функцией. Писать своё на C# с нуля, без построчного перевода.

| Проект | ★ | Последний push | Лицензия | Что нельзя |
|---|---|---|---|---|
| ERPNext — [frappe/erpnext](https://github.com/frappe/erpnext) | 40 962 | 05.11.2026 | **<span style="color:#c00">GPL-3.0</span>** | Копировать код |
| POS Awesome — [ucraft-com/POS-Awesome](https://github.com/ucraft-com/POS-Awesome), форк [defendicon/POS-Awesome-V15](https://github.com/defendicon/POS-Awesome-V15) | 518 / 117 | 12.08.2024 / 04.11.2026 | **<span style="color:#c00">GPL-3.0</span>** | Копировать код |
| NexoPOS — [Blair2004/NexoPOS](https://github.com/Blair2004/NexoPOS) | 1 302 | 03.11.2026 | **<span style="color:#c00">GPL-3.0</span>** | Копировать код |
| Dolibarr — [Dolibarr/dolibarr](https://github.com/Dolibarr/dolibarr) | 7 831 | 04.11.2026 | **<span style="color:#c00">GPL-3.0</span>** | Копировать код |
| uniCenta oPOS (зеркало [herbiehp/unicenta](https://github.com/herbiehp/unicenta)), Openbravo POS | 29 | 05.01.2015 | **<span style="color:#c00">GPL-3.0</span>** | Копировать код; проект заброшен |
| Chromis POS — [ChromisPos/ChromisPOS](https://github.com/ChromisPos/ChromisPOS) | 20 | 01.08.2025 | **<span style="color:#c00">не указана (потомок GPL uniCenta)</span>** | Копировать код |
| Openbravo — [GitLab openbravoos/openbravocore/openbravo](https://gitlab.com/openbravoos/openbravocore/openbravo) | 1 | 02.01.2023 | **<span style="color:#c00">Openbravo Public License (на основе MPL)</span>** | Копировать файлы без раскрытия |
| Floreant POS — на GitHub только старые форки ([Jabro/floreantpos](https://github.com/Jabro/floreantpos), 2015) | 19 | 18.09.2015 | **<span style="color:#c00">не указана</span>** | Копировать код |
| OCA/pos — [OCA/pos](https://github.com/OCA/pos) | 366 | 30.10.2026 | **<span style="color:#c00">AGPL-3.0</span>** | Копировать код (даже по сети) |
| OCA/stock-logistics-warehouse — [ссылка](https://github.com/OCA/stock-logistics-warehouse) | 421 | 04.11.2026 | **<span style="color:#c00">AGPL-3.0</span>** | Копировать код |
| Snipe-IT — [grokability/snipe-it](https://github.com/grokability/snipe-it) | 15 149 | 04.11.2026 | **<span style="color:#c00">AGPL-3.0</span>** | Копировать код |
| Part-DB — [Part-DB/Part-DB-server](https://github.com/Part-DB/Part-DB-server) | 1 829 | 02.11.2026 | **<span style="color:#c00">AGPL-3.0</span>** | Копировать код |
| Frappe Books — [frappe/books](https://github.com/frappe/books) | 5 007 | 02.10.2026 (архив) | **<span style="color:#c00">AGPL-3.0</span>** | Копировать код |
| Open Food Facts server / Robotoff — [server](https://github.com/openfoodfacts/openfoodfacts-server), [robotoff](https://github.com/openfoodfacts/robotoff) | 1 198 / 117 | 04.11.2026 | **<span style="color:#c00">AGPL-3.0</span>** (код); данные — ODbL, можно с указанием источника | Копировать код |
| Odoo — [odoo/odoo](https://github.com/odoo/odoo) | 55 554 | 05.11.2026 | **<span style="color:#c00">LGPL-3.0</span>** (Community; Enterprise — закрыт) | Копировать код в закрытый продукт |
| OpenBoxes — [openboxes/openboxes](https://github.com/openboxes/openboxes) | 921 | 04.11.2026 | **<span style="color:#c00">EPL-1.0</span>** | Копировать файлы без раскрытия |
| Invoice Ninja — [invoiceninja/invoiceninja](https://github.com/invoiceninja/invoiceninja) | 10 494 | 05.11.2026 | **<span style="color:#c00">Elastic License 2.0</span>** (не открытая) | Копировать, давать как сервис |
| Akaunting — [akaunting/akaunting](https://github.com/akaunting/akaunting) | 10 278 | 05.11.2026 | **<span style="color:#c00">Business Source License 1.1</span>** | Копировать в коммерческий продукт |
| Эвотор — [evotor/integration-library](https://github.com/evotor/integration-library) | 27 | 03.11.2026 | **<span style="color:#c00">не указана</span>** | Копировать код |
| kirana-ai-bot — [SourishAFK/kirana-ai-bot](https://github.com/SourishAFK/kirana-ai-bot) | 0 | 01.09.2026 | **<span style="color:#c00">не указана</span>** | Копировать код |

**Можно брать код (с уведомлением об авторстве):**

| Проект | ★ | Последний push | Лицензия | Зачем нам |
|---|---|---|---|---|
| [medusajs/medusa](https://github.com/medusajs/medusa) | 37 091 | 05.11.2026 | MIT (кроме папок Enterprise) | Модель акций и кампаний (п. 10) |
| [saleor/saleor](https://github.com/saleor/saleor) | 23 508 | 04.11.2026 | BSD-3-Clause | Акции, ваучеры, сертификаты, стратегия складов |
| [bagisto/bagisto](https://github.com/bagisto/bagisto) | 28 331 | 05.11.2026 | MIT | Правила корзины и каталога |
| [frappe/frappe](https://github.com/frappe/frappe) | 11 058 | 05.11.2026 | MIT | Оповещения по условиям, отчёты по расписанию, журнал версий (п. 5) |
| [inventree/InvenTree](https://github.com/inventree/InvenTree) | 7 817 | 05.11.2026 | MIT | Пересчёт, история движения, шаблоны этикеток |
| [grocy/grocy](https://github.com/grocy/grocy) | 9 657 | 16.09.2026 | MIT | Сроки, «мало — в список покупок», поиск по штрихкоду |
| [opensourcepos/opensourcepos](https://github.com/opensourcepos/opensourcepos) | 4 501 | 05.11.2026 | MIT **+ обязательная видимая подпись OSPOS на каждой странице** — код лучше не брать | Чеки по email/SMS, сертификаты, комплекты |
| [Nixtla/statsforecast](https://github.com/Nixtla/statsforecast) | 4 961 | 04.11.2026 | Apache-2.0 | Формулы Croston/TSB (п. 3) |
| [wiremock/WireMock.Net](https://github.com/wiremock/WireMock.Net) | 1 683 | 01.11.2026 | Apache-2.0 | Поддельный NurCRM для тестов (п. 4) |
| [VerifyTests/Verify](https://github.com/VerifyTests/Verify) | 3 473 | 05.11.2026 | MIT | Снимки чеков, X/Z (п. 4) |
| [AvaloniaUI/Avalonia](https://github.com/AvaloniaUI/Avalonia) (Headless) | 31 760 | 04.11.2026 | MIT | Тесты окон без экрана |
| [aiogram/aiogram](https://github.com/aiogram/aiogram), [ilyarolf/AiogramShopBot](https://github.com/ilyarolf/AiogramShopBot) | 5 915 / 249 | 01.11 / 31.08.2026 | MIT | Идеи бота-витрины (мы на C#) |
| [mindee/doctr](https://github.com/mindee/doctr), [PaddlePaddle/PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) | 6 413 / 92 282 | 03.11 / 16.09.2026 | Apache-2.0 | Офлайн OCR накладной |
| [adib0073/Importance_Forecastability_Segmentation](https://github.com/adib0073/Importance_Forecastability_Segmentation) | 11 | 10.05.2020 | MIT | Методика ABC-XYZ (п. 6) |

Не найдено открытого кода: Tillpoint, Stockpile, Loyverse (закрытые сервисы), клоны МойСклад, кассы КР/54-ФЗ с товароучётом, интеграции ELQR/MBank/Optima.

---

## 6. Что предложить Умару (в ТЗ бэкенду)

1. Приём событий кассы (удаление строки после пробития, ручная скидка, открытие ящика, PIN-подтверждения) — для антифрода на нескольких кассах (п. 2, 12).
2. Партия у строки прихода: срок годности, остаток партии, списание с ранней (п. 14).
3. Как сервер принимает скидку строки от акции и считает округление, чтобы Z-отчёт сошёлся (п. 10).
4. Публичная страница чека по токену для QR на чеке (п. 8; понадобится и для фискализации).
5. Баланс сертификата/депозита клиента (раздел 3).

## Источники

- Odoo POS: https://www.odoo.com/documentation/18.0/applications/sales/point_of_sale.html ; пополнение: https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/warehouses_storage/replenishment.html ; сотрудники: https://www.odoo.com/documentation/18.0/applications/sales/point_of_sale/employee_login.html
- Модули OCA (список папок ветки 18.0): https://github.com/OCA/pos , https://github.com/OCA/stock-logistics-warehouse
- Medusa акции: https://docs.medusajs.com/resources/commerce-modules/promotion/concepts
- Saleor скидки и склады: https://docs.saleor.io/docs/developer/discounts , https://docs.saleor.io/developer/stock-allocation
- Invoice Ninja банковские правила: https://invoiceninja.github.io/en/user-guide/transactions
- Akaunting сверка: https://github.com/akaunting/akaunting/issues/549
- Loyverse функции: https://loyverse.com/features
- NexoPOS, OSPOS, POS Awesome — README репозиториев (ссылки в разделе 5)
- ELQR: https://economist.kg/novosti-kompanii/2026/03/02/qr-vmiesto-nalichnykh-kak-elqr-za-6-liet-pieriestroil-platiezhnyi-rynok-kyrghyzstana/
- ККМ для КР у МойСклад: https://www.moysklad.ru/integratsii/apps/newcas-fiscal-goodoo/ , https://www.moysklad.ru/integratsii/apps/integrationkkm1-tmgkg/ ; «О!Касса»: https://24.kg/biznes_info/251236_registriruyte_kkm-onlayn_okassa_ponovyim_standartam_dlya_biznesa/
- Антифрод (коммерческие, для идеи): https://3xlogic.com/media/12496/download , https://us.fitgap.com/stack-guides/stopping-shrink-at-the-register-with-real-time-exception-monitoring-and-coaching-loops

*Ограничения: лимит GitHub API без токена (60 запросов/ч) — звёзды и даты сняты одним проходом 05.11.2026; «последний push» ≈ дата последнего коммита. Функции Odoo/ERPNext частично описаны по их документации, а не по коду. Наличие функций у нас проверено поиском по коду, не живьём.*
