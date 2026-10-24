using Avalonia.Media;
using NurMarketKassa.Services;

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
    // 2026-10-22 (статьи изъятия): статья операции — ключ (CashArticles), её русское название (канон для сервера, чека и
    // отчётов владельца) и комментарий кассира отдельно. Пусто — операция до статей или из «Доп. услуги → Расход».
    public string? ArticleKey { get; init; }
    public string? ArticleRu { get; init; }
    public string? ArticleComment { get; init; }
    public bool HasArticle => !string.IsNullOrWhiteSpace(ArticleKey);
    /// <summary>Статья на текущем языке (журнал смены, окно закрытия смены).</summary>
    public string ArticleTitle => HasArticle
        ? NurMarketKassa.Services.CashArticles.TitleFor(Kind == CashOperationKind.Withdrawal, ArticleKey, ArticleRu)
        : "";
    /// <summary>Строка под типом в журнале смены: «Аренда — за октябрь»; без статьи — причина/комментарий как раньше.</summary>
    public string NoteDisplay => HasArticle
        ? (string.IsNullOrWhiteSpace(ArticleComment) ? ArticleTitle : $"{ArticleTitle} — {ArticleComment!.Trim()}")
        : Note;
    public bool HasNoteDisplay => !string.IsNullOrWhiteSpace(NoteDisplay);
    /// <summary>Основание на бумажном чеке (чек — по-русски, как весь расходный/приходный чек).</summary>
    public string ReceiptNote => HasArticle
        ? (string.IsNullOrWhiteSpace(ArticleComment) ? ArticleRu ?? "" : $"{ArticleRu} — {ArticleComment!.Trim()}")
        : Note;
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
        ? Tr.T($"-{MoneyText.Amount(Amount)} сом", $"-{MoneyText.Amount(Amount)} сом", $"-{MoneyText.Amount(Amount)} som", $"-{MoneyText.Amount(Amount)} som", $"-{MoneyText.Amount(Amount)} so'm") : Tr.T($"+{MoneyText.Amount(Amount)} сом", $"+{MoneyText.Amount(Amount)} сом", $"+{MoneyText.Amount(Amount)} som", $"+{MoneyText.Amount(Amount)} som", $"+{MoneyText.Amount(Amount)} so'm");
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