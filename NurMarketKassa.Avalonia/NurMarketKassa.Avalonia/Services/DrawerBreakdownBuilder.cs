using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-23 (аудит, круг 7): раскладка «в ящике должно быть» по слагаемым — общая для X-отчёта (ShiftDetailsDialog)
/// и окна «Закрыть смену» (CloseShiftDialog), чтобы не дублировать. Формула сверки не менялась: известные слагаемые
/// показаны как есть, остаток до итога — одной строкой «Корректировка по данным сервера», чтобы столбец складывался в итог.</summary>
public static class DrawerBreakdownBuilder
{
    private static string Som => Tr.T("сом", "сом", "som", "som", "so'm");

    public static void Fill(Control host, Panel panel, decimal opening, decimal cashSales, decimal debtPaidCash, decimal deposits,
        decimal withdrawals, decimal returns, decimal expected)
    {
        panel.Children.Clear();
        void Row(string label, decimal value, string sign, bool bold = false, string? tip = null)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var l = new TextBlock { Text = label, FontSize = 12, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal };
            var r = new TextBlock { Text = $"{sign}{value:N2} {Som}", FontSize = 12, TextAlignment = TextAlignment.Right, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal };
            l.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable(bold ? "BrushText" : "BrushTextSoft"));
            r.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable("BrushText"));
            Grid.SetColumn(r, 1);
            g.Children.Add(l);
            g.Children.Add(r);
            if (tip is not null)
                ToolTip.SetTip(g, tip);
            panel.Children.Add(g);
        }
        Row(Tr.T("Начальная сумма", "Баштапкы сумма", "Opening amount", "Başlangıç tutarı", "Boshlang'ich summa"), opening, "");
        Row(Tr.T("Наличные продажи (с предоплатой по долгам)", "Накталай сатуулар (карыздын алдынкы төлөмү менен)", "Cash sales (incl. debt prepayments)",
            "Nakit satışlar (borç ön ödemeleri dahil)", "Naqd sotuvlar (qarz oldindan to'lovlari bilan)"), cashSales, "+");
        if (debtPaidCash > 0m)
            Row(Tr.T("Оплата долгов наличными", "Карызды накталай төлөө", "Debt payments in cash", "Borç ödemeleri (nakit)", "Qarz to'lovlari (naqd)"), debtPaidCash, "+");
        if (deposits > 0m)
            Row(Tr.T("Внесения", "Киргизүүлөр", "Deposits", "Para girişleri", "Kiritishlar"), deposits, "+");
        if (withdrawals > 0m)
            Row(Tr.T("Изъятия", "Алуулар", "Withdrawals", "Para çıkışları", "Olishlar"), withdrawals, "−");
        if (returns > 0m)
            Row(Tr.T("Возвраты", "Кайтаруулар", "Returns", "İadeler", "Qaytarishlar"), returns, "−");
        var sum = opening + cashSales + debtPaidCash + deposits - withdrawals - returns;
        var rest = expected - sum;
        // 2026-10-23 (аудит, круг 8, В-7): строка «Корректировка» — только если |остаток| ≥ 0,01 (копеечный хвост округления не показываем).
        if (Math.Abs(rest) >= 0.01m)
            Row(Tr.T("Корректировка по данным сервера (возврат/предоплата)", "Сервер маалыматы боюнча оңдоо (кайтаруу/алдын ала төлөм)", "Adjustment from server data (return/prepayment)",
                    "Sunucu verisine göre düzeltme (iade/ön ödeme)", "Server ma'lumoti bo'yicha tuzatish (qaytarish/oldindan to'lov)"),
                Math.Abs(rest), rest > 0m ? "+" : "−", tip: Tr.T(
                    "Сервер учёл в сумме по системе то, чего нет в строках выше: возвращённую продажу, предоплаты по долгам, возврат безналом. Ящик под эту строку не подгоняйте — пересчитайте наличные.",
                    "Сервер система боюнча суммага жогорудагы саптарда жок нерселерди эске алды: кайтарылган сатуу, карыздын алдын ала төлөмдөрү, накталай эмес кайтаруу. Кутуну бул сапка тууралабаңыз — накталайды кайра санаңыз.",
                    "The server counted in the system amount things missing from the rows above: a returned sale, debt prepayments, a non-cash refund. Do not adjust the drawer to this row — recount the cash.",
                    "Sunucu, yukarıdaki satırlarda olmayanları sistem tutarına kattı: iade edilen satış, borç ön ödemeleri, nakitsiz iade. Çekmeceyi bu satıra göre ayarlamayın — nakdi yeniden sayın.",
                    "Server tizim summasiga yuqoridagi qatorlarda yo'q narsalarni qo'shdi: qaytarilgan sotuv, qarz oldindan to'lovlari, naqd emas qaytarish. Tortmani bu qatorga moslamang — naqdni qayta sanang."));
        Row(Tr.T("= В ящике должно быть", "= Кассада болушу керек", "= Should be in the drawer", "= Çekmecede olması gereken", "= Qutida bo'lishi kerak"), expected, "", true);
        panel.IsVisible = true;
    }
}
