using System.Text;
using System.Text.Json;
using NurSupportBot.Data;
using NurSupportBot.Services;
using NurSupportBot.Telegram;
using static NurSupportBot.Bot.Ui;

namespace NurSupportBot.Bot;

/// <summary>Бот обучения и поддержки клиентов NurCRM. Путь клиента: меню → раздел → операция →
/// видео + пошаговый текст → «Помогло / Не помогло» → при необходимости оператор. Обращения уходят
/// в группу поддержки; оператор отвечает ответом на сообщение, клиент получает ответ в боте.</summary>
public sealed partial class SupportBot
{
    private readonly BotConfig _config;
    private readonly TgClient _tg;
    private readonly Db _db;
    private readonly NurCrmClient _crm;
    private readonly TokenVault _vault;

    public SupportBot(BotConfig config, TgClient tg, Db db, NurCrmClient crm, TokenVault vault)
    {
        _config = config;
        _tg = tg;
        _db = db;
        _crm = crm;
        _vault = vault;
    }

    private bool IsAdmin(long tgId) => _config.AdminIds.Contains(tgId);

    private long? SupportChat => long.TryParse(_db.GetSetting("support_chat"), out var id) ? id : null;

    public static void Log(string text) => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}");

    public async Task RunAsync(CancellationToken ct)
    {
        var me = await _tg.GetMeAsync(ct).ConfigureAwait(false);
        Log($"Бот @{me?.Username} запущен. Инструкций и разделов в базе: {_db.NodeCount()}. Администраторов: {_config.AdminIds.Count}.");
        await _tg.SetCommandsAsync(new[]
        {
            ("menu", "Главное меню"),
            ("search", "Найти инструкцию по вопросу"),
            ("report", "Финансовый отчёт"),
            ("operator", "Связаться с оператором"),
            ("account", "Мой аккаунт NurCRM"),
            ("help", "Как пользоваться ботом"),
        }, ct).ConfigureAwait(false);

        var offset = long.TryParse(_db.GetSetting("offset"), out var saved) ? saved : 0;
        while (!ct.IsCancellationRequested)
        {
            List<Update> updates;
            try
            {
                updates = await _tg.GetUpdatesAsync(offset, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("getUpdates: " + ex.Message);
                await Task.Delay(5000, ct).ConfigureAwait(false);
                continue;
            }

            foreach (var update in updates)
            {
                offset = update.UpdateId + 1;
                try
                {
                    await HandleAsync(update, ct).ConfigureAwait(false);
                }
                catch (TgException ex) when (ex.RetryAfter is { } wait)
                {
                    Log($"Telegram просит паузу {wait} с");
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log($"Ошибка обработки {update.UpdateId}: {ex}");
                }
            }
            if (updates.Count > 0)
                _db.SetSetting("offset", offset.ToString(Ru));
        }
    }

    private async Task HandleAsync(Update update, CancellationToken ct)
    {
        if (update.CallbackQuery is { } callback)
        {
            await OnCallbackAsync(callback, ct).ConfigureAwait(false);
            return;
        }
        if (update.Message is not { From: { IsBot: false } } message)
            return;
        if (message.Chat.Type is "group" or "supergroup")
            await OnGroupMessageAsync(message, ct).ConfigureAwait(false);
        else if (message.Chat.Type == "private")
            await OnPrivateMessageAsync(message, ct).ConfigureAwait(false);
    }

    // ── личные сообщения ────────────────────────────────────────────────────────────

    private async Task OnPrivateMessageAsync(Message m, CancellationToken ct)
    {
        var from = m.From!;
        var user = _db.Touch(from.Id, from.Username, from.FirstName, from.LastName);
        var chat = m.Chat.Id;
        var text = (m.Text ?? "").Trim();

        if (text.StartsWith('/'))
        {
            var command = text.Split(' ', '@')[0].ToLowerInvariant();
            var handled = true;
            switch (command)
            {
                case "/start":
                    _db.ClearSession(from.Id);
                    _db.Log(from.Id, "start");
                    if (user.Onboarded)
                        await ShowMainMenuAsync(chat, null, ct).ConfigureAwait(false);
                    else
                        await ShowWelcomeAsync(chat, ct).ConfigureAwait(false);
                    break;
                case "/menu":
                case "/cancel":
                    _db.ClearSession(from.Id);
                    await ShowMainMenuAsync(chat, null, ct).ConfigureAwait(false);
                    break;
                case "/search":
                    await StartSearchAsync(chat, from.Id, ct).ConfigureAwait(false);
                    break;
                case "/report":
                    await ShowReportMenuAsync(chat, null, user, ct).ConfigureAwait(false);
                    break;
                case "/operator":
                    await ShowOperatorAsync(chat, null, null, ct).ConfigureAwait(false);
                    break;
                case "/account":
                    await ShowAccountAsync(chat, null, user, ct).ConfigureAwait(false);
                    break;
                case "/login":
                    await StartLoginAsync(chat, from.Id, ct).ConfigureAwait(false);
                    break;
                case "/help":
                    await ShowHelpAsync(chat, ct).ConfigureAwait(false);
                    break;
                case "/id":
                    await _tg.SendMessageAsync(chat, $"Ваш Telegram ID: <code>{from.Id}</code>", ct: ct).ConfigureAwait(false);
                    break;
                default:
                    handled = await OnAdminCommandAsync(m, command, text, ct).ConfigureAwait(false);
                    break;
            }
            if (handled)
                return;
        }

        if (m.Document != null && (m.Caption ?? "").Trim().StartsWith("/import", StringComparison.OrdinalIgnoreCase) && IsAdmin(from.Id))
        {
            await ImportKnowledgeBaseAsync(m, ct).ConfigureAwait(false);
            return;
        }

        var (state, data) = _db.GetSession(from.Id);
        switch (state)
        {
            case "login_email":
                await OnLoginEmailAsync(chat, from.Id, text, ct).ConfigureAwait(false);
                return;
            case "login_pwd":
                await OnLoginPasswordAsync(m, data ?? "", text, ct).ConfigureAwait(false);
                return;
            case "ticket":
                await AddToTicketDraftAsync(m, data, ct).ConfigureAwait(false);
                return;
            case "treply":
                await RelayClientReplyAsync(m, data, ct).ConfigureAwait(false);
                return;
        }
        if (state.StartsWith("a:", StringComparison.Ordinal) && IsAdmin(from.Id))
        {
            await OnAdminInputAsync(m, state, data, ct).ConfigureAwait(false);
            return;
        }

        if (text.Length > 0)
        {
            await RunSearchAsync(m, text, ct).ConfigureAwait(false);
            return;
        }
        if (m.HasMedia)
        {
            await _tg.SendMessageAsync(chat,
                "Чтобы отправить скриншот или видео оператору, нажмите «Связаться с оператором» → «Описать проблему».",
                new InlineKeyboard().Row(Operator()).Row(Menu), ct: ct).ConfigureAwait(false);
        }
    }

    // ── кнопки ──────────────────────────────────────────────────────────────────────

    private async Task OnCallbackAsync(CallbackQuery cb, CancellationToken ct)
    {
        var data = cb.Data ?? "";
        var from = cb.From;
        var user = _db.Touch(from.Id, from.Username, from.FirstName, from.LastName);
        var chat = cb.Message?.Chat.Id ?? from.Id;
        var msgId = cb.Message?.MessageId;
        string? answer = null;

        try
        {
            if (cb.Message?.Chat.Type is "group" or "supergroup")
            {
                answer = await OnGroupCallbackAsync(cb, data, ct).ConfigureAwait(false);
                return;
            }

            var parts = data.Split(':');
            switch (parts[0])
            {
                case "m":
                    _db.ClearSession(from.Id);
                    await ShowMainMenuAsync(chat, msgId, ct).ConfigureAwait(false);
                    break;
                case "skip":
                    _db.SetOnboarded(from.Id);
                    await ShowMainMenuAsync(chat, msgId, ct).ConfigureAwait(false);
                    break;
                case "n" when parts.Length > 1 && long.TryParse(parts[1], out var nodeId):
                    await OpenNodeAsync(chat, msgId, from.Id, nodeId, ct).ConfigureAwait(false);
                    break;
                case "h" when parts.Length > 1 && long.TryParse(parts[1], out var helpedId):
                    _db.Log(from.Id, "helped", helpedId);
                    answer = "Спасибо! Рады, что помогло.";
                    await OnHelpedAsync(chat, helpedId, ct).ConfigureAwait(false);
                    break;
                case "nh" when parts.Length > 1 && long.TryParse(parts[1], out var notHelpedId):
                    _db.Log(from.Id, "not_helped", notHelpedId);
                    await ShowOperatorAsync(chat, null, notHelpedId, ct, notHelped: true).ConfigureAwait(false);
                    break;
                case "op":
                    await ShowOperatorAsync(chat, msgId, parts.Length > 1 && long.TryParse(parts[1], out var opNode) ? opNode : null, ct)
                        .ConfigureAwait(false);
                    break;
                case "srch":
                    await StartSearchAsync(chat, from.Id, ct).ConfigureAwait(false);
                    break;
                case "acc":
                    await ShowAccountAsync(chat, msgId, user, ct).ConfigureAwait(false);
                    break;
                case "login":
                    await StartLoginAsync(chat, from.Id, ct).ConfigureAwait(false);
                    break;
                case "logout":
                    _db.Logout(from.Id);
                    answer = "Вы вышли из NurCRM в боте.";
                    await ShowAccountAsync(chat, msgId, _db.GetUser(from.Id)!, ct).ConfigureAwait(false);
                    break;
                case "rep":
                    if (parts.Length > 1)
                        await SendReportAsync(chat, from.Id, parts[1], ct).ConfigureAwait(false);
                    else
                        await ShowReportMenuAsync(chat, msgId, user, ct).ConfigureAwait(false);
                    break;
                case "tk":
                    answer = await OnTicketButtonAsync(chat, from.Id, parts, ct).ConfigureAwait(false);
                    break;
                case "tr" when parts.Length > 1 && long.TryParse(parts[1], out var replyTicket):
                    _db.SetSession(from.Id, "treply", replyTicket.ToString(Ru));
                    await _tg.SendMessageAsync(chat,
                        $"Напишите ответ оператору по обращению №{replyTicket}. Можно приложить скриншот.",
                        new InlineKeyboard().Row(Menu), ct: ct).ConfigureAwait(false);
                    break;
                case "tcu" when parts.Length > 1 && long.TryParse(parts[1], out var solved):
                    answer = await CloseTicketByClientAsync(chat, from.Id, solved, ct).ConfigureAwait(false);
                    break;
                case "a":
                    if (IsAdmin(from.Id))
                        await OnAdminCallbackAsync(chat, msgId, from.Id, parts, ct).ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            try
            {
                await _tg.AnswerCallbackAsync(cb.Id, answer, ct).ConfigureAwait(false);
            }
            catch (TgException)
            {
                // Кнопку нажали слишком давно — ответ на неё Telegram уже не принимает.
            }
        }
    }

    // ── экраны ──────────────────────────────────────────────────────────────────────

    /// <summary>Правит сообщение с кнопками, если можно, иначе шлёт новое (например, после видео).</summary>
    private async Task ShowAsync(long chat, long? editMsgId, string html, InlineKeyboard keyboard, CancellationToken ct)
    {
        if (editMsgId is { } id)
        {
            try
            {
                await _tg.EditMessageAsync(chat, id, html, keyboard, ct).ConfigureAwait(false);
                return;
            }
            catch (TgException ex) when (ex.Description.Contains("not modified", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            catch (TgException)
            {
                // Сообщение нельзя править (видео, старое) — ниже отправим новое.
            }
        }
        await _tg.SendMessageAsync(chat, html, keyboard, ct: ct).ConfigureAwait(false);
    }

    private async Task ShowWelcomeAsync(long chat, CancellationToken ct)
    {
        const string text = """
            👋 <b>Здравствуйте! Я помощник NurCRM.</b>

            Помогу быстро найти, как сделать нужную операцию: короткое видео и пошаговая инструкция. Если не получится — передам вопрос оператору поддержки.

            Войдите с логином NurCRM — тогда будут доступны финансовый отчёт, а оператор сразу увидит вашу компанию. Пароль бот не хранит.
            """;
        await _tg.SendMessageAsync(chat, text, new InlineKeyboard()
            .Row(Button.Cb("🔐 Войти в NurCRM", "login"))
            .Row(Button.Cb("Продолжить без входа", "skip")), ct: ct).ConfigureAwait(false);
    }

    private async Task ShowMainMenuAsync(long chat, long? editMsgId, CancellationToken ct)
    {
        var keyboard = new InlineKeyboard();
        var sections = _db.Children(null);
        for (var i = 0; i < sections.Count; i += 2)
        {
            keyboard.Row(sections.Skip(i).Take(2).Select(s => Button.Cb(s.Title, $"n:{s.Id}")).ToArray());
        }
        keyboard
            .Row(Button.Cb("🔎 Поиск по вопросу", "srch"), Button.Cb("📊 Финансовый отчёт", "rep"))
            .Row(Operator())
            .Row(Button.Cb("👤 Мой аккаунт", "acc"));
        const string text = """
            <b>Помощник NurCRM</b>

            Выберите раздел — или просто напишите вопрос своими словами, например: <i>как сделать возврат</i>.
            """;
        await ShowAsync(chat, editMsgId, text, keyboard, ct).ConfigureAwait(false);
    }

    private async Task ShowHelpAsync(long chat, CancellationToken ct)
    {
        const string text = """
            <b>Как пользоваться ботом</b>

            1. Выберите раздел программы и нужную операцию — придёт короткое видео и пошаговая инструкция.
            2. Можно не искать по меню: напишите вопрос своими словами, например «как списать товар».
            3. Не помогло — нажмите «Мне не помогло» или «Связаться с оператором», опишите проблему и приложите скриншот. Оператор ответит здесь же, в этом чате.
            4. «Финансовый отчёт» — выручка, чеки, возвраты и прибыль за день, неделю или месяц (нужен вход с логином NurCRM).

            Команды: /menu — меню, /search — поиск, /report — отчёт, /operator — оператор.
            """;
        await _tg.SendMessageAsync(chat, text, new InlineKeyboard().Row(Menu), ct: ct).ConfigureAwait(false);
    }

    private async Task OpenNodeAsync(long chat, long? editMsgId, long tgId, long nodeId, CancellationToken ct)
    {
        var node = _db.GetNode(nodeId);
        if (node == null || node.Archived)
        {
            await ShowAsync(chat, editMsgId, "Этот раздел больше недоступен.", new InlineKeyboard().Row(Menu), ct).ConfigureAwait(false);
            return;
        }

        if (node.IsArticle)
        {
            _db.Log(tgId, "view", node.Id);
            await SendArticleAsync(chat, node, ct).ConfigureAwait(false);
            return;
        }

        _db.Log(tgId, "menu", node.Id);
        var keyboard = new InlineKeyboard();
        foreach (var child in _db.Children(node.Id))
            keyboard.Row(Button.Cb((child.IsArticle ? "" : "📂 ") + child.Title, $"n:{child.Id}"));
        keyboard
            .Row(Back(node.ParentId is { } p ? $"n:{p}" : "m"), Menu)
            .Row(Operator(node.Id));
        var html = $"<b>{H(_db.PathOf(node.Id))}</b>\n\nВыберите операцию:";
        await ShowAsync(chat, editMsgId, html, keyboard, ct).ConfigureAwait(false);
    }

    private async Task SendArticleAsync(long chat, Node node, CancellationToken ct)
    {
        if (node.MediaFileId is { Length: > 0 } fileId)
        {
            try
            {
                await _tg.SendMediaAsync(chat, node.MediaKind ?? "video", fileId, $"<b>{H(node.Title)}</b>", ct: ct).ConfigureAwait(false);
            }
            catch (TgException ex)
            {
                Log($"Видео инструкции {node.Id} не отправилось: {ex.Message}");
            }
        }

        var html = new StringBuilder();
        html.Append("<b>").Append(H(node.Title)).Append("</b>\n");
        if (node.ParentId is { } parent)
            html.Append("<i>").Append(H(_db.PathOf(parent))).Append("</i>\n");
        html.Append('\n').Append(H(string.IsNullOrWhiteSpace(node.Text) ? "Инструкция готовится." : node.Text));

        var keyboard = new InlineKeyboard();
        if (!string.IsNullOrWhiteSpace(node.VideoUrl))
            keyboard.Row(Button.Link("▶️ Смотреть видео", node.VideoUrl));
        keyboard
            .Row(Button.Cb("✅ Помогло", $"h:{node.Id}"), Button.Cb("❌ Мне не помогло", $"nh:{node.Id}"))
            .Row(Operator(node.Id))
            .Row(Back(node.ParentId is { } p ? $"n:{p}" : "m"), Menu);
        await _tg.SendMessageAsync(chat, html.ToString(), keyboard, ct: ct).ConfigureAwait(false);
    }

    private async Task OnHelpedAsync(long chat, long nodeId, CancellationToken ct)
    {
        var node = _db.GetNode(nodeId);
        var keyboard = new InlineKeyboard();
        if (node?.ParentId is { } parent)
            keyboard.Row(Button.Cb("📂 Другие инструкции раздела", $"n:{parent}"));
        keyboard.Row(Menu);
        await _tg.SendMessageAsync(chat, "Отлично! Если появится ещё вопрос — выберите раздел или просто напишите его.", keyboard, ct: ct)
            .ConfigureAwait(false);
    }

    // ── поиск ───────────────────────────────────────────────────────────────────────

    private async Task StartSearchAsync(long chat, long tgId, CancellationToken ct)
    {
        _db.ClearSession(tgId);
        await _tg.SendMessageAsync(chat,
            "🔎 Напишите вопрос своими словами. Например: <i>как сделать возврат</i>, <i>где остатки товара</i>, <i>не печатается чек</i>.",
            new InlineKeyboard().Row(Menu), ct: ct).ConfigureAwait(false);
    }

    private async Task RunSearchAsync(Message m, string query, CancellationToken ct)
    {
        var tgId = m.From!.Id;
        var hits = Search.Find(_db.AllArticles(), query);
        _db.Log(tgId, "search", query: query.Length > 200 ? query[..200] : query, hits: hits.Count);

        if (hits.Count > 0)
        {
            var keyboard = new InlineKeyboard();
            foreach (var hit in hits)
                keyboard.Row(Button.Cb("📄 " + hit.Node.Title, $"n:{hit.Node.Id}"));
            keyboard.Row(Button.Cb("✍️ Нет нужного — спросить оператора", "tk:q")).Row(Menu);
            _db.SetSession(tgId, "lastq", JsonSerializer.Serialize(new TicketDraft { Messages = { m.MessageId }, Text = query }));
            await _tg.SendMessageAsync(m.Chat.Id, "Возможно, вы ищете:", keyboard, ct: ct).ConfigureAwait(false);
            return;
        }

        _db.SetSession(tgId, "lastq", JsonSerializer.Serialize(new TicketDraft { Messages = { m.MessageId }, Text = query }));
        await _tg.SendMessageAsync(m.Chat.Id,
            "Не нашёл инструкцию по этому вопросу. Попробуйте сказать иначе или отправьте вопрос оператору — он ответит здесь же.",
            new InlineKeyboard()
                .Row(Button.Cb("✍️ Отправить вопрос оператору", "tk:q"))
                .Row(Menu), ct: ct).ConfigureAwait(false);
    }

    // ── вход в NurCRM ───────────────────────────────────────────────────────────────

    private async Task StartLoginAsync(long chat, long tgId, CancellationToken ct)
    {
        _db.SetSession(tgId, "login_email");
        await _tg.SendMessageAsync(chat, "🔐 Введите логин (e-mail) от NurCRM:",
            new InlineKeyboard().Row(Button.Cb("✖️ Отмена", "m")), ct: ct).ConfigureAwait(false);
    }

    private async Task OnLoginEmailAsync(long chat, long tgId, string text, CancellationToken ct)
    {
        if (!text.Contains('@') || text.Contains(' '))
        {
            await _tg.SendMessageAsync(chat, "Это не похоже на e-mail. Введите логин NurCRM, например <i>market@example.kg</i>.",
                new InlineKeyboard().Row(Button.Cb("✖️ Отмена", "m")), ct: ct).ConfigureAwait(false);
            return;
        }
        _db.SetSession(tgId, "login_pwd", text);
        await _tg.SendMessageAsync(chat, "Теперь пароль. Сообщение с паролем я сразу удалю из чата и нигде его не сохраню.",
            new InlineKeyboard().Row(Button.Cb("✖️ Отмена", "m")), ct: ct).ConfigureAwait(false);
    }

    private async Task OnLoginPasswordAsync(Message m, string email, string password, CancellationToken ct)
    {
        var chat = m.Chat.Id;
        var tgId = m.From!.Id;
        await _tg.DeleteMessageAsync(chat, m.MessageId, ct).ConfigureAwait(false);
        _db.ClearSession(tgId);
        try
        {
            var login = await _crm.LoginAsync(email, password, ct).ConfigureAwait(false);
            _db.SaveLogin(tgId, email, login.Name, login.Company, login.Tariff, login.Sector, login.Role, _vault.Encrypt(login.Refresh));
            _db.Log(tgId, "login");
            await _tg.SendMessageAsync(chat,
                $"✅ Вход выполнен.\n\nКомпания: <b>{H(login.Company)}</b>\nТариф: {H(login.Tariff)} · {H(login.Sector)}\nВы: {H(login.Name)} ({H(login.Role)})",
                new InlineKeyboard().Row(Button.Cb("📊 Финансовый отчёт", "rep")).Row(Menu), ct: ct).ConfigureAwait(false);
        }
        catch (NurCrmException ex)
        {
            await _tg.SendMessageAsync(chat, "❌ " + H(ex.Message),
                new InlineKeyboard().Row(Button.Cb("Попробовать ещё раз", "login")).Row(Menu), ct: ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await _tg.SendMessageAsync(chat, "❌ Сервер NurCRM сейчас недоступен. Попробуйте чуть позже.",
                new InlineKeyboard().Row(Button.Cb("Попробовать ещё раз", "login")).Row(Menu), ct: ct).ConfigureAwait(false);
        }
    }

    private async Task ShowAccountAsync(long chat, long? editMsgId, BotUser user, CancellationToken ct)
    {
        string html;
        var keyboard = new InlineKeyboard();
        if (user.LoggedIn)
        {
            html = $"""
                👤 <b>Аккаунт NurCRM</b>

                Логин: {H(user.NurCrmEmail)}
                Компания: <b>{H(user.CompanyName)}</b>
                Тариф: {H(user.Tariff)} · {H(user.Sector)}
                Вы: {H(user.NurCrmName)} ({H(user.Role)})
                """;
            keyboard.Row(Button.Cb("📊 Финансовый отчёт", "rep")).Row(Button.Cb("🚪 Выйти", "logout"));
        }
        else
        {
            html = "👤 Вы не вошли в NurCRM.\n\nВход нужен для финансового отчёта, а оператору поддержки он сразу покажет вашу компанию.";
            keyboard.Row(Button.Cb("🔐 Войти в NurCRM", "login"));
        }
        keyboard.Row(Menu);
        await ShowAsync(chat, editMsgId, html, keyboard, ct).ConfigureAwait(false);
    }
}

/// <summary>Черновик обращения, пока клиент пишет и прикладывает скриншоты.</summary>
public sealed class TicketDraft
{
    public long? NodeId { get; set; }
    public List<long> Messages { get; set; } = new();
    public string Text { get; set; } = "";
}
