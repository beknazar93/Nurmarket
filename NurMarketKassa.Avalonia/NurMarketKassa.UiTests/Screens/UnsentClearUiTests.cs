using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21, «Очистить неотправленные чеки»: окно подтверждения (счёт, сумма, самый старый, предупреждение «не попадут в отчёты и учёт», фискально пробитые чеки, путь копии, галочка «Я понимаю…»)
/// и кнопка с отметками в «Неотправленном» (владелец/админ) — ru, ky, uz на 1024×600 и 1366×768. Проверка: всё помещается в экран без обрезки текста, кнопки внутри окна, кнопка «Очистить…» выключена
/// до галочки, по умолчанию ни один чек не отмечен, поля и подписи не налезают (FormFieldsOverlapUiTests.FindViolations). Снимки — в папку из переменной NM_UNSENT_SHOTS (для просмотра глазами).</summary>
[Collection("UiScenarios")]
public sealed class UnsentClearUiTests
{
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly UiSize[] Sizes = [new(1024, 600), new(1366, 768)];

    public static IEnumerable<object[]> Matrix() =>
        new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.SelectMany(l => Sizes.Select(s => new object[] { l, s.Width, s.Height }));

    private static UnsentClearLine Line(string id, int daysOld, decimal amount, bool fiscal = false, bool maybe = false, bool debt = false) =>
        new(id, DateTimeOffset.Now.AddDays(-daysOld), OfflineSaleEntry.Failed, amount, "cash", "Айгуль", "shift-1", 2, "Старше 7 суток", fiscal, maybe, debt);

    private static UnsentClearPlan Plan(params UnsentClearLine[] lines) => new() { Items = lines };

    private static void Shot(SKBitmap bmp, string name)
    {
        var dir = Environment.GetEnvironmentVariable("NM_UNSENT_SHOTS");
        if (string.IsNullOrWhiteSpace(dir))
            return;
        Directory.CreateDirectory(dir);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static string Code(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Dialog_ShowsSummaryAndAllWarnings_FitsScreen_AndNeedsAcknowledgement(AppLanguage lang, int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        // худший случай по высоте: выбраны 2 чека, все предупреждения включены
        var all = Plan(Line("a1", 20, 1250m, fiscal: true, maybe: true, debt: true), Line("b2", 9, 480m), Line("c3", 8, 99.5m));
        var selected = Plan(all.Items[0], all.Items[1]);
        var dialog = new UnsentClearDialog(all, selected, @"C:\Users\Владелец\AppData\Roaming\NurMarketKassa\data\backup\unsent-receipts");
        try
        {
            using var bmp = UiShot.Render(dialog);
            UiShot.Settle(4);
            var tag = $"{lang} {w}x{h}";
            Shot(bmp, $"dialog-{Code(lang)}-{w}x{h}");

            dialog.Bounds.Height.Should().BeLessThanOrEqualTo(h, $"окно очистки помещается по высоте ({tag})");
            dialog.Bounds.Width.Should().BeLessThanOrEqualTo(w);

            var texts = dialog.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            texts.Should().Contain(t => t.Contains(MoneyText.Som(1730m)), $"счёт и сумма выбранных чеков видны ({tag})");
            texts.Count(t => t.Length > 40).Should().BeGreaterThanOrEqualTo(4, "предупреждения (не попадут в учёт, фискальный чек, отправка начиналась, в долг) видны");

            var ok = dialog.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("danger"));
            ok.IsEnabled.Should().BeFalse($"пока не поставлена галочка «Я понимаю…», очищать нельзя ({tag})");
            var check = dialog.GetVisualDescendants().OfType<CheckBox>().Single();
            check.IsChecked.Should().NotBe(true, "по умолчанию галочка не стоит");
            check.IsChecked = true;
            UiShot.Settle(3);
            ok.IsEnabled.Should().BeTrue("после галочки кнопка доступна");
            ok.Bounds.Height.Should().BeGreaterThanOrEqualTo(40);

            var radios = dialog.GetVisualDescendants().OfType<RadioButton>().ToList();
            radios.Should().HaveCount(2);
            radios[0].IsChecked.Should().BeTrue("есть выбранные — по умолчанию «Только выбранные»");

            foreach (var b in dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
            {
                var p = b.TranslatePoint(default, dialog)!.Value;
                (p.X + b.Bounds.Width).Should().BeLessThanOrEqualTo(dialog.Bounds.Width + 0.5);
                (p.Y + b.Bounds.Height).Should().BeLessThanOrEqualTo(dialog.Bounds.Height + 0.5, $"кнопка внутри окна ({tag})");
            }

            TextClipChecker.Find(dialog, "unsent-clear-dialog", Code(lang)).Should().BeEmpty($"текст не обрезан ({tag})");
            FormFieldsOverlapUiTests.FindViolations(dialog, "unsent-clear-dialog").Should().BeEmpty($"поля и подписи не налезают ({tag})");

            // переключение на «Все» меняет счёт
            radios[1].IsChecked = true;
            UiShot.Settle(3);
            dialog.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")
                .Should().Contain(t => t.Contains(MoneyText.Som(1829.5m)), "в «Все» — 3 чека на всю сумму");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task OutboxWindow_ShowsClearButtonAndCheckboxes_ForAdminOnly_NothingSelectedByDefault(AppLanguage lang, int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = (OutboxWindow)Activator.CreateInstance(typeof(OutboxWindow), nonPublic: true)!;
        try
        {
            var items = new List<OutboxItem>
            {
                Sale("a1b2c3", 10, 1250m, OutboxStatus.Error),
                Sale("d4e5f6", 8, 480m, OutboxStatus.Waiting),
                new() { Source = "op", Id = "r1", Kind = OutboxKind.Return, Title = "Возврат по чеку 12", Amount = 100m, CreatedAt = DateTimeOffset.Now.AddDays(-1), Status = OutboxStatus.Waiting },
            };
            using var _ = UiShot.Render(window, new UiSize(w, h));
            UiShot.Settle(6);
            var access = typeof(OutboxWindow).GetField("_access", NonPublic)!;
            var apply = typeof(OutboxWindow).GetMethod("ApplySnapshot", NonPublic)!;
            var tag = $"{lang} {w}x{h}";

            // кассир: ни кнопки, ни галочек
            access.SetValue(window, new OutboxAccess(false, "Айгуль"));
            apply.Invoke(window, new object[] { items });
            UiShot.Settle(4);
            ClearButtons(window).Should().BeEmpty($"кассиру кнопки «Очистить…» нет ({tag})");
            window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).Should().BeEmpty("и отметок чеков нет");

            // владелец/админ
            access.SetValue(window, new OutboxAccess(true, "Владелец"));
            apply.Invoke(window, new object[] { items });
            UiShot.Settle(6);
            Shot(UiShot.Render(window, new UiSize(w, h)), $"outbox-{Code(lang)}-{w}x{h}");

            var clear = ClearButtons(window);
            clear.Should().HaveCount(1, $"владельцу видна «Очистить неотправленные чеки…» ({tag})");
            clear[0].Bounds.Height.Should().BeGreaterThanOrEqualTo(40);
            var p = clear[0].TranslatePoint(default, window)!.Value;
            (p.X + clear[0].Bounds.Width).Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5, "кнопка внутри окна");

            var checks = window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).ToList();
            checks.Should().HaveCount(2, "отметка есть у двух чеков продаж, у возврата её нет");
            checks.Should().OnlyContain(c => c.IsChecked != true, "по умолчанию ничего не выбрано");

            TextClipChecker.Find(window, "outbox-clear", Code(lang)).Should().BeEmpty($"текст не обрезан ({tag})");
            FormFieldsOverlapUiTests.FindViolations(window, "outbox-clear").Should().BeEmpty($"поля и кнопки не налезают ({tag})");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    private static List<Button> ClearButtons(Window window) => window.GetVisualDescendants().OfType<Button>()
        .Where(b => b.IsEffectivelyVisible && b.Content is string s && (s.StartsWith("Очистить неотправленные", StringComparison.Ordinal)
            || s.StartsWith("Жөнөтүлбөгөн чектерди тазалоо", StringComparison.Ordinal) || s.StartsWith("Yuborilmagan cheklarni tozalash", StringComparison.Ordinal))).ToList();

    private static OutboxItem Sale(string id, int daysOld, decimal amount, OutboxStatus status) => new()
    {
        Source = SalesQueueOutboxSource.SourceName,
        Id = id,
        Kind = OutboxKind.Sale,
        Title = "Чек " + id.ToUpperInvariant() + " · 05.11 10:00",
        Subtitle = "Айгуль",
        Amount = amount,
        LinesCount = 2,
        CreatedAt = DateTimeOffset.Now.AddDays(-daysOld),
        Status = status,
        Reason = status == OutboxStatus.Error ? "Старше 7 суток" : null,
        CanSend = status == OutboxStatus.Waiting,
        CanSupport = true,
    };
}
