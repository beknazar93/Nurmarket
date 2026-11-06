using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-06: подключение окна «Неотправленное» к кассе: какие очереди собираются (источники из Infrastructure + внесения/изъятия из проекта кассы), общая короткая
/// проверка связи и права (кассир видит и отправляет своё, владелец/админ — всё и может убирать). Вызывается при открытии окна и значка; повторный вызов безвреден.</summary>
public static class OutboxSetup
{
    private static readonly object Gate = new();
    private static bool _done;

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_done)
                return;
            _done = true;
        }
        var svc = OutboxService.Default;
        svc.Register(new SalesQueueOutboxSource());
        svc.Register(new OfflineOperationsOutboxSource());
        svc.Register(new ReceivingOutboxSource());
        svc.Register(new ProductOutboxSource());
        svc.Register(new ShiftCloseOutboxSource());
        svc.Register(new CashOperationsOutboxSource());
        svc.ReachCheck ??= async ct =>
        {
            var auth = App.GetRequiredService<IAuthApiService>();
            return await SharedConnectivityCheck.CheckAsync(auth.CanReachApiAsync, "Outbox", ct).ConfigureAwait(false);
        };
    }

    /// <summary>Кто сейчас смотрит окно. Владелец/админ — по роли из профиля (ключ --owner прав не даёт); автономный режим — единственный локальный владелец.</summary>
    public static OutboxAccess CurrentAccess()
    {
        var admin = false;
        try
        {
            if (App.GetRequiredService<IAutonomousAuthService>().IsCurrentSessionAutonomous)
                admin = true;
            else
                admin = PermissionService.IsOwnerOrAdmin(App.GetRequiredService<IAuthApiService>().UserPayload);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Неотправленное: роль не определена, права кассира ({ex.Message}).", "WARNING");
        }
        return new OutboxAccess(admin, NurMarketKassa.PosApp.CurrentUserDisplayName);
    }

    /// <summary>«Войти»: тот же вопрос «Сессия истекла», что показывают окна кассы и владельца (кнопка «Войти» ведёт на экран входа).</summary>
    public static async Task AskSignInAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        // object: на Android окна кассы наследуют своё NurMarketKassa.Window, а не Avalonia Window — проверка типа через object работает в обеих сборках.
        object? main = desktop.MainWindow;
        if (main is OwnerShellWindow owner)
            await SessionExpiredPrompt.AskAsync(owner, owner.SignOutAsync).ConfigureAwait(true);
        else if (main is MainWindow cashier)
            await SessionExpiredPrompt.AskAsync(cashier, cashier.SignInAgainAsync).ConfigureAwait(true);
    }
}

/// <summary>Внесения и изъятия денег кассы, ещё не записанные на сервер (cash_history.json). Отправка — ShiftCashFlowSync.PushPendingAsync: свой замок и сверка по source_id (id операции на кассе),
/// поэтому ручная отправка рядом с автоматикой второй раз ту же операцию не запишет.</summary>
public sealed class CashOperationsOutboxSource : IOutboxSource
{
    public const string SourceName = "cash";
    public string Name => SourceName;

    public IReadOnlyList<OutboxItem> Snapshot()
    {
        IReadOnlyList<(string Id, DateTime At, string Type, decimal Amount, string Comment, string? ShiftId, string UserId)> ops;
        try { ops = ShiftCashOperationsStore.PendingAll(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Неотправленное: внесения/изъятия не прочитаны ({ex.Message}).", "WARNING");
            return Array.Empty<OutboxItem>();
        }
        return ops.Select(o =>
        {
            var withdrawal = NurMarketKassa.Models.CashOperationModel.ResolveKind(o.Type) == NurMarketKassa.Models.CashOperationKind.Withdrawal;
            var offlineShift = (o.ShiftId ?? "").StartsWith("offline-", StringComparison.OrdinalIgnoreCase);
            return new OutboxItem
            {
                Source = SourceName,
                Id = o.Id,
                Kind = OutboxKind.CashOperation,
                Title = (withdrawal ? Tr.T("Изъятие", "Алуу", "Withdrawal", "Para çıkışı", "Olish") : Tr.T("Внесение", "Киргизүү", "Deposit", "Para girişi", "Kiritish"))
                        + (string.IsNullOrWhiteSpace(o.Comment) ? "" : " · " + o.Comment),
                Subtitle = o.UserId,
                Amount = o.Amount,
                CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(o.At, DateTimeKind.Local)),
                Status = OutboxStatus.Waiting,
                Reason = offlineShift
                    ? Tr.T("Смена открыта без связи — операция уйдёт, когда смена появится на сервере.", "Смена байланышсыз ачылган — операция смена серверде пайда болгондо кетет.",
                        "The shift was opened offline — the operation goes once the shift appears on the server.", "Vardiya bağlantısız açıldı — işlem vardiya sunucuda görününce gider.", "Smena aloqasiz ochilgan — operatsiya smena serverda paydo bo'lganda ketadi.")
                    : null,
                Owner = o.UserId,
                CanSend = Guid.TryParse(o.ShiftId, out _),
            };
        }).ToList();
    }

    public async Task SendAsync(string id, CancellationToken ct)
    {
        var op = ShiftCashOperationsStore.PendingAll().FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
        if (op.Id is null || string.IsNullOrWhiteSpace(op.ShiftId))
            return;
        await ShiftCashFlowSync.PushPendingAsync(op.ShiftId, null, ct).ConfigureAwait(false);
    }

    public bool Retry(string id) => false;

    public bool Remove(string id, string who) => false;

    public string? ExportText(string id)
    {
        var op = ShiftCashOperationsStore.PendingAll().FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
        return op.Id is null ? null
            : $"{op.Type}: {op.Amount.ToString("0.00", CultureInfo.InvariantCulture)}\nID операции (ключ source_id): {op.Id}\nСоздана: {op.At:dd.MM.yyyy HH:mm:ss}\nСмена: {op.ShiftId ?? "—"}\nКассир: {op.UserId}\nКомментарий: {op.Comment}";
    }

    public IDisposable Subscribe(Action changed)
    {
        void Handler() => changed();
        PosDataEvents.SalesChanged += Handler;
        return new Unsub(() => PosDataEvents.SalesChanged -= Handler);
    }

    private sealed class Unsub(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
