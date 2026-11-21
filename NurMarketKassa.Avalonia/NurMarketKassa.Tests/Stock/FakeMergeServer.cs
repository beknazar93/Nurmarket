using NurMarketKassa.Services;
using NurMarketKassa.Services.ProductMerge;

namespace NurMarketKassa.Tests.Stock;

/// <summary>Заглушка сервера в памяти. Штрихкод уникален в компании среди ВСЕХ карточек (и основной, и доп., и у архивных): повтор — 409 barcode_taken, как у NurCRM.</summary>
public sealed class FakeMergeServer : IProductMergeServer
{
    public Dictionary<string, MergeCard> Cards { get; } = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = [];
    public List<(string Note, List<(string Id, double Total)> Targets)> StockDocs { get; } = [];
    public bool CanArchive { get; set; } = true;
    public bool CostingByBatches { get; set; }
    public string? FailPatchOfMainWith { get; set; }
    public string? FailStockFor { get; set; }
    public string? UnknownStockFor { get; set; }
    public bool FailArchive { get; set; }

    public void Add(MergeCard c) => Cards[c.Id] = c;

    public Task<MergeCard?> ReadCardAsync(string id, CancellationToken ct)
    {
        Calls.Add("read:" + id);
        return Task.FromResult(Cards.TryGetValue(id, out var c) && !c.Archived ? c : null);
    }

    public Task PatchAsync(string id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct)
    {
        Calls.Add("patch:" + id);
        if (id == "m" && FailPatchOfMainWith is not null && fields.ContainsKey("alternate_barcodes"))
            throw new ApiException(FailPatchOfMainWith, 409);
        var c = Cards[id];
        var barcode = fields.TryGetValue("barcode", out var b) ? (string?)b : c.Barcode;
        var alt = fields.TryGetValue("alternate_barcodes", out var a)
            ? ((object[])a!).Cast<Dictionary<string, object?>>().Select(d => new MergeAltCode((string)d["barcode"]!, (string?)d["name"], Convert.ToDouble(d["quantity"]))).ToList()
            : c.Alt;
        // уникальность по всей компании
        var mine = new List<string>();
        if (!string.IsNullOrWhiteSpace(barcode))
            mine.Add(barcode);
        mine.AddRange(alt.Select(x => x.Barcode));
        foreach (var other in Cards.Values.Where(o => o.Id != id))
        {
            var theirs = new HashSet<string>(other.Alt.Select(x => x.Barcode).Append(other.Barcode ?? ""));
            var clash = mine.FirstOrDefault(code => code.Length > 0 && theirs.Contains(code));
            if (clash is not null)
                throw new ApiException($"barcode_taken: {clash} уже есть у товара «{other.Name}»", 409);
        }
        if (mine.Count != mine.Distinct().Count())
            throw new ApiException("barcode_taken: повтор внутри карточки", 400);
        c = c with { Barcode = barcode, Alt = alt };
        if (fields.TryGetValue("name", out var n))
            c = c with { Name = (string)n! };
        if (fields.TryGetValue("purchase_price", out var pp))
            c = c with { PurchasePrice = double.Parse((string)pp!, System.Globalization.CultureInfo.InvariantCulture) };
        Cards[id] = c;
        return Task.CompletedTask;
    }

    public Task SetStocksAsync(string note, IReadOnlyList<(string Id, double Total)> targets, CancellationToken ct)
    {
        Calls.Add("stock:" + string.Join(",", targets.Select(t => t.Id)));
        if (UnknownStockFor is not null && targets.Any(t => t.Id == UnknownStockFor))
            throw new MergeOutcomeUnknownException("связь оборвалась");
        if (FailStockFor is not null && targets.Any(t => t.Id == FailStockFor))
            throw new ApiException("сервер недоступен", 503);
        StockDocs.Add((note, targets.ToList()));
        foreach (var (id, total) in targets)
            Cards[id] = Cards[id] with { Stock = total };
        return Task.CompletedTask;
    }

    public Task ArchiveAsync(string id, CancellationToken ct)
    {
        Calls.Add("archive:" + id);
        if (FailArchive)
            throw new ApiException("архив недоступен", 500);
        Cards[id] = Cards[id] with { Archived = true };
        return Task.CompletedTask;
    }

    public Task UnarchiveAsync(string id, CancellationToken ct)
    {
        Calls.Add("unarchive:" + id);
        Cards[id] = Cards[id] with { Archived = false };
        return Task.CompletedTask;
    }

    // ───────────── серверное слияние (2026-11-21, ответ Умара 21.11) ─────────────

    public enum MergeMode
    {
        /// <summary>Старый сервер: POST main/products/merge/ → 404 (по умолчанию — прежние проверки идут ручным путём).</summary>
        Missing404,
        /// <summary>Серверное слияние есть.</summary>
        Supported,
        /// <summary>Есть, но товары не объединяет: 409 merge_not_supported + <see cref="NotSupportedReasons"/>.</summary>
        NotSupported,
    }

    public MergeMode ServerMode { get; set; } = MergeMode.Missing404;
    public List<string> NotSupportedReasons { get; } = ["has_variants"];

    /// <summary>Сколько первых запросов слияния выполнить, но «потерять» ответ (503): повтор с тем же ключом не должен слить второй раз.</summary>
    public int LoseResponseTimes { get; set; }

    /// <summary>Не пустое — откат блокируется (409 undo_blocked) с этой причиной.</summary>
    public string? UndoBlockedReason { get; set; }

    /// <summary>Сервер отвечает на слияние ошибкой по существу (400) — ничего не меняется.</summary>
    public bool RejectMerge { get; set; }

    public List<(bool Dry, string? Key, string Target, string[] Sources, Dictionary<string, string> Names)> MergeCalls { get; } = [];
    public List<string> UndoCalls { get; } = [];
    public int AppliedMerges { get; private set; }
    private readonly Dictionary<string, ServerMergeResponse> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, MergeCard>> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _moved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _targets = new(StringComparer.Ordinal);
    private int _lost;
    public bool? ServerMergeSupported { get; private set; }

    public Task<ServerMergeResponse> ServerMergeAsync(string targetId, IReadOnlyList<string> sourceIds, IReadOnlyDictionary<string, string> variantNames, bool dryRun, string? idempotencyKey, CancellationToken ct)
    {
        MergeCalls.Add((dryRun, idempotencyKey, targetId, sourceIds.ToArray(), new Dictionary<string, string>(variantNames)));
        if (ServerMode == MergeMode.Missing404)
        {
            ServerMergeSupported = false;
            return Task.FromResult(ServerMergeResponse.Unavailable);
        }
        ServerMergeSupported = true;
        if (ServerMode == MergeMode.NotSupported)
            return Task.FromResult(new ServerMergeResponse(ServerMergeKind.NotSupported) { Reasons = NotSupportedReasons.ToList() });
        if (dryRun)
            return Task.FromResult(new ServerMergeResponse(ServerMergeKind.Ok));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ApiException("idempotency_key_required", 400);
        if (RejectMerge)
            throw new ApiException("validation_error: target archived", 400);
        if (_byKey.TryGetValue(idempotencyKey, out var prior))
        {
            if (_lost < LoseResponseTimes)
            {
                _lost++;
                throw new ApiException("сервер недоступен (ответ потерян)", 503);
            }
            return Task.FromResult(prior with { Replayed = true });
        }

        var snapshot = Cards.Where(c => c.Key == targetId || sourceIds.Contains(c.Key)).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
        var target = Cards[targetId];
        var alt = target.Alt.ToList();
        var movedCodes = 0;
        var movedQty = 0.0;
        foreach (var id in sourceIds)
        {
            var s = Cards[id];
            var vname = variantNames.TryGetValue(id, out var vn) && vn.Length > 0 ? vn : s.Name;
            if (!string.IsNullOrWhiteSpace(s.Barcode))
            {
                alt.Add(new MergeAltCode(s.Barcode, vname, 0));
                movedCodes++;
            }
            foreach (var a in s.Alt)
            {
                alt.Add(new MergeAltCode(a.Barcode, string.IsNullOrWhiteSpace(a.Name) ? vname : a.Name, a.Quantity));
                movedCodes++;
            }
            movedQty += s.Stock;
            Cards[id] = s with { Barcode = "", Alt = [], Stock = 0, Archived = true };
        }
        Cards[targetId] = target with { Alt = alt, Stock = target.Stock + movedQty };
        AppliedMerges++;
        var mergeId = "srv-" + AppliedMerges;
        _snapshots[mergeId] = snapshot;
        _moved[mergeId] = movedQty;
        _targets[mergeId] = targetId;
        var response = new ServerMergeResponse(ServerMergeKind.Ok) { MergeId = mergeId, MovedBarcodes = movedCodes, MovedQty = movedQty, StockDocument = "doc-" + AppliedMerges, ArchivedIds = sourceIds.ToList() };
        _byKey[idempotencyKey] = response;
        if (_lost < LoseResponseTimes)
        {
            _lost++;
            throw new ApiException("сервер недоступен (ответ потерян)", 503);
        }
        return Task.FromResult(response);
    }

    public Task<ServerMergeResponse> ServerUndoAsync(string mergeId, CancellationToken ct)
    {
        UndoCalls.Add(mergeId);
        if (ServerMode == MergeMode.Missing404)
            return Task.FromResult(ServerMergeResponse.Unavailable);
        if (!_snapshots.TryGetValue(mergeId, out var snapshot))
            return Task.FromResult(new ServerMergeResponse(ServerMergeKind.NotFound));
        if (UndoBlockedReason is not null)
            return Task.FromResult(new ServerMergeResponse(ServerMergeKind.UndoBlocked) { Reasons = [UndoBlockedReason] });
        foreach (var (id, card) in snapshot)
        {
            // главному возвращаются его коды, но остаток — «текущий минус перенесённый»; источникам — как были
            // главному возвращаются его коды и остаток «текущий минус перенесённый»; название и закупку сервер при откате не трогает (их правила кассой после слияния)
            Cards[id] = id == _targets[mergeId] ? card with { Name = Cards[id].Name, PurchasePrice = Cards[id].PurchasePrice, Stock = Math.Max(0, Cards[id].Stock - _moved[mergeId]) } : card;
        }
        _snapshots.Remove(mergeId);
        return Task.FromResult(new ServerMergeResponse(ServerMergeKind.Ok) { MergeId = mergeId });
    }
}
