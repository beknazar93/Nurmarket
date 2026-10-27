using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-27, пакет К-П1 (ТЗ «Мобилка и касса», 3A): статусы чека телефона в журнале напечатанного.</summary>
public static class PhonePrintStatus
{
    /// <summary>Продажа была на сервере ещё до включения наблюдения — не печатается и в список не попадает.</summary>
    public const string Ignored = "ignored";
    /// <summary>Запись сделана ДО отправки на принтер. Если касса перезапустилась — исход неизвестен.</summary>
    public const string Printing = "printing";
    public const string Printed = "printed";
    /// <summary>Принтер отказал (или исход неизвестен после перезапуска) — в списке «Не напечатаны», повтор кнопкой.</summary>
    public const string Failed = "failed";
    /// <summary>Чек старше 3 минут (телефон был без связи, касса не работала) — автоматически не печатается, в списке.</summary>
    public const string Skipped = "skipped";
}

/// <summary>Одна запись журнала: ключ задания печати — id продажи на сервере.</summary>
public sealed class PhonePrintEntry
{
    public string SaleId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? CashboxId { get; set; }
    public string? Number { get; set; }
    public string? Staff { get; set; }
    public decimal Total { get; set; }
    public DateTime? SaleTimeUtc { get; set; }
    public DateTime DiscoveredUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public int Attempts { get; set; }
    /// <summary>Чек мог уже выйти на бумагу (исход печати неизвестен) — повтор печатается с пометкой «повторная печать».</summary>
    public bool MaybePrinted { get; set; }
    public string? Error { get; set; }

    public PhonePrintEntry Clone() => (PhonePrintEntry)MemberwiseClone();
}

/// <summary>2026-10-27, пакет К-П1: журнал напечатанного чеков с телефона — файл
/// %APPDATA%\NurMarketKassa\data\phone-print-journal.json, хранится 7 дней. Защита от двойной печати: запись со статусом
/// «printing» делается ДО отправки на принтер (если записать не удалось — не печатаем), после — «printed»/«failed».
/// При перезапуске кассы «printing» автоматически не перепечатывается: запись становится «failed» (исход неизвестен)
/// и попадает в список «Не напечатаны». Папка data переносится при смене компании (AccountDataIsolation), поэтому
/// журнал не смешивается между магазинами. Все методы потокобезопасны.</summary>
public sealed class PhoneSalePrintJournal
{
    public const int KeepDays = 7;
    private const int MaxOwnShifts = 40;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private sealed class OwnShift
    {
        public string Id { get; set; } = "";
        public DateTime SeenUtc { get; set; }
    }

    private sealed class Data
    {
        public int Version { get; set; } = 1;
        /// <summary>Метка включения (PhonePrintSettings.EnabledToken), для которой уже сделан «нулевой» опрос.</summary>
        public string? BaselineFor { get; set; }
        public List<PhonePrintEntry> Entries { get; set; } = new();
        /// <summary>Смены, которые эта касса видела своими: чек такой смены — не «чужой», даже когда смену уже закрыли.</summary>
        public List<OwnShift> OwnShifts { get; set; } = new();
    }

    private readonly object _gate = new();
    private readonly string _path;
    private Data? _data;

    public PhoneSalePrintJournal(string path) => _path = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        NurMarketKassa.Services.AppMode.DataFolderName,
        "data",
        "phone-print-journal.json");

    private Data Load()
    {
        if (_data != null)
            return _data;
        try
        {
            if (File.Exists(_path))
                _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path), Json);
        }
        catch (Exception ex)
        {
            // Повреждённый журнал не удаляем, а откладываем; новый начинается с «нулевого» опроса (BaselineFor пуст) —
            // всё, что уже есть на сервере, считается «до включения», двойной печати не будет.
            try { File.Move(_path, _path + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true); } catch { /* не критично */ }
            NurMarketKassa.Services.PosLogger.Log($"Чеки с телефона: журнал повреждён ({ex.Message}) — начат новый.", "WARNING");
            _data = null;
        }
        return _data ??= new Data();
    }

    private bool Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json));
            if (File.Exists(_path))
                File.Replace(tmp, _path, null);
            else
                File.Move(tmp, _path);
            return true;
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"Чеки с телефона: журнал не записан ({ex.Message}).", "WARNING");
            return false;
        }
    }

    public string? BaselineFor
    {
        get { lock (_gate) return Load().BaselineFor; }
    }

    public bool SetBaselineFor(string? token)
    {
        lock (_gate)
        {
            Load().BaselineFor = token;
            return Save();
        }
    }

    public PhonePrintEntry? Get(string saleId)
    {
        lock (_gate)
            return Load().Entries.FirstOrDefault(e => string.Equals(e.SaleId, saleId, StringComparison.OrdinalIgnoreCase))?.Clone();
    }

    public List<PhonePrintEntry> Snapshot()
    {
        lock (_gate)
            return Load().Entries.Select(e => e.Clone()).ToList();
    }

    /// <summary>Новая запись. false — такая продажа в журнале уже есть (кто-то успел раньше) или запись на диск не удалась.</summary>
    public bool TryAdd(PhonePrintEntry entry)
    {
        lock (_gate)
        {
            var d = Load();
            if (d.Entries.Any(e => string.Equals(e.SaleId, entry.SaleId, StringComparison.OrdinalIgnoreCase)))
                return false;
            d.Entries.Add(entry.Clone());
            if (Save())
                return true;
            d.Entries.RemoveAt(d.Entries.Count - 1);
            return false;
        }
    }

    /// <summary>Запись перед печатью: «failed»/«skipped» → «printing» (кнопка «Напечатать»). Уже «printing»/«printed»/«ignored»
    /// — отказ (двойное нажатие, другая вкладка). Возвращает копию записи ДО смены статуса.</summary>
    public PhonePrintEntry? TryBeginRetry(string saleId)
    {
        lock (_gate)
        {
            var d = Load();
            var e = d.Entries.FirstOrDefault(x => string.Equals(x.SaleId, saleId, StringComparison.OrdinalIgnoreCase));
            if (e is null || e.Status is not (PhonePrintStatus.Failed or PhonePrintStatus.Skipped))
                return null;
            var before = e.Clone();
            e.Status = PhonePrintStatus.Printing;
            e.UpdatedUtc = DateTime.UtcNow;
            e.Attempts++;
            if (Save())
                return before;
            e.Status = before.Status;
            e.Attempts = before.Attempts;
            e.UpdatedUtc = before.UpdatedUtc;
            return null;
        }
    }

    /// <summary>Итог печати. Ошибка записи итога не страшна: останется «printing», при перезапуске станет «failed».</summary>
    public void Finish(string saleId, string status, string? error, bool maybePrinted)
    {
        lock (_gate)
        {
            var e = Load().Entries.FirstOrDefault(x => string.Equals(x.SaleId, saleId, StringComparison.OrdinalIgnoreCase));
            if (e is null)
                return;
            e.Status = status;
            e.Error = error;
            e.MaybePrinted = maybePrinted;
            e.UpdatedUtc = DateTime.UtcNow;
            Save();
        }
    }

    /// <summary>Запуск кассы: «printing» с прошлого раза — исход неизвестен, автоматически не печатаем, в список «Не напечатаны».</summary>
    public int RecoverInterrupted()
    {
        lock (_gate)
        {
            var n = 0;
            foreach (var e in Load().Entries.Where(x => x.Status == PhonePrintStatus.Printing))
            {
                e.Status = PhonePrintStatus.Failed;
                e.MaybePrinted = true;
                e.Error = NurMarketKassa.Services.Tr.T(
                    "Касса перезапустилась во время печати — неизвестно, вышел ли чек.",
                    "Касса басып чыгаруу учурунда кайра иштей баштады — чек чыккан-чыкпаганы белгисиз.",
                    "The till restarted during printing — it is unknown whether the receipt came out.",
                    "Kasa yazdırma sırasında yeniden başladı — fişin çıkıp çıkmadığı bilinmiyor.",
                    "Kassa chop etish paytida qayta ishga tushdi — chek chiqqani noma'lum.");
                e.UpdatedUtc = DateTime.UtcNow;
                n++;
            }
            if (n > 0)
                Save();
            return n;
        }
    }

    public void RememberShift(string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return;
        lock (_gate)
        {
            var d = Load();
            var id = shiftId.Trim();
            var known = d.OwnShifts.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (known != null)
            {
                // Обновляем отметку времени не чаще раза в час, чтобы не писать файл каждые 5 секунд.
                if (DateTime.UtcNow - known.SeenUtc < TimeSpan.FromHours(1))
                    return;
                known.SeenUtc = DateTime.UtcNow;
            }
            else
            {
                d.OwnShifts.Add(new OwnShift { Id = id, SeenUtc = DateTime.UtcNow });
                if (d.OwnShifts.Count > MaxOwnShifts)
                    d.OwnShifts = d.OwnShifts.OrderByDescending(s => s.SeenUtc).Take(MaxOwnShifts).ToList();
            }
            Save();
        }
    }

    public bool IsOwnShift(string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return false;
        lock (_gate)
            return Load().OwnShifts.Any(s => string.Equals(s.Id, shiftId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Записи старше 7 дней и устаревшие отметки смен — удалить.</summary>
    public void Prune()
    {
        lock (_gate)
        {
            var d = Load();
            var limit = DateTime.UtcNow.AddDays(-KeepDays);
            var before = d.Entries.Count + d.OwnShifts.Count;
            d.Entries.RemoveAll(e => e.DiscoveredUtc < limit && e.Status != PhonePrintStatus.Printing);
            d.OwnShifts.RemoveAll(s => s.SeenUtc < limit);
            if (d.Entries.Count + d.OwnShifts.Count != before)
                Save();
        }
    }
}
