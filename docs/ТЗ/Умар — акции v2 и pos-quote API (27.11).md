# Конструктор акций (Ф1) + pos/quote/ — В БОЮ 27.11.2026 (prm2711)

Ответ на «ТЗ Умару — общие правила денег и расчёта» и «конструктор акций». Префикс адресов: `https://app.nurcrm.kg/api/main/`.
Признаки в `GET capabilities/` → `features`: **`promotions_v2`**, **`pos_quote`**. Без них касса работает как раньше.

## 0. Что из «общей части» уже было на сервере (менять не нужно)
- `expected_total` в `pos/checkout/` → 409 `total_mismatch {server_total, expected_total, lines[]}` (у новых клиентов; старым — только журнал). Офлайн-досылка не отказывает.
- Бонусы отдельно от скидки: поле **`bonus_redeemed`** в `pos/checkout/` (то, что в ТЗ названо `bonus_redeem`). Сервер сам списывает баллы в транзакции чека, `sale.bonus_redeemed` отдельно от `discount_total`.
- Подарочные карты (П3-1, 15.11): `gift-cards/`, оплата `{"method": "gift_card", "amount", "gift_card": "<код>"}` в `payment.payments[]`.

## 1. pos/quote/ — предпросмотр чека
`POST pos/quote/` — тело как у `pos/checkout/`, `payment` не нужен. Ничего не пишет (всё считается в транзакции и откатывается). Можно передать `"at": "2026-11-27T13:50:00+06:00"` — посчитать акции на этот момент (для офлайн-чека, иначе время сервера; с `offline`+`sold_at` берётся `sold_at`).

```json
{"subtotal":"419.00",
 "lines":[{"index":0,"product":"…","variant":null,"qty":"3.000","price":"123.00","subtotal":"369.00","discount":"123.00","line_total":"246.00",
           "applied":[{"kind":"promotion","id":"…","version":1,"title":"2+1","qty":"1","amount":"123.00"}]},
          {"index":1,"product":"…","qty":"1.000","price":"50.00","subtotal":"50.00","discount":"5.00","line_total":"45.00",
           "applied":[{"kind":"promotion","id":"…","version":1,"title":"-10%","qty":"1.000","amount":"5.00"}]}],
 "line_discount_total":"128.00","order_discounts":[],"bonus_redeem":"0.00",
 "total":"291.00","discount_total":"128.00",
 "bonus":{"earn_percent":"4.00","earn_preview":"11.64","redeem_max":"0.00"},
 "warnings":[],"hints":[{"promotion":"…","need_qty":1,"text":"Добавьте ещё 1 — будет бесплатно"}],
 "price_mismatch":[],"rules_version":"2026-11-27T…Z","at":"…"}
```
- **`total` — ровно то число, которое сверяет `expected_total`** (после скидок и баллов). Проверено вживую: quote 291,00 → checkout 291,00.
- `applied[].kind`: `promotion` (акция v2), `legacy_tier` (старая ступень товара `promotion_rules`), `manual` (скидка кассира).
- `order_discounts[]`: пока только `{"kind":"manual","amount"}` (скидка кассира на чек); промокод и уровень — с их ТЗ.

## 2. applied[] в продаже
В ответе `pos/checkout/` у каждой строки `items[]` появляется `applied[]` (если в чеке сработала акция v2). Плюс `discount_source: "promotion"` и `promotion_id` = id акции. В `warnings[]` могут прийти `promotion_capped_by_cost`, `discount_clamped`; в `hints[]` — подсказки.

## 3. Акции — API
| Метод | Адрес | Что |
|---|---|---|
| GET | `promotions/?status=active,paused&updated_since=` | список (`version`, `count`, `results[]`) |
| POST | `promotions/` + `Idempotency-Key` | создать (по умолчанию `status: "draft"`, можно сразу `"active"`); повтор ключа → тот же объект + `replayed: true` |
| GET / PATCH / DELETE | `promotions/<uuid>/` | карточка / изменить (`version`+1) / удалить черновик без продаж |
| POST | `promotions/<uuid>/start/` · `pause/` · `resume/` · `finish/` | статусы draft → active ⇄ paused → finished |
| POST | `promotions/preview/` | `{promotion:{…как при создании}, items:[{product, qty, price?, discount?}]}` → расчёт без сохранения |
| GET | `promotions/report/?date_from=&date_to=&promotion=` | по каждой: `receipts, units, discount, revenue, margin` |
| GET | `pos/promotions/active/?branch=` | для кассы, кэш без сети; `ETag` → `If-None-Match` → 304 |

Старый `promotions/active/` (сводка П3-1) остался как был.

Поля акции: `title, description, kind, params, scope, starts_at, ends_at, weekdays [1..7], time_from, time_to ("ЧЧ:ММ", время компании Asia/Bishkek; через полночь можно 22:00–02:00), audience (all | clients | levels*), priority, stackable_with_manual, stackable_with_promos, allow_below_cost, branches [], limit_per_receipt` (+ `limit_total, limit_per_client` — хранятся, считаются в Ф2).

`kind` в Ф1 и `params`:
| kind | params |
|---|---|
| `buy_n_get_m` | `{buy: 2, get: 1, get_discount_percent: 100, cheapest: true}` |
| `percent` | `{percent: 15}` (+ необязательно `min_amount`, `max_qty` — как старые ступени) |
| `amount` | `{amount: "20.00", per: "unit" \| "line"}` |
| `fixed_price` | `{price: "79.00"}` |

`scope`: `{all: true}` или `{products: [], categories: [], brands: [], exclude_products: []}`. Категория включает подкатегории.

## 4. Правила расчёта (одинаковые у нас и у вас)
1. Кандидаты строки — активные акции на момент продажи (дата, день недели, часы; офлайн — `sold_at`), подходящие по scope и филиалу. `audience: clients` — только если в чеке есть клиент.
2. Из кандидатов — наибольший `priority`, при равном — бо́льшая скидка. Другие добавляются, только если и выбранная, и добавляемая `stackable_with_promos`.
3. N+M: штуки всех подходящих строк сортируются от дорогих к дешёвым, блоками по N+M; в блоке скидка M самым дешёвым. `limit_per_receipt` = максимум блоков. Для остальных видов `limit_per_receipt` = максимум единиц со скидкой.
4. Ручная скидка кассира на строке с акцией — только если все её акции `stackable_with_manual`. Без акции — как сейчас.
5. Старые `promotion_rules` товара — кандидат с priority 0 (считаются как раньше). Поле в товаре оставили.
6. И-1: цена после акции не ниже закупки, если у акции нет `allow_below_cost` (ставит только владелец, иначе 403 `promotion_below_cost_forbidden`) → скидка урезается, `warnings[].promotion_capped_by_cost {index, cut, purchase_price}`. И-2: скидка ≤ сумма строки.
7. Округление: скидка строки до копейки HALF_UP; итог чека — как сейчас.

Тесты по вашим числам (раздел 7) прошли: 1, 2, 3, 4, 6, 8, 10 (+ ручная скидка, фикс-цена, исключения, подсказка). 5 (комбо), 7 (лимит всего), 9 (возврат 66,67) — Ф2; п.9 уже сейчас так: строка 3×100 со скидкой 100 возвращает 1 шт за 66,67.

## 5. Ошибки
400 `promotion_invalid {field, fields}` · 409 `promotion_kind_locked` (тип у запущенной) · 409 `promotion_finished` · 409 `promotion_bad_status` · 409 `promotion_not_draft` (удаление) · 403 `permission_denied {required: "can_manage_promotions"}` · 403 `promotion_below_cost_forbidden`.
Права: создавать/менять — владелец и админ (отдельный флаг `can_manage_promotions` для сотрудника — следующим шагом). Читать список и `pos/promotions/active/` — любой сотрудник.

## 6. Что ещё не сделано (Ф2)
`combo`, `qty_tiers`, `limit_total`/`limit_per_client` со счётчиками (две кассы одновременно), аудитория по уровням (после ТЗ уровней), раскладка возврата `refund[]` по средствам (карта/баллы), модуль в `public/modules` с ценой (решает владелец).

## 7. Ответы на ваши вопросы (сообщение 26.11 23:39)
1. **Возврат долгового чека №2139.** Верная цифра — **45** (предоплата 20 + гашения долга 10 + 15). Ошибка была у сервера: при полном возврате чека в долг с предоплатой в расход шла только предоплата. Исправлено (dret2711, в бою 27.11): предоплата уходит из ящика вместе с чеком, на гашения пишется отдельный расход «Возврат по чеку №… (гашения долга)» с влиянием на ящик. Старую запись по 2139 поправим отдельно.
2. **Округление цены единицы при частичном возврате.** Правило сервера (ТЗ Н-02, `refund_calc.py`): сумма к возврату = округл( округл(цена × кол-во − округл(скидка строки × кол-во / кол-во строки)) × (итог чека / сумма строк после скидок на позиции) ). Каждый шаг округляется до копейки по правилу **HALF_UP** (0,005 → вверх). Последний возврат по чеку — ровно остаток. Если у вас получилось 94,28, а у нас 94,27 — пришлите номер чека, разберём на нём.
3. **Ремонт этап 2.** Список `GET repair-orders/` → `{"count", "page", "results": [...]}`. Серверную квитанцию (без `external_id`) касса меняет обычными адресами `repair-orders/<uuid>/` (PATCH), `/status/`, `/prepay/`, `/issue/`; `PUT repair-orders/<external_id>/` — только для квитанций, созданных кассой.
4. **sales-stats → bargain.top_products** — до 10 элементов: `{"product_id", "name", "proposals" (сколько раз предлагали торг), "orders" (сколько заказов с этим торгом)}`, по убыванию `proposals`.

— Claude (ИИ-помощник Умара)
