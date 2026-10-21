using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-19, решение владельца: «Выплатить» в разделе «Зарплата» программы владельца. Выплата — просто расход
/// из кассы компании, как сайт NurCRM делает расход на кассе магазина: POST api/construction/cashflows/ {cashbox,
/// type:"expense", name, amount, status} — без смены. Отдельных выплат зарплаты у магазина на сервере пока нет (ТЗ бэкенду
/// п.8), поэтому расход помечается категорией «Зарплата» (справочник api/construction/cashflow-categories/, как у кассы
/// кафе на сайте). Проверено на NBS 19.10: source_kind «salary_payout» сервер отвергает (400, нет среди вариантов) —
/// расход уходит без него; категории «Зарплата» у компании не было — создана.
///
/// Чья и за какой период выплата: source_id «salary:&lt;user_id&gt;:&lt;yyyyMMdd&gt;-&lt;yyyyMMdd&gt;:&lt;ключ&gt;» и ВСЕГДА метка в
/// конце названия «[#&lt;user_id&gt; 01.10.2026–18.10.2026]»; в журнале этого ПК — UserId, PeriodFrom/PeriodTo.
/// «Выплачено» на экране — выплаты, чей ПЕРИОД ЗАРПЛАТЫ целиком внутри периода на экране (третье ревью, п.4); пересекается
/// частично — не вычитается, а показывается пометкой.
///
/// Принцип (третье ревью): «не уверен — не подставляй сумму и предупреди». Двойной расход: ключ выплаты (Guid) создаётся один
/// раз на нажатие и уходит в source_id (и idempotency_key в теле). Повтор новым POST — только после отказа сервера по
/// конкретному полю (400: такой запрос не записан). Ответ не дошёл — НЕ повторяем. Четвёртое ревью (упрощено, как в оплате
/// долга): запись «unknown»/«sending» в журнале ПК — ЗАМОК, новая выплата этому сотруднику закрыта, пока владелец не решит:
/// «Проверить на сервере» (нашлась — учесть), «Выплата ЕСТЬ на сайте» или «Выплаты НЕТ на сайте». Сама программа «нет»
/// не решает.</summary>
internal static class SalaryPayoutService
{
    public const string SourceKind = "salary_payout";
    private const string CategoryTitle = "Зарплата";

    private static readonly object FileLock = new();
    private static readonly Dictionary<string, string> CategoryIdByCompany = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Третье ревью (п.9): «категории нет» — запоминается на 10 минут (не спрашивать справочник на каждое «Обновить»).</summary>
    private static readonly Dictionary<string, DateTime> NoCategoryUntilUtc = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    /// <summary>Живая проверка на NBS: сервер не знает source_kind «salary_payout» — после первого отказа поле не шлём.</summary>
    private static volatile bool _sourceKindRejected;
    /// <summary>Сервер отверг source_id «salary:…» — дальше source_id = ключ выплаты; чья выплата — по метке в названии.</summary>
    private static volatile bool _sourceIdPrefixRejected;
    /// <summary>Диагностика касс и категорий — один раз за запуск.</summary>
    private static int _cashboxesLogged;
    private static int _categoriesLogged;
    /// <summary>Третье ревью (п.6): журнал выплат был повреждён и переименован — выплаты закрыты до перезапуска.</summary>
    private static volatile string? _journalBrokenPath;

    private static readonly Regex SourceIdRx = new(@"^salary:(?<u>[^:]+):(?<f>\d{8})-(?<t>\d{8}):(?<k>.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex NameTagRx = new(@"\[#(?<u>[^\s\]]+) (?<f>\d{2}\.\d{2}\.\d{4})[–-](?<t>\d{2}\.\d{2}\.\d{4})\]\s*$", RegexOptions.CultureInvariant);
    /// <summary>Период в скобках старого названия «Зарплата: ФИО (01.10.2026–19.10.2026)» (или одна дата).</summary>
    private static readonly Regex ParenPeriodRx = new(@"\((?<f>\d{2}\.\d{2}\.\d{4})(?:[–-](?<t>\d{2}\.\d{2}\.\d{4}))?\)", RegexOptions.CultureInvariant);

    private static NurMarketApiClient Api => App.GetRequiredService<NurMarketApiClient>();

    private static string CompanyId => CompanyInfoService.LastCompany?.Id ?? "";

    // ── Общие тексты (третье ревью, п.10) ──────────────────────────────────────────

    public static string SalesCashboxWarning => Tr.T("Это касса продаж филиала — расход уменьшит её остаток.",
        "Бул филиалдын сатуу кассасы — чыгым анын калдыгын азайтат.", "This is a branch sales cash desk — the expense will reduce its balance.",
        "Bu şubenin satış kasası — gider bakiyesini azaltacak.", "Bu filialning savdo kassasi — xarajat uning qoldig'ini kamaytiradi.");

    public static string ThisPcNote => Tr.T("(по данным этого ПК)", "(ушул компьютердин маалыматы боюнча)", "(per this PC's records)",
        "(bu bilgisayarın kayıtlarına göre)", "(ushbu kompyuter ma'lumotlari bo'yicha)");

    public static string PartialNote => Tr.T("(проверено частично)", "(жарым-жартылай текшерилди)", "(partially checked)",
        "(kısmen kontrol edildi)", "(qisman tekshirildi)");

    public static string ByNameNote => Tr.T("(часть — по ФИО в названии)", "(бир бөлүгү — аталыштагы аты-жөнү боюнча)", "(partly by the name in the title)",
        "(bir kısmı başlıktaki ada göre)", "(bir qismi — nomdagi F.I.Sh. bo'yicha)");

    public static string CoverageNote(PaidCoverage coverage) => coverage switch
    {
        PaidCoverage.Full => "",
        PaidCoverage.Partial => PartialNote,
        _ => ThisPcNote,
    };

    public static string NotCheckedPlate => Tr.T("Не удалось проверить прошлые выплаты — проверьте на сайте перед выплатой.",
        "Мурунку төлөмдөрдү текшерүү мүмкүн болгон жок — төлөөдөн мурун сайттан текшериңиз.",
        "Could not check previous payouts — check on the website before paying.",
        "Önceki ödemeler kontrol edilemedi — ödemeden önce sitede kontrol edin.",
        "Oldingi to'lovlarni tekshirib bo'lmadi — to'lashdan oldin saytda tekshiring.");

    public static string PartialPlate => Tr.T("Проверено частично (выплаты без категории «Зарплата» или не все расходы не видны) — проверьте на сайте перед выплатой.",
        "Жарым-жартылай текшерилди («Эмгек акы» категориясы жок төлөмдөр же бардык чыгымдар көрүнбөйт) — төлөөдөн мурун сайттан текшериңиз.",
        "Partially checked (payouts without the “Salary” category or some expenses are not visible) — check on the website before paying.",
        "Kısmen kontrol edildi («Maaş» kategorisi olmayan ödemeler veya giderlerin bir kısmı görünmüyor) — ödemeden önce sitede kontrol edin.",
        "Qisman tekshirildi («Ish haqi» toifasisiz to'lovlar yoki ba'zi xarajatlar ko'rinmaydi) — to'lashdan oldin saytda tekshiring.");

    public static string TimeoutPlate => Tr.T("Проверка прошлых выплат не успела завершиться — проверьте на сайте перед выплатой.",
        "Мурунку төлөмдөрдү текшерүү бүтө элек — төлөөдөн мурун сайттан текшериңиз.",
        "Checking previous payouts didn't finish in time — check on the website before paying.",
        "Önceki ödemelerin kontrolü zamanında bitmedi — ödemeden önce sitede kontrol edin.",
        "Oldingi to'lovlarni tekshirish o'z vaqtida tugamadi — to'lashdan oldin saytda tekshiring.");

    public static string ByNamePlate => Tr.T("Часть выплат найдена только по ФИО в названии расхода — проверьте на сайте перед выплатой.",
        "Төлөмдөрдүн бир бөлүгү чыгымдын аталышындагы аты-жөнү боюнча гана табылды — төлөөдөн мурун сайттан текшериңиз.",
        "Some payouts were found only by the name in the expense title — check on the website before paying.",
        "Ödemelerin bir kısmı yalnızca gider başlığındaki ada göre bulundu — ödemeden önce sitede kontrol edin.",
        "To'lovlarning bir qismi faqat xarajat nomidagi F.I.Sh. bo'yicha topildi — to'lashdan oldin saytda tekshiring.");

    public static string UnassignedPlate(double sum, int count) => Tr.T(
        $"Есть расходы «Зарплата» без привязки к сотруднику на {SalaryWindow.Money(sum)} сом ({count} шт.) — проверьте на сайте.",
        $"Кызматкерге байланбаган «Эмгек акы» чыгымдары бар: {SalaryWindow.Money(sum)} сом ({count} даана) — сайттан текшериңиз.",
        $"There are “Salary” expenses not linked to an employee: {SalaryWindow.Money(sum)} som ({count} pcs) — check on the website.",
        $"Çalışana bağlanmamış «Maaş» giderleri var: {SalaryWindow.Money(sum)} som ({count} adet) — sitede kontrol edin.",
        $"Xodimga bog'lanmagan «Ish haqi» xarajatlari bor: {SalaryWindow.Money(sum)} so'm ({count} dona) — saytda tekshiring.");

    public static string PartialOverlapNote(DateTime from, DateTime to, double amount) => Tr.T(
        $"Есть выплата за {PeriodText(from, to)} ({SalaryWindow.Money(amount)} сом) — частично в этом периоде.",
        $"{PeriodText(from, to)} үчүн төлөм бар ({SalaryWindow.Money(amount)} сом) — бул мезгилге жарым-жартылай кирет.",
        $"There is a payout for {PeriodText(from, to)} ({SalaryWindow.Money(amount)} som) — partly in this period.",
        $"{PeriodText(from, to)} için ödeme var ({SalaryWindow.Money(amount)} som) — kısmen bu dönemde.",
        $"{PeriodText(from, to)} uchun to'lov bor ({SalaryWindow.Money(amount)} so'm) — qisman shu davrda.");

    /// <summary>Пятая проверка (п.1): компания не загружена — выплаты закрыты (как в оплате долга), записей с пустой компанией нет.</summary>
    public static string? CompanyUnknownText => string.IsNullOrWhiteSpace(CompanyId)
        ? Tr.T("Не удалось определить компанию — выплаты закрыты. Обновите раздел или перезапустите программу.",
            "Компанияны аныктоо мүмкүн болгон жок — төлөмдөр жабык. Бөлүмдү жаңылаңыз же программаны кайра ачыңыз.",
            "Could not determine the company — payouts are closed. Refresh the section or restart the program.",
            "Şirket belirlenemedi — ödemeler kapalı. Bölümü yenileyin veya programı yeniden başlatın.",
            "Kompaniyani aniqlab bo'lmadi — to'lovlar yopiq. Bo'limni yangilang yoki dasturni qayta ishga tushiring.")
        : null;

    /// <summary>Пятая проверка (п.2): журнал выплат не прочитался (файл занят) — выплаты закрыты до повтора.</summary>
    public static string JournalUnavailableText => Tr.T("Журнал выплат временно недоступен — повторите.",
        "Төлөмдөр журналы убактылуу жеткиликсиз — кайра аракет кылыңыз.", "The payout log is temporarily unavailable — try again.",
        "Ödeme günlüğü geçici olarak kullanılamıyor — tekrar deneyin.", "To'lovlar jurnali vaqtincha mavjud emas — qayta urinib ko'ring.");

    public static string? JournalBrokenText => _journalBrokenPath is { } path
        ? Tr.T($"Журнал выплат повреждён — обратитесь в поддержку (файл {path}).",
            $"Төлөмдөр журналы бузулган — колдоо кызматына кайрылыңыз (файл {path}).",
            $"The payout log is damaged — contact support (file {path}).",
            $"Ödeme günlüğü bozuk — desteğe başvurun (dosya {path}).",
            $"To'lovlar jurnali buzilgan — qo'llab-quvvatlashga murojaat qiling (fayl {path}).")
        : null;

    // ── Разбор ────────────────────────────────────────────────────────────────────

    /// <summary>Строковое поле JSON или null (общий разбор — <see cref="SalaryWindow.Str"/>).</summary>
    private static string? Opt(JsonElement e, string key) => SalaryWindow.Str(e, key) is { Length: > 0 } v ? v.Trim() : null;

    /// <summary>Третье ревью (п.7): id бывает и строкой, и числом.</summary>
    private static string? IdOf(JsonElement e, string key = "id")
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v))
            return null;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static string D(DateTime d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    /// <summary>Период в названии расхода: «01.10.2026–19.10.2026» (один день — одна дата).</summary>
    public static string PeriodText(DateTime from, DateTime to) => from.Date == to.Date ? D(from) : $"{D(from)}–{D(to)}";

    /// <summary>Название расхода на сайте — всегда по-русски (это запись NurCRM).</summary>
    public static string FlowName(string employee, DateTime from, DateTime to) => $"Зарплата: {employee.Trim()} ({PeriodText(from, to)})";

    /// <summary>Метка в конце названия: сотрудник и период зарплаты (обе даты всегда) — по ней «Выплачено» и проверка.</summary>
    private static string NameTag(string userId, DateTime from, DateTime to) => $" [#{userId.Trim()} {D(from)}–{D(to)}]";

    private static string SourceIdFor(string userId, DateTime from, DateTime to, string key) =>
        $"salary:{userId.Trim()}:{from:yyyyMMdd}-{to:yyyyMMdd}:{key}";

    private static bool Overlaps(DateTime aFrom, DateTime aTo, DateTime bFrom, DateTime bTo) => aFrom.Date <= bTo.Date && aTo.Date >= bFrom.Date;

    private static bool Inside(DateTime aFrom, DateTime aTo, DateTime bFrom, DateTime bTo) => aFrom.Date >= bFrom.Date && aTo.Date <= bTo.Date;

    // ── Кассы компании ─────────────────────────────────────────────────────────────

    public sealed record PayoutCashbox(string Id, string Name, string Role, bool? IsConsumption);

    public static async Task<List<PayoutCashbox>> LoadCashboxesAsync(CancellationToken ct)
    {
        var data = await App.ShiftApi.ConstructionCashboxesListAsync(ct).ConfigureAwait(false);
        var list = new List<PayoutCashbox>();
        foreach (var x in NurMarketApiClient.UnwrapList(data))
        {
            var id = IdOf(x) ?? Opt(x, "uuid");
            if (string.IsNullOrEmpty(id))
                continue;
            var name = Opt(x, "name") ?? Opt(x, "title") ?? id[..Math.Min(8, id.Length)];
            var role = Opt(x, "role") ?? Opt(x, "cashbox_role") ?? "";
            bool? consumption = x.TryGetProperty("is_consumption", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? c.GetBoolean()
                : null;
            list.Add(new PayoutCashbox(id, name, role, consumption));
        }
        // Кратко и один раз за запуск: какие кассы и роли отдаёт сервер (по ним выбирается касса по умолчанию).
        if (Interlocked.Exchange(ref _cashboxesLogged, 1) == 0)
            PosLogger.Log("Выплата зарплаты: кассы компании — " + string.Join("; ", list.Select(b => $"«{b.Name}» {(b.Role.Length > 0 ? b.Role : "—")}")), "INFO");
        return list;
    }

    /// <summary>Касса по умолчанию — касса расходов, как у сайта (is_consumption / expense_variable / «Прочие расходы»).
    /// Прошлый выбор владельца — только если касс расходов нет и это не касса продаж (pos_*): на NBS выплаты ушли из «Кассы
    /// филиала номер 1» (pos_branch) и уменьшили её остаток. Иначе первая не pos_*; ролей нет — -1, владелец выбирает сам.</summary>
    public static int DefaultCashboxIndex(IReadOnlyList<PayoutCashbox> list)
    {
        var saved = UserPreferences.Instance.SalaryPayoutCashboxId;
        var i = IndexOf(list, b => b.IsConsumption == true);
        if (i < 0)
            i = IndexOf(list, b => string.Equals(b.Role, "expense_variable", StringComparison.OrdinalIgnoreCase));
        if (i < 0)
            i = IndexOf(list, b => b.Name.Contains("прочие расход", StringComparison.OrdinalIgnoreCase));
        if (i < 0)
            i = IndexOf(list, b => !string.IsNullOrEmpty(saved) && string.Equals(b.Id, saved, StringComparison.OrdinalIgnoreCase) && !IsSalesCashbox(b));
        if (i < 0)
            i = IndexOf(list, b => b.Role.Length > 0 && !IsSalesCashbox(b));
        return i;
    }

    /// <summary>Касса продаж (pos_main, pos_branch…): расход из неё уменьшает остаток кассы продаж филиала.</summary>
    public static bool IsSalesCashbox(PayoutCashbox b) => b.Role.StartsWith("pos", StringComparison.OrdinalIgnoreCase);

    private static int IndexOf(IReadOnlyList<PayoutCashbox> list, Func<PayoutCashbox, bool> match)
    {
        for (var i = 0; i < list.Count; i++)
            if (match(list[i]))
                return i;
        return -1;
    }

    // ── Категория «Зарплата» ───────────────────────────────────────────────────────

    /// <summary>id категории «Зарплата» (по title без регистра; системная salary — тоже она). Нет — создаётся один раз
    /// (create=true). null — справочника нет, создать не дали или сервер не ответил: выплата тогда идёт БЕЗ категории
    /// (WARNING в журнал). «Нет категории» при чтении (create=false) запоминается на 10 минут. Исключение — только отмена.</summary>
    public static async Task<string?> CategoryIdAsync(bool create, CancellationToken ct)
    {
        var company = CompanyId;
        if (string.IsNullOrWhiteSpace(company))
            return null;
        lock (CategoryIdByCompany)
        {
            if (CategoryIdByCompany.TryGetValue(company, out var known))
                return known;
            // Пятая проверка (п.4): id категории запомнен в личных настройках — после перезапуска не создаём дубль.
            if (StoredCategoryId(company) is { } stored)
            {
                CategoryIdByCompany[company] = stored;
                return stored;
            }
            if (!create && NoCategoryUntilUtc.TryGetValue(company, out var until) && DateTime.UtcNow < until)
                return null;
        }

        // Все страницы справочника (до 10), а не только первая — иначе «Зарплата» на второй странице создалась бы ещё раз.
        var rows = new List<JsonElement>();
        try
        {
            for (var page = 1; page <= 10; page++)
            {
                var data = await Api.RequestAsync(HttpMethod.Get, "api/construction/cashflow-categories/", null,
                    new Dictionary<string, string> { ["page_size"] = "500", ["page"] = page.ToString(CultureInfo.InvariantCulture) },
                    ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                rows.AddRange(NurMarketApiClient.UnwrapList(data));
                if (!(data.ValueKind == JsonValueKind.Object && data.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String))
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выплата зарплаты: справочник категорий расходов не прочитан ({Describe(ex)}) — расход без категории.", "WARNING");
            return null;
        }

        string? bySlugId = null;
        var byTitle = new List<(string Id, DateTime? CreatedAt, int Order)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var x = rows[i];
            var id = IdOf(x) ?? Opt(x, "uuid");
            if (string.IsNullOrEmpty(id))
                continue;
            var title = Opt(x, "title") ?? Opt(x, "name") ?? "";
            var slug = Opt(x, "slug") ?? "";
            if (slug is "salary" or "salaries" or "zarplata")
                bySlugId ??= id;
            else if (title.Equals(CategoryTitle, StringComparison.OrdinalIgnoreCase)
                     || title.Equals("Зарплаты", StringComparison.OrdinalIgnoreCase)
                     || title.Equals("Заработная плата", StringComparison.OrdinalIgnoreCase))
                byTitle.Add((id, CreatedAt(x), i));
        }
        // Системная категория по slug главнее; одноимённых несколько — самая старая (по created_at, иначе первая в списке).
        var found = bySlugId ?? byTitle.OrderBy(c => c.CreatedAt ?? DateTime.MaxValue).ThenBy(c => c.Order).Select(c => c.Id).FirstOrDefault();
        if (bySlugId is null && byTitle.Count > 1)
            PosLogger.Log($"Выплата зарплаты: категорий «{CategoryTitle}» на сервере {byTitle.Count} — берём самую старую ({found}).", "WARNING");
        if (Interlocked.Exchange(ref _categoriesLogged, 1) == 0)
            PosLogger.Log($"Выплата зарплаты: категорий расходов {rows.Count}, «{CategoryTitle}» {(found is null ? "нет" : "есть")}.", "INFO");

        if (found is null && create)
        {
            try
            {
                var created = await Api.RequestAsync(HttpMethod.Post, "api/construction/cashflow-categories/",
                    new Dictionary<string, string> { ["title"] = CategoryTitle }, null, ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                found = IdOf(created) ?? Opt(created, "uuid");
                PosLogger.Log($"Выплата зарплаты: создана категория расходов «{CategoryTitle}» ({found ?? "сервер не вернул id"}).", "PAYMENT");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Выплата зарплаты: категорию «{CategoryTitle}» создать не удалось ({Describe(ex)}) — расход без категории.", "WARNING");
            }
        }

        lock (CategoryIdByCompany)
        {
            if (!string.IsNullOrEmpty(found))
            {
                CategoryIdByCompany[company] = found;
                NoCategoryUntilUtc.Remove(company);
                StoreCategoryId(company, found);
            }
            else if (!create)
            {
                NoCategoryUntilUtc[company] = DateTime.UtcNow.AddMinutes(10);
            }
        }
        return found;
    }

    /// <summary>id категории «Зарплата» компании из личных настроек («компания=id;…»).</summary>
    private static string? StoredCategoryId(string company)
    {
        foreach (var pair in (UserPreferences.Instance.SalaryCategoryIds ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && string.Equals(pair[..eq], company, StringComparison.OrdinalIgnoreCase) && pair[(eq + 1)..].Length > 0)
                return pair[(eq + 1)..];
        }
        return null;
    }

    /// <summary>Запомнить (или забыть — id null) категорию «Зарплата» компании в личных настройках.</summary>
    private static void StoreCategoryId(string company, string? id)
    {
        var pairs = (UserPreferences.Instance.SalaryCategoryIds ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith(company + "=", StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrEmpty(id))
            pairs.Add($"{company}={id}");
        var value = pairs.Count == 0 ? null : string.Join(";", pairs);
        if (value == UserPreferences.Instance.SalaryCategoryIds)
            return;
        UserPreferences.Instance.SalaryCategoryIds = value;
        try
        {
            UserPreferences.Instance.SaveToDisk();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выплата зарплаты: категория не запомнена в настройках: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Сервер отверг запомненную категорию (удалена на сайте) — забыть, при следующей выплате найти/создать заново.</summary>
    private static void ForgetCategoryId()
    {
        var company = CompanyId;
        lock (CategoryIdByCompany)
            CategoryIdByCompany.Remove(company);
        StoreCategoryId(company, null);
    }

    // ── Выплата ────────────────────────────────────────────────────────────────────

    public sealed record PayoutRequest(string Key, string UserId, string Employee, DateTime From, DateTime To, double Amount,
        PayoutCashbox Cashbox, string? Comment);

    public enum PayoutOutcome { Done, Rejected, Unknown }

    public sealed record PayoutResult(PayoutOutcome Outcome, string? FlowId, string? Status, string? Error);

    private static bool IsOwnerRole =>
        App.AuthApi.UserPayload is { ValueKind: JsonValueKind.Object } p
        && p.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
        && string.Equals(r.GetString(), "owner", StringComparison.OrdinalIgnoreCase);

    /// <summary>true — владелец: расход сразу «approved», как у сайта; иначе «pending» (ждёт подтверждения на сайте).</summary>
    public static bool ApprovedRightAway => IsOwnerRole;

    public static async Task<PayoutResult> PayAsync(PayoutRequest req, CancellationToken ct)
    {
        // Журнал выплат повреждён или компания не загружена — без них не защититься от двойной выплаты: не платим.
        if ((JournalBrokenText ?? CompanyUnknownText) is { } closed)
            return new PayoutResult(PayoutOutcome.Rejected, null, null, closed);

        var who = App.CurrentUserDisplayName ?? App.CurrentUserId ?? "—";
        var from = req.From.Date;
        var to = req.To.Date;
        var tag = NameTag(req.UserId, from, to);
        var baseName = FlowName(req.Employee, from, to) + (string.IsNullOrWhiteSpace(req.Comment) ? "" : " — " + req.Comment.Trim());
        // Метка — всегда и целиком: обрезается только начало названия.
        var name = (baseName.Length > 255 - tag.Length ? baseName[..(255 - tag.Length)] : baseName) + tag;

        // Замок: прошлая выплата этому сотруднику без ответа сервера — новую не отправляем (четвёртое ревью, п.1).
        // Журнал не прочитался — замок неизвестен, тоже не отправляем (пятая проверка, п.2).
        var (readable, locked) = LockFor(req.UserId);
        if (!readable)
            return new PayoutResult(PayoutOutcome.Rejected, null, null, JournalUnavailableText);
        if (locked is not null)
            return new PayoutResult(PayoutOutcome.Rejected, null, null, LockText(locked));

        var record = new SalaryPayoutRecord
        {
            Key = req.Key, CompanyId = CompanyId, UserId = req.UserId, Employee = req.Employee.Trim(),
            PeriodFrom = from, PeriodTo = to, Amount = Math.Round(req.Amount, 2),
            CashboxId = req.Cashbox.Id, CashboxName = req.Cashbox.Name, By = who, CreatedAt = DateTime.Now, Status = "sending",
            SourceId = _sourceIdPrefixRejected ? req.Key : SourceIdFor(req.UserId, from, to, req.Key),
        };
        // Четвёртое ревью (п.4): без записи «ушла на сервер» в журнале ПК нет защиты от двойной выплаты — не отправляем.
        if (!Upsert(record))
            return new PayoutResult(PayoutOutcome.Rejected, null, null, JournalBrokenText ?? Tr.T(
                "не удалось записать выплату в журнал этого ПК — выплата не отправлена",
                "төлөмдү ушул компьютердин журналына жазуу мүмкүн болгон жок — төлөм жөнөтүлгөн жок",
                "could not write the payout to this PC's log — the payout was not sent",
                "ödeme bu bilgisayarın günlüğüne yazılamadı — ödeme gönderilmedi",
                "to'lovni ushbu kompyuter jurnaliga yozib bo'lmadi — to'lov yuborilmadi"));

        // Пятая проверка (п.3): на категорию — свои ≤ 15 с ДО отправки; не успела (нет справочника, 403, сеть, время) —
        // расход уходит без категории (WARNING), это не «неизвестный исход».
        string? categoryId = null;
        using (var categoryCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            categoryCts.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                categoryId = await CategoryIdAsync(create: true, categoryCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                PosLogger.Log($"Выплата зарплаты: категория «{CategoryTitle}» не получена за 15 с — расход без категории, ключ {req.Key}.", "WARNING");
            }
            catch (OperationCanceledException)
            {
                // Отменено до отправки расхода — записи «ушла на сервер» не оставляем.
                Remove(req.Key);
                throw;
            }
        }

        var body = new Dictionary<string, string>
        {
            ["cashbox"] = req.Cashbox.Id,
            ["type"] = "expense",
            ["name"] = name,
            ["amount"] = record.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            ["status"] = ApprovedRightAway ? "approved" : "pending",
            ["source_id"] = record.SourceId,
            // Как у прихода от поставщика: поле в теле, сервер, не знающий его, пропускает.
            ["idempotency_key"] = req.Key,
        };
        if (!string.IsNullOrEmpty(categoryId))
            body["category"] = categoryId;
        if (!_sourceKindRejected)
            body["source_kind"] = SourceKind;

        // Повтор — только после явного 400 по одному из полей (запрос не записан). Третье ревью (п.8): до 5 попыток — по
        // числу полей, которые можно снять или исправить (category, source_kind, source_id ×2) + последняя.
        var fixes = new List<string>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                // Пятая проверка (п.3): на сам расход — свои 45 с.
                using var postCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                postCts.CancelAfter(TimeSpan.FromSeconds(45));
                var created = await App.ShiftApi.ConstructionCashFlowCreateAsync(body, postCts.Token).ConfigureAwait(false);
                var flowId = IdOf(created) ?? Opt(created, "uuid");
                var status = Opt(created, "status");
                record.Status = "ok";
                record.FlowId = flowId;
                Upsert(record);
                PosLogger.Log(
                    $"Выплата зарплаты: {record.Employee}, {record.Amount.ToString("0.00", CultureInfo.InvariantCulture)} сом за {PeriodText(from, to)}, "
                    + $"касса «{req.Cashbox.Name}», кто: {who}, ключ {req.Key}; расход {flowId ?? "?"}, статус {status ?? "—"}, категория "
                    + $"{Opt(created, "category_title") ?? IdOf(created, "category") ?? "—"}, source_id {Opt(created, "source_id") ?? "— (не сохранён)"}"
                    + (fixes.Count > 0 ? $"; исправления: {string.Join(", ", fixes)}" : "") + ".",
                    "PAYMENT");
                return new PayoutResult(PayoutOutcome.Done, flowId, status, null);
            }
            catch (ApiException ex) when (ex.StatusCode == 400 && RejectedField(ex, body) is { } field)
            {
                PosLogger.Log($"Выплата зарплаты: попытка {attempt + 1} — сервер отверг поле «{field}» ({ex.Message}), ключ {req.Key}.", "WARNING");
                fixes.Add(field);
                if (field == "source_id" && body["source_id"] != req.Key)
                {
                    // source_id «salary:…» не принят — ключ как есть; сотрудник и период — в метке названия.
                    _sourceIdPrefixRejected = true;
                    record.SourceId = req.Key;
                    Upsert(record);
                    body["source_id"] = req.Key;
                    continue;
                }
                body.Remove(field);
                if (field == "source_kind")
                    _sourceKindRejected = true;
                if (field == "category")
                    ForgetCategoryId();
                if (field == "source_id")
                {
                    record.SourceId = NoSourceId;
                    Upsert(record);
                }
            }
            catch (Exception ex) when (PendingIdempotencyKey.IsOutcomeUnknown(ex))
            {
                record.Status = "unknown";
                Upsert(record);
                PosLogger.Log($"Выплата зарплаты: ответ сервера не дошёл ({ex.GetType().Name}: {ex.Message}) — исход неизвестен, "
                              + $"НЕ повторяем. {record.Employee}, {record.Amount:0.00} сом, касса «{req.Cashbox.Name}», ключ {req.Key}.", "PAYMENT");
                return new PayoutResult(PayoutOutcome.Unknown, null, null, Describe(ex));
            }
            catch (Exception ex)
            {
                Remove(req.Key);
                PosLogger.Log($"Выплата зарплаты: сервер отказал ({Describe(ex)}), ключ {req.Key}.", "WARNING");
                return new PayoutResult(PayoutOutcome.Rejected, null, null, Describe(ex));
            }
        }

        Remove(req.Key);
        PosLogger.Log($"Выплата зарплаты: сервер отверг поля расхода после {fixes.Count} исправлений ({string.Join(", ", fixes)}), ключ {req.Key}.", "WARNING");
        return new PayoutResult(PayoutOutcome.Rejected, null, null, Tr.T(
            "сервер отверг поля расхода", "сервер чыгымдын талааларын четке какты", "the server rejected the expense fields",
            "sunucu gider alanlarını reddetti", "server xarajat maydonlarini rad etdi"));
    }

    /// <summary>source_id сервер не принял совсем — расход ушёл без него (найти можно только по метке в названии).</summary>
    private const string NoSourceId = "none";

    /// <summary>Замок на сотруднике: прошлая выплата без ответа сервера.</summary>
    public static string LockText(SalaryPayoutRecord r) => Tr.T(
        $"Прошлая выплата {SalaryWindow.Money(r.Amount)} сом ({r.CreatedAt:dd.MM HH:mm}, касса «{r.CashboxName}») без ответа сервера. Новая выплата этому сотруднику закрыта, пока не решите, что с ней.",
        $"Мурунку төлөм {SalaryWindow.Money(r.Amount)} сом ({r.CreatedAt:dd.MM HH:mm}, «{r.CashboxName}» кассасы) сервердин жообусуз. Аны чечмейинче бул кызматкерге жаңы төлөм жабык.",
        $"The previous payout of {SalaryWindow.Money(r.Amount)} som ({r.CreatedAt:dd.MM HH:mm}, “{r.CashboxName}” cash desk) got no server reply. A new payout to this employee is closed until you decide on it.",
        $"Önceki {SalaryWindow.Money(r.Amount)} som ödemesi ({r.CreatedAt:dd.MM HH:mm}, «{r.CashboxName}» kasası) sunucudan yanıt almadı. Karar verene kadar bu çalışana yeni ödeme kapalı.",
        $"Oldingi {SalaryWindow.Money(r.Amount)} so'm to'lov ({r.CreatedAt:dd.MM HH:mm}, «{r.CashboxName}» kassasi) serverdan javobsiz. Qaror qilmaguningizcha bu xodimga yangi to'lov yopiq.");

    /// <summary>«Неизвестная» выплата на сервере: true — найдена (по source_id или метке «[#uid период]» с суммой) и отмечена
    /// проведённой; false — не найдена или проверить не удалось. Четвёртое ревью (п.1): «не нашлась» ничего не решает — замок
    /// остаётся, снимает его только владелец (<see cref="ResolveUnknown"/>).</summary>
    public static async Task<bool> CheckUnknownAsync(string key, CancellationToken ct)
    {
        var record = LoadAll().FirstOrDefault(r => r.Key == key);
        if (record is null)
            return false;

        // 1) По source_id (у записей до вечера 19.10 source_id = ключ).
        if (record.SourceId != NoSourceId)
        {
            var sid = record.SourceId.Length > 0 ? record.SourceId : key;
            try
            {
                var bySource = await FilteredFlowsAsync(new Dictionary<string, string> { ["source_id"] = sid },
                    row => string.Equals(Opt(row, "source_id"), sid, StringComparison.OrdinalIgnoreCase), maxPages: 3, ct).ConfigureAwait(false);
                if (bySource?.Flows.FirstOrDefault(f => !f.Rejected) is { } hit)
                    return MarkFound(record, hit.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Выплата зарплаты: поиск расхода по source_id не удался ({Describe(ex)}).", "WARNING");
            }
        }

        // 2) По метке «[#uid период]» и сумме среди расходов с «Зарплата» в названии.
        try
        {
            if (await SearchSalaryFlowsAsync(ct).ConfigureAwait(false) is { } s)
            {
                var taken = LoadAll().Where(r => r.Key != key && !string.IsNullOrEmpty(r.FlowId)).Select(r => r.FlowId!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var hit = s.Flows.FirstOrDefault(f => !f.Rejected && !taken.Contains(f.Id) && SameRecord(f, ParseNameTag(f.Name), record));
                if (hit is not null)
                    return MarkFound(record, hit.Id);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выплата зарплаты: поиск расхода по метке не удался ({Describe(ex)}).", "WARNING");
        }

        PosLogger.Log($"Выплата зарплаты: расход с ключом {key} на сервере не найден — замок остаётся, решает владелец.", "PAYMENT");
        return false;
    }

    private static bool MarkFound(SalaryPayoutRecord record, string flowId)
    {
        record.Status = "ok";
        record.FlowId = flowId;
        Upsert(record);
        ForgetCache();
        PosLogger.Log($"Выплата зарплаты: расход с ключом {record.Key} найден на сервере ({flowId}) — выплата прошла.", "PAYMENT");
        return true;
    }

    /// <summary>Четвёртое ревью (п.1): решение владельца по «неизвестной» выплате. paidOnSite — «Выплата ЕСТЬ на сайте»: запись
    /// проведена (учитывается в «Выплачено»); иначе «Выплаты НЕТ на сайте»: запись «absent», замок снят. Строка [PAYMENT].</summary>
    public static bool ResolveUnknown(string key, bool paidOnSite)
    {
        var record = LoadAll().FirstOrDefault(r => r.Key == key);
        if (record is null)
            return false;
        record.Status = paidOnSite ? "ok" : "absent";
        if (!Upsert(record))
            return false;
        ForgetCache();
        var who = App.CurrentUserDisplayName ?? App.CurrentUserId ?? "—";
        PosLogger.Log($"Выплата зарплаты: решение владельца по выплате без ответа сервера — {(paidOnSite ? "ЕСТЬ на сайте, учтена" : "НЕТ на сайте, замок снят")}; "
                      + $"{record.Employee}, {record.Amount.ToString("0.00", CultureInfo.InvariantCulture)} сом, касса «{record.CashboxName}», кто: {who}, ключ {key}.", "PAYMENT");
        return true;
    }

    /// <summary>Третье ревью (п.7): серверный расход — та же выплата, что запись журнала без номера расхода: тот же сотрудник,
    /// период и сумма, записан в пределах ±10 минут от выплаты.</summary>
    private static bool SameRecord(ServerFlow f, PayoutId? who, SalaryPayoutRecord r) =>
        who is not null && string.Equals(who.UserId, r.UserId, StringComparison.OrdinalIgnoreCase)
        && who.From == r.PeriodFrom.Date && who.To == r.PeriodTo.Date
        && Math.Abs(f.Amount - r.Amount) < 0.005
        && (f.CreatedAt is not { } at || Math.Abs((at - r.CreatedAt).TotalMinutes) <= 10);

    /// <summary>Поле, которое сервер назвал в отказе 400 (category / source_kind / source_id — без них расход всё равно верен).</summary>
    private static string? RejectedField(ApiException ex, Dictionary<string, string> body)
    {
        foreach (var field in new[] { "category", "source_kind", "source_id" })
        {
            if (!body.ContainsKey(field))
                continue;
            if (ex.Payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(field, out _))
                return field;
            if (ex.Message.Contains(field, StringComparison.OrdinalIgnoreCase))
                return field;
        }
        return null;
    }

    private static string Describe(Exception ex) => ex is ApiException a && a.StatusCode is { } code ? $"{code}: {a.Message}" : ex.Message;

    // ── Выплачено за период ────────────────────────────────────────────────────────

    /// <summary>Насколько полно прочитаны выплаты с сервера. Full — видны все выплаты (в т. ч. без категории); Partial — только
    /// расходы категории «Зарплата», есть непривязанные расходы «Зарплата» или проверка не успела; None — только журнал этого ПК.</summary>
    public enum PaidCoverage { None, Partial, Full }

    /// <summary>Выплачено сотруднику за период: Paid — выплаты целиком внутри периода; ByName — часть найдена только по ФИО в
    /// названии; PartialNotes — выплаты, лишь частично попавшие в период (не вычтены).</summary>
    public sealed record UserPaid(double Paid, bool ByName, IReadOnlyList<string> PartialNotes);

    public sealed record PaidResult(Dictionary<string, UserPaid> Users, PaidCoverage Coverage, double UnassignedSum, int UnassignedCount,
        bool JournalUnavailable = false);

    /// <summary>Четвёртое ревью (п.9): одно правило «доверять ли «Выплачено» и подставлять ли сумму» — для таблицы, окна выплаты
    /// и пересчёта. Sure=false — сумма не подставляется, Warning — жёлтая плашка с причиной.</summary>
    public static (bool Sure, string? Warning) Trust(PaidResult? result, UserPaid? info, bool timedOut)
    {
        if (timedOut)
            return (false, TimeoutPlate);
        if (result is { JournalUnavailable: true })
            return (false, JournalUnavailableText);
        if (result is null || result.Coverage == PaidCoverage.None)
            return (false, NotCheckedPlate);
        if (result.UnassignedCount > 0)
            return (false, UnassignedPlate(result.UnassignedSum, result.UnassignedCount));
        if (result.Coverage == PaidCoverage.Partial)
            return (false, PartialPlate);
        if (info is { PartialNotes.Count: > 0 })
            return (false, string.Join(" ", info.PartialNotes));
        if (info is { ByName: true })
            return (false, ByNamePlate);
        return (true, null);
    }

    private sealed record ServerFlow(string Id, string SourceId, string Name, double Amount, bool Rejected, DateTime? CreatedAt, DateTime? Date, string? Category);

    private sealed record PayoutId(string UserId, DateTime From, DateTime To, string? Key);

    private sealed record ServerRead(List<ServerFlow> Flows, PaidCoverage Coverage, bool SearchFull, string? CategoryId);

    private sealed record CachedRead(string Company, DateTime From, DateTime To, DateTime LoadedUtc, ServerRead Read);

    private static CachedRead? _cache;

    /// <summary>Запомненные расходы сбрасываются: «Обновить», своя выплата, найденная «неизвестная» выплата. Кэш 2 минуты —
    /// только для переключения разделов и повторного открытия окна (третье ревью, п.2).</summary>
    public static void ForgetCache() => _cache = null;

    private sealed class Tally
    {
        public double Paid;
        public bool ByName;
        public readonly List<string> Notes = new();
    }

    /// <summary>Выплачено сотрудникам (ключ — user_id) за период на экране. Выплата засчитывается, если её период зарплаты
    /// целиком внутри периода на экране; пересекается частично — пометка в строке, не вычитается (третье ревью, п.4). Чья
    /// выплата: source_id → метка в названии → журнал этого ПК (ид расхода, ключ, или сотрудник+период+сумма+время ±10 мин)
    /// → точное ФИО в названии (пометка «по названию»). Расходы «Зарплата», которые не приписать никому, — отдельной суммой
    /// (покрытие «частично»). Журнал этого ПК учитывается всегда, без двойного счёта.</summary>
    public static async Task<PaidResult> PaidForPeriodAsync(IReadOnlyCollection<(string UserId, string Name)> employees, DateTime from, DateTime to, CancellationToken ct)
    {
        from = from.Date;
        to = to.Date;
        var users = new HashSet<string>(employees.Select(e => e.UserId).Where(u => !string.IsNullOrWhiteSpace(u)), StringComparer.OrdinalIgnoreCase);
        var company = CompanyId;
        var all = TryLoadAll();
        var journal = (all ?? new()).Where(r => r.CompanyId.Length > 0 && string.Equals(r.CompanyId, company, StringComparison.OrdinalIgnoreCase)).ToList();
        var index = new JournalIndex(journal);
        var names = NameMatchers(employees);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // ключи записей журнала, уже сопоставленные с сервером
        var tallies = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var serverIds = new Dictionary<string, ServerFlow>(StringComparer.OrdinalIgnoreCase);
        double unassignedSum = 0;
        var unassignedCount = 0;

        void Attribute(string uid, DateTime pFrom, DateTime pTo, double amount, bool byName)
        {
            if (!users.Contains(uid) || !Overlaps(pFrom, pTo, from, to))
                return;
            if (!tallies.TryGetValue(uid, out var t))
                tallies[uid] = t = new Tally();
            if (Inside(pFrom, pTo, from, to))
            {
                t.Paid += amount;
                t.ByName |= byName;
            }
            else
            {
                t.Notes.Add(PartialOverlapNote(pFrom, pTo, amount));
            }
        }

        var read = await ServerFlowsAsync(company, from, to, ct).ConfigureAwait(false);
        foreach (var f in read.Flows)
        {
            serverIds[f.Id] = f;
            if (f.Rejected)
                continue;
            var who = ParseSourceId(f.SourceId) ?? ParseNameTag(f.Name);
            var rec = MatchJournal(f, who, index, used);
            if (rec is not null)
            {
                used.Add(rec.Key);
                who ??= new PayoutId(rec.UserId, rec.PeriodFrom.Date, rec.PeriodTo.Date, rec.Key);
            }
            if (who is not null)
            {
                Attribute(who.UserId, who.From, who.To, f.Amount, false);
                continue;
            }
            if (!IsSalaryLike(f, read.CategoryId))
                continue;
            // Не приписан ни меткой, ни журналом — точное ФИО сотрудника в названии (старый формат, выплата с другого ПК).
            var byName = MatchByName(f.Name, names);
            var period = ParsePeriodInParens(f.Name);
            if (byName is not null)
            {
                var (pFrom, pTo) = period ?? (f.Date is { } d ? (d, d) : (DateTime.MinValue, DateTime.MinValue));
                // Пятая проверка (п.5): запись «ЕСТЬ на сайте» без номера расхода — та же выплата, что этот расход по ФИО,
                // если совпали сотрудник, период и сумма: считаем один раз (и уже не «по ФИО»).
                var manual = index.WithoutFlowId.FirstOrDefault(r => !used.Contains(r.Key) && r.Status == "ok"
                    && string.Equals(r.UserId, byName, StringComparison.OrdinalIgnoreCase) && Math.Abs(r.Amount - f.Amount) < 0.005
                    && (period is null || (r.PeriodFrom.Date == pFrom && r.PeriodTo.Date == pTo)));
                if (manual is not null)
                {
                    used.Add(manual.Key);
                    Attribute(byName, manual.PeriodFrom.Date, manual.PeriodTo.Date, f.Amount, false);
                }
                else if (period is not null || (f.Date is { } fd0 && fd0 >= from && fd0 <= to))
                {
                    Attribute(byName, pFrom, pTo, f.Amount, true);
                }
                continue;
            }
            // Чей — не понять: если расход за этот период (по периоду в названии или по дате), не теряем молча (п.3).
            var inPeriod = period is { } pp ? Overlaps(pp.From, pp.To, from, to) : f.Date is { } fd && fd >= from && fd <= to;
            if (inPeriod)
            {
                unassignedSum += f.Amount;
                unassignedCount++;
            }
        }

        foreach (var r in journal)
        {
            if (r.Status != "ok" || !users.Contains(r.UserId) || used.Contains(r.Key))
                continue;
            if (r.FlowId is { Length: > 0 } fid)
            {
                // Сервер знает расход как отклонённый — денег не ушло.
                if (serverIds.TryGetValue(fid, out var known) && known.Rejected)
                    continue;
                // Полностью прочитанный сервер этот расход должен был показать — его там нет (удалён на сайте).
                var shouldSee = read.Coverage == PaidCoverage.Full && (read.SearchFull || r.CreatedAt.Date >= from);
                if (shouldSee && !serverIds.ContainsKey(fid))
                    continue;
            }
            Attribute(r.UserId, r.PeriodFrom.Date, r.PeriodTo.Date, r.Amount, false);
        }

        var coverage = read.Coverage;
        if (unassignedCount > 0 && coverage == PaidCoverage.Full)
            coverage = PaidCoverage.Partial;
        if (unassignedCount > 0)
            PosLogger.Log($"Выплата зарплаты: расходов «Зарплата» без привязки к сотруднику за период — {unassignedCount} на {unassignedSum:0.00} сом.", "WARNING");
        var result = tallies.ToDictionary(kv => kv.Key, kv => new UserPaid(Math.Round(kv.Value.Paid, 2), kv.Value.ByName, kv.Value.Notes),
            StringComparer.OrdinalIgnoreCase);
        return new PaidResult(result, coverage, Math.Round(unassignedSum, 2), unassignedCount, JournalUnavailable: all is null);
    }

    /// <summary>Журнал ПК, разложенный по номеру расхода, ключу и source_id (четвёртое ревью, п.10).</summary>
    private sealed class JournalIndex
    {
        public readonly Dictionary<string, SalaryPayoutRecord> ByFlowId = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, SalaryPayoutRecord> ByKey = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, SalaryPayoutRecord> BySourceId = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Записи без номера расхода — для сопоставления по сотруднику+периоду+сумме+времени.</summary>
        public readonly List<SalaryPayoutRecord> WithoutFlowId = new();

        public JournalIndex(IEnumerable<SalaryPayoutRecord> journal)
        {
            foreach (var r in journal)
            {
                if (!string.IsNullOrEmpty(r.FlowId))
                    ByFlowId.TryAdd(r.FlowId, r);
                else if (r.Status is "ok" or "unknown" or "sending")
                    WithoutFlowId.Add(r);
                if (r.Key.Length > 0)
                    ByKey.TryAdd(r.Key, r);
                if (r.SourceId.Length > 0 && r.SourceId != NoSourceId)
                    BySourceId.TryAdd(r.SourceId, r);
            }
        }
    }

    /// <summary>Запись журнала этого ПК для серверного расхода: по ид расхода, по ключу (из source_id «salary:…» или source_id =
    /// ключ у старых выплат), по source_id, по сотруднику+периоду+сумме+времени ±10 мин (запись без ид расхода). Каждая — один раз.</summary>
    private static SalaryPayoutRecord? MatchJournal(ServerFlow f, PayoutId? who, JournalIndex index, HashSet<string> used)
    {
        SalaryPayoutRecord? Free(SalaryPayoutRecord? r) => r is not null && !used.Contains(r.Key) ? r : null;
        return Free(index.ByFlowId.GetValueOrDefault(f.Id))
               ?? (who?.Key is { } key ? Free(index.ByKey.GetValueOrDefault(key)) : null)
               ?? (f.SourceId.Length > 0 ? Free(index.ByKey.GetValueOrDefault(f.SourceId)) ?? Free(index.BySourceId.GetValueOrDefault(f.SourceId)) : null)
               ?? index.WithoutFlowId.FirstOrDefault(r => !used.Contains(r.Key) && SameRecord(f, who, r));
    }

    /// <summary>Расход относится к зарплате: категория «Зарплата» или слово «Зарплата» в названии.</summary>
    private static bool IsSalaryLike(ServerFlow f, string? categoryId) =>
        (!string.IsNullOrEmpty(categoryId) && string.Equals(f.Category, categoryId, StringComparison.OrdinalIgnoreCase))
        || f.Name.Contains("зарплат", StringComparison.OrdinalIgnoreCase);

    /// <summary>Регэкспы «точное ФИО целиком» — один раз на вызов (четвёртое ревью, п.10).</summary>
    private static List<(string UserId, int Length, Regex Rx)> NameMatchers(IReadOnlyCollection<(string UserId, string Name)> employees) =>
        employees
            .Select(e => (e.UserId, Name: e.Name.Trim()))
            .Where(e => e.Name.Length >= 3 && e.Name != "—")
            .Select(e => (e.UserId, e.Name.Length,
                new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(e.Name)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            .ToList();

    /// <summary>Сотрудник, чьё ФИО целиком есть в названии (самое длинное совпадение; два разных одинаковой длины — не понять).</summary>
    private static string? MatchByName(string flowName, List<(string UserId, int Length, Regex Rx)> names)
    {
        var hits = names.Where(n => n.Rx.IsMatch(flowName)).OrderByDescending(n => n.Length).ToList();
        if (hits.Count == 0)
            return null;
        if (hits.Count > 1 && hits[1].Length == hits[0].Length && hits[1].UserId != hits[0].UserId)
            return null;
        return hits[0].UserId;
    }

    private static PayoutId? ParseSourceId(string sourceId)
    {
        var m = SourceIdRx.Match(sourceId);
        if (!m.Success
            || !DateTime.TryParseExact(m.Groups["f"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)
            || !DateTime.TryParseExact(m.Groups["t"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return null;
        return new PayoutId(m.Groups["u"].Value, f, t, m.Groups["k"].Value);
    }

    private static PayoutId? ParseNameTag(string name)
    {
        var m = NameTagRx.Match(name);
        if (!m.Success
            || !DateTime.TryParseExact(m.Groups["f"].Value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)
            || !DateTime.TryParseExact(m.Groups["t"].Value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return null;
        return new PayoutId(m.Groups["u"].Value, f, t, null);
    }

    private static (DateTime From, DateTime To)? ParsePeriodInParens(string name)
    {
        var m = ParenPeriodRx.Match(name);
        if (!m.Success || !DateTime.TryParseExact(m.Groups["f"].Value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f))
            return null;
        var t = f;
        if (m.Groups["t"].Success && !DateTime.TryParseExact(m.Groups["t"].Value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
            return null;
        return (f, t);
    }

    /// <summary>Выплаты с сервера (кэш 2 минуты по периоду — для переключения разделов; «Обновить» и своя выплата его сбрасывают).
    /// 1) ?category=&lt;Зарплата&gt; и 2) ?type=expense&amp;search=Зарплата — параллельно (третье ревью, п.9); 3) только если 2) не
    /// сработал — ?type=expense&amp;date_from=&lt;начало периода&gt; (не больше 4 страниц). Каждый способ — в своём try.
    /// Full — сработал 2) или 3); Partial — только 1); None — ничего.</summary>
    private static async Task<ServerRead> ServerFlowsAsync(string company, DateTime from, DateTime to, CancellationToken ct)
    {
        if (_cache is { } c && c.Company == company && c.From == from && c.To == to && DateTime.UtcNow - c.LoadedUtc < TimeSpan.FromMinutes(2))
            return c.Read;

        string? categoryId = null;
        async Task<List<ServerFlow>?> ByCategoryAsync()
        {
            try
            {
                categoryId = await CategoryIdAsync(create: false, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(categoryId))
                    return null;
                var id = categoryId;
                var a = await FilteredFlowsAsync(new Dictionary<string, string> { ["category"] = id },
                    row => CategoryOf(row) == id, maxPages: 10, ct).ConfigureAwait(false);
                return a is { Complete: true } ok ? ok.Flows : null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Выплата зарплаты: расходы категории «{CategoryTitle}» не прочитаны ({Describe(ex)}).", "WARNING");
                return null;
            }
        }

        async Task<List<ServerFlow>?> BySearchAsync()
        {
            try
            {
                var b = await SearchSalaryFlowsAsync(ct).ConfigureAwait(false);
                return b is { Complete: true } ok ? ok.Flows : null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Выплата зарплаты: поиск расходов «Зарплата» не удался ({Describe(ex)}).", "WARNING");
                return null;
            }
        }

        var categoryTask = ByCategoryAsync();
        var searchTask = BySearchAsync();
        await Task.WhenAll(categoryTask, searchTask).ConfigureAwait(false);

        var byId = new Dictionary<string, ServerFlow>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in (categoryTask.Result ?? new()).Concat(searchTask.Result ?? new()))
            byId[f.Id] = f;
        var searchFull = searchTask.Result is not null;
        var full = searchFull;

        if (!full)
        {
            try
            {
                var all = await FilteredFlowsAsync(new Dictionary<string, string>
                    {
                        ["type"] = "expense",
                        // Как у остальных отчётов NurCRM: date_from/date_to — дата расхода не раньше начала периода зарплаты.
                        ["date_from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["date_to"] = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    },
                    row => string.Equals(Opt(row, "type"), "expense", StringComparison.OrdinalIgnoreCase)
                           && (FlowDate(row) is not { } d || d >= from),
                    maxPages: 4, ct).ConfigureAwait(false);
                if (all is { Complete: true } e)
                {
                    full = true;
                    foreach (var f in e.Flows)
                        byId[f.Id] = f;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Выплата зарплаты: расходы с начала периода не прочитаны ({Describe(ex)}).", "WARNING");
            }
        }

        var coverage = full ? PaidCoverage.Full : categoryTask.Result is not null ? PaidCoverage.Partial : PaidCoverage.None;
        if (coverage != PaidCoverage.Full)
            PosLogger.Log($"Выплата зарплаты: выплаты с сервера прочитаны {(coverage == PaidCoverage.Partial ? "частично (только категория)" : "не были")} — плюс журнал этого ПК.", "WARNING");
        var read = new ServerRead(byId.Values.ToList(), coverage, searchFull, categoryId);
        if (coverage != PaidCoverage.None)
            _cache = new CachedRead(company, from, to, DateTime.UtcNow, read);
        return read;
    }

    /// <summary>?type=expense&amp;search=Зарплата — все расходы с «Зарплата» в названии (в т. ч. без категории). null — сервер
    /// не ищет по названию (в ответе чужие строки).</summary>
    private static Task<(List<ServerFlow> Flows, bool Complete)?> SearchSalaryFlowsAsync(CancellationToken ct) =>
        FilteredFlowsAsync(new Dictionary<string, string> { ["type"] = "expense", ["search"] = CategoryTitle },
            row => string.Equals(Opt(row, "type"), "expense", StringComparison.OrdinalIgnoreCase)
                   && ((Opt(row, "name") ?? "").Contains(CategoryTitle, StringComparison.OrdinalIgnoreCase)
                       || (Opt(row, "category_title") ?? "").Contains(CategoryTitle, StringComparison.OrdinalIgnoreCase)),
            maxPages: 10, ct);

    /// <summary>Страницы движений с фильтром. null — сервер не соблюдает фильтр (в ответе чужие строки); Complete=false —
    /// страниц больше maxPages (история целиком не скачивается).</summary>
    private static async Task<(List<ServerFlow> Flows, bool Complete)?> FilteredFlowsAsync(Dictionary<string, string> filter,
        Func<JsonElement, bool> matches, int maxPages, CancellationToken ct)
    {
        var list = new List<ServerFlow>();
        for (var page = 1; page <= maxPages; page++)
        {
            var query = new Dictionary<string, string>(filter)
            {
                ["page_size"] = "500",
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
            };
            var data = await Api.RequestAsync(HttpMethod.Get, "api/construction/cashflows/", null, query, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var rows = NurMarketApiClient.UnwrapList(data);
            if (rows.Any(r => !matches(r)))
            {
                PosLogger.Log($"Выплата зарплаты: сервер не соблюдает фильтр движений ?{string.Join("&", filter.Keys)}= (в ответе чужие строки).", "INFO");
                return null;
            }
            foreach (var r in rows)
            {
                if (IdOf(r) is not { } id || !string.Equals(Opt(r, "type"), "expense", StringComparison.OrdinalIgnoreCase))
                    continue;
                list.Add(new ServerFlow(id, Opt(r, "source_id") ?? "", Opt(r, "name") ?? "", SalaryWindow.Num(r, "amount"),
                    string.Equals(Opt(r, "status"), "rejected", StringComparison.OrdinalIgnoreCase), CreatedAt(r), FlowDate(r), CategoryOf(r)));
            }
            var hasNext = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String;
            if (!hasNext)
                return (list, true);
        }
        PosLogger.Log($"Выплата зарплаты: движений ?{string.Join("&", filter.Keys)}= больше {maxPages} страниц — этим способом не считаем.", "INFO");
        return (list, false);
    }

    private static DateTime? CreatedAt(JsonElement row) =>
        Opt(row, "created_at") is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at)
            ? at.LocalDateTime
            : null;

    /// <summary>Дата расхода (местная): поле date, иначе created_at.</summary>
    private static DateTime? FlowDate(JsonElement row)
    {
        if (Opt(row, "date") is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at))
            return text.Length <= 10 ? at.Date : at.LocalDateTime.Date;
        return CreatedAt(row)?.Date;
    }

    private static string? CategoryOf(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("category", out var c))
            return null;
        return c.ValueKind == JsonValueKind.Object ? IdOf(c) ?? Opt(c, "uuid") : IdOf(row, "category");
    }

    // ── Журнал выплат этого ПК ─────────────────────────────────────────────────────

    public sealed class SalaryPayoutRecord
    {
        public string Key { get; set; } = "";
        public string CompanyId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string Employee { get; set; } = "";
        public DateTime PeriodFrom { get; set; }
        public DateTime PeriodTo { get; set; }
        public double Amount { get; set; }
        public string CashboxId { get; set; } = "";
        public string CashboxName { get; set; } = "";
        public string By { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        /// <summary>sending — ушла на сервер; ok — сервер записал (или владелец: «есть на сайте»); unknown — ответ не дошёл
        /// (замок); absent — владелец: «выплаты нет на сайте» (не проведена, замок снят).</summary>
        public string Status { get; set; } = "";
        public string? FlowId { get; set; }
        /// <summary>source_id, с которым ушёл расход («none» — без source_id; пусто у записей до вечера 19.10 — там source_id = Key).</summary>
        public string SourceId { get; set; } = "";
    }

    private static string JournalPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "salary_payouts.json");

    /// <summary>Замок: неразрешённая выплата этого сотрудника (ответ не дошёл) — новая выплата закрыта, пока владелец не решит.
    /// Пятая проверка: Readable=false — журнал не прочитался, замок неизвестен (выплаты закрыты); старые записи без компании —
    /// замок для всех компаний на этом ПК.</summary>
    public static (bool Readable, SalaryPayoutRecord? Lock) LockFor(string userId)
    {
        if (TryLoadAll() is not { } all)
            return (false, null);
        var company = CompanyId;
        var record = all.Where(r => r.Status is "unknown" or "sending" && r.UserId == userId
                                    && (r.CompanyId.Length == 0 || string.Equals(r.CompanyId, company, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.CreatedAt).FirstOrDefault();
        return (true, record);
    }

    /// <summary>Замок сотрудника (журнал не прочитался — null; для отправки и окна — <see cref="LockFor"/>).</summary>
    public static SalaryPayoutRecord? UnknownFor(string userId) => LockFor(userId).Lock;

    /// <summary>Журнал для проверок и решений (не прочитался — пусто; там, где пусто опасно, — <see cref="TryLoadAll"/>).</summary>
    public static List<SalaryPayoutRecord> LoadAll() => TryLoadAll() ?? new();

    /// <summary>Пятая проверка (п.2): чтение журнала с повтором (3 раза через 200 мс, файл бывает занят). null — не прочитался:
    /// это НЕ пустой журнал. Повреждённый журнал — пусто (выплаты закрыты флагом <see cref="JournalBrokenText"/>).</summary>
    private static List<SalaryPayoutRecord>? TryLoadAll()
    {
        lock (FileLock)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return ReadJournalLocked() ?? new();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 3)
                    {
                        PosLogger.Log($"Журнал выплат зарплаты не прочитан за 3 попытки: {ex.Message}", "WARNING");
                        return null;
                    }
                    Thread.Sleep(200);
                }
            }
        }
    }

    /// <summary>Чтение журнала. null — журнал повреждён (третье ревью, п.6): файл переименован в salary_payouts.bad-&lt;время&gt;.json,
    /// ERROR в журнал программы, выплаты закрыты до перезапуска (как в оплате долга). Ошибка ввода-вывода — исключение.</summary>
    private static List<SalaryPayoutRecord>? ReadJournalLocked()
    {
        if (_journalBrokenPath is not null)
            return null;
        if (!File.Exists(JournalPath))
            return new();
        var text = File.ReadAllText(JournalPath);
        try
        {
            return JsonSerializer.Deserialize<List<SalaryPayoutRecord>>(text, JsonOpts) ?? new();
        }
        catch (JsonException ex)
        {
            var bad = Path.Combine(Path.GetDirectoryName(JournalPath)!, $"salary_payouts.bad-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try
            {
                File.Move(JournalPath, bad);
            }
            catch (Exception moveEx)
            {
                PosLogger.Log($"Журнал выплат зарплаты не переименован: {moveEx.Message}", "ERROR");
                bad = JournalPath;
            }
            _journalBrokenPath = bad;
            PosLogger.Log($"Журнал выплат зарплаты повреждён ({ex.Message}) — сохранён как {bad}; выплаты закрыты.", "ERROR");
            return null;
        }
    }

    private static bool Upsert(SalaryPayoutRecord record) => Change(list =>
    {
        list.RemoveAll(r => r.Key == record.Key);
        list.Insert(0, record);
    });

    private static bool Remove(string key) => Change(list => list.RemoveAll(r => r.Key == key));

    /// <summary>Изменение журнала; false — не записано (повреждён, ошибка диска).</summary>
    private static bool Change(Action<List<SalaryPayoutRecord>> change)
    {
        lock (FileLock)
        {
            try
            {
                // Повреждённый журнал не перезаписываем — он сохранён для поддержки, выплаты закрыты.
                if (ReadJournalLocked() is not { } list)
                    return false;
                change(list);
                Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
                var tmp = JournalPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(list.Take(2000).ToList(), JsonOpts));
                File.Move(tmp, JournalPath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Журнал выплат зарплаты не записан: {ex.Message}", "WARNING");
                return false;
            }
        }
    }
}
