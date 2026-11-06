using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

// 2026-11-05 (волна 2-Б, функция 2): «Подозрительные операции». Здесь — событие, журнал на диске, чистые правила (что подозрительно), балл кассира и тексты на 5 языках.
// Источники событий: журнал «Удалено из чека» (CartRemovalLogStore), журнал «Продажи в убыток» (LossSalesStore), возвраты и скидки (их сообщают окна кассы),
// закрытие смены с недостачей (X/Z), подтверждения PIN старшего (ActionGuard), «Некорректные чеки» (IrregularReceiptsStore). Журнал локальный, как и источники;
// для нескольких касс нужен приём событий на сервере (ТЗ Умару) — тогда FraudEventStore.Append станет отправкой.

public static class FraudKinds
{
    public const string Removal = "removal";
    public const string RemovalMany = "removal_many";
    public const string Return = "return";
    public const string ReturnNoReceipt = "return_no_receipt";
    public const string Discount = "discount";
    public const string BelowCost = "below_cost";
    public const string Shortage = "shortage";
    /// <summary>Старший ввёл PIN и разрешил действие.</summary>
    public const string PinOk = "pin_ok";
    /// <summary>PIN не введён или неверен — действие отменено.</summary>
    public const string PinDenied = "pin_denied";
    public const string Irregular = "irregular";
    /// <summary>2026-11-06: В3 — кто-то выключил защиту (PIN старшего или журнал подозрительных операций). Subject — какую, Cashier — кто.</summary>
    public const string ProtectionOff = "protection_off";
    /// <summary>2026-11-06: В12 (Л5-06, хвост) — ревизия с крупным расхождением проведена без проверки PIN владельца (нет связи с сервером). Amount — недостача.</summary>
    public const string RevisionNoPin = "revision_no_pin";

    public static readonly string[] All = { Removal, RemovalMany, Return, ReturnNoReceipt, Discount, BelowCost, Shortage, PinOk, PinDenied, Irregular, ProtectionOff, RevisionNoPin };

    /// <summary>Баллы подозрительности за одно событие. Подтверждённое PIN-ом (pin_ok) баллов не даёт: старший всё видел.</summary>
    public static int Weight(string kind) => kind switch
    {
        Removal => 2,
        RemovalMany => 3,
        Return => 2,
        ReturnNoReceipt => 4,
        Discount => 2,
        BelowCost => 3,
        Shortage => 4,
        PinDenied => 3,
        Irregular => 1,
        ProtectionOff => 3,
        RevisionNoPin => 3,
        _ => 0,
    };
}

/// <summary>Одно подозрительное (или подтверждённое PIN-ом) действие. Текст не хранится — строится на языке программы из полей (<see cref="FraudText"/>).</summary>
public sealed class FraudEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string Kind { get; set; } = FraudKinds.Removal;
    public string Cashier { get; set; } = "";
    public string? CompanyId { get; set; }
    public string? ShiftId { get; set; }

    /// <summary>Сумма в сомах (если применима).</summary>
    public double Amount { get; set; }

    /// <summary>Процент скидки или число удалений — смотря по виду.</summary>
    public double Value { get; set; }

    /// <summary>Товар, номер чека или название действия (для pin_*: <see cref="GuardAction"/> строкой).</summary>
    public string? Subject { get; set; }

    /// <summary>Id продажи — по нему из списка можно открыть чек.</summary>
    public string? SaleId { get; set; }

    public string? Note { get; set; }
}

/// <summary>Строка таблицы «балл кассира за неделю».</summary>
public sealed record CashierScore(string Cashier, int Points, int Events, double Amount);

/// <summary>Журнал событий: одна запись JSON на строку (дописывается, не переписывается), хранится 90 дней и не больше 10 000 записей.
/// Файл общий для кассы и владельца: %LOCALAPPDATA%\NurMarketKassa\fraud-events.jsonl.</summary>
public static class FraudEventStore
{
    public const int KeepDays = 90;
    public const int MaxRecords = 10000;
    private const int TrimEvery = 200;

    private static readonly object Gate = new();
    private static int _appendedSinceTrim = TrimEvery;

    public static string? PathOverride { get; set; }

    public static event Action? Changed;

    private static string FilePath => PathOverride ?? Path.Combine(AntiFraudSettingsStore.Folder, "fraud-events.jsonl");

    public static void Append(IEnumerable<FraudEvent> events)
    {
        var list = events.ToList();
        if (list.Count == 0)
            return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 0 && !EndsWithNewLine(FilePath))
                    File.AppendAllText(FilePath, Environment.NewLine);
                File.AppendAllLines(FilePath, list.Select(e => JsonSerializer.Serialize(e)));
                _appendedSinceTrim += list.Count;
                if (_appendedSinceTrim >= TrimEvery)
                {
                    _appendedSinceTrim = 0;
                    TrimUnlocked();
                }
            }
        }
        catch (Exception ex)
        {
            // Журнал антифрода не должен мешать продаже: действие уже сделано.
            PosLogger.Log($"Антифрод: журнал не записан ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
        try { Changed?.Invoke(); } catch { /* подписчик окна */ }
    }

    public static void Append(FraudEvent e) => Append(new[] { e });

    /// <summary>Все записи, новые сверху. Читает файл — вызывать не на UI-потоке.</summary>
    public static IReadOnlyList<FraudEvent> ReadAll()
    {
        lock (Gate)
            return ReadUnlocked().OrderByDescending(e => e.At).ToList();
    }

    private static List<FraudEvent> ReadUnlocked()
    {
        var result = new List<FraudEvent>();
        try
        {
            if (!File.Exists(FilePath))
                return result;
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    if (JsonSerializer.Deserialize<FraudEvent>(line) is { } e)
                        result.Add(e);
                }
                catch (JsonException)
                {
                    // недописанная строка (выключили питание) — пропускаем только её
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Антифрод: журнал не прочитан ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
        return result;
    }

    private static void TrimUnlocked()
    {
        var all = ReadUnlocked();
        var cutoff = DateTimeOffset.Now.AddDays(-KeepDays);
        var keep = all.Where(e => e.At >= cutoff).OrderBy(e => e.At).ToList();
        if (keep.Count > MaxRecords)
            keep = keep.Skip(keep.Count - MaxRecords).ToList();
        if (keep.Count == all.Count)
            return;
        var tmp = FilePath + ".tmp";
        File.WriteAllLines(tmp, keep.Select(e => JsonSerializer.Serialize(e)));
        File.Move(tmp, FilePath, true);
    }

    private static bool EndsWithNewLine(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
            return true;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }
}

/// <summary>Чистые правила: что подозрительно. Без сети, без диска — проверяются тестами.</summary>
public static class FraudAnalyzer
{
    public static bool IsRemovalSuspicious(double lineTotal, FraudRules r) => r.Removal && lineTotal >= r.RemovalAmount && lineTotal > 0;

    public static bool IsReturnSuspicious(double amount, bool hasReceipt, FraudRules r) =>
        (r.ReturnNoReceipt && !hasReceipt) || (r.Return && amount >= r.ReturnAmount && amount > 0);

    public static bool IsDiscountSuspicious(double percent, FraudRules r) => r.Discount && percent >= r.DiscountPercent && percent > 0;

    public static bool IsShortageSuspicious(double shortage, FraudRules r) => r.Shortage && shortage > 0 && shortage >= r.ShortageAmount;

    /// <summary>Процент скидки по введённым данным окна скидки. mode: "percent" — значение уже в процентах; иначе сумма в сомах от базы.</summary>
    public static double DiscountPercentOf(string? mode, double value, double baseAmount)
    {
        if (value <= 0 || !double.IsFinite(value))
            return 0;
        if (string.Equals(mode, "percent", StringComparison.OrdinalIgnoreCase))
            return Math.Min(100, value);
        return baseAmount > 0 ? Math.Min(100, value / baseAmount * 100) : 0;
    }

    /// <summary>События из журнала «Удалено из чека»: крупные удаления и «много удалений за смену». Сброс чека при закрытии смены не считается.</summary>
    public static List<FraudEvent> FromRemovals(IEnumerable<CartRemovalRecord> records, FraudRules r)
    {
        var result = new List<FraudEvent>();
        var list = records.Where(x => x.Kind != CartRemovalLogStore.KindShiftClose).ToList();
        foreach (var x in list)
        {
            if (!IsRemovalSuspicious(x.LineTotal, r))
                continue;
            result.Add(new FraudEvent
            {
                Id = "rem|" + x.At.UtcTicks.ToString(CultureInfo.InvariantCulture) + "|" + x.ProductId + "|" + x.Title,
                At = x.At, Kind = FraudKinds.Removal, Cashier = x.Cashier, CompanyId = x.CompanyId, ShiftId = x.ShiftId,
                Amount = x.LineTotal, Subject = x.Title, Note = x.Reason,
            });
        }
        if (r.RemovalMany)
            foreach (var group in list.Where(x => !string.IsNullOrEmpty(x.ShiftId)).GroupBy(x => x.ShiftId!))
            {
                var count = group.Count();
                if (count <= r.RemovalPerShift)
                    continue;
                var last = group.OrderByDescending(x => x.At).First();
                result.Add(new FraudEvent
                {
                    // Id один на смену: повторная проверка того же журнала не плодит сообщений.
                    Id = "many|" + group.Key,
                    At = last.At, Kind = FraudKinds.RemovalMany, Cashier = last.Cashier, CompanyId = last.CompanyId, ShiftId = group.Key,
                    Value = count, Amount = group.Sum(x => x.LineTotal),
                });
            }
        return result;
    }

    /// <summary>События из журнала «Продажи в убыток».</summary>
    public static List<FraudEvent> FromLoss(IEnumerable<LossSaleRecord> records, FraudRules r) =>
        !r.BelowCost
            ? new()
            : records.Where(x => x.Loss > 0).Select(x => new FraudEvent
            {
                Id = "loss|" + x.At.UtcTicks.ToString(CultureInfo.InvariantCulture) + "|" + x.SaleId,
                At = x.At, Kind = FraudKinds.BelowCost, Cashier = x.Cashier, Amount = x.Loss, SaleId = x.SaleId,
                Subject = string.Join(", ", x.Lines.Take(3).Select(l => l.Title)),
            }).ToList();

    /// <summary>События из «Некорректных чеков» (только открытые).</summary>
    public static List<FraudEvent> FromIrregular(IEnumerable<IrregularReceiptEntry> entries) =>
        entries.Where(e => e.ClosedAt is null).Select(e => new FraudEvent
        {
            Id = "irr|" + e.Id, At = e.CreatedAt, Kind = FraudKinds.Irregular, Cashier = e.CashierName ?? "", Amount = e.Total, Subject = e.Tag, Note = e.Note,
        }).ToList();

    /// <summary>Балл каждого кассира за период [from, to): сумма весов событий, самые подозрительные сверху. Без имени кассира — «—».</summary>
    public static List<CashierScore> Score(IEnumerable<FraudEvent> events, DateTimeOffset from, DateTimeOffset to) =>
        events.Where(e => e.At >= from && e.At < to)
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Cashier) ? "—" : e.Cashier.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new CashierScore(g.Key, g.Sum(e => FraudKinds.Weight(e.Kind)), g.Count(e => FraudKinds.Weight(e.Kind) > 0), g.Where(e => FraudKinds.Weight(e.Kind) > 0).Sum(e => e.Amount)))
            .OrderByDescending(s => s.Points).ThenByDescending(s => s.Amount).ToList();

    /// <summary>Начало «этой недели» (понедельник 00:00) — окно, за которое считается балл.</summary>
    public static DateTimeOffset WeekStart(DateTimeOffset now)
    {
        var date = now.Date;
        var back = ((int)date.DayOfWeek + 6) % 7;
        return new DateTimeOffset(date.AddDays(-back), now.Offset);
    }

    /// <summary>Все события для окна: собственный журнал + производные из удалений, убытков и некорректных чеков, без повторов по Id, новые сверху.</summary>
    public static List<FraudEvent> Merge(IEnumerable<FraudEvent> own, IEnumerable<FraudEvent> derived)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<FraudEvent>();
        foreach (var e in own.Concat(derived))
            if (seen.Add(e.Id))
                result.Add(e);
        return result.OrderByDescending(e => e.At).ToList();
    }
}

/// <summary>Тексты событий на 5 языков (для окна и Telegram).</summary>
public static class FraudText
{
    public static string Title(string kind) => kind switch
    {
        FraudKinds.Removal => Tr.T("Крупное удаление из чека", "Чектен чоң өчүрүү", "Large removal from a receipt", "Fişten büyük silme", "Chekdan katta o'chirish"),
        FraudKinds.RemovalMany => Tr.T("Много удалений за смену", "Сменада өчүрүүлөр көп", "Many removals in a shift", "Vardiyada çok silme", "Smenada ko'p o'chirishlar"),
        FraudKinds.Return => Tr.T("Крупный возврат", "Чоң кайтаруу", "Large return", "Büyük iade", "Katta qaytarish"),
        FraudKinds.ReturnNoReceipt => Tr.T("Возврат без чека", "Чексиз кайтаруу", "Return without a receipt", "Fişsiz iade", "Cheksiz qaytarish"),
        FraudKinds.Discount => Tr.T("Большая скидка", "Чоң арзандатуу", "Big discount", "Büyük indirim", "Katta chegirma"),
        FraudKinds.BelowCost => Tr.T("Цена ниже закупки", "Баа сатып алуудан төмөн", "Price below cost", "Fiyat maliyetin altında", "Narx xarid narxidan past"),
        FraudKinds.Shortage => Tr.T("Недостача при закрытии смены", "Смена жабылганда жетишпестик", "Shortage at shift close", "Vardiya kapanışında eksik", "Smena yopilishida kamomad"),
        FraudKinds.PinOk => Tr.T("Действие подтверждено PIN", "Аракет PIN менен ырасталды", "Action confirmed by PIN", "İşlem PIN ile onaylandı", "Amal PIN bilan tasdiqlandi"),
        FraudKinds.PinDenied => Tr.T("PIN не принят — действие отменено", "PIN кабыл алынган жок — аракет токтотулду", "PIN rejected — action cancelled", "PIN kabul edilmedi — işlem iptal", "PIN qabul qilinmadi — amal bekor qilindi"),
        FraudKinds.Irregular => Tr.T("Некорректный чек", "Туура эмес чек", "Irregular receipt", "Düzensiz fiş", "Noto'g'ri chek"),
        FraudKinds.ProtectionOff => Tr.T("Защита выключена", "Коргоо өчүрүлдү", "Protection switched off", "Koruma kapatıldı", "Himoya o'chirildi"),
        FraudKinds.RevisionNoPin => Tr.T("Ревизия без проверки PIN (нет связи)", "PIN текшерилбей ревизия (байланыш жок)", "Stocktake without a PIN check (offline)", "PIN doğrulanmadan sayım (bağlantı yok)", "PIN tekshirilmagan inventarizatsiya (aloqa yo'q)"),
        _ => kind,
    };

    public static string ActionName(string? action) => action switch
    {
        nameof(GuardAction.Discount) => Tr.T("скидка", "арзандатуу", "discount", "indirim", "chegirma"),
        nameof(GuardAction.Return) => Tr.T("возврат", "кайтаруу", "return", "iade", "qaytarish"),
        nameof(GuardAction.ManualPrice) => Tr.T("ручная цена", "кол менен баа", "manual price", "elle fiyat", "qo'lda narx"),
        nameof(GuardAction.RemovalAfterRing) => Tr.T("удаление строки после пробития", "сап урандан кийин өчүрүү", "removing a line after ringing it up", "okutulan satırı silme", "urilgan qatorni o'chirish"),
        nameof(GuardAction.OpenDrawer) => Tr.T("открытие ящика без продажи", "сатуусуз кутуну ачуу", "opening the drawer without a sale", "satışsız çekmece açma", "sotuvsiz tortmani ochish"),
        nameof(GuardAction.ShiftShortage) => Tr.T("закрытие смены с недостачей", "жетишпестик менен сменаны жабуу", "closing the shift with a shortage", "eksikle vardiya kapatma", "kamomad bilan smenani yopish"),
        _ => action ?? "",
    };

    /// <summary>Название защиты для события «выключена» (Subject: guard / fraud).</summary>
    public static string ProtectionName(string? subject) => subject switch
    {
        FraudMonitor.ProtectionGuard => Tr.T("PIN старшего по действиям", "Аракеттер боюнча улуунун PIN коду", "senior PIN for actions", "işlemler için yetkili PIN'i", "amallar uchun katta xodim PIN kodi"),
        FraudMonitor.ProtectionFraud => Tr.T("журнал подозрительных операций", "шектүү операциялар журналы", "suspicious operations log", "şüpheli işlem günlüğü", "shubhali amallar jurnali"),
        _ => subject ?? "",
    };

    /// <summary>Одна строка о событии: кто, что, сколько.</summary>
    public static string Describe(FraudEvent e)
    {
        var who = string.IsNullOrWhiteSpace(e.Cashier) ? "" : e.Cashier.Trim() + ": ";
        var sum = MoneyText.Som(e.Amount);
        var subj = string.IsNullOrWhiteSpace(e.Subject) ? "" : " «" + e.Subject!.Trim() + "»";
        switch (e.Kind)
        {
            case FraudKinds.Removal:
                return who + Tr.T($"убрано из чека{subj} на {sum}", $"чектен өчүрүлдү{subj}: {sum}", $"removed from the receipt{subj}: {sum}", $"fişten silindi{subj}: {sum}", $"chekdan o'chirildi{subj}: {sum}");
            case FraudKinds.RemovalMany:
                return who + Tr.T($"за смену убрано строк: {e.Value:0} (на {sum})", $"сменада өчүрүлгөн сап: {e.Value:0} ({sum})", $"lines removed in the shift: {e.Value:0} ({sum})", $"vardiyada silinen satır: {e.Value:0} ({sum})", $"smenada o'chirilgan qator: {e.Value:0} ({sum})");
            case FraudKinds.Return:
                return who + Tr.T($"возврат {sum}{subj}", $"кайтаруу {sum}{subj}", $"return {sum}{subj}", $"iade {sum}{subj}", $"qaytarish {sum}{subj}");
            case FraudKinds.ReturnNoReceipt:
                return who + Tr.T($"возврат без чека {sum}{subj}", $"чексиз кайтаруу {sum}{subj}", $"return without a receipt {sum}{subj}", $"fişsiz iade {sum}{subj}", $"cheksiz qaytarish {sum}{subj}");
            case FraudKinds.Discount:
                return who + Tr.T($"скидка {e.Value:0.#} %{(e.Amount > 0 ? " (" + sum + ")" : "")}{subj}", $"арзандатуу {e.Value:0.#} %{(e.Amount > 0 ? " (" + sum + ")" : "")}{subj}",
                    $"discount {e.Value:0.#} %{(e.Amount > 0 ? " (" + sum + ")" : "")}{subj}", $"indirim %{e.Value:0.#}{(e.Amount > 0 ? " (" + sum + ")" : "")}{subj}", $"chegirma {e.Value:0.#} %{(e.Amount > 0 ? " (" + sum + ")" : "")}{subj}");
            case FraudKinds.BelowCost:
                return who + Tr.T($"продажа ниже закупки, убыток {sum}{subj}", $"сатып алуудан төмөн сатуу, зыян {sum}{subj}", $"sold below cost, loss {sum}{subj}", $"maliyetin altında satış, zarar {sum}{subj}", $"xarid narxidan past sotuv, zarar {sum}{subj}");
            case FraudKinds.Shortage:
                return who + Tr.T($"недостача {sum}", $"жетишпестик {sum}", $"shortage {sum}", $"eksik {sum}", $"kamomad {sum}");
            case FraudKinds.PinOk:
            {
                var tail = e.Value > 0 ? $" ({e.Value:0.#} %)" : e.Amount > 0 ? " (" + sum + ")" : "";
                return who + Tr.T($"{ActionName(e.Subject)} — старший ввёл PIN{tail}", $"{ActionName(e.Subject)} — улуу PIN киргизди{tail}",
                    $"{ActionName(e.Subject)} — senior entered the PIN{tail}", $"{ActionName(e.Subject)} — yetkili PIN girdi{tail}", $"{ActionName(e.Subject)} — katta PIN kiritdi{tail}");
            }
            case FraudKinds.ProtectionOff:
                return who + Tr.T($"выключил защиту: {ProtectionName(e.Subject)}", $"коргоону өчүрдү: {ProtectionName(e.Subject)}", $"switched off protection: {ProtectionName(e.Subject)}",
                    $"korumayı kapattı: {ProtectionName(e.Subject)}", $"himoyani o'chirdi: {ProtectionName(e.Subject)}");
            case FraudKinds.PinDenied:
            {
                var tail = e.Value > 0 ? $" ({e.Value:0.#} %)" : e.Amount > 0 ? " (" + sum + ")" : "";
                return who + Tr.T($"{ActionName(e.Subject)} — PIN не принят{tail}", $"{ActionName(e.Subject)} — PIN кабыл алынган жок{tail}",
                    $"{ActionName(e.Subject)} — PIN rejected{tail}", $"{ActionName(e.Subject)} — PIN kabul edilmedi{tail}", $"{ActionName(e.Subject)} — PIN qabul qilinmadi{tail}");
            }
            default:
                return who + Title(e.Kind) + (e.Amount > 0 ? " " + sum : "");
        }
    }
}
