# Поиск решений на GitHub и GitLab — библиотеки и компоненты (05.11.2026)

**Что это.** Подбор готовых библиотек под 12 болей кассы (Avalonia 11.2.5 / .NET 8, Windows/Linux/Android) и мобилки (Flutter). Только рекомендации: код, зависимости и репозиторий не менялись, чужой код не запускался и не клонировался.
**Откуда цифры.** Звёзды, дата последнего push и лицензия — публичный GitHub API (search, без токена) на 05.11.2026. Последние версии — NuGet (`api.nuget.org`) и pub.dev. Языки моделей — страницы Vosk, PaddleOCR, Google ML Kit, Hugging Face.
**Красным** отмечены лицензии, которые нельзя встраивать в закрытую платную программу без отдельного решения: GPL/AGPL, «только некоммерческое» (NC), платный ключ.
**GitLab.** По этим темам (escpos, термопринтер, TSPL, кыргызский язык, Avalonia, офлайн-синхронизация) на gitlab.com нашлись только мелкие проекты с 0–26 звёздами. Брать оттуда нечего. Исключение — F-Droid (`gitlab.com/fdroid`): свой репозиторий APK (см. боль 11).

Что у нас уже есть, чтобы не дублировать: свой ESC/POS (`Infrastructure/Services/EscPosCommands.cs`, `EscPosTextReceiptPrinter.cs`, `GraphicReceiptGenerator.cs`), этикетки TSPL и GS v 0 (`BarcodeLabelService.cs`), ZXing.Net 0.16.10, QuestPDF, OpenXml (`ExcelWorkbookBuilder.cs`), свои графики (`AnalyticsChartRenderer.cs`, `BarChartRenderer.cs`), своё сопоставление накладных (`InvoiceImport/InvoiceMatcher.cs`, `InvoiceNameParser.cs`), ключи повтора для денег (`SlowMoneyCall.cs`), outbox в мобилке (`lib/core/sync/outbox_meta.dart`, `sync_service.dart`), Velopack 1.0.1, Avalonia.Headless. Голос (Vosk, sherpa-onnx) убран из установки 30.10.2026 ради размера.

---

## 1. Топ-10 «подключить первыми»

Для каждой строки нужно разрешение владельца: правило 4 — зависимости не меняем без спроса.

| # | Что | Боль | Польза | Труд | Лицензия |
|---|---|---|---|---|---|
| 1 | **WireMock.Net** — свой поддельный сервер NurCRM для тестов | 9 | 5 | M | Apache-2.0 |
| 2 | **CsCheck** (или FsCheck) — тесты денег на случайных данных | 9 | 4 | S | Apache-2.0 / BSD-3 |
| 3 | **Polly v8** (`Polly.Core` 8.9.0 или `Microsoft.Extensions.Http.Resilience`) — сроки ожидания, «предохранитель» при медленном сервере | 5 | 4 | M | BSD-3 / MIT |
| 4 | **sunmi_printer_plus** 4.2.0 (Flutter) — встроенный принтер Sunmi, все буквы | 1 | 4 | M | BSD-3 |
| 5 | **barcode** 2.2.9 (Dart) — штрихкод картинкой на этикетке | 1, 2 | 4 | S | Apache-2.0 |
| 6 | **dotnet-counters / dotnet-gcdump / dotnet-trace** и **BenchmarkDotNet** — найти утечки памяти и медленные места | 10 | 4 | S | MIT |
| 7 | **Velopack 1.0.1 → 1.2.161** — обновить то, что уже используем | 11 | 3 | S | MIT |
| 8 | **Raffinert.FuzzySharp** 6.0.1 и **AnyAscii** 0.3.3 — оценка похожести и транслит в сопоставлении накладных | 4 | 3 | S | MIT / ISC |
| 9 | **Vosk** + модель `vosk-model-small-ky-0.42` (49 МБ) — офлайн-голос на кыргызском, по желанию, докачкой | 6 | 3 | M | Apache-2.0 |
| 10 | **MiniExcel** 1.46.0 — быстрая выгрузка больших таблиц (15 000 товаров) с малой памятью | 7 | 3 | S | Apache-2.0 |

### План подключения (по шагам)
1. **WireMock.Net + CsCheck — только в `NurMarketKassa.Tests`, в установку не попадают.** Адрес сервера уже можно подменить переменной `DESKTOP_MARKET_API_URL`. Не хватает переменной для папки данных, например `NURMARKET_DATA_DIR`: это маленькая правка там, где строится путь `%APPDATA%\NurMarketKassa\data`. В тестах — SQLite в памяти (`Data Source=:memory:` / `Mode=Memory;Cache=Shared`), это уже умеет `Microsoft.Data.Sqlite`. После этого `dotnet test` перестаёт ходить в боевую базу, и правило 1 можно пересмотреть. Хук `block-dotnet-test.py` снимать только по решению владельца. Первые тесты на CsCheck: округление чека, весовые, скидка, сдача, возврат. Свойство: «сумма строк минус скидка плюс округление = итог сервера», на тысячах случайных чеков.
2. **Polly** — в `Infrastructure/Services/Api/TransportTuningHandler.cs` (рядом с `JwtBearerRefreshHandler.cs`) как `DelegatingHandler`: срок ожидания на запрос и «предохранитель». После N ошибок подряд касса 30 с не ждёт сервер, а сразу работает из кэша. **Повтор — только для GET.** POST с деньгами повторяется только через свой `SlowMoneyCall` с ключом повтора, иначе будет двойная продажа.
3. **sunmi_printer_plus** — в мобилке отдельный путь «встроенный принтер», если `Build.MANUFACTURER == SUNMI` (М5-П5). Обычный Bluetooth/LAN/USB остаётся на своём Kotlin. Живая проверка на Sunmi V2/T2: напечатать «ң ө ү ş ğ».
4. **barcode** — сделать явной строкой в `pubspec.yaml` (пакет уже в lock через `pdf`). Штрихкод на этикетке рисуется картинкой и уходит в TSPL `BITMAP` (М5-03).
5. **dotnet-counters/gcdump** — ставятся как инструменты (`dotnet tool install -g`), проект не меняется. Снимать на ПК кассы во время работы: `dotnet-gcdump collect -p <pid>`. BenchmarkDotNet — отдельный маленький проект для замеров поиска по 15 000 товаров и `InvoiceMatcher`.
6. **Velopack** — сначала поднять на тестовом канале (код тестировщика), проверить обновление 1.17.x → новая версия. Пакет и `vpk` обновлять одной версией.
7. **Raffinert.FuzzySharp** — заменить только самописные `Levenshtein`/`Similarity` в `InvoiceNameParser.cs:185` и `InvoiceMatcher.cs:350`. Правила «числа и объём должны совпасть» оставить свои. **AnyAscii** — запасной транслит для латиницы ↔ кириллицы (ң→n, ө→o, ү→u).
8. **Vosk-ky** — по желанию: кнопка «Скачать голос (49 МБ)», без включения в установку (решение 30.10 сохраняется).
9. **MiniExcel** — для выгрузок больше ~5 000 строк, вместо ручного OpenXml в `ExcelWorkbookBuilder.cs`. Стили по-прежнему делает OpenXml.

---

## 2. Таблица по болям

Обозначения. ★ — звёзды GitHub. «Push» — последний push в репозиторий. Польза 1–5. Труд S/M/L. Вывод: **брать** / **присм.** (присмотреться) / **не брать**.

### Боль 1. Чековые принтеры и этикетки

| Имя | ★ | Push | Лицензия | Платформы | Как у нас | Риск | Польза | Труд | Вывод |
|---|---|---|---|---|---|---|---|---|---|
| [ESC-POS-.NET](https://github.com/lukevp/ESC-POS-.NET) (NuGet `ESCPOS_NET` 3.0.0) | 672 | 2024-09-18 | MIT | net, Win/Linux/macOS, BT/USB/сеть | Заменил бы `EscPosCommands.cs` | Не развивается больше года; кириллица только через кодовые страницы; у нас своё богаче (CP866/1251, растр, ящик) | 2 | M | не брать |
| [sunmi_printer_plus](https://github.com/brasizza/sunmi_printer) (pub 4.2.0, 02.11.2026) | 59 | 2026-11-02 | BSD-3 | Flutter, только Android | Новый путь для Sunmi рядом с `printer_service.dart` | Работает только на Sunmi; проверить кыргызские буквы на устройстве | 4 | M | **брать** |
| [unified_esc_pos_printer](https://github.com/elrizwiraswara/unified_esc_pos_printer) (3.4.0) | 10 | 2026-07-16 | BSD-3 | Flutter: Android/iOS/Win/Linux; USB, BT, BLE, сеть | Мог бы заменить свой Kotlin в `MainActivity.kt` | Очень молодой (10 ★), один автор | 3 | L | присм. (как образец печати картинкой) |
| [print_bluetooth_thermal](https://github.com/andresperezmelo/print_bluetooth_termal) (1.2.5) | — | pub 2026-09-28 | проверить на pub.dev | Flutter, Android/iOS | — | Только Bluetooth; у нас уже есть | 1 | M | не брать |
| [blue_thermal_printer](https://github.com/kakzaki/blue_thermal_printer) | 175 | 2024-07-15 | MIT | Flutter, Android | — | Заброшен | 1 | — | не брать |
| [esc_pos_utils](https://github.com/andrey-ushakov/esc_pos_utils) / [esc_pos_utils_plus](https://github.com/kechankrisna/esc_pos_utils_plus) | — | pub 2021 / 2024 | MIT | Dart | `escpos.dart` | Репозитории не открываются в API (удалены или переименованы), выпусков давно нет | 1 | — | не брать |
| [xprinter_flutter](https://github.com/NafimAhmed/xprinter-flutter) (0.2.0) | 0 | 2026-10-11 | README: MIT, в GitHub лицензия не распознана | Flutter Android: TSPL по BT SPP/Wi-Fi/USB, BITMAP | Образец для этикеток TSPL в мобилке (М5-П3) | 0 ★, один автор | 3 | M | присм. — читать как образец, TSPL написать своим (это простой текст) |
| [flutter_print_label](https://github.com/mynameissuksan/flutter_print_label) (0.2.0) | 1 | 2026-10-16 | MIT (pub) | Flutter Android/iOS, BLE, сырой TSPL | — | 1 ★ | 2 | S | присм. |
| [BinaryKits.Zpl](https://github.com/BinaryKits/BinaryKits.Zpl) (3.3.1) | 411 | 2026-08-21 | MIT | net, сеть 9100, просмотр ZPL картинкой | Если клиенты принесут Zebra — новый вид в `BarcodeLabelService.cs` | Zebra у нас редкость (Xprinter/TSC — TSPL) | 2 | M | присм. (по запросу клиентов) |

Для **TSPL на .NET** готовой живой библиотеки не нашлось. Наш `BarcodeLabelService.cs` — нормальный путь.

### Боль 2. Штрихкоды и QR

| Имя | ★ | Push | Лицензия | Платформы | Как у нас | Риск | Польза | Труд | Вывод |
|---|---|---|---|---|---|---|---|---|---|
| [ZXing.Net](https://github.com/micjahn/ZXing.Net) (0.16.11) | 3104 | 2026-11-01 | Apache-2.0 | net, все | Уже 0.16.10; можно поднять до 0.16.11 | — | 3 | S | **брать** (обновить) |
| [zxing-cpp](https://github.com/zxing-cpp/zxing-cpp) (NuGet `ZXingCpp` 0.5.3) | 2086 | 2026-11-04 | Apache-2.0 | C++, обёртки .NET/Android | Быстрее и точнее ZXing.Net на мятых и мелких кодах, DataMatrix | Родная библиотека под каждую платформу (Win/Linux/Android) | 3 | M | присм. (если ZXing.Net плохо читает с камеры) |
| [mobile_scanner](https://github.com/juliansteenbakker/mobile_scanner) (7.x) | 1178 | 2026-11-03 | BSD-3 | Flutter, ML Kit/CameraX | Уже используем | — | — | — | оставить |
| [barcode (dart_barcode)](https://github.com/DavBfr/dart_barcode) (2.2.9) | 150 | 2025-01-25 | Apache-2.0 | Dart | Явная строка в `pubspec.yaml`, М5-03 | Пакет уже в lock | 4 | S | **брать** |
| [BarcodeLib](https://github.com/barnhill/barcodelib) (3.1.5) | 804 | 2025-03-11 | Apache-2.0 | net (SkiaSharp) | Только если ZXing не устроит | Дублирует ZXing | 1 | S | не брать |
| [SimpleSoft.Gs1Parser](https://github.com/simplesoft-pt/Gs1Parser) / [ilopez/GS1Parser](https://github.com/ilopez/GS1Parser) | 9 / 34 | 2024 / 2019 | MIT | net | Разбор GS1 DataMatrix (01 GTIN, 17 срок, 10 партия, 21 серия) | Мало пользователей; разбор AI по таблице GS1 проще написать своим (~150 строк) | 2 | S | присм. как образец |
| [gs1_barcode_parser](https://github.com/mobui/gs1_barcode_parser) (1.1.2) | 8 | 2025-05-13 | MIT | Dart | То же для мобилки | 8 ★ | 2 | S | присм. |

**Весовые EAN-13 (префикс 20–29, PLU, вес/цена).** Отдельной библиотеки нет и не нужно: это 20 строк разбора, у нас он уже есть в кассе и в мобилке (сверка в отчёте «Мобилка — камера, весы…», раздел 2.1). Готовые разборщики на GitHub — мелкие проекты без поддержки.

### Боль 3. Распознавание накладных и рукописного текста

| Имя | ★ | Push | Лицензия | Кириллица | Как у нас | Риск | Польза | Труд | Вывод |
|---|---|---|---|---|---|---|---|---|---|
| Gemini (уже используем) | — | — | платный API | да, рукописный тоже | `InvoiceImportService.cs` | Нужна сеть и ключ | 5 | — | оставить основным |
| [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) PP-OCRv5, модель `eslav` / `cyrillic` | 92 282 | 2026-09-16 | Apache-2.0 | да (ru/uk/be); ң ө ү — проверить | Офлайн-запас, когда нет сети | Только печатный текст, рукописный слабо | 3 | L | присм. |
| [PaddleSharp](https://github.com/sdcb/PaddleSharp) (`Sdcb.PaddleOCR` 3.3.1) | 1459 | 2026-08-30 | Apache-2.0 | модели Paddle | .NET-обёртка PaddleOCR для кассы | Родные библиотеки Paddle ~100+ МБ, Windows x64 в первую очередь | 3 | L | присм. |
| [RapidOCR](https://github.com/RapidAI/RapidOCR) / [RapidOcrNet](https://github.com/BobLd/RapidOcrNet) (4.2.0) | 8445 / 106 | 2026-11-05 / 2026-09-27 | Apache-2.0 | модели Paddle в ONNX | Легче Paddle: ONNX Runtime (MIT), работает на Win/Linux/Android | Модели кириллицы надо взять и проверить самим | 3 | M | присм. — лучший кандидат на офлайн-запас |
| [Tesseract](https://github.com/tesseract-ocr/tesseract) + [charlesw/tesseract](https://github.com/charlesw/tesseract) (NuGet `Tesseract` 5.2.0) | 77 355 / 2462 | 2026-11-02 / 2025-04-29 | Apache-2.0 | да: `rus`, `kir` (есть в tessdata_best) | Самый лёгкий офлайн-вариант | Накладные с таблицами и фото с телефона — слабо; рукописный — нет | 2 | M | присм. |
| [docTR](https://github.com/mindee/doctr) | 6413 | 2026-11-03 | Apache-2.0 | частично | Только Python, сервер | Своего сервера нет | 1 | L | не брать |
| [EasyOCR](https://github.com/JaidedAI/EasyOCR) | 30 126 | 2025-12-05 | Apache-2.0 | да | Только Python/PyTorch | Тяжёлый, сервер | 1 | L | не брать |
| [Surya](https://github.com/datalab-to/surya) | 21 578 | 2026-10-29 | код Apache-2.0; **<span style="color:red">веса моделей — ограничения для компаний (проверить лицензию модели)</span>** | 90+ языков | Python, GPU | Сервер, лицензия весов | 1 | L | не брать |
| [Donut](https://github.com/clovaai/donut) | 6927 | 2024-07-11 | MIT | надо дообучать | — | Заброшен, нужно обучение | 1 | L | не брать |
| [invoice2data](https://github.com/invoice-x/invoice2data) | 2243 | 2026-11-04 | MIT | шаблоны — да | Только PDF с текстом, шаблон на поставщика | Python; у нас фото, а не PDF | 1 | M | не брать |
| [google_mlkit_text_recognition](https://github.com/flutter-ml/google_ml_kit_flutter) (0.17.1) | 1293 | 2026-11-04 | MIT | **кириллицы нет** (v2: латиница, китайский, деванагари, японский, корейский) | — | Русский не читает | 0 | — | **не брать** |
| [flutter_tesseract_ocr](https://github.com/khjde1207/tesseract_ocr) (0.4.31) | — | pub 2026-06-01 | проверить | rus/kir | Офлайн в мобилке | +30–60 МБ к APK | 1 | M | не брать |

Вывод: Gemini остаётся основным. Если нужен офлайн-запас для печатных накладных — RapidOcrNet + ONNX-модель кириллицы. Рукописный текст офлайн хорошо не читает ни один открытый движок.

### Боль 4. Нечёткое сопоставление названий

| Имя | ★ | Push | Лицензия | Платформы | Как у нас | Риск | Польза | Труд | Вывод |
|---|---|---|---|---|---|---|---|---|---|
| [Raffinert.FuzzySharp](https://github.com/Raffinert/FuzzySharp) (6.0.1) | 49 | 2026-11-03 | MIT | net8 | `TokenSetRatio`/`PartialRatio` вместо самописной `Similarity` в `InvoiceMatcher.cs` | Мало звёзд, но живой форк | 3 | S | **брать** |
| [FuzzySharp](https://github.com/JakeBayer/FuzzySharp) (2.0.2) | 796 | 2024-08-20 | MIT | netstandard | То же | Не развивается | 2 | S | не брать (взять форк выше) |
| [Fastenshtein](https://github.com/DanHarltey/Fastenshtein) | 290 | 2026-09-09 | MIT | net | Быстрый Левенштейн вместо `InvoiceNameParser.cs:185` | Только расстояние | 2 | S | присм. |
| [SymSpell](https://github.com/wolfgarbe/SymSpell) (6.7.3) | 3490 | 2026-10-29 | MIT | net | Исправление опечаток в поиске товара по словарю каталога (15 000 названий) | Словарь ~ десятки МБ памяти | 3 | M | присм. (для поиска в кассе) |
| [AnyAscii](https://github.com/anyascii/anyascii) (0.3.3) | 424 | 2026-06-06 | ISC | net, Dart нет | Транслит для латиница ↔ кириллица в `InvoiceNameParser.cs` | — | 3 | S | **брать** |
| [RapidFuzz](https://github.com/rapidfuzz/RapidFuzz) | 4195 | 2026-11-03 | MIT | Python/C++ | Эталон алгоритмов, сам не нужен | — | 1 | — | не брать |
| [dart-fuzzywuzzy](https://github.com/SphericalKat/dart-fuzzywuzzy) (1.2.0) | 48 | 2026-10-29 | **<span style="color:red">GPL-2.0</span>** | Dart | — | Заражает закрытый APK | 0 | — | **не брать** |
| [string_similarity](https://github.com/jeremylandon/string-similarity) (2.2.0) | — | pub 2026-04-04 | проверить (вероятно MIT) | Dart | Похожесть в мобилке | Простой Dice | 2 | S | присм. |

Soundex/Metaphone для русского и кыргызского: живых библиотек нет. Транслит + Левенштейн + свои сокращения (как сделано 03.11) работают лучше.

### Боль 5. Офлайн-first, очереди, повторы

| Имя | ★ | Push | Лицензия | Платформы | Как у нас | Риск | Польза | Труд | Вывод |
|---|---|---|---|---|---|---|---|---|---|
| [Polly](https://github.com/App-vNext/Polly) (`Polly.Core` 8.9.0) | 14 255 | 2026-10-31 | BSD-3 | net8, все | `TransportTuningHandler.cs`: срок, предохранитель, повтор GET | Повтор POST с деньгами без ключа = двойная продажа | 4 | M | **брать** |
| [Microsoft.Extensions.Http.Resilience](https://github.com/dotnet/extensions) (10.11.0) | 3272 | 2026-11-05 | MIT | net8+ | То же, готовый «стандартный набор» поверх Polly | Тянет Microsoft.Extensions 10.x — проверить совместимость с нашими 8.0 | 3 | M | присм. (вместо голого Polly) |
| [PowerSync](https://github.com/powersync-ja/powersync.dart) (2.6.0) | 255 | 2026-11-02 | SDK Apache-2.0 (в GitHub не распознана), **<span style="color:red">сервис — свой сервер или платное облако</span>** | Flutter | — | Нужен доступ к базе Postgres сервера NurCRM — его у нас нет | 1 | L | не брать |
| [ElectricSQL](https://github.com/electric-sql/electric) | 10 420 | 2026-10-06 | Apache-2.0 | сервер + клиенты | — | Нужен свой сервер рядом с Postgres | 1 | L | не брать |
| [Replicache / Zero (rocicorp/mono)](https://github.com/rocicorp/mono) | 3435 | 2026-11-04 | Apache-2.0 | JS | — | Только JS, нужен сервер | 0 | — | не брать |
| [drift](https://github.com/simolus3/drift) | 3301 | 2026-11-04 | MIT | Flutter | Уже используем (outbox в `outbox_meta.dart`) | — | — | — | оставить |
| [dio_smart_retry](https://github.com/rodion-m/dio_smart_retry) | 106 | 2024-12-02 | MIT | Dart/dio | Повтор GET в мобилке | Почти не развивается; повтор POST — та же ловушка | 2 | S | присм. |

**Атомарное хранилище.** Готовой библиотеки не нужно. В .NET — запись во временный файл и `File.Replace` (уже сделано для файлов с деньгами), SQLite в режиме WAL. Во Flutter — транзакции drift и `flutter_secure_storage`. Outbox и идемпотентность у нас свои и правильные. Внешние «движки синхронизации» требуют своего сервера рядом с базой NurCRM, а его нет.

### Боль 6. Голос

| Имя | ★ | Push | Лицензия | Кыргызский | Как у нас | Риск | Польза | Труд | Вывод |
|---|---|---|---|---|---|---|---|---|---|
| [Vosk](https://github.com/alphacep/vosk-api) (NuGet `Vosk` 0.3.38) + модели | 15 229 | 2026-10-15 | Apache-2.0, модели тоже | **да:** `vosk-model-small-ky-0.42` (49 МБ), `vosk-model-ky-0.42` (1,1 ГБ); русская small 45 МБ | Вернуть по желанию, докачкой (`VoiceCommandParser`, `UserPreferences.cs:308`) | Качество малой модели — проверить на кассирах | 3 | M | **брать** (по желанию) |
| [vosk-flutter](https://github.com/alphacep/vosk-flutter) | 75 | 2024-10-26 | Apache-2.0 | те же модели | Голос в мобилке | Плагин давно не обновлялся | 2 | M | присм. |
| [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (pub 2.0.1) | 15 748 | 2026-11-05 | Apache-2.0; **<span style="color:red">для голосов Piper тянет espeak-ng (GPL-3) — проверить сборку</span>** | готовой кыргызской модели в каталоге нет | Один движок для распознавания, синтеза и ключевых слов на всех платформах | Модель придётся конвертировать | 3 | L | присм. |
| [whisper.cpp](https://github.com/ggml-org/whisper.cpp) / [Whisper.net](https://github.com/sandrohanea/whisper.net) (1.9.1) | 54 937 / 952 | 2026-11-05 / 2026-11-01 | MIT | обычный Whisper по-кыргызски слаб (CER ~35 %); дообученные: [BuzzASR/kyrgyz](https://huggingface.co/BuzzASR/kyrgyz) (MIT, large-v3, CER ~11 %), [UlutSoft whisper-small-kyrgyz](https://huggingface.co/UlutSoftLLC/whisper-small-kyrgyz) (**лицензия не указана**) | Распознавание диктовки на кассе | large-v3 ~3 ГБ, медленно без видеокарты; конвертация в GGML своими силами | 2 | L | присм. |
| [faster-whisper](https://github.com/SYSTRAN/faster-whisper) | 26 490 | 2026-11-02 | MIT | как Whisper | Python | Нет своего сервера | 1 | L | не брать |
| [Piper](https://github.com/OHF-Voice/piper1-gpl) (старый [rhasspy/piper](https://github.com/rhasspy/piper) — MIT, в архиве) | 6170 / 11 315 | 2026-11-02 / архив | **<span style="color:red">GPL-3.0</span>** | кыргызского голоса нет | — | GPL | 1 | L | не брать |
| [MMS-TTS kir](https://huggingface.co/facebook/mms-tts-kir) и производные Akyl-AI | — | — | **<span style="color:red">CC-BY-NC 4.0 (только некоммерческое)</span>** | да | — | Нельзя в платной кассе | 0 | — | **не брать** |
| [openWakeWord](https://github.com/dscripka/openWakeWord) | 2914 | 2025-12-30 | код Apache-2.0, **<span style="color:red">готовые модели CC-BY-NC-SA</span>** | — | Слово-активатор | Модели свои обучать | 1 | L | не брать |
| [Porcupine](https://github.com/Picovoice/porcupine) | 5018 | 2026-11-04 | SDK Apache-2.0, **<span style="color:red">нужен платный ключ AccessKey</span>** | нет | — | Плата и сеть для ключа | 1 | M | не брать |
| [flutter-webrtc](https://github.com/flutter-webrtc/flutter-webrtc) | 4504 | 2026-10-27 | MIT | — | Звонок ИИ в мобилке | Нужен сервер сигнализации | 2 | L | присм. |
| [LiveKit client Flutter](https://github.com/livekit/client-sdk-flutter) | 422 | 2026-11-04 | Apache-2.0 | — | Звонок ИИ через LiveKit | Нужен сервер LiveKit (свой Apache или облако) | 2 | L | присм. |

Кыргызского синтеза речи (TTS) с разрешённой коммерческой лицензией не нашлось. Варианты: облако (Gemini и подобные) или обучение своего голоса на своих записях (движок и данные — отдельное решение). Для звонка ИИ проще WebSocket к Live API модели, без WebRTC.

### Боль 7. Отчёты, Excel, PDF, графики

| Имя | ★ | Push | Лицензия | Платформы | Как у нас | Риск | Польза | Труд | Вывод |
|---|---|---|---|---|---|---|---|---|---|
| [MiniExcel](https://github.com/mini-software/MiniExcel) (1.46.0) | 3657 | 2026-11-04 | Apache-2.0 | net, без зависимостей | Большие выгрузки в `ExcelWorkbookBuilder.cs`, чтение импорта в `BulkImport/ProductFileReader.cs` | 2.0 пока предварительная; стили слабее | 3 | S | **брать** |
| [ClosedXML](https://github.com/ClosedXML/ClosedXML) (0.105.1) | 5751 | 2026-10-21 | MIT | net | Красивые отчёты владельца со стилями и формулами | Много памяти на больших файлах | 2 | M | присм. |
| [EPPlus](https://github.com/EPPlusSoftware/EPPlus) | 2045 | 2026-11-04 | **<span style="color:red">Polyform Noncommercial (с 5-й версии), для бизнеса — платная</span>** | net | — | Платно | 0 | — | **не брать** |
| [QuestPDF](https://github.com/QuestPDF/QuestPDF) (2026.9.1) | 14 217 | 2026-10-29 | **<span style="color:red">Community — бесплатно, только пока годовой оборот компании < 1 млн $</span>**, иначе от 999 $ | net | Уже используем (2024.12.3) | При росте оборота нужна покупка; госорганам всегда платно | 3 | S | оставить, следить за оборотом |
| [PDFsharp/MigraDoc](https://github.com/empira/PDFsharp) (6.5.0) | 1010 | 2026-10-19 | MIT (GitHub не распознал файл) | net6+ | Запасной вариант, если QuestPDF станет платным | Беднее разметка | 2 | M | присм. (план Б) |
| [ScottPlot](https://github.com/ScottPlot/ScottPlot) (`ScottPlot.Avalonia` 5.1.59) | 6798 | 2026-08-15 | MIT | Avalonia 11 | Вместо `AnalyticsChartRenderer.cs` для сложных графиков (масштаб, подсказки) | Свой вид придётся настраивать под тему | 2 | M | присм. |
| [LiveCharts2](https://github.com/Live-Charts/LiveCharts2) (2.0.5) | 5504 | 2026-07-20 | MIT | Avalonia (SkiaSharp) | Анимированные графики в сводке владельца | Тяжелее, SkiaSharp | 2 | M | присм. |
| [OxyPlot](https://github.com/oxyplot/oxyplot) (`OxyPlot.Avalonia` 2.2.0) | 3543 | 2026-10-17 | MIT | Avalonia | — | Обёртка Avalonia развивается медленно | 1 | M | не брать |
| [fl_chart](https://github.com/imaNNeo/fl_chart) | 7592 | 2026-10-30 | MIT | Flutter | Графики в мобилке владельца | — | 3 | S | присм. (если нужны графики в мобилке) |

### Боль 8. Avalonia: контролы, темы, доступность, тесты интерфейса

| Имя | ★ | Push | Лицензия | Как у нас | Риск | Польза | Труд | Вывод |
|---|---|---|---|---|---|---|---|---|
| Avalonia 11.2.5 → **11.3.22** | 31 760 | 2026-11-04 | MIT | Все проекты | Исправления виртуализации и памяти; 12.1.5 — крупный переход, позже | 3 | M | присм. (11.3, не 12) |
| [Avalonia.Controls.DataGrid](https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid) | 49 | 2026-08-03 | MIT | Уже используем | — | — | — | оставить |
| [TreeDataGrid](https://github.com/AvaloniaUI/Avalonia.Controls.TreeDataGrid) | 347 | архив 2025-10-13 | открытая копия MIT в архиве; **<span style="color:red">новые версии — Avalonia Accelerate (платно; бесплатно только для мелких)</span>** | Быстрые таблицы на 15 000 строк | Нет развития открытой копии | 1 | M | не брать |
| [Semi.Avalonia](https://github.com/irihitech/Semi.Avalonia) / [Ursa.Avalonia](https://github.com/irihitech/Ursa.Avalonia) | 1969 / 1581 | 2026-11-03 | MIT | Готовые контролы (пагинация, NumericUpDown, формы) | Своя тема «Coca» уже сделана — смешение стилей | 2 | M | присм. (брать отдельные контролы как образец) |
| [SukiUI](https://github.com/kikipoulet/SukiUI), [Material.Avalonia](https://github.com/AvaloniaCommunity/Material.Avalonia), [FluentAvalonia](https://github.com/amwx/FluentAvalonia) | 2702 / 1172 / 1621 | 2026 | MIT | Темы целиком | Конфликт с нашей темой | 1 | L | не брать |
| [Icons.Avalonia](https://github.com/Projektanker/Icons.Avalonia) | 430 | 2025-11-24 | MIT (иконки — Material Design / FontAwesome, свои лицензии) | Иконки шрифтом | У нас уже свои | 1 | S | не брать |
| [FlaUI](https://github.com/FlaUI/FlaUI) | 3195 | 2026-08-13 | MIT | Автопрогон кассы по hwnd окна (правило 5) | Только Windows | 3 | M | присм. (для живых проверок без координат) |
| [Verify](https://github.com/VerifyTests/Verify) + [Verify.Avalonia](https://github.com/VerifyTests/Verify.Avalonia) (1.4.1) | 3473 / 45 | 2026-11-05 / 2026-11-03 | MIT | Снимки окон через Avalonia.Headless (уже есть) | Снимки шрифтов разнятся между ПК | 3 | M | присм. (вместе с п. 1 топа) |

### Боль 9. Тесты без боевой базы

| Имя | ★ | Push | Лицензия | Как у нас | Риск | Польза | Труд | Вывод |
|---|---|---|---|---|---|---|---|---|
| [WireMock.Net](https://github.com/wiremock/WireMock.Net) (2.21.0) | 1683 | 2026-11-01 | Apache-2.0 | Поддельный NurCRM на `localhost`, адрес через `DESKTOP_MARKET_API_URL` | Ответы сервера надо записать с NBS и держать в актуальном виде | 5 | M | **брать** |
| [MockHttp](https://github.com/richardszalay/mockhttp) (7.1.0) | 1764 | 2026-08-08 | MIT | Подмена `HttpMessageHandler` без сети, легче WireMock | Только .NET-тесты | 4 | S | **брать** (для мелких тестов) |
| SQLite в памяти (`Microsoft.Data.Sqlite`, уже есть) | — | — | MIT | `pos_local.db` → `:memory:` | Нужна переменная пути данных | 4 | S | **брать** |
| [System.IO.Abstractions](https://github.com/TestableIO/System.IO.Abstractions) (22.3.0) | 1627 | 2026-11-04 | MIT | Подмена файловой системы | Большая переделка вызовов `File.*` | 2 | L | не брать (хватит временной папки) |
| [CsCheck](https://github.com/AnthonyLloyd/CsCheck) (5.0.1) | 271 | 2026-11-03 | Apache-2.0 | Тесты денег на случайных чеках, удобнее из C# | — | 4 | S | **брать** |
| [FsCheck](https://github.com/fscheck/FsCheck) (3.6.0) | 1256 | 2026-10-21 | BSD-3 | То же, крупнее сообщество | Сделан под F#, из C# многословнее | 3 | S | присм. |
| [Stryker.NET](https://github.com/stryker-mutator/stryker-net) (`dotnet-stryker` 5.0.0) | 2123 | 2026-11-05 | Apache-2.0 | Проверка, ловят ли тесты ошибки в коде денег | Запускает тесты — только после п. 1 (иначе боевая база) | 3 | M | присм. (после WireMock) |

### Боль 10. Память и скорость

| Имя | ★ | Push | Лицензия | Как у нас | Риск | Польза | Труд | Вывод |
|---|---|---|---|---|---|---|---|---|
| [dotnet-counters / dotnet-gcdump / dotnet-trace](https://github.com/dotnet/diagnostics) | 1332 | 2026-11-05 | MIT | Инструменты на ПК, код не меняется | Снимок памяти на секунду замораживает кассу — не во время продажи | 4 | S | **брать** |
| [PerfView](https://github.com/microsoft/perfview) | 4777 | 2026-11-03 | MIT | Разбор `.gcdump`/`.nettrace` на Windows | Сложный интерфейс | 3 | S | **брать** (как инструмент) |
| [BenchmarkDotNet](https://github.com/dotnet/BenchmarkDotNet) | 11 525 | 2026-11-01 | MIT | Отдельный проект замеров (поиск, сопоставление, округление) | — | 3 | S | **брать** |
| [Sentry .NET](https://github.com/getsentry/sentry-dotnet) / sentry_flutter | 774 | 2026-11-05 | SDK MIT; **<span style="color:red">свой сервер Sentry — FSL (не открытая), облако платное</span>** | Отчёты о падениях с ПК клиентов | Данные уходят наружу — решение владельца; совместимый открытый сервер — GlitchTip | 3 | M | присм. |
| [MemLab](https://github.com/facebook/memlab) | — | — | MIT | Только для браузера (сайт market.nurcrm.kg) | Не для .NET | 1 | — | не брать |
| [Flutter DevTools](https://github.com/flutter/devtools) | 1736 | 2026-11-04 | BSD-3 | `flutter run --profile` → память, кадры, сеть | — | 4 | S | **брать** (как инструмент) |

### Боль 11. Обновления и установка

| Имя | ★ | Push | Лицензия | Как у нас | Риск | Польза | Труд | Вывод |
|---|---|---|---|---|---|---|---|---|
| [Velopack](https://github.com/velopack/velopack) 1.0.1 → 1.2.161 | 2443 | 2026-11-04 | MIT | Уже используем | Проверить цепочку обновлений на тестовом канале | 3 | S | **брать** (обновить) |
| [ota_update](https://github.com/4Q-s-r-o/ota_update) (7.1.0) | 193 | 2026-02-25 | MIT | Мобилка: скачать APK с GitHub Releases и открыть установщик | Своё обновление уже есть | 2 | S | присм. |
| [in_app_update](https://github.com/jonasbark/flutter_in_app_update) (5.0.0) | 360 | 2026-09-21 | проверить (в GitHub не распознана) | Только Google Play | Мы раздаём APK сами | 1 | S | не брать (пока нет Play) |
| [Shorebird](https://github.com/shorebirdtech/shorebird) | 3050 | 2026-11-05 | **<span style="color:red">своя лицензия, сервис платный выше бесплатной квоты</span>** | Горячие правки Dart без нового APK | Код уходит через чужой сервис | 2 | M | присм. |
| [Obtainium](https://github.com/ImranR98/Obtainium) | 21 220 | 2026-11-01 | **<span style="color:red">GPL-3.0</span>** | Не встраивать. Клиенту можно поставить как отдельное приложение, оно следит за GitHub Releases | — | 1 | — | не брать в код |
| F-Droid (`gitlab.com/fdroid`) | — | — | **<span style="color:red">клиент GPL-3, сервер fdroidserver AGPL-3</span>** | Свой репозиторий APK как отдельная служба | Отдельный сервер, не встраивать | 1 | L | не брать |

Для Android-кассы (Avalonia) Velopack не подходит. Своё обновление через `PackageInstaller` — правильный путь, библиотека не нужна.

### Боль 12. Telegram

| Имя | ★ | Push | Лицензия | Как у нас | Риск | Польза | Труд | Вывод |
|---|---|---|---|---|---|---|---|---|
| [Telegram.Bot](https://github.com/TelegramBots/Telegram.Bot) (22.10.3.3) | 3650 | 2026-10-09 | MIT | Вместо ручных запросов к `api.telegram.org` в `OwnerMorningReport.cs`, `PurchasePaymentService.cs` и др. | Крупные версии ломают API — закрепить версию | 3 | M | присм. (выгода — типы и файлы чеков) |
| [WTelegramClient](https://github.com/wiz0u/WTelegramClient) (4.4.9) | 1326 | 2026-09-26 | MIT | MTProto от имени пользователя | Бан аккаунта, хранение сессии | 1 | L | не брать |
| [Televerse](https://github.com/theweaverlabs/televerse) (3.7.0) | 102 | 2026-10-10 | BSD-3 | Бот на Dart | Бот в мобилке не нужен | 1 | M | не брать |
| [Telethon](https://github.com/LonamiWebs/Telethon) | 12 065 | архив 2026-02 | MIT | Python | В архиве | 0 | — | не брать |
| [aiogram](https://github.com/aiogram/aiogram) | 5915 | 2026-11-01 | MIT | `NurSupportBot` — если когда-нибудь писать на Python | У нас бот на .NET | 1 | — | не брать |
| [python-telegram-bot](https://github.com/python-telegram-bot/python-telegram-bot) | 29 555 | 2026-11-01 | **<span style="color:red">LGPL-3 (GitHub показывает GPL-3)</span>** | — | — | 0 | — | не брать |

---

## 3. Лицензионные риски

| Риск | Что | Что делать |
|---|---|---|
| <span style="color:red">**GPL / AGPL (заражает закрытый код)**</span> | dart-fuzzywuzzy (GPL-2), Piper (piper1-gpl, GPL-3), espeak-ng внутри голосов Piper / sherpa-onnx TTS (GPL-3), Obtainium и F-Droid (GPL-3 / AGPL-3), python-telegram-bot (LGPL-3) | Не встраивать в кассу и APK. Допустимо только отдельной программой или службой. |
| <span style="color:red">**Только некоммерческое (NC)**</span> | facebook/mms-tts-kir и все Akyl-AI (CC-BY-NC 4.0), готовые модели openWakeWord (CC-BY-NC-SA), EPPlus 5+ (Polyform Noncommercial) | Нельзя в платной кассе. |
| <span style="color:red">**Платно выше порога / по ключу**</span> | QuestPDF (бесплатно при обороте < 1 млн $, госорганам платно), TreeDataGrid в Avalonia Accelerate, Porcupine (AccessKey), Shorebird и PowerSync Cloud (платные сервисы), свой сервер Sentry (FSL) | QuestPDF уже у нас: при обороте ≥ 1 млн $ купить лицензию или перейти на PDFsharp (MIT). Остальное не брать. |
| **Лицензия не указана или не распознана** | UlutSoft whisper-small-kyrgyz (нет лицензии = пользоваться нельзя), xprinter_flutter (в README MIT, файл не распознан), flutter_thermal_printer, in_app_update, print_bluetooth_thermal, flutter_tesseract_ocr | Перед подключением открыть LICENSE руками. Без лицензии не брать. |
| **Веса моделей отдельно от кода** | Surya, PaddleOCR, Vosk, Whisper-дообучения | Код Apache/MIT, но у каждой модели своя карточка. Проверены: Vosk ky — Apache-2.0, BuzzASR/kyrgyz — MIT. |
| **Безопасные** | MIT / Apache-2.0 / BSD-3 / ISC: всё, что «брать» в топ-10 | Сохранять NOTICE/LICENSE в папке установки (Apache-2.0 требует NOTICE). |

---

## 4. Что не нашлось (нужно делать самим или ждать)
- TSPL-библиотеки для .NET с поддержкой — нет; наш `BarcodeLabelService.cs` остаётся.
- Кыргызский TTS с коммерческой лицензией — нет.
- Офлайн-распознавание рукописных накладных на кириллице хорошего качества — нет; Gemini остаётся.
- Готовая синхронизация офлайн без своего сервера рядом с базой NurCRM — нет; свой outbox правильный.
- Фонетика (Soundex/Metaphone) для русского и кыргызского — живых библиотек нет.

Источники: GitHub API (search/repositories, 05.11.2026), api.nuget.org, pub.dev API, [модели Vosk](https://alphacephei.com/vosk/models), [PaddleOCR PP-OCRv5 multilingual](https://www.paddleocr.ai/latest/en/version3.x/algorithm/PP-OCRv5/PP-OCRv5_multi_languages.html), [языки ML Kit v2](https://developers.google.com/ml-kit/vision/text-recognition/v2/languages), [лицензия QuestPDF](https://questpdf.com/license/), [Avalonia Accelerate и TreeDataGrid](https://github.com/AvaloniaUI/Avalonia/discussions/19804), [facebook/mms-tts-kir](https://huggingface.co/facebook/mms-tts-kir), [BuzzASR/kyrgyz](https://huggingface.co/BuzzASR/kyrgyz), [архив rhasspy/piper](https://github.com/rhasspy/piper).


---

## Статус внедрения (05.11.2026, вечер)
- Подключено: **Raffinert.FuzzySharp 6.0.1** (Levenshtein.Distance вместо самописного в `InvoiceNameParser`) и **AnyAscii 0.3.3** (запасной транслит в `InvoiceNameData.Phonetic`) — `Similarity` в `InvoiceMatcher` была лишь обёрткой над `NameScore`, правила «числа и объём совпадают» свои; **MiniExcel 1.46.0** (`Services/LargeExcelWriter.cs`, от 5001 строки: ревизия, `SectionTableExport`); **ZXing.Net 0.16.11**; **Verify.Xunit 31.0.0** (снимки чека, возврата, TSPL; xunit 2.9.2 -> 2.9.3 из-за зависимости Verify) и тесты сопоставления/выгрузки; проект замеров `tools/bench/NurMarketKassa.Bench` (BenchmarkDotNet 0.15.8, вне решения); CI `.github/workflows/build.yml`; `dotnet-counters`/`dotnet-gcdump` установлены глобально.
- Не трогали: Velopack (план проверки — `Velopack — план обновления (05.11.2026).md`), Vosk-ky (решение 30.10).
- Памятка: `Как снимать память — dotnet-counters и dotnet-gcdump (05.11.2026).md`.
