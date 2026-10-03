using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>Клиент NurCRM, найденный по номеру телефона.</summary>
public sealed record ClientPhoneMatch(string Id, string FullName, string Phone);

/// <summary>2026-10-04: поиск клиента NurCRM по телефону — для QR клиента «NURCRM…» из приложения
/// NurCRM (см. <see cref="ClientQrCode"/>). Один экземпляр на окно кассы.
///
/// Поиск сервера (?search=) ищет подстроку в телефоне (проверено на тестовом аккаунте NBS 04.10):
/// по 9 цифрам находятся записи «+996771830438», «996771830438», «0771830438» и «771830438» — это
/// первый, быстрый запрос. Номер, записанный с пробелами или дефисами («0771 83-04-38» — так его мог
/// ввести кассир в окне оплаты), подстрокой не найти; только тогда касса берёт весь список клиентов
/// (так же, как окно оплаты при каждом открытии) и сравнивает 9 цифр номера. Список запоминается на
/// <see cref="FullListTtl"/>, а найденные по QR клиенты — до закрытия окна кассы: без связи с
/// сервером постоянного покупателя можно привязать только из этой памяти (своей таблицы клиентов у
/// кассы нет).</summary>
public sealed class ClientPhoneLookup
{
    private static readonly TimeSpan FullListTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FullListTimeout = TimeSpan.FromSeconds(5);

    private readonly IClientsApiService _api;
    private readonly object _sync = new();
    private readonly Dictionary<string, ClientPhoneMatch> _known = new(StringComparer.Ordinal);
    private List<(string National, ClientPhoneMatch Match)>? _fullList;
    private DateTime _fullListAtUtc;

    public ClientPhoneLookup(IClientsApiService api) => _api = api;

    public async Task<ClientPhoneMatch?> FindAsync(string national, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(national) || national.Length != 9)
            return null;

        var rows = await _api.GetClientsAsync(national, ct).ConfigureAwait(false);
        var match = PickMatch(rows.Select(row => (ClientQrCode.NationalDigits(Str(row, "phone")), row)), national);
        if (match is null)
        {
            // Весь список — не дольше FullListTimeout: у большого магазина он может грузиться долго, а
            // кассир ждёт у кассы. Не успели или сервер отказал — считаем, что клиента нет (кассиру
            // предложат завести его, и он сам решит).
            try
            {
                using var listCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                listCts.CancelAfter(FullListTimeout);
                var full = await GetFullListAsync(listCts.Token).ConfigureAwait(false);
                match = full.Where(item => item.National == national).Select(item => item.Match).FirstOrDefault();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"QR клиента: весь список клиентов не загрузился ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            }
        }

        if (match != null)
            Remember(national, match);
        return match;
    }

    /// <summary>Клиент, заведённый кассиром в окне «Новый клиент» после скана QR, — чтобы следующий скан
    /// того же QR без связи нашёл его в памяти.</summary>
    public void Remember(string id, string fullName, string phone)
    {
        if (!string.IsNullOrWhiteSpace(id) && ClientQrCode.NationalDigits(phone) is { } national)
            Remember(national, new ClientPhoneMatch(id, fullName, phone));
    }

    /// <summary>Без связи: клиент из памяти этого окна кассы (найденные по QR и последний
    /// загруженный список клиентов) или null.</summary>
    public ClientPhoneMatch? FindRemembered(string national)
    {
        lock (_sync)
        {
            if (_known.TryGetValue(national, out var known))
                return known;
            return _fullList?.Where(item => item.National == national).Select(item => item.Match).FirstOrDefault();
        }
    }

    private void Remember(string national, ClientPhoneMatch match)
    {
        lock (_sync)
            _known[national] = match;
    }

    private async Task<List<(string National, ClientPhoneMatch Match)>> GetFullListAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_fullList != null && DateTime.UtcNow - _fullListAtUtc < FullListTtl)
                return _fullList;
        }

        var rows = await _api.GetClientsAsync(null, ct).ConfigureAwait(false);
        var list = rows
            .Select(row => (National: ClientQrCode.NationalDigits(Str(row, "phone")), Row: row))
            .Where(item => item.National != null && IsClient(item.Row))
            .OrderBy(item => Str(item.Row, "created_at") ?? "", StringComparer.Ordinal)
            .Select(item => (item.National!, ToMatch(item.Row)))
            .Where(item => item.Item2.Id.Length > 0)
            .ToList();

        lock (_sync)
        {
            _fullList = list;
            _fullListAtUtc = DateTime.UtcNow;
        }

        return list;
    }

    private static ClientPhoneMatch? PickMatch(IEnumerable<(string? National, JsonElement Row)> rows, string national) =>
        rows
            .Where(item => item.National == national && IsClient(item.Row))
            // Если клиент записан дважды — самая ранняя запись.
            .OrderBy(item => Str(item.Row, "created_at") ?? "", StringComparer.Ordinal)
            .Select(item => ToMatch(item.Row))
            .FirstOrDefault(m => m.Id.Length > 0);

    /// <summary>В той же таблице NurCRM — поставщики и подрядчики; как и окно оплаты, берём только клиентов.</summary>
    private static bool IsClient(JsonElement row) => Str(row, "type") is null or "client";

    private static ClientPhoneMatch ToMatch(JsonElement row) =>
        new(Str(row, "id") ?? "", Str(row, "full_name") ?? "", Str(row, "phone") ?? "");

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
