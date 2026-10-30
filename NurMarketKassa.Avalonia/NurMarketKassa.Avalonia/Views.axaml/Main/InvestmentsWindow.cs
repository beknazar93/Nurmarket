using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Services;
using ChartPalette = NurMarketKassa.Core.Application.ChartPalette;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-29, владелец: «Для аналитики как расход на начало бизнеса» + макет design/Investments/Main.dc.html — раздел программы
/// владельца «Инвестиции и окупаемость»: окупаемость бизнеса, инвесторов и владельца, стартовые расходы, участники, условия инвестора по
/// сфере магазина (шаблоны, этапы выплат, убыточный месяц, ограничения), симулятор и ведомость выплат (Excel/CSV/PDF).
/// Цифры — только реальные: чистая прибыль по месяцам — отчёт сервера NurCRM «Прибыль» (та же единая функция <see cref="NetProfitCalculator"/>,
/// что в «Прибыли и деньгах»); вложения и условия — общий документ компании (<see cref="StartupCostsStore"/>). Расчёты — InvestmentAnalysis.
/// Окно собрано в коде, как «Прибыль и деньги»; цвета — из темы, диаграммы — ChartPalette. Файлы окна: этот (оболочка, загрузка, общие
/// части), .Dashboard, .Costs, .Terms, .Payouts.</summary>
public sealed partial class InvestmentsWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private enum Tab { Dashboard, Costs, Terms, Payouts }

    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl _body = new();
    private readonly ScrollViewer _scroll = new();
    private Tab _tab = Tab.Dashboard;
    private CancellationTokenSource? _cts;
    private int _loadGen;

    // Данные: вложения и условия — из общего документа, месяцы — из отчёта сервера.
    private List<PaybackMonth>? _months;
    private DateTime? _monthsStart;
    private string? _loadError;
    private bool _loading;
    private decimal? _stockCost;
    private InvestmentResult? _result;
    private int _forecastMonths = StartupPayback.ForecastMonths;

    public InvestmentsWindow()
    {
        Title = T("Инвестиции и окупаемость", "Инвестициялар жана кайтарым", "Investments and payback", "Yatırımlar ve geri dönüş", "Investitsiyalar va o'zini oqlash");
        Width = 1200;
        Height = 820;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        var root = new Grid { Margin = OwnerSectionLayout.Margin, RowDefinitions = new RowDefinitions("Auto,*") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 14) };
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(0, 0, 16, 0);
        var seg = UiKit.Segment(_tabs);
        Grid.SetColumn(seg, 1);
        header.Children.Add(_status);
        header.Children.Add(seg);
        root.Children.Add(header);

        _scroll.Content = _body;
        _scroll.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        _scroll.Padding = new Thickness(0, 0, 14, 0);
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);
        Content = root;

        BuildTabs();
        StartupCostsStore.Changed += OnStoreChanged;
        Opened += (_, _) => _ = ReloadAsync();
        Closed += (_, _) =>
        {
            StartupCostsStore.Changed -= OnStoreChanged;
            _cts?.Cancel();
        };
    }

    // ───────────────────────────── вкладки ─────────────────────────────

    private void BuildTabs()
    {
        _tabs.Children.Clear();
        void Add(Tab tab, string text)
        {
            var b = UiKit.Seg(text, tab == _tab);
            b.Click += (_, _) =>
            {
                if (_tab == tab)
                    return;
                if (_tab == Tab.Terms && _termsDirty && !PosDialogs.ConfirmYesNo(this,
                        T("Условия инвестора изменены и не сохранены. Уйти без сохранения?", "Инвестордун шарттары өзгөрдү, бирок сакталган жок. Сактабай чыгасызбы?",
                            "The investor terms are changed but not saved. Leave without saving?", "Yatırımcı koşulları değişti ama kaydedilmedi. Kaydetmeden çıkılsın mı?",
                            "Investor shartlari o'zgargan, lekin saqlanmagan. Saqlamasdan chiqasizmi?"),
                        null, T("Уйти", "Чыгуу", "Leave", "Çık", "Chiqish"), T("Остаться", "Калуу", "Stay", "Kal", "Qolish")))
                    return;
                GoTo(tab);
            };
            _tabs.Children.Add(b);
        }
        Add(Tab.Dashboard, T("Окупаемость", "Кайтарым", "Payback", "Geri dönüş", "O'zini oqlash"));
        Add(Tab.Costs, T("Вложения", "Салымдар", "Investments", "Yatırımlar", "Sarmoyalar"));
        Add(Tab.Terms, T("Условия инвестора", "Инвестордун шарттары", "Investor terms", "Yatırımcı koşulları", "Investor shartlari"));
        Add(Tab.Payouts, T("Выплаты", "Төлөмдөр", "Payouts", "Ödemeler", "To'lovlar"));
    }

    private void GoTo(Tab tab)
    {
        _tab = tab;
        BuildTabs();
        RenderTab();
        _scroll.Offset = default;
    }

    private void RenderTab()
    {
        EnsureColors();
        switch (_tab)
        {
            case Tab.Dashboard: _body.Content = BuildDashboard(); break;
            case Tab.Costs: _body.Content = BuildCosts(); break;
            case Tab.Terms: _body.Content = BuildTerms(); break;
            default: _body.Content = BuildPayouts(); break;
        }
        UpdateStatus();
    }

    // ───────────────────────────── загрузка ─────────────────────────────

    private void OnStoreChanged()
    {
        // Рисуем заново, если не правятся условия (черновик не теряем); дата начала сдвинулась раньше — месяцы грузим заново.
        var start = InvestmentAnalysis.AnalysisStart(StartupCostsStore.Items, StartupCostsStore.Meta, StartupCostsStore.Investors);
        if (start != _monthsStart && !_loading)
        {
            _ = ReloadAsync();
            return;
        }
        Recompute();
        if (_tab == Tab.Terms && _termsDirty)
            UpdateStatus();
        else
            RenderTab();
    }

    private async Task ReloadAsync()
    {
        var gen = ++_loadGen;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loading = true;
        _loadError = null;
        UpdateStatus();
        try
        {
            await StartupCostsStore.RefreshAsync(ct).ConfigureAwait(true);
            var costs = StartupCostsStore.Items;
            var meta = StartupCostsStore.Meta;
            var investors = StartupCostsStore.Investors;
            var start = InvestmentAnalysis.AnalysisStart(costs, meta, investors);
            _monthsStart = start;
            _months = null;
            _stockCost = null;
            if (start is { } s)
            {
                try
                {
                    _months = await StartupPaybackLoader.LoadAsync(StartupPaybackLoader.PnlOverride != null ? null : App.GetRequiredService<NurMarketApiClient>(), s, DateTime.Today, ct).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Инвестиции: отчёт прибыли по месяцам не получен: {ex.Message}", "WARNING");
                    _loadError = ex is InvalidOperationException ? ex.Message
                        : T("Не удалось получить отчёт «Прибыль» с сервера NurCRM — окупаемость не показана (данные не выдумываем). Проверьте связь и обновите.",
                            "NurCRM серверинен «Пайда» отчетун алуу мүмкүн болгон жок — кайтарым көрсөтүлгөн жок. Байланышты текшерип, жаңыртыңыз.",
                            "Could not get the “Profit” report from the NurCRM server — payback is not shown (we do not invent figures). Check the connection and refresh.",
                            "NurCRM sunucusundan «Kâr» raporu alınamadı — geri dönüş gösterilmiyor (rakam uydurmuyoruz). Bağlantıyı kontrol edip yenileyin.",
                            "NurCRM serveridan «Foyda» hisoboti olinmadi — o'zini oqlash ko'rsatilmadi (raqam o'ylab topilmaydi). Aloqani tekshirib, yangilang.");
                }
                if (_months != null && investors.Any(i => i.Terms.StockCredit))
                    _stockCost = await StockCostAsync(ct).ConfigureAwait(true);
            }
            if (gen != _loadGen)
                return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (gen == _loadGen)
                _loading = false;
        }
        Recompute();
        RenderTab();
    }

    /// <summary>Товарный остаток по закупочной цене — для строки «засчитывать остаток» (только по условию инвестора).</summary>
    private static async Task<decimal?> StockCostAsync(CancellationToken ct)
    {
        try
        {
            var rows = await OwnerShopFacts.StockRowsAsync(ct).ConfigureAwait(true);
            return (decimal)rows.Where(r => r.Stock > 0).Sum(r => r.Stock * r.Cost);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Инвестиции: товарный остаток не получен: {ex.Message}", "WARNING");
            return null;
        }
    }

    private void Recompute()
    {
        _result = null;
        if (_months == null)
            return;
        try
        {
            _result = InvestmentAnalysis.Build(StartupCostsStore.Items, StartupCostsStore.Meta, StartupCostsStore.Investors, _months, _stockCost, DateTime.Today, _forecastMonths);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Инвестиции: расчёт не удался: {ex}", "WARNING");
            _loadError = T("Не удалось посчитать окупаемость: ", "Кайтарымды эсептөө мүмкүн болгон жок: ", "Could not calculate payback: ", "Geri dönüş hesaplanamadı: ", "O'zini oqlashni hisoblab bo'lmadi: ") + ex.Message;
        }
    }

    private void UpdateStatus()
    {
        var sync = StartupCostsStore.State switch
        {
            StartupCostsStore.SyncState.Saving => T("Сохраняю…", "Сакталууда…", "Saving…", "Kaydediliyor…", "Saqlanmoqda…"),
            StartupCostsStore.SyncState.Saved => T("Сохранено в NurCRM (общие настройки компании).", "NurCRMде сакталды (компаниянын жалпы жөндөөлөрү).",
                "Saved in NurCRM (company shared settings).", "NurCRM'de kaydedildi (şirket ortak ayarları).", "NurCRMda saqlandi (kompaniyaning umumiy sozlamalari)."),
            StartupCostsStore.SyncState.LocalOnly => T("Нет связи с сервером: данные сохранены на этом компьютере и отправятся позже.", "Сервер менен байланыш жок: маалымат ушул компьютерде сакталды, кийин жөнөтүлөт.",
                "No connection: data is saved on this computer and will be sent later.", "Bağlantı yok: veriler bu bilgisayarda kayıtlı, sonra gönderilecek.", "Aloqa yo'q: ma'lumot shu kompyuterda saqlandi, keyin yuboriladi."),
            StartupCostsStore.SyncState.Forbidden => T("Только просмотр: менять вложения и условия может владелец или администратор.", "Көрүү гана: салымдарды жана шарттарды ээси же администратор өзгөртөт.",
                "View only: only the owner or an administrator can change investments and terms.", "Salt görüntüleme: yatırımları ve koşulları yalnızca sahip veya yönetici değiştirebilir.", "Faqat ko'rish: sarmoya va shartlarni egasi yoki administrator o'zgartiradi."),
            StartupCostsStore.SyncState.Damaged => StartupCostsStore.Notice ?? "",
            _ => "",
        };
        _status.Text = _loading
            ? T("Загрузка отчёта «Прибыль» по месяцам с сервера NurCRM…", "NurCRM серверинен айлар боюнча «Пайда» отчету жүктөлүүдө…", "Loading the monthly “Profit” report from the NurCRM server…",
                "NurCRM sunucusundan aylık «Kâr» raporu yükleniyor…", "NurCRM serveridan oylar bo'yicha «Foyda» hisoboti yuklanmoqda…")
            : T("Чистая прибыль — из отчёта «Прибыль и деньги» (сервер NurCRM). Стартовые расходы в неё не входят. ", "Таза пайда — «Пайда жана акча» отчетунан (NurCRM). Баштапкы чыгымдар анын ичине кирбейт. ",
                "Net profit comes from the “Profit & cash” report (NurCRM server). Startup costs are not part of it. ", "Net kâr «Kâr ve nakit» raporundandır (NurCRM). Başlangıç giderleri buna dahil değildir. ",
                "Sof foyda «Foyda va pul» hisobotidan (NurCRM). Boshlang'ich xarajatlar unga kirmaydi. ") + sync;
    }

    // ───────────────────────────── экспорт ─────────────────────────────

    private async Task ExportAsync(string kind, bool payouts)
    {
        var shop = UserPreferences.Instance.StoreName;
        var name = payouts ? InvestmentExport.SuggestFileName(shop, kind) : StartupCostsExport.SuggestFileName(shop, kind);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Сохранить отчёт", "Отчётту сактоо", "Save the report", "Raporu kaydet", "Hisobotni saqlash"),
            SuggestedFileName = name,
            FileTypeChoices = [new FilePickerFileType(kind.ToUpperInvariant()) { Patterns = [$"*.{kind}"] }],
        }).ConfigureAwait(true);
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            var costs = StartupCostsStore.Items;
            var meta = StartupCostsStore.Meta;
            var result = _result;
            var payments = StartupCostsStore.Payments;
            await Task.Run(() =>
            {
                if (payouts)
                {
                    if (result == null)
                        throw new InvalidOperationException(T("Нет данных: отчёт «Прибыль» не загружен.", "Маалымат жок: «Пайда» отчету жүктөлгөн жок.", "No data: the “Profit” report is not loaded.",
                            "Veri yok: «Kâr» raporu yüklenmedi.", "Ma'lumot yo'q: «Foyda» hisoboti yuklanmadi."));
                    switch (kind)
                    {
                        case "xlsx": InvestmentExport.ToExcel(path, result, payments, shop); break;
                        case "csv": InvestmentExport.ToCsv(path, result, payments); break;
                        default: InvestmentExport.ToPdf(path, result, payments, shop); break;
                    }
                }
                else
                {
                    var payback = result?.Business;
                    switch (kind)
                    {
                        case "xlsx": StartupCostsExport.ToExcel(path, costs, meta, payback, shop); break;
                        case "csv": StartupCostsExport.ToCsv(path, costs, meta, payback); break;
                        default: StartupCostsExport.ToPdf(path, costs, meta, payback, shop); break;
                    }
                }
            }).ConfigureAwait(true);
            _status.Text = T("Файл сохранён: ", "Файл сакталды: ", "File saved: ", "Dosya kaydedildi: ", "Fayl saqlandi: ") + path;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Инвестиции: выгрузка не удалась: {ex}", "WARNING");
            _status.Text = T("Не удалось сохранить файл: ", "Файлды сактоо мүмкүн болгон жок: ", "Could not save the file: ", "Dosya kaydedilemedi: ", "Faylni saqlab bo'lmadi: ") + ex.Message;
        }
    }

    private Control ExportRow(bool payouts)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (kind, title) in new[] { ("xlsx", "Excel"), ("csv", "CSV"), ("pdf", "PDF") })
        {
            var b = UiKit.Ghost(this, title);
            b.MinWidth = 90;
            Avalonia.Automation.AutomationProperties.SetName(b, T("Скачать ", "Жүктөө ", "Download ", "İndir ", "Yuklab olish ") + title);
            b.Click += async (_, _) => await ExportAsync(kind, payouts).ConfigureAwait(true);
            row.Children.Add(b);
        }
        return row;
    }

    // ───────────────────────────── общие части оформления ─────────────────────────────

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));

    private Border Card(Control child, double padding = 18)
    {
        var card = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(padding, padding - 2), Child = child };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        Use(card, Border.CornerRadiusProperty, "SettingsCardRadius");
        return card;
    }

    private TextBlock Heading(string text, double size = 16)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushText");
        return t;
    }

    private TextBlock Plain(string text, double size = 13.5, bool bold = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        Use(t, TextBlock.ForegroundProperty, "BrushText");
        return t;
    }

    private TextBlock Soft(string text, double size = 12.5)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private TextBlock Danger(string text, double size = 13)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushDanger");
        return t;
    }

    /// <summary>Пустое состояние со вторым действием.</summary>
    private Control EmptyState(string text, string? buttonText = null, Action? click = null)
    {
        var panel = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 40, 24, 0) };
        var t = Soft(text, 14.5);
        t.TextAlignment = TextAlignment.Center;
        t.MaxWidth = 560;
        panel.Children.Add(t);
        if (buttonText != null && click != null)
        {
            var b = UiKit.Primary(this, buttonText);
            b.HorizontalAlignment = HorizontalAlignment.Center;
            b.Click += (_, _) => click();
            panel.Children.Add(b);
        }
        return panel;
    }

    /// <summary>Подписанное поле ввода.</summary>
    private static StackPanel Labeled(Control host, string label, Control input, double width = double.NaN)
    {
        var caption = new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        caption.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable("BrushTextSoft"));
        var p = new StackPanel { Spacing = 4 };
        if (!double.IsNaN(width))
            p.Width = width;
        p.Children.Add(caption);
        p.Children.Add(input);
        Avalonia.Automation.AutomationProperties.SetName(input, label);
        return p;
    }

    /// <summary>Поле суммы: запятая и точка — десятичные, дробные разрешены (CheckoutValidation.TryParseAmount); неверный ввод красится.</summary>
    private TextBox NumBox(decimal value, Action<decimal> set, string? watermark = null, double width = double.NaN, Action<string?>? problem = null)
    {
        var box = UiKit.Input(this, watermark ?? "0", 44);
        if (!double.IsNaN(width))
            box.Width = width;
        box.Text = value == 0m ? "" : value.ToString("0.##", Ru);
        var last = value; // 2026-10-31: TextChanged без реальной правки значения не должен ставить «изменено и не сохранено»
        box.TextChanged += (_, _) =>
        {
            var parse = CheckoutValidation.TryParseAmount(box.Text, out var v);
            if (parse == CheckoutValidation.AmountParse.Ok || parse == CheckoutValidation.AmountParse.Empty)
            {
                Use(box, TextBox.ForegroundProperty, "BrushText");
                problem?.Invoke(null);
                var parsed = parse == CheckoutValidation.AmountParse.Ok ? (decimal)Math.Round(v, 2, MidpointRounding.AwayFromZero) : 0m;
                if (parsed == last)
                    return;
                last = parsed;
                set(parsed);
            }
            else
            {
                Use(box, TextBox.ForegroundProperty, "BrushDanger");
                problem?.Invoke(CheckoutValidation.AmountProblemText(parse));
            }
        };
        return box;
    }

    /// <summary>Поле даты «дд.мм.гггг».</summary>
    private TextBox DateBox(DateTime? value, Action<DateTime?> set, double width = 150)
    {
        var box = UiKit.Input(this, T("дд.мм.гггг", "кк.аа.жжжж", "dd.mm.yyyy", "gg.aa.yyyy", "kk.oo.yyyy"), 44);
        box.Width = width;
        box.Text = value?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "";
        var lastDate = value?.Date; // 2026-10-31: без реальной правки даты — не «изменено»
        box.TextChanged += (_, _) =>
        {
            var d = ParseDate(box.Text);
            if (d != null || string.IsNullOrWhiteSpace(box.Text))
            {
                Use(box, TextBox.ForegroundProperty, "BrushText");
                if (d == lastDate)
                    return;
                lastDate = d;
                set(d);
            }
            else
                Use(box, TextBox.ForegroundProperty, "BrushDanger");
        };
        return box;
    }

    internal static DateTime? ParseDate(string? text) =>
        DateTime.TryParseExact((text ?? "").Trim(), new[] { "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : null;

    /// <summary>Горизонтальная полоса прогресса: заполненная часть цветом, остальное — фоном.</summary>
    private Control Bar(double percent, string hex, double height = 10)
    {
        percent = Math.Clamp(double.IsNaN(percent) ? 0 : percent, 0, 100);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{percent.ToString("0.####", CultureInfo.InvariantCulture)}*,{(100 - percent).ToString("0.####", CultureInfo.InvariantCulture)}*") };
        grid.Children.Add(new Border { Background = Brush.Parse(hex), CornerRadius = new CornerRadius(height / 2) });
        var track = new Border { Height = height, CornerRadius = new CornerRadius(height / 2), ClipToBounds = true, Child = grid };
        Use(track, Border.BackgroundProperty, "BrushSurfaceSubtle");
        return track;
    }

    /// <summary>Строка «подпись — значение» (для сводных блоков).</summary>
    private Control Pair(string label, string value, bool bold = false, bool danger = false)
    {
        var a = Soft(label, 13);
        a.VerticalAlignment = VerticalAlignment.Center;
        var b = new TextBlock { Text = value, FontSize = 13.5, FontWeight = bold ? FontWeight.Bold : FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Use(b, TextBlock.ForegroundProperty, danger ? "BrushDanger" : "BrushText");
        // Длинное значение («через 26 мес. · 04.2028») уходит под подпись, а не рвёт её по буквам в узкой карточке.
        if (value.Length > 16 || label.Length > 26)
        {
            b.HorizontalAlignment = HorizontalAlignment.Left;
            return new StackPanel { Spacing = 1, Margin = new Thickness(0, 2), Children = { a, b } };
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 28 };
        b.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(b, 1);
        grid.Children.Add(a);
        grid.Children.Add(b);
        return grid;
    }

    private static string Money(decimal v) => v.ToString("N2", Ru) + " " + T("сом", "сом", "som", "som", "so'm");
    private static string Money0(decimal v) => v.ToString("N0", Ru) + " " + T("сом", "сом", "som", "som", "so'm");

    private static string Short(decimal v)
    {
        var a = Math.Abs(v);
        var s = a >= 1_000_000m ? (a / 1_000_000m).ToString("0.#", Ru) + " " + T("млн", "млн", "M", "mn", "mln")
            : a >= 1000m ? Math.Round(a / 1000m).ToString("0", Ru) + T("к", "м", "k", "b", "ming") : a.ToString("0", Ru);
        return (v < 0m ? "−" : "") + s;
    }

    private static string MonthName(DateTime d) => d.ToString("MMMM yyyy", Ru);

    private static string MonthsText(decimal months) => months.ToString("0.#", Ru) + " " + T("мес.", "ай", "mo.", "ay", "oy");

    private Button ChipBtn(string text, bool selected, Action click)
    {
        var b = UiKit.Chip(this, text, selected);
        b.Click += (_, _) => click();
        return b;
    }

    private Control Wrap(IEnumerable<Control> items, double spacing = 8)
    {
        var p = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in items)
        {
            c.Margin = new Thickness(0, 0, spacing, spacing);
            p.Children.Add(c);
        }
        return p;
    }
}
