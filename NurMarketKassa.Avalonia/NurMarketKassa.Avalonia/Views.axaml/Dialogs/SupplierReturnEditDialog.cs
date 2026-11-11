using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-10 (ТЗ «правка и аннулирование возвратных накладных 24 ч»): правка проведённого возврата поставщику — существующие строки
/// (количество, цена закупки, скидка, примечание; количество 0 = убрать строку), комментарий и обязательная причина правки; сумма пересчитывается сразу
/// (расчёт — SupplierReturnEdit/ReturnLineMath в Infrastructure). «Сохранить»: проверка → свежая карточка с сервера (версия и срок по времени сервера) →
/// PIN руководителя (токен на эту правку и версию) → PATCH suppliers/returns/{id}/ (If-Match, Idempotency-Key). «Удалить возврат» — общий шаг ReturnEditFlows.VoidAsync.
/// Склад, долг поставщику и деньги пересчитывает сервер. Появляется только при признаке supplier_return_edit_24h и can_edit документа.
/// 2026-11-11 (реальный контракт Умара): скидку и примечание строки сервер НЕ хранит — полей для них в окне больше нет, в PATCH уходят только id, qty, purchase_price
/// (+ причина и комментарий документа); сумма строки = кол-во × цена.
/// Результат ShowDialog — true: документ изменён/аннулирован или выяснилось, что его изменили, — список надо перечитать.</summary>
public sealed class SupplierReturnEditDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private sealed class Row
    {
        public SupplierReturnEditLine Source = null!;
        public TextBox Qty = null!;
        public TextBox Price = null!;
        public TextBlock Sum = null!;
    }

    private readonly SupplierReturnInfo _info;
    private readonly string _number;
    private readonly List<SupplierReturnEditLine> _original;
    private readonly List<Row> _rows = new();
    private readonly TextBox _comment;
    private readonly TextBox _reason;
    private readonly TextBlock _total = new() { FontSize = 15, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, MinWidth = 140 };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _save;
    private readonly Button _delete;
    private readonly ModalShell _shell;
    private readonly PendingIdempotencyKey _key = new();
    private ReturnEditApi? _api;
    private bool _changed;

    public SupplierReturnEditDialog(SupplierReturnInfo info, string number, ReturnEditApi? api = null)
    {
        _info = info;
        _number = number;
        _api = api;
        _original = SupplierReturnEdit.FromInfo(info);
        Title = T("Изменение возврата поставщику", "Жеткирүүчүгө кайтарууну өзгөртүү", "Edit return to supplier", "Tedarikçiye iadeyi düzenle", "Yetkazib beruvchiga qaytarishni tahrirlash");
        SectionCards.AsModal(this, 720);

        var panel = new StackPanel { Spacing = 8 };
        var lines = new StackPanel { Spacing = 10 };
        foreach (var line in _original)
            lines.Children.Add(BuildRow(line));
        panel.Children.Add(SectionCards.Group(this, lines));

        _comment = SectionCards.Field(new TextBox { Text = info.Comment, Watermark = T("Комментарий документа", "Документтин комментарийи", "Document comment", "Belge yorumu", "Hujjat izohi"), MaxLength = 500 });
        _reason = SectionCards.Field(new TextBox { Watermark = T("Причина правки (обязательно, от 5 знаков)", "Оңдоонун себеби (милдеттүү, 5 белгиден)", "Reason for the change (required, at least 5 characters)",
            "Düzenleme nedeni (zorunlu, en az 5 karakter)", "Tahrirlash sababi (majburiy, kamida 5 belgi)"), MaxLength = 500 });
        Avalonia.Automation.AutomationProperties.SetName(_comment, _comment.Watermark);
        Avalonia.Automation.AutomationProperties.SetName(_reason, _reason.Watermark);
        panel.Children.Add(Label(T("Комментарий", "Комментарий", "Comment", "Yorum", "Izoh")));
        panel.Children.Add(_comment);
        panel.Children.Add(Label(T("Причина правки", "Оңдоонун себеби", "Reason for the change", "Düzenleme nedeni", "Tahrirlash sababi")));
        panel.Children.Add(_reason);
        if (ReturnEditFlows.TimeLeftHint(info.EditState) is { } left)
            panel.Children.Add(Soft(left));
        panel.Children.Add(SectionCards.ErrorBox(this, _error));
        _total.Bind(TextBlock.ForegroundProperty, _total.GetResourceObservable("BrushText"));

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(_changed);
        _delete = SectionCards.Secondary(T("Удалить возврат", "Кайтарууну өчүрүү", "Delete return", "İadeyi sil", "Qaytarishni o'chirish"));
        _delete.IsVisible = ReturnEditGate.ShowVoid(ReturnDocKind.Supplier, info.EditState);
        _delete.Click += async (_, _) => await DeleteAsync().ConfigureAwait(true);
        _save = SectionCards.Primary(T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        Content = _shell = SectionCards.Shell(Title,
            T("Склад, деньги и долг поставщику пересчитает сервер. Нужен PIN руководителя.", "Кампаны, акчаны жана жеткирүүчүгө карызды сервер кайра эсептейт. Жетекчинин PIN коду керек.",
                "The server recalculates stock, money and the supplier debt. A manager PIN is required.", "Stok, para ve tedarikçi borcunu sunucu yeniden hesaplar. Yönetici PIN'i gerekir.",
                "Ombor, pul va yetkazib beruvchiga qarzni server qayta hisoblaydi. Rahbar PIN kodi kerak.") + " · " + number,
            panel, SectionCards.Buttons(cancel, _delete, _save), _total);
        UpdateTotal();
    }

    private ReturnEditApi Api() => _api ??= ReturnEditFlows.Api();

    private Control BuildRow(SupplierReturnEditLine line)
    {
        var box = new StackPanel { Spacing = 4 };
        var name = new TextBlock { Text = (line.Name.Length > 0 ? line.Name : "—") + (line.Unit.Length > 0 ? $" ({line.Unit})" : ""), FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable("BrushText"));
        box.Children.Add(name);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*,8,Auto") }; // 2026-11-11: без скидки и примечания строки
        var row = new Row
        {
            Source = line,
            Qty = Num(line.Qty.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture), T("Кол-во", "Саны", "Qty", "Adet", "Soni"), line.Name),
            Price = Num(line.Price.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture), T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"), line.Name),
            Sum = new TextBlock { FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 12), MinWidth = 96, TextAlignment = TextAlignment.Right },
        };
        row.Sum.Classes.Add("num");
        row.Sum.Bind(TextBlock.ForegroundProperty, row.Sum.GetResourceObservable("BrushText"));
        var col = 0;
        foreach (var (field, label) in new (TextBox, string)[]
                 {
                     (row.Qty, T("Кол-во (0 — убрать)", "Саны (0 — алып салуу)", "Qty (0 — remove)", "Adet (0 — kaldır)", "Soni (0 — olib tashlash)")), (row.Price, T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi")),
                 })
        {
            var cell = new StackPanel { Spacing = 2, Children = { Soft(label), field } };
            Grid.SetColumn(cell, col);
            grid.Children.Add(cell);
            col += 2;
        }
        Grid.SetColumn(row.Sum, 4);
        grid.Children.Add(row.Sum);
        box.Children.Add(grid);
        foreach (var f in new[] { row.Qty, row.Price })
            f.TextChanged += (_, _) => UpdateTotal();
        _rows.Add(row);
        return box;
    }

    private static TextBox Num(string text, string label, string product)
    {
        var t = SectionCards.Field(new TextBox { Text = text, HorizontalContentAlignment = HorizontalAlignment.Right, Watermark = "0", MaxLength = 14 });
        Avalonia.Automation.AutomationProperties.SetName(t, label + ": " + (product.Length > 0 ? product : "—"));
        return t;
    }

    private TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private static TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    /// <summary>Строки из полей; null — в каком-то поле не число (текст ошибки — в <paramref name="error"/>).</summary>
    private List<SupplierReturnEditLine>? ReadLines(out string? error)
    {
        error = null;
        var list = new List<SupplierReturnEditLine>();
        foreach (var r in _rows)
        {
            var qty = SupplierReturnEdit.ParseNumber(r.Qty.Text);
            var price = SupplierReturnEdit.ParseNumber(r.Price.Text);
            if (qty is null || price is null)
            {
                error = $"«{(r.Source.Name.Length > 0 ? r.Source.Name : "—")}»: " + T("введите число", "сан жазыңыз", "enter a number", "bir sayı girin", "son kiriting") + ".";
                return null;
            }
            list.Add(r.Source with { Qty = qty.Value, Price = price.Value });
        }
        return list;
    }

    private void UpdateTotal()
    {
        var lines = ReadLines(out _);
        foreach (var r in _rows)
        {
            var q = SupplierReturnEdit.ParseNumber(r.Qty.Text) ?? 0;
            var p = SupplierReturnEdit.ParseNumber(r.Price.Text) ?? 0;
            r.Sum.Text = MoneyText.Som(ReturnLineMath.LineTotal(q, p, 0));
        }
        var newTotal = lines is null ? (double?)null : SupplierReturnEdit.Total(lines);
        var oldTotal = SupplierReturnEdit.Total(_original);
        _total.Text = T("Сумма: ", "Суммасы: ", "Total: ", "Toplam: ", "Jami: ") + (newTotal is { } nt ? MoneyText.Som(nt) : "—")
                      + (newTotal is { } n && Math.Abs(n - oldTotal) > 0.004 ? T($" (было {MoneyText.Som(oldTotal)})", $" ({MoneyText.Som(oldTotal)} болчу)", $" (was {MoneyText.Som(oldTotal)})", $" (önce {MoneyText.Som(oldTotal)})", $" (edi {MoneyText.Som(oldTotal)})") : "");
    }

    private void ShowError(string? text)
    {
        _error.Text = text ?? "";
        _error.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private void Lock()
    {
        _save.IsEnabled = false;
        _delete.IsEnabled = false;
        _changed = true;
    }

    private async Task SaveAsync()
    {
        ShowError(null);
        var lines = ReadLines(out var parseError);
        if (lines is null)
        {
            ShowError(parseError);
            return;
        }
        var reason = (_reason.Text ?? "").Trim();
        var comment = (_comment.Text ?? "").Trim();
        if (SupplierReturnEdit.Validate(lines, reason) is { } problem)
        {
            ShowError(problem);
            return;
        }
        if (!SupplierReturnEdit.HasChanges(_original, lines, _info.Comment, comment))
        {
            ShowError(T("Изменений нет — сохранять нечего.", "Өзгөртүү жок — сактай турган эч нерсе жок.", "No changes — nothing to save.", "Değişiklik yok — kaydedilecek bir şey yok.", "O'zgarish yo'q — saqlanadigan narsa yo'q."));
            return;
        }
        if (!_shell.TryBeginBusy())
            return;
        _save.IsEnabled = false;
        try
        {
            // Свежая проверка на сервере: версия (не правил ли кто-то другой) и срок по времени сервера.
            var fresh = await Api().GetStateAsync(ReturnDocKind.Supplier, _info.Id).ConfigureAwait(true);
            if (_info.EditState.Version > 0 && fresh.Version != _info.EditState.Version)
            {
                ShowError(ReturnEditErrors.VersionConflictText());
                Lock();
                return;
            }
            if (!ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, fresh))
            {
                ShowError(ReturnEditErrors.NotEditableText(fresh)); // 2026-11-11: причина сервера (edit_block_reason)
                Lock();
                return;
            }
            _shell.EndBusy();
            var doc = new ReturnDocRef(ReturnDocKind.Supplier, _info.Id, _number, _info.Total, fresh);
            var (token, final) = await ReturnEditFlows.AskPinAsync(this, doc, ReturnEditAction.Edit, fresh.Version, Api()).ConfigureAwait(true);
            if (token is null)
            {
                if (final is not null)
                {
                    ShowError(ReturnEditErrors.Text(final));
                    Lock();
                }
                return;
            }
            if (!_shell.TryBeginBusy())
                return;
            var body = SupplierReturnEdit.BuildPatchBody(lines, reason, comment);
            var keyValue = _key.Get(SupplierReturnEdit.Signature(_info.Id, fresh.Version, lines, reason, comment));
            await Api().PatchAsync(ReturnDocKind.Supplier, _info.Id, fresh.Version, body, token, keyValue).ConfigureAwait(true);
            _key.OnSuccess();
            PosLogger.Log($"Возврат поставщику {_number} ({_info.Id}) изменён: v{fresh.Version} → новая версия, строк {lines.Count(l => l.Qty > 0)}.", "INFO");
            _changed = true;
            _shell.EndBusy();
            Close(true);
        }
        catch (ReturnEditException e)
        {
            _key.OnError(e.InnerException ?? e);
            ShowError(T("Не сохранено: ", "Сакталган жок: ", "Not saved: ", "Kaydedilmedi: ", "Saqlanmadi: ") + ReturnEditErrors.Text(e));
            if (e.IsFinal)
                Lock();
        }
        finally
        {
            _shell.EndBusy();
            if (!_changed)
                _save.IsEnabled = true;
        }
    }

    private async Task DeleteAsync()
    {
        var doc = new ReturnDocRef(ReturnDocKind.Supplier, _info.Id, _number, _info.Total, _info.EditState);
        if (await ReturnEditFlows.VoidAsync(this, doc, Api()).ConfigureAwait(true))
        {
            _changed = true;
            Close(true);
        }
    }

    /// <summary>Для тестов окна: ввести количество в строку <paramref name="index"/>.</summary>
    public void SetQtyForTests(int index, string text) => _rows[index].Qty.Text = text;

    public string TotalText => _total.Text ?? "";
}
