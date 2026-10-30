using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using ChartPalette = NurMarketKassa.Core.Application.ChartPalette;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Вкладка «Условия инвестора» (макет «Условия инвестора · сфера»): шаблоны сферы, 1. инвестор и вложение, 2. от чего считается доля,
/// 3. этапы выплат, 4. если месяц убыточный, 5. период и ограничения, 6. особенности сферы; справа — «Как это читается», предупреждения и симулятор.</summary>
public sealed partial class InvestmentsWindow
{
    private InvestorItem? _draft;
    private bool _termsDirty;
    private string? _termsMsg;
    private bool _termsMsgOk;
    private decimal? _simMonthly;
    private StackPanel? _side;
    private TextBox? _dateBox;
    private TextBox? _amountBox;

    /// <summary>Открыть условия инвестора: id — существующий, null — новый (черновик с условиями по умолчанию для сферы).</summary>
    private void OpenTerms(string? id)
    {
        _termsDirty = false;
        _termsMsg = null;
        _simMonthly = null;
        var found = id == null ? null : StartupCostsStore.Investors.FirstOrDefault(i => i.Id == id);
        if (found != null)
            _draft = found.Clone();
        else
            _draft = new InvestorItem { Date = DateTime.Today, Terms = InvestorTemplates.Default(MarketSpheres.Profile.Id) };
    }

    private bool IsSaved(InvestorItem item) => StartupCostsStore.Investors.Any(i => i.Id == item.Id);

    private Control BuildTerms()
    {
        var investors = StartupCostsStore.Investors;
        if (_draft == null)
            OpenTerms(investors.Count > 0 ? investors[0].Id : null);
        var draft = _draft!;
        var terms = draft.Terms;
        var sphere = MarketSpheres.Profile;
        var page = new StackPanel { Spacing = 14 };

        // Выбор инвестора.
        var picker = new List<Control>();
        foreach (var inv in investors)
        {
            var id = inv.Id;
            picker.Add(ChipBtn(inv.Name, id == draft.Id, () => SwitchInvestor(id)));
        }
        picker.Add(ChipBtn(T("＋ Новый инвестор", "＋ Жаңы инвестор", "＋ New investor", "＋ Yeni yatırımcı", "＋ Yangi investor"), !IsSaved(draft), () => SwitchInvestor(null)));
        page.Children.Add(Wrap(picker));
        page.Children.Add(Heading(T("Условия инвестора · ", "Инвестордун шарттары · ", "Investor terms · ", "Yatırımcı koşulları · ", "Investor shartlari · ") + sphere.ShortTitle.Get(), 20));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,360") };
        var left = new StackPanel { Spacing = 14 };
        Grid.SetColumn(left, 0);

        // Шаблоны сферы.
        var tpl = new StackPanel { Spacing = 8 };
        tpl.Children.Add(Heading(T("Шаблоны для сферы «", "«", "Templates for the “", "«", "«") + sphere.ShortTitle.Get() + T("»", "» тармагы үчүн шаблондор", "” sphere", "» sektörü için şablonlar", "» sohasi uchun shablonlar"), 15));
        tpl.Children.Add(Wrap(InvestorTemplates.For(sphere.Id).Select(t => (Control)ChipBtn(t.Title, terms.Template == t.Key, () =>
        {
            draft.Terms = InvestorTemplates.Apply(t, draft.Terms);
            MarkDirty();
            RenderTab();
        }))));
        tpl.Children.Add(Soft(T("Шаблон заполняет этапы ниже — дальше всё можно поменять вручную.", "Шаблон төмөнкү этаптарды толтурат — андан ары бардыгын кол менен өзгөртсө болот.", "A template fills in the stages below — after that you can change everything by hand.",
            "Şablon aşağıdaki aşamaları doldurur — sonra her şeyi elle değiştirebilirsiniz.", "Shablon quyidagi bosqichlarni to'ldiradi — keyin hammasini qo'lda o'zgartirish mumkin.")));
        left.Children.Add(Card(tpl));

        // 1. Инвестор и вложение.
        var b1 = new StackPanel { Spacing = 10 };
        b1.Children.Add(Heading(T("1. Инвестор и вложение", "1. Инвестор жана салым", "1. Investor and investment", "1. Yatırımcı ve yatırım", "1. Investor va sarmoya"), 15));
        var nameBox = UiKit.Input(this, T("Например: Азамат Б.", "Мисалы: Азамат Б.", "E.g. John D.", "Örn.: Ahmet B.", "Masalan: Azamat B."), 44);
        nameBox.MaxLength = 80;
        nameBox.Text = draft.Name;
        nameBox.TextChanged += (_, _) =>
        {
            if ((nameBox.Text ?? "") == draft.Name)
                return; // 2026-10-31: TextChanged без правки имени не должен ставить «изменено и не сохранено»
            draft.Name = nameBox.Text ?? "";
            MarkDirty();
            RebuildSide();
        };
        _amountBox = NumBox(draft.Amount, v =>
        {
            draft.Amount = v;
            MarkDirty();
            RebuildSide();
        }, "1500000", problem: ShowTermsProblem);
        _dateBox = DateBox(draft.Date, d =>
        {
            if (d != null)
                draft.Date = d.Value;
            MarkDirty();
        }, 160);
        var r1 = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, input, w) in new[]
                 {
                     (T("Имя инвестора", "Инвестордун аты", "Investor name", "Yatırımcı adı", "Investor ismi"), (Control)nameBox, 260.0),
                     (T("Сумма вложения, сом", "Салымдын суммасы, сом", "Investment amount, som", "Yatırım tutarı, som", "Sarmoya summasi, so'm"), _amountBox, 190.0),
                     (T("Дата вложения", "Салым күнү", "Investment date", "Yatırım tarihi", "Sarmoya sanasi"), _dateBox, 170.0),
                 })
        {
            var p = Labeled(this, label, input);
            p.Width = w;
            p.Margin = new Thickness(0, 0, 14, 8);
            r1.Children.Add(p);
        }
        b1.Children.Add(r1);
        var costTotal = StartupCostsStore.Total;
        var otherInv = investors.Where(i => i.Id != draft.Id).Sum(i => i.Amount);
        b1.Children.Add(Soft(T($"Свои вложения владельца: {Money0(Math.Max(0m, costTotal - otherInv - draft.Amount))} (стартовые расходы {Money0(costTotal)} минус инвесторы).",
            $"Ээсинин жеке салымдары: {Money0(Math.Max(0m, costTotal - otherInv - draft.Amount))} (баштапкы чыгымдар {Money0(costTotal)} минус инвесторлор).",
            $"Owner's own investment: {Money0(Math.Max(0m, costTotal - otherInv - draft.Amount))} (startup costs {Money0(costTotal)} minus investors).",
            $"Sahibin kendi yatırımı: {Money0(Math.Max(0m, costTotal - otherInv - draft.Amount))} (başlangıç giderleri {Money0(costTotal)} eksi yatırımcılar).",
            $"Egasining o'z sarmoyasi: {Money0(Math.Max(0m, costTotal - otherInv - draft.Amount))} (boshlang'ich xarajatlar {Money0(costTotal)} minus investorlar)."), 12));
        b1.Children.Add(Soft(T("Валюта — сом. Расчёт в долларах не ведётся: курса в данных программы нет, цифры не выдумываем.", "Валюта — сом. Доллар менен эсептелбейт: программада курс жок, сандарды ойлоп таппайбыз.",
            "Currency is som. No dollar accounting: the program has no exchange rate, so figures are not invented.", "Para birimi som. Dolar hesabı yok: programda kur verisi yok, rakam uydurmuyoruz.", "Valyuta — so'm. Dollarda hisob yuritilmaydi: dasturda kurs yo'q, raqamlar o'ylab topilmaydi."), 12));
        left.Children.Add(Card(b1));

        // 2. От чего считается доля.
        var b2 = new StackPanel { Spacing = 10 };
        var b2head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        b2head.Children.Add(Heading(T("2. От чего считается доля", "2. Үлүш эмнеден эсептелет", "2. What the share is calculated from", "2. Pay neye göre hesaplanır", "2. Ulush nimadan hisoblanadi"), 15));
        var last = _result?.Months.LastOrDefault(m => m.Detail != null);
        if (last?.Detail is { } lp)
        {
            var now = Soft(T("База за ", "Базасы: ", "Base for ", "Taban: ", "Baza: ") + MonthName(last.From) + ": " + Money0(InvestmentAnalysis.BaseOf(lp, terms)), 12.5);
            now.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(now, 1);
            b2head.Children.Add(now);
        }
        b2.Children.Add(b2head);
        var baseSeg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, title) in new[]
                 {
                     ("revenue", T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum")),
                     ("gross", T("Валовая прибыль", "Дүң пайда", "Gross profit", "Brüt kâr", "Yalpi foyda")),
                     ("net", T("Чистая прибыль", "Таза пайда", "Net profit", "Net kâr", "Sof foyda")),
                 })
        {
            var b = UiKit.Seg(title, terms.Base == key);
            b.Click += (_, _) =>
            {
                terms.Base = key;
                terms.Template = "";
                MarkDirty();
                RenderTab();
            };
            baseSeg.Children.Add(b);
        }
        b2.Children.Add(new ScrollViewer { Content = UiKit.Segment(baseSeg), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        if (terms.Base == "net")
        {
            b2.Children.Add(Plain(T("Какие расходы вычитать", "Кайсы чыгымдар кемитилет", "Which expenses to deduct", "Hangi giderler düşülsün", "Qaysi xarajatlar ayiriladi"), 13, true));
            foreach (var key in NetProfitCalculator.OpexKeys)
            {
                var k = key;
                var amount = last?.Detail?.OpexParts is { } parts && parts.TryGetValue(k, out var v) ? Money0(v) + " / " + T("мес.", "ай", "mo.", "ay", "oy") : "";
                var cb = new CheckBox { IsChecked = !terms.ExcludedExpenses.Contains(k), MinHeight = 44, Content = InvestmentText.ExpenseTitle(k) + (amount.Length > 0 ? "   ·   " + amount : "") };
                cb.IsCheckedChanged += (_, _) =>
                {
                    terms.ExcludedExpenses.Remove(k);
                    if (cb.IsChecked != true)
                        terms.ExcludedExpenses.Add(k);
                    terms.Template = "";
                    MarkDirty();
                    RebuildSide();
                };
                b2.Children.Add(cb);
            }
            b2.Children.Add(Soft(T("Все расходы отмечены — это ровно «Чистая прибыль» из «Прибыли и денег». Снимите галочку, чтобы статью не вычитать из базы инвестора.", "Бардык чыгым белгиленген — бул «Пайда жана акчадагы» «Таза пайда». Беренени инвестордун базасынан кемитпөө үчүн белгини алыңыз.",
                "With every expense ticked this is exactly “Net profit” from “Profit & cash”. Untick an item to keep it out of the investor's base.", "Tüm giderler işaretliyken bu tam olarak «Kâr ve nakit»teki «Net kâr»dır. Bir kalemi yatırımcı tabanından çıkarmak için işareti kaldırın.", "Barcha xarajatlar belgilansa — bu «Foyda va pul»dagi «Sof foyda». Moddani investor bazasidan chiqarish uchun belgini oling."), 12));
        }
        left.Children.Add(Card(b2));

        // 3. Этапы выплат.
        left.Children.Add(Card(BuildStages(draft)));

        // 4. Если месяц убыточный.
        var b4 = new StackPanel { Spacing = 10 };
        b4.Children.Add(Heading(T("4. Если месяц убыточный", "4. Ай зыяндуу болсо", "4. If the month is at a loss", "4. Ay zararlıysa", "4. Oy zarar bilan tugasa"), 15));
        var lossRow = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, title, desc) in new[]
                 {
                     ("carry", T("Переносить убыток", "Зыянды ташуу", "Carry the loss over", "Zararı devret", "Zararni ko'chirish"), T("Сначала убыток покрывается из будущей прибыли, потом считается доля", "Адегенде зыян келечектеги пайдадан жабылат, анан үлүш эсептелет", "The loss is first covered from future profit, then the share is counted", "Önce zarar gelecek kârdan kapatılır, sonra pay hesaplanır", "Avval zarar kelgusi foydadan yopiladi, keyin ulush hisoblanadi")),
                     ("none", T("Не переносить", "Ташыбоо", "Do not carry over", "Devretme", "Ko'chirmaslik"), T("Каждый месяц считается отдельно, убыток просто без выплаты", "Ар бир ай өзүнчө эсептелет, зыян жөн эле төлөмсүз", "Each month stands alone; a loss simply means no payout", "Her ay ayrı hesaplanır, zarar sadece ödemesiz kalır", "Har oy alohida hisoblanadi, zarar shunchaki to'lovsiz")),
                     ("share", T("Инвестор участвует", "Инвестор катышат", "The investor shares the loss", "Yatırımcı zarara katılır", "Investor ishtirok etadi"), T("Его доля убытка уменьшает сумму, которую он вернул", "Анын зыяндагы үлүшү кайтарган суммасын азайтат", "His share of the loss reduces what he has got back", "Zarardaki payı geri aldığı tutarı azaltır", "Uning zarardagi ulushi qaytargan summasini kamaytiradi")),
                 })
        {
            var content = new StackPanel { Spacing = 3, MaxWidth = 250 };
            content.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = desc, FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            var btn = new Button { Content = content, MinHeight = 76, Padding = new Thickness(14, 10), HorizontalContentAlignment = HorizontalAlignment.Left, Focusable = false, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
            btn.Classes.Add("sec-chip");
            UiKit.SetActive(btn, terms.LossRule == key);
            Avalonia.Automation.AutomationProperties.SetName(btn, title);
            btn.Click += (_, _) =>
            {
                terms.LossRule = key;
                MarkDirty();
                RenderTab();
            };
            lossRow.Children.Add(btn);
        }
        b4.Children.Add(lossRow);
        left.Children.Add(Card(b4));

        // 5. Период и ограничения.
        var b5 = new StackPanel { Spacing = 10 };
        b5.Children.Add(Heading(T("5. Период и ограничения", "5. Мезгил жана чектөөлөр", "5. Period and limits", "5. Dönem ve sınırlar", "5. Davr va cheklovlar"), 15));
        b5.Children.Add(Soft(T("Как часто считать и выплачивать", "Канчалык жыш эсептеп, төлөө", "How often to calculate and pay", "Ne sıklıkla hesaplanıp ödenecek", "Qanchalik tez-tez hisoblash va to'lash")));
        var perSeg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, title) in new[]
                 {
                     ("month", T("Месяц", "Ай", "Month", "Ay", "Oy")), ("quarter", T("Квартал", "Чейрек", "Quarter", "Çeyrek", "Chorak")),
                     ("year", T("Год", "Жыл", "Year", "Yıl", "Yil")), ("manual", T("Вручную", "Кол менен", "Manually", "Elle", "Qo'lda")),
                 })
        {
            var b = UiKit.Seg(title, terms.Period == key);
            b.Click += (_, _) =>
            {
                terms.Period = key;
                MarkDirty();
                RenderTab();
            };
            perSeg.Children.Add(b);
        }
        b5.Children.Add(new ScrollViewer { Content = UiKit.Segment(perSeg), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var limits = new WrapPanel { Orientation = Orientation.Horizontal };
        var minBox = NumBox(terms.MinMonthly, v => { terms.MinMonthly = v; MarkDirty(); RebuildSide(); }, "0", 190, ShowTermsProblem);
        var taxBox = NumBox(terms.TaxPct, v => { terms.TaxPct = v; MarkDirty(); RebuildSide(); }, "0", 190, ShowTermsProblem);
        var m1 = Labeled(this, T("Минимальная выплата в месяц, сом", "Айына минималдуу төлөм, сом", "Minimum monthly payout, som", "Aylık asgari ödeme, som", "Oylik minimal to'lov, so'm"), minBox);
        m1.Margin = new Thickness(0, 0, 14, 8);
        m1.Width = 250;
        var m2 = Labeled(this, T("Удержание налога с выплат, %", "Төлөмдөрдөн салык кармоо, %", "Tax withheld from payouts, %", "Ödemelerden vergi kesintisi, %", "To'lovlardan soliq ushlash, %"), taxBox);
        m2.Margin = new Thickness(0, 0, 14, 8);
        m2.Width = 250;
        limits.Children.Add(m1);
        limits.Children.Add(m2);
        b5.Children.Add(limits);
        b5.Children.Add(Soft(T("Выплата за месяц не бывает больше чистой прибыли месяца — минимальная выплата тоже урезается до неё.", "Айлык төлөм айдын таза пайдасынан ашпайт — минималдуу төлөм да ага чейин кыскарат.",
            "A month's payout never exceeds the month's net profit — the minimum payout is also cut down to it.", "Aylık ödeme o ayın net kârını aşmaz — asgari ödeme de buna kısılır.", "Oylik to'lov oyning sof foydasidan oshmaydi — minimal to'lov ham unga qisqartiriladi."), 12));
        left.Children.Add(Card(b5));

        // 6. Особенности сферы.
        left.Children.Add(BuildSphereBlock(draft, sphere));
        grid.Children.Add(left);

        // Правая колонка: «Как это читается», симулятор, сохранение.
        var right = new StackPanel { Spacing = 14 };
        Grid.SetColumn(right, 2);
        _side = new StackPanel { Spacing = 14 };
        right.Children.Add(_side);
        var save = UiKit.Primary(this, T("Сохранить условия", "Шарттарды сактоо", "Save the terms", "Koşulları kaydet", "Shartlarni saqlash"));
        save.Click += (_, _) => SaveTerms();
        right.Children.Add(save);
        if (_termsMsg != null)
            right.Children.Add(_termsMsgOk ? Card(Plain(_termsMsg, 13), 12) : Danger(_termsMsg));
        grid.Children.Add(right);
        RebuildSide();
        page.Children.Add(grid);
        return page;
    }

    private void MarkDirty()
    {
        _termsDirty = true;
        _termsMsg = null;
    }

    private void ShowTermsProblem(string? text)
    {
        if (text != null)
            _status.Text = text;
    }

    private void SwitchInvestor(string? id)
    {
        if (_termsDirty && !PosDialogs.ConfirmYesNo(this, T("Условия изменены и не сохранены. Перейти без сохранения?", "Шарттар өзгөрдү, бирок сакталган жок. Сактабай өтөсүзбү?", "The terms are changed but not saved. Switch without saving?",
                "Koşullar değişti ama kaydedilmedi. Kaydetmeden geçilsin mi?", "Shartlar o'zgargan, lekin saqlanmagan. Saqlamasdan o'tasizmi?"), null, T("Перейти", "Өтүү", "Switch", "Geç", "O'tish"), T("Остаться", "Калуу", "Stay", "Kal", "Qolish")))
            return;
        OpenTerms(id);
        RenderTab();
    }

    private void SaveTerms()
    {
        if (_draft == null)
            return;
        if (_dateBox != null)
        {
            var parsed = ParseDate(_dateBox.Text);
            if (parsed is null)
            {
                _termsMsg = T("Введите дату вложения в виде 15.03.2026.", "Салым күнүн 15.03.2026 түрүндө жазыңыз.", "Enter the investment date like 15.03.2026.", "Yatırım tarihini 15.03.2026 biçiminde girin.", "Sarmoya sanasini 15.03.2026 ko'rinishida kiriting.");
                _termsMsgOk = false;
                RenderTab();
                return;
            }
            _draft.Date = parsed.Value;
        }
        var wasNew = !IsSaved(_draft);
        var err = StartupCostsStore.SaveInvestor(_draft);
        if (err != null)
        {
            _termsMsg = err;
            _termsMsgOk = false;
            RenderTab();
            return;
        }
        _termsDirty = false;
        var saved = StartupCostsStore.Investors.FirstOrDefault(i => i.Id == _draft.Id);
        if (saved != null)
            _draft = saved.Clone();
        _termsMsgOk = true;
        _termsMsg = T($"Сохранено как версия {saved?.Terms.Version ?? 1} условий. Ведомость выплат считается по этим условиям.", $"Шарттардын {saved?.Terms.Version ?? 1}-версиясы катары сакталды. Төлөм ведомосту ушул шарттар боюнча эсептелет.",
            $"Saved as version {saved?.Terms.Version ?? 1} of the terms. The payout statement uses these terms.", $"Koşulların {saved?.Terms.Version ?? 1}. sürümü olarak kaydedildi. Ödeme çizelgesi bu koşullara göre hesaplanır.", $"Shartlarning {saved?.Terms.Version ?? 1}-versiyasi sifatida saqlandi. To'lovlar qaydnomasi shu shartlar bo'yicha hisoblanadi.");
        _ = wasNew;
        // Store.Changed перерисует вкладку; на случай если сервер не отвечает — перерисуем сами.
        Recompute();
        RenderTab();
    }

    // ───────────────────────────── 3. этапы ─────────────────────────────

    private Control BuildStages(InvestorItem draft)
    {
        var terms = draft.Terms;
        var box = new StackPanel { Spacing = 10 };
        box.Children.Add(Heading(T("3. Этапы выплат", "3. Төлөм этаптары", "3. Payout stages", "3. Ödeme aşamaları", "3. To'lov bosqichlari"), 15));
        for (var i = 0; i < terms.Stages.Count; i++)
        {
            var idx = i;
            var st = terms.Stages[i];
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            var badge = new Border
            {
                Background = Brush.Parse(ChartPalette.ColorFor("stage:" + i)), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 0), Height = 32, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8),
                Child = new TextBlock { Text = T($"Этап {i + 1}", $"{i + 1}-этап", $"Stage {i + 1}", $"{i + 1}. aşama", $"{i + 1}-bosqich"), FontWeight = FontWeight.Bold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White },
            };
            row.Children.Add(badge);
            var pctBox = NumBox(st.Pct, v => { st.Pct = v; terms.Template = ""; MarkDirty(); RebuildSide(); }, "0", 100, ShowTermsProblem);
            var pctL = Labeled(this, T("Доля, %", "Үлүш, %", "Share, %", "Pay, %", "Ulush, %"), pctBox);
            pctL.Margin = new Thickness(0, 0, 12, 8);
            row.Children.Add(pctL);
            var cond = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (key, title) in new[]
                     {
                         ("return", T("пока не вернётся", "кайтканга чейин", "until returned", "geri dönene dek", "qaytgunga qadar")),
                         ("months", T("в течение", "ичинде", "for", "boyunca", "davomida")),
                         ("forever", T("бессрочно", "чексиз", "forever", "süresiz", "muddatsiz")),
                     })
            {
                var b = UiKit.Seg(title, st.Cond == key);
                b.Click += (_, _) =>
                {
                    var prevMax = terms.Stages.Take(idx).Where(s => s.Cond == "return").Select(s => s.Val).DefaultIfEmpty(0m).Max();
                    st.Cond = key;
                    st.Val = key == "return" ? (prevMax > 0m ? prevMax + 50m : 100m) : key == "months" ? 12m : 0m;
                    terms.Template = "";
                    MarkDirty();
                    RenderTab();
                };
                cond.Children.Add(b);
            }
            var condL = Labeled(this, T("Действует", "Аракеттенет", "Applies", "Geçerli", "Amal qiladi"), UiKit.Segment(cond));
            condL.Margin = new Thickness(0, 0, 12, 8);
            row.Children.Add(condL);
            if (st.Cond != "forever")
            {
                var valBox = NumBox(st.Val, v => { st.Val = v; terms.Template = ""; MarkDirty(); RebuildSide(); }, "100", 110, ShowTermsProblem);
                var unit = st.Cond == "return" ? T("% от вложений", "% салымдан", "% of the investment", "% yatırımdan", "% sarmoyadan") : T("мес.", "ай", "months", "ay", "oy");
                var valL = Labeled(this, unit, valBox);
                valL.Margin = new Thickness(0, 0, 12, 8);
                row.Children.Add(valL);
                if (st.Cond == "return")
                {
                    var hint = Soft("= " + Money0(draft.Amount * st.Val / 100m), 12.5);
                    hint.VerticalAlignment = VerticalAlignment.Center;
                    hint.Margin = new Thickness(0, 10, 12, 0);
                    row.Children.Add(hint);
                }
            }
            if (terms.Stages.Count > 1)
            {
                var rm = UiKit.Ghost(this, "✕");
                rm.Width = 44;
                rm.Height = 44;
                rm.Padding = new Thickness(0);
                rm.Margin = new Thickness(0, 22, 0, 8);
                Avalonia.Automation.AutomationProperties.SetName(rm, T("Удалить этап", "Этапты өчүрүү", "Remove the stage", "Aşamayı sil", "Bosqichni o'chirish"));
                rm.Click += (_, _) =>
                {
                    terms.Stages.RemoveAt(idx);
                    terms.Template = "";
                    MarkDirty();
                    RenderTab();
                };
                row.Children.Add(rm);
            }
            var wrap = new Border { Padding = new Thickness(12, 10, 12, 2), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = row };
            Use(wrap, Border.BorderBrushProperty, "BrushBorder");
            Use(wrap, Border.BackgroundProperty, "BrushSurfaceSubtle");
            box.Children.Add(wrap);
        }
        var add = UiKit.Ghost(this, T("+ Добавить этап", "+ Этап кошуу", "+ Add a stage", "+ Aşama ekle", "+ Bosqich qo'shish"));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Click += (_, _) =>
        {
            var lastSt = terms.Stages.LastOrDefault();
            if (lastSt is { Cond: "forever" })
            {
                var prevMax = terms.Stages.Take(terms.Stages.Count - 1).Where(s => s.Cond == "return").Select(s => s.Val).DefaultIfEmpty(0m).Max();
                lastSt.Cond = "return";
                lastSt.Val = prevMax > 0m ? prevMax + 50m : 100m;
            }
            terms.Stages.Add(new PayStage { Pct = Math.Max(5m, Math.Round((lastSt?.Pct ?? 40m) / 2m)), Cond = "forever", Val = 0m });
            terms.Template = "";
            MarkDirty();
            RenderTab();
        };
        box.Children.Add(add);
        return box;
    }

    // ───────────────────────────── 6. особенности сферы ─────────────────────────────

    private Control BuildSphereBlock(InvestorItem draft, SphereProfile sphere)
    {
        var terms = draft.Terms;
        var box = new StackPanel { Spacing = 10 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var pill = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = T("Только для сферы", "Тармак үчүн гана", "Only for this sphere", "Yalnızca bu sektör", "Faqat shu soha uchun"), FontSize = 12, FontWeight = FontWeight.Bold } };
        Use(pill, Border.BackgroundProperty, "BrushSurfaceSubtle");
        head.Children.Add(pill);
        head.Children.Add(Heading(T("6. Особенности: ", "6. Өзгөчөлүктөр: ", "6. Specifics: ", "6. Özellikler: ", "6. Xususiyatlar: ") + sphere.ShortTitle.Get(), 15));
        box.Children.Add(head);

        // Резерв — для любой сферы.
        var reserveBox = NumBox(terms.ReservePct, v => { terms.ReservePct = v; MarkDirty(); RebuildSide(); }, "0", 110, ShowTermsProblem);
        var reserve = new StackPanel { Spacing = 4 };
        reserve.Children.Add(Plain(sphere.BaseBehavior == MarketSpheres.Services ? T("Резерв на обновление оборудования", "Жабдууну жаңылоо резерви", "Equipment renewal reserve", "Ekipman yenileme yedeği", "Uskunani yangilash zaxirasi")
            : T("Резервный фонд на ремонт и технику", "Оңдоо жана техника үчүн резерв", "Reserve fund for repairs and equipment", "Tadilat ve ekipman yedek fonu", "Ta'mir va texnika uchun zaxira fondi"), 14, true));
        reserve.Children.Add(Soft(T("Процент положительной базы откладывается до распределения между инвестором и владельцем.", "Оң базанын пайызы инвестор менен ээсинин ортосунда бөлүштүрүүгө чейин сакталат.", "A percentage of a positive base is set aside before sharing between the investor and the owner.",
            "Pozitif tabanın bir yüzdesi yatırımcı ile sahip arasında paylaştırılmadan önce ayrılır.", "Musbat bazaning bir foizi investor va egasi o'rtasida taqsimlanishdan oldin ajratiladi."), 12));
        var rl = Labeled(this, "%", reserveBox);
        reserve.Children.Add(rl);
        box.Children.Add(Group(reserve));

        // Товарный остаток — для магазинов с товаром.
        if (sphere.BaseBehavior != MarketSpheres.Services)
        {
            var stock = new StackPanel { Spacing = 4 };
            var cb = new CheckBox { IsChecked = terms.StockCredit, MinHeight = 44, Content = T("Засчитывать товарный остаток в окупаемость", "Товар калдыгын кайтарымга эсептөө", "Count the stock on hand towards payback", "Mal stokunu geri dönüşe say", "Tovar qoldig'ini oqlanishga hisoblash") };
            var liqBox = NumBox(terms.StockLiquidityPct, v => { terms.StockLiquidityPct = v; MarkDirty(); }, "70", 110, ShowTermsProblem);
            cb.IsCheckedChanged += (_, _) =>
            {
                terms.StockCredit = cb.IsChecked == true;
                MarkDirty();
            };
            stock.Children.Add(cb);
            stock.Children.Add(Soft(T("Деньги, вложенные в товар на полках, не потеряны: остаток по закупочной цене (из склада) × ликвидность прибавляется к «Вернулось».", "Текчедеги товарга салынган акча жоголгон жок: калдык сатып алуу баасы боюнча (складдан) × ликвидүүлүк «Кайтты» кошулат.",
                "Money put into stock on the shelves is not lost: the stock at purchase price (from the warehouse) × liquidity is added to “Returned”.", "Raflardaki mala yatırılan para kaybolmadı: alış fiyatıyla stok (depodan) × likidite «Geri dönen»e eklenir.", "Javondagi tovarga kiritilgan pul yo'qolmagan: xarid narxidagi qoldiq (ombordan) × likvidlik «Qaytdi»ga qo'shiladi."), 12));
            stock.Children.Add(Labeled(this, T("Ликвидность остатка, %", "Калдыктын ликвидүүлүгү, %", "Stock liquidity, %", "Stok likiditesi, %", "Qoldiq likvidligi, %"), liqBox));
            box.Children.Add(Group(stock));
        }
        return Card(box);
    }

    private Control Group(Control child)
    {
        var b = new Border { Padding = new Thickness(14, 10), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = child };
        Use(b, Border.BorderBrushProperty, "BrushBorder");
        return b;
    }

    // ───────────────────────────── правая колонка ─────────────────────────────

    private void RebuildSide()
    {
        if (_side == null || _draft == null)
            return;
        var draft = _draft;
        var terms = draft.Terms;
        _side.Children.Clear();

        // «Как это читается».
        var reading = new StackPanel { Spacing = 8 };
        reading.Children.Add(new TextBlock { Text = T("КАК ЭТО ЧИТАЕТСЯ", "БУЛ ЭМНЕНИ БИЛДИРЕТ", "HOW IT READS", "NASIL OKUNUR", "BU QANDAY O'QILADI"), FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#B9BEC6") });
        reading.Children.Add(new TextBlock { Text = Summary(draft), FontSize = 14.5, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White });
        var warnings = InvestorTemplates.Warnings(terms);
        var sim = InvestmentAnalysis.Simulate(terms, draft.Amount, OwnInvestedFor(draft), SimMonthly(terms));
        if (sim.InvestorMonths is null && sim.Monthly > 0m && draft.Amount > 0m)
            warnings.Add(T("При такой прибыли инвестор не вернёт вложения за 10 лет.", "Мындай пайда менен инвестор 10 жылда салымын кайтарбайт.", "At this profit the investor will not get the investment back in 10 years.", "Bu kârla yatırımcı 10 yılda yatırımını geri alamaz.", "Bunday foyda bilan investor 10 yilda sarmoyasini qaytarib olmaydi."));
        if (warnings.Count == 0)
            reading.Children.Add(new TextBlock { Text = T("Условия корректны", "Шарттар туура", "The terms are valid", "Koşullar doğru", "Shartlar to'g'ri"), FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#8FD3B4") });
        foreach (var w in warnings)
            reading.Children.Add(new Border { Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(8), Background = Brush.Parse("#3A2A12"), Child = new TextBlock { Text = "⚠ " + w, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#F5C98A") } });
        _side.Children.Add(new Border { Background = Brush.Parse("#15171C"), CornerRadius = new CornerRadius(16), Padding = new Thickness(18), Child = reading });

        // Симулятор.
        var simBox = new StackPanel { Spacing = 10 };
        simBox.Children.Add(Heading(T("Симулятор", "Симулятор", "Simulator", "Simülatör", "Simulyator"), 15));
        var baseDefault = DefaultSimMonthly(terms);
        var simInput = NumBox(SimMonthly(terms), v =>
        {
            _simMonthly = v;
            RebuildSimResult();
        }, "0", double.NaN);
        simBox.Children.Add(Labeled(this, T("Тестовая ", "Сыноо ", "Test ", "Deneme ", "Sinov ") + InvestmentText.BaseTitle(terms.Base) + T(" в месяц, сом", " айына, сом", " per month, som", " aylık, som", " oyiga, so'm"), simInput));
        if (baseDefault > 0m)
        {
            var chips = new List<Control>();
            foreach (var mult in new[] { 0.6m, 1m, 1.6m, 2.4m })
            {
                var v = Math.Round(baseDefault * mult / 10000m) * 10000m;
                if (v <= 0m)
                    continue;
                chips.Add(ChipBtn(Short(v), SimMonthly(terms) == v, () =>
                {
                    _simMonthly = v;
                    simInput.Text = v.ToString("0.##", Ru);
                    RebuildSide();
                }));
            }
            simBox.Children.Add(Wrap(chips, 6));
            simBox.Children.Add(Soft(T("Для сравнения: база прошлого месяца — ", "Салыштыруу үчүн: өткөн айдын базасы — ", "For comparison: last month's base is ", "Karşılaştırma için: geçen ayın tabanı ", "Taqqoslash uchun: o'tgan oy bazasi — ") + Money0(baseDefault) + ".", 12));
        }
        _simResult = new StackPanel { Spacing = 8 };
        simBox.Children.Add(_simResult);
        _side.Children.Add(Card(simBox));
        RebuildSimResult();
    }

    private StackPanel? _simResult;

    private decimal OwnInvestedFor(InvestorItem draft)
    {
        var others = StartupCostsStore.Investors.Where(i => i.Id != draft.Id).Sum(i => i.Amount);
        return Math.Max(0m, StartupCostsStore.Total - others - draft.Amount);
    }

    /// <summary>База последнего месяца по этим условиям — основа симулятора (реальные данные); нет отчёта — 0.</summary>
    private decimal DefaultSimMonthly(InvestorTerms terms)
    {
        var last = _result?.Months.LastOrDefault(m => m.IsFull && m.Detail != null) ?? _result?.Months.LastOrDefault(m => m.Detail != null);
        return last?.Detail is { } p ? Math.Max(0m, InvestmentAnalysis.BaseOf(p, terms)) : 0m;
    }

    private decimal SimMonthly(InvestorTerms terms) => _simMonthly ?? Math.Round(DefaultSimMonthly(terms) / 10000m) * 10000m;

    private void RebuildSimResult()
    {
        if (_simResult == null || _draft == null)
            return;
        var draft = _draft;
        var terms = draft.Terms;
        _simResult.Children.Clear();
        var monthly = SimMonthly(terms);
        var sim = InvestmentAnalysis.Simulate(terms, draft.Amount, OwnInvestedFor(draft), monthly);
        string M(int? m) => m is { } v ? v + " " + T("мес", "ай", "mo", "ay", "oy") : T("не окупится", "кайтпайт", "will not pay back", "geri dönmez", "oqlanmaydi");
        void Kpi(string label, string value, string colorKey)
        {
            // 2026-10-31: длинное значение («нужна база «чистая прибыль»») съедало ширину и подпись шла по 2 буквы в строке — длинное значение уходит второй строкой под подпись.
            var longValue = value.Length > 14;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(longValue ? "Auto,*" : "Auto,*,Auto") };
            var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = Brush.Parse(ColorOf(colorKey)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var l = Soft(label, 13);
            l.VerticalAlignment = VerticalAlignment.Center;
            l.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumn(l, 1);
            var v = Heading(value, longValue ? 13 : 15);
            v.TextWrapping = TextWrapping.Wrap;
            if (longValue)
            {
                row.RowDefinitions = new RowDefinitions("Auto,Auto");
                v.Margin = new Thickness(0, 4, 0, 0);
                Grid.SetRow(v, 1);
                Grid.SetColumn(v, 1);
            }
            else
            {
                v.VerticalAlignment = VerticalAlignment.Center;
                v.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(v, 2);
            }
            row.Children.Add(dot);
            row.Children.Add(l);
            row.Children.Add(v);
            var b = new Border { Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(10), Child = row };
            Use(b, Border.BackgroundProperty, "BrushSurfaceSubtle");
            _simResult.Children.Add(b);
        }
        var name = string.IsNullOrWhiteSpace(draft.Name) ? T("Инвестор", "Инвестор", "The investor", "Yatırımcı", "Investor") : draft.Name;
        Kpi(name + T(" вернёт вложения", " салымын кайтарат", " gets the investment back", " yatırımını geri alır", " sarmoyasini qaytaradi"), M(sim.InvestorMonths), "investor:" + draft.Id);
        Kpi(T("Бизнес окупится", "Бизнес кайтарат", "The business pays back", "İşletme geri döner", "Biznes oqlanadi"), terms.Base == "net" ? M(sim.BusinessMonths) : T("нужна база «чистая прибыль»", "«таза пайда» базасы керек", "needs the “net profit” base", "«net kâr» tabanı gerekli", "«sof foyda» bazasi kerak"), "business");
        Kpi(T("Владелец окупится", "Ээси кайтарат", "The owner pays back", "Sahibi geri alır", "Egasi oqlanadi"), terms.Base == "net" ? M(sim.OwnerMonths) : T("нужна база «чистая прибыль»", "«таза пайда» базасы керек", "needs the “net profit” base", "«net kâr» tabanı gerekli", "«sof foyda» bazasi kerak"), OwnerKey);

        // Шкала 4 лет.
        _simResult.Children.Add(Soft(T("Этапы на шкале: 4 года", "4 жылдык шкаладагы этаптар", "Stages on a 4-year scale", "4 yıllık ölçekte aşamalar", "4 yillik shkaladagi bosqichlar"), 12));
        var groups = sim.Stages.Where(g => g.From < 48).ToList();
        if (groups.Count > 0)
        {
            var widths = string.Join(",", groups.Select(g => (Math.Min(g.To, 47) - g.From + 1).ToString(CultureInfo.InvariantCulture) + "*"));
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions(widths), Height = 28 };
            for (var i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                var ended = g.Stage >= terms.Stages.Count;
                var cell = new Border { Background = ended ? Brush.Parse("#D9DBD5") : Brush.Parse(ChartPalette.ColorFor("stage:" + g.Stage)), BorderThickness = new Thickness(0, 0, 2, 0), BorderBrush = Brushes.Transparent };
                if ((Math.Min(g.To, 47) - g.From + 1) > 4)
                    cell.Child = new TextBlock { Text = ended ? T("стоп", "токтоо", "stop", "dur", "stop") : (T("Э", "Э", "S", "A", "B") + (g.Stage + 1)), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = ended ? Brushes.Black : Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(cell, i);
                line.Children.Add(cell);
            }
            _simResult.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = line });
            var scale = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*") };
            for (var i = 0; i < 5; i++)
            {
                var t = Soft(i == 0 ? T("1 мес", "1 ай", "1 mo", "1 ay", "1 oy") : (i * 12).ToString(CultureInfo.InvariantCulture), 11);
                t.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : i == 4 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
                Grid.SetColumn(t, i);
                scale.Children.Add(t);
            }
            _simResult.Children.Add(scale);
        }
        foreach (var g in sim.Stages.Take(6))
        {
            var ended = g.Stage >= terms.Stages.Count;
            var open = g == sim.Stages[^1];
            var range = open ? T($"с {g.From + 1}-го мес и далее", $"{g.From + 1}-айдан баштап", $"from month {g.From + 1} on", $"{g.From + 1}. aydan itibaren", $"{g.From + 1}-oydan boshlab") : T("мес ", "ай ", "month ", "ay ", "oy ") + (g.From + 1) + (g.To > g.From ? "–" + (g.To + 1) : "");
            var row = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4) };
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            top.Children.Add(Plain(InvestmentText.StageName(terms, g.Stage) + "  ·  " + range, 13, true));
            var pay = Plain("≈ " + Money0(g.PayPerMonth) + " / " + T("мес", "ай", "mo", "ay", "oy"), 13, true);
            pay.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(pay, 1);
            top.Children.Add(pay);
            row.Children.Add(top);
            row.Children.Add(Soft(T("Бизнесу остаётся ≈ ", "Бизнеске калат ≈ ", "The business keeps ≈ ", "İşletmede kalan ≈ ", "Biznesda qoladi ≈ ") + Money0(Math.Max(0m, sim.Monthly - g.PayPerMonth)) + " / " + T("мес", "ай", "mo", "ay", "oy"), 12));
            _simResult.Children.Add(row);
        }
    }

    // ───────────────────────────── «Как это читается» ─────────────────────────────

    private string Summary(InvestorItem draft)
    {
        var t = draft.Terms;
        var name = string.IsNullOrWhiteSpace(draft.Name) ? T("Инвестор", "Инвестор", "The investor", "Yatırımcı", "Investor") : draft.Name.Trim();
        var baseWord = t.Base switch
        {
            "revenue" => T("выручки", "түшүмдөн", "of revenue", "cirodan", "tushumdan"),
            "gross" => T("валовой прибыли", "дүң пайдадан", "of gross profit", "brüt kârdan", "yalpi foydadan"),
            _ => T("чистой прибыли", "таза пайдадан", "of net profit", "net kârdan", "sof foydadan"),
        };
        var parts = new List<string>();
        for (var i = 0; i < t.Stages.Count; i++)
        {
            var st = t.Stages[i];
            var pct = st.Pct.ToString("0.##", Ru);
            var s = (i == 0 ? "" : T("затем ", "андан кийин ", "then ", "sonra ", "keyin ")) + pct + "%" + (i == 0 ? " " + baseWord : "");
            if (st.Cond == "return")
                s += T(", пока не получит ", ", алганга чейин ", ", until he receives ", ", şunu alana kadar ", ", olguncha ") + (st.Val == 100m ? T("обратно всю сумму вложений", "салымдын бардык суммасын кайра", "the whole investment back", "yatırımın tamamını geri", "sarmoyaning butun summasini qaytarib")
                    : T($"{st.Val:0.##}% от вложений", $"салымдын {st.Val:0.##}%", $"{st.Val:0.##}% of the investment", $"yatırımın %{st.Val:0.##}'ini", $"sarmoyaning {st.Val:0.##}%")) + " (" + Money0(draft.Amount * st.Val / 100m) + ")";
            else if (st.Cond == "months")
                s += T($" в течение {st.Val:0.##} мес.", $" {st.Val:0.##} ай ичинде", $" for {st.Val:0.##} months", $" {st.Val:0.##} ay boyunca", $" {st.Val:0.##} oy davomida");
            else
                s += T(" бессрочно", " чексиз", " indefinitely", " süresiz", " muddatsiz");
            parts.Add(s);
        }
        var text = name + T(" получает ", " алат: ", " receives ", " alır: ", " oladi: ") + string.Join("; ", parts) + ".";
        if (t.Stages.Count > 0 && t.Stages[^1].Cond != "forever")
            text += T(" После этого выплаты прекращаются.", " Андан кийин төлөмдөр токтойт.", " After that the payouts stop.", " Bundan sonra ödemeler durur.", " Shundan keyin to'lovlar to'xtaydi.");
        if (t.ReservePct > 0m)
            text += T($" Перед распределением {t.ReservePct:0.##}% прибыли уходит в резервный фонд.", $" Бөлүштүрүүдөн мурун пайданын {t.ReservePct:0.##}% резервге кетет.", $" Before sharing, {t.ReservePct:0.##}% of the profit goes to the reserve fund.", $" Paylaşımdan önce kârın %{t.ReservePct:0.##}'i yedek fona gider.", $" Taqsimlashdan oldin foydaning {t.ReservePct:0.##}% zaxira fondiga ketadi.");
        text += " " + t.LossRule switch
        {
            "carry" => T("Убыток переносится и сначала покрывается из будущей прибыли.", "Зыян ташылат жана адегенде келечектеги пайдадан жабылат.", "A loss is carried over and first covered from future profit.", "Zarar devredilir ve önce gelecek kârdan kapatılır.", "Zarar ko'chiriladi va avval kelgusi foydadan yopiladi."),
            "none" => T("Убыточные месяцы не переносятся.", "Зыяндуу айлар ташылбайт.", "Loss-making months are not carried over.", "Zararlı aylar devredilmez.", "Zararli oylar ko'chirilmaydi."),
            _ => T("Инвестор участвует в убытке пропорционально доле.", "Инвестор зыянга үлүшүнө жараша катышат.", "The investor shares the loss in proportion to his share.", "Yatırımcı zarara payı oranında katılır.", "Investor zararda ulushiga mutanosib ishtirok etadi."),
        };
        text += " " + t.Period switch
        {
            "month" => T("Расчёт ежемесячно.", "Эсептөө ай сайын.", "Calculated monthly.", "Hesaplama aylık.", "Hisob har oy."),
            "quarter" => T("Расчёт ежеквартально.", "Эсептөө чейрек сайын.", "Calculated quarterly.", "Hesaplama üç aylık.", "Hisob har chorak."),
            "year" => T("Расчёт раз в год.", "Эсептөө жылына бир жолу.", "Calculated once a year.", "Hesaplama yılda bir.", "Hisob yilda bir marta."),
            _ => T("Расчёт вручную, по решению владельца.", "Эсептөө кол менен, ээсинин чечими боюнча.", "Calculated manually, at the owner's decision.", "Hesaplama elle, sahibin kararıyla.", "Hisob qo'lda, egasi qaroriga ko'ra."),
        };
        if (t.MinMonthly > 0m)
            text += T($" Минимальная выплата — {Money0(t.MinMonthly)} в месяц.", $" Минималдуу төлөм — айына {Money0(t.MinMonthly)}.", $" Minimum payout — {Money0(t.MinMonthly)} a month.", $" Asgari ödeme — ayda {Money0(t.MinMonthly)}.", $" Minimal to'lov — oyiga {Money0(t.MinMonthly)}.");
        if (t.TaxPct > 0m)
            text += T($" С выплат удерживается налог {t.TaxPct:0.##}%.", $" Төлөмдөрдөн {t.TaxPct:0.##}% салык кармалат.", $" A {t.TaxPct:0.##}% tax is withheld from payouts.", $" Ödemelerden %{t.TaxPct:0.##} vergi kesilir.", $" To'lovlardan {t.TaxPct:0.##}% soliq ushlanadi.");
        text += T(" Выплата за месяц не больше чистой прибыли месяца.", " Айлык төлөм айдын таза пайдасынан ашпайт.", " A month's payout never exceeds the month's net profit.", " Aylık ödeme o ayın net kârını aşmaz.", " Oylik to'lov oyning sof foydasidan oshmaydi.");
        return text;
    }
}
