using System.Collections.Specialized;
using System.Globalization;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-06, исследование «Кассы для одежды» (О-70), владелец: «делай всё по этапно». Приёмка одежды по размерам:
/// товар с размерами при добавлении в приёмку открывает сетку «цвет × размер» (VariantReceiveWindow); скан этикетки размера
/// прибавляет 1 этому размеру; колонка «Размеры» — что принимается, нажатие на неё открывает сетку снова. После проведения —
/// этикетки на принятые размеры. Только в сфере «Одежда».</summary>
public partial class WarehouseWindow
{
    private bool _variantGridOpen;
    private bool _suppressAutoVariantGrid;

    /// <summary>Колонка «Размеры» — последняя в таблице приёмки (см. разметку ReceivingGrid).</summary>
    private Avalonia.Controls.DataGridColumn? SizesColumn => ReceivingGrid.Columns.Count > 9 ? ReceivingGrid.Columns[9] : null;

    private void InitVariantReceiving()
    {
        if (SizesColumn is { } col)
        {
            col.Header = Tr.T("Размеры", "Өлчөмдөр", "Sizes", "Bedenler", "O'lchamlar");
            col.IsVisible = MarketSpheres.IsClothing;
        }
        if (!MarketSpheres.IsClothing)
            return;

        _viewModel.ReceivingLines.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null || _suppressAutoVariantGrid)
                return;
            foreach (var line in e.NewItems.OfType<ReceivingLineVm>().ToList())
                Dispatcher.UIThread.Post(() => _ = OpenVariantGridAsync(line, automatic: true), DispatcherPriority.Background);
        };
        // Повторный скан штрихкода товара с размерами прибавляет 1 к строке — тогда сумма по размерам не сходится:
        // открываем сетку, чтобы указать, какой это размер.
        _viewModel.ReceivingLineAdded += line =>
        {
            if (line.VariantQuantities is { Count: > 0 } && Math.Abs(line.Quantity - line.VariantTotal) > 0.0005)
                Dispatcher.UIThread.Post(() => _ = OpenVariantGridAsync(line, automatic: false), DispatcherPriority.Background);
        };
        _viewModel.ReceivingPosted += lines => Dispatcher.UIThread.Post(() => _ = OfferVariantLabelsAsync(lines), DispatcherPriority.Background);
    }

    /// <summary>Сетка размеров для строки. automatic — строку только что добавили: у товара без размеров ничего не делаем,
    /// а если кассир закрыл сетку, не указав размеры, строку убираем (товар с размерами без них не принять — сервер
    /// держит остаток товара равным сумме остатков размеров).</summary>
    private async Task OpenVariantGridAsync(ReceivingLineVm line, bool automatic)
    {
        if (_variantGridOpen || string.IsNullOrWhiteSpace(line.ProductId) || !_viewModel.ReceivingLines.Contains(line))
            return;
        if (automatic && line.VariantQuantities is { Count: > 0 })
            return;
        var variants = await ProductVariantCache.GetAsync(line.ProductId, TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        if (variants is not { Count: > 0 })
        {
            if (!automatic)
                PosMessageBox.Show(this, Tr.T("У этого товара нет размеров и цветов (или они не загрузились).", "Бул товардын өлчөмдөрү жана түстөрү жок (же жүктөлгөн жок).",
                        "This product has no sizes or colours (or they did not load).", "Bu ürünün bedeni ve rengi yok (veya yüklenemedi).", "Bu mahsulotning o'lcham va ranglari yo'q (yoki yuklanmadi)."),
                    Tr.T("Размеры", "Өлчөмдөр", "Sizes", "Bedenler", "O'lchamlar"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _variantGridOpen = true;
        try
        {
            var dialog = new VariantReceiveWindow(line.ProductName, variants, line.VariantQuantities);
            await dialog.ShowDialog(this).ConfigureAwait(true);
            if (dialog.Result is { Count: > 0 } result)
            {
                line.SetVariants(result, dialog.Labels);
                PosLogger.Log($"Приёмка: размеры товара {line.ProductId} — {line.VariantSummary}.", "CATALOG");
            }
            else if (dialog.Result is not null || automatic)
            {
                if (line.VariantQuantities is not { Count: > 0 })
                {
                    _viewModel.ReceivingLines.Remove(line);
                    ReceivingSummaryText.Text = Tr.T($"«{line.ProductName}» — товар с размерами: укажите в сетке, сколько каких пришло. Строка убрана.",
                        $"«{line.ProductName}» — өлчөмдүү товар: торчодо кайсынысы канча келгенин көрсөтүңүз. Сап алынды.",
                        $"“{line.ProductName}” has sizes: enter in the grid how many of each arrived. The row was removed.",
                        $"«{line.ProductName}» bedenli ürün: tabloda hangisinden kaç geldiğini girin. Satır kaldırıldı.",
                        $"«{line.ProductName}» — o'lchamli mahsulot: jadvalda qaysi biridan qancha kelganini kiriting. Qator olib tashlandi.");
                }
                else
                {
                    line.Quantity = line.VariantTotal;
                }
            }
            else if (line.VariantQuantities is { Count: > 0 })
            {
                // Отмена у строки, где размеры уже были, — количество обратно к сумме по размерам.
                line.Quantity = line.VariantTotal;
            }
        }
        finally
        {
            _variantGridOpen = false;
            RefreshReceivingSummary();
        }
    }

    /// <summary>Скан этикетки размера в приёмке: строка товара (новая или уже есть) +N этому размеру. true — код был размером.</summary>
    private bool TryReceiveVariantScan(string code, double quantity)
    {
        if (!MarketSpheres.IsClothing || VariantBarcodeIndex.Find(code) is not { } hit || hit.Variant.Id is not { } variantId)
            return false;
        var product = CatalogCacheService.Products.ToList().FirstOrDefault(p => string.Equals(p.Id, hit.ProductId, StringComparison.OrdinalIgnoreCase));
        if (product is null)
            return false;

        var line = _viewModel.ReceivingLines.FirstOrDefault(l => string.Equals(l.ProductId, product.Id, StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            _suppressAutoVariantGrid = true;
            try
            {
                _viewModel.AddReceivingProduct(product, quantity);
            }
            finally
            {
                _suppressAutoVariantGrid = false;
            }
            line = _viewModel.ReceivingLines.FirstOrDefault(l => string.Equals(l.ProductId, product.Id, StringComparison.OrdinalIgnoreCase));
            if (line is null)
                return false;
            line.SetVariants(new Dictionary<string, double>(), new Dictionary<string, string>());
        }
        var label = string.Join(", ", new[] { hit.Variant.Size, hit.Variant.Color }.Where(x => !string.IsNullOrWhiteSpace(x)));
        line.AddVariant(variantId, label, quantity);
        ReceivingGrid.ScrollIntoView(line, null);
        PosLogger.Log($"Приёмка: скан размера {code} → {product.Title} ({label}) +{quantity:0.###}.", "CATALOG");
        return true;
    }

    /// <summary>После проведения: этикетки на принятые размеры — столько, сколько пришло каждого размера.</summary>
    private async Task OfferVariantLabelsAsync(IReadOnlyList<ReceivingLineVm> lines)
    {
        var items = new List<(string Title, string Barcode, string VariantText, double Price, int Copies)>();
        var missing = 0;
        foreach (var line in lines.Where(l => l.VariantQuantities is { Count: > 0 } && !string.IsNullOrWhiteSpace(l.ProductId)))
        {
            var variants = await ProductVariantCache.GetAsync(line.ProductId!, TimeSpan.FromSeconds(5)).ConfigureAwait(true) ?? new();
            foreach (var (variantId, qty) in line.VariantQuantities!)
            {
                var v = variants.FirstOrDefault(x => string.Equals(x.Id, variantId, StringComparison.OrdinalIgnoreCase));
                var copies = (int)Math.Ceiling(qty);
                if (v is null || copies <= 0)
                    continue;
                if (string.IsNullOrWhiteSpace(v.Barcode))
                {
                    missing += copies;
                    continue;
                }
                var text = string.Join(", ", new[] { v.Size, v.Color }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));
                items.Add((line.ProductName, v.Barcode!.Trim(), text, v.Price is > 0 ? v.Price.Value : line.SalePrice, copies));
            }
        }
        if (items.Count == 0 && missing == 0)
            return;
        var total = items.Sum(i => i.Copies);
        var missingNote = missing > 0
            ? Tr.T($"\n\nУ {missing} шт. нет штрихкода размера — создайте его в «Размеры и цвета» («Создать штрихкоды») и напечатайте там.",
                $"\n\n{missing} даанада өлчөмдүн штрихкоду жок — аны «Өлчөмдөр жана түстөр» бөлүмүндө түзүп, ошол жерден басыңыз.",
                $"\n\n{missing} pcs have no size barcode — create it in “Sizes and colours” (“Create barcodes”) and print there.",
                $"\n\n{missing} adedin beden barkodu yok — «Bedenler ve renkler»de oluşturup oradan yazdırın.",
                $"\n\n{missing} donada o'lcham shtrix-kodi yo'q — uni «O'lchamlar va ranglar»da yarating va o'sha yerdan chop eting.")
            : "";
        if (total == 0)
        {
            PosMessageBox.Show(this, missingNote.Trim(), Tr.T("Этикетки", "Этикеткалар", "Labels", "Etiketler", "Yorliqlar"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!await PosDialogs.ConfirmYesNoModalAsync(this, Tr.T($"Напечатать этикетки на принятые размеры: {total} шт.?", $"Кабыл алынган өлчөмдөргө этикетка басылсынбы: {total} даана?",
                    $"Print labels for the received sizes: {total} pcs?", $"Kabul edilen bedenler için etiket yazdırılsın mı: {total} adet?",
                    $"Qabul qilingan o'lchamlarga yorliq chop etilsinmi: {total} dona?") + missingNote))
            return;
        var printer = UserPreferences.Instance.LabelPrinterDevicePath;
        if (string.IsNullOrWhiteSpace(printer))
        {
            PosMessageBox.Show(this, Tr.T("Принтер этикеток не выбран — выберите его один раз в окне «Этикетка» любого товара.", "Этикетка принтери тандалган эмес — аны каалаган товардын «Этикетка» терезесинен бир жолу тандаңыз.",
                    "No label printer selected — choose it once in the “Label” window of any product.", "Etiket yazıcısı seçilmedi — herhangi bir ürünün «Etiket» penceresinden bir kez seçin.",
                    "Yorliq printeri tanlanmagan — uni istalgan mahsulotning «Yorliq» oynasida bir marta tanlang."),
                Tr.T("Этикетки", "Этикеткалар", "Labels", "Etiketler", "Yorliqlar"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var template = LabelTemplateStore.Load();
        int printed = 0, failed = 0;
        foreach (var item in items)
        {
            var left = item.Copies;
            while (left > 0)
            {
                var batch = Math.Min(left, 99);
                var request = new LabelPrintRequest(item.Title, item.Barcode, item.Price.ToString("0.00", CultureInfo.InvariantCulture) + " сом", batch, printer, template,
                    StoreName: UserPreferences.Instance.StoreName, VariantText: item.VariantText);
                var result = await Task.Run(() => BarcodeLabelService.Print(request)).ConfigureAwait(true);
                if (result == LabelPrintResult.Success)
                    printed += batch;
                else
                    failed++;
                left -= batch;
            }
        }
        PosLogger.Log($"Приёмка: этикетки размеров напечатаны {printed} шт., сбоев {failed}.", "CATALOG");
        if (failed > 0)
            PosMessageBox.Show(this, Tr.T($"Напечатано {printed} шт., сбоев печати: {failed} — проверьте принтер этикеток.", $"{printed} даана басылды, басуу каталары: {failed} — этикетка принтерин текшериңиз.",
                    $"Printed {printed}, print failures: {failed} — check the label printer.", $"{printed} adet yazdırıldı, hata: {failed} — etiket yazıcısını kontrol edin.",
                    $"{printed} dona chop etildi, xatolar: {failed} — yorliq printerini tekshiring."),
                Tr.T("Этикетки", "Этикеткалар", "Labels", "Etiketler", "Yorliqlar"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
