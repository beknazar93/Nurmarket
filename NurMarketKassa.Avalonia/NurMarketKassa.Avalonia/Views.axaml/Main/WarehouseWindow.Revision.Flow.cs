using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-06: В15г — часть класса WarehouseWindow (Flow): перенесено из WarehouseWindow.Revision.cs без изменений логики.
public partial class WarehouseWindow
{
    // ------------------------------------------------------------------ текущая строка и карточка

    private void RevisionListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_rev is null || !e.GetCurrentPoint(_revList).Properties.IsLeftButtonPressed)
            return;
        var view = (e.Source as Visual)?.FindAncestorOfType<RevisionRowView>(true);
        if (view is null)
            return;
        var row = view.Row;
        // После нажатия список сам забирает фокус на строку — курсор в «Факт» ставим следующим тактом.
        Dispatcher.UIThread.Post(() =>
        {
            if (_rev is not null)
                RevisionSetCurrent(row, focusFact: true);
        }, DispatcherPriority.Input);
    }

    private void RevisionSetCurrent(RevisionRow? row, bool focusFact, string? note = null)
    {
        if (!ReferenceEquals(row, _revCurrent))
            RevisionCommitPending();
        _revCurrent = row;
        if (row is not null)
        {
            RevisionEnsureVisible(row);
            _revSuppressSel = true;
            _revList.SelectedItem = row;
            _revSuppressSel = false;
            // Строка не у самого края: сначала подводим строку чуть ниже неё, потом её саму (виден «контекст», а не последняя строка у края).
            var at = _revView.IndexOf(row);
            var visibleRows = (int)(_revList.Bounds.Height / RevisionRowView.RowHeight);
            var ahead = Math.Min(3, visibleRows - 2);
            if (at >= 0 && ahead > 0 && at + ahead < _revView.Count)
                _revList.ScrollIntoView(at + ahead);
            _revList.ScrollIntoView(at >= 0 ? at : 0);
        }

        RevisionUpdateCard(note);
        if (focusFact && row is { IsReadOnly: false })
        {
            _revFactBox.Focus();
            _revFactBox.SelectAll();
        }
    }

    private static string RevisionNumberText(double v) => v.ToString("0.###", CultureInfo.CurrentCulture);

    private void RevisionUpdateCard(string? note)
    {
        var r = _revCurrent;
        if (r is null)
        {
            _revCardName.Text = RevT("Пикните товар или выберите строку в списке", "Товарды сканерлеңиз же тизмеден саптны тандаңыз", "Scan a product or pick a line in the list", "Ürünü okutun veya listeden bir satır seçin", "Mahsulotni skanerlang yoki ro'yxatdan qatorni tanlang");
            _revCardMeta.Text = "";
            _revCardNote.Text = "";
            _revCardBook.Text = "";
            _revCardDiff.Text = "";
            _revCardCost.Text = "";
            _revCardMoney.Text = "";
            _revFactBox.Text = "";
            _revFactInitial = "";
            _revFactBox.IsEnabled = false;
            _revEqualButton.IsEnabled = false;
            _revResetButton.IsEnabled = false;
            return;
        }

        var pcs = string.IsNullOrWhiteSpace(r.Unit) ? RevT("шт", "дана", "pcs", "adet", "dona") : r.Unit;
        _revCardName.Text = r.Name;
        _revCardMeta.Text = string.Join("  ·  ", new[] { r.Barcode, r.Article, r.Category }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var book = r.IsCounted ? r.ExpectedAtCount : r.Expected;
        _revCardBook.Text = ReportFormat.Quantity(book) + " " + pcs;
        _revFactBox.IsEnabled = !r.IsReadOnly && !(_rev?.IsLocked ?? true);
        _revFactBox.Text = r.Fact is { } f ? RevisionNumberText(f) : "";
        _revFactInitial = _revFactBox.Text ?? "";
        _revEqualButton.IsEnabled = _revFactBox.IsEnabled;
        _revResetButton.IsEnabled = _revFactBox.IsEnabled && r.IsCounted;
        if (note is null)
        {
            if (r.IsReadOnly)
                note = RevT("Товар с размерами — пересчитайте по размерам в карточке товара.", "Өлчөмдүү товар — товардын карточкасында өлчөмдөр боюнча кайра санаңыз.",
                    "Product with sizes — count by size in the product card.", "Bedenli ürün — ürün kartında bedenlere göre sayın.", "O'lchamli mahsulot — mahsulot kartochkasida o'lchamlar bo'yicha sanang.");
            else if (r.IsCounted)
                note = RevT($"Уже посчитано: {ReportFormat.Quantity(r.Fact ?? 0)} ({r.CountedAt:HH:mm})", $"Мурун саналган: {ReportFormat.Quantity(r.Fact ?? 0)} ({r.CountedAt:HH:mm})",
                    $"Already counted: {ReportFormat.Quantity(r.Fact ?? 0)} ({r.CountedAt:HH:mm})", $"Zaten sayıldı: {ReportFormat.Quantity(r.Fact ?? 0)} ({r.CountedAt:HH:mm})", $"Allaqachon sanalgan: {ReportFormat.Quantity(r.Fact ?? 0)} ({r.CountedAt:HH:mm})");
            else
                note = CycleCountStore.HistoryText(r.ProductId); // 2026-11-05 (функция 9): «Считали: 12.10.2026 (−2, порча)»
        }

        _revCardNote.Text = note;
        RevisionUpdateLiveDiff();
    }

    /// <summary>«Разница» в карточке считается на лету по тому, что набрано в «Факт» (число заменяет, «+3» прибавляет).</summary>
    private void RevisionUpdateLiveDiff()
    {
        var r = _revCurrent;
        if (r is null)
            return;
        double? fact = r.Fact;
        var text = _revFactBox.Text?.Trim() ?? "";
        if (text.Length > 0 && text != _revFactInitial.Trim() && RevisionSession.TryParseEntry(text, out var v, out var add))
            fact = add ? (r.Fact ?? 0) + v : v;
        // 2026-10-29: закупка за единицу и сумма разницы (разница × закупка; закупки нет — по цене продажи, пометка «*»).
        _revCardCost.Text = r.HasPurchase ? ReportFormat.Money(r.PurchasePrice) : "—";
        _revCardCost.Bind(TextBlock.ForegroundProperty, _revCardCost.GetResourceObservable(r.HasPurchase ? "BrushText" : "BrushTextSoft"));
        if (fact is not { } fv)
        {
            _revCardDiff.Text = "—";
            _revCardDiff.Foreground = null;
            _revCardMoney.Text = "—";
            _revCardMoney.Foreground = null;
            ToolTip.SetTip(_revCardMoney, null);
            return;
        }

        var d = Math.Round(fv - (r.IsCounted ? r.ExpectedAtCount : r.Expected), 3);
        _revCardDiff.Text = d > 0 ? "+" + ReportFormat.Quantity(d) : ReportFormat.Quantity(d);
        var key = d < -0.0005 ? "BrushDanger" : d > 0.0005 ? "BrushSuccess" : "BrushText";
        _revCardDiff.Bind(TextBlock.ForegroundProperty, _revCardDiff.GetResourceObservable(key));
        if (RevisionRowView.DiffMoney(r, d) is { } m)
        {
            var star = m.Fallback && Math.Abs(m.Amount) > 0.004;
            _revCardMoney.Text = RevisionRowView.SignedMoney(m.Amount) + (star ? "*" : "");
            _revCardMoney.Bind(TextBlock.ForegroundProperty, _revCardMoney.GetResourceObservable(key));
            ToolTip.SetTip(_revCardMoney, star
                ? RevT("Закупки у товара нет — сумма посчитана по цене продажи.", "Товардын сатып алуу баасы жок — сумма сатуу баасы боюнча эсептелген.",
                    "No purchase price for this product — the amount uses the selling price.", "Ürünün alış fiyatı yok — tutar satış fiyatıyla hesaplandı.", "Mahsulotda xarid narxi yo'q — summa sotuv narxida hisoblangan.")
                : null);
        }
        else
        {
            _revCardMoney.Text = "—";
            _revCardMoney.Foreground = null;
            ToolTip.SetTip(_revCardMoney, null);
        }
    }

    // ------------------------------------------------------------------ ввод факта

    private void RevisionSay(string? text) => RevisionSayCore(text, "BrushText");

    private void RevisionSayOk() => RevisionSayCore(_revMessage.Text, "BrushSuccess");

    private void RevisionSayCore(string? text, string brush)
    {
        _revMessage.Text = text ?? "";
        _revMessage.Bind(TextBlock.ForegroundProperty, _revMessage.GetResourceObservable(brush));
    }

    private void RevisionWarn(string text) => RevisionSayCore(text, "BrushDanger");

    private void RevisionFactKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                RevisionCommitFromBox();
                break;
            case Key.Escape:
                e.Handled = true;
                _revFactBox.Text = _revFactInitial;
                RevisionFocusScan();
                break;
            case Key.Down or Key.Up when _revCurrent is not null:
                e.Handled = true;
                RevisionMove(e.Key == Key.Down ? 1 : -1);
                break;
        }
    }

    private void RevisionMove(int delta)
    {
        if (_revCurrent is null)
            return;
        var i = _revView.IndexOf(_revCurrent);
        var next = i + delta;
        if (i < 0 || next < 0 || next >= _revView.Count)
            return;
        RevisionSetCurrent(_revView[next], focusFact: true);
    }

    /// <summary>Enter в «Факт»: записать число (заменяет, «+3» прибавляет) и вернуть курсор в поле скана.</summary>
    private void RevisionCommitFromBox()
    {
        var s = _rev;
        var row = _revCurrent;
        if (s is null || row is null)
        {
            RevisionFocusScan();
            return;
        }

        var text = (_revFactBox.Text ?? "").Trim();
        if (text.Length == 0 || text == _revFactInitial.Trim())
        {
            RevisionFocusScan(); // ничего не набрано — просто дальше
            return;
        }

        if (!RevisionSession.TryParseEntry(text, out _, out _))
        {
            RevisionWarn(RevT("Введите число: 5, 5,5 или +3 (прибавить).", "Сан киргизиңиз: 5, 5,5 же +3 (кошуу).", "Enter a number: 5, 5.5 or +3 (add).", "Bir sayı yazın: 5, 5,5 veya +3 (ekle).", "Son kiriting: 5, 5,5 yoki +3 (qo'shish)."));
            _revFactBox.SelectAll();
            return;
        }

        if (!s.CommitEntry(row, text))
        {
            RevisionWarn(RevT("Число не принято: итог не может быть меньше нуля.", "Сан кабыл алынган жок: жыйынтык нөлдөн аз болбошу керек.", "The number was not accepted: the result cannot be below zero.", "Sayı kabul edilmedi: sonuç sıfırdan küçük olamaz.", "Son qabul qilinmadi: natija noldan kam bo'lmasligi kerak."));
            _revFactBox.SelectAll();
            return;
        }

        RevisionAfterCommit(row, focusScan: true);
    }

    /// <summary>Набрали число, но не нажали Enter, и пикнули следующий (или щёлкнули другую строку) — число сохраняется (ТЗ 2.1, п. 3).
    /// Нетронутое поле (текст тот же, что показали) не записывается: иначе перезапись сдвинула бы учёт на момент пересчёта.</summary>
    private void RevisionCommitPending()
    {
        var s = _rev;
        var row = _revCurrent;
        if (s is null || row is null || !_revFactBox.IsEnabled)
            return;
        var text = (_revFactBox.Text ?? "").Trim();
        if (text.Length == 0 || text == _revFactInitial.Trim())
            return;
        _revFactInitial = _revFactBox.Text ?? "";
        if (RevisionSession.TryParseEntry(text, out _, out _) && s.CommitEntry(row, text))
            RevisionAfterCommit(row, focusScan: false);
        else
            RevisionWarn(RevT($"«{row.Name}»: набранное «{text}» не принято.", $"«{row.Name}»: терилген «{text}» кабыл алынган жок.", $"“{row.Name}”: the typed “{text}” was not accepted.", $"“{row.Name}”: yazılan “{text}” kabul edilmedi.", $"«{row.Name}»: kiritilgan «{text}» qabul qilinmadi."));
    }

    private void RevisionAfterCommit(RevisionRow row, bool focusScan)
    {
        // Фильтр «не посчитанные» / «с расхождением»: строка, которая перестала подходить, уходит из списка.
        var leaves = (_revFilter == RevisionFilter.NotCounted && row.IsCounted) || (_revFilter == RevisionFilter.WithDifference && !row.HasDifference);
        if (leaves && _revView.Contains(row))
        {
            _revSuppressSel = true;
            _revView.Remove(row);
            _revSuppressSel = false;
            _revEmptyList.IsVisible = _revView.Count == 0;
        }

        if (ReferenceEquals(row, _revCurrent))
            RevisionUpdateCard(null);
        var d = row.Difference;
        RevisionSayCore(RevT($"✓ {row.Name}: факт {ReportFormat.Quantity(row.Fact ?? 0)}, разница {(d > 0 ? "+" : "")}{ReportFormat.Quantity(d)}",
            $"✓ {row.Name}: факт {ReportFormat.Quantity(row.Fact ?? 0)}, айырма {(d > 0 ? "+" : "")}{ReportFormat.Quantity(d)}",
            $"✓ {row.Name}: counted {ReportFormat.Quantity(row.Fact ?? 0)}, difference {(d > 0 ? "+" : "")}{ReportFormat.Quantity(d)}",
            $"✓ {row.Name}: sayım {ReportFormat.Quantity(row.Fact ?? 0)}, fark {(d > 0 ? "+" : "")}{ReportFormat.Quantity(d)}",
            $"✓ {row.Name}: haqiqiy {ReportFormat.Quantity(row.Fact ?? 0)}, farq {(d > 0 ? "+" : "")}{ReportFormat.Quantity(d)}"),
            d < -0.0005 ? "BrushDanger" : d > 0.0005 ? "BrushSuccess" : "BrushText");
        RevisionScheduleTotals();
        if (focusScan)
            RevisionFocusScan();
    }

    private void RevisionMarkEqual()
    {
        var s = _rev;
        var row = _revCurrent;
        if (s is null || row is null || row.IsReadOnly)
            return;
        if (s.SetFact(row, row.Expected))
            RevisionAfterCommit(row, focusScan: true);
    }

    private void RevisionClearCurrent()
    {
        var s = _rev;
        var row = _revCurrent;
        if (s is null || row is null)
            return;
        if (s.ClearFact(row))
        {
            RevisionAfterCommit(row, focusScan: true);
            RevisionSay(RevT($"Отметка снята: {row.Name}", $"Белги алынды: {row.Name}", $"Mark removed: {row.Name}", $"İşaret kaldırıldı: {row.Name}", $"Belgi olib tashlandi: {row.Name}"));
        }
    }

    // ------------------------------------------------------------------ поле «Скан / поиск»

    private void RevisionSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(_revSearchBox.Text))
            {
                e.Handled = true;
                _revSearchBox.Text = "";
                RevisionRebuildView(false);
            }

            return;
        }

        if (e.Key == Key.Down && _revView.Count > 0)
        {
            e.Handled = true;
            RevisionSetCurrent(_revCurrent is not null && _revView.Contains(_revCurrent) ? _revCurrent : _revView[0], focusFact: false);
            _revList.Focus();
            return;
        }

        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        var s = _rev;
        var text = _revSearchBox.Text?.Trim() ?? "";
        if (s is null || text.Length == 0)
            return;
        if (s.FindByCode(text) is not null || WarehouseViewModel.LooksLikeCode(text))
        {
            // Штрихкод (со сканера или набранный руками) — это скан.
            _revSearchBox.Text = "";
            RevisionRebuildView(false);
            RevisionOnScanned(text);
            return;
        }

        // Название: список уже отфильтрован — Enter берёт первую найденную строку.
        RevisionRebuildView(false);
        if (_revView.Count > 0)
            RevisionSetCurrent(_revView[0], focusFact: true);
        else
            RevisionWarn(RevT($"«{text}» не найдено.", $"«{text}» табылган жок.", $"“{text}” not found.", $"“{text}” bulunamadı.", $"«{text}» topilmadi."));
    }

    // ------------------------------------------------------------------ скан

    /// <summary>Нужен ли перехват быстрого ввода внутри этого поля (HandleScanInsideField): везде, кроме «Скан / поиск» — оно ловит скан само.</summary>
    private bool RevisionNeedsFieldScanIntercept(TextBox box) => !ReferenceEquals(box, _revSearchBox);

    /// <summary>Код извне (камера, переход из другого окна): в открытую ревизию; без ревизии — игнорируется.</summary>
    private void RevisionExternalScan(string barcode)
    {
        if (_rev is not null)
            RevisionOnScanned(barcode);
        else
            PosLogger.Log($"Склад: код '{barcode}' пришёл, а ревизия не начата — пропущен.", "STOCK");
    }

    /// <summary>Скан со сканера (вне полей) или из поля «Факт»: ставится в очередь и обрабатывается по одному, по порядку.</summary>
    private void RevisionOnScanned(string barcode)
    {
        if (_rev is null || string.IsNullOrWhiteSpace(barcode))
            return;
        _revScanQueue.Enqueue(barcode);
        if (!_revScanRunning)
            _ = RevisionDrainScansAsync();
    }

    private async Task RevisionDrainScansAsync()
    {
        _revScanRunning = true;
        try
        {
            while (_revScanQueue.Count > 0 && _rev is not null)
            {
                var code = _revScanQueue.Dequeue();
                try
                {
                    await RevisionHandleScanAsync(code).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Склад: скан '{code}' в ревизии не обработан: {ex.Message}", "WARNING");
                }
            }
        }
        finally
        {
            _revScanRunning = false;
        }
    }

    private async Task RevisionHandleScanAsync(string raw)
    {
        var s = _rev;
        if (s is null)
            return;
        // Число, набранное и не подтверждённое Enter, сохраняется раньше, чем обработается новый скан.
        RevisionCommitPending();
        var plusOne = _revPlusOne.IsChecked == true;
        var res = s.Scan(raw, plusOne ? RevisionScanMode.PlusOne : RevisionScanMode.EnterNumber);
        switch (res.Status)
        {
            case RevisionScanStatus.Ignored:
                return;
            case RevisionScanStatus.Locked when res.Match is { } lockedMatch:
                RevisionSetCurrent(lockedMatch.Row, focusFact: false);
                RevisionFocusScan();
                if (s.IsLocked && !lockedMatch.Row.IsReadOnly)
                    RevisionWarn(RevT("Ревизия отправляется на сервер — менять подсчёт нельзя.", "Ревизия серверге жөнөтүлүп жатат — саноону өзгөртүүгө болбойт.", "The stocktake is being sent to the server — the count cannot be changed.", "Sayım sunucuya gönderiliyor — sayım değiştirilemez.", "Inventarizatsiya serverga yuborilmoqda — hisobni o'zgartirib bo'lmaydi."));
                return;
            case RevisionScanStatus.NotFound:
            {
                var row = await RevisionResolveUnknownAsync(res.Code).ConfigureAwait(true);
                if (row is null || _rev is null)
                {
                    RevisionFocusScan();
                    return;
                }

                if (plusOne)
                {
                    _rev.AddFact(row, 1);
                    RevisionSetCurrent(row, focusFact: false, RevT("Новый товар: +1", "Жаңы товар: +1", "New product: +1", "Yeni ürün: +1", "Yangi mahsulot: +1"));
                    RevisionFocusScan();
                }
                else
                {
                    RevisionSetCurrent(row, focusFact: true, RevT("Новый товар: учёт 0", "Жаңы товар: эсеп 0", "New product: book 0", "Yeni ürün: sistem 0", "Yangi mahsulot: hisob 0"));
                }

                return;
            }
        }

        var match = res.Match!;
        if (plusOne)
        {
            RevisionSetCurrent(match.Row, focusFact: false, RevT($"+{ReportFormat.Quantity(res.Added)} → {ReportFormat.Quantity(match.Row.Fact ?? 0)}", $"+{ReportFormat.Quantity(res.Added)} → {ReportFormat.Quantity(match.Row.Fact ?? 0)}",
                $"+{ReportFormat.Quantity(res.Added)} → {ReportFormat.Quantity(match.Row.Fact ?? 0)}", $"+{ReportFormat.Quantity(res.Added)} → {ReportFormat.Quantity(match.Row.Fact ?? 0)}", $"+{ReportFormat.Quantity(res.Added)} → {ReportFormat.Quantity(match.Row.Fact ?? 0)}"));
            RevisionAfterCommit(match.Row, focusScan: true);
            return;
        }

        string? note = null;
        if (match.PackageQuantity > 1)
            note = RevT($"Упаковка ×{ReportFormat.Quantity(match.PackageQuantity)} шт", $"Таңгак ×{ReportFormat.Quantity(match.PackageQuantity)} дана", $"Pack ×{ReportFormat.Quantity(match.PackageQuantity)} pcs", $"Koli ×{ReportFormat.Quantity(match.PackageQuantity)} adet", $"Qadoq ×{ReportFormat.Quantity(match.PackageQuantity)} dona");
        RevisionSetCurrent(match.Row, focusFact: true, note);
    }

    /// <summary>Код не из списка ревизии: товар есть в каталоге (добавлен позже) → строка; нет — сервер, затем «Создать товар» (как раньше).</summary>
    private async Task<RevisionRow?> RevisionResolveUnknownAsync(string code)
    {
        var key = ProductCodeLookup.Normalize(code);
        CatalogProductTileVm? tile = null;
        var local = ProductCodeLookup.FindLocal(key)?.Product;
        if (local is not null)
        {
            tile = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, local.Id, StringComparison.OrdinalIgnoreCase)) ?? local;
        }
        else
        {
            if (!WarehouseViewModel.LooksLikeCode(key))
            {
                RevisionWarn(RevT($"«{key}» не найдено.", $"«{key}» табылган жок.", $"“{key}” not found.", $"“{key}” bulunamadı.", $"«{key}» topilmadi."));
                return null;
            }

            ProductCodeLookup.Result server;
            try
            {
                server = await ProductCodeLookup.FindOnServerAsync(key).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Склад: ревизия — поиск '{key}' на сервере упал: {ex.Message}", "STOCK");
                server = new ProductCodeLookup.Result(null, true);
            }

            if (server.Hit?.Product is { } hit)
            {
                tile = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, hit.Id, StringComparison.OrdinalIgnoreCase)) ?? hit;
            }
            else if (server.Unavailable)
            {
                RevisionWarn(RevT("Не удалось проверить штрихкод на сервере. Проверьте связь и пикните ещё раз.", "Штрихкодду серверден текшерүү мүмкүн болгон жок. Байланышты текшерип, кайра сканерлеңиз.",
                    "The barcode could not be verified with the server. Check the connection and scan again.", "Barkod sunucuda doğrulanamadı. Bağlantıyı kontrol edip tekrar okutun.", "Shtrix-kodni serverda tekshirib bo'lmadi. Ulanishni tekshirib, qayta skanerlang."));
                return null;
            }
            else
            {
                tile = await OfferCreateProductAsync(key).ConfigureAwait(true);
            }
        }

        return tile is null ? null : _rev?.AddCatalogProduct(tile);
    }

    // ------------------------------------------------------------------ завершить, бросить

    private async Task RevisionDiscardAsync()
    {
        var s = _rev;
        if (s is null)
            return;
        RevisionCommitPending();
        // 2026-11-05 (приёмка волн 0–3, Н-4): ничего не посчитано (0 позиций) — терять нечего, вопрос «Бросить ревизию?» не задаём.
        var ok = s.CountedCount == 0 || PosConfirmDialog.Show(this,
            RevT("Бросить ревизию?", "Ревизияны таштайсызбы?", "Discard the stocktake?", "Sayım bırakılsın mı?", "Inventarizatsiya tashlansinmi?"),
            RevT($"Подсчёт ({s.CountedCount} позиций) будет удалён, остатки не изменятся.", $"Саноо ({s.CountedCount} позиция) өчүрүлөт, калдыктар өзгөрбөйт.",
                $"The count ({s.CountedCount} items) will be deleted; stock will not change.", $"Sayım ({s.CountedCount} kalem) silinecek, stoklar değişmeyecek.", $"Hisob ({s.CountedCount} pozitsiya) o'chiriladi, qoldiqlar o'zgarmaydi."),
            confirmText: RevT("Бросить", "Таштоо", "Discard", "Bırak", "Tashlash"),
            cancelText: RevT("Остаться", "Калуу", "Stay", "Vazgeç", "Qolish"),
            accent: PosConfirmAccent.Danger);
        if (!ok)
            return;
        if (!s.Discard())
        {
            RevisionWarn(RevT("Часть актов уже создана на сервере — бросить нельзя, завершите ревизию.", "Актылардын бир бөлүгү серверде түзүлгөн — таштоого болбойт, ревизияны аякташ керек.",
                "Some documents are already created on the server — cannot discard, finish the stocktake.", "Bazı tutanaklar sunucuda oluşturuldu — bırakılamaz, sayımı bitirin.", "Aktlarning bir qismi serverda yaratilgan — tashlab bo'lmaydi, inventarizatsiyani yakunlang."));
            return;
        }

        RevisionDetach();
        await Task.CompletedTask;
    }

    private async Task RevisionFinishAsync()
    {
        var s = _rev;
        if (s is null)
            return;
        RevisionCommitPending();
        if (s.CountedCount == 0 && !s.IsPostingStarted)
        {
            RevisionWarn(RevT("Ничего не посчитано.", "Эч нерсе саналган жок.", "Nothing counted yet.", "Henüz hiçbir şey sayılmadı.", "Hali hech narsa sanalmagan."));
            return;
        }

        var api = App.AppHost?.Services.GetService<IInventoryApiService>();
        if (api is null)
        {
            RevisionWarn(RevT("Сервис ревизии недоступен в этом режиме.", "Бул режимде ревизия кызматы жеткиликсиз.", "The stocktake service isn't available in this mode.", "Sayım hizmeti bu modda kullanılamıyor.", "Bu rejimda inventarizatsiya xizmati mavjud emas."));
            return;
        }

        // 2026-11-05 (функция 9): причины расхождений (в примечание акта) и PIN владельца при крупной недостаче — до окна итога.
        if (!s.IsPostingStarted && !await CycleCountBeforeFinishAsync(s).ConfigureAwait(true))
            return;
        var dialog = new RevisionFinishDialog(s, api, RevisionRefreshForApplyAsync, RevisionShowExportMenu);
        await PosDialogHost.ShowAsync(dialog, this).ConfigureAwait(true);
        if (dialog.Result is { Status: RevisionApplyStatus.Done } done)
        {
            CycleCountRecordDone(s); // 2026-11-05 (функция 9): история «когда считали товар»
            RevisionDetach();
            _viewModel.ReapplyProductFilter();
            RefreshWarehouseTotals();
            SuccessToast.Show(RevT(
                $"Ревизия проведена: позиций {done.SentRows}, актов {done.SessionIds.Count}.", $"Ревизия өткөрүлдү: позиция {done.SentRows}, акт {done.SessionIds.Count}.",
                $"Stocktake posted: {done.SentRows} items, {done.SessionIds.Count} documents.", $"Sayım tamamlandı: {done.SentRows} kalem, {done.SessionIds.Count} tutanak.", $"Inventarizatsiya o'tkazildi: {done.SentRows} pozitsiya, {done.SessionIds.Count} akt."));
            return;
        }

        RevisionRefreshTotals();
        RevisionUpdateCard(null);
        RevisionFocusScan();
    }

    /// <summary>Свежий каталог для «Применить» (поправка на продажи, ТЗ 2.3). Сбой обновления останавливает «Применить»: со старым учётом поправка была бы неверной.</summary>
    private async Task<IReadOnlyList<CatalogProductTileVm>> RevisionRefreshForApplyAsync(CancellationToken ct)
    {
        var r = await RevisionSyncCatalogAsync(ct).ConfigureAwait(true);
        if (!r.Ok)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.Error) ? RevT("сервер NurCRM недоступен", "NurCRM сервери жеткиликсиз", "the NurCRM server is unavailable", "NurCRM sunucusu kullanılamıyor", "NurCRM serveri mavjud emas") : r.Error); // 2026-11-01 (В-08): единая фраза о сбое сервера
        return CatalogCacheService.Products.ToList();
    }

    // ------------------------------------------------------------------ выгрузка и прошлые ревизии

    private static List<RevisionExportRow> RevisionBuildExportRows(RevisionSession s, IReadOnlyDictionary<string, double>? recorded)
    {
        var buy = RevisionShowPurchase;
        var list = new List<RevisionExportRow>(s.Rows.Count);
        foreach (var r in s.Rows)
        {
            double? written = recorded is not null && recorded.TryGetValue(r.ProductId, out var w) ? w : null;
            list.Add(new RevisionExportRow(r.Name, r.Barcode, r.IsCounted ? r.ExpectedAtCount : r.Expected, r.Fact, r.SalePrice, buy ? r.PurchasePrice : null, r.CountedAt, written));
        }

        return list;
    }

    private static string RevisionScopeName(RevisionExportScope scope) => scope switch
    {
        RevisionExportScope.Scanned => RevT("Отсканированные", "Сканерленгендер", "Scanned only", "Okutulanlar", "Skanerlanganlar"),
        RevisionExportScope.Discrepancy => RevT("С расхождением", "Айырмасы барлар", "With differences", "Farklı olanlar", "Farqlilar"),
        _ => RevT("Весь склад", "Бүт кампа", "Whole warehouse", "Tüm depo", "Butun ombor"),
    };

    private static string RevisionFormatName(RevisionExportFormat f) => f switch
    {
        RevisionExportFormat.Csv => "CSV",
        RevisionExportFormat.Pdf => "PDF",
        _ => "Excel",
    };

    /// <summary>Пункты «Excel / CSV / PDF» с объёмами «весь склад / отсканированные / с расхождением».</summary>
    private static void RevisionFillFormatItems(ItemCollection items, Action<RevisionExportFormat, RevisionExportScope> run)
    {
        foreach (var format in new[] { RevisionExportFormat.Excel, RevisionExportFormat.Csv, RevisionExportFormat.Pdf })
        {
            var formatItem = new MenuItem { Header = RevisionFormatName(format) };
            foreach (var scope in new[] { RevisionExportScope.All, RevisionExportScope.Scanned, RevisionExportScope.Discrepancy })
            {
                var item = new MenuItem { Header = RevisionScopeName(scope) };
                var f = format;
                var sc = scope;
                item.Click += (_, _) => run(f, sc);
                formatItem.Items.Add(item);
            }

            items.Add(formatItem);
        }
    }

    /// <summary>Меню «Выгрузить ▾» (черновик или окно итога): <paramref name="recorded"/> — что уйдёт в акт (колонка «Записано в акт»).</summary>
    internal void RevisionShowExportMenu(Window owner, Control anchor, IReadOnlyDictionary<string, double>? recorded, bool isDraft, Action<string?> say, Action ok)
    {
        var s = _rev;
        if (s is null)
            return;
        RevisionCommitPending();
        var menu = new ContextMenu();
        RevisionFillFormatItems(menu.Items, (format, scope) =>
        {
            var current = _rev;
            if (current is null)
                return;
            var rows = RevisionBuildExportRows(current, recorded);
            var meta = new RevisionExportMeta
            {
                ShopName = ReportShopName.Current, // 2026-11-01 (Ж-16): в программе владельца — название компании NurCRM, не «Market»
                IsDraft = isDraft,
                StartedAt = current.StartedAt,
                ConductedBy = current.UserName,
                StockAsOf = current.StockAt,
            };
            _ = RevisionExportUi.RunAsync(owner, format, rows, meta, new RevisionExportOptions { Scope = scope, IncludePurchasePrices = RevisionShowPurchase }, say, ok);
        });
        menu.Open(anchor);
    }

    private async Task RevisionShowPastAsync()
    {
        _revPastButton.IsEnabled = false;
        List<RevisionActSummary> acts;
        // 2026-10-27, аудит владельца В-2: у владельца к местным ревизиям добавляются ревизии с других касс и ПК (акты сервера).
        List<(string Key, DateTime WhenUtc, string By, List<InventoryAct> Parts)> remote = new();
        try
        {
            acts = await Task.Run(RevisionActStore.List).ConfigureAwait(true);
            if (AppMode.IsOwner && App.AppHost?.Services.GetService<IInventoryApiService>() is { } invApi)
            {
                var known = new HashSet<string>(acts.SelectMany(a => a.SessionIds ?? Array.Empty<string>()), StringComparer.OrdinalIgnoreCase);
                // 2026-11-03 (С-06/С-15, аудит «зависание при медленном сервере»): не дольше 8 с, а таймаут/сбой сервера — не «Произошла ошибка»: показываем местные ревизии.
                IReadOnlyList<InventoryAct>? all = null;
                try
                {
                    all = await ServerAnswerWait.ForegroundAsync("inventory/acts", t => invApi.ListActsAsync(t)).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Прошлые ревизии: акты сервера не получены ({ServerOutageMonitor.Describe(ex)}) — только местные.", "WARNING");
                }

                // Части одной ревизии («… (1/3)», «… (2/3)») — один пункт списка.
                foreach (var g in (all ?? Array.Empty<InventoryAct>())
                             .Where(a => a.IsApplied && a.Note.TrimStart().StartsWith("Ревизия", StringComparison.OrdinalIgnoreCase) && !known.Contains(a.Id))
                             .GroupBy(a => System.Text.RegularExpressions.Regex.Replace(a.Note.Trim(), @"\s*\(\d+/\d+\)\s*$", "")))
                    remote.Add((g.Key, g.Max(a => a.CreatedUtc), g.First().CreatedByName, g.ToList()));
                remote = remote.OrderByDescending(r => r.WhenUtc).Take(30).ToList();
            }
        }
        finally
        {
            _revPastButton.IsEnabled = true;
        }

        var menu = new ContextMenu();
        if (acts.Count == 0 && remote.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = RevT("Проведённых ревизий пока нет", "Өткөрүлгөн ревизиялар азырынча жок", "No completed stocktakes yet", "Henüz tamamlanmış sayım yok", "Hali o'tkazilgan inventarizatsiyalar yo'q"), IsEnabled = false });
        }

        foreach (var act in acts)
        {
            var item = new MenuItem { Header = $"{act.FinishedAt:dd.MM.yyyy HH:mm}  ·  " + RevT($"посчитано {act.Counted} из {act.Total}", $"саналды {act.Counted} / {act.Total}", $"counted {act.Counted} of {act.Total}", $"sayılan {act.Counted} / {act.Total}", $"sanaldi {act.Counted} / {act.Total}") };
            var file = act.FileName;
            RevisionFillFormatItems(item.Items, (format, scope) => _ = RevisionExportActAsync(file, format, scope));
            menu.Items.Add(item);
        }

        foreach (var r in remote)
        {
            var parts = r.Parts;
            var lineCount = parts.Sum(a => a.Lines.Count);
            var by = string.IsNullOrWhiteSpace(r.By) ? "" : "  ·  " + r.By;
            var item = new MenuItem
            {
                Header = $"{r.WhenUtc.ToLocalTime():dd.MM.yyyy HH:mm}  ·  " + RevT($"на другой кассе: изменено {lineCount}", $"башка кассада: өзгөргөн {lineCount}", $"on another till: {lineCount} changed",
                    $"başka kasada: {lineCount} değişti", $"boshqa kassada: {lineCount} ta o'zgardi") + by,
            };
            RevisionFillFormatItems(item.Items, (format, scope) => _ = RevisionExportRemoteAsync(parts, r.WhenUtc, format, scope));
            menu.Items.Add(item);
        }

        menu.Open(_revPastButton);
    }

    /// <summary>2026-10-27, В-2: выгрузка ревизии, проведённой на другой кассе, по актам сервера (было / стало; цены и штрихкод — из каталога, если товар найден).</summary>
    private async Task RevisionExportRemoteAsync(List<InventoryAct> parts, DateTime whenUtc, RevisionExportFormat format, RevisionExportScope scope)
    {
        var buy = RevisionShowPurchase;
        var rows = new List<RevisionExportRow>();
        foreach (var line in parts.SelectMany(a => a.Lines))
        {
            var card = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, line.ProductId, StringComparison.OrdinalIgnoreCase));
            rows.Add(new RevisionExportRow(line.Name, card?.BarcodeDisplay, line.Before, line.Fact,
                card is null ? 0 : LocalCartService.ParsePrice(card.PriceLine), buy ? card?.PurchasePrice : null, whenUtc.ToLocalTime(), line.Fact));
        }

        var meta = new RevisionExportMeta
        {
            ShopName = ReportShopName.Current,
            IsDraft = false,
            StartedAt = whenUtc.ToLocalTime(),
            FinishedAt = whenUtc.ToLocalTime(),
            ConductedBy = parts.FirstOrDefault()?.CreatedByName,
            ActNumbers = parts.Select(a => a.Id).ToList(),
        };
        var success = false;
        await RevisionExportUi.RunAsync(this, format, rows, meta, new RevisionExportOptions { Scope = scope, IncludePurchasePrices = buy },
            text => _revStartStatus.Text = text ?? "", () => success = true).ConfigureAwait(true);
        if (success)
            _revStartStatus.Bind(TextBlock.ForegroundProperty, _revStartStatus.GetResourceObservable("BrushSuccess"));
    }

    private async Task RevisionExportActAsync(string fileName, RevisionExportFormat format, RevisionExportScope scope)
    {
        var act = await Task.Run(() => RevisionActStore.Load(fileName)).ConfigureAwait(true);
        if (act is null)
        {
            _revStartStatus.Text = RevT("Не удалось прочитать сохранённый акт.", "Сакталган актты окуу мүмкүн болгон жок.", "Could not read the saved document.", "Kayıtlı tutanak okunamadı.", "Saqlangan aktni o'qib bo'lmadi.");
            return;
        }

        var buy = RevisionShowPurchase;
        var rows = act.Rows.Select(r => new RevisionExportRow(r.Name, r.Barcode, r.Expected, r.Fact, r.SalePrice, buy ? r.PurchasePrice : null, r.CountedAt, r.Written)).ToList();
        var meta = new RevisionExportMeta
        {
            ShopName = ReportShopName.Current,
            IsDraft = false,
            StartedAt = act.StartedAt,
            FinishedAt = act.FinishedAt,
            ConductedBy = act.UserName,
            ActNumbers = act.SessionIds,
            StockAsOf = act.StockAt,
        };
        var success = false;
        await RevisionExportUi.RunAsync(this, format, rows, meta, new RevisionExportOptions { Scope = scope, IncludePurchasePrices = buy },
            text => _revStartStatus.Text = text ?? "", () => success = true).ConfigureAwait(true);
        if (success)
            _revStartStatus.Bind(TextBlock.ForegroundProperty, _revStartStatus.GetResourceObservable("BrushSuccess"));
    }
}
