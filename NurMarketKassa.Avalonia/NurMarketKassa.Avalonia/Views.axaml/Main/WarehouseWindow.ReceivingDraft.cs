using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-11 (О-5 «Неоконченная приёмка пропадает»): черновик «Склад → Приёмка». Строки и шапка (поставщик, оплата/долг, срок, № накладной) сами пишутся в файл
/// (ReceivingDraftSession: дебаунс 1,5 с, сразу при закрытии/потере фокуса окна); при первом открытии вкладки «Приёмка» кассе предлагают «Продолжить / Начать заново».
/// После постановки прихода в очередь («Оприходовать») и «Очистить список» список пустеет — черновик удаляется сразу; при отказе сервера строки возвращаются в список
/// и пишутся снова. Не вмешивается в ввод, скан и упаковки: слушает только изменения строк.</summary>
public partial class WarehouseWindow
{
    private ReceivingDraftSession? _receivingDraft;
    private bool _receivingDraftAsked;
    private readonly HashSet<ReceivingLineVm> _draftWatched = new();
    private PropertyChangedEventHandler? _draftLineHandler;

    private void InitReceivingDraft()
    {
        if (_receivingDraft is not null || ReceivingDraftStore.Disabled)
            return;
        try
        {
            // Второе окно склада в этой же программе свой черновик не ведёт — два окна затирали бы один файл.
            if (ReceivingDraftSession.IsSlotActive(ReceivingDraftSlots.Warehouse))
            {
                PosLogger.Log("Черновик приёмки (склад): другое окно склада уже ведёт черновик — это окно его не пишет.", "STOCK");
                return;
            }

            var session = new ReceivingDraftSession(ReceivingDraftSlots.Warehouse, CaptureReceivingDraft);
            _receivingDraft = session;
            if (ReceivingSummaryText.Parent is StackPanel box)
            {
                var caption = ReceivingDraftUi.CreateCaption(this);
                box.Children.Add(caption);
                ReceivingDraftUi.BindCaption(caption, session);
            }

            ReceivingDraftUi.WireFlush(this, session);
            _draftLineHandler = (_, _) => session.Touch();
            foreach (var line in _viewModel.ReceivingLines)
                WatchDraftLine(line);
            _viewModel.ReceivingLines.CollectionChanged += OnDraftLinesChanged;
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(_viewModel.ReceivingSupplier) or nameof(_viewModel.ReceivingPaidNow))
                    session.Touch();
            };
            if (_receivingDebtDays is not null)
                _receivingDebtDays.TextChanged += (_, _) => session.Touch();

            // Вопрос — когда кассир открыл вкладку «Приёмка» (а не при входе в «Склад» ради товаров).
            WarehouseTabs.SelectionChanged += (_, e) =>
            {
                if (ReferenceEquals(e.Source, WarehouseTabs) && ReferenceEquals(WarehouseTabs.SelectedItem, ReceivingTabItem))
                    _ = AskReceivingDraftAsync();
            };
            if (ReferenceEquals(WarehouseTabs.SelectedItem, ReceivingTabItem))
                _ = AskReceivingDraftAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Черновик приёмки (склад) не включён: {ex.Message}", "WARNING");
        }
    }

    private void WatchDraftLine(ReceivingLineVm line)
    {
        if (_draftLineHandler is not null && _draftWatched.Add(line))
            line.PropertyChanged += _draftLineHandler;
    }

    private void OnDraftLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_receivingDraft is not { } session)
            return;
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // «Очистить список»: Reset без старых элементов — отписываемся от всех, кто ушёл.
            foreach (var gone in _draftWatched.Where(l => !_viewModel.ReceivingLines.Contains(l)).ToList())
            {
                gone.PropertyChanged -= _draftLineHandler;
                _draftWatched.Remove(gone);
            }
        }
        if (e.OldItems is not null)
            foreach (var old in e.OldItems.OfType<ReceivingLineVm>().Where(o => !_viewModel.ReceivingLines.Contains(o)))
            {
                old.PropertyChanged -= _draftLineHandler;
                _draftWatched.Remove(old);
            }
        if (e.NewItems is not null)
            foreach (var added in e.NewItems.OfType<ReceivingLineVm>())
                WatchDraftLine(added);

        // Список опустел (приход принят в очередь, «Очистить список») — черновик убираем СРАЗУ, не ждём дебаунса: иначе сбой в эти 1,5 с вернул бы уже оприходованное.
        // Перестановка строки «наверх» (Remove + Insert) — не опустошение.
        if (_viewModel.ReceivingLines.Count == 0 && !_viewModel.IsReorderingReceiving)
            session.Flush(wait: false);
        else
            session.Touch();
    }

    private ReceivingDraftFile? CaptureReceivingDraft()
    {
        var vm = _viewModel;
        var file = new ReceivingDraftFile
        {
            Lines = ReceivingDraftRestore.Capture(vm.ReceivingLines),
            PaidNow = vm.ReceivingPaidNow,
            DebtDays = vm.ReceivingDebtDays,
            DocNumber = vm.ReceivingSupplierDocNumber,
            DocDate = vm.ReceivingSupplierDocDate,
        };
        if (vm.HasReceivingSupplier && vm.ReceivingSupplier is { } s)
        {
            file.SupplierId = s.Id;
            file.SupplierName = s.Name;
            file.SupplierInn = s.Inn;
            file.SupplierPhone = s.Phone;
        }
        return file;
    }

    /// <summary>Вопрос «Найдена неоконченная приёмка… Продолжить / Начать заново». Один раз за открытие окна.</summary>
    private async Task AskReceivingDraftAsync()
    {
        if (_receivingDraft is not { } session || _receivingDraftAsked)
            return;
        _receivingDraftAsked = true;
        ReceivingDraftFile? file;
        try
        {
            file = await Task.Run(() => ReceivingDraftStore.Load(ReceivingDraftSlots.Warehouse)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Черновик приёмки (склад) не прочитан: {ex.Message}", "WARNING");
            return; // не знаем, что на диске, — автосохранение не включаем, чтобы не затереть
        }
        if (file is null)
        {
            session.Arm(); // черновика нет (или он битый и убран в .bad) — пишем с этой минуты
            session.Touch(); // строки, добавленные до включения (скан), тоже попадут в файл
            return;
        }

        bool resume;
        try
        {
            var info = new ReceivingDraftInfo(ReceivingDraftSlots.Warehouse, file.Lines.Count, file.StartedAt, file.SavedAt, ReceivingDraftStore.IsStale(file));
            resume = await ReceivingDraftUi.AskContinueAsync(this, info).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Черновик приёмки (склад): вопрос не показан: {ex.Message}", "WARNING");
            return;
        }

        if (!resume)
        {
            session.Discard();
            session.Arm();
            session.Touch();
            PosLogger.Log("Приёмка (склад): черновик удалён кассиром («Начать заново»).", "STOCK");
            return;
        }

        try
        {
            RestoreReceivingDraft(session, file);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Черновик приёмки (склад) не восстановлен: {ex}", "WARNING");
            _viewModel.ShowReceivingInfo(Tr.T("Не удалось восстановить черновик приёмки — файл остался на месте.", "Кабыл алуу долбоорун калыбына келтирүү мүмкүн болгон жок — файл ордунда калды.",
                "Could not restore the receiving draft — the file was left in place.", "Mal kabul taslağı geri yüklenemedi — dosya yerinde bırakıldı.", "Qabul qoralamasini tiklab bo'lmadi — fayl joyida qoldi."));
        }
    }

    private void RestoreReceivingDraft(ReceivingDraftSession session, ReceivingDraftFile file)
    {
        var restored = ReceivingDraftRestore.Build(file);
        using (session.Suspend())
        {
            // Шапка: поставщик/оплата — только если кассир ещё ничего не выбрал.
            if (!_viewModel.HasReceivingSupplier && !string.IsNullOrEmpty(file.SupplierId))
                _viewModel.SelectOrAddReceivingSupplier(new PurchaseReceivingService.Supplier(file.SupplierId!, file.SupplierName ?? "", file.SupplierInn, file.SupplierPhone));
            if (_viewModel.HasReceivingSupplier)
            {
                _viewModel.ReceivingPaidNow = file.PaidNow;
                ReceivingPaidRadio.IsChecked = file.PaidNow;
                ReceivingDebtRadio.IsChecked = !file.PaidNow;
                if (file.DebtDays is >= 1 and <= 3650)
                {
                    _viewModel.ReceivingDebtDays = file.DebtDays;
                    if (_receivingDebtDays is not null)
                        _receivingDebtDays.Text = file.DebtDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            if (!string.IsNullOrWhiteSpace(file.DocNumber) && string.IsNullOrWhiteSpace(_viewModel.ReceivingSupplierDocNumber))
            {
                _viewModel.ReceivingSupplierDocNumber = file.DocNumber;
                _viewModel.ReceivingSupplierDocDate = file.DocDate;
            }
            foreach (var line in restored.Lines)
                _viewModel.ReceivingLines.Add(line);
        }
        session.Adopt(file);
        session.Touch();
        RefreshReceivingSummary();
        _viewModel.ShowReceivingInfo(restored.StockChanged > 0 ? ReceivingDraftTexts.StockChanged(restored.StockChanged) : ReceivingDraftTexts.Restored(restored.Lines.Count));
        PosLogger.Log($"Приёмка (склад): продолжен черновик, строк {restored.Lines.Count}, остаток изменился у {restored.StockChanged}.", "STOCK");
    }
}
