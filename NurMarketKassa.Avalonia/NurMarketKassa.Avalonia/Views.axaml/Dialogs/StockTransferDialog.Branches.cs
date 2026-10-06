using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-06, владелец (снимок окна «Перемещение» на складе): «перемещение на филиалы сделай».
///
/// Раньше документ перемещения на складе был только учётом на этой кассе: «Откуда/Куда» — любой текст, остатки не
/// менялись. Теперь, если у компании есть филиалы, у «Откуда/Куда» есть «▾» — главный склад или активный филиал.
/// Когда оба конца — главный склад/филиалы, «Отправить» проводит перемещение на сервере (POST api/main/branch-transfers/,
/// как раздел «Филиалы» программы владельца и сайт NurCRM): остатки отправителя и получателя меняются сразу. Номер и id
/// накладной сервера пишутся в примечание документа. «Отменить» такого документа в пути отменяет и на сервере
/// (…/cancel/ — товар возвращается отправителю); «Принять» — только отметка на кассе (остатки уже перемещены).
/// Свои места (зоны, ячейки, любой текст) — как раньше, только учёт на этой кассе. Нет филиалов или нет прав
/// у кассира (сервер не отдал список) — окно как было.</summary>
public partial class StockTransferDialog
{
    private static readonly Regex ServerIdInNote = new(@"\[([0-9a-fA-F-]{8,})\]", RegexOptions.CultureInvariant);
    // «Главный склад» на всех языках программы: документ могли создать при другом языке.
    private static readonly string[] MainWarehouseNames = ["Главный склад", "Башкы кампа", "Main warehouse", "Ana depo", "Asosiy ombor"];
    private List<(string? Id, string Name)> _branchPlaces = new();

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private async Task LoadBranchPlacesAsync()
    {
        try
        {
            var branches = await BranchesWindow.LoadBranchListAsync(App.GetRequiredService<NurMarketApiClient>()).ConfigureAwait(true);
            var active = branches.Where(b => b.IsActive && b.Name.Length > 0).ToList();
            if (active.Count > 0)
            {
                _branchPlaces = new List<(string? Id, string Name)> { (null, BranchesWindow.MainWarehouse) };
                _branchPlaces.AddRange(active.Select(b => ((string?)b.Id, b.Name)));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Перемещение: филиалы не загружены ({ex.Message}) — документ только на этой кассе.", "INFO");
        }
        UpdateBranchUi();
    }

    private int PlaceIndex(string? text)
    {
        var name = (text ?? "").Trim();
        if (name.Length == 0 || _branchPlaces.Count == 0)
            return -1;
        if (MainWarehouseNames.Any(n => string.Equals(n, name, StringComparison.CurrentCultureIgnoreCase)))
            return 0;
        return _branchPlaces.FindIndex(p => string.Equals(p.Name.Trim(), name, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Маршрут между главным складом и филиалом (или двумя филиалами); null — свои места, только эта касса.</summary>
    private (string? FromId, string? ToId, string FromName, string ToName)? ServerRoute()
    {
        var from = PlaceIndex(FromBox.Text);
        var to = PlaceIndex(ToBox.Text);
        if (from < 0 || to < 0 || from == to)
            return null;
        return (_branchPlaces[from].Id, _branchPlaces[to].Id, _branchPlaces[from].Name, _branchPlaces[to].Name);
    }

    private string? ServerTransferId() =>
        _transfer?.Note is { } note && ServerIdInNote.Match(note) is { Success: true } m ? m.Groups[1].Value : null;

    private void UpdateBranchUi()
    {
        var linked = ServerTransferId() is not null;
        var hasBranches = _branchPlaces.Count > 0;
        var editable = _transfer?.Status == StockTransferService.StatusCreated && !linked;
        FromPickButton.IsVisible = ToPickButton.IsVisible = hasBranches;
        FromPickButton.IsEnabled = ToPickButton.IsEnabled = editable;
        // Проведённое на сервере перемещение уже не перенаправить — маршрут только для чтения.
        FromBox.IsReadOnly = ToBox.IsReadOnly = linked;
        // Состав уже ушёл на сервер — новые строки туда не попали бы.
        if (linked)
        {
            ItemEditPanel.IsEnabled = false;
            SuggestionsBox.IsVisible = false;
        }
        BranchHint.IsVisible = hasBranches || linked;
        var shown = linked ? Regex.Match(_transfer?.Note ?? "", @"№\S+").Value : "";
        BranchHint.Text = !linked ? null
            : _transfer?.Status == StockTransferService.StatusCancelled
                ? T($"Отменено и на сервере NurCRM (накладная {shown}): товар вернулся отправителю.",
                    $"NurCRM серверинде да жокко чыгарылды (накладная {shown}): товар жөнөтүүчүгө кайтты.",
                    $"Canceled on the NurCRM server too (waybill {shown}): the goods went back to the sender.",
                    $"NurCRM sunucusunda da iptal edildi (irsaliye {shown}): ürünler gönderene döndü.",
                    $"NurCRM serverida ham bekor qilindi (yuk xati {shown}): mahsulot jo'natuvchiga qaytdi.")
            : _transfer?.Status == StockTransferService.StatusDelivered
                ? T($"Проведено на сервере NurCRM (накладная {shown}): остатки перемещены.",
                    $"NurCRM серверинде өткөрүлдү (накладная {shown}): калдыктар жылдырылды.",
                    $"Done on the NurCRM server (waybill {shown}): stock has been moved.",
                    $"NurCRM sunucusunda yapıldı (irsaliye {shown}): stok taşındı.",
                    $"NurCRM serverida o'tkazildi (yuk xati {shown}): qoldiqlar ko'chirildi.")
                : T($"Проведено на сервере NurCRM (накладная {shown}): остатки уже перемещены. «Отменить» вернёт товар отправителю и на сервере.",
                    $"NurCRM серверинде өткөрүлдү (накладная {shown}): калдыктар жылдырылды. «Жокко чыгаруу» товарды серверде да жөнөтүүчүгө кайтарат.",
                    $"Done on the NurCRM server (waybill {shown}): stock has been moved. “Cancel” returns the goods to the sender on the server too.",
                    $"NurCRM sunucusunda yapıldı (irsaliye {shown}): stok taşındı. «İptal» ürünleri sunucuda da gönderene iade eder.",
                    $"NurCRM serverida o'tkazildi (yuk xati {shown}): qoldiqlar ko'chirildi. «Bekor qilish» mahsulotni serverda ham jo'natuvchiga qaytaradi.");
        if (linked)
            return;
        BranchHint.Text = ServerRoute() is { } route
                ? T($"{route.FromName} → {route.ToName}: при «Отправить» перемещение проводится на сервере, остатки меняются сразу — как в разделе «Филиалы» и на сайте.",
                    $"{route.FromName} → {route.ToName}: «Жөнөтүү» басылганда жылдыруу серверде өткөрүлөт, калдыктар дароо өзгөрөт — «Филиалдар» бөлүмүндөгүдөй жана сайттагыдай.",
                    $"{route.FromName} → {route.ToName}: “Send” makes the transfer on the server and stock changes right away — as in “Branches” and on the website.",
                    $"{route.FromName} → {route.ToName}: «Gönder» transferi sunucuda yapar, stok hemen değişir — «Şubeler» bölümünde ve sitede olduğu gibi.",
                    $"{route.FromName} → {route.ToName}: «Jo'natish» ko'chirishni serverda o'tkazadi, qoldiqlar darhol o'zgaradi — «Filiallar» bo'limi va saytdagidek.")
                : T("Перемещение в филиал: нажмите ▾ у «Откуда» и «Куда» и выберите главный склад или филиал — тогда остатки переместятся на сервере. Свои места (зоны, ячейки) — только учёт на этой кассе.",
                    "Филиалга жылдыруу: «Кайдан» жана «Кайда» жанындагы ▾ басып, башкы кампаны же филиалды тандаңыз — ошондо калдыктар серверде жылат. Өз жайлар (зоналар, уячалар) — бул кассада гана эсеп.",
                    "Transfer to a branch: press ▾ next to “From” and “To” and choose the main warehouse or a branch — then stock moves on the server. Your own places (zones, cells) are tracked on this till only.",
                    "Şubeye transfer: «Nereden» ve «Nereye» yanındaki ▾ düğmesine basıp ana depoyu veya şubeyi seçin — stok sunucuda taşınır. Kendi yerleriniz (bölge, hücre) yalnızca bu kasada izlenir.",
                    "Filialga ko'chirish: «Qayerdan» va «Qayerga» yonidagi ▾ ni bosib, asosiy ombor yoki filialni tanlang — shunda qoldiqlar serverda ko'chadi. O'z joylaringiz (zona, katak) — faqat shu kassada hisob.");
    }

    private void PickPlace_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _branchPlaces.Count == 0)
            return;
        var target = Equals(button.Tag, "to") ? ToBox : FromBox;
        var menu = new MenuFlyout();
        foreach (var place in _branchPlaces)
        {
            var name = place.Name;
            var item = new MenuItem { Header = name };
            item.Click += (_, _) =>
            {
                target.Text = name;
                SaveHeader();
                UpdateBranchUi();
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(button);
    }

    private async Task ShipAsync()
    {
        if (ServerRoute() is { } route && ServerTransferId() is null)
        {
            await ShipToServerAsync(route);
            return;
        }
        ChangeStatus(StockTransferService.StatusInTransit);
    }

    private string? DeliverNote() => ServerTransferId() is null
        ? null
        : T("Принято. Остатки переместил сервер при отправке.", "Кабыл алынды. Калдыктарды сервер жөнөтүүдө жылдырган.", "Received. The server moved the stock when it was sent.",
            "Teslim alındı. Stoku sunucu gönderimde taşıdı.", "Qabul qilindi. Qoldiqlarni server jo'natishda ko'chirgan.");

    private async Task ShipToServerAsync((string? FromId, string? ToId, string FromName, string ToName) route)
    {
        if (_transfer is null)
            return;
        SaveHeader();
        var items = StockTransferService.Instance.LoadItems(_transferId);
        if (items.Count == 0)
        {
            PosDialogs.Warning(this, T("Добавьте товары в перемещение.", "Жылдырууга товар кошуңуз.", "Add products to the transfer.", "Transfere ürün ekleyin.", "Ko'chirishga mahsulot qo'shing."));
            return;
        }

        var api = App.GetRequiredService<NurMarketApiClient>();
        ShipButton.IsEnabled = false;
        try
        {
            // Товар и остаток — со склада отправителя: у главного склада и у филиала свои карточки товара (свой id).
            var lines = new List<(string ProductId, double Quantity)>();
            var problems = new List<string>();
            foreach (var group in items.GroupBy(i => i.ProductId ?? ("name:" + i.ProductName.Trim().ToLowerInvariant())))
            {
                var first = group.First();
                var quantity = Math.Round(group.Sum(i => i.Quantity), 3);
                var found = await FindOnSenderAsync(api, route.FromId, first.ProductId, first.Barcode, first.ProductName);
                if (found is null)
                    problems.Add(T($"«{first.ProductName}» — нет на складе «{route.FromName}»", $"«{first.ProductName}» — «{route.FromName}» кампасында жок",
                        $"“{first.ProductName}” — not in “{route.FromName}”", $"«{first.ProductName}» — «{route.FromName}» deposunda yok", $"«{first.ProductName}» — «{route.FromName}» omborida yo'q"));
                else if (found.Value.Available + 1e-9 < quantity)
                    problems.Add(T($"«{first.ProductName}» — есть {found.Value.Available:0.###}, нужно {quantity:0.###}", $"«{first.ProductName}» — бар {found.Value.Available:0.###}, керек {quantity:0.###}",
                        $"“{first.ProductName}” — {found.Value.Available:0.###} in stock, {quantity:0.###} needed", $"«{first.ProductName}» — stokta {found.Value.Available:0.###}, gereken {quantity:0.###}",
                        $"«{first.ProductName}» — bor {found.Value.Available:0.###}, kerak {quantity:0.###}"));
                else
                    lines.Add((found.Value.Id, quantity));
            }
            if (problems.Count > 0)
            {
                PosDialogs.Warning(this, T("Перемещение не проведено:", "Жылдыруу өткөрүлгөн жок:", "The transfer was not made:", "Transfer yapılmadı:", "Ko'chirish o'tkazilmadi:")
                                         + "\n• " + string.Join("\n• ", problems));
                return;
            }

            var qtyTotal = lines.Sum(l => l.Quantity);
            if (!await PosDialogs.ConfirmYesNoModalAsync(this, T(
                    $"Провести перемещение на сервере: {route.FromName} → {route.ToName}, {lines.Count} поз., {qtyTotal:0.###} ед.? Остатки изменятся сразу.",
                    $"Жылдырууну серверде өткөрөсүзбү: {route.FromName} → {route.ToName}, {lines.Count} позиция, {qtyTotal:0.###} бирдик? Калдыктар дароо өзгөрөт.",
                    $"Make the transfer on the server: {route.FromName} → {route.ToName}, {lines.Count} items, {qtyTotal:0.###} units? Stock changes right away.",
                    $"Transfer sunucuda yapılsın mı: {route.FromName} → {route.ToName}, {lines.Count} kalem, {qtyTotal:0.###} birim? Stok hemen değişir.",
                    $"Ko'chirish serverda o'tkazilsinmi: {route.FromName} → {route.ToName}, {lines.Count} pozitsiya, {qtyTotal:0.###} birlik? Qoldiqlar darhol o'zgaradi.")))
                return;

            var comment = T($"Склад кассы, документ {_transfer.Number}", $"Кассанын кампасы, документ {_transfer.Number}", $"Till warehouse, document {_transfer.Number}",
                $"Kasa deposu, belge {_transfer.Number}", $"Kassa ombori, hujjat {_transfer.Number}");
            if (!string.IsNullOrWhiteSpace(_transfer.Carrier) || !string.IsNullOrWhiteSpace(_transfer.TrackingNumber))
                comment += " · " + string.Join(" ", new[] { _transfer.Carrier, _transfer.TrackingNumber }.Where(x => !string.IsNullOrWhiteSpace(x)));
            // Как у сайта и раздела «Филиалы»: главный склад — null, количество — число.
            var body = new Dictionary<string, object?>
            {
                ["from_branch"] = route.FromId,
                ["to_branch"] = route.ToId,
                ["date"] = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["comment"] = comment.Length > 500 ? comment[..500] : comment,
                ["items"] = lines.Select(l => new Dictionary<string, object?> { ["product"] = l.ProductId, ["quantity"] = l.Quantity }).ToList(),
            };
            var data = await api.RequestAsync(HttpMethod.Post, "api/main/branch-transfers/", body, null, CancellationToken.None, TimeSpan.FromSeconds(45)).ConfigureAwait(true);
            var number = BranchesWindow.Str(data, "number");
            var id = BranchesWindow.Str(data, "id");
            StockTransferService.Instance.SetNote(_transferId, $"NurCRM №{number} [{id}]");
            PosLogger.Log($"Перемещение {_transfer.Number} проведено на сервере: №{number}, {route.FromName} → {route.ToName}, {lines.Count} поз., {qtyTotal:0.###} ед.", "INFO");
            ChangeStatus(StockTransferService.StatusInTransit, T($"Проведено на сервере: накладная №{number}, остатки перемещены.", $"Серверде өткөрүлдү: накладная №{number}, калдыктар жылдырылды.",
                $"Done on the server: waybill №{number}, stock moved.", $"Sunucuda yapıldı: irsaliye №{number}, stok taşındı.", $"Serverda o'tkazildi: yuk xati №{number}, qoldiqlar ko'chirildi."));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Перемещение {_transfer.Number} на сервере не проведено ({ex.Message}).", "WARNING");
            PosDialogs.Warning(this, T("Перемещение не проведено на сервере: ", "Жылдыруу серверде өткөрүлгөн жок: ", "The transfer was not made on the server: ",
                "Transfer sunucuda yapılmadı: ", "Ko'chirish serverda o'tkazilmadi: ") + ServerTelegramBotApi.DescribeFields(ex));
        }
        finally
        {
            Reload();
        }
    }

    /// <summary>Товар на складе отправителя: по id (если касса работает с тем же складом), иначе по штрихкоду, иначе по названию.</summary>
    private static async Task<(string Id, double Available)?> FindOnSenderAsync(NurMarketApiClient api, string? fromBranchId, string? productId, string? barcode, string name)
    {
        // Склад отправителя: филиал — branch=<id>, главный склад — branch=main (как в BranchTransferDialog).
        async Task<List<JsonElement>> Search(string text)
        {
            var query = new Dictionary<string, string> { ["branch"] = fromBranchId ?? "main", ["page_size"] = "50", ["search"] = text.Trim() };
            var data = await api.RequestAsync(HttpMethod.Get, "api/main/products/list/", null, query, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            return BranchesWindow.Rows(data).Select(x => x.Clone()).ToList();
        }

        var candidates = new List<JsonElement>();
        if (!string.IsNullOrWhiteSpace(barcode))
            candidates.AddRange(await Search(barcode));
        if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(name))
            candidates.AddRange(await Search(name));
        var match = candidates.FirstOrDefault(p => productId is { Length: > 0 } && BranchesWindow.Str(p, "id") == productId);
        if (match.ValueKind != JsonValueKind.Object && !string.IsNullOrWhiteSpace(barcode))
            match = candidates.FirstOrDefault(p => string.Equals(BranchesWindow.Str(p, "barcode").Trim(), barcode.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match.ValueKind != JsonValueKind.Object)
            match = candidates.FirstOrDefault(p => string.Equals(BranchesWindow.Str(p, "name").Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase));
        return match.ValueKind == JsonValueKind.Object ? (BranchesWindow.Str(match, "id"), BranchesWindow.Num(match, "quantity")) : null;
    }

    private async Task CancelTransferAsync()
    {
        if (_transfer is null)
            return;
        if (ServerTransferId() is not { } serverId || _transfer.Status != StockTransferService.StatusInTransit)
        {
            ChangeStatus(StockTransferService.StatusCancelled);
            return;
        }

        if (!await PosDialogs.ConfirmYesNoModalAsync(this, T(
                $"Отменить перемещение и на сервере? Товар вернётся: {_transfer.ToPlaceName} → {_transfer.FromPlaceName}.",
                $"Жылдырууну серверде да жокко чыгарасызбы? Товар кайтат: {_transfer.ToPlaceName} → {_transfer.FromPlaceName}.",
                $"Cancel the transfer on the server too? The goods go back: {_transfer.ToPlaceName} → {_transfer.FromPlaceName}.",
                $"Transfer sunucuda da iptal edilsin mi? Ürünler geri döner: {_transfer.ToPlaceName} → {_transfer.FromPlaceName}.",
                $"Ko'chirish serverda ham bekor qilinsinmi? Mahsulot qaytadi: {_transfer.ToPlaceName} → {_transfer.FromPlaceName}.")))
            return;
        CancelTransferButton.IsEnabled = false;
        try
        {
            var body = new Dictionary<string, object?>
            {
                ["reason"] = T($"Отменено на кассе, документ {_transfer.Number}", $"Кассада жокко чыгарылды, документ {_transfer.Number}", $"Canceled at the till, document {_transfer.Number}",
                    $"Kasada iptal edildi, belge {_transfer.Number}", $"Kassada bekor qilindi, hujjat {_transfer.Number}"),
            };
            await App.GetRequiredService<NurMarketApiClient>().RequestAsync(HttpMethod.Post, $"api/main/branch-transfers/{Uri.EscapeDataString(serverId)}/cancel/", body, null,
                CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            PosLogger.Log($"Перемещение {_transfer.Number} отменено на сервере ({_transfer.Note}).", "INFO");
            ChangeStatus(StockTransferService.StatusCancelled, T("Отменено на сервере: товар вернулся отправителю.", "Серверде жокко чыгарылды: товар жөнөтүүчүгө кайтты.",
                "Canceled on the server: the goods went back to the sender.", "Sunucuda iptal edildi: ürünler gönderene döndü.", "Serverda bekor qilindi: mahsulot jo'natuvchiga qaytdi."));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Перемещение {_transfer.Number}: отмена на сервере не прошла ({ex.Message}).", "WARNING");
            PosDialogs.Warning(this, T("Не отменено на сервере: ", "Серверде жокко чыгарылган жок: ", "Not canceled on the server: ", "Sunucuda iptal edilmedi: ", "Serverda bekor qilinmadi: ")
                                     + ServerTelegramBotApi.DescribeFields(ex));
            Reload();
        }
    }
}
