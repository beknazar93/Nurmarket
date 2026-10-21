using System.Security.Cryptography;
using System.Text;

namespace NurMarketKassa.Services;

/// <summary>
/// Кто из покупателей разрешил боту себе писать. Telegram не позволяет отправить сообщение по
/// номеру телефона — написать можно только тому, кто сам нажал «Старт» у бота. Поэтому касса
/// выдаёт клиенту персональную ссылку вида t.me/&lt;бот&gt;?start=&lt;id клиента&gt;, а когда он по
/// ней переходит, здесь сохраняется пара «чат ↔ клиент NurCRM».
///
/// Ключ — chat_id, а не id клиента: у одного человека может быть несколько устройств и чатов,
/// и каждое из них должно получать напоминание.
/// </summary>
public static class TelegramSubscriberStore
{
    private static DatabaseService Db => DatabaseService.Instance;

    public static void Subscribe(string chatId, string clientId, string? displayName) =>
        Db.SaveTelegramSubscriber(chatId, clientId, displayName);

    public static void Unsubscribe(string chatId) => Db.DeleteTelegramSubscriber(chatId);

    /// <summary>Чат покупателя для рассылки, или null, если он на бота не подписан.</summary>
    public static string? TryGetChatId(string clientId) => Db.GetTelegramChatIdForClient(clientId);

    /// <summary>Карточка клиента, к которой привязан этот чат — чтобы ответить на «/dolg».</summary>
    public static string? GetClientId(string chatId) => Db.GetTelegramClientIdForChat(chatId);

    public static int Count => Db.CountTelegramSubscribers();

    /// <summary>Ссылка, которую клиенту показывают на кассе (QR или просто текстом). Возвращает
    /// null, если имя бота ещё не сохранено в настройках — без него ссылку не построить.</summary>
    public static string? BuildInviteLink(string clientId)
    {
        var botName = UserPreferences.Instance.TelegramBotUsername?.Trim().TrimStart('@');
        return string.IsNullOrWhiteSpace(botName) || string.IsNullOrWhiteSpace(clientId)
            ? null
            : $"https://t.me/{botName}?start={StartParameter(clientId)}";
    }

    // 2026-10-21 (бот, ВАЖНО 3 отчёта 20.10): ссылка с подписью. Раньше в ссылке был голый id клиента: кто знал или подобрал id
    // (он виден в адресах сайта, в чеке), привязывался к чужой карточке, видел долг и получал напоминания; любой «/start реклама»
    // сохранялся как клиент. Теперь в ссылке «s_<id>_<подпись>»: подпись — HMAC от токена бота (его знают только программы магазина),
    // подделать или собрать ссылку к чужой карточке нельзя. Дальше бот ещё сверяет номер телефона кнопкой «Подтвердить номер».

    /// <summary>Параметр «start» персональной ссылки: «s_» + id + «_» + 10 знаков подписи (до 64 знаков, как требует Telegram).</summary>
    internal static string StartParameter(string clientId)
    {
        var id = clientId.Trim();
        return IsLinkSafe(id) && Sign(id) is { } sig ? $"s_{id}_{sig}" : id;
    }

    /// <summary>Разбор «/start …»: id клиента и признак, что ссылка подписана программой. Старая ссылка (голый GUID) — id без подписи
    /// (бот тогда обязательно сверит телефон); мусор и рекламные метки — (null, false).</summary>
    public static (string? ClientId, bool Signed) ParseStartParameter(string? argument)
    {
        var a = (argument ?? "").Trim();
        if (a.Length > 13 && a.StartsWith("s_", StringComparison.Ordinal) && a[^11] == '_')
        {
            var id = a[2..^11];
            var sig = a[^10..];
            var expected = Sign(id);
            var ok = expected != null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(sig));
            return ok ? (id, true) : (null, false);
        }
        return Guid.TryParse(a, out _) ? (a, false) : (null, false);
    }

    private static bool IsLinkSafe(string id) =>
        id.Length is > 0 and <= 50 && id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-');

    private static string? Sign(string id)
    {
        var token = UserPreferences.Instance.TelegramBotToken;
        if (string.IsNullOrWhiteSpace(token))
            return null;
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("nurmarket-tg-sub:" + token.Trim()));
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(id.ToLowerInvariant()));
        return Convert.ToBase64String(mac).Replace('+', '-').Replace('/', '_').TrimEnd('=')[..10];
    }
}
