# Печать и PDF без System.Drawing — инвентарь (В18, 06.11.2026)

Исходные пункты плана: А4-08 (этикетки и ценники на Linux/Android не печатаются), А5-04 (PDF на Android), АЛ-4 (предпросмотр листа A4 пустой), АЛ-5 (CUPS для A4), АЛ-1 (графики Word).
Почему падает: `System.Drawing.Common` в .NET 7+ работает только в Windows — на Linux и Android любой вызов `Bitmap`/`Graphics`/`Font` даёт `PlatformNotSupportedException`.
`System.Drawing.Printing` (PrintDocument, PrinterSettings) тоже только Windows — это печать через драйвер, она остаётся под `OperatingSystem.IsWindows()`.
QuestPDF берёт нативную `libQuestPdfSkia.so` (glibc): на Android она не грузится (в APK 1.17.77 натив убран, кнопки PDF скрыты через `PdfSupport`).
SkiaSharp 2.88.9 уже приходит во все приложения через Avalonia (Windows, Portable-Linux, Android: `SkiaSharp.NativeAssets.Android`/`Linux`/`Win32`) — прямая ссылка в Infrastructure той же версии новых пакетов не добавляет.

## Таблица (файл → что использует → где падает → план)

| № | Файл | Что использует | Падает на | План / итог |
|---|------|----------------|-----------|-------------|
| 1 | `BarcodeLabelService.cs` (GenerateLabelBitmap, DrawLinearBlock, DrawQrBlock, BitmapToEscPosRaster, SendRawLabel, BuildTsplJob, CreateSpoolerDocument) | Graphics/Bitmap/Font/StringFormat, PrintDocument | Linux, Android (растр этикетки для термопринтера) | Растр этикетки на Skia (`LabelSceneSkia` по готовой `LabelScene`), ESC/POS и TSPL из `SKBitmap`; печать на Linux/Android — через сырой транспорт (CUPS raw, /dev/usb/lp, TCP:9100). Драйверный путь Windows (PrintDocument) без изменений |
| 2 | `BarcodeLabelService.Scene.cs` | чистая «сцена» (без System.Drawing) | работает везде | источник раскладки для Skia-растра |
| 3 | `PriceTagService.cs` (GenerateBitmap, 6 шаблонов, PrintThermal, A4-сетка) | Graphics/Bitmap | Linux, Android | растр — Skia по `BuildScene`; термопечать — как этикетки; A4-сетка через драйвер остаётся Windows |
| 4 | `PriceTagService.Scene.cs` | сцена без System.Drawing | работает | источник раскладки |
| 5 | `A4SheetService.cs` (DrawCell, RenderPage, RenderCell, CreateDocument) | Graphics/Bitmap/Font/StringFormat, PrintDocument | Linux, Android (предпросмотр пустой — АЛ-4; печать — А4-08) | `A4SheetSkia`: DrawCell/RenderPage на SKCanvas (предпросмотр везде), PDF листов через `SKDocument.CreatePdf` и печать на Linux через CUPS (`lp`) |
| 6 | `BarcodeA4SheetWindow.cs:404-406` | `bmp.Save(System.Drawing…Png)` | Linux, Android | предпросмотр берёт PNG из Skia там, где нет Windows |
| 7 | `LabelSceneRenderer.cs` (Avalonia) | System.Drawing только в ветке Windows | ветка Windows | оставлен: на Windows предпросмотр = GDI+ как раньше |
| 8 | `GraphicReceiptGenerator.cs` + `MonospaceReceiptRenderer.cs` (графический чек, ESC *) | Graphics/Bitmap/Font, `[SupportedOSPlatform("windows")]` | Linux, Android (чек печатался текстом) | `MonospaceReceiptSkia` + растр ESC * из SKBitmap; режим «Графический» разрешён везде |
| 9 | `AnalyticsChartRenderer.cs` (RenderBars, RenderPareto, RenderParetoClassic, RenderPie) | Graphics/Bitmap/Font, `[SupportedOSPlatform("windows")]` | Linux, Android (графики пропускались, `SafeChartImage`) | `AnalyticsChartSkia` (те же размеры, цвета, подписи), `SafeChartImage` рисует везде |
| 10 | `SafeChartImage.cs` | обход на не-Windows | — | вызывает Skia-версию на не-Windows |
| 11 | `ClientDocPrint.cs` (печать страниц-PNG документа клиента) | Image/Graphics/PrintDocument | Linux | на Linux: страницы в PDF (`SKDocument`) и `lp`; Windows без изменений |
| 12 | `Purchases/PurchasesRegistryExport.cs:389-413` (печать реестра приходов) | System.Drawing.Image, PrintDocument | Linux | то же, что ClientDocPrint |
| 13 | `ScaleKeySheetService.cs` (печать листа кнопок весов) | Graphics/Font/PrintDocument | Linux, Android | Word остаётся; печать — только Windows (понятное сообщение уже есть, `InstalledPrinters` пуст) |
| 14 | `DynamicPaymentQrService.cs` | Bitmap только в Windows-методах; на Android/Linux — ZXing + `QrPngWriter` | уже без System.Drawing | не менялось |
| 15 | `QrPngWriter.cs`, `PortableImageReader.cs`, `ShtrikhPcPictureConverter.cs`, `ShtrikhPcPlaceholderRenderer.cs`, `ShtrikhPcBmp.cs` | уже Skia / ручной BMP | работает | образец стиля |
| 16 | `RemoteSupportHost.cs` (снимок экрана JPEG, EncoderParameter) | System.Drawing (захват экрана) | Linux, Android | удалённая помощь — только Windows; в В18 не меняется (заглушка в Portable) |
| 17 | `WebView2Host.cs` | `System.Drawing.Rectangle` (WebView2) | не собирается в Portable (заменён `WebView2Stubs.cs`) | не трогаем |
| 18 | `LabelPrinterSelector.cs` (`PrintingSupported`, PrinterSettings) | `PrinterSettings` под `IsWindows()` | — | `PrintingSupported` = true там, где есть сырой транспорт |
| 19 | `ReceiptPrintService.cs:194-200` | режим «Графический» → текст на не-Windows | — | после п. 8 графический чек печатается и на Linux/Android |
| 20 | `Tests/Snapshots/PrintTextSnapshotTests.cs` | `using System.Drawing` (тест, Windows) | — | не менялось |
| 21 | QuestPDF-рендеры (10): `ClientDocRenderer`, `InvestmentExport`, `TransferDocumentRenderer`, `SupplierReturnDocumentRenderer`, `StartupCostsExport`, `PurchasesRegistryExport`, `ProfitPayExport`, `ReceiptPaperPdf`, `RevisionExportService`, `ReceiptPdfPreviewService` | QuestPDF (натив glibc) | Android | Windows/Linux — QuestPDF как раньше; Android: PDF чека через Skia (`ReceiptSkiaPdf`), для остальных 9 — PDF скрыт (Word/Excel остаются), см. отчёт |
| 22 | `Infrastructure.csproj`, `Android.csproj` | `System.Drawing.Common 10.0.8`, `QuestPDF … ExcludeAssets=native` | — | + `SkiaSharp 2.88.9` в Infrastructure; строка ExcludeAssets остаётся (остальные 9 рендеров на Android всё ещё QuestPDF) |

Итого: 19 файлов с `System.Drawing` (по `using`/полным именам), 10 рендеров QuestPDF, 2 csproj.

## Что выбрано (и почему)

- Windows-печать через драйвер (PrintDocument) и Windows-предпросмотр GDI+ **не менялись**: вид на принтере проверить нельзя, поэтому Skia-растр на Windows используется только в тестах для сравнения с GDI+; на Linux/Android он основной.
- Раскладку не дублируем: Skia рисует готовые `LabelScene` (этикетки, ценники) — их же рисует Avalonia в предпросмотре.
- Шрифты на Linux: нет Arial/Consolas/Courier New. `SkiaFonts` подбирает близкие (Arial → Liberation Sans/Arimo/DejaVu Sans; Consolas/Courier New → Liberation Mono/DejaVu Sans Mono) и для символов, которых нет в шрифте (ө ү ң ў қ ғ ҳ), берёт `SKFontManager.MatchCharacter`.
- CUPS: `lp -d <очередь> [-n копии] [-o media=A4] [-o print-scaling=none] -- файл` через `ArgumentList` (без shell), таймаут 30 с, очередь — из `lpstat -e` (проверка по списку и по шаблону имени). Сырые данные (ESC/POS, TSPL) уже шли через `lp -o raw` в `PosixPrinterTransport`.

## Итог (что сделано в В18)

Новые файлы (Infrastructure, `Services/Skia/` и рядом): `SkiaText.cs` (шрифты и текст), `LabelSceneSkia.cs` (растр сцены этикетки/ценника, PNG, яркость), `BarcodeLabelService.Skia.cs` (штрихкод на SKCanvas, проверка «текст не помещается»), `A4SheetSkia.cs` (лист A4: предпросмотр, ячейка, PDF), `PriceTagSheetSkia.cs` (сетка ценников в PDF), `GraphicReceiptSkia.cs` (графический чек), `AnalyticsChartSkia.cs` (4 графика), `ReceiptSkiaPdf.cs` (PDF чека для Android), `PngPagesPdf.cs` (страницы документа в PDF), `CupsPrintService.cs`.

| Пункт | Что изменено |
|-------|--------------|
| А4-08 | Linux/Android: этикетки (`BarcodeLabelService.Print`) и термо-ценники (`PriceTagService.PrintThermal`) рисует Skia и отправляет командами ESC/POS или TSPL через транспорт платформы. `LabelPrinterSelector.PrintingSupported` = Windows или есть транспорт принтера. Растр штрихов совпадает со старым GDI+ на 0 точек (тест) |
| АЛ-4 | Предпросмотр листа A4 на Linux/Android рисует Skia (`A4SheetSkia.RenderPage`); ячейка и штрихи на 203/300/600 dpi совпадают со старым растром на 0 точек |
| АЛ-5 (CUPS) | Листы A4 (этикетки, ценники — готовые виды и свой шаблон) и документы клиента/накладные: PDF (Skia) → `lp`. Принтеры A4 на Linux — только очереди `CUPS:` |
| А5-04 | PDF чека на Android пишет Skia (`ReceiptSkiaPdf`); кнопка «PDF» на чеке снова видна везде (условие в `ReceiptActionBar` было перевёрнуто: кнопка показывалась только там, где PDF нет). Остальные 9 PDF-рендеров на Android — QuestPDF не загружается, кнопки скрыты (`PdfSupport.IsAvailable`), Word/Excel остаются |
| АЛ-1 | Графики Word-отчётов на Linux/Android больше не пропускаются: `AnalyticsChartRenderer` на не-Windows отдаёт `AnalyticsChartSkia`; на Windows — GDI+ как раньше; столбцы совпадают по цвету и площади на ±1 % |
| АЛ-3 | Графический чек (ESC *) работает на Linux/Android (`GraphicReceiptSkia`); режим «Графика» в настройках больше не закрыт |

Тесты: `NurMarketKassa.Tests/Wave18/` (размеры, углы, не однородная картинка, пиксельное сравнение штрихов со старым растром GDI+, текст — по числу тёмных точек и габаритам, ESC/POS/TSPL/ESC * одинаковые байты, запасные шрифты для ө ү ң ў қ ғ ҳ, PDF — число страниц и размер, CUPS — проверка имён и аргументов `lp`).

Не сделано: прямая печать реестра закупок (`PurchasesRegistryExport.PrintLandscape`) и листа кнопок весов (`ScaleKeySheetService.Print`) на Linux — остаётся «PDF/Word и открыть в просмотрщике»; PDF остальных 9 QuestPDF-документов на Android (нужен либо отдельный Skia-построитель по каждому документу, либо другой пакет — не добавлялся); снимок экрана удалённой помощи (`RemoteSupportHost`, System.Drawing) — программа удалённой помощи на Linux/Android в кассе остаётся заглушкой.
