using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-28, владелец: «Схема зарплат от чистой прибыли, от чистой маржи». Редактор правила начисления: от чего считать
/// (маржа / чистая прибыль), процент, минимальная прибыль (порог), «только сверх порога», прогрессивная шкала «до X — a %, свыше — b %».
/// Используется в окне схемы сотрудника и в общих настройках (правило пула).</summary>
internal sealed class ProfitPayRuleEditor
{
    private readonly ComboBox _base = new();
    private readonly TextBox _pct = SectionCards.Field(new TextBox { Watermark = "5" });
    private readonly TextBox _min = SectionCards.Field(new TextBox { Watermark = "0" });
    private readonly CheckBox _excess = new();
    private readonly CheckBox _prog = new();
    private readonly TextBox _limit = SectionCards.Field(new TextBox { Watermark = "100000" });
    private readonly TextBox _above = SectionCards.Field(new TextBox { Watermark = "7" });
    private readonly StackPanel _progPanel = new() { Spacing = 4 };
    private readonly TextBlock _pctLabel;
    public StackPanel Panel { get; } = new() { Spacing = 6 };

    public ProfitPayRuleEditor(Control host, bool allowNone)
    {
        if (allowNone)
            _base.Items.Add(new ComboBoxItem { Content = Tr.T("Нет надбавки от прибыли", "Пайдадан кошумча жок", "No profit-based pay", "Kârdan ek ödeme yok", "Foydadan ustama yo'q"), Tag = ProfitPayBase.None });
        _base.Items.Add(new ComboBoxItem { Content = Tr.T("% от чистой маржи (выручка − закупка проданного)", "Таза маржадан % (түшүм − сатылгандын баасы)", "% of net margin (revenue − cost of goods sold)", "Net marjdan % (ciro − satılanın maliyeti)", "Sof marjadan % (tushum − sotilganning narxi)"), Tag = ProfitPayBase.Margin });
        _base.Items.Add(new ComboBoxItem { Content = Tr.T("% от чистой прибыли (маржа − расходы магазина)", "Таза пайдадан % (маржа − дүкөн чыгымдары)", "% of net profit (margin − shop expenses)", "Net kârdan % (marj − mağaza giderleri)", "Sof foydadan % (marja − do'kon xarajatlari)"), Tag = ProfitPayBase.NetProfit });
        _base.HorizontalAlignment = HorizontalAlignment.Stretch;
        _base.MinHeight = 44;
        _base.SelectionChanged += (_, _) => UpdateVisibility();
        Panel.Children.Add(L(host, Tr.T("От чего считать", "Эмнеден эсептөө", "Calculated from", "Neye göre hesaplanır", "Nimadan hisoblanadi")));
        Panel.Children.Add(_base);
        _pctLabel = L(host, "");
        Panel.Children.Add(_pctLabel);
        Panel.Children.Add(_pct);
        Panel.Children.Add(L(host, Tr.T("Минимальная прибыль для начисления, сом (0 — без порога)", "Эсептөө үчүн минималдуу пайда, сом (0 — босогосуз)", "Minimum profit to accrue, som (0 — no threshold)", "Tahakkuk için asgari kâr, som (0 — eşiksiz)", "Hisob uchun minimal foyda, so'm (0 — chegarasiz)")));
        Panel.Children.Add(_min);
        _excess.Content = Tr.T("Процент только с суммы сверх порога", "Пайыз босогодон ашкан суммадан гана", "Percentage only on the amount above the threshold", "Yüzde yalnızca eşiği aşan tutardan", "Foiz faqat chegaradan oshgan summadan");
        _excess.MinHeight = 44;
        Panel.Children.Add(_excess);
        _prog.Content = Tr.T("Прогрессивная шкала: до предела — один процент, свыше — другой", "Прогрессивдүү шкала: чекке чейин бир пайыз, андан ашса башка", "Progressive scale: one percentage up to the limit, another above", "Kademeli ölçek: sınıra kadar bir yüzde, üstü başka", "Progressiv shkala: chegaragacha bir foiz, undan oshsa boshqa");
        _prog.MinHeight = 44;
        _prog.IsCheckedChanged += (_, _) => UpdateVisibility();
        Panel.Children.Add(_prog);
        _progPanel.Children.Add(L(host, Tr.T("Предел шкалы, сом (до него — основной процент)", "Шкала чеги, сом (ага чейин — негизги пайыз)", "Scale limit, som (up to it — the main percentage)", "Ölçek sınırı, som (ona kadar — ana yüzde)", "Shkala chegarasi, so'm (unga qadar — asosiy foiz)")));
        _progPanel.Children.Add(_limit);
        _progPanel.Children.Add(L(host, Tr.T("Процент свыше предела, %", "Чектен ашканына пайыз, %", "Percentage above the limit, %", "Sınırın üstündeki yüzde, %", "Chegaradan oshganiga foiz, %")));
        _progPanel.Children.Add(_above);
        Panel.Children.Add(_progPanel);
        _base.SelectedIndex = 0;
        UpdateVisibility();
    }

    private static TextBlock L(Control host, string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        t.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private ProfitPayBase CurrentBase => (_base.SelectedItem as ComboBoxItem)?.Tag is ProfitPayBase b ? b : ProfitPayBase.Margin;

    private void UpdateVisibility()
    {
        var active = CurrentBase != ProfitPayBase.None;
        foreach (var c in Panel.Children.Skip(2))
            c.IsVisible = active;
        _progPanel.IsVisible = active && _prog.IsChecked == true;
        _pctLabel.Text = _prog.IsChecked == true
            ? Tr.T("Процент до предела, %", "Чекке чейинки пайыз, %", "Percentage up to the limit, %", "Sınıra kadar yüzde, %", "Chegaragacha foiz, %")
            : Tr.T("Процент, %", "Пайыз, %", "Percentage, %", "Yüzde, %", "Foiz, %");
    }

    public void Load(ProfitPayRule rule)
    {
        foreach (var item in _base.Items.OfType<ComboBoxItem>())
            if (item.Tag is ProfitPayBase b && b == rule.Base)
                _base.SelectedItem = item;
        if (_base.SelectedItem is null)
            _base.SelectedIndex = 0;
        _pct.Text = Fmt(rule.Percent);
        _min.Text = Fmt(rule.MinProfit);
        _excess.IsChecked = rule.OnlyExcess;
        _prog.IsChecked = rule.Progressive;
        _limit.Text = Fmt(rule.Limit);
        _above.Text = Fmt(rule.PercentAbove);
        UpdateVisibility();
    }

    private static string Fmt(decimal v) => v > 0 ? v.ToString("0.##", CultureInfo.InvariantCulture) : "";

    private static decimal Parse(string? raw)
    {
        var text = new string((raw ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace(',', '.');
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : -1;
    }

    /// <summary>Правило из полей; при ошибке — null и текст ошибки.</summary>
    public (ProfitPayRule? Rule, string? Error, Control? Field) Read()
    {
        var rule = new ProfitPayRule { Base = CurrentBase };
        if (rule.Base == ProfitPayBase.None)
            return (rule, null, null);
        var pct = Parse(_pct.Text);
        if (pct <= 0 || pct > 100)
            return (null, Tr.T("Процент — число больше 0 и не больше 100.", "Пайыз — 0дөн чоң жана 100дөн ашпаган сан.", "The percentage must be above 0 and at most 100.", "Yüzde 0'dan büyük ve en fazla 100 olmalı.", "Foiz 0 dan katta va 100 dan oshmasligi kerak."), _pct);
        var min = string.IsNullOrWhiteSpace(_min.Text) ? 0 : Parse(_min.Text);
        if (min < 0)
            return (null, Tr.T("Порог — число не меньше 0.", "Босого — 0дөн кем эмес сан.", "The threshold must be a number of at least 0.", "Eşik en az 0 olmalı.", "Chegara kamida 0 bo'lishi kerak."), _min);
        rule.Percent = pct;
        rule.MinProfit = min;
        rule.OnlyExcess = _excess.IsChecked == true && min > 0;
        if (_prog.IsChecked == true)
        {
            var limit = Parse(_limit.Text);
            var above = Parse(_above.Text);
            if (limit <= 0)
                return (null, Tr.T("Предел шкалы — число больше 0.", "Шкала чеги — 0дөн чоң сан.", "The scale limit must be above 0.", "Ölçek sınırı 0'dan büyük olmalı.", "Shkala chegarasi 0 dan katta bo'lishi kerak."), _limit);
            if (above < 0 || above > 100)
                return (null, Tr.T("Процент свыше предела — от 0 до 100.", "Чектен ашканына пайыз — 0дөн 100гө чейин.", "The percentage above the limit must be between 0 and 100.", "Sınır üstü yüzde 0 ile 100 arasında olmalı.", "Chegaradan oshganiga foiz 0 dan 100 gacha bo'lishi kerak."), _above);
            rule.Progressive = true;
            rule.Limit = limit;
            rule.PercentAbove = above;
        }
        return (rule, null, null);
    }
}

/// <summary>Схема «от прибыли» одного сотрудника. Результат ShowDialog — сохранённая схема (null — отмена).</summary>
public sealed class ProfitPaySchemeDialog : Window
{
    private readonly ProfitPayScheme _scheme;
    private readonly ComboBox _scope = new() { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ProfitPayRuleEditor _rule;
    private readonly TextBlock _poolHint;
    private readonly TextBlock _error;
    private readonly StackPanel _ruleHost = new() { Spacing = 6 };

    public ProfitPaySchemeDialog(ProfitPayScheme scheme)
    {
        _scheme = scheme.Clone();
        var title = Tr.T("Зарплата от прибыли", "Пайдадан эмгек акы", "Profit-based pay", "Kârdan maaş", "Foydadan ish haqi");
        Title = title;
        SectionCards.AsModal(this, 592);
        _rule = new ProfitPayRuleEditor(this, allowNone: true);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = Tr.T("Надбавка прибавляется к тому, что насчитал сервер по схеме сотрудника (оклад, процент от продаж, за товар) — так получаются комбинации «оклад + % от маржи» и «оклад + % от чистой прибыли».",
                "Кошумча сервер кызматкердин схемасы боюнча (айлык, сатуудан пайыз, товар үчүн) эсептегенге кошулат — ушундай «айлык + маржадан %» жана «айлык + таза пайдадан %» чыгат.",
                "The extra is added to what the server accrued by the employee's scheme (salary, sales percentage, per item) — this gives “salary + % of margin” and “salary + % of net profit”.",
                "Ek, sunucunun çalışanın şemasına göre hesapladığına eklenir (maaş, satış yüzdesi, ürün başına) — böylece «maaş + marj %» ve «maaş + net kâr %» olur.",
                "Ustama server xodim sxemasi bo'yicha hisoblaganiga qo'shiladi (maosh, sotuvdan foiz, mahsulot uchun) — shunda «maosh + marja %» va «maosh + sof foyda %» chiqadi."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        });
        ((TextBlock)panel.Children[0]).Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(Lbl(Tr.T("По чьим цифрам", "Кимдин сандары боюнча", "Whose figures", "Kimin rakamlarına göre", "Kimning raqamlari bo'yicha")));
        _scope.Items.Add(new ComboBoxItem { Content = Tr.T("По своим продажам (доля сотрудника в продажах)", "Өз сатуулары боюнча (кызматкердин сатуудагы үлүшү)", "By own sales (the employee's share of sales)", "Kendi satışlarına göre (çalışanın satış payı)", "O'z sotuvlari bo'yicha (xodimning sotuvdagi ulushi)"), Tag = ProfitPayScope.Own });
        _scope.Items.Add(new ComboBoxItem { Content = Tr.T("По общему результату магазина (процент от всей прибыли)", "Дүкөндүн жалпы жыйынтыгы боюнча (бүт пайдадан %)", "By the shop's overall result (percentage of all profit)", "Mağazanın genel sonucuna göre (tüm kârdan yüzde)", "Do'konning umumiy natijasi bo'yicha (butun foydadan foiz)"), Tag = ProfitPayScope.Shop });
        _scope.Items.Add(new ComboBoxItem { Content = Tr.T("Общий пул (делится между участниками)", "Жалпы пул (катышуучулар арасында бөлүнөт)", "Shared pool (split between participants)", "Ortak havuz (katılımcılar arasında bölünür)", "Umumiy pul (ishtirokchilar o'rtasida bo'linadi)"), Tag = ProfitPayScope.Pool });
        _scope.SelectionChanged += (_, _) => UpdateScope();
        panel.Children.Add(_scope);
        _poolHint = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false, Text = Tr.T("Правило пула и способ деления (по продажам, по часам, поровну) — в «Настройки магазина» окна «Зарплата от прибыли».", "Пулдун эрежеси жана бөлүү жолу (сатуу, саат, теңдей) — «Зарплата от прибыли» терезесинин «Дүкөн жөндөөлөрүндө».", "The pool rule and how it is split (by sales, hours, equally) are in “Shop settings” of the “Profit-based pay” window.", "Havuz kuralı ve bölüşüm (satış, saat, eşit) «Kârdan maaş» penceresinin «Mağaza ayarları»nda.", "Pul qoidasi va bo'lish usuli (sotuv, soat, teng) — «Foydadan ish haqi» oynasining «Do'kon sozlamalari»da.") };
        _poolHint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(_poolHint);
        _ruleHost.Children.Add(_rule.Panel);
        panel.Children.Add(_ruleHost);
        _error = SectionCards.ErrorText(this);
        panel.Children.Add(_error);

        var save = SectionCards.Primary(Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), isDefault: true);
        var cancel = SectionCards.Cancel(Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(null);
        save.Click += (_, _) => Save();
        Content = SectionCards.Shell(title, scheme.Name, panel, SectionCards.Buttons(cancel, save));

        foreach (var item in _scope.Items.OfType<ComboBoxItem>())
            if (item.Tag is ProfitPayScope s && s == _scheme.Scope)
                _scope.SelectedItem = item;
        if (_scope.SelectedItem is null)
            _scope.SelectedIndex = 0;
        _rule.Load(_scheme.Rule);
        UpdateScope();
    }

    private TextBlock Lbl(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
        t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private ProfitPayScope CurrentScope => (_scope.SelectedItem as ComboBoxItem)?.Tag is ProfitPayScope s ? s : ProfitPayScope.Own;

    private void UpdateScope()
    {
        var pool = CurrentScope == ProfitPayScope.Pool;
        _poolHint.IsVisible = pool;
        _ruleHost.IsVisible = !pool;
        SectionCards.ShowError(_error, null);
    }

    private void Save()
    {
        _scheme.Scope = CurrentScope;
        if (_scheme.Scope == ProfitPayScope.Pool)
        {
            // Правило пула — общее; у сотрудника достаточно отметки «участвует». Базу берём любую активную, чтобы схема считалась активной.
            _scheme.Rule = new ProfitPayRule { Base = ProfitPayBase.Margin, Percent = 1 };
        }
        else
        {
            var (rule, error, field) = _rule.Read();
            if (rule is null)
            {
                SectionCards.ShowError(_error, error, field);
                return;
            }
            _scheme.Rule = rule;
        }
        Close(_scheme);
    }
}

/// <summary>Общие настройки зарплаты от прибыли: какие расходы вычитать, возвраты, округление, пул.</summary>
public sealed class ProfitPayShopDialog : Window
{
    private readonly ProfitPayShopSettings _shop;
    private readonly CheckBox _sal, _rent, _tax, _util, _other, _supplier, _returns;
    private readonly ComboBox _round = new() { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _share = new() { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ProfitPayRuleEditor _pool;
    private readonly TextBlock _error;

    private static CheckBox Box(string text, bool on) => new() { Content = text, IsChecked = on, MinHeight = 44 };

    public ProfitPayShopDialog(ProfitPayShopSettings shop)
    {
        _shop = shop.Clone();
        var title = Tr.T("Настройки: зарплата от прибыли", "Жөндөөлөр: пайдадан эмгек акы", "Settings: profit-based pay", "Ayarlar: kârdan maaş", "Sozlamalar: foydadan ish haqi");
        Title = title;
        SectionCards.AsModal(this, 672);
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Head(Tr.T("Что вычитать из чистой прибыли (расходы — те же статьи, что в «Прибыли и деньгах»)", "Таза пайдадан эмнени алуу (чыгымдар — «Пайда жана акча» сыяктуу беренелер)", "What to subtract from net profit (expenses — the same categories as in “Profit & cash”)", "Net kârdan ne düşülsün (giderler — «Kâr ve nakit»teki kalemlerle aynı)", "Sof foydadan nima ayirish (xarajatlar — «Foyda va pul»dagi moddalar)")));
        _sal = Box(Tr.T("Зарплаты (сама зарплата сотрудников)", "Эмгек акы (кызматкерлердин өзүнүн эмгек акысы)", "Salaries (the staff pay itself)", "Maaşlar (personelin kendi maaşı)", "Ish haqi (xodimlarning o'z ish haqi)"), _shop.SubtractSalaries);
        _rent = Box(Tr.T("Аренда", "Ижара", "Rent", "Kira", "Ijara"), _shop.SubtractRent);
        _tax = Box(Tr.T("Налоги", "Салыктар", "Taxes", "Vergiler", "Soliqlar"), _shop.SubtractTaxes);
        _util = Box(Tr.T("Коммунальные", "Коммуналдык", "Utilities", "Faturalar", "Kommunal"), _shop.SubtractUtilities);
        _other = Box(Tr.T("Прочие расходы", "Башка чыгымдар", "Other expenses", "Diğer giderler", "Boshqa xarajatlar"), _shop.SubtractOther);
        _supplier = Box(Tr.T("Закупки «из ящика» кассы (товар уже учтён в закупочной цене проданного — по умолчанию не вычитать)", "Кассанын «суурмасынан» сатып алуулар (товар сатылгандын баасында эсепке алынган — демейки алынбайт)", "Purchases paid “from the till” (goods are already in the cost of goods sold — not subtracted by default)", "Kasa «çekmecesinden» alımlar (mal zaten satılanın maliyetinde — varsayılan düşülmez)", "Kassa «tortmasi»dan xaridlar (tovar sotilgan narxida hisobga olingan — odatda ayirilmaydi)"), _shop.SubtractSupplierCash);
        _returns = Box(Tr.T("Вычесть возвраты отдельно (включайте, только если выручка сервера возвраты не вычитает)", "Кайтаруууларды өзүнчө алуу (сервердин түшүмү кайтарууну албаса гана)", "Subtract refunds separately (only if the server revenue does not already subtract them)", "İadeleri ayrıca düş (yalnızca sunucu cirosu düşmüyorsa)", "Qaytarishlarni alohida ayirish (faqat server tushumi ayirmasa)"), _shop.SubtractReturns);
        foreach (var c in new[] { _sal, _rent, _tax, _util, _other, _supplier, _returns })
            panel.Children.Add(c);
        panel.Children.Add(Head(Tr.T("Округление начисления", "Эсептөөнү тегеректөө", "Rounding of the accrual", "Tahakkuk yuvarlaması", "Hisobni yaxlitlash")));
        _round.Items.Add(new ComboBoxItem { Content = Tr.T("До тыйына (0,01 сом)", "Тыйынга чейин (0,01 сом)", "To the tyiyn (0.01 som)", "Kuruşa kadar (0,01 som)", "Tiyingacha (0,01 so'm)"), Tag = ProfitPayRounding.Tyiyn });
        _round.Items.Add(new ComboBoxItem { Content = Tr.T("До целого сома", "Бүтүн сомго чейин", "To a whole som", "Tam soma", "Butun so'mgacha"), Tag = ProfitPayRounding.Som });
        _round.SelectedIndex = _shop.Rounding == ProfitPayRounding.Som ? 1 : 0;
        panel.Children.Add(_round);
        panel.Children.Add(Head(Tr.T("Общий пул (для сотрудников со схемой «общий пул»)", "Жалпы пул («жалпы пул» схемасындагы кызматкерлер үчүн)", "Shared pool (for employees with the “shared pool” scheme)", "Ortak havuz («ortak havuz» şemalı çalışanlar için)", "Umumiy pul («umumiy pul» sxemali xodimlar uchun)")));
        _pool = new ProfitPayRuleEditor(this, allowNone: false);
        _pool.Load(_shop.Pool.Base == ProfitPayBase.None ? new ProfitPayRule { Base = ProfitPayBase.Margin } : _shop.Pool);
        panel.Children.Add(_pool.Panel);
        panel.Children.Add(Head(Tr.T("Как делить пул", "Пулду кантип бөлүү", "How to split the pool", "Havuz nasıl bölünsün", "Pulni qanday bo'lish")));
        _share.Items.Add(new ComboBoxItem { Content = Tr.T("По доле продаж", "Сатуу үлүшү боюнча", "By sales share", "Satış payına göre", "Sotuv ulushi bo'yicha"), Tag = ProfitPayShare.Sales });
        _share.Items.Add(new ComboBoxItem { Content = Tr.T("По отработанным часам (табель)", "Иштелген сааттар боюнча (табель)", "By hours worked (timesheet)", "Çalışılan saate göre (puantaj)", "Ishlangan soat bo'yicha (tabel)"), Tag = ProfitPayShare.Hours });
        _share.Items.Add(new ComboBoxItem { Content = Tr.T("Поровну", "Теңдей", "Equally", "Eşit", "Teng"), Tag = ProfitPayShare.Equal });
        _share.SelectedIndex = (int)_shop.Share;
        panel.Children.Add(_share);
        _error = SectionCards.ErrorText(this);
        panel.Children.Add(_error);

        var save = SectionCards.Primary(Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), isDefault: true);
        var cancel = SectionCards.Cancel(Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(null);
        save.Click += (_, _) => Save();
        Content = SectionCards.Shell(title, null, panel, SectionCards.Buttons(cancel, save));
    }

    private TextBlock Head(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 2) };
        t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        return t;
    }

    private void Save()
    {
        var (rule, error, field) = _pool.Read();
        if (rule is null)
        {
            SectionCards.ShowError(_error, error, field);
            return;
        }
        _shop.SubtractSalaries = _sal.IsChecked == true;
        _shop.SubtractRent = _rent.IsChecked == true;
        _shop.SubtractTaxes = _tax.IsChecked == true;
        _shop.SubtractUtilities = _util.IsChecked == true;
        _shop.SubtractOther = _other.IsChecked == true;
        _shop.SubtractSupplierCash = _supplier.IsChecked == true;
        _shop.SubtractReturns = _returns.IsChecked == true;
        _shop.Rounding = (_round.SelectedItem as ComboBoxItem)?.Tag is ProfitPayRounding r ? r : ProfitPayRounding.Tyiyn;
        _shop.Share = (_share.SelectedItem as ComboBoxItem)?.Tag is ProfitPayShare s ? s : ProfitPayShare.Sales;
        _shop.Pool = rule;
        Close(_shop);
    }
}
