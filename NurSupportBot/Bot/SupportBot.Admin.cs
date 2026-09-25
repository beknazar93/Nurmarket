using System.Text;
using NurSupportBot.Data;
using NurSupportBot.Services;
using NurSupportBot.Telegram;
using static NurSupportBot.Bot.Ui;

namespace NurSupportBot.Bot;

/// <summary>Управление ботом без программиста — прямо в Telegram, только для AdminIds.
/// Видео к инструкции: нажать «🎬 Видео» и просто отправить ролик боту — Telegram хранит его сам.</summary>
public sealed partial class SupportBot
{
    private async Task<bool> OnAdminCommandAsync(Message m, string command, string text, CancellationToken ct)
    {
        var tgId = m.From!.Id;
        if (!IsAdmin(tgId))
            return false;
        switch (command)
        {
            case "/admin":
                _db.ClearSession(tgId);
                await ShowAdminMenuAsync(m.Chat.Id, null, ct).ConfigureAwait(false);
                return true;
            case "/stats":
                await ShowStatsAsync(m.Chat.Id, null, ct).ConfigureAwait(false);
                return true;
            case "/export":
                await ExportKnowledgeBaseAsync(m.Chat.Id, ct).ConfigureAwait(false);
                return true;
        }
        return false;
    }

    private static InlineKeyboard AdminBack(string data) => new InlineKeyboard().Row(Back(data), Button.Cb("🛠 Админ-меню", "a"));

    private async Task ShowAdminMenuAsync(long chat, long? editMsgId, CancellationToken ct)
    {
        var open = _db.Count("SELECT COUNT(*) FROM tickets WHERE status='open'");
        var group = SupportChat is { } g ? $"подключена ({g})" : "не подключена — напишите /setsupport в группе";
        var html = $"""
            🛠 <b>Управление ботом</b>

            Группа поддержки: {H(group)}
            Открытых обращений: {open}

            Команды: /stats — статистика, /export — выгрузить базу инструкций, файл JSON с подписью /import — загрузить.
            """;
        await ShowAsync(chat, editMsgId, html, new InlineKeyboard()
            .Row(Button.Cb("📚 Разделы и инструкции", "a:n:0"))
            .Row(Button.Cb("📨 Открытые обращения", "a:tk"), Button.Cb("📈 Статистика", "a:st"))
            .Row(Button.Cb("☎️ Контакты поддержки", "a:ct"), Button.Cb("📤 Выгрузить базу", "a:exp"))
            .Row(Menu), ct).ConfigureAwait(false);
    }

    private async Task OnAdminCallbackAsync(long chat, long? msgId, long tgId, string[] parts, CancellationToken ct)
    {
        if (parts.Length == 1)
        {
            _db.ClearSession(tgId);
            await ShowAdminMenuAsync(chat, msgId, ct).ConfigureAwait(false);
            return;
        }
        long.TryParse(parts.ElementAtOrDefault(2), out var id);
        switch (parts[1])
        {
            case "n":
                _db.ClearSession(tgId);
                await ShowAdminNodeAsync(chat, msgId, id, ct).ConfigureAwait(false);
                break;
            case "am":
                await AskAdminAsync(chat, tgId, "a:newm", id, "Название нового раздела (например: «Возвраты»):", ct).ConfigureAwait(false);
                break;
            case "aa":
                await AskAdminAsync(chat, tgId, "a:newa", id, "Название новой инструкции (например: «Как оформить возврат»):", ct).ConfigureAwait(false);
                break;
            case "et":
                await AskAdminAsync(chat, tgId, "a:title", id, "Новое название:", ct).ConfigureAwait(false);
                break;
            case "ex":
                await AskAdminAsync(chat, tgId, "a:text", id,
                    "Отправьте текст инструкции целиком одним сообщением (например, «Шаг 1. …» с новой строки каждый шаг).", ct).ConfigureAwait(false);
                break;
            case "ek":
                await AskAdminAsync(chat, tgId, "a:kw", id,
                    "Ключевые слова через запятую — как клиенты спрашивают об этом своими словами (например: возврат, вернуть, отменить чек):", ct)
                    .ConfigureAwait(false);
                break;
            case "ev":
                await AskAdminAsync(chat, tgId, "a:media", id,
                    "Отправьте видео (или гифку, фото, файл) — оно будет показываться перед текстом инструкции. «-» — убрать видео.", ct)
                    .ConfigureAwait(false);
                break;
            case "eu":
                await AskAdminAsync(chat, tgId, "a:url", id,
                    "Ссылка на видео (YouTube и т.п.) — появится кнопка «Смотреть видео». «-» — убрать ссылку.", ct).ConfigureAwait(false);
                break;
            case "up":
            case "dn":
                _db.MoveNode(id, parts[1] == "up" ? -1 : 1);
                await ShowAdminNodeAsync(chat, msgId, _db.GetNode(id)?.ParentId ?? 0, ct).ConfigureAwait(false);
                break;
            case "ar":
                if (_db.GetNode(id) is { } toggle)
                    _db.UpdateNode(id, "archived", toggle.Archived ? 0 : 1);
                await ShowAdminNodeAsync(chat, msgId, id, ct).ConfigureAwait(false);
                break;
            case "rm":
                if (_db.GetNode(id) is { } doomed)
                {
                    await ShowAsync(chat, msgId,
                        $"Удалить «{H(doomed.Title)}»" + (doomed.IsArticle ? "?" : " вместе со всеми инструкциями внутри?")
                        + "\n\nЕсли инструкция просто устарела — лучше «🗄 В архив»: её можно вернуть.",
                        new InlineKeyboard().Row(Button.Cb("🗑 Да, удалить", $"a:rmy:{id}"), Button.Cb("Отмена", $"a:n:{id}")), ct)
                        .ConfigureAwait(false);
                }
                break;
            case "rmy":
                var parent = _db.GetNode(id)?.ParentId ?? 0;
                _db.DeleteNode(id);
                await ShowAdminNodeAsync(chat, msgId, parent, ct).ConfigureAwait(false);
                break;
            case "tk":
                await ShowOpenTicketsAsync(chat, msgId, ct).ConfigureAwait(false);
                break;
            case "st":
                await ShowStatsAsync(chat, msgId, ct).ConfigureAwait(false);
                break;
            case "ct":
                await ShowContactsAsync(chat, msgId, ct).ConfigureAwait(false);
                break;
            case "cp":
                await AskAdminAsync(chat, tgId, "a:phone", 0, "Телефон поддержки (как показывать клиентам). «-» — убрать.", ct).ConfigureAwait(false);
                break;
            case "cu":
                await AskAdminAsync(chat, tgId, "a:user", 0,
                    "Username оператора в Telegram без @ (например: nbs_support) — появится кнопка «Написать оператору». «-» — убрать.", ct)
                    .ConfigureAwait(false);
                break;
            case "exp":
                await ExportKnowledgeBaseAsync(chat, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task AskAdminAsync(long chat, long tgId, string state, long id, string prompt, CancellationToken ct)
    {
        _db.SetSession(tgId, state, id.ToString(Ru));
        await _tg.SendMessageAsync(chat, "✏️ " + prompt,
            new InlineKeyboard().Row(Button.Cb("✖️ Отмена", state is "a:phone" or "a:user" ? "a:ct" : $"a:n:{id}")), ct: ct)
            .ConfigureAwait(false);
    }

    private async Task OnAdminInputAsync(Message m, string state, string? data, CancellationToken ct)
    {
        var chat = m.Chat.Id;
        var tgId = m.From!.Id;
        long.TryParse(data, out var id);
        var text = (m.Text ?? "").Trim();
        var clear = text == "-";

        switch (state)
        {
            case "a:newm":
            case "a:newa":
                if (text.Length == 0)
                    return;
                var newId = _db.AddNode(id == 0 ? null : id, text, state == "a:newa");
                _db.ClearSession(tgId);
                // Сразу открываем созданное: в новый раздел обычно тут же добавляют инструкции,
                // в новую инструкцию — текст и видео.
                await ShowAdminNodeAsync(chat, null, newId, ct).ConfigureAwait(false);
                return;
            case "a:title":
                if (text.Length == 0)
                    return;
                _db.UpdateNode(id, "title", text);
                break;
            case "a:text":
                if (text.Length == 0)
                    return;
                _db.UpdateNode(id, "text", text);
                break;
            case "a:kw":
                _db.UpdateNode(id, "keywords", clear ? null : text);
                break;
            case "a:url":
                if (!clear && !text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    await _tg.SendMessageAsync(chat, "Нужна ссылка, начинающаяся с http. Или «-», чтобы убрать.", ct: ct).ConfigureAwait(false);
                    return;
                }
                _db.UpdateNode(id, "video_url", clear ? null : text);
                break;
            case "a:media":
                var (kind, fileId) = m.Video != null ? ("video", m.Video.FileId)
                    : m.Animation != null ? ("animation", m.Animation.FileId)
                    : m.Photo is { Count: > 0 } ? ("photo", m.Photo.OrderByDescending(p => p.Width).First().FileId)
                    : m.Document != null ? ("document", m.Document.FileId)
                    : (null, null);
                if (clear)
                {
                    _db.UpdateNode(id, "media_file_id", null);
                    _db.UpdateNode(id, "media_kind", null);
                }
                else if (fileId == null)
                {
                    await _tg.SendMessageAsync(chat, "Отправьте именно видео, гифку, фото или файл. Или «-», чтобы убрать.", ct: ct).ConfigureAwait(false);
                    return;
                }
                else
                {
                    _db.UpdateNode(id, "media_kind", kind);
                    _db.UpdateNode(id, "media_file_id", fileId);
                }
                break;
            case "a:phone":
                _db.SetSetting("support_phone", clear ? null : text);
                _db.ClearSession(tgId);
                await ShowContactsAsync(chat, null, ct).ConfigureAwait(false);
                return;
            case "a:user":
                _db.SetSetting("support_username", clear ? null : text.TrimStart('@'));
                _db.ClearSession(tgId);
                await ShowContactsAsync(chat, null, ct).ConfigureAwait(false);
                return;
            default:
                _db.ClearSession(tgId);
                return;
        }
        _db.ClearSession(tgId);
        await _tg.SendMessageAsync(chat, "✅ Сохранено.", ct: ct).ConfigureAwait(false);
        await ShowAdminNodeAsync(chat, null, id, ct).ConfigureAwait(false);
    }

    private async Task ShowAdminNodeAsync(long chat, long? editMsgId, long id, CancellationToken ct)
    {
        var node = id == 0 ? null : _db.GetNode(id);
        if (id != 0 && node == null)
        {
            await ShowAdminMenuAsync(chat, editMsgId, ct).ConfigureAwait(false);
            return;
        }

        var keyboard = new InlineKeyboard();
        var html = new StringBuilder();

        if (node is { IsArticle: true })
        {
            html.Append("📄 <b>").Append(H(node.Title)).Append("</b>").Append(node.Archived ? " (в архиве)" : "").Append('\n');
            html.Append("<i>").Append(H(_db.PathOf(node.Id))).Append("</i>\n\n");
            html.Append("🎬 Видео: ").Append(node.MediaFileId != null ? node.MediaKind : "нет").Append('\n');
            html.Append("🔗 Ссылка: ").Append(H(node.VideoUrl ?? "нет")).Append('\n');
            html.Append("🔑 Ключевые слова: ").Append(H(node.Keywords ?? "нет")).Append('\n');
            html.Append("🕒 Изменено: ").Append(H(node.UpdatedAt)).Append(" (UTC)\n\n");
            var text = node.Text ?? "(текста нет)";
            html.Append(H(text.Length > 1500 ? text[..1500] + "…" : text));
            keyboard
                .Row(Button.Cb("✏️ Название", $"a:et:{id}"), Button.Cb("📝 Текст", $"a:ex:{id}"))
                .Row(Button.Cb("🎬 Видео", $"a:ev:{id}"), Button.Cb("🔗 Ссылка на видео", $"a:eu:{id}"))
                .Row(Button.Cb("🔑 Ключевые слова", $"a:ek:{id}"), Button.Cb("👁 Как видит клиент", $"n:{id}"));
        }
        else
        {
            html.Append("📚 <b>").Append(H(node == null ? "Все разделы" : _db.PathOf(node.Id))).Append("</b>")
                .Append(node?.Archived == true ? " (в архиве)" : "").Append("\n\n");
            var children = _db.Children(node?.Id, includeArchived: true);
            html.Append(children.Count == 0 ? "Пока пусто — добавьте раздел или инструкцию." : "Нажмите, чтобы открыть:");
            foreach (var child in children)
            {
                var icon = child.Archived ? "🗄 " : child.IsArticle ? (child.MediaFileId != null ? "🎬 " : "📄 ") : "📁 ";
                keyboard.Row(Button.Cb(icon + child.Title, $"a:n:{child.Id}"));
            }
            keyboard.Row(Button.Cb("➕ Раздел", $"a:am:{id}"), Button.Cb("➕ Инструкция", $"a:aa:{id}"));
            if (node != null)
                keyboard.Row(Button.Cb("✏️ Название", $"a:et:{id}"));
        }

        if (node != null)
        {
            keyboard
                .Row(Button.Cb("⬆️ Выше", $"a:up:{id}"), Button.Cb("⬇️ Ниже", $"a:dn:{id}"))
                .Row(Button.Cb(node.Archived ? "♻️ Вернуть из архива" : "🗄 В архив", $"a:ar:{id}"), Button.Cb("🗑 Удалить", $"a:rm:{id}"))
                .Row(Back($"a:n:{node.ParentId ?? 0}"), Button.Cb("🛠 Админ-меню", "a"));
        }
        else
        {
            keyboard.Row(Button.Cb("🛠 Админ-меню", "a"));
        }
        await ShowAsync(chat, editMsgId, html.ToString(), keyboard, ct).ConfigureAwait(false);
    }

    private async Task ShowContactsAsync(long chat, long? editMsgId, CancellationToken ct)
    {
        var html = $"""
            ☎️ <b>Контакты поддержки</b> (видят клиенты на экране «Связаться с оператором»)

            Телефон: {H(_db.GetSetting("support_phone") ?? "не указан")}
            Telegram оператора: {H(_db.GetSetting("support_username") is { Length: > 0 } u ? "@" + u : "не указан")}
            Группа поддержки: {(SupportChat is { } g ? g.ToString(Ru) : "не подключена — добавьте бота в группу и напишите там /setsupport")}
            """;
        await ShowAsync(chat, editMsgId, html, new InlineKeyboard()
            .Row(Button.Cb("📞 Телефон", "a:cp"), Button.Cb("💬 Telegram оператора", "a:cu"))
            .Row(Button.Cb("🛠 Админ-меню", "a")), ct).ConfigureAwait(false);
    }

    private async Task ShowOpenTicketsAsync(long chat, long? editMsgId, CancellationToken ct)
    {
        var tickets = _db.OpenTickets();
        var html = new StringBuilder("📨 <b>Открытые обращения</b>\n\n");
        if (tickets.Count == 0)
            html.Append("Открытых обращений нет.");
        foreach (var t in tickets)
        {
            var user = _db.GetUser(t.TgId);
            html.Append("№").Append(t.Id).Append(" · ").Append(H(t.CreatedAt)).Append(" UTC\n")
                .Append(H(user?.CompanyName ?? user?.FirstName ?? t.TgId.ToString(Ru))).Append(" · ").Append(H(t.Topic)).Append('\n')
                .Append("<i>").Append(H(t.Text.Length > 120 ? t.Text[..120] + "…" : t.Text)).Append("</i>\n\n");
        }
        html.Append("Отвечать и закрывать — в группе поддержки.");
        await ShowAsync(chat, editMsgId, html.ToString(), AdminBack("a"), ct).ConfigureAwait(false);
    }

    /// <summary>Статистика за 30 дней: что открывают, что не помогает, что ищут и не находят.</summary>
    private async Task ShowStatsAsync(long chat, long? editMsgId, CancellationToken ct)
    {
        const string since = "datetime('now','-30 days')";
        var users = _db.Count("SELECT COUNT(*) FROM users");
        var active7 = _db.Count("SELECT COUNT(*) FROM users WHERE last_seen >= datetime('now','-7 days')");
        var active30 = _db.Count($"SELECT COUNT(*) FROM users WHERE last_seen >= {since}");
        var logged = _db.Count("SELECT COUNT(*) FROM users WHERE refresh_enc IS NOT NULL");
        long Events(string kind) => _db.Count($"SELECT COUNT(*) FROM events WHERE kind=$k AND ts >= {since}", ("$k", kind));
        var views = Events("view");
        var helped = Events("helped");
        var notHelped = Events("not_helped");
        var tickets = Events("ticket");
        var searches = Events("search");
        var solvedShare = helped + tickets > 0 ? 100.0 * helped / (helped + tickets) : 0;

        var html = new StringBuilder();
        html.Append("📈 <b>Статистика за 30 дней</b>\n\n");
        html.Append($"👥 Пользователей: {users} (активны за 7 дней: {active7}, за 30: {active30}, вошли в NurCRM: {logged})\n");
        html.Append($"📄 Просмотров инструкций: {views}\n");
        html.Append($"✅ «Помогло»: {helped} · ❌ «Не помогло»: {notHelped}\n");
        html.Append($"📨 Обращений к оператору: {tickets} (открыто сейчас: {_db.Count("SELECT COUNT(*) FROM tickets WHERE status='open'")})\n");
        html.Append($"🎯 Решено без оператора: {solvedShare:0}% («помогло» против обращений)\n");
        html.Append($"🔎 Поисков: {searches}\n");

        void Section(string title, List<(string Label, long Count)> rows)
        {
            if (rows.Count == 0)
                return;
            html.Append("\n<b>").Append(title).Append("</b>\n");
            foreach (var (label, count) in rows)
                html.Append("• ").Append(H(label)).Append(" — ").Append(count).Append('\n');
        }

        Section("Разделы, которые открывают чаще всего", _db.Top(
            $"SELECT n.title, COUNT(*) FROM events e JOIN nodes n ON n.id=e.node_id WHERE e.kind='menu' AND n.parent_id IS NULL AND e.ts >= {since} GROUP BY n.id ORDER BY 2 DESC LIMIT 5"));
        Section("Самые просматриваемые инструкции", _db.Top(
            $"SELECT n.title, COUNT(*) FROM events e JOIN nodes n ON n.id=e.node_id WHERE e.kind='view' AND e.ts >= {since} GROUP BY n.id ORDER BY 2 DESC LIMIT 10"));
        Section("Чаще всего «не помогло»", _db.Top(
            $"SELECT n.title, COUNT(*) FROM events e JOIN nodes n ON n.id=e.node_id WHERE e.kind='not_helped' AND e.ts >= {since} GROUP BY n.id ORDER BY 2 DESC LIMIT 5"));
        Section("После каких инструкций обращаются к оператору", _db.Top(
            $"SELECT n.title, COUNT(*) FROM events e JOIN nodes n ON n.id=e.node_id WHERE e.kind='ticket' AND e.ts >= {since} GROUP BY n.id ORDER BY 2 DESC LIMIT 5"));
        Section("Частые вопросы в поиске", _db.Top(
            $"SELECT lower(query), COUNT(*) FROM events WHERE kind='search' AND ts >= {since} GROUP BY lower(query) ORDER BY 2 DESC LIMIT 8"));
        Section("Искали, но не нашли (кандидаты в новые инструкции)", _db.Top(
            $"SELECT lower(query), COUNT(*) FROM events WHERE kind='search' AND hits=0 AND ts >= {since} GROUP BY lower(query) ORDER BY 2 DESC LIMIT 8"));

        var text = html.ToString();
        if (text.Length > 4000)
            text = text[..4000] + "…";
        await ShowAsync(chat, editMsgId, text, AdminBack("a"), ct).ConfigureAwait(false);
    }

    private async Task ExportKnowledgeBaseAsync(long chat, CancellationToken ct)
    {
        var json = KnowledgeBase.Export(_db);
        await _tg.SendDocumentBytesAsync(chat, $"kb-{DateTime.Now:yyyy-MM-dd-HHmm}.json", Encoding.UTF8.GetBytes(json),
            "База инструкций. Поправьте файл и отправьте его боту с подписью /import — база заменится целиком.", ct).ConfigureAwait(false);
    }

    private async Task ImportKnowledgeBaseAsync(Message m, CancellationToken ct)
    {
        var chat = m.Chat.Id;
        try
        {
            var bytes = await _tg.DownloadFileAsync(m.Document!.FileId, ct).ConfigureAwait(false);
            var json = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
            // Сначала страховочная копия текущей базы — вдруг в файле ошибка по смыслу.
            await _tg.SendDocumentBytesAsync(chat, $"kb-backup-{DateTime.Now:yyyy-MM-dd-HHmm}.json",
                Encoding.UTF8.GetBytes(KnowledgeBase.Export(_db)), "Копия базы ДО загрузки — на случай, если нужно вернуть.", ct)
                .ConfigureAwait(false);
            var count = KnowledgeBase.Import(_db, json, replace: true);
            await _tg.SendMessageAsync(chat, $"✅ База инструкций заменена: {count} разделов и инструкций.",
                new InlineKeyboard().Row(Button.Cb("📚 Открыть", "a:n:0")), ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            await _tg.SendMessageAsync(chat, "❌ Файл не разобрался как база инструкций: " + H(ex.Message), ct: ct).ConfigureAwait(false);
        }
    }
}
