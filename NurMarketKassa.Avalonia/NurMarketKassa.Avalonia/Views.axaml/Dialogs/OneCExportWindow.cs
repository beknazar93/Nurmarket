using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-26 («создай то, чего у нас нет»: выгрузка в 1С): окно «Экспорт в 1С» — период, что выгрузить (номенклатура, остатки, продажи, возвраты, приходы, контрагенты)
/// и формат (Excel / CSV / CommerceML). Файл сохраняется через диалог выбора файла; данные собирает <see cref="OneCExportLoader"/>, формат пишет <see cref="OneCExport"/>.
/// Окно собрано в коде: подписи над полями, колонки «*», содержимое в прокрутке (вписывается в 1024×600).</summary>
public sealed class OneCExportWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static OneCExportWindow? _open;

    private readonly CalendarDatePicker _from = new() { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch, SelectedDateFormat = CalendarDatePickerFormat.Short };
    private readonly CalendarDatePicker _to = new() { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch, SelectedDateFormat = CalendarDatePickerFormat.Short };
    private readonly Dictionary<OneCExport.Part, CheckBox> _parts = new();
    private readonly RadioButton _excel = new() { GroupName = "OneCFormat", IsChecked = true };
    private readonly RadioButton _csv = new() { GroupName = "OneCFormat" };
    private readonly RadioButton _xml = new() { GroupName = "OneCFormat" };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _formatHint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer _scroll;
    private readonly Button _go;
    private readonly Button _close;
    private readonly List<Control> _inputs = new();
    private CancellationTokenSource? _cts;
    private int _busy;

    /// <summary>Только для тестов: подмена сохранения (вместо диалога выбора файла) — путь, куда писать; null — обычный диалог.</summary>
    public static Func<string, string?>? SavePathOverride { get; set; }

    /// <summary>2026-11-26 (стресс-тест): файл последних настроек окна; тесты подменяют на временный (null — обычный файл в папке данных).</summary>
    public static string? PrefsPathForTests { get; set; }

    private static string PrefsPath => PrefsPathForTests ?? OneCExport.DefaultPrefsPath;

    /// <summary>Только для тестов: пауза перед записью файла (проверка отмены во время выгрузки).</summary>
    public static Func<CancellationToken, Task>? BeforeWriteForTests { get; set; }

    public static void Open(Window? owner)
    {
        if (_open is { } existing)
        {
            existing.Activate();
            return;
        }
        var window = new OneCExportWindow();
        _open = window;
        window.Closed += (_, _) => _open = null;
        if (owner is null)
            window.Show();
        else
            _ = window.ShowDialog(owner);
    }

    public OneCExportWindow()
    {
        Title = OneCExport.FileTitle;
        SectionCards.AsModal(this, 620);
        MinWidth = 480;
        Avalonia.Automation.AutomationProperties.SetAutomationId(this, "OneCExportWindow");

        // 2026-11-26 (стресс-тест, Г): компактнее — на 1024×600 всё главное (период, что выгрузить, формат, кнопка) видно без прокрутки:
        // короткое вступление, быстрые периоды в строке дат, отметки в 3 колонки, форматы в ряд с одной подсказкой выбранного.
        var intro = Soft(T("Файл для «Загрузки данных из табличного документа» в 1С или обмена каталогом. Суммы — как в отчётах, до копейки.",
            "1С'теги «Табличный документтен маалымат жүктөө» же каталог алмашуу үчүн файл. Суммалар — отчёттордогудай, тыйынына чейин.",
            "A file for “Load data from a spreadsheet” in 1C or catalog exchange. Amounts match the reports to the last tyiyn.",
            "1C'deki «Tablodan veri yükleme» veya katalog alışverişi için dosya. Tutarlar raporlardaki gibi, kuruşu kuruşuna.",
            "1C dagi «Jadvaldan ma'lumot yuklash» yoki katalog almashinuvi uchun fayl. Summalar — hisobotlardagidek, tiyinigacha."));

        var (monthFrom, monthTo) = OneCExport.ThisMonth(DateTime.Today);
        _from.SelectedDate = monthFrom;
        _to.SelectedDate = monthTo;
        var thisMonth = QuickPeriod("OneCThisMonth", T("Этот месяц", "Ушул ай", "This month", "Bu ay", "Shu oy"), () => OneCExport.ThisMonth(DateTime.Today));
        var lastMonth = QuickPeriod("OneCLastMonth", T("Прошлый месяц", "Өткөн ай", "Last month", "Geçen ay", "O'tgan oy"), () => OneCExport.LastMonth(DateTime.Today));
        var quick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { thisMonth, lastMonth } };
        var period = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*,8,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        period.Children.Add(Caption(T("С даты", "Күндөн", "From", "Başlangıç", "Sanadan")));
        var toCaption = Caption(T("По дату", "Күнгө чейин", "To", "Bitiş", "Sanagacha"));
        Grid.SetColumn(toCaption, 2);
        period.Children.Add(toCaption);
        Grid.SetRow(_from, 1);
        Grid.SetRow(_to, 1);
        Grid.SetColumn(_to, 2);
        Grid.SetRow(quick, 1);
        Grid.SetColumn(quick, 4);
        period.Children.Add(_from);
        period.Children.Add(_to);
        period.Children.Add(quick);
        Avalonia.Automation.AutomationProperties.SetAutomationId(_from, "OneCFrom");
        Avalonia.Automation.AutomationProperties.SetAutomationId(_to, "OneCTo");
        _inputs.AddRange([_from, _to, thisMonth, lastMonth]);

        // последние настройки (что и в каком формате выгружали) — 2026-11-26, стресс-тест (Д)
        var prefs = OneCExport.LoadPrefs(PrefsPath);
        var partsGrid = new UniformGrid { Columns = 3 };
        foreach (var part in new[] { OneCExport.Part.Products, OneCExport.Part.Stock, OneCExport.Part.Sales, OneCExport.Part.Returns, OneCExport.Part.Receipts, OneCExport.Part.Counterparties })
        {
            var box = new CheckBox
            {
                MinHeight = 40,
                // 2026-11-27 (живой проход): «Возвраты от клиентов» упирался в квадратик соседней «Приходы от поставщиков» — без зазора
                // в ячейке UniformGrid подпись не переносилась. Отступ справа: подпись переносится раньше, между колонками есть воздух.
                Margin = new Thickness(0, 0, 12, 0),
                IsChecked = prefs is { } p ? p.Parts.Contains(part) : part is OneCExport.Part.Products or OneCExport.Part.Stock or OneCExport.Part.Sales,
                Content = new TextBlock { Text = OneCExport.PartTitle(part), FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
            };
            Avalonia.Automation.AutomationProperties.SetAutomationId(box, "OneCPart" + part);
            _parts[part] = box;
            partsGrid.Children.Add(box);
            _inputs.Add(box);
        }

        Describe(_excel, "OneCFormatExcel", "Excel (.xlsx)");
        Describe(_csv, "OneCFormatCsv", "CSV");
        Describe(_xml, "OneCFormatXml", "CommerceML (XML)");
        switch (prefs?.Format)
        {
            case OneCExport.Format.Csv: _csv.IsChecked = true; break;
            case OneCExport.Format.CommerceMl: _xml.IsChecked = true; break;
        }
        foreach (var radio in new[] { _excel, _csv, _xml })
        {
            radio.IsCheckedChanged += (_, _) => UpdateFormatHint();
            _inputs.Add(radio);
        }
        _formatHint.Bind(TextBlock.ForegroundProperty, _formatHint.GetResourceObservable("BrushTextSoft"));
        UpdateFormatHint();

        var formats = new UniformGrid { Columns = 3, Children = { _excel, _csv, _xml } };
        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable("BrushTextSoft"));

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                intro, period,
                Caption(T("Что выгрузить", "Эмнени чыгаруу", "What to export", "Neyi aktarmalı", "Nimani chiqarish")), partsGrid,
                Caption(T("Формат", "Формат", "Format", "Biçim", "Format")), formats, _formatHint,
                _status,
            },
        };
        _scroll = new ScrollViewer { Content = body, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var scroll = _scroll;

        _close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        _close.MinHeight = 44;
        Avalonia.Automation.AutomationProperties.SetAutomationId(_close, "OneCCloseButton");
        // 2026-11-26 (стресс-тест): во время выгрузки кнопка (и Esc) — «Отменить»: останавливает выгрузку, окно остаётся, прежний файл не тронут
        _close.Click += (_, _) =>
        {
            if (Volatile.Read(ref _busy) == 1 && _cts is { } running)
                running.Cancel();
            else
                Close();
        };
        _go = SectionCards.Primary(T("Выгрузить…", "Чыгаруу…", "Export…", "Aktar…", "Chiqarish…"));
        _go.MinHeight = 44;
        Avalonia.Automation.AutomationProperties.SetAutomationId(_go, "OneCExportButton");
        _go.Click += async (_, _) => await RunAsync().ConfigureAwait(true);
        Closed += (_, _) => _cts?.Cancel();

        Content = SectionCards.Shell(Title ?? "", ReportShopName.Current, scroll, SectionCards.Buttons(_close, _go));
    }

    private TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private static void Describe(RadioButton radio, string id, string title)
    {
        radio.Content = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        radio.MinHeight = 44;
        Avalonia.Automation.AutomationProperties.SetAutomationId(radio, id);
        Avalonia.Automation.AutomationProperties.SetName(radio, title);
    }

    /// <summary>2026-11-26 (стресс-тест): подсказка только к выбранному формату (раньше — три абзаца сразу, «Формат» уходил за прокрутку на 1024×600).</summary>
    private void UpdateFormatHint() => _formatHint.Text = SelectedFormat switch
    {
        OneCExport.Format.Csv => T("Текстовые файлы: UTF-8, разделитель «;», десятичная запятая. Одна таблица — один файл, несколько — архив .zip.",
            "Тексттик файлдар: UTF-8, бөлгүч «;», ондук үтүр. Бир таблица — бир файл, бир нечеси — .zip архиви.",
            "Text files: UTF-8, “;” separator, decimal comma. One table is one file, several are a .zip archive.",
            "Metin dosyaları: UTF-8, «;» ayırıcı, ondalık virgül. Tek tablo tek dosya, birden fazlası .zip arşivi.",
            "Matn fayllari: UTF-8, «;» ajratgich, o'nli vergul. Bitta jadval — bitta fayl, bir nechtasi — .zip arxiv."),
        OneCExport.Format.CommerceMl => T("Каталог и остатки — import.xml и offers.xml (CommerceML 2.x, обмен «как с сайтом»), документы — файлами CSV; всё в архиве .zip.",
            "Каталог жана калдыктар — import.xml жана offers.xml (CommerceML 2.x, «сайттай» алмашуу), документтер — CSV файлдары; баары .zip архивинде.",
            "Catalog and stock as import.xml and offers.xml (CommerceML 2.x, “website” exchange), documents as CSV files; all in a .zip archive.",
            "Katalog ve stok import.xml ve offers.xml (CommerceML 2.x, «web sitesi» alışverişi), belgeler CSV dosyaları; hepsi .zip arşivinde.",
            "Katalog va qoldiqlar — import.xml va offers.xml (CommerceML 2.x, «sayt kabi» almashinuv), hujjatlar — CSV fayllari; hammasi .zip arxivida."),
        _ => T("Одна книга, лист на таблицу, заголовки в первой строке — для «Загрузки данных из табличного документа». Рекомендуется.",
            "Бир китеп, ар бир таблицага барак, аталыштар биринчи сапта — «Табличный документтен маалымат жүктөө» үчүн. Сунушталат.",
            "One workbook, a sheet per table, headers in the first row — for “Load data from a spreadsheet”. Recommended.",
            "Tek kitap, her tablo için bir sayfa, başlıklar ilk satırda — «Tablodan veri yükleme» için. Önerilir.",
            "Bitta kitob, har jadvalga varaq, sarlavhalar birinchi qatorda — «Jadvaldan ma'lumot yuklash» uchun. Tavsiya etiladi."),
    };

    /// <summary>2026-11-26 (стресс-тест, сценарий «повторно за прошлый месяц»): кнопка быстрого периода в строке дат.</summary>
    private Button QuickPeriod(string id, string text, Func<(DateTime From, DateTime To)> range)
    {
        var b = SectionCards.Secondary(text);
        b.MinHeight = 44;
        b.Padding = new Thickness(10, 0);
        b.VerticalAlignment = VerticalAlignment.Bottom;
        Avalonia.Automation.AutomationProperties.SetAutomationId(b, id);
        b.Click += (_, _) =>
        {
            var (from, to) = range();
            _from.SelectedDate = from;
            _to.SelectedDate = to;
        };
        return b;
    }

    /// <summary>Во время выгрузки поля не меняются, «Закрыть» становится «Отменить».</summary>
    private void SetBusy(bool busy)
    {
        foreach (var control in _inputs)
            control.IsEnabled = !busy;
        _go.IsEnabled = !busy;
        var text = busy ? T("Отменить", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish") : T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish");
        _close.Content = text;
        Avalonia.Automation.AutomationProperties.SetName(_close, text);
    }

    private OneCExport.Format SelectedFormat => _xml.IsChecked == true ? OneCExport.Format.CommerceMl : _csv.IsChecked == true ? OneCExport.Format.Csv : OneCExport.Format.Excel;

    private HashSet<OneCExport.Part> SelectedParts => _parts.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToHashSet();

    private void SetStatus(string? text, bool error = false)
    {
        _status.Text = text ?? "";
        _status.IsVisible = !string.IsNullOrWhiteSpace(text);
        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable(error ? "BrushDangerOnSoft" : "BrushTextSoft"));
        if (_status.IsVisible)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background); // 2026-11-26: итог виден без прокрутки руками
    }

    private async Task RunAsync()
    {
        var parts = SelectedParts;
        if (parts.Count == 0)
        {
            SetStatus(T("Отметьте, что выгрузить.", "Эмнени чыгарууну белгилеңиз.", "Tick what to export.", "Neyi aktaracağınızı işaretleyin.", "Nimani chiqarishni belgilang."), error: true);
            return;
        }
        if (_from.SelectedDate is not { } from || _to.SelectedDate is not { } to || from.Date > to.Date)
        {
            SetStatus(T("Проверьте период: «С даты» не может быть позже «По дату».", "Мезгилди текшериңиз: «Күндөн» «Күнгө чейин» дан кийин болбошу керек.",
                "Check the period: “From” cannot be later than “To”.", "Dönemi kontrol edin: «Başlangıç» «Bitiş»ten sonra olamaz.", "Davrni tekshiring: «Sanadan» «Sanagacha» dan keyin bo'lishi mumkin emas."), error: true);
            return;
        }
        if (!TariffGate.CanUseAnalyticsExport)
        {
            SetStatus(TariffGate.AnalyticsExportLockedMessage, error: true);
            return;
        }
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;

        var format = SelectedFormat;
        SetBusy(true);
        _cts = new CancellationTokenSource();
        try
        {
            var shop = ReportShopName.Current;
            var tableCount = OneCExport.BuildTables(new OneCExport.Data(from.Date, to.Date, shop, [], [], [], [], []), parts).Count;
            var suggested = OneCExport.SuggestFileName(from.Date, to.Date, shop, format, tableCount);
            string? path;
            IStorageFile? file = null;
            if (SavePathOverride is { } pick)
                path = pick(suggested);
            else
            {
                var ext = System.IO.Path.GetExtension(suggested).TrimStart('.');
                file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = T("Сохранить выгрузку для 1С", "1С үчүн чыгарууну сактоо", "Save the export for 1C", "1C için aktarımı kaydet", "1C uchun eksportni saqlash"),
                    SuggestedFileName = suggested,
                    FileTypeChoices = [new FilePickerFileType(ext.ToUpperInvariant()) { Patterns = ["*." + ext] }],
                }).ConfigureAwait(true);
                path = file is null ? null : ExportFileBridge.PathFor(file);
            }
            if (path is null)
                return;

            // 2026-11-26 (стресс-тест): файл открыт в Excel, папки нет, только для чтения — говорим сразу, до загрузки данных (а не через минуту)
            if (OneCExport.CheckWritable(path) is { } cannotWrite)
            {
                SetStatus(cannotWrite, error: true);
                return;
            }
            OneCExport.SavePrefs(PrefsPath, parts, format);

            SetStatus(T("Готовлю выгрузку…", "Чыгаруу даярдалууда…", "Preparing the export…", "Aktarım hazırlanıyor…", "Eksport tayyorlanmoqda…"));
            var progress = new Progress<string>(text => SetStatus(text)); // создан на потоке окна — сообщения приходят туда же
            var loaded = await OneCExportLoader.LoadAsync(from.Date, to.Date, parts, progress, _cts.Token).ConfigureAwait(true);
            if (BeforeWriteForTests is { } pause)
                await pause(_cts.Token).ConfigureAwait(true);
            SetStatus(T("Записываю файл…", "Файл жазылууда…", "Writing the file…", "Dosya yazılıyor…", "Fayl yozilmoqda…"));
            var token = _cts.Token;
            var result = await Task.Run(() => OneCExport.Export(path, format, loaded.Data, parts, token), token).ConfigureAwait(true);
            var shown = file is null ? path : await ExportFileBridge.CompleteAsync(file, path).ConfigureAwait(true);

            var lines = new List<string>
            {
                T($"Готово: {shown}", $"Даяр: {shown}", $"Done: {shown}", $"Hazır: {shown}", $"Tayyor: {shown}"),
                T($"Строк в файле: {result.Rows}.", $"Файлдагы саптар: {result.Rows}.", $"Rows in the file: {result.Rows}.", $"Dosyadaki satır: {result.Rows}.", $"Fayldagi qatorlar: {result.Rows}."),
            };
            if (result.Rows == 0) // 2026-11-26 (стресс-тест): пустой период — честно, а не «Готово» с пустыми листами без пояснения
                lines.Add(T("За выбранный период данных нет — в файле только заголовки столбцов. Проверьте период.",
                    "Тандалган мезгилде маалымат жок — файлда мамычалардын аталыштары гана. Мезгилди текшериңиз.",
                    "There is no data for the selected period — the file has column headers only. Check the period.",
                    "Seçilen dönemde veri yok — dosyada yalnızca sütun başlıkları var. Dönemi kontrol edin.",
                    "Tanlangan davrda ma'lumot yo'q — faylda faqat ustun sarlavhalari bor. Davrni tekshiring."));
            if (parts.Contains(OneCExport.Part.Sales))
                lines.AddRange(OneCExport.TotalsLines(loaded.Data)); // 2026-11-26: сумма чеков, выручка и сверка со сводкой сервера
            lines.AddRange(loaded.Warnings);
            var mismatch = loaded.Data.ServerRevenue is { } sr && Math.Abs(sr - OneCExport.Revenue(loaded.Data)) >= 0.01m && parts.Contains(OneCExport.Part.Sales);
            SetStatus(string.Join("\n", lines), error: loaded.Warnings.Count > 0 || mismatch);
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Выгрузка в 1С отменена.", "INFO");
            if (IsVisible) // 2026-11-26 (стресс-тест): отмена кнопкой — окно открыто, говорим, что файл не тронут
                SetStatus(T("Выгрузка отменена — файл не создан и не изменён.", "Чыгаруу жокко чыгарылды — файл түзүлгөн жок жана өзгөргөн жок.",
                    "The export was cancelled — the file was not created or changed.", "Aktarım iptal edildi — dosya oluşturulmadı ve değiştirilmedi.",
                    "Eksport bekor qilindi — fayl yaratilmadi va o'zgartirilmadi."));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выгрузка в 1С не удалась: {ex}", "WARNING");
            SetStatus(T("Не удалось выгрузить: ", "Чыгаруу мүмкүн болгон жок: ", "Could not export: ", "Aktarılamadı: ", "Eksport qilib bo'lmadi: ") + ex.Message, error: true);
        }
        finally
        {
            SetBusy(false);
            Interlocked.Exchange(ref _busy, 0);
        }
    }
}
