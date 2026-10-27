namespace NurMarketKassa.Services;

/// <summary>2026-10-27: ход Нур Советника «открой …» без нейросети и общий предел ожидания ответа. Чистая логика (без окон): окно программы
/// передаёт сюда функцию открытия раздела, которая возвращает ФАКТ (<see cref="NavOutcome"/>), и показывает текст, построенный из факта.
/// Модель не участвует и ничего «не обещает»: «Открыл …» пишется только после проверки, что раздел стал активным.</summary>
public static class AdvisorTurn
{
    public enum ReplyKind
    {
        /// <summary>Раздел открыт (факт проверен).</summary>
        Opened,
        /// <summary>Не получилось открыть (нет прав, скрыт, вкладка недоступна).</summary>
        Failed,
        /// <summary>Ничья — кнопки вариантов.</summary>
        Choose,
        /// <summary>Команда «открой …», но такого раздела нет.</summary>
        Unknown,
        /// <summary>Не переход — отвечает обычный путь.</summary>
        NotNav,
    }

    public sealed record NavReply(ReplyKind Kind, string Text, IReadOnlyList<AdvisorNavRouter.NavTarget> Options,
        AdvisorNavRouter.NavTarget? Target, AdvisorNavRouter.NavTarget? Companion, IReadOnlyList<string> Residual, NavOutcome? Outcome, long Ms);

    /// <summary>Ответ из факта открытия: «Открыл «Склад → Приёмка»» / «Открыл «Склад», но вкладку … выбрать не удалось» / «Не получилось открыть …».</summary>
    public static string FactText(AdvisorNavRouter.NavTarget target, NavOutcome outcome)
    {
        var wanted = target.Title;
        if (outcome.Ok && outcome.Problem is null)
            return Tr.T($"Открыл «{outcome.Shown}».", $"«{outcome.Shown}» ачылды.", $"Opened “{outcome.Shown}”.", $"«{outcome.Shown}» açıldı.", $"«{outcome.Shown}» ochildi.");
        if (outcome.Ok)
            return Tr.T($"Открыл «{outcome.Shown}», но «{wanted}» показать не получилось: {outcome.Problem}.",
                $"«{outcome.Shown}» ачылды, бирок «{wanted}» көрсөтүү болгон жок: {outcome.Problem}.",
                $"Opened “{outcome.Shown}”, but couldn't show “{wanted}”: {outcome.Problem}.",
                $"«{outcome.Shown}» açıldı, ancak «{wanted}» gösterilemedi: {outcome.Problem}.",
                $"«{outcome.Shown}» ochildi, lekin «{wanted}» ko'rsatib bo'lmadi: {outcome.Problem}.");
        return Tr.T($"Не получилось открыть «{wanted}»: {outcome.Problem ?? "раздел не открылся"}.",
            $"«{wanted}» ачуу болгон жок: {outcome.Problem ?? "бөлүм ачылган жок"}.",
            $"Couldn't open “{wanted}”: {outcome.Problem ?? "the section did not open"}.",
            $"«{wanted}» açılamadı: {outcome.Problem ?? "bölüm açılmadı"}.",
            $"«{wanted}» ochib bo'lmadi: {outcome.Problem ?? "bo'lim ochilmadi"}.");
    }

    public static string ChooseText(IReadOnlyList<AdvisorNavRouter.NavTarget> options) =>
        Tr.T("Не уверен, какой раздел нужен. Выберите:", "Кайсы бөлүм керек экенин так билбейм. Тандаңыз:",
            "I'm not sure which section you mean. Choose one:", "Hangi bölümü kastettiğinizden emin değilim. Seçin:", "Qaysi bo'lim kerakligiga ishonchim komil emas. Tanlang:");

    public static string UnknownText(IReadOnlyList<string> residual) =>
        Tr.T($"Не нашёл в программе такого раздела{(residual.Count > 0 ? " («" + string.Join(" ", residual) + "»)" : "")}. Откройте нужный из списка:",
            $"Программадан мындай бөлүм табылган жок{(residual.Count > 0 ? " («" + string.Join(" ", residual) + "»)" : "")}. Тизмеден тандаңыз:",
            $"I couldn't find such a section{(residual.Count > 0 ? " (“" + string.Join(" ", residual) + "”)" : "")}. Pick one from the list:",
            $"Programda böyle bir bölüm bulamadım{(residual.Count > 0 ? " («" + string.Join(" ", residual) + "»)" : "")}. Listeden seçin:",
            $"Dasturda bunday bo'lim topilmadi{(residual.Count > 0 ? " («" + string.Join(" ", residual) + "»)" : "")}. Ro'yxatdan tanlang:");

    /// <summary>Разделы для кнопок, когда названного нет.</summary>
    public static IReadOnlyList<AdvisorNavRouter.NavTarget> Popular() =>
        new[] { "warehouse", "salesfinance", "overview", "settings" }.Select(AdvisorNavRouter.ById).Where(t => t is not null).Select(t => t!).ToList();

    /// <summary>Открыть один пункт и построить ответ из факта. <paramref name="open"/> обязан вернуть результат ПРОВЕРКИ (раздел активен).</summary>
    public static async Task<NavReply> OpenAsync(AdvisorNavRouter.NavTarget target, Func<string, Task<NavOutcome>> open,
        AdvisorNavRouter.NavTarget? companion = null, IReadOnlyList<string>? residual = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        NavOutcome outcome;
        try
        {
            outcome = await open(target.Id).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"ИИ-советник: переход «{target.TitleRu}» упал ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            outcome = new NavOutcome(false, "", Tr.T("ошибка при открытии", "ачууда ката кетти", "error while opening", "açılırken hata", "ochishda xato"));
        }
        PosLogger.Log($"ADVISOR_PERF nav: «{target.TitleRu}» — {(outcome.Ok ? "открыто" : "не открыто")} за {watch.ElapsedMilliseconds} мс (без нейросети).", "INFO");
        return new NavReply(outcome.Ok ? ReplyKind.Opened : ReplyKind.Failed, FactText(target, outcome), Array.Empty<AdvisorNavRouter.NavTarget>(),
            target, companion, residual ?? Array.Empty<string>(), outcome, watch.ElapsedMilliseconds);
    }

    /// <summary>Разобрать фразу и, если это понятный переход, выполнить его. NotNav — отдать фразу обычному пути.</summary>
    public static async Task<NavReply> NavigateAsync(string? question, Func<string, Task<NavOutcome>> open)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var route = AdvisorNavRouter.Parse(question);
        switch (route.Kind)
        {
            case AdvisorNavRouter.RouteKind.Single:
                return await OpenAsync(route.Targets[0], open, route.Companion, route.Residual).ConfigureAwait(false);
            case AdvisorNavRouter.RouteKind.Ambiguous:
                PosLogger.Log($"ADVISOR_PERF nav: ничья из {route.Targets.Count} пунктов за {watch.ElapsedMilliseconds} мс.", "INFO");
                return new NavReply(ReplyKind.Choose, ChooseText(route.Targets), route.Targets, null, route.Companion, route.Residual, null, watch.ElapsedMilliseconds);
            case AdvisorNavRouter.RouteKind.Unknown:
                return new NavReply(ReplyKind.Unknown, UnknownText(route.Residual), Popular(), null, null, route.Residual, null, watch.ElapsedMilliseconds);
            default:
                return new NavReply(ReplyKind.NotNav, "", Array.Empty<AdvisorNavRouter.NavTarget>(), null, null, Array.Empty<string>(), null, watch.ElapsedMilliseconds);
        }
    }

    /// <summary>Общий предел ожидания ответа: работа получает свой токен, который отменяется по истечении срока. TimedOut — «ИИ долго отвечает — повторить?».
    /// Отмена владельцем (<paramref name="ct"/>) пробрасывается как обычно.</summary>
    public static async Task<(T? Value, bool TimedOut)> WithBudget<T>(Func<CancellationToken, Task<T>> work, TimeSpan budget, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = work(linked.Token);
        var delay = Task.Delay(budget, ct);
        var first = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (first == task)
            return (await task.ConfigureAwait(false), false);
        ct.ThrowIfCancellationRequested();
        linked.Cancel();
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return (default, true);
    }

    /// <summary>Замер этапа ответа в журнал: «ADVISOR_PERF сводка 120 мс; модель gemini-… ; …».</summary>
    public static void Perf(string stage, long ms, string? detail = null) =>
        PosLogger.Log($"ADVISOR_PERF {stage}: {ms} мс{(string.IsNullOrEmpty(detail) ? "" : " — " + detail)}.", "INFO");
}
