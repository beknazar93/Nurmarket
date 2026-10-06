using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-05, запрос команды NurCRM («для Бекназара»): «баланс в окне оплаты с сервера, выгрузка накопленных баллов,
/// процент начисления в настройку магазина». Бонусы покупателя теперь на сервере NurCRM — одни на все кассы и в приложении
/// NurCRM, где покупатель видит свой баланс:
/// • баланс — <c>bonus_balance</c> клиента (<c>GET api/main/clients/{id}/</c>);
/// • начисление и списание — <c>POST api/main/clients/{id}/bonus/ {delta, reason: earn | redeem | manual, sale, note}</c>
///   (проверено 05.10: чужая причина → 400, списание больше баланса → 400 «Недостаточно бонусов у клиента»);
/// • баллы, накопленные раньше на этом компьютере, — один раз <c>POST api/main/clients/bonus/import/</c>
///   (сервер сам не даст загрузить дважды и не тронет клиента, у которого уже есть движения на сервере);
/// • включены ли баллы и процент начисления — настройка магазина <c>GET/PATCH api/main/app-shop-settings/</c>
///   (points_enabled, points_percent), общая для всех касс и приложения.
/// Локальный баланс (ClientLoyaltyStore) остаётся запасным: без связи касса показывает последний известный, а операции
/// без связи ждут в очереди (loyalty-queue.json) и уходят при следующей связи.
/// </summary>
public static class ServerLoyalty
{
    /// <summary>Клиент API сервера (ставит App при запуске).</summary>
    public static Func<NurMarketApiClient?>? ApiProvider { get; set; }

    private static readonly SemaphoreSlim QueueLock = new(1, 1);
    private static readonly object FileLock = new();

    /// <summary>Баллы включены в настройке магазина на сервере (null — ещё не знаем, тогда как раньше).</summary>
    public static bool? PointsEnabled { get; private set; }

    /// <summary>Процент начисления из настройки магазина (null — ещё не знаем).</summary>
    public static double? PointsPercent { get; private set; }

    /// <summary>Сколько процентов начислять: из настройки магазина на сервере, без связи — последнее известное.</summary>
    public static double EarnPercent => PointsPercent ?? UserPreferences.Instance.LoyaltyEarnPercent;

    /// <summary>Начислять ли баллы: владелец не выключил их в настройке магазина.</summary>
    public static bool EarnEnabled => PointsEnabled ?? true;

    private static NurMarketApiClient? Api => ApiProvider?.Invoke() is { } api && !string.IsNullOrEmpty(api.AccessToken) ? api : null;

    private static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName);

    private static string QueuePath => Path.Combine(DataDir, "loyalty-queue.json");

    private static string Inv(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;

    /// <summary>После входа и обновления компании: настройка магазина, разовая выгрузка баллов, очередь.</summary>
    public static async Task AfterCompanyRefreshAsync(string? companyId)
    {
        try
        {
            await RefreshShopSettingsAsync().ConfigureAwait(false);
            if (UserPreferences.Instance.LoyaltyEnabled && !string.IsNullOrWhiteSpace(companyId))
                await ImportLocalBalancesOnceAsync(companyId).ConfigureAwait(false);
            await FlushQueueAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Бонусы: синхронизация с сервером не прошла ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Настройка магазина: баллы включены и процент. Процент запоминается для работы без связи.</summary>
    public static async Task RefreshShopSettingsAsync(CancellationToken ct = default)
    {
        if (Api is not { } api)
            return;
        var data = await api.RequestAsync(HttpMethod.Get, "api/main/app-shop-settings/", null, null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Object)
            return;
        if (data.TryGetProperty("points_enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False)
            PointsEnabled = en.ValueKind == JsonValueKind.True;
        var percent = Num(data, "points_percent");
        if (!double.IsNaN(percent))
        {
            PointsPercent = Math.Clamp(percent, 0, 100);
            var prefs = UserPreferences.Instance;
            if (Math.Abs(prefs.LoyaltyEarnPercent - PointsPercent.Value) > 0.001)
            {
                prefs.LoyaltyEarnPercent = PointsPercent.Value;
                prefs.SaveToDisk();
            }
        }
    }

    /// <summary>Процент начисления — в настройку магазина на сервере (её может менять владелец или администратор).</summary>
    public static async Task SavePercentAsync(double percent, CancellationToken ct = default)
    {
        var api = Api ?? throw new InvalidOperationException(Tr.T("нет входа в NurCRM", "NurCRMге кирүү жок", "not signed in to NurCRM", "NurCRM'e giriş yok", "NurCRMga kirilmagan"));
        await api.RequestAsync(new HttpMethod("PATCH"), "api/main/app-shop-settings/",
            new Dictionary<string, object?> { ["points_percent"] = Inv(Math.Clamp(percent, 0, 100)) }, null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        PointsPercent = Math.Clamp(percent, 0, 100);
        PosLogger.Log($"Бонусы: процент начисления {PointsPercent:0.##} % сохранён в настройке магазина.", "INFO");
    }

    /// <summary>Баланс клиента на сервере; null — нет связи или входа (тогда показывается локальный).</summary>
    public static async Task<double?> GetBalanceAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || Api is not { } api)
            return null;
        try
        {
            var data = await api.RequestAsync(HttpMethod.Get, $"api/main/clients/{Uri.EscapeDataString(clientId)}/", null, null, ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var balance = Num(data, "bonus_balance");
            return double.IsNaN(balance) ? null : balance;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Бонусы: баланс клиента не получен с сервера ({ex.Message}) — показан локальный.", "WARNING");
            return null;
        }
    }

    private sealed record QueuedOp(string ClientId, double Delta, string Reason, string? SaleId, string? Note, DateTime AtUtc);

    /// <summary>Начислить (delta &gt; 0) или списать (delta &lt; 0) баллы на сервере. Без связи — в очередь.</summary>
    public static async Task PostAsync(string clientId, double delta, string reason, string? saleId, string? note)
    {
        if (string.IsNullOrWhiteSpace(clientId) || Math.Abs(delta) < 0.005)
            return;
        var op = new QueuedOp(clientId, Math.Round(delta, 2), reason, saleId, note, DateTime.UtcNow);
        if (!await TrySendAsync(op).ConfigureAwait(false))
            Enqueue(op);
        else
            await FlushQueueAsync().ConfigureAwait(false);
    }

    /// <summary>true — сервер принял или окончательно отказал (повтор не поможет); false — нет связи, повторить позже.</summary>
    private static async Task<bool> TrySendAsync(QueuedOp op)
    {
        if (Api is not { } api)
            return false;
        var body = new Dictionary<string, object?> { ["delta"] = Inv(op.Delta), ["reason"] = op.Reason };
        if (!string.IsNullOrWhiteSpace(op.SaleId))
            body["sale"] = op.SaleId;
        if (!string.IsNullOrWhiteSpace(op.Note))
            body["note"] = op.Note;
        try
        {
            var data = await api.RequestAsync(HttpMethod.Post, $"api/main/clients/{Uri.EscapeDataString(op.ClientId)}/bonus/", body, null, CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var balance = Num(data, "bonus_balance");
            PosLogger.Log($"Бонусы на сервере: {op.Reason} {op.Delta:+0.##;-0.##}, баланс клиента {(double.IsNaN(balance) ? "?" : balance.ToString("0.##", CultureInfo.InvariantCulture))}.", "INFO");
            return true;
        }
        catch (ApiException ex) when (ex.StatusCode is >= 400 and < 500 && ex.StatusCode != 401 && ex.StatusCode != 408 && ex.StatusCode != 429)
        {
            // «Недостаточно бонусов» (списали на другой кассе) и т. п. — повтор не поможет.
            PosLogger.Log($"Бонусы: сервер не принял {op.Reason} {op.Delta:+0.##;-0.##} ({ex.StatusCode}: {ex.Message}).", "WARNING");
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Бонусы: нет связи с сервером ({ex.Message}) — операция {op.Reason} {op.Delta:+0.##;-0.##} в очереди.", "WARNING");
            return false;
        }
    }

    private static List<QueuedOp> ReadQueue()
    {
        lock (FileLock)
        {
            try
            {
                return File.Exists(QueuePath) ? JsonSerializer.Deserialize<List<QueuedOp>>(File.ReadAllText(QueuePath)) ?? new() : new();
            }
            catch
            {
                return new();
            }
        }
    }

    private static void WriteQueue(List<QueuedOp> queue)
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(DataDir);
            if (queue.Count == 0)
            {
                if (File.Exists(QueuePath))
                    File.Delete(QueuePath);
                return;
            }
            File.WriteAllText(QueuePath, JsonSerializer.Serialize(queue));
        }
    }

    private static void Enqueue(QueuedOp op)
    {
        var queue = ReadQueue();
        queue.Add(op);
        WriteQueue(queue);
    }

    /// <summary>Отправить операции, ждавшие связи (по порядку; первая неудача — остальные ждут дальше).</summary>
    public static async Task FlushQueueAsync()
    {
        if (!await QueueLock.WaitAsync(0).ConfigureAwait(false))
            return;
        try
        {
            var queue = ReadQueue();
            if (queue.Count == 0)
                return;
            var sent = 0;
            foreach (var op in queue.ToList())
            {
                if (!await TrySendAsync(op).ConfigureAwait(false))
                    break;
                queue.Remove(op);
                sent++;
            }
            WriteQueue(queue);
            if (sent > 0)
                PosLogger.Log($"Бонусы: из очереди отправлено {sent}, осталось {queue.Count}.", "INFO");
        }
        finally
        {
            QueueLock.Release();
        }
    }

    /// <summary>Разово: баллы, накопленные на этом компьютере, — на сервер. Отметка — файл на компанию.</summary>
    public static async Task ImportLocalBalancesOnceAsync(string companyId)
    {
        var flag = Path.Combine(DataDir, $"loyalty-imported-{companyId}.flag");
        if (File.Exists(flag) || Api is not { } api)
            return;
        var balances = DatabaseService.Instance.GetAllClientLoyaltyBalances().Where(b => b.Balance >= 0.01).ToList();
        if (balances.Count > 0)
        {
            var items = balances.Select(b => new Dictionary<string, object?>
            {
                ["client_id"] = b.ClientId,
                ["balance"] = Inv(b.Balance),
                ["note"] = "NurMarket: баллы, накопленные на кассе до " + DateTime.Now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            }).ToList();
            var data = await api.RequestAsync(HttpMethod.Post, "api/main/clients/bonus/import/", new Dictionary<string, object?> { ["items"] = items }, null,
                CancellationToken.None, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            var text = data.ValueKind == JsonValueKind.Undefined ? "" : data.GetRawText();
            PosLogger.Log($"Бонусы: выгружены на сервер балансы {balances.Count} клиент(ов), сумма {balances.Sum(b => b.Balance):0.##}. Ответ: {(text.Length > 300 ? text[..300] : text)}", "INFO");
        }
        else
        {
            PosLogger.Log("Бонусы: на этом компьютере накопленных баллов нет — выгружать нечего.", "INFO");
        }
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(flag, DateTime.UtcNow.ToString("O"));
    }
}
