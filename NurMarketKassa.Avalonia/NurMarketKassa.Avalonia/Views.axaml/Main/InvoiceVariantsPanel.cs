using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-20 («в один товар и доп. штрихкоды»: «Асу» и «Асу со вкусом клубники» — один товар): блок карточки накладной Нур Советника — результат группировки вариантов ДО записи.
/// Группа — одна строка «Асу» с вложенными вариантами (вкус, штрихкод, количество, закупка), итог «товаров: N, вариантов/доп. кодов: M», кнопки «Разъединить», «Объединить с …»,
/// правка названия варианта. Позиции с той же маркой, но другой ценой не объединяются сами — под ними «Объединить? цены разные». Ничего не пишется: панель только меняет план
/// (<see cref="InvoiceVariantPlan"/>); смену состава группы (<c>changed</c>) карточка встречает пересборкой, правку названия варианта — нет.
/// Раскладка — Grid с колонками * / фиксированными и MinWidth, подпись колонок отдельной строкой над полями (чек-лист новых окон 20.11.2026).</summary>
public sealed class InvoiceVariantsPanel : Border
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly InvoiceVariantPlan _plan;
    private readonly Action _changed;
    private readonly Action? _before;
    private readonly TextBlock _summary;

    /// <param name="before">Вызывается ДО каждой правки состава групп: карточка успевает перенести введённые владельцем штрихкоды в план (номера позиций ещё прежние).</param>
    public InvoiceVariantsPanel(InvoiceVariantPlan plan, Action changed, Action? before = null)
    {
        _plan = plan;
        _changed = changed;
        _before = before;
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(10, 8);
        BorderThickness = new Thickness(1);
        Margin = new Thickness(0, 6, 0, 2);
        Bind(BorderBrushProperty, this.GetResourceObservable("BrushAccentStrong"));
        HorizontalAlignment = HorizontalAlignment.Stretch;

        var body = new StackPanel { Spacing = 8 };
        var head = new Grid { ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(GridLength.Auto) } };
        var title = Text(T("Один товар — несколько вкусов (вариантов)", "Бир товар — бир нече даам (вариант)", "One product — several flavors (variants)", "Tek ürün — birkaç tat (varyant)", "Bitta mahsulot — bir nechta ta'm (variant)"), 14, true, "BrushText");
        title.VerticalAlignment = VerticalAlignment.Center;
        _summary = Text("", 13, true, "BrushAccentStrong");
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _summary.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(_summary, 1);
        head.Children.Add(title);
        head.Children.Add(_summary);
        body.Children.Add(head);
        body.Children.Add(Text(T("Проверьте группы: ничего не записывается, пока вы не нажмёте «Провести закупку…». Основной штрихкод — у первого варианта, остальные станут дополнительными штрихкодами с названием вкуса.",
            "Топторду текшериңиз: «Сатып алууну өткөрүү…» баскычын басмайынча эч нерсе жазылбайт. Негизги штрихкод — биринчи вариантта, калгандары даамдын аты менен кошумча штрихкод болот.",
            "Check the groups: nothing is saved until you press “Post purchase…”. The main barcode is the first variant's, the others become extra barcodes named after the flavor.",
            "Grupları kontrol edin: «Alımı işle…»ye basmadan hiçbir şey kaydedilmez. Ana barkod ilk varyantınki, diğerleri tat adıyla ek barkod olur.",
            "Guruhlarni tekshiring: «Xaridni o'tkazish…» bosilmaguncha hech narsa yozilmaydi. Asosiy shtrix-kod birinchi variantniki, qolganlari ta'm nomi bilan qo'shimcha shtrix-kod bo'ladi."), 12.5, false, "BrushTextSoft"));
        foreach (var e in _plan.Entries.Where(e => e.IsGroup))
            body.Children.Add(BuildGroup(e));
        foreach (var e in _plan.Entries.Where(e => !e.IsGroup && !e.IsPassthrough))
            if (_plan.PriceConflictFor(e) is { } conflict)
                body.Children.Add(BuildConflict(e, conflict.Other, conflict.ThisPrice, conflict.OtherPrice));
        if (_plan.SplitMates() is { Count: > 0 } mates)
            body.Children.Add(BuildSplitNote(mates));
        Child = body;
        RefreshSummary();
    }

    private void RefreshSummary() =>
        _summary.Text = T($"товаров: {_plan.ProductCount}, вариантов/доп. кодов: {_plan.ExtraCodeCount}", $"товарлар: {_plan.ProductCount}, варианттар/кошумча коддор: {_plan.ExtraCodeCount}",
            $"products: {_plan.ProductCount}, variants/extra codes: {_plan.ExtraCodeCount}", $"ürün: {_plan.ProductCount}, varyant/ek kod: {_plan.ExtraCodeCount}",
            $"mahsulotlar: {_plan.ProductCount}, variantlar/qo'shimcha kodlar: {_plan.ExtraCodeCount}");

    private TextBlock Text(string text, double size, bool bold, string brush)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(brush));
        return t;
    }

    private Button Small(string text, string tip)
    {
        var b = UiKit.Ghost(this, text);
        b.Height = 36;
        b.FontSize = 12.5;
        b.Padding = new Thickness(12, 0);
        b.MinWidth = 44;
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.CurrentCulture);

    private Control BuildGroup(InvoiceVariantPlan.Entry e)
    {
        var host = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8) };
        host.Bind(Border.BorderBrushProperty, host.GetResourceObservable("BrushBorder"));
        var stack = new StackPanel { Spacing = 6 };

        // Заголовок группы: название и пояснение слева, кнопки справа.
        var head = new Grid { ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(GridLength.Auto) } };
        var names = new StackPanel { Spacing = 2 };
        names.Children.Add(Text(e.Base, 14.5, true, "BrushText"));
        var sub = new List<string>();
        if (e.Brand.Length > 0)
            sub.Add(e.Brand);
        sub.Add(e.Existing is null
            ? T("новый товар: основной штрихкод — у первого варианта, остальные — дополнительные", "жаңы товар: негизги штрихкод — биринчи вариантта, калгандары — кошумча", "new product: the main barcode is the first variant's, the others are extra",
                "yeni ürün: ana barkod ilk varyantınki, diğerleri ek", "yangi mahsulot: asosiy shtrix-kod birinchi variantniki, qolganlari qo'shimcha")
            : T($"товар «{e.Existing.Title}» уже есть на складе — вкусы добавятся ему дополнительными штрихкодами", $"«{e.Existing.Title}» товары кампада бар — даамдар ага кошумча штрихкод болуп кошулат",
                $"“{e.Existing.Title}” is already in stock — flavors are added to it as extra barcodes", $"«{e.Existing.Title}» zaten stokta — tatlar ona ek barkod olarak eklenir",
                $"«{e.Existing.Title}» omborda bor — ta'mlar unga qo'shimcha shtrix-kod sifatida qo'shiladi"));
        sub.Add(T($"всего {Num(e.TotalQty)} {e.Unit}", $"бардыгы {Num(e.TotalQty)} {e.Unit}", $"total {Num(e.TotalQty)} {e.Unit}", $"toplam {Num(e.TotalQty)} {e.Unit}", $"jami {Num(e.TotalQty)} {e.Unit}"));
        names.Children.Add(Text(string.Join(" · ", sub), 12.5, false, "BrushTextSoft"));
        head.Children.Add(names);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
        var mergeWith = MergeButton(e);
        if (mergeWith is not null)
            buttons.Children.Add(mergeWith);
        var splitAll = Small(T("Разъединить", "Ажыратуу", "Split", "Ayır", "Ajratish"), T("Каждый вариант — отдельным товаром, как в накладной", "Ар бир вариант — накладнойдогудай өзүнчө товар", "Each variant becomes a separate product, as in the invoice",
            "Her varyant faturadaki gibi ayrı ürün olur", "Har bir variant yuk xatidagidek alohida mahsulot bo'ladi"));
        splitAll.Click += (_, _) =>
        {
            _before?.Invoke();
            _plan.SplitAll(e);
            _changed();
        };
        buttons.Children.Add(splitAll);
        Grid.SetColumn(buttons, 1);
        head.Children.Add(buttons);
        stack.Children.Add(head);

        // Таблица вариантов: одна сетка колонок для подписей и строк.
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 150 },
                new ColumnDefinition(new GridLength(150)),
                new ColumnDefinition(new GridLength(76)),
                new ColumnDefinition(new GridLength(86)),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        void Caption(string text, int col, HorizontalAlignment align = HorizontalAlignment.Left)
        {
            var c = Text(text, 12.5, false, "BrushTextSoft");
            c.Margin = new Thickness(0, 0, 0, 2);
            c.HorizontalAlignment = align;
            Grid.SetColumn(c, col);
            grid.Children.Add(c);
        }
        Caption(T("Вкус / вариант", "Даам / вариант", "Flavor / variant", "Tat / varyant", "Ta'm / variant"), 0);
        Caption(T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"), 1);
        Caption(T("Кол-во", "Саны", "Qty", "Miktar", "Miqdor"), 2, HorizontalAlignment.Right);
        Caption(T("Закупка", "Сатып алуу", "Cost", "Alış", "Xarid"), 3, HorizontalAlignment.Right);
        var row = 1;
        foreach (var m in e.Members.ToList())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var box = new TextBox
            {
                Text = m.Variant, Height = 36, Padding = new Thickness(10, 0), CornerRadius = new CornerRadius(8), FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center,
                Watermark = T("обычный (без вкуса)", "кадимки (даамсыз)", "plain (no flavor)", "normal (tatsız)", "oddiy (ta'msiz)"), MaxLength = 60,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            ToolTip.SetTip(box, T($"Название варианта (из накладной: «{m.InvoiceName}»)", $"Вариант аты (накладнойдо: «{m.InvoiceName}»)", $"Variant name (invoice: “{m.InvoiceName}”)", $"Varyant adı (fatura: «{m.InvoiceName}»)", $"Variant nomi (yuk xati: «{m.InvoiceName}»)"));
            Avalonia.Automation.AutomationProperties.SetName(box, T("Название варианта", "Вариант аты", "Variant name", "Varyant adı", "Variant nomi"));
            var member = m;
            void Commit() => _plan.RenameVariant(member, box.Text);
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, k) =>
            {
                if (k.Key == Avalonia.Input.Key.Enter)
                {
                    Commit();
                    k.Handled = true;
                }
            };
            Grid.SetRow(box, row);
            grid.Children.Add(box);

            var code = Text(m.Barcode.Length > 0 ? m.Barcode : "—", 13.5, false, m.Barcode.Length > 0 ? "BrushText" : "BrushWarning");
            code.VerticalAlignment = VerticalAlignment.Center;
            code.TextWrapping = TextWrapping.NoWrap;
            code.TextTrimming = TextTrimming.CharacterEllipsis;
            if (m.Barcode.Length == 0)
                ToolTip.SetTip(code, T("штрихкод не прочитан", "штрихкод окулган жок", "barcode not read", "barkod okunamadı", "shtrix-kod o'qilmadi"));
            Grid.SetRow(code, row);
            Grid.SetColumn(code, 1);
            grid.Children.Add(code);

            var qty = Text(Num(m.Qty), 13.5, false, "BrushText");
            qty.VerticalAlignment = VerticalAlignment.Center;
            qty.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(qty, row);
            Grid.SetColumn(qty, 2);
            grid.Children.Add(qty);

            var price = Text(m.Step.Purchase is { } p ? p.ToString("N2", CultureInfo.CurrentCulture) : "—", 13.5, false, "BrushText");
            price.VerticalAlignment = VerticalAlignment.Center;
            price.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(price, row);
            Grid.SetColumn(price, 3);
            grid.Children.Add(price);

            var alone = Small(T("Отдельно", "Өзүнчө", "Separate", "Ayrı", "Alohida"), T("Сделать этот вариант отдельным товаром", "Бул вариантты өзүнчө товар кылуу", "Make this variant a separate product", "Bu varyantı ayrı ürün yap", "Bu variantni alohida mahsulot qilish"));
            alone.Click += (_, _) =>
            {
                Commit();
                _before?.Invoke();
                _plan.Split(e, member);
                _changed();
            };
            Grid.SetRow(alone, row);
            Grid.SetColumn(alone, 4);
            grid.Children.Add(alone);
            row++;
        }
        // Avalonia 11.2: у Grid нет ColumnSpacing/RowSpacing — промежутки 8 px между колонками и 4 px между строками задаются полями ячеек.
        foreach (var cell in grid.Children.OfType<Control>())
            cell.Margin = new Thickness(cell.Margin.Left, cell.Margin.Top, Grid.GetColumn(cell) == 4 ? 0 : 8, cell.Margin.Bottom + 4);
        stack.Children.Add(grid);
        foreach (var note in e.Notes)
            stack.Children.Add(Text("⚠ " + note, 12.5, false, "BrushWarning"));
        host.Child = stack;
        return host;
    }

    /// <summary>«Объединить? цены разные» — позиция того же товара (название + марка), но с другой ценой.</summary>
    private Control BuildConflict(InvoiceVariantPlan.Entry e, InvoiceVariantPlan.Entry other, double? thisPrice, double? otherPrice)
    {
        string P(double? v) => v is { } x ? x.ToString("0.##", CultureInfo.CurrentCulture) : "—";
        var host = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8) };
        host.Bind(Border.BorderBrushProperty, host.GetResourceObservable("BrushWarning"));
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(GridLength.Auto) } };
        var m = e.Members[0];
        var label = m.Variant.Length > 0 ? m.Variant : m.InvoiceName;
        var text = Text(T($"«{m.InvoiceName}» — закупка {P(thisPrice)}, а у «{other.Title}» {P(otherPrice)}: цены разные, сами не объединяем. Объединить?",
            $"«{m.InvoiceName}» — сатып алуу {P(thisPrice)}, ал «{other.Title}» {P(otherPrice)}: баалар ар башка, өзүбүз бириктирбейбиз. Бириктиребизби?",
            $"“{m.InvoiceName}” — cost {P(thisPrice)}, while “{other.Title}” is {P(otherPrice)}: prices differ, we do not merge on our own. Merge?",
            $"«{m.InvoiceName}» — alış {P(thisPrice)}, «{other.Title}» ise {P(otherPrice)}: fiyatlar farklı, kendiliğimizden birleştirmiyoruz. Birleştirilsin mi?",
            $"«{m.InvoiceName}» — xarid {P(thisPrice)}, «{other.Title}» esa {P(otherPrice)}: narxlar har xil, o'zimiz birlashtirmaymiz. Birlashtiraymi?"), 13, false, "BrushText");
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        var merge = Small(T("Объединить", "Бириктирүү", "Merge", "Birleştir", "Birlashtirish"), T($"Добавить «{label}» вариантом к «{other.Title}»", $"«{label}» вариантын «{other.Title}» товарына кошуу", $"Add “{label}” as a variant of “{other.Title}”",
            $"«{label}» varyantını «{other.Title}» ürününe ekle", $"«{label}» variantini «{other.Title}» mahsulotiga qo'shish"));
        merge.Margin = new Thickness(8, 0, 0, 0);
        merge.Click += (_, _) =>
        {
            _before?.Invoke();
            if (_plan.Merge(e, other))
                _changed();
        };
        Grid.SetColumn(merge, 1);
        row.Children.Add(merge);
        host.Child = row;
        return host;
    }

    /// <summary>«Объединить снова» — позиции, разъединённые владельцем, но с той же маркой, единицей и ценой.</summary>
    private Control BuildSplitNote(List<InvoiceVariantPlan.Entry> mates)
    {
        var host = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8) };
        host.Bind(Border.BorderBrushProperty, host.GetResourceObservable("BrushBorder"));
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(GridLength.Auto) } };
        var names = string.Join(", ", mates.Take(3).Select(e => "«" + e.Members[0].InvoiceName + "»")) + (mates.Count > 3 ? " …" : "");
        var text = Text(T($"Разъединено: {names} — та же марка и цена, отдельные товары. Объединить снова?", $"Ажыратылды: {names} — бир эле бренд жана баа, өзүнчө товарлар. Кайра бириктиребизби?",
            $"Split: {names} — same brand and price, separate products. Merge again?", $"Ayrıldı: {names} — aynı marka ve fiyat, ayrı ürünler. Yeniden birleştirilsin mi?",
            $"Ajratildi: {names} — bir xil brend va narx, alohida mahsulotlar. Yana birlashtiraymi?"), 13, false, "BrushTextSoft");
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        var again = Small(T("Объединить снова", "Кайра бириктирүү", "Merge again", "Yeniden birleştir", "Yana birlashtirish"), T("Вернуть вкусы в один товар", "Даамдарды кайра бир товарга кайтаруу", "Put the flavors back into one product", "Tatları tekrar tek ürüne al", "Ta'mlarni yana bitta mahsulotga qaytarish"));
        again.Margin = new Thickness(8, 0, 0, 0);
        again.Click += (_, _) =>
        {
            _before?.Invoke();
            _plan.MergeSplit();
            _changed();
        };
        Grid.SetColumn(again, 1);
        row.Children.Add(again);
        host.Child = row;
        return host;
    }

    /// <summary>«Объединить с …» — список других товарных позиций карточки той же единицы; выбор вливает выбранную позицию в эту группу.</summary>
    private Button? MergeButton(InvoiceVariantPlan.Entry target)
    {
        var options = _plan.MergeTargets(target);
        if (options.Count == 0)
            return null;
        var button = Small(T("Объединить с …", "Бириктирүү …", "Merge with …", "Şununla birleştir …", "Birlashtirish …"), T("Добавить другую позицию накладной вариантом в эту группу", "Накладнойдун башка позициясын бул топко вариант катары кошуу",
            "Add another invoice line to this group as a variant", "Faturanın başka bir satırını bu gruba varyant olarak ekle", "Yuk xatining boshqa qatorini shu guruhga variant sifatida qo'shish"));
        var flyout = new MenuFlyout();
        foreach (var other in options.Take(30))
        {
            var item = new MenuItem { Header = other.Title + " · " + Num(other.TotalQty) };
            var source = other;
            item.Click += (_, _) =>
            {
                _before?.Invoke();
                if (_plan.Merge(source, target))
                    _changed();
            };
            flyout.Items.Add(item);
        }
        button.Flyout = flyout;
        return button;
    }
}
