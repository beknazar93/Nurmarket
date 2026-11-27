using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using System.Text.Json;
using System;

// 2026-11-26 (Д1, отчёт «Проверка сфер», autoparts): сервер на возврат чека отвечает 403 pin_required. Раньше касса показывала «Нет права на это действие», PIN не спрашивала.
// Теперь: PIN владельца/админа (тот же диалог и та же проверка users/auth/pin/verify/, что у позднего чека) → тот же запрос с X-Confirm-Token и ТЕМ ЖЕ Idempotency-Key.
namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ReturnSaleDialog
{
    /// <summary>Отправка возврата с повтором по PIN: <paramref name="send"/> получает токен подтверждения (null — первая отправка без него).</summary>
    private Task<JsonElement> SendWithPinRetryAsync(Func<string?, Task<JsonElement>> send) =>
        ReturnPinRetry.RunAsync(send, AskReturnPinTokenAsync);

    /// <summary>Окно PIN → токен для X-Confirm-Token. null — отмена или отказ (причина уже показана). Окно возврата на время ввода не «занято».</summary>
    private async Task<string?> AskReturnPinTokenAsync()
    {
        IsBusy = false;
        try
        {
            var refusal = (string?)null;
            var pin = await LateAcceptPrompt.AskConfirmTokenAsync(App.GetRequiredService<SyncService>(), m => refusal = m, ReturnPinInfoText()).ConfigureAwait(true);
            if (pin is null && !string.IsNullOrEmpty(refusal))
                PosMessageBox.Show(this, refusal, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return pin?.Token;
        }
        finally
        {
            IsBusy = true;
        }
    }

    /// <summary>Пояснение в окне PIN: почему спрашиваем (правило компании, не пароль от входа).</summary>
    public static string ReturnPinInfoText() => Tr.T(
        "Для возврата этого чека сервер просит подтверждение: введите PIN владельца или администратора (не пароль от входа).",
        "Бул чекти кайтаруу үчүн сервер ырастоону суранат: ээсинин же администратордун PIN кодун киргизиңиз (кирүү сырсөзү эмес).",
        "The server asks for confirmation to return this receipt: enter the owner's or administrator's PIN (not the login password).",
        "Bu fişin iadesi için sunucu onay istiyor: sahibin veya yöneticinin PIN'ini girin (giriş şifresi değil).",
        "Bu chekni qaytarish uchun server tasdiq so'raydi: egasi yoki administrator PIN kodini kiriting (kirish paroli emas).");
}
