using Avalonia.Controls;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-05 (волна 2-А, функция 1): предупреждение о просрочке при продаже. Отдельный сервис — в окне кассы одна точка вызова
// (MainWindow.AddProductFromCatalogAsync, куда сходятся и сканер, и плитки). Режим — ShopAutomationSettings.ExpiryMode:
// off — молчит, warn — «Товар просрочен (дата). Продать всё равно?», block — продать можно только после PIN владельца (проверка на сервере, как «старый чек»).
// Сроки берутся из справочника сервера (ProductExpiryIndex); пока он не загружен — решение «можно», загрузка идёт в фоне (кассу не задерживаем).

public static class ExpiredSaleGuard
{
    /// <summary>2026-11-05 (С13): последняя проверка PIN не состоялась из-за связи или сервера (а не потому, что PIN неверный).</summary>
    internal static bool LastPinCheckUnavailable { get; private set; }

    // 2026-11-05 (ревью волн 0–3, С5): решение «продать» по товару помним 3 минуты — шесть одинаковых йогуртов не требуют шести окон и шести PIN.
    private static readonly Dictionary<string, DateTime> Allowed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan AllowedFor = TimeSpan.FromMinutes(3);

    private static bool IsRemembered(string productId)
    {
        lock (Allowed)
            return Allowed.TryGetValue(productId, out var until) && until > DateTime.UtcNow;
    }

    private static void Remember(string productId)
    {
        lock (Allowed)
        {
            var now = DateTime.UtcNow;
            foreach (var old in Allowed.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                Allowed.Remove(old);
            Allowed[productId] = now + AllowedFor;
        }
    }

    /// <summary>true — товар можно добавить в чек. Ничего не бросает: любая ошибка = «можно» (касса не должна встать из-за проверки сроков).</summary>
    public static async Task<bool> ConfirmAsync(Window? owner, string productId, string title)
    {
        try
        {
            var mode = ExpiryModes.Normalize(ShopAutomationSettings.Current.ExpiryMode);
            if (mode == ExpiryModes.Off || string.IsNullOrWhiteSpace(productId))
                return true;
            if (IsRemembered(productId))
                return true;
            _ = ProductExpiryIndex.RefreshAsync(false); // обновится в фоне (не чаще раза в 5 минут); эта продажа решается по тому, что уже есть
            if (!ProductExpiryIndex.Marks.TryGetValue(productId, out var mark))
                return true;
            var decision = ExpiryRules.Decide(mode, mark, DateTime.Today);
            if (decision == ExpirySaleDecision.Allow)
                return true;

            var needPin = decision == ExpirySaleDecision.NeedOwnerPin;
            var text = ExpiryRules.SaleWarningText(title, mark, needPin);
            var sell = await PosDialogs.ConfirmYesNoModalAsync(owner, text,
                Tr.T("Товар просрочен", "Товардын мөөнөтү өткөн", "Product expired", "Ürünün süresi geçmiş", "Mahsulot muddati o'tgan"),
                confirmText: needPin ? Tr.T("Ввести PIN", "PIN киргизүү", "Enter PIN", "PIN gir", "PIN kiritish") : Tr.T("Продать всё равно", "Баары бир сатуу", "Sell anyway", "Yine de sat", "Baribir sotish"),
                cancelText: Tr.T("Убрать", "Алып салуу", "Remove", "Kaldır", "Olib tashlash"), danger: true,
                enterCancels: true).ConfigureAwait(true); // 2026-11-06 (Н16): Enter = «Убрать», продать — только мышью/касанием
            if (!sell)
            {
                PosLogger.Log($"Просрочка: «{title}» (срок {mark.Date:dd.MM.yyyy}) не добавлен в чек — кассир убрал.", "INFO");
                return false;
            }
            if (!needPin)
            {
                PosLogger.Log($"Просрочка: «{title}» (срок {mark.Date:dd.MM.yyyy}) продан по подтверждению кассира.", "INFO");
                Remember(productId);
                return true;
            }
            var ok = await AskOwnerPinAsync().ConfigureAwait(true);
            PosLogger.Log($"Просрочка: «{title}» (срок {mark.Date:dd.MM.yyyy}) — PIN владельца {(ok ? "принят" : "не принят")}.", ok ? "INFO" : "WARNING");
            if (ok)
                Remember(productId);
            return ok;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Просрочка: проверка не выполнена ({ex.GetType().Name}: {ex.Message}) — товар добавлен.", "WARNING");
            return true;
        }
    }

    /// <summary>PIN владельца (та же проверка на сервере, что для «проведения старого чека»). PIN нигде не сохраняется.</summary>
    // 2026-11-05 (волна 2-Б, функция 12): internal — тот же вопрос PIN использует ActionGuard (права кассира по действиям).
    internal static async Task<bool> AskOwnerPinAsync()
    {
        LastPinCheckUnavailable = false;
        var prompts = App.GetRequiredService<IUserPrompts>();
        var sync = App.GetRequiredService<SyncService>();
        var info = Tr.T("Введите PIN владельца (не пароль от входа).", "Ээсинин PIN кодун киргизиңиз (кирүү сырсөзү эмес).", "Enter the owner PIN (not the login password).",
            "Sahip PIN'ini girin (giriş şifresi değil).", "Egasining PIN kodini kiriting (kirish paroli emas).");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            string? entered = null;
            var granted = await prompts.ConfirmWithCodeAsync(
                Tr.T("PIN владельца", "Ээсинин PIN коду", "Owner PIN", "Sahip PIN'i", "Egasining PIN kodi"), info,
                code => { entered = code?.Trim(); return !string.IsNullOrEmpty(entered); }).ConfigureAwait(true);
            if (!granted || string.IsNullOrEmpty(entered))
                return false;
            var verified = await sync.VerifyOwnerPinForLateAsync(entered).ConfigureAwait(true);
            entered = null;
            if (verified.Status == LatePinStatus.Ok)
                return true;
            info = verified.Message;
            if (verified.Status != LatePinStatus.Wrong)
            {
                // 2026-11-06 (аудит №5, Л5-06): «нет связи» — только Unavailable; ошибка/отказ сервера (4xx, нет прав) = Error = отказ, обхода нет.
                LastPinCheckUnavailable = verified.Status == LatePinStatus.Unavailable;
                prompts.ShowError(verified.Message);
                return false;
            }
        }
        return false;
    }
}
