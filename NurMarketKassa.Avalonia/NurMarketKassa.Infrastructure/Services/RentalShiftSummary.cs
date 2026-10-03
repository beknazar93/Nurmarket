using System.Globalization;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-04, клиент: «при закрытии смены на Z-отчёт вывести информацию и об аренде». Прокат за
/// время смены по документам проката сервера (GET /api/rentals/): выдано и возвращено, залоги деньгами
/// (приняты, возвращены, удержаны), штрафы; сейчас на руках и из них просрочено.</summary>
public sealed record RentalShiftSummary(
    int Issued,
    double DepositsTaken,
    int DocumentDeposits,
    int Returned,
    double DepositsRefunded,
    double DepositsWithheld,
    double Penalties,
    int OnHand,
    int Overdue)
{
    /// <summary>Проката не было и нет на руках — блок в отчёте не нужен.</summary>
    public bool IsEmpty => Issued == 0 && Returned == 0 && OnHand == 0;

    public static RentalShiftSummary Compute(IEnumerable<RentalDto> rentals, DateTime? from, DateTime? to)
    {
        var start = from ?? DateTime.Today;
        var end = to ?? DateTime.Now;
        bool In(DateTimeOffset? at) => at is { } value && value.LocalDateTime >= start && value.LocalDateTime <= end;

        int issued = 0, documents = 0, returned = 0, onHand = 0, overdue = 0;
        double taken = 0, refunded = 0, withheld = 0, penalties = 0;
        foreach (var r in rentals)
        {
            if (In(r.CreatedAt))
            {
                issued++;
                if (r.IsDocumentDeposit)
                    documents++;
                else
                    taken += r.DepositAmount;
            }
            if (In(r.ReturnedAt))
            {
                returned++;
                refunded += r.DepositRefunded;
                withheld += r.DepositWithheld;
                penalties += r.Penalty;
            }
            if (r.IsActive)
            {
                onHand++;
                if (r.IsOverdue)
                    overdue++;
            }
        }
        return new RentalShiftSummary(issued, taken, documents, returned, refunded, withheld, penalties, onHand, overdue);
    }

    private static string M(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Строки для печатного отчёта смены (печать — по-русски, как весь отчёт).</summary>
    public IEnumerable<string> PrintLines()
    {
        yield return "ПРОКАТ";
        yield return $"Выдано: {Issued}" + (DepositsTaken > 0.005 ? $", залог +{M(DepositsTaken)} сом" : "")
            + (DocumentDeposits > 0 ? $", под документ: {DocumentDeposits}" : "");
        yield return $"Возвращено: {Returned}" + (DepositsRefunded > 0.005 ? $", залог -{M(DepositsRefunded)} сом" : "");
        if (DepositsWithheld > 0.005)
            yield return $"Удержано из залога: {M(DepositsWithheld)} сом";
        if (Penalties > 0.005)
            yield return $"Штрафы: {M(Penalties)} сом";
        yield return $"На руках сейчас: {OnHand}" + (Overdue > 0 ? $", просрочено: {Overdue}" : "");
    }

    /// <summary>Те же строки на языке программы — для окна отчёта.</summary>
    public string DisplayText()
    {
        var lines = new List<string>
        {
            Tr.T($"Выдано: {Issued}", $"Берилди: {Issued}", $"Issued: {Issued}", $"Verilen: {Issued}", $"Berildi: {Issued}")
                + (DepositsTaken > 0.005 ? Tr.T($", залог +{M(DepositsTaken)} сом", $", күрөө +{M(DepositsTaken)} сом", $", deposit +{M(DepositsTaken)} som", $", depozito +{M(DepositsTaken)} som", $", garov +{M(DepositsTaken)} so'm") : "")
                + (DocumentDeposits > 0 ? Tr.T($", под документ: {DocumentDeposits}", $", документ менен: {DocumentDeposits}", $", against a document: {DocumentDeposits}", $", belge karşılığı: {DocumentDeposits}", $", hujjat evaziga: {DocumentDeposits}") : ""),
            Tr.T($"Возвращено: {Returned}", $"Кайтарылды: {Returned}", $"Returned: {Returned}", $"İade edilen: {Returned}", $"Qaytarildi: {Returned}")
                + (DepositsRefunded > 0.005 ? Tr.T($", залог −{M(DepositsRefunded)} сом", $", күрөө −{M(DepositsRefunded)} сом", $", deposit −{M(DepositsRefunded)} som", $", depozito −{M(DepositsRefunded)} som", $", garov −{M(DepositsRefunded)} so'm") : ""),
        };
        if (DepositsWithheld > 0.005)
            lines.Add(Tr.T($"Удержано из залога: {M(DepositsWithheld)} сом", $"Күрөөдөн кармалды: {M(DepositsWithheld)} сом", $"Withheld from deposits: {M(DepositsWithheld)} som", $"Depozitodan kesilen: {M(DepositsWithheld)} som", $"Garovdan ushlab qolindi: {M(DepositsWithheld)} so'm"));
        if (Penalties > 0.005)
            lines.Add(Tr.T($"Штрафы: {M(Penalties)} сом", $"Айыптар: {M(Penalties)} сом", $"Penalties: {M(Penalties)} som", $"Cezalar: {M(Penalties)} som", $"Jarimalar: {M(Penalties)} so'm"));
        lines.Add(Tr.T($"На руках сейчас: {OnHand}", $"Азыр колдо: {OnHand}", $"Currently out: {OnHand}", $"Şu an dışarıda: {OnHand}", $"Hozir qo'lda: {OnHand}")
            + (Overdue > 0 ? Tr.T($", просрочено: {Overdue}", $", мөөнөтү өткөн: {Overdue}", $", overdue: {Overdue}", $", gecikmiş: {Overdue}", $", muddati o'tgan: {Overdue}") : ""));
        return string.Join("\n", lines);
    }
}
