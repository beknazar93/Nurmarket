using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using ChartPalette = NurMarketKassa.Core.Application.ChartPalette;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Вкладка «Вложения»: дата открытия, амортизация, стартовые расходы (список, добавить/изменить/удалить, итог), участники-инвесторы.</summary>
public sealed partial class InvestmentsWindow
{
    private Control BuildCosts()
    {
        var page = new StackPanel { Spacing = 16 };
        var costs = StartupCostsStore.Items;
        var meta = StartupCostsStore.Meta;
        var investors = StartupCostsStore.Investors;

        // 1. Дата открытия и амортизация.
        var settings = new StackPanel { Spacing = 10 };
        settings.Children.Add(Heading(T("Дата открытия и амортизация", "Ачылган күн жана амортизация", "Opening date and amortization", "Açılış tarihi ve amortisman", "Ochilgan sana va amortizatsiya")));
        DateTime? opened = meta.OpenedOn;
        var amortize = meta.Amortize;
        decimal months = meta.AmortMonths;
        var msg = Danger("");
        msg.IsVisible = false;
        var openedBox = DateBox(opened, d => opened = d, 160);
        var monthsBox = NumBox(months, v => months = v, "36", 100);
        var amortCheck = new CheckBox { Content = T("Списывать стартовые расходы равномерно (амортизация) — показывать «чистая прибыль с учётом амортизации»", "Баштапкы чыгымдарды бирдей эсептен чыгаруу (амортизация) — «амортизацияны эске алган таза пайда» көрсөтүлсүн",
            "Write startup costs off evenly (amortization) — show “net profit after amortization”", "Başlangıç giderlerini eşit yaz (amortisman) — «amortisman sonrası net kâr» göster", "Boshlang'ich xarajatlarni teng hisobdan chiqarish (amortizatsiya) — «amortizatsiyadan keyingi sof foyda» ko'rsatilsin"), IsChecked = amortize, MinHeight = 44 };
        amortCheck.IsCheckedChanged += (_, _) => amortize = amortCheck.IsChecked == true;
        var saveMeta = UiKit.Primary(this, T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        saveMeta.HorizontalAlignment = HorizontalAlignment.Left;
        saveMeta.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(openedBox.Text) && ParseDate(openedBox.Text) is null)
            {
                msg.IsVisible = true;
                msg.Text = T("Введите дату в виде 15.03.2026.", "Күндү 15.03.2026 түрүндө жазыңыз.", "Enter the date like 15.03.2026.", "Tarihi 15.03.2026 biçiminde girin.", "Sanani 15.03.2026 ko'rinishida kiriting.");
                return;
            }
            var err = StartupCostsStore.SetMeta(opened, amortize, (int)Math.Round(months), meta.PlanMonths);
            msg.IsVisible = err != null;
            msg.Text = err ?? "";
        };
        var inputs = new WrapPanel { Orientation = Orientation.Horizontal };
        var o1 = Labeled(this, T("Дата открытия бизнеса", "Бизнес ачылган күн", "Business opening date", "İşletme açılış tarihi", "Biznes ochilgan sana"), openedBox);
        o1.Margin = new Thickness(0, 0, 16, 8);
        var o2 = Labeled(this, T("Списывать на, мес.", "Эсептен чыгаруу, ай", "Write off over, months", "Yazma süresi, ay", "Hisobdan chiqarish, oy"), monthsBox);
        o2.Margin = new Thickness(0, 0, 16, 8);
        inputs.Children.Add(o1);
        inputs.Children.Add(o2);
        settings.Children.Add(inputs);
        settings.Children.Add(amortCheck);
        settings.Children.Add(Soft(T("Окупаемость считается от даты открытия: накопленная чистая прибыль с этой даты делится на вложения. Дату можно не указывать — тогда берётся самая ранняя дата вложения.",
            "Кайтарым ачылган күндөн эсептелет: ошол күндөн бери топтолгон таза пайда салымдарга бөлүнөт. Күндү көрсөтпөсөңүз — эң эрте салым күнү алынат.",
            "Payback counts from the opening date: the net profit accumulated since then is divided by the investment. You may leave the date empty — the earliest investment date is used.",
            "Geri dönüş açılış tarihinden hesaplanır: o tarihten beri biriken net kâr yatırıma bölünür. Tarihi boş bırakabilirsiniz — en erken yatırım tarihi alınır.",
            "O'zini oqlash ochilgan sanadan hisoblanadi: shu sanadan beri jamlangan sof foyda sarmoyaga bo'linadi. Sanani kiritmasangiz — eng erta sarmoya sanasi olinadi.")));
        settings.Children.Add(saveMeta);
        settings.Children.Add(msg);
        page.Children.Add(Card(settings));

        // 2. Стартовые расходы.
        var list = new StackPanel { Spacing = 0 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        var titleStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(Heading(T("Стартовые расходы", "Баштапкы чыгымдар", "Startup costs", "Başlangıç giderleri", "Boshlang'ich xarajatlar")));
        titleStack.Children.Add(Soft(T("Вложено на открытие: ", "Ачылышка салынды: ", "Invested at opening: ", "Açılışta yatırılan: ", "Ochilishda kiritilgan: ") + Money(StartupCostsStore.Sum(costs)) + (costs.Count > 0 ? $" · {costs.Count}" : ""), 13));
        var add = UiKit.Primary(this, T("＋ Добавить статью", "＋ Берене кошуу", "＋ Add an item", "＋ Kalem ekle", "＋ Modda qo'shish"));
        add.Click += async (_, _) =>
        {
            var dlg = new StartupCostDialog(null);
            await dlg.ShowDialog(this).ConfigureAwait(true);
        };
        Grid.SetColumn(add, 1);
        head.Children.Add(titleStack);
        head.Children.Add(add);
        list.Children.Add(head);
        if (costs.Count == 0)
            list.Children.Add(Soft(T("Добавьте стартовые расходы — программа покажет, когда магазин окупится.", "Баштапкы чыгымдарды кошуңуз — дүкөн качан кайтарарын программа көрсөтөт.",
                "Add startup costs — the program will show when the store pays back.", "Başlangıç giderlerini ekleyin — program mağazanın ne zaman geri döneceğini gösterir.", "Boshlang'ich xarajatlarni qo'shing — dastur do'kon qachon o'zini oqlashini ko'rsatadi."), 14));
        foreach (var c in costs)
            list.Children.Add(CostRow(c));
        page.Children.Add(Card(list));

        // 3. Участники.
        var people = new StackPanel { Spacing = 0 };
        var phead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        var ptitle = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        ptitle.Children.Add(Heading(T("Участники и инвесторы", "Катышуучулар жана инвесторлор", "Participants and investors", "Katılımcılar ve yatırımcılar", "Ishtirokchilar va investorlar")));
        var invSum = investors.Sum(i => i.Amount);
        var costSum = StartupCostsStore.Sum(costs);
        ptitle.Children.Add(Soft(T("Инвестиции: ", "Инвестициялар: ", "Investments: ", "Yatırımlar: ", "Investitsiyalar: ") + Money(invSum)
            + (costSum > 0m ? " · " + T("свои средства владельца: ", "ээсинин жеке каражаты: ", "owner's own funds: ", "sahibin öz kaynağı: ", "egasining o'z mablag'i: ") + Money(Math.Max(0m, costSum - invSum)) : ""), 13));
        var addInv = UiKit.Primary(this, T("＋ Инвестор", "＋ Инвестор", "＋ Investor", "＋ Yatırımcı", "＋ Investor"));
        addInv.Click += (_, _) =>
        {
            OpenTerms(null);
            GoTo(Tab.Terms);
        };
        Grid.SetColumn(addInv, 1);
        phead.Children.Add(ptitle);
        phead.Children.Add(addInv);
        people.Children.Add(phead);
        if (investors.Count == 0)
            people.Children.Add(Soft(T("Инвесторов нет: все вложения — ваши. Если кто-то вложился в бизнес, добавьте его — программа посчитает его долю и срок возврата.", "Инвесторлор жок: бардык салым — сиздики. Бирөө бизнеске салым салса, аны кошуңуз — программа анын үлүшүн жана кайтаруу мөөнөтүн эсептейт.",
                "No investors: all the money is yours. If someone invested in the business, add them — the program will calculate their share and payback time.", "Yatırımcı yok: tüm para sizin. Biri işe yatırım yaptıysa ekleyin — program payını ve geri dönüş süresini hesaplar.", "Investorlar yo'q: barcha pul sizniki. Kimdir biznesga sarmoya kiritgan bo'lsa, qo'shing — dastur uning ulushini va qaytish muddatini hisoblaydi."), 14));
        foreach (var inv in investors)
            people.Children.Add(InvestorRow(inv));
        page.Children.Add(Card(people));

        page.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Heading(T("Выгрузка", "Жүктөө", "Export", "Dışa aktar", "Eksport")),
                Soft(T("Стартовые расходы, итог вложений и окупаемость по месяцам.", "Баштапкы чыгымдар, салымдардын жыйынтыгы жана айлар боюнча кайтарым.", "Startup costs, the investment total and payback by month.", "Başlangıç giderleri, yatırım toplamı ve aylara göre geri dönüş.", "Boshlang'ich xarajatlar, sarmoyalar jami va oylar bo'yicha oqlanish.")),
                ExportRow(false),
            },
        }));
        return page;
    }

    private Control CostRow(StartupCostItem c)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), MinHeight = 56, Margin = new Thickness(0, 0, 0, 0) };
        var names = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 12, 6) };
        names.Children.Add(Plain(c.Title, 14, true));
        var sub = $"{c.Date:dd.MM.yyyy} · {StartupCostsStore.CategoryTitle(c.Category)}" + (string.IsNullOrWhiteSpace(c.Comment) ? "" : " · " + c.Comment);
        names.Children.Add(Soft(sub, 12));
        var amount = Plain(Money(c.Amount), 14.5, true);
        amount.VerticalAlignment = VerticalAlignment.Center;
        amount.Margin = new Thickness(0, 0, 14, 0);
        Grid.SetColumn(amount, 1);
        var edit = UiKit.Ghost(this, T("Изменить", "Өзгөртүү", "Edit", "Düzenle", "O'zgartirish"));
        edit.Height = 44;
        edit.Margin = new Thickness(0, 0, 8, 0);
        edit.Click += async (_, _) =>
        {
            var dlg = new StartupCostDialog(c);
            await dlg.ShowDialog(this).ConfigureAwait(true);
        };
        Grid.SetColumn(edit, 2);
        var del = UiKit.Ghost(this, T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"));
        del.Height = 44;
        del.Click += (_, _) =>
        {
            if (!PosDialogs.ConfirmYesNo(this, T($"Удалить статью «{c.Title}» ({Money(c.Amount)})? Окупаемость пересчитается.", $"«{c.Title}» ({Money(c.Amount)}) беренесин өчүрөсүзбү? Кайтарым кайра эсептелет.",
                    $"Delete the item “{c.Title}” ({Money(c.Amount)})? Payback will be recalculated.", $"«{c.Title}» kalemi ({Money(c.Amount)}) silinsin mi? Geri dönüş yeniden hesaplanır.", $"«{c.Title}» moddasi ({Money(c.Amount)}) o'chirilsinmi? O'zini oqlash qayta hisoblanadi."),
                    null, T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"), null, danger: true))
                return;
            if (StartupCostsStore.Delete(c.Id) is { } err)
                _status.Text = err;
        };
        Grid.SetColumn(del, 3);
        row.Children.Add(names);
        row.Children.Add(amount);
        row.Children.Add(edit);
        row.Children.Add(del);
        return Line(row);
    }

    private Control InvestorRow(InvestorItem inv)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), MinHeight = 60 };
        var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(4), Background = Brush.Parse(ColorOf("investor:" + inv.Id)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var names = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 12, 6) };
        names.Children.Add(Plain(inv.Name, 14, true));
        names.Children.Add(Soft($"{inv.Date:dd.MM.yyyy} · " + T("база: ", "база: ", "base: ", "taban: ", "baza: ") + InvestmentText.BaseTitle(inv.Terms.Base) + " · "
            + string.Join(" → ", inv.Terms.Stages.Select(s => s.Pct.ToString("0.##", Ru) + "%")) + " · " + T("версия ", "версия ", "version ", "sürüm ", "versiya ") + inv.Terms.Version, 12));
        Grid.SetColumn(names, 1);
        var amount = Plain(Money(inv.Amount), 14.5, true);
        amount.VerticalAlignment = VerticalAlignment.Center;
        amount.Margin = new Thickness(0, 0, 14, 0);
        Grid.SetColumn(amount, 2);
        var edit = UiKit.Ghost(this, T("Условия", "Шарттар", "Terms", "Koşullar", "Shartlar"));
        edit.Height = 44;
        edit.Margin = new Thickness(0, 0, 8, 0);
        edit.Click += (_, _) =>
        {
            OpenTerms(inv.Id);
            GoTo(Tab.Terms);
        };
        Grid.SetColumn(edit, 3);
        var del = UiKit.Ghost(this, T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"));
        del.Height = 44;
        del.Click += (_, _) =>
        {
            if (!PosDialogs.ConfirmYesNo(this, T($"Удалить инвестора «{inv.Name}»? Его условия и отметки о выплатах будут убраны.", $"«{inv.Name}» инвесторун өчүрөсүзбү? Анын шарттары жана төлөм белгилери алынат.",
                    $"Delete the investor “{inv.Name}”? His terms and payout marks will be removed.", $"«{inv.Name}» yatırımcısı silinsin mi? Koşulları ve ödeme işaretleri kaldırılır.", $"«{inv.Name}» investori o'chirilsinmi? Uning shartlari va to'lov belgilari olib tashlanadi."),
                    null, T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"), null, danger: true))
                return;
            if (StartupCostsStore.DeleteInvestor(inv.Id) is { } err)
                _status.Text = err;
            else if (_draft?.Id == inv.Id)
                OpenTerms(null);
        };
        Grid.SetColumn(del, 4);
        row.Children.Add(dot);
        row.Children.Add(names);
        row.Children.Add(amount);
        row.Children.Add(edit);
        row.Children.Add(del);
        return Line(row);
    }

    /// <summary>Строка списка: линия снизу, как таблица.</summary>
    private Control Line(Control content)
    {
        var b = new Border { Child = content, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 2) };
        Use(b, Border.BorderBrushProperty, "BrushBorder");
        return b;
    }
}
