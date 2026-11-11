using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-11 (О-5 «Неоконченная приёмка пропадает»): общая часть интерфейса черновика приёмки для «Склад → Приёмка» и «Закупки → Приём товара» —
/// вопрос «Продолжить / Начать заново», запись при закрытии и потере фокуса окна, тихая подпись «Черновик сохранён чч:мм». Сама логика — ReceivingDraftSession (Infrastructure).</summary>
public static class ReceivingDraftUi
{
    /// <summary>Окно вопроса при открытии приёмки (отдельно от показа — чтобы проверить вид и текст без ожидания ответа).
    /// Обычный черновик: «Продолжить» (по умолчанию) / «Начать заново». Старше 30 дней: «Удалить черновик» / «Всё же продолжить».</summary>
    public static PosConfirmDialog BuildQuestion(ReceivingDraftInfo info) =>
        info.Stale
            ? new PosConfirmDialog(ReceivingDraftTexts.Title, ReceivingDraftTexts.Question(info), ReceivingDraftTexts.DeleteDraftText, ReceivingDraftTexts.ContinueAnywayText, PosConfirmAccent.Danger)
            : new PosConfirmDialog(ReceivingDraftTexts.Title, ReceivingDraftTexts.Question(info), ReceivingDraftTexts.ContinueText, ReceivingDraftTexts.FreshText);

    /// <summary>Подтверждение удаления («Начать заново»).</summary>
    public static PosConfirmDialog BuildDeleteConfirm(ReceivingDraftInfo info) =>
        new(ReceivingDraftTexts.DeleteTitle, ReceivingDraftTexts.DeleteQuestion(info), ReceivingDraftTexts.DeleteText, ReceivingDraftTexts.BackText, PosConfirmAccent.Danger);

    /// <summary>Как показать окно вопроса. По умолчанию — общий модальный показ кассы (PosDialogHost: затемнение, вложенный цикл). Сквозные тесты окон подменяют на обычный
    /// <c>ShowDialog</c> без вложенного цикла: вложенный цикл в Headless не возвращает управление тесту, пока окно открыто (так завис прогон 11.11).</summary>
    public static Func<Window, Window?, Task<bool?>> ShowModal { get; set; } = (dialog, owner) => PosDialogHost.ShowModalAsync(dialog, owner);

    /// <summary>Спросить кассира. true — «Продолжить» (строки грузить), false — «Начать заново» (удаление подтверждено, черновик удалять). «Назад» из подтверждения возвращает к вопросу.</summary>
    public static async Task<bool> AskContinueAsync(Window? owner, ReceivingDraftInfo info)
    {
        while (true)
        {
            var first = await ShowModal(BuildQuestion(info), owner).ConfigureAwait(true) == true;
            var wantsFresh = info.Stale ? first : !first; // у старого черновика главная кнопка — «Удалить»
            if (!wantsFresh)
                return true;
            if (await ShowModal(BuildDeleteConfirm(info), owner).ConfigureAwait(true) == true)
                return false;
        }
    }

    /// <summary>Записать черновик при сворачивании/потере фокуса (в фоне) и при закрытии окна (дожидаясь записи). Подписки снимаются вместе с окном.</summary>
    public static void WireFlush(Window window, ReceivingDraftSession session)
    {
        window.Deactivated += (_, _) => session.Flush(wait: false);
        window.Closed += (_, _) =>
        {
            try { session.Flush(wait: true); }
            finally { session.Dispose(); }
        };
    }

    /// <summary>Тихая подпись «Черновик сохранён чч:мм»: мелкий мягкий текст, без шума — виден только после первой записи и прячется, когда черновик удалён.</summary>
    public static TextBlock CreateCaption(Control themeHost)
    {
        var caption = new TextBlock
        {
            FontSize = 11.5, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 4, 0),
        };
        caption.Bind(TextBlock.ForegroundProperty, themeHost.GetResourceObservable("BrushTextSoft"));
        ToolTip.SetTip(caption, ReceivingDraftTexts.SavedTip);
        Avalonia.Automation.AutomationProperties.SetName(caption, ReceivingDraftTexts.SavedTip);
        return caption;
    }

    /// <summary>Связать подпись с сессией (события приходят в потоке UI).</summary>
    public static void BindCaption(TextBlock caption, ReceivingDraftSession session)
    {
        session.Saved += at =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                caption.Text = ReceivingDraftTexts.SavedCaption(at);
                caption.IsVisible = true;
            });
        };
        session.Cleared += () => Dispatcher.UIThread.Post(() => caption.IsVisible = false);
    }
}
