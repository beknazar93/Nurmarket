using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.Services;

/// <summary>2026-10-28, владелец: «для аналитики как расход на начало бизнеса». Одна статья вложений на открытие магазина
/// (ремонт, оборудование, мебель, первоначальная закупка, лицензии, реклама, залог за аренду…).</summary>
public sealed class StartupCostItem : StampedRecord
{
    public string Title { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime Date { get; set; } = DateTime.Today;
    /// <summary>Ключ категории из <see cref="StartupCostsStore.Categories"/> (незнакомый ключ — «Прочее», но сохраняется как есть).</summary>
    public string Category { get; set; } = "other";
    public string Comment { get; set; } = "";

    public StartupCostItem Clone() => (StartupCostItem)MemberwiseClone();

    internal override StampedRecord CloneRecord() => Clone();
}

/// <summary>Общие данные для окупаемости: дата открытия бизнеса, план окупаемости и переключатель амортизации.</summary>
public sealed class StartupCostsMeta
{
    /// <summary>Дата открытия бизнеса; null — не указана (окупаемость не считается).</summary>
    public DateTime? OpenedOn { get; set; }
    /// <summary>Учитывать амортизацию: показывать «чистая прибыль с учётом амортизации» (по умолчанию нет).</summary>
    public bool Amortize { get; set; }
    /// <summary>На сколько месяцев равномерно списываются вложения при включённой амортизации.</summary>
    public int AmortMonths { get; set; } = StartupCostsStore.DefaultAmortMonths;
    /// <summary>План окупаемости, месяцев; 0 — не задан.</summary>
    public int PlanMonths { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.MinValue;
    internal JsonObject? Extra { get; set; }

    public StartupCostsMeta Clone() => (StartupCostsMeta)MemberwiseClone();
}

/// <summary>Документ целиком (то, что лежит под ключом StartupCosts).</summary>
public sealed class StartupCostsDoc
{
    public StartupCostsMeta Meta { get; set; } = new();
    public List<StartupCostItem> Items { get; set; } = new();
    public List<InvestorItem> Investors { get; set; } = new();
    public List<PayMark> Payments { get; set; } = new();
    internal JsonObject? Extra { get; set; }
}

/// <summary>2026-10-28, владелец: «для аналитики как расход на начало бизнеса» — стартовые расходы (вложения на открытие) как отдельная
/// сущность аналитики программы владельца: окупаемость вложений, срок окупаемости, амортизация (см. <see cref="StartupPayback"/>).
///
/// Где хранится. Схему pos_local.db не меняем. Данные лежат в ОБЩЕМ документе настроек компании NurCRM — том же, что общие настройки
/// кассы и программы владельца (api/main/app-settings/nurmarket-kassa/, см. SettingsCloudSync.CompanyApp), под отдельным ключом
/// «StartupCosts» (SettingsCloudSync этого ключа не знает и его не трогает: он отправляет только свои ключи). Читают все, меняют владелец
/// и администратор (у кассира — 403, тогда статьи остаются только на этом ПК). Значение — объект:
///   {"v":1, "meta":{"openedOn":"2026-03-01","amortize":false,"amortMonths":36,"updatedAt":"…Z"},
///    "items":[{"id","title","amount","date","category","comment","updatedAt","deleted"}]}
/// Слияние двух программ — по записям (как документы клиента, ClientDocsStore): у каждой статьи есть id и «updatedAt» (UTC); побеждает
/// более свежая; удаление — запись с "deleted": true (90 дней); дата открытия и амортизация — одним блоком meta, побеждает свежее.
/// Запись: свежий GET → слияние → PATCH только ключа StartupCosts → проверочный GET (если чужая запись затёрла нашу — слияние ещё
/// раз, до 3 раз). Резервная копия — %APPDATA%\{папка программы}\startup-costs\{компания}.json (работа без связи не теряется).
///
/// Незнакомые поля записей и документа сохраняются. Нечитаемый ключ на сервере не перезаписывается (сообщение владельцу).</summary>
public static class StartupCostsStore
{
    public const string CloudPath = "api/main/app-settings/nurmarket-kassa/";
    public const string Key = "StartupCosts";
    public const int DefaultAmortMonths = 36;
    public const int MinAmortMonths = 3;
    public const int MaxAmortMonths = 120;
    /// <summary>Сколько дней хранится «надгробие» удалённой статьи.</summary>
    public const int TombstoneDays = 90;
    public const decimal MaxAmount = 100_000_000_000m;
    private const int MaxPushRounds = 3;

    public enum SyncState { Unknown, Saving, Saved, LocalOnly, Forbidden, Damaged }

    /// <summary>Стенд проверки: своя папка вместо %APPDATA% и без сервера.</summary>
    public static string? DataDirOverride { get; set; }
    public static bool CloudDisabled { get; set; }
    /// <summary>Стенд проверки слияния: свой «сервер» вместо NurCRM (GET → ответ, PATCH → тело).</summary>
    public static Func<HttpMethod, JsonObject?, Task<JsonElement>>? TransportOverride { get; set; }

    /// <summary>Категории вложений: ключ → название на 5 языках.</summary>
    public static IReadOnlyList<(string Key, string Title)> Categories => new[]
    {
        ("repair", Tr.T("Ремонт и отделка", "Оңдоо жана жасалгалоо", "Repairs and finishing", "Tadilat ve dekorasyon", "Ta'mirlash va pardozlash")),
        ("equipment", Tr.T("Оборудование", "Жабдуулар", "Equipment", "Ekipman", "Uskunalar")),
        ("furniture", Tr.T("Мебель и стеллажи", "Эмерек жана текчелер", "Furniture and shelving", "Mobilya ve raflar", "Mebel va javonlar")),
        ("stock", Tr.T("Первоначальная закупка товара", "Товарды баштапкы сатып алуу", "Initial stock purchase", "İlk mal alımı", "Dastlabki tovar xaridi")),
        ("license", Tr.T("Лицензии и документы", "Лицензиялар жана документтер", "Licences and paperwork", "Lisanslar ve belgeler", "Litsenziyalar va hujjatlar")),
        ("ads", Tr.T("Реклама и вывеска", "Жарнама жана көрнөк", "Advertising and signage", "Reklam ve tabela", "Reklama va peshtaxta")),
        ("rent", Tr.T("Залог и аренда при открытии", "Ачылышындагы күрөө жана ижара", "Deposit and opening rent", "Depozito ve açılış kirası", "Garov va ochilishdagi ijara")),
        ("other", Tr.T("Прочее", "Башкасы", "Other", "Diğer", "Boshqa")),
    };

    public static string CategoryTitle(string? key) =>
        Categories.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)).Title ?? Categories[^1].Title;

    private static readonly object Sync = new();
    private static readonly SemaphoreSlim PushGate = new(1, 1);
    private static StartupCostsDoc _doc = new();
    private static string? _loadedFor;
    private static CancellationTokenSource? _pushDelay;
    private static int _failures;
    private static bool _remoteDamaged;

    public static SyncState State { get; private set; } = SyncState.Unknown;
    public static DateTime? LastSavedAt { get; private set; }
    /// <summary>Последнее сообщение для владельца (нечитаемый ключ на сервере, нет прав…); null — нет.</summary>
    public static string? Notice { get; private set; }

    /// <summary>Список или дата открытия изменились (событие приходит на UI-потоке).</summary>
    public static event Action? Changed;

    private static string CompanyKey => CompanyInfoService.LastCompany?.Id is { Length: > 0 } id ? id : "default";

    private static string FilePath
    {
        get
        {
            var dir = DataDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "startup-costs");
            var safe = string.Concat(CompanyKey.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
            return Path.Combine(dir, (safe.Length == 0 ? "default" : safe) + ".json");
        }
    }

    // ───────────────────────────── чтение ─────────────────────────────

    /// <summary>Статьи (без удалённых), новые даты — сверху.</summary>
    public static IReadOnlyList<StartupCostItem> Items
    {
        get
        {
            lock (Sync)
            {
                EnsureLocal();
                return _doc.Items.Where(i => !i.Deleted).OrderByDescending(i => i.Date).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
                    .Select(i => i.Clone()).ToList();
            }
        }
    }

    public static StartupCostsMeta Meta
    {
        get
        {
            lock (Sync)
            {
                EnsureLocal();
                return _doc.Meta.Clone();
            }
        }
    }

    /// <summary>Сумма стартовых расходов.</summary>
    public static decimal Total => Sum(Items);

    public static decimal Sum(IEnumerable<StartupCostItem> items) => items.Where(i => !i.Deleted).Sum(i => i.Amount);

    /// <summary>Менять данные можно: у кассира сервер ответил 403 — тогда только просмотр.</summary>
    public static bool CanEdit => State != SyncState.Forbidden;

    // ───────────────────────────── правки ─────────────────────────────

    /// <summary>Новая или изменённая статья. Возвращает текст ошибки или null.</summary>
    public static string? Save(StartupCostItem item)
    {
        var problem = Validate(item);
        if (problem != null)
            return problem;
        if (!CanEdit)
            return NoRightsText;
        lock (Sync)
        {
            EnsureLocal();
            var copy = item.Clone();
            copy.Title = copy.Title.Trim();
            copy.Comment = (copy.Comment ?? "").Trim();
            copy.Amount = Math.Round(copy.Amount, 2, MidpointRounding.AwayFromZero);
            copy.Date = copy.Date.Date;
            copy.Deleted = false;
            copy.UpdatedAt = Stamp(copy.UpdatedAt);
            var at = _doc.Items.FindIndex(i => i.Id == copy.Id);
            if (at >= 0)
            {
                // Незнакомые поля прежней записи не теряем.
                copy.Extra ??= _doc.Items[at].Extra;
                _doc.Items[at] = copy;
            }
            else
                _doc.Items.Add(copy);
            PersistLocal();
        }
        AfterEdit();
        return null;
    }

    public static string? Delete(string id)
    {
        if (!CanEdit)
            return NoRightsText;
        lock (Sync)
        {
            EnsureLocal();
            var at = _doc.Items.FindIndex(i => i.Id == id);
            if (at < 0)
                return null;
            var tomb = _doc.Items[at].Clone();
            tomb.Deleted = true;
            tomb.UpdatedAt = Stamp(tomb.UpdatedAt);
            _doc.Items[at] = tomb;
            PersistLocal();
        }
        AfterEdit();
        return null;
    }

    /// <summary>Дата открытия бизнеса, амортизация. openedOn = null — убрать дату.</summary>
    public static string? SetMeta(DateTime? openedOn, bool amortize, int amortMonths, int planMonths = 0)
    {
        if (amortMonths is < MinAmortMonths or > MaxAmortMonths)
            return Tr.T($"Срок списания — от {MinAmortMonths} до {MaxAmortMonths} месяцев.", $"Эсептен чыгаруу мөөнөтү — {MinAmortMonths}-{MaxAmortMonths} ай.",
                $"The write-off period is {MinAmortMonths} to {MaxAmortMonths} months.", $"Gider yazma süresi {MinAmortMonths}-{MaxAmortMonths} ay.",
                $"Hisobdan chiqarish muddati — {MinAmortMonths}-{MaxAmortMonths} oy.");
        if (openedOn is { } d && (d.Year < 2000 || d.Date > DateTime.Today.AddDays(1)))
            return Tr.T("Дата открытия не может быть в будущем.", "Ачылган күн келечекте болбойт.", "The opening date cannot be in the future.",
                "Açılış tarihi gelecekte olamaz.", "Ochilgan sana kelajakda bo'lishi mumkin emas.");
        if (!CanEdit)
            return NoRightsText;
        lock (Sync)
        {
            EnsureLocal();
            var meta = _doc.Meta;
            if (meta.OpenedOn?.Date == openedOn?.Date && meta.Amortize == amortize && meta.AmortMonths == amortMonths && meta.PlanMonths == planMonths)
                return null;
            meta.OpenedOn = openedOn?.Date;
            meta.Amortize = amortize;
            meta.AmortMonths = amortMonths;
            meta.PlanMonths = planMonths;
            meta.UpdatedAt = Stamp(meta.UpdatedAt);
            PersistLocal();
        }
        AfterEdit();
        return null;
    }

    // ───────────────────────────── инвесторы и выплаты ─────────────────────────────

    /// <summary>Инвесторы (без удалённых): по дате вложения, затем по имени.</summary>
    public static IReadOnlyList<InvestorItem> Investors
    {
        get
        {
            lock (Sync)
            {
                EnsureLocal();
                return _doc.Investors.Where(i => !i.Deleted).OrderBy(i => i.Date).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Id, StringComparer.Ordinal)
                    .Select(i => i.Clone()).ToList();
            }
        }
    }

    /// <summary>Отметки «выплачено» (без снятых).</summary>
    public static IReadOnlyList<PayMark> Payments
    {
        get
        {
            lock (Sync)
            {
                EnsureLocal();
                return _doc.Payments.Where(i => !i.Deleted).Select(i => i.Clone()).ToList();
            }
        }
    }

    /// <summary>Проверка инвестора: имя, сумма вложения больше нуля, дата, условия.</summary>
    public static string? ValidateInvestor(InvestorItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Name))
            return Tr.T("Введите имя инвестора.", "Инвестордун атын жазыңыз.", "Enter the investor's name.", "Yatırımcının adını girin.", "Investor nomini kiriting.");
        if (item.Name.Trim().Length > 80)
            return Tr.T("Имя — не больше 80 знаков.", "Аты — 80 белгиден ашпайт.", "The name is at most 80 characters.", "Ad en fazla 80 karakter.", "Nomi 80 belgidan oshmasin.");
        if (item.Amount <= 0m || item.Amount > MaxAmount)
            return Tr.T("Сумма вложения должна быть больше нуля.", "Салымдын суммасы нөлдөн чоң болушу керек.", "The investment must be greater than zero.",
                "Yatırım tutarı sıfırdan büyük olmalı.", "Sarmoya summasi noldan katta bo'lishi kerak.");
        if (item.Date.Year < 2000 || item.Date.Date > DateTime.Today.AddYears(1))
            return Tr.T("Проверьте дату вложения.", "Салым күнүн текшериңиз.", "Check the investment date.", "Yatırım tarihini kontrol edin.", "Sarmoya sanasini tekshiring.");
        var warn = InvestorTemplates.Warnings(item.Terms);
        if (item.Terms.Stages.Count == 0 || warn.Count > 0)
            return warn.Count > 0 ? warn[0] : null;
        return null;
    }

    /// <summary>Новый или изменённый инвестор. Версия условий растёт, если условия изменились. Возвращает текст ошибки или null.</summary>
    public static string? SaveInvestor(InvestorItem item)
    {
        var problem = ValidateInvestor(item);
        if (problem != null)
            return problem;
        if (!CanEdit)
            return NoRightsText;
        lock (Sync)
        {
            EnsureLocal();
            var copy = item.Clone();
            copy.Name = copy.Name.Trim();
            copy.Comment = (copy.Comment ?? "").Trim();
            copy.Amount = Math.Round(copy.Amount, 2, MidpointRounding.AwayFromZero);
            copy.Date = copy.Date.Date;
            copy.Deleted = false;
            copy.UpdatedAt = Stamp(copy.UpdatedAt);
            var at = _doc.Investors.FindIndex(i => i.Id == copy.Id);
            if (at >= 0)
            {
                var old = _doc.Investors[at];
                copy.Extra ??= old.Extra;
                copy.Terms.Extra ??= old.Terms.Extra;
                var a = TermsToJson(old.Terms);
                var b = TermsToJson(copy.Terms);
                a.Remove("ver");
                b.Remove("ver");
                copy.Terms.Version = a.ToJsonString() == b.ToJsonString() ? old.Terms.Version : old.Terms.Version + 1;
                _doc.Investors[at] = copy;
            }
            else
            {
                copy.Terms.Version = 1;
                _doc.Investors.Add(copy);
            }
            PersistLocal();
        }
        AfterEdit();
        return null;
    }

    /// <summary>Удалить инвестора вместе с его отметками выплат (остаются «надгробиями» на 90 дней).</summary>
    public static string? DeleteInvestor(string id)
    {
        if (!CanEdit)
            return NoRightsText;
        lock (Sync)
        {
            EnsureLocal();
            var at = _doc.Investors.FindIndex(i => i.Id == id);
            if (at < 0)
                return null;
            var tomb = _doc.Investors[at].Clone();
            tomb.Deleted = true;
            tomb.UpdatedAt = Stamp(tomb.UpdatedAt);
            _doc.Investors[at] = tomb;
            for (var k = 0; k < _doc.Payments.Count; k++)
                if (_doc.Payments[k].InvestorId == id && !_doc.Payments[k].Deleted)
                {
                    var p = _doc.Payments[k].Clone();
                    p.Deleted = true;
                    p.UpdatedAt = Stamp(p.UpdatedAt);
                    _doc.Payments[k] = p;
                }
            PersistLocal();
        }
        AfterEdit();
        return null;
    }

    /// <summary>Отметить месяц выплаченным (с суммой на момент отметки) или снять отметку.</summary>
    public static string? SetPaid(string investorId, string month, decimal amount, bool paid)
    {
        if (!CanEdit)
            return NoRightsText;
        lock (Sync)
        {
            EnsureLocal();
            var id = PayMark.KeyOf(investorId, month);
            var at = _doc.Payments.FindIndex(p => p.Id == id);
            if (at >= 0)
            {
                var m = _doc.Payments[at].Clone();
                m.Deleted = !paid;
                m.Amount = paid ? Math.Round(amount, 2, MidpointRounding.AwayFromZero) : m.Amount;
                m.PaidOn = paid ? DateTime.Today : m.PaidOn;
                m.UpdatedAt = Stamp(m.UpdatedAt);
                _doc.Payments[at] = m;
            }
            else if (paid)
                _doc.Payments.Add(new PayMark { Id = id, InvestorId = investorId, Month = month, Amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero), PaidOn = DateTime.Today });
            else
                return null;
            PersistLocal();
        }
        AfterEdit();
        return null;
    }

    private static string NoRightsText => Tr.T("Стартовые расходы меняют только владелец и администратор.", "Баштапкы чыгымдарды ээси жана администратор гана өзгөртөт.",
        "Only the owner and administrators can change startup costs.", "Başlangıç giderlerini yalnızca sahip ve yönetici değiştirebilir.",
        "Boshlang'ich xarajatlarni faqat egasi va administrator o'zgartiradi.");

    /// <summary>Проверка статьи: название, сумма больше нуля и разумная, дата.</summary>
    public static string? Validate(StartupCostItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Title))
            return Tr.T("Введите название статьи.", "Беренин атын жазыңыз.", "Enter the item name.", "Kalem adını girin.", "Modda nomini kiriting.");
        if (item.Title.Trim().Length > 120)
            return Tr.T("Название — не больше 120 знаков.", "Аты — 120 белгиден ашпайт.", "The name is at most 120 characters.", "Ad en fazla 120 karakter.", "Nomi 120 belgidan oshmasin.");
        if (item.Amount <= 0m || item.Amount > MaxAmount)
            return Tr.T("Сумма должна быть больше нуля.", "Сумма нөлдөн чоң болушу керек.", "The amount must be greater than zero.", "Tutar sıfırdan büyük olmalı.", "Summa noldan katta bo'lishi kerak.");
        if (item.Date.Year < 2000 || item.Date.Date > DateTime.Today.AddYears(1))
            return Tr.T("Проверьте дату: она должна быть не раньше 2000 года и не позже чем через год.", "Күндү текшериңиз: 2000-жылдан эрте эмес жана бир жылдан кеч эмес болушу керек.",
                "Check the date: not earlier than 2000 and not more than a year ahead.", "Tarihi kontrol edin: 2000'den önce ve bir yıldan fazla ileri olmamalı.",
                "Sanani tekshiring: 2000-yildan oldin va bir yildan keyin bo'lmasligi kerak.");
        if ((item.Comment ?? "").Length > 500)
            return Tr.T("Комментарий — не больше 500 знаков.", "Комментарий — 500 белгиден ашпайт.", "The comment is at most 500 characters.", "Yorum en fazla 500 karakter.", "Izoh 500 belgidan oshmasin.");
        return null;
    }

    /// <summary>Метка времени новой правки: строго позже предыдущей записи этой же статьи (часы двух ПК расходятся — но свою
    /// последнюю правку мы всегда считаем свежее своей же предыдущей).</summary>
    private static DateTime Stamp(DateTime previous)
    {
        var now = DateTime.UtcNow;
        var ms = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        return ms <= previous ? previous.AddMilliseconds(1) : ms;
    }

    private static void AfterEdit()
    {
        RaiseChanged();
        SchedulePush(TimeSpan.FromMilliseconds(900));
    }

    // ───────────────────────────── слияние ─────────────────────────────

    /// <summary>Слияние по записям одного списка: по id побеждает больший updatedAt (при равенстве — удалённая, затем «правая»).</summary>
    private static List<T> MergeList<T>(IEnumerable<T> local, IEnumerable<T> remote, DateTime limit) where T : StampedRecord
    {
        var byId = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in remote)
            byId[item.Id] = (T)item.CloneRecord();
        foreach (var item in local)
        {
            if (!byId.TryGetValue(item.Id, out var other)
                || item.UpdatedAt > other.UpdatedAt || (item.UpdatedAt == other.UpdatedAt && item.Deleted && !other.Deleted))
                byId[item.Id] = (T)item.CloneRecord();
        }
        return byId.Values.Where(i => !i.Deleted || i.UpdatedAt >= limit).OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Слияние двух документов: статьи, инвесторы и отметки выплат — по id (побеждает больший updatedAt),
    /// дата открытия, план и амортизация — по updatedAt блока meta. Старые «надгробия» (> 90 дней) убираются. Результат — новый объект.</summary>
    public static StartupCostsDoc Merge(StartupCostsDoc local, StartupCostsDoc remote, DateTime? now = null)
    {
        var limit = (now ?? DateTime.UtcNow).AddDays(-TombstoneDays);
        var meta = local.Meta.UpdatedAt > remote.Meta.UpdatedAt ? local.Meta.Clone() : remote.Meta.Clone();
        return new StartupCostsDoc
        {
            Meta = meta,
            Extra = remote.Extra ?? local.Extra,
            Items = MergeList(local.Items, remote.Items, limit),
            Investors = MergeList(local.Investors, remote.Investors, limit),
            Payments = MergeList(local.Payments, remote.Payments, limit),
        };
    }

    /// <summary>Одинаковы ли документы по содержимому (для проверки «сервер уже такой же»).</summary>
    public static bool SameContent(StartupCostsDoc a, StartupCostsDoc b) =>
        string.Equals(Canonical(a), Canonical(b), StringComparison.Ordinal);

    private static string Canonical(StartupCostsDoc d)
    {
        static string One(IEnumerable<StampedRecord> l) => string.Join("|", l.OrderBy(i => i.Id, StringComparer.Ordinal)
            .Select(i => $"{i.Id};{i.UpdatedAt.Ticks};{(i.Deleted ? 1 : 0)}"));
        return One(d.Items) + "#" + One(d.Investors) + "#" + One(d.Payments) + "#" + d.Meta.UpdatedAt.Ticks;
    }

    // ───────────────────────────── JSON ─────────────────────────────

    private static JsonObject StampedToJson(StampedRecord r)
    {
        var o = r.Extra != null ? (JsonObject)r.Extra.DeepClone() : new JsonObject();
        o["id"] = r.Id;
        o["updatedAt"] = Iso(r.UpdatedAt);
        if (r.Deleted)
            o["deleted"] = true;
        else
            o.Remove("deleted");
        return o;
    }

    private static string DateOnly(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static JsonObject ToJson(StartupCostsDoc doc)
    {
        var root = doc.Extra != null ? (JsonObject)doc.Extra.DeepClone() : new JsonObject();
        root["v"] = 2;
        var meta = doc.Meta.Extra != null ? (JsonObject)doc.Meta.Extra.DeepClone() : new JsonObject();
        meta["openedOn"] = doc.Meta.OpenedOn is { } d ? DateOnly(d) : null;
        meta["amortize"] = doc.Meta.Amortize;
        meta["amortMonths"] = doc.Meta.AmortMonths;
        meta["planMonths"] = doc.Meta.PlanMonths;
        meta["updatedAt"] = Iso(doc.Meta.UpdatedAt);
        root["meta"] = meta;
        var items = new JsonArray();
        foreach (var i in doc.Items.OrderBy(x => x.Date).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            var o = StampedToJson(i);
            o["title"] = i.Title;
            o["amount"] = i.Amount;
            o["date"] = DateOnly(i.Date);
            o["category"] = i.Category;
            o["comment"] = i.Comment ?? "";
            items.Add(o);
        }
        root["items"] = items;
        var investors = new JsonArray();
        foreach (var i in doc.Investors.OrderBy(x => x.Date).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            var o = StampedToJson(i);
            o["name"] = i.Name;
            o["amount"] = i.Amount;
            o["date"] = DateOnly(i.Date);
            o["comment"] = i.Comment ?? "";
            o["terms"] = TermsToJson(i.Terms);
            investors.Add(o);
        }
        root["investors"] = investors;
        var payments = new JsonArray();
        foreach (var p in doc.Payments.OrderBy(x => x.Month, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            var o = StampedToJson(p);
            o["investorId"] = p.InvestorId;
            o["month"] = p.Month;
            o["amount"] = p.Amount;
            o["paidOn"] = DateOnly(p.PaidOn);
            payments.Add(o);
        }
        root["payments"] = payments;
        return root;
    }

    private static JsonObject TermsToJson(InvestorTerms t)
    {
        var o = t.Extra != null ? (JsonObject)t.Extra.DeepClone() : new JsonObject();
        o["tpl"] = t.Template;
        o["base"] = t.Base;
        o["excl"] = new JsonArray(t.ExcludedExpenses.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        o["stages"] = new JsonArray(t.Stages.Select(s => (JsonNode?)new JsonObject { ["pct"] = s.Pct, ["cond"] = s.Cond, ["val"] = s.Val }).ToArray());
        o["loss"] = t.LossRule;
        o["period"] = t.Period;
        o["minMonthly"] = t.MinMonthly;
        o["tax"] = t.TaxPct;
        o["reserve"] = t.ReservePct;
        o["stock"] = t.StockCredit;
        o["liq"] = t.StockLiquidityPct;
        o["ver"] = t.Version;
        return o;
    }

    private static InvestorTerms TermsFromJson(JsonNode? node)
    {
        var t = new InvestorTerms();
        if (node is not JsonObject o)
            return t;
        var x = (JsonObject)o.DeepClone();
        foreach (var k in new[] { "tpl", "base", "excl", "stages", "loss", "period", "minMonthly", "tax", "reserve", "stock", "liq", "ver" })
            x.Remove(k);
        t.Extra = x.Count > 0 ? x : null;
        t.Template = Str(o, "tpl") ?? "";
        t.Base = Str(o, "base") is "revenue" or "gross" or "net" ? Str(o, "base")! : "net";
        if (o["excl"] is JsonArray ex)
            t.ExcludedExpenses = ex.Select(e => e?.ToString() ?? "").Where(e => e.Length > 0).ToList();
        if (o["stages"] is JsonArray st)
            foreach (var s in st.OfType<JsonObject>())
                t.Stages.Add(new PayStage
                {
                    Pct = Math.Clamp(Dec(s["pct"]) ?? 0m, 0m, 1000m),
                    Cond = Str(s, "cond") is "return" or "months" or "forever" ? Str(s, "cond")! : "forever",
                    Val = Math.Clamp(Dec(s["val"]) ?? 0m, 0m, 100000m),
                });
        t.LossRule = Str(o, "loss") is "carry" or "none" or "share" ? Str(o, "loss")! : "carry";
        t.Period = Str(o, "period") is "month" or "quarter" or "year" or "manual" ? Str(o, "period")! : "month";
        t.MinMonthly = Math.Max(0m, Dec(o["minMonthly"]) ?? 0m);
        t.TaxPct = Math.Clamp(Dec(o["tax"]) ?? 0m, 0m, 100m);
        t.ReservePct = Math.Clamp(Dec(o["reserve"]) ?? 0m, 0m, 100m);
        t.StockCredit = o["stock"] is JsonValue sv && sv.TryGetValue<bool>(out var sc) && sc;
        t.StockLiquidityPct = Math.Clamp(Dec(o["liq"]) ?? 70m, 0m, 100m);
        t.Version = Math.Max(1, (int)(Dec(o["ver"]) ?? 1m));
        return t;
    }

    private static void ReadStamped(JsonObject o, StampedRecord r, params string[] known)
    {
        var ox = (JsonObject)o.DeepClone();
        foreach (var k in new[] { "id", "updatedAt", "deleted" }.Concat(known))
            ox.Remove(k);
        r.Id = Str(o, "id")!;
        r.UpdatedAt = ParseStamp(Str(o, "updatedAt"));
        r.Deleted = o["deleted"] is JsonValue dv && dv.TryGetValue<bool>(out var del) && del;
        r.Extra = ox.Count > 0 ? ox : null;
    }

    /// <summary>Разбор документа; нечитаемый (не объект, списки не массивы) — null. Плохие записи пропускаются.</summary>
    public static StartupCostsDoc? FromJson(JsonNode? node)
    {
        if (node is not JsonObject root)
            return null;
        if ((root["items"] is { } it && it is not JsonArray) || (root["investors"] is { } iv && iv is not JsonArray) || (root["payments"] is { } pv && pv is not JsonArray))
            return null;
        var doc = new StartupCostsDoc();
        var extra = (JsonObject)root.DeepClone();
        foreach (var k in new[] { "v", "meta", "items", "investors", "payments" })
            extra.Remove(k);
        doc.Extra = extra.Count > 0 ? extra : null;
        if (root["meta"] is JsonObject m)
        {
            var mx = (JsonObject)m.DeepClone();
            foreach (var k in new[] { "openedOn", "amortize", "amortMonths", "planMonths", "updatedAt" })
                mx.Remove(k);
            doc.Meta = new StartupCostsMeta
            {
                OpenedOn = ParseDate(Str(m, "openedOn")),
                Amortize = m["amortize"] is JsonValue av && av.TryGetValue<bool>(out var b) && b,
                AmortMonths = Math.Clamp((int)(Dec(m["amortMonths"]) ?? DefaultAmortMonths), MinAmortMonths, MaxAmortMonths),
                PlanMonths = Math.Clamp((int)(Dec(m["planMonths"]) ?? 0m), 0, 600),
                UpdatedAt = ParseStamp(Str(m, "updatedAt")),
                Extra = mx.Count > 0 ? mx : null,
            };
        }
        if (root["items"] is JsonArray arr)
            foreach (var o in arr.OfType<JsonObject>())
            {
                if (Str(o, "id") is not { Length: > 0 } || ParseDate(Str(o, "date")) is not { } date)
                    continue;
                var item = new StartupCostItem
                {
                    Title = Str(o, "title") ?? "",
                    Amount = Dec(o["amount"]) ?? 0m,
                    Date = date,
                    Category = Str(o, "category") is { Length: > 0 } c ? c : "other",
                    Comment = Str(o, "comment") ?? "",
                };
                ReadStamped(o, item, "title", "amount", "date", "category", "comment");
                doc.Items.Add(item);
            }
        if (root["investors"] is JsonArray inv)
            foreach (var o in inv.OfType<JsonObject>())
            {
                if (Str(o, "id") is not { Length: > 0 } || ParseDate(Str(o, "date")) is not { } date)
                    continue;
                var item = new InvestorItem
                {
                    Name = Str(o, "name") ?? "",
                    Amount = Dec(o["amount"]) ?? 0m,
                    Date = date,
                    Comment = Str(o, "comment") ?? "",
                    Terms = TermsFromJson(o["terms"]),
                };
                ReadStamped(o, item, "name", "amount", "date", "comment", "terms");
                doc.Investors.Add(item);
            }
        if (root["payments"] is JsonArray pay)
            foreach (var o in pay.OfType<JsonObject>())
            {
                if (Str(o, "id") is not { Length: > 0 } || Str(o, "investorId") is not { Length: > 0 } iid || Str(o, "month") is not { Length: 7 } month)
                    continue;
                var item = new PayMark
                {
                    InvestorId = iid,
                    Month = month,
                    Amount = Dec(o["amount"]) ?? 0m,
                    PaidOn = ParseDate(Str(o, "paidOn")) ?? DateTime.Today,
                };
                ReadStamped(o, item, "investorId", "month", "amount", "paidOn");
                doc.Payments.Add(item);
            }
        return doc;
    }

    private static string Iso(DateTime utc) => utc == DateTime.MinValue ? "" : utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[name] is JsonValue v2 ? v2.ToString() : null;

    private static decimal? Dec(JsonNode? n)
    {
        if (n is not JsonValue v)
            return null;
        if (v.TryGetValue<decimal>(out var d))
            return d;
        if (v.TryGetValue<double>(out var dd) && !double.IsNaN(dd) && !double.IsInfinity(dd) && Math.Abs(dd) < 1e15)
            return (decimal)dd;
        return decimal.TryParse(v.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParseExact((s ?? "").Length >= 10 ? s![..10] : s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static DateTime ParseStamp(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : DateTime.MinValue;

    // ───────────────────────────── копия на этом ПК ─────────────────────────────

    private static void EnsureLocal()
    {
        var key = CompanyKey;
        if (_loadedFor == key)
            return;
        _loadedFor = key;
        _doc = new StartupCostsDoc();
        State = SyncState.Unknown;
        _remoteDamaged = false;
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return;
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (root != null && FromJson(root["doc"]) is { } doc)
                    _doc = doc;
            }
            catch (JsonException)
            {
                // Повреждённый файл не мешает работе: откладываем в сторону и начинаем с сервера.
                try { File.Move(path, path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), true); } catch (IOException) { }
                PosLogger.Log("Стартовые расходы: файл копии на ПК повреждён — отложен в сторону.", "WARNING");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Стартовые расходы: копия на ПК не прочитана ({ex.Message}).", "WARNING");
        }
    }

    private static void PersistLocal()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var root = new JsonObject { ["company"] = CompanyKey, ["savedAt"] = Iso(DateTime.UtcNow), ["doc"] = ToJson(_doc) };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.Move(tmp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Стартовые расходы: копия на ПК не записана ({ex.Message}).", "WARNING");
        }
    }

    // ───────────────────────────── сервер ─────────────────────────────

    /// <summary>Загрузить копию с ПК и сверить с сервером (слияние; если у нас есть что отправить — отправляется).</summary>
    public static async Task RefreshAsync(CancellationToken ct = default)
    {
        lock (Sync)
            EnsureLocal();
        RaiseChanged();
        await SyncAsync(ct).ConfigureAwait(false);
    }

    private static void SchedulePush(TimeSpan delay)
    {
        CancellationTokenSource cts;
        lock (Sync)
        {
            _pushDelay?.Cancel();
            _pushDelay = cts = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                await SyncAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Стартовые расходы: отправка не удалась ({ex.Message}).", "WARNING");
            }
        });
    }

    private static NurMarketApiClient? Api()
    {
        if (CloudDisabled || ServerOutageMonitor.IsOutage || CompanyInfoService.LastCompany?.Id is not { Length: > 0 })
            return null;
        return SettingsCloudSync.ApiProvider?.Invoke() is { } api && !string.IsNullOrEmpty(api.AccessToken) ? api : null;
    }

    private static Task<JsonElement> Request(NurMarketApiClient? api, HttpMethod method, JsonObject? body) =>
        TransportOverride is { } t ? t(method, body) : api!.RequestAsync(method, CloudPath, body, null, CancellationToken.None, TimeSpan.FromSeconds(20));

    /// <summary>Свежий GET → слияние с локальным → PATCH, пока сервер не станет таким же (до 3 кругов).</summary>
    private static async Task SyncAsync(CancellationToken ct)
    {
        NurMarketApiClient? api = null;
        if (CloudDisabled || (TransportOverride == null && (api = Api()) == null))
        {
            SetState(SyncState.LocalOnly);
            return;
        }
        await PushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var company = CompanyKey;
            SetState(SyncState.Saving);
            for (var round = 0; round < MaxPushRounds; round++)
            {
                JsonElement data;
                try
                {
                    data = await Request(api, HttpMethod.Get, null).ConfigureAwait(false);
                }
                catch (ApiException ex) when (ex.StatusCode == 403)
                {
                    SetState(SyncState.Forbidden);
                    return;
                }
                if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("settings", out var settings))
                    throw new InvalidDataException("ответ без поля settings");

                StartupCostsDoc remote = new();
                var remoteExists = false;
                if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(Key, out var value) && value.ValueKind != JsonValueKind.Null)
                {
                    remoteExists = true;
                    var parsed = FromJson(JsonNode.Parse(value.GetRawText()));
                    if (parsed == null)
                    {
                        // Нечитаемый ключ целиком не перезаписываем.
                        _remoteDamaged = true;
                        Notice = Tr.T("В общих настройках компании стартовые расходы повреждены — на сервер ничего не отправляется, данные остаются на этом ПК.",
                            "Компаниянын жалпы жөндөөлөрүндө баштапкы чыгымдар бузулган — серверге эч нерсе жөнөтүлбөйт, маалымат ушул ПКда калат.",
                            "Startup costs in the company's shared settings are damaged — nothing is sent to the server, the data stays on this PC.",
                            "Şirketin ortak ayarlarında başlangıç giderleri bozulmuş — sunucuya hiçbir şey gönderilmez, veriler bu bilgisayarda kalır.",
                            "Kompaniyaning umumiy sozlamalarida boshlang'ich xarajatlar buzilgan — serverga hech narsa yuborilmaydi, ma'lumot shu kompyuterda qoladi.");
                        PosLogger.Log("Стартовые расходы: ключ StartupCosts на сервере нечитаем — не перезаписываем.", "WARNING");
                        SetState(SyncState.Damaged);
                        return;
                    }
                    remote = parsed;
                }

                StartupCostsDoc merged;
                lock (Sync)
                {
                    if (_loadedFor != company)
                        return; // сменили компанию, пока шёл запрос
                    merged = Merge(_doc, remote);
                    var changedLocally = !SameContent(merged, _doc);
                    _doc = merged;
                    if (changedLocally)
                        PersistLocal();
                }
                RaiseChanged();

                if (remoteExists && SameContent(merged, remote))
                {
                    _failures = 0;
                    LastSavedAt = DateTime.Now;
                    Notice = null;
                    SetState(SyncState.Saved);
                    return;
                }
                if (!remoteExists && merged.Items.Count == 0 && merged.Investors.Count == 0 && merged.Payments.Count == 0 && merged.Meta.UpdatedAt == DateTime.MinValue)
                {
                    // И у нас, и на сервере пусто — писать нечего.
                    SetState(SyncState.Saved);
                    return;
                }

                try
                {
                    await Request(api, new HttpMethod("PATCH"), new JsonObject { [Key] = ToJson(merged) }).ConfigureAwait(false);
                    PosLogger.Log($"Стартовые расходы: отправлено на сервер ({merged.Items.Count(i => !i.Deleted)} шт.).", "INFO");
                }
                catch (ApiException ex) when (ex.StatusCode == 403)
                {
                    SetState(SyncState.Forbidden);
                    return;
                }
                // Следующий круг: свежий GET покажет, не затёрла ли нашу запись чужая.
            }
            _failures++;
            SetState(SyncState.LocalOnly);
            SchedulePush(RetryDelay());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _failures++;
            PosLogger.Log($"Стартовые расходы: сервер недоступен ({ex.Message}) — данные сохранены на этом ПК, отправятся позже.", "WARNING");
            SetState(SyncState.LocalOnly);
            SchedulePush(RetryDelay());
        }
        finally
        {
            PushGate.Release();
        }
    }

    private static TimeSpan RetryDelay() => TimeSpan.FromSeconds(Math.Min(600, 15 * Math.Pow(2, Math.Min(_failures, 6))));

    private static void SetState(SyncState state)
    {
        if (State == state)
            return;
        State = state;
        RaiseChanged();
    }

    private static void RaiseChanged()
    {
        try
        {
            _ = UiDispatcherHolder.InvokeAsync(() =>
            {
                try
                {
                    Changed?.Invoke();
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Стартовые расходы: обновление окна не удалось ({ex.Message}).", "WARNING");
                }
            });
        }
        catch (Exception)
        {
            // Нет UI-потока (стенд проверки без окон) — подписчиков и нет.
        }
    }

    /// <summary>Только для стенда проверки: забыть загруженное, чтобы следующий вызов перечитал копию с диска.</summary>
    public static void ResetCache()
    {
        lock (Sync)
        {
            _loadedFor = null;
            _doc = new StartupCostsDoc();
            State = SyncState.Unknown;
            _failures = 0;
        }
    }
}
