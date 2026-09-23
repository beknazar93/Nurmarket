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
            : $"https://t.me/{botName}?start={clientId.Trim()}";
    }
}
