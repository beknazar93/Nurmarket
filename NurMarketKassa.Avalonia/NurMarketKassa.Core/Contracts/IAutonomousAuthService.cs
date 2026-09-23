namespace NurMarketKassa.Core.Contracts;

/// <summary>2026-09-09: автономный (офлайн, без NurCRM) режим кассы — ключ активации
/// проверяется РОВНО ОДИН РАЗ онлайн (см. реализацию), дальше локальный логин/пароль работают
/// полностью без интернета.</summary>
public interface IAutonomousAuthService
{
    /// <summary>Активация уже выполнена на этом ПК (ключ принят).</summary>
    bool IsActivated { get; }

    /// <summary>Локальный аккаунт владельца уже создан (после активации).</summary>
    bool HasLocalAccount { get; }

    /// <summary>Именно ТЕКУЩИЙ запуск кассы сейчас работает в автономном режиме (в отличие от
    /// IsActivated — тот означает лишь "ключ когда-то был принят на этом ПК"; активированный ПК
    /// всё ещё может войти через обычный NurCRM-логин). Используется, чтобы пропускать
    /// 60-часовой монитор офлайна (MainWindow) и серверные вызовы (раздел "Сотрудники").</summary>
    bool IsCurrentSessionAutonomous { get; }

    /// <summary>Единственное действие, которому нужен интернет во всём автономном режиме.</summary>
    Task<(bool Success, string? Error)> ActivateAsync(string activationKey, CancellationToken ct = default);

    /// <summary>Первичное создание локального аккаунта (сразу после активации).</summary>
    (bool Success, string? Error) CreateLocalAccount(string email, string password, string? displayName);

    /// <summary>Обычный локальный вход — без интернета.</summary>
    (bool Success, string? Error, string? DisplayName) LoginLocal(string email, string password);

    /// <summary>2026-09-10: тихое продолжение автономной сессии при перезапуске кассы — тот же
    /// уровень доверия, что и обычный автовход по сохранённому токену NurCRM (пароль повторно не
    /// спрашивается). true, только если последний успешный вход на этом ПК был локальным
    /// (LoginLocal) и локальный аккаунт всё ещё существует. Вызывается из App.axaml.cs ДО обычного
    /// IOnlineOfflineAuthenticationService.AutoLoginAsync — без этого метода касса на каждом
    /// перезапуске сначала откатывалась бы на старую (часто протухшую) NurCRM-сессию, и экран
    /// «Работать автономно» пришлось бы проходить заново после каждого закрытия приложения.</summary>
    (bool Success, string? Email, string? DisplayName) TryAutoResume();

    /// <summary>Сброс IsCurrentSessionAutonomous — вызывается при выходе из кассы и при успешном
    /// входе через обычный NurCRM-логин (значит текущая сессия больше не автономная). Также снимает
    /// признак "продолжать автономно при следующем запуске" (TryAutoResume).</summary>
    void EndAutonomousSession();

    /// <summary>2026-09-13, по просьбе владельца ("с онлайна на офлайн переходил с базой") —
    /// одноразовая пометка: следующий LoginLocal НЕ должен чистить локальный каталог (обычно
    /// он это делает при явном входе, см. её комментарий) — вызывающий код должен ПЕРЕД этим
    /// полностью досинхронизировать каталог с сервера, чтобы перенести именно текущий онлайн-
    /// аккаунт, а не то, что случайно уже было закэшировано.</summary>
    void MarkCatalogPreservedForNextLogin();
}
