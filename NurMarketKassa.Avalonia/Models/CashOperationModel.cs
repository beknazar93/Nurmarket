using Avalonia.Media;

namespace NurMarketKassa.Models;

public enum CashOperationKind
{
    Deposit, Withdrawal, ShiftOpen, ShiftClose, OpeningBalance, Other,
}

public sealed class CashOperationModel
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public string Type { get; init; } = "";
    public CashOperationKind Kind { get; init; } = CashOperationKind.Other;
    public decimal Amount { get; init; }
    public string Cashier { get; init; } = "";
    public string? Reason { get; init; }
    public string? Comment { get; init; }
    public string Note => string.IsNullOrWhiteSpace(Comment) ? Reason ?? "" : Comment;
    public string IconGlyph => Kind switch
    {
        CashOperationKind.Deposit or CashOperationKind.OpeningBalance => "\uE710",
        CashOperationKind.Withdrawal => "\uE711",
        CashOperationKind.ShiftOpen => "\uE8A7",
        CashOperationKind.ShiftClose => "\uE7E8",
        _ => "\uE8A5",
    };
    public IBrush IconBrush => Kind switch
    {
        CashOperationKind.Deposit or CashOperationKind.OpeningBalance or CashOperationKind.ShiftOpen => Brushes.SeaGreen,
        CashOperationKind.Withdrawal or CashOperationKind.ShiftClose => Brushes.Firebrick,
        _ => Brushes.Gray,
    };
    public string AmountDisplay => Kind is CashOperationKind.Withdrawal or CashOperationKind.ShiftClose
        ? $"-{Amount:N2} сом" : $"+{Amount:N2} сом";
    public IBrush AmountBrush => Kind is CashOperationKind.Withdrawal or CashOperationKind.ShiftClose
        ? Brushes.Firebrick : Brushes.SeaGreen;
    public static CashOperationKind ResolveKind(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return CashOperationKind.Other;
        var t = type.Trim();
        if (t.Contains("Внес", StringComparison.OrdinalIgnoreCase) || t.Contains("Начальн", StringComparison.OrdinalIgnoreCase))
            return t.Contains("Начальн", StringComparison.OrdinalIgnoreCase) ? CashOperationKind.OpeningBalance : CashOperationKind.Deposit;
        if (t.Contains("Изъят", StringComparison.OrdinalIgnoreCase)) return CashOperationKind.Withdrawal;
        if (t.Contains("Открыт", StringComparison.OrdinalIgnoreCase)) return CashOperationKind.ShiftOpen;
        if (t.Contains("Закрыт", StringComparison.OrdinalIgnoreCase)) return CashOperationKind.ShiftClose;
        return CashOperationKind.Other;
    }
}