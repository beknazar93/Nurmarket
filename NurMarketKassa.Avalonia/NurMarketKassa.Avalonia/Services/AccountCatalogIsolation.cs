namespace NurMarketKassa.Services;

/// <summary>
/// Этот файл изолирует локальный каталог товаров при смене учётной записи кассира:
/// очищает SQLite-кэш и помечает необходимость принудительной синхронизации с API сайта.
/// </summary>
public static class AccountCatalogIsolation
{
    public static bool RequireForcedCatalogSync { get; private set; }

    public static void PrepareForAuthenticatedUser(string email, string? userId)
    {
        var key = BuildUserKey(email, userId);
        var previous = UserPreferences.Instance.LastCatalogUserKey;

        if (!string.IsNullOrEmpty(previous)
            && string.Equals(previous, key, StringComparison.OrdinalIgnoreCase))
        {
            RequireForcedCatalogSync = false;
            return;
        }

        PosLogger.Log(
            $"Смена пользователя: «{previous ?? "—"}» → «{key}». Очистка локального каталога.",
            "AUTH");

        ClearLocalCatalogData();
        UserPreferences.Instance.LastCatalogUserKey = key;
        UserPreferences.Instance.SaveToDisk();
        RequireForcedCatalogSync = true;
    }

    public static void ClearForcedCatalogSyncFlag() => RequireForcedCatalogSync = false;

    public static void ClearLocalCatalogData()
    {
        try { LocalProductRepository.Instance.ClearAll(); } catch (Exception ex) { PosLogger.Log($"CATALOG clear failed: {ex.Message}", "CATALOG"); }
    }

    private static string BuildUserKey(string email, string? userId)
    {
        var mail = (email ?? "").Trim().ToLowerInvariant();
        var id = (userId ?? "").Trim();
        return string.IsNullOrEmpty(id) ? mail : $"{mail}|{id}";
    }
}

/// <summary>
/// Этот файл открывает и закрывает экранную клавиатуру Avalonia-кассы
/// для ввода текста на сенсорных терминалах.
/// </summary>
public static class TouchKeyboard
{
    public static void TryShow(Avalonia.Controls.Window? owner = null) => NurMarketKassa.AvaloniaHost.Views.Dialogs.FrmKeyboard.ShowKeyboard(owner);
    public static void ShowOnDemand(Avalonia.Controls.Window? owner = null) => NurMarketKassa.AvaloniaHost.Views.Dialogs.FrmKeyboard.ShowKeyboard(owner);
    public static void Close() => NurMarketKassa.AvaloniaHost.Views.Dialogs.FrmKeyboard.KillKeyboard();
}

/// <summary>
/// Этот файл передаёт нажатия виртуальной клавиатуры в активное текстовое поле:
/// вставка символов, Backspace, Delete и навигация по вводу.
/// </summary>
public static class VirtualKeyboardInput
{
    private static WeakReference<Avalonia.Controls.TextBox>? _lastInputTarget;

    public static void RememberInputTarget(Avalonia.Input.IInputElement? element)
    {
        if (element is Avalonia.Controls.TextBox tb)
            _lastInputTarget = new WeakReference<Avalonia.Controls.TextBox>(tb);
    }

    private static Avalonia.Controls.TextBox? GetTarget()
    {
        if (_lastInputTarget?.TryGetTarget(out var cached) == true && cached.IsEnabled && cached.IsVisible)
            return cached;
        return (global::Avalonia.Application.Current?.ApplicationLifetime as global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow?.FocusManager?.GetFocusedElement() as Avalonia.Controls.TextBox;
    }

    public static void InsertText(string text)
    {
        var tb = GetTarget();
        if (tb is null || string.IsNullOrEmpty(text)) return;
        var caret = tb.CaretIndex >= 0 ? tb.CaretIndex : tb.Text?.Length ?? 0;
        tb.Text = tb.Text?.Insert(caret, text) ?? text;
        tb.CaretIndex = caret + text.Length;
    }

    public static void SendBackspace()
    {
        var tb = GetTarget();
        if (tb is null || string.IsNullOrEmpty(tb.Text)) return;
        var caret = tb.CaretIndex;
        if (caret <= 0) return;
        tb.Text = tb.Text.Remove(caret - 1, 1);
        tb.CaretIndex = caret - 1;
    }

    public static void SendDelete()
    {
        var tb = GetTarget();
        if (tb is null || string.IsNullOrEmpty(tb.Text)) return;
        var caret = tb.CaretIndex;
        if (caret >= tb.Text.Length) return;
        tb.Text = tb.Text.Remove(caret, 1);
        tb.CaretIndex = caret;
    }

    public static void SendEnter() { }
    public static void SendTab() { }
}

/// <summary>
/// Этот файл отправляет запросы на возврат продажи через REST API сайта:
/// полный возврат чека или возврат отдельных позиций.
/// </summary>
public static class PosRefundService
{
    public static async Task RefundWholeSaleAsync(
        Api.ISalesApiService api,
        string saleId,
        string reason,
        string? cashboxId,
        CancellationToken ct = default)
    {
        await api.PosReturnWholeSaleAsync(saleId, reason, ct).ConfigureAwait(false);
    }

    public static async Task RefundLinesAsync(
        Api.ISalesApiService api,
        string saleId,
        IReadOnlyList<PosRefundLineRequest> lines,
        string reason,
        string? cashboxId,
        CancellationToken ct = default)
    {
        // Один запрос на весь возврат — тот же, что делает сайт: POST pos/sales/{id}/return/
        // с массивом items. Раньше здесь был цикл по позициям, и каждая уходила в
        // cart-item-deletions/get/, которого на сервере нет (404) — возврат не работал никогда,
        // а кассир получал «Не удалось вернуть позицию: <название товара>».
        ct.ThrowIfCancellationRequested();
        await api.PosReturnSaleAsync(saleId, lines, ct).ConfigureAwait(false);
    }
}
