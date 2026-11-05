using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-05 (функция 9): окно «Пересчёт дня» — показывает, какие 20–30 позиций предлагает касса (и почему), даёт изменить их число и (владельцу/администратору)
/// порог расхождения для PIN. «Начать» — в ревизии остаётся только этот список. Выбор считает <see cref="CycleCountPlanner"/>; окно ничего не пишет, кроме настроек.</summary>
public sealed class CycleCountDialog : Window
{
    private readonly IReadOnlyList<CycleCandidate> _candidates;
    private readonly TextBox _count;
    private readonly TextBox _threshold;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _note = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly Button _start;
    private List<CycleCountPick> _picks = new();

    /// <summary>Выбранные позиции (после «Начать»); null — окно закрыли без выбора.</summary>
    public IReadOnlyList<CycleCountPick>? Result { get; private set; }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public CycleCountDialog(IReadOnlyList<CycleCandidate> candidates, bool canEditSettings)
    {
        _candidates = candidates;
        var data = CycleCountStore.Current;
        Title = T("Пересчёт дня", "Күндүн кайра саноосу", "Daily count", "Günün sayımı", "Kunlik qayta sanash");
        SectionCards.AsModal(this, 912);

        _count = new TextBox { Width = 90, Height = 40, Text = data.DayCount.ToString() };
        _count.TextChanged += (_, _) => Rebuild();
        _threshold = new TextBox { Width = 130, Height = 40, IsEnabled = canEditSettings, Text = data.PinThreshold.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) };

        TextBlock Label(string text)
        {
            var tb = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable("BrushText"));
            return tb;
        }
        var settings = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children =
            {
                Label(T("Позиций в день", "Күнүнө позиция", "Items per day", "Günlük kalem", "Kuniga pozitsiya")), _count,
                new Border { Width = 18 },
                Label(T("Порог для PIN, сом (0 — выключено)", "PIN үчүн чек, сом (0 — өчүк)", "PIN threshold, som (0 — off)", "PIN eşiği, som (0 — kapalı)", "PIN uchun chegara, so'm (0 — o'chirilgan)")), _threshold,
            },
        };
        _note.Bind(TextBlock.ForegroundProperty, _note.GetResourceObservable("BrushTextSoft"));
        var header = Row(T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq"), T("Почему в списке", "Эмне үчүн тизмеде", "Why it is listed", "Neden listede", "Nega ro'yxatda"), soft: true);
        var scroll = new ScrollViewer { MaxHeight = 330, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = _rows };
        var root = new StackPanel { Spacing = 10, Children = { settings, header, scroll, _note } };

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close();
        _start = SectionCards.Primary("", isDefault: true);
        _start.Click += (_, _) =>
        {
            SaveSettings();
            Result = _picks;
            Close();
        };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _start } };

        Content = SectionCards.Shell(Title,
            T("Каждый день — 20–30 позиций вместо полной ревизии: сначала отрицательные остатки, частые удаления из чека и давно не считанные, затем по классам ABC.",
                "Күн сайын толук ревизиянын ордуна 20–30 позиция: адегенде терс калдыктар, чектен тез-тез өчүрүлгөндөр жана көптөн бери саналбагандар, андан кийин ABC классы боюнча.",
                "Every day 20–30 items instead of a full stocktake: first negative stock, frequent removals from receipts and long-uncounted items, then by ABC class.",
                "Her gün tam sayım yerine 20–30 kalem: önce eksi stok, fişten sık silinenler ve uzun süredir sayılmayanlar, sonra ABC sınıfına göre.",
                "Har kuni to'liq inventarizatsiya o'rniga 20–30 pozitsiya: avval manfiy qoldiq, chekdan tez-tez o'chirilganlar va anchadan beri sanalmaganlar, keyin ABC sinfi bo'yicha."),
            root, footer);
        Rebuild();
    }

    private int Count => int.TryParse(_count.Text?.Trim(), out var n) ? Math.Clamp(n, 5, 100) : CycleCountStore.Current.DayCount;

    private void SaveSettings()
    {
        var n = Count;
        var thresholdOk = CheckoutValidation.TryParseAmount(_threshold.Text, out var thr) == CheckoutValidation.AmountParse.Ok;
        CycleCountStore.Update(d =>
        {
            d.DayCount = n;
            if (_threshold.IsEnabled && thresholdOk)
                d.PinThreshold = thr;
        });
    }

    private void Rebuild()
    {
        _picks = CycleCountPlanner.Pick(_candidates, Count, DateTime.Today);
        _rows.Children.Clear();
        foreach (var p in _picks)
            _rows.Children.Add(Row(p.Item.Name, ReportFormat.Quantity(p.Item.Stock), CycleCountPlanner.ReasonText(p), soft: false));
        _note.Text = _picks.Count == 0
            ? T("Подходящих позиций нет: всё недавно считали или склад пуст.", "Ылайыктуу позиция жок: баары жакында саналган же кампа бош.", "No suitable items: everything was counted recently or the warehouse is empty.", "Uygun kalem yok: hepsi yakında sayıldı veya depo boş.", "Mos pozitsiya yo'q: hammasi yaqinda sanalgan yoki ombor bo'sh.")
            : T($"Выбрано {_picks.Count}. Остальные позиции в этой ревизии не меняются.", $"{_picks.Count} тандалды. Калган позициялар бул ревизияда өзгөрбөйт.", $"{_picks.Count} selected. The other items are not changed in this stocktake.",
                $"{_picks.Count} seçildi. Diğer kalemler bu sayımda değişmez.", $"{_picks.Count} ta tanlandi. Qolgan pozitsiyalar bu inventarizatsiyada o'zgarmaydi.");
        _start.IsEnabled = _picks.Count > 0;
        _start.Content = T("Начать пересчёт", "Кайра саноону баштоо", "Start the count", "Sayımı başlat", "Sanashni boshlash");
        Avalonia.Automation.AutomationProperties.SetName(_start, _start.Content as string); // 2026-11-05 (Н-1): у кнопки не было имени для экранного диктора
    }

    private Control Row(string name, string stock, string why, bool soft)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,90,360"), MinHeight = soft ? 0 : 28, Margin = new Thickness(0, 2) };
        void Cell(int col, string text, bool trim, bool right = false)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = soft ? 12 : 13, FontWeight = soft ? FontWeight.SemiBold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
                TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, Margin = new Thickness(0, 0, 10, 0),
            };
            tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable(soft ? "BrushTextSoft" : "BrushText"));
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }
        Cell(0, name, true);
        Cell(1, stock, false, right: true);
        Cell(2, why, true);
        return grid;
    }
}

/// <summary>2026-11-05 (функция 9): перед проведением ревизии — причина каждого расхождения из списка (порча, кража, пересорт, ошибка прихода, другое).
/// Выбранные причины уходят в примечание акта на сервере (RevisionSession.DifferenceReasons). Можно оставить «не указана».</summary>
public sealed class CycleCountReasonsDialog : Window
{
    private readonly List<(RevisionRow Row, ComboBox Box)> _items = new();

    /// <summary>Выбранные причины (productId → код); null — окно отменили (ревизию не завершаем).</summary>
    public Dictionary<string, string>? Result { get; private set; }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static ComboBox MakeBox(string? selected)
    {
        var box = new ComboBox { MinWidth = 190, MinHeight = 34 };
        box.Items.Add(new ComboBoxItem { Content = CycleCountReasons.Title(null), Tag = "" });
        foreach (var code in CycleCountReasons.Codes)
            box.Items.Add(new ComboBoxItem { Content = CycleCountReasons.Title(code), Tag = code });
        box.SelectedIndex = Math.Max(0, Array.IndexOf(CycleCountReasons.Codes, selected) + 1);
        return box;
    }

    private static string Tag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    public CycleCountReasonsDialog(IReadOnlyList<RevisionRow> diffRows, IReadOnlyDictionary<string, string> existing)
    {
        Title = T("Причины расхождений", "Айырмалардын себептери", "Reasons for differences", "Farkların nedenleri", "Farqlar sabablari");
        SectionCards.AsModal(this, 760);

        var all = MakeBox(null);
        all.SelectionChanged += (_, _) =>
        {
            var code = Tag(all);
            if (code.Length == 0)
                return;
            foreach (var (_, box) in _items)
                box.SelectedIndex = Array.IndexOf(CycleCountReasons.Codes, code) + 1;
        };
        var allLabel = new TextBlock { Text = T("Причина для всех", "Баарына себеп", "Reason for all", "Tümü için neden", "Hammasi uchun sabab"), FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        allLabel.Bind(TextBlock.ForegroundProperty, allLabel.GetResourceObservable("BrushText"));
        var allRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { allLabel, all } };

        var rows = new StackPanel { Spacing = 4 };
        foreach (var r in diffRows.Take(60))
        {
            var box = MakeBox(existing.TryGetValue(r.ProductId, out var c) ? c : null);
            _items.Add((r, box));
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,90,210"), Margin = new Thickness(0, 2) };
            var name = new TextBlock { Text = r.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable("BrushText"));
            var diff = new TextBlock
            {
                Text = (r.Difference > 0 ? "+" : "−") + ReportFormat.Quantity(Math.Abs(r.Difference)), FontSize = 13, FontWeight = FontWeight.SemiBold,
                TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            };
            diff.Bind(TextBlock.ForegroundProperty, diff.GetResourceObservable(r.Difference < 0 ? "BrushDanger" : "BrushSuccess"));
            Grid.SetColumn(diff, 1);
            Grid.SetColumn(box, 2);
            grid.Children.Add(name);
            grid.Children.Add(diff);
            grid.Children.Add(box);
            rows.Children.Add(grid);
        }
        var more = new TextBlock { FontSize = 12.5, IsVisible = diffRows.Count > 60, TextWrapping = TextWrapping.Wrap };
        more.Text = T($"Показаны первые 60 из {diffRows.Count} расхождений; у остальных причина не указывается.", $"{diffRows.Count} айырмадан алгачкы 60 көрсөтүлдү; калгандарына себеп көрсөтүлбөйт.",
            $"First 60 of {diffRows.Count} differences are shown; the rest get no reason.", $"{diffRows.Count} farktan ilk 60'ı gösteriliyor; kalanlara neden yazılmaz.", $"{diffRows.Count} ta farqdan dastlabki 60 tasi ko'rsatilgan; qolganlariga sabab yozilmaydi.");
        more.Bind(TextBlock.ForegroundProperty, more.GetResourceObservable("BrushTextSoft"));
        var scroll = new ScrollViewer { MaxHeight = 360, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = rows };
        var root = new StackPanel { Spacing = 10, Children = { allRow, scroll, more } };

        var cancel = SectionCards.Cancel(T("Назад", "Артка", "Back", "Geri", "Orqaga"));
        cancel.Click += (_, _) => Close();
        var next = SectionCards.Primary(T("Далее", "Андан ары", "Next", "İleri", "Keyingisi"), isDefault: true);
        next.Click += (_, _) =>
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (row, box) in _items)
                if (Tag(box) is { Length: > 0 } code)
                    map[row.ProductId] = code;
            Result = map;
            Close();
        };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, next } };
        Content = SectionCards.Shell(Title,
            T("Выберите причину у каждого расхождения — она запишется в примечание акта на сервере. Можно пропустить.",
                "Ар бир айырма үчүн себепти тандаңыз — ал серверде актынын эскертмесине жазылат. Өткөрүп жиберсеңиз болот.",
                "Pick a reason for each difference — it goes into the document note on the server. You can skip it.",
                "Her fark için bir neden seçin — sunucuda tutanak notuna yazılır. Geçebilirsiniz.",
                "Har bir farq uchun sabab tanlang — u serverdagi akt izohiga yoziladi. O'tkazib yuborish mumkin."),
            root, footer);
    }
}
