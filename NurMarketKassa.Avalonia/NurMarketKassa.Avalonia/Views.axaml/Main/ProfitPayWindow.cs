using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-28, владелец: «Схема зарплат от чистой прибыли, от чистой маржи!». Окно «Зарплата от прибыли»: надбавка к зарплате
/// в процентах от чистой маржи или чистой прибыли магазина за период — по своим продажам, по общему результату или общим пулом
/// (по продажам / часам табеля / поровну). К тому, что насчитал сервер по схеме сотрудника, прибавляется надбавка; под каждой цифрой —
/// «Как посчитано» (формула и числа построчно). Цифры магазина — общая функция <see cref="ProfitReport"/>, те же, что в «Прибыли и
/// деньгах». Выплата зарплаты (расход «Зарплата») этим окном не делается — надбавка видна и выгружается в ведомость.
/// Открывается из раздела «Зарплата» (владелец и администратор — как вся зарплата).</summary>
public sealed class ProfitPayWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly DateTime _from;
    private readonly DateTime _to;
    private ProfitPayData? _data;
    private CancellationTokenSource? _cts;
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly Border _errorBox = new() { IsVisible = false, Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(10) };
    private readonly ProgressBar _loading = new() { IsIndeterminate = true, Height = 3, MinHeight = 3, IsVisible = false, VerticalAlignment = VerticalAlignment.Top };
    private readonly Button _settings, _refresh, _excel, _csv, _pdf, _close;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public ProfitPayWindow(DateTime from, DateTime to)
    {
        _from = from;
        _to = to;
        Title = T("Зарплата от прибыли", "Пайдадан эмгек акы", "Profit-based pay", "Kârdan maaş", "Foydadan ish haqi");
        Width = 1180;
        Height = 820;
        MinWidth = 900;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");
        EscapeKey.Attach(this);

        var title = new TextBlock { Text = Title, FontSize = 26, FontWeight = FontWeight.Bold };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, _status } };

        _settings = Outline(T("Настройки магазина", "Дүкөн жөндөөлөрү", "Shop settings", "Mağaza ayarları", "Do'kon sozlamalari"));
        _refresh = Outline(T("Обновить", "Жаңылоо", "Refresh", "Yenile", "Yangilash"));
        _excel = Outline("Excel");
        _csv = Outline("CSV");
        _pdf = Outline("PDF");
        _close = Outline(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        _close.IsCancel = true;
        _settings.Click += async (_, _) => await EditShopAsync();
        _refresh.Click += async (_, _) => await LoadAsync();
        _excel.Click += async (_, _) => await ExportAsync("xlsx");
        _csv.Click += async (_, _) => await ExportAsync("csv");
        _pdf.Click += async (_, _) => await ExportAsync("pdf");
        _close.Click += (_, _) => Close();
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in new[] { _settings, _refresh, _excel, _csv, _pdf, _close })
        {
            b.Margin = new Thickness(0, 0, 8, 6);
            buttons.Children.Add(b);
        }
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(left);
        Grid.SetColumn(buttons, 1);
        header.Children.Add(buttons);

        Use(_error, TextBlock.ForegroundProperty, "BrushDangerOnSoft");
        _errorBox.Child = _error;
        Use(_errorBox, Border.BackgroundProperty, "BrushDangerSoft");
        _errorBox.Margin = new Thickness(0, 0, 0, 10);

        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        var top = new StackPanel { Children = { header, _errorBox } };
        root.Children.Add(top);
        var scroll = new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        _body.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        _loading.ZIndex = 100;
        root.Children.Add(_loading);
        Content = root;
        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    /// <summary>В разделе владельца (AsOwnerSection окна «Зарплата») окно тоже отдельное, поэтому отдельной вставки не нужно.</summary>
    public static void Open(Window? owner, DateTime from, DateTime to)
    {
        var w = new ProfitPayWindow(from, to);
        if (owner != null)
            w.Show(owner);
        else
            w.Show();
    }

    private Button Outline(string text)
    {
        var b = new Button { Content = text };
        b.Classes.Add("sec-outline");
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) => target.Bind(property, this.GetResourceObservable(key));

    private void ShowError(string? text)
    {
        _error.Text = text ?? "";
        _errorBox.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private static string M(decimal v) => ProfitPayCalculator.M(v);

    // ───────────────────────────── загрузка ─────────────────────────────

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _loading.IsVisible = true;
        ShowError(null);
        _status.Text = T("Загрузка с сервера NurCRM…", "NurCRM серверинен жүктөлүүдө…", "Loading from the NurCRM server…", "NurCRM sunucusundan yükleniyor…", "NurCRM serveridan yuklanmoqda…");
        try
        {
            var data = await ProfitPayData_LoadAsync(cts.Token);
            if (cts.IsCancellationRequested)
                return;
            _data = data;
            Render();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Зарплата от прибыли: не загружено: {ex}", "WARNING");
            ShowError(T("Не удалось загрузить данные", "Маалыматтарды жүктөө мүмкүн болгон жок", "Could not load the data", "Veriler yüklenemedi", "Ma'lumotlarni yuklab bo'lmadi") + ": " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
                _loading.IsVisible = false;
        }
    }

    /// <summary>Тестовая точка: стенд подменяет загрузку готовыми данными.</summary>
    internal static Func<DateTime, DateTime, CancellationToken, Task<ProfitPayData>>? LoadOverride { get; set; }

    private Task<ProfitPayData> ProfitPayData_LoadAsync(CancellationToken ct) =>
        LoadOverride is { } o ? o(_from, _to, ct) : ProfitPayData.LoadAsync(_from, _to, ct);

    internal ProfitPayData? Data => _data;

    // ───────────────────────────── вид ─────────────────────────────

    private void Render()
    {
        _body.Children.Clear();
        var d = _data!;
        var days = (_to - _from).Days + 1;
        _status.Text = T($"Период: {_from:dd.MM.yyyy} — {_to:dd.MM.yyyy} ({days} дн.). К тому, что насчитал сервер по схеме сотрудника, прибавляется надбавка от прибыли.",
            $"Мезгил: {_from:dd.MM.yyyy} — {_to:dd.MM.yyyy} ({days} күн). Сервер кызматкердин схемасы боюнча эсептегенге пайдадан кошумча кошулат.",
            $"Period: {_from:dd.MM.yyyy} — {_to:dd.MM.yyyy} ({days} days). The profit-based extra is added to what the server accrued by the employee's scheme.",
            $"Dönem: {_from:dd.MM.yyyy} — {_to:dd.MM.yyyy} ({days} gün). Sunucunun çalışanın şemasına göre hesapladığına kârdan ek eklenir.",
            $"Davr: {_from:dd.MM.yyyy} — {_to:dd.MM.yyyy} ({days} kun). Server xodim sxemasi bo'yicha hisoblaganiga foydadan ustama qo'shiladi.");
        var ready = d.Snapshot is not null && d.Result is not null;
        foreach (var b in new[] { _excel, _csv, _pdf })
            b.IsEnabled = ready && d.Rows.Count > 0;
        if (!ready)
        {
            _body.Children.Add(SoftText(T("Отчёт «Прибыль» не получен — сервер не ответил или ещё не поддерживает этот отчёт. Зарплату от прибыли посчитать нельзя: цифры не придумываем.",
                "«Пайда» отчёту алынган жок — сервер жооп берген жок. Пайдадан эмгек акыны эсептөөгө болбойт: сандарды ойлоп таппайбыз.",
                "The “Profit” report was not received — the server did not respond. Profit-based pay cannot be calculated: we do not make figures up.",
                "«Kâr» raporu alınamadı — sunucu yanıt vermedi. Kârdan maaş hesaplanamaz: rakam uydurmayız.",
                "«Foyda» hisoboti olinmadi — server javob bermadi. Foydadan ish haqini hisoblab bo'lmaydi: raqam to'qimaymiz.")));
            return;
        }
        var res = d.Result!;
        _body.Children.Add(ShopCard(res, d));

        var list = new StackPanel { Spacing = 0 };
        var head = new TextBlock { Text = T("Сотрудники", "Кызматкерлер", "Employees", "Çalışanlar", "Xodimlar"), FontSize = 16, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 8) };
        Use(head, TextBlock.ForegroundProperty, "BrushText");
        list.Children.Add(head);
        if (d.Rows.Count == 0)
        {
            list.Children.Add(SoftText(T("За период нет сотрудников с продажами или схемой. Схему сотруднику задают в разделе «Зарплата» (кнопка «Схема»), а надбавку от прибыли — здесь.",
                "Мезгилде сатуусу же схемасы бар кызматкер жок. Схеманы «Эмгек акы» бөлүмүндө коёсуз, пайдадан кошумчаны — бул жерде.",
                "No employees with sales or a scheme in this period. The server scheme is set in “Salary” (the “Scheme” button), the profit-based extra — here.",
                "Dönemde satışı veya şeması olan çalışan yok. Sunucu şeması «Maaş»ta, kârdan ek burada ayarlanır.",
                "Davrda sotuvi yoki sxemasi bor xodim yo'q. Server sxemasi «Ish haqi»da, foydadan ustama — shu yerda.")));
        }
        else
        {
            list.Children.Add(RowHeader());
            foreach (var r in d.Rows)
                list.Children.Add(EmployeeRow(r, d));
            list.Children.Add(TotalRow(d.Rows));
        }
        _body.Children.Add(Card(list));
        if (!d.SalaryLoaded)
            _body.Children.Add(SoftText(T("Зарплата по схемам сервера не получена — «Начислено по схеме» не показано, считается только надбавка от прибыли.",
                "Сервердин схемалары боюнча эмгек акы алынган жок — пайдадан кошумча гана эсептелет.",
                "The server-scheme pay was not received — only the profit-based extra is calculated.",
                "Sunucu şemalarına göre maaş alınamadı — yalnızca kârdan ek hesaplanır.",
                "Server sxemalari bo'yicha ish haqi olinmadi — faqat foydadan ustama hisoblanadi.")));
        if (d.Doc.Shop.Share == ProfitPayShare.Hours && !d.HoursLoaded)
            _body.Children.Add(SoftText(T("Табель (смены) не получен — деление пула по часам невозможно.", "Табель (смендер) алынган жок — пулду саат боюнча бөлүүгө болбойт.", "The timesheet (shifts) was not received — the pool cannot be split by hours.", "Puantaj (vardiyalar) alınamadı — havuz saate göre bölünemez.", "Tabel (smenalar) olinmadi — pulni soat bo'yicha bo'lib bo'lmaydi.")));
        _body.Children.Add(SoftText(ProfitStoreNote()));
        if (res.ShopSteps.Count > 0 && (d.Snapshot?.Notes.Count ?? 0) > 0)
            _body.Children.Add(SoftText(string.Join("; ", d.Snapshot!.Notes)));
    }

    private static string ProfitStoreNote() => ProfitPayStore.State switch
    {
        ProfitPayStore.SyncState.Forbidden => T("Схемы сохранены только на этом компьютере: общие настройки магазина меняет владелец или администратор.", "Схемалар ушул компьютерде гана сакталды: дүкөндүн жалпы жөндөөлөрүн ээси же администратор өзгөртөт.", "Schemes are saved on this computer only: shop-wide settings are changed by the owner or an administrator.", "Şemalar yalnızca bu bilgisayarda: genel ayarları sahip veya yönetici değiştirir.", "Sxemalar faqat shu kompyuterda: umumiy sozlamalarni egasi yoki administrator o'zgartiradi."),
        ProfitPayStore.SyncState.LocalOnly => T("Нет связи с сервером: схемы сохранены на этом компьютере и уйдут при следующем сохранении.", "Сервер менен байланыш жок: схемалар ушул компьютерде сакталды, кийинки сактоодо жөнөтүлөт.", "No connection: schemes are saved on this computer and will be sent on the next save.", "Bağlantı yok: şemalar bu bilgisayarda, bir sonraki kayıtta gönderilir.", "Aloqa yo'q: sxemalar shu kompyuterda saqlandi, keyingi saqlashda yuboriladi."),
        _ => T("Схемы «от прибыли» хранятся в общих настройках магазина NurCRM — их видят все компьютеры владельца и администраторов.", "«Пайдадан» схемалары NurCRM дүкөнүнүн жалпы жөндөөлөрүндө сакталат — аларды ээсинин жана администраторлордун бардык компьютерлери көрөт.", "“Profit-based” schemes are stored in the shop's shared NurCRM settings — all owner and administrator computers see them.", "«Kârdan» şemaları NurCRM mağaza ortak ayarlarında saklanır — sahip ve yöneticilerin tüm bilgisayarları görür.", "«Foydadan» sxemalari NurCRM do'konining umumiy sozlamalarida saqlanadi — egasi va administratorlarning barcha kompyuterlari ko'radi."),
    };

    private TextBlock SoftText(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private Border Card(Control child)
    {
        var card = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(18, 16), Child = child };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        Use(card, Border.CornerRadiusProperty, "SettingsCardRadius");
        return card;
    }

    private Control KeyValue(string key, string value, bool bold = false, bool loss = false)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 3) };
        var a = new TextBlock { Text = key, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal };
        var b = new TextBlock { Text = value, FontSize = 13.5, FontWeight = bold ? FontWeight.Bold : FontWeight.SemiBold, Margin = new Thickness(12, 0, 0, 0) };
        b.FontFeatures = Tabular;
        Use(a, TextBlock.ForegroundProperty, "BrushText");
        Use(b, TextBlock.ForegroundProperty, loss ? "BrushDanger" : "BrushText");
        Grid.SetColumn(b, 1);
        g.Children.Add(a);
        g.Children.Add(b);
        return g;
    }

    private static readonly FontFeatureCollection Tabular = new() { new FontFeature { Tag = "tnum", Value = 1 } };

    private Control ShopCard(ProfitPayResult res, ProfitPayData d)
    {
        var p = new StackPanel { Spacing = 2 };
        var head = new TextBlock { Text = T("Результат магазина за период", "Мезгилдеги дүкөндүн жыйынтыгы", "Shop result for the period", "Dönem mağaza sonucu", "Davr uchun do'kon natijasi"), FontSize = 16, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 6) };
        Use(head, TextBlock.ForegroundProperty, "BrushText");
        p.Children.Add(head);
        p.Children.Add(KeyValue(T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), M(res.Revenue)));
        p.Children.Add(KeyValue("− " + T("Закупочная цена проданного", "Сатылгандын сатып алуу баасы", "Purchase cost of goods sold", "Satılanların alış fiyatı", "Sotilganlarning xarid narxi"), M(res.Cogs)));
        p.Children.Add(KeyValue(ProfitTerms.Gross, M(res.Gross), bold: true));
        if (res.Returns > 0)
            p.Children.Add(KeyValue("− " + T("Возвраты", "Кайтаруулар", "Refunds", "İadeler", "Qaytarishlar"), M(res.Returns)));
        p.Children.Add(KeyValue(T("Чистая маржа", "Таза маржа", "Net margin", "Net marj", "Sof marja"), M(res.Margin), bold: true, loss: res.Margin < 0));
        foreach (var (t, a) in res.ExpensesSubtracted)
            p.Children.Add(KeyValue("− " + t, M(a)));
        foreach (var (t, a) in res.ExpensesSkipped)
            p.Children.Add(KeyValue(t + " (" + T("не вычитается", "алынбайт", "not subtracted", "düşülmez", "ayirilmaydi") + ")", M(a)));
        p.Children.Add(KeyValue(ProfitTerms.Net, M(res.NetProfit), bold: true, loss: res.NetProfit < 0));
        if (res.NetProfit < 0 || res.Margin < 0)
            p.Children.Add(SoftText(T("Прибыль отрицательная — надбавка от прибыли за период равна 0, зарплата в минус не уходит.", "Пайда терс — мезгилдеги пайдадан кошумча 0, эмгек акы минуска кетпейт.", "Profit is negative — the profit-based extra is 0 for the period, pay never goes negative.", "Kâr negatif — dönem için kârdan ek 0, maaş eksiye düşmez.", "Foyda manfiy — davr uchun foydadan ustama 0, ish haqi manfiyga tushmaydi.")));
        p.Children.Add(Steps(res.ShopSteps));
        if (res.PoolAmount > 0 || d.Doc.Schemes.Values.Any(s => s.Active && s.Scope == ProfitPayScope.Pool))
            p.Children.Add(KeyValue(T("Общий пул", "Жалпы пул", "Shared pool", "Ortak havuz", "Umumiy pul"), M(res.PoolAmount)));
        return Card(p);
    }

    private Control Steps(IReadOnlyList<string> steps)
    {
        var panel = new StackPanel { Spacing = 2, IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var s in steps)
            panel.Children.Add(SoftText(s));
        var toggle = new Button { Content = "▸ " + T("Как посчитано", "Кантип эсептелди", "How it was calculated", "Nasıl hesaplandı", "Qanday hisoblandi"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 6), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        Use(toggle, Button.ForegroundProperty, "BrushText");
        toggle.Click += (_, _) =>
        {
            panel.IsVisible = !panel.IsVisible;
            toggle.Content = (panel.IsVisible ? "▾ " : "▸ ") + T("Как посчитано", "Кантип эсептелди", "How it was calculated", "Nasıl hesaplandı", "Qanday hisoblandi");
        };
        Avalonia.Automation.AutomationProperties.SetName(toggle, T("Как посчитано", "Кантип эсептелди", "How it was calculated", "Nasıl hesaplandı", "Qanday hisoblandi"));
        return new StackPanel { Children = { toggle, panel } };
    }

    private const string Cols = "2*,1.2*,1.2*,1.2*,1.2*,110";

    private Control RowHeader()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(Cols), Margin = new Thickness(0, 0, 0, 4) };
        void H(string t, int c)
        {
            var tb = new TextBlock { Text = t, FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            Use(tb, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(tb, c);
            g.Children.Add(tb);
        }
        H(T("Сотрудник и схема", "Кызматкер жана схема", "Employee and scheme", "Çalışan ve şema", "Xodim va sxema"), 0);
        H(T("Начислено по схеме, сом", "Схема боюнча эсептелди, сом", "Accrued by scheme, som", "Şemaya göre tahakkuk, som", "Sxema bo'yicha, so'm"), 1);
        H(T("База (маржа / прибыль)", "База (маржа / пайда)", "Base (margin / profit)", "Taban (marj / kâr)", "Baza (marja / foyda)"), 2);
        H(T("От прибыли, сом", "Пайдадан, сом", "From profit, som", "Kârdan, som", "Foydadan, so'm"), 3);
        H(T("Итого, сом", "Жыйынтыгы, сом", "Total, som", "Toplam, som", "Jami, so'm"), 4);
        return g;
    }

    private Control EmployeeRow(ProfitPayExport.Row r, ProfitPayData d)
    {
        var userId = r.UserId;
        d.Doc.Schemes.TryGetValue(userId, out var scheme);
        var line = d.Result!.Lines.FirstOrDefault(l => string.Equals(l.UserId, userId, StringComparison.OrdinalIgnoreCase));

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(Cols), Margin = new Thickness(0, 8, 0, 0) };
        var name = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 12, 0) };
        var n = new TextBlock { Text = r.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Use(n, TextBlock.ForegroundProperty, "BrushText");
        name.Children.Add(n);
        name.Children.Add(SoftText(r.SchemeText.Length > 0 ? r.SchemeText : T("схема сервера не настроена", "сервер схемасы жөндөлгөн эмес", "no server scheme", "sunucu şeması yok", "server sxemasi yo'q")));
        Grid.SetColumn(name, 0);
        g.Children.Add(name);

        void Cell(string text, int col, bool bold = false, bool soft = false)
        {
            var tb = new TextBlock { Text = text, FontSize = 14, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Top, TextWrapping = TextWrapping.Wrap };
            tb.FontFeatures = Tabular;
            Use(tb, TextBlock.ForegroundProperty, soft ? "BrushTextSoft" : "BrushText");
            Grid.SetColumn(tb, col);
            g.Children.Add(tb);
        }
        Cell(M(r.Server), 1);
        Cell(line is not null && scheme is { Active: true } ? M(line.BaseValue) : "—", 2, soft: true);
        Cell(M(r.Profit), 3, bold: true);
        Cell(M(r.Total), 4, bold: true);

        var edit = Outline(T("Схема", "Схема", "Scheme", "Şema", "Sxema"));
        edit.HorizontalAlignment = HorizontalAlignment.Stretch;
        edit.Click += async (_, _) => await EditSchemeAsync(userId, r.Name, scheme);
        Grid.SetColumn(edit, 5);
        edit.VerticalAlignment = VerticalAlignment.Top;
        g.Children.Add(edit);

        var box = new StackPanel { Spacing = 2 };
        box.Children.Add(g);
        if (r.Note.Length > 0)
        {
            var note = new TextBlock { Text = "⚠ " + r.Note, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            Use(note, TextBlock.ForegroundProperty, "BrushWarning");
            box.Children.Add(note);
        }
        if (r.Steps.Count > 0)
            box.Children.Add(Steps(r.Steps));
        var border = new Border { Child = box, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 8) };
        Use(border, Border.BorderBrushProperty, "BrushBorder");
        return border;
    }

    private Control TotalRow(IReadOnlyList<ProfitPayExport.Row> rows)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(Cols), Margin = new Thickness(0, 10, 0, 0) };
        void C(string t, int c)
        {
            var tb = new TextBlock { Text = t, FontSize = 14.5, FontWeight = FontWeight.Bold };
            tb.FontFeatures = Tabular;
            Use(tb, TextBlock.ForegroundProperty, "BrushText");
            Grid.SetColumn(tb, c);
            g.Children.Add(tb);
        }
        C(ReportLabels.Total, 0);
        C(M(rows.Sum(x => x.Server)), 1);
        C(M(rows.Sum(x => x.Profit)), 3);
        C(M(rows.Sum(x => x.Total)), 4);
        return g;
    }

    // ───────────────────────────── действия ─────────────────────────────

    private async Task EditSchemeAsync(string userId, string name, ProfitPayScheme? scheme)
    {
        if (_data is null || userId.Length == 0)
            return;
        var current = scheme?.Clone() ?? new ProfitPayScheme { UserId = userId, Name = name, Rule = new ProfitPayRule { Base = ProfitPayBase.Margin } };
        current.Name = name;
        var saved = await new ProfitPaySchemeDialog(current).ShowDialog<ProfitPayScheme?>(this);
        if (saved is null)
            return;
        saved.UserId = userId;
        saved.Name = name;
        _data.Doc = await ProfitPayStore.SaveSchemeAsync(saved);
        PosLogger.Log($"Зарплата от прибыли: схема сохранена: user={userId}, база={saved.Rule.Base}, область={saved.Scope}, %={saved.Rule.Percent}", "INFO");
        _data.Recompute();
        Render();
    }

    private async Task EditShopAsync()
    {
        if (_data is null)
            return;
        var saved = await new ProfitPayShopDialog(_data.Doc.Shop).ShowDialog<ProfitPayShopSettings?>(this);
        if (saved is null)
            return;
        _data.Doc = await ProfitPayStore.SaveShopAsync(saved);
        PosLogger.Log("Зарплата от прибыли: общие настройки сохранены.", "INFO");
        _data.Recompute();
        Render();
    }

    private async Task ExportAsync(string ext)
    {
        if (_data?.Result is not { } res || _data.Rows.Count == 0)
            return;
        var shop = UserPreferences.Instance.StoreName;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Сохранить ведомость", "Ведомостту сактоо", "Save the statement", "Bordroyu kaydet", "Qaydnomani saqlash"),
            SuggestedFileName = ProfitPayExport.SuggestFileName(_from, _to, shop, ext),
            FileTypeChoices = [new FilePickerFileType(ext.ToUpperInvariant()) { Patterns = [$"*.{ext}"] }],
        });
        if (file is null)
            return;
        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            ShowError(T("Не удалось определить путь файла — выберите папку на этом компьютере.", "Файлдын жолун аныктоо мүмкүн болгон жок — ушул компьютерден папка тандаңыз.", "Could not determine the file path — choose a folder on this computer.", "Dosya yolu belirlenemedi — bu bilgisayarda bir klasör seçin.", "Fayl yo'lini aniqlab bo'lmadi — shu kompyuterdagi papkani tanlang."));
            return;
        }
        var rows = _data.Rows;
        try
        {
            await Task.Run(() =>
            {
                switch (ext)
                {
                    case "xlsx": ProfitPayExport.ExportExcel(path, res, rows, _from, _to, shop); break;
                    case "csv": ProfitPayExport.ExportCsv(path, res, rows, _from, _to); break;
                    default: ProfitPayExport.ExportPdf(path, res, rows, _from, _to, shop); break;
                }
            }).ConfigureAwait(true);
            ShowError(null);
            _status.Text = T("Ведомость сохранена", "Ведомость сакталды", "Statement saved", "Bordro kaydedildi", "Qaydnoma saqlandi") + ": " + path;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Зарплата от прибыли: выгрузка не удалась: {ex}", "WARNING");
            ShowError(T("Не удалось сохранить ведомость", "Ведомостту сактоо мүмкүн болгон жок", "Could not save the statement", "Bordro kaydedilemedi", "Qaydnomani saqlab bo'lmadi") + ": " + ex.Message);
        }
    }
}
