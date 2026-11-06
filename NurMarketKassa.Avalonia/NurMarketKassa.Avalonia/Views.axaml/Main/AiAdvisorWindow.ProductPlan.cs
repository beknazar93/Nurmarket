using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-06 (В13, Л4-17): часть окна Нур Советника — вложения, карточки действий с товарами и накладной (строки 2550–3714 прежнего файла), без изменения логики.</summary>
public sealed partial class AiAdvisorWindow
{
    /// <summary>Одно вложение (например, снимок с камеры) — как раньше; добавляется к уже прикреплённым страницам.</summary>
    private void SetAttachment(byte[] data, string mime, string name)
    {
        if (_attachments.Count < MaxAttachments)
            _attachments.Add((data, mime, name));
        RenderAttachments();
    }

    private void ClearAttachment()
    {
        _attachments.Clear();
        RenderAttachments();
    }

    /// <summary>Подготовленные изменения, которые ждут «Выполнить» (или голосового «да, выполни» в звонке).</summary>
    private Func<Task<string>>? _pendingProductApply;
    private Action? _pendingProductCancel;
    // 2026-10-06: «поставь наценку 25 %» даёт новую карточку — прежняя гаснет, иначе два «Выполнить» создали бы товары дважды.
    private Action? _pendingProductSupersede;
    // 2026-10-19: поставщик, которого ИИ прочитал в последней накладной (ProductActionPlan.SupplierHint), — подсказка окну закупки.
    private (string? Name, string? Inn) _invoiceSupplierHint;
    // 2026-10-31: итоги последней накладной (InvoiceReconcile) — окно закупки сверяет с ними сумму строк.
    private InvoiceReference? _invoiceTotals;

    /// <summary>2026-10-06, владелец: «открывать товар на складе голосом». «Открыть» — сразу, остальное — карточкой с подтверждением.</summary>
    /// <summary>2026-10-07, владелец: «открывай вкладки только после команды открыть что-то из программы». Явное «открой / перейди /
    /// зайди / ач / кир» во фразе владельца.</summary>
    private static bool HasOpenCommand(string text) =>
        System.Text.RegularExpressions.Regex.Split((text ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}']+")
            .Any(w => new[] { "откр", "перей", "зайди", "зайти", "ачып", "ачкыла", "аччы", "кирип", "open", "aç", "och" }.Any(w.StartsWith) || w is "ач" or "кир");

    private void ShowProductSteps(List<ProductActionPlan.Step> steps, string request)
    {
        // Открыть раздел или товар — только если владелец сам сказал «открой» (ИИ по своей воле вкладки не переключает).
        // 2026-10-27: раздел от нейросети открывается по словарю с проверкой, а в чат идёт факт («Открыл …» / «Не получилось …»), а не слова модели.
        foreach (var open in steps.Where(st => st.Op is "open" or "open_section" && HasOpenCommand(request)).Take(1))
            _ = ShowOpenResultAsync(open);
        // 2026-10-06: «найди фото» от ИИ — не ставим первое найденное, а показываем варианты на выбор.
        var photos = steps.Where(st => st.Op == "photo").Select(st => st.Product).DistinctBy(p => p.Id).Take(3).ToList();
        if (photos.Count > 0)
            _ = ShowPhotoChoicesAsync(photos);
        var changes = steps.Where(st => st.Op is not ("open" or "open_section" or "photo")).ToList();
        if (changes.Count > 0)
            AddProductActionsCard(changes, request);
    }

    private static bool LooksLikeInvoiceRequest(string request)
    {
        var text = (request ?? "").ToLowerInvariant();
        return new[] { "накладн", "накладную", "счёт-фактур", "счет-фактур", "invoice", "fatura", "yuk xati", "накладнойдун" }
            .Any(text.Contains);
    }

    // 2026-10-06, владелец «завис!!!» (снимок карточки с полями штрихкода): карточка сама ставила курсор в поле штрихкода —
    // ответ на вопрос ИИ «наценка 20 % — оставить?» печатался туда, Enter ничего не отправлял. Теперь курсор не трогаем,
    // а скан сканера (очень быстрый набор + Enter) где бы ни стоял курсор попадает в следующее пустое поле штрихкода карточки.
    private List<TextBox>? _scanTargets;
    private string _scanBuf = "";
    private long _scanLastTick;
    private int _scanFastRun;
    private TextBox? _scanFocusBox;
    private string? _scanFocusText;
    private int _scanFocusCaret;
    private const int ScanInterkeyMs = 40;

    // 2026-10-15: прежде с 3-го быстрого символа нажатия глушились (e.Handled) в расчёте на Enter сканера. Без Enter символы
    // пропадали: автоповтор клавиши (~33 мс) превращал «100000» в «10», быстрый набор терял буквы; Shift сбрасывал буфер (буквы
    // сканера в верхнем регистре терялись), суффикс Tab не принимался. Теперь символы НЕ глушатся — печатаются, куда стоит курсор;
    // только когда быстрая серия закончилась Enter или Tab, это скан: поле с курсором возвращается к тексту до серии, а код уходит
    // в поле штрихкода. Серия из одной и той же клавиши (автоповтор) сканом не считается; Shift буфер не сбрасывает.
    private void ScanKeyDown(object? sender, KeyEventArgs e)
    {
        if (_scanTargets is not { Count: > 0 } targets || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
            return;
        var focused = FocusManager?.GetFocusedElement();
        if (focused is TextBox fb && targets.Contains(fb))
            return; // курсор в поле штрихкода — скан печатается прямо туда, Enter — к следующему полю
        if (e.Key is Key.LeftShift or Key.RightShift or Key.CapsLock)
            return; // 2026-10-15: Shift перед заглавной буквой сканера — часть скана, буфер не сбрасываем
        var now = Environment.TickCount64;
        var fast = _scanBuf.Length > 0 && now - _scanLastTick is >= 0 and <= ScanInterkeyMs;
        if (e.Key is Key.Enter or Key.Tab)
        {
            var code = _scanBuf;
            var isScan = fast && code.Length >= 4 && _scanFastRun >= code.Length - 1 && code.Distinct().Count() > 1;
            var leaked = _scanFocusBox;
            var before = _scanFocusText;
            var caret = _scanFocusCaret;
            _scanBuf = "";
            _scanFastRun = 0;
            _scanFocusBox = null;
            if (!isScan)
                return;
            e.Handled = true;
            if (leaked is not null && ReferenceEquals(leaked, focused))
            {
                // 2026-10-15: символы скана напечатались в поле с курсором (вопрос и т. п.) — вернуть его текст как был до скана.
                leaked.Text = before;
                leaked.CaretIndex = Math.Min(caret, (before ?? "").Length);
            }
            if (targets.FirstOrDefault(t => t.IsEnabled && string.IsNullOrWhiteSpace(t.Text)) is { } target)
            {
                target.Text = code;
                target.BringIntoView();
            }
            return;
        }
        char? ch = e.Key switch
        {
            >= Key.D0 and <= Key.D9 => (char)('0' + (e.Key - Key.D0)),
            >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (e.Key - Key.NumPad0)),
            // 2026-10-15: буква — с регистром, как её прислал сканер (KeySymbol).
            // 2026-10-16: KeySymbol идёт по текущей раскладке: при русской (или турецкой) Code128 «AB12345» приходил как «фи12345»,
            // товар по штрихкоду не находился и создавался новый с мусорным штрихкодом. KeySymbol берём, только если это латинская
            // буква; иначе буква — по самой клавише (e.Key не зависит от раскладки), регистр — по Shift и CapsLock.
            >= Key.A and <= Key.Z => e.KeySymbol is { Length: 1 } sym && char.IsAsciiLetter(sym[0])
                ? sym[0]
                : (char)((((e.KeyModifiers & KeyModifiers.Shift) != 0) ^ (OperatingSystem.IsWindows() && Console.CapsLock) ? 'A' : 'a') + (e.Key - Key.A)),
            Key.OemMinus or Key.Subtract => '-',
            _ => null,
        };
        if (ch is null)
        {
            _scanBuf = "";
            _scanFastRun = 0;
            _scanFocusBox = null;
            return;
        }
        if (fast)
            _scanFastRun++;
        else
        {
            _scanBuf = "";
            _scanFastRun = 0;
            _scanFocusBox = focused as TextBox;
            _scanFocusText = _scanFocusBox?.Text;
            _scanFocusCaret = _scanFocusBox?.CaretIndex ?? 0;
        }
        _scanBuf += ch;
        _scanLastTick = now;
    }

    /// <summary>2026-10-24: строка блока «Штрихкоды товаров» карточки накладной — одна позиция накладной со своим полем.</summary>
    private sealed class BarcodeRow
    {
        public int Index;
        public ProductActionPlan.Step Step = null!;
        public TextBox Box = null!;
        public Border Host = null!;
        public TextBlock Note = null!, Num = null!, Label = null!;
        public Grid Grid = null!;
        public bool Editable;
        public bool DupMarked;
        public string Initial = "";
        public string? LastCode;
        public NurMarketKassa.Models.Pos.CatalogProductTileVm? Existing;
        public string Code => (Box.Text ?? "").Trim();
    }

    /// <summary>Раскладка строки: широкая — «№ · название · кол-во» слева, поле справа, пометка под ними; узкая (&lt; 700 px) — подпись
    /// над полем, поле на всю ширину.</summary>
    private static void PlaceBarcodeRow(BarcodeRow r, bool narrow)
    {
        Grid.SetRow(r.Num, 0); Grid.SetColumn(r.Num, 0);
        if (narrow)
        {
            Grid.SetRow(r.Label, 0); Grid.SetColumn(r.Label, 1); Grid.SetColumnSpan(r.Label, 2);
            Grid.SetRow(r.Host, 1); Grid.SetColumn(r.Host, 0); Grid.SetColumnSpan(r.Host, 3);
            Grid.SetRow(r.Note, 2); Grid.SetColumn(r.Note, 0); Grid.SetColumnSpan(r.Note, 3);
        }
        else
        {
            Grid.SetRow(r.Label, 0); Grid.SetColumn(r.Label, 1); Grid.SetColumnSpan(r.Label, 1);
            Grid.SetRow(r.Host, 0); Grid.SetColumn(r.Host, 2); Grid.SetColumnSpan(r.Host, 1);
            Grid.SetRow(r.Note, 1); Grid.SetColumn(r.Note, 1); Grid.SetColumnSpan(r.Note, 2);
        }
    }

    /// <summary>2026-10-06: предложенные ИИ действия с товарами — карточкой; выполняются только по «Выполнить» (ProductActionPlan).</summary>
    /// <param name="proposedUtc">2026-10-16: когда предложена исходная карточка (у отредактированной — время исходной): карточка,
    /// предложенная раньше выполнения такой же (в боте или здесь), не выполняется повторно (ProductActionPlan.ExecuteAllAsync).</param>
    private void AddProductActionsCard(List<ProductActionPlan.Step> steps, string request, DateTime? proposedUtc = null)
    {
        var proposed = proposedUtc ?? DateTime.UtcNow;
        var invoice = LooksLikeInvoiceRequest(request);
        // 2026-10-19, владелец: «при скане накладной добавляет товар на склад напрямую, а не через закупку». Приход и новые товары
        // с количеством — закупка у поставщика: «Провести закупку…» открывает окно закупки (InvoiceEditorWindow), черновик живёт
        // в карточке (повтор после обрыва — те же строки и тот же ключ идемпотентности).
        var purchase = steps.Any(ProductActionPlan.IsPurchaseStep);
        var supplierHint = _invoiceSupplierHint;
        var invoiceTotals = _invoiceTotals;
        InvoiceEditorWindow.PurchaseDraft? draft = null;
        AdvisorSupplierPrompt? supplierPrompt = null; // 2026-10-30: «Поставщик «X» не найден — создать?» в карточке накладной
        var editorOpen = false;
        var title = new TextBlock
        {
            Text = invoice
                ? T("Накладная — проверьте строки и итоги", "Накладная — саптарды жана жыйынтыкты текшериңиз", "Invoice — review items and totals", "Fatura — satırları ve toplamları kontrol edin", "Yuk xati — qatorlar va jami summani tekshiring")
                : T("Изменить товары?", "Товарларды өзгөртөлүбү?", "Change the products?", "Ürünler değiştirilsin mi?", "Mahsulotlarni o'zgartiraymi?"),
            FontWeight = FontWeight.Bold,
            FontSize = 14.5,
        };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        // 2026-10-06, владелец: «при загрузке товара спрашивать штрихкод через сканер». У нового товара без штрихкода — поле
        // «отсканируйте»: сканер пишет в поле и жмёт Enter — курсор переходит к следующему. Штрихкод уже есть у товара в
        // каталоге — это не новый товар, а приход к нему (дубль не создаётся).
        var body = new StackPanel { Spacing = 4 };
        var barcodeBoxes = new Dictionary<int, (TextBox Box, TextBlock Note)>();
        // 2026-10-10: список новых товаров и приходов показываем таблицей, даже если формулировка запроса не распознана как накладная.
        var showProductTable = invoice || steps.Any(step => step.Op is "create" or "receive");
        if (showProductTable)
        {
            // 2026-10-21 (О10): заголовки накладной — на языке программы (были только по-русски).
            var header = invoice
                ? new[]
                {
                    T("№", "№", "No.", "No.", "№"),
                    T("Наименование товара", "Товардын аталышы", "Product name", "Ürün adı", "Mahsulot nomi"),
                    T("Штрихкод / Артикул", "Штрихкод / Артикул", "Barcode / SKU", "Barkod / Stok kodu", "Shtrix-kod / Artikul"),
                    T("Ед. изм.", "Өлчөм бирд.", "Unit", "Birim", "O'lchov bir."),
                    T("Кол-во", "Саны", "Qty", "Miktar", "Miqdor"),
                    T("Цена закупа", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"),
                    T("Розничная цена", "Чекене баа", "Retail price", "Perakende fiyatı", "Chakana narx"),
                    T("Сумма (закуп)", "Сумма (сатып алуу)", "Total (purchase)", "Tutar (alış)", "Summa (xarid)"),
                }
                : new[]
                {
                    T("№", "№", "No.", "No.", "№"),
                    T("Наименование товара", "Товардын аталышы", "Product name", "Ürün adı", "Mahsulot nomi"),
                    T("Действие", "Аракет", "Action", "İşlem", "Amal"),
                    T("Ед. изм.", "Өлчөм бирд.", "Unit", "Birim", "O'lchov bir."),
                    T("Кол-во", "Саны", "Qty", "Miktar", "Miqdor"),
                    T("Цена закупа", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"),
                    T("Розничная цена", "Чекене баа", "Retail price", "Perakende fiyatı", "Chakana narx"),
                    T("Штрихкод / Артикул", "Штрихкод / Артикул", "Barcode / SKU", "Barkod / Stok kodu", "Shtrix-kod / Artikul"),
                    T("Наценка", "Үстөк", "Markup", "Kâr payı", "Ustama"),
                };
            var rows = steps.Select((step, index) =>
            {
                var unit = string.IsNullOrWhiteSpace(step.Unit)
                    ? (step.Product.MustWeigh ? "кг" : "шт")
                    : step.Unit!;
                var quantity = step.Qty is { } q ? q.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) : "-";
                var purchase = step.Purchase is { } cost ? cost.ToString("N2", System.Globalization.CultureInfo.CurrentCulture) : "-";
                var retailValue = step.Price ?? (step.Product.PriceValue > 0 ? step.Product.PriceValue : null);
                var retail = retailValue is { } price ? price.ToString("N2", System.Globalization.CultureInfo.CurrentCulture) : "-";
                var lineTotal = step.Qty is { } qty && step.Purchase is { } unitCost
                    ? (qty * unitCost).ToString("N2", System.Globalization.CultureInfo.CurrentCulture)
                    : "-";
                var barcode = !string.IsNullOrWhiteSpace(step.Barcode) ? step.Barcode : step.Product.Barcode;
                if (invoice)
                {
                    return new[]
                    {
                        (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), step.Product.Title,
                        string.IsNullOrWhiteSpace(barcode) ? "-" : barcode, unit, quantity, purchase, retail, lineTotal,
                    };
                }
                var markup = step.RaisedToMin
                    ? $"мин. {ProductActionPlan.MinMarkupPercent:0.#}%"
                    : step.Purchase is > 0 && retailValue is { } retailPrice
                        ? $"{((retailPrice / step.Purchase.Value - 1) * 100):0.#}%"
                        : "-";
                var operation = step.Op == "create"
                    ? T("Новый товар", "Жаңы товар", "New product", "Yeni ürün", "Yangi mahsulot")
                    : step.Op == "receive"
                        ? T("Приход", "Киреше", "Stock receipt", "Stok girişi", "Kirim")
                        : ProductActionPlan.Describe(step);
                return new[]
                {
                    (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), step.Product.Title, operation, unit,
                    quantity, purchase, retail, string.IsNullOrWhiteSpace(barcode) ? "-" : barcode, markup,
                };
            }).ToList();
            body.Children.Add(BuildTable(header, rows));
            // 2026-10-15: в таблице у такой строки — текущая цена; почему цена накладной не поставлена — пометкой под таблицей.
            var priceNotes = steps.Select(step => ProductActionPlan.PriceNotLoweredNote(step) is { } n ? $"• «{step.Product.Title}»: {n}" : null)
                .Where(n => n is not null).ToList();
            if (priceNotes.Count > 0)
            {
                var priceNote = new TextBlock { Text = string.Join("\n", priceNotes), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(2, 2, 2, 4) };
                Use(priceNote, TextBlock.ForegroundProperty, "BrushWarning");
                body.Children.Add(priceNote);
            }

        }
        if (invoice)
        {
            var totalNames = steps.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            var unitTotals = steps
                .Where(step => step.Qty is not null)
                .GroupBy(step => string.IsNullOrWhiteSpace(step.Unit) ? (step.Product.MustWeigh ? "кг" : "шт") : step.Unit!)
                .Select(group => $"{group.Sum(step => step.Qty!.Value).ToString("0.###", System.Globalization.CultureInfo.CurrentCulture)} {group.Key}")
                .ToList();
            var totalQuantity = unitTotals.Count == 0 ? "-" : string.Join("; ", unitTotals);
            var totalPurchase = steps.All(step => step.Qty is not null && step.Purchase is not null)
                ? steps.Sum(step => step.Qty!.Value * step.Purchase!.Value).ToString("N2", System.Globalization.CultureInfo.CurrentCulture) + " " + T("сом", "сом", "som", "som", "so'm")
                : "-";
            var totals = new TextBlock
            {
                Text = T($"Всего наименований: {totalNames}\nОбщее количество единиц: {totalQuantity}\nОбщая сумма закупа: {totalPurchase}",
                    $"Бардык аталыштар: {totalNames}\nБирдиктердин жалпы саны: {totalQuantity}\nСатып алуунун жалпы суммасы: {totalPurchase}",
                    $"Items: {totalNames}\nTotal units: {totalQuantity}\nTotal purchase amount: {totalPurchase}",
                    $"Kalem sayısı: {totalNames}\nToplam birim: {totalQuantity}\nToplam alış tutarı: {totalPurchase}",
                    $"Jami nomlar: {totalNames}\nJami birliklar: {totalQuantity}\nJami xarid summasi: {totalPurchase}"),
                FontSize = 13.5,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
                Margin = new Thickness(2, 4, 2, 6),
            };
            Use(totals, TextBlock.ForegroundProperty, "BrushText");
            body.Children.Add(totals);
        }
        if (purchase)
        {
            var purchaseNote = new TextBlock
            {
                Text = T("Накладная проводится закупкой у поставщика (как «Приёмка» склада): нажмите «Провести закупку…», выберите поставщика и оплату.",
                    "Накладной жеткирүүчүдөн сатып алуу менен өткөрүлөт (кампанын «Кабыл алуусу» сыяктуу): «Сатып алууну өткөрүү…» баскычын басып, жеткирүүчүнү жана төлөмдү тандаңыз.",
                    "The invoice is posted as a purchase from a supplier (like warehouse “Receiving”): press “Post purchase…”, choose the supplier and payment.",
                    "Fatura tedarikçiden alım olarak işlenir (depodaki «Mal kabul» gibi): «Alımı işle…»ye basın, tedarikçiyi ve ödemeyi seçin.",
                    "Yuk xati yetkazib beruvchidan xarid sifatida o'tkaziladi (ombordagi «Qabul» kabi): «Xaridni o'tkazish…»ni bosing, yetkazib beruvchi va to'lovni tanlang."),
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(2, 0, 2, 4),
            };
            Use(purchaseNote, TextBlock.ForegroundProperty, "BrushTextSoft");
            body.Children.Add(purchaseNote);
            // 2026-10-30, жалоба «нет создания поставщика при «Оприходовать товар»»: поставщик из накладной — найден / создать / выбрать другого / без поставщика (без ИИ).
            if (supplierHint.Name is not null || supplierHint.Inn is not null)
            {
                supplierPrompt = new AdvisorSupplierPrompt(this, supplierHint.Name, supplierHint.Inn, ProductActionPlan.LastSupplierExtra.Phone, ProductActionPlan.LastSupplierExtra.Address);
                body.Children.Add(supplierPrompt);
            }
            // 2026-10-19, ревью: часть строк этой накладной уже проведена (прежней карточкой) — показать их; в окне закупки они
            // помечены «уже проведено» и без второго подтверждения не проводятся.
            var alreadyPosted = steps.Where(ProductActionPlan.IsPurchaseStep)
                .Select(st => (st, hit: InvoiceEditorWindow.PostedLines.Find(null, st.Op == "receive" ? st.Product.Id : null, new[] { st.Product.Title }, st.Qty ?? 0, st.Purchase ?? 0)))
                .Where(x => x.hit is not null)
                .Select(x => $"«{x.st.Product.Title}» {(x.st.Qty ?? 0).ToString("0.###", System.Globalization.CultureInfo.CurrentCulture)} — {x.hit!.At:HH:mm}")
                .ToList();
            if (alreadyPosted.Count > 0)
            {
                var postedNote = new TextBlock
                {
                    Text = "⚠ " + T("Уже проведено закупкой: ", "Сатып алуу менен өткөрүлгөн: ", "Already posted as a purchase: ", "Alım olarak zaten işlendi: ", "Xarid sifatida o'tkazilgan: ")
                           + string.Join("; ", alreadyPosted),
                    FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(2, 0, 2, 4),
                };
                Use(postedNote, TextBlock.ForegroundProperty, "BrushWarning");
                body.Children.Add(postedNote);
            }
        }
        // Только точное совпадение штрихкода (Find ищет и по названию — недописанный код не должен «найти» чужой товар).
        static NurMarketKassa.Models.Pos.CatalogProductTileVm? ByBarcode(string code) =>
            code.Length >= 4 && ProductActions.Find(code) is { } p && string.Equals((p.Barcode ?? "").Trim(), code, StringComparison.OrdinalIgnoreCase) ? p : null;
        // 2026-10-15: новый товар по скану оказался товаром склада — приход к нему; цена нового товара (по наценке) ниже текущей
        // сама не ставится, как и у прихода из разбора (ProductActionPlan.KeepCurrentPriceIfLower).
        static ProductActionPlan.Step AsReceive(ProductActionPlan.Step st, NurMarketKassa.Models.Pos.CatalogProductTileVm existing) =>
            ProductActionPlan.KeepCurrentPriceIfLower(st.Price, existing.PriceValue) is var (price, notLowered)
                ? new ProductActionPlan.Step("receive", existing, st.Qty, null, null, st.Purchase, price, ExpectedStock: existing.Quantity, PriceNotLowered: notLowered)
                : st;
        // 2026-10-24, владелец: «внизу … поле для ввода штрих кода (столько поле для ввода, сколько товара)», «разделить каждому
        // товару своё поле». Прежде поля были безымянными, только у новых товаров без кода. Теперь — блок «Штрихкоды товаров»:
        // строка на КАЖДУЮ позицию накладной (приход или новый товар) «№ · название · кол-во» со своим полем; известный код
        // подставлен (у товара склада — серый, не правится); счётчик «введено N из M»; нехватка кодов проведению не мешает,
        // повтор одного кода у двух строк — мешает. Код сохраняется существующими путями: новый товар — закупкой (Barcode шага),
        // приход к товару склада без кода — шагом set_barcode (ProductActions.UpdateFieldsAsync) после закупки.
        var barcodeRows = new List<BarcodeRow>();
        var dupRows = new HashSet<int>();
        var dupShown = false;
        Action? afterRefresh = null;
        TextBlock? counterText = null, statusText = null, dupText = null;
        Border? blockHost = null;
        string ApplyName() => purchase
            ? T("Провести закупку…", "Сатып алууну өткөрүү…", "Post purchase…", "Alımı işle…", "Xaridni o'tkazish…")
            : T("Применить изменения", "Өзгөртүүлөрдү колдонуу", "Apply changes", "Değişiklikleri uygula", "O'zgarishlarni qo'llash");
        void SetNote(BarcodeRow r, string text, string brush)
        {
            r.Note.Text = text;
            r.Note.IsVisible = text.Length > 0;
            if (text.Length > 0)
                Use(r.Note, TextBlock.ForegroundProperty, brush);
        }
        void RefreshBarcodes()
        {
            foreach (var r in barcodeRows)
            {
                var c = r.Code;
                if (!string.Equals(c, r.LastCode, StringComparison.Ordinal))
                {
                    r.LastCode = c;
                    r.Existing = r.Editable ? ByBarcode(c) : null;
                }
            }
            // Повтор кода: тот же код (от 4 знаков) у двух строк, если хоть одна из них правится. Один и тот же товар склада
            // дважды в накладной (серые строки с одним кодом) — не повтор, приход к нему сольётся в одну строку.
            dupRows.Clear();
            foreach (var g in barcodeRows.Where(r => r.Code.Length >= 4).GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase))
                if (g.Count() > 1 && g.Any(r => r.Editable))
                    foreach (var r in g)
                        dupRows.Add(r.Index);
            foreach (var r in barcodeRows)
            {
                var code = r.Code;
                var dup = dupRows.Contains(r.Index);
                if (dup != r.DupMarked)
                {
                    r.DupMarked = dup;
                    if (dup)
                        Use(r.Host, Border.BorderBrushProperty, "BrushDanger");
                    else
                        r.Host.ClearValue(Border.BorderBrushProperty);
                }
                if (dup)
                {
                    var other = barcodeRows.First(o => o.Index != r.Index && string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase)).Index + 1;
                    SetNote(r, T($"этот штрихкод уже у строки {other}", $"бул штрихкод {other}-сапта бар", $"this barcode is already on line {other}",
                        $"bu barkod zaten {other}. satırda", $"bu shtrix-kod {other}-qatorda allaqachon bor"), "BrushDanger");
                }
                else if (!r.Editable)
                    SetNote(r, T("уже у товара", "товарда бар", "already on the product", "üründe zaten var", "mahsulotda allaqachon bor"), "BrushTextSoft");
                else if (code.Length == 0)
                    SetNote(r, "", "BrushTextSoft");
                else if (code.Length < 4)
                    SetNote(r, T("слишком короткий — проверьте", "өтө кыска — текшериңиз", "too short — please check", "çok kısa — kontrol edin", "juda qisqa — tekshiring"), "BrushWarning");
                else if (r.Existing is { } existing && r.Step.Op == "create")
                {
                    var text = T($"Этот штрихкод уже у товара «{existing.Title}» — будет приход к нему, новый товар не создаётся.",
                        $"Бул штрихкод «{existing.Title}» товарында бар — ага кириш болот, жаңы товар түзүлбөйт.",
                        $"This barcode already belongs to “{existing.Title}” — it will be received there, no new product.",
                        $"Bu barkod zaten «{existing.Title}» ürününde — ona giriş yapılır, yeni ürün oluşturulmaz.",
                        $"Bu shtrix-kod «{existing.Title}» mahsulotida bor — unga kirim bo'ladi, yangi mahsulot yaratilmaydi.");
                    // 2026-10-15: цена склада не снижается приходом — сказать об этом здесь же.
                    if (ProductActionPlan.PriceNotLoweredNote(AsReceive(r.Step, existing)) is { } priceNote)
                        text += " " + priceNote;
                    SetNote(r, text, "BrushWarning");
                }
                else if (r.Existing is { } owner && !string.Equals(owner.Id, r.Step.Product.Id, StringComparison.OrdinalIgnoreCase))
                    SetNote(r, T($"этот штрихкод уже у товара «{owner.Title}» — для этой строки он не будет поставлен",
                        $"бул штрихкод «{owner.Title}» товарында бар — бул сап үчүн коюлбайт",
                        $"this barcode already belongs to “{owner.Title}” — it will not be set for this line",
                        $"bu barkod zaten «{owner.Title}» ürününde — bu satır için atanmaz",
                        $"bu shtrix-kod «{owner.Title}» mahsulotida bor — bu qator uchun qo'yilmaydi"), "BrushWarning");
                else if (r.Initial.Length > 0 && string.Equals(code, r.Initial, StringComparison.Ordinal))
                    SetNote(r, T("прочитано с накладной — сверьте", "накладнойдон окулду — салыштырыңыз", "read from the invoice — please check",
                        "faturadan okundu — kontrol edin", "yuk xatidan o'qildi — tekshiring"), "BrushTextSoft");
                else if (r.Step.Op == "create")
                    SetNote(r, T("✓ новый товар", "✓ жаңы товар", "✓ new product", "✓ yeni ürün", "✓ yangi mahsulot"), "BrushSuccess");
                else
                    SetNote(r, T("после проведения штрихкод будет поставлен товару", "өткөргөндөн кийин штрихкод товарга коюлат",
                        "after posting, the barcode is set on the product", "işlemden sonra barkod ürüne atanır", "o'tkazilgandan keyin shtrix-kod mahsulotga qo'yiladi"), "BrushTextSoft");
            }
            var editable = barcodeRows.Where(r => r.Editable).ToList();
            var filled = editable.Count(r => r.Code.Length > 0);
            var missing = editable.Count - filled;
            if (counterText is not null)
            {
                counterText.IsVisible = editable.Count > 0;
                counterText.Text = T($"введено {filled} из {editable.Count}", $"киргизилди {filled} / {editable.Count}", $"entered {filled} of {editable.Count}",
                    $"girilen {filled} / {editable.Count}", $"kiritildi {filled} / {editable.Count}");
            }
            if (statusText is not null)
            {
                statusText.IsVisible = editable.Count > 0 && (missing > 0 || dupRows.Count == 0); // при повторе кода вместо «все введены» — красная строка ниже
                if (missing > 0)
                {
                    statusText.Text = T($"⚠ Не хватает штрихкодов: {missing}. Можно провести и без них — товар создастся без штрихкода, поставить его можно позже в карточке товара.",
                        $"⚠ Штрихкод жетишпейт: {missing}. Алар жок да өткөрсө болот — товар штрихкодсуз түзүлөт, аны кийин товардын карточкасында коюуга болот.",
                        $"⚠ Barcodes missing: {missing}. You can still post without them — the product is created without a barcode; you can add it later in the product card.",
                        $"⚠ Eksik barkod: {missing}. Onlarsız da işleyebilirsiniz — ürün barkodsuz oluşturulur, barkodu sonra ürün kartından ekleyebilirsiniz.",
                        $"⚠ Shtrix-kod yetishmayapti: {missing}. Ularsiz ham o'tkazish mumkin — mahsulot shtrix-kodsiz yaratiladi, uni keyin mahsulot kartasida qo'yish mumkin.");
                    Use(statusText, TextBlock.ForegroundProperty, "BrushWarning");
                }
                else
                {
                    var name = ApplyName();
                    statusText.Text = T($"Все штрихкоды введены — нажмите «{name}».", $"Бардык штрихкоддор киргизилди — «{name}» баскычын басыңыз.",
                        $"All barcodes entered — press “{name}”.", $"Tüm barkodlar girildi — «{name}» düğmesine basın.", $"Barcha shtrix-kodlar kiritildi — «{name}» tugmasini bosing.");
                    Use(statusText, TextBlock.ForegroundProperty, "BrushTextSoft");
                }
            }
            if (dupText is not null)
                dupText.IsVisible = dupRows.Count > 0;
            afterRefresh?.Invoke();
        }
        BarcodeRow AddBarcodeRow(int i, StackPanel host)
        {
            var step = steps[i];
            var unit = string.IsNullOrWhiteSpace(step.Unit) ? (step.Product.MustWeigh ? "кг" : "шт") : step.Unit!;
            var qtyText = step.Qty is { } q ? " · " + q.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) + " " + unit : "";
            var known = !string.IsNullOrWhiteSpace(step.Barcode) ? step.Barcode!.Trim() : (step.Product.Barcode ?? "").Trim();
            // Приход к товару склада с известным штрихкодом — серое поле «только чтение».
            var editable = !(step.Op == "receive" && known.Length > 0);
            var num = new TextBlock { Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Use(num, TextBlock.ForegroundProperty, "BrushTextSoft");
            var label = new TextBlock { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            label.Inlines!.Add(new Avalonia.Controls.Documents.Run(step.Product.Title) { FontWeight = FontWeight.SemiBold });
            if (qtyText.Length > 0)
                label.Inlines.Add(new Avalonia.Controls.Documents.Run(qtyText));
            Use(label, TextBlock.ForegroundProperty, "BrushText");
            // 2026-10-24: подсказка короткая — поле строки 300 px, длинная обрезалась.
            var box = UiKit.Input(this, T("Штрихкод: скан или ввод", "Штрихкод: скан же жазуу", "Barcode: scan or type",
                "Barkod: okutun veya yazın", "Shtrix-kod: skaner yoki kiritish"), 36);
            box.Text = known;
            if (!editable)
            {
                box.IsReadOnly = true;
                box.Focusable = false;
                box.IsTabStop = false;
                box.IsHitTestVisible = false;
                Use(box, TextBox.ForegroundProperty, "BrushTextSoft");
            }
            var fieldHost = new Border { Child = box, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(11) };
            var note = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 0, 0, 2) };
            var row = new BarcodeRow { Index = i, Step = step, Box = box, Host = fieldHost, Note = note, Num = num, Label = label, Editable = editable, Initial = editable ? known : "" };
            row.Grid = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(30, GridUnitType.Pixel), new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(300, GridUnitType.Pixel) },
                RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) },
                Children = { num, label, fieldHost, note },
            };
            PlaceBarcodeRow(row, false);
            var rowStack = new StackPanel { Spacing = 1, Children = { row.Grid } };
            host.Children.Add(rowStack);
            barcodeRows.Add(row);
            if (!editable)
                return row;
            box.TextChanged += (_, _) => RefreshBarcodes();
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter)
                    return;
                e.Handled = true;
                // Следующее пустое правимое поле (серые пропускаются); все заполнены — фокус остаётся, счётчик это пишет.
                barcodeRows.Where(r => r.Editable && r.Index > i && string.IsNullOrWhiteSpace(r.Box.Text)).Select(r => r.Box).FirstOrDefault()?.Focus();
            };
            barcodeBoxes[i] = (box, note);
            // 2026-10-07, владелец: «если наименование есть — поставь штрихкод из barcode-list.ru, но дай проверить и сверить с товаром».
            // Под строкой — варианты из базы с их названием; нажатие вписывает штрихкод в поле (поле можно поправить до «Выполнить»).
            if (step.Op == "create" && known.Length == 0)
            {
                var suggest = new WrapPanel { Margin = new Thickness(30, 0, 0, 4), IsVisible = false };
                rowStack.Children.Add(suggest);
                var stepTitle = step.Product.Title;
                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    List<BarcodeListLookup.Suggestion> found;
                    try
                    {
                        found = await BarcodeListLookup.FindByNameAsync(stepTitle, LifetimeToken).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException) when (_closed)
                    {
                        return; // 2026-11-06 (Л4-26): окно закрыли
                    }
                    if (found.Count == 0 || !string.IsNullOrWhiteSpace(box.Text))
                        return;
                    var hint = new TextBlock
                    {
                        Text = T("Штрихкод из barcode-list.ru — сверьте с товаром:", "barcode-list.ru'дан штрихкод — товар менен салыштырыңыз:",
                            "Barcode from barcode-list.ru — check against the product:", "barcode-list.ru'dan barkod — ürünle karşılaştırın:",
                            "barcode-list.ru'dan shtrix-kod — mahsulot bilan solishtiring:"),
                        FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4),
                    };
                    Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");
                    suggest.Children.Add(hint);
                    foreach (var s in found)
                    {
                        var pick = UiKit.Ghost(this, $"{s.Barcode} · {s.Name}");
                        pick.Height = 30;
                        pick.FontSize = 12;
                        pick.Padding = new Thickness(10, 0);
                        pick.Margin = new Thickness(0, 0, 6, 4);
                        pick.Click += (_, _) => box.Text = s.Barcode;
                        suggest.Children.Add(pick);
                    }
                    suggest.IsVisible = true;
                });
            }
            return row;
        }
        for (var i = 0; i < steps.Count; i++)
        {
            if (!showProductTable)
            {
                var line = new TextBlock { Text = "• " + ProductActionPlan.Describe(steps[i]), FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
                Use(line, TextBlock.ForegroundProperty, "BrushText");
                body.Children.Add(line);
            }
        }
        if (showProductTable && steps.Any(st => st.Op is "create" or "receive"))
        {
            var rowsPanel = new StackPanel { Spacing = 6 };
            var titleRow = new Grid { ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) } };
            var blockTitle = new TextBlock
            {
                Text = T("Штрихкоды товаров", "Товарлардын штрихкоддору", "Product barcodes", "Ürün barkodları", "Mahsulotlar shtrix-kodlari"),
                FontSize = 14, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center,
            };
            Use(blockTitle, TextBlock.ForegroundProperty, "BrushText");
            counterText = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Use(counterText, TextBlock.ForegroundProperty, "BrushAccentStrong");
            Grid.SetColumn(counterText, 2);
            titleRow.Children.Add(blockTitle);
            titleRow.Children.Add(counterText);
            rowsPanel.Children.Add(titleRow);
            for (var i = 0; i < steps.Count; i++)
                if (steps[i].Op is "create" or "receive")
                    AddBarcodeRow(i, rowsPanel);
            // 2026-10-26 (требование владельца): «Сгенерировать» — свой штрихкод новым товарам с пустым полем (BarcodeGenerator:
            // EAN-13, не с 0 и не весовой; введённые руками коды не трогает, повторов с каталогом и между строками нет).
            if (steps.Any(st => st.Op == "create"))
            {
                var makeCodes = UiKit.Ghost(this, T("Сгенерировать", "Түзүү", "Generate", "Oluştur", "Yaratish"));
                makeCodes.Height = 32;
                makeCodes.FontSize = 12.5;
                makeCodes.Padding = new Thickness(12, 0);
                makeCodes.Margin = new Thickness(0, 0, 10, 0);
                ToolTip.SetTip(makeCodes, T("Проставить свой штрихкод новым товарам с пустым полем", "Талаасы бош жаңы товарларга өз штрихкодун коюу",
                    "Set an own barcode on new products with an empty field", "Alanı boş yeni ürünlere kendi barkodunu ver", "Maydoni bo'sh yangi mahsulotlarga o'z shtrix-kodini qo'yish"));
                Grid.SetColumn(makeCodes, 1);
                makeCodes.Click += (_, _) =>
                {
                    var typed = barcodeRows.Select(r => r.Code).Where(c => c.Length > 0).ToList();
                    foreach (var r in barcodeRows)
                    {
                        if (r.Step.Op != "create" || !r.Editable || r.Code.Length > 0)
                            continue;
                        var code = BarcodeGenerator.GenerateForCatalog(typed);
                        typed.Add(code);
                        r.Box.Text = code;
                    }
                };
                titleRow.Children.Add(makeCodes);
            }
            statusText = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(2, 2, 2, 0) };
            dupText = new TextBlock
            {
                Text = T("⚠ Один и тот же штрихкод стоит у двух строк — исправьте, иначе провести нельзя.", "⚠ Бир эле штрихкод эки сапта турат — оңдоңуз, болбосо өткөрүүгө болбойт.",
                    "⚠ The same barcode is on two lines — fix it, otherwise it cannot be posted.", "⚠ Aynı barkod iki satırda — düzeltin, aksi halde işlenemez.",
                    "⚠ Bir xil shtrix-kod ikki qatorda — tuzating, aks holda o'tkazib bo'lmaydi."),
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(2, 0, 2, 0), IsVisible = false,
            };
            Use(dupText, TextBlock.ForegroundProperty, "BrushDanger");
            rowsPanel.Children.Add(statusText);
            rowsPanel.Children.Add(dupText);
            blockHost = new Border { Child = rowsPanel, Margin = new Thickness(0, 6, 0, 2), Padding = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) };
            Use(blockHost, Border.BorderBrushProperty, "BrushBorder");
            // Узкое окно (< 700 px): подпись строки над полем, поле на всю ширину.
            var narrowNow = false;
            rowsPanel.SizeChanged += (_, e) =>
            {
                var narrow = e.NewSize.Width < 640;
                if (narrow == narrowNow)
                    return;
                narrowNow = narrow;
                foreach (var r in barcodeRows)
                    PlaceBarcodeRow(r, narrow);
            };
            body.Children.Add(blockHost);
            RefreshBarcodes();
        }

        // Шаги с отсканированными штрихкодами (или приход к уже существующему товару с этим штрихкодом).
        List<ProductActionPlan.Step> ResolveSteps()
        {
            var list = new List<ProductActionPlan.Step>();
            for (var i = 0; i < steps.Count; i++)
            {
                var st = steps[i];
                if (!barcodeBoxes.TryGetValue(i, out var b) || (b.Box.Text ?? "").Trim() is not { Length: >= 4 } code)
                {
                    list.Add(st);
                    continue;
                }
                if (st.Op == "receive")
                {
                    // Приход к товару склада без штрихкода: закупка — как есть, штрихкод ставит шаг set_barcode после неё
                    // (существующий путь ProductActions.UpdateFieldsAsync); код другого товара склада не ставим — сервер не примет дубль.
                    list.Add(st);
                    if (st.Product.Id is { Length: > 0 } pid && ByBarcode(code) is null
                        && !list.Any(x => x.Op == "set_barcode" && string.Equals(x.Product.Id, pid, StringComparison.OrdinalIgnoreCase)))
                        list.Add(new ProductActionPlan.Step("set_barcode", st.Product, null, code, null));
                    continue;
                }
                list.Add(ByBarcode(code) is { Id.Length: > 0 } existing ? AsReceive(st, existing) : st with { Barcode = code });
            }
            return list;
        }
        var scanTargets = barcodeBoxes.OrderBy(kv => kv.Key).Select(kv => kv.Value.Box).ToList();
        _scanTargets = scanTargets.Count > 0 ? scanTargets : null;

        // 2026-10-21 (И10): проверка перед «Выполнить» — цена ниже закупки, наценка < 0 или > 500 %, остаток в минус, списание больше
        // половины остатка, больше 20 товаров разом: предупреждение с цифрами в карточке и второе подтверждение.
        var warnings = ProductActionWarnings(steps);
        if (warnings.Count > 0)
            body.Children.Add(WarningsPanel(warnings));
        var apply = UiKit.Primary(this, purchase
            ? T("Провести закупку…", "Сатып алууну өткөрүү…", "Post purchase…", "Alımı işle…", "Xaridni o'tkazish…")
            : T($"Применить изменения ({steps.Count})", $"Өзгөртүүлөрдү колдонуу ({steps.Count})", $"Apply changes ({steps.Count})", $"Değişiklikleri uygula ({steps.Count})", $"O'zgarishlarni qo'llash ({steps.Count})"));
        apply.Height = 38;
        Avalonia.Automation.AutomationProperties.SetName(apply, purchase ? T("Провести закупку", "Сатып алууну өткөрүү", "Post purchase", "Alımı işle", "Xaridni o'tkazish")
            : T("Применить изменения", "Өзгөртүүлөрдү колдонуу", "Apply changes", "Değişiklikleri uygula", "O'zgarishlarni qo'llash"));
        var confirmedTwice = warnings.Count == 0;
        var cancel = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Height = 38;
        var result = new TextBlock { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, IsVisible = false, LineHeight = 20 };
        Use(result, TextBlock.ForegroundProperty, "BrushText");
        // 2026-10-24: сообщение «повторяются штрихкоды» (из Apply) гаснет, когда повтор исправлен.
        afterRefresh = () =>
        {
            if (dupShown && dupRows.Count == 0)
            {
                dupShown = false;
                result.IsVisible = false;
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { apply, cancel } };
        // 2026-10-11, тестировщик (иишка.md, 2): «рядом с одобрением — кнопка «Отредактировать»: подробный редактор списка».
        // 2026-10-19: редактор накладной стал окном закупки — его открывает «Провести закупку…» (отдельная «✎ Отредактировать» не нужна).
        var card = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10), MaxWidth = invoice ? 900 : 680, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Spacing = 8, Children = { title, body, buttons, result } },
        };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushAccentStrong");
        var finished = false;
        var superseded = false;
        void Cancel()
        {
            // 2026-10-19: пока открыто окно закупки, карточка не гаснет (новое предложение ниже её не отменяет).
            if (finished || editorOpen)
                return;
            finished = true;
            if (_pendingProductCancel == Cancel)
            {
                _pendingProductApply = null;
                _pendingProductCancel = null;
            }
            if (ReferenceEquals(_scanTargets, scanTargets))
                _scanTargets = null;
            buttons.IsVisible = false;
            result.IsVisible = true;
            foreach (var (box, _) in barcodeBoxes.Values)
                box.IsEnabled = false;
            // 2026-10-19, ревью: замена карточки после частичного проведения — «Проведено частично: …», карточка больше не нажимается,
            // остальные строки — в новой (там проведённые помечены по реестру InvoiceEditorWindow.PostedLines).
            result.Text = (draft is { PostedSummaries.Count: > 0 }
                    ? T("Проведено частично: ", "Жарым-жартылай өткөрүлдү: ", "Partly posted: ", "Kısmen işlendi: ", "Qisman o'tkazildi: ")
                      + "\n✓ " + string.Join("\n✓ ", draft.PostedSummaries) + "\n"
                    : "")
                + (superseded
                ? T("Заменено новым предложением ниже.", "Төмөнкү жаңы сунуш менен алмаштырылды.", "Replaced by the new proposal below.",
                    "Aşağıdaki yeni öneriyle değiştirildi.", "Quyidagi yangi taklif bilan almashtirildi.")
                : draft is { PostedSummaries.Count: > 0 }
                    ? T("Остальные строки не проведены.", "Калган саптар өткөрүлгөн жок.", "The remaining lines were not posted.", "Kalan satırlar işlenmedi.", "Qolgan qatorlar o'tkazilmadi.")
                    : T("Не меняли.", "Өзгөртүлгөн жок.", "Not changed.", "Değiştirilmedi.", "O'zgartirilmadi."));
        }
        // 2026-10-19: закупка по накладной — окно закупки; карточка завершается, когда проведены все строки.
        async Task<string> OpenPurchase()
        {
            if (finished || editorOpen)
                return "";
            editorOpen = true;
            try
            {
                if (draft is null)
                {
                    draft = InvoiceEditorWindow.PurchaseDraft.FromSteps(ResolveSteps(), supplierHint.Name, supplierHint.Inn, invoiceTotals);
                    // Штрихкоды теперь правятся в окне закупки — поля карточки больше не действуют.
                    foreach (var (box, _) in barcodeBoxes.Values)
                        box.IsEnabled = false;
                    if (ReferenceEquals(_scanTargets, scanTargets))
                        _scanTargets = null;
                }
                // 2026-10-30 (приёмка 1.17.66): выбор поставщика в реплике переходит в черновик при КАЖДОМ открытии окна (раньше — только при первом).
                supplierPrompt?.Attach(draft);
                await ShowEditorDialogAsync(new InvoiceEditorWindow(draft)).ConfigureAwait(true); // 2026-10-30: окно закупки переживает смену раздела
            }
            finally
            {
                editorOpen = false;
                supplierPrompt?.SyncFromDraft(); // в реплике — тот поставщик, с которым проведётся приход
            }
            if (draft.PostedSummaries.Count > 0)
            {
                result.IsVisible = true;
                result.Text = "✓ " + string.Join("\n✓ ", draft.PostedSummaries)
                    + (draft.Rows.Count > 0
                        ? "\n⚠ " + T($"Не проведено строк: {draft.Rows.Count} — нажмите «Провести закупку…» ещё раз.", $"Өткөрүлбөгөн саптар: {draft.Rows.Count} — «Сатып алууну өткөрүү…» баскычын кайра басыңыз.",
                            $"Lines not posted: {draft.Rows.Count} — press “Post purchase…” again.", $"İşlenmeyen satır: {draft.Rows.Count} — «Alımı işle…»ye tekrar basın.",
                            $"O'tkazilmagan qatorlar: {draft.Rows.Count} — «Xaridni o'tkazish…»ni yana bosing.")
                        : "");
            }
            // 2026-10-25 (ТЗ «оприходование в фоне», Q3): часть закупки ещё проводится в фоне — карточка не ждёт сервер.
            if (draft.PendingJobs.Count > 0)
            {
                result.IsVisible = true;
                result.Text = (draft.PostedSummaries.Count > 0 ? result.Text + "\n" : "") + "⏳ "
                    + T("Операция выполняется в фоне — можно продолжать работу", "Операция фондо аткарылууда — иштей берсеңиз болот",
                        "The operation is running in the background — you can keep working", "İşlem arka planda yürütülüyor — çalışmaya devam edebilirsiniz",
                        "Amal fonda bajarilmoqda — ishni davom ettirishingiz mumkin");
            }
            if (!draft.Completed)
            {
                // 2026-10-19, ревью: пока окно было открыто, пришла новая карточка — эта закрывается и после частичного проведения.
                if (superseded)
                    Cancel();
                return draft.PostedSummaries.Count > 0 ? result.Text ?? "" : "";
            }
            finished = true;
            if (_pendingProductCancel == Cancel)
            {
                _pendingProductApply = null;
                _pendingProductCancel = null;
            }
            buttons.IsVisible = false;
            // Прочие шаги карточки (описание, категория, штрихкод set_barcode…) — как раньше, после закупки.
            // 2026-10-25 (Q3): закупка может проводиться в фоне — тогда шаги выполняются ПОСЛЕ её завершения (draft.OnSettled, UI-поток),
            // один раз (othersRan), и только если все части закупки оприходованы полностью (иначе штрихкод и пр. не ставим: приход к
            // товару мог не пройти). Прежний порядок (быстрый ответ) не меняется.
            var othersRan = false;
            async Task AfterPurchaseAsync(bool allDone, bool rewriteText)
            {
                if (othersRan)
                    return;
                othersRan = true;
                if (rewriteText)
                {
                    result.IsVisible = true;
                    result.Text = draft.PostedSummaries.Count > 0 ? "✓ " + string.Join("\n✓ ", draft.PostedSummaries) : "";
                }
                if (!allDone)
                {
                    result.IsVisible = true;
                    result.Text += (result.Text is { Length: > 0 } ? "\n" : "") + "⚠ "
                        + T("Закупка проведена не полностью — откройте «Приходы в фоне». Штрихкоды и прочие шаги карточки не выполнены.",
                            "Сатып алуу толук өткөрүлгөн жок — «Фондогу кирүүлөрдү» ачыңыз. Штрихкоддор жана карточканын башка кадамдары аткарылган жок.",
                            "The purchase was not fully posted — open “Background receipts”. Barcodes and other card steps were not applied.",
                            "Alım tam işlenmedi — «Arka plandaki girişler»i açın. Barkodlar ve kartın diğer adımları uygulanmadı.",
                            "Xarid to'liq o'tkazilmadi — «Fondagi kirimlar»ni oching. Shtrixkodlar va kartaning boshqa qadamlari bajarilmadi.");
                }
                else
                {
                    var others = ResolveSteps().Where(st => !ProductActionPlan.IsPurchaseStep(st)).ToList();
                    if (others.Count > 0)
                    {
                        var done = await ProductActionPlan.ExecuteAllAsync(others, T("Нур Советник", "Нур Кеңешчи", "Nur Advisor", "Nur Danışman", "Nur Maslahatchi"), CancellationToken.None,
                            proposed).ConfigureAwait(true);
                        result.Text += "\n" + done;
                    }
                }
                PosLogger.Log($"ИИ-советник: накладная проведена закупкой ({steps.Count(ProductActionPlan.IsPurchaseStep)} строк).", "INFO");
                SaveCurrentChat();
                ScrollToEnd();
            }
            if (draft.PendingJobs.Count > 0)
            {
                draft.OnSettled = allDone => AfterPurchaseAsync(allDone, rewriteText: true);
                SaveCurrentChat();
                ScrollToEnd();
                return result.Text ?? "";
            }
            await AfterPurchaseAsync(!draft.BackgroundFailed, rewriteText: false).ConfigureAwait(true);
            return result.Text ?? "";
        }
        async Task<string> Apply()
        {
            if (finished)
                return "";
            // 2026-10-21 (И10): есть предупреждения — сначала второе подтверждение в карточке (голосовое «да» его не заменяет).
            // 2026-10-24: один и тот же штрихкод у двух строк — сервер второй товар с ним не примет; пока повтор не исправлен, не проводим.
            // Нехватка штрихкодов (пустые поля) проведению не мешает.
            if (dupRows.Count > 0)
            {
                var lines = string.Join(", ", dupRows.OrderBy(n => n).Select(n => (n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                dupShown = true;
                result.IsVisible = true;
                result.Text = T($"⚠ Нельзя провести: один и тот же штрихкод у строк {lines}. Исправьте и нажмите ещё раз.",
                    $"⚠ Өткөрүүгө болбойт: бир эле штрихкод {lines}-саптарда. Оңдоп, кайра басыңыз.",
                    $"⚠ Cannot post: the same barcode is on lines {lines}. Fix it and press again.",
                    $"⚠ İşlenemez: aynı barkod {lines}. satırlarda. Düzeltip tekrar basın.",
                    $"⚠ O'tkazib bo'lmaydi: bir xil shtrix-kod {lines}-qatorlarda. Tuzatib, yana bosing.");
                return result.Text;
            }
            if (!confirmedTwice)
            {
                ShowSecondConfirm(card, warnings, () => { confirmedTwice = true; _ = Apply(); }, buttons);
                return T("Есть предупреждения — подтвердите ещё раз кнопкой на экране.", "Эскертүүлөр бар — экрандагы баскыч менен дагы бир жолу ырастаңыз.",
                    "There are warnings — confirm once more with the button on screen.", "Uyarılar var — ekrandaki düğmeyle bir kez daha onaylayın.",
                    "Ogohlantirishlar bor — ekrandagi tugma bilan yana bir bor tasdiqlang.");
            }
            if (purchase)
                return await OpenPurchase().ConfigureAwait(true);
            finished = true;
            if (_pendingProductCancel == Cancel)
            {
                _pendingProductApply = null;
                _pendingProductCancel = null;
            }
            if (ReferenceEquals(_scanTargets, scanTargets))
                _scanTargets = null;
            buttons.IsVisible = false;
            result.IsVisible = true;
            result.Text = T("Выполняю…", "Аткарып жатам…", "Working…", "Uygulanıyor…", "Bajarilmoqda…");
            // 2026-10-16: с временем карточки — такой же план, уже выполненный после её появления (в боте или новой карточкой), повторно
            // не выполнится; кнопка и голосовое «да» (_pendingProductApply) идут сюда же.
            var done = await ProductActionPlan.ExecuteAllAsync(ResolveSteps(), T("Нур Советник", "Нур Кеңешчи", "Nur Advisor", "Nur Danışman", "Nur Maslahatchi"), CancellationToken.None,
                proposed).ConfigureAwait(true);
            foreach (var (box, _) in barcodeBoxes.Values)
                box.IsEnabled = false;
            result.Text = done;
            PosLogger.Log($"ИИ-советник: действия с товарами выполнены по подтверждению владельца ({steps.Count}).", "INFO");
            SaveCurrentChat();
            ScrollToEnd();
            return done;
        }
        cancel.Click += (_, _) => Cancel();
        apply.Click += async (_, _) => await Apply().ConfigureAwait(true);
        _pendingProductSupersede?.Invoke();
        _pendingProductSupersede = () =>
        {
            superseded = true;
            Cancel();
        };
        _pendingProductApply = Apply;
        _pendingProductCancel = Cancel;
        _messages.Children.Add(card);
        ScrollToEnd();
    }

    /// <summary>2026-10-06, владелец (снимок звонка: «Голосом прямо в звонке поменять информацию не могу»): «дай возможность голосом
    /// менять информацию, открывать товар на складе, добавлять и удалять информацию». Реплика владельца в звонке про товар —
    /// параллельно со звонком тот же разбор, что в чате (сведения из интернета, строки «ТОВАР:»): «открыть» — сразу, изменения —
    /// карточкой; «да, выполни» голосом или кнопка — выполнить. Советнику в звонке программа сообщает, что сделано.</summary>
    private async Task LiveProductPassAsync(string utterance, GeminiLiveVoice live)
    {
        try
        {
            var (sections, _) = await GetSummaryAsync().ConfigureAwait(true);
            var access = CurrentAccess();
            // 2026-10-21 (О7): разбор фразы звонка — БЕЗ истории текстового чата (звонок 07.10 искал фото товаров из прошлых просьб).
            var res = await TelegramAiChat.AskOwnerAppAsync(
                utterance + "\n(Сказано голосом во время звонка. Ответь одной-двумя фразами и строками «ТОВАР:», если нужны изменения или открыть товар. "
                + "Действия — только для товаров, названных в ЭТОЙ фразе.)",
                sections, access, LifetimeToken, useHistory: false).ConfigureAwait(true);
            var (answer, error, webSources) = (res.Answer, res.Error, res.Sources);
            if (_live != live || answer is not { Length: > 0 })
            {
                if (error != null)
                    PosLogger.Log($"ИИ-советник: в звонке товар не разобран ({error}).", "WARNING");
                return;
            }
            // 2026-10-19, ревью: в звонке фото нет — подсказку поставщика меняет только новая строка «ПОСТАВЩИК».
            if (ProductActionPlan.SupplierHint(answer) is var liveHint && (liveHint.Name is not null || liveHint.Inn is not null))
                _invoiceSupplierHint = liveHint;
            var (text, steps, _) = ProductActionPlan.Extract(answer, utterance);
            // 2026-10-21 (О7): фото — только для товаров из этой фразы; без права «Склад» изменений нет (О4).
            var named = ProductsByWords(utterance, 6).Select(p => p.Id).ToHashSet();
            steps = steps.Where(st => st.Op != "photo" || named.Contains(st.Product.Id)).ToList();
            if (!access.Products)
                steps = steps.Where(st => st.Op is "open" or "open_section").ToList();
            var changes = steps.Where(st => st.Op is not ("open" or "open_section")).ToList();
            // Просили сведения (без изменений) — ответ текстом на экране, со ссылками.
            if (changes.Count == 0 && text.Length > 0 && ProductInfoResearch.LooksLikeInfoRequest(utterance))
            {
                AddBubble(TelegramAiChat.ToPlainText(text), fromOwner: false);
                if (webSources.Count > 0)
                    AddWebSources(webSources);
            }
            ShowProductSteps(steps, utterance);
            var note = changes.Count > 0
                ? "[Программа] На экране список изменений: " + string.Join("; ", changes.Select(ProductActionPlan.Describe))
                  + ". Коротко скажи владельцу, что подготовлено, и спроси: выполнить? (ответ «да, выполни» или кнопка)."
                : steps.Any(st => st.Op == "open") && HasOpenCommand(utterance)
                    ? "[Программа] Товар открыт на складе: " + string.Join(", ", steps.Where(st => st.Op == "open").Select(st => st.Product.Title)) + ". Скажи об этом одной фразой."
                    : text.Length > 0
                        ? "[Программа] Найдено о товаре: " + (text.Length > 600 ? text[..600] : text) + ". Коротко перескажи главное."
                        : "";
            if (note.Length > 0)
                await live.SendTextAsync(note).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: разбор товара в звонке не удался ({ex.Message}).", "WARNING");
        }
    }

    // Сведения о сотрудниках уже есть у советника в звонке (в инструкции или присланы после начала).
    private bool _liveStaffDelivered;

    private static readonly string[] LiveStaffPeriodWords = { "сегодн", "вчера", "недел", "прошл", "бүгүн", "кечээ", "жума", "өткөн" };

    /// <summary>2026-10-06: сведения о сотрудниках для звонка — не дольше 6 с (не держать начало разговора); не вышло — пусто.</summary>
    private static async Task<string> LiveStaffContextAsync(string question)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var text = await TelegramAiChat.StaffContextForAsync(question, cts.Token).ConfigureAwait(false);
            PosLogger.Log($"ИИ-советник: сотрудники для звонка — {text.Length} симв. за {watch.ElapsedMilliseconds} мс.", "INFO");
            return text;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: сотрудники для звонка не получены ({ex.Message}).", "WARNING");
            return "";
        }
    }

    private static readonly string[] DataWords =
    {
        "продаж", "продал", "выручк", "чек", "остат", "товар", "долг", "должник", "зарплат", "табел", "смен", "прибыл", "топ", "сколько",
        "отчёт", "отчет", "аналит", "наценк", "касс", "клиент", "покупател", "поставщ", "расход", "доход", "закончил", "заканчива", "срок",
        "сатуу", "саттык", "калдык", "карыз", "айлык", "эмгек", "канча", "пайда", "киреше", "кардар", "сотрудник", "кызматкер", "сумм",
        "sales", "revenue", "stock", "debt", "salary", "how much", "satış", "borç", "sotuv", "qarz",
    };

    /// <summary>Вопрос с цифрами магазина (продажи, чеки, остатки, долги, сотрудники…) или «найди Mars» — ответ таблицей в чат.</summary>
    private static bool LooksLikeDataQuestion(string spoken)
    {
        var t = spoken.ToLowerInvariant();
        if (DataWords.Any(t.Contains) || OwnerSalesData.LooksLikeQuestion(spoken))
            return true;
        var find = new[] { "найд", "найт", "поищ", "тап", "таап", "покаж", "көрсөт", "find", "show" }.Any(t.Contains);
        return find && ProductsByWords(spoken, 1).Count > 0;
    }

    /// <summary>2026-10-07, владелец: «баг — ИИ должен сам собирать информацию и выводить в чате таблицей, и на звонок добавь».
    /// В звонке голосом таблицу не покажешь: параллельно программа спрашивает ИИ тем же путём, что и чат (сводка, сотрудники,
    /// чеки за период с сервера, карточки товаров), просит ответ таблицей и показывает его в чате; советнику — итог для голоса.</summary>
    private async Task LiveTablePassAsync(string utterance, GeminiLiveVoice live, bool speak)
    {
        var bubble = AddBubble(T("◐ Собираю данные для таблицы…", "◐ Таблица үчүн маалымат чогултуп жатам…", "◐ Collecting data for a table…",
            "◐ Tablo için veriler toplanıyor…", "◐ Jadval uchun ma'lumot yig'ilmoqda…"), fromOwner: false);
        try
        {
            var (sections, names) = await GetSummaryAsync().ConfigureAwait(true);
            var res = await TelegramAiChat.AskOwnerAppAsync(
                utterance + "\n(Вопрос задан голосом во время звонка. Ответь ТАБЛИЦЕЙ Markdown по данным магазина: перед таблицей одна короткая "
                + "строка, в таблице — заголовок, строки и строка «Итого», где уместно. Без вступлений и советов. Данных нет — одной строкой честно.)",
                sections, CurrentAccess(), LifetimeToken, useHistory: false, forceFull: ForceSections(utterance)).ConfigureAwait(true);
            var answer = res.Answer;
            if (answer is not { Length: > 0 })
            {
                // 2026-10-21 (О8): понятный текст вместо «The operation was canceled.».
                bubble.Text = FriendlyErrorText(res.Error);
                return;
            }
            var (clean, _, _) = ProductActionPlan.Extract(answer, utterance);
            var text = OwnerAiContext.RevealCodes(TelegramAiChat.ToPlainText(clean), names);
            bubble.Text = text;
            ScrollToEnd();
            PosLogger.Log($"ИИ-советник: таблица к вопросу в звонке ({text.Length} симв.).", "INFO");
            if (_live == live && live.IsOpen)
                await live.SendTextAsync("[Программа] На экране таблица по вопросу владельца. Данные таблицы: "
                                         + (text.Length > 900 ? text[..900] + "…" : text)
                                         + (speak ? " Коротко скажи голосом главный итог (одна-две фразы), таблицу не зачитывай." : " Просто запомни."),
                    respond: speak).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            bubble.Text = FriendlyErrorText(ex is OperationCanceledException ? "timeout" : "internal");
            PosLogger.Log($"ИИ-советник: таблица в звонке не собрана ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>Сведения о сотрудниках пришли после начала звонка — в память разговора, без ответа вслух.</summary>
    private async Task SendStaffWhenReadyAsync(Task<string> staffTask, GeminiLiveVoice live)
    {
        var staff = await staffTask.ConfigureAwait(true);
        for (var i = 0; i < 60 && _live == live && !live.IsOpen; i++)
            await Task.Delay(250).ConfigureAwait(true);
        if (_live != live || staff.Length == 0 || !live.IsOpen)
            return;
        await live.SendTextAsync("[Программа] Справка о сотрудниках для ответов на вопросы владельца (сейчас вслух ничего не говори, просто запомни): " + staff,
            respond: false).ConfigureAwait(true);
        _liveStaffDelivered = true;
        PosLogger.Log("ИИ-советник: сотрудники переданы в звонок после его начала.", "INFO");
    }

    /// <summary>2026-10-06: сводка магазина собралась после начала звонка — в память разговора, без ответа вслух.</summary>
    private async Task SendSummaryWhenReadyAsync(Task<(IReadOnlyList<AiSection> Sections, IReadOnlyDictionary<int, string> Names)> summaryTask, GeminiLiveVoice live)
    {
        try
        {
            var (sections, names) = await summaryTask.ConfigureAwait(true);
            // 2026-10-21 (О1): тот же построитель по важности, что у чата.
            var summary = AiContextBudget.Compose(sections, 24000).Text;
            for (var i = 0; i < 60 && _live == live && !live.IsOpen; i++)
                await Task.Delay(250).ConfigureAwait(true);
            if (_live != live || summary.Length == 0 || !live.IsOpen)
                return;
            _liveDebtors = names;
            await live.SendTextAsync("[Программа] Данные магазина для ответов владельцу (сейчас вслух ничего не говори, просто запомни; "
                                     + "раньше этих данных у тебя не было — теперь отвечай по ним): " + summary, respond: false).ConfigureAwait(true);
            PosLogger.Log($"ИИ-советник: сводка ({summary.Length} симв.) передана в звонок после его начала.", "INFO");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: сводка для звонка не собрана ({ex.Message}).", "WARNING");
        }
    }

    private async Task LiveStaffPassAsync(string utterance, GeminiLiveVoice live)
    {
        var staff = await LiveStaffContextAsync(utterance).ConfigureAwait(true);
        if (_live != live || staff.Length == 0)
            return;
        await live.SendTextAsync("[Программа] Данные о сотрудниках по вопросу владельца: " + staff + " Коротко ответь владельцу по этим данным.").ConfigureAwait(true);
        _liveStaffDelivered = true;
    }

    private void AddBotChangeCard(Dictionary<string, bool> change)
    {
        var lines = string.Join("\n", change.Select(c =>
            (c.Value ? T("Включить: ", "Күйгүзүү: ", "Turn on: ", "Aç: ", "Yoqish: ") : T("Выключить: ", "Өчүрүү: ", "Turn off: ", "Kapat: ", "O'chirish: "))
            + BotSwitches.First(s => s.Key == c.Key).Title));
        var title = new TextBlock { Text = T("Изменить настройки бота?", "Боттун жөндөөлөрүн өзгөртөлүбү?", "Change the bot settings?", "Bot ayarları değiştirilsin mi?", "Bot sozlamalarini o'zgartiraymi?"), FontWeight = FontWeight.Bold, FontSize = 14.5 };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        var body = new TextBlock { Text = lines, FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        Use(body, TextBlock.ForegroundProperty, "BrushText");
        var apply = UiKit.Primary(this, T("Применить", "Колдонуу", "Apply", "Uygula", "Qo'llash"));
        apply.Height = 38;
        var cancel = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Height = 38;
        var result = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        Use(result, TextBlock.ForegroundProperty, "BrushTextSoft");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { apply, cancel } };
        var card = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10), MaxWidth = 680, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Spacing = 8, Children = { title, body, buttons, result } },
        };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushAccentStrong");
        cancel.Click += (_, _) =>
        {
            buttons.IsVisible = false;
            result.IsVisible = true;
            result.Text = T("Не меняли.", "Өзгөртүлгөн жок.", "Not changed.", "Değiştirilmedi.", "O'zgartirilmadi.");
        };
        apply.Click += async (_, _) =>
        {
            buttons.IsVisible = false;
            result.IsVisible = true;
            result.Text = T("Применяю…", "Колдонуп жатам…", "Applying…", "Uygulanıyor…", "Qo'llanmoqda…");
            try
            {
                var api = App.AppHost?.Services.GetService<ServerTelegramBotApi>() ?? throw new InvalidOperationException("API бота недоступно");
                var settings = await api.PatchSettingsAsync(change.ToDictionary(c => c.Key, c => (object?)c.Value)).ConfigureAwait(true);
                _botCache = settings;
                _botCacheAt = DateTime.UtcNow;
                result.Text = T("✓ Готово — бот уже работает с новыми настройками.", "✓ Даяр — бот жаңы жөндөөлөр менен иштеп жатат.", "✓ Done — the bot already uses the new settings.",
                    "✓ Tamam — bot yeni ayarlarla çalışıyor.", "✓ Tayyor — bot yangi sozlamalar bilan ishlayapti.");
                PosLogger.Log("ИИ-советник: настройки бота изменены по подтверждению владельца: "
                    + string.Join(", ", change.Select(c => $"{c.Key}={c.Value}")) + ".", "INFO");
            }
            catch (Exception ex)
            {
                buttons.IsVisible = true;
                result.Text = T("Не получилось: ", "Болбой калды: ", "Failed: ", "Olmadı: ", "Bo'lmadi: ") + ServerTelegramBotApi.Describe(ex);
                PosLogger.Log($"ИИ-советник: настройки бота не изменены ({ex.Message}).", "WARNING");
            }
        };
        _messages.Children.Add(card);
        ScrollToEnd();
    }

}
