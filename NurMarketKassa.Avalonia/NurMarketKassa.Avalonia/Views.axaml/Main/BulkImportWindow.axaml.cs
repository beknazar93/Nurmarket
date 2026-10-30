using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Массовая загрузка товаров из CSV (2026-09-04) — практичная замена "переноса между
/// складами" из ТЗ: у аккаунта только один общий склад (см. память сессии), так что реальный
/// перенос между складами не построить на API; вместо этого — быстрая загрузка/обновление
/// большого списка товаров из файла (например, от поставщика), самый частый практический смысл
/// "массового переноса товаров" для магазина с одной точкой.
/// <para>Совпадение с существующим товаром — по штрихкоду. Сохранение идёт через тот же
/// ICatalogApiService.CreateProductAsync/UpdateProductAsync, что и обычная форма "Новый товар" —
/// никакой отдельной, непроверенной логики сохранения.</para></summary>
public partial class BulkImportWindow : Window
{
    private List<PreviewRowVm> _rows = [];
    private ICatalogApiService? _catalogApi;
    private bool _closed;

    public BulkImportWindow()
    {
        InitializeComponent();
        // 2026-10-19, этап О9: пустой предпросмотр — что делать.
        EmptyHintTitle.Text = Tr.T("Файл ещё не выбран", "Файл тандала элек", "No file selected yet", "Henüz dosya seçilmedi", "Fayl hali tanlanmagan");
        EmptyHintText.Text = Tr.T("Скачайте шаблон CSV, впишите товары (название, штрихкод, количество, цену) и нажмите «Выбрать файл…» — здесь появится проверка каждой строки.",
            "CSV үлгүсүн жүктөп алып, товарларды жазыңыз (аты, штрихкод, саны, баасы) жана «Файлды тандоо…» баскычын басыңыз — бул жерде ар бир сап текшерилет.",
            "Download the CSV template, fill in the products (name, barcode, quantity, price) and press «Choose file…» — each row will be checked here.",
            "CSV şablonunu indirin, ürünleri yazın (ad, barkod, miktar, fiyat) ve «Dosya seç…»e basın — her satır burada kontrol edilir.",
            "CSV shablonini yuklab oling, mahsulotlarni yozing (nomi, shtrix-kod, miqdor, narx) va «Faylni tanlash…»ni bosing — har bir qator shu yerda tekshiriladi.");
        _catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
        // Окно закрыли посреди загрузки — остальные строки не отправляем: иначе товары
        // продолжали бы создаваться невидимо, без итога и без возможности остановить.
        Closed += (_, _) => _closed = true;
    }

    public static void Open(Window? owner)
    {
        var window = new BulkImportWindow();
        if (owner != null)
            window.Show(owner);
        else
            window.Show();
    }

    // 2026-10-29: срочный баг клиента — любое исключение в async void обработчике уходило в общий диалог
    // «Ошибка — Произошла ошибка в программе». Теперь каждый обработчик окна ловит всё сам, пишет в журнал
    // и показывает понятный текст в строке итога.
    private void ReportFailure(string where, Exception ex)
    {
        PosLogger.Log($"Массовая загрузка: {where}: {ex}", "WAREHOUSE");
        try
        {
            SummaryText.Text = Tr.T($"Не удалось выполнить действие: {ex.Message}", $"Аракетти аткаруу мүмкүн болгон жок: {ex.Message}",
                $"Could not complete the action: {ex.Message}", $"İşlem tamamlanamadı: {ex.Message}", $"Amalni bajarib bo'lmadi: {ex.Message}");
        }
        catch
        {
            // Окно уже закрыто — журнала достаточно.
        }
    }

    private async void DownloadTemplate_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Tr.T("Сохранить шаблон CSV", "CSV үлгүсүн сактоо", "Save CSV template", "CSV şablonunu kaydet", "CSV shablonini saqlash"),
                SuggestedFileName = "products_template.csv",
                FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
            });
            if (file is null)
                return;

            await using var stream = await file.OpenWriteAsync();
            // 2026-10-29: при перезаписи более длинного файла «хвост» старого содержимого оставался.
            if (stream.CanSeek)
                stream.SetLength(0);
            var bytes = new UTF8Encoding(true).GetBytes(ProductCsvImporter.BuildTemplateCsv());
            await stream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            ReportFailure("шаблон не сохранён", ex);
        }
    }

    private async void PickFile_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await PickFileCoreAsync();
        }
        catch (Exception ex)
        {
            // Предпросмотр от прошлого файла не оставляем: иначе «Загрузить» отправил бы его строки.
            _rows = [];
            StartImportButton.IsEnabled = false;
            ReportFailure("файл не обработан", ex);
        }
    }

    /// <summary>Читает файл и разбирает его в таблицу предпросмотра. Ошибка файла — в SummaryText, не исключение.</summary>
    private async Task PickFileCoreAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("Выберите CSV-файл", "CSV файлын тандаңыз", "Choose a CSV file", "Bir CSV dosyası seçin", "CSV faylni tanlang"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (files.Count == 0)
            return;

        // 2026-10-29: без локального пути (Android, облачные провайдеры) молча ничего не делали — читаем поток.
        var path = files[0].TryGetLocalPath();
        FileNameText.Text = path is not null ? Path.GetFileName(path) : files[0].Name;

        byte[] bytes;
        try
        {
            // FileShare.ReadWrite: файл может быть открыт в Excel (он держит запись) — раньше это был IOException.
            await using var stream = path is not null
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
                : await files[0].OpenReadAsync();
            if (stream.CanSeek && stream.Length > ProductFileReader.MaxFileBytes)
            {
                ShowFileError(Tr.T("Файл больше 20 МБ. Разделите его на части.", "Файл 20 МБдан чоң. Бөлүктөргө бөлүңүз.",
                    "The file is larger than 20 MB. Split it into parts.", "Dosya 20 MB'tan büyük. Parçalara bölün.", "Fayl 20 MB dan katta. Uni qismlarga bo'ling."));
                return;
            }
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            bytes = buffer.ToArray();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовая загрузка: файл не прочитан: {ex}", "WAREHOUSE");
            ShowFileError(Tr.T($"Не удалось прочитать файл: {ex.Message}", $"Файлды окуу мүмкүн болгон жок: {ex.Message}",
                $"Could not read the file: {ex.Message}", $"Dosya okunamadı: {ex.Message}", $"Faylni o'qib bo'lmadi: {ex.Message}"));
            return;
        }

        // Кодировка (UTF-8 с BOM и без, UTF-16, Windows-1251), Excel вместо CSV, заголовки — внутри разборщика.
        var outcome = ProductCsvImporter.ParseBytes(bytes);
        if (outcome.Error is not null)
        {
            ShowFileError(outcome.Error);
            return;
        }
        var parsed = outcome.Rows;

        // 2026-10-29: снимок списка каталога + словарь по штрихкоду вместо FirstOrDefault на каждую строку
        // (строки × товары — окно зависало на тысячах строк) и устойчиво к повторам штрихкода в каталоге.
        var byBarcode = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in CatalogCacheService.Products.ToArray())
        {
            var code = p?.Barcode?.Trim();
            if (!string.IsNullOrEmpty(code))
                byBarcode.TryAdd(code, p!);
        }
        // Штрихкод, уже встретившийся выше в этом же файле (2026-09-26, стресс-тест массовой
        // загрузки): обе строки считались «новыми», и на сервере появлялись два товара с одним
        // штрихкодом. Берём первую, повтор показываем и пропускаем.
        var firstRowByBarcode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        _rows = parsed.Select(row =>
        {
            var vm = new PreviewRowVm
            {
                RowNumber = row.RowNumber,
                // В таблице — не больше 150 символов (в файле может быть огромная ячейка); полное название в Source.
                Name = row.Name.Length > 150 ? row.Name[..150] + "…" : row.Name,
                Barcode = row.Barcode,
                QuantityText = row.Quantity?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—",
                PriceText = row.Price?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
                Source = row,
            };

            if (!row.IsValid)
            {
                vm.StatusText = $"❌ {row.ParseError}";
                return vm;
            }

            if (firstRowByBarcode.TryGetValue(row.Barcode, out var firstRow))
            {
                vm.DuplicateOfRow = firstRow;
                vm.StatusText = "❌ " + Tr.T(
                    $"штрихкод уже есть в строке {firstRow} — строка пропускается",
                    $"штрихкод {firstRow}-сапта мурунтан эле бар — бул сап өткөрүлүп жиберилет",
                    $"barcode already appears in row {firstRow} — row skipped",
                    $"bu barkod zaten {firstRow}. satırda var — satır atlanıyor",
                    $"shtrix-kod {firstRow}-qatorda allaqachon bor — qator o'tkazib yuboriladi");
                return vm;
            }
            firstRowByBarcode[row.Barcode] = row.RowNumber;

            byBarcode.TryGetValue(row.Barcode, out var existing);
            if (existing != null)
            {
                vm.ExistingProductId = existing.Id;
                vm.StatusText = Tr.T($"Будет обновлён: «{existing.Title}»", $"Жаңыртылат: «{existing.Title}»",
                    $"Will be updated: “{existing.Title}”", $"Güncellenecek: «{existing.Title}»",
                    $"Yangilanadi: «{existing.Title}»");

                // Остаток ЗАМЕНЯЕТСЯ, а не прибавляется. Владельцы регулярно грузят накладную
                // поставщика, ожидая приход: при 40 на складе и «60» в файле станет 60, а не 100.
                // Показываем это прямо в строке, пока импорт ещё не запущен.
                if (row.Quantity is { } newQty)
                {
                    var oldQty = existing.Quantity;
                    if (Math.Abs(oldQty - newQty) > 1e-6)
                    {
                        var oldText = oldQty.ToString("0.###", CultureInfo.InvariantCulture);
                        var newText = newQty.ToString("0.###", CultureInfo.InvariantCulture);
                        vm.StatusText += Tr.T(
                            $" · остаток {oldText} → {newText} (замена, не приход)",
                            $" · калдык {oldText} → {newText} (алмаштыруу, кошуу эмес)",
                            $" · stock {oldText} → {newText} (replaced, not added)",
                            $" · stok {oldText} → {newText} (değiştirilir, stok girişi değil)",
                            $" · qoldiq {oldText} → {newText} (almashtirish, kirim emas)");
                    }
                }
            }
            else
            {
                vm.StatusText = Tr.T("Новый товар", "Жаңы товар", "New product", "Yeni ürün", "Yangi mahsulot");
            }

            return vm;
        }).ToList();

        PreviewGrid.ItemsSource = _rows;
        var validCount = _rows.Count(r => r.CanImport);
        SummaryText.Text = Tr.T($"Строк: {_rows.Count}, из них корректных: {validCount}.",
            $"Саптар: {_rows.Count}, анын ичинен туурасы: {validCount}.",
            $"Rows: {_rows.Count}, valid: {validCount}.",
            $"Satır: {_rows.Count}, geçerli: {validCount}.",
            $"Qatorlar: {_rows.Count}, shundan to'g'ri: {validCount}.");
        // Каталог ещё не загружался — совпадения по штрихкоду не найдены, все строки выглядят «новыми».
        if (byBarcode.Count == 0 && CatalogCacheService.LastSyncTime is null)
            SummaryText.Text += " " + Tr.T("Каталог ещё не загружен — «Новый товар» может оказаться уже существующим; дождитесь загрузки каталога и выберите файл снова.",
                "Каталог али жүктөлө элек — «Жаңы товар» мурунтан бар болушу мүмкүн; каталог жүктөлгөнчө күтүп, файлды кайра тандаңыз.",
                "The catalog has not loaded yet — a “New product” may already exist; wait for the catalog and choose the file again.",
                "Katalog henüz yüklenmedi — «Yeni ürün» zaten var olabilir; katalogun yüklenmesini bekleyip dosyayı yeniden seçin.",
                "Katalog hali yuklanmagan — «Yangi mahsulot» allaqachon mavjud bo'lishi mumkin; katalog yuklanishini kutib, faylni qayta tanlang.");
        StartImportButton.IsEnabled = validCount > 0 && _catalogApi != null;
    }

    /// <summary>Файл не принят целиком: пустой предпросмотр (не «Файл ещё не выбран»), кнопка загрузки выключена, причина — в итоге.</summary>
    private void ShowFileError(string message)
    {
        _rows = [];
        PreviewGrid.ItemsSource = _rows;
        StartImportButton.IsEnabled = false;
        SummaryText.Text = message;
    }

    private async void StartImport_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await StartImportCoreAsync();
        }
        catch (Exception ex)
        {
            ReportFailure("загрузка прервана", ex);
            try
            {
                StartImportButton.IsEnabled = _rows.Any(r => r.CanImport && !r.Done);
            }
            catch
            {
                // Окно закрыто.
            }
        }
    }

    private async Task StartImportCoreAsync()
    {
        if (_catalogApi is null || _rows.Count == 0)
            return;

        StartImportButton.IsEnabled = false;

        var created = 0;
        var updated = 0;
        var failed = 0;

        foreach (var row in _rows)
        {
            if (_closed)
            {
                PosLogger.Log($"Массовая загрузка остановлена: окно закрыто (создано {created}, обновлено {updated}).", "WAREHOUSE");
                return;
            }

            // Уже загруженные строки при повторном запуске не трогаем (2026-09-26): кнопка после
            // загрузки снова активна, и раньше второе нажатие создавало все новые товары ещё раз.
            // Теперь повторный запуск дозагружает только строки с ошибкой.
            if (row.Done)
                continue;

            if (!row.CanImport)
            {
                failed++;
                continue;
            }

            row.StatusText = "⏳ " + Tr.T("Загружается…", "Жүктөлүүдө…", "Uploading…", "Yükleniyor…", "Yuklanmoqda…");
            SummaryText.Text = Tr.T($"Обработка строки {row.RowNumber} из {_rows.Count}…",
                $"{_rows.Count} саптын {row.RowNumber}-сабы иштетилүүдө…",
                $"Processing row {row.RowNumber} of {_rows.Count}…",
                $"Satır işleniyor: {row.RowNumber} / {_rows.Count}…",
                $"Qator qayta ishlanmoqda: {row.RowNumber} / {_rows.Count}…");

            var request = new ProductEditRequest
            {
                Name = row.Source.Name,
                Barcode = row.Source.Barcode,
                Article = row.Source.Article,
                CategoryName = row.Source.Category,
                BrandName = row.Source.Brand,
                Unit = row.Source.Unit,
                IsWeight = row.Source.IsWeight,
                // Поля, которых не было в файле, НЕ отправляем: тело запроса абсолютное
                // («стало столько»), и ноль вместо «не трогать» обнулил бы остаток или цену
                // живого товара. Для нового товара разницы нет — сервер подставит свой ноль.
                Quantity = row.Source.Quantity ?? 0,
                SendQuantity = row.Source.Quantity.HasValue,
                PurchasePrice = row.Source.PurchasePrice,
                SendPurchasePrice = row.Source.PurchasePrice.HasValue,
                MarkupPercent = row.Source.MarkupPercent,
                SendMarkupPercent = row.Source.MarkupPercent.HasValue,
                Price = row.Source.Price,
                SendPrice = row.Source.Price.HasValue,
                IsNew = row.ExistingProductId is null,
            };

            try
            {
                if (row.ExistingProductId is { } id)
                {
                    await SendWithThrottleRetryAsync(row, () => _catalogApi.UpdateProductAsync(id, request), createdBarcode: null).ConfigureAwait(true);
                    row.StatusText = "✅ " + Tr.T("Обновлён", "Жаңыртылды", "Updated", "Güncellendi", "Yangilandi");
                    updated++;
                }
                else
                {
                    await SendWithThrottleRetryAsync(row, () => _catalogApi.CreateProductAsync(request), createdBarcode: row.Source.Barcode).ConfigureAwait(true);
                    row.StatusText = "✅ " + Tr.T("Создан", "Түзүлдү", "Created", "Oluşturuldu", "Yaratildi");
                    created++;
                }
                row.Done = true;
            }
            catch (Exception ex)
            {
                row.StatusText = "❌ " + Tr.T($"Ошибка: {ex.Message}", $"Ката: {ex.Message}",
                    $"Error: {ex.Message}", $"Hata: {ex.Message}", $"Xato: {ex.Message}");
                PosLogger.Log($"Массовая загрузка: строка {row.RowNumber} не сохранена: {ex}", "WAREHOUSE");
                failed++;
            }
        }

        SummaryText.Text = Tr.T($"Готово: создано {created}, обновлено {updated}, ошибок {failed}.",
            $"Даяр: түзүлдү — {created}, жаңыртылды — {updated}, ката — {failed}.",
            $"Done: created {created}, updated {updated}, errors {failed}.",
            $"Tamamlandı: {created} oluşturuldu, {updated} güncellendi, {failed} hata.",
            $"Tayyor: yaratildi — {created}, yangilandi — {updated}, xato — {failed}.");
        StartImportButton.IsEnabled = true;

        if (created > 0 || updated > 0)
            await CatalogCacheService.RefreshFromApiAsync().ConfigureAwait(true);
    }

    /// <summary>Сервер NurCRM на частые запросы отвечает 429 «Запрос был проигнорирован» —
    /// запрос при этом не выполнен, повторить его безопасно. Сотни строк подряд упираются в этот
    /// предел, и без паузы часть товаров просто не загружалась бы с ошибкой.
    ///
    /// <para>Обрыв соединения (2026-09-26, стресс-тест: 4 строки из 307 — «Удалённый хост
    /// принудительно разорвал подключение») тоже повторяем. Изменение товара повторять безопасно:
    /// тело абсолютное. Создание — только убедившись, что товара с этим штрихкодом на сервере
    /// нет: соединение могло оборваться уже после записи, и повтор дал бы дубль.</para></summary>
    private async Task SendWithThrottleRetryAsync(PreviewRowVm row, Func<Task> send, string? createdBarcode)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await send().ConfigureAwait(true);
                return;
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException
                                       && attempt < 4 && !_closed && _catalogApi != null)
            {
                PosLogger.Log($"Массовая загрузка: строка {row.RowNumber} — обрыв связи ({ex.Message}), повтор {attempt}.", "WAREHOUSE");
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt)).ConfigureAwait(true);
                if (createdBarcode is not null)
                {
                    try
                    {
                        if (await _catalogApi.FindWarehouseProductByBarcodeAsync(createdBarcode).ConfigureAwait(true) is not null)
                        {
                            PosLogger.Log($"Массовая загрузка: строка {row.RowNumber} — товар уже создан до обрыва, повтор не нужен.", "WAREHOUSE");
                            return;
                        }
                    }
                    catch (Exception checkEx) when (checkEx is System.Net.Http.HttpRequestException or TaskCanceledException)
                    {
                        // Проверить не удалось — связь всё ещё рвётся; следующая попытка разберётся.
                    }
                }
            }
            catch (ApiException ex) when (ex.StatusCode == 429 && attempt < 4 && !_closed)
            {
                var wait = TimeSpan.FromSeconds(15 * attempt);
                row.StatusText = "⏸ " + Tr.T(
                    $"сервер просит паузу, повтор через {wait.TotalSeconds:0} с",
                    $"сервер тыныгуу сурайт, {wait.TotalSeconds:0} с кийин кайталанат",
                    $"server asked to slow down, retrying in {wait.TotalSeconds:0} s",
                    $"sunucu beklemeyi istedi, {wait.TotalSeconds:0} sn sonra tekrar denenecek",
                    $"server pauza so'radi, {wait.TotalSeconds:0} soniyadan keyin qayta urinish");
                PosLogger.Log($"Массовая загрузка: строка {row.RowNumber} — 429, повтор через {wait.TotalSeconds:0} с.", "WAREHOUSE");
                await Task.Delay(wait).ConfigureAwait(true);
            }
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    // 2026-10-24 (аудит круга 8, С-8, П-10): Header_PointerPressed убран — шапку, ✕ и Esc даёт ModalShell.

    private sealed class PreviewRowVm : INotifyPropertyChanged
    {
        public int RowNumber { get; init; }
        public string Name { get; init; } = "";
        public string Barcode { get; init; } = "";
        public string QuantityText { get; init; } = "";
        public string PriceText { get; init; } = "";
        public ProductCsvImporter.ImportRow Source { get; init; } = null!;
        public string? ExistingProductId { get; set; }
        /// <summary>Номер строки файла с тем же штрихкодом выше — эта строка пропускается.</summary>
        public int? DuplicateOfRow { get; set; }
        /// <summary>Строка уже записана на сервер в этом окне.</summary>
        public bool Done { get; set; }
        public bool CanImport => Source.IsValid && DuplicateOfRow is null;

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            set
            {
                _statusText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
