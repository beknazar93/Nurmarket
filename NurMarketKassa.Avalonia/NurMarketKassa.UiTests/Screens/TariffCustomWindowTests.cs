using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-08 (ТЗ п. 32, Б-39): окно тарифа «Своё» в Avalonia.Headless на заглушке ITariffApi (сервер не нужен). Проверяется: 11 пакетов с ценами с сервера, живой пересчёт,
/// «Стандарт дешевле», «уже подключено», ключ Idempotency-Key на попытку (тот же при повторе после обрыва, новый после смены набора), сообщение «Заявка №… принята»,
/// текст не обрезан на 1024×600 и 1366×768 (ru, ky). Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\tariff-custom-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class TariffCustomWindowTests
{
    private sealed class FakeTariffApi : ITariffApi
    {
        public static readonly string[] Codes = ["ai", "clients", "sales_analytics", "restock", "salary", "debts", "service", "scales", "telegram_bot", "shift_analytics", "analytics_export"];
        public HashSet<string> Active { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Keys { get; } = [];
        public int FailSubmits;
        public Exception? QuoteError;
        public HashSet<string>? Included; // 2026-11-20: какие пакеты входят в «Стандарт» (included_in); null — все (как раньше)
        public List<IReadOnlyList<string>> Quotes { get; } = [];
        public List<IReadOnlyList<string>> Submits { get; } = [];

        public Task<TariffCatalog> GetCatalogAsync(CancellationToken ct = default) =>
            Task.FromResult(new TariffCatalog(
                [new TariffPlan("start", "Старт", 1300m, null), new TariffPlan("standard", "Стандарт", 3600m, null), new TariffPlan("custom", "Своё", null, "start")],
                Codes.Select(c => new TariffPack(c, c switch { "ai" => "ИИ-помощник", "clients" => "Клиенты и бонусы", "sales_analytics" => "Аналитика продаж", "analytics_export" => "Выгрузка аналитики", _ => "Пакет " + c },
                    "Описание пакета, которое может быть достаточно длинным и переноситься на вторую строку в узком окне", 1500m, 400m, Included is null || Included.Contains(c) ? ["standard"] : [])).ToList()));

        public Task<TariffQuote> QuoteAsync(IReadOnlyList<string> packs, CancellationToken ct = default)
        {
            if (QuoteError is not null)
                throw QuoteError;
            Quotes.Add(packs.ToList());
            var rows = packs.Select(p => Active.Contains(p) ? new TariffQuotePack(p, 0m, 0m, true) : new TariffQuotePack(p, 400m, 1500m, false)).ToList();
            var monthly = rows.Sum(r => r.Monthly);
            return Task.FromResult(new TariffQuote(monthly, rows.Sum(r => r.Once), 1300m, rows, 1300m + monthly > 3600m, 3600m));
        }

        public Task<TariffOrder> SubmitCustomAsync(IReadOnlyList<string> packs, string idempotencyKey, CancellationToken ct = default)
        {
            Keys.Add(idempotencyKey);
            if (FailSubmits-- > 0)
                throw new HttpRequestException("обрыв связи");
            Submits.Add(packs.ToList());
            return Task.FromResult(new TariffOrder("o-501", "pending", 400m, 1500m, null));
        }
    }

    private static string Norm(string text) => text.Replace((char)0x00A0, ' ').Replace((char)0x202F, ' ');

    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task Window_ShowsServerPrices_LivePrice_StandardHint_AndFitsScreen(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var api = new FakeTariffApi();
        api.Active.Add("clients");
        var window = new TariffCustomWindow(api);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            await window.LoadAsync();
            UiShot.Settle(8);
            var tag = $"{width}x{height} {lang}";

            var checks = window.GetVisualDescendants().OfType<CheckBox>().ToList();
            checks.Should().HaveCount(11, $"все пакеты каталога ({tag})");
            checks.Count(c => c.IsChecked == true && !c.IsEnabled).Should().Be(1, "«Клиенты» уже подключены: галочка стоит, снять нельзя");
            var all = Norm(UiDrive.AllText(window));
            all.Should().Contain("1 500 ").And.Contain("400 ", "цены берутся с сервера");

            foreach (var code in new[] { "ai", "sales_analytics", "restock", "salary", "debts", "service", "scales" })
                window.SetChecked(code, true);
            await window.QuoteForTestAsync();
            UiShot.Settle(8);

            api.Quotes.Last().Should().NotContain("clients", "подключённый пакет в расчёт не уходит");
            Norm(window.SummaryText).Should().Contain("1 300").And.Contain("2 800", "пакеты 7 × 400").And.Contain("10 500").And.Contain("4 100");
            Norm(UiDrive.AllText(window)).Should().Contain("3 600", "подсказка «Стандарт дешевле» с ценой сервера");
            window.CanSubmit.Should().BeTrue();

            // Все тексты внутри окна, обрезки нет (по высоте окно прокручивается, по ширине — нет горизонтальной прокрутки).
            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
            var clipped = TextClipChecker.Find(window, "tariff-custom", langCode);
            clipped.Should().BeEmpty($"текст не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));
            foreach (var btn in window.GetVisualDescendants().OfType<Button>().Where(b => b.GetType() == typeof(Button) && b.IsEffectivelyVisible && b.Bounds.Height > 0)) // CheckBox — тоже Button, он прокручивается
            {
                var p = btn.TranslatePoint(default, window)!.Value;
                (p.Y + btn.Bounds.Height).Should().BeLessThanOrEqualTo(window.Bounds.Height + 0.5, $"кнопка «{UiDrive.TextOf(btn)}» внутри окна ({tag})");
            }

            using var shot = UiShot.Render(window);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"tariff-custom-{langCode}-{width}x{height}.png"), data.ToArray());
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Submit_KeyIsPerAttempt_SameAfterNetworkError_NewAfterChange_AndShowsOrderNumber()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeTariffApi { FailSubmits = 1 };
        var window = new TariffCustomWindow(api) { Confirm = _ => Task.FromResult(true) };
        try
        {
            window.Show();
            await window.LoadAsync();
            window.SetChecked("ai", true);
            await window.QuoteForTestAsync();
            window.CanSubmit.Should().BeTrue();

            await window.SubmitForTestAsync(); // обрыв связи
            window.ErrorText.Should().NotBeNull().And.Contain("не задвоится");
            api.Keys.Should().HaveCount(1);
            window.CanSubmit.Should().BeTrue("повторить можно");

            await window.SubmitForTestAsync(); // тот же ключ — успех
            api.Keys.Should().HaveCount(2);
            api.Keys[1].Should().Be(api.Keys[0], "повтор после обрыва — с тем же Idempotency-Key");
            Guid.TryParse(api.Keys[0], out _).Should().BeTrue();
            window.DoneText.Should().Contain("Заявка №o-501 принята").And.Contain("менеджер свяжется");
            window.CanSubmit.Should().BeFalse();
            api.Submits.Single().Should().Equal("ai");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }

        // Смена набора пакетов после обрыва — другая попытка, другой ключ.
        var api2 = new FakeTariffApi { FailSubmits = 1 };
        var window2 = new TariffCustomWindow(api2) { Confirm = _ => Task.FromResult(true) };
        try
        {
            window2.Show();
            await window2.LoadAsync();
            window2.SetChecked("ai", true);
            await window2.QuoteForTestAsync();
            await window2.SubmitForTestAsync();
            window2.SetChecked("scales", true);
            await window2.QuoteForTestAsync();
            await window2.SubmitForTestAsync();
            api2.Keys.Should().HaveCount(2);
            api2.Keys[1].Should().NotBe(api2.Keys[0]);
            api2.Submits.Single().Should().Equal("ai", "scales");
        }
        finally
        {
            window2.Close();
            UiShot.Pump();
        }
    }

    private static readonly (int W, int H)[] SmallSizes = [(1024, 600), (1366, 768)];
    private static readonly AppLanguage[] Langs3 = [AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek];

    public static IEnumerable<object[]> Sizes3() =>
        SmallSizes.SelectMany(s => Langs3.Select(l => new object[] { s.W, s.H, l }));

    private static string Code(AppLanguage l) => l switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    /// <summary>2026-11-20: тариф «Стандарт» — конструктор доступен; пакеты, что входят в «Стандарт», помечены «Входит в ваш тариф» и в расчёт не уходят; ru/ky/uz без обрезки.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(Sizes3))]
    public async Task Standard_MarksIncludedPacks_NotQuoted_AndFitsScreen(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var api = new FakeTariffApi { Included = ["ai", "clients", "scales"] };
        var window = new TariffCustomWindow(api, "Стандарт");
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            await window.LoadAsync();
            UiShot.Settle(8);
            var tag = $"{width}x{height} {lang}";

            foreach (var code in new[] { "ai", "clients", "scales" })
            {
                window.IsIncluded(code).Should().BeTrue($"{code} входит в Стандарт ({tag})");
                window.PackStatusText(code).Should().NotBeEmpty();
            }
            window.IsIncluded("salary").Should().BeFalse();
            window.PackStatusText("ai").Should().Be(Tr.T("Входит в ваш тариф", "Тарифиңизге кирет", "Included in your plan", "Tarifinize dahil", "Tarifingizga kiradi"));
            api.Quotes.Single().Should().NotContain(["ai", "clients", "scales"], "входящие в тариф пакеты в расчёт не уходят");
            window.ErrorText.Should().BeNull("конструктор доступен");
            window.ManagerButtonVisible.Should().BeFalse();

            window.SetChecked("salary", true);
            window.SetChecked("debts", true);
            await window.QuoteForTestAsync();
            UiShot.Settle(8);
            api.Quotes.Last().Should().Equal("salary", "debts");
            Norm(window.SummaryText).Should().Contain("Стандарт");
            window.CanSubmit.Should().BeTrue();
            Norm(UiDrive.AllText(window)).Should().NotContain("«Старта»").And.NotContain("“Start”");

            var clipped = TextClipChecker.Find(window, "tariff-custom-standard", Code(lang));
            clipped.Should().BeEmpty($"текст не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));
            SaveShot(window, $"tariff-custom-standard-{Code(lang)}-{width}x{height}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-20: сервер отказал по тарифу (409, «Мини»/«Старт» без права) — понятный текст без «поверх Старта», без «обратитесь к менеджеру», кнопка «Написать менеджеру» открывает чат поддержки.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(Sizes3))]
    public async Task ServerRefusal409_ShowsClearText_AndManagerButton_AndFitsScreen(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var api = new FakeTariffApi { QuoteError = new ApiException("plan_not_allowed", 409, System.Text.Json.JsonDocument.Parse("""{"detail":"plan_not_allowed"}""").RootElement) };
        var window = new TariffCustomWindow(api, "Мини");
        string? opened = null;
        window.OpenChat = url => { opened = url; return true; };
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            await window.LoadAsync();
            UiShot.Settle(8);
            var tag = $"{width}x{height} {lang}";

            window.ErrorText.Should().NotBeNullOrWhiteSpace();
            window.ErrorText.Should().NotContain("Старта").And.NotContain("Обратитесь к менеджеру");
            window.ManagerButtonVisible.Should().BeTrue($"({tag})");
            window.CanSubmit.Should().BeFalse();
            window.GetVisualDescendants().OfType<CheckBox>().Should().OnlyContain(c => !c.IsEnabled);

            window.ManagerClickForTest();
            opened.Should().NotBeNull().And.StartWith("https://wa.me/996221068796?text=");

            var clipped = TextClipChecker.Find(window, "tariff-custom-refusal", Code(lang));
            clipped.Should().BeEmpty($"текст не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));
            SaveShot(window, $"tariff-custom-refusal-{Code(lang)}-{width}x{height}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-20: «Старт» — как прежде: пакеты не входят в тариф, считаются все; помеченных «Входит в ваш тариф» нет.</summary>
    [AvaloniaFact]
    public async Task Start_NoIncludedPacks_AllQuoted()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeTariffApi { Included = [] };
        var window = new TariffCustomWindow(api, "Старт");
        try
        {
            window.Show();
            await window.LoadAsync();
            FakeTariffApi.Codes.Should().OnlyContain(c => !window.IsIncluded(c));
            api.Quotes.Single().Should().HaveCount(11);
            window.ManagerButtonVisible.Should().BeFalse();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task NotOwner_403_ShowsMessage_AndLocksWindow()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeTariffApi { QuoteError = new ApiException("forbidden", 403) };
        var window = new TariffCustomWindow(api);
        try
        {
            window.Show();
            await window.LoadAsync();
            UiShot.Settle();
            window.ErrorText.Should().Contain("только владелец");
            window.GetVisualDescendants().OfType<CheckBox>().Should().OnlyContain(c => !c.IsEnabled);
            window.CanSubmit.Should().BeFalse();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
