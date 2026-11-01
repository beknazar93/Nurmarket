using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-10-30: режим правки накладной («Б-лайт»). Меняется только КОЛИЧЕСТВО: меньше — возврат поставщику по строке прихода, больше — доприход по тому же
// поставщику «дополнение к ПР-…»; меньше проданного нельзя. Перед сохранением — предпросмотр «до → после», причина и пароль владельца. Версии и откат — в журнале.
// Цена закупки, поставщик и оплата — серым («станет доступно после обновления сервера»). Без связи правка ставится в очередь (ничего не отправляется).
public sealed partial class InvoiceDocumentWindow
{
    private int _revertOf;
    private bool _busy;
    // 2026-11-01 (ответ Умара: PATCH suppliers/receipts/{id}/ есть на сервере): признаки сервера и несохранённые реквизиты накладной
    private ReceiptEditCaps _caps = ReceiptEditCaps.None;
    private bool _patchMode => _caps.Edit;
    private HeaderEdit? _header;

    private async Task EnterEditAsync()
    {
        ApplyAccess();
        // Начатая, но не законченная правка (оборвалась связь) — сначала довести её тем же ключом, новую не начинаем.
        // 2026-10-30 (приёмка 1.17.67, «Важно» 5): это ДО проверки окна 24/48 ч — начатое владельцем нужно уметь довести или отменить и после истечения окна
        // (раньше окно закрывалось, и недоведённая правка оставалась навсегда). Новую правку окно по-прежнему не пускает.
        if (IsOwner() && _store.Get(_r.Id).Edits.LastOrDefault(e => e.Status is "applying" or "partial") is { } unfinished)
        {
            await FinishUnfinishedAsync(unfinished).ConfigureAwait(true);
            return;
        }
        if (!_access.Allowed)
        {
            Say(ReceiptEditPolicy.DeniedText(_access));
            return;
        }
        try
        {
            using var capsCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            _caps = await new ApiReceiptEditServer(App.GetRequiredService<NurMarketApiClient>()).GetCapsAsync(capsCts.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _caps = ReceiptEditCaps.None; // признаков нет — запасной путь (возврат / доприход)
            PosLogger.Log($"Накладная: признаки редактора не получены ({ex.Message}).", "WARNING");
        }
        _header = null;
        _edit = true;
        _undo.Clear();
        _revertOf = 0;
        SwitchTable();
        var queued = _store.Queue(_r.Id).FirstOrDefault();
        if (queued is not null)
        {
            if (queued.BaseVersion == _baseVersion && queued.BaseFingerprint == _baseFingerprint)
            {
                _revertOf = queued.RevertedVersion;
                foreach (var kv in queued.NewQty)
                    if (_rows.FirstOrDefault(r => r.LineId == kv.Key) is { } row)
                        row.Qty = kv.Value;
                Say(T("Подставлена правка, поставленная в очередь без связи. Проверьте её и подтвердите паролем.", "Байланышсыз кезекке коюлган оңдоо коюлду. Текшерип, сырсөз менен ырастаңыз.",
                    "The edit queued without a connection is filled in. Review it and confirm with your password.", "Bağlantısız kuyruğa alınan düzenleme yerleştirildi. Gözden geçirip parolayla onaylayın.", "Aloqasiz navbatga qo'yilgan tahrir qo'yildi. Tekshirib parol bilan tasdiqlang."));
            }
            else
            {
                _store.Dequeue(queued.Id);
                Say(ReceiptEditService.ConflictText);
            }
        }
        else
            // 2026-11-01: цена продажи/наценка теперь пишутся в карточку товара (после подтверждения); закупка, поставщик, дата, скидка, НДС — после обновления сервера.
            Say(T("Меняйте количество, цену продажи и наценку в строках. Изменённая цена продажи запишется в карточку товара (покажем перед сохранением). Закупка, поставщик, дата, скидка и НДС правятся после обновления сервера.",
                "Саптардагы санды, сатуу баасын жана үстөктү өзгөртүңүз. Өзгөргөн сатуу баасы товардын карточкасына жазылат (сактоодон мурун көрсөтөбүз). Сатып алуу баасы, жеткирүүчү, күнү, арзандатуу жана КНС сервер жаңыргандан кийин оңделет.",
                "Change quantities, sale price and markup in the lines. A changed sale price is written to the product card (shown before saving). Purchase price, supplier, date, discount and VAT can be edited after the server update.",
                "Satırlardaki miktarı, satış fiyatını ve kârı değiştirin. Değişen satış fiyatı ürün kartına yazılır (kaydetmeden önce gösterilir). Alış fiyatı, tedarikçi, tarih, indirim ve KDV sunucu güncellemesinden sonra düzenlenir.",
                "Qatorlardagi miqdor, sotuv narxi va ustamani o'zgartiring. O'zgargan sotuv narxi mahsulot kartasiga yoziladi (saqlashdan oldin ko'rsatamiz). Xarid narxi, yetkazib beruvchi, sana, chegirma va QQS server yangilangandan keyin tahrirlanadi."));
        UpdateTitle();
        UpdateStatus();
        _strip.Child = BuildRequisites();
        BuildFooter();
        Refresh();
        UpdateBanner();
    }

    private void CancelEdit()
    {
        _rows.RemoveAll(r => r.IsPendingAdd); // 2026-11-01: добавленные в этой правке товары снимаются вместе с правкой
        _header = null;
        foreach (var r in _rows)
        {
            r.SetQtySilently(r.OrigQty);
            r.ResetPrice();
            r.ErrorText = null;
            r.Min = null;
        }
        _undo.Clear();
        _edit = false;
        _revertOf = 0;
        _chip = "all";
        SwitchTable();
        UpdateTitle();
        UpdateStatus();
        _strip.Child = BuildRequisites();
        BuildFooter();
        Refresh();
        UpdateBanner();
        Say("");
    }

    /// <summary>Режим таблицы сменился (просмотр ↔ правка): ширины колонок и шаблон строки собираются заново.</summary>
    private void SwitchTable()
    {
        FillHeader();
        _list.ItemTemplate = new FuncDataTemplate<InvoiceRow>((row, _) => row is null ? new Border() : BuildRow(row), true);
        _list.ItemsSource = null;
        _list.ItemsSource = _shown;
    }

    // ------------------------------------------------------------------ проверка → предпросмотр → проведение

    private string Operator()
    {
        var u = App.AuthApi?.UserPayload ?? default;
        return PayloadText(u, "full_name") is { Length: > 0 } n ? n : PayloadText(u, "email");
    }

    /// <summary>2026-11-01: товары, добавленные в этой правке (количество больше 0) — уйдут одним ДОПОЛНИТЕЛЬНЫМ приходом к накладной.</summary>
    private List<AddedItem> AddedItems() =>
        _rows.Where(r => r.IsPendingAdd && r.Qty > 0.0005 && r.ProductId.Length > 0).Select(r => new AddedItem(r.ProductId, r.Name, r.Unit, r.Qty, r.Purchase)).ToList();

    private static string N3(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>2026-11-01: что отправить серверным PATCH — изменённые строки (количество / закупка / цена продажи / добавить / удалить) и реквизиты накладной + «было → стало» по каждому полю для журнала.</summary>
    private PatchPlan BuildPatchPlan()
    {
        var diffs = new List<ReceiptEditAuditField>();
        var lines = new List<PatchLine>();
        ReceiptEditAuditField D(string field, InvoiceRow r, string before, string after, string? lineId = null) =>
            new() { Field = field, LineId = lineId ?? r.LineId, ProductId = r.ProductId, Subject = r.Name, Before = before, After = after };
        double before = 0, after = 0;
        foreach (var r in _rows.Where(r => !r.ReadOnlyRow))
        {
            if (r.IsPendingAdd)
            {
                if (r.Qty <= 0.0005 || r.ProductId.Length == 0)
                    continue;
                lines.Add(new PatchLine("", r.ProductId, r.Name, r.Qty, r.Purchase, r.SaleChanged && _caps.SalePrice ? r.Sale : null, false, true, Guid.NewGuid().ToString()));
                diffs.Add(D("added_item", r, "0", N3(r.Qty), "new:" + r.ProductId));
                diffs.Add(D("purchase_price", r, "—", N3(r.Purchase), "new:" + r.ProductId));
                after += ReceivingPricing.LineTotal(r.Qty, r.Purchase);
                continue;
            }
            if (r.Input is not { } input)
                continue;
            before += ReceivingPricing.LineTotal(r.OrigQty, r.OrigPurchase);
            after += ReceivingPricing.LineTotal(r.Qty, r.Purchase);
            // количество в приходе = «живёт» + возвращено − доприходы старым путём (возврат / доприход прошлых версий)
            var extra = r.OrigQty - (input.OriginalQty - input.ReturnedQty);
            if (r.Changed && r.Qty <= 0.0005 && input.ReturnedQty <= 0.0005 && extra <= 0.0005)
            {
                lines.Add(new PatchLine(r.LineId, r.ProductId, r.Name, null, null, null, true, false));
                diffs.Add(D("removed", r, N3(r.OrigQty), "0"));
                continue;
            }
            double? qty = r.Changed ? Math.Round(r.Qty - extra + input.ReturnedQty, 3) : null;
            double? price = r.PurchaseChanged ? r.Purchase : null;
            double? sale = r.SaleChanged && _caps.SalePrice ? r.Sale : null;
            if (qty is null && price is null && sale is null)
                continue;
            lines.Add(new PatchLine(r.LineId, r.ProductId, r.Name, qty, price, sale, false, false));
            if (r.Changed)
                diffs.Add(D("quantity", r, N3(r.OrigQty), N3(r.Qty)));
            if (r.PurchaseChanged)
                diffs.Add(D("purchase_price", r, N3(r.OrigPurchase), N3(r.Purchase)));
            if (sale is not null)
                diffs.Add(D("sale_price", r, N3(r.OrigSale), N3(r.Sale)));
        }
        PatchHeader? header = null;
        if (_header is { } h)
        {
            header = new PatchHeader(h.SupplierId, h.DocNumber, h.DocDate, h.Comment, h.PayType, h.Paid, h.Discount, h.DiscountPercent);
            ReceiptEditAuditField H(string field, string was, string now) => new() { Field = field, Subject = T("Накладная", "Накладной", "Invoice", "Fatura", "Yuk xati"), Before = was, After = now };
            if (h.SupplierId is not null)
                diffs.Add(H("supplier", _r.SupplierName, h.SupplierName ?? h.SupplierId));
            if (h.DocNumber is not null)
                diffs.Add(H("document_number", _r.SupplierDocNumber, h.DocNumber));
            if (h.DocDate is { } dd)
                diffs.Add(H("document_date", _r.SupplierDocDate?.ToString("dd.MM.yyyy") ?? "—", dd.ToString("dd.MM.yyyy")));
            if (h.Comment is not null)
                diffs.Add(H("comment", _r.Comment, h.Comment));
            if (h.PayType is not null)
                diffs.Add(H("payment", PayText(), h.PayType + (h.Paid is { } pd ? $" ({N3(pd)})" : "")));
            if (h.Discount is { } dsc)
                diffs.Add(H("discount", N3(_r.Discount), N3(dsc)));
            if (h.DiscountPercent is { } dsp)
                diffs.Add(H("discount", N3(_r.DiscountPercent) + " %", N3(dsp) + " %"));
        }
        return new PatchPlan(header, lines, diffs, _r.Total, Math.Round(_r.Total + after - before, 2), _r.Version, _caps.SalePrice);
    }

    /// <summary>«Реквизиты…»: поставщик, номер и дата накладной поставщика, комментарий, оплата, скидка — уйдут вместе с остальной правкой одним PATCH.</summary>
    private async Task EditHeaderAsync()
    {
        if (!_edit || !_patchMode)
            return;
        var dialog = new InvoiceHeaderDialog(_r, new SupplierApiService(App.GetRequiredService<NurMarketApiClient>()), _caps, _header);
        await PosDialogHost.ShowAsync(dialog, this).ConfigureAwait(true);
        if (!dialog.Saved)
            return;
        _header = dialog.Result;
        Say(_header is null ? T("Реквизиты не менялись.", "Реквизиттер өзгөргөн жок.", "Details unchanged.", "Bilgiler değişmedi.", "Rekvizitlar o'zgarmadi.")
            : T("Реквизиты накладной изменены — они уйдут при сохранении правки.", "Накладнойдун реквизиттери өзгөрдү — оңдоону сактаганда жөнөтүлөт.", "Invoice details changed — they are sent when the edit is saved.", "Fatura bilgileri değişti — düzenleme kaydedilince gönderilir.", "Yuk xati rekvizitlari o'zgardi — tahrir saqlanganda yuboriladi."));
    }

    /// <summary>«+ Добавить товар»: поиск → количество и закупка → строка в таблице. Уже есть в накладной — просто «+» к его строке.</summary>
    private async Task AddItemAsync()
    {
        if (!_edit || _busy)
            return;
        var dialog = new InvoiceAddItemDialog(_docNumber, _supplier?.Name ?? _r.SupplierName, _patchMode);
        await PosDialogHost.ShowAsync(dialog, this).ConfigureAwait(true);
        if (dialog.Result is not { } pick)
            return;
        var existing = _rows.FirstOrDefault(r => !r.ReadOnlyRow && string.Equals(r.ProductId, pick.ProductId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Qty += pick.Qty;
            _chip = "all";
            _search.Text = "";
            Refresh();
            _list.SelectedItem = existing;
            _list.ScrollIntoView(existing);
            Say(T($"«{existing.Name}» уже есть в накладной: количество увеличено до {existing.QtyText} (доприход).", $"«{existing.Name}» накладнойдо бар: саны {existing.QtyText} болуп көбөйдү (кошумча кирүү).",
                $"“{existing.Name}” is already in the invoice: the quantity is raised to {existing.QtyText} (additional receipt).", $"«{existing.Name}» faturada zaten var: miktar {existing.QtyText} oldu (ek giriş).", $"«{existing.Name}» yuk xatida bor: miqdor {existing.QtyText} ga oshirildi (qo'shimcha kirim)."));
            return;
        }
        var row = new InvoiceRow(0, pick.Sale)
        {
            No = _rows.Count + 1, LineId = "new:" + pick.ProductId, ProductId = pick.ProductId, Code = pick.Code, Barcode = pick.Barcode, Name = pick.Name, Unit = pick.Unit,
            Purchase = pick.Price, IsPendingAdd = true, IsNew = true,
        };
        row.QtyEdited += OnQtyEdited;
        row.PriceEdited += _ => Recalc();
        _rows.Add(row);
        row.Qty = pick.Qty;
        _chip = "all";
        _search.Text = "";
        Refresh();
        _list.SelectedItem = row;
        _list.ScrollIntoView(row);
        Say(_patchMode
            ? T($"«{pick.Name}» добавлен в правку. При сохранении он будет добавлен в эту накладную отдельной строкой.", $"«{pick.Name}» оңдоого кошулду. Сактаганда ал ушул накладнойго өзүнчө сап болуп кошулат.",
                $"“{pick.Name}” is added to the edit. Saving adds it to this invoice as a separate line.", $"«{pick.Name}» düzenlemeye eklendi. Kaydedince bu faturaya ayrı bir satır olarak eklenir.", $"«{pick.Name}» tahrirga qo'shildi. Saqlaganda u shu yuk xatiga alohida qator bo'lib qo'shiladi.")
            : T($"«{pick.Name}» добавлен в правку. При сохранении будет создан дополнительный приход к этой накладной.", $"«{pick.Name}» оңдоого кошулду. Сактаганда ушул накладнойго кошумча кирүү түзүлөт.",
                $"“{pick.Name}” is added to the edit. Saving creates an additional receipt for this invoice.", $"«{pick.Name}» düzenlemeye eklendi. Kaydedince bu faturaya ek giriş oluşturulur.", $"«{pick.Name}» tahrirga qo'shildi. Saqlaganda shu yuk xatiga qo'shimcha kirim yaratiladi."));
    }

    /// <summary>2026-11-01: изменения цены продажи / наценки по строкам (по одному на товар) — уходят шагами «price» (PATCH товара). «Было» — из формы, при отправке уточняется по серверу.</summary>
    private List<PriceEdit> PriceEdits() =>
        _rows.Where(r => r.SaleChanged && r.ProductId.Length > 0).GroupBy(r => r.ProductId, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())
            .Select(r => new PriceEdit(r.ProductId, r.Name, r.Purchase, r.OrigSale > 0 ? r.OrigSale : null, r.Sale,
                r.Purchase > 0 && r.OrigSale > 0 ? InvoiceMath.MarkupFromSale(r.Purchase, r.OrigSale) : null, r.Purchase > 0 ? InvoiceMath.MarkupFromSale(r.Purchase, r.Sale) : null)).ToList();

    /// <summary>2026-11-01: id пользователя из профиля (число или строка) — в журнал правок вместе с именем и ролью.</summary>
    private static string UserId()
    {
        var u = App.AuthApi?.UserPayload ?? default;
        if (u.ValueKind != JsonValueKind.Object)
            return "";
        foreach (var holder in new[] { u }.Concat(new[] { "user", "profile", "data" }.Select(n => u.TryGetProperty(n, out var h) && h.ValueKind == JsonValueKind.Object ? h : default)))
            if (holder.ValueKind == JsonValueKind.Object && holder.TryGetProperty("id", out var id))
                return id.ValueKind switch { JsonValueKind.String => id.GetString() ?? "", JsonValueKind.Number => id.GetRawText(), _ => "" };
        return "";
    }

    private static string PayloadText(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return "";
        if (payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        foreach (var nest in new[] { "user", "profile", "data" })
            if (payload.TryGetProperty(nest, out var n) && n.ValueKind == JsonValueKind.Object && n.TryGetProperty(name, out var vv) && vv.ValueKind == JsonValueKind.String)
                return vv.GetString() ?? "";
        return "";
    }

    private async Task<Dictionary<string, double>> LoadStocksAsync(IEnumerable<string> productIds)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(4);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await Task.WhenAll(productIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(async id =>
        {
            await gate.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                if (await PosApp.CatalogApi.ProductsDetailAsync(id, cts.Token).ConfigureAwait(false) is { } d && d.ValueKind == JsonValueKind.Object)
                    lock (result)
                        result[id] = SupplierApiService.Num(d, "quantity");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Накладная: остаток товара {id} не получен ({ex.Message}).", "WARNING");
            }
            catch (OperationCanceledException)
            {
                // остаток не получен — уменьшение этой строки будет закрыто с понятной причиной
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(true);
        return result;
    }

    private async Task ReviewAsync()
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            _list.Focus(); // потеря фокуса ячейкой записывает введённое значение в строку
            await Task.Delay(30).ConfigureAwait(true);
            ApplyAccess();
            if (!_access.Allowed)
            {
                Say(ReceiptEditPolicy.DeniedText(_access));
                BuildFooter();
                return;
            }
            var changed = _rows.Where(r => r.Changed && r.Input is not null).ToList(); // 2026-11-01: добавляемые товары (Input нет) идут отдельным списком
            var priced = _patchMode && _caps.SalePrice ? new List<PriceEdit>() : PriceEdits(); // с receipt_edit_sale_price цена продажи едет в строке PATCH
            var addedRows = AddedItems();
            var patch = _patchMode ? BuildPatchPlan() : null;
            var patchExtra = patch is not null && (patch.Header is not null || patch.Lines.Count > 0);
            if (changed.Count == 0 && priced.Count == 0 && addedRows.Count == 0 && !patchExtra)
            {
                Say(T("Изменений нет — поменяйте количество или цену продажи хотя бы в одной строке.", "Өзгөртүү жок — жок дегенде бир саптагы санды же сатуу баасын өзгөртүңүз.", "No changes — change the quantity or the sale price in at least one line.", "Değişiklik yok — en az bir satırda miktarı veya satış fiyatını değiştirin.", "O'zgarish yo'q — kamida bitta qatorda miqdor yoki sotuv narxini o'zgartiring."));
                return;
            }
            Say(T("Проверяю остатки на складе…", "Кампадагы калдыктарды текшерүүдө…", "Checking stock…", "Stok kontrol ediliyor…", "Ombor qoldig'i tekshirilmoqda…"));
            var stocks = await LoadStocksAsync(changed.Where(r => r.Qty < r.OrigQty).Select(r => r.ProductId)).ConfigureAwait(true);
            // 2026-10-30 (приёмка 1.17.66, 3г): продажи, ещё не отправленные из офлайн-очереди этой кассы, сервер не знает — доступное количество меньше на их сумму.
            var pendingSales = ReceiptEditService.PendingOfflineSales();
            foreach (var id in stocks.Keys.ToList())
                if (pendingSales.TryGetValue(id, out var queuedQty) && queuedQty > 0)
                {
                    PosLogger.Log($"Накладная: остаток товара {id} на сервере {stocks[id]:0.###}, в офлайн-очереди {queuedQty:0.###} — для правки учитывается остаток без них.", "STOCK");
                    stocks[id] -= queuedQty;
                }
            foreach (var r in _rows.Where(r => r.Input is not null))
                if (stocks.TryGetValue(r.ProductId, out var s))
                {
                    r.Stock = s;
                    r.Input = r.Input! with { Stock = s };
                }
                else if (changed.Contains(r))
                    r.Input = r.Input! with { Stock = null }; // остаток не получен — уменьшать нельзя
            var plan = ReceiptEditPlanner.Build(_rows.Where(r => r.Input is not null).Select(r => r.Input!).ToList(), changed.ToDictionary(r => r.LineId, r => r.Qty), _patchMode ? ReceiptPayKind.Cash : PayForEdit); // при PATCH деньги считает сервер: проверка «доприход для способа оплаты» не нужна
            foreach (var lp in plan.Lines)
            {
                var row = _rows.First(r => r.LineId == lp.Input.LineId);
                row.Min = lp.MinQty > 0 ? lp.MinQty : null;
                row.ErrorText = lp.Error;
            }
            Refresh();
            if (!plan.IsValid)
            {
                _chip = "error";
                ApplyFilter();
                Say(string.Join("\n", plan.Errors.Take(4)));
                return;
            }
            Say("");

            var dialog = new InvoiceEditPreviewDialog(plan, PayForEdit, _docNumber, _r, _supplier?.Name ?? _r.SupplierName, _baseVersion, _revertOf > 0, VerifyOwnerPasswordAsync,
                priced.Select(p => ReceiptEditAuditLog.FieldText(new ReceiptEditAuditField { Field = "sale_price", Subject = p.Name, Before = p.SaleBefore?.ToString("0.##") ?? "?", After = p.SaleAfter.ToString("0.##") })
                    + (p.MarkupAfter is { } mk ? $" ({T("наценка", "үстөк", "markup", "kâr", "ustama")} {p.MarkupBefore?.ToString("0.#") ?? "?"} → {mk:0.#} %)" : "")).Concat(patch is null ? Array.Empty<string>() : patch.Diffs.Where(d => d.Field is not ("quantity" or "removed" or "added_item")).Select(ReceiptEditAuditLog.FieldText)).ToList(), addedRows, patch);
            await PosDialogHost.ShowAsync(dialog, this).ConfigureAwait(true);
            if (dialog.Result is not { } result)
                return;
            if (result.Queue && (priced.Count > 0 || addedRows.Count > 0 || patchExtra))
            {
                // 2026-11-01: без связи в очередь встают только количества; цена продажи — PATCH товара, а новый товар — дополнительный приход: их вслепую не ставим.
                Say(T("Без связи цену продажи и добавление товара сохранить нельзя: уберите их из правки или повторите, когда появится связь.", "Байланышсыз сатуу баасын жана товар кошууну сактоого болбойт: аларды оңдоодон алып салыңыз же байланыш пайда болгондо кайталаңыз.",
                    "The sale price and adding a product can't be saved offline: remove them from the edit or retry when the connection is back.", "Bağlantı yokken satış fiyatı ve ürün ekleme kaydedilemez: bunları düzenlemeden çıkarın veya bağlantı gelince tekrar deneyin.", "Aloqasiz sotuv narxi va mahsulot qo'shishni saqlab bo'lmaydi: ularni tahrirdan olib tashlang yoki aloqa tiklanganda qayta urining."));
                return;
            }
            if (result.Queue)
            {
                _store.Enqueue(new QueuedEdit
                {
                    ReceiptId = _r.Id, BaseVersion = _baseVersion, BaseFingerprint = _baseFingerprint, CreatedAt = DateTime.Now, By = Operator(), Reason = result.Reason,
                    Compensation = result.Compensation, NewQty = changed.ToDictionary(r => r.LineId, r => r.Qty), RevertedVersion = _revertOf,
                });
                PosLogger.Log($"AUDIT: правка накладной {_docNumber} поставлена в очередь без связи ({changed.Count} строк).", "INFO");
                // 2026-11-01: правка без связи — тоже в ленту purchase-edits.jsonl (кто, что было → стало, причина), чтобы она не терялась.
                ReceiptEditAuditLog.WriteQueued(_store.Queue(_r.Id).LastOrDefault() ?? new QueuedEdit { ReceiptId = _r.Id, By = Operator(), Reason = result.Reason, CreatedAt = DateTime.Now }, _docNumber, _r.SupplierName, _r.CreatedAt,
                    UserId(), PayloadText(App.AuthApi?.UserPayload ?? default, "role"),
                    changed.Select(r => new ReceiptEditAuditLine { Product = r.Name, ProductId = r.ProductId, Unit = r.Unit, LineId = r.LineId, QtyBefore = r.OrigQty, QtyAfter = r.Qty, Price = r.Purchase }).ToList());
                CancelEdit();
                UpdateStatus();
                UpdateBanner();
                Say(T("Правка поставлена в очередь: связи нет. Откройте накладную, когда связь появится, и подтвердите её паролем.", "Оңдоо кезекке коюлду: байланыш жок. Байланыш пайда болгондо накладнойду ачып, сырсөз менен ырастаңыз.",
                    "The edit is queued: there is no connection. Open the invoice when it is back and confirm with your password.", "Düzenleme kuyruğa alındı: bağlantı yok. Bağlantı gelince faturayı açıp parolayla onaylayın.", "Tahrir navbatga qo'yildi: aloqa yo'q. Aloqa tiklangach yuk xatini ochib parol bilan tasdiqlang."));
                return;
            }
            await ApplyEditAsync(plan, result).ConfigureAwait(true);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>2026-10-30 (приёмка 1.17.66, 3ж): проверка пароля с таймаутом 8 с и без блокировки окна; не успели / нет связи — null (правка ставится в очередь).</summary>
    private async Task<bool?> VerifyOwnerPasswordAsync(string password)
    {
        var email = PayloadText(App.AuthApi?.UserPayload ?? default, "email");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            return await App.GetRequiredService<NurMarketApiClient>().VerifyPasswordAsync(email, password, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Чем оплачивать доприход в этой правке: у обычной правки — способ оплаты прихода, у отката — по возмещению откатываемой версии (без денег, которых не было).</summary>
    private ReceiptPayKind PayForEdit =>
        _revertOf > 0 && _store.Get(_r.Id).Edits.LastOrDefault(e => e.Version == _revertOf && e.Status == "done") is { } target ? ReceiptEditService.RevertPay(target, _pay) : _pay;

    private async Task<string?> MainCashboxIdAsync()
    {
        try
        {
            var data = await App.GetRequiredService<NurMarketApiClient>().ConstructionCashboxesListAsync().ConfigureAwait(true);
            var rows = (data.ValueKind == JsonValueKind.Array ? data.EnumerateArray() : data.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray() : Enumerable.Empty<JsonElement>()).ToList();
            var main = rows.FirstOrDefault(x => SupplierApiService.Str(x, "name").Contains("Основн", StringComparison.OrdinalIgnoreCase))
                       is { ValueKind: JsonValueKind.Object } byName ? byName
                : rows.FirstOrDefault(x => string.Equals(SupplierApiService.Str(x, "role"), "pos_main", StringComparison.OrdinalIgnoreCase) || string.Equals(SupplierApiService.Str(x, "cashbox_role"), "pos_main", StringComparison.OrdinalIgnoreCase));
            return main.ValueKind == JsonValueKind.Object ? SupplierApiService.Str(main, "id") : null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Накладная: кассы не получены ({ex.Message}) — сервер возьмёт свою.", "WARNING");
            return null;
        }
    }

    private ReceiptEditService NewService() => new(new ApiReceiptEditServer(App.GetRequiredService<NurMarketApiClient>()), _store, () => ReceiptEditPolicy.ServerNow);

    private async Task ApplyEditAsync(ReceiptEditPlan plan, PreviewResult result)
    {
        Say(T("Сохраняю правку…", "Оңдоо сакталууда…", "Saving the edit…", "Düzenleme kaydediliyor…", "Tahrir saqlanmoqda…"));
        var cashbox = result.Compensation == "cash" && plan.Returns.Count > 0 ? await MainCashboxIdAsync().ConfigureAwait(true) : null;
        var req = new EditRequest(_r, _docNumber, _pay, _rows.Where(r => r.Input is not null).Select(r => r.Input!).ToList(), _rows.Where(r => r.Changed && r.Input is not null).ToDictionary(r => r.LineId, r => r.Qty),
            result.Reason, Operator(), Environment.MachineName, result.Compensation, cashbox, IsOwner(), ReceiptEditPolicy.Current, _baseFingerprint, _baseVersion, _revertOf, InQueue(), _r.SupplierName,
            UserId(), PayloadText(App.AuthApi?.UserPayload ?? default, "role"), _patchMode && _caps.SalePrice ? null : PriceEdits(), _patchMode ? null : AddedItems(), _patchMode ? BuildPatchPlan() : null);
        var res = await NewService().ApplyAsync(req).ConfigureAwait(true);
        if (res.Ok)
        {
            foreach (var q in _store.Queue(_r.Id))
                _store.Dequeue(q.Id);
            SuccessToast.Show(T($"Приход исправлен, версия {res.Version}", $"Кирүү оңдолду, версия {res.Version}", $"Receipt corrected, version {res.Version}", $"Giriş düzeltildi, sürüm {res.Version}", $"Kirim tuzatildi, versiya {res.Version}"));
            Action = "edited"; // окно закроется, список «Закупок» перечитает приход и откроет накладную заново
            _closeConfirmed = true;
            Close();
            return;
        }
        if (res.Conflict)
        {
            Say(res.Error ?? ReceiptEditService.ConflictText);
            if (_patchMode)
            {
                // 2026-11-01: накладную изменили (версия на сервере другая) — показываем причину, закрываем и открываем заново с актуальными данными
                await Task.Delay(2500).ConfigureAwait(true);
                Action = "edited";
                _closeConfirmed = true;
                Close();
            }
            return;
        }
        if (res.Partial || res.Record is { Status: "applying" })
        {
            Say(T("Правка выполнена не полностью: ", "Оңдоо толук аткарылган жок: ", "The edit was not completed: ", "Düzenleme tamamlanmadı: ", "Tahrir to'liq bajarilmadi: ") + res.Error + "\n"
                + T("Закройте накладную, откройте снова и нажмите «Изменить» → довести правку: повтор пойдёт тем же ключом, ничего не задвоится.",
                    "Накладнойду жаап, кайра ачып «Өзгөртүү» басыңыз: кайталоо ошол эле ачкыч менен өтөт, эч нерсе эки эсе болбойт.",
                    "Close the invoice, open it again and press “Edit” to finish the edit: the retry goes with the same key and nothing is doubled.",
                    "Faturayı kapatıp yeniden açın ve «Düzenle»ye basın: tekrar aynı anahtarla gider, hiçbir şey ikilenmez.", "Yuk xatini yopib qayta oching va «O'zgartirish»ni bosing: takror o'sha kalit bilan ketadi, hech narsa ikkilanmaydi."));
            return;
        }
        Say(res.Error ?? "");
    }

    /// <summary>2026-10-30 (приёмка 1.17.67, «Важно» 5): отказаться от недоведённой правки — с паролем владельца. Касса сначала проверяет по серверу, что шаги не проведены;
    /// проверить не вышло — отказ только после подтверждения «я проверил вручную».</summary>
    private async Task OfferAbandonAsync(ReceiptEditRecord unfinished)
    {
        var done = unfinished.Steps.Count(s => s.Status == "done");
        var go = await PosConfirmDialog.ShowModalAsync(this, T("Отказаться от правки?", "Оңдоодон баш тартасызбы?", "Abandon the edit?", "Düzenlemeden vazgeçilsin mi?", "Tahrirdan voz kechilsinmi?"),
            T($"Правка v{unfinished.Version} останется недоведённой ({done} из {unfinished.Steps.Count} шагов проведено). Уже проведённое на сервере не отменяется; остальные шаги закроются, накладную можно будет править заново. Касса сверится с сервером, что оставшиеся шаги не проведены.",
                $"v{unfinished.Version} оңдоосу аягына чыкпай калат ({done}/{unfinished.Steps.Count} кадам өткөрүлгөн). Серверде өткөрүлгөн жокко чыгарылбайт; калган кадамдар жабылат, накладнойду кайра оңдосо болот. Касса калган кадамдар өткөрүлбөгөнүн серверден текшерет.",
                $"Edit v{unfinished.Version} will stay unfinished ({done} of {unfinished.Steps.Count} steps posted). What is already posted on the server is not undone; the remaining steps are closed and the invoice can be edited again. The till checks with the server that the remaining steps were not posted.",
                $"v{unfinished.Version} düzenlemesi yarım kalacak ({done}/{unfinished.Steps.Count} adım işlendi). Sunucuda işlenenler geri alınmaz; kalan adımlar kapatılır, fatura yeniden düzenlenebilir. Kasa kalan adımların işlenmediğini sunucudan doğrular.",
                $"v{unfinished.Version} tahriri tugallanmay qoladi ({done}/{unfinished.Steps.Count} qadam o'tkazilgan). Serverda o'tkazilgani bekor qilinmaydi; qolgan qadamlar yopiladi, yuk xatini qayta tahrirlash mumkin. Kassa qolgan qadamlar o'tkazilmaganini serverdan tekshiradi."),
            T("Отказаться от правки", "Оңдоодон баш тартуу", "Abandon the edit", "Düzenlemeden vazgeç", "Tahrirdan voz kechish"), T("Оставить", "Калтыруу", "Keep it", "Kalsın", "Qoldirish"), PosConfirmAccent.Danger).ConfigureAwait(true);
        if (!go)
            return;
        var pwd = new OwnerPasswordDialog(T("Подтверждение владельца", "Ээсинин ырастоосу", "Owner confirmation", "Sahip onayı", "Egasi tasdig'i"),
            T("Введите пароль владельца NurCRM.", "NurCRM ээсинин сырсөзүн киргизиңиз.", "Enter the NurCRM owner's password.", "NurCRM sahibinin parolasını girin.", "NurCRM egasi parolini kiriting."), VerifyOwnerPasswordAsync);
        await PosDialogHost.ShowAsync(pwd, this).ConfigureAwait(true);
        if (!pwd.Confirmed)
            return;
        Say(T("Проверяю шаги на сервере…", "Кадамдарды серверден текшерүүдө…", "Checking the steps on the server…", "Adımlar sunucuda kontrol ediliyor…", "Qadamlar serverda tekshirilmoqda…"));
        var service = NewService();
        var res = await service.AbandonAsync(_r, _docNumber, manuallyChecked: false).ConfigureAwait(true);
        if (res is { Ok: false, NeedsManualCheck: true })
        {
            var sure = await PosConfirmDialog.ShowModalAsync(this, T("Проверить не удалось", "Текшерүү мүмкүн болгон жок", "Could not verify", "Doğrulanamadı", "Tekshirib bo'lmadi"), res.Error ?? "",
                T("Проверил вручную — отказаться", "Кол менен текшердим — баш тартуу", "Checked by hand — abandon", "Elle kontrol ettim — vazgeç", "Qo'lda tekshirdim — voz kechish"), T("Оставить", "Калтыруу", "Keep it", "Kalsın", "Qoldirish"), PosConfirmAccent.Danger).ConfigureAwait(true);
            if (!sure)
            {
                Say(res.Error ?? "");
                return;
            }
            res = await service.AbandonAsync(_r, _docNumber, manuallyChecked: true).ConfigureAwait(true);
        }
        if (!res.Ok)
        {
            Say(res.Error ?? "");
            return;
        }
        SuccessToast.Show(T("Правка прекращена", "Оңдоо токтотулду", "Edit abandoned", "Düzenleme sonlandırıldı", "Tahrir to'xtatildi"));
        Action = "edited"; // окно закроется, список «Закупок» перечитает приход и откроет накладную заново
        _closeConfirmed = true;
        Close();
    }

    private async Task FinishUnfinishedAsync(ReceiptEditRecord unfinished)
    {
        var ok = await PosConfirmDialog.ShowModalAsync(this, T("Довести правку?", "Оңдоону аягына чыгаруу керекпи?", "Finish the edit?", "Düzenleme tamamlansın mı?", "Tahrir tugatilsinmi?"),
            T($"Правка v{unfinished.Version} выполнена не полностью ({unfinished.Steps.Count(s => s.Status == "done")} из {unfinished.Steps.Count} шагов). Оставшиеся шаги уйдут тем же ключом — ничего не задвоится.",
                $"v{unfinished.Version} оңдоосу толук аткарылган эмес. Калган кадамдар ошол эле ачкыч менен кетет — эч нерсе эки эсе болбойт.",
                $"Edit v{unfinished.Version} is incomplete. The remaining steps go with the same key — nothing is doubled.", $"v{unfinished.Version} düzenlemesi tamamlanmadı. Kalan adımlar aynı anahtarla gider — hiçbir şey ikilenmez.", $"v{unfinished.Version} tahriri tugallanmagan. Qolgan qadamlar o'sha kalit bilan ketadi."),
            T("Довести", "Аягына чыгаруу", "Finish", "Tamamla", "Tugatish"), T("Не сейчас", "Азыр эмес", "Not now", "Şimdi değil", "Hozir emas")).ConfigureAwait(true);
        if (!ok)
        {
            // 2026-10-30 (приёмка 1.17.67, «Важно» 5): не доводить — значит либо оставить на потом, либо отказаться от правки совсем (иначе она висит навсегда).
            await OfferAbandonAsync(unfinished).ConfigureAwait(true);
            return;
        }
        // 2026-10-30 (приёмка 1.17.66, 3ж): проверка пароля — асинхронно, с таймаутом 8 с и индикатором (раньше .GetResult() замораживал окно до 55 с и писал «пароль неверный»).
        var pwd = new OwnerPasswordDialog(T("Подтверждение владельца", "Ээсинин ырастоосу", "Owner confirmation", "Sahip onayı", "Egasi tasdig'i"),
            T("Введите пароль владельца NurCRM.", "NurCRM ээсинин сырсөзүн киргизиңиз.", "Enter the NurCRM owner's password.", "NurCRM sahibinin parolasını girin.", "NurCRM egasi parolini kiriting."), VerifyOwnerPasswordAsync);
        await PosDialogHost.ShowAsync(pwd, this).ConfigureAwait(true);
        if (!pwd.Confirmed)
            return;
        var cashbox = unfinished.Compensation == "cash" ? await MainCashboxIdAsync().ConfigureAwait(true) : null;
        var res = await NewService().ResumeAsync(_r, _docNumber, _pay, unfinished.Compensation, cashbox, _r.SupplierName).ConfigureAwait(true);
        if (res.Ok)
        {
            SuccessToast.Show(T($"Правка доведена, версия {res.Version}", $"Оңдоо аягына чыкты, версия {res.Version}", $"Edit finished, version {res.Version}", $"Düzenleme tamamlandı, sürüm {res.Version}", $"Tahrir tugatildi, versiya {res.Version}"));
            Action = "edited";
            _closeConfirmed = true;
            Close();
        }
        else
            Say(res.Error ?? "");
    }

    // ------------------------------------------------------------------ история версий и откат

    private async Task ShowHistoryAsync()
    {
        var canRevert = _access.Allowed && IsOwner() && !_edit;
        // 2026-11-01: серверная история правок (GET suppliers/receipts/{id}/history/) показывается вместе с локальным журналом; не отдаёт — только локальный
        List<ServerEditVersion>? server = null;
        try
        {
            using var hCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            server = await new SupplierApiService(App.GetRequiredService<NurMarketApiClient>()).GetReceiptHistoryAsync(_r.Id, hCts.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Накладная {_docNumber}: серверная история правок не получена ({ex.Message}).", "INFO");
        }
        var dialog = new InvoiceHistoryDialog(_r, _docNumber, _store.Get(_r.Id), canRevert, _showMoney, server);
        await PosDialogHost.ShowAsync(dialog, this).ConfigureAwait(true);
        if (dialog.RevertVersion is not { } v)
            return;
        // 2026-10-30 (приёмка 1.17.66, 3б): откатываем проведённую версию (done), а не отклонённую попытку с тем же номером.
        var rec = _store.Get(_r.Id).Edits.LastOrDefault(e => e.Version == v && e.Status == "done");
        if (rec is null)
            return;
        if (ReceiptEditService.RevertBlocked(rec))
        {
            Say(ReceiptEditService.RevertBlockedText);
            return;
        }
        // Откат — это новая правка с составом версии до выбранной: то же окно времени, предпросмотр, причина и пароль.
        await EnterEditAsync().ConfigureAwait(true);
        if (!_edit)
            return;
        _revertOf = v;
        foreach (var line in rec.Lines)
            if (_rows.FirstOrDefault(r => r.LineId == line.LineId) is { } row)
                row.Qty = line.QtyBefore;
        Say(T($"Откат версии {v}: количества возвращены к составу до этой правки. Нажмите «Проверить →».", $"{v}-версияны кайтаруу: сандар ушул оңдоодон мурунку абалга кайтарылды. «Текшерүү →» басыңыз.",
            $"Rolling back version {v}: quantities are set to the state before that edit. Press “Review →”.", $"{v}. sürümü geri alma: miktarlar o düzenlemeden önceki duruma getirildi. «Gözden geçir →»ye basın.", $"{v}-versiyani qaytarish: miqdorlar shu tahrirdan oldingi holatga qaytarildi. «Tekshirish →»ni bosing."));
    }
}
