using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-19 (ответ Умара 19.11, очередь старше 7 суток): окно «Неотправленное» (ru, ky, uz) с двумя чеками «Старше 7 суток — нужно подтверждение», одним старше 90 суток
/// и пометкой «Отправлено, но: продано ниже закупки / ушло в минус по остатку». Проверка: кнопка «Подтвердить отправку…» в строке и «Подтвердить отправку (N)…» в подвале есть,
/// у чека старше 90 суток кнопки нет (только «в поддержку»), ни одна подпись не обрезана, кнопки внутри окна и не ниже 40 px. Сервер не нужен: список подставляется отражением.
/// Диалог ввода PIN — общий IUserPrompts.ConfirmWithCodeAsync (проверен отдельно: EmployeeAccessDialogUiTests).</summary>
[Collection("UiScenarios")]
public sealed class OutboxConfirmLateUiTests
{
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    public static IEnumerable<object[]> Languages() =>
        new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { l });

    private static OutboxItem Sale(string id, int daysOld, bool canConfirm, string reason) => new()
    {
        Source = SalesQueueOutboxSource.SourceName,
        Id = id,
        Kind = OutboxKind.Sale,
        Title = "Чек " + id.ToUpperInvariant() + " · 05.11 10:00",
        Subtitle = "Айгуль",
        Amount = 1250m,
        LinesCount = 2,
        CreatedAt = DateTimeOffset.Now.AddDays(-daysOld),
        Status = OutboxStatus.Error,
        Reason = reason,
        Attempts = 1,
        CanSend = false,
        CanRetry = false,
        CanSupport = true,
        CanConfirm = canConfirm,
    };

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task OutboxWindow_ShowsConfirmButtons_ForOldReceipts_AndFitsWithoutClipping(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = (OutboxWindow)Activator.CreateInstance(typeof(OutboxWindow), nonPublic: true)!;
        try
        {
            var old1 = new OfflineSaleEntry { CreatedAt = DateTimeOffset.Now.AddDays(-10) };
            var old2 = new OfflineSaleEntry { CreatedAt = DateTimeOffset.Now.AddDays(-8) };
            var items = new List<OutboxItem>
            {
                Sale("a1b2c3", 10, true, SyncService.NeedsConfirmText(old1)),
                Sale("d4e5f6", 8, true, SyncService.NeedsConfirmText(old2)),
                Sale("0a0b0c", 100, false, SyncService.TooOldSupportText()),
            };
            using (var warn = JsonDocument.Parse("{\"warnings\":[{\"code\":\"below_cost\",\"index\":0},{\"code\":\"negative_stock\",\"index\":0}]}"))
                OutboxSentNotes.Record("9f9e9d9c", "sale-1", warn.RootElement.Clone());

            using var _ = UiShot.Render(window, new UiSize(1024, 640));
            UiShot.Settle(6);
            // фоновое чтение настоящих очередей при открытии окна может перетереть подставленный список — подставляем после него
            var apply = typeof(OutboxWindow).GetMethod("ApplySnapshot", NonPublic)!;
            apply.Invoke(window, new object[] { items });
            UiShot.Settle(6);

            var tag = lang.ToString();
            var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
            string Label(Button b) => b.Content as string ?? "";
            var rowConfirm = buttons.Where(b => IsConfirmWord(Label(b)) && !Label(b).Contains('(')).ToList();
            var footerConfirm = buttons.Where(b => IsConfirmWord(Label(b)) && Label(b).Contains("(2)")).ToList();
            rowConfirm.Should().HaveCount(2, $"кнопка «Подтвердить отправку…» у каждого из двух чеков, у чека старше 90 суток её нет ({tag})");
            footerConfirm.Should().HaveCount(1, $"в подвале «Подтвердить отправку (2)…» — только по чекам, которые сервер примет ({tag})");

            foreach (var b in rowConfirm.Concat(footerConfirm))
            {
                b.Bounds.Height.Should().BeGreaterThanOrEqualTo(40, "цель нажатия не меньше 40");
                var p = b.TranslatePoint(default, window)!.Value;
                (p.X + b.Bounds.Width).Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5, $"кнопка внутри окна по ширине ({tag})");
                p.X.Should().BeGreaterThanOrEqualTo(0);
            }

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            texts.Should().Contain(t => t.Contains(SyncService.TooOldSupportText().Substring(0, 12)), $"у чека старше 90 суток — «обратитесь в поддержку» ({tag})");
            texts.Should().Contain(t => t.Contains("ниже закупки") || t.Contains("төмөн") || t.Contains("past sotildi"),
                $"пометка «Отправлено, но…» видна ({tag})");

            var code = lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };
            var clipped = TextClipChecker.Find(window, "outbox-confirm-late", code);
            clipped.Should().BeEmpty($"текст не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));
        }
        finally
        {
            OutboxSentNotes.Clear();
            window.Close();
            UiShot.Pump();
        }
    }

    private static bool IsConfirmWord(string label) =>
        label.StartsWith("Подтвердить отправку", StringComparison.Ordinal)
        || label.StartsWith("Жөнөтүүнү ырастоо", StringComparison.Ordinal)
        || label.StartsWith("Yuborishni tasdiqlash", StringComparison.Ordinal);
}
