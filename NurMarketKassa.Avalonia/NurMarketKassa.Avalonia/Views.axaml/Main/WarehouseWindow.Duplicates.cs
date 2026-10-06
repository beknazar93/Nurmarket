using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-06, ТЗ ч.12, п. 2.6 (сервер выложил 05.10): старые дубли штрихкодов — один штрихкод у нескольких
/// товаров (созданы до запрета сервера). На кассе такой штрихкод находит не тот товар. В программе владельца на складе —
/// кнопка «⚠ Дубли штрихкодов: N» (только когда они есть) и список: штрихкод → товары с остатком и датой создания,
/// чтобы владелец удалил или поправил лишний товар (GET api/main/products/barcode-duplicates/).</summary>
public partial class WarehouseWindow
{
    private Button? _duplicatesButton;

    private async Task ShowBarcodeDuplicatesButtonAsync()
    {
        try
        {
            var api = App.GetRequiredService<NurMarketApiClient>();
            var data = await api.RequestAsync(HttpMethod.Get, "api/main/products/barcode-duplicates/", null, null, CancellationToken.None, TimeSpan.FromSeconds(30))
                .ConfigureAwait(true);
            // 2026-10-06, проверка: сервер считает «дублем» и копию товара в филиале (перемещение создаёт в филиале свой товар
            // с тем же штрихкодом — у него branch = id филиала). Это не дубль: у каждого склада свой товар. Дубль — только
            // повтор штрихкода внутри одного склада (главный — branch = null).
            var groups = new List<(string Barcode, List<JsonElement> Products)>();
            if (data.ValueKind == JsonValueKind.Array)
                foreach (var g in data.EnumerateArray())
                {
                    if (!g.TryGetProperty("products", out var ps) || ps.ValueKind != JsonValueKind.Array)
                        continue;
                    var barcode = g.TryGetProperty("barcode", out var bc) ? bc.ToString() : "";
                    foreach (var sameWarehouse in ps.EnumerateArray().Select(x => x.Clone())
                                 .GroupBy(x => x.TryGetProperty("branch", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "")
                                 .Where(x => x.Count() > 1))
                        groups.Add((barcode, sameWarehouse.ToList()));
                }
            if (groups.Count == 0 || RefreshWarehouseButton.Parent is not Panel header)
            {
                if (_duplicatesButton != null)
                    _duplicatesButton.IsVisible = false;
                return;
            }
            if (_duplicatesButton == null)
            {
                _duplicatesButton = new Button { Classes = { "SecondaryButton" }, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6) };
                _duplicatesButton.Bind(Button.ForegroundProperty, this.GetResourceObservable("BrushWarning"));
                header.Children.Insert(header.Children.IndexOf(RefreshWarehouseButton), _duplicatesButton);
            }
            _duplicatesButton.Content = Tr.T($"⚠ Дубли штрихкодов: {groups.Count}", $"⚠ Кайталанган штрихкоддор: {groups.Count}", $"⚠ Duplicate barcodes: {groups.Count}",
                $"⚠ Yinelenen barkodlar: {groups.Count}", $"⚠ Takroriy shtrix-kodlar: {groups.Count}");
            ToolTip.SetTip(_duplicatesButton, Tr.T("Один штрихкод у нескольких товаров — касса может найти не тот товар", "Бир штрихкод бир нече товардо — касса башка товарды табышы мүмкүн",
                "One barcode on several products — the till may pick the wrong one", "Bir barkod birden fazla üründe — kasa yanlış ürünü bulabilir",
                "Bitta shtrix-kod bir nechta mahsulotda — kassa boshqa mahsulotni topishi mumkin"));
            _duplicatesButton.Click -= DuplicatesButton_Click;
            _duplicatesButton.Click += DuplicatesButton_Click;
            _duplicatesButton.Tag = groups;
            _duplicatesButton.IsVisible = true;
            PosLogger.Log($"Склад: дублей штрихкодов {groups.Count}.", "INFO");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Склад: дубли штрихкодов не получены ({ex.Message}).", "WARNING");
        }
    }

    private async void DuplicatesButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_duplicatesButton?.Tag is not List<(string Barcode, List<JsonElement> Products)> groups)
            return;
        try
        {
            await ShowDuplicatesAsync(groups).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Склад: список дублей не открылся: {ex}", "WARNING");
        }
    }

    private async Task ShowDuplicatesAsync(List<(string Barcode, List<JsonElement> Products)> groups)
    {
        static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";

        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var dialog = new Window
        {
            Width = 640, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = Tr.T("Дубли штрихкодов", "Кайталанган штрихкоддор", "Duplicate barcodes", "Yinelenen barkodlar", "Takroriy shtrix-kodlar"),
        };
        dialog.Bind(BackgroundProperty, this.GetResourceObservable("BrushWindowBackdrop"));
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        var title = new TextBlock { Text = dialog.Title, FontSize = 20, FontWeight = FontWeight.Bold };
        title.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        panel.Children.Add(title);
        var hint = new TextBlock
        {
            Text = Tr.T("Эти штрихкоды стоят у нескольких товаров — так было до запрета сервера. Касса при сканировании может взять не тот товар. Оставьте у штрихкода один товар: лишний удалите или поменяйте ему штрихкод (поиск по складу — по названию).",
                "Бул штрихкоддор бир нече товарда турат — сервер тыюу салганга чейин ушундай болгон. Касса сканерлегенде башка товарды алышы мүмкүн. Штрихкодго бир товар калтырыңыз: ашыкчасын өчүрүңүз же штрихкодун алмаштырыңыз.",
                "These barcodes are on several products — created before the server banned it. When scanning, the till may pick the wrong product. Keep one product per barcode: delete the extra one or change its barcode.",
                "Bu barkodlar birden fazla üründe — sunucu yasaklamadan önce oluşturuldu. Kasa tararken yanlış ürünü alabilir. Her barkodda bir ürün bırakın: fazlasını silin veya barkodunu değiştirin.",
                "Bu shtrix-kodlar bir nechta mahsulotda — server taqiqlagunga qadar shunday bo'lgan. Kassa skanerlaganda boshqa mahsulotni olishi mumkin. Shtrix-kodda bitta mahsulot qoldiring: ortiqchasini o'chiring yoki shtrix-kodini almashtiring."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        };
        hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(hint);

        foreach (var group in groups)
        {
            var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(14, 10) };
            card.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushPanel"));
            card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushBorder"));
            var body = new StackPanel { Spacing = 4 };
            var code = new TextBlock { Text = group.Barcode, FontSize = 15, FontWeight = FontWeight.SemiBold };
            code.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
            body.Children.Add(code);
            foreach (var p in group.Products)
            {
                var qty = double.TryParse(Str(p, "quantity"), NumberStyles.Any, CultureInfo.InvariantCulture, out var q) ? q.ToString("0.###", ru) : Str(p, "quantity");
                var created = DateTime.TryParse(Str(p, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var c) ? c.ToString("dd.MM.yyyy", ru) : "";
                var alt = p.TryGetProperty("is_alternate", out var a) && a.ValueKind == JsonValueKind.True;
                var line = new TextBlock
                {
                    Text = "• " + Str(p, "name")
                           + Tr.T($" — остаток {qty}", $" — калдык {qty}", $" — stock {qty}", $" — stok {qty}", $" — qoldiq {qty}")
                           + (created.Length > 0 ? Tr.T($", создан {created}", $", түзүлгөн {created}", $", created {created}", $", oluşturuldu {created}", $", yaratilgan {created}") : "")
                           + (alt ? Tr.T(" (доп. штрихкод)", " (кошумча штрихкод)", " (extra barcode)", " (ek barkod)", " (qo'shimcha shtrix-kod)") : ""),
                    FontSize = 13, TextWrapping = TextWrapping.Wrap,
                };
                line.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
                body.Children.Add(line);
            }
            card.Child = body;
            panel.Children.Add(card);
        }

        var ok = UiKit.Primary(this, Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        ok.Click += (_, _) => dialog.Close();
        panel.Children.Add(ok);
        dialog.Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        await dialog.ShowDialog(this).ConfigureAwait(true);
    }
}
