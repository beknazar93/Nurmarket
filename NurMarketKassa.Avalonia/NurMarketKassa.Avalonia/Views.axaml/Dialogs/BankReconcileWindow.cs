using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-05 (функция 13 «Сверка банковской выписки»): окно открывается кнопкой «Сверка выписки…» в «Прибыль и деньги». Владелец выбирает месяц и файл выписки
/// (Excel/CSV MBank, Optima и др.); касса берёт безнал смен месяца с сервера (NurCRM), оплаты поставщикам (приходы) и сопоставляет с банком
/// (<see cref="BankReconciler"/>). Итог: «безнал по кассе X, по банку Y, разница Z — N чеков». Непонятные строки — «расход?» / «поступление?» с правилом «запомнить»
/// (BankRulesStore, локально). Выписка читается только на этом ПК и никуда не отправляется. Выгрузка итога — Excel.</summary>
public sealed class BankReconcileWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly ComboBox _month = new() { MinHeight = 40, MinWidth = 150 };
    private readonly TextBlock _file = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _summary = new() { Spacing = 3 };
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly CheckBox _onlyAttention = new();
    private readonly Button _load;
    private readonly Button _export;

    private BankStatement? _statement;
    private string _fileName = "";
    private List<ShiftCashless> _shifts = new();
    private List<SupplierOutgoing> _supplierPay = new();
    private List<string> _supplierNames = new();
    private BankReconcileResult? _result;
    private BankMonthSummary? _summaryData;
    private DateTime _from, _to;
    private bool _busy;
    private bool _setup = true;
    private CancellationTokenSource? _cts;
    // 2026-11-05 (приёмка волн 0–3, С-4): список смен с сервера (он за всё время, не за месяц) и приходы по месяцам держим в окне — смена месяца не ждёт сервер заново.
    private IReadOnlyList<NurMarketKassa.Models.ShiftHistoryEntry>? _entriesCache;
    private DateTime _entriesAt;
    private readonly Dictionary<DateTime, (List<SupplierOutgoing> Pay, List<string> Names, string Warning)> _receiptsCache = new();
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 4, IsVisible = false };

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public BankReconcileWindow()
    {
        Title = T("Сверка банковской выписки", "Банк көчүрмөсүн салыштыруу", "Bank statement reconciliation", "Banka ekstresi mutabakatı", "Bank ko'chirmasini solishtirish");
        SectionCards.AsModal(this, 1100);

        for (var i = 0; i < 12; i++)
        {
            var m = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-i);
            _month.Items.Add(new ComboBoxItem { Content = m.ToString("MM.yyyy", Ru), Tag = m });
        }
        _month.SelectedIndex = DateTime.Today.Day <= 5 ? 1 : 0; // в первые дни месяца чаще сверяют прошлый
        _month.SelectionChanged += async (_, _) =>
        {
            if (!_setup)
                await RefreshAsync(fetch: true).ConfigureAwait(true);
        };

        _load = SectionCards.Primary(T("Загрузить выписку…", "Көчүрмөнү жүктөө…", "Load statement…", "Ekstreyi yükle…", "Ko'chirmani yuklash…"));
        _load.Click += async (_, _) => await PickFileAsync().ConfigureAwait(true);
        _file.Text = T("Файл не выбран. Подойдёт Excel (.xlsx) или CSV из интернет-банка.", "Файл тандалган эмес. Интернет-банктан Excel (.xlsx) же CSV ылайык.", "No file chosen. An Excel (.xlsx) or CSV file from internet banking works.",
            "Dosya seçilmedi. İnternet bankacılığından Excel (.xlsx) veya CSV uygundur.", "Fayl tanlanmagan. Internet-bankdan Excel (.xlsx) yoki CSV mos keladi.");
        _file.Bind(TextBlock.ForegroundProperty, _file.GetResourceObservable("BrushTextSoft"));
        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable("BrushTextSoft"));
        var monthLabel = new TextBlock { Text = T("Месяц", "Ай", "Month", "Ay", "Oy"), FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        monthLabel.Bind(TextBlock.ForegroundProperty, monthLabel.GetResourceObservable("BrushText"));
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*"), Margin = new Thickness(0, 0, 0, 4) };
        monthLabel.Margin = new Thickness(0, 0, 8, 0);
        _load.Margin = new Thickness(12, 0, 12, 0);
        Grid.SetColumn(_month, 1);
        Grid.SetColumn(_load, 2);
        Grid.SetColumn(_file, 3);
        top.Children.Add(monthLabel);
        top.Children.Add(_month);
        top.Children.Add(_load);
        top.Children.Add(_file);

        _onlyAttention.Content = T("Только то, что требует внимания (расход? / поступление?)", "Көңүл бурууну талап кылганы гана (чыгым? / түшүү?)", "Only items that need attention (expense? / income?)",
            "Yalnızca dikkat gerektirenler (gider? / gelir?)", "Faqat e'tibor talab qilganlar (xarajat? / kirim?)");
        _onlyAttention.IsCheckedChanged += (_, _) =>
        {
            if (!_setup)
                RenderRows();
        };
        var header = Row(T("Дата", "Күнү", "Date", "Tarih", "Sana"), T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), T("Описание в выписке", "Көчүрмөдөгү сүрөттөмө", "Statement description", "Ekstre açıklaması", "Ko'chirmadagi tavsif"),
            T("Что это", "Бул эмне", "What it is", "Bu ne", "Bu nima"), null, soft: true);
        var scroll = new ScrollViewer { MaxHeight = 330, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = _rows };
        var root = new StackPanel { Spacing = 8, Children = { top, _status, _progress, _summary, _onlyAttention, header, scroll } };

        _export = SectionCards.Secondary(T("Выгрузить в Excel", "Excel'ге чыгаруу", "Export to Excel", "Excel'e aktar", "Excel'ga yuklash"));
        _export.IsEnabled = false;
        _export.Click += async (_, _) => await ExportAsync().ConfigureAwait(true);
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { _export, close } };

        Content = SectionCards.Shell(Title,
            T("Безналичные продажи по сменам сверяются с поступлениями в банк, оплаты поставщикам — со списаниями. Файл выписки остаётся на этом компьютере.",
                "Смендер боюнча накталай эмес сатуулар банктын түшүүлөрү менен, жеткирүүчүлөргө төлөмдөр — тартуулар менен салыштырылат. Көчүрмө файлы ушул компьютерде калат.",
                "Cashless sales by shift are matched with bank credits, supplier payments with debits. The statement file stays on this computer.",
                "Vardiya bazında kartlı satışlar banka girişleriyle, tedarikçi ödemeleri çıkışlarla eşleştirilir. Ekstre dosyası bu bilgisayarda kalır.",
                "Smenalar bo'yicha naqdsiz sotuvlar bankka tushumlar bilan, yetkazib beruvchilarga to'lovlar yechib olishlar bilan solishtiriladi. Ko'chirma fayli shu kompyuterda qoladi."),
            root, footer);
        Closed += (_, _) => _cts?.Cancel();
        _setup = false;
    }

    private async Task PickFileAsync()
    {
        if (_busy || StorageProvider is not { } storage)
            return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Выписка банка", "Банк көчүрмөсү", "Bank statement", "Banka ekstresi", "Bank ko'chirmasi"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(T("Выписка (Excel, CSV)", "Көчүрмө (Excel, CSV)", "Statement (Excel, CSV)", "Ekstre (Excel, CSV)", "Ko'chirma (Excel, CSV)")) { Patterns = ["*.xlsx", "*.xls", "*.csv", "*.txt"] }],
        }).ConfigureAwait(true);
        var path = files.Count > 0 ? await ImportFileBridge.TemporaryPathAsync(files[0]).ConfigureAwait(true) : null;
        if (string.IsNullOrEmpty(path))
            return;
        SetBusy(true, T("Читаю выписку…", "Көчүрмөнү окуп жатам…", "Reading the statement…", "Ekstre okunuyor…", "Ko'chirma o'qilmoqda…"));
        try
        {
            var read = await Task.Run(() => ProductFileReader.ReadFile(path)).ConfigureAwait(true);
            if (!read.Ok || read.Table is not { } table)
            {
                SetBusy(false, read.Error ?? T("Файл не прочитался.", "Файл окулган жок.", "The file could not be read.", "Dosya okunamadı.", "Fayl o'qilmadi."), warn: true);
                return;
            }
            var statement = BankStatementParser.Parse(table.Rows.Select(r => r.Cells).ToList());
            if (statement.Lines.Count == 0)
            {
                SetBusy(false, string.Join(" ", statement.Warnings), warn: true);
                return;
            }
            _statement = statement;
            // 2026-11-05 (приёмка волн 0–3, С-4): сверяем месяц самой выписки (по датам строк), а не «текущий/прошлый» — иначе выписка за ноябрь сверялась с октябрём (ложная разница).
            SelectStatementMonth(statement);
            _fileName = files[0].Name;
            _file.Text = $"{_fileName} · " + T($"операций: {statement.Lines.Count}", $"операция: {statement.Lines.Count}", $"transactions: {statement.Lines.Count}", $"işlem: {statement.Lines.Count}", $"operatsiya: {statement.Lines.Count}")
                + $" · {statement.Columns}";
            PosLogger.Log($"Сверка выписки: прочитано {statement.Lines.Count} операций, колонки: {statement.Columns}.", "FINANCE");
            await RefreshAsync(fetch: true).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Сверка выписки: файл не разобран ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            SetBusy(false, T("Не удалось разобрать файл — проверьте, что это выписка банка.", "Файлды талдоо мүмкүн болгон жок — банк көчүрмөсү экенин текшериңиз.", "The file could not be parsed — check that it is a bank statement.",
                "Dosya çözümlenemedi — banka ekstresi olduğundan emin olun.", "Faylni tahlil qilib bo'lmadi — bu bank ko'chirmasi ekanini tekshiring."), warn: true);
        }
    }

    /// <summary>Выбрать в списке месяц, на который приходится больше всего операций выписки (нет в списке — добавить). Без повторной загрузки: её делает вызывающий.</summary>
    private void SelectStatementMonth(BankStatement statement)
    {
        if (BankReconciler.DominantMonth(statement.Lines) is not { } month)
            return;
        var item = _month.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is DateTime d && d == month);
        _setup = true; // выбор месяца не должен сам запускать загрузку
        try
        {
            if (item is null)
            {
                item = new ComboBoxItem { Content = month.ToString("MM.yyyy", Ru), Tag = month };
                var at = _month.Items.OfType<ComboBoxItem>().TakeWhile(i => i.Tag is DateTime d && d > month).Count();
                _month.Items.Insert(at, item);
            }
            _month.SelectedItem = item;
        }
        finally
        {
            _setup = false;
        }
    }

    private (DateTime From, DateTime To) Period()
    {
        var m = (DateTime)((_month.SelectedItem as ComboBoxItem)?.Tag ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));
        return (m, m.AddMonths(1).AddDays(-1));
    }

    /// <summary>Подтянуть смены и приходы месяца с сервера (если нужно) и пересчитать сверку.</summary>
    private async Task RefreshAsync(bool fetch)
    {
        if (_statement is null)
            return;
        (_from, _to) = Period();
        _cts?.Cancel();
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = _cts.Token;
        if (fetch)
        {
            SetBusy(true, T("Получаю смены и приходы с сервера…", "Серверден смендерди жана кирүүлөрдү алып жатам…", "Fetching shifts and deliveries from the server…", "Vardiya ve girişler sunucudan alınıyor…", "Smenalar va kirimlar serverdan olinmoqda…"));
            try
            {
                // 2026-11-05 (С-4): список смен за всё время — один раз на окно (10 минут); смена месяца только фильтрует его, сервер заново не ждёт.
                IReadOnlyList<NurMarketKassa.Models.ShiftHistoryEntry> entries;
                var loadFailed = false;
                if (_entriesCache is { } cachedEntries && DateTime.UtcNow - _entriesAt < TimeSpan.FromMinutes(10))
                    entries = cachedEntries;
                else
                {
                    entries = await ShiftHistoryService.LoadAsync(ct, fresh: true).ConfigureAwait(true);
                    loadFailed = ShiftHistoryService.LastLoadFailed;
                    if (!loadFailed)
                    {
                        _entriesCache = entries;
                        _entriesAt = DateTime.UtcNow;
                    }
                }
                if (loadFailed)
                {
                    SetBusy(false, T("Нет связи с сервером — смены не получены. Сверка невозможна.", "Сервер менен байланыш жок — смендер алынган жок. Салыштыруу мүмкүн эмес.", "No connection to the server — shifts not received. Reconciliation is not possible.",
                        "Sunucu bağlantısı yok — vardiyalar alınamadı. Mutabakat yapılamaz.", "Server bilan aloqa yo'q — smenalar olinmadi. Solishtirib bo'lmaydi."), warn: true);
                    return;
                }
                var windowFrom = _from.AddDays(-7);
                _shifts = entries
                    .Select(s => (Entry: s, Day: (s.ClosedAt ?? s.OpenedAt)?.Date))
                    .Where(x => x.Day is { } d && d >= windowFrom && d <= _to && x.Entry.NonCashSales is > 0)
                    .Select(x => new ShiftCashless(x.Entry.ShiftNumber, x.Day!.Value, (double)x.Entry.NonCashSales!.Value, x.Entry.SalesCount ?? 0,
                        $"{x.Entry.Cashier} · {(x.Entry.OpenedAt ?? x.Day.Value):dd.MM HH:mm}"))
                    .ToList();
                var warning = "";
                if (_receiptsCache.TryGetValue(_from, out var cachedReceipts))
                {
                    _supplierPay = cachedReceipts.Pay;
                    _supplierNames = cachedReceipts.Names;
                    warning = cachedReceipts.Warning;
                }
                else
                try
                {
                    var svc = new SupplierApiService(App.GetRequiredService<NurMarketApiClient>());
                    var page = await svc.GetReceiptsPageAsync(1, 200, null, _from.AddDays(-10), _to, ct).ConfigureAwait(true);
                    _supplierPay = page.Receipts.Where(r => r.Total > 0.005 && r.CreatedAt is not null)
                        .Select(r => new SupplierOutgoing(r.CreatedAt!.Value.Date, r.Total, r.SupplierName)).ToList();
                    _supplierNames = (await svc.ListAsync(ct).ConfigureAwait(true)).Select(s => s.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                    if (page.HasNext)
                        warning = T(" Приходов больше 200 — показаны последние, часть оплат поставщикам может не сопоставиться.", " Кирүүлөр 200дөн көп — акыркылары көрсөтүлдү, бир бөлүгү дал келбеши мүмкүн.",
                            " More than 200 deliveries — the latest are used, some supplier payments may stay unmatched.", " 200'den fazla giriş var — sonuncular kullanıldı, bazı ödemeler eşleşmeyebilir.", " Kirimlar 200 tadan ko'p — oxirgilari ishlatildi, ba'zi to'lovlar mos kelmasligi mumkin.");
                    _receiptsCache[_from] = (_supplierPay, _supplierNames, warning); // 2026-11-05 (С-4): приходы месяца — один раз на окно
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    _supplierPay = new List<SupplierOutgoing>();
                    _supplierNames = new List<string>();
                    PosLogger.Log($"Сверка выписки: приходы поставщиков не получены ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                    warning = T(" Оплаты поставщикам не сверены: приходы не получены.", " Жеткирүүчүлөргө төлөмдөр салыштырылган жок: кирүүлөр алынган жок.", " Supplier payments were not matched: deliveries not received.",
                        " Tedarikçi ödemeleri eşleştirilmedi: girişler alınamadı.", " Yetkazib beruvchilarga to'lovlar solishtirilmadi: kirimlar olinmadi.");
                }
                SetBusy(false, warning.Trim());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Сверка выписки: смены не получены ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                SetBusy(false, T("Не удалось получить смены с сервера.", "Серверден смендерди алуу мүмкүн болгон жок.", "Could not get shifts from the server.", "Vardiyalar sunucudan alınamadı.", "Smenalarni serverdan olib bo'lmadi."), warn: true);
                return;
            }
        }

        var lines = _statement.Lines.Where(l => l.Date.Date >= _from && l.Date.Date <= _to.AddDays(BankReconciler.CreditDelayDays)).ToList();
        // 2026-11-05 (С-4): в выбранном месяце в выписке нет операций — не показываем «разницу» (она была бы целиком ложной), а говорим об этом.
        if (!lines.Any(l => l.Date.Date <= _to))
        {
            _result = null;
            _summaryData = null;
            _summary.Children.Clear();
            _rows.Children.Clear();
            _export.IsEnabled = false;
            var first = _statement.Lines.Min(l => l.Date).Date;
            var last = _statement.Lines.Max(l => l.Date).Date;
            SetBusy(false, T($"В выписке нет операций за {_from:MM.yyyy} (в файле: {first:dd.MM.yyyy} — {last:dd.MM.yyyy}). Выберите месяц выписки.",
                $"Көчүрмөдө {_from:MM.yyyy} үчүн операция жок (файлда: {first:dd.MM.yyyy} — {last:dd.MM.yyyy}). Көчүрмөнүн айын тандаңыз.",
                $"The statement has no transactions for {_from:MM.yyyy} (file: {first:dd.MM.yyyy} — {last:dd.MM.yyyy}). Pick the statement's month.",
                $"Ekstrede {_from:MM.yyyy} için işlem yok (dosya: {first:dd.MM.yyyy} — {last:dd.MM.yyyy}). Ekstrenin ayını seçin.",
                $"Ko'chirmada {_from:MM.yyyy} uchun operatsiya yo'q (fayl: {first:dd.MM.yyyy} — {last:dd.MM.yyyy}). Ko'chirma oyini tanlang."), warn: true);
            return;
        }
        _result = BankReconciler.Reconcile(lines, _shifts, _supplierPay, _supplierNames, BankRulesStore.All);
        _summaryData = BankReconciler.Summarize(_result, _shifts, _from, _to);
        RenderSummary();
        RenderRows();
        _export.IsEnabled = true;
    }

    private void SetBusy(bool busy, string text, bool warn = false)
    {
        _busy = busy;
        _progress.IsVisible = busy; // 2026-11-05 (С-4): индикатор ожидания, пока идёт загрузка/пересчёт
        _load.IsEnabled = !busy;
        _month.IsEnabled = !busy;
        _status.Text = text;
        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable(warn ? "BrushDanger" : "BrushTextSoft"));
    }

    private TextBlock Line(string text, bool bold = false, string brush = "BrushText", double size = 13.5)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (bold)
            t.FontWeight = FontWeight.SemiBold;
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(brush));
        return t;
    }

    private static string Som(double v) => v.ToString("#,##0.00", Ru) + " " + T("сом", "сом", "som", "som", "so'm");

    private void RenderSummary()
    {
        _summary.Children.Clear();
        if (_summaryData is not { } s || _result is not { } r)
            return;
        var month = _from.ToString("MM.yyyy", Ru);
        _summary.Children.Add(Line(T($"За {month} безнал по кассе {Som(s.Kassa)}, по банку {Som(s.Bank)}, разница {Som(s.Difference)}" + (s.ReceiptsNotFound > 0 ? $" — {s.ReceiptsNotFound} {Tr.Plural(s.ReceiptsNotFound, "чек", "чека", "чеков")}" : ""),
            $"{month} үчүн касса боюнча безнал {Som(s.Kassa)}, банк боюнча {Som(s.Bank)}, айырма {Som(s.Difference)}" + (s.ReceiptsNotFound > 0 ? $" — {s.ReceiptsNotFound} чек" : ""),
            $"For {month} cashless per register {Som(s.Kassa)}, per bank {Som(s.Bank)}, difference {Som(s.Difference)}" + (s.ReceiptsNotFound > 0 ? $" — {s.ReceiptsNotFound} {Tr.PluralEn(s.ReceiptsNotFound, "receipt", "receipts")}" : ""),
            $"{month} için kasaya göre kartlı {Som(s.Kassa)}, bankaya göre {Som(s.Bank)}, fark {Som(s.Difference)}" + (s.ReceiptsNotFound > 0 ? $" — {s.ReceiptsNotFound} fiş" : ""),
            $"{month} uchun kassa bo'yicha naqdsiz {Som(s.Kassa)}, bank bo'yicha {Som(s.Bank)}, farq {Som(s.Difference)}" + (s.ReceiptsNotFound > 0 ? $" — {s.ReceiptsNotFound} ta chek" : "")),
            bold: true, brush: Math.Abs(s.Difference) > 0.5 ? "BrushDanger" : "BrushSuccess", size: 15));
        var attention = r.Count(BankLineKind.UnknownExpense) + r.Count(BankLineKind.UnknownIncome);
        _summary.Children.Add(Line(T($"Смен не найдено в банке: {s.ShiftsNotFound} · строк выписки: {r.Lines.Count} · требуют внимания: {attention}",
            $"Банктан табылбаган смендер: {s.ShiftsNotFound} · көчүрмө саптары: {r.Lines.Count} · көңүл бурууну талап кылат: {attention}",
            $"Shifts not found in the bank: {s.ShiftsNotFound} · statement rows: {r.Lines.Count} · need attention: {attention}",
            $"Bankada bulunmayan vardiya: {s.ShiftsNotFound} · ekstre satırı: {r.Lines.Count} · dikkat gerektiren: {attention}",
            $"Bankda topilmagan smenalar: {s.ShiftsNotFound} · ko'chirma qatorlari: {r.Lines.Count} · e'tibor talab qiladi: {attention}"), brush: "BrushTextSoft", size: 12.5));
        foreach (var missing in r.ShiftsNotFound.Where(x => x.Day >= _from && x.Day <= _to).Take(5))
            _summary.Children.Add(Line($"• {missing.Day:dd.MM} · {missing.Label} · {Som(missing.Amount)}", brush: "BrushTextSoft", size: 12));
    }

    private void RenderRows()
    {
        _rows.Children.Clear();
        if (_result is not { } r)
            return;
        var shown = r.Lines.Where(l => _onlyAttention.IsChecked != true || l.Kind is BankLineKind.UnknownExpense or BankLineKind.UnknownIncome).Take(400).ToList();
        foreach (var l in shown)
        {
            var (text, brush) = l.Kind switch
            {
                BankLineKind.SalesMatched => (BankReconcileExport.KindTitle(l.Kind), "BrushSuccess"),
                BankLineKind.SalesWithFee => (BankReconcileExport.KindTitle(l.Kind), "BrushAccent"),
                BankLineKind.SupplierPayment or BankLineKind.SupplierByName => (BankReconcileExport.KindTitle(l.Kind) + (l.Supplier is { Length: > 0 } sp ? " · " + sp : ""), "BrushSuccess"),
                BankLineKind.Rule => (BankReconcileExport.KindTitle(l.Kind) + " · " + BankCategories.Title(l.Category), "BrushTextSoft"),
                _ => (BankReconcileExport.KindTitle(l.Kind), "BrushDanger"),
            };
            Control? action = null;
            if (l.Kind is BankLineKind.UnknownExpense or BankLineKind.UnknownIncome)
                action = BuildRememberControl(l);
            _rows.Children.Add(Row(l.Line.Date.ToString("dd.MM.yyyy", Ru), (l.Line.Amount > 0 ? "+" : "−") + Math.Abs(l.Line.Amount).ToString("#,##0.00", Ru), l.Line.Description, text, action, soft: false, brush, l.Line.Amount > 0));
        }
        if (shown.Count == 0)
            _rows.Children.Add(Line(T("Нет строк для показа.", "Көрсөтүүгө саптар жок.", "No rows to show.", "Gösterilecek satır yok.", "Ko'rsatish uchun qator yo'q."), brush: "BrushTextSoft", size: 13));
        else if (r.Lines.Count > 400 && _onlyAttention.IsChecked != true)
            _rows.Children.Add(Line(T("Показаны первые 400 строк; остальные есть в Excel.", "Алгачкы 400 сап көрсөтүлдү; калгандары Excel'де.", "The first 400 rows are shown; the rest are in Excel.", "İlk 400 satır gösterildi; kalanlar Excel'de.", "Dastlabki 400 qator ko'rsatildi; qolganlari Excel'da."), brush: "BrushTextSoft", size: 12));
    }

    /// <summary>«Расход?» → список категорий и «Запомнить»: описание превращается в правило (BankRulesStore), и следующие такие строки сопоставляются сами.</summary>
    private Control BuildRememberControl(BankLineResult l)
    {
        var income = l.Line.Amount > 0;
        var box = new ComboBox { MinHeight = 32, MinWidth = 160 }; // 2026-11-05 (Н-9): колонки подогнаны, чтобы «Запомнить» не обрезалось
        foreach (var code in BankCategories.Codes.Where(c => income ? c == "not_sales" : c != "not_sales"))
            box.Items.Add(new ComboBoxItem { Content = BankCategories.Title(code), Tag = code });
        box.SelectedIndex = 0;
        var remember = SectionCards.Secondary(T("Запомнить", "Эстеп калуу", "Remember", "Hatırla", "Eslab qolish"));
        remember.MinHeight = 32;
        remember.Margin = new Thickness(6, 0, 0, 0);
        remember.Click += async (_, _) =>
        {
            var code = (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            if (!BankRulesStore.Remember(l.Line.Description, code))
            {
                SetBusy(false, T("Из этого описания нечего запомнить — в нём только служебные слова.", "Бул сүрөттөмөдөн эстей турган нерсе жок — кызматтык сөздөр гана.", "Nothing to remember in this description — only generic words.",
                    "Bu açıklamada hatırlanacak bir şey yok — yalnızca genel sözcükler.", "Bu tavsifda eslab qolish uchun hech narsa yo'q — faqat umumiy so'zlar."), warn: true);
                return;
            }
            PosLogger.Log($"Сверка выписки: запомнено правило «{BankRules.MakePattern(l.Line.Description)}» → {code}.", "FINANCE");
            SetBusy(false, "");
            await RefreshAsync(fetch: false).ConfigureAwait(true);
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { box, remember } };
    }

    private Control Row(string date, string amount, string description, string what, Control? action, bool soft, string brush = "BrushText", bool positive = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("92,120,*,190,350"), MinHeight = soft ? 0 : 34, Margin = new Thickness(0, 2) };
        void Cell(int col, string text, bool trim, string b, TextAlignment align = TextAlignment.Left, bool bold = false)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = soft ? 12 : 13, FontWeight = soft || bold ? FontWeight.SemiBold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
                TextAlignment = align, Margin = new Thickness(0, 0, 10, 0),
            };
            if (trim && !soft)
                ToolTip.SetTip(tb, text);
            tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable(b));
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }
        var soft1 = soft ? "BrushTextSoft" : "BrushText";
        Cell(0, date, false, soft1);
        Cell(1, amount, false, soft ? "BrushTextSoft" : positive ? "BrushSuccess" : "BrushText", TextAlignment.Right, bold: !soft);
        Cell(2, description, true, soft1);
        Cell(3, what, true, soft ? "BrushTextSoft" : brush);
        if (action is not null)
        {
            Grid.SetColumn(action, 4);
            grid.Children.Add(action);
        }
        return grid;
    }

    private async Task ExportAsync()
    {
        if (_result is not { } result || _summaryData is not { } summary || StorageProvider is not { } storage)
            return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Сохранить сверку в Excel", "Салыштырууну Excel'ге сактоо", "Save the reconciliation to Excel", "Mutabakatı Excel'e kaydet", "Solishtirishni Excel'ga saqlash"),
            SuggestedFileName = T("Сверка выписки", "Көчүрмөнү салыштыруу", "Statement reconciliation", "Ekstre mutabakatı", "Ko'chirma solishtirish") + $" {_from:yyyy-MM}.xlsx",
            FileTypeChoices = [new FilePickerFileType("Excel") { Patterns = ["*.xlsx"] }],
        }).ConfigureAwait(true);
        var path = file is null ? null : ExportFileBridge.PathFor(file);
        if (file is null || string.IsNullOrEmpty(path))
            return;
        try
        {
            var from = _from;
            var to = _to;
            var shifts = _shifts;
            await Task.Run(() =>
            {
                var meta = ReportMeta.Create(T("Сверка банковской выписки", "Банк көчүрмөсүн салыштыруу", "Bank statement reconciliation", "Banka ekstresi mutabakatı", "Bank ko'chirmasini solishtirish"),
                    UserPreferences.Instance.StoreName, $"{from:dd.MM.yyyy} — {to:dd.MM.yyyy}");
                BankReconcileExport.Save(path!, meta, result, summary, shifts, from, to);
            }).ConfigureAwait(true);
            var done = await ExportFileBridge.CompleteAsync(file, path!).ConfigureAwait(true);
            SetBusy(false, T($"Сохранено: {done}", $"Сакталды: {done}", $"Saved: {done}", $"Kaydedildi: {done}", $"Saqlandi: {done}"));
            PosLogger.Log("Сверка выписки: итог выгружен в Excel.", "FINANCE");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Сверка выписки: Excel не сохранён ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            SetBusy(false, T("Не удалось сохранить файл.", "Файлды сактоо мүмкүн болгон жок.", "Could not save the file.", "Dosya kaydedilemedi.", "Faylni saqlab bo'lmadi."), warn: true);
        }
    }
}
