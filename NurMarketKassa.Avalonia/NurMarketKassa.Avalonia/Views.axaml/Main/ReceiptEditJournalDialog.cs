using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-01 (просьба владельца «правка накладной всегда открыта, но все изменения записывай»): «Журнал правок накладных» — только для владельца, во вкладке «Покупки».
/// Показывает правки ВСЕХ приходов этого ПК (из receipt-edits.json; построчная копия — purchase-edits.jsonl): когда, кто, номер ПР-…, итог до → после, причина, а по раскрытию —
/// каждая строка «товар: было → стало». Отбор — период (неделя / месяц / всё) и пользователь. Правка старше 7 суток от прихода помечена «задним числом».</summary>
internal sealed class ReceiptEditJournalDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly List<ReceiptEditRecord> _all;
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly StackPanel _periodSeg = new() { Orientation = Orientation.Horizontal };
    private readonly ComboBox _user = new() { MinHeight = 44, MinWidth = 220 };
    private readonly TextBlock _count = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private string _period = "month";

    public ReceiptEditJournalDialog()
    {
        Title = T("Журнал правок накладных", "Накладнойлорду оңдоо журналы", "Invoice edit journal", "Fatura düzenleme günlüğü", "Yuk xati tahrirlari jurnali");
        SectionCards.AsModal(this, 860);
        // «failed» — попытка, при которой сервер ничего не принял: документ не менялся, в журнал изменений не входит.
        _all = ReceiptEditStore.Default.AllEdits().Where(e => e.Status is "done" or "partial" or "abandoned" or "applying").OrderByDescending(e => e.At).ToList();

        var users = new List<string> { T("Все пользователи", "Бардык колдонуучулар", "All users", "Tüm kullanıcılar", "Barcha foydalanuvchilar") };
        users.AddRange(_all.Select(e => e.By).Where(b => b.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(b => b, StringComparer.CurrentCultureIgnoreCase));
        _user.ItemsSource = users;
        _user.SelectedIndex = 0;
        _user.SelectionChanged += (_, _) => Render();
        _count.Bind(TextBlock.ForegroundProperty, _count.GetResourceObservable("BrushTextSoft"));
        BuildPeriods();

        var periodBox = UiKit.Segment(_periodSeg);
        periodBox.Margin = new Thickness(0, 0, 8, 0);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        bar.Children.Add(periodBox);
        bar.Children.Add(_user);
        bar.Children.Add(_count);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(bar);
        panel.Children.Add(_list);
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        Content = SectionCards.Shell(Title, T("Кто, когда и что менял в принятых накладных закупок. Записи не стираются.", "Кабыл алынган накладнойлордо ким, качан жана эмнени өзгөрттү. Жазуулар өчүрүлбөйт.",
            "Who changed what and when in posted purchase invoices. Entries are never erased.", "Kabul edilmiş alım faturalarında kim, ne zaman, neyi değiştirdi. Kayıtlar silinmez.", "Qabul qilingan xarid yuk xatlarida kim, qachon, nimani o'zgartirdi. Yozuvlar o'chirilmaydi."), panel, SectionCards.Buttons(close));
        Render();
    }

    private void BuildPeriods()
    {
        _periodSeg.Children.Clear();
        foreach (var (key, text) in new[]
                 {
                     ("week", T("Неделя", "Апта", "Week", "Hafta", "Hafta")), ("month", T("Месяц", "Ай", "Month", "Ay", "Oy")), ("all", T("Всё", "Баары", "All", "Tümü", "Barchasi")),
                 })
        {
            var seg = UiKit.Seg(text, key == _period);
            seg.MinHeight = 44;
            seg.Click += (_, _) =>
            {
                _period = key;
                BuildPeriods();
                Render();
            };
            _periodSeg.Children.Add(seg);
        }
    }

    private TextBlock Line(string text, string brush = "BrushText", double size = 13, FontWeight? w = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = w ?? FontWeight.Normal };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(brush));
        return t;
    }

    private void Render()
    {
        _list.Children.Clear();
        var from = _period switch { "week" => DateTime.Today.AddDays(-6), "month" => DateTime.Today.AddDays(-29), _ => DateTime.MinValue };
        var user = _user.SelectedIndex > 0 ? _user.SelectedItem as string : null;
        var shown = _all.Where(e => e.At >= from && (user is null || string.Equals(e.By, user, StringComparison.CurrentCultureIgnoreCase))).ToList();
        _count.Text = T($"Правок: {shown.Count}", $"Оңдоолор: {shown.Count}", $"Edits: {shown.Count}", $"Düzenleme: {shown.Count}", $"Tahrirlar: {shown.Count}");
        if (shown.Count == 0)
        {
            _list.Children.Add(SectionCards.Empty(this, T("За выбранный период правок нет.", "Тандалган мезгилде оңдоо жок.", "No edits in the selected period.", "Seçilen dönemde düzenleme yok.", "Tanlangan davrda tahrir yo'q.")));
            return;
        }
        foreach (var e in shown.Take(300))
        {
            var card = new StackPanel { Spacing = 2 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            head.Children.Add(Line(e.DocNumber.Length > 0 ? e.DocNumber : "№ " + (e.ReceiptId.Length > 8 ? e.ReceiptId[..8] : e.ReceiptId), size: 15, w: FontWeight.Bold));
            head.Children.Add(Line($"v{e.Version}" + (e.Kind == "revert" ? T($" · откат v{e.RevertedVersion}", $" · v{e.RevertedVersion} кайтаруу", $" · rollback of v{e.RevertedVersion}", $" · v{e.RevertedVersion} geri alma", $" · v{e.RevertedVersion} qaytarish") : ""), "BrushTextSoft"));
            if (ReceiptEditAuditLog.IsBackdated(e.ReceiptAt, e.At))
                head.Children.Add(SectionCards.Pill(this, T("задним числом", "эски датада", "backdated", "geriye dönük", "orqaga sanada"), "BrushWarningSoft", "BrushText"));
            if (e.Status == "partial")
                head.Children.Add(SectionCards.Pill(this, T("не до конца", "аягына чыккан жок", "incomplete", "tamamlanmadı", "tugallanmagan"), "BrushWarningSoft", "BrushText"));
            else if (e.Status == "abandoned")
                head.Children.Add(SectionCards.Pill(this, T("прекращена", "токтотулган", "abandoned", "vazgeçildi", "to'xtatilgan"), "BrushWarningSoft", "BrushText"));
            else if (e.Status == "applying")
                head.Children.Add(SectionCards.Pill(this, T("отправляется", "жөнөтүлүүдө", "sending", "gönderiliyor", "yuborilmoqda"), "BrushWarningSoft", "BrushText"));
            card.Children.Add(head);
            card.Children.Add(Line($"{e.At:dd.MM.yyyy HH:mm} · {e.By}" + (e.ByRole.Length > 0 ? $" ({e.ByRole})" : "") + (e.SupplierName.Length > 0 ? " · " + e.SupplierName : "") + (e.Device.Length > 0 ? " · " + e.Device : ""), "BrushTextSoft", 12.5));
            card.Children.Add(Line(T("Причина: ", "Себеби: ", "Reason: ", "Neden: ", "Sabab: ") + e.Reason, w: FontWeight.SemiBold));
            card.Children.Add(Line($"{MoneyText.Som(e.TotalBefore)} → {MoneyText.Som(e.TotalAfter)}"));
            var lines = ReceiptEditAuditLog.FieldChanges(e).Select(ReceiptEditAuditLog.FieldText).ToList(); // количество, цена продажи, наценка — каждое поле «было → стало»
            if (lines.Count > 0)
            {
                var more = new Expander
                {
                    // 2026-11-01: «Позиция» — названия позиций, затронутых правкой (добавленные, удалённые, с новым количеством или ценой)
                    Header = T("Позиции: ", "Позициялар: ", "Lines: ", "Satırlar: ", "Pozitsiyalar: ") + string.Join(", ", ReceiptEditAuditLog.FieldChanges(e).Select(f => f.Subject).Distinct().Take(3)) + (ReceiptEditAuditLog.FieldChanges(e).Select(f => f.Subject).Distinct().Count() > 3 ? "…" : "")
                        + T($" · что изменено: {lines.Count}", $" · эмне өзгөрдү: {lines.Count}", $" · changes: {lines.Count}", $" · değişiklik: {lines.Count}", $" · o'zgarishlar: {lines.Count}"),
                    Content = Line(string.Join("\n", lines), "BrushTextSoft", 12.5), MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                card.Children.Add(more);
            }
            var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = card };
            box.Bind(Border.BackgroundProperty, box.GetResourceObservable("BrushPanelSoft"));
            _list.Children.Add(box);
        }
        if (shown.Count > 300)
            _list.Children.Add(Line(T("Показаны последние 300 правок — сузьте период или пользователя.", "Акыркы 300 оңдоо көрсөтүлдү — мөөнөттү же колдонуучуну тарытыңыз.", "Showing the latest 300 edits — narrow the period or user.", "Son 300 düzenleme gösteriliyor — dönemi veya kullanıcıyı daraltın.", "Oxirgi 300 ta tahrir ko'rsatildi — davr yoki foydalanuvchini toraytiring."), "BrushTextSoft", 12));
    }
}
