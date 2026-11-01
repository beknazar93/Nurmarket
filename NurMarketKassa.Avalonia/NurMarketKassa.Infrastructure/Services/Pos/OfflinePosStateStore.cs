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

    /// <summary>2026-11-01 (К-02, приёмка): кассир, которому принадлежит сохранённая смена. Нужен, чтобы смену кассира А не открыли на сервере на кассира Б.
    /// null — файл старого вида (владелец неизвестен): серверную смену автоматически не открываем.</summary>
    public string? CashierId { get; set; }

    /// <summary>2026-11-01 (К-02, code-review): сумма, с которой кассир открыл ОФЛАЙН-смену. Фиксируется один раз при открытии и больше не перезаписывается
    /// (ShiftCashBalance — «текущий остаток», его обновляют ответы сервера и SaveFromApp). Серверная смена для офлайн-смены открывается именно этой суммой;
    /// null (старый файл) — автоматически не открываем.</summary>
    public decimal? OfflineOpeningCash { get; set; }

    /// <summary>2026-11-01 (code-review): id офлайн-смены («offline-shift-…»), которую касса передала серверу (ActiveShiftId заменён на серверный GUID). Нужен, чтобы при
    /// закрытии смены чеки и внесения/изъятия, оформленные под офлайн-id, проверялись и ПОСЛЕ подмены id — до полной досылки. null — смена не была офлайн-сменой.</summary>
    public string? OriginOfflineShiftId { get; set; }

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
            NurMarketKassa.Services.AppMode.DataFolderName,
            "offline_pos_state.json");

    public static void SaveFromApp(decimal shiftCashBalance = 0)
    {
        // 2026-11-01 (К-02, приёмка): та же смена, что уже сохранена, — владелец остаётся прежним (даже если файл переписал другой вошедший кассир);
        // у старого файла без владельца он так и остаётся неизвестным. Новая смена — текущий кассир.
        var prior = TryLoad();
        var sameShift = prior is not null && !string.IsNullOrEmpty(PosApp.ActiveShiftId)
                        && string.Equals(prior.ActiveShiftId, PosApp.ActiveShiftId, StringComparison.OrdinalIgnoreCase);
        var state = new OfflinePosState
        {
            CashierId = sameShift ? prior!.CashierId : PosApp.CurrentUserId,
            OfflineOpeningCash = sameShift ? prior!.OfflineOpeningCash : null,
            OriginOfflineShiftId = sameShift ? prior!.OriginOfflineShiftId : null,
            ActiveShiftId = PosApp.ActiveShiftId,
            PosCashboxId = PosApp.PosCashboxId,
            PosCashboxDisplayName = PosApp.PosCashboxDisplayName,
            ShiftCashBalance = shiftCashBalance,
            SavedAt = DateTimeOffset.Now,
        };
        Write(state);
    }

    /// <summary>2026-11-01 (К-02, code-review): запомнить сумму открытия только что открытой офлайн-смены (вызывать после SaveFromApp при открытии).</summary>
    public static void RememberOfflineOpening(decimal openingCash)
    {
        var state = TryLoad();
        if (state is null || string.IsNullOrEmpty(PosApp.ActiveShiftId)
            || !string.Equals(state.ActiveShiftId, PosApp.ActiveShiftId, StringComparison.OrdinalIgnoreCase))
            return;
        state.OfflineOpeningCash = openingCash;
        Write(state);
    }

    /// <summary>2026-11-01 (code-review): офлайн-смена передана серверу — запомнить исходный офлайн-id при серверной смене (вызывать ПОСЛЕ SaveFromApp с новым ActiveShiftId).</summary>
    public static void RememberOriginOfflineShift(string offlineShiftId)
    {
        var state = TryLoad();
        if (state is null || string.IsNullOrEmpty(PosApp.ActiveShiftId)
            || !string.Equals(state.ActiveShiftId, PosApp.ActiveShiftId, StringComparison.OrdinalIgnoreCase))
            return;
        state.OriginOfflineShiftId = offlineShiftId;
        Write(state);
    }

    /// <summary>Исходный офлайн-id для серверной смены <paramref name="serverShiftId"/> (если она была передана из офлайн-смены), иначе null.</summary>
    public static string? OriginOfflineShiftFor(string? serverShiftId)
    {
        var state = TryLoad();
        return state is not null && !string.IsNullOrEmpty(serverShiftId)
               && string.Equals(state.ActiveShiftId, serverShiftId, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrEmpty(state.OriginOfflineShiftId)
            ? state.OriginOfflineShiftId
            : null;
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
