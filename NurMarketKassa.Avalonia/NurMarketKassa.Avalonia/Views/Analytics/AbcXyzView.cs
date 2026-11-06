using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Analytics;

/// <summary>2026-11-05 (волна 2-А, функция 6): вкладка «ABC×XYZ» — матрица 3×3 «важность × предсказуемость спроса» за 12 недель продаж.
/// A/B/C — доля выручки (80/15/5 %), X/Y/Z — насколько ровно продаётся по неделям (коэффициент вариации до 25 % / до 50 % / выше).
/// AX держать всегда, CZ — под заказ или вывести. Клик по клетке — список товаров. Считается по истории продаж этого ПК (SoldLineItemsStore),
/// товаров с историей короче 4 недель — отдельной строкой «мало истории». Итог по классу идёт в автозаказ (страховой запас) и подсказку Нур Советнику.</summary>
public sealed class AbcXyzView : UserControl
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly StackPanel _root = new() { Margin = new Thickness(10), Spacing = 8 };
    private readonly TextBlock _note = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly Grid _matrix = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _hint = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly StackPanel _list = new() { Spacing = 2 };
    private AbcXyzResult? _result;
    private string? _cell;
    private bool _loading;
    private DateTime _loadedAt = DateTime.MinValue;
    // 2026-11-05 (ревью волн 0–3, В5): подкачка истории — не чаще раза в час и не параллельно; после неё пересчёт БЕЗ новой подкачки.
    // Раньше условие «мало истории» оставалось истинным после каждой подкачки (новый магазин, длинный «хвост» товаров) и цикл крутился без пауз.
    private DateTime _lastBackfillAt = DateTime.MinValue;
    private bool _backfilling;

    public AbcXyzView()
    {
        _note.Foreground = ThemeInk.Soft;
        _root.Children.Add(_note);
        _root.Children.Add(_matrix);
        _root.Children.Add(_hint);
        _root.Children.Add(_list);
        Content = new ScrollViewer { Content = _root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    /// <summary>Построить при первом показе (и не чаще раза в 2 минуты).</summary>
    public void EnsureLoaded()
    {
        if (_loading || DateTime.Now - _loadedAt < TimeSpan.FromMinutes(2))
            return;
        _ = LoadAsync();
    }

    private async Task LoadAsync(bool allowBackfill = true)
    {
        _loading = true;
        _note.Text = T("Считаю по истории продаж за 12 недель…", "12 жумалык сатуу тарыхы боюнча эсептелүүдө…", "Calculating from 12 weeks of sales…", "12 haftalık satış geçmişinden hesaplanıyor…", "12 haftalik sotuv tarixidan hisoblanmoqda…");
        try
        {
            var today = DateTime.Today;
            var since = today.AddDays(-83).ToUniversalTime();
            var result = await Task.Run(() =>
            {
                var lines = SoldLineItemsStore.LoadWithPriceSince(since);
                return AbcXyz.Compute(lines.Select(l => (l.ProductId, l.ProductName, l.Quantity, l.Quantity * l.UnitPrice, l.SoldAt.ToLocalTime())), today);
            }).ConfigureAwait(true);
            _result = result;
            _loadedAt = DateTime.Now;
            Render();
            // Истории мало — подтянуть с сервера в фоне (как «Пополнение») и пересчитать.
            if (allowBackfill && !_backfilling && DateTime.Now - _lastBackfillAt > TimeSpan.FromHours(1)
                && (result.Items.Count == 0 || result.Items.Count(i => i.Xyz is null) > result.Items.Count / 2))
                _ = BackfillAsync();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ABC×XYZ не построен: {ex.GetType().Name}: {ex.Message}", "WARNING");
            _note.Text = T("Не удалось построить матрицу.", "Матрицаны түзүү мүмкүн болгон жок.", "Could not build the matrix.", "Matris oluşturulamadı.", "Matritsani tuzib bo'lmadi.");
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task BackfillAsync()
    {
        _backfilling = true;
        _lastBackfillAt = DateTime.Now;
        try
        {
            await SalesHistoryBackfill.RunAsync().ConfigureAwait(true);
            _loadedAt = DateTime.MinValue;
            // Окно ждёт, пока предыдущий расчёт закончится (он мог ещё идти) — потом один пересчёт без новой подкачки.
            for (var i = 0; i < 50 && _loading; i++)
                await Task.Delay(100).ConfigureAwait(true);
            if (!_loading)
                await LoadAsync(allowBackfill: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ABC×XYZ: история продаж не подтянута ({ex.GetType().Name}).", "WARNING");
        }
        finally
        {
            _backfilling = false;
        }
    }

    private void Render()
    {
        if (_result is not { } r)
            return;
        var known = r.Items.Count(i => i.Xyz is not null);
        // 2026-11-06 (В12, Н10 «Ревью волн 0–3 — функции магазина»): буквы A/B/C здесь считаются по продажам, записанным НА ЭТОЙ кассе, а вкладки ABC в «Аналитике» — по серверу;
        // классы могут не совпасть. Серверный срез по неделям недоступен, поэтому подписываем источник (раньше он нигде не был виден).
        var source = "\n" + T("Источник: продажи, записанные на этой кассе (не весь магазин). Вкладка ABC в «Аналитике» считает по серверу — классы могут отличаться.",
            "Булак: ушул кассада жазылган сатуулар (бүтүн дүкөн эмес). «Аналитикадагы» ABC өтмөгү серверден эсептейт — класстар айырмаланышы мүмкүн.",
            "Source: sales recorded on this till (not the whole shop). The ABC tab in “Analytics” counts from the server — classes may differ.",
            "Kaynak: bu kasada kayıtlı satışlar (tüm mağaza değil). «Analiz»deki ABC sekmesi sunucudan hesaplar — sınıflar farklı olabilir.",
            "Manba: shu kassada yozilgan sotuvlar (butun do'kon emas). «Analitika»dagi ABC yorlig'i serverdan hisoblaydi — sinflar farq qilishi mumkin.");
        _note.Text = r.Items.Count == 0
            ? T("Продаж за последние 12 недель нет — считать нечего.", "Акыркы 12 жумада сатуу жок — эсептей турган эч нерсе жок.", "No sales in the last 12 weeks — nothing to calculate.", "Son 12 haftada satış yok — hesaplanacak bir şey yok.", "Oxirgi 12 haftada sotuv yo'q — hisoblaydigan narsa yo'q.")
            : T($"Продажи за 12 недель: {r.Items.Count} товаров, у {known} достаточно истории (4 недели и больше). A — 80 % выручки, B — 15 %, C — 5 %. X — спрос ровный (разброс по неделям до 25 %), Y — до 50 %, Z — случайный. Нажмите клетку — увидите товары.",
                $"12 жумалык сатуу: {r.Items.Count} товар, {known} товарда тарых жетиштүү (4 жума жана андан көп). A — түшүмдүн 80 %, B — 15 %, C — 5 %. X — суроо туруктуу (жумалар боюнча 25 % чейин), Y — 50 % чейин, Z — кокус. Клетканы басыңыз.",
                $"Sales over 12 weeks: {r.Items.Count} products, {known} have enough history (4+ weeks). A — 80 % of revenue, B — 15 %, C — 5 %. X — steady demand (weekly spread up to 25 %), Y — up to 50 %, Z — random. Click a cell to see the products.",
                $"12 haftalık satış: {r.Items.Count} ürün, {known} ürünün geçmişi yeterli (4+ hafta). A — cironun %80'i, B — %15, C — %5. X — düzenli talep (haftalık sapma %25'e kadar), Y — %50'ye kadar, Z — rastgele. Ürünleri görmek için hücreye tıklayın.",
                $"12 haftalik sotuv: {r.Items.Count} mahsulot, {known} tasida tarix yetarli (4+ hafta). A — tushumning 80 %, B — 15 %, C — 5 %. X — talab barqaror (haftalar bo'yicha 25 % gacha), Y — 50 % gacha, Z — tasodifiy. Mahsulotlarni ko'rish uchun katakni bosing.") + source;

        _matrix.Children.Clear();
        _matrix.RowDefinitions.Clear();
        _matrix.ColumnDefinitions.Clear();
        _matrix.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var i = 0; i < 3; i++)
            _matrix.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var i = 0; i < 5; i++)
            _matrix.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var xyz = new[] { "X", "Y", "Z" };
        var xyzTitle = new[] { T("X — ровно", "X — туруктуу", "X — steady", "X — düzenli", "X — barqaror"), T("Y — колеблется", "Y — термелет", "Y — varies", "Y — dalgalı", "Y — tebranadi"), T("Z — случайно", "Z — кокус", "Z — random", "Z — rastgele", "Z — tasodifiy") };
        for (var c = 0; c < 3; c++)
            AddText(xyzTitle[c], 0, c + 1, true);
        var abc = new[] { "A", "B", "C" };
        for (var row = 0; row < 3; row++)
        {
            AddText(abc[row], row + 1, 0, true);
            for (var c = 0; c < 3; c++)
                AddCell(abc[row] + xyz[c], row + 1, c + 1, r);
        }
        // Мало истории — отдельная строка под матрицей.
        var short1 = r.Items.Count(i => i.Xyz is null);
        AddText(T("мало истории", "тарых аз", "short history", "az geçmiş", "tarix kam"), 4, 0, true);
        var shortCell = new Border
        {
            Margin = new Thickness(3), Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(8), Cursor = new Cursor(StandardCursorType.Hand),
            Background = new SolidColorBrush(Color.Parse("#22808080")), BorderBrush = _cell == "?" ? Brushes.Gray : Brushes.Transparent, BorderThickness = new Thickness(2),
            Child = new TextBlock { Text = short1 + " " + T("товаров (меньше 4 недель продаж)", "товар (4 жумадан аз сатуу)", "products (under 4 weeks of sales)", "ürün (4 haftadan az satış)", "mahsulot (4 haftadan kam sotuv)"), FontSize = 13 },
        };
        shortCell.PointerPressed += (_, _) => Pick("?");
        Grid.SetRow(shortCell, 4);
        Grid.SetColumn(shortCell, 1);
        Grid.SetColumnSpan(shortCell, 3);
        _matrix.Children.Add(shortCell);
        ShowList();
    }

    private static string Tint(string cell) => cell switch
    {
        "AX" or "AY" or "BX" => "#3322C55E",
        "AZ" or "BY" or "CX" => "#33EAB308",
        "BZ" or "CY" => "#33F97316",
        _ => "#33EF4444",
    };

    private void AddText(string text, int row, int col, bool bold)
    {
        var tb = new TextBlock { Text = text, FontSize = 13, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, Margin = new Thickness(6, 4), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center };
        Grid.SetRow(tb, row);
        Grid.SetColumn(tb, col);
        _matrix.Children.Add(tb);
    }

    private void AddCell(string cell, int row, int col, AbcXyzResult r)
    {
        var count = r.Count(cell);
        var share = r.Items.Sum(i => i.Revenue) is var total && total > 0 ? r.Revenue(cell) / total * 100 : 0;
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(new TextBlock { Text = cell, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = ThemeInk.Soft });
        body.Children.Add(new TextBlock { Text = count.ToString("N0"), FontSize = 20, FontWeight = FontWeight.Bold });
        body.Children.Add(new TextBlock { Text = T($"{share:0.#} % выручки", $"түшүмдүн {share:0.#} %", $"{share:0.#} % of revenue", $"cironun %{share:0.#}'i", $"tushumning {share:0.#} %"), FontSize = 11.5, Foreground = ThemeInk.Soft });
        var border = new Border
        {
            Margin = new Thickness(3), Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(8), Cursor = new Cursor(StandardCursorType.Hand),
            Background = new SolidColorBrush(Color.Parse(Tint(cell))), BorderBrush = _cell == cell ? Brushes.Gray : Brushes.Transparent, BorderThickness = new Thickness(2),
            Child = body,
        };
        ToolTip.SetTip(border, AbcXyz.Hint(cell));
        border.PointerPressed += (_, _) => Pick(cell);
        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        _matrix.Children.Add(border);
    }

    private void Pick(string cell)
    {
        _cell = _cell == cell ? null : cell;
        Render();
    }

    private void ShowList()
    {
        _list.Children.Clear();
        if (_cell is null || _result is not { } r)
        {
            _hint.Text = "";
            return;
        }
        _hint.Text = _cell + " — " + AbcXyz.Hint(_cell);
        var items = r.Items.Where(i => (_cell == "?" ? i.Xyz is null : i.Cell == _cell)).OrderByDescending(i => i.Revenue).Take(40).ToList();
        foreach (var i in items)
        {
            var cv = i.Cv is { } v ? T($"разброс {v * 100:0} %", $"айырма {v * 100:0} %", $"spread {v * 100:0} %", $"sapma %{v * 100:0}", $"farq {v * 100:0} %") : T("мало истории", "тарых аз", "short history", "az geçmiş", "tarix kam");
            _list.Children.Add(new TextBlock { Text = $"{i.Name} — {i.Revenue:N0} · {cv}", FontSize = 13, TextWrapping = TextWrapping.Wrap });
        }
        var total = r.Items.Count(i => _cell == "?" ? i.Xyz is null : i.Cell == _cell);
        if (total > items.Count)
            _list.Children.Add(new TextBlock { Text = T($"… и ещё {total - items.Count}", $"… дагы {total - items.Count}", $"… and {total - items.Count} more", $"… ve {total - items.Count} tane daha", $"… yana {total - items.Count} ta"), FontSize = 12.5, Foreground = ThemeInk.Soft });
    }
}
