namespace NurMarketKassa.Services;

/// <summary>2026-11-01 (живая проверка NBS, Д-1): одно место, где «ожидаемо в ящике» сервера (expected_cash смены) получает поправку на известные ошибки сервера
/// (возврат долгового чека, обмен, предоплата безналом, выдача кассы больше записанной сервером — ExchangeCashCorrections). Так одно и то же число видят «Касса:» в шапке
/// (там поправка входит через ShiftCashOperationsStore.NetForShift), X/Z-отчёт, сводка в Telegram, утренний отчёт владельца и выплата зарплаты из ящика.
/// Infrastructure не знает проекта кассы, где лежит файл поправок, поэтому источник подставляется при запуске (App.axaml.cs); пока не подставлен или поправок нет — сумма сервера как есть.</summary>
public static class ShiftExpectedCash
{
    /// <summary>Сумма поправок по смене (id смены → сумма, обычно отрицательная). По умолчанию — ноль.</summary>
    public static Func<string?, decimal> CorrectionProvider { get; set; } = _ => 0m;

    /// <summary>Поправка по смене; сбой источника — 0 (отчёт не должен падать из-за файла поправок).</summary>
    public static decimal Correction(string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return 0m;
        try
        {
            return CorrectionProvider(shiftId);
        }
        catch
        {
            return 0m;
        }
    }

    /// <summary>Ожидаемая сумма сервера + поправка. null (сервер сумму не дал) остаётся null — вызывающий код берёт свою запасную формулу.</summary>
    public static decimal? WithFix(decimal? serverExpected, string? shiftId) =>
        serverExpected is { } expected ? expected + Correction(shiftId) : null;
}
