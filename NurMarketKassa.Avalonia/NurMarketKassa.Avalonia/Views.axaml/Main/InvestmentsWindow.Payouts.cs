using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using ChartPalette = NurMarketKassa.Core.Application.ChartPalette;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Вкладка «Выплаты» (макет «Распределение прибыли»): ведомость по месяцам для выбранного инвестора — чистая прибыль, база расчёта,
/// перенос убытка, этап, сколько инвестору и владельцу, статус «Начислено / Выплачено» (отметка ставится вручную); выгрузка Excel/CSV/PDF.</summary>
public sealed partial class InvestmentsWindow
{
    private string? _payInvestorId;

    private Control BuildPayouts()
    {
        var page = new StackPanel { Spacing = 14 };
        if (_loading && _result == null)
        {
            page.Children.Add(Soft(T("Загрузка…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…"), 14));
            return page;
        }
        if (_loadError != null)
        {
            page.Children.Add(Card(Danger(_loadError, 14)));
            return page;
        }
        if (_result is not { } R || R.Investors.Count == 0)
        {
            page.Children.Add(EmptyState(T("Инвесторов пока нет. Добавьте инвестора и его условия — здесь появится ведомость выплат по месяцам.", "Азырынча инвесторлор жок. Инвесторду жана анын шарттарын кошуңуз — бул жерде айлар боюнча төлөм ведомосту чыгат.",
                "No investors yet. Add an investor and his terms — the monthly payout statement will appear here.", "Henüz yatırımcı yok. Bir yatırımcı ve koşullarını ekleyin — aylık ödeme çizelgesi burada görünecek.", "Hozircha investorlar yo'q. Investor va uning shartlarini qo'shing — bu yerda oylar bo'yicha to'lovlar qaydnomasi paydo bo'ladi."),
                T("Добавить инвестора", "Инвестор кошуу", "Add an investor", "Yatırımcı ekle", "Investor qo'shish"), () =>
                {
                    OpenTerms(null);
                    GoTo(Tab.Terms);
                }));
            return page;
        }
        var sel = R.Investors.FirstOrDefault(i => i.Investor.Id == _payInvestorId) ?? R.Investors[0];
        _payInvestorId = sel.Investor.Id;
        var payments = StartupCostsStore.Payments;

        page.Children.Add(Wrap(R.Investors.Select(i => (Control)ChipBtn(i.Investor.Name, i.Investor.Id == sel.Investor.Id, () =>
        {
            _payInvestorId = i.Investor.Id;
            RenderTab();
        }))));
        page.Children.Add(Heading(T("Распределение прибыли · ", "Пайданы бөлүштүрүү · ", "Profit distribution · ", "Kâr dağıtımı · ", "Foydani taqsimlash · ") + sel.Investor.Name, 20));
        page.Children.Add(Soft(T($"Вложение {Money0(sel.Invested)} от {sel.Investor.Date:dd.MM.yyyy} · база — {InvestmentText.BaseTitle(sel.Investor.Terms.Base)} · версия условий {sel.Investor.Terms.Version}.",
            $"Салым {Money0(sel.Invested)}, {sel.Investor.Date:dd.MM.yyyy} · база — {InvestmentText.BaseTitle(sel.Investor.Terms.Base)} · шарттардын {sel.Investor.Terms.Version}-версиясы.",
            $"Investment {Money0(sel.Invested)} on {sel.Investor.Date:dd.MM.yyyy} · base — {InvestmentText.BaseTitle(sel.Investor.Terms.Base)} · terms version {sel.Investor.Terms.Version}.",
            $"Yatırım {Money0(sel.Invested)}, {sel.Investor.Date:dd.MM.yyyy} · taban — {InvestmentText.BaseTitle(sel.Investor.Terms.Base)} · koşul sürümü {sel.Investor.Terms.Version}.",
            $"Sarmoya {Money0(sel.Invested)}, {sel.Investor.Date:dd.MM.yyyy} · baza — {InvestmentText.BaseTitle(sel.Investor.Terms.Base)} · shartlar versiyasi {sel.Investor.Terms.Version}."), 13));

        var rows = InvestmentText.Statement(sel, payments);
        if (rows.Count == 0)
        {
            page.Children.Add(Soft(T("С даты вложения ещё нет ни одного месяца.", "Салым күнүнөн бери бир да ай жок.", "There is not a single month since the investment date yet.", "Yatırım tarihinden beri henüz ay yok.", "Sarmoya sanasidan beri hali bitta ham oy yo'q."), 14));
            return page;
        }

        // Таблица: горизонтальная прокрутка на узком окне.
        const string cols = "86,120,120,110,130,130,90,120,120,170";
        var table = new StackPanel { Spacing = 0, MinWidth = 1130 };
        string[] heads =
        {
            T("Период", "Мезгил", "Period", "Dönem", "Davr"), T("Чистая прибыль", "Таза пайда", "Net profit", "Net kâr", "Sof foyda"), T("База расчёта", "Эсептөө базасы", "Base", "Hesap tabanı", "Hisob bazasi"),
            T("Перенос убытка", "Зыянды ташуу", "Loss carried", "Taşınan zarar", "Zarar ko'chirildi"), T("Этап", "Этап", "Stage", "Aşama", "Bosqich"), T("Инвестору", "Инвесторго", "To investor", "Yatırımcıya", "Investorga"),
            T("Налог", "Салык", "Tax", "Vergi", "Soliq"), T("К выплате", "Төлөнүүчү", "To pay", "Ödenecek", "To'lanadi"), T("Владельцу", "Ээсине", "To owner", "Sahibe", "Egasiga"), T("Статус", "Абалы", "Status", "Durum", "Holat"),
        };
        var headRow = new Grid { ColumnDefinitions = new ColumnDefinitions(cols), Margin = new Thickness(0, 0, 0, 4) };
        for (var k = 0; k < heads.Length; k++)
        {
            var h = Soft(heads[k], 12);
            h.FontWeight = FontWeight.Bold;
            h.HorizontalAlignment = k is 0 or 4 or 9 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            if (k == 4)
                h.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(h, k);
            headRow.Children.Add(h);
        }
        table.Children.Add(Line(headRow));
        foreach (var r in rows)
            table.Children.Add(Line(PayRow(sel, r, cols)));

        var tot = new Grid { ColumnDefinitions = new ColumnDefinitions(cols), Margin = new Thickness(0, 6, 0, 0) };
        void Cell(int col, string text, bool left = false, bool danger = false)
        {
            var t = Plain(text, 13.5, true);
            t.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            if (danger)
                Use(t, TextBlock.ForegroundProperty, "BrushDanger");
            Grid.SetColumn(t, col);
            tot.Children.Add(t);
        }
        Cell(0, T("Итого", "Жалпы", "Total", "Toplam", "Jami"), left: true);
        Cell(1, rows.Sum(x => x.Net).ToString("N2", Ru), danger: rows.Sum(x => x.Net) < 0m);
        Cell(2, rows.Sum(x => x.Base).ToString("N2", Ru));
        Cell(5, rows.Sum(x => x.Pay).ToString("N2", Ru));
        Cell(6, rows.Sum(x => x.Tax).ToString("N2", Ru));
        Cell(7, rows.Sum(x => x.ToHand).ToString("N2", Ru));
        Cell(8, rows.Sum(x => x.Owner).ToString("N2", Ru));
        table.Children.Add(tot);
        page.Children.Add(Card(new ScrollViewer { Content = table, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }));

        var paid = rows.Where(x => x.Paid).Sum(x => x.ToHand);
        var due = rows.Where(x => !x.Paid).Sum(x => x.ToHand);
        page.Children.Add(Plain(T($"Выплачено: {Money(paid)} · Начислено, но не выплачено: {Money(due)}.", $"Төлөндү: {Money(paid)} · Эсептелди, бирок төлөнгөн жок: {Money(due)}.", $"Paid: {Money(paid)} · Accrued but not paid: {Money(due)}.",
            $"Ödendi: {Money(paid)} · Tahakkuk etti ama ödenmedi: {Money(due)}.", $"To'landi: {Money(paid)} · Hisoblandi, lekin to'lanmadi: {Money(due)}."), 14, true));
        page.Children.Add(Soft(T("Чистая прибыль — из отчёта «Прибыль и деньги». Выплата за месяц не больше чистой прибыли месяца (если доля выше — она урезается, это видно в строке). Отметку «Выплачено» вы ставите сами, когда деньги переданы; она сохраняется в общих настройках компании.",
            "Таза пайда — «Пайда жана акча» отчетунан. Айлык төлөм айдын таза пайдасынан ашпайт (үлүш чоң болсо — кыскартылат, ал сапта көрүнөт). «Төлөндү» белгисин акча берилгенде өзүңүз коёсуз; ал компаниянын жалпы жөндөөлөрүндө сакталат.",
            "Net profit is from the “Profit & cash” report. A month's payout never exceeds the month's net profit (a higher share is cut down; the row shows it). You tick “Paid” yourself when the money is handed over; it is stored in the company's shared settings.",
            "Net kâr «Kâr ve nakit» raporundandır. Aylık ödeme o ayın net kârını aşmaz (pay daha yüksekse kısılır, satırda görünür). Para verildiğinde «Ödendi» işaretini kendiniz koyarsınız; şirketin ortak ayarlarında saklanır.",
            "Sof foyda «Foyda va pul» hisobotidan. Oylik to'lov oyning sof foydasidan oshmaydi (ulush yuqori bo'lsa — qisqartiriladi, bu qatorda ko'rinadi). «To'landi» belgisini pul berilganda o'zingiz qo'yasiz; u kompaniyaning umumiy sozlamalarida saqlanadi."), 12.5));
        page.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Heading(T("Ведомость выплат инвесторам", "Инвесторлорго төлөмдөрдүн ведомосту", "Investor payout statement", "Yatırımcı ödeme çizelgesi", "Investorlarga to'lovlar qaydnomasi")),
                Soft(T("Все инвесторы: по листу (странице) на каждого.", "Бардык инвесторлор: ар бирине өзүнчө барак.", "All investors: one sheet (page) per investor.", "Tüm yatırımcılar: her biri için bir sayfa.", "Barcha investorlar: har biriga bitta varaq.")),
                ExportRow(true),
            },
        }));
        return page;
    }

    private Control PayRow(InvestorResult inv, StatementRow r, string cols)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(cols), MinHeight = 52 };
        void Cell(int col, string text, bool left = false, bool bold = false, string brush = "BrushText", string? sub = null)
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            var t = new TextBlock { Text = text, FontSize = 13.5, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right, TextWrapping = TextWrapping.Wrap };
            Use(t, TextBlock.ForegroundProperty, brush);
            stack.Children.Add(t);
            if (sub != null)
            {
                var s = Soft(sub, 11);
                s.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                s.TextAlignment = left ? TextAlignment.Left : TextAlignment.Right;
                stack.Children.Add(s);
            }
            if (col == 4)
                stack.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(stack, col);
            grid.Children.Add(stack);
        }
        Cell(0, InvestmentText.MonthTitle(r.MonthKey), left: true, bold: true);
        Cell(1, r.Net.ToString("N2", Ru), brush: r.Net < 0m ? "BrushDanger" : "BrushText");
        Cell(2, r.Base.ToString("N2", Ru));
        Cell(3, r.Carried > 0m ? r.Carried.ToString("N2", Ru) : "—", brush: "BrushTextSoft");
        Cell(4, r.Stage, left: true, brush: "BrushTextSoft");
        Cell(5, r.Pay.ToString("N2", Ru), bold: true, sub: r.CutByCap > 0m
            ? T("урезано до чистой прибыли: −", "таза пайдага чейин кыскартылды: −", "cut to net profit: −", "net kâra kısıldı: −", "sof foydagacha qisqartirildi: −") + r.CutByCap.ToString("N2", Ru) : null);
        Cell(6, r.Tax.ToString("N2", Ru), brush: "BrushTextSoft");
        Cell(7, r.ToHand.ToString("N2", Ru), bold: true);
        Cell(8, r.Owner.ToString("N2", Ru), brush: r.Owner < 0m ? "BrushDanger" : "BrushText");

        // Статус: кнопка-отметка (44 px).
        var canMark = r.Pay > 0m || r.Paid;
        var btn = new Button
        {
            Content = r.Paid ? T("✓ Выплачено", "✓ Төлөндү", "✓ Paid", "✓ Ödendi", "✓ To'landi") : canMark ? T("Начислено · отметить", "Эсептелди · белгилөө", "Accrued · mark paid", "Tahakkuk · işaretle", "Hisoblandi · belgilash") : "—",
            MinHeight = 44, Padding = new Thickness(10, 0), CornerRadius = new CornerRadius(10), FontSize = 12.5, IsEnabled = canMark, Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        btn.Classes.Add("sec-chip");
        UiKit.SetActive(btn, r.Paid);
        Avalonia.Automation.AutomationProperties.SetName(btn, T("Отметка выплаты за ", "Төлөм белгиси: ", "Payout mark for ", "Ödeme işareti: ", "To'lov belgisi: ") + InvestmentText.MonthTitle(r.MonthKey));
        btn.Click += (_, _) =>
        {
            if (r.Paid && !PosDialogs.ConfirmYesNo(this, T("Снять отметку «Выплачено»?", "«Төлөндү» белгисин алып салабызбы?", "Remove the “Paid” mark?", "«Ödendi» işareti kaldırılsın mı?", "«To'landi» belgisi olib tashlansinmi?"), null, T("Снять", "Алып салуу", "Remove", "Kaldır", "Olib tashlash")))
                return;
            if (!r.Paid && !PosDialogs.ConfirmYesNo(this, T($"Отметить выплату инвестору {inv.Investor.Name} за {InvestmentText.MonthTitle(r.MonthKey)} — {Money(r.ToHand)} — выплаченной?", $"{inv.Investor.Name} инвесторуна {InvestmentText.MonthTitle(r.MonthKey)} үчүн {Money(r.ToHand)} төлөндү деп белгилейсизби?",
                    $"Mark the payout to {inv.Investor.Name} for {InvestmentText.MonthTitle(r.MonthKey)} — {Money(r.ToHand)} — as paid?", $"{inv.Investor.Name} için {InvestmentText.MonthTitle(r.MonthKey)} ödemesi — {Money(r.ToHand)} — ödendi olarak işaretlensin mi?", $"{inv.Investor.Name} uchun {InvestmentText.MonthTitle(r.MonthKey)} to'lovi — {Money(r.ToHand)} — to'langan deb belgilansinmi?"),
                    null, T("Отметить", "Белгилөө", "Mark", "İşaretle", "Belgilash")))
                return;
            if (StartupCostsStore.SetPaid(inv.Investor.Id, r.MonthKey, r.ToHand, !r.Paid) is { } err)
                _status.Text = err;
        };
        Grid.SetColumn(btn, 9);
        grid.Children.Add(btn);
        return grid;
    }
}
