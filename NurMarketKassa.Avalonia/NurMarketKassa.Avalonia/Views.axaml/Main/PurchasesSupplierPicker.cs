using NurMarketKassa.Core.Application;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-22 (закупки как в вебе): выбор поставщика с поиском — как «Выберите поставщика 🔍 ▾» и «+» сайта. Кнопка во всю ширину
/// (44 px) с выбранным поставщиком; нажатие — список с поиском по названию и цифрам телефона; рядом «＋» — новый поставщик (SupplierEditDialog,
/// те же реквизиты, что в «Поставщиках»). <see cref="AllowAll"/> — первая строка «Все поставщики» (фильтр истории).</summary>
public sealed class PurchasesSupplierPicker : Grid
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly SupplierApiService _svc;
    private readonly Button _button;
    private readonly TextBlock _text = new() { FontSize = 14.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Left };
    private readonly Flyout _flyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private readonly TextBox _search;
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly Button _add;
    private List<SupplierInfo> _all = new();
    private SupplierInfo? _selected;
    private bool _loading;

    public bool AllowAll { get; init; }
    public event Action<SupplierInfo?>? Changed;
    public SupplierInfo? Selected => _selected;
    public IReadOnlyList<SupplierInfo> All => _all;

    public PurchasesSupplierPicker(SupplierApiService svc)
    {
        _svc = svc;
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        var chevron = new TextBlock { Text = "▾", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        Use(_text, TextBlock.ForegroundProperty, "BrushText");
        Use(chevron, TextBlock.ForegroundProperty, "BrushTextSoft");
        var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _text, chevron } };
        Grid.SetColumn(chevron, 1);
        _button = new Button
        {
            Content = inner, Height = 44, MinHeight = 44, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center,
        };
        _button.Classes.Add("sec-outline");
        Children.Add(_button);

        _add = new Button
        {
            Content = "＋", Width = 44, Height = 44, MinHeight = 44, Padding = new Thickness(0), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            FontSize = 18, Margin = new Thickness(8, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        _add.Classes.Add("sec-outline");
        var addName = T("Новый поставщик", "Жаңы жеткирүүчү", "New supplier", "Yeni tedarikçi", "Yangi yetkazib beruvchi");
        ToolTip.SetTip(_add, addName);
        Avalonia.Automation.AutomationProperties.SetName(_add, addName);
        _add.Click += async (_, _) => await CreateAsync().ConfigureAwait(true);
        Grid.SetColumn(_add, 1);
        Children.Add(_add);

        _search = new TextBox
        {
            Watermark = T("Поиск: название или телефон", "Издөө: аталышы же телефон", "Search: name or phone", "Ara: ad veya telefon", "Qidirish: nomi yoki telefon"),
            Height = 42, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(10), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 14.5,
        };
        Avalonia.Automation.AutomationProperties.SetName(_search, _search.Watermark);
        _search.TextChanged += (_, _) => RenderList();
        _search.KeyDown += (_, e) =>
        {
            // Enter — первый найденный.
            if (e.Key == Key.Enter && _list.Children.OfType<Button>().FirstOrDefault() is { } first)
            {
                e.Handled = true;
                Pick(first.Tag as SupplierInfo);
            }
        };
        var scroll = new ScrollViewer { Content = _list, MaxHeight = 320, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var box = new StackPanel { Spacing = 8, Width = 380, Children = { _search, scroll } };
        _flyout.Content = box;
        _button.Flyout = _flyout;
        _flyout.Opened += (_, _) =>
        {
            _search.Text = "";
            RenderList();
            _search.Focus();
        };
        Render();
        Avalonia.Automation.AutomationProperties.SetName(_button, T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"));
    }

    /// <summary>Скрыть «＋» (там, где новый поставщик не нужен).</summary>
    public bool ShowAdd
    {
        get => _add.IsVisible;
        set => _add.IsVisible = value;
    }

    public async Task LoadAsync()
    {
        if (_loading)
            return;
        _loading = true;
        Render();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            _all = await _svc.ListAsync(cts.Token).ConfigureAwait(true);
            if (_selected is { } s)
                _selected = _all.FirstOrDefault(x => x.Id == s.Id) ?? s;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: поставщики не загружены ({ex.Message}).", "WARNING");
        }
        finally
        {
            _loading = false;
            Render();
        }
    }

    /// <summary>Заполнить список без запроса (стенд снимков, переход из «Пополнения»).</summary>
    public void SetSuppliers(IEnumerable<SupplierInfo> suppliers)
    {
        _all = suppliers.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Render();
    }

    public void Select(string? supplierId, bool raise = true)
    {
        var s = string.IsNullOrEmpty(supplierId) ? null : _all.FirstOrDefault(x => x.Id == supplierId);
        _selected = s;
        Render();
        if (raise)
            Changed?.Invoke(s);
    }

    public void Select(SupplierInfo? s, bool raise = true)
    {
        if (s is not null && _all.All(x => x.Id != s.Id))
            _all = _all.Append(s).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _selected = s;
        Render();
        if (raise)
            Changed?.Invoke(s);
    }

    private void Pick(SupplierInfo? s)
    {
        _flyout.Hide();
        if (s?.Id == _selected?.Id)
            return;
        _selected = s;
        Render();
        Changed?.Invoke(s);
    }

    private void Render()
    {
        _text.Text = _selected?.Name
                     ?? (_loading ? T("Загружаю поставщиков…", "Жеткирүүчүлөр жүктөлүүдө…", "Loading suppliers…", "Tedarikçiler yükleniyor…", "Yetkazib beruvchilar yuklanmoqda…")
                         : AllowAll ? T("Все поставщики", "Бардык жеткирүүчүлөр", "All suppliers", "Tüm tedarikçiler", "Barcha yetkazib beruvchilar")
                         : T("Выберите поставщика", "Жеткирүүчүнү тандаңыз", "Choose a supplier", "Tedarikçi seçin", "Yetkazib beruvchini tanlang"));
        Use(_text, TextBlock.ForegroundProperty, _selected is null && !AllowAll ? "BrushTextSoft" : "BrushText");
        _text.FontWeight = _selected is null ? FontWeight.Normal : FontWeight.SemiBold;
    }

    private void RenderList()
    {
        _list.Children.Clear();
        var q = (_search.Text ?? "").Trim();
        var digits = new string(q.Where(char.IsDigit).ToArray());
        if (AllowAll && q.Length == 0)
            _list.Children.Add(Row(null, T("Все поставщики", "Бардык жеткирүүчүлөр", "All suppliers", "Tüm tedarikçiler", "Barcha yetkazib beruvchilar"), ""));
        var shown = _all.Where(s => q.Length == 0 || SearchMatch.Matches(q, s.Name, s.Llc) // 2026-10-29: единый поиск
                                    || (digits.Length >= 3 && new string(s.Phone.Where(char.IsDigit).ToArray()).Contains(digits, StringComparison.Ordinal))).Take(200).ToList();
        foreach (var s in shown)
            _list.Children.Add(Row(s, s.Name, s.Phone));
        if (shown.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = _all.Count == 0
                    ? T("Поставщиков пока нет — нажмите «＋».", "Азырынча жеткирүүчүлөр жок — «＋» басыңыз.", "No suppliers yet — press “＋”.", "Henüz tedarikçi yok — «＋»ya basın.", "Hozircha yetkazib beruvchilar yo'q — «＋» ni bosing.")
                    : T("Не найдено. Проверьте написание или нажмите «＋».", "Табылган жок. Жазылышын текшериңиз же «＋» басыңыз.", "Not found. Check the spelling or press “＋”.", "Bulunamadı. Yazımı kontrol edin veya «＋»ya basın.", "Topilmadi. Yozilishini tekshiring yoki «＋» ni bosing."),
                FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 8),
            };
            Use(empty, TextBlock.ForegroundProperty, "BrushTextSoft");
            _list.Children.Add(empty);
        }
    }

    private Button Row(SupplierInfo? s, string name, string sub)
    {
        var text = new StackPanel { Spacing = 1 };
        var title = new TextBlock { Text = name, FontSize = 14, FontWeight = s?.Id == _selected?.Id ? FontWeight.Bold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        text.Children.Add(title);
        if (sub.Length > 0)
        {
            var phone = new TextBlock { Text = sub, FontSize = 12 };
            Use(phone, TextBlock.ForegroundProperty, "BrushTextSoft");
            text.Children.Add(phone);
        }
        var b = new Button
        {
            Content = text, Tag = s, MinHeight = 44, Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center,
        };
        Avalonia.Automation.AutomationProperties.SetName(b, name);
        b.Click += (_, _) => Pick(s);
        return b;
    }

    private async Task CreateAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var dialog = new SupplierEditDialog(_svc, null, _all);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        if (dialog.Saved is not { } created)
            return;
        PosLogger.Log($"Закупки: добавлен поставщик {created.Id}.", "INFO");
        Select(created);
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) => target.Bind(property, this.GetResourceObservable(key));
}
