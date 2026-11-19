using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-19 (склад «10 из 10», К-4/К-5/К-8): окна «Партии» товара, «Резервы» (с 409 insufficient_available и «Всё равно зарезервировать»), «Срок годности и партия» строки прихода
/// и предупреждение кассы о просрочке — на ru / ky / uz при 1024×600 и 1366×768: окно в экране, текст не обрезан, кнопки видны. Сервер не нужен (заглушки загрузки).
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\stock-{окно}-{язык}-{ширина}x{высота}.png (не эталоны).</summary>
[Collection("UiScenarios")]
public sealed class StockBatchesUiTests
{
    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static string Slug(AppLanguage lang, int w, int h) => $"{lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" }}-{w}x{h}";

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static IDisposable FakeScreen(int width, int height)
    {
        ModalShell.WorkingAreaForTests = new Avalonia.Size(width, height);
        return new ScreenRestore();
    }

    private sealed class ScreenRestore : IDisposable
    {
        public void Dispose() => ModalShell.WorkingAreaForTests = null;
    }

    private static void Fits(Window dialog, int width, int height, string screen, AppLanguage lang)
    {
        var tag = $"{screen} {width}x{height} {lang}";
        if (dialog.Content is ModalShell shell)
            shell.Bounds.Height.Should().BeLessThanOrEqualTo(Math.Floor(height * 0.85) + 0.5, $"ModalShell ограничил карточку 85 % экрана ({tag})");
        dialog.Bounds.Height.Should().BeLessThanOrEqualTo(height, $"окно не выше экрана ({tag})");
        dialog.Bounds.Width.Should().BeLessThanOrEqualTo(width, $"окно не шире экрана ({tag})");
        foreach (var b in dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("modal-primary")))
        {
            var p = b.TranslatePoint(new Avalonia.Point(0, b.Bounds.Height), dialog);
            p!.Value.Y.Should().BeLessThanOrEqualTo(dialog.Bounds.Height + 0.5, $"главная кнопка видна ({tag})");
        }
        TextClipChecker.Find(dialog, screen, lang.ToString()).Should().BeEmpty($"текст не обрезан ({tag})");
    }

    private static List<StockBatch> Sample() =>
    [
        new("b1", 20, 6, 10.5, DateTime.Today.AddDays(-9), null, "A-1", "ОсОО «Ала-Тоо Сүт»", DateTime.Today.AddDays(-40), ""),
        new("b2", 15, 15, 12, DateTime.Today.AddDays(12), null, "B-7", "Поставщик 2", DateTime.Today.AddDays(-10), ""),
        new("b3", 8, 8, 11, null, null, "", "", DateTime.Today.AddDays(-3), ""),
    ];

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void ProductBatchesWindow_ShowsBatches_WithStates_AndFits(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var owner = new Window();
        using var fake = FakeScreen(width, height);
        using var shotHost = UiShot.Render(owner, new UiSize(width, height));
        var dialog = new ProductBatchesWindow("Йогурт «Нурсүт» клубника 200 г", "шт", true, _ => Task.FromResult(Sample()));
        try
        {
            _ = dialog.ShowDialog(owner);
            UiShot.Settle(8);
            dialog.RowsPanel.Children.Should().HaveCount(3);
            var text = UiDrive.AllText(dialog);
            text.Should().Contain("A-1").And.Contain("B-7");
            text.Should().Contain(ServerStock.BatchState(Sample()[0], DateTime.Today), "просроченная партия помечена");
            text.Should().Contain(ServerStock.BatchState(Sample()[1], DateTime.Today), "скоро истекает помечена");
            // сортировка по сроку: просроченная — первой, без срока — последней
            dialog.RowsPanel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => t is "A-1" or "B-7").Should().Equal("A-1", "B-7");
            Fits(dialog, width, height, "stock-batches", lang);
            SaveShot(dialog, "stock-batches-" + Slug(lang, width, height));
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void ProductBatchesWindow_EmptyAndError_ShowMessages()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var owner = new Window();
        using var shotHost = UiShot.Render(owner, new UiSize(1024, 600));
        var empty = new ProductBatchesWindow("Товар", "шт", false, _ => Task.FromResult(new List<StockBatch>()));
        _ = empty.ShowDialog(owner);
        UiShot.Settle(6);
        UiDrive.AllText(empty).Should().Contain("Партий нет");
        empty.Close();
        var failing = new ProductBatchesWindow("Товар", "шт", false, _ => throw new InvalidOperationException("нет связи"));
        _ = failing.ShowDialog(owner);
        UiShot.Settle(6);
        UiDrive.AllText(failing).Should().Contain("Не удалось загрузить партии");
        failing.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void ReservationsWindow_409_OffersReserveAnyway_AndReleases(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var owner = new Window();
        using var fake = FakeScreen(width, height);
        using var shotHost = UiShot.Render(owner, new UiSize(width, height));
        var reserved = new List<(string Product, double Qty, bool AllowOver, string Key)>();
        var released = new List<string>();
        var rows = new List<StockReservation> { new("r1", "p1", "Молоко «Айналайын» 1 л", 3, "для Аиды", DateTime.Today.AddDays(5), DateTime.Today, "active") };
        var ops = new ReservationsWindow.Ops(
            _ => Task.FromResult(rows.ToList()),
            (product, qty, comment, until, allowOver, key) =>
            {
                reserved.Add((product, qty, allowOver, key));
                if (!allowOver)
                    throw new ApiException("conflict", 409, JsonDocument.Parse("{\"code\":\"insufficient_available\",\"available\":\"1\"}").RootElement.Clone());
                rows.Add(new StockReservation("r2", product, "Молоко «Айналайын» 1 л", qty, comment, until, DateTime.Today, "active"));
                return Task.CompletedTask;
            },
            id => { released.Add(id); rows.RemoveAll(r => r.Id == id); return Task.CompletedTask; },
            _ => Array.Empty<CatalogProductTileVm>());
        var product = new CatalogProductTileVm("p1", "Молоко «Айналайын» 1 л", "60.00 сом", false);
        var dialog = new ReservationsWindow(ops, product);
        try
        {
            _ = dialog.ShowDialog(owner);
            UiShot.Settle(8);
            dialog.RowsPanel.Children.Should().HaveCount(1, "резерв из списка сервера");
            dialog.ForceButton.IsVisible.Should().BeFalse("«Всё равно» появляется только после отказа сервера");

            UiDrive.Find<TextBox>(dialog, "ReserveQty").Text = "5";
            UiDrive.Click(dialog, UiDrive.Find<Button>(dialog, "ReserveSubmit"));
            UiShot.Settle(6);
            dialog.ForceButton.IsVisible.Should().BeTrue("409 insufficient_available — предложено «Всё равно зарезервировать»");
            dialog.Message.IsVisible.Should().BeTrue();
            dialog.Message.Text.Should().Contain("1", "сообщение называет, сколько свободно");
            reserved.Should().ContainSingle().Which.AllowOver.Should().BeFalse();
            Fits(dialog, width, height, "stock-reservations", lang);
            SaveShot(dialog, "stock-reservations-409-" + Slug(lang, width, height));

            UiDrive.Click(dialog, dialog.ForceButton);
            UiShot.Settle(6);
            reserved.Should().HaveCount(2);
            reserved[1].AllowOver.Should().BeTrue();
            reserved[1].Key.Should().NotBe(reserved[0].Key, "«Всё равно» — отдельный запрос с новым ключом повтора");
            dialog.RowsPanel.Children.Should().HaveCount(2, "список обновился");

            var release = dialog.RowsPanel.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Tag, "reservation-release"));
            UiDrive.Click(dialog, release);
            UiShot.Settle(6);
            released.Should().ContainSingle();
            dialog.RowsPanel.Children.Should().HaveCount(1);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void ReceivingBatchDialog_SetsExpiryAndLot_AndFits(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var owner = new Window();
        using var fake = FakeScreen(width, height);
        using var shotHost = UiShot.Render(owner, new UiSize(width, height));
        var line = new ReceivingLineVm { ProductName = "Йогурт «Нурсүт» клубника 200 г" };
        var dialog = new ReceivingBatchDialog(line);
        try
        {
            var task = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(8);
            UiDrive.Type(dialog, UiDrive.Find<TextBox>(dialog, "BatchExpires"), "01.12.2026");
            UiDrive.Type(dialog, UiDrive.Find<TextBox>(dialog, "BatchLotNo"), "A-12");
            Fits(dialog, width, height, "stock-receiving-batch", lang);
            SaveShot(dialog, "stock-receiving-batch-" + Slug(lang, width, height));
            UiDrive.Click(dialog, UiDrive.Find<Button>(dialog, "BatchSave"));
            UiShot.Settle(4);
            line.ExpiresAt.Should().Be(new DateTime(2026, 12, 1));
            line.LotNo.Should().Be("A-12");
            ReceivingBatchDialog.Summary(line).Should().Contain("01.12.2026").And.Contain("A-12");
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void CashierExpiredWarning_TextsFit_OnThreeLanguages(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var owner = new Window();
        using var shotHost = UiShot.Render(owner, new UiSize(width, height));
        var date = DateTime.Today.AddDays(-5);
        var title = Tr.T("Товар просрочен", "Товардын мөөнөтү өткөн", "Product expired", "Ürünün süresi geçmiş", "Mahsulot muddati o'tgan");
        var warn = new PosConfirmDialog(title, ServerStock.ExpiredSaleText("Йогурт «Нурсүт» клубника 200 г", date),
            Tr.T("Продать всё равно", "Баары бир сатуу", "Sell anyway", "Yine de sat", "Baribir sotish"),
            Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), PosConfirmAccent.Danger);
        try
        {
            _ = warn.ShowDialog(owner);
            UiShot.Settle(6);
            UiDrive.AllText(warn).Should().Contain(date.ToString("dd.MM.yyyy"), "в предупреждении — дата срока");
            TextClipChecker.Find(warn, "stock-expired-warn", lang.ToString()).Should().BeEmpty($"текст не обрезан ({lang} {width}x{height})");
            SaveShot(warn, "stock-expired-warn-" + Slug(lang, width, height));
        }
        finally
        {
            warn.Close();
        }
        var over = new PosConfirmDialog(Tr.T("Товар в резерве", "Товар резервде", "Goods on reserve", "Ürün rezervde", "Tovar zaxirada"),
            ServerStock.OverAvailableText("Йогурт «Нурсүт» клубника 200 г", 4, 6),
            Tr.T("Продать всё равно", "Баары бир сатуу", "Sell anyway", "Yine de sat", "Baribir sotish"),
            Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), PosConfirmAccent.Danger);
        try
        {
            _ = over.ShowDialog(owner);
            UiShot.Settle(6);
            UiDrive.AllText(over).Should().Contain("4").And.Contain("6", "свободно и в резерве названы");
            TextClipChecker.Find(over, "stock-over-available", lang.ToString()).Should().BeEmpty($"текст не обрезан ({lang} {width}x{height})");
        }
        finally
        {
            over.Close();
        }
    }
}
