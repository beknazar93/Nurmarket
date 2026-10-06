using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-06, исследование «Кассы для одежды» (О-01), владелец: «делай всё по этапно». Скан штрихкода размера
/// (этикетка «Платье — 44, Красный») сразу добавляет этот размер в чек, без окна выбора.
///
/// Сервер такой штрихкод не находит (проверено 06.10: штрихкод варианта → products/barcode/ 404, поиск — 0; ТЗ бэкенда,
/// часть 14, п. 14.4), а общего списка вариантов компании у него нет. Поэтому касса ведёт свой справочник
/// «штрихкод → товар и размер»: пополняет его всякий раз, когда загружает размеры товара (окно выбора, карточка,
/// печать этикеток), и в сфере «Одежда» тихо обходит каталог в фоне — по одному товару раз в 1,5 с, каждый товар
/// не чаще раза в сутки. Справочник хранится на диске (работает и без интернета), у каждой компании свой
/// (variant_barcodes.json в AccountDataIsolation).
///
/// 2026-10-06, владелец: «касса не видит товары на складе — при сканере, даже если товар есть на складе, говорит нет».
/// Владелец создал штрихкоды размеров и напечатал этикетки в программе владельца, а касса на этом же компьютере их не
/// знала: у программ были разные файлы справочника, и касса перепроверяла товар раз в сутки. Теперь файл общий для
/// кассы и программы владельца одной компании (%APPDATA%\NurMarketShared\{компания}), сохраняется сразу и
/// перечитывается, как только его изменила другая программа; товары с размерами перепроверяются каждые 2 часа.
/// </summary>
public static class VariantBarcodeIndex
{
    public sealed record Hit(string ProductId, ProductVariantDto Variant);

    private sealed class Stored
    {
        public string P { get; set; } = "";
        public string V { get; set; } = "";
        public string S { get; set; } = "";
        public string C { get; set; } = "";
        public double? Price { get; set; }
        public double Q { get; set; }
        public bool A { get; set; } = true;
    }

    private sealed class FileModel
    {
        public Dictionary<string, Stored> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DateTime> Checked { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>2026-10-06 (О-74): все размеры товара с остатками (и без штрихкода) — для «Заканчиваются размеры».</summary>
        public Dictionary<string, List<Stored>> Variants { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Версия формата: файл без остатков размеров (версия 0–1) перепроверяется целиком один раз.</summary>
        public int Version { get; set; }
    }

    private const int CurrentVersion = 2;

    /// <summary>Размер, который заканчивается: остался 0–1 шт., а другие размеры этого товара ещё есть.</summary>
    public sealed record LowSize(string ProductId, string Size, string Color, double Quantity);

    /// <summary>Как часто фоновый обход перепроверяет товар без размеров.</summary>
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromHours(24);

    /// <summary>Товар, у которого есть штрихкоды размеров, — чаще: размеры и этикетки могли поменять на сайте.</summary>
    private static readonly TimeSpan RecheckVariantsAfter = TimeSpan.FromHours(2);

    private static readonly object Lock = new();
    private static FileModel? _data;
    private static string? _loadedPath;
    private static DateTime _loadedStamp;

    /// <summary>Общий файл кассы и программы владельца одной компании; компания ещё не известна — файл программы.</summary>
    private static string FilePath
    {
        get
        {
            var company = CompanyInfoService.LastCompany?.Id;
            if (!string.IsNullOrWhiteSpace(company) && company.All(ch => char.IsLetterOrDigit(ch) || ch == '-'))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketShared", company, "variant_barcodes.json");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "variant_barcodes.json");
        }
    }

    private static DateTime Stamp(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Справочник в памяти; другая программа изменила файл или сменилась компания — перечитываем. Вызывать под Lock.</summary>
    private static FileModel Data
    {
        get
        {
            var path = FilePath;
            if (_data != null && path == _loadedPath && Stamp(path) <= _loadedStamp)
                return _data;
            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        _data = new FileModel
                        {
                            Items = new Dictionary<string, Stored>(loaded.Items ?? new(), StringComparer.OrdinalIgnoreCase),
                            Checked = new Dictionary<string, DateTime>(loaded.Checked ?? new(), StringComparer.OrdinalIgnoreCase),
                            Variants = new Dictionary<string, List<Stored>>(loaded.Variants ?? new(), StringComparer.OrdinalIgnoreCase),
                            Version = CurrentVersion,
                        };
                        // Файл старой версии (без остатков размеров) — все товары перепроверить.
                        if (loaded.Version < CurrentVersion)
                            _data.Checked.Clear();
                        _loadedPath = path;
                        _loadedStamp = Stamp(path);
                        return _data;
                    }
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Штрихкоды размеров: файл не прочитан ({ex.Message}).", "CATALOG");
                if (_data != null && path == _loadedPath)
                    return _data;
            }
            _data = new FileModel { Version = CurrentVersion };
            _loadedPath = path;
            _loadedStamp = Stamp(path);
            return _data;
        }
    }

    /// <summary>Свежий список размеров товара — заменяет его прежние штрихкоды в справочнике.</summary>
    public static void Update(string productId, IReadOnlyList<ProductVariantDto>? variants)
    {
        if (string.IsNullOrWhiteSpace(productId) || variants is null)
            return;
        var pid = productId.Trim();
        lock (Lock)
        {
            var data = Data;
            foreach (var key in data.Items.Where(kv => string.Equals(kv.Value.P, pid, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
                data.Items.Remove(key);
            foreach (var v in variants)
            {
                var code = v.Barcode?.Trim();
                if (string.IsNullOrEmpty(code) || string.IsNullOrWhiteSpace(v.Id))
                    continue;
                data.Items[code] = new Stored { P = pid, V = v.Id!.Trim(), S = v.Size ?? "", C = v.Color ?? "", Price = v.Price, Q = v.Quantity, A = v.IsActive };
            }
            data.Checked[pid] = DateTime.UtcNow;
            if (variants.Count == 0)
                data.Variants.Remove(pid);
            else
                data.Variants[pid] = variants
                    .Where(v => !string.IsNullOrWhiteSpace(v.Id))
                    .Select(v => new Stored { P = pid, V = v.Id!.Trim(), S = v.Size ?? "", C = v.Color ?? "", Price = v.Price, Q = v.Quantity, A = v.IsActive })
                    .ToList();
            Save(data);
        }
    }

    /// <summary>2026-10-06 (О-80): все известные размеры с остатками — для отчёта «Размеры и цвета».</summary>
    public static List<LowSize> AllVariants()
    {
        lock (Lock)
            return Data.Variants.Values.SelectMany(v => v).Where(v => v.A).Select(v => new LowSize(v.P, v.S, v.C, v.Q)).ToList();
    }

    /// <summary>2026-10-06, исследование «Кассы для одежды» (О-74): размеры, которые заканчиваются, — остаток 0–1, а у
    /// товара есть другие размеры в наличии (значит, модель продаётся, и пропавший размер — упущенные продажи).
    /// Сначала нулевые, потом по одному.</summary>
    public static List<LowSize> LowSizes(int max)
    {
        lock (Lock)
        {
            return Data.Variants
                .Where(kv => kv.Value.Count >= 2 && kv.Value.Where(v => v.A).Sum(v => Math.Max(0, v.Q)) > 1)
                .SelectMany(kv => kv.Value.Where(v => v.A && v.Q <= 1))
                .OrderBy(v => v.Q)
                .ThenBy(v => v.P)
                .Take(Math.Max(0, max))
                .Select(v => new LowSize(v.P, v.S, v.C, v.Q))
                .ToList();
        }
    }

    /// <summary>Id товара, у которого есть размер с таким штрихкодом (для поиска по складу и каталогу); иначе null.</summary>
    public static string? ProductIdFor(string? barcode) => Find(barcode)?.ProductId;

    /// <summary>Размер по отсканированному штрихкоду; null — такого штрихкода размера касса не знает.</summary>
    public static Hit? Find(string? barcode)
    {
        var code = barcode?.Trim();
        if (string.IsNullOrEmpty(code))
            return null;
        lock (Lock)
        {
            if (!Data.Items.TryGetValue(code, out var s))
                return null;
            return new Hit(s.P, new ProductVariantDto { Id = s.V, Size = s.S, Color = s.C, Barcode = code, Price = s.Price, Quantity = s.Q, IsActive = s.A });
        }
    }

    /// <summary>Штрихкод уже занят каким-то размером (для создания новых штрихкодов без повторов).</summary>
    public static bool Contains(string? barcode)
    {
        var code = barcode?.Trim();
        if (string.IsNullOrEmpty(code))
            return false;
        lock (Lock)
            return Data.Items.ContainsKey(code);
    }

    public static int Count
    {
        get
        {
            lock (Lock)
                return Data.Items.Count;
        }
    }

    /// <summary>Смена компании: файл переезжает (AccountDataIsolation), справочник перечитывается.</summary>
    public static void Clear()
    {
        lock (Lock)
        {
            _data = null;
            _loadedPath = null;
        }
    }

    /// <summary>Сразу на диск (под Lock): касса и программа владельца на одном компьютере видят изменения друг друга.</summary>
    private static void Save(FileModel data)
    {
        var path = _loadedPath ?? FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data));
            File.Move(tmp, path, overwrite: true);
            _loadedStamp = Stamp(path);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Штрихкоды размеров: файл не сохранён ({ex.Message}).", "CATALOG");
        }
    }

    /// <summary>Фоновый обход каталога (только пока <paramref name="enabled"/> — сфера «Одежда», касса на связи):
    /// по одному товару раз в 1,5 с, товары, проверенные меньше суток назад, пропускаются. Ответ 429 (лимит запросов
    /// сервера) — пауза 2 минуты.</summary>
    public static async Task RunBackgroundAsync(Func<bool> enabled, Func<IReadOnlyList<string>> productIds, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                if (enabled() && ProductVariantCache.Loader is { } loader && !OfflineModeHelper.SellLocally)
                {
                    var ids = productIds();
                    List<string> stale;
                    lock (Lock)
                    {
                        var data = Data;
                        var withVariants = new HashSet<string>(data.Items.Values.Select(v => v.P), StringComparer.OrdinalIgnoreCase);
                        stale = ids
                            .Where(id => !data.Checked.TryGetValue(id, out var at)
                                         || DateTime.UtcNow - at > (withVariants.Contains(id) ? RecheckVariantsAfter : RecheckAfter))
                            .ToList();
                    }
                    var done = 0;
                    foreach (var id in stale)
                    {
                        if (ct.IsCancellationRequested || !enabled() || OfflineModeHelper.SellLocally)
                            break;
                        // Касса и программа владельца на одном компьютере обходят каталог вместе: товар, который только что
                        // проверила другая программа (файл общий), не запрашиваем второй раз.
                        bool freshNow;
                        lock (Lock)
                            freshNow = Data.Checked.TryGetValue(id, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(30);
                        if (freshNow)
                            continue;
                        try
                        {
                            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            cts.CancelAfter(TimeSpan.FromSeconds(10));
                            var list = await loader(id, cts.Token).ConfigureAwait(false) ?? new List<ProductVariantDto>();
                            ProductVariantCache.Put(id, list);
                            done++;
                        }
                        catch (ApiException ex) when (ex.StatusCode == 429)
                        {
                            PosLogger.Log("Штрихкоды размеров: сервер просит подождать (429) — пауза 2 минуты.", "CATALOG");
                            await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            PosLogger.Log($"Штрихкоды размеров: товар {id} не проверен ({ex.Message}).", "CATALOG");
                        }
                        await Task.Delay(1500, ct).ConfigureAwait(false);
                    }
                    if (done > 0)
                        PosLogger.Log($"Штрихкоды размеров: проверено товаров {done}, в справочнике штрихкодов {Count}.", "CATALOG");
                }
                await Task.Delay(TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Штрихкоды размеров: фоновый обход остановлен ({ex.Message}).", "WARNING");
        }
    }
}
