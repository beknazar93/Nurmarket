using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-11 (О-5 «Неоконченная приёмка пропадает»): черновик «Закупки → Приём товара». Строки и шапка (поставщик, № накладной, доп. расходы, заказ) сами пишутся
/// в файл (ReceivingDraftSession), при открытии приёма кассе предлагают «Продолжить / Начать заново». «Оприходовать», «Очистить» и опустошение списка удаляют черновик сразу;
/// «← К закупкам» и закрытие окна — записывают его (набранное остаётся). Сессия живёт, пока открыта эта страница приёма (завершается через ReceiveState.Detach).</summary>
public sealed partial class PurchasesWindow
{
    private ReceivingDraftSession? _receiveDraft;
    private readonly HashSet<ReceivingLineVm> _receiveDraftWatched = new();
    private bool _receiveDraftDeactivatedWired;

    /// <summary>Вызывается в конце OpenReceive, когда страница уже показана.</summary>
    private void ReceiveDraftInit(ReceiveState s)
    {
        if (ReceivingDraftStore.Disabled)
            return;
        try
        {
            ReceiveDraftEnd(); // прежняя страница приёма (если была) уже записана через Detach; на всякий случай
            // Второе окно «Закупок» в этой программе свой черновик не ведёт — два окна затирали бы один файл.
            if (ReceivingDraftSession.IsSlotActive(ReceivingDraftSlots.Purchases))
            {
                PosLogger.Log("Черновик приёмки (закупки): другое окно уже ведёт черновик — это окно его не пишет.", "STOCK");
                return;
            }

            var session = _receiveDraft = new ReceivingDraftSession(ReceivingDraftSlots.Purchases, () => ReceiveDraftCapture(s));
            if (_sub is Grid page)
            {
                var caption = ReceivingDraftUi.CreateCaption(this);
                caption.Margin = new Avalonia.Thickness(0, -4, 4, 6);
                Grid.SetRow(caption, 4); // строка 4 страницы приёма свободна: подпись появляется только после первой записи
                page.Children.Add(caption);
                ReceivingDraftUi.BindCaption(caption, session);
            }

            PropertyChangedEventHandler lineHandler = (_, _) => session.Touch();
            foreach (var line in s.Lines)
                if (_receiveDraftWatched.Add(line))
                    line.PropertyChanged += lineHandler;
            s.Lines.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                    foreach (var gone in _receiveDraftWatched.Where(l => !s.Lines.Contains(l)).ToList())
                    {
                        gone.PropertyChanged -= lineHandler;
                        _receiveDraftWatched.Remove(gone);
                    }
                if (e.OldItems is not null)
                    foreach (var old in e.OldItems.OfType<ReceivingLineVm>().Where(o => !s.Lines.Contains(o)))
                    {
                        old.PropertyChanged -= lineHandler;
                        _receiveDraftWatched.Remove(old);
                    }
                if (e.NewItems is not null)
                    foreach (var added in e.NewItems.OfType<ReceivingLineVm>())
                        if (_receiveDraftWatched.Add(added))
                            added.PropertyChanged += lineHandler;
                // Список опустел (все строки оприходованы или убраны) — черновик убираем сразу, не ждём дебаунса.
                if (s.Lines.Count == 0)
                    session.Flush(wait: false);
                else
                    session.Touch();
            };
            s.Picker.Changed += _ => session.Touch();
            s.DocNumber.TextChanged += (_, _) => session.Touch();

            if (!_receiveDraftDeactivatedWired)
            {
                _receiveDraftDeactivatedWired = true;
                Deactivated += (_, _) => _receiveDraft?.Flush(wait: false);
            }

            // Страница закрывается (← К закупкам, другой раздел, закрытие окна) — Detach вызывают все пути: записываем и отпускаем сессию.
            var previousDetach = s.Detach;
            s.Detach = () =>
            {
                previousDetach?.Invoke();
                if (ReferenceEquals(_receiveDraft, session))
                    ReceiveDraftEnd();
            };

            _ = ReceiveDraftAskAsync(s, session);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Черновик приёмки (закупки) не включён: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Записать черновик (если есть что) и освободить сессию страницы.</summary>
    private void ReceiveDraftEnd()
    {
        var session = _receiveDraft;
        if (session is null)
            return;
        _receiveDraft = null;
        try { session.Flush(wait: true); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Черновик приёмки (закупки): не записан при закрытии: {ex.Message}", "WARNING"); }
        finally { session.Dispose(); }
    }

    private static ReceivingDraftFile ReceiveDraftCapture(ReceiveState s)
    {
        var file = new ReceivingDraftFile
        {
            Lines = ReceivingDraftRestore.Capture(s.Lines),
            DocNumber = string.IsNullOrWhiteSpace(s.DocNumber.Text) ? null : s.DocNumber.Text!.Trim(),
            OrderId = s.OrderId,
            OrderNumber = s.OrderNumber,
            LandedTotal = s.LandedTotal,
            LandedMode = s.LandedTotal > 0 ? s.LandedMode.ToString() : null,
        };
        if (s.Picker.Selected is { } sup)
        {
            file.SupplierId = sup.Id;
            file.SupplierName = sup.Name;
            file.SupplierInn = sup.Inn;
            file.SupplierPhone = sup.Phone;
        }
        return file;
    }

    private async Task ReceiveDraftAskAsync(ReceiveState s, ReceivingDraftSession session)
    {
        ReceivingDraftFile? file;
        try
        {
            file = await Task.Run(() => ReceivingDraftStore.Load(ReceivingDraftSlots.Purchases)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Черновик приёмки (закупки) не прочитан: {ex.Message}", "WARNING");
            return; // не знаем, что на диске, — автосохранение не включаем, чтобы не затереть
        }
        if (_receive != s || !ReferenceEquals(_receiveDraft, session))
            return; // страницу уже закрыли
        if (file is null)
        {
            session.Arm();
            session.Touch(); // строки, добавленные до включения (заказ, скан), тоже попадут в файл
            return;
        }

        bool resume;
        try
        {
            var info = new ReceivingDraftInfo(ReceivingDraftSlots.Purchases, file.Lines.Count, file.StartedAt, file.SavedAt, ReceivingDraftStore.IsStale(file));
            resume = await ReceivingDraftUi.AskContinueAsync(this, info).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Черновик приёмки (закупки): вопрос не показан: {ex.Message}", "WARNING");
            return;
        }
        if (_receive != s || !ReferenceEquals(_receiveDraft, session))
            return; // пока отвечали, страницу закрыли: черновик остался как был
        if (!resume)
        {
            session.Discard();
            session.Arm();
            session.Touch();
            PosLogger.Log("Приёмка (закупки): черновик удалён кассиром («Начать заново»).", "STOCK");
            return;
        }

        try
        {
            ReceiveDraftRestore(s, session, file);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Черновик приёмки (закупки) не восстановлен: {ex}", "WARNING");
            ReceiveMessage(T("Не удалось восстановить черновик приёмки — файл остался на месте.", "Кабыл алуу долбоорун калыбына келтирүү мүмкүн болгон жок — файл ордунда калды.",
                "Could not restore the receiving draft — the file was left in place.", "Mal kabul taslağı geri yüklenemedi — dosya yerinde bırakıldı.", "Qabul qoralamasini tiklab bo'lmadi — fayl joyida qoldi."), warning: true);
        }
    }

    private void ReceiveDraftRestore(ReceiveState s, ReceivingDraftSession session, ReceivingDraftFile file)
    {
        var restored = ReceivingDraftRestore.Build(file);
        using (session.Suspend())
        {
            // Шапка: то, что кассир уже выбрал/набрал, не перезаписываем.
            if (s.Picker.Selected is null && !string.IsNullOrEmpty(file.SupplierId))
            {
                var known = _filterSupplier.All.FirstOrDefault(x => x.Id == file.SupplierId)
                            ?? new SupplierInfo(file.SupplierId!, file.SupplierName ?? "", file.SupplierPhone ?? "", "", "", file.SupplierInn ?? "", "", "", "", "", null);
                s.Picker.Select(known, raise: false);
                _ = ReceiveLoadGoodsAsync(s, known);
            }
            if (!string.IsNullOrWhiteSpace(file.DocNumber) && string.IsNullOrWhiteSpace(s.DocNumber.Text))
                s.DocNumber.Text = file.DocNumber;
            if (s.OrderId is null && !string.IsNullOrEmpty(file.OrderId))
            {
                s.OrderId = file.OrderId;
                s.OrderNumber = file.OrderNumber;
                if (s.Title is not null && file.OrderNumber is { Length: > 0 } num)
                    s.Title.Text = T("Приём товара", "Товар кабыл алуу", "Receive goods", "Mal kabul", "Tovar qabul qilish")
                                   + T($" по заказу {num}", $" — {num} буйрутмасы боюнча", $" for order {num}", $" — {num} siparişi", $" — {num} buyurtma bo'yicha");
            }
            if (s.LandedTotal <= 0 && file.LandedTotal > 0 && Enum.TryParse<LandedMethod>(file.LandedMode, out var mode))
            {
                s.LandedTotal = file.LandedTotal;
                s.LandedMode = mode;
            }
            foreach (var line in restored.Lines)
                s.Lines.Add(line);
        }
        session.Adopt(file);
        session.Touch();
        ReceiveRender(); // без ReceiveReapplyPurchases: цены закупки — ровно те, что были в черновике
        ReceiveMessage(restored.StockChanged > 0 ? ReceivingDraftTexts.StockChanged(restored.StockChanged) : ReceivingDraftTexts.Restored(restored.Lines.Count), warning: restored.StockChanged > 0, notice: restored.StockChanged == 0);
        PosLogger.Log($"Приёмка (закупки): продолжен черновик, строк {restored.Lines.Count}, остаток изменился у {restored.StockChanged}.", "STOCK");
    }
}
