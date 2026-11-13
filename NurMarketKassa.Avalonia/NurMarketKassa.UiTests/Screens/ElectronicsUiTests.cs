using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (ТЗ «Сфера Электроника», ЭЛ2–ЭЛ4): окно «Серийный номер / IMEI» и поиск чека по серийнику в «Истории чеков» в Avalonia.Headless
/// на 1024×600 и 1366×768 (ru, ky). Окно: поля по числу штук, ошибка для неверного IMEI, повтор номера, «Пропустить»; текст не обрезан, окно не выше 85 % экрана.
/// История: в сфере «Электроника» чек находится по последним цифрам IMEI, чужой номер — «Не найдено на этой кассе»; кнопка «Гарантийный талон» только у чека с номерами;
/// в других сферах кнопки нет. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\electronics-*.png.</summary>
[Collection("UiScenarios")]
public sealed class ElectronicsUiTests : IDisposable
{
    private const string ValidImei = "490154203237518";
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly string? _shift = PosApp.ActiveShiftId;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "electronics-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public ElectronicsUiTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
        SerialJournal.FileOverride = Path.Combine(_dir, "serial-journal.json");
        ElectronicsSettings.FileOverride = Path.Combine(_dir, "electronics-settings.json");
    }

    public void Dispose()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        PosApp.ActiveShiftId = _shift;
        ModalShell.WorkingAreaForTests = null;
        SerialJournal.FileOverride = null;
        ElectronicsSettings.FileOverride = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static void Save(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "electronics-" + name + ".png"), data.ToArray());
    }

    // ───────────── окно серийных номеров ─────────────

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Serial_dialog_one_and_three_fields_fit_screen_and_text_is_not_clipped(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var slug = $"{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}";
        foreach (var count in new[] { 1, 3 })
        {
            var dlg = new SerialNumberDialog("Смартфон Samsung Galaxy A15 128 ГБ чёрный", count, new[] { "ABC12345" });
            try
            {
                using var _ = UiShot.Render(dlg);
                // Кнопки подвала («Пропустить», «Отмена», «Готово») целиком внутри окна — на узком окне «Готово» обрезалось справа.
                foreach (var name in new[] { "SerialSkip", "SerialCancel", "SerialOk" })
                {
                    var button = UiDrive.Find<Button>(dlg, name);
                    var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), dlg)!.Value.X;
                    right.Should().BeLessThanOrEqualTo(dlg.Bounds.Width - 16, $"кнопка {name} внутри окна ({width}x{height} {lang})");
                }
                dlg.Boxes.Should().HaveCount(count, "по одному полю на штуку");
                dlg.Boxes.Should().OnlyContain(b => b.Bounds.Height >= 44, "поле ввода — цель нажатия ≥ 44 px");
                dlg.Bounds.Height.Should().BeLessThanOrEqualTo(height * 0.85 + 36, $"окно не выше 85 % экрана ({width}x{height}, {count} шт.)");

                // Неверный IMEI — ошибка под полями, окно открыто.
                dlg.SetTexts(Enumerable.Repeat("490154203237519", count).ToArray());
                dlg.Confirm();
                UiShot.Pump();
                dlg.ErrorText.IsVisible.Should().BeTrue();
                dlg.ErrorText.Text.Should().Contain("IMEI");
                dlg.IsVisible.Should().BeTrue("с ошибкой окно не закрывается");
                Save(dlg, $"serial-dialog-{count}-error-{slug}");

                var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
                TextClipChecker.Find(dlg, "electronics-serial", langCode).Should().BeEmpty($"текст не обрезан ({width}x{height} {lang}, {count} шт.)");
            }
            finally
            {
                dlg.Close();
                UiShot.Pump();
            }
        }
    }

    [AvaloniaFact]
    public async Task Serial_dialog_accepts_valid_numbers_rejects_duplicates_and_skip_works()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);

        // Два номера: верный IMEI и серийный номер — нормализуются (пробелы, дефисы, регистр).
        var ok = new SerialNumberDialog("Телефон", 2);
        ok.Show();
        UiShot.Pump();
        ok.SetTexts("490-154 203237518", "f2lxk0qj1234");
        ok.Confirm();
        UiShot.Pump();
        ok.Serials.Should().Equal(ValidImei, "F2LXK0QJ1234");
        ok.Skipped.Should().BeFalse();
        ok.IsVisible.Should().BeFalse("после «Готово» окно закрыто");

        // Повтор внутри окна и повтор номера, уже стоящего в чеке — нельзя.
        var dup = new SerialNumberDialog("Телефон", 2);
        dup.Show();
        UiShot.Pump();
        dup.SetTexts("F2LXK0QJ1234", "f2lxk0qj1234");
        dup.Confirm();
        UiShot.Pump();
        dup.ErrorText.IsVisible.Should().BeTrue();
        dup.Serials.Should().BeEmpty();
        dup.Close();

        var inCart = new SerialNumberDialog("Телефон", 1, new[] { ValidImei });
        inCart.Show();
        UiShot.Pump();
        inCart.SetTexts(ValidImei);
        inCart.Confirm();
        UiShot.Pump();
        inCart.ErrorText.IsVisible.Should().BeTrue("этот номер уже в чеке");
        inCart.Close();

        // Пустое поле — ошибка (есть «Пропустить»).
        var empty = new SerialNumberDialog("Телефон", 1);
        empty.Show();
        UiShot.Pump();
        empty.Confirm();
        UiShot.Pump();
        empty.ErrorText.IsVisible.Should().BeTrue();
        empty.Close();
        UiShot.Pump();
    }

    // ───────────── История чеков ─────────────

    private static string Row(string id, string number, DateTime at, string item) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = id,
            ["number"] = number,
            ["status"] = "paid",
            ["total"] = "20000.00",
            ["payment_method"] = "cash",
            ["created_at"] = at.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            ["first_item_name"] = item,
        });

    private static void Wait(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            Thread.Sleep(50);
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task History_finds_receipt_by_imei_tail_foreign_serial_says_not_found_here_and_talon_button_only_for_serial_receipts(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        UiFake.Reset();
        PosApp.ActiveShiftId = null;
        var p = UserPreferences.Instance;
        p.MarketSphere = MarketSpheres.Grocery;
        p.MarketSphereDetail = ElectronicsProfile.Id;

        var at = DateTime.Today.AddHours(11);
        UiFake.Server.Json("GET", "/api/main/pos/sales/", 200,
            "{\"count\":2,\"next\":null,\"previous\":null,\"results\":[" + Row("sale-e1", "000701", at, "Телефон X1") + "," + Row("sale-e2", "000702", at.AddMinutes(5), "Чехол X1") + "]}");
        var cart = new System.Text.Json.Nodes.JsonObject
        {
            ["items"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "l1", ["product_id"] = Guid.NewGuid().ToString(), ["product_name"] = "Телефон X1", ["quantity"] = 1, ["unit_price"] = 20000.0, ["line_total"] = 20000.0,
                [ElectronicsProfile.SerialsField] = new System.Text.Json.Nodes.JsonArray(ValidImei), [ElectronicsProfile.WarrantyMonthsField] = 12,
            }),
        }.ToJsonString();
        SerialJournal.RecordSale("sale-e1", "000701", at, cart);

        var tag = $"{width}x{height} {lang}";
        var slug = $"{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}";
        var window = new ReceiptHistoryWindow();
        try
        {
            UiShot.Render(window, new UiSize(width, height));
            var list = UiDrive.Find<ListBox>(window, "ReceiptsList");
            Wait(() => list.ItemCount == 2);
            list.ItemCount.Should().Be(2, tag);

            var search = UiDrive.Find<TextBox>(window, "SearchBox");
            UiDrive.Type(window, search, "237518"); // последние 6 цифр IMEI
            Wait(() => list.ItemCount == 1);
            list.ItemCount.Should().Be(1, $"по последним цифрам IMEI найден один чек ({tag})");

            UiDrive.Type(window, search, "777777"); // чужой номер
            Wait(() => list.ItemCount == 0);
            var noMatch = UiDrive.Find<TextBlock>(window, "NoMatchText");
            noMatch.IsVisible.Should().BeTrue();
            noMatch.Text.Should().Contain(Tr.T("Не найдено на этой кассе", "Бул кассада табылган жок", "Not found on this till", "Bu kasada bulunamadı", "Bu kassada topilmadi"));
            Save(window, "history-not-found-" + slug);

            UiDrive.Type(window, search, "237518");
            Wait(() => list.ItemCount == 1);
            list.SelectedIndex = 0;
            var talon = UiDrive.Find<Button>(window, "WarrantyCardButton");
            Wait(() => talon.IsVisible);
            talon.IsVisible.Should().BeTrue("у чека с серийным номером есть «Гарантийный талон»");
            Save(window, "history-found-" + slug);

            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
            TextClipChecker.Find(window, "electronics-history", langCode).Where(v => v.Detail.Contains("Warranty") || v.Detail.Contains("талон") || v.Detail.Contains("талону") || v.Detail.Contains("табылган") || v.Detail.Contains("найдено"))
                .Should().BeEmpty($"текст не обрезан ({tag})");

            // Чек без номеров — кнопки нет.
            search.Text = "";
            UiShot.Pump();
            Wait(() => list.ItemCount == 2);
            var visibleFor = new List<bool>();
            for (var idx = 0; idx < 2; idx++)
            {
                list.SelectedIndex = -1;
                UiShot.Pump();
                list.SelectedIndex = idx;
                UiShot.Settle(6);
                visibleFor.Add(talon.IsVisible);
            }
            visibleFor.Count(v => v).Should().Be(1, "талон есть только у чека с серийными номерами, у чехла — нет");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task History_in_other_spheres_has_no_serial_search_and_no_talon_button()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UiFake.Reset();
        PosApp.ActiveShiftId = null;
        var p = UserPreferences.Instance;
        p.MarketSphere = MarketSpheres.Grocery;
        p.MarketSphereDetail = null;
        var at = DateTime.Today.AddHours(11);
        UiFake.Server.Json("GET", "/api/main/pos/sales/", 200,
            "{\"count\":1,\"next\":null,\"previous\":null,\"results\":[" + Row("sale-g1", "000801", at, "Хлеб") + "]}");
        // Журнал лежит в файле (как после работы в «Электронике»), но в продуктовой сфере он не используется.
        SerialJournal.Append(new[] { new SerialRecord { SaleId = "sale-g1", ReceiptNo = "000801", SoldAt = at, Title = "Хлеб", Serial = ValidImei } });

        var window = new ReceiptHistoryWindow();
        try
        {
            UiShot.Render(window, new UiSize(1024, 600));
            var list = UiDrive.Find<ListBox>(window, "ReceiptsList");
            Wait(() => list.ItemCount == 1);
            UiDrive.Type(window, UiDrive.Find<TextBox>(window, "SearchBox"), "237518");
            Wait(() => list.ItemCount == 0);
            list.ItemCount.Should().Be(0, "в продуктовой сфере поиск по серийнику не включается");
            list.SelectedIndex = -1;
            UiDrive.Find<TextBox>(window, "SearchBox").Text = "";
            UiShot.Pump();
            Wait(() => list.ItemCount == 1);
            list.SelectedIndex = 0;
            UiShot.Pump();
            UiDrive.Find<Button>(window, "WarrantyCardButton").IsVisible.Should().BeFalse("кнопка талона только в «Электронике»");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────── ЭЛ5: срок обмена и возврата ─────────────

    private static void SetSphere(string baseSphere, string? detail)
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = baseSphere;
        p.MarketSphereDetail = detail;
    }

    [Fact]
    public void Exchange_term_applies_to_electronics_and_clothing_but_not_grocery()
    {
        var prefs = UserPreferences.Instance;
        var savedDays = prefs.ExchangeDaysLimit;
        try
        {
            prefs.ExchangeDaysLimit = 14;
            SetSphere(MarketSpheres.Grocery, ElectronicsProfile.Id);
            NonExchangeableRules.DaysLimit.Should().Be(14);
            NonExchangeableRules.IsOutOfTerm(DateTime.Today.AddDays(-10)).Should().BeFalse("10 дней назад — в срок");
            NonExchangeableRules.IsOutOfTerm(DateTime.Today.AddDays(-20)).Should().BeTrue("20 дней — срок прошёл, кассир увидит предупреждение");
            NonExchangeableRules.NonExchangeableCategoryOf("any").Should().BeNull("список «без обмена» — только у одежды");

            SetSphere("clothing", null);
            NonExchangeableRules.DaysLimit.Should().Be(14);
            NonExchangeableRules.IsOutOfTerm(DateTime.Today.AddDays(-20)).Should().BeTrue();

            foreach (var (b, d) in new[] { ("grocery", (string?)null), ("services", null), ("grocery", "hardware") })
            {
                SetSphere(b, d);
                NonExchangeableRules.DaysLimit.Should().Be(0, $"{b}/{d}: срок не проверяется, как раньше");
                NonExchangeableRules.IsOutOfTerm(DateTime.Today.AddDays(-400)).Should().BeFalse();
            }

            SetSphere(MarketSpheres.Grocery, ElectronicsProfile.Id);
            prefs.ExchangeDaysLimit = 30;
            NonExchangeableRules.DaysLimit.Should().Be(30, "срок — настройка, 14 только по умолчанию");
        }
        finally
        {
            prefs.ExchangeDaysLimit = savedDays;
        }
    }
}
