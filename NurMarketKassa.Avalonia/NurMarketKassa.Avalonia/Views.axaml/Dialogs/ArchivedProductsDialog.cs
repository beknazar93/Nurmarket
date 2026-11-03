using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-02 (аудит №2, С3-12): окно «Архив товаров» в «Складе». Архивные товары не видны в каталоге и в продажах; здесь их можно найти и вернуть
/// (POST products/{id}/unarchive/). Список — GET products/list/?include_archived=1, фильтр по признаку is_archived.</summary>
internal sealed class ArchivedProductsDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly ICatalogApiService _api;
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private bool _busy;

    /// <summary>true — хотя бы один товар вернули из архива (вызывающий обновляет каталог).</summary>
    public bool Restored { get; private set; }

    public ArchivedProductsDialog(ICatalogApiService api)
    {
        _api = api;
        Title = T("Архив товаров", "Товарлардын архиви", "Product archive", "Ürün arşivi", "Mahsulotlar arxivi");
        SectionCards.AsModal(this, 560);
        _status.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        var scroll = new ScrollViewer { MaxHeight = 420, Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = T("Архивные товары не видны в каталоге и не продаются, история приходов и продаж сохранена. «Вернуть» снова делает товар обычным.",
                "Архивдеги товарлар каталогто көрүнбөйт жана сатылбайт, кирешелер менен сатуулардын тарыхы сакталган. «Кайтаруу» товарды кайра кадимки кылат.",
                "Archived products are hidden from the catalog and not sold; the receipt and sales history is kept. “Restore” makes the product regular again.",
                "Arşivlenen ürünler katalogda görünmez ve satılmaz; giriş ve satış geçmişi saklanır. «Geri al» ürünü yeniden normal yapar.",
                "Arxivdagi mahsulotlar katalogda ko'rinmaydi va sotilmaydi; kirim va sotuvlar tarixi saqlanadi. «Qaytarish» mahsulotni yana oddiy qiladi."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        });
        body.Children.Add(_status);
        body.Children.Add(scroll);
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close(Restored);
        var shell = SectionCards.Shell(Title ?? "", null, body, SectionCards.Buttons(close));
        shell.InitialFocus = close;
        Content = shell;
        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _status.Text = T("Загружаю архив…", "Архив жүктөлүүдө…", "Loading the archive…", "Arşiv yükleniyor…", "Arxiv yuklanmoqda…");
        _list.Children.Clear();
        List<JsonElement> items;
        try
        {
            // 2026-11-02 (приёмка круга 3): архив читается постранично до конца, ход виден («Загружено N из M…»); «пусто» — только если ответ получен и архивных товаров реально 0.
            var progress = new Progress<(int Loaded, int? Total)>(p => _status.Text = p.Total is { } total
                ? T($"Загружено {p.Loaded} из {total}…", $"Жүктөлдү {p.Loaded} / {total}…", $"Loaded {p.Loaded} of {total}…", $"Yüklendi {p.Loaded} / {total}…", $"Yuklandi {p.Loaded} / {total}…")
                : T($"Загружено {p.Loaded}…", $"Жүктөлдү {p.Loaded}…", $"Loaded {p.Loaded}…", $"Yüklendi {p.Loaded}…", $"Yuklandi {p.Loaded}…"));
            items = await _api.ListArchivedProductsAsync(progress).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Склад: архив товаров не загружен ({ex.Message}).", "WARNING");
            _status.Text = T("Не удалось загрузить архив: нет связи с сервером или сервер не отдаёт архивные товары.",
                "Архивди жүктөө мүмкүн болгон жок: сервер менен байланыш жок же сервер архивдеги товарларды бербейт.",
                "Could not load the archive: no connection to the server, or the server does not return archived products.",
                "Arşiv yüklenemedi: sunucuyla bağlantı yok ya da sunucu arşivlenen ürünleri vermiyor.",
                "Arxivni yuklab bo'lmadi: server bilan aloqa yo'q yoki server arxivdagi mahsulotlarni bermaydi.");
            return;
        }

        if (items.Count == 0)
        {
            _status.Text = T("В архиве пусто.", "Архив бош.", "The archive is empty.", "Arşiv boş.", "Arxiv bo'sh.");
            return;
        }
        _status.Text = T($"Товаров в архиве: {items.Count}.", $"Архивдеги товарлар: {items.Count}.", $"Products in the archive: {items.Count}.", $"Arşivdeki ürünler: {items.Count}.", $"Arxivdagi mahsulotlar: {items.Count}.");
        foreach (var item in items.OrderBy(i => Text(i, "name"), StringComparer.CurrentCultureIgnoreCase))
            _list.Children.Add(Row(item));
    }

    private static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : ""
            : "";

    private Control Row(JsonElement item)
    {
        var id = Text(item, "id");
        var name = Text(item, "name");
        var barcode = Text(item, "barcode");
        var stock = Text(item, "quantity");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2) };
        var info = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(name) ? id : name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var detail = string.Join(" · ", new[]
        {
            barcode.Length > 0 ? barcode : null,
            stock.Length > 0 ? T($"остаток {stock}", $"калдык {stock}", $"stock {stock}", $"stok {stock}", $"qoldiq {stock}") : null,
        }.Where(s => s is not null));
        if (detail.Length > 0)
        {
            var d = new TextBlock { Text = detail, FontSize = 12 };
            d.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            info.Children.Add(d);
        }
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);
        var restore = SectionCards.Secondary(T("Вернуть", "Кайтаруу", "Restore", "Geri al", "Qaytarish"));
        restore.Click += async (_, _) => await RestoreAsync(id, name, restore);
        Grid.SetColumn(restore, 1);
        grid.Children.Add(restore);
        return grid;
    }

    private async Task RestoreAsync(string id, string name, Button button)
    {
        if (_busy || string.IsNullOrWhiteSpace(id))
            return;
        _busy = true;
        button.IsEnabled = false;
        try
        {
            await _api.UnarchiveProductAsync(id).ConfigureAwait(true);
            PosLogger.Log($"AUDIT: товар «{name}» ({id}) возвращён из архива.", "INFO");
            Restored = true;
            if (button.Parent is Grid row)
                _list.Children.Remove(row);
            _status.Text = T($"«{name}» возвращён из архива. Каталог обновится при закрытии окна.", $"«{name}» архивден кайтарылды. Терезе жабылганда каталог жаңыланат.",
                $"“{name}” was restored from the archive. The catalog refreshes when you close the window.", $"«{name}» arşivden geri alındı. Pencere kapanınca katalog yenilenir.",
                $"«{name}» arxivdan qaytarildi. Oyna yopilganda katalog yangilanadi.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Склад: товар «{name}» ({id}) не возвращён из архива ({ex.Message}).", "WARNING");
            _status.Text = T($"Не удалось вернуть «{name}» из архива. Проверьте связь с сервером и повторите.", $"«{name}» архивден кайтаруу мүмкүн болгон жок. Сервер менен байланышты текшерип, кайталаңыз.",
                $"Could not restore “{name}” from the archive. Check the server connection and try again.", $"«{name}» arşivden geri alınamadı. Sunucu bağlantısını kontrol edip tekrar deneyin.",
                $"«{name}» ni arxivdan qaytarib bo'lmadi. Server bilan aloqani tekshirib, qayta urinib ko'ring.");
            button.IsEnabled = true;
        }
        finally
        {
            _busy = false;
        }
    }
}
