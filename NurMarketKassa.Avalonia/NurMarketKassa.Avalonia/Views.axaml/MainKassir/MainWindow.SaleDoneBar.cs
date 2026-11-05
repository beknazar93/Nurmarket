using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Main.Controls;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir;

/// <summary>2026-10-18, редизайн (этап 6, макет MAfterPay): плашка «Продажа прошла» вместо окна.
///
/// Раньше после оплаты было модальное окно «Оплата успешно» (успех в PaymentStatusDialog): 0,6 с или до первой
/// клавиши, остальная работа после оплаты (экран покупателя, перерисовка чека, CheckoutSucceeded, отложенный
/// чек) ждала его закрытия. Теперь окно закрывается сразу (AvaloniaPosCheckoutUiFlow), а эта плашка показывается
/// по CheckoutSucceeded — то есть ПОСЛЕ того, как чек уже сброшен и экран покупателя получил сдачу. Деньги,
/// печать и корзину плашка не трогает: она только показывает итог и даёт «Печать копии».
///
/// 2026-10-18, ревью этапа 6: все числа — из BasketPanelViewModel.LastSale (что реально провёл сервер или сохранила
/// офлайн-очередь; сдача — та же, что у экрана покупателя), а не из окна оплаты. Сообщение офлайн-продажи — из
/// ShowPaymentResultAsync (NoteSaleDoneMessage). «Печать копии» печатает именно эту продажу (PrintSaleCopyAsync).</summary>
public partial class MainWindow
{
    private string? _saleDoneNote;

    /// <summary>Сообщение «Платёж принят…» из потока оплаты. Обычное («Платёж принят. Открываем новый чек.»)
    /// на плашке не нужно — его заменяет заголовок; офлайн/авария («Чек сохранён, отправится автоматически.»)
    /// показывается строкой под суммой.</summary>
    internal void NoteSaleDoneMessage(string? message)
    {
        var usual = Tr.T("Платёж принят. Открываем новый чек.", "Төлөм кабыл алынды. Жаңы чек ачылууда.", "Payment accepted. Opening a new receipt.", "Ödeme alındı. Yeni fiş açılıyor.", "To'lov qabul qilindi. Yangi chek ochilmoqda.");
        _saleDoneNote = string.IsNullOrWhiteSpace(message) || message == usual ? null : message;
    }

    /// <summary>Перед новой оплатой (PrepareCheckoutAsync) плашка прошлой продажи убирается.</summary>
    internal void DismissSaleDoneBar() => SaleDoneBar.Dismiss();

    private void InitSaleDoneBar()
    {
        _viewModel.Basket.CheckoutSucceeded += OnSaleDoneShowBar;
        // Любая клавиша (первый символ скана, Enter, Esc, F-клавиши) и любое касание кассы вне плашки убирают её.
        // Событие НЕ помечается обработанным и фокус не трогается — сканер и клавиши кассы работают как раньше.
        AddHandler(KeyDownEvent, OnSaleDoneKeyTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnSaleDonePointerTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnSaleDoneShowBar(object? sender, EventArgs e)
    {
        try
        {
            var sale = _viewModel.Basket.LastSale;
            var note = sale?.SavedOffline == true ? _saleDoneNote : null;
            _saleDoneNote = null;

            // «Печать копии» — копия именно этой продажи; нет снимка продажи — кнопки нет.
            SaleDoneBar.PrintCopy = sale is null ? null : button => PrintSaleDoneCopyAsync(button, sale);
            // 2026-10-27: «Чек» — чек этой продажи на экране «как в мобилке» (окно с бумагой и панелью «Печать / QR / PDF / Отправить»).
            SaleDoneBar.ShowReceipt = sale is null ? null : _ => ShowSaleDoneReceiptAsync(sale);

            var host = SaleDoneBar.GetVisualParent() as Control;
            var hostWidth = host?.Bounds.Width ?? 0;
            SaleDoneBar.Width = hostWidth > 0 ? Math.Min(480, Math.Max(240, hostWidth - 32)) : 480;
            SaleDoneBar.Show(new SaleDoneInfo(
                sale?.Total,
                sale?.PaymentMethod,
                sale?.PaymentMethod == "cash" ? sale.CashReceived : null,
                sale?.PaymentMethod == "cash" ? sale.ChangeDue : null,
                note));
        }
        catch (Exception ex)
        {
            // Плашка — только подсказка: её сбой не должен мешать продаже.
            PosLogger.Log($"Плашка «Продажа прошла» не показана: {ex.Message}", "PAYMENT");
        }
    }

    private void OnSaleDoneKeyTunnel(object? sender, KeyEventArgs e)
    {
        if (SaleDoneBar.IsShown)
            SaleDoneBar.Dismiss();
    }

    private void OnSaleDonePointerTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (!SaleDoneBar.IsShown)
            return;
        // «Печать копии» и ✕ плашку не убирают — кнопка сделает своё. 2026-10-18, ревью этапа 6: нажатие по самой
        // плашке (не по кнопкам) тоже убирает её — раньше такое нажатие было «мёртвым».
        if (e.Source is Visual v && SaleDoneBar.IsOwnButton(v))
            return;
        SaleDoneBar.Dismiss();
    }

    /// <summary>2026-10-27: окно «Чек» с плашки. Продажа на сервере — чек из ответа сервера (как в «Продажах»), офлайн или сервер не ответил —
    /// из корзины этой продажи (как копия офлайн-чека). Деньги и печать не меняются: «Печать» — та же «Печать копии» плашки.</summary>
    private async Task ShowSaleDoneReceiptAsync(LastSaleInfo sale)
    {
        // 2026-11-03 (Д-2, живая проверка 1.17.73): чек сервера (с составом из базы) запрашивается, но окно не ждёт его 8 с в тишине и не открывает
        // поверх модалку «Показан чек кассы». Сервер ответил быстро (до 1,5 с) — чек сервера, как раньше. Нет — окно сразу с чеком кассы
        // (номер из ответа оплаты) и пометкой внутри бумаги; чек сервера заменяет бумагу на месте, когда придёт. Второго окна нет.
        SaleReceiptCardData BuildLocal(string? note)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(sale.CartJson) ? "{}" : sale.CartJson);
            var cart = doc.RootElement;
            var totals = CartTotalsCalculator.Calculate(cart);
            var local = SaleReceiptCardData.Build(cart, sale.SaleNumber, (decimal)(totals.LineDiscounts + totals.OrderDiscount), (decimal)sale.Total,
                paymentMethodOverride: sale.PaymentMethod, cashReceivedOverride: sale.CashReceivedText, soldAt: sale.SoldAt, savedOffline: sale.SavedOffline);
            if (!string.IsNullOrEmpty(note))
                local.OfflineNote = note;
            return local;
        }

        static SaleReceiptCardData BuildFromServer(System.Text.Json.JsonElement json) =>
            SaleReceiptCardData.Build(json, SalesWindow.TryReceiptNumber(json), SalesWindow.ReadSaleDiscount(json), SalesWindow.ReadSaleTotal(json));

        var print = new Func<Task<(bool Ok, string Message)>>(async () => await ReceiptHistoryService.PrintSaleCopyAsync(sale).ConfigureAwait(true));

        if (sale.SavedOffline || string.IsNullOrWhiteSpace(sale.SaleId))
        {
            await PosDialogHost.ShowAsync(new ReceiptViewDialog(BuildLocal(null), print), this).ConfigureAwait(true);
            return;
        }

        // 2026-11-03 (С-05, аудит «зависание при медленном сервере»): запрос не дольше 8 с (раньше до 55 с без индикатора); строка «Ждём ответ сервера…» в корзине.
        using var wait = new NurMarketKassa.AvaloniaHost.Services.ServerWaitIndicator(text => _viewModel.Basket.CartMessage = text ?? "");
        var saleId = sale.SaleId;
        var serverTask = wait.RunAsync("pos/sales/get", ct => App.SalesApi.PosSaleGetAsync(saleId, ct));
        await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromMilliseconds(300))).ConfigureAwait(true); // 2026-11-05 (С-05): окно «Чек» открывается за < 0,5 с (раньше ждало до 1,5 с), чек сервера заменяет бумагу на месте

        if (serverTask.IsCompleted)
        {
            SaleReceiptCardData ready;
            try
            {
                ready = BuildFromServer(await serverTask.ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Окно «Чек» с плашки: сервер не отдал продажу {sale.SaleId} ({ex.Message}) — из корзины.", "PAYMENT");
                ready = BuildLocal(NoServerDataNote);
            }
            await PosDialogHost.ShowAsync(new ReceiptViewDialog(ready, print), this).ConfigureAwait(true);
            return;
        }

        var slowDialog = new ReceiptViewDialog(BuildLocal(string.IsNullOrWhiteSpace(sale.SaleNumber)
            ? Tr.T("Показан чек кассы. Номер появится после ответа сервера…", "Кассанын чеги көрсөтүлдү. Номери сервер жооп бергенде чыгат…",
                "The register's receipt is shown. The number will appear once the server answers…", "Kasanın fişi gösteriliyor. Numara sunucu yanıt verince görünecek…", "Kassa cheki ko'rsatildi. Raqam server javob bergach chiqadi…")
            : Tr.T("Показан чек кассы. Данные сервера обновляются…", "Кассанын чеги көрсөтүлдү. Сервердин маалыматы жаңыртылууда…",
                "The register's receipt is shown. Server data is being updated…", "Kasanın fişi gösteriliyor. Sunucu verileri güncelleniyor…", "Kassa cheki ko'rsatildi. Server ma'lumotlari yangilanmoqda…")), print);
        _ = ApplyServerReceiptAsync();
        await PosDialogHost.ShowAsync(slowDialog, this).ConfigureAwait(true);

        async Task ApplyServerReceiptAsync()
        {
            SaleReceiptCardData next;
            try
            {
                next = BuildFromServer(await serverTask.ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                // Окно закрыли (ожидание отменено вместе с индикатором) — менять нечего.
                if (!slowDialog.IsVisible)
                    return;
                PosLogger.Log($"Окно «Чек» с плашки: сервер не отдал продажу {sale.SaleId} ({ex.Message}) — остался чек кассы.", "PAYMENT");
                next = BuildLocal(NoServerDataNote);
            }

            if (slowDialog.IsVisible)
                slowDialog.ReplaceCard(next);
        }
    }

    /// <summary>2026-11-03 (Д-2): пометка внутри бумаги, когда чек сервера не пришёл.</summary>
    private static string NoServerDataNote => Tr.T("Показан чек кассы; данные сервера не пришли.", "Кассанын чеги көрсөтүлдү; сервердин маалыматы келген жок.",
        "Showing the register's receipt; server data did not arrive.", "Kasanın fişi gösteriliyor; sunucu verileri gelmedi.", "Kassa cheki ko'rsatildi; server ma'lumotlari kelmadi.");

    /// <summary>2026-10-18, ревью этапа 6: «Печать копии» с плашки — как «Печать последнего чека» в меню «Ещё»
    /// (сообщение строкой под чеком, ошибка — уведомлением), но копия именно этой продажи.</summary>
    private async Task PrintSaleDoneCopyAsync(Button button, LastSaleInfo sale)
    {
        var basket = _viewModel.Basket;
        button.IsEnabled = false;
        try
        {
            basket.CartMessage = Tr.T("Печатаю копию чека…", "Чектин көчүрмөсү басылып жатат…", "Printing a copy of the receipt…", "Fiş kopyası yazdırılıyor…", "Chek nusxasi chop etilmoqda…");
            var (ok, message) = await ReceiptHistoryService.PrintSaleCopyAsync(sale).ConfigureAwait(true);
            basket.CartMessage = ok ? message : "";
            if (!ok)
                _prompts.ShowToast(message, isWarning: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Плашка «Продажа прошла»: копия чека: {ex}", "PRINTER");
            basket.CartMessage = "";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
