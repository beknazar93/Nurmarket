## v1.17.44

Срочное исправление: ошибка при смене вида кассы.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).

Обновление не меняет ваши настройки. Всё новое из 1.17.43 (создание товара сканом из базы NurCRM, продажа в убыток, документы A4, опт) — тоже здесь.

---

# Русский

### Исправлено

| Что | Было | Стало |
|---|---|---|
| Смена вида кассы | При выборе вида «Профи» или «Карточки» выходило «Произошла ошибка в программе», касса в этом виде не работала | Оба вида открываются и работают |
| Документ A4 из чека | Если на компьютере не выбрана программа для файлов .html, выходило «Указанному файлу не сопоставлено ни одно приложение» | Документ открывается в Edge, Chrome или Firefox сразу с окном печати |

### Проверено (03–04.10)

- Касса запущена в видах «Профи», «Карточки» и «Стандарт» — ошибок нет.
- «Товарный чек (A4)» открылся в Edge с окном печати на компьютере без программы для .html.

---

# Кыргызча

- **Оңдолду:** «Профи» же «Карточкалар» касса көрүнүшүн тандаганда ката чыгып жатты — эми иштейт.
- **Оңдолду:** чектен A4 документ .html үчүн программа жок компьютерде да браузерде ачылат.

---

# English

- **Fixed:** choosing the “Pro” or “Cards” till view showed an error — both views work now.
- **Fixed:** the A4 document from a receipt opens in a browser even when no program is set for .html files.

---

# Türkçe

- **Düzeltildi:** «Profi» veya «Kartlar» kasa görünümü seçilince hata çıkıyordu — artık ikisi de çalışıyor.
- **Düzeltildi:** fişten A4 belge, .html için program ayarlı olmasa da tarayıcıda açılır.

---

# O‘zbekcha

- **Tuzatildi:** «Profi» yoki «Kartochkalar» kassa ko'rinishi tanlanganda xato chiqardi — endi ikkalasi ham ishlaydi.
- **Tuzatildi:** chekdan A4 hujjat .html uchun dastur tanlanmagan kompyuterda ham brauzerda ochiladi.
