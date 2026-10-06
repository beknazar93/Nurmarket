using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// 2026-10-06, исследование «Кассы для одежды» (О-80…О-82), владелец: «делай всё по этапно». Раздел программы владельца
/// «Размеры и цвета» (только в сфере «Одежда»): сколько продано за период по размерам, по цветам и по моделям с размерами,
/// выручка, остаток и доля проданного (продано / (продано + остаток)) — какие размеры уходят первыми и что дозаказать.
/// Сервер такого отчёта пока не даёт (ТЗ бэкенда, часть 14, п. 14.10), поэтому он считается по чекам периода: список
/// продаж и состав чеков (запомненные чеки — SaleSizesCache, ограничитель запросов). Возвращённое не считается. Остатки —
/// из справочника размеров касс (VariantBarcodeIndex).
/// </summary>
public sealed class SizesReportWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly WrapPanel _periods = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
    private readonly StackPanel _body = new() { Spacing = 18 };
    private CancellationTokenSource? _cts;
    // По умолчанию неделя: первый раз отчёт читает каждый чек периода (за 30 дней на тестовом аккаунте — 1083 чека, около
    // 2 минут); потом чеки берутся из памяти программы (SaleSizesCache), а программа владельца подгружает 30 дней заранее.
    private int _days = 7;

    private sealed class Agg
    {
        public double Qty;
        public double Revenue;
    }

    public SizesReportWindow()
    {
        Title = Tr.T("Размеры и цвета", "Өлчөмдөр жана түстөр", "Sizes and colours", "Bedenler ve renkler", "O'lchamlar va ranglar");
        Width = 1100;
        Height = 800;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        var root = new StackPanel { Margin = new Thickness(24, 16, 24, 24), Spacing = 6 };
        var title = new TextBlock { Text = Title, FontSize = 22, FontWeight = FontWeight.Bold };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(title);
        root.Children.Add(Soft(Tr.T(
            "Что продано по размерам и цветам за период (возвращённое не считается). «Доля проданного» — продано от продано + остаток: у какого размера она выше, тот уходит первым — его и дозаказывайте.",
            "Мезгилде өлчөмдөр жана түстөр боюнча эмне сатылды (кайтарылгандар эсептелбейт). «Сатылган үлүш» — сатылганы / (сатылганы + калдык): кайсы өлчөмдө жогору болсо, ошол биринчи кетет — аны кайра буйртма бериңиз.",
            "What sold by size and colour in the period (returns excluded). “Sell-through” — sold / (sold + stock): the higher it is, the faster the size sells — reorder it.",
            "Dönemde beden ve renge göre satılanlar (iadeler hariç). «Satış oranı» — satılan / (satılan + stok): yüksek olan beden önce tükenir — onu yeniden sipariş edin.",
            "Davr ichida o'lcham va ranglar bo'yicha nima sotildi (qaytarilganlar hisoblanmaydi). «Sotilgan ulush» — sotilgan / (sotilgan + qoldiq): qaysi o'lchamda yuqori bo'lsa, o'sha birinchi tugaydi — uni qayta buyurtma qiling.")));
        root.Children.Add(_periods);
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        root.Children.Add(_status);
        root.Children.Add(_body);
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        BuildPeriods();
        Opened += async (_, _) => await LoadAsync().ConfigureAwait(true);
        Closed += (_, _) => _cts?.Cancel();
    }

    private void BuildPeriods()
    {
        _periods.Children.Clear();
        foreach (var (days, text) in new[]
                 {
                     (1, Tr.T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun")),
                     (7, Tr.T("7 дней", "7 күн", "7 days", "7 gün", "7 kun")),
                     (30, Tr.T("30 дней", "30 күн", "30 days", "30 gün", "30 kun")),
                     (90, Tr.T("90 дней", "90 күн", "90 days", "90 gün", "90 kun")),
                 })
        {
            var chip = UiKit.Chip(this, text, _days == days);
            chip.Margin = new Thickness(0, 6, 8, 0);
            chip.Click += async (_, _) =>
            {
                _days = days;
                BuildPeriods();
                await LoadAsync().ConfigureAwait(true);
            };
            _periods.Children.Add(chip);
        }
    }

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        _body.Children.Clear();
        _status.Text = Tr.T("Загружаю чеки…", "Чектер жүктөлүүдө…", "Loading receipts…", "Fişler yükleniyor…", "Cheklar yuklanmoqda…");
        try
        {
            // 2026-10-06, владелец: «очень долгая загрузка» (30 дней — 1083 чека, 2–3 минуты при каждом открытии).
            // Прочитанные чеки теперь запоминаются (SaleSizesCache): с сервера — только новые и те, по которым был возврат;
            // таблицы дорисовываются по ходу загрузки, а не после последнего чека.
            var from = DateTime.Today.AddDays(-(_days - 1));
            var toExclusive = DateTime.Today.AddDays(1);
            var result = await SaleSizesCache.GetPeriodAsync(from, toExclusive, false, (done, total, partial) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (ct.IsCancellationRequested || done >= total)
                        return;
                    _status.Text = Tr.T($"Загружаю чеки: {done} из {total}… Таблицы ниже дополняются по ходу. Прочитанные чеки программа запоминает — в следующий раз отчёт откроется быстро.",
                        $"Чектер жүктөлүүдө: {done} / {total}… Төмөнкү таблицалар жүрүшүндө толукталат. Окулган чектерди программа эстеп калат — кийинки жолу отчёт тез ачылат.",
                        $"Loading receipts: {done} of {total}… The tables below fill in as it goes. Loaded receipts are remembered — next time the report opens quickly.",
                        $"Fişler yükleniyor: {done} / {total}… Aşağıdaki tablolar yüklendikçe dolar. Okunan fişler hatırlanır — rapor bir dahaki sefere hızlı açılır.",
                        $"Cheklar yuklanmoqda: {done} / {total}… Quyidagi jadvallar yuklanish davomida to'ldiriladi. O'qilgan cheklar eslab qolinadi — keyingi safar hisobot tez ochiladi.");
                    if (partial is not null)
                        Render(partial);
                }), ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
                return;

            var (qty, revenue) = Render(result.Lines);
            _status.Text = Tr.T($"Чеков за период: {result.Receipts}. Продано по размерам: {qty:0.###} шт. на {Money(revenue)}.",
                $"Мезгилдеги чектер: {result.Receipts}. Өлчөмдөр боюнча сатылды: {qty:0.###} даана, {Money(revenue)}.",
                $"Receipts in period: {result.Receipts}. Sold by size: {qty:0.###} pcs, {Money(revenue)}.",
                $"Dönemdeki fişler: {result.Receipts}. Bedene göre satılan: {qty:0.###} adet, {Money(revenue)}.",
                $"Davrdagi cheklar: {result.Receipts}. O'lchamlar bo'yicha sotildi: {qty:0.###} dona, {Money(revenue)}.")
                + (result.Failed > 0
                    ? Tr.T($" Не прочитано чеков: {result.Failed} — откройте отчёт ещё раз позже.", $" Окулбаган чектер: {result.Failed} — отчётту кийинчерээк кайра ачыңыз.",
                        $" Receipts not loaded: {result.Failed} — open the report again later.", $" Okunamayan fiş: {result.Failed} — raporu daha sonra yeniden açın.",
                        $" O'qilmagan cheklar: {result.Failed} — hisobotni keyinroq qayta oching.")
                    : "");
            PosLogger.Log($"Размеры и цвета: {_days} дн., чеков {result.Receipts}, с сервера {result.Downloaded}, не прочитано {result.Failed}.", "UI");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _status.Text = Tr.T("Отчёт не построен: ", "Отчёт түзүлгөн жок: ", "The report was not built: ", "Rapor oluşturulamadı: ", "Hisobot tuzilmadi: ") + ex.Message;
            PosLogger.Log($"Размеры и цвета: отчёт не построен ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Таблицы по размерам, цветам и моделям из строк чеков; возвращает итог (штук, выручка) по размерам.</summary>
    private (double Qty, double Revenue) Render(List<SaleSizesCache.Line> lines)
    {
        var bySize = new Dictionary<string, Agg>(StringComparer.OrdinalIgnoreCase);
        var byColor = new Dictionary<string, Agg>(StringComparer.OrdinalIgnoreCase);
        var byModel = new Dictionary<(string Product, string Name, string Size, string Color), Agg>();
        void Add<TKey>(Dictionary<TKey, Agg> d, TKey key, SaleSizesCache.Line line) where TKey : notnull
        {
            if (!d.TryGetValue(key, out var a))
                d[key] = a = new Agg();
            a.Qty += line.Quantity;
            a.Revenue += line.Revenue;
        }
        foreach (var line in lines)
        {
            if (line.Size.Length > 0)
                Add(bySize, line.Size, line);
            if (line.Color.Length > 0)
                Add(byColor, line.Color, line);
            Add(byModel, (line.ProductId, line.Name, line.Size, line.Color), line);
        }

        _body.Children.Clear();
        if (bySize.Count == 0 && byColor.Count == 0)
        {
            _body.Children.Add(Soft(Tr.T("За этот период продаж с размерами и цветами нет.", "Бул мезгилде өлчөм жана түс менен сатуу жок.", "No sales with sizes and colours in this period.",
                "Bu dönemde beden ve renkli satış yok.", "Bu davrda o'lcham va rangli sotuvlar yo'q.")));
            return (0, 0);
        }

        // Остатки — из справочника размеров касс (обновляется в фоне).
        var stock = VariantBarcodeIndex.AllVariants();
        var stockBySize = stock.GroupBy(v => v.Size.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(v => Math.Max(0, v.Quantity)), StringComparer.OrdinalIgnoreCase);
        var stockByColor = stock.GroupBy(v => v.Color.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(v => Math.Max(0, v.Quantity)), StringComparer.OrdinalIgnoreCase);
        var stockByVariant = stock.GroupBy(v => (v.ProductId, v.Size.Trim().ToLowerInvariant(), v.Color.Trim().ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => g.Sum(v => Math.Max(0, v.Quantity)));

        _body.Children.Add(Table(Tr.T("По размерам", "Өлчөмдөр боюнча", "By size", "Bedene göre", "O'lchamlar bo'yicha"),
            Tr.T("Размер", "Өлчөм", "Size", "Beden", "O'lcham"),
            bySize.OrderBy(kv => SizeRank(kv.Key)).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (kv.Key, kv.Value, stockBySize.TryGetValue(kv.Key, out var s) ? s : (double?)null)).ToList()));
        _body.Children.Add(Table(Tr.T("По цветам", "Түстөр боюнча", "By colour", "Renge göre", "Ranglar bo'yicha"),
            Tr.T("Цвет", "Түс", "Colour", "Renk", "Rang"),
            byColor.OrderByDescending(kv => kv.Value.Qty)
                .Select(kv => (kv.Key, kv.Value, stockByColor.TryGetValue(kv.Key, out var s) ? s : (double?)null)).ToList()));
        _body.Children.Add(Table(Tr.T("Модели и размеры — лучшие 30", "Моделдер жана өлчөмдөр — эң мыкты 30", "Models and sizes — top 30", "Modeller ve bedenler — en iyi 30", "Modellar va o'lchamlar — eng yaxshi 30"),
            Tr.T("Товар — размер, цвет", "Товар — өлчөм, түс", "Product — size, colour", "Ürün — beden, renk", "Mahsulot — o'lcham, rang"),
            byModel.OrderByDescending(kv => kv.Value.Qty).Take(30)
                .Select(kv => (kv.Key.Name + " — " + string.Join(", ", new[] { kv.Key.Size, kv.Key.Color }.Where(x => x.Length > 0)),
                    kv.Value,
                    stockByVariant.TryGetValue((kv.Key.Product, kv.Key.Size.ToLowerInvariant(), kv.Key.Color.ToLowerInvariant()), out var s) ? s : (double?)null)).ToList()));
        return (bySize.Values.Sum(a => a.Qty), bySize.Values.Sum(a => a.Revenue));
    }

    /// <summary>Таблица: название | продано | выручка | остаток | доля проданного.</summary>
    private Control Table(string title, string firstHeader, List<(string Key, Agg Agg, double? Stock)> rows)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 12), BorderThickness = new Thickness(1) };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        var stack = new StackPanel { Spacing = 6 };
        var head = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 4) };
        Use(head, TextBlock.ForegroundProperty, "BrushText");
        stack.Children.Add(head);
        stack.Children.Add(Row(firstHeader, Tr.T("Продано", "Сатылды", "Sold", "Satılan", "Sotildi"), Tr.T("Выручка", "Түшүү", "Revenue", "Ciro", "Tushum"),
            Tr.T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq"), Tr.T("Доля проданного", "Сатылган үлүш", "Sell-through", "Satış oranı", "Sotilgan ulush"), header: true));
        foreach (var (key, agg, stock) in rows)
        {
            var share = stock is { } s && agg.Qty + s > 0 ? $"{agg.Qty / (agg.Qty + s) * 100:0}%" : "—";
            stack.Children.Add(Row(key.Length == 0 ? "—" : key, agg.Qty.ToString("0.###", Ru), Money(agg.Revenue), stock is { } st ? st.ToString("0.###", Ru) : "—", share, header: false));
        }
        card.Child = stack;
        return card;
    }

    private Grid Row(string a, string b, string c, string d, string e, bool header)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110,150,110,150") };
        var i = 0;
        foreach (var text in new[] { a, b, c, d, e })
        {
            var t = new TextBlock
            {
                Text = text, FontSize = header ? 12.5 : 14, FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                Margin = new Thickness(0, 2, i == 4 ? 0 : 12, 2),
            };
            Use(t, TextBlock.ForegroundProperty, header ? "BrushTextSoft" : "BrushText");
            Grid.SetColumn(t, i++);
            g.Children.Add(t);
        }
        return g;
    }

    private static double SizeRank(string size) => Dialogs.VariantReceiveWindow.SizeRank(size);

    private static string Money(double v) => v.ToString("N0", Ru) + " " + Tr.T("сом", "сом", "som", "som", "so'm");

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;

    private static double Num(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
