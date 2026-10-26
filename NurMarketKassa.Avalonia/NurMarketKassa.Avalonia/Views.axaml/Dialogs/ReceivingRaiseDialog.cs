using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-24, ТЗ «Склад» (Б0/Б1, 4.3): окно-превью «Обновить цены продажи» в Склад → Приёмка. Показывает по каждой строке с подорожанием:
/// товар · закупка было → стало · продажа было → станет · наценка; переключатель «сохранить прежнюю наценку %» / «поднять на столько же сом».
/// Считает и применяет <see cref="ReceivingPricing"/> (PreviewRaise / ApplyRaise) — окно только показывает. «Применить» меняет цены продажи
/// только в строках приёмки; на сервер они уходят при «Оприходовать». Округление — шаг ReceivingPricing.SalePriceRoundStep (вверх).</summary>
public sealed class ReceivingRaiseDialog : Window
{
    private readonly IReadOnlyList<ReceivingLineVm> _lines;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _empty = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly RadioButton _keepPercent;
    private readonly RadioButton _addAmount;
    private readonly Button _apply;
    private List<SalePriceUpdateRow> _preview = new();

    /// <summary>Сколько строк обновлено (0 — окно закрыли без применения).</summary>
    public int AppliedCount { get; private set; }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);
    private static string Money(double v) => v.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    public ReceivingRaiseDialog(IReadOnlyList<ReceivingLineVm> lines)
    {
        _lines = lines;
        Title = T("Обновить цены продажи", "Сатуу баасын жаңыртуу", "Update sale prices", "Satış fiyatlarını güncelle", "Sotuv narxlarini yangilash");
        SectionCards.AsModal(this, 912);

        _keepPercent = new RadioButton
        {
            GroupName = "RaiseMode", IsChecked = true,
            Content = T("Сохранить прежнюю наценку %", "Мурдагы кошумча %ти сактоо", "Keep the previous markup %", "Önceki kâr %'sini koru", "Avvalgi ustama %ni saqlash"),
        };
        _addAmount = new RadioButton
        {
            GroupName = "RaiseMode",
            Content = T("Поднять на столько же сом", "Ошончо сомго көтөрүү", "Raise by the same amount in som", "Aynı tutarda artır", "Shuncha so'mga ko'tarish"),
        };
        _keepPercent.IsCheckedChanged += (_, _) => Rebuild();
        _addAmount.IsCheckedChanged += (_, _) => Rebuild();
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Children = { _keepPercent, _addAmount } };

        _empty.Text = T("Нет строк, где цену продажи можно поднять: закупка не выросла или цену продажи уже меняли.",
            "Сатуу баасын көтөрө турган саптар жок: сатып алуу баасы өспөгөн же сатуу баасы өзгөртүлгөн.",
            "No lines whose sale price can be raised: the purchase price did not grow or the sale price was already changed.",
            "Satış fiyatı artırılabilecek satır yok: alış fiyatı artmamış veya satış fiyatı zaten değiştirilmiş.",
            "Sotuv narxini ko'tarish mumkin bo'lgan qator yo'q: xarid narxi oshmagan yoki sotuv narxi allaqachon o'zgartirilgan.");
        _empty.Bind(TextBlock.ForegroundProperty, _empty.GetResourceObservable("BrushTextSoft"));

        var header = Row(T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("Закупка", "Сатып алуу", "Purchase", "Alış", "Xarid"),
            T("Продажа", "Сатуу", "Sale", "Satış", "Sotuv"), T("Наценка", "Кошумча", "Markup", "Kâr", "Ustama"), soft: true);
        var scroll = new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = _rows };
        var root = new StackPanel { Spacing = 10, Children = { modes, header, scroll, _empty } };

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close();
        _apply = SectionCards.Primary("", isDefault: true);
        _apply.Click += (_, _) =>
        {
            AppliedCount = ReceivingPricing.ApplyRaise(_preview);
            Close();
        };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _apply } };

        Content = SectionCards.Shell(Title,
            T("Закупка выросла, а цена продажи прежняя. Новые цены встанут в строки приёмки; на сервер уйдут при «Оприходовать».",
                "Сатып алуу баасы өстү, ал эми сатуу баасы мурдагыдай. Жаңы баалар кабыл алуу саптарына коюлат; серверге «Кирешеге алуу» басылганда кетет.",
                "The purchase price grew but the sale price is the old one. New prices go into the receiving lines; they reach the server on “Receive”.",
                "Alış fiyatı arttı ama satış fiyatı eski. Yeni fiyatlar mal kabul satırlarına yazılır; sunucuya «Stoğa al» ile gider.",
                "Xarid narxi oshdi, sotuv narxi esa eskicha. Yangi narxlar qabul qatorlariga qo'yiladi; serverga «Kirim qilish»da ketadi."),
            root, footer);
        Rebuild();
    }

    private SalePriceRaiseMode Mode => _addAmount.IsChecked == true ? SalePriceRaiseMode.AddSameAmount : SalePriceRaiseMode.KeepMarkupPercent;

    private void Rebuild()
    {
        _preview = ReceivingPricing.PreviewRaise(_lines, Mode);
        _rows.Children.Clear();
        foreach (var r in _preview)
            _rows.Children.Add(Row(string.IsNullOrWhiteSpace(r.Line.ProductName) ? r.Line.Barcode : r.Line.ProductName,
                $"{Money(r.OldPurchase)} → {Money(r.NewPurchase)}", $"{Money(r.OldSale)} → {Money(r.NewSale)}",
                $"{(r.OldMarkup is { } o ? Money(o) : "—")} → {(r.NewMarkup is { } n ? Money(n) : "—")} %", soft: false));
        _empty.IsVisible = _preview.Count == 0;
        _apply.IsEnabled = _preview.Count > 0;
        _apply.Content = T($"Применить к {_preview.Count} строкам", $"{_preview.Count} сапка колдонуу", $"Apply to {_preview.Count} lines",
            $"{_preview.Count} satıra uygula", $"{_preview.Count} qatorga qo'llash");
    }

    private Control Row(string name, string purchase, string sale, string markup, bool soft)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,150,150,130"), MinHeight = soft ? 0 : 30, Margin = new Thickness(0, 2) };
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
        Cell(2, sale, false);
        Cell(3, markup, false);
        return grid;
    }
}
