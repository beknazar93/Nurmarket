using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-27 (раздел «Акции» программы владельца, конструктор акций Ф1): список и каждый шаг мастера (все 4 вида, «выбранные товары», условия, предпросмотр) на 1024×600,
/// 1366×768 и 1024×768, языки ru/ky/uz — поля не налезают (та же проверка, что FormFieldsOverlapUiTests.FindViolations) и надписи не обрезаны (TextClipChecker). Снимки на 1024×600 —
/// в %TEMP%\nurmarket-tests\ui-actual\promotions-*.png. Плюс поведение: создание с одним ключом Idempotency-Key, повтор после 400 promotion_invalid{title} — шаг 1 и новый ключ;
/// у запущенной акции вид заблокирован; «Завершить» спрашивает подтверждение. Сервер — фейковый (<see cref="FakePromotionsApi"/>).</summary>
[Collection("UiScenarios")]
public sealed class PromotionsWindowUiTests : IDisposable
{
    public PromotionsWindowUiTests() => TestHost.Ensure();

    public void Dispose() => PromotionsFixture.Reset();

    public static IEnumerable<object[]> Cases() =>
        new[] { (1024, 600), (1366, 768), (1024, 768) }.SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.Item1, s.Item2, l }));

    private static string Code(AppLanguage lang) => lang == AppLanguage.Kyrgyz ? "ky" : lang == AppLanguage.Uzbek ? "uz" : "ru";

    private static List<string> Problems(Window w, string scope, string lang)
    {
        UiShot.Settle(3, 30);
        var list = FormFieldsOverlapUiTests.FindViolations(w, scope).Select(v => scope + ": " + v.Detail).ToList();
        list.AddRange(TextClipChecker.Find(w, scope, lang).Select(v => scope + ": " + v.Detail));
        return list;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task List_And_EveryWizardStep_Fit_WithoutOverlapOrClipping(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var code = Code(lang);
        var window = PromotionsFixture.CreateWindow();
        var problems = new List<string>();
        var shots = width == 1024 && height == 600;
        try
        {
            using (UiShot.Render(window, new UiSize(width, height)))
            {
            }
            UiShot.Settle(6, 30);
            UiDrive.AllText(window).Should().Contain("2+1 на воду", "активная акция в списке");
            problems.AddRange(Problems(window, "список", code));
            if (shots)
                ServiceFormsFitUiTests.Save(window, $"promotions-list-{code}-{width}x{height}.png");

            window.OpenWizardForTests(null);
            for (var step = 1; step <= 6; step++)
            {
                window.ShowStepForTests(step);
                if (step == 2)
                {
                    foreach (var kind in new[] { Promotion.KindPercent, Promotion.KindAmount, Promotion.KindFixedPrice, Promotion.KindBuyNGetM })
                    {
                        window.SetKindForTests(kind);
                        problems.AddRange(Problems(window, "шаг 2/" + kind, code));
                    }
                }
                else if (step == 3)
                {
                    window.SetScopeAllForTests(false);
                    problems.AddRange(Problems(window, "шаг 3/выбранные", code));
                    window.SetScopeAllForTests(true);
                }
                problems.AddRange(Problems(window, "шаг " + step, code));
                if (shots)
                    ServiceFormsFitUiTests.Save(window, $"promotions-step{step}-{code}-{width}x{height}.png");
            }

            // Изменить запущенную: на шаге 5 — галочка «ниже закупки», на шаге 6 — пример корзины с товаром и результат предпросмотра.
            var api = FakePromotionsApi.Filled();
            var bread = api.Items.First(i => i.Id == "s1");
            window.OpenWizardForTests(bread);
            UiShot.Settle(6, 30);
            window.ShowStepForTests(6);
            UiShot.Settle(8, 40);
            UiDrive.AllText(window).Should().Contain("→", "предпросмотр посчитан");
            problems.AddRange(Problems(window, "изменить/шаг 6", code));
            if (shots)
                ServiceFormsFitUiTests.Save(window, $"promotions-edit-step6-{code}-{width}x{height}.png");
            window.ShowStepForTests(5);
            problems.AddRange(Problems(window, "изменить/шаг 5", code));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
        problems.Should().BeEmpty($"раздел «Акции» {width}×{height} {code}: поля не налезают, текст не обрезан\n  " + string.Join("\n  ", problems.Distinct().Take(30)));
    }

    [AvaloniaFact]
    public async Task EmptyList_ShowsPlaceholder_WithNewPromotionButton()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = PromotionsFixture.CreateWindow(new FakePromotionsApi());
        try
        {
            using (UiShot.Render(window, new UiSize(1024, 600)))
            {
            }
            UiShot.Settle(6, 30);
            UiDrive.AllText(window).Should().Contain("Акций пока нет");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Новая акция (пустой список)"));
            UiShot.Settle(3, 30);
            window.WizardOpenForTests.Should().BeTrue();
            window.CurrentStepForTests.Should().Be(1);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Create_UsesOneKey_RetriesAfter400WithNewKey_AndJumpsToTheField()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = FakePromotionsApi.Filled();
        var window = PromotionsFixture.CreateWindow(api);
        try
        {
            using (UiShot.Render(window, new UiSize(1366, 768)))
            {
            }
            UiShot.Settle(6, 30);
            window.OpenWizardForTests(null);
            UiShot.Settle(3, 30);

            // «Далее» без названия — остаёмся на шаге 1 с подсказкой.
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Далее"));
            window.CurrentStepForTests.Should().Be(1);
            window.WizardStatusForTests.Should().Contain("Укажите название акции");

            var title = window.GetVisualDescendants().OfType<TextBox>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Название акции");
            UiDrive.Type(window, title, "ТЕСТ 2+1 вода");
            window.ShowStepForTests(3);
            window.SetScopeAllForTests(true);
            window.ShowStepForTests(6);
            UiShot.Settle(3, 30);

            // Сервер отвечает 400 promotion_invalid{title} — мастер открывает шаг 1, ключ меняется (запрос точно не прошёл).
            api.NextCreateError = PromotionsFixture.Invalid("title", "Укажите название акции.");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Запустить"));
            UiShot.Settle(4, 30);
            window.CurrentStepForTests.Should().Be(1, "promotion_invalid{field:title} → шаг «Название»");
            window.WizardStatusForTests.Should().Contain("Исправьте на шаге «Название»");

            window.ShowStepForTests(6);
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Запустить"));
            UiShot.Settle(6, 30);
            window.WizardOpenForTests.Should().BeFalse("после сохранения — снова список");
            api.Creates.Should().HaveCount(2);
            api.Creates[1].Status.Should().Be("active", "«Запустить» создаёт сразу активной");
            api.Creates[0].Key.Should().NotBe(api.Creates[1].Key, "после 4xx — новый ключ");
            api.Items.Should().Contain(i => i.Title == "ТЕСТ 2+1 вода" && i.Status == "active" && i.ScopeAll);
            UiDrive.AllText(window).Should().Contain("запущена");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task NetworkFailure_KeepsTheSameKey_ForTheRetry()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = FakePromotionsApi.Filled();
        var window = PromotionsFixture.CreateWindow(api);
        try
        {
            using (UiShot.Render(window, new UiSize(1366, 768)))
            {
            }
            UiShot.Settle(6, 30);
            var draft = new Promotion { Title = "Черновик связи", Kind = Promotion.KindPercent, Percent = 10, ScopeAll = true };
            window.OpenWizardForTests(null);
            UiShot.Settle(3, 30);
            var title = window.GetVisualDescendants().OfType<TextBox>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Название акции");
            UiDrive.Type(window, title, draft.Title);
            window.ShowStepForTests(3);
            window.SetScopeAllForTests(true);
            window.ShowStepForTests(6);
            api.NextCreateError = new HttpRequestException("обрыв");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Сохранить черновиком"));
            UiShot.Settle(4, 30);
            window.WizardStatusForTests.Should().Contain("Нет связи");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Сохранить черновиком"));
            UiShot.Settle(6, 30);
            api.Creates.Should().HaveCount(2);
            api.Creates[0].Key.Should().Be(api.Creates[1].Key, "исход неизвестен — повтор тем же ключом, чтобы не создать вторую акцию");
            api.Creates[1].Status.Should().BeNull("черновик — без status");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task StartedPromotion_KindIsLocked_AndFinishAsksConfirmation()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = FakePromotionsApi.Filled();
        var window = PromotionsFixture.CreateWindow(api);
        var asked = new List<string>();
        PromotionsWindow.ConfirmForTests = m =>
        {
            asked.Add(m);
            return Task.FromResult(true);
        };
        try
        {
            using (UiShot.Render(window, new UiSize(1366, 768)))
            {
            }
            UiShot.Settle(6, 30);
            UiDrive.AllText(window).Replace(' ', ' ').Should().Contain("Сработала: 128 чек., скидка 25 600 сом", "из promotions/report/");

            UiDrive.Click(window, UiDrive.ButtonByText(window, "Изменить: 2+1 на воду"));
            UiShot.Settle(4, 30);
            window.ShowStepForTests(2);
            var cards = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("sec-chip") && b.MinHeight >= 72).ToList();
            cards.Should().HaveCount(4);
            cards.Count(c => c.IsEnabled).Should().Be(1, "у запущенной акции выбор вида заблокирован — доступна только текущая карточка");
            UiDrive.AllText(window).Should().Contain("вид менять нельзя");
            UiDrive.ButtonByText(window, "Сохранить изменения").IsVisible.Should().BeTrue();

            window.ShowStepForTests(1);
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Сохранить изменения"));
            UiShot.Settle(6, 30);
            api.Updates.Should().ContainSingle(u => u.Id == "a1");

            UiDrive.Click(window, UiDrive.ButtonByText(window, "Завершить: 2+1 на воду"));
            UiShot.Settle(6, 30);
            asked.Should().ContainSingle(m => m.Contains("Завершить акцию"));
            api.Actions.Should().Contain("finish:a1");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
