using System.Globalization;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// Бот владельца, отвечающий на команды: выручка, топ товаров, что заказать, должники.
/// Плюс подписка клиентов — тем, кто нажал «Старт» по ссылке из чека, касса может слать
/// напоминания о долге прямо в Telegram.
///
/// ЧЕСТНОЕ ОГРАНИЧЕНИЕ. Telegram не даёт написать человеку по номеру телефона: бот может
/// отвечать только тем, кто сам ему написал. Поэтому должнику сообщение уходит автоматически
/// ТОЛЬКО если он подписался на бота; для остальных владелец получает список с готовой
/// ссылкой WhatsApp и текстом напоминания — одно нажатие, и сообщение уже набрано.
/// Настоящая SMS-рассылка потребовала бы договора с оператором, которого нет.
///
/// Второе ограничение, тоже честное: бот отвечает, пока КАССА ВКЛЮЧЕНА. Своего сервера у
/// программы нет, новые сообщения спрашивает сама касса (getUpdates). Выключили кассу — бот
/// молчит и ответит, когда её включат снова.
/// </summary>
public sealed class TelegramBotPollingService
{
    /// <summary>Сколько секунд Telegram держит ответ, если новых сообщений нет. 25 — компромисс:
    /// реже дёргаем сеть, но заметно меньше 60-секундного таймаута HTTP-клиента.</summary>
    private const int LongPollSeconds = 25;

    private readonly ISalesApiService _sales;
    private readonly IClientsApiService? _clients;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _offset;
    private string? _lastPollError;

    public TelegramBotPollingService(ISalesApiService sales, IClientsApiService? clients)
    {
        _sales = sales;
        _clients = clients;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Запускает опрос. Повторный вызов при уже запущенном опросе ничего не делает —
    /// вызывается и при входе в кассу, и при сохранении настроек бота.</summary>
    public void Start()
    {
        if (IsRunning)
            return;
        if (!TelegramBotService.IsConfigured || !UserPreferences.Instance.TelegramCommandsEnabled)
            return;

        // Тариф: на «Старт» бот — платная доп. услуга (Маркетплейс → Доп. функции).
        // На «Стандарт» и выше входит в тариф.
        if (!TariffGate.CanUseTelegramBot)
        {
            PosLogger.Log("Телеграм-бот не запущен: функция недоступна на текущем тарифе.", "TELEGRAM");
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        PosLogger.Log("Телеграм-бот: опрос команд запущен.", "TELEGRAM");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Уже остановлен — нормальная ситуация при закрытии кассы.
        }

        _cts = null;
        _loop = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Первый круг делаем с offset = -1: Telegram отдаёт только ПОСЛЕДНЕЕ сообщение, и бот
        // не начнёт отвечать на всё, что владелец писал, пока касса была выключена.
        var first = true;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (updates, error) = await TelegramBotService
                    .GetUpdatesAsync(first ? -1 : _offset, LongPollSeconds, ct)
                    .ConfigureAwait(false);

                if (error != null)
                {
                    // Не засоряем журнал: одна и та же ошибка сети пишется один раз, а не
                    // каждые 25 секунд круглые сутки.
                    if (error != _lastPollError)
                    {
                        PosLogger.Log($"Телеграм-бот: опрос не удался ({error}).", "WARNING");
                        _lastPollError = error;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    continue;
                }

                _lastPollError = null;

                foreach (var update in updates)
                {
                    if (update.TryGetProperty("update_id", out var id) && id.TryGetInt64(out var updateId))
                        _offset = Math.Max(_offset, updateId + 1);

                    if (first)
                        continue;   // самое последнее старое сообщение не обрабатываем

                    await HandleUpdateAsync(update, ct).ConfigureAwait(false);
                }

                first = false;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Телеграм-бот: сбой обработки ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task HandleUpdateAsync(JsonElement update, CancellationToken ct)
    {
        if (!update.TryGetProperty("message", out var message)
            || !message.TryGetProperty("chat", out var chat)
            || !chat.TryGetProperty("id", out var chatIdElement))
        {
            return;
        }

        var chatId = chatIdElement.ValueKind == JsonValueKind.Number
            ? chatIdElement.GetInt64().ToString(CultureInfo.InvariantCulture)
            : chatIdElement.GetString();
        if (string.IsNullOrWhiteSpace(chatId))
            return;

        var text = message.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
            return;

        var isOwner = string.Equals(chatId, UserPreferences.Instance.TelegramChatId, StringComparison.Ordinal);
        var command = text!.Trim();

        // «/команда@ИмяБота» — так Telegram присылает команды в групповых чатах.
        var at = command.IndexOf('@');
        var space = command.IndexOf(' ');
        var argument = space > 0 ? command[(space + 1)..].Trim() : "";
        if (space > 0)
            command = command[..space];
        if (at > 0 && (space < 0 || at < space))
            command = command[..at];

        command = command.TrimStart('/').ToLowerInvariant();

        var reply = command switch
        {
            "start" when !string.IsNullOrWhiteSpace(argument) => SubscribeClient(chatId!, argument, message),
            "start" when isOwner => TelegramReportBuilder.BuildHelp(),
            "start" => "Здравствуйте! Чтобы получать напоминания о задолженности, откройте ссылку, которую вам дали на кассе.",
            "help" or "помощь" when isOwner => TelegramReportBuilder.BuildHelp(),
            "segodnya" or "сегодня" when isOwner => TelegramReportBuilder.BuildRevenue(1, "Сегодня"),
            "nedelya" or "неделя" when isOwner => TelegramReportBuilder.BuildRevenue(7, "За 7 дней"),
            "top" or "топ" when isOwner => TelegramReportBuilder.BuildTopProducts(7),
            "zakaz" or "заказ" when isOwner => TelegramReportBuilder.BuildRestockSuggestions(),
            "ostatki" or "остатки" when isOwner => TelegramReportBuilder.BuildLowStock(),
            // Долги живут только на сервере — эти две команды требуют запроса и обрабатываются
            // ниже, асинхронно.
            "dolgi" or "долги" when isOwner => null,
            "dolg" or "долг" => null,
            _ when isOwner => TelegramReportBuilder.BuildHelp(),
            _ => null,
        };

        if (reply != null)
        {
            await TelegramBotService.SendToAsync(chatId!, reply, ct).ConfigureAwait(false);
            return;
        }

        if (isOwner && command is "dolgi" or "долги")
        {
            await TelegramBotService
                .SendToAsync(chatId!, await BuildDebtorsReportAsync(ct).ConfigureAwait(false), ct)
                .ConfigureAwait(false);
            return;
        }

        if (command is "dolg" or "долг")
        {
            var clientId = TelegramSubscriberStore.GetClientId(chatId!);
            var answer = string.IsNullOrWhiteSpace(clientId)
                ? "Вы ещё не привязаны к карточке клиента. Откройте ссылку, которую вам дали на кассе."
                : await BuildClientDebtAsync(clientId!, ct).ConfigureAwait(false);
            await TelegramBotService.SendToAsync(chatId!, answer, ct).ConfigureAwait(false);
        }
    }

    /// <summary>«/start &lt;id клиента&gt;» — покупатель перешёл по персональной ссылке с чека и
    /// тем самым разрешил боту себе писать. Без этого шага Telegram написать ему не позволит.</summary>
    private static string SubscribeClient(string chatId, string clientId, JsonElement message)
    {
        var name = message.TryGetProperty("from", out var from) && from.TryGetProperty("first_name", out var first)
            ? first.GetString()
            : null;

        TelegramSubscriberStore.Subscribe(chatId, clientId.Trim(), name);
        PosLogger.Log($"Телеграм-бот: подписан клиент {clientId}.", "TELEGRAM");

        return "Готово! Теперь напоминания о задолженности и об акциях будут приходить сюда.\n"
             + "Команда /dolg покажет ваш текущий долг.";
    }

    /// <summary>Список должников для владельца. У каждого — сумма и готовая ссылка WhatsApp с
    /// уже набранным текстом: Telegram написать по номеру телефона не даёт, а WhatsApp по
    /// ссылке wa.me открывается в один тап и не требует никакого договора.</summary>
    public async Task<string> BuildDebtorsReportAsync(CancellationToken ct)
    {
        List<JsonElement> debts;
        try
        {
            debts = await _sales.PosDebtSalesAsync(null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return "Не удалось получить долги с сервера: " + Escape(ex.Message);
        }

        var byClient = new Dictionary<string, (string Name, double Amount, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var debt in debts)
        {
            var clientId = ReadString(debt, "client");
            if (string.IsNullOrWhiteSpace(clientId))
                continue;

            // debt_amount — остаток долга; total — сумма чека. Если остатка в ответе нет,
            // берём сумму чека, иначе должник молча выпал бы из отчёта с нулём.
            var amount = ReadDouble(debt, "debt_amount") ?? ReadDouble(debt, "total") ?? 0;
            if (amount <= 0.005)
                continue;

            var name = ReadString(debt, "client_name") ?? "Без имени";
            if (byClient.TryGetValue(clientId!, out var existing))
                byClient[clientId!] = (existing.Name, existing.Amount + amount, existing.Count + 1);
            else
                byClient[clientId!] = (name, amount, 1);
        }

        var sb = new StringBuilder();
        sb.AppendLine("<b>Должники</b>");

        if (byClient.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Непогашенных долгов нет.");
            return sb.ToString();
        }

        var phones = await LoadPhonesAsync(ct).ConfigureAwait(false);
        var total = byClient.Values.Sum(v => v.Amount);

        sb.AppendLine($"Всего: {total.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} сом " +
                      $"у {byClient.Count} чел.");
        sb.AppendLine();

        foreach (var (clientId, info) in byClient.OrderByDescending(x => x.Value.Amount).Take(25))
        {
            var amountText = info.Amount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
            sb.AppendLine($"<b>{Escape(info.Name)}</b> — {amountText} сом ({info.Count} чек.)");

            if (TelegramSubscriberStore.TryGetChatId(clientId) is { } subscriberChat
                && !string.IsNullOrWhiteSpace(subscriberChat))
            {
                sb.AppendLine("  подписан на бота — напоминание уйдёт автоматически");
            }
            else if (phones.TryGetValue(clientId, out var phone) && !string.IsNullOrWhiteSpace(phone))
            {
                var digits = new string(phone.Where(char.IsDigit).ToArray());
                var reminder = Uri.EscapeDataString(
                    $"Здравствуйте, {info.Name}! Напоминаем о задолженности {amountText} сом. Спасибо!");
                sb.AppendLine($"  <a href=\"https://wa.me/{digits}?text={reminder}\">написать в WhatsApp</a>");
            }
            else
            {
                sb.AppendLine("  <i>нет телефона в карточке клиента</i>");
            }
        }

        return sb.ToString();
    }

    /// <summary>Долг конкретного клиента — ответ на «/dolg» самому покупателю.</summary>
    private async Task<string> BuildClientDebtAsync(string clientId, CancellationToken ct)
    {
        try
        {
            var debts = await _sales.PosDebtSalesAsync(clientId, ct).ConfigureAwait(false);
            var total = debts.Sum(d => ReadDouble(d, "debt_amount") ?? ReadDouble(d, "total") ?? 0);

            return total <= 0.005
                ? "За вами задолженности нет. Спасибо!"
                : $"Ваша задолженность: <b>{total.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} сом</b>.";
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: долг клиента не получен ({ex.Message}).", "WARNING");
            return "Сейчас не получается проверить долг. Попробуйте позже.";
        }
    }

    /// <summary>Рассылает напоминания о долге ТЕМ, КТО ПОДПИСАН на бота. Возвращает, сколько
    /// сообщений ушло. Неподписанным написать нельзя — их владелец видит в /dolgi со ссылкой
    /// на WhatsApp.</summary>
    public async Task<int> SendDebtRemindersAsync(CancellationToken ct = default)
    {
        List<JsonElement> debts;
        try
        {
            debts = await _sales.PosDebtSalesAsync(null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: напоминания не отправлены ({ex.Message}).", "WARNING");
            return 0;
        }

        var byClient = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var debt in debts)
        {
            var clientId = ReadString(debt, "client");
            if (string.IsNullOrWhiteSpace(clientId))
                continue;

            var amount = ReadDouble(debt, "debt_amount") ?? ReadDouble(debt, "total") ?? 0;
            if (amount > 0.005)
                byClient[clientId!] = byClient.GetValueOrDefault(clientId!) + amount;
        }

        var sent = 0;
        foreach (var (clientId, amount) in byClient)
        {
            var chatId = TelegramSubscriberStore.TryGetChatId(clientId);
            if (string.IsNullOrWhiteSpace(chatId))
                continue;

            var text = $"Напоминаем о задолженности: <b>{amount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} сом</b>.";
            if (await TelegramBotService.SendToAsync(chatId!, text, ct).ConfigureAwait(false) == null)
                sent++;
        }

        PosLogger.Log($"Телеграм-бот: напоминаний о долге отправлено {sent}.", "TELEGRAM");
        return sent;
    }

    private async Task<Dictionary<string, string>> LoadPhonesAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_clients == null)
            return result;

        try
        {
            foreach (var client in await _clients.GetClientsAsync(null, ct).ConfigureAwait(false))
            {
                var id = ReadString(client, "id");
                var phone = ReadString(client, "phone");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(phone))
                    result[id!] = phone!;
            }
        }
        catch (Exception ex)
        {
            // Без телефонов отчёт всё равно полезен — суммы и имена в нём есть.
            PosLogger.Log($"Телеграм-бот: телефоны клиентов не загружены ({ex.Message}).", "WARNING");
        }

        return result;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static double? ReadDouble(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string Escape(string? text) => (text ?? "")
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
