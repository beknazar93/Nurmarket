using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public sealed class DeferredCartsDialogActions
{
    public Func<IReadOnlyList<DeferredCartEntry>, Task<bool>>? MergeIntoCurrentAsync { get; init; }

    public Func<DeferredCartEntry, Task<bool>>? OpenAsSeparateAsync { get; init; }

    /// <summary>2026-11-21 (владелец: «список отложенных должен показывать все»): чеки, отложенные кнопкой «Отложить», живут вкладками
    /// «Отложен ЧЧ:ММ» — раньше окно показывало только очередь (файл), а вкладки в нём не было. Id записи = id вкладки.</summary>
    public Func<IReadOnlyList<DeferredCartEntry>>? GetHeldTabs { get; init; }

    /// <summary>Чек из вкладки — это вкладка, а не запись очереди: «Открыть отдельным» переключает на неё, «Добавить в текущий» затем закрывает вкладку.</summary>
    public Func<string, bool>? IsHeldTab { get; init; }
}
