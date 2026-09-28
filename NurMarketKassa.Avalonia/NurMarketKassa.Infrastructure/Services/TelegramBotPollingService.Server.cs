using System.Globalization;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-09-28: бот на новых адресах NurCRM (docs/BACKEND_API.md). Отдельной частью класса, чтобы
/// старый путь в TelegramBotPollingService.cs остался как был и работал запасным:
///
/// • /dolgi, /dolg и рассылка напоминаний — сводка должников одним запросом
///   (GET clients/debtors/, BE-03) вместо сбора из pos/sales/?status=debt по страницам;
/// • «Старт» по персональной ссылке — chat_id сохраняется и в карточке клиента на сервере
///   (PATCH clients/{id}/ telegram_chat_id, BE-04), а напоминание берёт chat_id с сервера: клиент,
///   подписавшийся через одну кассу, получает напоминания и от другой;
/// • /ostatki — товары с малым остатком фильтром сервера (quantity_lte, is_service=false, BE-05)
///   вместо локального каталога этой кассы.
///
/// Если сервер эти адреса не знает или не ответил — бот молча берёт старый путь.
/// </summary>
public sealed partial class TelegramBotPollingService
{
    /// <summary>Порог «заканчивается» для /ostatki — как у старого локального отчёта.</summary>
    private const double LowStockThreshold = 3;

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Сводка должников с сервера или null, если её получить не удалось (тогда — старый путь).</summary>
    private async Task<List<DebtorInfo>?> TryLoadServerDebtorsAsync(CancellationToken ct)
    {
        if (_debtsApi == null)
            return null;
        try
        {
            return await _debtsApi.GetDebtorsAsync(0, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: сводка должников с сервера не получена, беру старый путь ({ex.Message}).", "WARNING");
            return null;
        }
    }

    /// <summary>Отчёт /dolgi по сводке сервера. Тот же вид, что у старого отчёта: сумма, число
    /// чеков и готовая ссылка WhatsApp; подписанность на бота — по серверу ИЛИ по этой кассе.</summary>
    internal static string BuildDebtorsReportFromServer(IReadOnlyList<DebtorInfo> debtors, Func<string, string?> localChatLookup)
    {
        var owing = debtors.Where(d => d.DebtTotal > 0.005).OrderByDescending(d => d.DebtTotal).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("<b>Должники</b>");

        if (owing.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Непогашенных долгов нет.");
            return sb.ToString();
        }

        sb.AppendLine($"Всего: {owing.Sum(d => d.DebtTotal).ToString("N2", Ru)} сом у {owing.Count} чел.");
        sb.AppendLine();

        foreach (var debtor in owing.Take(25))
        {
            var name = string.IsNullOrWhiteSpace(debtor.Name) ? "Без имени" : debtor.Name;
            var amountText = debtor.DebtTotal.ToString("N2", Ru);
            sb.AppendLine($"<b>{Escape(name)}</b> — {amountText} сом ({debtor.SalesCount} чек.)");

            var chatId = !string.IsNullOrWhiteSpace(debtor.TelegramChatId)
                ? debtor.TelegramChatId
                : localChatLookup(debtor.ClientId);
            if (!string.IsNullOrWhiteSpace(chatId))
            {
                sb.AppendLine("  подписан на бота — напоминание уйдёт автоматически");
            }
            else if (!string.IsNullOrWhiteSpace(debtor.Phone) && debtor.Phone.Any(char.IsDigit))
            {
                var digits = new string(debtor.Phone.Where(char.IsDigit).ToArray());
                var reminder = Uri.EscapeDataString(
                    $"Здравствуйте, {name}! Напоминаем о задолженности {amountText} сом. Спасибо!");
                sb.AppendLine($"  <a href=\"https://wa.me/{digits}?text={reminder}\">написать в WhatsApp</a>");
            }
            else
            {
                sb.AppendLine("  <i>нет телефона в карточке клиента</i>");
            }
        }

        if (owing.Count > 25)
            sb.AppendLine($"\n<i>…и ещё {owing.Count - 25} чел.</i>");

        return sb.ToString();
    }

    /// <summary>«/start &lt;id клиента&gt;»: chat_id уходит в карточку клиента на сервере, чтобы
    /// напоминание могла отправить любая касса компании, а не только та, где клиент нажал «Старт».
    /// Ошибка сервера подписку не отменяет — локальная связка уже сохранена.</summary>
    private void PushSubscriberToServer(string chatId, string clientId)
    {
        if (_debtsApi == null || !Guid.TryParse(clientId, out _))
            return;

        var api = _debtsApi;
        _ = Task.Run(async () =>
        {
            try
            {
                await api.SetClientTelegramChatIdAsync(clientId, chatId).ConfigureAwait(false);
                PosLogger.Log($"Телеграм-бот: chat_id клиента {clientId} сохранён на сервере.", "TELEGRAM");
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Телеграм-бот: chat_id клиента {clientId} на сервер не сохранён ({ex.Message}).", "WARNING");
            }
        });
    }

    /// <summary>Клиент, подписавшийся через ДРУГУЮ кассу: в местной базе его нет, но chat_id
    /// лежит в карточке на сервере и приходит в сводке должников.</summary>
    private async Task<string?> FindClientByChatOnServerAsync(string chatId, CancellationToken ct)
    {
        var debtors = await TryLoadServerDebtorsAsync(ct).ConfigureAwait(false);
        return debtors?.FirstOrDefault(d => string.Equals(d.TelegramChatId, chatId, StringComparison.Ordinal))?.ClientId;
    }

    /// <summary>/ostatki по серверу. null — сервер не ответил, тогда отчёт строится по каталогу кассы.</summary>
    private async Task<string?> TryBuildLowStockFromServerAsync(CancellationToken ct)
    {
        if (_debtsApi == null)
            return null;
        try
        {
            var (items, count) = await _debtsApi.GetLowStockProductsAsync(LowStockThreshold, ct: ct).ConfigureAwait(false);
            return BuildLowStockFromServer(items, count, LowStockThreshold);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: остатки с сервера не получены, беру каталог кассы ({ex.Message}).", "WARNING");
            return null;
        }
    }

    /// <summary>Тот же вид, что у TelegramReportBuilder.BuildLowStock: меньший остаток — выше.</summary>
    internal static string BuildLowStockFromServer(IReadOnlyList<JsonElement> items, int? count, double threshold, int take = 20)
    {
        var rows = items
            .Select(p => (
                Name: ClientDebtsApiService.ReadString(p, "name") ?? "Без названия",
                Quantity: ClientDebtsApiService.ReadDouble(p, "quantity") ?? 0,
                Unit: ClientDebtsApiService.ReadString(p, "unit")))
            .OrderBy(r => r.Quantity)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("<b>Заканчивается на складе</b>");
        sb.AppendLine();

        if (rows.Count == 0)
        {
            sb.AppendLine($"Товаров с остатком ниже {threshold:0.##} нет.");
            return sb.ToString();
        }

        foreach (var row in rows.Take(take))
        {
            var unit = string.IsNullOrWhiteSpace(row.Unit) ? "" : " " + Escape(row.Unit);
            sb.AppendLine($"• {Escape(row.Name)} — {row.Quantity.ToString("0.##", CultureInfo.InvariantCulture)}{unit}");
        }

        var total = Math.Max(count ?? rows.Count, rows.Count);
        if (total > take)
            sb.AppendLine($"\n<i>…и ещё {total - take}. Всего с остатком до {threshold:0.##}: {total}.</i>");

        return sb.ToString();
    }
}
