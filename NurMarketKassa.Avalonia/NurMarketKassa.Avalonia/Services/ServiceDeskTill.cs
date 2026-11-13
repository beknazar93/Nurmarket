using NurMarketKassa.AvaloniaHost.Views.MainKassir;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-13: что окна «Ремонт и сервис» и «Trade-in / скупка» могут сделать с кассой: внести/изъять деньги смены и положить строку в чек.
/// В программе владельца кассы нет — <see cref="Resolve"/> отдаёт null, и окна работают как журнал (деньги там не двигаются). Тест подставляет свою реализацию.</summary>
public interface IServiceDeskTill
{
    decimal CartTotal { get; }
    bool CartHasItems { get; }
    bool AddLine(string name, decimal amount, bool credit);
    Task<bool> CashAsync(bool withdrawal, decimal amount, string reason);
}

public static class ServiceDeskTill
{
    /// <summary>Только для проверок: подмена кассы.</summary>
    public static IServiceDeskTill? Override { get; set; }

    /// <summary>Печать текста на принтере чеков. Программа владельца без смены и ящика чек на принтер не печатает (печать по ESC/POS открывала бы ящик) — как у чека возврата.</summary>
    public static Task<string?> PrintReceiptText(string text) =>
        NurMarketKassa.Services.AppMode.IsOwner
            ? Task.FromResult<string?>(NurMarketKassa.Services.Tr.T("Печать — в кассе (в программе владельца чеки на принтер не печатаются).", "Басып чыгаруу — кассада (ээсинин программасында чектер принтерге басылбайт).", "Printing is done at the till (the owner's program does not print receipts).", "Yazdırma kasada yapılır (sahip programı fiş yazdırmaz).", "Chop etish — kassada (egasining dasturida cheklar printerga chop etilmaydi)."))
            : ReceiptHistoryService.PrintAsync(text);

    /// <summary>Касса, если открыто окно кассы (не программа владельца); иначе null.</summary>
    public static IServiceDeskTill? Resolve()
    {
        if (Override is not null)
            return Override;
        try
        {
            return App.GetRequiredService<MainWindowHostBridge>().Window is { } w ? new Till(w) : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class Till(MainWindow window) : IServiceDeskTill
    {
        public decimal CartTotal => window.ServiceDeskCartTotal;
        public bool CartHasItems => window.ServiceDeskCartHasItems;
        public bool AddLine(string name, decimal amount, bool credit) => window.ServiceDeskAddLine(name, amount, credit);
        public Task<bool> CashAsync(bool withdrawal, decimal amount, string reason) => window.ServiceDeskCashAsync(withdrawal, amount, reason);
    }
}
