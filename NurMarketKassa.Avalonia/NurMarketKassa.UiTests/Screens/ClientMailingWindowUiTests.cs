using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 («Клиенты → Рассылка», ТОП-15 п. 2): окно на 1024×600 и 1366×768 (ru, ky, uz) — список получателей и число, предпросмотр без «сырых» подстановок и без чужих данных,
/// текст не обрезан; смена сегмента; очередь открывает ОДИН чат на одно нажатие и ничего сама не открывает; «не беспокоить» и повтор в те же дни не пускают клиента в очередь.
/// Ссылки не запускаются (<see cref="ClientMailingWindow.OpenUrlForTests"/>), сервер и файл настроек не используются. Наложение полей — общий <c>FormFieldsOverlapUiTests</c> (окно «x-client-mailing»).</summary>
[Collection("UiScenarios")]
public sealed class ClientMailingWindowUiTests : IDisposable
{
    public ClientMailingWindowUiTests() => TestHost.Ensure();

    public void Dispose()
    {
        ClientMailingData.SeedForTests(null);
        ClientMailing.ResetForTests();
        ClientMailingWindow.OpenUrlForTests = null;
        ClientMailingWindow.ConfirmStartForTests = null;
        ClientMailingWindow.QuietHoursForTests = null;
    }

    public static IEnumerable<object[]> Cases() =>
        new[] { (1024, 600), (1366, 768) }.SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.Item1, s.Item2, l }));

    private static ClientMailingWindow Create(Action? beforeOpen = null)
    {
        ClientMailing.ResetForTests();
        ClientMailingData.SeedForTests(ClientMailingFixture.Snapshot());
        ClientMailingWindow.QuietHoursForTests = () => false;
        beforeOpen?.Invoke();
        return new ClientMailingWindow();
    }

    private static void Show(Window w, int width = 1366, int height = 768)
    {
        using var _ = UiShot.Render(w, new UiSize(width, height));
        UiShot.Settle(6, 30); // Opened → загрузка готового списка → пересчёт
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Window_ShowsRecipientsAndPreview_FitsAndIsNotClipped(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = Create();
        try
        {
            Show(window, width, height);
            var all = UiDrive.AllText(window);
            all.Should().Contain("Айбек Асанов", "должник с самым большим долгом — в списке и выбран для предпросмотра");
            all.Should().Contain(ClientMailingFixture.LongName[..10], "очень длинное имя не пропадает");
            all.Should().NotContain("Бермет", "в сегменте «Должники» её нет — чужие данные не светятся");
            window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !t.GetVisualAncestors().OfType<Button>().Any()).Select(t => t.Text ?? "")
                .Should().NotContain(t => t.Contains('{') && t.Contains('}'), "в предпросмотре подстановки заменены (кнопки-подстановки «{имя}» — не в счёт)");
            var found = TextClipChecker.Find(window, "client-mailing-" + width, lang == AppLanguage.Kyrgyz ? "ky" : lang == AppLanguage.Uzbek ? "uz" : "ru");
            found.Should().BeEmpty("текст не обрезан: " + string.Join("; ", found.Select(v => v.Detail)));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Switching_segment_changes_the_list_and_shows_the_parameter()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = Create();
        try
        {
            Show(window);
            UiDrive.Click(window, UiDrive.ButtonByText(window, "VIP-клиенты"));
            UiShot.Settle(4, 30);
            var vip = UiDrive.AllText(window);
            vip.Should().Contain("Гульнара Исаева", "купила на 41 000 за год");
            vip.Should().Contain("Покупки за год от", "у VIP есть порог суммы");
            vip.Should().NotContain("Данияр Орозов", "купил на 3 000 — не VIP");

            UiDrive.Click(window, UiDrive.ButtonByText(window, "Давно не покупали"));
            UiShot.Settle(4, 30);
            var inactive = UiDrive.AllText(window);
            inactive.Should().Contain("Бермет Токтогулова", "последняя покупка 60 дней назад");
            inactive.Should().Contain("Не покупали, дней");
            inactive.Should().NotContain("Жаныбек Абдыров", "покупал 12 дней назад");

            UiDrive.Click(window, UiDrive.ButtonByText(window, "День рождения"));
            UiShot.Settle(4, 30);
            UiDrive.AllText(window).Should().Contain("Айбек Асанов", "день рождения через 3 дня");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Queue_opens_exactly_one_chat_per_press_and_never_by_itself()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var opened = new List<string>();
        ClientMailingWindow.OpenUrlForTests = url =>
        {
            opened.Add(url);
            return true;
        };
        ClientMailingWindow.ConfirmStartForTests = _ => Task.FromResult(true);
        // «Не беспокоить»: Данияр Орозов (долг 2 300) в очередь не попадёт.
        var window = Create(() => ClientMailing.SetDoNotDisturb("c8", "Данияр Орозов", true));
        try
        {
            Show(window);
            opened.Should().BeEmpty("пока владелец не нажал — ни одного чата");
            var button = UiDrive.ButtonByText(window, "Открыть WhatsApp по очереди");
            UiDrive.LabelOf(button).Should().Contain("(3)", "Айбек (12 500), клиент без имени (700), Жумабаев (480); второй Айбек — тот же номер, без телефона и «не беспокоить» не считаются");

            UiDrive.Click(window, button);
            WaitFor(() => opened.Count >= 1, window);
            opened.Should().HaveCount(1, "одно нажатие — один чат");
            opened[0].Should().StartWith("https://wa.me/996700111222?text=");
            var text = Uri.UnescapeDataString(opened[0][(opened[0].IndexOf("text=", StringComparison.Ordinal) + 5)..]);
            text.Should().Contain("Айбек Асанов").And.Contain("12").And.NotContain("Жумабаев").And.NotContain("{");
            ClientMailing.Journal().Should().HaveCount(1);

            for (var i = 2; i <= 3; i++)
            {
                UiDrive.Click(window, UiDrive.ButtonByText(window, "Следующий"));
                var want = i;
                WaitFor(() => opened.Count >= want, window);
                opened.Should().HaveCount(i);
            }
            opened.Select(u => u[..u.IndexOf('?')]).Should().OnlyHaveUniqueItems("один номер — один чат");
            opened.Should().NotContain(u => u.Contains("996312654321"), "«не беспокоить» не открывается");
            UiDrive.ButtonByText(window, "Все чаты открыты").IsEnabled.Should().BeFalse();
            ClientMailing.Journal().Should().HaveCount(3);
            ClientMailing.LastMailed().Keys.Should().BeEquivalentTo("c1", "c6", "c3");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Clients_written_recently_are_not_offered_again()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = Create(() =>
        {
            foreach (var c in ClientMailingFixture.Snapshot().Contacts.Where(c => c.Debt > 0))
                ClientMailing.LogOpened(c, ClientMailing.Segment.Debtors, "ru", DateTime.Now.AddDays(-2));
        });
        try
        {
            Show(window);
            var button = UiDrive.ButtonByText(window, "Открыть WhatsApp по очереди");
            button.IsEnabled.Should().BeFalse("всем должникам писали два дня назад, окно 14 дней");
            UiDrive.LabelOf(button).Should().Contain("(0)");
            UiDrive.AllText(window).Should().Contain("недавно писали", "причина видна в списке");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task During_quiet_hours_chats_cannot_be_opened()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = Create();
        ClientMailingWindow.QuietHoursForTests = () => true;
        try
        {
            Show(window);
            UiDrive.ButtonByText(window, "Открыть WhatsApp по очереди").IsEnabled.Should().BeFalse();
            UiDrive.AllText(window).Should().Contain("Тихие часы");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    private static void WaitFor(Func<bool> condition, Window window)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < until)
            UiShot.Settle(2, 40);
        UiShot.Settle(3, 30);
    }
}
