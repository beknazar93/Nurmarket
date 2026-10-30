using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-24, ТЗ «Склад», пакет Р2 (п. 2.1.10): окно «Завершить ревизию» — итог ДО отправки: сколько посчитано, сколько с расхождением,
/// суммы по цене продажи и закупки, продажи за время ревизии (поправка ТЗ 2.3, флажок), предупреждения и список расхождений (виртуализированный;
/// колонка «В акт» — что реально уйдёт в акт). «Применить» — <see cref="RevisionSession.ApplyAsync"/> частями по 500 строк с прогрессом «часть N из M»;
/// при обрыве окно остаётся, повторное нажатие продолжает с места остановки (акты не создаются второй раз — это делает движок).
/// Не посчитанные позиции в акт не входят и остатки не меняют. Сам окно ничего не считает и не пишет — только показывает результат
/// <see cref="RevisionSession.Preview"/> и запускает движок. Результат — <see cref="Result"/>.</summary>
internal sealed class RevisionFinishDialog : Window
{
    private readonly RevisionSession _s;
    private readonly IInventoryApiService _api;
    private readonly Func<CancellationToken, Task<IReadOnlyList<CatalogProductTileVm>>> _refresh;
    private readonly Action<Window, Control, IReadOnlyDictionary<string, double>?, bool, Action<string?>, Action> _export;
    private readonly CancellationTokenSource _cts = new();
    private readonly ModalShell _shell;
    private readonly StackPanel _summary = new() { Spacing = 3 };
    private readonly CheckBox _correction;
    private readonly TextBlock _sold = new();
    private readonly TextBlock _warnings = new();
    private readonly TextBlock _progress = new();
    private readonly TextBlock _error = new();
    private readonly ListBox _list;
    private readonly Button _apply;
    private readonly Button _back;
    private readonly Button _exportButton;
    private readonly Button _abandon;
    private RevisionPreview _preview = null!;
    private IReadOnlyDictionary<string, double> _sendMap = new Dictionary<string, double>();
    private bool _rebuilding;
    private readonly RadioButton _segAll;
    private readonly RadioButton _segTop;
    private readonly Border _segBox;
    private bool _topOnly;

    public RevisionApplyResult? Result { get; private set; }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static TextBlock Text(string text, double size, bool soft = false, bool bold = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (bold)
            t.FontWeight = FontWeight.SemiBold;
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(soft ? "BrushTextSoft" : "BrushText"));
        return t;
    }

    public RevisionFinishDialog(
        RevisionSession session,
        IInventoryApiService api,
        Func<CancellationToken, Task<IReadOnlyList<CatalogProductTileVm>>> refresh,
        Action<Window, Control, IReadOnlyDictionary<string, double>?, bool, Action<string?>, Action> export)
    {
        _s = session;
        _api = api;
        _refresh = refresh;
        _export = export;
        Title = T("Завершить ревизию", "Ревизияны аякташ", "Finish the stocktake", "Sayımı bitir", "Inventarizatsiyani yakunlash");
        Width = 912;
        Height = RevisionRowView.ShowMoney ? 720 : 660; // 2026-10-29: с деньгами сводка выше — список топ-10 виден почти целиком
        MinWidth = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));
        // Пока идёт отправка, окно не закрывается (Esc, ✕, Alt+F4): владелец должен увидеть, чем всё кончилось.
        Closing += (_, e) =>
        {
            if (_s.IsApplying)
                e.Cancel = true;
        };
        Closed += (_, _) => _cts.Cancel();

        // Сводка + флажок поправки.
        _correction = new CheckBox
        {
            Content = T("Учесть продажи за время ревизии (рекомендуем)", "Ревизия учурундагы сатууларды эске алуу (сунушталат)",
                "Take sales made during the stocktake into account (recommended)", "Sayım sırasındaki satışları hesaba kat (önerilir)", "Inventarizatsiya vaqtidagi sotuvlarni hisobga olish (tavsiya etiladi)"),
            IsChecked = _s.SalesCorrection,
            IsEnabled = !_s.IsPostingStarted,
        };
        _correction.IsCheckedChanged += (_, _) =>
        {
            if (_rebuilding)
                return;
            _s.SalesCorrection = _correction.IsChecked == true;
            Rebuild();
        };
        ToolTip.SetTip(_correction, T("Пока шёл пересчёт, товар продавался. В акт уходит факт минус то, что продали после ввода факта, — иначе проданное «вернулось» бы на склад.",
            "Саноо учурунда товар сатылып турду. Актыга факт минус факт киргизилгенден кийин сатылганы жазылат — антпесе сатылганы кампага кайра кошулуп калмак.",
            "Goods were being sold during the count. The act gets the counted number minus what was sold after it was entered — otherwise sold items would “return” to stock.",
            "Sayım sırasında ürün satıldı. Tutanağa sayılan miktar eksi sayımdan sonra satılanlar yazılır — yoksa satılanlar depoya geri dönerdi.",
            "Sanash vaqtida mahsulot sotilib turdi. Aktga haqiqiy miqdor minus kiritilgandan keyin sotilgani yoziladi — aks holda sotilgani omborga qaytib qolardi."));
        _sold.FontSize = 12.5;
        _sold.TextWrapping = TextWrapping.Wrap;
        _sold.Bind(TextBlock.ForegroundProperty, _sold.GetResourceObservable("BrushAccent"));
        _warnings.FontSize = 12.5;
        _warnings.TextWrapping = TextWrapping.Wrap;
        _warnings.Bind(TextBlock.ForegroundProperty, _warnings.GetResourceObservable("BrushDanger"));

        // 2026-10-29: переключатель списка «Все расхождения / Топ-10 недостач» (только там, где видна закупка). По умолчанию —
        // топ-10, если недостача есть: перед проведением владелец сразу видит, на какие товары ушло больше всего денег.
        _segAll = new RadioButton { GroupName = "RevFinishView", Focusable = false };
        _segTop = new RadioButton { GroupName = "RevFinishView", Focusable = false };
        _segAll.Classes.Add("sec-seg");
        _segTop.Classes.Add("sec-seg");
        _topOnly = RevisionRowView.ShowMoney && _s.GetTotals().ShortageQty > 0;
        _segAll.IsChecked = !_topOnly;
        _segTop.IsChecked = _topOnly;
        _segAll.IsCheckedChanged += (_, _) => OnViewToggle();
        _segTop.IsCheckedChanged += (_, _) => OnViewToggle();
        var segPanel = new StackPanel { Orientation = Orientation.Horizontal, Children = { _segAll, _segTop } };
        _segBox = new Border { Classes = { "sec-segment" }, Child = segPanel, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = RevisionRowView.ShowMoney, Margin = new Thickness(0, 4, 0, 0) };

        var top = new StackPanel { Spacing = 6 };
        top.Children.Add(_summary);
        top.Children.Add(_correction);
        top.Children.Add(_sold);
        top.Children.Add(new ScrollViewer { Content = _warnings, MaxHeight = 64, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        top.Children.Add(_segBox);

        // Список расхождений.
        var header = RevisionRowView.BuildHeader(true);
        header.Margin = new Thickness(0, 8, 0, 0);
        _list = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<RevisionRow>((row, _) => row is null ? new Control() : new RevisionRowView(row, _sendMap)),
            MinHeight = 90,
        };
        _list.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(PaddingProperty, new Thickness(0)),
                new Avalonia.Styling.Setter(MinHeightProperty, 0.0),
            },
        });

        _error.FontSize = 13;
        _error.FontWeight = FontWeight.SemiBold;
        _error.TextWrapping = TextWrapping.Wrap;
        _error.IsVisible = false;
        _error.Bind(TextBlock.ForegroundProperty, _error.GetResourceObservable("BrushDanger"));

        _abandon = new Button { Content = T("Отменить отправку", "Жөнөтүүнү жокко чыгаруу", "Cancel sending", "Göndermeyi iptal et", "Yuborishni bekor qilish"), MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Left };
        _abandon.Classes.Add("SecondaryButton");
        _abandon.Click += (_, _) =>
        {
            if (_s.AbandonPosting())
            {
                _correction.IsEnabled = true;
                Rebuild();
            }
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        Grid.SetRow(top, 0);
        Grid.SetRow(header, 1);
        Grid.SetRow(_list, 3);
        var listTitle = Text(T("Расхождения и позиции с поправкой", "Айырмалар жана оңдоолуу позициялар", "Differences and adjusted items", "Farklar ve düzeltilen kalemler", "Farqlar va tuzatilgan pozitsiyalar"), 12.5, soft: true, bold: true);
        listTitle.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(listTitle, 1);
        header.Margin = new Thickness(0, 24, 0, 0);
        if (RevisionRowView.ShowMoney)
        {
            listTitle.IsVisible = false; // вместо заголовка — переключатель «Все расхождения / Топ-10 недостач»
            header.Margin = new Thickness(0, 8, 0, 0);
        }

        var abandonRow = new StackPanel { Spacing = 6 };
        abandonRow.Children.Add(_error);
        abandonRow.Children.Add(_abandon);
        Grid.SetRow(abandonRow, 4);
        grid.Children.Add(top);
        grid.Children.Add(listTitle);
        grid.Children.Add(header);
        grid.Children.Add(_list);
        grid.Children.Add(abandonRow);

        // Подвал: «Выгрузить ▾», «Назад», «Применить».
        _exportButton = new Button { Content = T("Выгрузить ▾", "Чыгаруу ▾", "Export ▾", "Dışa aktar ▾", "Yuklab olish ▾") };
        _exportButton.Classes.Add("modal-cancel");
        _exportButton.Click += (_, _) => _export(this, _exportButton, _sendMap, true,
            text => _progress.Text = text ?? "", () => _progress.Bind(TextBlock.ForegroundProperty, _progress.GetResourceObservable("BrushSuccess")));
        _back = new Button { Content = T("Назад", "Артка", "Back", "Geri", "Orqaga"), IsCancel = true };
        _back.Classes.Add("modal-cancel");
        _back.Click += (_, _) => Close(false);
        _apply = new Button { IsDefault = false };
        _apply.Classes.Add("modal-primary");
        _apply.Click += async (_, _) => await ApplyAsync().ConfigureAwait(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { _exportButton, _back, _apply } };

        _progress.FontSize = 12.5;
        _progress.TextWrapping = TextWrapping.Wrap;
        _progress.Bind(TextBlock.ForegroundProperty, _progress.GetResourceObservable("BrushTextSoft"));

        _shell = new ModalShell
        {
            Margin = new Thickness(16),
            Title = Title,
            Footer = buttons,
            Hint = _progress,
            ContentScroll = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = grid,
        };
        Content = _shell;
        Rebuild();
        Opened += (_, _) => this.ClampToScreenHeight();
    }

    /// <summary>Пересчитать итог и список (после смены флажка поправки, отказа от отправки или неудачной отправки).</summary>
    private void Rebuild()
    {
        _rebuilding = true;
        try
        {
            _correction.IsChecked = _s.SalesCorrection;
            _correction.IsEnabled = !_s.IsPostingStarted;
        }
        finally
        {
            _rebuilding = false;
        }

        _preview = _s.Preview();
        var send = new Dictionary<string, double>(_preview.Items.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var it in _preview.Items)
            send[it.Row.ProductId] = it.Send;
        _sendMap = send;
        var totals = _s.GetTotals();
        var som = T("сом", "сом", "som", "TL", "so'm");
        var pcs = T("шт", "дана", "pcs", "adet", "dona");
        // 2026-10-29: деньги по закупке видит тот, кому открыта закупка (владелец, администратор, автономный режим) — было только AppMode.IsOwner.
        var owner = RevisionRowView.ShowMoney;

        _summary.Children.Clear();
        _summary.Children.Add(Text(T($"Посчитано {ReportFormat.Integer(totals.Counted)} из {ReportFormat.Integer(totals.Total)} позиций. В акт уйдёт: {ReportFormat.Integer(_preview.Items.Count)}.",
            $"Саналды {ReportFormat.Integer(totals.Counted)} / {ReportFormat.Integer(totals.Total)} позиция. Актыга жөнөтүлөт: {ReportFormat.Integer(_preview.Items.Count)}.",
            $"Counted {ReportFormat.Integer(totals.Counted)} of {ReportFormat.Integer(totals.Total)} items. Going into the act: {ReportFormat.Integer(_preview.Items.Count)}.",
            $"{ReportFormat.Integer(totals.Counted)} / {ReportFormat.Integer(totals.Total)} kalem sayıldı. Tutanağa girecek: {ReportFormat.Integer(_preview.Items.Count)}.",
            $"{ReportFormat.Integer(totals.Counted)} / {ReportFormat.Integer(totals.Total)} pozitsiya sanaldi. Aktga kiradi: {ReportFormat.Integer(_preview.Items.Count)}."), 15, bold: true));
        var diffLine = T($"С расхождением: {ReportFormat.Integer(totals.WithDifference)}", $"Айырмасы бар: {ReportFormat.Integer(totals.WithDifference)}",
            $"With differences: {ReportFormat.Integer(totals.WithDifference)}", $"Farklı: {ReportFormat.Integer(totals.WithDifference)}", $"Farqli: {ReportFormat.Integer(totals.WithDifference)}");
        // 2026-10-29: с закупкой — «С расхождением» и отдельные строки Недостача / Излишек / Чистый результат / «Не хватает закупки» (как в подвале окна ревизии).
        var moneyLines = owner ? RevisionRowView.MoneyLines(totals) : new List<(string Text, string Brush, string? Tip)>();
        if (totals.WithDifference > 0 && !owner)
        {
            var shortCost = owner ? T($", по закупке −{ReportFormat.Money(totals.ShortageAtCost)}", $", сатып алуу боюнча −{ReportFormat.Money(totals.ShortageAtCost)}", $", at cost −{ReportFormat.Money(totals.ShortageAtCost)}", $", alış fiyatıyla −{ReportFormat.Money(totals.ShortageAtCost)}", $", xarid narxida −{ReportFormat.Money(totals.ShortageAtCost)}") : "";
            var surCost = owner ? T($", по закупке +{ReportFormat.Money(totals.SurplusAtCost)}", $", сатып алуу боюнча +{ReportFormat.Money(totals.SurplusAtCost)}", $", at cost +{ReportFormat.Money(totals.SurplusAtCost)}", $", alış fiyatıyla +{ReportFormat.Money(totals.SurplusAtCost)}", $", xarid narxida +{ReportFormat.Money(totals.SurplusAtCost)}") : "";
            diffLine += T($"  ·  недостача −{ReportFormat.Quantity(totals.ShortageQty)} {pcs} (−{ReportFormat.Money(totals.ShortageAtSale)} {som}{shortCost})", $"  ·  жетишсиздик −{ReportFormat.Quantity(totals.ShortageQty)} {pcs} (−{ReportFormat.Money(totals.ShortageAtSale)} {som}{shortCost})",
                $"  ·  shortage −{ReportFormat.Quantity(totals.ShortageQty)} {pcs} (−{ReportFormat.Money(totals.ShortageAtSale)} {som}{shortCost})", $"  ·  eksik −{ReportFormat.Quantity(totals.ShortageQty)} {pcs} (−{ReportFormat.Money(totals.ShortageAtSale)} {som}{shortCost})", $"  ·  kamomad −{ReportFormat.Quantity(totals.ShortageQty)} {pcs} (−{ReportFormat.Money(totals.ShortageAtSale)} {som}{shortCost})");
            diffLine += T($"  ·  излишек +{ReportFormat.Quantity(totals.SurplusQty)} {pcs} (+{ReportFormat.Money(totals.SurplusAtSale)} {som}{surCost})", $"  ·  ашыкча +{ReportFormat.Quantity(totals.SurplusQty)} {pcs} (+{ReportFormat.Money(totals.SurplusAtSale)} {som}{surCost})",
                $"  ·  surplus +{ReportFormat.Quantity(totals.SurplusQty)} {pcs} (+{ReportFormat.Money(totals.SurplusAtSale)} {som}{surCost})", $"  ·  fazla +{ReportFormat.Quantity(totals.SurplusQty)} {pcs} (+{ReportFormat.Money(totals.SurplusAtSale)} {som}{surCost})", $"  ·  ortiqcha +{ReportFormat.Quantity(totals.SurplusQty)} {pcs} (+{ReportFormat.Money(totals.SurplusAtSale)} {som}{surCost})");
        }

        if (moneyLines.Count == 0)
            _summary.Children.Add(Text(owner ? diffLine : diffLine + T("  (по цене продажи)", "  (сатуу баасы боюнча)", "  (at selling price)", "  (satış fiyatıyla)", "  (sotuv narxida)"), 13));
        else
        {
            // Строки денег переносятся по ширине: «Чистый результат» и «Не хватает закупки» встают в одну строку — окно ниже на низком экране.
            var moneyPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var (lineText, brush, tip) in moneyLines)
            {
                var tb = new TextBlock { Text = lineText, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 18, 0), TextWrapping = TextWrapping.NoWrap };
                tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable(brush));
                if (tip is not null)
                    ToolTip.SetTip(tb, tip);
                moneyPanel.Children.Add(tb);
            }

            _summary.Children.Add(moneyPanel);
        }

        _summary.Children.Add(Text(T($"Не посчитанные позиции ({ReportFormat.Integer(totals.NotCounted)}) не меняются.", $"Саналбаган позициялар ({ReportFormat.Integer(totals.NotCounted)}) өзгөрбөйт.",
            $"Items that were not counted ({ReportFormat.Integer(totals.NotCounted)}) stay as they are.", $"Sayılmayan kalemler ({ReportFormat.Integer(totals.NotCounted)}) değişmez.", $"Sanalmagan pozitsiyalar ({ReportFormat.Integer(totals.NotCounted)}) o'zgarmaydi."), 13, soft: true));
        if (totals.ReadOnly > 0)
            _summary.Children.Add(Text(T($"Товаров с размерами: {totals.ReadOnly} — пересчитываются по размерам в карточке, в акт не входят.", $"Өлчөмдүү товарлар: {totals.ReadOnly} — товар карточкасында өлчөмдөр боюнча саналат, актыга кирбейт.",
                $"Products with sizes: {totals.ReadOnly} — counted by size in the product card, not part of the act.", $"Bedenli ürünler: {totals.ReadOnly} — ürün kartında bedenlere göre sayılır, tutanağa girmez.", $"O'lchamli mahsulotlar: {totals.ReadOnly} — kartochkada o'lchamlar bo'yicha sanaladi, aktga kirmaydi."), 12.5, soft: true));
        if (_s.IsPostingStarted)
            _summary.Children.Add(Text(T("Часть актов уже создана на сервере: нажмите «Применить» — ревизия будет доведена до конца без повторов.", "Актылардын бир бөлүгү серверде түзүлгөн: «Колдонуу» басыңыз — ревизия кайталоосуз аякталат.",
                "Some documents are already created on the server: press “Apply” to finish without duplicates.", "Bazı tutanaklar sunucuda oluşturuldu: «Uygula»ya basın — tekrar olmadan tamamlanır.", "Aktlarning bir qismi serverda yaratilgan: «Qo'llash»ni bosing — takrorlarsiz yakunlanadi."), 12.5, bold: true));

        _sold.Text = _s.SalesCorrection && _preview.SoldDuringCount > 0
            ? T($"Продано во время ревизии: {_preview.SoldDuringCount} {Tr.Plural(_preview.SoldDuringCount, "товар", "товара", "товаров")}, поправка учтена.", $"Ревизия учурунда сатылган: {_preview.SoldDuringCount} товар, оңдоо эске алынды.",
                $"Sold during the stocktake: {_preview.SoldDuringCount} {Tr.PluralEn(_preview.SoldDuringCount, "product", "products")}, adjustment applied.", $"Sayım sırasında satılan: {_preview.SoldDuringCount} ürün, düzeltme uygulandı.", $"Inventarizatsiya vaqtida sotilgan: {_preview.SoldDuringCount} mahsulot, tuzatish hisobga olindi.")
            : "";
        _sold.IsVisible = _sold.Text.Length > 0;
        if (_preview.Warnings.Count > 0)
        {
            var lines = _preview.Warnings.Take(3).Select(w => "• " + w.Text).ToList();
            if (_preview.Warnings.Count > 3)
                lines.Add(T($"…и ещё {_preview.Warnings.Count - 3}", $"…жана дагы {_preview.Warnings.Count - 3}", $"…and {_preview.Warnings.Count - 3} more", $"…ve {_preview.Warnings.Count - 3} tane daha", $"…va yana {_preview.Warnings.Count - 3} ta"));
            _warnings.Text = string.Join("\n", lines);
            _warnings.IsVisible = true;
        }
        else
        {
            _warnings.Text = "";
            _warnings.IsVisible = false;
        }

        // Список: расхождения и позиции, у которых «В акт» не равно факту (поправка на продажи). Одним присвоением.
        FillList();

        var n = _preview.Items.Count;
        _apply.Content = T($"Применить {n} поз.", $"{n} позицияны колдонуу", $"Apply {n} items", $"{n} kalemi uygula", $"{n} ta pozitsiyani qo'llash");
        _apply.IsEnabled = n > 0 || _s.IsPostingStarted;
        _abandon.IsVisible = _s.IsPostingStarted && _s.CanAbandonPosting;
    }

    /// <summary>2026-10-29: список под сводкой — все расхождения или топ-10 самых дорогих недостач (по закупке; нет закупки — по цене продажи).</summary>
    private void FillList()
    {
        var all = _preview.Items
            .Where(i => i.Row.HasDifference || Math.Abs(i.Send - i.Fact) > 0.0004)
            .Select(i => i.Row)
            .ToList();
        var top = all
            .Where(r => r.HasDifference && r.Difference < 0)
            .Select(r => (Row: r, Loss: RevisionRowView.DiffMoney(r, r.Difference)?.Amount ?? 0))
            .OrderBy(x => x.Loss)
            .Take(10)
            .Select(x => x.Row)
            .ToList();
        if (_topOnly && top.Count == 0)
        {
            _topOnly = false; // недостач не осталось — показываем все расхождения
            _rebuilding = true;
            _segAll.IsChecked = true;
            _rebuilding = false;
        }

        var count = all.Count(r => r.HasDifference);
        _segAll.Content = T($"Все расхождения: {count}", $"Бардык айырмалар: {count}", $"All differences: {count}", $"Tüm farklar: {count}", $"Barcha farqlar: {count}");
        _segTop.Content = T($"Топ-{top.Count} недостач", $"Жетишсиздиктин топ-{top.Count}", $"Top {top.Count} shortages", $"En büyük {top.Count} eksik", $"Eng katta {top.Count} kamomad");
        _list.ItemsSource = _topOnly ? top : all;
    }

    private void OnViewToggle()
    {
        if (_rebuilding)
            return;
        var topOnly = _segTop.IsChecked == true;
        if (topOnly == _topOnly)
            return;
        _topOnly = topOnly;
        FillList();
    }

    private static string StageText(RevisionApplyProgress p) => p.Stage switch
    {
        "refresh" => T("Обновляю остатки с сервера…", "Калдыктарды серверден жаңыртып жатам…", "Refreshing stock from the server…", "Stoklar sunucudan güncelleniyor…", "Qoldiqlar serverdan yangilanmoqda…"),
        "create" => T($"Создаю акт: часть {p.Part} из {p.PartsTotal}…", $"Акт түзүп жатам: {p.Part}/{p.PartsTotal}-бөлүк…", $"Creating the document: part {p.Part} of {p.PartsTotal}…", $"Tutanak oluşturuluyor: {p.Part}/{p.PartsTotal}. bölüm…", $"Akt yaratilmoqda: {p.Part}/{p.PartsTotal}-qism…"),
        "check" => T($"Проверяю акт: часть {p.Part} из {p.PartsTotal}…", $"Актыны текшерип жатам: {p.Part}/{p.PartsTotal}-бөлүк…", $"Checking the document: part {p.Part} of {p.PartsTotal}…", $"Tutanak kontrol ediliyor: {p.Part}/{p.PartsTotal}. bölüm…", $"Akt tekshirilmoqda: {p.Part}/{p.PartsTotal}-qism…"),
        _ => T($"Провожу акт: часть {p.Part} из {p.PartsTotal}…", $"Актыны өткөрүп жатам: {p.Part}/{p.PartsTotal}-бөлүк…", $"Posting the document: part {p.Part} of {p.PartsTotal}…", $"Tutanak işleniyor: {p.Part}/{p.PartsTotal}. bölüm…", $"Akt o'tkazilmoqda: {p.Part}/{p.PartsTotal}-qism…"),
    };

    private async Task ApplyAsync()
    {
        // Повторное нажатие, пока идёт отправка, ничего не делает (оболочка и сама выключает кнопки).
        if (!_shell.TryBeginBusy())
            return;
        _error.IsVisible = false;
        _abandon.IsVisible = false;
        _progress.Bind(TextBlock.ForegroundProperty, _progress.GetResourceObservable("BrushTextSoft"));
        try
        {
            var progress = new Progress<RevisionApplyProgress>(p => _progress.Text = StageText(p));
            var result = await _s.ApplyAsync(_api, _s.SalesCorrection ? _refresh : null, progress, _cts.Token).ConfigureAwait(true);
            switch (result.Status)
            {
                case RevisionApplyStatus.Done:
                    Result = result;
                    Close(true);
                    return;
                case RevisionApplyStatus.NothingToSend:
                    ShowError(T("Нечего отправлять: ни одной посчитанной позиции.", "Жөнөтө турган эч нерсе жок: бир да саналган позиция жок.", "Nothing to send: no counted items.", "Gönderilecek bir şey yok: sayılan kalem yok.", "Yuboradigan narsa yo'q: sanalgan pozitsiya yo'q."));
                    break;
                default:
                    ShowError(result.ErrorMessage + "  " + T($"Выполнено частей: {result.PartsApplied} из {result.PartsTotal}. Нажмите «Повторить» — отправка продолжится с места остановки, дублей не будет.",
                        $"Аткарылган бөлүктөр: {result.PartsApplied}/{result.PartsTotal}. «Кайталоо» басыңыз — жөнөтүү токтогон жеринен улантылат, кайталоо болбойт.",
                        $"Parts done: {result.PartsApplied} of {result.PartsTotal}. Press “Retry” — sending continues from where it stopped, with no duplicates.",
                        $"Tamamlanan bölüm: {result.PartsApplied}/{result.PartsTotal}. «Tekrar dene»ye basın — gönderim kaldığı yerden devam eder, tekrar olmaz.",
                        $"Bajarilgan qismlar: {result.PartsApplied}/{result.PartsTotal}. «Qayta urinish»ni bosing — yuborish to'xtagan joydan davom etadi, takror bo'lmaydi."));
                    _apply.Content = T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish");
                    break;
            }

            Rebuild();
            if (result.Status == RevisionApplyStatus.Failed)
                _apply.Content = T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Ревизия: окно итога — отправка упала: {ex}", "WARNING");
            ShowError(ex.Message);
        }
        finally
        {
            _shell.EndBusy();
        }
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.IsVisible = true;
        _progress.Text = "";
    }
}
