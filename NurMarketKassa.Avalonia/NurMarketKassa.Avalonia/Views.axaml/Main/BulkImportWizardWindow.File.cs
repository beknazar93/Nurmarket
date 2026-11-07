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

// 2026-11-06: В15г — часть класса BulkImportWizardWindow (File): перенесено из BulkImportWizardWindow.cs без изменений логики.
public partial class BulkImportWizardWindow
{
    // ------------------------------------------------------------------ шаг 1: файл

    private async Task PickFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Выберите файл с товарами", "Товарлары бар файлды тандаңыз", "Choose a file with products", "Ürünlerin bulunduğu dosyayı seçin", "Mahsulotlar bor faylni tanlang"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Excel / CSV") { Patterns = ["*.xlsx", "*.csv", "*.txt"] },
            ],
        });
        if (files.Count == 0)
            return;
        var path = files[0].TryGetLocalPath() ?? await CopyToTempAsync(files[0]).ConfigureAwait(true);
        if (path is null)
            return;
        await LoadPathAsync(path).ConfigureAwait(true);
    }

    private async Task PasteAsync()
    {
        string? text = null;
        try
        {
            if (Clipboard is { } clip)
                text = await clip.GetTextAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовая загрузка: буфер обмена не прочитан: {ex.Message}", "WARNING");
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            _status.Text = T("В буфере обмена нет таблицы. Скопируйте ячейки в Excel (Ctrl+C) и нажмите «Вставить из буфера».",
                "Буферде таблица жок. Excel'де уячейкаларды көчүрүп (Ctrl+C), «Буферден коюу» басыңыз.",
                "There is no table in the clipboard. Copy the cells in Excel (Ctrl+C) and press “Paste from clipboard”.",
                "Panoda tablo yok. Excel'de hücreleri kopyalayın (Ctrl+C) ve «Panodan yapıştır»a basın.",
                "Buferda jadval yo'q. Excel'da kataklarni nusxalang (Ctrl+C) va «Buferdan qo'yish»ni bosing.");
            return;
        }
        await LoadResultAsync(ProductFileReader.ReadClipboard(text), T("из буфера обмена", "буферден", "from the clipboard", "panodan", "buferdan")).ConfigureAwait(true);
    }

    /// <summary>Прочитать файл и сразу проверить (стенд зовёт напрямую).</summary>
    public async Task LoadPathAsync(string path)
    {
        _status.Text = T("Читаю файл…", "Файл окулууда…", "Reading the file…", "Dosya okunuyor…", "Fayl o'qilmoqda…");
        var result = await Task.Run(() => ProductFileReader.ReadFile(path)).ConfigureAwait(true);
        await LoadResultAsync(result, Path.GetFileName(path)).ConfigureAwait(true);
    }

    private async Task LoadResultAsync(ProductFileReadResult result, string name)
    {
        if (_started)
            return;
        if (!result.Ok || result.Table is null)
        {
            _table = null;
            _plan = null;
            _fileInfo.Text = "";
            _fileWarn.IsVisible = true;
            _fileWarn.Text = result.Error ?? "";
            _status.Text = "";
            _mapPanel.IsVisible = false;
            ClearResult();
            return;
        }

        _table = result.Table;
        _fileName = name;
        _fileWarn.IsVisible = result.Table.Warnings.Count > 0;
        _fileWarn.Text = string.Join(" ", result.Table.Warnings);
        _mapping = ProductImportMapping.Resolve(_table.Headers, _table); // 2026-10-30: с проверкой по данным столбцов
        _loadingFile = true;
        _cardsCheck.IsChecked = !_mapping.HasQuantity; // нет столбца количества — «только карточки и цены» по умолчанию
        _loadingFile = false;
        _hintPanel.IsVisible = false;
        var sheet = _table.SheetName is { Length: > 0 } s ? T($" · лист «{s}»", $" · «{s}» барагы", $" · sheet “{s}”", $" · «{s}» sayfası", $" · «{s}» varag'i") : "";
        var enc = _table.EncodingName is { Length: > 0 } e ? $" · {e}" : "";
        _fileInfo.Text = T($"«{name}»{sheet}{enc} · строк: {_table.DataRowCount}, столбцов: {_table.ColumnCount}",
            $"«{name}»{sheet}{enc} · саптар: {_table.DataRowCount}, мамычалар: {_table.ColumnCount}",
            $"“{name}”{sheet}{enc} · rows: {_table.DataRowCount}, columns: {_table.ColumnCount}",
            $"«{name}»{sheet}{enc} · satır: {_table.DataRowCount}, sütun: {_table.ColumnCount}",
            $"«{name}»{sheet}{enc} · qatorlar: {_table.DataRowCount}, ustunlar: {_table.ColumnCount}");
        BuildMappingChoices();
        _mapPanel.IsVisible = true; // развёрнута до «Проверить», потом сворачивается
        await CheckAsync().ConfigureAwait(true);
    }

    private async Task SaveTemplateAsync(bool xlsx)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = xlsx ? T("Сохранить шаблон Excel", "Excel үлгүсүн сактоо", "Save Excel template", "Excel şablonunu kaydet", "Excel shablonini saqlash")
                : T("Сохранить шаблон CSV", "CSV үлгүсүн сактоо", "Save CSV template", "CSV şablonunu kaydet", "CSV shablonini saqlash"),
            SuggestedFileName = xlsx ? "products_template.xlsx" : "products_template.csv",
            FileTypeChoices = [xlsx ? new FilePickerFileType("Excel") { Patterns = ["*.xlsx"] } : new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (file is null)
            return;
        var path = file.TryGetLocalPath();
        try
        {
            // 2026-10-29: Android — локального пути нет: шаблон собираем во временный файл и копируем в выбранное место потоком.
            var target = path ?? Path.Combine(Path.GetTempPath(), xlsx ? "products_template.xlsx" : "products_template.csv");
            if (xlsx)
                ProductImportTemplate.SaveXlsx(target);
            else
                ProductImportTemplate.SaveCsv(target);
            if (path is null)
            {
                await using var output = await file.OpenWriteAsync().ConfigureAwait(true);
                if (output.CanSeek)
                    output.SetLength(0);
                await using var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                await input.CopyToAsync(output).ConfigureAwait(true);
                path = file.Name;
            }
            _status.Text = T($"Шаблон сохранён: {path}", $"Үлгү сакталды: {path}", $"Template saved: {path}", $"Şablon kaydedildi: {path}", $"Shablon saqlandi: {path}");
        }
        catch (Exception ex)
        {
            _status.Text = T($"Не удалось сохранить шаблон: {ex.Message}", $"Үлгүнү сактоо мүмкүн болгон жок: {ex.Message}", $"Could not save the template: {ex.Message}",
                $"Şablon kaydedilemedi: {ex.Message}", $"Shablonni saqlab bo'lmadi: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ шаг 2: столбцы

    private static readonly ProductImportField[] MappedFields =
    [
        ProductImportField.Barcode, ProductImportField.Name, ProductImportField.AltBarcodes, ProductImportField.Quantity, ProductImportField.Unit, ProductImportField.Purchase,
        ProductImportField.Markup, ProductImportField.Sale, ProductImportField.Category, ProductImportField.Weight,
    ];

    private static bool IsRequired(ProductImportField f) => f is ProductImportField.Barcode; // 2026-10-30: количество необязательно

    private static string ColumnLetter(int index)
    {
        var s = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
            s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    private void BuildMappingChoices()
    {
        _mapWrap.Children.Clear();
        _choices.Clear();
        if (_table is null)
            return;
        var headers = _table.Headers;
        var items = new List<string>();
        for (var i = 0; i < headers.Count; i++)
            items.Add(string.IsNullOrWhiteSpace(headers[i]) ? ColumnLetter(i) : $"{ColumnLetter(i)}: {headers[i].Trim()}");
        var sample = _table.DataRows.FirstOrDefault();
        foreach (var field in MappedFields)
        {
            var choice = new ColumnChoice(items, T("— не загружать —", "— жүктөбөө —", "— do not load —", "— yükleme —", "— yuklamaslik —"))
            {
                Width = 230,
                Selected = _mapping.ColumnOf(field),
            };
            var f = field;
            choice.SelectedChanged += col =>
            {
                _mapping.Set(f, col);
                SyncChoices();
                if (!_mapping.HasQuantity)
                {
                    _loadingFile = true;
                    _cardsCheck.IsChecked = true;
                    _loadingFile = false;
                }
                ScheduleRecheck(); // 2026-10-30: проверка заново сама (с задержкой), не вручную
            };
            _choices[field] = choice;
            var label = ProductImportMapping.FieldLabel(field) + (IsRequired(field) ? " *" : "");
            // 2026-10-30: «Уверенность» автоподбора; спорные (угадано по данным или по части заголовка) подсвечиваются.
            var conf = _mapping.ConfidenceOf(field);
            if (conf != MappingConfidence.None)
                label += conf switch
                {
                    MappingConfidence.High => "",
                    MappingConfidence.Medium => T("  · уверенность 70 % — проверьте", "  · ишеним 70 % — текшериңиз", "  · confidence 70% — check", "  · güven %70 — kontrol edin", "  · ishonch 70 % — tekshiring"),
                    _ => T("  · уверенность 40 % — проверьте!", "  · ишеним 40 % — текшериңиз!", "  · confidence 40% — check!", "  · güven %40 — kontrol edin!", "  · ishonch 40 % — tekshiring!"),
                };
            var caption = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 0, 0, 2) };
            caption.Bind(TextBlock.ForegroundProperty, caption.GetResourceObservable(conf is MappingConfidence.Medium or MappingConfidence.Low ? "BrushDanger" : "BrushTextSoft"));
            var stack = new StackPanel { Margin = new Thickness(0, 6, 12, 4), Children = { caption, choice } };
            _mapWrap.Children.Add(stack);
        }
        _ = sample;
    }

    private void SyncChoices()
    {
        foreach (var (field, choice) in _choices)
            choice.Selected = _mapping.ColumnOf(field);
    }

    // ------------------------------------------------------------------ шаг 3: проверка

    private BulkImportSettings ReadSettings()
    {
        double? markup = null;
        if (CheckoutValidation.TryParseAmount(_markupBox.Text, out var m, 2) == CheckoutValidation.AmountParse.Ok && m >= 0)
            markup = m;
        return new BulkImportSettings
        {
            DefaultMarkupPercent = markup,
            Duplicates = _dupFirst.IsChecked == true ? BulkImportDuplicateMode.FirstRow : BulkImportDuplicateMode.SumQuantity,
            DefaultUnit = _defUnitCheck.IsChecked == true ? _defUnit.Value : null,
            UseReceivingMarkup = true,
            CardsOnly = _cardsCheck.IsChecked == true || !_mapping.HasQuantity,
            MergeSameName = _mergeNameCheck.IsChecked == true,
        };
    }

    /// <summary>Проверить файл заново (после смены столбцов или параметров). Вызывается и автоматически после выбора файла.</summary>
    public async Task CheckAsync(bool collapse = false)
    {
        if (_table is null || _building || _started)
            return;
        _building = true;
        _checkBtn.IsEnabled = false;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        try
        {
            var total = _table.DataRowCount;
            if (RefreshCatalogBeforeCheck && BulkImportPlan.CatalogNeedsRefresh)
            {
                _status.Text = T("Обновляю каталог товаров…", "Товарлар каталогу жаңыртылууда…", "Refreshing the product catalog…", "Ürün kataloğu güncelleniyor…", "Mahsulotlar katalogi yangilanmoqda…");
                try
                {
                    await CatalogCacheService.RefreshFromApiAsync().WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    PosLogger.Log($"Массовая загрузка: каталог перед проверкой не обновлён: {ex.Message}", "WARNING");
                }
            }

            _bar.IsVisible = total > 500;
            _bar.Maximum = Math.Max(1, total);
            var progress = new Progress<int>(n =>
            {
                _bar.Value = n;
                _status.Text = T($"Разбор {n:N0} из {total:N0}…", $"Талдоо {n:N0} / {total:N0}…", $"Parsing {n:N0} of {total:N0}…", $"Ayrıştırma {n:N0} / {total:N0}…", $"Tahlil {n:N0} / {total:N0}…");
            });
            var plan = await BulkImportPlan.BuildAsync(_table, _mapping, ReadSettings(), null, progress, token).ConfigureAwait(true);
            _bar.IsVisible = false;
            _plan = plan;
            if (plan.Problem is { } problem)
            {
                _status.Text = problem;
                ClearResult();
                return;
            }
            RefreshJournal();
            BuildRows();
            _status.Text = ResumeNote();
            // 2026-10-30: панель столбцов сворачивается только по кнопке «Проверить» — чтобы владелец успел посмотреть подбор столбцов.
            if (collapse)
            {
                _mapPanel.IsVisible = false;
                _opsPanel.IsVisible = false;
            }
            UpdateToggles();
            UpdateModeNote();
            Dispatcher.UIThread.Post(Refit, DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException)
        {
            // окно закрыли или запустили проверку заново
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовая загрузка: проверка файла не удалась: {ex}", "WAREHOUSE");
            _status.Text = T($"Не удалось проверить файл: {ex.Message}", $"Файлды текшерүү мүмкүн болгон жок: {ex.Message}", $"Could not check the file: {ex.Message}",
                $"Dosya kontrol edilemedi: {ex.Message}", $"Faylni tekshirib bo'lmadi: {ex.Message}");
        }
        finally
        {
            _building = false;
            _checkBtn.IsEnabled = true;
            _bar.IsVisible = false;
        }
    }

    /// <summary>2026-10-30 (приёмка 1.17.65): найти журнал этого файла (хэш содержимого + режим). Пачка прихода, закончившаяся пока окно было закрыто,
    /// сворачивается в «записано» здесь же. Журнал не открылся — работаем без него (как раньше).</summary>
    private void RefreshJournal()
    {
        _journal = null;
        _journalDone = 0;
        if (_table is null || _plan is null || _plan.Problem is not null)
            return;
        try
        {
            var mode = _plan.CardsOnly ? "cards" : "receipt";
            var j = BulkImportJournal.Open(BulkImportJournal.HashOf(_table, mode), mode, _fileName);
            if (j.ReceiptBatchId is { } id)
                j.FoldReceipt(BulkReceivingFeeder.Instance.Get(id));
            _journal = j;
            _journalDone = j.DoneCount == 0 ? 0 : _plan.Lines.Count(l => j.IsDone(l.Barcode));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовая загрузка: журнал файла не открыт: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Сколько строк ещё осталось записать (режим карточек — по списку загрузчика, режим прихода — по строкам прихода).</summary>
    private int LeftToLoad()
    {
        var plan = _plan;
        if (plan is null)
            return 0;
        if (plan.CardsOnly)
        {
            var sel = BulkCardsLoader.Select(plan);
            return _journalDone == 0 ? sel.Count : sel.Count(r => r.Line is { } l && !(_journal?.IsDone(l.Barcode) ?? false));
        }
        return Math.Max(0, plan.ReadyCount - _journalDone);
    }

    private string ResumeNote()
    {
        if (_journal is null || _journalDone == 0 || _plan is null)
            return "";
        if (_journal.ReceiptBatchId is not null)
            return T("Этот файл ещё загружается в фоне — дождитесь итога.", "Бул файл дагы фондо жүктөлүп жатат — жыйынтыкты күтүңүз.", "This file is still loading in the background — wait for the result.",
                "Bu dosya hâlâ arka planda yükleniyor — sonucu bekleyin.", "Bu fayl hali fonda yuklanmoqda — natijani kuting.");
        if (LeftToLoad() == 0)
            return T($"Этот файл уже загружен ({_journal.UpdatedAt:dd.MM.yyyy HH:mm}). Повторно загружать не нужно — иначе остаток прибавится ещё раз.",
                $"Бул файл жүктөлгөн ({_journal.UpdatedAt:dd.MM.yyyy HH:mm}). Кайра жүктөөнүн кереги жок — болбосо калдык дагы бир жолу кошулат.",
                $"This file is already loaded ({_journal.UpdatedAt:dd.MM.yyyy HH:mm}). No need to load it again — the stock would be added twice.",
                $"Bu dosya zaten yüklendi ({_journal.UpdatedAt:dd.MM.yyyy HH:mm}). Tekrar yüklemeye gerek yok — stok iki kez eklenir.",
                $"Bu fayl allaqachon yuklangan ({_journal.UpdatedAt:dd.MM.yyyy HH:mm}). Qayta yuklash shart emas — qoldiq ikki marta qo'shiladi.");
        return T($"Прошлая загрузка этого файла не закончена: записано {_journalDone}. Нажмите «Продолжить загрузку» — уже записанное пропустится.",
            $"Бул файлдын мурунку жүктөөсү бүтө элек: жазылды {_journalDone}. «Жүктөөнү улантуу» басыңыз — жазылганы өткөрүлөт.",
            $"The previous load of this file did not finish: {_journalDone} written. Press “Continue loading” — what is written will be skipped.",
            $"Bu dosyanın önceki yüklemesi bitmedi: yazılan {_journalDone}. «Yüklemeye devam et»e basın — yazılanlar atlanır.",
            $"Bu faylning oldingi yuklashi tugamagan: yozildi {_journalDone}. «Yuklashni davom ettirish»ni bosing — yozilganlari o'tkaziladi.");
    }

    /// <summary>2026-10-30: проверка заново после смены столбцов/параметров — сама, через 0,7 с после последней правки (дебаунс). Идёт проверка —
    /// отменяется и начинается новая.</summary>
    private void ScheduleRecheck()
    {
        if (_loadingFile || _table is null || _started)
            return;
        _recheckTimer ??= new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _recheckTimer.Tick -= OnRecheckTick;
        _recheckTimer.Tick += OnRecheckTick;
        _recheckTimer.Stop();
        _recheckTimer.Start();
    }

    private async void OnRecheckTick(object? sender, EventArgs e)
    {
        _recheckTimer?.Stop();
        if (_table is null || _started)
            return;
        if (_building)
        {
            _cts?.Cancel(); // текущая проверка устарела
            _recheckTimer?.Start();
            return;
        }
        await GuardAsync(() => CheckAsync(), "проверка").ConfigureAwait(true);
    }

    private Avalonia.Threading.DispatcherTimer? _refilterTimer;

    private void ScheduleRefilter()
    {
        _refilterTimer ??= new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _refilterTimer.Tick -= OnRefilterTick;
        _refilterTimer.Tick += OnRefilterTick;
        _refilterTimer.Stop();
        _refilterTimer.Start();
    }

    private void OnRefilterTick(object? sender, EventArgs e)
    {
        _refilterTimer?.Stop();
        if (_plan is not null && _rows.Count > 0)
            ApplyFilter();
    }

    private void UpdateModeNote()
    {
        var cards = _plan?.CardsOnly == true;
        _modeNote.IsVisible = _table is not null;
        _modeNote.Text = cards
            ? (_mapping.HasQuantity
                ? T("Режим «только карточки и цены»: количества из файла игнорируются — остаток не изменится.", "«Карточкалар жана баалар гана» режими: файлдагы сандар эске алынбайт — калдык өзгөрбөйт.",
                    "“Cards and prices only” mode: quantities in the file are ignored — the stock does not change.", "«Yalnızca kart ve fiyatlar» modu: dosyadaki miktarlar yok sayılır — stok değişmez.", "«Faqat kartochkalar va narxlar» rejimi: fayldagi miqdorlar hisobga olinmaydi — qoldiq o'zgarmaydi.")
                : T("Количества нет в файле — остаток не изменится. Загрузятся только карточки и цены.", "Файлда сандар жок — калдык өзгөрбөйт. Карточкалар жана баалар гана жүктөлөт.",
                    "There is no quantity in the file — the stock will not change. Only cards and prices are loaded.", "Dosyada miktar yok — stok değişmez. Yalnızca kart ve fiyatlar yüklenir.", "Faylda miqdor yo'q — qoldiq o'zgarmaydi. Faqat kartochkalar va narxlar yuklanadi."))
            : T("Количество из файла прибавляется к остатку (приход).", "Файлдагы сан калдыкка кошулат (кирешe).", "The quantity from the file is added to the stock (a receipt).", "Dosyadaki miktar stoğa eklenir (mal kabul).", "Fayldagi miqdor qoldiqqa qo'shiladi (kirim).");
        _modeNote.Foreground = Brush("BrushTextSoft");
    }

    /// <summary>2026-10-30: «Какой файл подходит» — колонки и пример.</summary>
    private void BuildHintPanel()
    {
        _hintPanel.Bind(Border.BackgroundProperty, _hintPanel.GetResourceObservable("BrushPanelSoft"));
        _hintPanel.Bind(Border.BorderBrushProperty, _hintPanel.GetResourceObservable("BrushBorder"));
        var title = Caption(T("Какой файл подходит", "Кандай файл ылайыктуу", "Which file fits", "Hangi dosya uygun", "Qanday fayl mos"));
        var text = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("BrushTextSoft"));
        text.Text = T("Первая строка — заголовки столбцов: Штрихкод, Название, Доп. штрихкоды, Количество, Единица, Закупка, Наценка %, Цена продажи, Категория, Весовой. "
                + "Обязателен только «Штрихкод» (для новых товаров ещё «Название» и «Цена продажи»). «Количество» можно не писать — тогда остаток не изменится. "
                + "«Доп. штрихкоды» — несколько кодов в одной ячейке через ; , пробел или |. Штрихкоды — только цифры, 4–14 знаков; столбец штрихкода сохраняйте как текст. Для примера нажмите «Шаблон Excel».",
            "Биринчи сап — мамычалардын аталыштары: Штрих-код, Аталышы, Кошумча штрих-коддор, Саны, Бирдик, Сатып алуу баасы, Үстөк %, Сатуу баасы, Категория, Весовой. "
                + "Милдеттүү — «Штрих-код» гана (жаңы товарларга дагы «Аталышы» жана «Сатуу баасы»). «Саны» болбосо — калдык өзгөрбөйт. "
                + "«Кошумча штрих-коддор» — бир уячада бир нече код ; , боштук же | менен. Штрих-коддор — цифралар гана, 4–14 белги; мамычаны текст катары сактаңыз. Үлгү үчүн «Excel үлгүсү» басыңыз.",
            "The first row holds column headers: Barcode, Name, Extra barcodes, Quantity, Unit, Purchase, Markup %, Sale price, Category, By weight. "
                + "Only “Barcode” is required (new products also need “Name” and “Sale price”). “Quantity” may be left out — the stock then does not change. "
                + "“Extra barcodes” — several codes in one cell separated by ; , space or |. Barcodes are digits only, 4–14 characters; save the barcode column as text. Press “Excel template” for an example.",
            "İlk satır sütun başlıklarıdır: Barkod, Ürün adı, Ek barkodlar, Miktar, Birim, Alış, Kâr %, Satış fiyatı, Kategori, Tartılı. "
                + "Yalnızca «Barkod» zorunlu (yeni ürünler için ayrıca «Ürün adı» ve «Satış fiyatı»). «Miktar» yazılmayabilir — stok değişmez. "
                + "«Ek barkodlar» — tek hücrede ; , boşluk veya | ile ayrılmış birkaç kod. Barkodlar yalnızca rakam, 4–14 karakter; sütunu metin olarak kaydedin. Örnek için «Excel şablonu»na basın.",
            "Birinchi qator — ustun sarlavhalari: Shtrix-kod, Nomi, Qo'shimcha shtrix-kodlar, Miqdor, Birlik, Xarid, Ustama %, Sotuv narxi, Kategoriya, Vaznli. "
                + "Faqat «Shtrix-kod» majburiy (yangi mahsulotlar uchun yana «Nomi» va «Sotuv narxi»). «Miqdor»ni yozmasa ham bo'ladi — qoldiq o'zgarmaydi. "
                + "«Qo'shimcha shtrix-kodlar» — bir katakda ; , bo'shliq yoki | bilan bir nechta kod. Shtrix-kodlar faqat raqam, 4–14 belgi; ustunni matn sifatida saqlang. Namuna uchun «Excel shabloni»ni bosing.");
        _hintPanel.Child = new StackPanel { Spacing = 4, Children = { title, text } };
    }

}
