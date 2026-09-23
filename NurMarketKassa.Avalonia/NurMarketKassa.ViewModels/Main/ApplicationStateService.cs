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

    private static string StateFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NurMarketKassa",
            "state.json");

    public ApplicationState Load()
    {
        try
        {
            if (!File.Exists(StateFilePath))
                return new ApplicationState();

            var json = File.ReadAllText(StateFilePath);
            return JsonSerializer.Deserialize<ApplicationState>(json, JsonOptions) ?? new ApplicationState();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Application state load failed: {ex}", "WARNING");
            return new ApplicationState();
        }
    }

    public void Save(ApplicationState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(StateFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(state, JsonOptions));
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

        _ = SaveAfterDelayAsync(snapshot, currentCts, delayMs);
    }

    private async Task SaveAfterDelayAsync(
        ApplicationState snapshot,
        CancellationTokenSource cts,
        int delayMs)
    {
        try
        {
            await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
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
