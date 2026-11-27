using System.Text.Json;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>2026-11-27 (раздел «Акции»): фейковый сервер акций в памяти — акции во всех статусах (с длинными названиями), отчёт, категории деревом, бренды, филиалы, поиск товаров,
/// preview по простому правилу. Запросы записываются (ключи Idempotency-Key, статус при создании); можно задать ошибку следующего создания.</summary>
public sealed class FakePromotionsApi : IPromotionsApi
{
    public static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.FromHours(6));

    public List<Promotion> Items { get; } = new();
    public List<(string Key, string? Status, string Title)> Creates { get; } = new();
    public List<string> Actions { get; } = new();
    public List<Promotion> Updates { get; } = new();
    public int Previews { get; private set; }
    /// <summary>Ошибка для следующего CreateAsync (один раз).</summary>
    public Exception? NextCreateError { get; set; }

    public static FakePromotionsApi Filled()
    {
        var api = new FakePromotionsApi();
        var day = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.FromHours(6));
        api.Items.Add(new Promotion { Id = "a1", Title = "2+1 на воду «Арашан» 1 л и минеральную воду всех брендов", Status = "active", Kind = Promotion.KindBuyNGetM, Buy = 2, Get = 1,
            Categories = { "c-water" }, StartsAt = day, EndsAt = day.AddDays(40), Priority = 10, Description = "Договорились с поставщиком до конца декабря" });
        api.Items.Add(new Promotion { Id = "a2", Title = "Счастливые часы −15 % на молочку", Status = "active", Kind = Promotion.KindPercent, Percent = 15, MaxQty = 10, Categories = { "c-milk" },
            StartsAt = day, Weekdays = { 1, 2, 3, 4, 5 }, TimeFrom = "10:00", TimeTo = "14:00", Priority = 5 });
        api.Items.Add(new Promotion { Id = "s1", Title = "Новогодняя цена на хлеб", Status = "active", Kind = Promotion.KindFixedPrice, Price = 79, Products = { "p-bread" },
            StartsAt = Now.AddDays(4), EndsAt = Now.AddDays(10) });
        api.Items.Add(new Promotion { Id = "d1", Title = "−20 сом за штуку на соки (черновик)", Status = "draft", Kind = Promotion.KindAmount, Amount = 20, Per = "unit", Brands = { "b-1" } });
        api.Items.Add(new Promotion { Id = "z1", Title = "Ночная скидка", Status = "paused", Kind = Promotion.KindPercent, Percent = 5, ScopeAll = true, TimeFrom = "22:00", TimeTo = "02:00" });
        api.Items.Add(new Promotion { Id = "f1", Title = "Осенняя акция (завершена)", Status = "finished", Kind = Promotion.KindBuyNGetM, Buy = 3, Get = 1, ScopeAll = true, StartsAt = day.AddDays(-60), EndsAt = day.AddDays(-10) });
        return api;
    }

    public Task<PromotionList> ListAsync(string statuses = "active,paused,draft,finished", CancellationToken ct = default) =>
        Task.FromResult(new PromotionList("v1", Items.Count, Items.Select(i => i.Clone()).ToList()));

    public Task<Promotion> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.First(i => i.Id == id).Clone());

    public Task<Promotion> CreateAsync(Promotion promotion, string idempotencyKey, string? status = null, CancellationToken ct = default)
    {
        Creates.Add((idempotencyKey, status, promotion.Title));
        if (NextCreateError is { } err)
        {
            NextCreateError = null;
            return Task.FromException<Promotion>(err);
        }
        var p = promotion.Clone();
        p.Id = "new-" + Creates.Count;
        p.Status = status ?? "draft";
        p.Version = 1;
        Items.Add(p);
        return Task.FromResult(p.Clone());
    }

    public Task<Promotion> UpdateAsync(Promotion promotion, CancellationToken ct = default)
    {
        Updates.Add(promotion.Clone());
        var i = Items.FindIndex(x => x.Id == promotion.Id);
        var p = promotion.Clone();
        p.Version++;
        if (i >= 0)
            Items[i] = p;
        return Task.FromResult(p.Clone());
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        Actions.Add("delete:" + id);
        Items.RemoveAll(x => x.Id == id);
        return Task.CompletedTask;
    }

    public Task<Promotion?> ChangeStatusAsync(string id, string action, CancellationToken ct = default)
    {
        Actions.Add(action + ":" + id);
        var p = Items.First(x => x.Id == id);
        p.Status = action switch { "start" or "resume" => "active", "pause" => "paused", _ => "finished" };
        return Task.FromResult<Promotion?>(p.Clone());
    }

    public Task<PromotionPreview> PreviewAsync(Promotion promotion, IReadOnlyList<PromotionPreviewItem> items, CancellationToken ct = default)
    {
        Previews++;
        var lines = items.Select((it, i) =>
        {
            var price = it.Price ?? 100m;
            var disc = promotion.Kind == Promotion.KindBuyNGetM ? Math.Floor(it.Qty / (promotion.Buy + promotion.Get)) * promotion.Get * price : Math.Round(price * it.Qty * 0.1m, 2);
            return new PromotionPreviewLine(i, it.Product, it.Qty, price, disc, price * it.Qty - disc,
                disc > 0 ? new[] { new PromotionApplied("promotion", "preview", 1, promotion.Title, 1, disc) } : Array.Empty<PromotionApplied>());
        }).ToList();
        var warnings = new List<PromotionWarning> { new("promotion_capped_by_cost", 0, 10m, 90m, "") };
        var hints = new List<PromotionHint> { new("preview", 1, "Добавьте ещё 1 — будет бесплатно") };
        return Task.FromResult(new PromotionPreview(lines.Sum(l => l.Price * l.Qty), lines.Sum(l => l.LineTotal), lines.Sum(l => l.Discount), lines, warnings, hints));
    }

    public Task<IReadOnlyList<PromotionReportRow>> ReportAsync(DateTime from, DateTime to, string? promotionId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PromotionReportRow>>(new[] { new PromotionReportRow("a1", "2+1", 128, 384, 25600m, 76800m, 9100m) });

    public Task<IReadOnlyList<PromotionRef>> CategoriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PromotionRef>>(new[]
    {
        new PromotionRef("c-water", "Вода и напитки"), new PromotionRef("c-water-min", "Минеральная вода с газом и без газа", "c-water"),
        new PromotionRef("c-milk", "Молочные продукты"), new PromotionRef("c-kefir", "Кефир", "c-milk"), new PromotionRef("c-bread", "Хлеб"),
    });

    public Task<IReadOnlyList<PromotionRef>> BrandsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PromotionRef>>(new[] { new PromotionRef("b-1", "Нурсүт"), new PromotionRef("b-2", "Арашан") });

    public Task<IReadOnlyList<PromotionRef>> BranchesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PromotionRef>>(new[] { new PromotionRef("br-1", "Главный магазин"), new PromotionRef("br-2", "Филиал на Ахунбаева") });

    public Task<IReadOnlyList<PromotionProduct>> SearchProductsAsync(string query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PromotionProduct>>(new[] { new PromotionProduct("p-water", "Вода «Арашан» 1 л", 100m), new PromotionProduct("p-kefir", "Кефир «Нурсүт» 2,5 % 1 л", 60m) }
            .Where(p => p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList());

    public Task<PromotionProduct?> ProductAsync(string id, CancellationToken ct = default) =>
        Task.FromResult<PromotionProduct?>(id == "p-bread" ? new PromotionProduct("p-bread", "Хлеб «Бишкек нан» формовой 550 г", 45m) : null);
}

public static class PromotionsFixture
{
    public static PromotionsWindow CreateWindow(FakePromotionsApi? api = null, bool owner = true)
    {
        PromotionsWindow.NowForTests = () => FakePromotionsApi.Now;
        PromotionsWindow.ConfirmForTests = _ => Task.FromResult(true);
        return new PromotionsWindow(api ?? FakePromotionsApi.Filled(), owner);
    }

    public static void Reset()
    {
        PromotionsWindow.NowForTests = null;
        PromotionsWindow.ConfirmForTests = null;
        PromotionsWindow.ApiFactoryForTests = null;
        PromotionsWindow.IsOwnerForTests = null;
    }

    public static ApiException Invalid(string field, string detail) =>
        new("Request failed", 400, JsonDocument.Parse("{\"code\":\"promotion_invalid\",\"detail\":\"" + detail + "\",\"field\":\"" + field + "\",\"fields\":{\"" + field + "\":[\"" + detail + "\"]}}").RootElement.Clone());
}
