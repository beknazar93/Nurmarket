using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-20 (ТЗ «Сфера Автозапчасти», пакеты импорта): профиль «Прайс поставщика (автозапчасти)» в «Массовой загрузке».
// Логика разбора — AutoPartsPriceImport (Infrastructure); здесь только панель: что узнано, столбцы названия, остаток у существующих,
// итог «будет создано / обновлено / пропущено» и предпросмотр первых 200 строк с причинами пропуска.
public partial class BulkImportWizardWindow
{
    private readonly Border _apBox = new() { IsVisible = false, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _apInfo = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _apSummary = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly WrapPanel _apNameWrap = new() { Orientation = Orientation.Horizontal };
    private readonly CheckBox _apStockCheck = new() { MinHeight = 44, IsChecked = true };
    private readonly StackPanel _apPreviewRows = new() { Spacing = 2 };
    private readonly Border _apPreviewBox = new() { IsVisible = false, MaxHeight = 220, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(8, 4) };
    private Button _apPreviewBtn = null!;
    private AutoPartsPrepared? _ap;
    private bool _apOn;
    private HashSet<int>? _apNameCols;

    /// <summary>Панель профиля — в «Столбцы и параметры» (собирается один раз).</summary>
    private Control BuildAutoPartsBox()
    {
        _apStockCheck.Content = new TextBlock
        {
            Text = T("Обновлять остаток у существующих товаров", "Бар товарлардын калдыгын жаңыртуу", "Update the stock of existing products",
                "Mevcut ürünlerin stokunu güncelle", "Mavjud mahsulotlar qoldig'ini yangilash"),
            TextWrapping = TextWrapping.Wrap,
        };
        _apStockCheck.IsCheckedChanged += (_, _) => ScheduleRecheck();
        _apPreviewBtn = SectionCards.Secondary(T("Предпросмотр: первые 200 строк", "Алдын ала көрүү: алгачкы 200 сап", "Preview: first 200 rows",
            "Önizleme: ilk 200 satır", "Oldindan ko'rish: dastlabki 200 qator"));
        _apPreviewBtn.MinHeight = 44;
        _apPreviewBtn.Click += (_, _) =>
        {
            _apPreviewBox.IsVisible = !_apPreviewBox.IsVisible;
            Avalonia.Threading.Dispatcher.UIThread.Post(Refit, Avalonia.Threading.DispatcherPriority.Loaded);
        };
        _apInfo.Bind(TextBlock.ForegroundProperty, _apInfo.GetResourceObservable("BrushTextSoft"));
        _apSummary.Bind(TextBlock.ForegroundProperty, _apSummary.GetResourceObservable("BrushText"));
        _apBox.Bind(Border.BackgroundProperty, _apBox.GetResourceObservable("BrushPanelSoft"));
        _apBox.Bind(Border.BorderBrushProperty, _apBox.GetResourceObservable("BrushBorder"));
        _apPreviewBox.Bind(Border.BorderBrushProperty, _apPreviewBox.GetResourceObservable("BrushBorder"));
        _apPreviewBox.Child = new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = _apPreviewRows };
        var nameCaption = new TextBlock
        {
            Text = T("Название собрать из столбцов (если в прайсе нет «Наименования» — берутся подходящие сами):",
                "Аталышын мамычалардан чогултуу («Наименование» жок болсо — ылайыктуулары өзү алынат):",
                "Build the name from columns (if the price list has no “Name” column, suitable ones are taken automatically):",
                "Adı sütunlardan oluştur (fiyat listesinde «Ad» sütunu yoksa uygun olanlar otomatik alınır):",
                "Nomni ustunlardan yig'ish (prays-listda «Nomi» ustuni bo'lmasa, mos ustunlar o'zi olinadi):"),
            FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2),
        };
        nameCaption.Bind(TextBlock.ForegroundProperty, nameCaption.GetResourceObservable("BrushTextSoft"));
        _apBox.Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Caption(AutoPartsPriceImport.ProfileName), _apInfo, nameCaption, _apNameWrap, _apStockCheck, _apSummary, _apPreviewBtn, _apPreviewBox,
            },
        };
        return _apBox;
    }

    /// <summary>Включить профиль (сфера «Автозапчасти»): строка заголовков ищется среди первых 30 строк, обычная панель столбцов прячется.</summary>
    private void EnableAutoParts()
    {
        if (_table is null)
            return;
        var header = AutoPartsPriceImport.FindHeaderRow(_table);
        if (header >= 0 && header != _table.HeaderIndex)
            _table.SetHeaderRow(header);
        _apOn = true;
        _apNameCols = null;
        BuildApNameChoices();
        _mapWrap.IsVisible = false;
        _apBox.IsVisible = true;
        _loadingFile = true;
        _cardsCheck.IsChecked = true; // прайс — «карточки и цены»; остаток пишет сам профиль (создание и, по галочке, существующие)
        _cardsCheck.IsEnabled = false;
        _loadingFile = false;
    }

    private void DisableAutoParts()
    {
        _apOn = false;
        _ap = null;
        _mapWrap.IsVisible = true;
        _apBox.IsVisible = false;
        _cardsCheck.IsEnabled = true;
    }

    /// <summary>После чтения файла: в сфере «Автозапчасти» прайс с номером детали и ценой подхватывается сам; иначе профиль выключен.</summary>
    private void AutoSelectAutoParts()
    {
        DisableAutoParts();
        if (_table is not null && AutoPartsProfile.IsActive && AutoPartsPriceImport.LooksLikePrice(_table))
            EnableAutoParts();
    }

    private void BuildApNameChoices()
    {
        _apNameWrap.Children.Clear();
        if (_table is null)
            return;
        var map = AutoPartsPriceImport.Detect(_table.Headers, out var stockCols);
        var def = new HashSet<int>(AutoPartsPriceImport.DefaultNameColumns(_table, map, stockCols));
        var headers = _table.Headers;
        for (var i = 0; i < headers.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(headers[i]))
                continue;
            var col = i;
            var cb = new CheckBox { Content = headers[i].Trim(), IsChecked = def.Contains(i), MinHeight = 44, Margin = new Thickness(0, 0, 12, 0) };
            cb.IsCheckedChanged += (_, _) =>
            {
                _apNameCols = _apNameWrap.Children.OfType<CheckBox>().Select((c, idx) => (c, idx)).Where(x => x.c.IsChecked == true)
                    .Select(x => (int)x.c.Tag!).ToHashSet();
                ScheduleRecheck();
            };
            cb.Tag = col;
            _apNameWrap.Children.Add(cb);
        }
    }

    private AutoPartsPriceOptions ReadApOptions() => new() { NameColumns = _apNameCols, UpdateStock = _apStockCheck.IsChecked == true };

    /// <summary>Подготовить прайс и показать итог и предпросмотр; возвращает таблицу и сопоставление для обычного плана.</summary>
    private async Task<(ProductFileTable Table, ProductImportMapping Mapping)> PrepareAutoPartsAsync(System.Threading.CancellationToken token)
    {
        var table = _table!;
        var options = ReadApOptions();
        var prepared = await Task.Run(() => AutoPartsPriceImport.Prepare(table, options), token).ConfigureAwait(true);
        _ap = prepared;
        ShowAutoPartsSummary(prepared);
        return (prepared.ToTable(), AutoPartsPrepared.TableMapping());
    }

    private void ShowAutoPartsSummary(AutoPartsPrepared p)
    {
        string Col(PartColumn f) => p.Columns.TryGetValue(f, out var c) && _table is not null && c < _table.Headers.Count ? "«" + _table.Headers[c].Trim() + "»" : "—";
        _apInfo.Text = T($"Номер детали: {Col(PartColumn.PartNo)} · бренд: {Col(PartColumn.Brand)} · цена: {Col(PartColumn.Sale)} · закупка: {Col(PartColumn.Purchase)} · остаток: {Col(PartColumn.Stock)}. Строка заголовков: {(_table?.HeaderIndex ?? 0) + 1}.",
            $"Тетик номери: {Col(PartColumn.PartNo)} · бренд: {Col(PartColumn.Brand)} · баасы: {Col(PartColumn.Sale)} · сатып алуу: {Col(PartColumn.Purchase)} · калдык: {Col(PartColumn.Stock)}. Аталыштар сабы: {(_table?.HeaderIndex ?? 0) + 1}.",
            $"Part number: {Col(PartColumn.PartNo)} · brand: {Col(PartColumn.Brand)} · price: {Col(PartColumn.Sale)} · purchase: {Col(PartColumn.Purchase)} · stock: {Col(PartColumn.Stock)}. Header row: {(_table?.HeaderIndex ?? 0) + 1}.",
            $"Parça numarası: {Col(PartColumn.PartNo)} · marka: {Col(PartColumn.Brand)} · fiyat: {Col(PartColumn.Sale)} · alış: {Col(PartColumn.Purchase)} · stok: {Col(PartColumn.Stock)}. Başlık satırı: {(_table?.HeaderIndex ?? 0) + 1}.",
            $"Detal raqami: {Col(PartColumn.PartNo)} · brend: {Col(PartColumn.Brand)} · narx: {Col(PartColumn.Sale)} · xarid: {Col(PartColumn.Purchase)} · qoldiq: {Col(PartColumn.Stock)}. Sarlavha qatori: {(_table?.HeaderIndex ?? 0) + 1}.");
        _apSummary.Text = p.SummaryText;
        _apPreviewRows.Children.Clear();
        foreach (var r in p.Rows.Take(AutoPartsPriceImport.PreviewRows))
        {
            var skipped = r.Status == AutoPartsRowStatus.Skipped;
            var text = $"{r.FileRow}: {(r.Name.Length > 0 ? r.Name : "—")}"
                       + (r.PartNo.Length > 0 ? " · №" + r.PartNo : "") + (r.Brand.Length > 0 ? " · " + r.Brand : "")
                       + (skipped ? " — " + T("пропущено", "өткөрүлдү", "skipped", "atlandı", "o'tkazildi") + ": " + r.SkipReason
                           : (r.Stock is { } s ? " · " + T("ост.", "калд.", "stock", "stok", "qoldiq") + " " + s.ToString("0.###") : "") + " · " + r.Sale.ToString("0.###")
                             + (r.StockNote.Length > 0 ? " · " + r.StockNote : ""));
            var tb = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable(skipped ? "BrushTextSoft" : "BrushText"));
            _apPreviewRows.Children.Add(tb);
        }
    }
}
