namespace NurMarketKassa.Services.Fiscal;

/// <summary>2026-11-14: МЕСТО и ШАБЛОН под фискальные реквизиты в печатном чеке — пока ВЫКЛЮЧЕНО (<see cref="Enabled"/> = false, пока нет рабочего провайдера ККМ).
/// В печать чека эта часть НЕ подключена: когда появится провайдер eKassa (Ф5), печатный чек вызовет <see cref="Lines"/> после строки «ИТОГО» и заменит заголовок
/// «Товарный чек» на «Кассовый чек» (по документации ККМ). Нефискальный чек никогда не выдаётся за фискальный: без принятого чека (accepted) строк нет.</summary>
public static class FiscalReceiptTemplate
{
    /// <summary>Печатать реквизиты и QR в чеке: сервер знает фискализацию, режим kassa, провайдер подключён. Сейчас всегда false (заглушка «ЭККМ не подключена»).</summary>
    public static bool Enabled => FiscalSettings.KassaMustFiscalize && FiscalProviders.Current.IsConnected;

    /// <summary>Строки реквизитов для печати шириной <paramref name="width"/> знаков (ФД, ФМ, РН ККМ, признак, время). Пусто, если шаблон выключен или чек не принят.
    /// QR — отдельно: <see cref="QrPayload"/> (его рисует принтер или картинка).</summary>
    public static IReadOnlyList<string> Lines(FiscalRequisites? requisites, int width = 32, bool? enabled = null)
    {
        if (!(enabled ?? Enabled) || requisites is not { Status: FiscalStatus.Accepted })
            return Array.Empty<string>();
        var lines = new List<string>();
        void Add(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            var text = label + ": " + value!.Trim();
            lines.Add(text.Length > width ? text[..width] : text);
        }
        Add(Tr.T("ФД", "ФД", "FD", "FB", "FH"), requisites.DocNumber);
        Add(Tr.T("ФМ", "ФМ", "FM", "FM", "FM"), requisites.MemoryNumber);
        Add(Tr.T("РН ККМ", "ККМ РН", "KKM reg. no.", "ÖKC sicil no", "KKM RN"), requisites.KkmRegNumber);
        Add(Tr.T("Фискальный признак", "Фискалдык белги", "Fiscal sign", "Mali işaret", "Fiskal belgi"), requisites.Sign);
        if (requisites.At is { } at)
            Add(Tr.T("Время ККМ", "ККМ убактысы", "Cash register time", "ÖKC saati", "KKM vaqti"), at.ToLocalTime().ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        return lines;
    }

    /// <summary>Текст для QR в чеке: ссылка проверки от ККМ (fiscal_qr_url). null — QR печатать нечем.</summary>
    public static string? QrPayload(FiscalRequisites? requisites, bool? enabled = null) =>
        (enabled ?? Enabled) && requisites is { Status: FiscalStatus.Accepted, QrUrl: { Length: > 0 } url } ? url.Trim() : null;
}
