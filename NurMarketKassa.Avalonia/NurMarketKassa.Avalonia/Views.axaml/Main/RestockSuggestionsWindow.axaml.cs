using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>«Пополнение и сроки» (AI-фичи 2026-09-03, п.1 и п.5 из мозгового штурма) — две вкладки:
/// прогноз пополнения склада по локальной истории продаж (<see cref="SoldLineItemsStore"/>) и
/// оценка срока годности по категории товара (<see cref="ExpiryEstimator"/>, без реального
/// интернет-поиска — нужен ИИ/поисковый API-ключ, которого пока нет).</summary>
public partial class RestockSuggestionsWindow : Window, IOwnerSection
{
    // По умолчанию — месяц: сглаживает случайные всплески лучше недели для оценки "хватит на N
    // дней". Переключатель Неделя/Месяц — п.11 AI-фич (см. AI features 2026-09-03).
    private int _salesLookbackDays = 30;

    // 2026-10-21: на сколько дней продаж заказывать — «сколько заказать» = продажи в день × дни − остаток (пакет 1, п. 112).
    private int _coverDays = 14;
    private List<ReorderRow> _reorderAll = new();
    // Заказ переживает «Обновить» и смену периода — по товару, а не по строке таблицы.
    private readonly HashSet<string> _ordered = new(StringComparer.OrdinalIgnoreCase);
    private int _supplierGen;

    public RestockSuggestionsWindow()
    {
        InitializeComponent();
        // 2026-10-20, аудит программы владельца: кнопка считает 30 дней, а «Месяц» в других окнах — с 1-го числа.
        LookbackMonthButton.Content = Tr.T("30 дней", "30 күн", "30 days", "30 gün", "30 kun");
        ApplyPackage1Texts();
    }

    /// <summary>2026-10-21 (пакет 1, п. 112, 113): подписи, которых нет в общих строках, — на 5 языках в коде.</summary>
    private void ApplyPackage1Texts()
    {
        CoverLabel.Text = Tr.T("Заказать на:", "Буйрутма:", "Order for:", "Sipariş süresi:", "Buyurtma muddati:");
        Cover7Button.Content = Tr.T("неделю", "1 апта", "1 week", "1 hafta", "1 hafta");
        Cover14Button.Content = Tr.T("2 недели", "2 апта", "2 weeks", "2 hafta", "2 hafta");
        Cover30Button.Content = Tr.T("месяц", "1 ай", "1 month", "1 ay", "1 oy");
        ReorderSearchBox.Watermark = Tr.T("Найти товар или поставщика", "Товар же жеткирүүчүнү табуу", "Find a product or supplier",
            "Ürün veya tedarikçi ara", "Mahsulot yoki yetkazib beruvchini topish");
        ReorderGrid.Columns[2].Header = Tr.T("Продаётся в день", "Күнүнө сатылат", "Sold per day", "Günlük satış", "Kuniga sotiladi");
        ReorderGrid.Columns[4].Header = Tr.T("Сколько заказать", "Канча буйрутма берүү", "How much to order", "Ne kadar sipariş", "Qancha buyurtma qilish");
        ClearOrderButton.Content = Tr.T("Очистить заказ", "Буйрутманы тазалоо", "Clear order", "Siparişi temizle", "Buyurtmani tozalash");
        CopyOrderButton.Content = Tr.T("Копировать заказ", "Буйрутманы көчүрүү", "Copy order", "Siparişi kopyala", "Buyurtmani nusxalash");
        ExpiryGrid.Columns[4].Header = Tr.T("Осталось примерно", "Болжол менен калды", "Approx. left", "Yaklaşık kalan", "Taxminan qoldi");
        // Узкая таблица (1024, программа владельца): «Поступил» прячется — дата «≈ до» и «осталось» важнее и не обрезаются.
        ExpiryGrid.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0)
                ExpiryGrid.Columns[2].IsVisible = e.NewSize.Width >= 900;
        };
    }

    private void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        LoadReorderTab();
        LoadExpiryTab();
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        LoadReorderTab();
        LoadExpiryTab();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Раздел программы владельца (см. <see cref="IOwnerSection"/>): название уже над
    /// разделом, «Закрыть» не нужна. «Подтянуть историю с сервера» и «Обновить» остаются строкой справа.</summary>
    public void AsOwnerSection()
    {
        TitleText.IsVisible = false;
        CloseButton.IsVisible = false;
        RefreshButton.Margin = new Avalonia.Thickness(0);
        RootGrid.Margin = OwnerSectionLayout.Margin;
    }

    private void Lookback_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tagStr } || !int.TryParse(tagStr, out var days))
            return;

        _salesLookbackDays = days;
        LookbackWeekButton.Classes.Set("Active", days == 7);
        LookbackMonthButton.Classes.Set("Active", days == 30);
        LoadReorderTab();
    }

    // 2026-10-21 (пакет 1, п. 112): запас «неделя / 2 недели / месяц» — пересчёт «сколько заказать» без новой загрузки.
    private void Cover_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tagStr } || !int.TryParse(tagStr, out var days))
            return;

        _coverDays = days;
        Cover7Button.Classes.Set("Active", days == 7);
        Cover14Button.Classes.Set("Active", days == 14);
        Cover30Button.Classes.Set("Active", days == 30);
        foreach (var row in _reorderAll)
            row.SetCoverDays(days);
        ReorderSubtitle.Text = ReorderHint();
        UpdateOrderBar();
    }

    /// <summary>Готовит вкладку «Что пора заказать».
    ///
    /// Чтение истории продаж вынесено в фоновый поток: раньше оно шло прямо в обработчике
    /// открытия окна, и на магазине с длинной историей касса замирала на секунды. Сама
    /// раскладка по гриду остаётся на UI-потоке — это уже дёшево.</summary>
    // 2026-10-20: проверка фильтров — быстрое «Неделя» → «Месяц»: недельный расчёт мог прийти последним и лечь под
    // подсвеченный «Месяц». Устаревший расчёт теперь отбрасывается.
    private int _reorderGen;

    private async void LoadReorderTab()
    {
        var gen = ++_reorderGen;
        // 2026-10-20, проверка фильтров: «Неделя» = 7 дней включая сегодня (как PeriodRange во всех окнах), «Месяц» — 30 дней
        // включая сегодня: здесь это глубина истории для средней продажи в день, а не отчётный месяц (с 1-го числа в начале
        // месяца средняя считалась бы по 1–2 дням). Даты — подписью рядом.
        var sinceLocal = DateTime.Today.AddDays(-(_salesLookbackDays - 1));
        var since = sinceLocal.ToUniversalTime();
        LookbackPeriodText.Text = NurMarketKassa.AvaloniaHost.Services.PeriodRange.Label(sinceLocal, DateTime.Today);
        ReorderSubtitle.Text = Tr.T("Считаю по истории продаж…", "Сатуу тарыхы боюнча эсептелүүдө…",
            "Calculating from the sales history…", "Satış geçmişinden hesaplanıyor…",
            "Sotuvlar tarixi bo'yicha hisoblanmoqda…");

        // 2026-10-21 (пакет 1): локальная база не открылась — пустой прогноз с подсказкой, а не «Считаю…» навсегда.
        List<(string ProductId, string ProductName, double Quantity, DateTime SoldAt)> soldLines;
        try
        {
            soldLines = await Task.Run(() => SoldLineItemsStore.LoadSince(since)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Пополнение: история продаж не прочитана: {ex.Message}", "WARNING");
            soldLines = new();
        }
        if (gen != _reorderGen)
            return;

        var earliest = soldLines.Count > 0 ? soldLines.Min(l => l.SoldAt) : (DateTime?)null;
        var spanDays = earliest.HasValue
            ? Math.Max(1.0, (DateTime.UtcNow - earliest.Value).TotalDays)
            : 1.0;

        var byProduct = soldLines
            .GroupBy(l => l.ProductId)
            .Select(g => new { ProductId = g.Key, Name = g.First().ProductName, TotalQty = g.Sum(x => x.Quantity) })
            .ToDictionary(x => x.ProductId, x => x);

        var rows = new List<ReorderRow>();
        foreach (var product in CatalogCacheService.Products)
        {
            if (!byProduct.TryGetValue(product.Id, out var sold))
                continue;

            var dailyRate = sold.TotalQty / spanDays;
            if (dailyRate <= 0)
                continue;

            var daysLeft = Math.Max(0, product.Quantity) / dailyRate;
            rows.Add(new ReorderRow
            {
                ProductId = product.Id,
                ProductName = product.Title,
                // 2026-10-21 (пакет 1, п. 112): числа с единицей — «12 шт», «1,5 кг в день» (было «0.03» без единиц).
                Unit = UnitOf(product.Unit, product.IsWeighted),
                IsWeighted = product.IsWeighted,
                Stock = product.Quantity,
                DailyRate = dailyRate,
                DaysLeft = daysLeft,
            });
        }

        ApplyReorderRows(rows.OrderBy(r => r.DaysLeft).ToList());
    }

    /// <summary>2026-10-21 (пакет 1, п. 112): строки прогноза на экран — запас, отметки «в заказе», поиск, поставщики.</summary>
    private void ApplyReorderRows(List<ReorderRow> rows)
    {
        _reorderAll = rows;
        foreach (var row in rows)
        {
            row.SetCoverDays(_coverDays);
            row.InOrder = row.ProductId.Length > 0 && _ordered.Contains(row.ProductId);
        }
        ReorderSubtitle.Text = ReorderHint();
        ApplyReorderFilter();
        UpdateOrderBar();
        _ = LoadSuppliersAsync(rows);
    }

    private string ReorderHint() => Tr.T(
        $"Сначала то, что закончится раньше. «Сколько заказать» — чтобы хватило на {_coverDays} дн. продаж с учётом остатка.",
        $"Алгач эртерээк бүтө турганы. «Канча буйрутма берүү» — калдыкты эсепке алып, {_coverDays} күндүк сатууга жетиштүү болушу үчүн.",
        $"What runs out first comes first. «How much to order» covers {_coverDays} days of sales, counting current stock.",
        $"Önce en erken bitecek olanlar. «Ne kadar sipariş» — mevcut stok dahil {_coverDays} günlük satışa yetecek miktar.",
        $"Avval tezroq tugaydiganlari. «Qancha buyurtma qilish» — qoldiqni hisobga olib, {_coverDays} kunlik sotuvga yetadigan miqdor.");

    private void ReorderSearch_TextChanged(object? sender, TextChangedEventArgs e) => ApplyReorderFilter();

    private void ApplyReorderFilter()
    {
        var q = (ReorderSearchBox.Text ?? "").Trim();
        var shown = q.Length == 0
            ? _reorderAll
            : _reorderAll.Where(r => r.ProductName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                     || r.SupplierName.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        ReorderGrid.ItemsSource = shown;
        ReorderEmptyText.IsVisible = shown.Count == 0;
        if (_reorderAll.Count == 0)
            ReorderEmptyText.Text = this.TryFindResource("restock.reorderEmpty", out var text) ? text as string : "";
        else
            ReorderEmptyText.Text = Tr.T($"По «{q}» ничего не найдено.", $"«{q}» боюнча эч нерсе табылган жок.", $"Nothing found for «{q}».",
                $"«{q}» için sonuç yok.", $"«{q}» bo'yicha hech narsa topilmadi.");
    }

    // ------------------------------------------------------------------ заказ (пакет 1, п. 112)

    private void OrderRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReorderRow row } || !row.HasRecommendation)
            return;
        row.InOrder = !row.InOrder;
        if (row.ProductId.Length > 0)
        {
            if (row.InOrder)
                _ordered.Add(row.ProductId);
            else
                _ordered.Remove(row.ProductId);
        }
        UpdateOrderBar();
    }

    private List<ReorderRow> OrderedRows() => _reorderAll.Where(r => r.InOrder && r.HasRecommendation).ToList();

    private void UpdateOrderBar()
    {
        var rows = OrderedRows();
        OrderBar.IsVisible = rows.Count > 0;
        if (rows.Count == 0)
            return;
        var suppliers = rows.Select(r => r.SupplierName).Where(s => s.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).Count();
        OrderSummaryText.Text = Tr.T($"В заказе позиций: {rows.Count}", $"Буйрутмада: {rows.Count} позиция", $"Items in the order: {rows.Count}",
                                    $"Siparişteki kalem: {rows.Count}", $"Buyurtmada: {rows.Count} ta pozitsiya")
            + (suppliers > 0
                ? Tr.T($" · поставщиков: {suppliers}", $" · жеткирүүчү: {suppliers}", $" · suppliers: {suppliers}", $" · tedarikçi: {suppliers}", $" · yetkazib beruvchi: {suppliers}")
                : "");
    }

    private void ClearOrder_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _reorderAll)
            row.InOrder = false;
        _ordered.Clear();
        UpdateOrderBar();
    }

    /// <summary>Текст заказа по поставщикам — вставить в WhatsApp/Telegram или распечатать.</summary>
    private string BuildOrderText()
    {
        var sb = new StringBuilder();
        var shop = CompanyInfoService.LastCompany?.Name;
        sb.Append(Tr.T("Заказ", "Буйрутма", "Order", "Sipariş", "Buyurtma")).Append(' ').Append(DateTime.Today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(shop))
            sb.Append(" — ").Append(shop.Trim());
        sb.AppendLine();
        var noSupplier = Tr.T("Без поставщика", "Жеткирүүчүсү жок", "No supplier", "Tedarikçisiz", "Yetkazib beruvchisiz");
        foreach (var group in OrderedRows().GroupBy(r => r.SupplierName.Length > 0 ? r.SupplierName : noSupplier)
                     .OrderBy(g => g.Key == noSupplier).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            sb.AppendLine();
            sb.Append(group.Key).AppendLine(":");
            foreach (var row in group)
                sb.Append("• ").Append(row.ProductName).Append(" — ").AppendLine(row.RecommendedText);
        }
        return sb.ToString().TrimEnd();
    }

    private async void CopyOrder_Click(object? sender, RoutedEventArgs e)
    {
        if (OrderedRows().Count == 0)
            return;
        var text = BuildOrderText();
        try
        {
            var clipboard = TopLevel.GetTopLevel(CopyOrderButton)?.Clipboard ?? Clipboard;
            if (clipboard is null)
                return;
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
            PosLogger.Log($"Пополнение: заказ скопирован ({OrderedRows().Count} поз.).", "STOCK");
            var label = CopyOrderButton.Content;
            CopyOrderButton.Content = Tr.T("Скопировано ✓", "Көчүрүлдү ✓", "Copied ✓", "Kopyalandı ✓", "Nusxalandi ✓");
            Avalonia.Threading.DispatcherTimer.RunOnce(() => CopyOrderButton.Content = label, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Пополнение: заказ не скопирован: {ex.Message}", "WARNING");
        }
    }

    /// <summary>2026-10-21 (пакет 1, п. 112): поставщик товара — последний приход: сначала записи приёмки на этом компьютере,
    /// затем приходы с сервера (только чтение, 2 страницы). Нет связи — строки остаются без поставщика.</summary>
    private async Task LoadSuppliersAsync(List<ReorderRow> rows)
    {
        var gen = ++_supplierGen;
        var ids = rows.Select(r => r.ProductId).Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(300).ToList();
        if (ids.Count == 0)
            return;

        var map = await Task.Run(async () =>
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var id in ids)
                {
                    var last = PurchaseReceivingService.Instance.LoadLocalHistory(id).FirstOrDefault(h => !string.IsNullOrWhiteSpace(h.SupplierName));
                    if (last is not null)
                        found[id] = last.SupplierName!.Trim();
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Пополнение: поставщики из истории приёмки не прочитаны: {ex.Message}", "WARNING");
            }

            if (found.Count < ids.Count)
            {
                var wanted = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
                try
                {
                    for (var page = 1; page <= 2; page++)
                    {
                        var data = await App.CatalogApi.ListSupplierReceiptsAsync(page, 100).ConfigureAwait(false);
                        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("results", out var receipts) || receipts.ValueKind != JsonValueKind.Array)
                            break;
                        // Приходы — свежие первыми: первый встреченный поставщик товара — последний.
                        foreach (var receipt in receipts.EnumerateArray())
                        {
                            if (!receipt.TryGetProperty("supplier_name", out var sn) || sn.ValueKind != JsonValueKind.String
                                || string.IsNullOrWhiteSpace(sn.GetString()) || !receipt.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                                continue;
                            foreach (var item in items.EnumerateArray())
                            {
                                var pid = Str(item, "product_id") ?? Str(item, "product");
                                if (pid is not null && wanted.Contains(pid) && !found.ContainsKey(pid))
                                    found[pid] = sn.GetString()!.Trim();
                            }
                        }
                        if (!data.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String)
                            break;
                    }
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Пополнение: поставщики с сервера не загружены: {ex.Message}", "WARNING");
                }
            }
            return found;
        }).ConfigureAwait(true);

        if (gen != _supplierGen || map.Count == 0)
            return;
        foreach (var row in rows)
            if (map.TryGetValue(row.ProductId, out var supplier))
                row.SupplierName = supplier;
        if ((ReorderSearchBox.Text ?? "").Trim().Length > 0)
            ApplyReorderFilter();
        UpdateOrderBar();
    }

    private static string? Str(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    // ------------------------------------------------------------------ сроки годности (пакет 1, п. 113)

    private void LoadExpiryTab()
    {
        // 2026-10-21 (пакет 1): база не открылась — пустой список с подсказкой вместо ошибки окна.
        Dictionary<string, DateTime> intakeDates;
        try
        {
            intakeDates = LocalProductRepository.Instance.LoadIntakeDates();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сроки годности: даты поступления не прочитаны: {ex.Message}", "WARNING");
            intakeDates = new();
        }

        var rows = new List<ExpiryRow>();
        foreach (var product in CatalogCacheService.Products)
        {
            if (!intakeDates.TryGetValue(product.Id, out var intake))
                continue;

            var expiry = ExpiryEstimator.EstimateExpiryDate(intake, product.Category);
            if (expiry is null)
                continue;

            var (shelfDays, label, _) = ExpiryEstimator.EstimateShelfLife(product.Category);
            rows.Add(new ExpiryRow
            {
                ProductId = product.Id,
                ProductName = product.Title,
                CategoryLabel = label,
                ShelfLifeDays = shelfDays,
                IntakeDateText = intake.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                ExpiryDateText = expiry.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                DaysLeft = (expiry.Value.Date - DateTime.Today).TotalDays,
            });
        }

        ApplyExpiryRows(rows.OrderBy(r => r.DaysLeft).Take(60).ToList());
    }

    private void ApplyExpiryRows(List<ExpiryRow> rows)
    {
        ExpiryGrid.ItemsSource = rows;
        ExpiryEmptyText.IsVisible = rows.Count == 0;
    }

    /// <summary>2026-10-21 (пакет 1, п. 113): «Списать» — склад на вкладке «Списание» с этим товаром и причиной «Просрочка».</summary>
    private void WriteOffRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ExpiryRow row } && row.ProductId.Length > 0)
            WarehouseWindow.RequestWriteOff(this, row.ProductId);
    }

    private async void Backfill_Click(object? sender, RoutedEventArgs e)
    {
        BackfillButton.IsEnabled = false;
        try
        {
            var count = await SalesHistoryBackfill.RunAsync();
            PosDialogs.Info(this,
                count > 0
                    ? Tr.T($"Готово: добавлено записей — {count}.", $"Даяр: {count} жазуу кошулду.",
                        $"Done: {count} entries added.", $"Tamamlandı: {count} kayıt eklendi.", $"Tayyor: {count} ta yozuv qo'shildi.")
                    : Tr.T("Новых записей не найдено (либо истории продаж на сервере нет, либо она уже подтянута).",
                           "Жаңы жазуулар табылган жок (сервердеги сатуу тарыхы жок же ал мурунтан эле тартылган).", "No new entries found (either there is no sales history on the server, or it has already been loaded).", "Yeni kayıt bulunamadı (sunucuda satış geçmişi yok ya da zaten alınmış).", "Yangi yozuvlar topilmadi (serverda sotuvlar tarixi yo'q yoki u allaqachon yuklangan)."),
                Tr.T("Пополнение склада", "Кампаны толуктоо", "Warehouse restock", "Stok yenileme", "Omborni to'ldirish"));
            LoadReorderTab();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Restock backfill failed: {ex}", "WARNING");
            PosDialogs.Error(this, Tr.T($"Не удалось подтянуть историю: {ex.Message}", $"Тарыхты тартуу мүмкүн болгон жок: {ex.Message}",
                $"Could not load the history: {ex.Message}", $"Geçmiş yüklenemedi: {ex.Message}", $"Tarixni yuklab bo'lmadi: {ex.Message}"),
                Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"));
        }
        finally
        {
            BackfillButton.IsEnabled = true;
        }
    }

    // 2026-10-20, аудит программы владельца: числа были с точкой («0.03», «16.17», «8.75»), в остальных окнах — запятая.
    // Числа на экране — с запятой (ru-RU), как везде.
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static string FormatQuantity(double qty) =>
        qty == Math.Floor(qty) ? qty.ToString("0", Ru) : qty.ToString("0.###", Ru);

    /// <summary>2026-10-21 (пакет 1, п. 112): единица у чисел; «шт.» и «шт» — одна и та же («шт»), весовой без единицы — «кг».</summary>
    private static string UnitOf(string? unit, bool isWeighted)
    {
        var u = (unit ?? "").Trim().TrimEnd('.');
        if (u.Length == 0)
            u = isWeighted ? "кг" : "шт";
        return u;
    }

    private sealed class ReorderRow : INotifyPropertyChanged
    {
        public string ProductId { get; set; } = "";
        public string ProductName { get; set; } = "";
        public string Unit { get; set; } = "шт";
        public bool IsWeighted { get; set; }
        public double Stock { get; set; }
        public double DailyRate { get; set; }
        public double DaysLeft { get; set; }
        private int _coverDays = 14;
        private bool _inOrder;
        private string _supplierName = "";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string StockText => Stock <= 0
            ? Tr.T("нет", "жок", "none", "yok", "yo'q")
            : $"{FormatQuantity(Stock)} {Unit}";
        public string DailyRateText => $"{DailyRate.ToString(DailyRate >= 10 ? "0.#" : "0.##", Ru)} {Unit}";
        private string Days => DaysLeft.ToString("0.#", Ru);
        public string DaysLeftText => Stock <= 0
            ? Tr.T("закончился", "бүттү", "out of stock", "tükendi", "tugadi")
            : Tr.T($"{Days} дн.", $"{Days} күн", $"{Days} days", $"{Days} gün", $"{Days} kun");
        public bool IsUrgent => DaysLeft <= 2;
        public bool IsSoon => DaysLeft > 2 && DaysLeft <= 7;
        public bool IsOk => DaysLeft > 7;

        public string SupplierName
        {
            get => _supplierName;
            set
            {
                _supplierName = value ?? "";
                Raise(nameof(SupplierName));
                Raise(nameof(SupplierText));
                Raise(nameof(HasSupplier));
            }
        }
        public bool HasSupplier => _supplierName.Length > 0;
        public string SupplierText => Tr.T("Поставщик: ", "Жеткирүүчү: ", "Supplier: ", "Tedarikçi: ", "Yetkazib beruvchi: ") + _supplierName;

        /// <summary>Сколько заказать, чтобы хватило на выбранный запас: продажи в день × дни − остаток (минус на складе — как 0).
        /// Штучное — целыми вверх, весовое — до 0,1 вверх.</summary>
        public double RecommendedQty
        {
            get
            {
                var need = DailyRate * _coverDays - Math.Max(0, Stock);
                if (need <= 0.0001)
                    return 0;
                return IsWeighted ? Math.Ceiling(Math.Round(need * 10, 6)) / 10 : Math.Ceiling(Math.Round(need, 6));
            }
        }
        public bool HasRecommendation => RecommendedQty > 0;
        public bool NoRecommendation => !HasRecommendation;
        public string RecommendedText => $"{FormatQuantity(RecommendedQty)} {Unit}";
        public string OrderButtonText => _inOrder
            ? Tr.T($"✓ В заказе {RecommendedText}", $"✓ Буйрутмада {RecommendedText}", $"✓ Ordered {RecommendedText}", $"✓ Siparişte {RecommendedText}", $"✓ Buyurtmada {RecommendedText}")
            : Tr.T($"Заказать {RecommendedText}", $"Буйрутма: {RecommendedText}", $"Order {RecommendedText}", $"Sipariş: {RecommendedText}", $"Buyurtma: {RecommendedText}");
        public string EnoughText => Tr.T("хватает", "жетиштүү", "enough", "yeterli", "yetarli");

        public bool InOrder
        {
            get => _inOrder;
            set
            {
                if (_inOrder == value)
                    return;
                _inOrder = value;
                Raise(nameof(InOrder));
                Raise(nameof(OrderButtonText));
            }
        }

        internal void SetCoverDays(int days)
        {
            _coverDays = days;
            Raise(nameof(RecommendedQty));
            Raise(nameof(HasRecommendation));
            Raise(nameof(NoRecommendation));
            Raise(nameof(RecommendedText));
            Raise(nameof(OrderButtonText));
        }
    }

    private sealed class ExpiryRow
    {
        public string ProductId { get; set; } = "";
        public string ProductName { get; set; } = "";
        public string CategoryLabel { get; set; } = "";
        public int ShelfLifeDays { get; set; }
        public string IntakeDateText { get; set; } = "";
        public string ExpiryDateText { get; set; } = "";
        public double DaysLeft { get; set; }
        // 2026-10-21 (пакет 1, п. 113): дата — догадка по категории, а не с упаковки: «≈ 25.10.2026» и «обычно 7 дн.» под ней.
        public string ApproxDateText => "≈ " + ExpiryDateText;
        public string ShelfLifeText => ShelfLifeDays > 0
            ? Tr.T($"обычно {ShelfLifeDays} дн.", $"адатта {ShelfLifeDays} күн", $"usually {ShelfLifeDays} days", $"genelde {ShelfLifeDays} gün", $"odatda {ShelfLifeDays} kun")
            : "";
        public string DaysLeftText => DaysLeft < 0
            ? Tr.T("Проверьте срок", "Текшериңиз", "Check date", "Kontrol edin", "Tekshiring")
            : Tr.T($"≈ {DaysLeft:0} дн.", $"≈ {DaysLeft:0} күн", $"≈ {DaysLeft:0} days", $"≈ {DaysLeft:0} gün", $"≈ {DaysLeft:0} kun");
        public bool IsUrgent => DaysLeft <= 2;
        public bool IsSoon => DaysLeft > 2 && DaysLeft <= 7;
        public bool IsOk => DaysLeft > 7;
        // «Списать» — только у того, что скоро истечёт или, возможно, уже истекло (у нормы кнопка лишняя).
        public bool CanWriteOff => DaysLeft <= 7 && ProductId.Length > 0;
        public string WriteOffText => Tr.T("Списать", "Эсептен чыгаруу", "Write off", "Düş", "Hisobdan chiqarish");
    }
}
