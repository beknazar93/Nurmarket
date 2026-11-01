using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Выбранный для добавления товар: сколько и по какой закупочной цене.</summary>
internal sealed record AddPick(string ProductId, string Name, string Unit, string Barcode, string Code, double Qty, double Price, double Sale);

/// <summary>2026-11-01 (просьба владельца «добавить товар в накладную»): «+ Добавить товар» — поиск товара (название, штрихкод, артикул — тот же поиск, что при приёме товара:
/// ё=е, раскладка, транслит), количество и закупочная цена. Так как PATCH прихода у сервера нет, товар уходит ДОПОЛНИТЕЛЬНЫМ приходом к этой накладной.</summary>
internal sealed class InvoiceAddItemDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public AddPick? Result { get; private set; }

    private readonly TextBox _search = new() { MinHeight = 44 };
    private readonly ListBox _list = new() { MaxHeight = 260, MinHeight = 120 };
    private readonly TextBox _qty = new() { MinHeight = 44, Text = "1" };
    private readonly TextBox _price = new() { MinHeight = 44 };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _ok;
    private List<CatalogProductTileVm> _all = new();

    public InvoiceAddItemDialog(string docNumber, string supplier, bool viaPatch = false)
    {
        Title = T("Добавить товар в накладную", "Накладнойго товар кошуу", "Add a product to the invoice", "Faturaya ürün ekle", "Yuk xatiga mahsulot qo'shish");
        SectionCards.AsModal(this, 640);
        try
        {
            _all = CatalogCacheService.Products.ToList();
        }
        catch (InvalidOperationException)
        {
            // каталог обновлялся в этот момент — откроем с тем, что успело прочитаться
        }
        var panel = new StackPanel { Spacing = 8 };
        TextBlock Line(string text, string brush = "BrushText", double size = 13, FontWeight? w = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = w ?? FontWeight.Normal };
            t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(brush));
            return t;
        }
        // Простыми словами: что произойдёт.
        if (viaPatch)
            panel.Children.Add(Line(T($"Товар будет добавлен в накладную {docNumber} отдельной строкой (сервер сам пересчитает остаток, долг поставщику «{supplier}» и деньги). В истории правок — с вашим именем и причиной.",
                $"Товар {docNumber} накладнойуна өзүнчө сап болуп кошулат (калдыкты, жеткирүүчү «{supplier}» алдындагы карызды жана акчаны сервер өзү кайра эсептейт). Оңдоо тарыхында — сиздин атыңыз жана себеп менен.",
                $"The product will be added to invoice {docNumber} as a separate line (the server recalculates stock, the debt to supplier “{supplier}” and the money). In the edit history — with your name and the reason.",
                $"Ürün {docNumber} faturasına ayrı bir satır olarak eklenir (stok, tedarikçi «{supplier}» borcu ve parayı sunucu yeniden hesaplar). Düzenleme geçmişinde — adınız ve nedenle.",
                $"Mahsulot {docNumber} yuk xatiga alohida qator bo'lib qo'shiladi (ombor qoldig'i, yetkazib beruvchi «{supplier}» qarzi va pulni server qayta hisoblaydi). Tahrir tarixida — ismingiz va sabab bilan."), "BrushTextSoft", 12.5));
        else
        panel.Children.Add(Line(T($"Будет создан дополнительный приход к накладной {docNumber} (тот же поставщик «{supplier}», тот же способ оплаты). Сама накладная не меняется: новый товар появится в ней отдельной строкой «из доп. прихода», а в истории правок — с вашим именем и причиной.",
            $"{docNumber} накладнойуна кошумча кирүү түзүлөт (ошол эле жеткирүүчү «{supplier}», ошол эле төлөм ыкмасы). Накладнойдун өзү өзгөрбөйт: жаңы товар анда «кошумча кирүүдөн» өзүнчө сап болуп чыгат, ал эми оңдоо тарыхында — сиздин атыңыз жана себеп менен.",
            $"An additional receipt will be created for invoice {docNumber} (same supplier “{supplier}”, same payment type). The invoice itself does not change: the new product appears in it as a separate line “from an additional receipt”, and in the edit history — with your name and the reason.",
            $"{docNumber} faturası için ek giriş oluşturulur (aynı tedarikçi «{supplier}», aynı ödeme türü). Faturanın kendisi değişmez: yeni ürün onda «ek girişten» ayrı bir satır olarak görünür, düzenleme geçmişinde ise adınız ve nedenle.",
            $"{docNumber} yuk xatiga qo'shimcha kirim yaratiladi (o'sha yetkazib beruvchi «{supplier}», o'sha to'lov turi). Yuk xatining o'zi o'zgarmaydi: yangi mahsulot unda «qo'shimcha kirimdan» alohida qator bo'lib chiqadi, tahrir tarixida esa — ismingiz va sabab bilan."), "BrushTextSoft", 12.5));
        _search.Watermark = T("Название, штрихкод или артикул", "Аталышы, штрих-код же артикул", "Name, barcode or article", "Ad, barkod veya ürün kodu", "Nomi, shtrix-kod yoki artikul");
        panel.Children.Add(_search);
        _list.ItemTemplate = new FuncDataTemplate<CatalogProductTileVm>((p, _) =>
        {
            var sp = new StackPanel { Margin = new Thickness(6, 4) };
            sp.Children.Add(new TextBlock { Text = p?.Title ?? "", FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            var sub = new TextBlock { Text = string.Join(" · ", new[] { p?.Barcode, p?.ProductCode ?? p?.Article }.Where(s => !string.IsNullOrWhiteSpace(s))), FontSize = 12 };
            sub.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            sp.Children.Add(sub);
            return sp;
        }, true);
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is CatalogProductTileVm p && p.PurchasePrice > 0 && string.IsNullOrWhiteSpace(_price.Text))
                _price.Text = p.PurchasePrice.ToString("0.##");
            Check();
        };
        panel.Children.Add(_list);
        panel.Children.Add(Line(T("Количество", "Саны", "Quantity", "Miktar", "Miqdor"), "BrushTextSoft", 12.5, FontWeight.SemiBold));
        panel.Children.Add(_qty);
        panel.Children.Add(Line(T("Закупочная цена (за единицу)", "Сатып алуу баасы (бирдигине)", "Purchase price (per unit)", "Alış fiyatı (birim başına)", "Xarid narxi (birligiga)"), "BrushTextSoft", 12.5, FontWeight.SemiBold));
        panel.Children.Add(_price);
        panel.Children.Add(SectionCards.ErrorBox(this, _error));

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close();
        _ok = SectionCards.Primary(T("Добавить в правку", "Оңдоого кошуу", "Add to the edit", "Düzenlemeye ekle", "Tahrirga qo'shish"));
        _ok.IsEnabled = false;
        _ok.Click += (_, _) => Accept();
        _search.TextChanged += (_, _) => Refresh();
        _qty.TextChanged += (_, _) => Check();
        _price.TextChanged += (_, _) => Check();
        Content = SectionCards.Shell(Title, T("Товар попадёт в правку; сохраняется он кнопкой «Сохранить правку» после проверки.", "Товар оңдоого түшөт; текшерүүдөн кийин «Оңдоону сактоо» баскычы менен сакталат.",
            "The product goes into the edit; it is saved with “Save the edit” after the review.", "Ürün düzenlemeye girer; incelemeden sonra «Düzenlemeyi kaydet» ile kaydedilir.", "Mahsulot tahrirga tushadi; tekshiruvdan keyin «Tahrirni saqlash» bilan saqlanadi."), panel, SectionCards.Buttons(cancel, _ok));
        Opened += (_, _) => _search.Focus();
        Refresh();
    }

    private void Refresh()
    {
        var q = (_search.Text ?? "").Trim();
        _list.ItemsSource = q.Length == 0 ? _all.Take(30).ToList() : SearchMatch.Products(_all, q).Take(30).ToList();
    }

    private static bool Parse(string? text, out double v) =>
        double.TryParse((text ?? "").Replace(" ", "").Replace(',', '.').Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) && v > 0 && !double.IsInfinity(v);

    private void Check() => _ok.IsEnabled = _list.SelectedItem is CatalogProductTileVm && Parse(_qty.Text, out _) && Parse(_price.Text, out _);

    private void Accept()
    {
        if (_list.SelectedItem is not CatalogProductTileVm p || p.Id is not { Length: > 0 } id || !Parse(_qty.Text, out var qty) || !Parse(_price.Text, out var price))
            return;
        Result = new AddPick(id, p.Title, string.IsNullOrWhiteSpace(p.Unit) ? "шт" : p.Unit!, p.Barcode ?? "", p.ProductCode ?? p.Article ?? "", Math.Round(qty, 3), Math.Round(price, 2), p.PriceValue);
        Close();
    }
}
