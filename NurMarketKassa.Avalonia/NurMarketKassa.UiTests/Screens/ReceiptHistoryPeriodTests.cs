using Avalonia;
using System.Globalization;
using System.Text.Json;
using System.Web;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11 (владелец: «в кассе история чеков — добавь фильтр: месяц, неделя, день, специальная дата!»): период «Истории чеков» кассы —
/// «Неделя», «Месяц», «Дата…» (диапазон «с — по») рядом с «Эта смена / Сегодня / Вчера». Правила границ — общие с PeriodRange; запрос к серверу идёт по границам дат
/// (поддельный NurCRM — WireMock); фильтр работает вместе с поиском; окно проверяется при 1024×600 и 1366×768 (ru, ky): кнопки ≥ 44 px, текст не обрезан.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\receipts-period-*.png.</summary>
[Collection("UiScenarios")]
public sealed class ReceiptHistoryPeriodTests : IDisposable
{
    private readonly string? _savedShift = PosApp.ActiveShiftId;

    public ReceiptHistoryPeriodTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        PosApp.ActiveShiftId = null; // смены нет — окно открывается на «Сегодня», история — по датам
    }

    public void Dispose() => PosApp.ActiveShiftId = _savedShift;

    private static DateTime D(int daysAgo, int hour = 11) => DateTime.Today.AddDays(-daysAgo).AddHours(hour);

    private static string Row(string id, string number, DateTime at, string item, string total = "100.00") =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = id,
            ["number"] = number,
            ["status"] = "paid",
            ["total"] = total,
            ["payment_method"] = "cash",
            ["created_at"] = at.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            ["first_item_name"] = item,
        });

    /// <summary>Сервер отдаёт все эти чеки на любой запрос (даты в запросе он «не знает») — отбор по периоду на стороне кассы проверяется отдельно от запроса.</summary>
    private static void StubSales(params string[] rows) =>
        UiFake.Server.Json("GET", "/api/main/pos/sales/", 200, "{\"count\":" + rows.Length + ",\"next\":null,\"previous\":null,\"results\":[" + string.Join(",", rows) + "]}");

    private static List<(string? From, string? To, string? PageSize)> SalesQueries() =>
        UiFake.Server.Server.LogEntries
            .Where(e => e.RequestMessage.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && e.RequestMessage.Path.TrimEnd('/').EndsWith("/api/main/pos/sales", StringComparison.Ordinal))
            .Select(e =>
            {
                var q = HttpUtility.ParseQueryString(new Uri(e.RequestMessage.Url).Query);
                return (q["date_from"], q["date_to"], q["page_size"]);
            })
            .ToList();

    private static string Iso(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ───────────── правила границ (чистая логика) ─────────────

    [Fact]
    public void Bounds_day_week_month_follow_PeriodRange_rules()
    {
        var today = new DateTime(2026, 11, 11, 23, 59, 0); // время суток границ не меняет
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Today, today).Should().Be((new DateTime(2026, 11, 11), new DateTime(2026, 11, 12)));
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Yesterday, today).Should().Be((new DateTime(2026, 11, 10), new DateTime(2026, 11, 11)));
        // «Неделя» — последние 7 дней включая сегодня (05.11–11.11); «Месяц» — с 1-го числа (01.11–11.11).
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Week, today).Should().Be((new DateTime(2026, 11, 5), new DateTime(2026, 11, 12)));
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Month, today).Should().Be((new DateTime(2026, 11, 1), new DateTime(2026, 11, 12)));
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Week, today).Should().Be((PeriodRange.WeekFrom(today), today.Date.AddDays(1)));
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Month, today).Should().Be((PeriodRange.MonthFrom(today), today.Date.AddDays(1)));
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.CurrentShift, today).Should().BeNull("границы смены зависят от смены, а не от календаря");
    }

    [Fact]
    public void Bounds_cross_midnight_month_and_year()
    {
        // Первое число месяца: «Месяц» — один день; «Неделя» уходит в прошлый месяц.
        var first = new DateTime(2026, 12, 1, 0, 5, 0);
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Month, first).Should().Be((new DateTime(2026, 12, 1), new DateTime(2026, 12, 2)));
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Week, first).Should().Be((new DateTime(2026, 11, 25), new DateTime(2026, 12, 2)));
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Yesterday, first).Should().Be((new DateTime(2026, 11, 30), new DateTime(2026, 12, 1)));
        // Новый год и високосный февраль.
        var newYear = new DateTime(2027, 1, 3, 12, 0, 0);
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Week, newYear).Should().Be((new DateTime(2026, 12, 28), new DateTime(2027, 1, 4)));
        var leap = new DateTime(2028, 3, 2);
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Week, leap).Should().Be((new DateTime(2028, 2, 25), new DateTime(2028, 3, 3)));
        // 23:59 и 00:00 — один и тот же день.
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Today, new DateTime(2026, 11, 11, 0, 0, 0)).Should()
            .Be(ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Today, new DateTime(2026, 11, 11, 23, 59, 59)));
    }

    [Fact]
    public void Bounds_custom_one_date_range_swapped_and_empty()
    {
        var today = new DateTime(2026, 11, 11);
        // Одна дата — один день (включительно).
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Custom, today, new DateTime(2026, 11, 3), new DateTime(2026, 11, 3))
            .Should().Be((new DateTime(2026, 11, 3), new DateTime(2026, 11, 4)));
        // Диапазон «с — по»: оба дня входят.
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Custom, today, new DateTime(2026, 10, 30), new DateTime(2026, 11, 2, 15, 0, 0))
            .Should().Be((new DateTime(2026, 10, 30), new DateTime(2026, 11, 3)));
        // Перепутан порядок — меняется; задана одна граница — период из неё; не заданы — сегодня.
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Custom, today, new DateTime(2026, 11, 9), new DateTime(2026, 11, 7))
            .Should().Be((new DateTime(2026, 11, 7), new DateTime(2026, 11, 10)));
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Custom, today, new DateTime(2026, 11, 9), null)
            .Should().Be((new DateTime(2026, 11, 9), new DateTime(2026, 11, 10)));
        ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Custom, today).Should().Be((today, today.AddDays(1)));
    }

    [Fact]
    public void Period_label_matches_the_bounds()
    {
        var today = DateTime.Today;
        var b = ReceiptHistoryService.PeriodBounds(ReceiptHistoryPeriod.Week, today)!.Value;
        PeriodRange.Label(b.From, b.ToExclusive.AddDays(-1)).Should().Be(PeriodRange.Label(today.AddDays(-6), today));
        PeriodRange.Label(new DateTime(2026, 11, 9), new DateTime(2026, 11, 9)).Should().Be("09.11");
        PeriodRange.Label(new DateTime(2026, 11, 9), new DateTime(2026, 11, 11)).Should().Be("09.11–11.11");
    }

    // ───────────── запрос к серверу и отбор по периоду ─────────────

    [AvaloniaFact]
    public async Task Server_is_asked_by_date_bounds_and_receipts_outside_the_period_are_dropped()
    {
        await TestHost.WarmAsync();
        UiFake.Reset();
        StubSales(Row("s0", "000500", D(0), "Хлеб"), Row("s3", "000499", D(3), "Молоко"), Row("s6", "000498", D(6), "Сыр"),
            Row("s7", "000497", D(7), "Чай"), Row("s40", "000496", D(40), "Кофе"));

        // «Неделя»: 7 дней включая сегодня; чеки старше — не показываются, даже если сервер их прислал.
        var week = await ReceiptHistoryService.LoadAsync(ReceiptHistoryPeriod.Week);
        week.Entries.Select(e => e.Id).Should().BeEquivalentTo(new[] { "s0", "s3", "s6" });
        var q = SalesQueries().Should().ContainSingle().Subject;
        q.From.Should().Be(Iso(DateTime.Today.AddDays(-6)));
        q.To.Should().Be(Iso(DateTime.Today.AddDays(1)), "date_to сервера не включает свой день — передаётся день ПОСЛЕ последнего");
        q.PageSize.Should().Be("200", "много чеков — страницы по 200, а не по 80");

        // «Дата…»: один день — только он и спрашивается у сервера.
        UiFake.Server.Server.ResetLogEntries();
        var custom = await ReceiptHistoryService.LoadAsync(ReceiptHistoryPeriod.Custom, default, DateTime.Today.AddDays(-3), DateTime.Today.AddDays(-3));
        custom.Entries.Select(e => e.Id).Should().BeEquivalentTo(new[] { "s3" });
        var one = SalesQueries().Should().ContainSingle().Subject;
        one.From.Should().Be(Iso(DateTime.Today.AddDays(-3)));
        one.To.Should().Be(Iso(DateTime.Today.AddDays(-2)));

        // «Дата…»: диапазон «с — по» включительно, с перепутанным порядком.
        UiFake.Server.Server.ResetLogEntries();
        var range = await ReceiptHistoryService.LoadAsync(ReceiptHistoryPeriod.Custom, default, DateTime.Today.AddDays(-7), DateTime.Today.AddDays(-3));
        range.Entries.Select(e => e.Id).Should().BeEquivalentTo(new[] { "s3", "s6", "s7" });
        var swapped = await ReceiptHistoryService.LoadAsync(ReceiptHistoryPeriod.Custom, default, DateTime.Today.AddDays(-3), DateTime.Today.AddDays(-7));
        swapped.Entries.Select(e => e.Id).Should().BeEquivalentTo(range.Entries.Select(e => e.Id));

        // «Сегодня» и «Вчера» по-прежнему по 80 на страницу и ровно своими границами.
        UiFake.Server.Server.ResetLogEntries();
        var today = await ReceiptHistoryService.LoadAsync(ReceiptHistoryPeriod.Today);
        today.Entries.Select(e => e.Id).Should().BeEquivalentTo(new[] { "s0" });
        SalesQueries().Should().ContainSingle().Which.PageSize.Should().Be("80");
    }

    [AvaloniaFact]
    public async Task Month_starts_on_the_first_day_of_the_month()
    {
        await TestHost.WarmAsync();
        UiFake.Reset();
        var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        StubSales(Row("m1", "000601", first.AddHours(9), "Хлеб"), Row("m0", "000600", first.AddMinutes(-30), "Молоко"));
        var month = await ReceiptHistoryService.LoadAsync(ReceiptHistoryPeriod.Month);
        month.Entries.Select(e => e.Id).Should().BeEquivalentTo(new[] { "m1" }, "чек за полчаса до 1-го числа — прошлый месяц");
        SalesQueries().Should().ContainSingle().Which.From.Should().Be(Iso(first));
    }

    // ───────────── окно: сегменты, даты, поиск, размеры ─────────────

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static void Wait(Func<bool> done, int maxMs = 6000)
    {
        var started = DateTime.UtcNow;
        while (!done() && (DateTime.UtcNow - started).TotalMilliseconds < maxMs)
            UiShot.Settle(1, 40);
        UiShot.Settle(2, 30);
    }

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "receipts-period-" + name + ".png"), data.ToArray());
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Window_period_segments_dates_count_and_search(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        UiFake.Reset();
        StubSales(Row("s0", "000500", D(0), "Хлеб"), Row("s3", "000499", D(3), "Молоко"), Row("s10", "000498", D(10), "Хлеб белый"));
        var tag = $"{width}x{height} {lang}";
        var slug = $"{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}";
        var window = new ReceiptHistoryWindow();
        try
        {
            UiShot.Render(window, new UiSize(width, height));
            Wait(() => SalesQueries().Count > 0);

            T<RadioButton>("WeekChip").Bounds.Height.Should().BeGreaterThanOrEqualTo(44, $"цель нажатия сегмента ≥ 44 ({tag})");
            T<RadioButton>("DateChip").Bounds.Height.Should().BeGreaterThanOrEqualTo(44, tag);
            T<RadioButton>("WeekChip").Content.Should().Be(Tr.T("Неделя", "Апта", "Week", "Hafta", "Hafta"));
            Info().Should().Contain(Tr.T("Найдено чеков: 1", "Табылган чектер: 1", "Receipts found: 1", "Bulunan fiş: 1", "Topilgan cheklar: 1"), "по умолчанию — «Сегодня»: один чек");
            T<WrapPanel>("CustomDatesPanel").IsVisible.Should().BeFalse("поля «с — по» только у «Даты…»");

            // «Неделя»: запрос по границам, подпись «дд.мм–дд.мм», найдено 2 (чек 10-дневной давности не входит).
            UiFake.Server.Server.ResetLogEntries();
            UiDrive.Click(window, T<RadioButton>("WeekChip"));
            Wait(() => SalesQueries().Count > 0 && Info().Contains(PeriodRange.Label(DateTime.Today.AddDays(-6), DateTime.Today)));
            var wq = SalesQueries().Last();
            (wq.From, wq.To).Should().Be((Iso(DateTime.Today.AddDays(-6)), Iso(DateTime.Today.AddDays(1))));
            Info().Should().StartWith(PeriodRange.Label(DateTime.Today.AddDays(-6), DateTime.Today)).And.Contain(": 2");
            SaveShot(window, "week-" + slug);

            // «Месяц»: с 1-го числа.
            UiFake.Server.Server.ResetLogEntries();
            UiDrive.Click(window, T<RadioButton>("MonthChip"));
            Wait(() => SalesQueries().Count > 0);
            SalesQueries().Last().From.Should().Be(Iso(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)));

            // «Дата…»: поля видны, применяются сразу; один день — 10 дней назад (найден один чек).
            UiFake.Server.Server.ResetLogEntries();
            UiDrive.Click(window, T<RadioButton>("DateChip"));
            Wait(() => SalesQueries().Count > 0);
            T<WrapPanel>("CustomDatesPanel").IsVisible.Should().BeTrue();
            var from = T<CalendarDatePicker>("DateFromPicker");
            var to = T<CalendarDatePicker>("DateToPicker");
            from.SelectedDate.Should().Be(DateTime.Today);
            to.SelectedDate.Should().Be(DateTime.Today);
            from.Bounds.Height.Should().BeGreaterThanOrEqualTo(44, tag);

            UiFake.Server.Server.ResetLogEntries();
            to.SelectedDate = DateTime.Today.AddDays(-10);
            Wait(() => SalesQueries().Count > 0);
            from.SelectedDate.Should().Be(DateTime.Today.AddDays(-10), "«по» раньше «с» — «с» подтягивается, период не перевёрнут");
            var oneDay = SalesQueries().Last();
            (oneDay.From, oneDay.To).Should().Be((Iso(DateTime.Today.AddDays(-10)), Iso(DateTime.Today.AddDays(-9))));
            Wait(() => Info().Contains(Tr.T("Найдено чеков: 1", "Табылган чектер: 1", "Receipts found: 1", "Bulunan fiş: 1", "Topilgan cheklar: 1")));
            Info().Should().StartWith(PeriodRange.Label(DateTime.Today.AddDays(-10), DateTime.Today.AddDays(-10)));

            // Диапазон «с — по»: 10 дней назад … сегодня — все три чека; поиск сужает до двух («Хлеб»), счётчик «N из M».
            UiFake.Server.Server.ResetLogEntries();
            to.SelectedDate = DateTime.Today;
            Wait(() => SalesQueries().Count > 0);
            var range = SalesQueries().Last();
            (range.From, range.To).Should().Be((Iso(DateTime.Today.AddDays(-10)), Iso(DateTime.Today.AddDays(1))));
            Wait(() => Info().Contains(": 3"));
            var search = T<TextBox>("SearchBox");
            UiDrive.Type(window, search, "Хлеб");
            Wait(() => Info().Contains(Tr.T(" из 3", " / 3", " of 3", " / 3", " / 3")));
            Info().Should().Contain(Tr.T("Найдено чеков: 2 из 3", "Табылган чектер: 2 / 3", "Receipts found: 2 of 3", "Bulunan fiş: 2 / 3", "Topilgan cheklar: 2 / 3"));
            SaveShot(window, "date-" + slug);

            // Выбор запоминается на время окна: «Сегодня» → обратно «Дата…» — прежние «с — по».
            UiDrive.Click(window, window.GetVisualDescendants().OfType<RadioButton>().First(r => "today".Equals(r.Tag)));
            UiShot.Settle(2);
            UiDrive.Click(window, T<RadioButton>("DateChip"));
            UiShot.Settle(2);
            from.SelectedDate.Should().Be(DateTime.Today.AddDays(-10));
            to.SelectedDate.Should().Be(DateTime.Today);

            // Не вылезает за окно, текст не обрезан.
            var row = T<WrapPanel>("PeriodInfoRow");
            var right = row.TranslatePoint(new Avalonia.Point(row.Bounds.Width, 0), window)!.Value.X;
            right.Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5, $"строка периода внутри окна ({tag})");
            TextClipChecker.Find(window, "ReceiptHistory-period", lang.ToString()).Should().BeEmpty($"текст не обрезан ({tag})");

            T<Control>("PeriodChips").Bounds.Right.Should().BeLessThanOrEqualTo(window.Bounds.Width, $"сегменты внутри окна ({tag})");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }

        TControl T<TControl>(string name) where TControl : Control => UiDrive.Find<TControl>(window, name);
        string Info() => T<TextBlock>("PeriodInfoText").Text ?? "";
    }
}
