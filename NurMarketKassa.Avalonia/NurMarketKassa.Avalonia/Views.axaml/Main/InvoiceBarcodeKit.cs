using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-20 (владелец: «при создании накладных и штрихкоды тоже добавь», как в мобилке — invoice_bind.dart / barcode_bind.dart): общие кусочки штрихкодов в накладных —
/// «Приём товара» (Закупки), «Закупка по накладной» (ИИ/фото и XML), «Возврат поставщику». Логика — NurMarketKassa.Services.InvoiceBarcodeRules / InvoiceBarcodeBinder
/// (без окон, с тестами); здесь только вид: ячейка «штрихкод + Сканировать + +N», окно «Доп. штрихкоды», вопрос «код уже у товара …».</summary>
internal enum BarcodeConflictChoice { Cancel, Keep, UseOwner }

/// <summary>Итог шага «код у строки»: что окну сделать со строкой.</summary>
internal enum BarcodeCommitKind
{
    /// <summary>Код принят: поставить его строке (у товара склада он уже записан на сервер).</summary>
    Set,
    /// <summary>Поле очищено: у строки нового товара кода нет.</summary>
    Cleared,
    /// <summary>Код уже у товара каталога, кассир выбрал «Выбрать его»: строка становится этим товаром.</summary>
    UseOwner,
    /// <summary>Код не принят (неверный формат, дубль, сервер не записал, «Оставить как есть», «Отмена»): строке вернуть прежний код.</summary>
    Rejected,
}

internal sealed record BarcodeCommit(BarcodeCommitKind Kind, string Code = "", string Message = "", CatalogProductTileVm? Owner = null, bool Warning = true);

/// <summary>Строка накладной для проверки кода: чей код, какой товар (null — новый), все строки накладной.</summary>
internal sealed record BarcodeRowContext(object Key, string? ProductId, bool IsNew, string RowName, Func<IEnumerable<InvoiceCodeRow>> Rows);

internal static class InvoiceBarcodeKit
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Подпись кнопки «Сканировать» в ячейке (полное название — в подсказке и имени для чтения с экрана).</summary>
    public static string ScanShort => T("Сканировать", "Сканерлөө", "Scan", "Tara", "Skanerlash");
    public static string ScanFull => T("Отсканировать штрихкод", "Штрихкодду сканерлөө", "Scan a barcode", "Barkodu tara", "Shtrix-kodni skanerlash");
    public static string ExtrasFull => T("Доп. штрихкоды", "Кошумча штрихкоддор", "Extra barcodes", "Ek barkodlar", "Qo'shimcha shtrix-kodlar");

    /// <summary>Единый шаг «код у строки накладной» (ввод руками, скан в поле, кнопка «Сканировать»): формат (4–14 цифр) → дубль внутри накладной → владелец в каталоге
    /// (база кассы, затем сервер) → вопрос «Выбрать его / Оставить / Отмена» → у товара склада запись кода на сервер. Окно применяет результат к строке.</summary>
    public static async Task<BarcodeCommit> CommitAsync(Window host, BarcodeRowContext row, string? typed)
    {
        var code = InvoiceBarcodeRules.Clean(typed);
        if (code.Length == 0)
            return new BarcodeCommit(BarcodeCommitKind.Cleared);
        var rows = row.Rows().ToList();
        // Дешёвые проверки без сети: формат и дубль в этой же накладной (код ещё не на сервере — каталог его не знает).
        var local = InvoiceBarcodeRules.Check(code, row.ProductId, row.Key, rows, null);
        if (local.Verdict is InvoiceCodeVerdict.BadFormat or InvoiceCodeVerdict.DuplicateInInvoice)
            return new BarcodeCommit(BarcodeCommitKind.Rejected, Message: InvoiceBarcodeRules.VerdictText(local, code));
        var found = await InvoiceBarcodeBinder.FindOwnerAsync(code).ConfigureAwait(true);
        var check = InvoiceBarcodeRules.Check(code, row.ProductId, row.Key, rows, found.Owner);
        switch (check.Verdict)
        {
            case InvoiceCodeVerdict.AlreadyOwn:
                return new BarcodeCommit(BarcodeCommitKind.Set, code, Warning: false);
            case InvoiceCodeVerdict.OwnedByOther when check.Owner is { } owner:
                var dlg = new BarcodeConflictDialog(code, owner.Title);
                var choice = await dlg.ShowDialog<BarcodeConflictChoice>(host).ConfigureAwait(true);
                return choice switch
                {
                    BarcodeConflictChoice.UseOwner => new BarcodeCommit(BarcodeCommitKind.UseOwner, code, Owner: owner, Warning: false),
                    BarcodeConflictChoice.Keep => new BarcodeCommit(BarcodeCommitKind.Rejected, "", T($"Код {code} оставлен у товара «{owner.Title}», у этой строки кода нет.", $"{code} коду «{owner.Title}» товарында калды, бул сапта код жок.",
                        $"Code {code} stays with “{owner.Title}”; this line has no code.", $"{code} kodu «{owner.Title}» ürününde kaldı, bu satırda kod yok.", $"{code} kodi «{owner.Title}» mahsulotida qoldi, bu qatorda kod yo'q."), Warning: false),
                    _ => new BarcodeCommit(BarcodeCommitKind.Rejected, "", Warning: false),
                };
        }
        // Код свободен. У товара склада (строка уже привязана к товару) — сразу на сервер: основной, если у товара кода нет, иначе дополнительный.
        if (!row.IsNew && !string.IsNullOrWhiteSpace(row.ProductId))
        {
            var saved = await InvoiceBarcodeBinder.BindAsync(row.ProductId!, code).ConfigureAwait(true);
            if (!saved.Ok && saved.Duplicate is { } dup)
                return await ServerDuplicateAsync(host, code, dup).ConfigureAwait(true); // 2026-11-21: сервер сам сказал, чей код (409 barcode_duplicate) — каталог повторно не ищем
            if (!saved.Ok)
                return new BarcodeCommit(BarcodeCommitKind.Rejected, Message: saved.Message);
            return new BarcodeCommit(BarcodeCommitKind.Set, code,
                saved.Plan == InvoiceBindPlan.Alt
                    ? T($"Код {code} добавлен товару «{row.RowName}» дополнительным.", $"{code} коду «{row.RowName}» товарына кошумча катары кошулду.", $"Code {code} added to “{row.RowName}” as an extra barcode.",
                        $"{code} kodu «{row.RowName}» ürününe ek barkod olarak eklendi.", $"{code} kodi «{row.RowName}» mahsulotiga qo'shimcha sifatida qo'shildi.")
                    : T($"Код {code} записан товару «{row.RowName}».", $"{code} коду «{row.RowName}» товарына жазылды.", $"Code {code} saved on “{row.RowName}”.", $"{code} kodu «{row.RowName}» ürününe yazıldı.", $"{code} kodi «{row.RowName}» mahsulotiga yozildi."),
                Warning: false);
        }
        return new BarcodeCommit(BarcodeCommitKind.Set, code, Warning: false);
    }

    /// <summary>2026-11-21 (ответ Умара): сервер ответил «код уже у товара …» (409 barcode_duplicate: product_id, name) — тот же вопрос «выбрать его / оставить / отмена», что и для владельца из каталога.
    /// «Выбрать» нужна плитка товара (product_id из ответа ищется в памяти кассы по id, не по коду); товара в каталоге нет — просто сообщение с названием от сервера.</summary>
    private static async Task<BarcodeCommit> ServerDuplicateAsync(Window host, string code, ServerBarcodeOwner dup)
    {
        var title = string.IsNullOrWhiteSpace(dup.Name) ? code : dup.Name.Trim();
        var tile = string.IsNullOrWhiteSpace(dup.ProductId) ? null : ProductActions.Find(dup.ProductId);
        if (tile is null)
            return new BarcodeCommit(BarcodeCommitKind.Rejected, Message: ServerBarcodeErrors.DuplicateText(dup));
        var choice = await new BarcodeConflictDialog(code, title).ShowDialog<BarcodeConflictChoice>(host).ConfigureAwait(true);
        return choice switch
        {
            BarcodeConflictChoice.UseOwner => new BarcodeCommit(BarcodeCommitKind.UseOwner, code, Owner: tile, Warning: false),
            BarcodeConflictChoice.Keep => new BarcodeCommit(BarcodeCommitKind.Rejected, "", T($"Код {code} оставлен у товара «{title}», у этой строки кода нет.", $"{code} коду «{title}» товарында калды, бул сапта код жок.",
                $"Code {code} stays with “{title}”; this line has no code.", $"{code} kodu «{title}» ürününde kaldı, bu satırda kod yok.", $"{code} kodi «{title}» mahsulotida qoldi, bu qatorda kod yo'q."), Warning: false),
            _ => new BarcodeCommit(BarcodeCommitKind.Rejected, "", Warning: false),
        };
    }

    /// <summary>Окно «Доп. штрихкоды» строки: список (название варианта, штук в упаковке), добавить/удалить. <paramref name="validate"/> — проверка нового кода (пусто — годится).
    /// Результат null — «Отмена»; иначе итоговый список.</summary>
    public static Task<List<AlternateBarcodeVariant>?> EditExtrasAsync(Window host, string productName, string mainCode, IReadOnlyList<AlternateBarcodeVariant> current,
        Func<string, Task<string>> validate) =>
        new BarcodeExtrasDialog(productName, mainCode, current, validate).ShowDialog<List<AlternateBarcodeVariant>?>(host);

    /// <summary>Итог «+N» строки: <see cref="List"/> — новый список доп. штрихкодов (null — ничего не менялось/не удалось), <see cref="Message"/> — что сказать кассиру.</summary>
    internal sealed record ExtrasOutcome(List<AlternateBarcodeVariant>? List, string Message, bool Warning);

    /// <summary>Весь шаг «+N»: у товара склада список читается с сервера и после «Готово» сразу пишется на сервер; у НОВОГО товара список остаётся в строке (уйдёт в создание).
    /// Проверка нового кода: формат, дубль в накладной, владелец в каталоге (база кассы, затем сервер). null — «Отмена».</summary>
    public static async Task<ExtrasOutcome?> EditExtrasFlowAsync(Window host, string name, string mainCode, string? productId, bool isNew, IReadOnlyList<AlternateBarcodeVariant> newList,
        object key, Func<IEnumerable<InvoiceCodeRow>> rows)
    {
        var existing = !isNew && !string.IsNullOrWhiteSpace(productId);
        List<AlternateBarcodeVariant> current;
        var main = mainCode;
        string? cardVersion = null;
        if (existing)
        {
            var card = await InvoiceBarcodeBinder.ReadCardFullAsync(productId!).ConfigureAwait(true);
            cardVersion = card?.Version; // 2026-11-21: версия карточки — If-Match при записи одного кода
            var tile = ProductActions.Find(productId);
            if (card is null && tile is null)
                return new ExtrasOutcome(null, T("Карточка товара не прочиталась с сервера — доп. штрихкоды сейчас не открыть.", "Товардын карточкасы серверден окулган жок — кошумча штрихкоддорду азыр ачууга болбойт.",
                    "The product card could not be read from the server — extra barcodes cannot be opened now.", "Ürün kartı sunucudan okunamadı — ek barkodlar şimdi açılamıyor.", "Mahsulot kartochkasi serverdan o'qilmadi — qo'shimcha shtrix-kodlarni hozir ochib bo'lmaydi."), true);
            current = card?.Alts ?? tile?.AlternateBarcodeVariants?.ToList() ?? new List<AlternateBarcodeVariant>();
            if (card is { } c && c.Barcode.Length > 0)
                main = c.Barcode;
        }
        else
            current = newList.ToList();
        async Task<string> Validate(string code)
        {
            var all = rows().ToList();
            var pid = isNew ? null : productId;
            var quick = InvoiceBarcodeRules.Check(code, pid, key, all, null);
            if (quick.Verdict is InvoiceCodeVerdict.BadFormat or InvoiceCodeVerdict.DuplicateInInvoice)
                return InvoiceBarcodeRules.VerdictText(quick, code);
            var found = await InvoiceBarcodeBinder.FindOwnerAsync(code).ConfigureAwait(true);
            var check = InvoiceBarcodeRules.Check(code, pid, key, all, found.Owner);
            return check.Verdict == InvoiceCodeVerdict.OwnedByOther ? InvoiceBarcodeRules.VerdictText(check, code) : "";
        }
        var result = await EditExtrasAsync(host, name, main, current, Validate).ConfigureAwait(true);
        if (result is null)
            return null;
        if (existing)
        {
            var saved = await InvoiceBarcodeBinder.SaveAlternatesAsync(productId!, result, default, current, cardVersion).ConfigureAwait(true); // 2026-11-21: добавленные — POST, убранные — DELETE по одному
            return saved.Ok
                ? new ExtrasOutcome(result, T($"Доп. штрихкоды товара «{name}» сохранены ({result.Count}).", $"«{name}» товарынын кошумча штрихкоддору сакталды ({result.Count}).", $"Extra barcodes of “{name}” saved ({result.Count}).",
                    $"«{name}» ürününün ek barkodları kaydedildi ({result.Count}).", $"«{name}» mahsulotining qo'shimcha shtrix-kodlari saqlandi ({result.Count})."), false)
                : new ExtrasOutcome(null, saved.Message, true);
        }
        return new ExtrasOutcome(result, T($"Доп. штрихкоды строки: {result.Count}. Создадутся вместе с товаром при оприходовании.", $"Саптын кошумча штрихкоддору: {result.Count}. Кирешке алганда товар менен бирге түзүлөт.",
            $"Extra barcodes of the line: {result.Count}. They are created with the product when posted.", $"Satırın ek barkodları: {result.Count}. Kaydedilince ürünle birlikte oluşturulur.",
            $"Qatorning qo'shimcha shtrix-kodlari: {result.Count}. Kirim qilinganda mahsulot bilan birga yaratiladi."), false);
    }

    /// <summary>Число «штук в упаковке» из поля: пусто — 0 (обычный второй код); иначе от 0 до предела.</summary>
    public static bool TryQuantity(string? text, out double value)
    {
        value = 0;
        var t = (text ?? "").Trim().Replace(" ", "").Replace(',', '.');
        if (t.Length == 0)
            return true;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v) || v < 0 || v > PackScanRules.MaxPackQuantity)
            return false;
        value = InvoiceBarcodeRules.RoundPackQuantity(v);
        return true;
    }

    internal static void BindBrush(Control c, AvaloniaProperty<IBrush?> property, string key) => c.Bind(property, c.GetResourceObservable(key));
}

/// <summary>Вопрос «Штрихкод уже у товара «…»: Выбрать его / Оставить как есть / Отмена» (как в мобилке — «Выбрать «…»» / «Оставить» / «Отмена»).</summary>
public sealed class BarcodeConflictDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public BarcodeConflictDialog(string code, string ownerTitle)
    {
        Title = T("Штрихкод уже занят", "Штрихкод ээленген", "Barcode is taken", "Barkod kullanımda", "Shtrix-kod band");
        SectionCards.AsModal(this, 640);
        var text = new TextBlock
        {
            Text = T($"Штрихкод {code} уже есть у товара «{ownerTitle}». Один код не может быть у двух товаров.\n\n«Выбрать этот товар» — строка станет им (новый товар не создаётся). «Оставить как есть» — код остаётся у него, у строки кода не будет. «Отмена» — отсканировать другой.",
                $"{code} штрихкоду «{ownerTitle}» товарында мурунтан бар. Бир код эки товарда болушу мүмкүн эмес.\n\n«Ушул товарды тандоо» — сап ошол товар болот (жаңы товар түзүлбөйт). «Калтыруу» — код ошондо калат, сапта код болбойт. «Жокко чыгаруу» — башкасын сканерлөө.",
                $"Barcode {code} already belongs to “{ownerTitle}”. One code cannot be on two products.\n\n“Choose this product” — the line becomes it (no new product). “Keep as is” — the code stays with it, the line has no code. “Cancel” — scan another one.",
                $"{code} barkodu zaten «{ownerTitle}» ürününde var. Bir kod iki üründe olamaz.\n\n«Bu ürünü seç» — satır o ürün olur (yeni ürün oluşmaz). «Olduğu gibi bırak» — kod onda kalır, satırın kodu olmaz. «İptal» — başkasını okutun.",
                $"{code} shtrix-kodi «{ownerTitle}» mahsulotida allaqachon bor. Bitta kod ikki mahsulotda bo'lishi mumkin emas.\n\n«Shu mahsulotni tanlash» — qator shu mahsulot bo'ladi (yangi mahsulot yaratilmaydi). «Shunday qoldirish» — kod unda qoladi, qatorda kod bo'lmaydi. «Bekor qilish» — boshqasini skanerlang."),
            FontSize = 14, TextWrapping = TextWrapping.Wrap,
        };
        InvoiceBarcodeKit.BindBrush(text, TextBlock.ForegroundProperty, "BrushText");
        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(BarcodeConflictChoice.Cancel);
        var keep = SectionCards.Secondary(T("Оставить как есть", "Калтыруу", "Keep as is", "Olduğu gibi bırak", "Shunday qoldirish"));
        keep.Click += (_, _) => Close(BarcodeConflictChoice.Keep);
        var use = SectionCards.Primary(T("Выбрать этот товар", "Ушул товарды тандоо", "Choose this product", "Bu ürünü seç", "Shu mahsulotni tanlash"));
        use.Click += (_, _) => Close(BarcodeConflictChoice.UseOwner);
        Content = SectionCards.Shell(Title, ownerTitle, text, SectionCards.Buttons(cancel, keep, use));
    }
}

/// <summary>Окно «Доп. штрихкоды» строки накладной — то же, что вкладка «Доп. штрих-коды» карточки товара: код, название варианта, «штук в упаковке» N. Поля — сеткой
/// (подпись отдельной строкой над полем, поле без своей ширины), список с прокруткой, ошибка под полями. Код — только цифры 4–14, скан сканером в поле добавляет по Enter.</summary>
public sealed class BarcodeExtrasDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private const string ListColumns = "150,*,120,44";

    private sealed class ItemRow
    {
        public string Code = "";
        public TextBox Name = null!;
        public TextBox Qty = null!;
        public Control Visual = null!;
    }

    private readonly List<ItemRow> _items = new();
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly TextBox _code;
    private readonly TextBox _name;
    private readonly TextBox _qty;
    private readonly TextBlock _error = SectionCards.ErrorText(null!);
    private readonly TextBlock _empty;
    private readonly string _main;
    private readonly Func<string, Task<string>> _validate;
    private bool _busy;

    public BarcodeExtrasDialog(string productName, string mainCode, IReadOnlyList<AlternateBarcodeVariant> current, Func<string, Task<string>> validate)
    {
        _main = mainCode;
        _validate = validate;
        Title = InvoiceBarcodeKit.ExtrasFull;
        SectionCards.AsModal(this, 720);

        _empty = new TextBlock { Text = T("Дополнительных штрихкодов пока нет.", "Кошумча штрихкоддор азырынча жок.", "No extra barcodes yet.", "Henüz ek barkod yok.", "Qo'shimcha shtrix-kodlar hali yo'q."), FontSize = 13, Margin = new Thickness(0, 4) };
        InvoiceBarcodeKit.BindBrush(_empty, TextBlock.ForegroundProperty, "BrushTextSoft");

        TextBox Field(string watermark, string name, bool right = false)
        {
            var box = SectionCards.Field(new TextBox { Watermark = watermark, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center });
            if (right)
                box.HorizontalContentAlignment = HorizontalAlignment.Right;
            Avalonia.Automation.AutomationProperties.SetName(box, name);
            return box;
        }
        TextBlock Caption(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis };
            InvoiceBarcodeKit.BindBrush(t, TextBlock.ForegroundProperty, "BrushTextSoft");
            return t;
        }

        var codeLabel = T("Штрихкод (сканируйте)", "Штрихкод (сканерлеңиз)", "Barcode (scan it)", "Barkod (okutun)", "Shtrix-kod (skanerlang)");
        var nameLabel = T("Название варианта", "Вариантын аталышы", "Variant name", "Varyant adı", "Variant nomi");
        var qtyLabel = T("Штук в упаковке", "Таңгактагы даана", "Pieces per pack", "Paketteki adet", "Qadoqdagi dona");
        _code = Field("4870200221261", codeLabel);
        _code.Tag = "inv-extra-code";
        _name = Field(T("пачка, коробка…", "пачка, куту…", "pack, box…", "paket, kutu…", "pachka, quti…"), nameLabel);
        _qty = Field("1", qtyLabel, right: true);
        _qty.Watermark = T("пусто = обычный", "бош = кадимки", "empty = plain", "boş = normal", "bo'sh = oddiy");
        var add = SectionCards.Secondary(T("Добавить", "Кошуу", "Add", "Ekle", "Qo'shish"));
        add.MinHeight = 44;
        add.VerticalAlignment = VerticalAlignment.Bottom;
        add.Click += async (_, _) => await AddAsync();
        BarcodeInput.Attach(this, tb => ReferenceEquals(tb, _code));
        foreach (var b in new[] { _code, _name, _qty })
            b.KeyDown += async (_, e) =>
            {
                if (e.Key != Key.Enter)
                    return;
                e.Handled = true; // скан сканером заканчивается Enter: код добавляется, а не закрывает окно
                await AddAsync();
            };

        // Строка добавления: сетка — подпись над полем, поле растягивается в своей ячейке (правило «поля не налезают»).
        var addGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,130,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        void Put(Control c, int col, int row)
        {
            Grid.SetColumn(c, col);
            Grid.SetRow(c, row);
            addGrid.Children.Add(c);
        }
        var cap1 = Caption(codeLabel);
        var cap2 = Caption(nameLabel);
        var cap3 = Caption(qtyLabel);
        cap1.Margin = new Thickness(0, 0, 8, 4);
        cap2.Margin = new Thickness(0, 0, 8, 4);
        cap3.Margin = new Thickness(0, 0, 8, 4);
        _code.Margin = new Thickness(0, 0, 8, 0);
        _name.Margin = new Thickness(0, 0, 8, 0);
        _qty.Margin = new Thickness(0, 0, 8, 0);
        Put(cap1, 0, 0);
        Put(cap2, 1, 0);
        Put(cap3, 2, 0);
        Put(_code, 0, 1);
        Put(_name, 1, 1);
        Put(_qty, 2, 1);
        Put(add, 3, 1);

        var hint = new TextBlock
        {
            Text = T("«Штук в упаковке» — сколько штук в этой упаковке: при приходе скан такой упаковки сам поставит это количество («пачка = N шт.»). Пусто — обычный второй штрихкод того же товара.",
                "«Таңгактагы даана» — бул таңгакта канча даана бар: кирүүдө мындай таңгакты сканерлегенде ушунча даана өзү коюлат («пачка = N даана»). Бош — ошол эле товардын кадимки экинчи штрихкоду.",
                "“Pieces per pack” — how many pieces are in this pack: when receiving, scanning such a pack sets that quantity itself (“pack = N pcs”). Empty — an ordinary second barcode of the same product.",
                "«Paketteki adet» — bu pakette kaç adet var: girişte böyle bir paketi okutunca miktar kendiliğinden yazılır («paket = N adet»). Boş — aynı ürünün normal ikinci barkodu.",
                "«Qadoqdagi dona» — bu qadoqda nechta dona bor: kirimda bunday qadoqni skanerlasangiz, miqdor o'zi qo'yiladi («pachka = N dona»). Bo'sh — o'sha mahsulotning oddiy ikkinchi shtrix-kodi."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        };
        InvoiceBarcodeKit.BindBrush(hint, TextBlock.ForegroundProperty, "BrushTextSoft");

        var listBox = new ScrollViewer { Content = _list, MaxHeight = 170, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(listBox);
        body.Children.Add(addGrid);
        body.Children.Add(SectionCards.ErrorBox(this, _error));
        body.Children.Add(hint);

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(null);
        var done = SectionCards.Primary(T("Готово", "Даяр", "Done", "Tamam", "Tayyor"));
        done.Click += async (_, _) => await FinishAsync();
        var subtitle = mainCode.Length > 0 ? $"{productName} · {mainCode}" : productName;
        Content = SectionCards.Shell(Title, subtitle, body, SectionCards.Buttons(cancel, done));
        foreach (var v in current.Where(v => !string.IsNullOrWhiteSpace(v.Barcode)))
            AddRow(v.Barcode.Trim(), v.Name, v.Quantity);
        RefreshList();
        Opened += (_, _) => _code.Focus();
    }

    private void AddRow(string code, string? name, double qty)
    {
        var row = new ItemRow { Code = code };
        row.Name = SectionCards.Field(new TextBox { Text = name ?? "", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        Avalonia.Automation.AutomationProperties.SetName(row.Name, T("Название варианта", "Вариантын аталышы", "Variant name", "Varyant adı", "Variant nomi") + " " + code);
        row.Qty = SectionCards.Field(new TextBox
        {
            Text = qty > 0 ? MoneyText.Qty(qty) : "" /* 2026-11-21: общий форматтер количества (запятая; разбор поля её принимает) */, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        });
        Avalonia.Automation.AutomationProperties.SetName(row.Qty, T("Штук в упаковке", "Таңгактагы даана", "Pieces per pack", "Paketteki adet", "Qadoqdagi dona") + " " + code);
        var codeText = new TextBlock { Text = code, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        InvoiceBarcodeKit.BindBrush(codeText, TextBlock.ForegroundProperty, "BrushText");
        var remove = UiKit.Ghost(this, "✕");
        remove.Classes.Add("danger");
        remove.Width = 44;
        remove.Height = 44;
        remove.Padding = new Thickness(0);
        remove.Focusable = true;
        Avalonia.Automation.AutomationProperties.SetName(remove, T("Удалить штрихкод", "Штрихкодду өчүрүү", "Remove the barcode", "Barkodu sil", "Shtrix-kodni o'chirish") + " " + code);
        remove.Click += (_, _) =>
        {
            _items.Remove(row);
            RefreshList();
        };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(ListColumns) };
        Grid.SetColumn(row.Name, 1);
        Grid.SetColumn(row.Qty, 2);
        Grid.SetColumn(remove, 3);
        g.Children.Add(codeText);
        g.Children.Add(row.Name);
        g.Children.Add(row.Qty);
        g.Children.Add(remove);
        row.Visual = g;
        _items.Add(row);
    }

    private void RefreshList()
    {
        _list.Children.Clear();
        if (_items.Count == 0)
        {
            _list.Children.Add(_empty);
            return;
        }
        // Заголовки колонок — той же сеткой, что и строки (левые края совпадают).
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions(ListColumns) };
        string[] heads = [T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"), T("Название варианта", "Вариантын аталышы", "Variant name", "Varyant adı", "Variant nomi"), T("Штук в упаковке", "Таңгактагы даана", "Pieces per pack", "Paketteki adet", "Qadoqdagi dona")];
        for (var i = 0; i < heads.Length; i++)
        {
            var t = new TextBlock { Text = heads[i], FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = i == 2 ? TextAlignment.Right : TextAlignment.Left };
            InvoiceBarcodeKit.BindBrush(t, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(t, i);
            head.Children.Add(t);
        }
        _list.Children.Add(head);
        foreach (var it in _items)
            _list.Children.Add(it.Visual);
    }

    private void ShowError(string? text, Control? field = null) => SectionCards.ShowError(_error, text, field);

    private async Task AddAsync()
    {
        if (_busy)
            return;
        var code = InvoiceBarcodeRules.Clean(_code.Text);
        if (code.Length == 0)
        {
            ShowError(T("Отсканируйте или впишите штрихкод.", "Штрихкодду сканерлеңиз же жазыңыз.", "Scan or type a barcode.", "Barkodu okutun veya yazın.", "Shtrix-kodni skanerlang yoki yozing."), _code);
            return;
        }
        if (!InvoiceBarcodeRules.IsValidNew(code))
        {
            ShowError(InvoiceBarcodeRules.FormatHint(code), _code);
            return;
        }
        if (InvoiceBarcodeRules.Same(code, _main))
        {
            ShowError(T($"Код {code} — основной штрихкод этого товара, дополнительным его делать не нужно.", $"{code} коду — бул товардын негизги штрихкоду, аны кошумча кылуунун кереги жок.",
                $"Code {code} is the main barcode of this product — it need not be an extra one.", $"{code} kodu bu ürünün ana barkodu — ek yapmaya gerek yok.", $"{code} kodi bu mahsulotning asosiy shtrix-kodi — uni qo'shimcha qilish shart emas."), _code);
            return;
        }
        if (_items.Any(i => InvoiceBarcodeRules.Same(i.Code, code)))
        {
            ShowError(T($"Код {code} уже в этом списке.", $"{code} коду бул тизмеде бар.", $"Code {code} is already in this list.", $"{code} kodu bu listede zaten var.", $"{code} kodi bu ro'yxatda allaqachon bor."), _code);
            return;
        }
        if (!InvoiceBarcodeKit.TryQuantity(_qty.Text, out var qty))
        {
            ShowError(T("«Штук в упаковке» — число от 0 до 100 000 (пусто — обычный код).", "«Таңгактагы даана» — 0дөн 100 000ге чейинки сан (бош — кадимки код).", "“Pieces per pack” is a number from 0 to 100,000 (empty — a plain code).",
                "«Paketteki adet» 0 ile 100.000 arası bir sayı (boş — normal kod).", "«Qadoqdagi dona» — 0 dan 100 000 gacha son (bo'sh — oddiy kod)."), _qty);
            return;
        }
        _busy = true;
        try
        {
            var problem = await _validate(code).ConfigureAwait(true);
            if (problem.Length > 0)
            {
                ShowError(problem, _code);
                return;
            }
        }
        finally
        {
            _busy = false;
        }
        ShowError(null);
        AddRow(code, string.IsNullOrWhiteSpace(_name.Text) ? null : _name.Text.Trim(), qty);
        RefreshList();
        _code.Text = "";
        _name.Text = "";
        _qty.Text = "";
        _code.Focus();
    }

    private async Task FinishAsync()
    {
        // Недобавленный код в поле (сканировали и сразу нажали «Готово») не теряем.
        if (!string.IsNullOrWhiteSpace(_code.Text))
        {
            await AddAsync();
            if (_error.IsVisible)
                return;
        }
        var result = new List<AlternateBarcodeVariant>();
        foreach (var it in _items)
        {
            if (!InvoiceBarcodeKit.TryQuantity(it.Qty.Text, out var qty))
            {
                ShowError(T($"Код {it.Code}: «штук в упаковке» — число от 0 до 100 000.", $"{it.Code} коду: «таңгактагы даана» — 0дөн 100 000ге чейинки сан.", $"Code {it.Code}: “pieces per pack” is a number from 0 to 100,000.",
                    $"{it.Code} kodu: «paketteki adet» 0 ile 100.000 arası bir sayı.", $"{it.Code} kodi: «qadoqdagi dona» — 0 dan 100 000 gacha son."), it.Qty);
                return;
            }
            result.Add(new AlternateBarcodeVariant { Barcode = it.Code, Name = string.IsNullOrWhiteSpace(it.Name.Text) ? null : it.Name.Text.Trim(), Quantity = qty });
        }
        Close(result);
    }
}

/// <summary>Ячейка «Штрихкод» строки накладной: поле (цифры 4–14), «Сканировать» (фокус в поле, следующий скан привяжется к строке) и «+N» (доп. штрихкоды). Кнопки — в WrapPanel под полем,
/// на узком окне переносятся целиком, поле растягивается в своей ячейке.</summary>
internal sealed class InvoiceBarcodeCell
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public const string BoxTag = "inv-barcode";

    public TextBox Box { get; }
    public Button Scan { get; }
    public Button Extras { get; }
    /// <summary>Готовая ячейка (поле, под ним кнопки) — только в режиме <c>vertical</c> (колонка таблицы); иначе null: поле и кнопки (<see cref="Box"/>, <see cref="Scan"/>, <see cref="Extras"/>) окно кладёт в свою панель.</summary>
    public Control? Root { get; }
    private readonly string _watermark;

    public InvoiceBarcodeCell(Control host, string rowName, bool vertical, double boxHeight = 40, double buttonHeight = 32, TextBox? existingBox = null)
    {
        _watermark = existingBox?.Watermark ?? T("штрихкод", "штрихкод", "barcode", "barkod", "shtrix-kod");
        Box = existingBox ?? new TextBox
        {
            Watermark = _watermark, MinHeight = boxHeight, Height = boxHeight, FontSize = 14, MaxLength = InvoiceBarcodeRules.MaxLength, Tag = BoxTag,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center,
        };
        Avalonia.Automation.AutomationProperties.SetName(Box, T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod") + " " + rowName);
        Scan = new Button { Content = InvoiceBarcodeKit.ScanShort, MinHeight = buttonHeight, Padding = new Thickness(10, 0), Margin = vertical ? new Thickness(0, 4, 6, 0) : new Thickness(0, 0, 6, 4), FontSize = 12, Focusable = true, Tag = "inv-scan" };
        Scan.Classes.Add("sec-outline");
        Scan.Height = buttonHeight;
        ToolTip.SetTip(Scan, T("Отсканировать штрихкод товара: курсор встаёт в поле штрихкода, следующий скан запишется этой строке. Код уже у другого товара — спросим, что делать.",
            "Товардын штрихкодун сканерлөө: курсор штрихкод талаасына турат, кийинки скан ушул сапка жазылат. Код башка товарда болсо — эмне кылууну сурайбыз.",
            "Scan the product barcode: the cursor goes to the barcode field, the next scan is written to this line. If the code belongs to another product, we ask what to do.",
            "Ürün barkodunu okutun: imleç barkod alanına geçer, sonraki okutma bu satıra yazılır. Kod başka üründeyse ne yapılacağını sorarız.",
            "Mahsulot shtrix-kodini skanerlang: kursor shtrix-kod maydoniga o'tadi, keyingi skan shu qatorga yoziladi. Kod boshqa mahsulotda bo'lsa — nima qilishni so'raymiz."));
        Avalonia.Automation.AutomationProperties.SetName(Scan, InvoiceBarcodeKit.ScanFull + " " + rowName);
        Extras = new Button { Content = "+", MinHeight = buttonHeight, MinWidth = 44, Padding = new Thickness(10, 0), Margin = vertical ? new Thickness(0, 4, 0, 0) : new Thickness(0, 0, 6, 4), FontSize = 12, Focusable = true, Tag = "inv-extras" };
        Extras.Classes.Add("sec-outline");
        Extras.Height = buttonHeight;
        ToolTip.SetTip(Extras, T("Дополнительные штрихкоды: упаковка, второй вариант этикетки. Можно указать «штук в упаковке» — тогда скан упаковки при приходе поставит это количество.",
            "Кошумча штрихкоддор: таңгак, этикетканын экинчи варианты. «Таңгактагы даананы» көрсөтсө болот — анда кирүүдө таңгакты сканерлегенде ошол сан коюлат.",
            "Extra barcodes: a pack, a second label variant. You can set “pieces per pack” — then scanning the pack when receiving sets that quantity.",
            "Ek barkodlar: paket, etiketin ikinci biçimi. «Paketteki adet» yazılabilir — girişte paketi okutunca o miktar yazılır.",
            "Qo'shimcha shtrix-kodlar: qadoq, yorliqning ikkinchi varianti. «Qadoqdagi dona»ni ko'rsatish mumkin — kirimda qadoqni skanerlasangiz shu miqdor qo'yiladi."));
        Avalonia.Automation.AutomationProperties.SetName(Extras, InvoiceBarcodeKit.ExtrasFull + " " + rowName);
        if (vertical)
            Root = new StackPanel { Spacing = 0, Children = { Box, new WrapPanel { Orientation = Orientation.Horizontal, Children = { Scan, Extras } } } };
        else if (existingBox is null)
        {
            Box.Width = 168;
            Box.Margin = new Thickness(0, 0, 6, 4);
        }
        Scan.Click += (_, _) => Arm();
    }

    /// <summary>Показать состояние: поле правится или только читается; «Сканировать» видна у строки без кода; «+N» — число доп. штрихкодов.</summary>
    public void Apply(string code, bool editable, bool showScan, int extrasCount, bool showExtras)
    {
        if (!Box.IsFocused && (Box.Text ?? "") != code)
            Box.Text = code;
        Box.IsReadOnly = !editable;
        Box.Focusable = editable;
        Box.Opacity = editable ? 1 : 0.85;
        Scan.IsVisible = showScan;
        Extras.IsVisible = showExtras;
        Extras.Content = extrasCount > 0 ? "+" + extrasCount.ToString(CultureInfo.InvariantCulture) : "+";
    }

    /// <summary>«Сканировать»: фокус в поле штрихкода (число выделено), следующий скан сканером запишется в строку по Enter.</summary>
    public void Arm()
    {
        Box.Watermark = T("сканируйте…", "сканерлеңиз…", "scan now…", "şimdi okutun…", "skanerlang…");
        Box.Focus();
        Box.SelectAll();
    }

    public void Disarm() => Box.Watermark = _watermark;
}
