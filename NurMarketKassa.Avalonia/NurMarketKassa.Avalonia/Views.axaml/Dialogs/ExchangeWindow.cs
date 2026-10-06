using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Строка чека, которую покупатель возвращает при обмене. UnitRefund — сколько возвращается за 1 шт.</summary>
public sealed record ExchangeReturnLine(string LineId, string? ProductId, string Title, double MaxQuantity, decimal UnitRefund);

/// <summary>Итог проведённого обмена (суммы — по серверу).</summary>
public sealed record ExchangeOutcome(string ExchangeId, string? NewSaleId, string? NewSaleNumber, decimal ReturnedAmount,
    decimal NewAmount, decimal Difference, string PaymentMethod);

/// <summary>
/// 2026-10-06, исследование «Кассы для одежды» (О-30), владелец: «делай всё по этапно». Обмен в одном окне:
/// покупатель возвращает отмеченные позиции чека и берёт другой товар (другой размер, цвет или модель),
/// касса считает «Доплатить» или «Вернуть покупателю», сервер проводит всё одним документом
/// (POST pos/sales/{id}/exchange/: возврат + новый чек, где возвращённое засчитано, деньгами — только разница).
/// Новый товар — поиском по названию или сканером (штрихкод + Enter), у одежды — с выбором размера и цвета.
/// </summary>
public sealed class ExchangeWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private sealed class ReturnRow
    {
        public required ExchangeReturnLine Line { get; init; }
        public double Qty { get; set; }
        public decimal Sum => Math.Round(Line.UnitRefund * (decimal)Qty, 2);
    }

    private sealed class NewRow
    {
        public required string ProductId { get; init; }
        public string? VariantId { get; init; }
        public required string Title { get; init; }
        public decimal UnitPrice { get; init; }
        public double Qty { get; set; } = 1;
        public double MaxQty { get; init; } = double.MaxValue;
        public decimal Sum => Math.Round(UnitPrice * (decimal)Qty, 2);
    }

    private readonly string _saleId;
    private readonly string? _receiptNumber;
    private readonly DateTime? _saleDate;
    private readonly string? _reason;
    private readonly List<ReturnRow> _returnRows;
    private readonly List<NewRow> _newRows = new();
    private readonly StackPanel _returnPanel = new() { Spacing = 6 };
    private readonly StackPanel _newPanel = new() { Spacing = 6 };
    private readonly WrapPanel _results = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox _search;
    private readonly TextBlock _returnedText = new() { FontSize = 14 };
    private readonly TextBlock _newText = new() { FontSize = 14 };
    private readonly TextBlock _resultText = new() { FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _methods = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly Button _go;
    private string _method = "cash";

    public ExchangeOutcome? Outcome { get; private set; }

    public ExchangeWindow(string saleId, string? receiptNumber, DateTime? saleDate, IReadOnlyList<ExchangeReturnLine> lines, string? reason = null)
    {
        _saleId = saleId;
        _receiptNumber = receiptNumber;
        _saleDate = saleDate;
        _reason = reason;
        _returnRows = lines.Select(l => new ReturnRow { Line = l, Qty = l.MaxQuantity }).ToList();

        Title = T("Обмен товара", "Товарды алмаштыруу", "Exchange", "Ürün değişimi", "Mahsulotni almashtirish");
        Width = 780;
        Height = 860;
        MinWidth = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Use(this, BackgroundProperty, "BrushDialogPanel");
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(false); };

        _search = UiKit.Input(this, T("Название или штрихкод (можно сканером)", "Аталышы же штрихкоду (сканер менен болот)", "Name or barcode (scanner works too)",
            "Ad veya barkod (tarayıcı da olur)", "Nomi yoki shtrix-kodi (skaner bilan ham bo'ladi)"));

        var root = new StackPanel { Margin = new Thickness(24, 18, 24, 12), Spacing = 14 };
        var title = new TextBlock { Text = Title, FontSize = 22, FontWeight = FontWeight.Bold };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(title);
        var number = string.IsNullOrWhiteSpace(receiptNumber) ? "" : receiptNumber!.Trim().TrimStart('№');
        var sub = Small(T($"Чек №{number}", $"Чек №{number}", $"Receipt No. {number}", $"Fiş No. {number}", $"Chek №{number}")
                        + (saleDate is { } d && d > DateTime.MinValue
                            ? T($" от {d:dd.MM.yyyy}", $" {d:dd.MM.yyyy}", $" of {d:dd.MM.yyyy}", $", {d:dd.MM.yyyy}", $" {d:dd.MM.yyyy}") : ""));
        root.Children.Add(sub);

        // 1. Что возвращают
        root.Children.Add(Section(1, T("Покупатель возвращает", "Сатып алуучу кайтарат", "The customer returns", "Müşteri iade ediyor", "Xaridor qaytaradi"), _returnPanel));

        // 2. Что берут взамен
        _search.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            await AddByTextAsync().ConfigureAwait(true);
        };
        _search.TextChanged += (_, _) => RenderResults();
        root.Children.Add(Section(2, T("Берёт взамен", "Ордуна алат", "Takes instead", "Yerine alıyor", "O'rniga oladi"), _search, _results, _newPanel));

        // 3. Расчёт
        Use(_returnedText, TextBlock.ForegroundProperty, "BrushText");
        Use(_newText, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(Section(3, T("Расчёт", "Эсеп", "Settlement", "Hesap", "Hisob-kitob"), _returnedText, _newText, _resultText, _methods));

        Use(_error, TextBlock.ForegroundProperty, "BrushDanger");
        root.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(false);
        _go = UiKit.Primary(this, T("Провести обмен", "Алмаштырууну жүргүзүү", "Complete exchange", "Değişimi tamamla", "Almashtirishni o'tkazish"));
        _go.MinWidth = 220;
        _go.Click += async (_, _) => await RunAsync().ConfigureAwait(true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(_go);
        var footer = new Border { Padding = new Thickness(24, 12, 24, 16), BorderThickness = new Thickness(0, 1, 0, 0), Child = buttons };
        Use(footer, Border.BorderBrushProperty, "BrushBorder");

        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(new ScrollViewer { Content = root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Grid.SetRow(footer, 1);
        layout.Children.Add(footer);
        Content = layout;

        // Телефон: поля поуже, кнопки — во всю ширину.
        NarrowLayout.Attach(this, 700, narrow =>
        {
            root.Margin = narrow ? new Thickness(12, 12, 12, 8) : new Thickness(24, 18, 24, 12);
            footer.Padding = narrow ? new Thickness(12, 10) : new Thickness(24, 12, 24, 16);
        });

        RenderReturns();
        RenderNew();
        Opened += (_, _) => _search.Focus();
    }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static string Money(decimal v) => v.ToString("N2", Ru) + T(" сом", " сом", " som", " som", " so'm");

    private decimal ReturnedTotal => _returnRows.Sum(r => r.Sum);
    private decimal NewTotal => _newRows.Sum(r => r.Sum);

    // ── Возвращаемые позиции ───────────────────────────────────────────────────────────

    private void RenderReturns()
    {
        _returnPanel.Children.Clear();
        foreach (var row in _returnRows)
        {
            var whole = Math.Abs(row.Line.MaxQuantity - Math.Round(row.Line.MaxQuantity)) < 1e-6;
            _returnPanel.Children.Add(LineRow(row.Line.Title, row.Qty, row.Sum,
                whole && row.Line.MaxQuantity > 1 ? delta =>
                {
                    row.Qty = Math.Clamp(row.Qty + delta, 0, row.Line.MaxQuantity);
                    RenderReturns();
                } : null,
                null));
        }
        UpdateTotals();
    }

    // ── Новый товар ────────────────────────────────────────────────────────────────────

    private List<CatalogProductTileVm> Find(string text)
    {
        try
        {
            return CatalogCacheService.Products
                .Where(p => !string.IsNullOrWhiteSpace(p.Title) && p.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(p => p.Title).Take(8).ToList();
        }
        catch
        {
            return new List<CatalogProductTileVm>();
        }
    }

    /// <summary>2026-10-06, владелец: «касса не видит товары на складе — при сканере говорит нет». Раньше здесь был
    /// свой поиск только по основному штрихкоду; теперь — тот же, что у основного скана кассы (база кассы: доп.
    /// штрихкоды, EAN-13/UPC-A с ведущим нулём и без, артикул).</summary>
    private static CatalogProductTileVm? FindByBarcode(string code)
    {
        try
        {
            var repo = LocalProductRepository.Instance;
            return repo.TryGetTileByBarcode(code) ?? repo.TryGetTileBySku(code);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Обмен: поиск штрихкода {code} не удался ({ex.Message}).", "WARNING");
            return null;
        }
    }

    private void RenderResults()
    {
        _results.Children.Clear();
        var text = _search.Text?.Trim() ?? "";
        if (text.Length < 2)
            return;
        foreach (var p in Find(text))
        {
            var b = UiKit.Chip(this, p.Title, false);
            b.Margin = new Thickness(0, 0, 8, 8);
            b.Click += async (_, _) => await PickAsync(p).ConfigureAwait(true);
            _results.Children.Add(b);
        }
    }

    /// <summary>Enter в поле поиска: штрихкод (сканер) — сразу этот товар; иначе — единственный найденный по названию.</summary>
    private async Task AddByTextAsync()
    {
        var text = _search.Text?.Trim() ?? "";
        if (text.Length == 0)
            return;
        // Этикетка размера — сразу этот размер, без окна выбора.
        if (VariantBarcodeIndex.Find(text) is { } hit
            && CatalogCacheService.Products.ToList().FirstOrDefault(p => string.Equals(p.Id, hit.ProductId, StringComparison.OrdinalIgnoreCase)) is { } sized
            && hit.Variant.Id is { } hitVariantId)
        {
            var label = string.Join(", ", new[] { hit.Variant.Size, hit.Variant.Color }.Where(x => !string.IsNullOrWhiteSpace(x)));
            AddNew(new NewRow
            {
                ProductId = sized.Id, VariantId = hitVariantId, Title = sized.Title + (label.Length > 0 ? " — " + label : ""),
                UnitPrice = hit.Variant.Price is > 0 ? (decimal)hit.Variant.Price.Value : (decimal)LocalCartService.ParsePrice(sized.PriceLine),
                MaxQty = hit.Variant.Quantity > 0 ? hit.Variant.Quantity : double.MaxValue,
            });
            _search.Text = "";
            _results.Children.Clear();
            _error.Text = "";
            return;
        }
        var product = FindByBarcode(text);
        if (product is null)
        {
            var byName = Find(text);
            if (byName.Count == 1)
                product = byName[0];
        }
        if (product is null)
        {
            _error.Text = T($"Товар «{text}» не найден в каталоге кассы.", $"«{text}» товары кассанын каталогунан табылган жок.", $"Product “{text}” was not found in the till catalog.",
                $"«{text}» ürünü kasa kataloğunda bulunamadı.", $"«{text}» mahsuloti kassa katalogida topilmadi.");
            return;
        }
        await PickAsync(product).ConfigureAwait(true);
    }

    private async Task PickAsync(CatalogProductTileVm p)
    {
        _error.Text = "";
        var basePrice = (decimal)LocalCartService.ParsePrice(p.PriceLine);
        var variants = await ProductVariantCache.GetAsync(p.Id, TimeSpan.FromSeconds(4)).ConfigureAwait(true);
        if (variants is { Count: > 0 } && variants.Any(v => v.IsActive))
        {
            var picker = new VariantPickerWindow(p, variants, T("Выбрать", "Тандоо", "Choose", "Seç", "Tanlash"));
            await picker.ShowDialog(this).ConfigureAwait(true);
            if (picker.Result is not { Id: { } variantId } chosen)
                return;
            var label = string.Join(", ", new[] { chosen.Size, chosen.Color }.Where(x => !string.IsNullOrWhiteSpace(x)));
            AddNew(new NewRow
            {
                ProductId = p.Id, VariantId = variantId, Title = p.Title + (label.Length > 0 ? " — " + label : ""),
                UnitPrice = chosen.Price is > 0 ? (decimal)chosen.Price.Value : basePrice, Qty = Math.Max(1, picker.Quantity),
                MaxQty = chosen.Quantity > 0 ? chosen.Quantity : double.MaxValue,
            });
        }
        else
        {
            AddNew(new NewRow { ProductId = p.Id, Title = p.Title, UnitPrice = basePrice });
        }
        _search.Text = "";
        _results.Children.Clear();
        _search.Focus();
    }

    private void AddNew(NewRow row)
    {
        var same = _newRows.FirstOrDefault(r => r.ProductId == row.ProductId && r.VariantId == row.VariantId);
        if (same != null)
            same.Qty += row.Qty;
        else
            _newRows.Add(row);
        RenderNew();
    }

    private void RenderNew()
    {
        _newPanel.Children.Clear();
        if (_newRows.Count == 0)
            _newPanel.Children.Add(Small(T("Найдите товар по названию или отсканируйте его штрихкод.", "Товарды аты боюнча табыңыз же штрихкодун сканерлеңиз.",
                "Find a product by name or scan its barcode.", "Ürünü adıyla bulun veya barkodunu tarayın.", "Mahsulotni nomi bo'yicha toping yoki shtrix-kodini skanerlang.")));
        foreach (var row in _newRows.ToList())
        {
            _newPanel.Children.Add(LineRow(row.Title, row.Qty, row.Sum,
                delta =>
                {
                    row.Qty = Math.Clamp(row.Qty + delta, 1, Math.Max(1, row.MaxQty));
                    RenderNew();
                },
                () =>
                {
                    _newRows.Remove(row);
                    RenderNew();
                }));
        }
        UpdateTotals();
    }

    // ── Итог ───────────────────────────────────────────────────────────────────────────

    private decimal Difference => NewTotal - ReturnedTotal;

    private void UpdateTotals()
    {
        _returnedText.Text = T("Возвращают на: ", "Кайтарылат: ", "Returned: ", "İade: ", "Qaytariladi: ") + Money(ReturnedTotal);
        _newText.Text = T("Новый товар на: ", "Жаңы товар: ", "New items: ", "Yeni ürünler: ", "Yangi mahsulot: ") + Money(NewTotal);
        var diff = Difference;
        _methods.Children.Clear();
        if (diff > 0.004m)
        {
            _resultText.Text = T("Доплатить: ", "Кошумча төлөө: ", "Customer pays: ", "Müşteri öder: ", "Qo'shimcha to'lov: ") + Money(diff);
            Use(_resultText, TextBlock.ForegroundProperty, "BrushCatalogPrice");
            foreach (var (code, text) in new[]
                     {
                         ("cash", T("Наличными", "Накталай", "Cash", "Nakit", "Naqd")),
                         ("transfer", T("Переводом (QR, карта)", "Которуу (QR, карта)", "Transfer (QR, card)", "Havale (QR, kart)", "O'tkazma (QR, karta)")),
                     })
            {
                var chip = UiKit.Chip(this, text, _method == code);
                chip.Margin = new Thickness(0, 6, 8, 0);
                chip.Click += (_, _) => { _method = code; UpdateTotals(); };
                _methods.Children.Add(chip);
            }
        }
        else if (diff < -0.004m)
        {
            _resultText.Text = T("Вернуть покупателю: ", "Сатып алуучуга кайтаруу: ", "Give back to the customer: ", "Müşteriye iade: ", "Xaridorga qaytarish: ") + Money(-diff)
                               + T(" наличными", " накталай", " in cash", " nakit", " naqd");
            Use(_resultText, TextBlock.ForegroundProperty, "BrushWarning");
        }
        else
        {
            _resultText.Text = T("Без доплаты", "Кошумча төлөмсүз", "No difference to pay", "Fark yok", "Qo'shimcha to'lovsiz");
            Use(_resultText, TextBlock.ForegroundProperty, "BrushSuccess");
        }
        if (_go != null)
            _go.IsEnabled = _returnRows.Any(r => r.Qty > 0) && _newRows.Count > 0;
    }

    // ── Проведение ─────────────────────────────────────────────────────────────────────

    private async Task RunAsync()
    {
        _error.Text = "";
        var returns = _returnRows.Where(r => r.Qty > 0).ToList();
        if (returns.Count == 0 || _newRows.Count == 0)
        {
            _error.Text = T("Отметьте, что возвращают, и добавьте товар взамен.", "Эмне кайтарыларын белгилеп, ордуна товар кошуңуз.", "Mark what is returned and add the replacement.",
                "İade edileni işaretleyin ve yerine ürün ekleyin.", "Nima qaytarilishini belgilang va o'rniga mahsulot qo'shing.");
            return;
        }
        if (App.SalesApi is not IPosExchangeApi api)
        {
            _error.Text = T("Обмен в этой версии недоступен.", "Бул версияда алмаштыруу жок.", "Exchange is not available in this version.", "Bu sürümde değişim yok.", "Bu versiyada almashtirish yo'q.");
            return;
        }

        var estimate = Difference;
        var method = estimate > 0.004m ? _method : "cash";
        _go.IsEnabled = false;
        var client = App.GetRequiredService<NurMarketApiClient>();
        var shiftId = NurMarketKassa.PosApp.ActiveShiftId;
        try
        {
            // Остаток смены до обмена — чтобы поймать известную ошибку сервера (см. ExchangeCashCorrections).
            var before = await ExchangeCashCorrections.ReadExpectedCashAsync(client, shiftId).ConfigureAwait(true);
            var res = await api.PosExchangeAsync(_saleId,
                returns.Select(r => (r.Line.LineId, r.Qty)).ToList(),
                _newRows.Select(n => (n.ProductId, n.VariantId, n.Qty)).ToList(),
                method).ConfigureAwait(true);

            string? Str(string name) => res.ValueKind == JsonValueKind.Object && res.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
            decimal Dec(string name, decimal fallback) => decimal.TryParse(Str(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : fallback;
            var exchangeId = Str("exchange") ?? Guid.NewGuid().ToString();
            var returned = Dec("returned_amount", ReturnedTotal);
            var newAmount = Dec("new_amount", NewTotal);
            var difference = Dec("difference", newAmount - returned);
            Outcome = new ExchangeOutcome(exchangeId, Str("new_sale"), Str("new_sale_number"), returned, newAmount, difference, method);
            PosLogger.Log($"Обмен по продаже {_saleId}: вернули {returned:0.00}, выдали {newAmount:0.00}, разница {difference:0.00} ({method}), новый чек №{Str("new_sale_number")}, документ {exchangeId}.", "SALES");

            // Наличные, реально прошедшие через ящик: доплата наличными или сдача.
            var cashDelta = difference > 0 ? (method == "cash" ? difference : 0m) : difference;
            _ = Task.Run(() => ExchangeCashCorrections.CheckAsync(client, shiftId, before, cashDelta, returned, exchangeId));

            // В журнал смены — как возврат (сервер тоже считает его возвратом), чтобы отчёты кассы сходились с сервером.
            ShiftEventsStore.Record(ShiftEventsStore.KindReturn, shiftId, ShiftEventsStore.OperationKey(exchangeId), (double)returned, "exchange");
            SaleDetailCache.Forget(_saleId);
            foreach (var n in _newRows.Where(n => n.VariantId != null))
                ProductVariantCache.Adjust(n.ProductId, n.VariantId!, -n.Qty);
            PosDataEvents.RaiseSalesChanged();

            var returnedLines = returns.Select(r => (r.Line.Title, r.Qty, r.Line.UnitRefund, r.Sum)).ToList();
            var issuedLines = _newRows.Select(n => (n.Title, n.Qty, n.UnitPrice, n.Sum)).ToList();
            var cashier = NurMarketKassa.PosApp.CurrentUserDisplayName;
            var printError = await Task.Run(() => OperationReceiptPrinter.PrintExchange(_receiptNumber, _saleDate, Outcome.NewSaleNumber,
                returnedLines, issuedLines, returned, newAmount, difference, method, _reason, cashier)).ConfigureAwait(true);
            if (printError is not null)
                PosMessageBox.Show(this, printError, Title ?? "", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close(true);
        }
        catch (ApiException ex)
        {
            _error.Text = T("Обмен не проведён: ", "Алмаштыруу жүргүзүлгөн жок: ", "Exchange failed: ", "Değişim yapılamadı: ", "Almashtirish o'tkazilmadi: ") + ex.Message;
            PosLogger.Log($"Обмен по продаже {_saleId} не проведён: {ex.StatusCode} {ex.Message}", "WARNING");
            _go.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _error.Text = T("Обмен не проведён (нет связи с сервером?): ", "Алмаштыруу жүргүзүлгөн жок (сервер менен байланыш жокпу?): ", "Exchange failed (no connection?): ",
                "Değişim yapılamadı (bağlantı yok mu?): ", "Almashtirish o'tkazilmadi (server bilan aloqa yo'qmi?): ") + ex.Message;
            PosLogger.Log($"Обмен по продаже {_saleId} не проведён: {ex}", "WARNING");
            _go.IsEnabled = true;
        }
    }

    // ── Оформление ─────────────────────────────────────────────────────────────────────

    /// <summary>Строка позиции: название, «− кол-во +», сумма, крестик (у нового товара).</summary>
    private Border LineRow(string title, double qty, decimal sum, Action<double>? step, Action? remove)
    {
        var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), BorderThickness = new Thickness(1) };
        Use(row, Border.BackgroundProperty, "BrushPanelSoft");
        Use(row, Border.BorderBrushProperty, "BrushBorder");
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        var name = new TextBlock { Text = title, FontSize = 14.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Use(name, TextBlock.ForegroundProperty, qty > 0 ? "BrushText" : "BrushTextSoft");
        g.Children.Add(name);

        var qtyPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0) };
        if (step != null)
            qtyPanel.Children.Add(SmallButton("−", () => step(-1)));
        var q = new TextBlock { Text = "× " + qty.ToString("0.###", CultureInfo.InvariantCulture), FontSize = 14.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 36, TextAlignment = TextAlignment.Center };
        Use(q, TextBlock.ForegroundProperty, "BrushText");
        qtyPanel.Children.Add(q);
        if (step != null)
            qtyPanel.Children.Add(SmallButton("+", () => step(1)));
        Grid.SetColumn(qtyPanel, 1);
        g.Children.Add(qtyPanel);

        var s = new TextBlock { Text = Money(sum), FontSize = 14.5, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 90, TextAlignment = TextAlignment.Right };
        Use(s, TextBlock.ForegroundProperty, "BrushCatalogPrice");
        Grid.SetColumn(s, 2);
        g.Children.Add(s);

        if (remove != null)
        {
            var x = SmallButton("✕", remove);
            x.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(x, 3);
            g.Children.Add(x);
        }
        row.Child = g;
        return row;
    }

    private Button SmallButton(string text, Action click)
    {
        var b = new Button
        {
            Content = text, Width = 34, Height = 34, Padding = new Thickness(0), CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(1), Focusable = false,
            FontSize = 15, FontWeight = FontWeight.Bold, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        Use(b, Button.BackgroundProperty, "BrushPanel");
        Use(b, Button.BorderBrushProperty, "BrushBorder");
        Use(b, Button.ForegroundProperty, "BrushText");
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Пронумерованная карточка шага — как в «Новом прокате».</summary>
    private Border Section(int number, string title, params Control[] content)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14), BorderThickness = new Thickness(1) };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        var stack = new StackPanel { Spacing = 10 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13) };
        Use(badge, Border.BackgroundProperty, "BrushAccent");
        var num = new TextBlock { Text = number.ToString(CultureInfo.InvariantCulture), FontSize = 13, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Use(num, TextBlock.ForegroundProperty, "BrushAccentForeground");
        badge.Child = num;
        head.Children.Add(badge);
        var t = new TextBlock { Text = title, FontSize = 15.5, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        Use(t, TextBlock.ForegroundProperty, "BrushText");
        head.Children.Add(t);
        stack.Children.Add(head);
        foreach (var c in content)
            stack.Children.Add(c);
        card.Child = stack;
        return card;
    }

    private TextBlock Small(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
