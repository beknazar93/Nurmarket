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
    // 2026-10-24, ТЗ «Склад» (Б1): колонка найдена по имени (RcvColSizes) — «Наценка %» и «Изменение» сдвинули номера.
    private Avalonia.Controls.DataGridColumn? SizesColumn => RcvColSizes;

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
            if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null || _suppressAutoVariantGrid || _viewModel.IsReorderingReceiving)
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

    /// <summary>2026-11-01 (П-4): «Этикетки на приход» — окно массовой печати с товарами накладной; копии = принятое количество (целое 1…99; весовые и дробные — 1).
    /// Размеры одежды остаются в печати размеров прихода (OfferVariantLabelsAsync) — здесь не дублируются. Новые товары без карточки пропускаются.</summary>
    private async void ReceivingLabels_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!BarcodePrintAccess.Ensure(this)) // 2026-11-01 (П-10)
            return;
        var lines = _viewModel.ReceivingLines
            .Where(l => !string.IsNullOrWhiteSpace(l.ProductId) && l.Quantity > 0 && l.VariantQuantities is not { Count: > 0 })
            .ToList();
        if (lines.Count == 0)
        {
            PosMessageBox.Show(this, Tr.T("В накладной нет принятых товаров с карточкой — добавьте товары и укажите количество.", "Накладнада карточкасы бар кабыл алынган товарлар жок — товарларды кошуп, санын көрсөтүңүз.",
                    "The receipt has no received products with a card — add products and set the quantity.", "Fişte kartı olan alınmış ürün yok — ürün ekleyip miktarı girin.", "Nakladnoyda kartochkali qabul qilingan mahsulotlar yo'q — mahsulot qo'shib, miqdorini kiriting."),
                Tr.T("Этикетки на приход", "Кирешеге этикеткалар", "Labels for this receipt", "Bu giriş için etiketler", "Kirim uchun yorliqlar"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var tiles = new Dictionary<string, NurMarketKassa.Models.Pos.CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in CatalogCacheService.Products)
            tiles[t.Id] = t;
        var items = new List<(string ProductId, string? Barcode, int Copies)>();
        foreach (var l in lines)
        {
            var mustWeigh = tiles.TryGetValue(l.ProductId!, out var tile) && tile.MustWeigh;
            var copies = BarcodePrintCatalog.CopiesForQuantity(l.Quantity, l.Unit, mustWeigh, out var capped);
            items.Add((l.ProductId!, string.IsNullOrWhiteSpace(l.Barcode) ? null : l.Barcode.Trim(), capped ? 100 : copies)); // 100 — окно покажет «копий больше 99»
        }
        await BarcodePrintWindow.OpenWith(this, items).ConfigureAwait(true);
    }

    /// <summary>После проведения: этикетки на принятые размеры — столько, сколько пришло каждого размера.</summary>
    private async Task OfferVariantLabelsAsync(IReadOnlyList<ReceivingLineVm> lines)
    {
        if (!BarcodePrintAccess.IsAllowed()) // 2026-11-01 (П-10): без права «Печать штрих-кодов» предложение напечатать этикетки не показываем
            return;
        if (!LabelPrinterSelector.PrintingSupported) // 2026-11-02 (П-3): печать этикеток работает только в Windows — предлагать нечего
            return;
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
                    $"Qabul qilingan o'lchamlarga yorliq chop etilsinmi: {total} dona?") + missingNote,
                // 2026-10-22 (модалки, пакет 3): глагол с числом вместо «Продолжить», «Не печатать» вместо «Отмена».
                Tr.T("Этикетки", "Этикеткалар", "Labels", "Etiketler", "Yorliqlar"),
                Tr.T($"Напечатать {total} шт.", $"{total} даана басуу", $"Print {total}", $"{total} adet yazdır", $"{total} dona chop etish"),
                Tr.T("Не печатать", "Баспоо", "Don't print", "Yazdırma", "Chop etmaslik")))
            return;
        var printer = UserPreferences.Instance.LabelPrinterDevicePath;
        if (string.IsNullOrWhiteSpace(printer))
        {
            // 2026-11-01: единая подсказка — принтер этикеток выбирается в Настройки → Печать.
            PosMessageBox.Show(this, LabelPrinterSelector.MissingHint(),
                Tr.T("Этикетки", "Этикеткалар", "Labels", "Etiketler", "Yorliqlar"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var template = LabelTemplateStore.Load();
        // 2026-11-02 (С3-06): на первом сбое печать останавливается (раньше шла дальше, а повтор печатал всё заново); счёт «напечатано N из M»,
        // «Допечатать остальные» печатает только то, что ещё не напечатано.
        var left = items.Select(i => i.Copies).ToArray();
        var printedAll = 0;
        while (true)
        {
            var last = LabelPrintResult.Success;
            for (var i = 0; i < items.Count && last == LabelPrintResult.Success; i++)
            {
                var item = items[i];
                while (left[i] > 0)
                {
                    var batch = Math.Min(left[i], 99);
                    var request = new LabelPrintRequest(item.Title, item.Barcode, item.Price.ToString("0.00", CultureInfo.GetCultureInfo("ru-RU")) + " сом" /* 2026-11-05 (С2-43): запятая */, batch, printer, template,
                        StoreName: UserPreferences.Instance.StoreName, VariantText: item.VariantText);
                    BarcodeLabelService.ClearPartial(printer); // 2026-11-02 (С3-16): «N из M» хранится по принтеру
                    var result = await Task.Run(() => BarcodeLabelService.Print(request)).ConfigureAwait(true);
                    if (result == LabelPrintResult.Success)
                    {
                        printedAll += batch;
                        left[i] -= batch;
                        continue;
                    }
                    last = result;
                    if (result == LabelPrintResult.PartiallySent)
                    {
                        var sent = Math.Clamp(BarcodeLabelService.PartialFor(printer)?.Sent ?? 0, 0, batch);
                        printedAll += sent;
                        left[i] -= sent;
                    }
                    break;
                }
            }
            if (last == LabelPrintResult.Success)
            {
                BarcodeLabelService.ClearPartial(printer); // допечатано — прежний «N из M» больше не нужен
                break;
            }
            var rest = left.Sum();
            PosLogger.Log($"Приёмка: этикетки размеров — напечатано {printedAll} из {total}, остановка ({last}), осталось {rest}.", "CATALOG");
            var reason = last == LabelPrintResult.PartiallySent ? LabelPrinterSelector.PartialText(printer)
                : last == LabelPrintResult.PrinterNotFound ? LabelPrinterSelector.HintText(LabelPrinterState.NotFound)
                : Tr.T("Проверьте принтер этикеток.", "Этикетка принтерин текшериңиз.", "Check the label printer.", "Etiket yazıcısını kontrol edin.", "Yorliq printerini tekshiring.");
            var again = await PosDialogs.ConfirmYesNoModalAsync(this,
                Tr.T($"Напечатано {printedAll} из {total}. ", $"{printedAll}/{total} басылды. ", $"Printed {printedAll} of {total}. ", $"{printedAll}/{total} basıldı. ", $"{printedAll}/{total} chop etildi. ")
                + reason + "\n\n"
                + Tr.T($"Допечатать только остальные — {rest} шт.? Напечатанные повторно не печатаются.", $"Калганын гана басабызбы — {rest} даана? Басылгандар кайра басылбайт.",
                    $"Print only the rest — {rest} pcs? Printed labels are not printed again.", $"Yalnızca kalanı yazdıralım mı — {rest} adet? Basılanlar tekrar basılmaz.", $"Faqat qolganini chop etamizmi — {rest} dona? Chop etilganlar qayta chop etilmaydi."),
                Tr.T("Этикетки", "Этикеткалар", "Labels", "Etiketler", "Yorliqlar"),
                Tr.T($"Допечатать остальные: {rest} шт.", $"Калганын басуу: {rest} даана", $"Print the rest: {rest}", $"Kalanı yazdır: {rest}", $"Qolganini chop etish: {rest} dona"),
                Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish")).ConfigureAwait(true);
            if (!again)
                break;
        }
        PosLogger.Log($"Приёмка: этикетки размеров напечатаны {printedAll} шт. из {total}.", "CATALOG");
        if (printedAll > 0)
            LabelPrinterSelector.LogSent(null, printer, printedAll);
    }
}
