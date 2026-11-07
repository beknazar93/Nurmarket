using NurMarketKassa.Core.Application;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-06: В15г — часть класса PurchasesWindow (Goods): перенесено из PurchasesWindow.Receive.cs без изменений логики.
public partial class PurchasesWindow
{
    private void ReceiveFocusCell(ReceiveState s, ReceivingLineVm line)
    {
        if (!s.RowMap.TryGetValue(line, out var p))
            return;
        p.Refresh(true);
        var box = line.IsNew && string.IsNullOrWhiteSpace(line.ProductName) && p.Name is not null ? p.Name : p.Qty;
        box.Focus();
        box.SelectAll();
    }

    /// <summary>Конец цепочки Enter: снова в поле скана (если товар добавлен из панели поставщика и она открыта — в её поиск).</summary>
    private void ReceiveFocusScan(ReceiveState s)
    {
        var target = s.AddedFromPanel && s.GoodsOpen ? s.GoodsSearch : s.Scan;
        target.Focus();
        target.SelectAll();
    }

    private void ReceiveHighlight(ReceiveState s, ReceivingLineVm line)
    {
        if (s.Highlight is { } old && !ReferenceEquals(old, line) && s.RowMap.TryGetValue(old, out var po))
            ReceiveClearHighlight(po);
        s.Highlight = line;
        if (s.RowMap.TryGetValue(line, out var p))
            ReceiveApplyHighlight(p);
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            if (_receive != s || !ReferenceEquals(s.Highlight, line))
                return;
            s.Highlight = null;
            if (s.RowMap.TryGetValue(line, out var cur))
                ReceiveClearHighlight(cur);
        }, TimeSpan.FromMilliseconds(1500));
    }

    private static void ReceiveApplyHighlight(RowParts p)
    {
        p.HighlightBinding?.Dispose();
        p.HighlightBinding = p.Box.Bind(Border.BackgroundProperty, p.Box.GetResourceObservable("BrushWarningSoft"));
    }

    private static void ReceiveClearHighlight(RowParts p)
    {
        p.HighlightBinding?.Dispose();
        p.HighlightBinding = null;
        p.Box.ClearValue(Border.BackgroundProperty);
    }

    // ------------------------------------------------------------------ скан во время правки ячейки

    private static void ReceiveFieldScanReset(ReceiveState s)
    {
        s.FsBuf = "";
        s.FsRun = 0;
        s.FsBox = null;
        s.FsSnap = null;
    }

    /// <summary>2026-10-24 (Б2), как в Склад → Приёмка: штрихкод не должен печататься в «Количество»/цену. Скан в ячейке — только быстрая серия
    /// (символы чаще раза в 50 мс, ≥4 символов) и Enter сразу за ней; тогда ячейка возвращается к тексту до серии и штрихкод идёт в приём.
    /// Обычный ввод человека (пауза между клавишами больше 50 мс) не затрагивается.</summary>
    private void ReceiveFieldScanKey(ReceiveState s, KeyEventArgs e)
    {
        if (_receive != s)
            return;
        if (e.Source is not TextBox box || !Equals(box.Tag, "rcv-cell"))
        {
            ReceiveFieldScanReset(s);
            return;
        }
        var mods = e.KeyModifiers;
        if (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Alt) || mods.HasFlag(KeyModifiers.Meta))
            return;

        var now = Environment.TickCount64;
        // Пауза — по времени нажатия клавиши, а не обработки (пачка клавиш в очереди не выглядит «быстрой»), как в AvaloniaKeyboardWedgeBarcodeService.
        var raw = AvaloniaKeyboardWedgeBarcodeService.KeyPressTimestamp();
        var sinceLast = raw is { } rawTs && s.FsRaw is { } lastRawTs && rawTs >= lastRawTs
            ? (long)Math.Min(rawTs - lastRawTs, long.MaxValue)
            : now - s.FsTick;
        var sameBurst = s.FsBuf.Length > 0 && ReferenceEquals(box, s.FsBox);
        var fast = sameBurst && sinceLast is >= 0 and <= 50;
        s.FsTick = now;
        s.FsRaw = raw;

        if (e.Key == Key.Enter)
        {
            if (sameBurst && sinceLast is >= 0 and <= 150 && s.FsBuf.Length >= 4 && s.FsRun >= 2)
            {
                e.Handled = true;
                var code = s.FsBuf;
                box.Text = s.FsSnap;
                box.CaretIndex = Math.Min(s.FsCaret, box.Text?.Length ?? 0);
                ReceiveFieldScanReset(s);
                _ = ReceiveAddByCodeAsync(code);
                return;
            }
            ReceiveFieldScanReset(s);
            return;
        }

        var symbol = e.KeySymbol;
        if (symbol is not { Length: 1 } || !(char.IsLetterOrDigit(symbol[0]) || symbol[0] == '-'))
        {
            ReceiveFieldScanReset(s);
            return;
        }
        if (fast)
            s.FsRun++;
        else
        {
            s.FsBuf = "";
            s.FsRun = 0;
            s.FsBox = box;
            s.FsSnap = box.Text;
            s.FsCaret = box.CaretIndex;
        }
        s.FsBuf += symbol;
    }

    // ------------------------------------------------------------------ «Товары поставщика»: панель с поиском вместо добавления всех строк

    private string ReceiveGoodsButtonText(ReceiveState s)
    {
        if (s.GoodsLoading)
            return T("Товары поставщика…", "Жеткирүүчүнүн товарлары…", "Supplier's products…", "Tedarikçi ürünleri…", "Yetkazib beruvchi mahsulotlari…");
        var n = s.Picker?.Selected is not null && s.GoodsRows.Count > 0 ? $" ({s.GoodsRows.Count})" : "";
        return s.Narrow
            ? T("Товары пост." + n, "Жеткир. товары" + n, "Supplier items" + n, "Ted. ürünleri" + n, "Yetk. mahsulot" + n)
            : T("Товары поставщика" + n, "Жеткирүүчүнүн товарлары" + n, "Supplier's products" + n, "Tedarikçi ürünleri" + n, "Yetkazib beruvchi mahsulotlari" + n);
    }

    private void ReceiveGoodsButton(ReceiveState s)
    {
        s.SupplierGoods.Content = ReceiveGoodsButtonText(s);
        s.SupplierGoods.IsEnabled = !s.GoodsLoading;
    }

    private Control ReceiveBuildGoodsPanel(ReceiveState s)
    {
        s.GoodsSearch = UiKit.Search(this, T("Найти товар поставщика: название или штрихкод — Enter добавит первый",
            "Жеткирүүчүнүн товарын табуу: аталышы же штрихкод — Enter биринчисин кошот", "Find a supplier product: name or barcode — Enter adds the first",
            "Tedarikçi ürününü bul: ad veya barkod — Enter ilkini ekler", "Yetkazib beruvchi mahsulotini topish: nomi yoki shtrix-kod — Enter birinchisini qo'shadi"), 40);
        Avalonia.Automation.AutomationProperties.SetName(s.GoodsSearch, s.GoodsSearch.Watermark);
        UiKit.OnTypedDebounced(s.GoodsSearch, () => ReceiveApplyGoodsFilter(s)); // 2026-11-01: пауза 250 мс (товары поставщика)
        s.GoodsSearch.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            if (s.GoodsList.ItemsSource is IEnumerable<SupplierGoodsRow> found && found.FirstOrDefault() is { } first)
                _ = ReceiveAddGoodsAsync(s, first);
        };
        // Список виртуализирован (ListBox): 600 и больше товаров не строят 600 строк с полями ввода.
        s.GoodsList = new ListBox
        {
            Height = 150, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            ItemTemplate = new FuncDataTemplate<SupplierGoodsRow>((r, _) => ReceiveGoodsRowView(r), true),
        };
        s.GoodsList.SelectionChanged += (_, _) =>
        {
            if (s.GoodsPicking || s.GoodsList.SelectedItem is not SupplierGoodsRow row)
                return;
            s.GoodsPicking = true;
            try
            {
                s.GoodsList.SelectedItem = null; // нажатие = «добавить»; следующее нажатие по тому же товару сработает снова
            }
            finally
            {
                s.GoodsPicking = false;
            }
            _ = ReceiveAddGoodsAsync(s, row);
        };
        var close = new Button
        {
            Content = "✕", Width = 36, Height = 36, MinHeight = 36, Padding = new Thickness(0), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        Use(close, Button.ForegroundProperty, "BrushTextSoft");
        Avalonia.Automation.AutomationProperties.SetName(close, T("Закрыть список товаров поставщика", "Жеткирүүчүнүн товарларынын тизмесин жабуу", "Close the supplier's products list", "Tedarikçi ürün listesini kapat", "Yetkazib beruvchi mahsulotlari ro'yxatini yopish"));
        close.Click += (_, _) => ReceiveToggleGoods(s);
        Use(s.GoodsTitle, TextBlock.ForegroundProperty, "BrushTextSoft");
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(s.GoodsTitle);
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        s.GoodsPanel = new Border
        {
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(10, 6), Margin = new Thickness(0, 0, 0, 8), IsVisible = false,
            Child = new StackPanel { Spacing = 6, Children = { head, s.GoodsSearch, s.GoodsList } },
        };
        Use(s.GoodsPanel, Border.BackgroundProperty, "BrushPanel");
        Use(s.GoodsPanel, Border.BorderBrushProperty, "BrushBorder");
        return s.GoodsPanel;
    }

    private Control ReceiveGoodsRowView(SupplierGoodsRow r)
    {
        var name = new TextBlock { Text = r.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        Use(name, TextBlock.ForegroundProperty, "BrushText");
        var info = new TextBlock { Text = r.Info, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        Use(info, TextBlock.ForegroundProperty, "BrushTextSoft");
        var plus = new TextBlock { Text = "＋", FontSize = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };
        Use(plus, TextBlock.ForegroundProperty, "BrushText");
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 40 };
        g.Children.Add(new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { name, info } });
        Grid.SetColumn(plus, 1);
        g.Children.Add(plus);
        Avalonia.Automation.AutomationProperties.SetName(g, r.Name);
        return g;
    }

    private void ReceiveApplyGoodsFilter(ReceiveState s)
    {
        var query = (s.GoodsSearch.Text ?? "").Trim();
        IEnumerable<SupplierGoodsRow> rows = s.GoodsRows;
        if (query.Length > 0)
            rows = rows.Where(r => SearchMatch.Matches(query, r.SearchKey)); // 2026-10-29: единый поиск
        s.GoodsList.ItemsSource = rows.ToList();
    }

    /// <summary>Кнопка «Товары поставщика (N)»: открыть/закрыть панель.</summary>
    private void ReceiveToggleGoods(ReceiveState s)
    {
        if (s.Picker.Selected is null)
        {
            ReceiveMessage(T("Сначала выберите поставщика.", "Адегенде жеткирүүчүнү тандаңыз.", "Choose a supplier first.", "Önce tedarikçi seçin.", "Avval yetkazib beruvchini tanlang."), warning: true);
            return;
        }
        if (s.GoodsLoading)
            return;
        ReceiveMessage(null);
        s.GoodsOpen = !s.GoodsOpen;
        s.GoodsPanel.IsVisible = s.GoodsOpen;
        if (s.GoodsOpen)
            s.GoodsSearch.Focus();
        else
            s.Scan.Focus();
    }

    /// <summary>Товары выбранного поставщика (suppliers/{id}/products/ и товары из его приходов) — в панель поиска. Строки приёма не добавляются:
    /// 600 товаров — это 600 строк списка панели, а не 600 строк с полями ввода (так сидели «по полчаса, ища нужную»).</summary>
    private async Task ReceiveLoadGoodsAsync(ReceiveState s, SupplierInfo? supplier)
    {
        s.GoodsCts?.Cancel();
        s.GoodsRows = new List<SupplierGoodsRow>();
        s.SupplierPrices = new Dictionary<string, ReceivingDefaults.SupplierPrice>(StringComparer.OrdinalIgnoreCase);
        s.SupplierIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        s.GoodsList.ItemsSource = null;
        s.GoodsSearch.Text = "";
        s.GoodsOpen = false;
        s.GoodsPanel.IsVisible = false;
        if (supplier is null)
        {
            s.GoodsLoading = false;
            ReceiveGoodsButton(s);
            return;
        }
        var cts = s.GoodsCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        s.GoodsLoading = true;
        ReceiveGoodsButton(s);
        var productsTask = _svc.GetProductsAsync(supplier.Id, cts.Token);
        var receiptsTask = PurchaseReceivingService.Instance.LoadSupplierProductsAsync(new PurchaseReceivingService.Supplier(supplier.Id, supplier.Name), cts.Token);
        var products = new List<SupplierProduct>();
        var fromReceipts = new List<PurchaseReceivingService.SupplierProduct>();
        var failed = 0;
        string? failText = null;
        try
        {
            products = await productsTask.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            failed++;
            failText = ex.Message;
            if (!cts.IsCancellationRequested)
                PosLogger.Log($"Закупки: товары «{supplier.Name}» не загружены ({ex.Message}).", "WARNING");
        }
        try
        {
            fromReceipts = await receiptsTask.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            failed++;
            failText ??= ex.Message;
            if (!cts.IsCancellationRequested)
                PosLogger.Log($"Закупки: товары из приходов «{supplier.Name}» не загружены ({ex.Message}).", "WARNING");
        }
        if (_receive != s || !ReferenceEquals(s.GoodsCts, cts))
            return;

        var catalog = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in CatalogCacheService.Products)
            catalog.TryAdd(t.Id, t);
        var rows = new List<SupplierGoodsRow>(fromReceipts.Count + products.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in fromReceipts) // уже свежие приходы первыми
        {
            if (r.ProductId.Length == 0 || !seen.Add(r.ProductId))
                continue;
            catalog.TryGetValue(r.ProductId, out var tile);
            var name = tile?.Title is { Length: > 0 } tt ? tt : r.Name;
            if (name.Length == 0)
                continue;
            var info = (r.LastAt.Year >= 2000 ? r.LastAt.ToLocalTime().ToString("dd.MM.yyyy") + " · " : "") + Q(r.LastQuantity) + (r.LastPurchasePrice > 0 ? " × " + r.LastPurchasePrice.ToString("0.##", Ru) : "");
            rows.Add(new SupplierGoodsRow(r.ProductId, name, tile?.Unit ?? "", r.LastPurchasePrice, info, $"{name} {tile?.Barcode} {tile?.Article}"));
        }
        foreach (var p in products.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (p.Id.Length == 0 || !seen.Add(p.Id))
                continue;
            catalog.TryGetValue(p.Id, out var tile);
            var name = tile?.Title is { Length: > 0 } tt ? tt : p.Name;
            if (name.Length == 0)
                continue;
            var info = (tile is not null ? T("остаток ", "калдык ", "stock ", "stok ", "qoldiq ") + Q(tile.Quantity) : "")
                       + (p.PurchasePrice > 0 ? (tile is not null ? " · " : "") + T("закупка ", "сатып алуу ", "purchase ", "alış ", "xarid ") + p.PurchasePrice.ToString("0.##", Ru) : "");
            rows.Add(new SupplierGoodsRow(p.Id, name, tile?.Unit ?? "", p.PurchasePrice, info, $"{name} {tile?.Barcode ?? p.Code} {tile?.Article ?? p.Article}"));
        }
        s.GoodsRows = rows;
        foreach (var row in rows)
            s.SupplierIds.Add(row.ProductId);
        // 2026-11-06: закупки этого поставщика с сервера (в том числе сделанные на сайте) — цена закупки строк по умолчанию; уже добавленные строки пересчитываются.
        foreach (var r in fromReceipts)
            if (r.ProductId.Length > 0 && r.LastPurchasePrice > 0)
                s.SupplierPrices.TryAdd(r.ProductId, new ReceivingDefaults.SupplierPrice(r.LastPurchasePrice, r.LastAt));
        ReceiveReapplyPurchases(s);
        s.GoodsLoading = false;
        ReceiveGoodsButton(s);
        s.GoodsTitle.Text = rows.Count > 0
            ? T($"Товары «{supplier.Name}» ({rows.Count}) — последний приход: дата · количество × цена закупки. Найдите нужный и нажмите:",
                $"«{supplier.Name}» товарлары ({rows.Count}) — акыркы кирешеси: күнү · саны × сатып алуу баасы. Керектүүсүн тапып басыңыз:",
                $"Products of “{supplier.Name}” ({rows.Count}) — last receipt: date · quantity × purchase price. Find the one you need and press it:",
                $"«{supplier.Name}» ürünleri ({rows.Count}) — son giriş: tarih · miktar × alış fiyatı. İhtiyacınız olanı bulup basın:",
                $"«{supplier.Name}» mahsulotlari ({rows.Count}) — oxirgi kirim: sana · soni × xarid narxi. Keraklisini topib bosing:")
            : failed == 2
                ? T("Товары поставщика не загрузились: ", "Жеткирүүчүнүн товарлары жүктөлгөн жок: ", "The supplier's products didn't load: ", "Tedarikçi ürünleri yüklenmedi: ", "Yetkazib beruvchi mahsulotlari yuklanmadi: ") + failText
                : T("У этого поставщика ещё нет товаров — отсканируйте их или найдите по названию.", "Бул жеткирүүчүнүн товарлары азырынча жок — аларды сканерлеңиз же аты боюнча табыңыз.",
                    "This supplier has no products yet — scan them or find them by name.", "Bu tedarikçinin henüz ürünü yok — okutun veya adıyla bulun.",
                    "Bu yetkazib beruvchining mahsulotlari hali yo'q — skanerlang yoki nomi bo'yicha toping.");
        ReceiveApplyGoodsFilter(s);
    }

    /// <summary>Нажатие по товару в панели: строка сверху (повторное нажатие — +1), закупка — последняя у этого поставщика, если у товара своей ещё нет;
    /// курсор — в «Количество».</summary>
    private async Task ReceiveAddGoodsAsync(ReceiveState s, SupplierGoodsRow row)
    {
        try
        {
            var line = s.Lines.FirstOrDefault(x => string.Equals(x.ProductId, row.ProductId, StringComparison.OrdinalIgnoreCase));
            if (line is not null)
                line.Quantity += 1;
            else
            {
                line = await LineForProductAsync(row.ProductId, row.Name, row.Unit, 1, null).ConfigureAwait(true);
                if (_receive != s)
                    return;
                if (line is null)
                {
                    ReceiveMessage(T("Товар не удалось прочитать — попробуйте ещё раз.", "Товарды окуу мүмкүн болгон жок — дагы аракет кылыңыз.", "Couldn't read the product — try again.",
                        "Ürün okunamadı — tekrar deneyin.", "Mahsulotni o'qib bo'lmadi — qayta urinib ko'ring."), warning: true);
                    return;
                }
                if (s.Lines.FirstOrDefault(x => string.Equals(x.ProductId, line.ProductId, StringComparison.OrdinalIgnoreCase)) is { } raced)
                {
                    raced.Quantity += 1;
                    line = raced;
                }
            }
            s.GoodsSearch.Text = "";
            ReceiveShowTop(s, line, true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: товар поставщика {row.ProductId} не добавлен ({ex.Message}).", "WARNING");
        }
    }

    // ------------------------------------------------------------------ подорожание: «Обновить цены продажи» и подтверждение перед «Оприходовать»

    private async Task ReceiveRaiseAsync(ReceiveState s)
    {
        var dialog = new ReceivingRaiseDialog(s.Lines.Where(l => l.Quantity > 0).ToList());
        await dialog.ShowDialog(this).ConfigureAwait(true);
        if (dialog.AppliedCount > 0)
            PosLogger.Log($"Закупки: «Обновить цены продажи» — {dialog.AppliedCount} {Tr.Plural(dialog.AppliedCount, "строка", "строки", "строк")}.", "STOCK");
        if (_receive == s)
            ReceiveRefresh();
    }

    /// <summary>2026-11-05 (функция 11): «Доп. расходы…» — окно разнесения доставки/грузчиков на закупку строк.</summary>
    private async Task ReceiveLandedAsync(ReceiveState s)
    {
        var active = s.Lines.Where(l => l.Quantity > 0).ToList();
        if (active.Count == 0)
        {
            ReceiveMessage(T("Сначала добавьте товары в приход.", "Адегенде кирүүгө товарларды кошуңуз.", "Add the goods to the delivery first.", "Önce girişe ürün ekleyin.", "Avval kirimga mahsulot qo'shing."), warning: true);
            return;
        }
        var dialog = new ReceivingLandedCostDialog(active, s.LandedTotal, s.LandedMode);
        await dialog.ShowDialog(this).ConfigureAwait(true);
        if (dialog.Applied)
        {
            s.LandedTotal = dialog.AppliedTotal;
            s.LandedMode = dialog.AppliedMethod;
            PosLogger.Log($"Закупки: доп. расходы прихода {dialog.AppliedTotal:0.00} сом, способ {dialog.AppliedMethod}, строк {active.Count}.", "STOCK");
        }
        if (_receive == s)
            ReceiveRefresh();
    }

    /// <summary>2026-11-05 (функция 11): перед «Оприходовать» — «закупка 100,00 → 103,45» по строкам; долг поставщику остаётся по накладной. false — кассир передумал.</summary>
    private Task<bool> ReceiveConfirmLandedAsync(IReadOnlyList<LandedPreviewRow> rows, double total)
    {
        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        var shown = rows.Where(r => r.ExtraPerUnit > 0).Take(12).Select(r =>
            $"• {(string.IsNullOrWhiteSpace(r.Line.ProductName) ? r.Line.Barcode : r.Line.ProductName)}: {r.OldCost.ToString("0.00##", ru)} → {r.NewCost.ToString("0.00##", ru)}").ToList();
        var all = rows.Count(r => r.ExtraPerUnit > 0);
        if (all > shown.Count)
            shown.Add(T($"…и ещё {all - shown.Count}", $"…жана дагы {all - shown.Count}", $"…and {all - shown.Count} more", $"…ve {all - shown.Count} tane daha", $"…va yana {all - shown.Count}"));
        var message = T($"Доп. расходы {total.ToString("0.00", ru)} сом войдут в себестоимость товаров (закупка → с расходами):",
                $"Кошумча чыгым {total.ToString("0.00", ru)} сом товардын өздүк баасына кирет (сатып алуу → чыгым менен):",
                $"Extra costs {total.ToString("0.00", ru)} som go into the product cost (purchase → with costs):",
                $"Ek masraf {total.ToString("0.00", ru)} som ürün maliyetine eklenir (alış → masrafla):",
                $"Qo'shimcha xarajat {total.ToString("0.00", ru)} so'm mahsulot tannarxiga qo'shiladi (xarid → xarajat bilan):")
            + "\n\n" + string.Join("\n", shown) + "\n\n"
            + T("Долг поставщику не меняется: он по накладной.", "Жеткирүүчүгө карыз өзгөрбөйт: ал накладная боюнча.", "The supplier debt does not change: it stays as per the invoice.",
                "Tedarikçi borcu değişmez: faturadaki gibi kalır.", "Yetkazib beruvchiga qarz o'zgarmaydi: u nakladnoy bo'yicha.");
        return PosConfirmDialog.ShowModalAsync(this,
            T("Доп. расходы", "Кошумча чыгым", "Extra costs", "Ek masraf", "Qo'shimcha xarajat"), message,
            T("Продолжить", "Улантуу", "Continue", "Devam", "Davom etish"),
            T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), PosConfirmAccent.Primary);
    }

    /// <summary>Перед «Оприходовать»: «Цена продажи изменится у K товаров» со списком (прежняя → новая). false — кассир передумал.</summary>
    private Task<bool> ReceiveConfirmPriceChangesAsync(IReadOnlyList<ReceivingLineVm> changed)
    {
        var shown = changed.Take(12).Select(l =>
            $"• {(string.IsNullOrWhiteSpace(l.ProductName) ? l.Barcode : l.ProductName)}: {l.OriginalSalePrice:0.##} → {l.SalePrice:0.##}").ToList();
        if (changed.Count > shown.Count)
            shown.Add(T($"…и ещё {changed.Count - shown.Count}", $"…жана дагы {changed.Count - shown.Count}", $"…and {changed.Count - shown.Count} more",
                $"…ve {changed.Count - shown.Count} tane daha", $"…va yana {changed.Count - shown.Count}"));
        // 2026-10-27 (приёмка 26.10, Д-8): склонение «у 1 товара / у 2 товаров», «for 1 product».
        var message = T($"Цена продажи изменится у {changed.Count} {Tr.Plural(changed.Count, "товара", "товаров", "товаров")}:", $"{changed.Count} товардын сатуу баасы өзгөрөт:",
                $"The sale price will change for {changed.Count} {Tr.PluralEn(changed.Count, "product", "products")}:", $"{changed.Count} ürünün satış fiyatı değişecek:",
                $"{changed.Count} ta mahsulotning sotuv narxi o'zgaradi:")
            + "\n\n" + string.Join("\n", shown);
        return PosConfirmDialog.ShowModalAsync(this,
            T("Оприходовать", "Кирешеге алуу", "Receive", "Stoğa al", "Kirim qilish"), message,
            T("Оприходовать", "Кирешеге алуу", "Receive", "Stoğa al", "Kirim qilish"),
            T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), PosConfirmAccent.Primary);
    }

    private void ReceiveMessage(string? text, bool warning = false, bool notice = false)
    {
        if (_receive is not { } s)
            return;
        s.Message.IsVisible = !string.IsNullOrEmpty(text);
        s.MessageBox.IsVisible = s.Message.IsVisible;
        s.Message.Text = text ?? "";
        Use(s.Message, TextBlock.ForegroundProperty, notice ? "BrushText" : warning ? "BrushDanger" : "BrushTextSoft");
        // notice — нейтральная плашка «в фоне» (не красная): фон и поля; без неё сообщение выглядит как раньше.
        s.MessageBox.Padding = notice ? new Thickness(12, 8) : new Thickness(0);
        if (notice)
            Use(s.MessageBox, Border.BackgroundProperty, "BrushWarningSoft");
        else
            s.MessageBox.ClearValue(Border.BackgroundProperty);
    }

}
