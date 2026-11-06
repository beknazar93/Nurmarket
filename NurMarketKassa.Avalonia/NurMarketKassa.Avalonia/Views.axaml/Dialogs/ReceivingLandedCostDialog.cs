using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-05 (функция 11 «Себестоимость с доставкой»): окно «Доп. расходы прихода» в Закупки → Приём товара. Сумма расходов (доставка, грузчики)
/// и способ разнесения (по сумме строки / по количеству) → превью по каждой строке «закупка 100,00 → 103,45» → «Применить». Расчёт — <see cref="LandedCost"/>.
/// ДЕНЬГИ: расходы НЕ меняют документ прихода и долг поставщику — они записываются в строки как доля на единицу (ReceivingLineVm.LandedExtraPerUnit), а
/// себестоимость уходит в карточку товара после прихода. Сами расходы (оплата перевозчику) вносятся отдельным расходом.</summary>
public sealed class ReceivingLandedCostDialog : Window
{
    private readonly IReadOnlyList<ReceivingLineVm> _lines;
    private readonly TextBox _amount;
    private readonly RadioButton _byAmount;
    private readonly RadioButton _byQty;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _note = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly Button _apply;
    private List<LandedPreviewRow> _preview = new();

    /// <summary>true — расходы применены к строкам (в том числе «0» = сброс).</summary>
    public bool Applied { get; private set; }

    /// <summary>Применённая сумма расходов и способ (для пересчёта при «Оприходовать», если количество поменяли).</summary>
    public double AppliedTotal { get; private set; }
    public LandedMethod AppliedMethod { get; private set; }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);
    private static string Money(double v) => v.ToString("0.00##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    public ReceivingLandedCostDialog(IReadOnlyList<ReceivingLineVm> lines, double currentTotal, LandedMethod currentMethod)
    {
        _lines = lines;
        Title = T("Доп. расходы прихода", "Кирүүнүн кошумча чыгымдары", "Extra delivery costs", "Ek giriş masrafları", "Kirimning qo'shimcha xarajatlari");
        SectionCards.AsModal(this, 912);

        _amount = new TextBox { Width = 160, Height = 40, HorizontalAlignment = HorizontalAlignment.Left, Text = currentTotal > 0 ? currentTotal.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) : "" };
        _amount.TextChanged += (_, _) => Rebuild();
        _byAmount = new RadioButton
        {
            GroupName = "LandedMode", IsChecked = currentMethod == LandedMethod.ByAmount,
            Content = T("По сумме строки", "Саптын суммасы боюнча", "By line amount", "Satır tutarına göre", "Qator summasi bo'yicha"),
        };
        _byQty = new RadioButton
        {
            GroupName = "LandedMode", IsChecked = currentMethod == LandedMethod.ByQuantity,
            Content = T("По количеству", "Саны боюнча", "By quantity", "Miktara göre", "Miqdor bo'yicha"),
        };
        _byAmount.IsCheckedChanged += (_, _) => Rebuild();
        _byQty.IsCheckedChanged += (_, _) => Rebuild();
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Children = { _byAmount, _byQty } };

        _note.Bind(TextBlock.ForegroundProperty, _note.GetResourceObservable("BrushTextSoft"));
        var header = Row(T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("Закупка (накладная → с расходами)", "Сатып алуу (накладная → чыгым менен)", "Purchase (invoice → with costs)", "Alış (fatura → masrafla)", "Xarid (nakladnoy → xarajat bilan)"),
            T("Расходы на строку", "Сапка чыгым", "Costs on line", "Satıra masraf", "Qatorga xarajat"), soft: true);
        var scroll = new ScrollViewer { MaxHeight = 300, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = _rows };
        var amountLabel = new TextBlock { Text = T("Сумма расходов, сом", "Чыгымдардын суммасы, сом", "Costs amount, som", "Masraf tutarı, som", "Xarajat summasi, so'm"), FontSize = 13, FontWeight = FontWeight.SemiBold };
        amountLabel.Bind(TextBlock.ForegroundProperty, amountLabel.GetResourceObservable("BrushText"));
        var root = new StackPanel { Spacing = 10, Children = { amountLabel, _amount, modes, header, scroll, _note } };

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close();
        _apply = SectionCards.Primary("", isDefault: true);
        _apply.Click += (_, _) =>
        {
            LandedCost.Apply(_preview);
            AppliedTotal = Amount;
            AppliedMethod = Method;
            Applied = true;
            Close();
        };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _apply } };

        Content = SectionCards.Shell(Title,
            T("Доставка и грузчики попадают в себестоимость товара, а долг поставщику остаётся по накладной. Расходы внесите отдельным расходом. В карточку товара запишется себестоимость (закупка + доля расходов), а не цена накладной.",
                "Жеткирүү жана жүк ташуучулар товардын өздүк баасына кошулат, ал эми жеткирүүчүгө карыз накладная боюнча калат. Чыгымдарды өзүнчө чыгым катары киргизиңиз. Товар карточкасына өздүк баа (сатып алуу + чыгым үлүшү) жазылат, накладнойдогу баа эмес.",
                "Delivery and loaders go into the product cost; the supplier debt stays as per the invoice. Record the costs as a separate expense. The product card gets the cost (purchase + share of costs), not the invoice price.",
                "Nakliye ve hamal masrafı ürün maliyetine eklenir; tedarikçi borcu faturadaki gibi kalır. Masrafı ayrı gider olarak kaydedin. Ürün kartına fatura fiyatı değil maliyet (alış + masraf payı) yazılır.",
                "Yetkazib berish va yuk tashuvchilar mahsulot tannarxiga qo'shiladi, yetkazib beruvchiga qarz esa nakladnoy bo'yicha qoladi. Xarajatlarni alohida xarajat sifatida kiriting. Mahsulot kartasiga nakladnoy narxi emas, tannarx (xarid + xarajat ulushi) yoziladi."),
            root, footer);
        Rebuild();
    }

    private LandedMethod Method => _byQty.IsChecked == true ? LandedMethod.ByQuantity : LandedMethod.ByAmount;

    private double Amount => CheckoutValidation.TryParseAmount(_amount.Text, out var v) == CheckoutValidation.AmountParse.Ok ? v : 0;

    private void Rebuild()
    {
        _preview = LandedCost.Preview(_lines, Amount, Method);
        _rows.Children.Clear();
        foreach (var r in _preview)
            _rows.Children.Add(Row(string.IsNullOrWhiteSpace(r.Line.ProductName) ? r.Line.Barcode : r.Line.ProductName,
                $"{Money(r.OldCost)} → {Money(r.NewCost)}", Money(r.ExtraTotal), soft: false));
        var distributed = _preview.Sum(r => r.ExtraTotal);
        var hasAmount = Amount > 0;
        _note.Text = !string.IsNullOrWhiteSpace(_amount.Text) && CheckoutValidation.TryParseAmount(_amount.Text, out _) != CheckoutValidation.AmountParse.Ok
            ? T("Введите сумму числом, например 3000 или 1250,50.", "Сумманы сан менен жазыңыз, мисалы 3000 же 1250,50.", "Enter the amount as a number, e.g. 3000 or 1250.50.", "Tutarı sayı olarak girin, örn. 3000 veya 1250,50.", "Summani son bilan kiriting, masalan 3000 yoki 1250,50.")
            : hasAmount
                ? T($"Разнесено {Money(distributed)} из {Money(Amount)} сом (цена хранится до 4 знаков, поэтому возможна разница в копейки).",
                    $"{Money(Amount)} сомдун {Money(distributed)} сому бөлүштүрүлдү (баа 4 белгиге чейин сакталат, ошондуктан тыйынга айырма болушу мүмкүн).",
                    $"Distributed {Money(distributed)} of {Money(Amount)} som (the price is kept to 4 decimals, so a few tyiyn of difference is possible).",
                    $"{Money(Amount)} somun {Money(distributed)} somu dağıtıldı (fiyat 4 basamak tutulur, kuruş farkı olabilir).",
                    $"{Money(Amount)} so'mdan {Money(distributed)} so'm taqsimlandi (narx 4 xonagacha saqlanadi, tiyin farqi bo'lishi mumkin).")
                : T("Введите сумму расходов, чтобы увидеть новую закупку. Пусто или 0 — расходов нет.", "Жаңы сатып алуу баасын көрүү үчүн чыгымдын суммасын киргизиңиз. Бош же 0 — чыгым жок.",
                    "Enter the costs amount to see the new purchase price. Empty or 0 — no costs.", "Yeni alış fiyatını görmek için masraf tutarını girin. Boş veya 0 — masraf yok.", "Yangi xarid narxini ko'rish uchun xarajat summasini kiriting. Bo'sh yoki 0 — xarajat yo'q.");
        _apply.IsEnabled = string.IsNullOrWhiteSpace(_amount.Text) || CheckoutValidation.TryParseAmount(_amount.Text, out _) == CheckoutValidation.AmountParse.Ok;
        _apply.Content = hasAmount
            ? T("Применить", "Колдонуу", "Apply", "Uygula", "Qo'llash")
            : T("Без расходов", "Чыгымсыз", "No costs", "Masrafsız", "Xarajatsiz");
        Avalonia.Automation.AutomationProperties.SetName(_apply, _apply.Content as string); // 2026-11-05 (Н-1): имя кнопки для экранного диктора
    }

    private Control Row(string name, string purchase, string extra, bool soft)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,260,130"), MinHeight = soft ? 0 : 30, Margin = new Thickness(0, 2) };
        void Cell(int col, string text, bool trim)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = soft ? 12 : 13, FontWeight = soft ? FontWeight.SemiBold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            };
            tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable(soft ? "BrushTextSoft" : "BrushText"));
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }
        Cell(0, name, true);
        Cell(1, purchase, false);
        Cell(2, extra, false);
        return grid;
    }
}
