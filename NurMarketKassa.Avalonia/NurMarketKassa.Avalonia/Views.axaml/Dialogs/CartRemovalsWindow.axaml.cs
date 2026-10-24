using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-17, редизайн (этап 4, макет MCancelled): окно «Удалено из чека» — журнал CartRemovalLogStore
/// этой кассы за сегодня / текущую смену / 7 дней. Только просмотр, без выгрузок. Открывается из меню кассы
/// (☰ → Система) владельцу и администратору (право ViewSettings).</summary>
public partial class CartRemovalsWindow : Window
{
    /// <summary>Сколько строк рисовать за раз — больше кассиру не пролистать, а окно не должно тормозить.</summary>
    private const int MaxRows = 1000;

    /// <summary>Ширины колонок: время, кассир, товар (*), кол-во, сумма, причина.</summary>
    private const string RowColumns = "94,130,*,70,104,180";

    private IReadOnlyList<CartRemovalRecord> _all = Array.Empty<CartRemovalRecord>();
    private bool _loaded;

    public CartRemovalsWindow()
    {
        InitializeComponent();

        var title = Tr.T("Удалено из чека", "Чектен алынгандар", "Removed from receipt", "Fişten çıkarılanlar", "Chekdan olib tashlanganlar");
        Title = title;
        Shell.Title = title;
        Shell.Subtitle = Tr.T(
            "Строки, убранные из чека до оплаты, — журнал этой кассы, хранится 90 дней (на все кассы — нужен сервер NurCRM)",
            "Төлөөгө чейин чектен алынган саптар — ушул кассанын журналы, 90 күн сакталат (бардык кассалар үчүн — NurCRM сервери керек)",
            "Lines removed from the receipt before payment — this till's log, kept for 90 days (for all tills — needs the NurCRM server)",
            "Ödemeden önce fişten çıkarılan satırlar — bu kasanın günlüğü, 90 gün saklanır (tüm kasalar için — NurCRM sunucusu gerekir)",
            "To'lovdan oldin chekdan olib tashlangan qatorlar — shu kassa jurnali, 90 kun saqlanadi (barcha kassalar uchun — NurCRM serveri kerak)");
        TodayRadio.Content = Tr.T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun");
        ShiftRadio.Content = Tr.T("Эта смена", "Ушул смена", "This shift", "Bu vardiya", "Shu smena");
        WeekRadio.Content = Tr.T("7 дней", "7 күн", "7 days", "7 gün", "7 kun");
        CloseButton.Content = Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish");
        HeaderRow.Child = BuildRow(
            Tr.T("Время", "Убакыт", "Time", "Saat", "Vaqt"),
            Tr.T("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir"),
            Tr.T("Товар", "Товар", "Item", "Ürün", "Mahsulot"),
            Tr.T("Кол-во", "Саны", "Qty", "Adet", "Soni"),
            Tr.T("Сумма", "Сумма", "Amount", "Tutar", "Summa"),
            Tr.T("Причина", "Себеби", "Reason", "Neden", "Sabab"),
            isHeader: true);
        Shell.Hint = Tr.T("Загрузка…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…");

        Opened += async (_, _) => await ReloadAsync().ConfigureAwait(true);
        CartRemovalLogStore.Changed += OnStoreChanged;
        Closed += (_, _) => CartRemovalLogStore.Changed -= OnStoreChanged;
    }

    private void OnStoreChanged() => Dispatcher.UIThread.Post(() => _ = ReloadAsync());

    /// <summary>Файл журнала читается в фоне — окно открывается сразу.</summary>
    private async Task ReloadAsync()
    {
        try
        {
            _all = await Task.Run(CartRemovalLogStore.ReadAll).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно «Удалено из чека»: журнал не прочитан: {ex.Message}", "WARNING");
            _all = Array.Empty<CartRemovalRecord>();
        }
        _loaded = true;
        Render();
    }

    private void Period_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loaded && sender is RadioButton { IsChecked: true })
            Render();
    }

    private IEnumerable<CartRemovalRecord> Filtered()
    {
        // Журнал общий для всех аккаунтов этого ПК — показываем только записи своей компании.
        var companyId = CompanyInfoService.LastCompany?.Id;
        // 2026-10-17, ревью этапа 4: компания ещё не известна — не показываем записи чужих компаний (только без компании).
        var own = _all.Where(r => string.IsNullOrWhiteSpace(r.CompanyId)
                                  || (!string.IsNullOrWhiteSpace(companyId)
                                      && string.Equals(r.CompanyId, companyId, StringComparison.OrdinalIgnoreCase)));
        if (ShiftRadio.IsChecked == true)
        {
            var shiftId = PosApp.ActiveShiftId;
            return string.IsNullOrWhiteSpace(shiftId)
                ? Enumerable.Empty<CartRemovalRecord>()
                : own.Where(r => string.Equals(r.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase));
        }
        var from = WeekRadio.IsChecked == true ? DateTime.Today.AddDays(-6) : DateTime.Today;
        return own.Where(r => r.At.LocalDateTime >= from);
    }

    private void Render()
    {
        var rows = Filtered().ToList();
        RowsPanel.Children.Clear();
        foreach (var record in rows.Take(MaxRows))
            RowsPanel.Children.Add(BuildRecordRow(record));
        if (rows.Count > MaxRows)
            RowsPanel.Children.Add(Themed(new TextBlock
            {
                Margin = new Thickness(12, 10), FontSize = 12.5,
                Text = Tr.T($"Показаны последние {MaxRows} из {rows.Count}.", $"Акыркы {MaxRows} / {rows.Count} көрсөтүлдү.",
                    $"Showing the latest {MaxRows} of {rows.Count}.", $"Son {MaxRows} / {rows.Count} gösteriliyor.",
                    $"Oxirgi {MaxRows} / {rows.Count} ko'rsatildi."),
            }, TextBlock.ForegroundProperty, "BrushTextSoft"));
        ListScroll.Offset = default;

        EmptyText.IsVisible = rows.Count == 0;
        if (rows.Count == 0)
            EmptyText.Text = ShiftRadio.IsChecked == true && string.IsNullOrWhiteSpace(PosApp.ActiveShiftId)
                ? Tr.T("Смена не открыта.", "Смена ачылган эмес.", "The shift is not open.", "Vardiya açık değil.", "Smena ochilmagan.")
                : Tr.T("Ничего не удаляли.", "Эч нерсе алынган жок.", "Nothing was removed.", "Hiçbir şey çıkarılmadı.", "Hech narsa olib tashlanmagan.");

        var sum = rows.Sum(r => r.LineTotal);
        var money = CartRemovalReasonDialog.FormatMoney(sum);
        Shell.Hint = Tr.T($"Позиций: {rows.Count} · {money}", $"Позициялар: {rows.Count} · {money}", $"Lines: {rows.Count} · {money}",
            $"Kalem: {rows.Count} · {money}", $"Pozitsiyalar: {rows.Count} · {money}");
    }

    private Control BuildRecordRow(CartRemovalRecord r)
    {
        var local = r.At.LocalDateTime;
        var time = local.Date == DateTime.Today
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
        var qty = r.Quantity.ToString(Math.Abs(r.Quantity - Math.Round(r.Quantity)) < 1e-9 ? "0" : "0.###", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(r.Unit))
            qty += " " + r.Unit;
        var reason = ReasonText(r);

        var row = Themed(new Border
        {
            MinHeight = 44,
            Padding = new Thickness(12, 0),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = BuildRow(time, string.IsNullOrWhiteSpace(r.Cashier) ? "—" : r.Cashier, r.Title, qty,
                CartRemovalReasonDialog.FormatMoney(r.LineTotal), reason, isHeader: false),
        }, Border.BorderBrushProperty, "BrushBorder");
        ToolTip.SetTip(row, Tooltip(r, reason));
        return row;
    }

    /// <summary>Причина: из окна «Убрать из чека?» (с комментарием) или как убрали — очистка, удаление вкладки, смена.</summary>
    private static string ReasonText(CartRemovalRecord r)
    {
        var kind = r.Kind switch
        {
            CartRemovalLogStore.KindClear => Tr.T("Очистка чека", "Чекти тазалоо", "Receipt cleared", "Fiş temizlendi", "Chek tozalandi"),
            CartRemovalLogStore.KindReceipt => Tr.T("Удалён чек", "Чек өчүрүлдү", "Receipt deleted", "Fiş silindi", "Chek o'chirildi"),
            CartRemovalLogStore.KindShiftClose => Tr.T("Закрытие смены", "Сменаны жабуу", "Shift closed", "Vardiya kapandı", "Smena yopildi"),
            _ => "",
        };
        var reason = CartRemovalReasonDialog.ReasonTitle(r.Reason);
        if (r.Reason == CartRemovalReasonDialog.ReasonOther && !string.IsNullOrWhiteSpace(r.Note))
            reason = r.Note!;
        else if (!string.IsNullOrWhiteSpace(r.Note))
            reason = $"{reason} · {r.Note}";
        var text = string.Join(" · ", new[] { reason, kind }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return text.Length == 0 ? "—" : text;
    }

    private static string Tooltip(CartRemovalRecord r, string reason)
    {
        var lines = new List<string>
        {
            r.At.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(r.Receipt) ? "" : " · " + r.Receipt),
            r.Title,
            reason,
        };
        if (!string.IsNullOrWhiteSpace(r.ConfirmedBy))
        {
            var who = r.ConfirmedBy == CartRemovalLogStore.ConfirmedBySiteCode
                ? Tr.T("код с сайта", "сайттагы код", "code from the website", "web sitesindeki kod", "saytdagi kod")
                : r.ConfirmedBy;
            lines.Add(Tr.T($"Код доступа: {who}", $"Кирүү коду: {who}", $"Access code: {who}", $"Erişim kodu: {who}", $"Kirish kodi: {who}"));
        }
        return string.Join("\n", lines);
    }

    private Grid BuildRow(string time, string cashier, string title, string qty, string sum, string reason, bool isHeader)
    {
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(RowColumns), VerticalAlignment = VerticalAlignment.Center };
        void Cell(int column, string text, bool soft, bool bold = false, bool right = false)
        {
            var block = Themed(new TextBlock
            {
                Text = text,
                FontSize = isHeader ? 12.5 : 13, // 2026-10-22 (модалки, пакет 1): шапка таблицы 12,5 (было 11,5 — мельче стандарта 12)
                FontWeight = isHeader || bold ? FontWeight.SemiBold : FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(0, isHeader ? 6 : 0, column == 5 ? 0 : 12, isHeader ? 6 : 0),
            }, TextBlock.ForegroundProperty, soft || isHeader ? "BrushTextSoft" : "BrushText");
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }

        Cell(0, time, soft: true);
        Cell(1, cashier, soft: true);
        Cell(2, title, soft: false);
        Cell(3, qty, soft: false, right: true);
        Cell(4, sum, soft: false, bold: true, right: true);
        Cell(5, reason, soft: false);
        return grid;
    }

    /// <summary>Кисть темы — привязкой к ресурсу (смена темы при открытом окне перекрашивает строки).</summary>
    private T Themed<T>(T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, this.GetResourceObservable(key));
        return control;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
