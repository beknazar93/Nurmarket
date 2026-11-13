using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-11 (сервер Умара: правка возврата от клиента за 24 ч, признак pos_return_edit_24h): правка проведённого возврата от клиента по образцу
/// SupplierReturnEditDialog. Строки возврата (товар, количество, «можно до N» = в чеке − возвращено другими возвратами), правка количества и «Убрать» строку
/// (полный новый состав), обязательная причина (от 5 знаков), сумма — ориентировочно (точный пересчёт денег, склада и долга — на сервере).
/// «Сохранить»: проверка → свежая карточка (версия и срок по времени сервера) → PIN руководителя (токен на эту правку и версию) →
/// PATCH pos/returns/{id}/ {version, edit_reason, items:[{sale_item_id, quantity}]} с If-Match, Idempotency-Key, X-Confirm-Token. «Удалить возврат» — ReturnEditFlows.VoidAsync.
/// Результат ShowDialog — true: возврат изменён/аннулирован или выяснилось, что его изменили, — список, чек, долг и остатки надо перечитать.</summary>
public sealed class CustomerReturnEditDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private sealed class Row
    {
        public CustomerReturnEditLine Source = null!;
        public TextBox Qty = null!;
        public TextBlock Sum = null!;
        // 2026-11-12 (UX правки возврата): название (зачёркивается у убранной строки) и кнопка «убрать / вернуть строку»
        public TextBlock Name = null!;
        public Button Remove = null!;
    }

    // 2026-11-12 (UX правки возврата): строка — одна линия таблицы «товар | − кол-во + | сумма | ✕», как в правке накладной поставщику (раньше поле количества во всю ширину
    // с числом в верхнем углу и текстовая кнопка «Убрать», которая никак не показывала, что строка убрана)
    private const string Cols = "*,12,44,4,92,4,44,8,108,8,44";

    private readonly ReturnDocRef _doc;
    private readonly List<CustomerReturnEditLine> _original;
    private readonly List<Row> _rows = new();
    private readonly TextBox _reason;
    // 2026-11-12 (UX правки возврата): итог в подвале — подпись, крупная сумма и строка «было … · клиенту на … меньше/больше» (раньше одна строка 15 px, в ky ломалась на две)
    private readonly TextBlock _total = new() { FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _delta = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private string _totalText = "";
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _save;
    private readonly Button _delete;
    private readonly ModalShell _shell;
    // 2026-11-12 (аудит В-2): ключ повтора — по id возврата, а не по окну (как у возврата поставщику): обрыв связи после отправки, окно закрыли и открыли снова — тот же состав той же версии уйдёт
    // с тем же Idempotency-Key, и сервер вернёт прежний итог, а не проведёт правку второй раз.
    private static readonly Dictionary<string, PendingIdempotencyKey> Keys = new(StringComparer.Ordinal);
    private readonly string _saleId;
    private ReturnEditApi? _api;
    private bool _changed;
    private bool _closed;

    /// <summary>2026-11-11: состояние возврата из ответа PATCH (новая version, edit_count); null — не сохраняли.</summary>
    public ReturnEditState? SavedState { get; private set; }

    /// <summary>2026-11-12 (аудит К-1): что правка сделала с журналами кассы (запись возврата, сторно в смене); null — правка не проведена.</summary>
    public ReturnJournalChange? JournalChange { get; private set; }

    /// <summary>2026-11-12 (аудит В-1): что кассиру делать с деньгами («Заберите у клиента N сом» / «Выдайте ещё N сом»); показывает вызывающий код после закрытия окна.</summary>
    public string MoneyNote { get; private set; } = "";

    public CustomerReturnEditDialog(ReturnDocRef doc, IReadOnlyList<CustomerReturnEditLine> lines, ReturnEditApi? api = null, string saleId = "")
    {
        _doc = doc;
        _api = api;
        _saleId = saleId;
        _original = lines.ToList();
        Title = T("Изменение возврата от клиента", "Кардардан кайтарууну өзгөртүү", "Edit customer return", "Müşteri iadesini düzenle", "Mijozdan qaytarishni tahrirlash");
        SectionCards.AsModal(this, 720); // 2026-11-12 (UX): было 680 — строка-таблица с кнопками «−» и «+»

        var panel = new StackPanel { Spacing = 8 };
        var rows = new StackPanel { Spacing = 8 };
        // 2026-11-12 (UX правки возврата): шапка столбцов один раз над строками
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions(Cols) };
        var headProduct = Soft(T("Товар", "Товар", "Product", "Ürün", "Mahsulot"));
        var headQty = Soft(T("Вернуть, кол-во", "Кайтаруу, саны", "Return, qty", "İade, adet", "Qaytarish, soni"));
        headQty.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(headQty, 2);
        Grid.SetColumnSpan(headQty, 5);
        var headSum = Soft(T("Сумма ≈", "Суммасы ≈", "Total ≈", "Tutar ≈", "Summa ≈"));
        headSum.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(headSum, 8);
        head.Children.Add(headProduct);
        head.Children.Add(headQty);
        head.Children.Add(headSum);
        rows.Children.Add(head);
        foreach (var line in _original)
            rows.Children.Add(BuildRow(line));
        panel.Children.Add(SectionCards.Group(this, rows));

        // 2026-11-12 (UX): подсказка в поле короче — подпись «Причина правки *» над полем уже есть
        _reason = SectionCards.Field(new TextBox { Watermark = T("Обязательно, от 5 знаков", "Милдеттүү, 5 белгиден", "Required, at least 5 characters", "Zorunlu, en az 5 karakter", "Majburiy, kamida 5 belgi"),
            MaxLength = CustomerReturnEdit.ReasonMax, VerticalContentAlignment = VerticalAlignment.Center });
        Avalonia.Automation.AutomationProperties.SetName(_reason, T("Причина правки (обязательно, от 5 знаков)", "Оңдоонун себеби (милдеттүү, 5 белгиден)", "Reason for the change (required, at least 5 characters)",
            "Düzenleme nedeni (zorunlu, en az 5 karakter)", "Tahrirlash sababi (majburiy, kamida 5 belgi)"));
        panel.Children.Add(Label(T("Причина правки", "Оңдоонун себеби", "Reason for the change", "Düzenleme nedeni", "Tahrirlash sababi") + " *"));
        panel.Children.Add(_reason);
        ReturnEditFlows.AttachTimeLeft(panel, this, doc.State); // 2026-11-12 (аудит Н-2): подсказка срока пересчитывается раз в минуту
        panel.Children.Add(SectionCards.ErrorBox(this, _error));
        _total.Classes.Add("num");
        _total.Bind(TextBlock.ForegroundProperty, _total.GetResourceObservable("BrushText"));
        _delta.Bind(TextBlock.ForegroundProperty, _delta.GetResourceObservable("BrushTextSoft"));
        var totalBox = new StackPanel { Spacing = 0, MinWidth = 140, Children = { Soft(T("Сумма возврата", "Кайтаруу суммасы", "Return total", "İade toplamı", "Qaytarish summasi")), _total, _delta } };

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += async (_, _) => await CancelAsync().ConfigureAwait(true); // 2026-11-12 (аудит С-1): Отмена, ✕ и Esc с несохранёнными правками — сначала вопрос
        _delete = SectionCards.Secondary(T("Удалить возврат", "Кайтарууну өчүрүү", "Delete return", "İadeyi sil", "Qaytarishni o'chirish"));
        _delete.Classes.Add("danger"); // 2026-11-12 (UX): опасное действие — красной обводкой, первым слева, подальше от «Сохранить»
        _delete.IsVisible = ReturnEditGate.ShowVoid(ReturnDocKind.Customer, doc.State);
        _delete.Click += async (_, _) => await DeleteAsync().ConfigureAwait(true);
        _save = SectionCards.Primary(T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        // 2026-11-12 (UX): номер возврата — в начале подзаголовка (как в правке накладной поставщику)
        Content = _shell = SectionCards.Shell(Title,
            (doc.Number.Length > 0 ? doc.Number + " · " : "") +
            T("Деньги клиенту, склад и долг пересчитает сервер. Нужен PIN руководителя.", "Кардарга акчаны, кампаны жана карызды сервер кайра эсептейт. Жетекчинин PIN коду керек.",
                "The server recalculates the customer refund, stock and debt. A manager PIN is required.", "Müşteri iadesi, stok ve borcu sunucu yeniden hesaplar. Yönetici PIN'i gerekir.",
                "Mijozga pulni, omborni va qarzni server qayta hisoblaydi. Rahbar PIN kodi kerak."),
            panel, SectionCards.Buttons(_delete, cancel, _save), totalBox);
        UpdateTotal();
        Closed += (_, _) => _closed = true; // 2026-11-12 (аудит С-3): окно закрыли во время запроса — дальше PIN и PATCH не идут
    }

    private ReturnEditApi Api() => _api ??= ReturnEditFlows.Api();

    private PendingIdempotencyKey Key
    {
        get
        {
            lock (Keys)
                return Keys.TryGetValue(_doc.Id, out var k) ? k : Keys[_doc.Id] = new PendingIdempotencyKey();
        }
    }

    private void ForgetKey()
    {
        lock (Keys)
            Keys.Remove(_doc.Id);
    }

    /// <summary>2026-11-12 (аудит В-2): забыть ключи повтора между тестами.</summary>
    public static void ResetKeysForTests()
    {
        lock (Keys)
            Keys.Clear();
    }

    private static string UnknownOutcomeText() => T("Нет связи с сервером — неизвестно, дошла ли правка. Нажмите «Сохранить» ещё раз, когда связь появится: повтор не проведёт её второй раз.",
        "Сервер менен байланыш жок — оңдоо жеткени белгисиз. Байланыш пайда болгондо «Сактоо» дагы басыңыз: кайталоо аны экинчи жолу өткөрбөйт.",
        "No connection to the server — it is unknown whether the change arrived. Press “Save” again when the connection is back: a retry will not apply it twice.",
        "Sunucuyla bağlantı yok — düzenlemenin ulaşıp ulaşmadığı bilinmiyor. Bağlantı gelince yine «Kaydet»e basın: tekrar, onu ikinci kez işlemez.",
        "Server bilan aloqa yo'q — tahrir yetib borgani noma'lum. Aloqa paydo bo'lganda yana «Saqlash»ni bosing: takror uni ikkinchi marta o'tkazmaydi.");

    private static string LikelySavedText() => T("Связь оборвалась после отправки, а на сервере возврат уже новой версии с вашим составом — правка сохранена. Закройте окно: список обновится, проверьте возврат. Второй раз ничего не отправлено.",
        "Жөнөтүлгөндөн кийин байланыш үзүлдү, ал эми серверде кайтаруу сиздин курам менен жаңы версияда — оңдоо сакталды. Терезени жабыңыз: тизме жаңырат, кайтарууну текшериңиз. Экинчи жолу эч нерсе жөнөтүлгөн жок.",
        "The connection dropped after sending, and the server already has the return in a newer version with your lines — the change was saved. Close the window: the list will refresh, check the return. Nothing was sent a second time.",
        "Gönderimden sonra bağlantı koptu ve sunucuda iade sizin satırlarınızla yeni sürümde — düzenleme kaydedildi. Pencereyi kapatın: liste yenilenecek, iadeyi kontrol edin. İkinci kez bir şey gönderilmedi.",
        "Yuborilgandan keyin aloqa uzildi, serverda esa qaytarish sizning qatorlaringiz bilan yangi versiyada — tahrir saqlandi. Oynani yoping: ro'yxat yangilanadi, qaytarishni tekshiring. Ikkinchi marta hech narsa yuborilmadi.");

    /// <summary>2026-11-12 (аудит С-1): есть ли несохранённые правки (количества или причина).</summary>
    private bool IsDirty()
    {
        if (_changed)
            return false;
        if (!string.IsNullOrWhiteSpace(_reason.Text))
            return true;
        var lines = ReadLines(out _);
        return lines is null || CustomerReturnEdit.HasChanges(_original, lines);
    }

    private async Task CancelAsync()
    {
        if (IsDirty()
            && await PosMessageBox.ShowModalAsync(this,
                T("Закрыть без сохранения? Введённые изменения возврата пропадут.", "Сактабай жабасызбы? Киргизилген өзгөртүүлөр жоголот.",
                    "Close without saving? The changes you entered will be lost.", "Kaydetmeden kapatılsın mı? Girdiğiniz değişiklikler kaybolacak.", "Saqlamasdan yopilsinmi? Kiritilgan o'zgarishlar yo'qoladi."),
                Title ?? "", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question,
                confirmText: T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), cancelText: T("Остаться", "Калуу", "Stay", "Kal", "Qolish")).ConfigureAwait(true) != System.Windows.MessageBoxResult.Yes)
            return;
        Close(_changed);
    }

    /// <summary>Название товара по id товара — для текстов 409 (в карточке возврата от клиента id товара нет, текст без названия, если сервер не прислал product_name).</summary>
    private static string? ProductNameById(string id) => null;

    private Control BuildRow(CustomerReturnEditLine line)
    {
        // 2026-11-12 (UX правки возврата): строка — одна линия таблицы (столбцы Cols): название и «было · можно до» слева, «− количество +», сумма, «✕» (убрать) / «↩» (вернуть строку)
        var box = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = (line.Name.Length > 0 ? line.Name : "—") + (line.Unit.Length > 0 ? $" ({line.Unit})" : ""), FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable("BrushText"));
        box.Children.Add(name);
        var max = line.Max.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);
        var was = line.Qty.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);
        box.Children.Add(Soft(T($"В возврате было: {was} · можно до {max}", $"Кайтарууда болчу: {was} · {max} чейин болот", $"Was in the return: {was} · up to {max}",
            $"İadede vardı: {was} · en çok {max}", $"Qaytarishda edi: {was} · {max} gacha mumkin")));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Cols) };
        var row = new Row
        {
            Source = line,
            Name = name,
            Qty = SectionCards.Field(new TextBox { Text = was, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Watermark = "0", MaxLength = 14 }),
            Sum = new TextBlock { FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right },
        };
        var shownName = line.Name.Length > 0 ? line.Name : "—";
        Avalonia.Automation.AutomationProperties.SetName(row.Qty, T("Кол-во", "Саны", "Qty", "Adet", "Soni") + ": " + shownName);
        row.Sum.Classes.Add("num");
        row.Sum.Bind(TextBlock.ForegroundProperty, row.Sum.GetResourceObservable("BrushText"));
        Button Square(string glyph, string label)
        {
            var b = new Button
            {
                Content = glyph, Width = 44, Height = 44, MinHeight = 44, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                IsTabStop = false, // Tab идёт по полям количества и к причине, не застревая на кнопках строки
            };
            b.Classes.Add("sec-outline");
            Avalonia.Automation.AutomationProperties.SetName(b, label + ": " + shownName);
            return b;
        }
        // «−» и «+»: шаг 1 в пределах 0…«можно до» (дробное количество весового товара вводится в поле)
        void Step(double delta)
        {
            var current = SupplierReturnEdit.ParseNumber(row.Qty.Text) ?? 0;
            if (!double.IsFinite(current) || Math.Abs(current) > 1e9)
                current = 0; // мусор в поле («1e99») — считаем от нуля, без переполнения
            var next = Math.Clamp((double)((decimal)current + (decimal)delta), 0, Math.Max(0, line.Max)); // сложение в decimal — без «хвостов» 2,3000000001
            row.Qty.Text = next.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);
        }
        var minus = Square("−", T("Меньше", "Азыраак", "Less", "Azalt", "Kamroq"));
        var plus = Square("+", T("Больше", "Көбүрөөк", "More", "Artır", "Ko'proq"));
        minus.Click += (_, _) => Step(-1);
        plus.Click += (_, _) => Step(1);
        var remove = Square("✕", T("Убрать", "Алып салуу", "Remove", "Kaldır", "Olib tashlash"));
        row.Remove = remove;
        remove.Click += (_, _) => row.Qty.Text = SupplierReturnEdit.ParseNumber(row.Qty.Text) is { } q0 && q0 == 0 ? was : "0";
        Grid.SetColumn(minus, 2);
        Grid.SetColumn(row.Qty, 4);
        Grid.SetColumn(plus, 6);
        Grid.SetColumn(row.Sum, 8);
        Grid.SetColumn(remove, 10);
        grid.Children.Add(box);
        grid.Children.Add(minus);
        grid.Children.Add(row.Qty);
        grid.Children.Add(plus);
        grid.Children.Add(row.Sum);
        grid.Children.Add(remove);
        row.Qty.TextChanged += (_, _) => UpdateTotal();
        _rows.Add(row);
        return grid;
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
    private List<CustomerReturnEditLine>? ReadLines(out string? error)
    {
        error = null;
        var list = new List<CustomerReturnEditLine>();
        foreach (var r in _rows)
        {
            var qty = SupplierReturnEdit.ParseNumber(r.Qty.Text);
            if (qty is null)
            {
                error = $"«{(r.Source.Name.Length > 0 ? r.Source.Name : "—")}»: " + T("введите число", "сан жазыңыз", "enter a number", "bir sayı girin", "son kiriting") + ".";
                return null;
            }
            list.Add(r.Source with { Qty = qty.Value });
        }
        return list;
    }

    private void UpdateTotal()
    {
        var lines = ReadLines(out _);
        foreach (var r in _rows)
        {
            r.Sum.Text = MoneyText.Som(ReturnLineMath.LineTotal(SupplierReturnEdit.ParseNumber(r.Qty.Text) ?? 0, r.Source.UnitPrice, 0));
            // 2026-11-12 (UX): убранная строка (кол-во 0) — название зачёркнуто и бледнее, кнопка «✕» становится «↩ вернуть строку»
            var removed = SupplierReturnEdit.ParseNumber(r.Qty.Text) is { } rq && rq == 0;
            r.Name.TextDecorations = removed ? TextDecorations.Strikethrough : null;
            r.Name.Opacity = removed ? 0.55 : 1;
            r.Sum.Opacity = removed ? 0.55 : 1;
            r.Remove.Content = removed ? "↩" : "✕";
            var tip = (removed ? T("Вернуть строку", "Сапты кайтаруу", "Restore the line", "Satırı geri al", "Qatorni tiklash") : T("Убрать", "Алып салуу", "Remove", "Kaldır", "Olib tashlash"))
                      + ": " + (r.Source.Name.Length > 0 ? r.Source.Name : "—");
            Avalonia.Automation.AutomationProperties.SetName(r.Remove, tip);
            ToolTip.SetTip(r.Remove, tip);
        }
        var newTotal = lines is null ? (double?)null : CustomerReturnEdit.Total(lines);
        var oldTotal = _doc.Total > 0 ? _doc.Total : CustomerReturnEdit.Total(_original);
        _totalText = T("Сумма ≈ ", "Суммасы ≈ ", "Total ≈ ", "Toplam ≈ ", "Jami ≈ ") + (newTotal is { } nt ? MoneyText.Som(nt) : "—")
                      + T($" (было {MoneyText.Som(oldTotal)})", $" ({MoneyText.Som(oldTotal)} болчу)", $" (was {MoneyText.Som(oldTotal)})", $" (önce {MoneyText.Som(oldTotal)})", $" (edi {MoneyText.Som(oldTotal)})");
        // 2026-11-12 (UX правки возврата): в подвале — крупная новая сумма (ориентировочно) и строкой ниже, на сколько клиенту вернётся меньше/больше; точное «заберите / выдайте N сом» — после сохранения
        _total.Text = newTotal is { } shown ? "≈ " + MoneyText.Som(shown) : "—";
        if (newTotal is not { } now || Math.Abs(now - oldTotal) <= 0.004)
            _delta.Text = newTotal is null ? "" : T("без изменений", "өзгөрүүсүз", "no changes", "değişiklik yok", "o'zgarishsiz");
        else
        {
            var o = MoneyText.Som(oldTotal);
            var d = MoneyText.Som(Math.Abs(now - oldTotal));
            _delta.Text = now < oldTotal
                ? T($"было {o} · клиенту на {d} меньше", $"{o} болчу · кардарга {d} аз", $"was {o} · {d} less to the customer", $"önce {o} · müşteriye {d} daha az", $"edi {o} · mijozga {d} kam")
                : T($"было {o} · клиенту на {d} больше", $"{o} болчу · кардарга {d} көп", $"was {o} · {d} more to the customer", $"önce {o} · müşteriye {d} daha fazla", $"edi {o} · mijozga {d} ko'p");
        }
    }

    private void ShowError(string? text)
    {
        _error.Text = text ?? "";
        _error.IsVisible = !string.IsNullOrWhiteSpace(text);
        // 2026-11-12 (UX): плашка ошибки — внизу прокручиваемой части; показываем её, даже если окно прокручено к строкам
        if (_error.IsVisible)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _error.BringIntoView(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void Lock()
    {
        _save.IsEnabled = false;
        _delete.IsEnabled = false;
        _changed = true;
    }

    private async Task SaveAsync()
    {
        using var syncHold = ReturnJournalSync.Hold(); // 2026-11-13 (проверка денег, п. 2): пока правка уходит на сервер, фоновая сверка журнала молчит — журнал пишем здесь
        ShowError(null);
        var lines = ReadLines(out var parseError);
        if (lines is null)
        {
            ShowError(parseError);
            return;
        }
        var reason = (_reason.Text ?? "").Trim();
        if (CustomerReturnEdit.Validate(lines, reason, _original) is { } problem)
        {
            ShowError(problem);
            if (reason.Length < 5)
                _reason.Focus(); // 2026-11-12 (UX): не заполнена причина — курсор сразу в её поле
            return;
        }
        if (!CustomerReturnEdit.HasChanges(_original, lines))
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
            var card = await Api().GetDocumentAsync(ReturnDocKind.Customer, _doc.Id).ConfigureAwait(true);
            if (_closed)
                return;
            var fresh = ReturnEditState.Parse(card, DateTime.UtcNow);
            if (_doc.State.Version > 0 && fresh.Version != _doc.State.Version)
            {
                // 2026-11-12 (аудит В-2, В-3): прошлая отправка осталась без ответа (обрыв), а версия уже новая — если состав на сервере совпал с отправленным, это наша же правка
                // (журналы кассы обновляем по ней); иначе возврат изменил другой сотрудник
                if (Key.HasUnansweredAttempt && CustomerReturnEdit.SameComposition(CustomerReturnEdit.FromCards(card, default), lines))
                {
                    ApplySaved(card, lines);
                    ShowError(LikelySavedText());
                }
                else
                    ShowError(ReturnEditErrors.VersionConflictText());
                ForgetKey();
                Lock();
                return;
            }
            if (!ReturnEditGate.ShowEdit(ReturnDocKind.Customer, fresh))
            {
                ShowError(ReturnEditErrors.NotEditableText(fresh));
                Lock();
                return;
            }
            _shell.EndBusy();
            var (token, final) = await ReturnEditFlows.AskPinAsync(this, _doc with { State = fresh }, ReturnEditAction.Edit, fresh.Version, Api()).ConfigureAwait(true);
            if (token is null)
            {
                if (final is not null)
                {
                    ShowError(ReturnEditErrors.Text(final));
                    Lock();
                }
                return;
            }
            if (_closed || !_shell.TryBeginBusy())
                return;
            var body = CustomerReturnEdit.BuildPatchBody(lines, reason);
            var key = Key;
            var keyValue = key.Get(CustomerReturnEdit.Signature(_doc.Id, fresh.Version, lines, reason));
            var saved = await Api().PatchAsync(ReturnDocKind.Customer, _doc.Id, fresh.Version, body, token, keyValue).ConfigureAwait(true);
            key.OnSuccess();
            ForgetKey();
            // 2026-11-11 (сводка Умара): ответ PATCH — карточка с новой version и edit_count; берём их (вызывающий список всё равно перечитывает с сервера)
            SavedState = ReturnEditState.Parse(saved, DateTime.UtcNow);
            PosLogger.Log($"Возврат от клиента {_doc.Number} ({_doc.Id}) изменён: v{fresh.Version} → v{(SavedState.Version > 0 ? SavedState.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?")}"
                + $", правок {SavedState.EditCount}, строк {lines.Count(l => l.Qty > 0)}.", "INFO");
            _changed = true;
            ApplySaved(saved, lines); // 2026-11-12 (аудит К-1): журнал возвратов и смена кассы — по успешной правке
            _shell.EndBusy();
            Close(true);
        }
        catch (ReturnEditException e)
        {
            // 2026-11-12 (аудит В-3, В-2): «исход неизвестен» — только обрыв/5xx; отказ по существу (409 negative_stock и т.п.) — ключ забывается, признак «без ответа» не остаётся
            var unknown = ReturnEditErrors.IsOutcomeUnknown(e);
            if (unknown)
                Key.OnError(e.InnerException ?? e);
            else
                ForgetKey();
            ShowError(unknown ? UnknownOutcomeText() : T("Не сохранено: ", "Сакталган жок: ", "Not saved: ", "Kaydedilmedi: ", "Saqlanmadi: ") + ReturnEditErrors.Text(e, ProductNameById));
            if (e.IsFinal)
                Lock();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 2026-11-12 (аудит С-3): неожиданная ошибка не должна ронять кассу из обработчика кнопки
            PosLogger.Log($"Возврат от клиента {_doc.Id}: правка прервана ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            ShowError(T("Не сохранено: ", "Сакталган жок: ", "Not saved: ", "Kaydedilmedi: ", "Saqlanmadi: ") + ex.Message);
        }
        finally
        {
            _shell.EndBusy();
            if (!_changed)
                _save.IsEnabled = true;
        }
    }

    /// <summary>2026-11-12 (аудит К-1, В-1): правка прошла на сервере — запись возврата в журнале кассы пересчитана по новой сумме, в смене сторно/доплата, кассиру — что делать с деньгами.
    /// <paramref name="card"/> — ответ PATCH или свежая карточка (сумма документа); нет суммы — ориентировочная по строкам.</summary>
    private void ApplySaved(System.Text.Json.JsonElement card, IReadOnlyList<CustomerReturnEditLine> lines)
    {
        try
        {
            var oldTotal = (decimal)(_doc.Total > 0 ? _doc.Total : CustomerReturnEdit.Total(_original));
            var newTotal = CustomerReturnEdit.DocAmount(card) ?? (decimal)CustomerReturnEdit.Total(lines);
            JournalChange = CustomerReturnJournals.Edit(_saleId.Length > 0 ? _saleId : ReturnEditFlows.SaleIdOf(card), _doc.Id, oldTotal, newTotal, lines, PosApp.ActiveShiftId);
            MoneyNote = CustomerReturnJournals.MoneyNote(JournalChange, AppMode.IsOwner);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Возврат от клиента {_doc.Id}: журналы кассы после правки не обновлены ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    private async Task DeleteAsync()
    {
        try
        {
            if (await ReturnEditFlows.VoidAsync(this, _doc, Api()).ConfigureAwait(true))
            {
                _changed = true;
                Close(true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 2026-11-12 (аудит С-3): «Удалить возврат» — обработчик без try ронял программу при неожиданной ошибке
            PosLogger.Log($"Возврат от клиента {_doc.Id}: удаление прервано ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            ShowError(T("Не удалено: ", "Өчүрүлгөн жок: ", "Not deleted: ", "Silinmedi: ", "O'chirilmadi: ") + ex.Message);
        }
    }

    /// <summary>Для тестов окна: ввести количество в строку <paramref name="index"/>.</summary>
    public void SetQtyForTests(int index, string text) => _rows[index].Qty.Text = text;

    /// <summary>Для тестов окна: ввести причину.</summary>
    public void SetReasonForTests(string text) => _reason.Text = text;

    /// <summary>Для тестов окна: нажать «Сохранить».</summary>
    public Task SaveForTestsAsync() => SaveAsync();

    public bool SaveEnabledForTests => _save.IsEnabled;

    public string TotalText => _totalText;

    /// <summary>2026-11-12 (UX): строка под суммой в подвале — «было … · клиенту на … меньше/больше».</summary>
    public string DeltaTextForTests => _delta.Text ?? "";

    public string ErrorText => _error.IsVisible ? _error.Text ?? "" : "";
}
