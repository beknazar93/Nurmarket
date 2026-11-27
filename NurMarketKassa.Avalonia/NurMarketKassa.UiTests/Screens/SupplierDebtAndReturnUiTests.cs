using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (два замечания владельца по поставщикам): (1) «после возврата товара поставщику долг не минусуется» — в окне «Новый возврат поставщику» при долге у поставщика по умолчанию
/// «Зачесть в долг перед поставщиком», подсказка по долгу (нет долга / возврат больше долга), «Долг перед поставщиком: было … → стало …» после оформления; (2) «Поставщиктин карызын төлөгөндө
/// жарымын же 20% бере албайт экенсиң» — окно оплаты долга поставщику: сумма от 0,01 до остатка, «Весь долг / 50 % / 20 %», остаток после оплаты, способ оплаты.
/// Всё в Avalonia.Headless против поддельного сервера (WireMock).</summary>
[Collection("UiScenarios")]
public sealed class SupplierDebtAndReturnUiTests : IDisposable
{
    public SupplierDebtAndReturnUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
    }

    public void Dispose() => ModalShell.WorkingAreaForTests = null;

    private static Window Owner(int width, int height)
    {
        var owner = new Window();
        using var _ = UiShot.Render(owner, new UiSize(width, height));
        owner.Show();
        return owner;
    }

    private static async Task LoginAsync() =>
        await TestHost.Services.GetRequiredService<NurMarketApiClient>().LoginAsync("owner@test.kg", "12345678");

    private static void WaitFor(Func<bool> condition, int ms = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < deadline)
            UiShot.Settle(1, 30);
    }

    /// <summary>Кнопка-чип по надписи, как её видит кассир (без имени для диктора, которое могут переименовать другие тесты).</summary>
    private static Button ChipByLabel(Window w, string label) =>
        w.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && UiDrive.LabelOf(b) is { } t && t.Contains(label, StringComparison.OrdinalIgnoreCase));

    /// <summary>Пробелы между разрядами в ru-RU (неразрывные) → обычные: проверка не зависит от версии ICU.</summary>
    private static string Plain(string text) => text.Replace(' ', ' ').Replace(' ', ' ');

    public static IEnumerable<object[]> Screens() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    // ------------------------------------------------------------------ оплата долга поставщику: любая сумма

    [AvaloniaFact]
    public async Task PayDialog_QuickButtons_FillAmount_AndShowTheRemainingDebt()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var owner = Owner(1366, 768);
        var dialog = new SupplierDebtPayDialog("ОсОО «Поставщик»", "взнос №2", 1000, "Остаток взноса");
        try
        {
            ModalShell.WorkingAreaForTests = new Size(1366, 768);
            _ = dialog.ShowDialog<SupplierDebtPayChoice?>(owner);
            UiShot.Settle(6);
            dialog.AmountTextForTests.Should().Be("1000,00", "по умолчанию — весь остаток (как раньше), но поле можно менять");
            dialog.CanPayForTests.Should().BeTrue();
            Plain(dialog.PreviewForTests).Should().Contain("полностью");

            UiDrive.Click(dialog, UiDrive.Find<Button>(dialog, "SupplierPayQuick50"));
            dialog.AmountTextForTests.Should().Be("500,00", "«50 %» — половина долга");
            Plain(dialog.PreviewForTests).Should().Contain("Останется долг: 500,00");
            dialog.ChoiceForTests.Should().Be(new SupplierDebtPayChoice(500, false, "cash"));

            UiDrive.Click(dialog, UiDrive.Find<Button>(dialog, "SupplierPayQuick20"));
            dialog.AmountTextForTests.Should().Be("200,00", "«20 %» — пятая часть");
            Plain(dialog.PreviewForTests).Should().Contain("Останется долг: 800,00");

            UiDrive.Click(dialog, UiDrive.Find<Button>(dialog, "SupplierPayQuick100"));
            dialog.AmountTextForTests.Should().Be("1000,00");
            dialog.ChoiceForTests!.IsFull.Should().BeTrue("«Весь долг» закрывает остаток целиком");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData("1500", false)]   // больше долга
    [InlineData("1000,01", false)]
    [InlineData("0", false)]      // меньше копейки
    [InlineData("-5", false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("0,01", true)]    // наименьшая сумма
    [InlineData("333,33", true)]
    [InlineData("1000", true)]
    public async Task PayDialog_AmountOutsideTheDebt_DisablesPay(string typed, bool ok)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var owner = Owner(1366, 768);
        var dialog = new SupplierDebtPayDialog("ОсОО «Поставщик»", "весь долг", 1000, "Долг к оплате");
        try
        {
            ModalShell.WorkingAreaForTests = new Size(1366, 768);
            _ = dialog.ShowDialog<SupplierDebtPayChoice?>(owner);
            UiShot.Settle(4);
            dialog.SetAmountForTests(typed);
            UiShot.Settle(2);
            dialog.CanPayForTests.Should().Be(ok, $"«{typed}» из 1000: «Оплатить» {(ok ? "доступна" : "выключена")}");
            if (!ok)
                dialog.PreviewForTests.Should().NotBeNullOrWhiteSpace("причина — текстом под полем");
            if (typed == "1500")
                Plain(dialog.PreviewForTests).Should().Contain("больше долга").And.Contain("1 000,00");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task PayDialog_Pay_ReturnsAmountPartAndMethod_AndCancelReturnsNull()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Kyrgyz);
        var owner = Owner(1366, 768);
        var dialog = new SupplierDebtPayDialog("ОсОО «Поставщик»", "взнос №1", 400, "Остаток взноса");
        try
        {
            ModalShell.WorkingAreaForTests = new Size(1366, 768);
            var result = dialog.ShowDialog<SupplierDebtPayChoice?>(owner);
            UiShot.Settle(6);
            UiDrive.Click(dialog, UiDrive.Find<Button>(dialog, "SupplierPayQuick50"));
            UiDrive.Click(dialog, UiDrive.Find<Button>(dialog, "SupplierPayMethod_transfer"));
            UiDrive.Click(dialog, UiDrive.Find<Button>(dialog, "SupplierPayConfirm"));
            var choice = await result;
            choice.Should().Be(new SupplierDebtPayChoice(200, false, "transfer"), "половина взноса, безнал");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }

        var owner2 = Owner(1366, 768);
        var cancelled = new SupplierDebtPayDialog("ОсОО «Поставщик»", "взнос №1", 400, "Остаток взноса");
        try
        {
            var result = cancelled.ShowDialog<SupplierDebtPayChoice?>(owner2);
            UiShot.Settle(6);
            cancelled.Close(null);
            (await result).Should().BeNull("«Отмена» — оплаты нет");
        }
        finally
        {
            owner2.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Screens))]
    public async Task PayDialog_Fits_NoClippedText(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var owner = Owner(width, height);
        var dialog = new SupplierDebtPayDialog("ОсОО «Поставщик строительных материалов»", "взнос №2 по долгу «Закупка 12.11.2026», срок 30.11.2026", 23400.5, "Остаток взноса",
            "Долг без графика (1 200,00 сом) сюда не входит — его гасят на сайте NurCRM.");
        try
        {
            ModalShell.WorkingAreaForTests = new Size(width, height);
            _ = dialog.ShowDialog<SupplierDebtPayChoice?>(owner);
            UiShot.Settle(6);
            dialog.PickForTests(50);
            UiShot.Settle(3);
            (dialog.Content as ModalShell)!.Bounds.Height.Should().BeLessThanOrEqualTo(Math.Floor(height * 0.85) + 0.5, "окно не выше 85 % экрана");
            foreach (var b in dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
            {
                var p = b.TranslatePoint(new Point(b.Bounds.Width, b.Bounds.Height), dialog)!.Value;
                p.X.Should().BeLessThanOrEqualTo(dialog.Bounds.Width + 0.5, $"кнопка «{UiDrive.TextOf(b)}» внутри окна");
            }
            TextClipChecker.Find(dialog, "supplier-debt-pay", lang.ToString()).Should().BeEmpty();
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ «Новый возврат поставщику»: долг и зачёт

    [AvaloniaFact]
    public async Task ReturnDialog_DebtExists_DefaultIsOffset_NoDebtDefaultIsCash_PickedChoiceIsKept()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var owner = Owner(1366, 768);
        var dialog = new SupplierReturnDialog();
        try
        {
            ModalShell.WorkingAreaForTests = new Size(1366, 768);
            _ = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(6);
            dialog.CompensationForTests.Should().Be("cash", "поставщик не выбран — как раньше");

            dialog.SetSupplierDebtForTests(1500);
            UiShot.Settle(3);
            dialog.CompensationForTests.Should().Be("debt_offset", "у поставщика есть долг — по умолчанию «Зачесть в долг перед поставщиком»");
            Plain(dialog.DebtNoteForTests).Should().Contain("Долг перед поставщиком: 1 500,00");
            UiDrive.AllText(dialog).Should().Contain("Зачесть в долг перед поставщиком").And.Contain("Вернуть деньги в кассу", "оба выбора видны явно");

            dialog.SetSupplierDebtForTests(0);
            UiShot.Settle(3);
            dialog.CompensationForTests.Should().Be("cash", "долга нет — деньги в кассу");
            dialog.DebtNoteForTests.Should().Contain("Долга перед этим поставщиком нет");

            // кассир сам выбрал «в кассу» — автовыбор по долгу его выбор не трогает, а подсказка напоминает, что долг не изменится
            UiDrive.Click(dialog, ChipByLabel(dialog, "Вернуть деньги в кассу"));
            dialog.SetSupplierDebtForTests(2000);
            UiShot.Settle(3);
            dialog.CompensationForTests.Should().Be("cash", "выбор кассира сохраняется");
            dialog.DebtNoteForTests.Should().Contain("долг поставщику не изменится");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task ReturnDialog_ReturnOverDebt_ExplainsOffsetCannotExceedDebt_AndNoDebtOffsetWarns()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UiFake.Server.Json("GET", "/api/main/clients/", 200, "{\"results\":[{\"id\":\"sup-1\",\"full_name\":\"Поставщик А\"}]}");
        UiFake.Server.Json("GET", "/api/main/suppliers/sup-1/products/", 200, "[{\"id\":\"p-1\",\"name\":\"Подшипник\",\"unit\":\"шт\",\"purchase_price\":300,\"quantity\":5}]");
        UiFake.Server.Json("GET", "/api/main/clients/sup-1/kpis/", 200, "{\"debt\":{\"amount\":\"400.00\",\"count\":1},\"prepayment\":{\"amount\":\"0\",\"count\":0},\"sale\":{\"amount\":\"0\",\"count\":0}}");
        await LoginAsync();
        var owner = Owner(1366, 768);
        var dialog = new SupplierReturnDialog();
        try
        {
            dialog.ApplyPreset([("p-1", "Подшипник", 2)], "defect"); // «Со склада», 2 × 300 = 600 при долге 400
            ModalShell.WorkingAreaForTests = new Size(1366, 768);
            _ = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(6);
            var supplier = dialog.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible);
            WaitFor(() => supplier.ItemCount > 0);
            supplier.SelectedIndex = 0;
            WaitFor(() => dialog.DebtNoteForTests.Contains("больше долга") && dialog.GetVisualDescendants().OfType<TextBox>().Any(t => t.Text == "2"), 6000);
            UiShot.Settle(4);
            dialog.CompensationForTests.Should().Be("debt_offset", "долг 400 прочитан с сервера (kpis) — по умолчанию зачёт");
            var note = Plain(dialog.DebtNoteForTests);
            note.Should().Contain("Долг перед поставщиком: 400,00").And.Contain("больше долга", "возврат 600 больше долга 400")
                .And.Contain("не больше 400,00", "в долг зачтётся не больше долга");

            // кассир сам выбрал «Зачесть…», а долга уже нет — подсказка «зачесть не в чем» (красная), вид его выбора сохраняется
            var offsetChip = ChipByLabel(dialog, "Зачесть в долг перед поставщиком");
            offsetChip.BringIntoView(); // со строками возврата форма длиннее окна — чип сначала прокручиваем в видимую часть
            UiShot.Settle(3);
            UiDrive.Click(dialog, offsetChip);
            dialog.SetSupplierDebtForTests(0);
            UiShot.Settle(3);
            dialog.CompensationForTests.Should().Be("debt_offset", "вид выбрал кассир — автовыбор его не меняет");
            Plain(dialog.DebtNoteForTests).Should().Contain("Долга перед этим поставщиком нет").And.Contain("Зачесть не в чем");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task ReturnDialog_AfterOffsetReturn_ShowsDebtWasAndNow_FromServer()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var scenario = "supret-" + Guid.NewGuid().ToString("N")[..6];
        UiFake.Server.Json("GET", "/api/main/clients/", 200, "{\"results\":[{\"id\":\"sup-1\",\"full_name\":\"Поставщик А\"}]}");
        UiFake.Server.Json("GET", "/api/main/suppliers/sup-1/products/", 200, "[{\"id\":\"p-1\",\"name\":\"Подшипник\",\"unit\":\"шт\",\"purchase_price\":300,\"quantity\":5}]");
        // до возврата долг 1000 (обычная заглушка), после — 400: сервер зачёл 600. Состояние сценария «after» ставит POST возврата; пока его не было, заглушка «after» не подходит.
        UiFake.Server.Server.Given(Request.Create().WithPath("/api/main/clients/sup-1/kpis/").UsingGet()).AtPriority(5)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"debt\":{\"amount\":\"1000.00\",\"count\":1}}"));
        UiFake.Server.Server.Given(Request.Create().WithPath("/api/main/clients/sup-1/kpis/").UsingGet()).InScenario(scenario).WhenStateIs("after").AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"debt\":{\"amount\":\"400.00\",\"count\":1}}"));
        UiFake.Server.Server.Given(Request.Create().WithPath("/api/main/suppliers/sup-1/returns/").UsingPost()).InScenario(scenario).WillSetStateTo("after").AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(201).WithHeader("Content-Type", "application/json").WithBody("{\"id\":\"ret-1\"}"));
        await LoginAsync();
        var owner = Owner(1366, 768);
        var dialog = new SupplierReturnDialog();
        try
        {
            dialog.ApplyPreset([("p-1", "Подшипник", 2)], "defect"); // 2 × 300 = 600 из долга 1000
            ModalShell.WorkingAreaForTests = new Size(1366, 768);
            _ = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(6);
            var supplier = dialog.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible);
            WaitFor(() => supplier.ItemCount > 0);
            supplier.SelectedIndex = 0;
            WaitFor(() => dialog.DebtNoteForTests.Length > 0 && dialog.GetVisualDescendants().OfType<TextBox>().Any(t => t.Text == "2"), 5000);
            UiShot.Settle(4);
            dialog.CompensationForTests.Should().Be("debt_offset");
            Plain(dialog.DebtNoteForTests).Should().Contain("Долг уменьшится на 600,00").And.Contain("станет 400,00");

            UiDrive.Click(dialog, ChipByLabel(dialog, "Оформить возврат"));
            WaitFor(() => dialog.DebtResultForTests.Length > 0, 8000);
            var result = Plain(dialog.DebtResultForTests);
            result.Should().Contain("было 1 000,00").And.Contain("стало 400,00", "после возврата долг перечитан с сервера: было → стало");
            dialog.DebtNoteForTests.Should().BeEmpty("после оформления подсказка заменена итогом");
            UiFake.Server.Server.LogEntries.Count(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path == "/api/main/suppliers/sup-1/returns/").Should().Be(1);
            var body = UiFake.Server.Server.LogEntries.First(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path == "/api/main/suppliers/sup-1/returns/").RequestMessage.Body!;
            body.Should().Contain("\"compensation\":\"debt_offset\"", "в запросе — зачёт в долг, а не деньги в кассу");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task ReturnDialog_DebtResult_ColorsAndTexts_ForReducedCashAndNotReduced()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var owner = Owner(1366, 768);
        var dialog = new SupplierReturnDialog();
        try
        {
            ModalShell.WorkingAreaForTests = new Size(1366, 768);
            _ = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(4);
            dialog.ShowDebtResultForTests(1000, 700, "debt_offset");
            Plain(dialog.DebtResultForTests).Should().Contain("было 1 000,00").And.Contain("стало 700,00").And.NotContain("не уменьшился");
            dialog.ShowDebtResultForTests(1000, 1000, "cash");
            Plain(dialog.DebtResultForTests).Should().Contain("долг не менялся", "деньги в кассу — долг и не должен меняться");
            dialog.ShowDebtResultForTests(1000, 1000, "debt_offset");
            Plain(dialog.DebtResultForTests).Should().Contain("Долг не уменьшился", "зачёт, а долг прежний — честное предупреждение");
            dialog.ShowDebtResultForTests(null, 700, "debt_offset");
            // долг «до» неизвестен — прежний итог остаётся, ничего не выдумываем
            Plain(dialog.DebtResultForTests).Should().Contain("Долг не уменьшился");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Screens))]
    public async Task ReturnDialog_WithDebtNoteAndResult_Fits_NoClippedText(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var owner = Owner(width, height);
        var dialog = new SupplierReturnDialog();
        try
        {
            ModalShell.WorkingAreaForTests = new Size(width, height);
            _ = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(6);
            dialog.SetSupplierDebtForTests(1500);
            UiShot.Settle(3);
            (dialog.Content as ModalShell)!.Bounds.Height.Should().BeLessThanOrEqualTo(Math.Floor(height * 0.85) + 0.5, "ModalShell сам ограничил высоту 85 % экрана");
            TextClipChecker.Find(dialog, "supret-create-debt-note", lang.ToString()).Should().BeEmpty();
            dialog.ShowDebtResultForTests(1500, 1500, "debt_offset");
            UiShot.Settle(3);
            TextClipChecker.Find(dialog, "supret-create-debt-result", lang.ToString()).Should().BeEmpty();
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task ReturnDialog_ReasonOther_WithoutComment_IsStoppedBeforeSending()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UiFake.Server.Json("GET", "/api/main/clients/", 200, "{\"results\":[{\"id\":\"sup-1\",\"full_name\":\"Поставщик А\"}]}");
        UiFake.Server.Json("GET", "/api/main/suppliers/sup-1/products/", 200, "[{\"id\":\"p-1\",\"name\":\"Подшипник\",\"unit\":\"шт\",\"purchase_price\":300,\"quantity\":5}]");
        UiFake.Server.Json("POST", "/api/main/suppliers/sup-1/returns/", 201, "{\"id\":\"ret-1\"}");
        await LoginAsync();
        var owner = Owner(1366, 768);
        var dialog = new SupplierReturnDialog();
        try
        {
            dialog.ApplyPreset([("p-1", "Подшипник", 1)], "other"); // «Со склада», причина «Другое», комментария нет
            ModalShell.WorkingAreaForTests = new Size(1366, 768);
            _ = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(6);
            var supplier = dialog.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible);
            WaitFor(() => supplier.ItemCount > 0);
            supplier.SelectedIndex = 0;
            WaitFor(() => dialog.GetVisualDescendants().OfType<TextBox>().Any(t => t.Text == "1"), 5000);
            UiShot.Settle(4);
            UiDrive.Click(dialog, ChipByLabel(dialog, "Оформить возврат"));
            UiShot.Settle(6);
            UiDrive.AllText(dialog).Should().Contain("Причина «Другое» — напишите комментарий", "сервер требует комментарий при причине «Другое» — говорим сразу");
            UiFake.Server.Server.LogEntries.Count(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.EndsWith("/returns/")).Should().Be(0, "без комментария возврат не отправляется");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ карточка поставщика: «Погасить весь долг» — половина

    [AvaloniaFact]
    public async Task Card_PayAll_HalfOfTheDebt_IsOneRequestToPaySupplierDebt()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u1\",\"email\":\"owner@test.kg\",\"full_name\":\"Владелец\",\"role\":\"owner\",\"can_view_settings\":true}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u1\",\"email\":\"owner@test.kg\",\"full_name\":\"Владелец\",\"role\":\"owner\",\"can_view_settings\":true,\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        UiFake.Server.Json("GET", "/api/main/clients/sup-1/kpis/", 200, "{\"debt\":{\"amount\":\"1000.00\",\"count\":1},\"prepayment\":{\"amount\":\"0\",\"count\":0},\"sale\":{\"amount\":\"0\",\"count\":0}}");
        UiFake.Server.Json("GET", "/api/main/clients/sup-1/deals/", 200,
            "{\"count\":1,\"results\":[{\"id\":\"d-1\",\"title\":\"Долги Поставщик А\",\"kind\":\"debt\",\"debt_amount\":\"1000.00\",\"remaining_debt\":\"1000.00\",\"installments\":["
            + "{\"id\":\"i-1\",\"number\":1,\"due_date\":\"2026-12-01\",\"amount\":\"600.00\",\"paid_amount\":\"0\",\"remaining_for_period\":\"600.00\"},"
            + "{\"id\":\"i-2\",\"number\":2,\"due_date\":\"2026-12-15\",\"amount\":\"400.00\",\"paid_amount\":\"0\",\"remaining_for_period\":\"400.00\"}]}]}");
        UiFake.Server.Json("POST", "/api/main/clients/sup-1/pay-supplier-debt/", 200, "{\"paid\":\"500.00\",\"left\":\"500.00\",\"applied\":[],\"cashflows\":[]}");
        var api = TestHost.Services.GetRequiredService<NurMarketApiClient>();
        await api.LoginAsync("owner@test.kg", "12345678");

        var info = new SupplierInfo("sup-1", "Поставщик А", "", "", "", "", "", "", "", "", null);
        var host = new Window();
        using var _ = UiShot.Render(host, new UiSize(1024, 600));
        host.Content = new SupplierCardView(new SupplierApiService(api), info, null, new[] { info }, () => { }, _ => { });
        host.Show();
        UiRobot.Reset(host);
        UiRobot.WhenOpened<SupplierDebtPayDialog>(d =>
        {
            d.ChoiceForTests.Should().Be(new SupplierDebtPayChoice(1000, true, "cash"), "по умолчанию в окне весь долг — как раньше, но сумма редактируется");
            d.PickForTests(50);
            UiDrive.Click(d, UiDrive.Find<Button>(d, "SupplierPayConfirm"));
        });
        try
        {
            UiRobot.WaitFor(() => host.GetVisualDescendants().OfType<Button>().Any(b => UiDrive.LabelOf(b) == "Долги"), 6000).Should().BeTrue("карточка загрузилась");
            UiDrive.Click(host, ChipByLabel(host, "Долги"));
            UiShot.Settle(4);
            UiDrive.Click(host, ChipByLabel(host, "Погасить весь долг"));
            UiRobot.WaitFor(() => UiFake.Server.Server.LogEntries.Any(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.EndsWith("/pay-supplier-debt/")), 8000).Should().BeTrue("оплата ушла на сервер");
            UiRobot.Problems.Should().BeEmpty();
            var posts = UiFake.Server.Server.LogEntries.Where(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.Contains("/pay")).ToList();
            posts.Should().ContainSingle("половина долга — один запрос pay-supplier-debt, а не по взносам").Which.RequestMessage.Path.Should().Be("/api/main/clients/sup-1/pay-supplier-debt/");
            var body = posts[0].RequestMessage.Body!;
            body.Should().Contain("\"amount\":\"500.00\"", "50 % от 1000").And.Contain("\"method\":\"cash\"").And.Contain("idempotency_key");
            posts[0].RequestMessage.Headers!.Should().ContainKey("Idempotency-Key");
        }
        finally
        {
            UiRobot.CloseAll();
            host.Close();
            UiShot.Pump();
        }
    }
}
