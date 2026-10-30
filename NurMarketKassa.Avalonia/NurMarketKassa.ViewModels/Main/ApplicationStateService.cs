using System.IO;
using System.Text.Json;
using NurMarketKassa.Services;

namespace NurMarketKassa.ViewModels.Main;

public sealed class ApplicationState
{
    public double? CatalogWidth { get; set; }
    public double? CartWidth { get; set; }
    public CatalogPanelState Catalog { get; set; } = new();
    public BasketPanelState Basket { get; set; } = new();
}

public sealed class CatalogPanelState
{
    public int SelectedTabIndex { get; set; }
    public string SearchText { get; set; } = "";
    public int[] TabPages { get; set; } = [1, 1, 1];
}

public sealed class BasketPanelState
{
    public string ActiveSessionId { get; set; } = "";
    public List<OpenReceiptSessionState> Sessions { get; set; } = [];
}

public sealed class OpenReceiptSessionState
{
    public string Id { get; set; } = "";
    public string CartJson { get; set; } = "{}";

    /// <summary>Когда чек отложили кнопкой «Отложить чек» (null — обычная вкладка).</summary>
    public DateTime? DeferredAt { get; set; }
}

public sealed class ApplicationStateService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly object _syncRoot = new();
    private CancellationTokenSource? _saveDebounceCts;
    private bool _disposed;

    /// <summary>Номер поколения состояния. Отложенная запись запоминает его и молча
    /// отменяется, если поколение успело смениться. Нужно при разделении данных аккаунтов:
    /// иначе корзина прежней компании дописалась бы в state.json уже новой.</summary>
    private static int _generation;

    private static string StateFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            NurMarketKassa.Services.AppMode.DataFolderName,
            "state.json");

    public ApplicationState Load()
    {
        try
        {
            if (!File.Exists(StateFilePath))
                return new ApplicationState();

            // 2026-10-29 (аварийный аудит, п. 7): битый state.json (обрыв питания) не превращается молча в пустое состояние —
            // берём копию .bak (AtomicFile), в журнале строка WARNING.
            var json = AtomicFile.ReadAllText(StateFilePath);
            if (json is null)
                return new ApplicationState();
            return JsonSerializer.Deserialize<ApplicationState>(json, JsonOptions) ?? new ApplicationState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 2026-10-30 (приёмка 1.17.65, блокер 3): state.json занят — это не «пусто». Запись поверх блокируем (_loadBusy), иначе открытые чеки пропадут.
            _loadBusy = true;
            PosLogger.Log($"Application state: файл занят ({ex.Message}) — запись состояния отложена, прежние чеки не затираем.", "WARNING");
            return new ApplicationState();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Application state load failed: {ex}", "WARNING");
            return new ApplicationState();
        }
    }

    private static volatile bool _loadBusy;

    // 2026-10-30 (приёмка 1.17.66, «Важно» 7): «занят» ≠ «пусто». Файл был занят при запуске — в памяти пустое состояние, а в файле могут лежать отложенные чеки.
    // Раньше первая же запись после того, как файл освободился, перезаписывала его этим пустым состоянием. Теперь: пока файл не прочитан — не пишем, держим последнее
    // состояние в памяти (_heldState) и повторяем попытку; прочитался — чеки, которых нет в памяти, дописываются в состояние ПЕРЕД записью (не теряются).
    private static ApplicationState? _heldState;
    private static int _busyRetryScheduled;
    private static int _busyRetries;

    private bool TryFinishBusyLoad(ApplicationState state)
    {
        string? json;
        try
        {
            json = AtomicFile.ReadAllText(StateFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _heldState = state;
            if (_busyRetries == 0)
                PosLogger.Log($"Application state: файл всё ещё занят ({ex.Message}) — состояние держим в памяти, запись после успешного чтения.", "WARNING");
            ScheduleBusyRetry();
            return false;
        }

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var fromFile = JsonSerializer.Deserialize<ApplicationState>(json, JsonOptions);
                var known = new HashSet<string>(state.Basket.Sessions.Select(s => s.Id), StringComparer.Ordinal);
                var restored = 0;
                foreach (var session in fromFile?.Basket?.Sessions ?? [])
                {
                    if (string.IsNullOrEmpty(session.Id) || !known.Add(session.Id))
                        continue;
                    state.Basket.Sessions.Add(session);
                    restored++;
                }

                if (restored > 0)
                    PosLogger.Log($"Application state: файл освободился — чеков из него, которых не было в памяти: {restored}, они сохранены (после перезапуска откроются).", "WARNING");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                PosLogger.Log($"Application state: файл прочитан, но не разобран ({ex.Message}) — запись состояния не выполняется, прежнее содержимое не тронуто.", "WARNING");
                return false;
            }
        }

        _loadBusy = false;
        _heldState = null;
        _busyRetries = 0;
        return true;
    }

    private void ScheduleBusyRetry()
    {
        if (_busyRetries >= 30 || Interlocked.Exchange(ref _busyRetryScheduled, 1) != 0)
            return;
        _busyRetries++;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000).ConfigureAwait(false);
            Interlocked.Exchange(ref _busyRetryScheduled, 0);
            var held = _heldState;
            if (held is not null && _loadBusy && !_disposed)
                Save(held);
        });
    }

    public void Save(ApplicationState state)
    {
        if (_loadBusy && !TryFinishBusyLoad(state))
            return;
        try
        {
            var dir = Path.GetDirectoryName(StateFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            // 2026-10-29: атомарно (tmp → Flush → Replace + .bak): обрыв питания посреди записи не портит открытые чеки.
            AtomicFile.WriteAllText(StateFilePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Application state save failed: {ex}", "WARNING");
        }
    }

    public void SaveDebounced(Func<ApplicationState> stateFactory, int delayMs = 400)
    {
        ApplicationState snapshot;
        try
        {
            snapshot = stateFactory();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Application state capture failed: {ex}", "WARNING");
            return;
        }

        CancellationTokenSource currentCts;
        lock (_syncRoot)
        {
            if (_disposed)
                return;

            _saveDebounceCts?.Cancel();
            _saveDebounceCts = new CancellationTokenSource();
            currentCts = _saveDebounceCts;
        }

        _ = SaveAfterDelayAsync(snapshot, currentCts, delayMs, Volatile.Read(ref _generation));
    }

    /// <summary>Отменяет отложенные записи состояния: всё, что не успело лечь на диск,
    /// относится к прежнему аккаунту и в файлы нового попасть не должно.</summary>
    public static void CancelPendingSaves() => Interlocked.Increment(ref _generation);

    private async Task SaveAfterDelayAsync(
        ApplicationState snapshot,
        CancellationTokenSource cts,
        int delayMs,
        int generation)
    {
        try
        {
            await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
            if (Volatile.Read(ref _generation) != generation)
            {
                PosLogger.Log("Отложенная запись состояния отменена: сменился аккаунт.", "DEBUG");
                return;
            }

            Save(snapshot);
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Application state save debounce superseded.", "DEBUG");
        }
        finally
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_saveDebounceCts, cts))
                    _saveDebounceCts = null;
            }
            cts.Dispose();
        }
    }

    public void CancelPendingSave()
    {
        CancellationTokenSource? pending;
        lock (_syncRoot)
        {
            pending = _saveDebounceCts;
            pending?.Cancel();
            _saveDebounceCts = null;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        CancelPendingSave();
    }
}
