using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-19 (владелец, п. 6): движок интерактивного обучения — условия завершения шагов, переход, пропуск, «Назад», повтор, признак «пройдено», тексты на 5 языках.</summary>
[Collection("UserPreferences")]
public sealed class CoachTourEngineTests
{
    private static CoachSnapshot Idle => new(false, 0, 0);

    private static CoachTourEngine Engine(CoachSnapshot? initial = null) => new(CoachTour.DefaultSteps(), initial ?? Idle);

    private static int IndexOf(string id) => CoachTour.DefaultSteps().ToList().FindIndex(s => s.Id == id);

    private static void GoTo(CoachTourEngine engine, string id)
    {
        var guard = 0;
        while (engine.Current.Id != id && guard++ < 50)
            engine.Next();
        engine.Current.Id.Should().Be(id);
    }

    [Fact]
    public void ManualStep_Advances_OnlyOnNext_NotOnSnapshots()
    {
        var engine = Engine();
        engine.Current.Id.Should().Be("sphere");
        engine.Update(new CoachSnapshot(true, 3, 9)).Should().BeFalse("шаг-пояснение не проходится действием в кассе");
        engine.Index.Should().Be(0);
        engine.Next();
        engine.Index.Should().Be(1);
        engine.CompletedCount.Should().Be(1);
        engine.SkippedCount.Should().Be(0);
    }

    [Fact]
    public void MenuSteps_NeedRealOpenThenClose()
    {
        var engine = Engine();
        engine.Next();
        engine.Current.Id.Should().Be("menu-open");
        engine.Update(Idle).Should().BeFalse("меню не открыто — шаг не пройден");
        engine.Update(new CoachSnapshot(true, 0, 0)).Should().BeTrue();
        engine.Current.Id.Should().Be("menu-close");
        engine.Update(new CoachSnapshot(true, 0, 0)).Should().BeFalse("меню ещё открыто");
        engine.Update(Idle).Should().BeTrue();
        engine.Current.Id.Should().Be("devices");
        engine.IsStepDone(IndexOf("menu-open")).Should().BeTrue();
    }

    [Fact]
    public void FindStep_Completes_WhenLineAppearsInBasket()
    {
        var engine = Engine();
        GoTo(engine, "find");
        engine.Current.EnterTraining.Should().BeTrue("на этом шаге включается учебный режим");
        engine.Update(Idle).Should().BeFalse();
        engine.Update(new CoachSnapshot(false, 1, 0)).Should().BeTrue();
        engine.Current.Id.Should().Be("cart");
    }

    [Fact]
    public void PayStep_Completes_OnlyWhenNewTrainingSaleDone_NotBySalesAlreadyDone()
    {
        var engine = Engine(new CoachSnapshot(false, 0, 2)); // учебных продаж уже было две
        GoTo(engine, "pay");
        engine.Update(new CoachSnapshot(false, 1, 2)).Should().BeFalse("старые продажи не считаются");
        engine.Update(new CoachSnapshot(false, 0, 3)).Should().BeTrue();
        engine.Current.Id.Should().Be("shift");
    }

    [Fact]
    public void Skip_MarksSkipped_AndLastStepFinishes()
    {
        var engine = Engine();
        engine.Next(); // sphere (manual → «пройден»)
        engine.Next(); // menu-open пропущен без действия
        engine.SkippedCount.Should().Be(1);
        engine.IsStepDone(1).Should().BeFalse();
        GoTo(engine, "help");
        engine.IsFinished.Should().BeFalse();
        engine.Next();
        engine.IsFinished.Should().BeTrue("после последнего шага — экран «Обучение завершено!»");
        engine.Next(); // лишнее нажатие ничего не ломает
        engine.IsFinished.Should().BeTrue();
    }

    [Fact]
    public void Back_ReturnsToPreviousStep_AndDoesNotAutoSkipUntilStateChanges()
    {
        var engine = Engine();
        GoTo(engine, "find");
        engine.Update(new CoachSnapshot(false, 1, 0));
        engine.Current.Id.Should().Be("cart");
        engine.Back();
        engine.Current.Id.Should().Be("find");
        engine.IsStepDone(IndexOf("find")).Should().BeFalse("«Назад» стирает отметку шага");
        engine.Update(new CoachSnapshot(false, 1, 0)).Should().BeFalse("состояние то же — шаг сам не пролистывается");
        engine.Update(new CoachSnapshot(false, 2, 0)).Should().BeTrue("чек изменился — условие снова проверяется");
        new CoachTourEngine(CoachTour.DefaultSteps(), Idle).Back(); // с первого шага «Назад» ничего не делает
    }

    [Fact]
    public void Back_FromFinish_ReturnsToLastStep()
    {
        var engine = Engine();
        GoTo(engine, "help");
        engine.Next();
        engine.IsFinished.Should().BeTrue();
        engine.Back();
        engine.IsFinished.Should().BeFalse();
        engine.Current.Id.Should().Be("help");
    }

    [Fact]
    public void Restart_StartsOver_AndClearsMarks()
    {
        var engine = Engine();
        GoTo(engine, "help");
        engine.Next();
        engine.Restart();
        engine.IsFinished.Should().BeFalse();
        engine.Index.Should().Be(0);
        engine.CompletedCount.Should().Be(0);
        engine.SkippedCount.Should().Be(0);
    }

    [Fact]
    public void Abort_StopsReactingToSnapshots_AndRaisesChanged()
    {
        var engine = Engine();
        engine.Next();
        var changed = 0;
        engine.Changed += () => changed++;
        engine.Abort();
        engine.IsAborted.Should().BeTrue();
        changed.Should().Be(1);
        engine.Update(new CoachSnapshot(true, 0, 0)).Should().BeFalse();
        engine.Index.Should().Be(1);
    }

    [Fact]
    public void Update_RaisesChangedOnce_ForStepChange()
    {
        var engine = Engine();
        engine.Next();
        var changed = 0;
        engine.Changed += () => changed++;
        engine.Update(new CoachSnapshot(true, 0, 0));
        changed.Should().Be(1);
    }

    [Fact]
    public void Scenario_CoversUserPath_AndEveryStepHasFiveLanguages()
    {
        var steps = CoachTour.DefaultSteps();
        steps.Select(s => s.Id).Should().ContainInOrder("sphere", "menu-open", "menu-close", "devices", "find", "cart", "pay", "shift", "help");
        steps.Where(s => s.Completion != CoachCondition.Manual).Select(s => s.Id).Should().Contain(["menu-open", "menu-close", "find", "pay"], "настоящие действия, а не «Далее»");
        steps.Where(s => s.TargetKey.Length > 0).Select(s => s.TargetKey).Should().OnlyContain(k => new[] { "menu", "menuPanel", "input", "cart", "pay", "shift" }.Contains(k));
        var saved = UserPreferences.Instance.Language;
        try
        {
            var texts = new List<string>();
            foreach (var lang in Enum.GetValues<AppLanguage>())
            {
                UserPreferences.Instance.Language = lang;
                foreach (var step in steps)
                {
                    step.Title().Should().NotBeNullOrWhiteSpace();
                    step.Text().Should().NotBeNullOrWhiteSpace();
                    texts.Add(lang + "|" + step.Id + "|" + step.Text());
                }
            }
            // На разных языках тексты разные (не осталось копии русского в ky/en/tr/uz).
            foreach (var step in steps)
                texts.Where(t => t.Split('|')[1] == step.Id).Select(t => t.Split('|', 3)[2]).Distinct().Count().Should().Be(Enum.GetValues<AppLanguage>().Length, step.Id);
        }
        finally
        {
            UserPreferences.Instance.Language = saved;
        }
    }

    [Fact]
    public void DoneFlag_IsWrittenAndRead_InGivenFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nurmarket-coach-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            CoachTour.IsCompleted(folder).Should().BeFalse();
            CoachTour.MarkCompleted(folder);
            CoachTour.IsCompleted(folder).Should().BeTrue();
            File.Exists(Path.Combine(folder, CoachTour.DoneFlagFile)).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }
}
