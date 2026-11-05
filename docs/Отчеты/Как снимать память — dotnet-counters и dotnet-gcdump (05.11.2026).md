# Как снимать память кассы — dotnet-counters и dotnet-gcdump (05.11.2026)

Инструменты поставлены на этом ПК как глобальные (проект и сборка кассы не меняются): `dotnet tool install -g dotnet-counters dotnet-gcdump` (установлены 10.0.750501; `dotnet tool list -g`; PATH с `%USERPROFILE%\.dotnet\tools`). Оба — MIT. Работают с любым запущенным процессом .NET, в том числе с кассой и программой владельца.

## 1. Найти процесс
```
dotnet-counters ps
```
Нужные строки: `NurMarketKassa.Avalonia` (касса) и тот же exe с `--owner` (владелец; в журнале — `NurMarketOwner`). Берём PID.

## 2. Живые счётчики (утечка или нет)
```
dotnet-counters monitor -p <PID> --counters System.Runtime
```
Смотреть: `GC Heap Size`, `Gen 2 GC Count`, `Working Set`, `ThreadPool Thread Count`, `Allocation Rate`. Утечка — «GC Heap Size» после каждой сборки мусора стоит выше предыдущей. Запись в файл на час работы кассы:
```
dotnet-counters collect -p <PID> --counters System.Runtime --format csv -o %TEMP%\kassa-counters.csv --refresh-interval 10
```
Остановить — `Ctrl+C`. CSV открывается в Excel.

## 3. Снимок кучи (кто держит память)
```
dotnet-gcdump collect -p <PID> -o %TEMP%\kassa-1.gcdump
```
Сделать два снимка: сразу после запуска и после рабочего сценария (продажи, поиск, печать, окна). Сравнить: `dotnet-gcdump report %TEMP%\kassa-2.gcdump` (таблица типов: число объектов и размер). Файл `.gcdump` открывается в Visual Studio / PerfView — там видно цепочку «кто держит». Снимок на время паузит процесс на несколько секунд, на кассе при покупателе не делать.

## 4. Что смотреть в нашем коде
- Растущие типы: `CatalogProductTileVm` (каталог), `Bitmap`/`SKBitmap`-подобные картинки товаров, `JsonDocument`, строки отчётов, подписки на события окон (окно закрыли, а оно в куче).
- Рост «Gen 2» без роста работы — утечка подписок; рост «Working Set» без роста кучи — родная память (картинки, SQLite, WebView2).

## 5. Правила для этого ПК и данных
- Памяти всего 7,8 ГБ: не снимать gcdump у кассы и владельца одновременно, не запускать вместе со сборкой (замок `Global\NurMarketBuildLock`).
- Владелец часто работает в кассе сам: сначала спросить, снимать только тестовую копию или в паузе.
- Файлы `.gcdump` и `.csv` содержат данные из памяти (названия товаров, клиенты, возможно токены): класть в `%TEMP%`, не коммитить и не отправлять наружу.

## 6. Замеры в коде (без запущенной кассы)
Отдельный проект `tools/bench/NurMarketKassa.Bench` (BenchmarkDotNet): поиск по 15 000 товаров, сопоставление накладной, выгрузка Excel (OpenXml против MiniExcel) с колонкой `Allocated`. Запуск — в `README.md` проекта; ничего тяжёлого автоматически не запускается.

Для сравнения: тест `LargeExcelWriterTests.Write_15000Rows_AllocatesFarLessThanOpenXmlBuilder` оставляет цифры в `%TEMP%\nurmarket-tests\excel-memory-last.txt` (на 05.11.2026, 15 000 строк × 7 колонок: MiniExcel 61,8 МБ выделено за 0,2 с, OpenXml-построитель 113,5 МБ за 1,2 с; размер файлов одинаков).
