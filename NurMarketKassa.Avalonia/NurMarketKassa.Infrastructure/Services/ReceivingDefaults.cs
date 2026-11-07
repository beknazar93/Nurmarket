using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06 («Приём товара»): цены строки по умолчанию. Закупка — последняя у выбранного поставщика (история закупок), иначе закупка из карточки
/// товара, иначе 0; продажа — текущая цена каталога; наценка считается из этих двух цен. Вручную введённую закупку не затираем (ReceivingLineVm.PurchaseIsManual).</summary>
public static class ReceivingDefaults
{
    /// <summary>Последняя известная закупка товара у поставщика (из закупок на сервере) и когда это было.</summary>
    public readonly record struct SupplierPrice(double Price, DateTime At);

    /// <summary>Последняя закупка у поставщика с этим названием из истории этой кассы (записи без поставщика не считаются). null — нет.</summary>
    public static SupplierPrice? FromHistory(IEnumerable<PurchaseReceivingService.HistoryEntry> history, string? supplierName)
    {
        var name = (supplierName ?? "").Trim();
        if (name.Length == 0)
            return null;
        SupplierPrice? best = null;
        foreach (var h in history)
        {
            if (h.PurchasePrice <= 0 || !string.Equals((h.SupplierName ?? "").Trim(), name, StringComparison.CurrentCultureIgnoreCase))
                continue;
            if (best is null || h.At.ToUniversalTime() > best.Value.At.ToUniversalTime())
                best = new SupplierPrice(h.PurchasePrice, h.At);
        }
        return best;
    }

    /// <summary>Самая свежая из двух: история кассы или закупки поставщика с сервера.</summary>
    public static SupplierPrice? Newest(SupplierPrice? local, SupplierPrice? server) =>
        local is null ? server : server is null ? local : server.Value.At.ToUniversalTime() > local.Value.At.ToUniversalTime() ? server : local;

    /// <summary>Последняя известная цена продажи товара из истории приёмок (когда в карточке её нет): «по прошлой закупке». 0 — нет.</summary>
    public static double LastSalePrice(IEnumerable<PurchaseReceivingService.HistoryEntry> history) =>
        history.Where(h => h.SalePrice is > 0).OrderByDescending(h => h.At.ToUniversalTime()).Select(h => h.SalePrice!.Value).FirstOrDefault();

    /// <summary>Закупка по умолчанию: поставщик (если есть история) → карточка товара → 0.</summary>
    public static (double Price, PurchaseDefaultSource Source) PurchaseFor(SupplierPrice? supplier, double cardPurchase) =>
        supplier is { Price: > 0 } sp ? (sp.Price, PurchaseDefaultSource.SupplierHistory)
        : cardPurchase > 0 ? (cardPurchase, PurchaseDefaultSource.Card)
        : (0, PurchaseDefaultSource.None);

    /// <summary>Подставить строке закупку по умолчанию (у нового товара истории нет — не трогаем). true — цена изменилась. Ручную цену не затирает.</summary>
    public static bool ApplyPurchase(ReceivingLineVm line, SupplierPrice? supplier)
    {
        if (line.IsNew || line.PurchaseIsManual)
            return false;
        var (price, source) = PurchaseFor(supplier, line.OriginalPurchasePrice);
        return line.TrySetDefaultPurchase(price, source);
    }
}

/// <summary>2026-11-06 (жалоба владельца «при приёмке очередь и порядок сбиваются», Закупки → Приём товара): порядок строк списка приёма. Порядок на экране — это и порядок строк
/// в отправляемом приходе. Новая строка встаёт НАВЕРХ (последний скан перед глазами), а товар, который уже есть в списке, остаётся на своём месте — повторный скан только
/// прибавляет количество (раньше строка перескакивала наверх и порядок «плыл»). Правки количества и цен, смена поставщика, «Применить ко всем», подтяжка цен — меняют строки
/// на месте, список не пересобирают.</summary>
public static class ReceivingListOrder
{
    /// <summary>Поставить строку, добавленную сканом/выбором. true — строка новая и вставлена наверх; false — она уже в списке и осталась на месте.</summary>
    public static bool Place(IList<ReceivingLineVm> lines, ReceivingLineVm line)
    {
        if (lines.IndexOf(line) >= 0)
            return false;
        lines.Insert(0, line);
        Renumber(lines);
        return true;
    }

    /// <summary>2026-11-07 (ТЗ «Порядковая нумерация товаров в накладной»): номера строк «№» = порядок добавления. Строка без номера (LineNo = 0) получает следующий
    /// после наибольшего; остальные номера сохраняются и уплотняются без пропусков (после удаления). Порядок строк на экране (новая — сверху) не меняется,
    /// а повтор одного товара отдельной строкой получает свой номер. Возвращает true, если какой-то номер изменился.</summary>
    public static bool Renumber(IEnumerable<ReceivingLineVm> lines)
    {
        var list = lines.ToList();
        var order = list.Select((l, i) => (Line: l, Index: i)).OrderBy(x => x.Line.LineNo > 0 ? x.Line.LineNo : int.MaxValue).ThenBy(x => x.Index).ToList();
        var changed = false;
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i].Line.LineNo == i + 1)
                continue;
            order[i].Line.LineNo = i + 1;
            changed = true;
        }
        return changed;
    }

    /// <summary>2026-11-07: обработчик CollectionChanged списка приёмки склада: убранной строке номер сбрасывается (вернётся — получит следующий), остальные уплотняются без пропусков.
    /// Перестановка «наверх» (Remove + Insert одной и той же строки) обработчик не вызывают — номер при этом не меняется.</summary>
    public static void OnListChanged(IEnumerable<ReceivingLineVm> lines, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var old in e.OldItems.OfType<ReceivingLineVm>())
                old.LineNo = 0;
        Renumber(lines);
    }

    /// <summary>2026-11-07: строки в порядке добавления («№» 1, 2, 3…) — в таком порядке они уходят в приход, и открытая позже накладная показывает те же номера.
    /// Строки без номера (0) сохраняют своё взаимное положение (сортировка устойчивая).</summary>
    public static List<ReceivingLineVm> InAddedOrder(IEnumerable<ReceivingLineVm> lines) => lines.OrderBy(l => l.LineNo > 0 ? l.LineNo : int.MaxValue).ToList();
}

/// <summary>2026-11-06: ответ на вопрос «Поставщик не выбран».</summary>
public enum NoSupplierChoice
{
    ChooseSupplier,
    WithoutSupplier,
    Cancel,
}

/// <summary>2026-11-06: что делать по «Оприходовать товары», когда поставщик может быть не выбран. Сервер приход документом «Закупки» принимает только с поставщиком
/// (suppliers/receipts/ требует поставщика), поэтому «без поставщика» — тот же путь, что у Склад → Приёмка: остаток и цены пишутся в товар
/// (PurchaseReceivingService.PostAsync с supplier = null). Долг поставщику и оплата при этом не создаются.</summary>
public static class ReceivingSupplierRule
{
    public enum Step
    {
        /// <summary>Поставщик выбран — как раньше, окно «Подтверждение оплаты».</summary>
        PostWithSupplier,
        /// <summary>Поставщика нет и ответа ещё нет — показать вопрос с тремя кнопками.</summary>
        Ask,
        /// <summary>«Выбрать поставщика» — вопрос закрыт, открыть список поставщиков.</summary>
        OpenSupplierPicker,
        /// <summary>«Оприходовать без поставщика» — без оплаты и долга.</summary>
        PostWithoutSupplier,
        /// <summary>«Отмена» — ничего не делать.</summary>
        Stop,
    }

    public static Step Resolve(bool supplierSelected, NoSupplierChoice? answer) =>
        supplierSelected ? Step.PostWithSupplier
        : answer switch
        {
            null => Step.Ask,
            NoSupplierChoice.ChooseSupplier => Step.OpenSupplierPicker,
            NoSupplierChoice.WithoutSupplier => Step.PostWithoutSupplier,
            _ => Step.Stop,
        };

    /// <summary>«В долг» (и «Предоплата» с остатком в долг) возможны только с поставщиком: без него долгу не перед кем.</summary>
    public static bool DebtAllowed(bool supplierSelected) => supplierSelected;
}
