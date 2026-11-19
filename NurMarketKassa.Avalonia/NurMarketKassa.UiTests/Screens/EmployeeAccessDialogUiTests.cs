using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-19 (запрос владельца, п. 3): окно «Доступы: …» — группы-карточки со счётчиком, шаблоны ролей, поиск, подсветка несохранённых правок и три состояния
/// сохранения: «Сохраняю…» (кнопка заблокирована), «Доступы сохранены ✓», ошибка с «Повторить». Сервер не нужен: отправку подменяет заглушка.
/// Снимки: %TEMP%\nurmarket-tests\ui-actual\access-{тема}-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class EmployeeAccessDialogUiTests
{
    private static EmployeeAccessFlags Flags() => new() { CanViewSale = true, CanViewClients = true, CanUsePos = false, SellBelowCostShown = true };

    private static Border Banner(Window w) => UiDrive.Find<Border>(w, "AccessBanner");

    [AvaloniaFact]
    public async Task Save_ShowsSavingThenSuccess_AndClosesWithSavedFlag()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var gate = new TaskCompletionSource();
        EmployeeAccessFlags? sent = null;
        var dialog = new EmployeeAccessDialog("Айгуль", Flags(), async f => { sent = f; await gate.Task; });
        var oldDelay = EmployeeAccessDialog.CloseDelayMs;
        EmployeeAccessDialog.CloseDelayMs = 60_000;
        try
        {
            using var _ = UiShot.Render(dialog, new UiSize(1024, 640));
            var save = UiDrive.Find<Button>(dialog, "AccessSaveButton");

            // правок нет: баннера нет, кнопка притушена; после галочки — «Есть несохранённые изменения»
            Banner(dialog).IsVisible.Should().BeFalse();
            UiDrive.Click(dialog, UiDrive.Find<Button>(dialog, "Template_SeniorCashier"));
            dialog.HasChanges.Should().BeTrue();
            UiDrive.AllText(dialog).Should().Contain("несохранённые");
            save.Opacity.Should().Be(1);

            UiDrive.Click(dialog, save);
            save.IsEnabled.Should().BeFalse("пока идёт отправка кнопка заблокирована");
            UiDrive.AllText(dialog).Should().Contain("Сохраняю");
            save.GetVisualDescendants().OfType<ProgressBar>().Should().NotBeEmpty("спиннер в кнопке");
            sent.Should().NotBeNull();

            gate.SetResult();
            UiShot.Settle(6);
            dialog.Saved.Should().BeTrue();
            Banner(dialog).Tag.Should().Be("Success");
            UiDrive.AllText(dialog).Should().Contain("Доступы сохранены ✓");
        }
        finally
        {
            EmployeeAccessDialog.CloseDelayMs = oldDelay;
            dialog.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Save_Error_ShowsReasonAndRetry_ThenSucceeds()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var attempts = 0;
        var dialog = new EmployeeAccessDialog("Айгуль", Flags(), _ =>
        {
            attempts++;
            return attempts == 1 ? Task.FromException(new ApiException("forbidden", 403)) : Task.CompletedTask;
        });
        var oldDelay = EmployeeAccessDialog.CloseDelayMs;
        EmployeeAccessDialog.CloseDelayMs = 60_000;
        try
        {
            using var _ = UiShot.Render(dialog, new UiSize(1024, 640));
            UiDrive.Click(dialog, UiDrive.Find<Button>(dialog, "Template_Cashier"));
            var save = UiDrive.Find<Button>(dialog, "AccessSaveButton");
            UiDrive.Click(dialog, save);
            UiShot.Settle(6);

            dialog.Saved.Should().BeFalse();
            Banner(dialog).Tag.Should().Be("Error");
            UiDrive.AllText(dialog).Should().Contain("не сохранены");
            save.IsEnabled.Should().BeTrue();
            save.Content.Should().Be("Повторить");
            UiDrive.Find<Button>(dialog, "AccessCancelButton").IsEnabled.Should().BeTrue();

            UiShot.Settle(4);
            save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); // «Повторить»
            UiShot.Settle(6);
            attempts.Should().Be(2);
            dialog.Saved.Should().BeTrue();
            Banner(dialog).Tag.Should().Be("Success");
        }
        finally
        {
            EmployeeAccessDialog.CloseDelayMs = oldDelay;
            dialog.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Search_FiltersRights_AndCounterAndSelectAllWork()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var dialog = new EmployeeAccessDialog("Айгуль", Flags(), _ => Task.CompletedTask);
        try
        {
            using var _ = UiShot.Render(dialog, new UiSize(1024, 640));
            UiDrive.AllText(dialog).Should().Contain("2 из 10", "в «Базовых» отмечены продажа и клиенты");

            UiDrive.Type(dialog, UiDrive.Find<TextBox>(dialog, "AccessSearchBox"), "скидка");
            var text = UiDrive.AllText(dialog);
            text.Should().Contain("Скидка в кассе").And.NotContain("Аналитика");

            UiDrive.Type(dialog, UiDrive.Find<TextBox>(dialog, "AccessSearchBox"), "ццц");
            UiDrive.AllText(dialog).Should().Contain("Такого права нет");

            UiDrive.Find<TextBox>(dialog, "AccessSearchBox").Text = "";
            UiShot.Settle(3);
            UiDrive.Click(dialog, UiDrive.ButtonByText(dialog, "Выбрать все"));
            dialog.HasChanges.Should().BeTrue();
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

    public static IEnumerable<object[]> Looks() =>
    [
        [UiTheme.Light, 1366, 768],
        [UiTheme.Dark, 1366, 768],
        [UiTheme.Light, 1024, 600],
    ];

    [AvaloniaTheory]
    [MemberData(nameof(Looks))]
    public async Task Snapshot_AndTextNotClipped(UiTheme theme, int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, AppLanguage.Russian);
        var dialog = new EmployeeAccessDialog("Айгуль Сыдыкова", Flags(), _ => Task.CompletedTask);
        try
        {
            using var _ = UiShot.Render(dialog, new UiSize(w, h));
            UiDrive.Click(dialog, UiDrive.Find<Button>(dialog, "Template_SeniorCashier"));
            UiShot.Settle(3);
            var clipped = TextClipChecker.Find(dialog, "employee-access", "ru");
            clipped.Should().BeEmpty(string.Join("; ", clipped.Select(v => v.Detail)));

            using var shot = UiShot.Render(dialog);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var outDir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, $"access-{theme.ToString().ToLowerInvariant()}-ru-{w}x{h}.png"), data.ToArray());
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }
}
