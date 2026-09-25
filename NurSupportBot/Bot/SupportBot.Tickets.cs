using System.Text;
using System.Text.Json;
using NurSupportBot.Data;
using NurSupportBot.Telegram;
using static NurSupportBot.Bot.Ui;

namespace NurSupportBot.Bot;

public sealed partial class SupportBot
{
    /// <summary>«Связаться с оператором»: написать через бота (с контекстом), Telegram оператора, телефон.</summary>
    private async Task ShowOperatorAsync(long chat, long? editMsgId, long? nodeId, CancellationToken ct, bool notHelped = false)
    {
        var html = new StringBuilder();
        html.Append(notHelped ? "Жаль, что инструкция не помогла.\n\n" : "");
        html.Append("<b>Не нашли решение? Свяжитесь с технической поддержкой.</b>\n\n");
        html.Append("Лучше всего — опишите проблему здесь: оператор сразу увидит вашу компанию и раздел, где возник вопрос, и ответит в этом чате.");
        if (_db.GetSetting("support_phone") is { Length: > 0 } phone)
            html.Append("\n\n📞 Телефон поддержки: ").Append(H(phone));

        var keyboard = new InlineKeyboard()
            .Row(Button.Cb("✍️ Описать проблему оператору", nodeId is { } id ? $"tk:new:{id}" : "tk:new"));
        if (_db.GetSetting("support_username") is { Length: > 0 } username)
            keyboard.Row(Button.Link("💬 Написать оператору в Telegram", $"https://t.me/{username.TrimStart('@')}"));
        if (notHelped)
            keyboard.Row(Button.Cb("🔎 Поискать другую инструкцию", "srch"));
        keyboard.Row(Menu);
        await ShowAsync(chat, editMsgId, html.ToString(), keyboard, ct).ConfigureAwait(false);
    }

    private static InlineKeyboard DraftKeyboard => new InlineKeyboard()
        .Row(Button.Cb("📨 Отправить оператору", "tk:send"))
        .Row(Button.Cb("✖️ Отмена", "tk:cancel"));

    private async Task<string?> OnTicketButtonAsync(long chat, long tgId, string[] parts, CancellationToken ct)
    {
        switch (parts.ElementAtOrDefault(1))
        {
            case "new":
            {
                var draft = new TicketDraft { NodeId = parts.Length > 2 && long.TryParse(parts[2], out var n) ? n : null };
                _db.SetSession(tgId, "ticket", JsonSerializer.Serialize(draft));
                await _tg.SendMessageAsync(chat, """
                    ✍️ <b>Опишите проблему</b> одним или несколькими сообщениями.

                    Можно приложить скриншот, фото экрана или видео — так оператор быстрее поймёт, в чём дело.
                    Когда всё отправите, нажмите «Отправить оператору».
                    """, DraftKeyboard, ct: ct).ConfigureAwait(false);
                return null;
            }
            case "q":
            {
                // Вопрос из поиска сразу становится первым сообщением обращения.
                var (state, data) = _db.GetSession(tgId);
                var draft = state == "lastq" && data != null
                    ? JsonSerializer.Deserialize<TicketDraft>(data) ?? new TicketDraft()
                    : new TicketDraft();
                _db.SetSession(tgId, "ticket", JsonSerializer.Serialize(draft));
                await _tg.SendMessageAsync(chat,
                    draft.Messages.Count > 0
                        ? "Ваш вопрос добавлен в обращение. Можно дописать подробности или приложить скриншот, затем нажмите «Отправить оператору»."
                        : "✍️ Опишите проблему и при необходимости приложите скриншот, затем нажмите «Отправить оператору».",
                    DraftKeyboard, ct: ct).ConfigureAwait(false);
                return null;
            }
            case "cancel":
                _db.ClearSession(tgId);
                await ShowMainMenuAsync(chat, null, ct).ConfigureAwait(false);
                return "Обращение отменено";
            case "send":
                return await SubmitTicketAsync(chat, tgId, ct).ConfigureAwait(false);
        }
        return null;
    }

    private async Task AddToTicketDraftAsync(Message m, string? data, CancellationToken ct)
    {
        var draft = data != null ? JsonSerializer.Deserialize<TicketDraft>(data) ?? new TicketDraft() : new TicketDraft();
        if (draft.Messages.Count >= 20)
        {
            await _tg.SendMessageAsync(m.Chat.Id, "Достаточно — нажмите «Отправить оператору».", DraftKeyboard, ct: ct).ConfigureAwait(false);
            return;
        }
        draft.Messages.Add(m.MessageId);
        var piece = m.Text ?? m.Caption;
        if (!string.IsNullOrWhiteSpace(piece))
            draft.Text = (draft.Text.Length > 0 ? draft.Text + "\n" : "") + piece.Trim();
        _db.SetSession(m.From!.Id, "ticket", JsonSerializer.Serialize(draft));
        await _tg.SendMessageAsync(m.Chat.Id, "Добавлено ✅ Можно дописать ещё или нажать «Отправить оператору».", DraftKeyboard,
            replyTo: m.MessageId, ct: ct).ConfigureAwait(false);
    }

    private async Task<string?> SubmitTicketAsync(long chat, long tgId, CancellationToken ct)
    {
        var (state, data) = _db.GetSession(tgId);
        var draft = state == "ticket" && data != null ? JsonSerializer.Deserialize<TicketDraft>(data) : null;
        if (draft is not { Messages.Count: > 0 })
            return "Сначала опишите проблему сообщением";

        var user = _db.GetUser(tgId)!;
        var topic = draft.NodeId is { } node ? _db.PathOf(node) : "Общий вопрос";
        var ticketId = _db.CreateTicket(tgId, draft.NodeId, topic, draft.Text);
        _db.ClearSession(tgId);
        _db.Log(tgId, "ticket", draft.NodeId);

        var header = new StringBuilder();
        header.Append("🆕 <b>Обращение №").Append(ticketId).Append("</b>\n");
        header.Append("Клиент: ").Append(H(string.Join(" ", new[] { user.FirstName, user.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)))));
        if (user.Username is { Length: > 0 } un)
            header.Append(" (@").Append(H(un)).Append(')');
        header.Append('\n');
        if (user.LoggedIn || user.CompanyName != null)
        {
            header.Append("Компания: <b>").Append(H(user.CompanyName)).Append("</b> · ").Append(H(user.Tariff)).Append(" · ").Append(H(user.Sector)).Append('\n');
            header.Append("NurCRM: ").Append(H(user.NurCrmEmail)).Append(" — ").Append(H(user.NurCrmName)).Append(" (").Append(H(user.Role)).Append(")\n");
        }
        else
        {
            header.Append("Компания: не вошёл в NurCRM\n");
        }
        header.Append("Раздел: ").Append(H(topic)).Append('\n');
        if (draft.NodeId is { } viewed)
        {
            var notHelped = _db.Count("SELECT COUNT(*) FROM events WHERE tg_id=$u AND node_id=$n AND kind='not_helped'", ("$u", tgId), ("$n", viewed)) > 0;
            header.Append("Инструкцию смотрел").Append(notHelped ? ", отметил «не помогло»" : "").Append('\n');
        }
        header.Append("\n<i>Ответьте на это сообщение (Ответить / Reply) — ответ уйдёт клиенту. Закрыть — кнопкой или /close в ответ.</i>");

        var targets = SupportChat is { } group ? new List<long> { group } : _config.AdminIds.ToList();
        var delivered = false;
        foreach (var target in targets)
        {
            try
            {
                var headerMsg = await _tg.SendMessageAsync(target, header.ToString(),
                    new InlineKeyboard().Row(Button.Cb("✅ Закрыть обращение", $"tc:{ticketId}")), ct: ct).ConfigureAwait(false);
                if (headerMsg == null)
                    continue;
                if (target == SupportChat)
                    _db.SetTicketHeader(ticketId, headerMsg.MessageId);
                foreach (var msg in draft.Messages)
                {
                    var copy = await _tg.CopyMessageAsync(target, chat, msg, headerMsg.MessageId, ct: ct).ConfigureAwait(false);
                    if (target == SupportChat && copy > 0)
                        _db.MapGroupMessage(copy, ticketId);
                }
                delivered = true;
            }
            catch (TgException ex)
            {
                Log($"Обращение №{ticketId} не доставлено в {target}: {ex.Message}");
            }
        }

        var reply = delivered
            ? $"📨 Обращение №{ticketId} передано в поддержку. Оператор ответит здесь же, в этом чате."
            : $"Обращение №{ticketId} сохранено, но группа поддержки пока не подключена.";
        if (_db.GetSetting("support_phone") is { Length: > 0 } phone)
            reply += $"\nЕсли срочно — звоните: {phone}";
        await _tg.SendMessageAsync(chat, H(reply), new InlineKeyboard().Row(Menu), ct: ct).ConfigureAwait(false);
        return "Отправлено";
    }

    /// <summary>Клиент отвечает оператору: сообщение уходит в группу ответом на обращение.</summary>
    private async Task RelayClientReplyAsync(Message m, string? data, CancellationToken ct)
    {
        if (!long.TryParse(data, out var ticketId) || _db.GetTicket(ticketId) is not { } ticket || SupportChat is not { } group)
        {
            _db.ClearSession(m.From!.Id);
            await ShowMainMenuAsync(m.Chat.Id, null, ct).ConfigureAwait(false);
            return;
        }
        if (ticket.Status != "open")
            _db.ReopenTicket(ticket.Id);

        var note = await _tg.SendMessageAsync(group, $"↩️ Клиент по обращению №{ticket.Id}:", replyTo: ticket.HeaderMsgId, ct: ct).ConfigureAwait(false);
        if (note != null)
            _db.MapGroupMessage(note.MessageId, ticket.Id);
        var copy = await _tg.CopyMessageAsync(group, m.Chat.Id, m.MessageId, note?.MessageId ?? ticket.HeaderMsgId, ct: ct).ConfigureAwait(false);
        if (copy > 0)
            _db.MapGroupMessage(copy, ticket.Id);
        await _tg.SendMessageAsync(m.Chat.Id, "Отправлено оператору ✅",
            new InlineKeyboard().Row(Button.Cb("✅ Вопрос решён", $"tcu:{ticket.Id}")).Row(Menu), ct: ct).ConfigureAwait(false);
    }

    private async Task<string?> CloseTicketByClientAsync(long chat, long tgId, long ticketId, CancellationToken ct)
    {
        _db.ClearSession(tgId);
        if (_db.GetTicket(ticketId) is not { } ticket || ticket.TgId != tgId)
            return null;
        if (ticket.Status == "open")
        {
            _db.CloseTicket(ticketId);
            _db.Log(tgId, "ticket_closed_client", ticket.NodeId);
            if (SupportChat is { } group)
            {
                try
                {
                    await _tg.SendMessageAsync(group, $"✅ Клиент отметил обращение №{ticketId} решённым.", replyTo: ticket.HeaderMsgId, ct: ct)
                        .ConfigureAwait(false);
                }
                catch (TgException)
                {
                }
            }
        }
        await _tg.SendMessageAsync(chat, "Спасибо! Рады, что вопрос решён.", new InlineKeyboard().Row(Menu), ct: ct).ConfigureAwait(false);
        return "Обращение закрыто";
    }

    // ── группа поддержки ────────────────────────────────────────────────────────────

    private async Task OnGroupMessageAsync(Message m, CancellationToken ct)
    {
        var text = (m.Text ?? "").Trim();
        var command = text.StartsWith('/') ? text.Split(' ', '@')[0].ToLowerInvariant() : "";

        if (command == "/setsupport")
        {
            if (!IsAdmin(m.From!.Id))
            {
                await _tg.SendMessageAsync(m.Chat.Id, "Назначить группу поддержки может только администратор бота.", replyTo: m.MessageId, ct: ct)
                    .ConfigureAwait(false);
                return;
            }
            _db.SetSetting("support_chat", m.Chat.Id.ToString(Ru));
            await _tg.SendMessageAsync(m.Chat.Id,
                "✅ Эта группа назначена группой поддержки. Обращения клиентов будут приходить сюда. "
                + "Чтобы ответить клиенту — ответьте (Reply) на сообщение обращения.", ct: ct).ConfigureAwait(false);
            return;
        }
        if (command == "/id")
        {
            await _tg.SendMessageAsync(m.Chat.Id, $"ID группы: <code>{m.Chat.Id}</code>\nВаш ID: <code>{m.From!.Id}</code>", replyTo: m.MessageId, ct: ct)
                .ConfigureAwait(false);
            return;
        }

        if (m.Chat.Id != SupportChat || m.ReplyToMessage is not { } replied)
            return;
        if (_db.TicketByGroupMessage(replied.MessageId) is not { } ticketId || _db.GetTicket(ticketId) is not { } ticket)
            return;

        if (command == "/close")
        {
            await CloseTicketByOperatorAsync(ticket, m.From!, ct).ConfigureAwait(false);
            return;
        }
        if (command.Length > 0)
            return;

        // Ответ оператора — клиенту, с кнопками «Ответить» и «Вопрос решён».
        _db.MapGroupMessage(m.MessageId, ticket.Id);
        if (ticket.Status != "open")
            _db.ReopenTicket(ticket.Id);
        try
        {
            await _tg.SendMessageAsync(ticket.TgId, $"💬 <b>Ответ поддержки</b> по обращению №{ticket.Id}:", ct: ct).ConfigureAwait(false);
            await _tg.CopyMessageAsync(ticket.TgId, m.Chat.Id, m.MessageId, ct: ct).ConfigureAwait(false);
            await _tg.SendMessageAsync(ticket.TgId, "Если остались вопросы — ответьте оператору.",
                new InlineKeyboard()
                    .Row(Button.Cb("↩️ Ответить оператору", $"tr:{ticket.Id}"))
                    .Row(Button.Cb("✅ Вопрос решён", $"tcu:{ticket.Id}"))
                    .Row(Menu), ct: ct).ConfigureAwait(false);
            _db.Log(ticket.TgId, "operator_reply", ticket.NodeId);
        }
        catch (TgException ex)
        {
            await _tg.SendMessageAsync(m.Chat.Id, $"⚠️ Не удалось доставить ответ клиенту: {H(ex.Description)}", replyTo: m.MessageId, ct: ct)
                .ConfigureAwait(false);
        }
    }

    private async Task<string?> OnGroupCallbackAsync(CallbackQuery cb, string data, CancellationToken ct)
    {
        var parts = data.Split(':');
        if (parts[0] == "tc" && parts.Length > 1 && long.TryParse(parts[1], out var id) && _db.GetTicket(id) is { } ticket)
        {
            if (ticket.Status != "open")
                return "Обращение уже закрыто";
            await CloseTicketByOperatorAsync(ticket, cb.From, ct).ConfigureAwait(false);
            return "Закрыто";
        }
        return null;
    }

    private async Task CloseTicketByOperatorAsync(Ticket ticket, User operatorUser, CancellationToken ct)
    {
        _db.CloseTicket(ticket.Id);
        if (SupportChat is { } group)
        {
            await _tg.SendMessageAsync(group, $"✅ Обращение №{ticket.Id} закрыто ({H(operatorUser.DisplayName)}).",
                replyTo: ticket.HeaderMsgId, ct: ct).ConfigureAwait(false);
        }
        try
        {
            await _tg.SendMessageAsync(ticket.TgId,
                $"Обращение №{ticket.Id} закрыто. Если вопрос остался — нажмите «Связаться с оператором».",
                new InlineKeyboard().Row(Operator()).Row(Menu), ct: ct).ConfigureAwait(false);
        }
        catch (TgException)
        {
            // Клиент заблокировал бота — закрываем без уведомления.
        }
    }
}
