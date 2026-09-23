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

    public BulkImportWindow()
    {
        InitializeComponent();
        _catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
    }

    public static void Open(Window? owner)
    {
        var window = new BulkImportWindow();
        if (owner != null)
            window.Show(owner);
        else
            window.Show();
    }

    private async void DownloadTemplate_Click(object? sender, RoutedEventArgs e)
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
        var bytes = new UTF8Encoding(true).GetBytes(ProductCsvImporter.BuildTemplateCsv());
        await stream.WriteAsync(bytes);
    }

    private async void PickFile_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("Выберите CSV-файл", "CSV файлын тандаңыз", "Choose a CSV file", "Bir CSV dosyası seçin", "CSV faylni tanlang"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (files.Count == 0)
            return;

        var path = files[0].TryGetLocalPath();
        if (path is null)
            return;

        FileNameText.Text = Path.GetFileName(path);

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            SummaryText.Text = Tr.T($"Не удалось прочитать файл: {ex.Message}", $"Файлды окуу мүмкүн болгон жок: {ex.Message}",
                $"Could not read the file: {ex.Message}", $"Dosya okunamadı: {ex.Message}", $"Faylni o'qib bo'lmadi: {ex.Message}");
            return;
        }

        var parsed = ProductCsvImporter.Parse(text);
        var products = CatalogCacheService.Products;

        _rows = parsed.Select(row =>
        {
            var vm = new PreviewRowVm
            {
                RowNumber = row.RowNumber,
                Name = row.Name,
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

            var existing = products.FirstOrDefault(p => string.Equals(p.Barcode, row.Barcode, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                vm.ExistingProductId = existing.Id;
                vm.StatusText = Tr.T($"Будет обновлён: «{existing.Title}»", $"Жаңыртылат: «{existing.Title}»",
                    $"Will be updated: “{existing.Title}”", $"Güncellenecek: “{existing.Title}”",
                    $"Yangilanadi: “{existing.Title}”");

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
                            $" · stock {oldText} → {newText} (replace, not add)",
                            $" · stok {oldText} → {newText} (değiştirme, ekleme değil)",
                            $" · qoldiq {oldText} → {newText} (almashtirish, qo'shish emas)");
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
        var validCount = _rows.Count(r => r.Source.IsValid);
        SummaryText.Text = Tr.T($"Строк: {_rows.Count}, из них корректных: {validCount}.",
            $"Саптар: {_rows.Count}, туурасы: {validCount}.",
            $"Rows: {_rows.Count}, valid: {validCount}.",
            $"Satır: {_rows.Count}, geçerli: {validCount}.",
            $"Qatorlar: {_rows.Count}, to'g'risi: {validCount}.");
        StartImportButton.IsEnabled = validCount > 0 && _catalogApi != null;
    }

    private async void StartImport_Click(object? sender, RoutedEventArgs e)
    {
        if (_catalogApi is null || _rows.Count == 0)
            return;

        StartImportButton.IsEnabled = false;

        var created = 0;
        var updated = 0;
        var failed = 0;

        foreach (var row in _rows)
        {
            if (!row.Source.IsValid)
            {
                failed++;
                continue;
            }

            row.StatusText = "⏳ " + Tr.T("Загружается…", "Жүктөлүүдө…", "Uploading…", "Yükleniyor…", "Yuklanmoqda…");
            SummaryText.Text = Tr.T($"Обработка строки {row.RowNumber} из {_rows.Count}…",
                $"{row.RowNumber}-сап иштелүүдө, бардыгы {_rows.Count}…",
                $"Processing row {row.RowNumber} of {_rows.Count}…",
                $"{row.RowNumber}. satır işleniyor, toplam {_rows.Count}…",
                $"{row.RowNumber}-qator qayta ishlanmoqda, jami {_rows.Count}…");

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
                    await _catalogApi.UpdateProductAsync(id, request).ConfigureAwait(true);
                    row.StatusText = "✅ " + Tr.T("Обновлён", "Жаңыртылды", "Updated", "Güncellendi", "Yangilandi");
                    updated++;
                }
                else
                {
                    await _catalogApi.CreateProductAsync(request).ConfigureAwait(true);
                    row.StatusText = "✅ " + Tr.T("Создан", "Түзүлдү", "Created", "Oluşturuldu", "Yaratildi");
                    created++;
                }
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
            $"Даяр: түзүлдү {created}, жаңыртылды {updated}, каталар {failed}.",
            $"Done: created {created}, updated {updated}, errors {failed}.",
            $"Tamamlandı: oluşturulan {created}, güncellenen {updated}, hata {failed}.",
            $"Tayyor: yaratildi {created}, yangilandi {updated}, xatolar {failed}.");
        StartImportButton.IsEnabled = true;

        if (created > 0 || updated > 0)
            await CatalogCacheService.RefreshFromApiAsync().ConfigureAwait(true);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private sealed class PreviewRowVm : INotifyPropertyChanged
    {
        public int RowNumber { get; init; }
        public string Name { get; init; } = "";
        public string Barcode { get; init; } = "";
        public string QuantityText { get; init; } = "";
        public string PriceText { get; init; } = "";
        public ProductCsvImporter.ImportRow Source { get; init; } = null!;
        public string? ExistingProductId { get; set; }

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
