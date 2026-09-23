using System.Globalization;
using System.IO;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Локально сохранённое состояние кассы (смена, касса) для офлайн-работы.</summary>
public sealed class OfflinePosState
{
    public string? ActiveShiftId { get; set; }

    public string? PosCashboxId { get; set; }

    public string? PosCashboxDisplayName { get; set; }

    public decimal ShiftCashBalance { get; set; }

    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
}

public static class OfflinePosStateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly object SyncRoot = new();

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NurMarketKassa",
            "offline_pos_state.json");

    public static void SaveFromApp(decimal shiftCashBalance = 0)
    {
        var state = new OfflinePosState
        {
            ActiveShiftId = PosApp.ActiveShiftId,
            PosCashboxId = PosApp.PosCashboxId,
            PosCashboxDisplayName = PosApp.PosCashboxDisplayName,
            ShiftCashBalance = shiftCashBalance,
            SavedAt = DateTimeOffset.Now,
        };
        Write(state);
    }

    public static void RestoreToApp()
    {
        var state = TryLoad();
        if (state == null)
            return;

        if (!string.IsNullOrWhiteSpace(state.ActiveShiftId))
            PosApp.ActiveShiftId = state.ActiveShiftId;
        if (!string.IsNullOrWhiteSpace(state.PosCashboxId))
            PosApp.PosCashboxId = state.PosCashboxId;
        if (!string.IsNullOrWhiteSpace(state.PosCashboxDisplayName))
            PosApp.PosCashboxDisplayName = state.PosCashboxDisplayName;
    }

    public static decimal ReadShiftCashBalance()
    {
        var state = TryLoad();
        return state?.ShiftCashBalance ?? 0m;
    }

    public static OfflinePosState? TryLoad()
    {
        lock (SyncRoot)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return null;
                return JsonSerializer.Deserialize<OfflinePosState>(File.ReadAllText(FilePath), JsonOpts);
            }
            catch
            {
                return null;
            }
        }
    }

    private static decimal ReadCurrentShiftBalance() =>
        TryLoad()?.ShiftCashBalance ?? 0m;

    private static void Write(OfflinePosState state)
    {
        lock (SyncRoot)
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts));
            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, null);
            else
                File.Move(tmp, FilePath);
        }
    }
}
