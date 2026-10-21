using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-20, этап П3: подтверждение «Записать долг» в «Поставщиках» — поставщик, сумма, дата прихода, срок оплаты.
/// Два случая: очередь «долг не записан» (приход «в долг», после которого долг не записался; срок — из приёмки) и старая закупка «в долг»
/// без сделки-долга (срок задаётся здесь, «Оплатить до» — от сегодня). Запись — SupplierDebtRecorder с той же защитой от двойного
/// долга, что при приёмке: метка прихода, перечитывание сделок, журнал. Пока идёт запрос, окно не закрывается. Recorded — сколько записано.</summary>
public sealed class SupplierDebtRecordDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly List<SupplierDebtRequest> _items;
    private readonly bool _editableTerm;
    private readonly TextBox? _days;
    private readonly TextBlock _dueText = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _results = new() { Spacing = 4, IsVisible = false };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly ContentControl _table = new();
    private readonly Button _cancel;
    private readonly Button _ok;
    private bool _busy;
    private bool _done;

    public int Recorded { get; private set; }

    public SupplierDebtRecordDialog(IReadOnlyList<SupplierDebtRequest> items, bool editableTerm)
    {
        _items = items.ToList();
        _editableTerm = editableTerm && _items.Count == 1;
        Title = T("Записать долг поставщику", "Жеткирүүчүгө карызды жазуу", "Record the debt to the supplier", "Tedarikçiye borcu kaydet", "Yetkazib beruvchiga qarzni yozish");
        SectionCards.AsModal(this, 620);

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(_table);
        if (_items.Count > 1)
        {
            var total = new TextBlock
            {
                Text = T($"Всего: {_items.Count} на {MoneyText.Som(_items.Sum(i => i.Amount))}", $"Баары: {_items.Count}, {MoneyText.Som(_items.Sum(i => i.Amount))}",
                    $"Total: {_items.Count}, {MoneyText.Som(_items.Sum(i => i.Amount))}", $"Toplam: {_items.Count}, {MoneyText.Som(_items.Sum(i => i.Amount))}",
                    $"Jami: {_items.Count} ta, {MoneyText.Som(_items.Sum(i => i.Amount))}"),
                FontSize = 14, FontWeight = FontWeight.SemiBold,
            };
            Use(total, TextBlock.ForegroundProperty, "BrushText");
            panel.Children.Add(total);
        }
        if (_editableTerm)
        {
            var label = new TextBlock { Text = T("Срок оплаты, дней", "Төлөө мөөнөтү, күн", "Pay within, days", "Ödeme vadesi, gün", "To'lov muddati, kun"),
                FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Use(label, TextBlock.ForegroundProperty, "BrushTextSoft");
            _days = UiKit.Input(this, "30", 40);
            _days.Width = 90;
            _days.MaxLength = 4;
            _days.Text = _items[0].DueDays.ToString(CultureInfo.InvariantCulture);
            Avalonia.Automation.AutomationProperties.SetName(_days, label.Text);
            _days.TextChanged += (_, _) => UpdateTerm();
            Use(_dueText, TextBlock.ForegroundProperty, "BrushText");
            var termRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { label, _days, _dueText } };
            _dueText.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(termRow);
        }
        panel.Children.Add(_results);
        panel.Children.Add(SectionCards.ErrorBox(this, _error));

        _cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        _cancel.Click += (_, _) => { if (!_busy) Close(); };
        _ok = SectionCards.Primary(_items.Count > 1
            ? T($"Записать долги ({_items.Count})", $"Карыздарды жазуу ({_items.Count})", $"Record debts ({_items.Count})", $"Borçları kaydet ({_items.Count})", $"Qarzlarni yozish ({_items.Count})")
            : T("Записать долг", "Карызды жазуу", "Record debt", "Borcu kaydet", "Qarzni yozish"));
        _ok.IsDefault = true;
        _ok.Click += async (_, _) =>
        {
            if (_done)
                Close();
            else
                await RecordAsync().ConfigureAwait(true);
        };

        var hint = new TextBlock
        {
            Text = T("Повторное нажатие не создаст второй долг: касса сначала ищет долг этого прихода на сервере.",
                "Кайра басуу экинчи карызды түзбөйт: касса адегенде бул кириштин карызын серверден издейт.",
                "Pressing again won't create a second debt: the app first looks for this delivery's debt on the server.",
                "Tekrar basmak ikinci borç oluşturmaz: uygulama önce bu girişin borcunu sunucuda arar.",
                "Qayta bosish ikkinchi qarzni yaratmaydi: kassa avval bu kirim qarzini serverdan qidiradi."),
            FontSize = 12, TextWrapping = TextWrapping.Wrap, MinWidth = 140,
        };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");

        Content = SectionCards.Shell(Title,
            T("Долг появится у поставщика в «Долгах» и на сайте NurCRM — как закупка «в долг» на сайте: сумма прихода, оплата до указанной даты.",
                "Карыз жеткирүүчүнүн «Карыздарында» жана NurCRM сайтында пайда болот — сайттагы «карызга» сатып алуудай: кириштин суммасы, көрсөтүлгөн күнгө чейин төлөө.",
                "The debt will appear in the supplier's “Debts” and on the NurCRM website — like a purchase “on credit” on the website: the delivery amount, due on the given date.",
                "Borç tedarikçinin «Borçlar» bölümünde ve NurCRM sitesinde görünecek — sitedeki «veresiye» alım gibi: giriş tutarı, belirtilen tarihe kadar ödeme.",
                "Qarz yetkazib beruvchining «Qarzlar»ida va NurCRM saytida paydo bo'ladi — saytdagi «qarzga» xarid kabi: kirim summasi, ko'rsatilgan sanagacha to'lov."),
            panel, SectionCards.Buttons(_cancel, _ok), hint);

        RenderTable();
        UpdateTerm();
        Closing += (_, e) =>
        {
            // Пока запрос идёт, окно не закрывается — иначе не узнать, записан ли долг.
            if (_busy)
                e.Cancel = true;
        };
    }

    private void RenderTable()
    {
        var rows = _items.Select(i => (i.SupplierName,
            i.ReceiptAt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
            i.DueDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            MoneyText.Som(i.Amount))).ToList();
        _table.Content = SectionCards.LinesTable(this, rows, T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"),
            T("Приход", "Кириш", "Delivery", "Giriş", "Kirim"), T("Оплатить до", "Төлөө мөөнөтү", "Pay by", "Son ödeme", "To'lash muddati"),
            T("Сумма", "Суммасы", "Amount", "Tutar", "Summa"));
    }

    /// <summary>Срок задаётся здесь (старая закупка): «Оплатить до» — сегодня + дни.</summary>
    private void UpdateTerm()
    {
        if (!_editableTerm || _days is null)
        {
            _ok.IsEnabled = !_busy;
            return;
        }
        var ok = int.TryParse((_days.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 3650;
        _ok.IsEnabled = ok && !_busy;
        if (!ok)
        {
            _dueText.Text = T("от 1 до 3650 дней", "1ден 3650 күнгө чейин", "from 1 to 3650 days", "1–3650 gün", "1 dan 3650 kungacha");
            return;
        }
        var due = DateTime.Today.AddDays(days);
        _items[0] = _items[0] with { DueDays = days, DueDate = due };
        _dueText.Text = T($"оплатить до {due:dd.MM.yyyy}", $"{due:dd.MM.yyyy} чейин төлөө", $"pay by {due:dd.MM.yyyy}", $"son ödeme {due:dd.MM.yyyy}", $"{due:dd.MM.yyyy} gacha to'lash");
        RenderTable();
    }

    private async Task RecordAsync()
    {
        if (_busy)
            return;
        _busy = true;
        _ok.IsEnabled = false;
        _cancel.IsEnabled = false;
        if (_days is not null)
            _days.IsEnabled = false;
        _error.IsVisible = false;
        _results.Children.Clear();
        _results.IsVisible = true;
        _ok.Content = T("Записываю…", "Жазылууда…", "Recording…", "Kaydediliyor…", "Yozilmoqda…");
        var failed = 0;
        try
        {
            var api = App.GetRequiredService<NurMarketApiClient>();
            foreach (var item in _items)
            {
                SupplierDebtResult r;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                    r = await SupplierDebtRecorder.RecordAsync(api, item, cts.Token).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Долг поставщику {item.SupplierId} ({item.Marker}): «Записать долг» упало ({ex.GetType().Name}: {ex.Message}).", "ERROR");
                    r = new SupplierDebtResult(SupplierDebtResultKind.Failed, null, item.Amount, ex.Message);
                }
                if (r.Recorded)
                    Recorded++;
                else
                    failed++;
                var line = new TextBlock { Text = (r.Recorded ? "✓ " : "⚠ ") + item.SupplierName + ": " + SupplierDebtRecorder.ResultText(r), FontSize = 13, TextWrapping = TextWrapping.Wrap };
                Use(line, TextBlock.ForegroundProperty, r.Recorded ? "BrushText" : "BrushDanger");
                _results.Children.Add(line);
                if (r.Kind == SupplierDebtResultKind.Closed)
                    break;
            }
        }
        finally
        {
            _busy = false;
        }
        _done = failed == 0;
        _cancel.IsEnabled = true;
        _ok.IsEnabled = true;
        _ok.Content = _done ? T("Готово", "Даяр", "Done", "Tamam", "Tayyor") : T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish");
        if (!_done && _days is not null)
            _days.IsEnabled = true;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
