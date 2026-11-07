using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NurMarketKassa.Services;

/// <summary>2026-11-07 (живая проверка 1.17.81, Б-C1): плитки «Выручка» и «Чеки» в «Продажах и финансах» считают только оплаченные чеки — как сайт NurCRM
/// (cards.revenue / cards.transactions), продажа в долг до оплаты в них не входит. В списке «Продажи» долговые чеки видны, и владелец видел «в списке 4 чека, плитка — 2».
/// Решение: плитки не меняются (совпадают с сайтом), а под «Чеками» показывается вторая строка «+ в долг: N на S сом» — сколько чеков и на какую сумму не вошло.
/// Чистые функции без окон — проверяются тестом (UiTests/Screens/ScreenFixesTests).</summary>
public static class SalesTileRules
{
    /// <summary>Продажа в долг, ещё не оплаченная: статус сервера «debt». Оплаченный долг получает статус paid и входит в плитки; офлайн-чек без статуса — тоже оплаченный.</summary>
    public static bool IsOpenDebt(string? status) => string.Equals(status, "debt", StringComparison.OrdinalIgnoreCase);

    /// <summary>Сколько неоплаченных долговых чеков и на какую сумму (из того же списка чеков, что показан ниже плиток).</summary>
    public static (int Count, decimal Amount) OpenDebt(IEnumerable<(string? Status, decimal Total)> receipts)
    {
        var debt = receipts.Where(r => IsOpenDebt(r.Status)).ToList();
        return (debt.Count, debt.Sum(r => r.Total));
    }

    /// <summary>Текст под плиткой «Чеки»: «+ в долг: 2 на 120,00 сом»; нет долговых чеков — пусто.</summary>
    public static string DebtNote(int count, decimal amount)
    {
        if (count <= 0)
            return "";
        var sum = MoneyText.Som(amount);
        return Tr.T($"+ в долг: {count} на {sum}", $"+ карызга: {count}, {sum}", $"+ on credit: {count} for {sum}",
            $"+ veresiye: {count} adet, {sum}", $"+ qarzga: {count} ta, {sum}");
    }

    /// <summary>Подсказка к строке: почему этих чеков нет в плитках.</summary>
    public static string DebtTip() => Tr.T(
        "Продажи в долг до оплаты в «Выручку», «Чеки» и «Средний чек» не входят — так считает и сайт. Когда клиент оплатит долг, чек войдёт в плитки.",
        "Карызга сатуулар төлөнгөнгө чейин «Түшүм», «Чектер» жана «Орточо чек» эсебине кирбейт — сайт да ошондой эсептейт. Кардар карызды төлөгөндө чек плиткаларга кирет.",
        "Sales on credit are not counted in Revenue, Receipts and Average receipt until paid — the website counts the same way. Once the client pays the debt, the receipt enters the tiles.",
        "Veresiye satışlar ödenene kadar Ciro, Fişler ve Ortalama fişe dahil edilmez — site de böyle sayar. Müşteri borcu ödeyince fiş karolara girer.",
        "Qarzga sotuvlar to'langunga qadar Tushum, Cheklar va O'rtacha chekka kirmaydi — sayt ham shunday hisoblaydi. Mijoz qarzni to'lagach, chek plitkalarga kiradi.");

    /// <summary>Показывает (или прячет) строку под плиткой «Чеки».</summary>
    public static void Show(TextBlock? target, IEnumerable<(string? Status, decimal Total)>? receipts)
    {
        if (target is null)
            return;
        var (count, amount) = receipts is null ? (0, 0m) : OpenDebt(receipts);
        target.Text = DebtNote(count, amount);
        target.IsVisible = count > 0;
        ToolTip.SetTip(target, count > 0 ? DebtTip() : null);
    }
}
