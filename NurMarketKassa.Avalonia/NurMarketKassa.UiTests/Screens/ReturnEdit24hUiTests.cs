using Avalonia;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-10 (ТЗ «правка и аннулирование возвратных накладных 24 ч»): окна PIN руководителя, правки возврата поставщику и истории изменений
/// в Avalonia.Headless при 1024×600 и 1366×768 (ru, ky): окно не выше и не шире экрана, текст не обрезан, кнопки видны; PIN-окно — с сообщением
/// «неверный PIN» и с блокировкой (кнопки выключены); правка — сумма пересчитывается при вводе количества. Сервер не нужен (проверка PIN — заглушка).
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\ret24-{окно}-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class ReturnEdit24hUiTests
{
    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static string Slug(AppLanguage lang, int w, int h) => $"{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{w}x{h}";

    /// <summary>2026-11-12 (аудит 12.11, Т-11): экран Headless больше окна теста — подменяем рабочую область экрана (ModalShell.WorkingAreaForTests) ДО показа окна;
    /// высоту карточки ограничивает сам ModalShell (85 % экрана), тест её не выставляет.</summary>
    private static IDisposable FakeScreen(int width, int height)
    {
        ModalShell.WorkingAreaForTests = new Avalonia.Size(width, height);
        return new ScreenRestore();
    }

    private sealed class ScreenRestore : IDisposable
    {
        public void Dispose() => ModalShell.WorkingAreaForTests = null;
    }

    /// <summary>Окно помещается в «экран» теста (подменён FakeScreen). <paramref name="noScroll"/> — окно обязано поместиться целиком, без прокрутки (PIN-окно).</summary>
    private static void Fits(Window dialog, int width, int height, string screen, AppLanguage lang, bool noScroll = false)
    {
        var tag = $"{screen} {width}x{height} {lang}";
        var cap = Math.Floor(height * 0.85);
        if (noScroll)
            dialog.Bounds.Height.Should().BeLessThanOrEqualTo(cap + 32, $"окно помещается без прокрутки ({tag})");
        else if (dialog.Content is ModalShell shell)
            shell.Bounds.Height.Should().BeLessThanOrEqualTo(cap + 0.5, $"ModalShell сам ограничил карточку 85 % экрана ({tag})");
        dialog.Bounds.Height.Should().BeLessThanOrEqualTo(height, $"окно не выше экрана ({tag})");
        dialog.Bounds.Width.Should().BeLessThanOrEqualTo(width, tag);
        foreach (var b in dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Content is string { Length: 1 }))
        {
            // кнопки цифрового блока PIN — целиком внутри окна (не обрезаны справа)
            var right = b.TranslatePoint(new Avalonia.Point(b.Bounds.Width, 0), dialog);
            right!.Value.X.Should().BeLessThanOrEqualTo(dialog.Bounds.Width - 16 + 0.5, $"кнопка «{b.Content}» внутри окна ({tag})");
        }
        foreach (var b in dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("modal-primary")))
        {
            var p = b.TranslatePoint(new Avalonia.Point(0, b.Bounds.Height), dialog);
            p.Should().NotBeNull();
            p!.Value.Y.Should().BeLessThanOrEqualTo(dialog.Bounds.Height + 0.5, $"главная кнопка видна ({tag})");
        }
        TextClipChecker.Find(dialog, screen, lang.ToString()).Should().BeEmpty($"текст не обрезан ({tag})");
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task PinDialog_FitsScreen_WrongPinMessage_LockDisablesInput(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var owner = new Window();
        using var fakeScreen = FakeScreen(width, height); // аудит 12.11, Т-11: размер экрана подменён, карточку ограничивает сам ModalShell
        using var _ = UiShot.Render(owner, new UiSize(width, height));
        var dialog = new ManagerPinDialog(Tr.T("Удаление: возврат поставщику ВП-2026-000123", "Өчүрүү: жеткирүүчүгө кайтаруу ВП-2026-000123", "Deletion", "Silme", "O'chirish"),
            (_, _) => Task.FromResult(new ReturnConfirmResult(false, null, "", new ReturnEditException(ReturnEditFailure.PinInvalid, "pin_invalid", "") { AttemptsLeft = 3 })));
        try
        {
            var task = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(8);
            UiDrive.ButtonByText(dialog, "Подтвердить", "Ырастоо").IsEnabled.Should().BeFalse("пустой PIN не отправляется");
            foreach (var d in new[] { "7", "3", "9", "1" })
                UiDrive.Click(dialog, UiDrive.ButtonByText(dialog, d));
            UiShot.Settle(2);
            UiDrive.ButtonByText(dialog, "Подтвердить", "Ырастоо").IsEnabled.Should().BeTrue("PIN набран цифровым блоком");

            dialog.Apply(new ReturnConfirmResult(false, null, "", new ReturnEditException(ReturnEditFailure.PinInvalid, "pin_invalid", "") { AttemptsLeft = 3 }));
            UiShot.Settle(3);
            UiDrive.AllText(dialog).Should().Contain(ServerErrorCodes.PinInvalidText(3), "аудит 12.11, Т-7: полный текст «Осталось попыток: 3.», а не цифра где-нибудь в окне");
            Fits(dialog, width, height, "ret24-pin", lang, noScroll: true);
            SaveShot(dialog, "ret24-pin-wrong-" + Slug(lang, width, height));

            dialog.Apply(new ReturnConfirmResult(false, null, "", new ReturnEditException(ReturnEditFailure.PinLocked, "pin_locked", "") { RetryAfterSeconds = 900 }));
            UiShot.Settle(3);
            UiDrive.ButtonByText(dialog, "7").IsEffectivelyEnabled.Should().BeFalse("во время блокировки цифровой блок выключен");
            UiDrive.ButtonByText(dialog, "Подтвердить", "Ырастоо").IsEnabled.Should().BeFalse();
            Fits(dialog, width, height, "ret24-pin", lang, noScroll: true);
            SaveShot(dialog, "ret24-pin-locked-" + Slug(lang, width, height));

            dialog.Apply(new ReturnConfirmResult(true, "tok", "Айбек", null));
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
                UiShot.Settle(1, 20);
            (await task).Should().BeTrue();
            dialog.ConfirmToken.Should().Be("tok");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    private static SupplierReturnInfo Info() => SupplierApiService.ParseReturn(JsonDocument.Parse(
        "{\"id\":\"r1\",\"number_display\":\"ВП-2026-000123\",\"version\":3,\"status\":\"posted\",\"can_edit\":true,\"can_void\":true,"
        + "\"editable_until\":\"2026-11-11T08:00:00Z\",\"server_now\":\"2026-11-11T03:00:00Z\",\"total_amount\":\"520.00\",\"comment\":\"брак упаковки\","
        + "\"items\":[{\"id\":\"l1\",\"product_id\":\"p1\",\"product_name\":\"Молоко «Простоквашино» 3,2 % 1 л (длинное название для проверки)\",\"qty\":\"2\",\"unit\":\"шт\",\"purchase_price\":\"150\"},"
        + "{\"id\":\"l2\",\"product_id\":\"p2\",\"product_name\":\"Сыр российский\",\"qty\":\"1.5\",\"unit\":\"кг\",\"purchase_price\":\"146.67\",\"discount\":\"0.01\",\"note\":\"срок\"}]}").RootElement.Clone());

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void EditDialog_FitsScreen_TotalRecalculates(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var owner = new Window();
        using var fakeScreen = FakeScreen(width, height); // аудит 12.11, Т-11: размер экрана подменён, карточку ограничивает сам ModalShell
        using var _ = UiShot.Render(owner, new UiSize(width, height));
        var dialog = new SupplierReturnEditDialog(Info(), "ВП-2026-000123");
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(8);
            UiDrive.ButtonByText(dialog, "Сохранить", "Сактоо").IsVisible.Should().BeTrue();
            dialog.TotalText.Should().Contain("520");
            dialog.SetQtyForTests(0, "1");
            UiShot.Settle(2);
            dialog.TotalText.Should().StartWith(Tr.T("Сумма: ", "Суммасы: ", "Total: ", "Toplam: ", "Jami: ") + MoneyText.Som(370.01m), "аудит 12.11, Т-7: до копейки — 1 × 150 + 1,5 × 146,67 = 370,01 (2026-11-11: скидку строки сервер не хранит — не вычитается)");
            UiDrive.AllText(dialog).Should().NotContain(lang == AppLanguage.Kyrgyz ? "Арзандатуу" : "Скидка", "полей скидки и примечания строки больше нет");
            Fits(dialog, width, height, "ret24-edit", lang);
            SaveShot(dialog, "ret24-edit-" + Slug(lang, width, height));
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    // ───────────── 2026-11-11: возврат от клиента (сервер Умара) ─────────────

    private static List<CustomerReturnEditLine> CustomerLines() => CustomerReturnEdit.FromCards(
        JsonDocument.Parse("{\"id\":\"pr1\",\"sale\":\"s1\",\"version\":3,\"can_edit\":true,\"can_void\":true,\"amount\":\"350.00\","
            + "\"items\":[{\"sale_item_id\":\"si-1\",\"name\":\"Молоко «Простоквашино» 3,2 % 1 л (длинное название для проверки переноса)\",\"quantity\":\"2\",\"amount\":\"200.00\"},"
            + "{\"sale_item_id\":\"si-2\",\"name\":\"Сыр российский\",\"unit\":\"кг\",\"quantity\":\"1.5\",\"price\":\"100\"}]}").RootElement.Clone(),
        JsonDocument.Parse("{\"id\":\"s1\",\"items\":[{\"id\":\"si-1\",\"quantity\":\"5\",\"returnable_qty\":\"3\"},{\"id\":\"si-2\",\"quantity\":\"2\",\"returnable_qty\":\"0.5\"}]}").RootElement.Clone());

    private static ReturnDocRef CustomerDoc() => new(ReturnDocKind.Customer, "pr1", "№1115 · 11.11 10:05", 350,
        ReturnEditState.Parse(JsonDocument.Parse("{\"version\":3,\"can_edit\":true,\"can_void\":true,\"editable_until\":\"2026-11-12T08:00:00Z\",\"server_now\":\"2026-11-11T09:00:00Z\"}").RootElement.Clone()));

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task CustomerEditDialog_FitsScreen_MaxShown_TotalRecalculates_OverMaxRefused(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var owner = new Window();
        using var fakeScreen = FakeScreen(width, height); // аудит 12.11, Т-11: размер экрана подменён, карточку ограничивает сам ModalShell
        using var _ = UiShot.Render(owner, new UiSize(width, height));
        var dialog = new CustomerReturnEditDialog(CustomerDoc(), CustomerLines());
        try
        {
            var shown = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(8);
            UiDrive.ButtonByText(dialog, "Сохранить", "Сактоо").IsVisible.Should().BeTrue();
            UiDrive.AllText(dialog).Should().Contain(lang == AppLanguage.Kyrgyz ? "5 чейин" : "можно до 5", "максимум = можно вернуть по чеку (3) + уже в этом возврате (2)");
            dialog.TotalText.Should().StartWith(Tr.T("Сумма ≈ ", "Суммасы ≈ ", "Total ≈ ", "Toplam ≈ ", "Jami ≈ ") + MoneyText.Som(350.00m), "аудит 12.11, Т-7: до копейки");
            dialog.SetQtyForTests(0, "1");
            UiShot.Settle(2);
            dialog.TotalText.Should().StartWith(Tr.T("Сумма ≈ ", "Суммасы ≈ ", "Total ≈ ", "Toplam ≈ ", "Jami ≈ ") + MoneyText.Som(250.00m), "аудит 12.11, Т-7: до копейки — 1 × 100 + 1,5 × 100 = 250 (ориентировочно)");
            Fits(dialog, width, height, "ret24-customer-edit", lang);
            SaveShot(dialog, "ret24-customer-edit-" + Slug(lang, width, height));

            // больше, чем можно вернуть по чеку, — не отправляется (проверка до PIN и до сервера)
            dialog.SetQtyForTests(0, "6");
            dialog.SetReasonForTests("ошиблись количеством");
            await dialog.SaveForTestsAsync();
            UiShot.Settle(2);
            dialog.ErrorText.Should().Contain("5");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    private static void StubLogin(WireMock.Server.WireMockServer server) =>
        server.Given(WireMock.RequestBuilders.Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));

    private static void StubCaps(WireMock.Server.WireMockServer server, string json) =>
        server.Given(WireMock.RequestBuilders.Request.Create().WithPath("/api/main/capabilities/").UsingMethod("GET"))
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(json));

    /// <summary>Признак pos_return_edit_24h — как от сервера (GET capabilities у заглушки); после теста признаки снова пустые (и в файле последнего списка).</summary>
    private static async Task WithPosReturnFeatureAsync(Action body)
    {
        using var server = WireMock.Server.WireMockServer.Start();
        StubLogin(server);
        var api = new NurMarketApiClient(new NurMarketKassa.Configuration.AppSettings { ApiBaseUrl = server.Url! });
        await api.LoginAsync("a@b.kg", "12345678");
        StubCaps(server, "[\"pos_return_edit_24h\"]");
        await ServerCapabilities.LoadAsync(api, force: true);
        try
        {
            body();
        }
        finally
        {
            server.Reset();
            StubLogin(server);
            StubCaps(server, "[]");
            await ServerCapabilities.LoadAsync(api, force: true);
            ServerCapabilities.Reset();
        }
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    public async Task Bar_DebtSale_NoButtons_ReasonShown_EditableShowsButtons(AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        await WithPosReturnFeatureAsync(() =>
        {
            ServerCapabilities.Has(ServerCapabilities.PosReturnEdit24h).Should().BeTrue();
            var host = new Window { Width = 640, Height = 300 };
            var panel = new StackPanel();
            host.Content = panel;
            var debt = new ReturnDocRef(ReturnDocKind.Customer, "pr9", "№77", 120, ReturnEditState.Parse(JsonDocument.Parse(
                "{\"version\":2,\"can_edit\":false,\"can_void\":false,\"edit_block_reason\":\"debt_sale_not_supported\"}").RootElement.Clone()));
            panel.Children.Add(NurMarketKassa.AvaloniaHost.Services.ReturnEditFlows.CustomerBar(panel, debt, () => Task.CompletedTask));
            panel.Children.Add(new Separator());
            panel.Children.Add(NurMarketKassa.AvaloniaHost.Services.ReturnEditFlows.CustomerBar(panel, CustomerDoc(), () => Task.CompletedTask));
            using (UiShot.Render(host, new UiSize(640, 300)))
            {
                UiShot.Settle(4);
                var debtBar = (Control)panel.Children[0];
                debtBar.IsVisible.Should().BeTrue("у долгового чека видно пояснение");
                // «История» руководителю может быть (зависит от того, кто вошёл, — в общем прогоне другой тест мог войти владельцем); правки и удаления — нет
                debtBar.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string)
                    .Should().NotContain(new[] { "Редактировать", "Удалить", "Оңдоо", "Өчүрүү" }, "у долгового чека кнопок правки и удаления нет");
                var hint = string.Join(" ", debtBar.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
                hint.Should().Contain(lang == AppLanguage.Kyrgyz ? "Насыя чек" : "Чек в долг");
                var okBar = (Control)panel.Children[2];
                okBar.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string)
                    .Should().Contain(new[] { lang == AppLanguage.Kyrgyz ? "Оңдоо" : "Редактировать", lang == AppLanguage.Kyrgyz ? "Өчүрүү" : "Удалить" });
                SaveShot(host, "ret24-bar-debt-" + (lang == AppLanguage.Kyrgyz ? "ky" : "ru"));
            }
            host.Close();
            UiShot.Pump();
        });
    }

    [AvaloniaFact]
    public void PinDialog_PinNotSet_FinalMessage_InputOff()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var owner = new Window();
        using var fakeScreen = FakeScreen(1024, 600); // аудит 12.11, Т-11
        using var _ = UiShot.Render(owner, new UiSize(1024, 600));
        var dialog = new ManagerPinDialog("Изменение: возврат от клиента №1115", (_, _) => Task.FromResult(new ReturnConfirmResult(false, null, "", null)));
        try
        {
            var task = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(6);
            dialog.Apply(new ReturnConfirmResult(false, null, "", new ReturnEditException(ReturnEditFailure.PinNotSet, "pin_not_set", "")));
            UiShot.Settle(3);
            UiDrive.AllText(dialog).Should().Contain("не задан PIN");
            dialog.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled && b.Content as string == "7")
                .Should().BeFalse("повтор PIN бессмыслен — цифровой блок выключен и спрятан");
            UiDrive.ButtonByText(dialog, "Подтвердить").IsEnabled.Should().BeFalse();
            dialog.FinalError!.Failure.Should().Be(ReturnEditFailure.PinNotSet);
            Fits(dialog, 1024, 600, "ret24-pin", AppLanguage.Russian, noScroll: true);
            SaveShot(dialog, "ret24-pin-notset-ru-1024x600");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void HistoryDialog_FitsScreen_ShowsOriginalVersion(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var owner = new Window();
        using var fakeScreen = FakeScreen(width, height); // аудит 12.11, Т-11: размер экрана подменён, карточку ограничивает сам ModalShell
        using var _ = UiShot.Render(owner, new UiSize(width, height));
        var entries = ReturnEditApi.ParseHistory(JsonDocument.Parse(
            "[{\"version\":1,\"action\":\"created\",\"at\":\"2026-11-10T08:00:00Z\",\"user_name\":\"Кассир Айгуль\",\"total\":\"300\",\"snapshot\":{\"items\":[{\"product_name\":\"Молоко\",\"qty\":\"2\",\"purchase_price\":\"150\"}]}},"
            + "{\"version\":2,\"action\":\"edited\",\"at\":\"2026-11-10T10:00:00Z\",\"user_name\":\"Кассир Айгуль\",\"approved_by_name\":\"Айбек (владелец)\",\"reason\":\"ошиблись количеством\",\"total\":\"150\"},"
            + "{\"version\":3,\"action\":\"voided\",\"at\":\"2026-11-10T11:00:00Z\",\"user_name\":\"Админ\",\"approved_by_name\":\"Айбек (владелец)\",\"reason\":\"дубль возврата\",\"total\":\"0\"}]").RootElement.Clone());
        var dialog = new ReturnHistoryDialog("ВП-2026-000123", _ => Task.FromResult(entries));
        try
        {
            var shown = dialog.ShowDialog(owner);
            UiShot.Settle(8);
            var text = UiDrive.AllText(dialog);
            text.Should().Contain(lang == AppLanguage.Kyrgyz ? "баштапкы" : "исходная");
            text.Should().Contain("Айбек (владелец)");
            Fits(dialog, width, height, "ret24-history", lang);
            SaveShot(dialog, "ret24-history-" + Slug(lang, width, height));
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }
}
