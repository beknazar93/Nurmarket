using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-22 (аудит новой сборки, П1): тексты про смену, закрытую сервером (автозакрытие NurCRM в 23:00),
/// на 5 языках — для шапки, блокировки оплаты/возврата/внесения и одного уведомления с итогом.</summary>
internal static class ServerShiftTexts
{
    /// <summary>Короткая подпись для шапки и меню.</summary>
    public static string Short(ShiftClosedByServer info)
    {
        var t = ServerShiftClosedState.TimeText(info);
        return t.Length == 0
            ? Tr.T("Смена закрыта сервером", "Смена сервер тарабынан жабылды", "Shift closed by the server", "Vardiya sunucu tarafından kapatıldı", "Smena server tomonidan yopilgan")
            : Tr.T($"Смена закрыта сервером в {t}", $"Смена сервер тарабынан {t} жабылды", $"Shift closed by the server at {t}", $"Vardiya sunucu tarafından {t} kapatıldı", $"Smena server tomonidan {t} da yopilgan");
    }

    /// <summary>Текст блокировки операции: чек в корзине остаётся, нужно открыть новую смену.</summary>
    public static string Block(ShiftClosedByServer info)
    {
        var t = ServerShiftClosedState.TimeText(info);
        return t.Length == 0
            ? Tr.T("Смена закрыта сервером — откройте новую смену.", "Смена сервер тарабынан жабылды — жаңы смена ачыңыз.", "The shift was closed by the server — open a new shift.", "Vardiya sunucu tarafından kapatıldı — yeni vardiya açın.", "Smena server tomonidan yopilgan — yangi smena oching.")
            : Tr.T($"Смена закрыта сервером в {t} — откройте новую смену.", $"Смена сервер тарабынан {t} жабылды — жаңы смена ачыңыз.", $"The shift was closed by the server at {t} — open a new shift.", $"Vardiya sunucu tarafından {t} kapatıldı — yeni vardiya açın.", $"Smena server tomonidan {t} da yopilgan — yangi smena oching.");
    }

    /// <summary>Одно уведомление с итогом закрытой смены (ожидалось/закрыто, продажи).</summary>
    public static string Summary(ShiftClosedByServer info)
    {
        var parts = new List<string> { Short(info) + "." };
        if (info.ExpectedCash is { } exp)
        {
            var closed = info.ClosingCash is { } c ? MoneyText.Som(c) : "—";
            var expText = MoneyText.Som(exp);
            parts.Add(Tr.T($"Ожидалось {expText}, закрыто на {closed}.", $"Күтүлгөн {expText}, жабылган {closed}.",
                $"Expected {expText}, closed at {closed}.", $"Beklenen {expText}, kapanış {closed}.", $"Kutilgan {expText}, yopilgan {closed}."));
        }
        if (info.TotalSales is { } sales)
        {
            var salesText = MoneyText.Som(sales);
            var cnt = info.SalesCount is { } n ? $" ({n})" : "";
            parts.Add(Tr.T($"Продажи: {salesText}{cnt}.", $"Сатуулар: {salesText}{cnt}.", $"Sales: {salesText}{cnt}.", $"Satışlar: {salesText}{cnt}.", $"Sotuvlar: {salesText}{cnt}."));
        }
        parts.Add(Tr.T("Чек в корзине сохранён — откройте новую смену.", "Себеттеги чек сакталды — жаңы смена ачыңыз.",
            "The receipt in the basket is kept — open a new shift.", "Sepetteki fiş korundu — yeni vardiya açın.", "Savatdagi chek saqlangan — yangi smena oching."));
        return string.Join(" ", parts);
    }
    // 2026-10-22 (смена 23 ч): предупреждение заранее — сервер закроет смену сам через 24 ч (ближайший целый час).
    /// <summary>Плашка в шапке, уведомление и строка под чеком (один текст).</summary>
    public static string AgeWarning(DateTime openedAt, DateTime now)
    {
        var h = ShiftAgeWarning.WholeHours(openedAt, now);
        var t = ShiftAgeWarning.ServerCloseAt(openedAt).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return Tr.T($"Смена идёт {h} ч — закройте заранее: сервер закроет её сам около {t}",
            $"Смена {h} саат бою ачык — алдын ала жабыңыз: сервер өзү {t} чамасында жабат",
            $"The shift has been open for {h} h — close it in advance: the server will close it itself around {t}",
            $"Vardiya {h} saattir açık — önceden kapatın: sunucu kendisi yaklaşık {t} civarında kapatacak",
            $"Smena {h} soatdan beri ochiq — oldindan yoping: server o'zi taxminan {t} da yopadi");
    }

    /// <summary>Подпись кнопки в плашке.</summary>
    public static string CloseShiftButton() =>
        Tr.T("Закрыть смену", "Сменаны жабуу", "Close shift", "Vardiyayı kapat", "Smenani yopish");
}
