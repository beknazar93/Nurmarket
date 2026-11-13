using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11 (О-5 «Неоконченная приёмка пропадает», «Маркет ОРУК Ош»): настоящие окна в Avalonia.Headless. Вопрос «Найдена неоконченная приёмка… Продолжить / Начать заново» при 1024×600 и
/// 1366×768 (ru, ky; обычный и старше 30 дней) — текст не обрезан, окно не выше экрана. Сквозные сценарии «Склад → Приёмка» и «Закупки → Приём товара»: «Продолжить» возвращает все поля строк,
/// «Начать заново» просит подтверждение и удаляет черновик («Назад» возвращает к вопросу), набор строк сам пишет файл и показывает «Черновик сохранён», «Очистить» удаляет файл.
/// Папка черновика — временная; в остальных UI-тестах черновик выключен (UiTestEnv). Снимки: %TEMP%\nurmarket-tests\ui-actual\receiving-draft-*.png.</summary>
[Collection("UiScenarios")]
public sealed class ReceivingDraftUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-draft-ui-" + Guid.NewGuid().ToString("N"));

    public ReceivingDraftUiTests()
    {
        Directory.CreateDirectory(_dir);
        ReceivingDraftStore.DataDirOverride = _dir;
        ReceivingDraftStore.Disabled = false;
        ReceivingDraftStore.ResetForTests();
        // Вопрос — обычным ShowDialog без вложенного цикла (PosDialogHost.Show в Headless не возвращает управление тесту, пока окно открыто — прогон 11.11 завис).
        ReceivingDraftUi.ShowModal = (dialog, owner) => dialog.ShowDialog<bool?>(owner!);
    }

    public void Dispose()
    {
        ReceivingDraftUi.ShowModal = (dialog, owner) => PosDialogHost.ShowModalAsync(dialog, owner);
        ReceivingDraftStore.Disabled = true;
        ReceivingDraftStore.DataDirOverride = null;
        ReceivingDraftStore.ResetForTests();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* антивирус */ }
    }

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.SelectMany(l => new[] { false, true }.Select(stale => new object[] { s.W, s.H, l, stale })));

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static void Press(Window w, PhysicalKey key) => w.KeyPressQwerty(key, RawInputModifiers.None);

    private static async Task<T> Within<T>(Task<T> task, int ms = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
            UiShot.Settle(1, 20);
        task.IsCompleted.Should().BeTrue("окно должно закрыться");
        return await task;
    }

    private static bool WaitFor(Func<bool> condition, int ms = 6000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < until)
            UiShot.Settle(1, 40);
        return condition();
    }

    private static ReceivingDraftInfo Info(bool stale) =>
        new(ReceivingDraftSlots.Warehouse, 12, new DateTime(2026, 11, 11, 9, 0, 0), stale ? DateTime.Now.AddDays(-40) : new DateTime(2026, 11, 11, 14, 5, 0), stale);

    // ------------------------------------------------------------------ вопрос при открытии: вид

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void Question_FitsScreen_TextNotClipped_BothButtonsPresent(int width, int height, AppLanguage lang, bool stale)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var owner = new Window();
        using var _ = UiShot.Render(owner, new UiSize(width, height));
        var dialog = ReceivingDraftUi.BuildQuestion(Info(stale));
        try
        {
            var shown = dialog.ShowDialog<bool?>(owner);
            shown.IsCompleted.Should().BeFalse();
            UiShot.Settle(8);
            var tag = $"{width}x{height} {lang} stale={stale}";
            var text = UiDrive.AllText(dialog);
            text.Should().Contain(lang == AppLanguage.Kyrgyz ? "Бүтпөгөн кабыл алуу" : "Неоконченная приёмка");
            text.Should().Contain("12 позици"); // ru «12 позиций», ky «12 позиция» — общее начало
            if (!stale)
                text.Should().Contain("11.11 14:05");
            var first = lang == AppLanguage.Kyrgyz ? (stale ? "Долбоорду өчүрүү" : "Улантуу") : (stale ? "Удалить черновик" : "Продолжить");
            var second = lang == AppLanguage.Kyrgyz ? (stale ? "Баары бир улантуу" : "Кайра баштоо") : (stale ? "Всё же продолжить" : "Начать заново");
            UiDrive.ButtonByText(dialog, first).IsEnabled.Should().BeTrue(tag);
            UiDrive.ButtonByText(dialog, second).IsEnabled.Should().BeTrue(tag);
            TextClipChecker.Find(dialog, "receiving-draft-question", lang.ToString()).Should().BeEmpty($"текст не обрезан ({tag})");
            dialog.Bounds.Height.Should().BeLessThanOrEqualTo(height, tag);
            dialog.Bounds.Width.Should().BeLessThanOrEqualTo(width, tag);
            SaveShot(dialog, $"receiving-draft-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}{(stale ? "-stale" : "")}");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Question_Enter_IsContinue_ByDefault_AndDeleteConfirmFits()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var owner = new Window();
        using var _ = UiShot.Render(owner, new UiSize(1024, 600));
        var dialog = ReceivingDraftUi.BuildQuestion(Info(false));
        var confirm = ReceivingDraftUi.BuildDeleteConfirm(Info(false));
        try
        {
            var task = dialog.ShowDialog<bool?>(owner);
            UiShot.Settle(6);
            Press(dialog, PhysicalKey.Enter);
            (await Within(task)).Should().BeTrue("Enter — «Продолжить» (по умолчанию)");

            var task2 = confirm.ShowDialog<bool?>(owner);
            UiShot.Settle(6);
            UiDrive.AllText(confirm).Should().Contain("Удалить неоконченную приёмку (12 позиций)").And.Contain("нельзя");
            TextClipChecker.Find(confirm, "receiving-draft-delete", "ru").Should().BeEmpty();
            UiDrive.Click(confirm, UiDrive.ButtonByText(confirm, "Назад"));
            (await Within(task2)).Should().NotBe(true, "«Назад» — не удалять");
        }
        finally
        {
            dialog.Close();
            confirm.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ «Склад → Приёмка»

    private static ReceivingDraftFile TwoLinesDraft()
    {
        var packs = new ReceivingLineVm { Barcode = "4600000000011", ProductName = "Ак Кант 1 кг (мой текст)", OriginalName = "Ак Кант", Source = ReceivingSource.Warehouse, ProductId = "ak1", OriginalSalePrice = 60, OriginalPurchasePrice = 40, StockBefore = 5 };
        packs.Quantity = 1;
        packs.PurchasePrice = 40;
        packs.SalePrice = 60;
        packs.SetPackMode(true, 24);
        packs.SetPackCount(5);
        packs.SetPiecesPerPack(24);
        packs.SetPackPriceText("960");
        packs.SalePrice = 60;
        var net = new ReceivingLineVm { Barcode = "4601234567890", ProductName = "Печенье из интернета", Source = ReceivingSource.Internet, Quantity = 12, PurchasePrice = 18.5, SalePrice = 25, CategoryName = "Печенье" };
        return new ReceivingDraftFile
        {
            Version = 1, Slot = ReceivingDraftSlots.Warehouse, StartedAt = DateTime.Now.AddHours(-3), SavedAt = DateTime.Now.AddMinutes(-20),
            SupplierId = "sup1", SupplierName = "ОсОО Тест-Поставщик", PaidNow = false, DebtDays = 14,
            Lines = new List<ReceivingDraftLine> { packs.ToDraft(), net.ToDraft() },
        };
    }

    private static void WriteDraft(ReceivingDraftFile file) =>
        ReceivingDraftStore.WriteForTests(file).Should().BeTrue();

    private static async Task<(WarehouseWindow Window, WarehouseViewModel Vm)> OpenWarehouseAsync(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        using var _ = UiShot.Render(window, new UiSize(1366, 768));
        UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
        var vm = (WarehouseViewModel)window.DataContext!;
        return (window, vm);
    }

    private static PosConfirmDialog? Question(Window owner) => owner.OwnedWindows.OfType<PosConfirmDialog>().FirstOrDefault(d => d.IsVisible);

    [AvaloniaFact]
    public async Task Warehouse_Continue_RestoresAllFields_ThenCaptionAppears()
    {
        WriteDraft(TwoLinesDraft());
        var (window, vm) = await OpenWarehouseAsync(AppLanguage.Russian);
        try
        {
            vm.ReceivingLines.Clear();
            WaitFor(() => Question(window) is not null).Should().BeTrue("при открытии приёмки спросили про черновик");
            var q = Question(window)!;
            UiDrive.AllText(q).Should().Contain("Найдена неоконченная приёмка (2 позиции");
            vm.ReceivingLines.Should().BeEmpty("до ответа ничего не загружено");
            SaveShot(window, "receiving-draft-warehouse-question");

            UiShot.Settle(6); // окну нужно успеть получить фокус, иначе Enter теряется
            UiDrive.Click(q, UiDrive.ButtonByText(q, "Продолжить")); // нажатие, как у кассира
            WaitFor(() => vm.ReceivingLines.Count == 2).Should().BeTrue("строки загружены");

            var ak = vm.ReceivingLines.Single(l => l.ProductId == "ak1");
            ak.PackMode.Should().BeTrue();
            ak.PackCount.Should().Be(5);
            ak.PiecesPerPack.Should().Be(24);
            ak.PackPrice.Should().Be(960);
            ak.Quantity.Should().Be(120);
            ak.ProductName.Should().Be("Ак Кант 1 кг (мой текст)");
            ak.OriginalName.Should().Be("Ак Кант");
            var net = vm.ReceivingLines.Single(l => l.Source == ReceivingSource.Internet);
            net.ProductName.Should().Be("Печенье из интернета");
            net.PurchasePrice.Should().Be(18.5);
            net.CategoryName.Should().Be("Печенье");
            vm.ReceivingSupplier!.Id.Should().Be("sup1");
            vm.ReceivingPaidNow.Should().BeFalse();
            vm.ReceivingDebtDays.Should().Be(14);
            UiDrive.Find<RadioButton>(window, "ReceivingDebtRadio").IsChecked.Should().BeTrue();

            // после «Продолжить» файл переписывается (тот же черновик) и появляется тихая подпись
            ak.Quantity = 121; // правка → запись через ~1,5 с
            WaitFor(() => UiDrive.AllText(window).Contains("Черновик сохранён")).Should().BeTrue("подпись «Черновик сохранён чч:мм»");
            ReceivingDraftStore.Load(ReceivingDraftSlots.Warehouse)!.Lines.Single(l => l.ProductId == "ak1").Quantity.Should().Be(121);
            SaveShot(window, "receiving-draft-warehouse-saved");

            // «Очистить» (все строки убраны) — файл удалён сразу
            vm.ReceivingLines.Clear();
            WaitFor(() => !File.Exists(ReceivingDraftStore.PathFor(ReceivingDraftSlots.Warehouse))).Should().BeTrue("список пуст — черновик удалён");
        }
        finally
        {
            vm.ReceivingLines.Clear();
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Warehouse_StartOver_Back_ReturnsToQuestion_ThenDeleteRemovesFile()
    {
        WriteDraft(TwoLinesDraft());
        var (window, vm) = await OpenWarehouseAsync(AppLanguage.Russian);
        try
        {
            vm.ReceivingLines.Clear();
            WaitFor(() => Question(window) is not null).Should().BeTrue();
            UiDrive.Click(Question(window)!, UiDrive.ButtonByText(Question(window)!, "Начать заново"));
            WaitFor(() => Question(window) is { } c && UiDrive.AllText(c).Contains("нельзя")).Should().BeTrue("«Начать заново» просит подтверждение");
            UiDrive.Click(Question(window)!, UiDrive.ButtonByText(Question(window)!, "Назад"));
            WaitFor(() => Question(window) is { } c && UiDrive.AllText(c).Contains("Найдена неоконченная")).Should().BeTrue("«Назад» вернул к вопросу");
            File.Exists(ReceivingDraftStore.PathFor(ReceivingDraftSlots.Warehouse)).Should().BeTrue("пока не подтвердили — файл на месте");

            UiDrive.Click(Question(window)!, UiDrive.ButtonByText(Question(window)!, "Начать заново"));
            WaitFor(() => Question(window) is { } c && UiDrive.AllText(c).Contains("нельзя")).Should().BeTrue();
            UiDrive.Click(Question(window)!, UiDrive.ButtonByText(Question(window)!, "Удалить"));
            WaitFor(() => !File.Exists(ReceivingDraftStore.PathFor(ReceivingDraftSlots.Warehouse))).Should().BeTrue("подтвердили — черновик удалён");
            vm.ReceivingLines.Should().BeEmpty("приёмка пустая, начата заново");
        }
        finally
        {
            vm.ReceivingLines.Clear();
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Warehouse_NoDraft_NoQuestion_TypingWritesFile_AndCloseKeepsIt()
    {
        var (window, vm) = await OpenWarehouseAsync(AppLanguage.Russian);
        try
        {
            vm.ReceivingLines.Clear();
            UiShot.Settle(10);
            Question(window).Should().BeNull("черновика нет — вопроса нет");
            var line = new ReceivingLineVm { Barcode = "4600000000028", ProductName = "Кефир", Source = ReceivingSource.Unknown, Quantity = 3, PurchasePrice = 50, SalePrice = 70 };
            vm.ReceivingLines.Add(line);
            WaitFor(() => File.Exists(ReceivingDraftStore.PathFor(ReceivingDraftSlots.Warehouse))).Should().BeTrue("набранная строка сама попала в файл");
            line.Quantity = 9;
            window.Close(); // закрытие — запись сразу (не ждём дебаунса)
            UiShot.Pump();
            ReceivingDraftStore.Load(ReceivingDraftSlots.Warehouse)!.Lines.Single().Quantity.Should().Be(9, "при закрытии окна записано последнее значение");
        }
        finally
        {
            vm.ReceivingLines.Clear();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-11 (Д-5, живая проверка «Склада»): «Закрыть склад?» писал «приёмка — 2. После закрытия они пропадут», хотя черновик пишется.
    /// Теперь при записанном черновике — «сохранится черновиком», «пропадут» — только про массовое добавление и при незаписанном черновике.</summary>
    [AvaloniaFact]
    public async Task Warehouse_CloseQuestion_SaysDraftKept_WhenDraftWritten_OldWarningOtherwise()
    {
        var (window, vm) = await OpenWarehouseAsync(AppLanguage.Russian);
        try
        {
            vm.ReceivingLines.Clear();
            UiShot.Settle(10);
            vm.ReceivingLines.Add(new ReceivingLineVm { Barcode = "4600000000028", ProductName = "Кефир", Source = ReceivingSource.Unknown, Quantity = 3, PurchasePrice = 50, SalePrice = 70 });
            vm.ReceivingLines.Add(new ReceivingLineVm { Barcode = "4600000000011", ProductName = "Молоко", Source = ReceivingSource.Unknown, Quantity = 1, PurchasePrice = 40, SalePrice = 55 });
            UiShot.Settle(4);
            var draft = (ReceivingDraftSession)typeof(WarehouseWindow).GetField("_receivingDraft", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
            draft.SaveNowAndCheck().Should().BeTrue("черновик включён и записан");

            var kept = WarehouseWindow.CloseQuestionText(2, 0, receivingKept: true);
            kept.Should().Contain("сохранится черновиком").And.NotContain("пропадут");
            WarehouseWindow.CloseQuestionText(2, 0, receivingKept: false).Should().Contain("приёмка — 2").And.Contain("пропадут", "черновик не записался — прежнее предупреждение");
            var mixed = WarehouseWindow.CloseQuestionText(2, 3, receivingKept: true);
            mixed.Should().Contain("массовое добавление — 3").And.Contain("пропадут").And.Contain("сохранится черновиком").And.NotContain("приёмка — 2");

            UiShot.Setup(UiTheme.Light, AppLanguage.Kyrgyz);
            WarehouseWindow.CloseQuestionText(2, 0, receivingKept: true).Should().Contain("долбоор болуп сакталат");
        }
        finally
        {
            UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
            vm.ReceivingLines.Clear();
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ «Закупки → Приём товара»

    [AvaloniaTheory]
    [InlineData(1024, 600, AppLanguage.Russian)]
    [InlineData(1366, 768, AppLanguage.Kyrgyz)]
    public async Task Purchases_Continue_RestoresLinesAndHeader(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var file = TwoLinesDraft();
        file.Slot = ReceivingDraftSlots.Purchases;
        file.DocNumber = "НКЛ-77";
        WriteDraft(file);
        var window = new PurchasesWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            window.OpenReceive(null);
            var tag = $"{width}x{height} {lang}";
            WaitFor(() => Question(window) is not null).Should().BeTrue($"при открытии приёма спросили про черновик ({tag})");
            var q = Question(window)!;
            TextClipChecker.Find(q, "receiving-draft-purchases", lang.ToString()).Should().BeEmpty(tag);
            UiShot.Settle(6);
            UiDrive.Click(q, UiDrive.ButtonByText(q, "Продолжить", "Улантуу"));
            WaitFor(() => UiDrive.AllText(window).Contains("Печенье из интернета")).Should().BeTrue($"строки загружены ({tag})");
            var text = UiDrive.AllText(window);
            text.Should().Contain("Ак Кант 1 кг (мой текст)");
            text.Should().Contain("НКЛ-77", "номер накладной поставщика вернулся");
            SaveShot(window, $"receiving-draft-purchases-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
