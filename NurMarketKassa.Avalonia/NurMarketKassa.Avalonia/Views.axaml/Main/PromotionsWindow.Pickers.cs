using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-27: выбор товаров (поиск каталога NurCRM, выбранные — снимаемые «чипы») и список категорий / брендов / филиалов с галочками (категории — деревом, по UUID)
/// для мастера акций.</summary>
public sealed partial class PromotionsWindow
{
    /// <summary>Поиск товара + выбранные. С <c>onPick</c> — режим «добавить в пример» (без списка выбранных).</summary>
    private sealed class ProductPicker
    {
        private readonly PromotionsWindow _w;
        private readonly List<string> _ids;
        private readonly Action<PromotionProduct>? _onPick;
        private readonly TextBox _search;
        private readonly StackPanel _results = new() { Spacing = 4 };
        private readonly WrapPanel _chosen = new() { Orientation = Orientation.Horizontal };
        private readonly TextBlock _note = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        private int _gen;

        public StackPanel View { get; } = new() { Spacing = 6 };

        public List<string> Ids => _ids.ToList();

        public ProductPicker(PromotionsWindow w, List<string> ids, string watermark, Action<PromotionProduct>? onPick)
        {
            _w = w;
            _ids = ids.ToList();
            _onPick = onPick;
            _search = UiKit.Search(w, watermark, FieldHeight);
            _search.HorizontalAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetName(_search, watermark);
            UiKit.OnTypedDebounced(_search, () => _ = SearchAsync(), 350);
            w.Use(_note, TextBlock.ForegroundProperty, "BrushTextSoft");
            View.Children.Add(_search);
            View.Children.Add(_note);
            View.Children.Add(_results);
            if (onPick == null)
                View.Children.Add(_chosen);
            RenderChosen();
        }

        private async Task SearchAsync()
        {
            var q = (_search.Text ?? "").Trim();
            var gen = ++_gen;
            _results.Children.Clear();
            if (q.Length < 2)
            {
                _note.IsVisible = false;
                return;
            }
            _note.Text = T("Ищу…", "Издеп жатам…", "Searching…", "Aranıyor…", "Qidirilmoqda…");
            _note.IsVisible = true;
            try
            {
                var found = await _w._api.SearchProductsAsync(q, _w._cts.Token).ConfigureAwait(true);
                if (gen != _gen)
                    return;
                foreach (var p in found)
                    _w._productNames[p.Id] = p;
                _note.Text = found.Count == 0 ? T("Ничего не найдено. Попробуйте часть названия или штрихкод.", "Эч нерсе табылган жок. Аталыштын бир бөлүгүн же штрихкодду жазып көрүңүз.",
                    "Nothing found. Try part of the name or the barcode.", "Hiçbir şey bulunamadı. Adın bir kısmını veya barkodu deneyin.", "Hech narsa topilmadi. Nomning bir qismini yoki shtrix-kodni sinab ko'ring.") : "";
                _note.IsVisible = found.Count == 0;
                foreach (var p in found.Take(8))
                    _results.Children.Add(ResultRow(p));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (gen != _gen)
                    return;
                PosLogger.Log($"Акции: поиск товара не удался ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                _note.Text = PromotionErrors.Describe(ex).Text;
                _note.IsVisible = true;
            }
        }

        private Control ResultRow(PromotionProduct p)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var name = new TextBlock
            {
                Text = p.Name + (p.Price is > 0 ? " · " + PromotionText.Money(p.Price.Value) : ""),
                FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            };
            _w.Use(name, TextBlock.ForegroundProperty, "BrushText");
            row.Children.Add(name);
            var already = _onPick == null && _ids.Contains(p.Id);
            var add = Small(UiKit.Ghost(_w, already ? T("Выбран", "Тандалды", "Chosen", "Seçildi", "Tanlangan") : T("Добавить", "Кошуу", "Add", "Ekle", "Qo'shish")));
            add.IsEnabled = !already;
            add.Margin = new Thickness(8, 0, 0, 0);
            AutomationProperties.SetName(add, (string)add.Content! + ": " + p.Name);
            add.Click += (_, _) =>
            {
                if (_onPick != null)
                    _onPick(p);
                else if (!_ids.Contains(p.Id))
                {
                    _ids.Add(p.Id);
                    RenderChosen();
                }
                _search.Text = "";
                _results.Children.Clear();
                _note.IsVisible = false;
            };
            Grid.SetColumn(add, 1);
            row.Children.Add(add);
            return row;
        }

        public void RenderChosen()
        {
            _chosen.Children.Clear();
            if (_onPick != null)
                return;
            foreach (var id in _ids)
            {
                var pid = id;
                var label = new TextBlock { Text = _w.ProductName(id) + "  ✕", FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                var chip = UiKit.Chip(_w, "", false);
                chip.Content = label;
                chip.MaxWidth = 340;
                chip.Margin = new Thickness(0, 4, 6, 0);
                AutomationProperties.SetName(chip, T("Убрать: ", "Алып салуу: ", "Remove: ", "Kaldır: ", "Olib tashlash: ") + _w.ProductName(id));
                chip.Click += (_, _) =>
                {
                    _ids.Remove(pid);
                    RenderChosen();
                };
                _chosen.Children.Add(chip);
            }
        }
    }

    /// <summary>Список с галочками по UUID; tree=true — категории деревом (подкатегории с отступом). Неизвестные id из акции сохраняются как есть.</summary>
    private sealed class RefChecklist
    {
        private readonly PromotionsWindow _w;
        private readonly HashSet<string> _selected;
        private readonly bool _tree;
        private readonly TextBox _filter;
        private readonly StackPanel _items = new() { Spacing = 0 };
        private readonly TextBlock _note = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        private readonly List<(CheckBox Box, string Name)> _boxes = new();

        public StackPanel View { get; } = new() { Spacing = 6 };

        public List<string> Ids => _selected.ToList();

        public RefChecklist(PromotionsWindow w, List<string> selected, bool tree)
        {
            _w = w;
            _selected = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
            _tree = tree;
            _filter = UiKit.Search(w, T("Найти в списке", "Тизмеден табуу", "Find in the list", "Listede bul", "Ro'yxatdan topish"), FieldHeight);
            _filter.HorizontalAlignment = HorizontalAlignment.Stretch;
            _filter.IsVisible = false;
            AutomationProperties.SetName(_filter, _filter.Watermark ?? "");
            _filter.TextChanged += (_, _) => ApplyFilter();
            w.Use(_note, TextBlock.ForegroundProperty, "BrushTextSoft");
            _note.Text = T("Загружаю список…", "Тизмени жүктөп жатам…", "Loading the list…", "Liste yükleniyor…", "Ro'yxat yuklanmoqda…");
            var scroll = new ScrollViewer { Content = _items, MaxHeight = 240, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            View.Children.Add(_filter);
            View.Children.Add(_note);
            View.Children.Add(scroll);
        }

        public void SetItems(IReadOnlyList<PromotionRef> refs)
        {
            _items.Children.Clear();
            _boxes.Clear();
            var ordered = new List<(PromotionRef Ref, int Depth)>();
            if (_tree)
            {
                var ids = refs.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var byParent = refs.GroupBy(r => r.ParentId is { } pid && ids.Contains(pid) ? pid : "", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void Walk(string parent, int depth)
                {
                    if (depth > 8 || !byParent.TryGetValue(parent, out var kids))
                        return;
                    foreach (var k in kids)
                    {
                        if (!seen.Add(k.Id))
                            continue;
                        ordered.Add((k, depth));
                        Walk(k.Id, depth + 1);
                    }
                }
                Walk("", 0);
            }
            else
                ordered.AddRange(refs.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(r => (r, 0)));

            foreach (var (r, depth) in ordered)
            {
                var id = r.Id;
                var text = r.Name.Length > 0 ? r.Name : "…" + (id.Length > 6 ? id[^6..] : id);
                var box = new CheckBox
                {
                    IsChecked = _selected.Contains(id), MinHeight = 36, Margin = new Thickness(depth * 22, 0, 0, 0),
                    Content = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                };
                AutomationProperties.SetName(box, text);
                box.IsCheckedChanged += (_, _) =>
                {
                    if (box.IsChecked == true)
                        _selected.Add(id);
                    else
                        _selected.Remove(id);
                };
                _boxes.Add((box, text));
                _items.Children.Add(box);
            }
            _note.Text = ordered.Count == 0 ? T("Список пуст (или не загрузился).", "Тизме бош (же жүктөлгөн жок).", "The list is empty (or didn't load).", "Liste boş (veya yüklenmedi).", "Ro'yxat bo'sh (yoki yuklanmadi).") : "";
            _note.IsVisible = ordered.Count == 0;
            _filter.IsVisible = ordered.Count > 12;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var q = (_filter.Text ?? "").Trim();
            foreach (var (box, name) in _boxes)
                box.IsVisible = q.Length == 0 || name.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
