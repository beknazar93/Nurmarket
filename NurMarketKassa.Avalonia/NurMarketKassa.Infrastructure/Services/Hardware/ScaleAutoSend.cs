using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NurMarketKassa.Services.Hardware;

/// <summary>2026-11-13 (решение владельца по сравнению с Paloma365): автоотправка изменённых товаров на весы. Здесь — правила без окна и сети
/// (проверяются тестами): отпечаток товара, склейка изменений, повтор, кому можно включать, координатор.
///
/// 2026-11-13: что именно на весах зависит от товара — отпечаток для «изменился ли товар с прошлой отправки».
/// Остаток, закупочная цена и т. п. в отпечаток не входят: на весах их нет, отправлять из-за них незачем.</summary>
public static class ScaleAutoSendSignature
{
    public static string Of(string? name, decimal priceSom, string? unit, bool weighed, int? plu, string? barcodeCode) =>
        string.Join("|",
            (name ?? "").Trim(),
            priceSom.ToString("0.####", CultureInfo.InvariantCulture),
            (unit ?? "").Trim().ToLowerInvariant(),
            weighed ? "w" : "p",
            plu is > 0 ? plu.Value.ToString(CultureInfo.InvariantCulture) : "",
            (barcodeCode ?? "").Trim().TrimStart('0'));

    /// <summary>Какие товары надо отправить: новые (на весах не были) и с другим отпечатком. Исчезнувшие из каталога не трогаем.</summary>
    public static List<string> Changed(IEnumerable<(string Id, string Signature)> current, IReadOnlyDictionary<string, string?> sent)
    {
        var result = new List<string>();
        foreach (var (id, sig) in current)
            if (!sent.TryGetValue(id, out var was) || !string.Equals(was, sig, StringComparison.Ordinal))
                result.Add(id);
        return result;
    }
}

/// <summary>Склейка изменений и повтор: «каталог изменился» помечает очередь грязной; отправка — когда с последнего изменения
/// прошло <see cref="Quiet"/> (по умолчанию 90 с), но не позже <see cref="MaxWait"/> с первого (10 мин) — чтобы поток правок цен не откладывал отправку вечно.
/// После неудачи (весы выключены, нет связи) — следующая попытка через 2, 5, 15, 30 минут (потолок), без окон-ошибок.</summary>
public sealed class ScaleAutoSendQueue
{
    public TimeSpan Quiet { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan[] Backoff = { TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30) };

    private DateTime? _first, _last, _retryAt;
    private int _failures;

    public bool Dirty => _first is not null;
    public int Failures => _failures;
    public DateTime? RetryAt => _retryAt;

    public void Note(DateTime now)
    {
        _first ??= now;
        _last = now;
    }

    public bool IsDue(DateTime now)
    {
        if (_first is null || _last is null)
            return false;
        if (_retryAt is { } retry && now < retry)
            return false;
        return now - _last.Value >= Quiet || now - _first.Value >= MaxWait;
    }

    public void Succeeded()
    {
        _first = _last = _retryAt = null;
        _failures = 0;
    }

    /// <summary>Отправка не удалась: очередь остаётся грязной, следующая попытка позже.</summary>
    public void Failed(DateTime now)
    {
        _failures++;
        _retryAt = now + Backoff[Math.Min(_failures - 1, Backoff.Length - 1)];
    }
}

/// <summary>Результат одного запуска автоотправки — для окна «последняя автоотправка» и журнала [SCALES].</summary>
public sealed record ScaleAutoSendResult(bool Ok, int Sent, string? Error, bool Retry);

/// <summary>Кто и что отправляет. Реализация в приложении (скрытое окно «Весы»); в тестах — подставная, без сети.</summary>
public interface IScaleAutoSendSink
{
    Task<ScaleAutoSendResult> SendAsync(string profileId, IReadOnlyList<string> productIds, CancellationToken ct);
}

/// <summary>Профиль весов, как его видит координатор (подмножество LabelScaleProfile).</summary>
public sealed class ScaleAutoSendProfile
{
    public string Id { get; init; } = "";
    public string Brand { get; init; } = "";
    public bool AutoSend { get; init; }
    /// <summary>Способ отправки позволяет тихую отправку (Rongta напрямую, Штрих-ПРИНТ напрямую, РС-200, TM-30F).</summary>
    public bool DirectSend { get; init; }
    /// <summary>Номера PLU уже закреплены (хотя бы раз отправляли) — иначе автоотправка молчит.</summary>
    public bool Prepared { get; init; }
}

public static class ScaleAutoSendPolicy
{
    /// <summary>Можно ли включать выключатель у этих весов (марка и способ дают прямую отправку).</summary>
    public static bool Supported(string brand, bool rongtaDirect, bool shtrikhDirect, bool massaKVerified = false) => brand switch
    {
        "tm" or "shtrikhpc" => true,
        "rongta" => rongtaDirect,
        "shtrikh" => shtrikhDirect,
        "massak" => massaKVerified, // решение 13.11: Масса-К — только после UserPreferences.MassaKVerified == true
        "cas" => false,             // решение 13.11: CAS в первой версии не включаем (сеть не проверена)
        _ => false, // AI — только файл; Rongta через RLS1000 и «через сервер NurCRM» — не тихие
    };

    public static bool ShouldRun(ScaleAutoSendProfile p) => p.AutoSend && p.DirectSend && p.Prepared;
}

/// <summary>Координатор: слушает «каталог изменился», склеивает, раз в тик решает, пора ли, считает изменённые товары и отдаёт их приёмнику.
/// Часы, каталог и хранилище отпечатков — делегатами (без окна и сети).</summary>
public sealed class ScaleAutoSendCoordinator
{
    private readonly Func<DateTime> _now;
    private readonly Func<IReadOnlyList<ScaleAutoSendProfile>> _profiles;
    private readonly Func<string, IReadOnlyList<(string Id, string Signature)>> _catalog; // весовые товары профиля с отпечатками
    private readonly Func<string, IReadOnlyDictionary<string, string?>> _sentSignatures;
    private readonly IScaleAutoSendSink _sink;
    private readonly Func<bool> _blocked; // открыто окно «Весы»/настройки весов или идёт ручная отправка — не мешаем
    private readonly Dictionary<string, ScaleAutoSendQueue> _queues = new();

    public ScaleAutoSendCoordinator(Func<DateTime> now, Func<IReadOnlyList<ScaleAutoSendProfile>> profiles,
        Func<string, IReadOnlyList<(string Id, string Signature)>> catalog, Func<string, IReadOnlyDictionary<string, string?>> sentSignatures,
        IScaleAutoSendSink sink, Func<bool> blocked)
    {
        _now = now; _profiles = profiles; _catalog = catalog; _sentSignatures = sentSignatures; _sink = sink; _blocked = blocked;
    }

    public ScaleAutoSendQueue QueueOf(string profileId) =>
        _queues.TryGetValue(profileId, out var q) ? q : _queues[profileId] = new ScaleAutoSendQueue();

    /// <summary>Подписка на CatalogCacheService.CatalogChanged.</summary>
    public void OnCatalogChanged()
    {
        var now = _now();
        foreach (var p in _profiles().Where(ScaleAutoSendPolicy.ShouldRun))
            QueueOf(p.Id).Note(now);
    }

    /// <summary>Раз в 20 секунд (UI-поток). Возвращает число запущенных отправок (для тестов).</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        if (_blocked())
            return 0;
        var now = _now();
        var started = 0;
        foreach (var p in _profiles().Where(ScaleAutoSendPolicy.ShouldRun))
        {
            var queue = QueueOf(p.Id);
            if (!queue.IsDue(now))
                continue;
            var ids = ScaleAutoSendSignature.Changed(_catalog(p.Id), _sentSignatures(p.Id));
            if (ids.Count == 0)
            {
                queue.Succeeded(); // менялось не то, что есть на весах (остаток, закупочная цена)
                continue;
            }
            started++;
            ScaleAutoSendResult result;
            try { result = await _sink.SendAsync(p.Id, ids, ct).ConfigureAwait(true); }
            catch (Exception ex) when (ex is not OperationCanceledException) { result = new ScaleAutoSendResult(false, 0, ex.Message, true); }
            if (result.Ok || !result.Retry)
                queue.Succeeded();
            else
                queue.Failed(_now());
        }
        return started;
    }
}

/// <summary>Строка «последняя автоотправка» для окна «Весы» (на 5 языках).</summary>
public static class ScaleAutoSendText
{
    public static string Last(DateTime? at, int count, string? error, DateTime? retryAt)
    {
        if (at is null)
            return Tr.T("автоотправка: ещё не было", "автожөнөтүү: азырынча болгон жок", "auto-send: none yet", "otomatik gönderim: henüz yok", "avtoyuborish: hali bo‘lmagan");
        var time = at.Value.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(error))
        {
            var again = retryAt is { } r ? r.ToString("HH:mm", CultureInfo.InvariantCulture) : "—";
            return Tr.T($"последняя автоотправка {time}: не удалась ({error}), повтор в {again}",
                $"акыркы автожөнөтүү {time}: болбоду ({error}), кайра аракет: {again}",
                $"last auto-send {time}: failed ({error}), retry at {again}",
                $"son otomatik gönderim {time}: başarısız ({error}), yeniden deneme: {again}",
                $"oxirgi avtoyuborish {time}: bo‘lmadi ({error}), qayta urinish: {again}");
        }
        return Tr.T($"последняя автоотправка: {time}, товаров: {count}",
            $"акыркы автожөнөтүү: {time}, товарлар: {count}",
            $"last auto-send: {time}, products: {count}",
            $"son otomatik gönderim: {time}, ürün: {count}",
            $"oxirgi avtoyuborish: {time}, tovarlar: {count}");
    }
}
