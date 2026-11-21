using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

// 2026-11-21 («Базар», модуль «Цена по количеству», ТЗ Контейнер-базар 2.4 / 3.2): в карточке товара — «ступени цены»: «от N шт — цена за штуку» (до 8). Касса сама ставит в чек цену подходящей ступени
// по количеству строки. Блок виден при включённом модуле и только у уже сохранённого товара (ступени привязаны к его id; у нового — подсказка «сохраните товар»). Ступени с сервера
// (GET products/<id>/prices/ → volume_prices) показываются отдельной строкой и главнее местных; записать ступени на сервер пока нельзя (адрес записи в ответах Умара не описан) —
// поэтому правка сохраняется на этой кассе (volume-prices.json), об этом сказано в блоке. Раскладка — Grid с общими колонками для заголовка и строк (чек-лист новых окон).
public partial class ProductEditDialog
{
    private readonly List<(TextBox Qty, TextBox Price)> _tierRows = new();
    private StackPanel? _tierRowsHost;
    private TextBlock? _tierMessage;
    private string? _tierProductId;
    private decimal? _tierRetailPrice;

    private void InitBazaarTiersBlock(CatalogProductTileVm? existing)
    {
        if (!BazaarModules.VolumePriceOn)
            return;
        var panel = BazaarTiersBlock;
        panel.IsVisible = true;
        var head = new TextBlock { Text = Tr.T("Цена по количеству", "Санына жараша баа", "Price by quantity", "Miktara göre fiyat", "Miqdorga qarab narx"), FontSize = 16, FontWeight = FontWeight.Bold };
        head.Bind(TextBlock.ForegroundProperty, head.GetResourceObservable("BrushText"));
        panel.Children.Add(head);

        if (existing is null || string.IsNullOrWhiteSpace(existing.Id))
        {
            panel.Children.Add(Hint(Tr.T("Сначала сохраните товар — потом здесь можно задать цены «от N штук».", "Адегенде товарды сактаңыз — андан кийин бул жерде «N даанадан» баасын коюуга болот.",
                "Save the product first — then you can set “from N pieces” prices here.", "Önce ürünü kaydedin — sonra burada «N adetten» fiyatları girilebilir.", "Avval mahsulotni saqlang — keyin bu yerda «N dan» narxlarini belgilash mumkin.")));
            return;
        }
        _tierProductId = existing.Id;
        _tierRetailPrice = existing.PriceValue > 0 ? (decimal)existing.PriceValue : null;
        panel.Children.Add(Hint(Tr.T("Покупает от N штук — цена за штуку ниже. Касса сама ставит её в чеке по количеству; ручная цена кассира главнее.",
            "N даанадан сатып алса — даанасынын баасы төмөн. Касса чекте санына жараша өзү коёт; кассирдин кол менен баасы маанилүү.",
            "Buying from N pieces — a lower price per piece. The till sets it in the receipt by quantity; the cashier's manual price wins.",
            "N adetten alırsa — adet fiyatı düşer. Kasa fişte miktara göre kendisi uygular; kasiyerin elle fiyatı önceliklidir.",
            "N dan sotib olsa — donasi arzonroq. Kassa chekda miqdorga qarab o'zi qo'yadi; kassirning qo'lda narxi ustun.")));

        if (BazaarVolumePrices.ServerTiers(existing.Id) is { Count: > 0 } server)
            panel.Children.Add(Hint(Tr.T("С сервера: ", "Сервердеги: ", "From the server: ", "Sunucudan: ", "Serverdan: ")
                + string.Join("; ", server.Select(t => $"{t.MinQty:0.###} → {MoneyText.Amount((double)t.Price)}"))
                + Tr.T(" — они главнее местных.", " — алар жергиликтүүдөн маанилүү.", " — they win over local ones.", " — yerel olanlardan önceliklidir.", " — ular mahalliylardan ustun.")));

        // Заголовок и строки — одна сетка колонок: [от, шт] [цена] [убрать]
        var header = Row();
        header.Children.Add(Cell(Label(Tr.T("От, шт", "Мин., даана", "From, pcs", "En az, adet", "Dan, dona")), 0));
        header.Children.Add(Cell(Label(Tr.T("Цена за штуку, сом", "Даанасы, сом", "Price per piece, som", "Adet fiyatı, som", "Donasi, so'm")), 2));
        panel.Children.Add(header);

        _tierRowsHost = new StackPanel { Spacing = 6 };
        panel.Children.Add(_tierRowsHost);
        foreach (var t in BazaarVolumePrices.LocalTiers(existing.Id))
            AddTierRow(t.MinQty.ToString("0.###"), t.Price.ToString("0.##"));
        if (_tierRows.Count == 0)
            AddTierRow("", "");

        var add = new Button { Content = "+ " + Tr.T("Ступень", "Баскыч", "Tier", "Kademe", "Bosqich"), MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 0) };
        add.Classes.Add("modal-cancel");
        add.Click += (_, _) => { if (_tierRows.Count < BazaarVolumePrices.MaxTiers) AddTierRow("", ""); };
        var save = new Button { Content = Tr.T("Сохранить ступени", "Баскычтарды сактоо", "Save tiers", "Kademeleri kaydet", "Bosqichlarni saqlash"), MinHeight = 44, Padding = new Thickness(16, 0) };
        save.Classes.Add("modal-primary");
        save.Click += (_, _) => SaveTiers();
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,10,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetColumn(save, 2);
        actions.Children.Add(add);
        actions.Children.Add(save);
        panel.Children.Add(actions);

        _tierMessage = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        panel.Children.Add(_tierMessage);
    }

    private static TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private static Grid Row() => new() { ColumnDefinitions = new ColumnDefinitions("*,10,*,10,44") };

    private static Control Cell(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private void AddTierRow(string qty, string price)
    {
        var q = new TextBox { Text = qty, MaxLength = 8, Height = 44, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
        var p = new TextBox { Text = price, MaxLength = 12, Height = 44, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
        var remove = new Button { Content = "✕", Width = 44, Height = 44, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
        remove.Classes.Add("modal-cancel");
        var row = Row();
        row.Children.Add(Cell(q, 0));
        row.Children.Add(Cell(p, 2));
        row.Children.Add(Cell(remove, 4));
        var item = (q, p);
        remove.Click += (_, _) =>
        {
            _tierRows.Remove(item);
            _tierRowsHost?.Children.Remove(row);
        };
        _tierRows.Add(item);
        _tierRowsHost?.Children.Add(row);
    }

    private void SaveTiers()
    {
        if (_tierProductId is null)
            return;
        var tiers = new List<VolumeTier>();
        foreach (var (q, p) in _tierRows)
        {
            if (string.IsNullOrWhiteSpace(q.Text) && string.IsNullOrWhiteSpace(p.Text))
                continue; // пустая строка — не ступень
            var qty = BazaarHaggle.ParseAmount(q.Text);
            var price = BazaarHaggle.ParseAmount(p.Text);
            if (qty is not > 0m || price is not > 0m)
            {
                ShowTierMessage(VolumeTiersError.BadQuantity, qty is not > 0m ? null : VolumeTiersError.BadPrice);
                return;
            }
            tiers.Add(new VolumeTier(qty.Value, price.Value));
        }
        var error = BazaarVolumePrices.Validate(tiers, _tierRetailPrice);
        if (error != VolumeTiersError.None)
        {
            ShowTierMessage(error, null);
            return;
        }
        if (!BazaarVolumePrices.SetLocalTiers(_tierProductId, tiers, _tierRetailPrice))
        {
            ShowTierText(Tr.T("Не удалось сохранить ступени на этой кассе.", "Баскычтарды бул кассага сактоо мүмкүн болгон жок.", "Could not save the tiers on this till.", "Kademeler bu kasaya kaydedilemedi.", "Bosqichlarni bu kassaga saqlab bo'lmadi."), isError: true);
            return;
        }
        PosLogger.Log($"Цена по количеству: товар {_tierProductId}, ступеней {tiers.Count} (местные).", "INFO");
        ShowTierText(tiers.Count == 0
            ? Tr.T("Ступени убраны.", "Баскычтар өчүрүлдү.", "Tiers removed.", "Kademeler kaldırıldı.", "Bosqichlar olib tashlandi.")
            : Tr.T($"Ступеней сохранено: {tiers.Count} (на этой кассе).", $"Баскычтар сакталды: {tiers.Count} (бул кассада).", $"Tiers saved: {tiers.Count} (on this till).", $"Kaydedilen kademe: {tiers.Count} (bu kasada).", $"Saqlangan bosqichlar: {tiers.Count} (shu kassada)."), isError: false);
    }

    private void ShowTierMessage(VolumeTiersError error, VolumeTiersError? alt) =>
        ShowTierText(BazaarVolumePrices.ErrorText(alt ?? error), isError: true);

    private void ShowTierText(string text, bool isError)
    {
        if (_tierMessage is null)
            return;
        _tierMessage.Text = text;
        _tierMessage.IsVisible = true;
        _tierMessage.Bind(TextBlock.ForegroundProperty, _tierMessage.GetResourceObservable(isError ? "BrushDanger" : "BrushUiStatusOk"));
    }
}
