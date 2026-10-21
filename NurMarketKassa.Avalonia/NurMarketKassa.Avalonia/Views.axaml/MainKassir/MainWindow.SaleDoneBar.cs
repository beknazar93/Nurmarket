using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Main.Controls;
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
