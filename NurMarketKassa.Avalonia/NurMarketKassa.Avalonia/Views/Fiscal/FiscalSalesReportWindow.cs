using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Fiscal;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-14 (П0-1, ответ Умара 13.11 вечер): раздел программы владельца «Продажи без фискального чека» — чеки периода, по которым нет пробитого фискального чека
/// (GET api/main/reports/sales-without-fiscal/?from&amp;to&amp;page&amp;page_size; период не больше 92 дней). Сверка «продажа в NurCRM есть, фискального чека нет» для бухгалтера и владельца.
/// Данные читаются страницами по 200 (до 25 страниц), в окне — по 50 строк на странице с «Назад / Далее»; Excel выгружает ВСЕ загруженные строки (кнопка оболочки владельца,
/// <see cref="IOwnerSectionExcel"/>). Ошибки сервера и «нет связи» — общий блок состояния (<see cref="StateBlock"/>) с «Повторить». Раздел виден, только когда у компании включена фискализация.
/// Ничего не меняет на сервере и ничего не шлёт в ККМ.</summary>
public sealed class FiscalSalesReportWindow : Window, IOwnerSectionRefresh, IOwnerSectionExcel
{
    public const int ServerPageSize = 200;
    public const int MaxServerPages = 25;
    public const int RowsPerPage = 50;

    private readonly StackPanel _periods = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly TextBlock _status = new() { Name = "FiscalReportStatus", FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly StackPanel _body = new() { Name = "FiscalReportBody", Spacing = 2 };
    private readonly Panel _stateHost = new() { Name = "FiscalReportState" };
    private readonly StackPanel _pager = new() { Name = "FiscalReportPager", Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
    private readonly TextBlock _pagerText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _prev;
    private readonly Button _next;
    private CancellationTokenSource? _cts;
    private int _days = 30;
    private int _page;
    private DateTime _loadedUtc = DateTime.UtcNow;
    private List<FiscalReportRow>? _rows;
    private int _serverCount;
    private bool _truncated;
    private (DateTime From, DateTime To) _range;
    private bool _preview;

    public FiscalSalesReportWindow()
    {
        Title = TitleText;
        Width = 1100;
        Height = 800;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        var root = new StackPanel { Margin = OwnerSectionLayout.Margin, Spacing = 6 };
        root.Children.Add(Soft(Tr.T(
            "Продажи периода, по которым нет фискального чека из ККМ: чек не пробит, ждёт пробития или касса получила ошибку. Сверьте их с кассой и пробейте или оформите возврат.",
            "Мезгилдеги ККМдин фискалдык чеги жок сатуулар: чек бектилген эмес, бектилүүнү күтүүдө же касса ката алды. Аларды касса менен салыштырып, бекитиңиз же кайтарууну тариздеңиз.",
            "Sales of the period without a fiscal receipt from the cash register: not fiscalized, waiting, or the kassa got an error. Check them against the kassa and fiscalize them or make a return.",
            "Dönemde ÖKC'den mali fişi olmayan satışlar: fiş kesilmedi, bekliyor veya kasa hata aldı. Kasayla karşılaştırıp fişi kesin veya iade düzenleyin.",
            "Davrdagi KKMdan fiskal cheki yo'q sotuvlar: chek urilmagan, kutmoqda yoki kassa xato oldi. Ularni kassa bilan solishtirib, uring yoki qaytarish rasmiylashtiring.")));
        var periods = UiKit.Segment(_periods);
        periods.Margin = new Thickness(0, 6, 0, 0);
        root.Children.Add(periods);
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        root.Children.Add(_status);
        root.Children.Add(_stateHost);
        root.Children.Add(new ScrollViewer
        {
            Content = _body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        });

        _prev = UiKit.Ghost(this, Tr.T("Назад", "Артка", "Back", "Geri", "Orqaga"));
        _prev.Name = "FiscalReportPrev";
        _next = UiKit.Ghost(this, Tr.T("Далее", "Кийинки", "Next", "İleri", "Keyingi"));
        _next.Name = "FiscalReportNext";
        _prev.Click += (_, _) => { if (_page > 0) { _page--; Render(); } };
        _next.Click += (_, _) => { _page++; Render(); };
        Use(_pagerText, TextBlock.ForegroundProperty, "BrushTextSoft");
        _pager.Children.Add(_prev);
        _pager.Children.Add(_pagerText);
        _pager.Children.Add(_next);
        root.Children.Add(_pager);
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        BuildPeriods();
        Opened += (_, _) => _ = LoadAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    public static string TitleText => Tr.T("Продажи без фискального чека", "Фискалдык чексиз сатуулар", "Sales without a fiscal receipt", "Mali fişsiz satışlar", "Fiskal chekisiz sotuvlar");

    private void BuildPeriods()
    {
        _periods.Children.Clear();
        foreach (var (days, text) in new[]
                 {
                     (1, Tr.T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun")),
                     (7, Tr.T("7 дней", "7 күн", "7 days", "7 gün", "7 kun")),
                     (30, Tr.T("30 дней", "30 күн", "30 days", "30 gün", "30 kun")),
                     (FiscalApi.MaxReportDays, Tr.T("92 дня", "92 күн", "92 days", "92 gün", "92 kun")),
                 })
        {
            var chip = UiKit.Seg(text, _days == days);
            chip.Click += (_, _) =>
            {
                _days = days;
                BuildPeriods();
                _ = LoadAsync();
            };
            _periods.Children.Add(chip);
        }
    }

    public DateTime? RefreshOnShow()
    {
        if (DateTime.UtcNow - _loadedUtc <= TimeSpan.FromSeconds(60))
            return null;
        var dataAt = _loadedUtc.ToLocalTime();
        _ = LoadAsync();
        return dataAt;
    }

    private async Task LoadAsync()
    {
        if (_preview)
        {
            Render();
            return;
        }
        _loadedUtc = DateTime.UtcNow;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        var to = ServerClock.Today;
        var from = to.AddDays(-(_days - 1));
        _range = (from, to);
        _page = 0;
        _body.Children.Clear();
        _pager.IsVisible = false;
        ShowState(StateBlock.Loading());
        _status.Text = "";
        try
        {
            if (NurCrmReportsApi.ClientResolver?.Invoke() is not { } api)
                throw new ApiException(Tr.T("Нет входа в NurCRM — войдите заново.", "NurCRMге кирген жоксуз — кайра кириңиз.", "You are not signed in to NurCRM — sign in again.",
                    "NurCRM'e giriş yapılmadı — yeniden girin.", "NurCRMga kirilmagan — qayta kiring."), 401);
            var rows = new List<FiscalReportRow>();
            var count = 0;
            var hasNext = true;
            for (var page = 1; page <= MaxServerPages && hasNext; page++)
            {
                var result = await FiscalApi.GetSalesWithoutFiscalAsync(api, from, to, page, ServerPageSize, ct).ConfigureAwait(true);
                rows.AddRange(result.Rows);
                count = result.Count;
                hasNext = result.HasNext && result.Rows.Count > 0;
            }
            if (ct.IsCancellationRequested)
                return;
            _rows = rows;
            _serverCount = Math.Max(count, rows.Count);
            _truncated = hasNext;
            Render();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _rows = null;
            _body.Children.Clear();
            _pager.IsVisible = false;
            _status.Text = "";
            ShowState(StateBlock.Failure(this, ex, Tr.T("Отчёт не построен", "Отчёт түзүлгөн жок", "The report was not built", "Rapor oluşturulamadı", "Hisobot tuzilmadi"), () => _ = LoadAsync()));
            PosLogger.Log($"Продажи без фискального чека: отчёт не построен ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Снимки и UI-тесты: показать готовые строки без сервера.</summary>
    public void ShowForPreview(IReadOnlyList<FiscalReportRow> rows, DateTime from, DateTime to, int count, bool truncated = false)
    {
        _preview = true;
        _rows = rows.ToList();
        _serverCount = count;
        _truncated = truncated;
        _range = (from, to);
        _page = 0;
        Render();
    }

    private void ShowState(Control? block)
    {
        _stateHost.Children.Clear();
        if (block is not null)
            _stateHost.Children.Add(block);
    }

    private static readonly string Cols = "90,60,90,130,*,130,*";

    private void Render()
    {
        if (_rows is null)
            return;
        ShowState(null);
        _body.Children.Clear();
        _status.Text = Tr.T($"Продаж без фискального чека: {_serverCount} за {PeriodRange.Label(_range.From, _range.To)}.",
                $"Фискалдык чексиз сатуулар: {_serverCount}, {PeriodRange.Label(_range.From, _range.To)}.",
                $"Sales without a fiscal receipt: {_serverCount} for {PeriodRange.Label(_range.From, _range.To)}.",
                $"Mali fişsiz satış: {_serverCount}, {PeriodRange.Label(_range.From, _range.To)}.",
                $"Fiskal chekisiz sotuv: {_serverCount}, {PeriodRange.Label(_range.From, _range.To)}.")
            + (_truncated ? Tr.T($" Показаны первые {_rows.Count} — выберите период короче.", $" Биринчи {_rows.Count} көрсөтүлдү — мезгилди кыскартыңыз.", $" The first {_rows.Count} are shown — choose a shorter period.",
                $" İlk {_rows.Count} gösterildi — daha kısa bir dönem seçin.", $" Dastlabki {_rows.Count} ko'rsatildi — qisqaroq davr tanlang.") : "");
        if (_rows.Count == 0)
        {
            _pager.IsVisible = false;
            ShowState(StateBlock.Empty(Tr.T("Все продажи периода пробиты", "Мезгилдеги бардык сатуулар бектилген", "All sales of the period are fiscalized", "Dönemdeki tüm satışlar kesildi",
                    "Davrdagi barcha sotuvlar urilgan"),
                Tr.T("Продаж без фискального чека нет.", "Фискалдык чексиз сатуу жок.", "There are no sales without a fiscal receipt.", "Mali fişsiz satış yok.", "Fiskal chekisiz sotuv yo'q.")));
            return;
        }
        var pages = (_rows.Count + RowsPerPage - 1) / RowsPerPage;
        _page = Math.Clamp(_page, 0, Math.Max(0, pages - 1));
        _body.Children.Add(TableRow(new[]
        {
            Tr.T("Дата", "Күнү", "Date", "Tarih", "Sana"), Tr.T("Время", "Убакыт", "Time", "Saat", "Vaqt"), Tr.T("Чек", "Чек", "Receipt", "Fiş", "Chek"),
            Tr.T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), Tr.T("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir"),
            Tr.T("ККМ", "ККМ", "KKM", "ÖKC", "KKM"), Tr.T("Причина", "Себеби", "Reason", "Neden", "Sabab"),
        }, header: true, status: FiscalStatus.None));
        foreach (var r in _rows.Skip(_page * RowsPerPage).Take(RowsPerPage))
            _body.Children.Add(TableRow(RowCells(r), header: false, status: r.Status));
        _pager.IsVisible = pages > 1;
        _prev.IsEnabled = _page > 0;
        _next.IsEnabled = _page < pages - 1;
        _pagerText.Text = Tr.T($"Страница {_page + 1} из {pages}", $"{_page + 1} / {pages}-бет", $"Page {_page + 1} of {pages}", $"Sayfa {_page + 1} / {pages}", $"{_page + 1} / {pages}-sahifa");
    }

    /// <summary>Ячейки строки: дата, время, чек, сумма, кассир, статус ККМ, причина (текст ошибки ККМ или пояснение статуса).</summary>
    internal static string[] RowCells(FiscalReportRow r) => new[]
    {
        r.CreatedAt?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "—",
        r.CreatedAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "—",
        r.Number.Length == 0 ? "—" : (r.Number.StartsWith('№') ? r.Number : "№" + r.Number),
        MoneyText.Amount(r.Total),
        r.Cashier.Length == 0 ? "—" : r.Cashier,
        FiscalStatusNames.Label(r.Status),
        r.Error ?? StatusHint(r.Status),
    };

    private static string StatusHint(FiscalStatus status) => status switch
    {
        FiscalStatus.Pending => Tr.T("Ждёт пробития", "Бектилүүнү күтүүдө", "Waiting to be fiscalized", "Fiş kesilmesini bekliyor", "Urilishini kutmoqda"),
        FiscalStatus.Failed => Tr.T("Ошибка ККМ", "ККМ катасы", "Cash register error", "ÖKC hatası", "KKM xatosi"),
        _ => "—",
    };

    private Grid TableRow(IReadOnlyList<string> texts, bool header, FiscalStatus status)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(Cols), MinWidth = 880 };
        for (var i = 0; i < texts.Count; i++)
        {
            var t = new TextBlock
            {
                Text = texts[i], FontSize = header ? 12.5 : 14, FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                TextTrimming = i is 4 or 6 ? TextTrimming.CharacterEllipsis : TextTrimming.None,
                HorizontalAlignment = i == 3 ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(0, 3, 12, 3),
            };
            var brush = header ? "BrushTextSoft" : i == 5 && status == FiscalStatus.Failed ? "BrushDanger" : "BrushText";
            Use(t, TextBlock.ForegroundProperty, brush);
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        return g;
    }

    // ── Excel (кнопка оболочки владельца): все загруженные строки ──

    public SectionTable? BuildExportTable()
    {
        if (_rows is null || _rows.Count == 0)
            return null;
        var som = Tr.T("сом", "сом", "som", "som", "so'm");
        var columns = new List<XlColumn>
        {
            new(Tr.T("Дата", "Күнү", "Date", "Tarih", "Sana")),
            new(Tr.T("Время", "Убакыт", "Time", "Saat", "Vaqt")),
            new(Tr.T("Чек", "Чек", "Receipt", "Fiş", "Chek")),
            new(Tr.T("Сумма, ", "Сумма, ", "Amount, ", "Tutar, ", "Summa, ") + som, XlKind.Money, XlTotal.Sum),
            new(Tr.T("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir")),
            new(Tr.T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov")),
            new(Tr.T("ККМ", "ККМ", "KKM", "ÖKC", "KKM")),
            new(Tr.T("Причина", "Себеби", "Reason", "Neden", "Sabab")),
        };
        var data = _rows.Select(r => new object?[]
        {
            r.CreatedAt?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "", r.CreatedAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "",
            r.Number, r.Total, r.Cashier, r.PaymentMethod, FiscalStatusNames.Label(r.Status), r.Error ?? StatusHint(r.Status),
        }).ToList();
        return new SectionTable(TitleText, PeriodRange.Label(_range.From, _range.To), columns, data);
    }

    private TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, (target is Control own ? own : this).GetResourceObservable(key));
}
