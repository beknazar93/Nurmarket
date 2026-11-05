using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-05 (волна 2-Б, функция 12): «Права кассира по действиям + PIN старшего». Один сервис и по одной строке в местах вызова
// (скидка на позицию и на чек, возврат, закрытие смены с недостачей): `if (!await ActionGuard.ConfirmAsync(GuardAction.X, значение)) return;`.
// Правила (какое действие, с какого порога) — AntiFraudSettingsStore.Current.Guard, по умолчанию выключено (поведение как раньше).
// Владелец и администратор (право «Настройки») PIN не вводят — они и есть старшие. PIN старшего — тот же PIN владельца, что у «проведения старого чека» и «просрочки»
// (проверка на сервере, нигде не хранится); PermissionService только читается. Каждое подтверждение и отказ — в журнал антифрода (FraudMonitor.OnPin).

public static class ActionGuard
{
    /// <summary>true — действие можно выполнять. Ничего не бросает. 2026-11-06 (аудит №5): сбой проверки, когда PIN нужен, = отказ (кроме закрытия смены с недостачей).</summary>
    /// <param name="value">Процент скидки (Discount) или сумма в сомах (Return, RemovalAfterRing, ShiftShortage).</param>
    public static async Task<bool> ConfirmAsync(GuardAction action, double value, string? subject = null)
    {
        var needsPin = false; // 2026-11-06 (аудит №5, Л5-06/Л5-10): знаем ли мы, что PIN нужен, — от этого зависит, чем кончается сбой самой проверки
        try
        {
            var rules = AntiFraudSettingsStore.Current.Guard;
            if (!ActionGuardPolicy.NeedsPin(action, value, rules) || IsSenior())
                return true;
            needsPin = true;

            PosLogger.Log($"PIN старшего: нужен для действия «{ActionGuardPolicy.Describe(action, value)}».", "SECURITY");
            var granted = await ExpiredSaleGuard.AskOwnerPinAsync().ConfigureAwait(true);
            // 2026-11-05 (ревью волн 0–3, С13): без связи PIN проверить нельзя — закрытие смены с недостачей не блокируем: предупреждение, запись в журнал антифрода
            // («PIN не проверен — нет связи»), подтверждение владельцем — позже. Остальные действия (скидка, возврат…) по-прежнему требуют PIN.
            if (!granted && action == GuardAction.ShiftShortage && ExpiredSaleGuard.LastPinCheckUnavailable)
            {
                PosLogger.Log($"PIN старшего: нет связи — смена с недостачей {value:0.##} закрывается без проверки PIN (подтвердить позже).", "SECURITY");
                FraudMonitor.OnShortagePinSkipped(value);
                App.GetRequiredService<IUserPrompts>().ShowToast(
                    Tr.T("Нет связи: PIN старшего не проверен. Смена закрывается, недостача записана в журнал — подтвердите её у владельца.",
                        "Байланыш жок: улуунун PIN коду текшерилген жок. Смена жабылат, жетишпестик журналга жазылды — ээси менен ырастаңыз.",
                        "No connection: the senior's PIN was not checked. The shift is closing; the shortage is logged — confirm it with the owner.",
                        "Bağlantı yok: yetkili PIN'i doğrulanamadı. Vardiya kapanıyor, eksik deftere yazıldı — sahibiyle onaylayın.",
                        "Aloqa yo'q: katta xodimning PIN kodi tekshirilmadi. Smena yopiladi, kamomad jurnalga yozildi — egasi bilan tasdiqlang."), isWarning: true);
                return true;
            }
            FraudMonitor.OnPin(action, value, granted, subject);
            if (!granted)
            {
                App.GetRequiredService<IUserPrompts>().ShowToast(
                    Tr.T("Нужен PIN старшего — действие отменено.", "Улуунун PIN коду керек — аракет токтотулду.", "A senior's PIN is needed — the action was cancelled.",
                        "Yetkili PIN'i gerekli — işlem iptal edildi.", "Katta xodimning PIN kodi kerak — amal bekor qilindi."), isWarning: true);
            }
            return granted;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 2026-11-06 (аудит №5, Л5-06/Л5-10): сбой проверки ≠ разрешение. Если PIN нужен — отказ (скидка, возврат, ручная цена, ящик…); исключение — закрытие смены
            // с недостачей: смену не блокируем (как и при отсутствии связи), но пишем в журнал антифрода.
            if (!needsPin)
            {
                PosLogger.Log($"PIN старшего: правила не прочитаны ({ex.GetType().Name}: {ex.Message}) — PIN не требуется.", "WARNING");
                return true;
            }
            if (ActionGuardPolicy.AllowWhenCheckFailed(action, needsPin)) // здесь needsPin = true: разрешено только закрытие смены с недостачей
            {
                PosLogger.Log($"PIN старшего: проверка не выполнена ({ex.GetType().Name}: {ex.Message}) — смена с недостачей {value:0.##} закрывается, подтвердить позже.", "SECURITY");
                try { FraudMonitor.OnShortagePinSkipped(value); } catch (Exception ex2) when (ex2 is not OutOfMemoryException) { }
                return true;
            }
            PosLogger.Log($"PIN старшего: проверка не выполнена ({ex.GetType().Name}: {ex.Message}) — действие «{action}» отклонено.", "SECURITY");
            try
            {
                App.GetRequiredService<IUserPrompts>().ShowToast(
                    Tr.T("Не удалось проверить PIN старшего — действие отменено. Повторите.", "Улуунун PIN кодун текшерүү мүмкүн болгон жок — аракет токтотулду. Кайталаңыз.",
                        "The senior's PIN could not be checked — the action was cancelled. Try again.", "Yetkili PIN'i doğrulanamadı — işlem iptal edildi. Tekrar deneyin.",
                        "Katta xodimning PIN kodini tekshirib bo'lmadi — amal bekor qilindi. Qayta urinib ko'ring."), isWarning: true);
            }
            catch (Exception ex2) when (ex2 is not OutOfMemoryException) { }
            return false;
        }
    }

    /// <summary>2026-11-05 (ревью волн 0–3, В3): менять защиту (PIN старшего, подозрительные операции, режим просрочки, оповещения) может только владелец или администратор.
    /// Закрыто при неизвестности: нет службы прав — нельзя. Права только читаются через PermissionService; ключ --owner сам по себе прав не даёт.</summary>
    internal static bool CanChangeProtection() =>
        ((App.AppHost?.Services.GetService(typeof(IPermissionService)) as IPermissionService)?.HasPermission(PosPermissions.ViewSettings) ?? false)
        || NurMarketKassa.AvaloniaHost.Views.Dialogs.ReceivingMarkupDialog.CanEditSettings();

    /// <summary>true — можно менять защиту; иначе показывает подсказку и пишет в журнал.</summary>
    internal static bool EnsureCanChangeProtection()
    {
        if (CanChangeProtection())
            return true;
        PosLogger.Log("Защита магазина: кассиру закрыты настройки PIN старшего, подозрительных операций, просрочки и оповещений.", "SECURITY");
        try
        {
            App.GetRequiredService<IUserPrompts>().ShowToast(
                Tr.T("Эти настройки доступны только владельцу или администратору.", "Бул жөндөөлөр ээсине же администраторго гана жеткиликтүү.", "These settings are available to the owner or an administrator only.",
                    "Bu ayarlara yalnızca sahip veya yönetici erişebilir.", "Bu sozlamalar faqat egasi yoki administratorga ochiq."), isWarning: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Защита магазина: подсказка не показана ({ex.GetType().Name}).", "WARNING");
        }
        return false;
    }

    /// <summary>Владелец/администратор (право «Настройки» или роль owner/admin в профиле, автономный режим) — сами старшие.
    /// 2026-11-05 (ревью волн 0–3, Л4-10): запуск с ключом --owner сам по себе старшим НЕ делает — кассир мог так обходить PIN старшего.</summary>
    internal static bool IsSenior() =>
        (App.AppHost?.Services.GetService(typeof(IPermissionService)) as IPermissionService)?.HasPermission(PosPermissions.ViewSettings) != false
        || NurMarketKassa.AvaloniaHost.Views.Dialogs.ReceivingMarkupDialog.CanEditSettings();
}
