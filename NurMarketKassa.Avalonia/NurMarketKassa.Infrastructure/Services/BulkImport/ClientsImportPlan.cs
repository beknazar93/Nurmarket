using System.Text.Json;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.InvoiceImport;

namespace NurMarketKassa.Services;

// 2026-11-13 (Л-10, «Новичок: перенос данных»): перенос клиентов из чужой программы (Excel/CSV: 1С, МойСклад, Paloma365, Umag, тетрадь).
// Клиенты заводятся ТОЛЬКО существующим запросом IClientsApiService.CreateClientAsync (имя, телефон, адрес) — как кнопка «Новый клиент».
// Долги из файла в кассу НЕ записываются: в API нет безопасного способа завести «начальный долг» (новый денежный путь без ТЗ), вопрос — Умару
// (docs/Вопросы Умару 13.11.md, п. «Начальный долг клиента»). План лишь считает и показывает сумму долгов, чтобы владелец мог внести их вручную.

public enum ClientImportField
{
    Name,
    Phone,
    Address,
    Debt,
}

public sealed class ClientImportRow
{
    /// <summary>Номер строки так, как его видит человек в Excel.</summary>
    public int FileRow { get; init; }
    public string Name { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Address { get; init; } = "";

    /// <summary>Долг из файла (0 — нет). Не записывается, только показывается.</summary>
    public double Debt { get; init; }

    /// <summary>Строку нельзя загрузить: причина на языке кассы. null — можно.</summary>
    public string? Problem { get; set; }

    /// <summary>Клиент с таким телефоном уже есть (в файле выше или в базе) — пропускается без ошибки.</summary>
    public bool AlreadyExists { get; set; }

    /// <summary>Клиент заведён этим запуском (повторная загрузка его не тронет).</summary>
    public bool Done { get; set; }

    public bool Ready => Problem is null && !AlreadyExists && !Done;
}

public sealed class ClientsImportPlan
{
    public IReadOnlyList<ClientImportRow> Rows { get; init; } = [];
    public IReadOnlyDictionary<ClientImportField, int> Columns { get; init; } = new Dictionary<ClientImportField, int>();

    /// <summary>Нет столбца имени или телефона — загрузка невозможна; текст для окна.</summary>
    public string? Problem { get; init; }

    public int ReadyCount => Rows.Count(r => r.Ready);
    public int ErrorCount => Rows.Count(r => r.Problem is not null);
    public int ExistsCount => Rows.Count(r => r.AlreadyExists);
    public int DebtorsCount => Rows.Count(r => r.Debt > 0);
    public double DebtTotal => Rows.Sum(r => r.Debt);

    private static readonly Dictionary<ClientImportField, string[]> Exact = new()
    {
        [ClientImportField.Name] = ["фио", "имя", "клиент", "контрагент", "покупатель", "наименование", "name", "fullname", "customer", "client", "аты", "атыжөнү", "кардар", "ism", "mijoz", "müşteri", "adsoyad", "ad"],
        [ClientImportField.Phone] = ["телефон", "тел", "номертелефона", "контактныйтелефон", "мобильный", "мобильныйтелефон", "phone", "mobile", "tel", "telefon", "raqam", "телефонномери"],
        [ClientImportField.Address] = ["адрес", "address", "дарек", "adres", "manzil"],
        [ClientImportField.Debt] = ["долг", "задолженность", "суммадолга", "сальдо", "остатокдолга", "debt", "balance", "карыз", "borç", "borc", "qarz", "дебиторскаязадолженность"],
    };

    private static readonly (ClientImportField Field, string[] Words)[] Partial =
    [
        (ClientImportField.Debt, ["долг", "задолж", "сальдо", "debt", "карыз", "borç", "qarz"]),
        (ClientImportField.Phone, ["телефон", "phone", "telefon", "мобил"]),
        (ClientImportField.Address, ["адрес", "address", "дарек", "adres", "manzil"]),
        (ClientImportField.Name, ["фио", "клиент", "контрагент", "покупател", "fullname", "кардар", "mijoz", "müşteri", "наименование"]),
    ];

    /// <summary>Подобрать столбцы по заголовкам.</summary>
    public static Dictionary<ClientImportField, int> DetectColumns(IReadOnlyList<string> headers)
    {
        var map = new Dictionary<ClientImportField, int>();
        var keys = headers.Select(ProductImportMapping.Key).ToArray();
        for (var i = 0; i < keys.Length; i++)
        {
            foreach (var (field, words) in Exact)
            {
                if (!map.ContainsKey(field) && keys[i].Length > 0 && words.Contains(keys[i]))
                {
                    map[field] = i;
                    break;
                }
            }
        }
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i].Length == 0 || map.ContainsValue(i))
                continue;
            foreach (var (field, words) in Partial)
            {
                if (!map.ContainsKey(field) && words.Any(w => keys[i].Contains(w, StringComparison.Ordinal)))
                {
                    map[field] = i;
                    break;
                }
            }
        }
        return map;
    }

    /// <summary>Узнана ли таблица как список клиентов (есть имя и телефон) — чтобы окно предложило «Клиенты» вместо «Товары».</summary>
    public static bool LooksLikeClients(IReadOnlyList<string> headers)
    {
        var map = DetectColumns(headers);
        return map.ContainsKey(ClientImportField.Name) && map.ContainsKey(ClientImportField.Phone)
            && !ProductImportMapping.AutoDetect(headers).Columns.ContainsKey(ProductImportField.Barcode);
    }

    /// <summary>Разобрать таблицу. <paramref name="existingNationalPhones"/> — последние 9 цифр телефонов клиентов, уже заведённых в NurCRM.</summary>
    public static ClientsImportPlan Build(ProductFileTable table, IReadOnlyDictionary<ClientImportField, int>? columns = null, IReadOnlySet<string>? existingNationalPhones = null)
    {
        var cols = columns ?? DetectColumns(table.Headers);
        if (!cols.ContainsKey(ClientImportField.Name) || !cols.ContainsKey(ClientImportField.Phone))
        {
            return new ClientsImportPlan
            {
                Columns = cols,
                Problem = Tr.T("Не нашёл столбцы «Имя» и «Телефон». Нужны оба; «Адрес» и «Долг» — по желанию.",
                    "«Аты» жана «Телефон» мамычалары табылган жок. Экөө тең керек; «Дарек» жана «Карыз» — кааласаңыз.",
                    "Could not find the “Name” and “Phone” columns. Both are required; “Address” and “Debt” are optional.",
                    "«Ad» ve «Telefon» sütunları bulunamadı. İkisi de gerekli; «Adres» ve «Borç» isteğe bağlı.",
                    "«Ism» va «Telefon» ustunlari topilmadi. Ikkalasi ham kerak; «Manzil» va «Qarz» — xohlasangiz."),
            };
        }

        var rows = new List<ClientImportRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var src in table.DataRows)
        {
            string Cell(ClientImportField f) => cols.TryGetValue(f, out var c) ? src[c].Trim() : "";
            var name = Cell(ClientImportField.Name);
            var phone = Cell(ClientImportField.Phone);
            var address = Cell(ClientImportField.Address);
            if (name.Length == 0 && phone.Length == 0 && address.Length == 0)
                continue;
            double debt = 0;
            var debtText = Cell(ClientImportField.Debt);
            var debtOk = debtText.Length == 0 || InvoiceText.TryParseNumber(debtText, out debt);

            var row = new ClientImportRow { FileRow = src.FileRow, Name = name, Phone = phone, Address = address, Debt = debtOk && debt > 0 ? debt : 0 };
            if (name.Length == 0)
                row.Problem = Tr.T("нет имени", "аты жок", "no name", "ad yok", "ism yo'q");
            else if (phone.Length == 0)
                row.Problem = Tr.T("нет телефона", "телефон жок", "no phone", "telefon yok", "telefon yo'q");
            else if (NationalOf(phone) is not { } national)
                row.Problem = Tr.T("телефон не похож на номер", "телефон номерге окшобойт", "the phone does not look like a number", "telefon numaraya benzemiyor", "telefon raqamga o'xshamaydi");
            else if (!seen.Add(national) || existingNationalPhones?.Contains(national) == true)
                row.AlreadyExists = true;
            if (!debtOk && row.Problem is null)
                row.Problem = Tr.T("долг не число", "карыз сан эмес", "the debt is not a number", "borç sayı değil", "qarz son emas");
            rows.Add(row);
        }
        return new ClientsImportPlan { Rows = rows, Columns = cols };
    }

    /// <summary>Последние 9 цифр номера (так касса сравнивает клиентов); меньше 9 цифр — null.</summary>
    public static string? NationalOf(string phone)
    {
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 9 ? digits[^9..] : null;
    }

    /// <summary>Телефоны уже заведённых клиентов (последние 9 цифр).</summary>
    public static HashSet<string> ExistingPhones(IEnumerable<JsonElement> clients)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in clients)
        {
            if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("phone", out var p) && p.ValueKind == JsonValueKind.String
                && NationalOf(p.GetString() ?? "") is { } n)
                set.Add(n);
        }
        return set;
    }

    /// <summary>Завести готовые строки в NurCRM существующим запросом. Прогресс — число обработанных; ошибка строки не останавливает загрузку.
    /// Возвращает (создано, ошибок). Повторный запуск безопасен: клиент с тем же телефоном пропускается при следующей проверке.</summary>
    public static async Task<(int Created, int Failed)> RunAsync(ClientsImportPlan plan, IClientsApiService api, IProgress<int>? progress, CancellationToken ct)
    {
        var created = 0;
        var failed = 0;
        var done = 0;
        foreach (var row in plan.Rows.Where(r => r.Ready))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await api.CreateClientAsync(row.Name, row.Phone, null, row.Address.Length == 0 ? null : row.Address, ct).ConfigureAwait(false);
                row.Done = true;
                created++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                row.Problem = ex.Message;
                PosLogger.Log($"Перенос клиентов: строка {row.FileRow} не записана ({ex.GetType().Name}).", "WARNING");
            }
            progress?.Report(++done);
        }
        return (created, failed);
    }
}
