using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-26: итог окна «Оплата долга поставщику» — сумма, закрывает ли она весь долг окна, способ оплаты («cash» — наличные, «transfer» — безнал).</summary>
public sealed record SupplierDebtPayChoice(double Amount, bool IsFull, string Method);

/// <summary>2026-11-26 (владелец: «Поставщиктин карызын төлөгөндө жарымын же 20% бере албайт экенсиң, только 100 пайыз төлөш керек экен»): окно оплаты долга поставщику с любой суммой.
/// Раньше «Оплатить взнос» и «Погасить весь долг» показывали только подтверждение «Оплатить N сом?» — гасился весь взнос/долг. Теперь: поле «Сумма оплаты» от 0,01 до остатка (расчёт —
/// SupplierDebtPayMath), кнопки «Весь долг», «50 %», «20 %», строка «Останется долг: …», способ оплаты «Наличные / Безнал». «Оплатить» возвращает <see cref="SupplierDebtPayChoice"/>
/// (Close(choice)), «Отмена» — null. Сами запросы и ключи повтора — в карточке поставщика (SupplierCardView).</summary>
public sealed class SupplierDebtPayDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly double _max;
    private readonly TextBox _amount;
    private readonly TextBlock _preview = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MinHeight = 22 };
    private readonly StackPanel _methodSeg = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private readonly Button _pay;
    private string _method = "cash";
    private bool _syncing;
    private bool? _previewDanger;

    /// <param name="supplierName">Поставщик (в заголовке).</param>
    /// <param name="scope">Что оплачивается: «Взнос №2 по долгу «…», срок …» или «Весь долг: 3 взноса».</param>
    /// <param name="max">Остаток, больше которого платить нельзя.</param>
    /// <param name="maxLabel">Подпись суммы остатка: «Долг к оплате» / «Остаток взноса».</param>
    /// <param name="extraNote">Пояснения под текстом (долг без графика, подозрение на двойную запись) — как раньше в вопросе «Оплатить весь долг?»; пусто — нет.</param>
    public SupplierDebtPayDialog(string supplierName, string scope, double max, string maxLabel, string extraNote = "")
    {
        _max = max;
        Title = T("Оплата долга поставщику", "Жеткирүүчүгө карызды төлөө", "Pay the supplier debt", "Tedarikçi borcunu öde", "Yetkazib beruvchiga qarzni to'lash");
        SectionCards.AsModal(this, 520);

        var maxText = new TextBlock { Text = MoneyText.Som(max), FontSize = 22, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        maxText.Classes.Add("num");
        maxText.Bind(TextBlock.ForegroundProperty, maxText.GetResourceObservable("BrushText"));

        _amount = SectionCards.Field(new TextBox
        {
            Text = MoneyText.Weight(max, "0.00"), MaxLength = 14, HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 18, FontWeight = FontWeight.Bold, Watermark = "0,00",
        });
        Avalonia.Automation.AutomationProperties.SetName(_amount, T("Сумма оплаты", "Төлөө суммасы", "Payment amount", "Ödeme tutarı", "To'lov summasi"));
        _amount.TextChanged += (_, _) =>
        {
            if (!_syncing)
                Refresh();
        };

        // «Весь долг / 50 % / 20 %» — три равные ячейки одной строкой (Grid, а не WrapPanel: правило полей форм)
        var quick = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*,8,*") };
        var quickItems = new (int Percent, string Text)[]
        {
            (100, T("Весь долг", "Бардык карыз", "Whole debt", "Tüm borç", "Butun qarz")),
            (50, "50 %"),
            (20, "20 %"),
        };
        for (var i = 0; i < quickItems.Length; i++)
        {
            var (percent, text) = quickItems[i];
            var chip = UiKit.Chip(this, text, false);
            chip.HorizontalAlignment = HorizontalAlignment.Stretch;
            chip.Name = "SupplierPayQuick" + percent;
            Avalonia.Automation.AutomationProperties.SetName(chip, percent == 100 ? text : T($"{percent} % от долга", $"Карыздын {percent} %", $"{percent} % of the debt", $"Borcun %{percent}'i", $"Qarzning {percent} %"));
            chip.Click += (_, _) => Pick(percent);
            Grid.SetColumn(chip, i * 2);
            quick.Children.Add(chip);
        }

        BuildMethodSegment();

        var note = new TextBlock
        {
            Text = T("Деньги спишутся расходом из основной кассы компании. Платить можно частями — остаток долга останется у поставщика, оплату можно повторить.",
                "Акча компаниянын негизги кассасынан чыгым катары чыгат. Бөлүп-бөлүп төлөөгө болот — карыздын калганы жеткирүүчүдө калат, төлөөнү кайталаса болот.",
                "The money is written off as an expense from the company's main till. You can pay in parts — the rest of the debt stays with the supplier and you can pay again.",
                "Para şirketin ana kasasından gider olarak düşülür. Parça parça ödenebilir — borcun kalanı tedarikçide kalır, ödeme tekrarlanabilir.",
                "Pul kompaniyaning asosiy kassasidan xarajat sifatida yechiladi. Qismlarga bo'lib to'lash mumkin — qarzning qolgani yetkazib beruvchida qoladi, to'lovni takrorlash mumkin."),
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        note.Bind(TextBlock.ForegroundProperty, note.GetResourceObservable("BrushTextSoft"));

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Label(maxLabel));
        panel.Children.Add(maxText);
        panel.Children.Add(Label(T("Сумма оплаты", "Төлөө суммасы", "Payment amount", "Ödeme tutarı", "To'lov summasi")));
        panel.Children.Add(_amount);
        panel.Children.Add(quick);
        panel.Children.Add(_preview);
        panel.Children.Add(Label(T("Способ оплаты", "Төлөө ыкмасы", "Payment method", "Ödeme yöntemi", "To'lov usuli")));
        panel.Children.Add(UiKit.Segment(_methodSeg));
        panel.Children.Add(note);
        if (extraNote.Trim().Length > 0)
        {
            var extra = new TextBlock { Text = extraNote.Trim(), FontSize = 12, TextWrapping = TextWrapping.Wrap };
            extra.Bind(TextBlock.ForegroundProperty, extra.GetResourceObservable("BrushWarning"));
            panel.Children.Add(extra);
        }

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(null);
        _pay = SectionCards.Primary(T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash"), isDefault: true);
        _pay.Name = "SupplierPayConfirm";
        _pay.Click += (_, _) =>
        {
            if (Read() is { } choice)
                Close(choice);
        };

        Content = SectionCards.Shell(Title, supplierName.Length > 0 ? supplierName + " · " + scope : scope, panel, SectionCards.Buttons(cancel, _pay));
        Opened += (_, _) =>
        {
            _amount.Focus();
            _amount.SelectAll();
        };
        Refresh();
    }

    private static TextBlock Label(string text)
    {
        var l = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
        l.Bind(TextBlock.ForegroundProperty, l.GetResourceObservable("BrushTextSoft"));
        return l;
    }

    private void BuildMethodSegment()
    {
        _methodSeg.Children.Clear();
        foreach (var (method, text) in new[]
                 {
                     ("cash", T("Наличные", "Накталай", "Cash", "Nakit", "Naqd")),
                     ("transfer", T("Безнал", "Накталай эмес", "Non-cash", "Nakitsiz", "Naqd pulsiz")),
                 })
        {
            var seg = UiKit.Seg(text, method == _method);
            seg.Name = "SupplierPayMethod_" + method;
            seg.Click += (_, _) =>
            {
                _method = method;
                BuildMethodSegment();
            };
            _methodSeg.Children.Add(seg);
        }
    }

    private void Pick(int percent)
    {
        _syncing = true;
        _amount.Text = MoneyText.Weight(SupplierDebtPayMath.Percent(_max, percent), "0.00");
        _syncing = false;
        Refresh();
        _amount.Focus();
    }

    /// <summary>Выбор по текущему полю; null — сумма не проходит (подсказка уже показана).</summary>
    private SupplierDebtPayChoice? Read() =>
        SupplierDebtPayMath.TryParse(_amount.Text, out var amount) && SupplierDebtPayMath.Check(amount, _max) == SupplierDebtPayMath.Problem.None
            ? new SupplierDebtPayChoice(amount, SupplierDebtPayMath.IsFull(_max, amount), _method)
            : null;

    private void Refresh()
    {
        string text;
        var ok = false;
        var danger = false;
        if (!SupplierDebtPayMath.TryParse(_amount.Text, out var amount))
        {
            text = T("Введите сумму числом, например 500 или 500,50.", "Сумманы сан менен жазыңыз, мисалы 500 же 500,50.", "Enter the amount as a number, for example 500 or 500.50.",
                "Tutarı sayıyla girin, örneğin 500 veya 500,50.", "Summani son bilan kiriting, masalan 500 yoki 500,50.");
            danger = true;
        }
        else
            switch (SupplierDebtPayMath.Check(amount, _max))
            {
                case SupplierDebtPayMath.Problem.TooSmall:
                    text = T("Сумма должна быть не меньше 0,01.", "Сумма 0,01 ден кем болбошу керек.", "The amount must be at least 0.01.", "Tutar en az 0,01 olmalı.", "Summa kamida 0,01 bo'lishi kerak.");
                    danger = true;
                    break;
                case SupplierDebtPayMath.Problem.TooBig:
                    text = T($"Сумма больше долга: можно не больше {MoneyText.Som(_max)}.", $"Сумма карыздан көп: {MoneyText.Som(_max)} ашпоо керек.", $"The amount is more than the debt: at most {MoneyText.Som(_max)}.",
                        $"Tutar borçtan fazla: en fazla {MoneyText.Som(_max)}.", $"Summa qarzdan ko'p: {MoneyText.Som(_max)} dan oshmasin.");
                    danger = true;
                    break;
                default:
                    ok = true;
                    text = SupplierDebtPayMath.IsFull(_max, amount)
                        ? T("Долг будет оплачен полностью.", "Карыз толугу менен төлөнөт.", "The debt will be paid in full.", "Borç tamamen ödenecek.", "Qarz to'liq to'lanadi.")
                        : T($"Останется долг: {MoneyText.Som(SupplierDebtPayMath.Left(_max, amount))}", $"Карыздын калганы: {MoneyText.Som(SupplierDebtPayMath.Left(_max, amount))}",
                            $"The debt left: {MoneyText.Som(SupplierDebtPayMath.Left(_max, amount))}", $"Kalan borç: {MoneyText.Som(SupplierDebtPayMath.Left(_max, amount))}",
                            $"Qarzning qolgani: {MoneyText.Som(SupplierDebtPayMath.Left(_max, amount))}");
                    break;
            }
        _preview.Text = text;
        if (_previewDanger != danger) // кисть перепривязываем только при смене цвета (Refresh зовётся на каждую цифру)
        {
            _previewDanger = danger;
            _preview.Bind(TextBlock.ForegroundProperty, _preview.GetResourceObservable(danger ? "BrushDanger" : "BrushText"));
        }
        _pay.IsEnabled = ok;
    }

    /// <summary>2026-11-26: для тестов — подставить текст поля суммы, как будто его ввели руками.</summary>
    public void SetAmountForTests(string text) => _amount.Text = text;

    /// <summary>2026-11-26: для тестов — нажатие «Весь долг» (100), «50 %» (50), «20 %» (20).</summary>
    public void PickForTests(int percent) => Pick(percent);

    public string AmountTextForTests => _amount.Text ?? "";
    public string PreviewForTests => _preview.Text ?? "";
    public bool CanPayForTests => _pay.IsEnabled;

    /// <summary>2026-11-26: для тестов — выбор окна по текущему полю и способу (null — «Оплатить» недоступно).</summary>
    public SupplierDebtPayChoice? ChoiceForTests => Read();

    /// <summary>2026-11-26: для тестов — выбрать способ оплаты («cash» / «transfer»).</summary>
    public void SetMethodForTests(string method)
    {
        _method = method;
        BuildMethodSegment();
    }
}
