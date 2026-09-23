using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
public partial class RestockSuggestionsWindow : Window
{
    // По умолчанию — месяц: сглаживает случайные всплески лучше недели для оценки "хватит на N
    // дней". Переключатель Неделя/Месяц — п.11 AI-фич (см. AI features 2026-09-03).
    private int _salesLookbackDays = 30;

    public RestockSuggestionsWindow()
    {
        InitializeComponent();
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

    private void Lookback_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tagStr } || !int.TryParse(tagStr, out var days))
            return;

        _salesLookbackDays = days;
        LookbackWeekButton.Classes.Set("Active", days == 7);
        LookbackMonthButton.Classes.Set("Active", days == 30);
        LoadReorderTab();
    }

    /// <summary>Готовит вкладку «Что пора заказать».
    ///
    /// Чтение истории продаж вынесено в фоновый поток: раньше оно шло прямо в обработчике
    /// открытия окна, и на магазине с длинной историей касса замирала на секунды. Сама
    /// раскладка по гриду остаётся на UI-потоке — это уже дёшево.</summary>
    private async void LoadReorderTab()
    {
        var since = DateTime.UtcNow.AddDays(-_salesLookbackDays);
        ReorderSubtitle.Text = Tr.T("Считаю по истории продаж…", "Сатуу тарыхы боюнча эсептелүүдө…",
            "Calculating from the sales history…", "Satış geçmişinden hesaplanıyor…",
            "Sotuvlar tarixi bo'yicha hisoblanmoqda…");

        var soldLines = await Task.Run(() => SoldLineItemsStore.LoadSince(since)).ConfigureAwait(true);

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

            var daysLeft = product.Quantity / dailyRate;
            rows.Add(new ReorderRow
            {
                ProductName = product.Title,
                StockText = FormatQuantity(product.Quantity),
                DailyRateText = dailyRate.ToString("0.##", CultureInfo.InvariantCulture),
                DaysLeft = daysLeft,
            });
        }

        var sorted = rows.OrderBy(r => r.DaysLeft).Take(60).ToList();
        ReorderSubtitle.Text = Tr.T("Что пора заказать", "Эмнени заказ кылуу керек", "What to reorder",
            "Neyi yeniden sipariş etmeli", "Nimani qayta buyurtma qilish kerak");
        ReorderGrid.ItemsSource = sorted;
        ReorderEmptyText.IsVisible = sorted.Count == 0;
    }

    private void LoadExpiryTab()
    {
        var intakeDates = LocalProductRepository.Instance.LoadIntakeDates();

        var rows = new List<ExpiryRow>();
        foreach (var product in CatalogCacheService.Products)
        {
            if (!intakeDates.TryGetValue(product.Id, out var intake))
                continue;

            var expiry = ExpiryEstimator.EstimateExpiryDate(intake, product.Category);
            if (expiry is null)
                continue;

            var (_, label, _) = ExpiryEstimator.EstimateShelfLife(product.Category);
            rows.Add(new ExpiryRow
            {
                ProductName = product.Title,
                CategoryLabel = label,
                IntakeDateText = intake.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                ExpiryDateText = expiry.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                DaysLeft = (expiry.Value.Date - DateTime.Today).TotalDays,
            });
        }

        var sorted = rows.OrderBy(r => r.DaysLeft).Take(60).ToList();
        ExpiryGrid.ItemsSource = sorted;
        ExpiryEmptyText.IsVisible = sorted.Count == 0;
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
                           "Жаңы жазуулар табылган жок (сервердеги сатуу тарыхы жок же ал мурунтан эле тартылган)."),
                Tr.T("Пополнение склада", "Складды толуктоо", "Warehouse restock", "Depo ikmali", "Omborni to'ldirish"));
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

    private static string FormatQuantity(double qty) =>
        qty == Math.Floor(qty) ? qty.ToString("0", CultureInfo.InvariantCulture) : qty.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed class ReorderRow
    {
        public string ProductName { get; set; } = "";
        public string StockText { get; set; } = "";
        public string DailyRateText { get; set; } = "";
        public double DaysLeft { get; set; }
        public string DaysLeftText => $"{DaysLeft:0.#} дн.";
        public bool IsUrgent => DaysLeft <= 2;
        public bool IsSoon => DaysLeft > 2 && DaysLeft <= 7;
        public bool IsOk => DaysLeft > 7;
    }

    private sealed class ExpiryRow
    {
        public string ProductName { get; set; } = "";
        public string CategoryLabel { get; set; } = "";
        public string IntakeDateText { get; set; } = "";
        public string ExpiryDateText { get; set; } = "";
        public double DaysLeft { get; set; }
        public string DaysLeftText => DaysLeft < 0 ? "Истёк" : $"{DaysLeft:0} дн.";
        public bool IsUrgent => DaysLeft <= 2;
        public bool IsSoon => DaysLeft > 2 && DaysLeft <= 7;
        public bool IsOk => DaysLeft > 7;
    }
}
