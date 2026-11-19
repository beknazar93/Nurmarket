using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-13 (решение владельца по сравнению с Paloma365): автоотправка изменённых товаров на весы. Слушает «каталог изменился»
/// (CatalogCacheService.CatalogChanged: лента товаров, полная загрузка, точечное обновление, правка товара на кассе), склеивает правки 90 с,
/// находит товары, у которых цена, название, единица, PLU или код отличаются от записанного на весах (ScaleSentPlu.Sig), и тихо отправляет
/// ТОЛЬКО их тем же кодом, что кнопка «Отправить на весы» (скрытый экземпляр окна «Весы», ScalesPluWindow.AutoSend.cs). Правила — ScaleAutoSend.cs.
///
/// Один компьютер: файл label-scales.json свой на каждом ПК, поэтому галочка «Отправлять изменения на весы автоматически» включает
/// автоотправку только на этом компьютере (кассе или программе владельца). Включать её на двух компьютерах для одних весов не надо.
///
/// Не мешаем: пока открыто окно «Весы» или идёт другая отправка — ждём; чтобы отправить на весы, которые сейчас не выбраны, выбор весов
/// на время отправки переключается и возвращается (если открыты окна настроек — ждём).</summary>
public static class ScaleAutoSendService
{
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(20);

    private static ScaleAutoSendCoordinator? _coordinator;
    private static DispatcherTimer? _timer;
    private static bool _running;
    private static bool _baselined;

    /// <summary>2026-11-13: решение координатора — Масса-К включается только после UserPreferences.MassaKVerified (проверена вживую), CAS — не в первой версии.</summary>
    private static bool MassaKVerified => UserPreferences.Instance.MassaKVerified;

    /// <summary>Запуск при старте программы (App.axaml.cs). Повторный вызов ничего не делает.</summary>
    public static void Start()
    {
        if (_coordinator is not null)
            return;
        _coordinator = new ScaleAutoSendCoordinator(
            now: () => DateTime.Now,
            profiles: Profiles,
            catalog: CatalogSignatures,
            sentSignatures: SentSignatures,
            sink: new Sink(),
            blocked: Blocked);
        CatalogCacheService.CatalogChanged += () => _coordinator?.OnCatalogChanged();
        _timer = new DispatcherTimer { Interval = TickEvery };
        _timer.Tick += async (_, _) => await TickSafeAsync().ConfigureAwait(true);
        _timer.Start();
    }

    private static async Task TickSafeAsync()
    {
        if (_running || _coordinator is null)
            return;
        _running = true;
        try
        {
            if (!_baselined && CatalogCacheService.Products.Count > 0)
            {
                _baselined = true;
                foreach (var profile in LabelScaleStore.All)
                    BaselineSignatures(profile);
            }
            var started = await _coordinator.TickAsync(CancellationToken.None).ConfigureAwait(true);
            if (started > 0)
            {
                // Когда повторим после неудачи — для подписи в окне «Весы».
                foreach (var profile in LabelScaleStore.All.Where(p => p.AutoSendError is not null))
                    profile.AutoSendRetryAt = _coordinator.QueueOf(profile.Id).RetryAt;
                LabelScaleStore.Save();
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Автоотправка на весы: сбой цикла: {ex.Message}", "SCALES");
        }
        finally
        {
            _running = false;
        }
    }

    // ------------------------------------------------------------------ профили, каталог, отпечатки

    /// <summary>Автоотправка у этих весов возможна по марке и способу отправки (Rongta напрямую, Штрих-ПРИНТ напрямую, РС-200, TM-30F).</summary>
    public static bool Supported(LabelScaleProfile profile) =>
        ScaleAutoSendPolicy.Supported(profile.Brand, string.Equals(profile.RongtaSource, "lan", StringComparison.OrdinalIgnoreCase), profile.ShtrikhDirect, MassaKVerified);

    /// <summary>Номера PLU закреплены (была хотя бы одна ручная отправка) — иначе автоотправка молчит: у РС-200 номеров нет, там смотрим на «что на весах».</summary>
    private static bool Prepared(LabelScaleProfile profile) => profile.PluNumbers.Count > 0 || profile.SentPlus.Count > 0;

    private static IReadOnlyList<ScaleAutoSendProfile> Profiles() =>
        LabelScaleStore.All.Select(p => new ScaleAutoSendProfile
        {
            Id = p.Id,
            Brand = p.Brand,
            AutoSend = p.AutoSend,
            DirectSend = Supported(p),
            Prepared = Prepared(p),
        }).ToList();

    /// <summary>Весовые товары этих весов: категории весов, а если их нет — отмеченные вручную, а если и их нет — все весовые
    /// (как ScalesPluWindow.ApplyProfileSelection).</summary>
    internal static List<CatalogProductTileVm> CandidatesOf(LabelScaleProfile profile)
    {
        var products = CatalogCacheService.Products;
        var anyMustWeigh = products.Any(p => p.MustWeigh);
        var weighed = products.Where(p => anyMustWeigh ? p.MustWeigh : p.IsWeighted);
        if (profile.Categories.Count > 0)
        {
            var set = new HashSet<string>(profile.Categories, StringComparer.CurrentCultureIgnoreCase);
            return weighed.Where(p => set.Contains((p.Category ?? "").Trim())).ToList();
        }
        if (profile.ProductIds.Count > 0)
        {
            var ids = new HashSet<string>(profile.ProductIds, StringComparer.Ordinal);
            return weighed.Where(p => ids.Contains(p.Id)).ToList();
        }
        return weighed.ToList();
    }

    /// <summary>Отпечаток товара для этих весов: название, цена, единица, весовой, PLU и «Код в ШК» (закреплённый или по умолчанию).</summary>
    public static string SignatureOf(CatalogProductTileVm p, LabelScaleProfile profile)
    {
        var code = profile.BarcodeCodes.TryGetValue(p.Id, out var saved) && !string.IsNullOrWhiteSpace(saved) ? saved : ScalesPluWindow.DefaultBarcodeCode(p);
        return ScaleAutoSendSignature.Of(p.Title, (decimal)LocalCartService.ParsePrice(p.PriceLine), p.Unit, p.IsWeighted, p.Plu, code);
    }

    private static IReadOnlyList<(string Id, string Signature)> CatalogSignatures(string profileId)
    {
        var profile = LabelScaleStore.Find(profileId);
        return profile is null
            ? Array.Empty<(string, string)>()
            : CandidatesOf(profile).Select(p => (p.Id, SignatureOf(p, profile))).ToList();
    }

    private static IReadOnlyDictionary<string, string?> SentSignatures(string profileId)
    {
        var profile = LabelScaleStore.Find(profileId);
        return profile is null
            ? new Dictionary<string, string?>()
            : profile.SentPlus.ToDictionary(kv => kv.Key, kv => kv.Value.Sig, StringComparer.Ordinal);
    }

    /// <summary>Товары, отправленные до автоотправки (отпечатка нет), считаются «уже на весах такими, как сейчас»: иначе первая же
    /// автоотправка переслала бы весь список. Вызывается при старте (каталог загружен) и при включении галочки.</summary>
    public static int BaselineSignatures(LabelScaleProfile profile)
    {
        var stamped = 0;
        foreach (var (id, sent) in profile.SentPlus.Where(kv => kv.Value.Sig is null).ToList())
        {
            if (CatalogCacheService.FindProductById(id) is not { } tile)
                continue;
            sent.Sig = SignatureOf(tile, profile);
            stamped++;
        }
        if (stamped > 0)
            LabelScaleStore.Save();
        return stamped;
    }

    // ------------------------------------------------------------------ когда нельзя мешать

    /// <summary>Открыто окно «Весы» (идёт ручная работа) или окна настроек весов — выбор весов не трогаем, ждём.</summary>
    private static bool Blocked()
    {
        if (CatalogCacheService.Products.Count == 0)
            return true; // каталог ещё не загружен — сравнивать не с чем
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return false;
        foreach (var window in desktop.Windows)
        {
            if (!window.IsVisible)
                continue;
            if (window is ScalesPluWindow)
                return true;
            var name = window.GetType().Name;
            if (name.Contains("Scale", StringComparison.Ordinal) || name.Contains("Settings", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ отправка

    private sealed class Sink : IScaleAutoSendSink
    {
        public async Task<ScaleAutoSendResult> SendAsync(string profileId, IReadOnlyList<string> productIds, CancellationToken ct)
        {
            var profile = LabelScaleStore.Find(profileId);
            if (profile is null)
                return new ScaleAutoSendResult(true, 0, null, false);
            PosLogger.Log($"Автоотправка на весы «{profile.Name}»: изменено товаров {productIds.Count}, отправляю.", "SCALES");
            var result = await ScalesPluWindow.RunAutoSendAsync(profile, productIds).ConfigureAwait(true);
            // Профиль мог быть заменён копией (Restore) — берём актуальный.
            profile = LabelScaleStore.Find(profileId) ?? profile;
            profile.AutoSendAt = DateTime.Now;
            profile.AutoSendCount = result.Sent;
            profile.AutoSendError = result.Ok ? null : (result.Error ?? "—");
            profile.AutoSendRetryAt = null; // время повтора проставит TickSafeAsync по очереди
            LabelScaleStore.Save();
            PosLogger.Log(result.Ok
                ? $"Автоотправка на весы «{profile.Name}»: отправлено {result.Sent}."
                : $"Автоотправка на весы «{profile.Name}»: не удалась ({result.Error}), отправлено {result.Sent}" + (result.Retry ? ", повтор позже." : "."), "SCALES");
            return result;
        }
    }
}
