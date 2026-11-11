using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using Microsoft.Extensions.DependencyInjection;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-10 (владелец: «возврат добавь в клиентов которые брали в долг», «возвраты добавь в программу владельца тоже», «функцию возврата добавь в продажи»):
/// ОДНА точка открытия окна возврата (ReturnSaleDialog) из «Продаж», карточки клиента, «Истории покупок», «Долгов клиентов» и меню программы владельца —
/// с одинаковыми проверками. Право — «Возврат» (can_view_market_employee_return; у владельца есть всегда). Касса: без связи — «Возврат без связи»,
/// без открытой смены — «Откройте смену» (как меню кассы, М-1 аудита 01.11). Программа владельца своей смены не открывает (как «Принять оплату», решение 18.10):
/// возврат проходит на сервере, деньги владелец выдаёт сам — перед первым возвратом за сеанс об этом спрашиваем; долговой чек денег не требует (долг уменьшается).</summary>
public static class ReturnSaleLauncher
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Владелец уже подтвердил «возврат без смены» в этом сеансе программы.</summary>
    private static bool _ownerNoShiftConfirmed;

    /// <summary>Есть право на возврат (служба прав недоступна — нет).</summary>
    public static bool CanReturn()
    {
        try
        {
            return App.GetRequiredService<IPermissionService>().HasPermission(PosPermissions.EmployeeReturn);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Чек возвращён целиком — кнопку «Возврат» показываем неактивной с подсказкой.</summary>
    public static bool IsFullyReturned(string? status, string? returnStatus) =>
        SaleListFilter.ReturnStateOf(status, returnStatus) == SaleListFilter.ReturnState.Full;

    public static string FullyReturnedHint => T("Чек уже возвращён целиком — возвращать нечего.", "Чек толугу менен кайтарылган — кайтара турган эч нерсе жок.",
        "The receipt has already been fully returned — nothing left to return.", "Fiş zaten tamamen iade edildi — iade edilecek bir şey yok.", "Chek allaqachon to'liq qaytarilgan — qaytaradigan narsa yo'q.");

    public static string ButtonText => T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish");

    /// <summary>Пометка у чека в списках: «↩ возвращён» / «↩ частично» / пусто.</summary>
    public static string ReturnMark(string? status, string? returnStatus) => SaleListFilter.ReturnStateOf(status, returnStatus) switch
    {
        SaleListFilter.ReturnState.Full => "↩ " + T("возвращён", "кайтарылды", "returned", "iade edildi", "qaytarildi"),
        SaleListFilter.ReturnState.Partial => "↩ " + T("частично", "жарым-жартылай", "partly", "kısmen", "qisman"),
        _ => "",
    };

    /// <summary>Открыть окно возврата. <paramref name="saleId"/> — чек сразу выбран; <paramref name="clientId"/> — список только чеков клиента;
    /// <paramref name="status"/> — статус в фильтре (должник — «Долговой»); <paramref name="productQuery"/> — поиск по товару. true — окно было открыто.</summary>
    public static async Task<bool> OpenAsync(Window owner, string? saleId = null, string? receiptNumber = null, string? productQuery = null,
        string? clientId = null, string? clientName = null, SaleListFilter.StatusKind? status = null)
    {
        var title = ButtonText;
        if (!CanReturn())
        {
            await PosMessageBox.ShowModalAsync(owner, T("Недостаточно прав для возврата.", "Кайтарууга укук жетишсиз.", "You do not have permission to make returns.",
                "İade yapma yetkiniz yok.", "Qaytarish uchun huquq yetarli emas."), title, MessageBoxButton.OK, MessageBoxImage.Warning).ConfigureAwait(true);
            return false;
        }

        if (!AppMode.IsOwner)
        {
            // Касса: без связи — возврат без связи по чекам из памяти кассы (как меню «Возврат» и «История чеков»).
            if (OfflineModeHelper.SellLocally)
            {
                await OfflineOperationsWindow.OpenAsync(owner, OfflineOperationsWindow.Mode.Return, saleId).ConfigureAwait(true);
                return true;
            }
            if (string.IsNullOrEmpty(PosApp.ActiveShiftId))
            {
                await PosMessageBox.ShowModalAsync(owner, T("Откройте смену, чтобы оформить возврат.", "Кайтарууну жасоо үчүн сменаны ачыңыз.", "Open a shift to make a return.",
                    "İade yapmak için vardiya açın.", "Qaytarishni rasmiylashtirish uchun smenani oching."), title, MessageBoxButton.OK, MessageBoxImage.Warning).ConfigureAwait(true);
                return false;
            }
        }
        else
        {
            if (OfflineModeHelper.IsServerOutage)
            {
                await PosMessageBox.ShowModalAsync(owner, OfflineModeHelper.ReturnUnavailableInOutage, title, MessageBoxButton.OK, MessageBoxImage.Warning).ConfigureAwait(true);
                return false;
            }
            if (string.IsNullOrEmpty(PosApp.ActiveShiftId) && !_ownerNoShiftConfirmed)
            {
                var answer = await PosMessageBox.ShowModalAsync(owner, OwnerNoShiftText, title, MessageBoxButton.YesNo, MessageBoxImage.Question,
                    confirmText: T("Продолжить", "Улантуу", "Continue", "Devam", "Davom etish"),
                    cancelText: T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true);
                if (answer != MessageBoxResult.Yes)
                    return false;
                _ownerNoShiftConfirmed = true;
                PosLogger.Log("Возврат в программе владельца без смены кассы: владелец подтвердил (деньги выдаёт сам, в смену кассы не попадёт).", "SALES");
            }
        }

        try
        {
            var dialog = App.GetRequiredService<ReturnSaleDialog>();
            dialog.InitialSaleId = string.IsNullOrWhiteSpace(saleId) ? null : saleId.Trim();
            dialog.InitialReceiptNumber = receiptNumber;
            dialog.InitialProductQuery = productQuery;
            dialog.InitialClientId = clientId;
            dialog.InitialClientName = clientName;
            dialog.InitialStatus = status;
            dialog.FitToOwnerWindow(owner);
            await PosDialogHost.ShowAsync(dialog, owner).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно возврата не открылось: {ex}", "WARNING");
            await PosMessageBox.ShowModalAsync(owner, T("Не удалось открыть возврат. Подробности — в журнале ошибок.", "Кайтарууну ачуу мүмкүн болгон жок. Чоо-жайы — каталар журналында.",
                "Could not open the return. See the error log for details.", "İade açılamadı. Ayrıntılar hata günlüğünde.", "Qaytarishni ochib bo'lmadi. Tafsilotlar xatolar jurnalida."),
                title, MessageBoxButton.OK, MessageBoxImage.Warning).ConfigureAwait(true);
            return false;
        }
    }

    /// <summary>Текст подтверждения «возврат без смены» в программе владельца.</summary>
    public static string OwnerNoShiftText => T(
        "В программе владельца смена кассы не открыта. Возврат пройдёт на сервере NurCRM, а деньги покупателю вы выдаёте сами — в смену кассы и Z-отчёт эта выдача не попадёт. Возврат долгового чека денег не требует: долг клиента уменьшится. Если смена открыта на кассе и деньги выдаются из её ящика — оформите возврат на кассе.",
        "Ээсинин программасында кассанын сменасы ачылган эмес. Кайтаруу NurCRM серверинде өтөт, ал эми акчаны сатып алуучуга өзүңүз бересиз — бул берүү кассанын сменасына жана Z-отчётко кирбейт. Карыз чекти кайтарууга акча керек эмес: кардардын карызы азаят. Эгер смена кассада ачык болуп, акча анын тартмасынан берилсе — кайтарууну кассада жасаңыз.",
        "No till shift is open in the owner app. The return will be recorded on the NurCRM server, and you pay the customer yourself — this payout will not go into a till shift or Z report. Returning a credit receipt needs no money: the customer's debt decreases. If a shift is open on the till and money comes from its drawer, make the return on the till.",
        "Sahip programında kasa vardiyası açık değil. İade NurCRM sunucusunda kaydedilir, müşteriye parayı siz verirsiniz — bu ödeme kasa vardiyasına ve Z raporuna girmez. Veresiye fişin iadesi para gerektirmez: müşterinin borcu azalır. Kasada vardiya açıksa ve para çekmeceden veriliyorsa iadeyi kasada yapın.",
        "Egasi dasturida kassa smenasi ochilmagan. Qaytarish NurCRM serverida o'tadi, pulni xaridorga o'zingiz berasiz — bu to'lov kassa smenasiga va Z-hisobotga kirmaydi. Qarz chekni qaytarish pul talab qilmaydi: mijoz qarzi kamayadi. Agar smena kassada ochiq bo'lsa va pul uning tortmasidan berilsa — qaytarishni kassada rasmiylashtiring.");
}
