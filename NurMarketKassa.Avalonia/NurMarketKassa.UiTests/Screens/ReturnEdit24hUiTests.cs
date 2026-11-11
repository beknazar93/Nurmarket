using Avalonia;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
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

    /// <summary>ModalShell в кассе ограничивает высоту 85 % рабочей области ЭКРАНА (DialogScreenFit); экран Headless больше окна теста — ставим то же
    /// ограничение по размеру «экрана» теста. <paramref name="noScroll"/> — окно обязано поместиться целиком, без прокрутки (PIN-окно).</summary>
    private static void Fits(Window dialog, int width, int height, string screen, AppLanguage lang, bool noScroll = false)
    {
        var tag = $"{screen} {width}x{height} {lang}";
        var cap = Math.Floor(height * 0.85);
        if (noScroll)
            dialog.Bounds.Height.Should().BeLessThanOrEqualTo(cap + 32, $"окно помещается без прокрутки ({tag})");
        else if (dialog.Content is ModalShell shell)
        {
            shell.MaxHeight = cap;
            UiShot.Settle(3);
        }
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
            UiDrive.AllText(dialog).Should().Contain("3");
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
            dialog.TotalText.Should().Contain("370", "1 × 150 + 1,5 × 146,67 − 0,01 = 370");
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

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void HistoryDialog_FitsScreen_ShowsOriginalVersion(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var owner = new Window();
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
