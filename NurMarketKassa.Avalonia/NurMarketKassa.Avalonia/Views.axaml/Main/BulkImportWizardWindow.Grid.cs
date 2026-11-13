using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services; // 2026-10-29: PosDialogs (без этого сборка падала)
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-06: В15г — часть класса BulkImportWizardWindow (Grid): перенесено из BulkImportWizardWindow.cs без изменений логики.
public partial class BulkImportWizardWindow
{
    private void ClearResult()
    {
        _rows = [];
        _grid.ItemsSource = null;
        _grid.IsVisible = false;
        _tiles.IsVisible = false;
        _opsPanel.IsVisible = false;
        _loadBtn.IsEnabled = false;
        _errorsBtn.IsEnabled = false;
        _summary.Text = "";
    }

    private void BuildRows()
    {
        if (_plan is null)
            return;
        var m = _mapping;
        var cols = (Barcode: m.ColumnOf(ProductImportField.Barcode), Name: m.ColumnOf(ProductImportField.Name), Qty: m.ColumnOf(ProductImportField.Quantity),
            Purchase: m.ColumnOf(ProductImportField.Purchase), Sale: m.ColumnOf(ProductImportField.Sale), Markup: m.ColumnOf(ProductImportField.Markup), Unit: m.ColumnOf(ProductImportField.Unit));
        _rows = _plan.Rows.Select(r => new ImportRowVm(r, cols.Barcode, cols.Name, cols.Qty, cols.Purchase, cols.Sale, cols.Markup, cols.Unit)).ToList();
        _checkedCount = 0;
        foreach (var vm in _rows)
            vm.PropertyChanged += OnRowChanged;
        _grid.IsVisible = true;
        _tiles.IsVisible = true;
        ApplyFilter();
        Dispatcher.UIThread.Post(Refit, DispatcherPriority.Loaded);
    }

    private int _checkedCount;

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ImportRowVm.IsChecked) || sender is not ImportRowVm vm)
            return;
        _checkedCount += vm.IsChecked ? 1 : -1; // счётчик, а не пересчёт всех строк: «Все» на 15 000 строк не должно виснуть
        UpdateOpsInfo();
    }

    private void ApplyFilter()
    {
        IEnumerable<ImportRowVm> list = _rows;
        list = _filter switch
        {
            Filter.Receipt => list.Where(r => r.Row.Kind == BulkImportRowKind.Receipt),
            Filter.New => list.Where(r => r.Row.Kind == BulkImportRowKind.New),
            Filter.Error => list.Where(r => r.Row.Kind == BulkImportRowKind.Error && (_errorGroup is null || r.Row.ErrorKey == _errorGroup)),
            Filter.Warning => list.Where(r => !string.IsNullOrEmpty(r.Row.Warning)),
            Filter.Merged => list.Where(r => r.Row.Kind == BulkImportRowKind.Merged || r.Row.HasMerged),
            _ => list,
        };
        // 2026-10-30: быстрый поиск по таблице (штрихкод, название, причина).
        if (_searchText.Length > 0)
        {
            var q = _searchText;
            list = list.Where(r => r.Barcode.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || (r.Row.Error ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        _grid.ItemsSource = list.ToList();
        UpdateTiles();
        UpdateErrorGroups();
    }

    /// <summary>2026-10-30: ошибки по типу: «15 412: укажите закупку… → показать». Видны на фильтре «Ошибки».</summary>
    private void UpdateErrorGroups()
    {
        _errGroups.Children.Clear();
        _errGroups.IsVisible = false;
        if (_plan is null || _filter != Filter.Error || _plan.ErrorCount == 0)
            return;
        foreach (var (key, text, count) in _plan.ErrorGroups().Take(8))
        {
            var shown = text.Length > 60 ? text[..60] + "…" : text;
            var b = _errorGroup == key ? SectionCards.Primary("") : SectionCards.Secondary("");
            b.MinHeight = 40;
            b.Margin = new Thickness(0, 0, 8, 6);
            b.Content = T($"{count:N0}: {shown} → показать", $"{count:N0}: {shown} → көрсөтүү", $"{count:N0}: {shown} → show", $"{count:N0}: {shown} → göster", $"{count:N0}: {shown} → ko'rsatish");
            var k = key;
            b.Click += (_, _) =>
            {
                _errorGroup = _errorGroup == k ? null : k;
                ApplyFilter();
            };
            _errGroups.Children.Add(b);
        }
        _errGroups.IsVisible = _errGroups.Children.Count > 0;
    }

    private void UpdateTiles()
    {
        var plan = _plan;
        int all = plan?.Rows.Count ?? 0, receipt = plan?.ReceiptCount ?? 0, created = plan?.NewCount ?? 0, errors = plan?.ErrorCount ?? 0;
        string Mark(Filter f, string text) => _filter == f ? "● " + text : text;
        _tileButtons[Filter.All].Content = Mark(Filter.All, T($"Все {all}", $"Баары {all}", $"All {all}", $"Tümü {all}", $"Hammasi {all}"));
        _tileButtons[Filter.Receipt].Content = Mark(Filter.Receipt, T($"Приход к товарам склада {receipt}", $"Кампадагы товарларга кирешe {receipt}", $"Receipt to stock products {receipt}",
            $"Depo ürünlerine giriş {receipt}", $"Ombor mahsulotlariga kirim {receipt}"));
        _tileButtons[Filter.New].Content = Mark(Filter.New, T($"Новые {created}", $"Жаңылар {created}", $"New {created}", $"Yeni {created}", $"Yangilar {created}"));
        _tileButtons[Filter.Error].Content = Mark(Filter.Error, T($"Ошибки {errors}", $"Каталар {errors}", $"Errors {errors}", $"Hatalar {errors}", $"Xatolar {errors}"));
        var warns = plan?.WarningCount ?? 0;
        var mergedN = plan?.MergedCount ?? 0;
        _tileButtons[Filter.Warning].Content = Mark(Filter.Warning, T($"Предупреждения {warns}", $"Эскертүүлөр {warns}", $"Warnings {warns}", $"Uyarılar {warns}", $"Ogohlantirishlar {warns}"));
        _tileButtons[Filter.Merged].Content = Mark(Filter.Merged, T($"Объединено {mergedN}", $"Бириктирилди {mergedN}", $"Merged {mergedN}", $"Birleştirilen {mergedN}", $"Birlashtirilgan {mergedN}"));
        _tileButtons[Filter.Merged].IsVisible = mergedN > 0;
        if (plan?.CardsOnly == true)
            _tileButtons[Filter.Receipt].Content = Mark(Filter.Receipt, T($"Обновить товары склада {receipt - plan.UnchangedCount} (без изменений {plan.UnchangedCount})", $"Кампадагы товарларды жаңыртуу {receipt - plan.UnchangedCount} (өзгөрүүсүз {plan.UnchangedCount})",
                $"Update stock products {receipt - plan.UnchangedCount} (unchanged {plan.UnchangedCount})", $"Depo ürünlerini güncelle {receipt - plan.UnchangedCount} (değişmeyen {plan.UnchangedCount})", $"Ombor mahsulotlarini yangilash {receipt - plan.UnchangedCount} (o'zgarishsiz {plan.UnchangedCount})"));
        var ready = plan?.ReadyCount ?? 0;
        _summary.Text = plan is null || plan.Rows.Count == 0
            ? ""
            : T($"Строк готово к добавлению: {ready}, с ошибками: {errors}" + (plan.MergedCount > 0 ? $", повторов сложено: {plan.MergedCount}" : ""),
                $"Кошууга даяр саптар: {ready}, каталуу: {errors}" + (plan.MergedCount > 0 ? $", кайталоолор кошулду: {plan.MergedCount}" : ""),
                $"Rows ready to add: {ready}, with errors: {errors}" + (plan.MergedCount > 0 ? $", repeats added up: {plan.MergedCount}" : ""),
                $"Eklemeye hazır satır: {ready}, hatalı: {errors}" + (plan.MergedCount > 0 ? $", birleştirilen tekrarlar: {plan.MergedCount}" : ""),
                $"Qo'shishga tayyor qatorlar: {ready}, xatoli: {errors}" + (plan.MergedCount > 0 ? $", qo'shilgan takrorlar: {plan.MergedCount}" : ""));
        _loadBtn.Content = ready > 0 ? T($"Загрузить {ready}", $"Жүктөө {ready}", $"Load {ready}", $"Yükle {ready}", $"Yuklash {ready}") : T("Загрузить", "Жүктөө", "Load", "Yükle", "Yuklash");
        var canLoad = ready > 0 && !_started;
        // 2026-10-30: журнал знает часть строк — кнопка «Продолжить загрузку N» (остаток), а если всё уже записано — недоступна (повтор не прибавит остаток второй раз).
        if (ready > 0 && _journalDone > 0)
        {
            var left = LeftToLoad();
            _loadBtn.Content = left > 0
                ? T($"Продолжить загрузку {left}", $"Жүктөөнү улантуу {left}", $"Continue loading {left}", $"Yüklemeye devam et {left}", $"Yuklashni davom ettirish {left}")
                : T("Уже загружено", "Мурунтан жүктөлгөн", "Already loaded", "Zaten yüklendi", "Allaqachon yuklangan");
            canLoad = canLoad && left > 0 && _journal?.ReceiptBatchId is null;
        }
        _loadBtn.IsEnabled = canLoad;
        _errorsBtn.IsEnabled = errors > 0;
    }

    // ------------------------------------------------------------------ таблица и правки

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var b) && b is IBrush brush ? brush : null;

    private void BuildGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.CanUserSortColumns = false;
        _grid.CanUserReorderColumns = false;
        _grid.RowHeight = 46;
        _grid.IsReadOnly = false;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        _grid.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;

        _grid.Columns.Add(new DataGridCheckBoxColumn { Header = H(""), Width = new DataGridLength(46), Binding = new Binding(nameof(ImportRowVm.IsChecked)) { Mode = BindingMode.TwoWay } });
        _grid.Columns.Add(new DataGridTextColumn { Header = H("№"), Width = new DataGridLength(56), IsReadOnly = true, Binding = new Binding(nameof(ImportRowVm.FileRow)) });
        _grid.Columns.Add(Column(T("Штрихкод", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"), 150, nameof(ImportRowVm.Barcode), ProductImportField.Barcode, readOnly: true));
        _grid.Columns.Add(Column(T("Название", "Аталышы", "Name", "Ad", "Nomi"), 220, nameof(ImportRowVm.Name), ProductImportField.Name, star: true));
        _grid.Columns.Add(Column(T("Единица", "Бирдик", "Unit", "Birim", "Birlik"), 104, nameof(ImportRowVm.UnitText), ProductImportField.Unit));
        _grid.Columns.Add(Column(T("Кол-во", "Саны", "Qty", "Miktar", "Soni"), 90, nameof(ImportRowVm.QuantityText), ProductImportField.Quantity, comma: true));
        _grid.Columns.Add(Column(T("Закупка", "Сатып алуу", "Purchase", "Alış", "Xarid"), 100, nameof(ImportRowVm.PurchaseText), ProductImportField.Purchase, comma: true));
        _grid.Columns.Add(Column(T("Наценка %", "Үстөк %", "Markup %", "Kâr %", "Ustama %"), 112, nameof(ImportRowVm.MarkupText), ProductImportField.Markup, comma: true));
        _grid.Columns.Add(Column(T("Цена продажи", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotuv narxi"), 112, nameof(ImportRowVm.SaleText), ProductImportField.Sale, comma: true));
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = H(T("Что будет / ошибка", "Эмне болот / ката", "What happens / error", "Ne olacak / hata", "Nima bo'ladi / xato")),
            Width = new DataGridLength(260), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<ImportRowVm>((_, _) => new RowCell(this, vm => vm.StatusText, null, status: true), supportsRecycling: false),
        });

        _grid.BeginningEdit += (_, e) =>
        {
            if (e.Row.DataContext is not ImportRowVm vm || _started)
            {
                e.Cancel = true;
                return;
            }
            var header = (e.Column.Header as TextBlock)?.Text;
            // Править можно только строки, у которых есть строка прихода; название и единицу — только у новых товаров (у товара склада они в его карточке).
            if (vm.Row.Line is null || vm.Row.Kind == BulkImportRowKind.Merged
                || ((e.Column.Tag as string is nameof(ImportRowVm.Name) or nameof(ImportRowVm.UnitText)) && !vm.Row.Line.IsNew))
                e.Cancel = true;
            _ = header;
        };
        _grid.CellEditEnded += (_, e) =>
        {
            if (e.Row.DataContext is ImportRowVm vm)
                Dispatcher.UIThread.Post(() => AfterEdit(vm), DispatcherPriority.Background);
        };
    }

    private DataGridColumn Column(string header, double width, string path, ProductImportField field, bool readOnly = false, bool comma = false, bool star = false)
    {
        return new DataGridTemplateColumn
        {
            Header = H(header),
            Tag = path,
            IsReadOnly = readOnly,
            Width = star ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
            MinWidth = star ? 160 : width,
            MaxWidth = star ? 460 : double.PositiveInfinity, // 2026-11-13: название — не шире 460 px (жалоба владельца «в массовой загрузке наименование очень большое»)
            CellTemplate = new FuncDataTemplate<ImportRowVm>((_, _) => new RowCell(this, vm => vm.Get(path), field, status: false), supportsRecycling: false),
            CellEditingTemplate = new FuncDataTemplate<ImportRowVm>((_, _) =>
            {
                var box = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, MinHeight = 40 };
                var binding = new Binding(path) { Mode = BindingMode.TwoWay };
                if (comma)
                    binding.Converter = WarehouseWindow.CommaDecimal;
                box.Bind(TextBox.TextProperty, binding);
                return box;
            }, supportsRecycling: false),
        };
    }

    /// <summary>После правки ячейки: строки заново проверяются (BulkImportPlan.Recompute), плитки и кнопка «Загрузить N» обновляются.</summary>
    private void AfterEdit(ImportRowVm vm)
    {
        if (_plan is null)
            return;
        _plan.Recompute();
        foreach (var row in _rows)
            row.RaiseAll();
        UpdateTiles();
        if (_filter != Filter.All && !MatchesFilter(vm))
            ApplyFilter();
    }

    private bool MatchesFilter(ImportRowVm vm) => _filter switch
    {
        Filter.Receipt => vm.Row.Kind == BulkImportRowKind.Receipt,
        Filter.New => vm.Row.Kind == BulkImportRowKind.New,
        Filter.Error => vm.Row.Kind == BulkImportRowKind.Error,
        Filter.Warning => !string.IsNullOrEmpty(vm.Row.Warning),
        Filter.Merged => vm.Row.Kind == BulkImportRowKind.Merged,
        _ => true,
    };

    // ------------------------------------------------------------------ действия над отмеченными строками

    private UnitPickerBox _opsUnit = null!;
    private TextBox _opsMarkup = null!, _opsPrice = null!, _opsQty = null!;

    private void BuildOpsPanel()
    {
        var all = new CheckBox { Content = T("Все", "Баары", "All", "Tümü", "Hammasi"), MinHeight = 44, VerticalAlignment = VerticalAlignment.Center };
        all.IsCheckedChanged += (_, _) =>
        {
            foreach (var r in (_grid.ItemsSource as IEnumerable<ImportRowVm>) ?? _rows)
                r.IsChecked = all.IsChecked == true;
        };
        _opsUnit = new UnitPickerBox { Width = 190, SecondaryClass = "modal-secondary", PrimaryClass = "modal-primary" };
        var unitApply = SectionCards.Secondary(T("Установить единицу", "Бирдикти коюу", "Set unit", "Birimi ayarla", "Birlikni qo'yish"));
        unitApply.Click += (_, _) => ApplyToChecked("unit", _opsUnit.Value);
        _opsMarkup = new TextBox { MinHeight = 44, Width = 100, Watermark = T("Наценка, %", "Үстөк, %", "Markup, %", "Kâr, %", "Ustama, %"), VerticalContentAlignment = VerticalAlignment.Center };
        var markupApply = SectionCards.Secondary(T("Установить наценку", "Үстөктү коюу", "Set markup", "Kârı ayarla", "Ustamani qo'yish"));
        markupApply.Click += (_, _) => ApplyToChecked("markup", _opsMarkup.Text ?? "");
        _opsPrice = new TextBox { MinHeight = 44, Width = 120, Watermark = T("Цена продажи", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotuv narxi"), VerticalContentAlignment = VerticalAlignment.Center };
        var priceApply = SectionCards.Secondary(T("Установить цену", "Баа коюу", "Set price", "Fiyatı ayarla", "Narxni qo'yish"));
        priceApply.Click += (_, _) => ApplyToChecked("price", _opsPrice.Text ?? "");
        _opsQty = new TextBox { MinHeight = 44, Width = 100, Watermark = T("Количество", "Саны", "Quantity", "Miktar", "Miqdor"), VerticalContentAlignment = VerticalAlignment.Center };
        var qtyApply = SectionCards.Secondary(T("Установить количество", "Санды коюу", "Set quantity", "Miktarı ayarla", "Miqdorni qo'yish"));
        qtyApply.Click += (_, _) => ApplyToChecked("qty", _opsQty.Text ?? "");
        foreach (var b in new[] { unitApply, markupApply, priceApply, qtyApply })
            b.MinHeight = 44;

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new Control[] { all, Caption(T("Для отмеченных строк:", "Белгиленген саптар үчүн:", "For the ticked rows:", "İşaretli satırlar için:", "Belgilangan qatorlar uchun:")),
                     _opsUnit, unitApply, _opsMarkup, markupApply, _opsPrice, priceApply, _opsQty, qtyApply })
        {
            c.Margin = new Thickness(0, 0, 8, 6);
            wrap.Children.Add(c);
        }
        _opsPanel.Child = new StackPanel { Spacing = 2, Children = { wrap, _opsInfo } };
        UpdateOpsInfo();
    }

    private void UpdateOpsInfo()
    {
        var n = _checkedCount;
        _opsInfo.Text = n == 0
            ? T("Отметьте строки галочками в первом столбце — и нажмите «Установить …». Ячейки таблицы можно править двойным щелчком.",
                "Биринчи мамычада саптарды белгилеңиз — «… коюу» басыңыз. Таблицанын уячейкаларын эки жолу чыкылдатып оңдоого болот.",
                "Tick rows in the first column, then press “Set …”. Table cells can be edited with a double click.",
                "İlk sütunda satırları işaretleyin ve «… ayarla»ya basın. Tablo hücreleri çift tıklayarak düzenlenir.",
                "Birinchi ustunda qatorlarni belgilang va «… qo'yish»ni bosing. Jadval kataklarini ikki marta bosib tahrirlash mumkin.")
            : T($"Отмечено строк: {n}.", $"Белгиленген саптар: {n}.", $"Rows ticked: {n}.", $"İşaretli satır: {n}.", $"Belgilangan qatorlar: {n}.");
    }

    private void ApplyToChecked(string what, string text)
    {
        if (_plan is null || _started)
            return;
        var rows = _rows.Where(r => r.IsChecked && r.Row.Line is not null && r.Row.Kind != BulkImportRowKind.Merged).ToList();
        if (rows.Count == 0)
        {
            _opsInfo.Text = T("Сначала отметьте строки (с готовой строкой прихода) галочками.", "Адегенде саптарды белгилеңиз.", "Tick some rows first.", "Önce satırları işaretleyin.", "Avval qatorlarni belgilang.");
            return;
        }
        double number = 0;
        if (what != "unit")
        {
            var decimals = what == "qty" ? 3 : 2;
            var parse = CheckoutValidation.TryParseAmount(text, out number, decimals);
            if (parse != CheckoutValidation.AmountParse.Ok || (what != "markup" && number <= 0))
            {
                _opsInfo.Text = parse == CheckoutValidation.AmountParse.Empty || (parse == CheckoutValidation.AmountParse.Ok && number <= 0)
                    ? T("Впишите число больше нуля.", "Нөлдөн чоң сан жазыңыз.", "Enter a number greater than zero.", "Sıfırdan büyük bir sayı girin.", "Noldan katta son kiriting.")
                    : CheckoutValidation.AmountProblemText(parse, decimals) ?? "";
                return;
            }
        }

        int ok = 0, skipped = 0;
        var inv = CultureInfo.InvariantCulture;
        foreach (var vm in rows)
        {
            var line = vm.Row.Line!;
            switch (what)
            {
                case "unit":
                    if (vm.Row.SetUnit(text))
                        ok++;
                    else
                        skipped++;
                    break;
                case "markup":
                    if (line.PurchasePrice > 0)
                    {
                        line.MarkupText = number.ToString("0.##", inv);
                        ok++;
                    }
                    else
                        skipped++;
                    break;
                case "price":
                    line.SetSaleText(number.ToString("0.##", inv));
                    ok++;
                    break;
                default:
                    line.QuantityText = number.ToString("0.###", inv);
                    ok++;
                    break;
            }
        }
        _plan.Recompute();
        foreach (var row in _rows)
            row.RaiseAll();
        UpdateTiles();
        ApplyFilterIfNeeded();
        var skipText = skipped > 0
            ? T($", пропущено {skipped} ({(what == "unit" ? "товары склада" : "нет закупки")})", $", {skipped} өткөрүлдү", $", skipped {skipped}", $", {skipped} atlandı", $", {skipped} o'tkazildi")
            : "";
        _opsInfo.Text = T($"Готово: изменено {ok} строк{skipText}.", $"Даяр: {ok} сап өзгөрдү{skipText}.", $"Done: {ok} rows changed{skipText}.", $"Tamam: {ok} satır değişti{skipText}.", $"Tayyor: {ok} qator o'zgardi{skipText}.");
    }

    private void ApplyFilterIfNeeded()
    {
        if (_filter != Filter.All)
            ApplyFilter();
    }

    // ------------------------------------------------------------------ скачать ошибки и загрузить

    private async Task SaveErrorsAsync()
    {
        if (_plan is null || _plan.ErrorCount == 0)
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Сохранить строки с ошибками", "Катасы бар саптарды сактоо", "Save rows with errors", "Hatalı satırları kaydet", "Xatoli qatorlarni saqlash"),
            SuggestedFileName = "import_errors.csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (file is null)
            return;
        var path = file.TryGetLocalPath() ?? file.Name;
        try
        {
            var bytes = ProductImportTemplate.CsvBytes(_plan.BuildErrorsCsv());
            if (file.TryGetLocalPath() is { } localPath)
                await File.WriteAllBytesAsync(localPath, bytes).ConfigureAwait(true);
            else
            {
                // 2026-10-29: Android — без локального пути пишем потоком провайдера файлов.
                await using var output = await file.OpenWriteAsync().ConfigureAwait(true);
                if (output.CanSeek)
                    output.SetLength(0);
                await output.WriteAsync(bytes).ConfigureAwait(true);
            }
            _status.Text = T($"Строки с ошибками сохранены: {path}. Исправьте их и загрузите файл снова.", $"Катасы бар саптар сакталды: {path}. Оңдоп, файлды кайра жүктөңүз.",
                $"Rows with errors saved: {path}. Fix them and load the file again.", $"Hatalı satırlar kaydedildi: {path}. Düzeltip dosyayı yeniden yükleyin.", $"Xatoli qatorlar saqlandi: {path}. Tuzatib, faylni qayta yuklang.");
        }
        catch (Exception ex)
        {
            _status.Text = T($"Не удалось сохранить: {ex.Message}", $"Сактоо мүмкүн болгон жок: {ex.Message}", $"Could not save: {ex.Message}", $"Kaydedilemedi: {ex.Message}", $"Saqlab bo'lmadi: {ex.Message}");
        }
    }

}
