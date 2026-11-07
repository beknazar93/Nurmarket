using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-02 (аудит №2, С3-12): окно «Архив товаров» в «Складе». Архивные товары не видны в каталоге и в продажах; здесь их можно найти и вернуть
/// (POST products/{id}/unarchive/). Список — GET products/list/?include_archived=1, фильтр по признаку is_archived.</summary>
internal sealed class ArchivedProductsDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly ICatalogApiService _api;
    private readonly bool _canRestore;
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly TextBox _search = new() { Height = 40, FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _stop;
    private readonly Button _refresh;
    private readonly List<JsonElement> _found = new();
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _loading;

    /// <summary>2026-11-03 (аудит №3, Т3-04): кэш списка архива на 10 минут. Сервер игнорирует фильтр is_archived, и архив читается по всем страницам каталога (70–83 с);
    /// повторное открытие окна берёт список из файла. Файл принадлежит компании (AccountDataIsolation.CompanyItems) и хранит её ключ.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private static string CachePath => Path.Combine(
        NurMarketKassa.Services.AppPaths.Roaming, AppMode.DataFolderName, "archive-products-cache.json");

    /// <summary>true — хотя бы один товар вернули из архива (вызывающий обновляет каталог).</summary>
    public bool Restored { get; private set; }

    /// <summary>2026-11-07: код возвращённых из архива товаров — склад обновляет в каталоге только их (CatalogCacheService.RefreshProductsAsync), а не весь каталог.</summary>
    public List<string> RestoredIds { get; } = [];

    public ArchivedProductsDialog(ICatalogApiService api, bool canRestore = true)
    {
        _api = api;
        _canRestore = canRestore;
        Title = T("Архив товаров", "Товарлардын архиви", "Product archive", "Ürün arşivi", "Mahsulotlar arxivi");
        SectionCards.AsModal(this, 560);
        _status.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        var scroll = new ScrollViewer { MaxHeight = 420, Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = T("Архивные товары не видны в каталоге и не продаются, история приходов и продаж сохранена. «Вернуть» снова делает товар обычным.",
                "Архивдеги товарлар каталогто көрүнбөйт жана сатылбайт, кирешелер менен сатуулардын тарыхы сакталган. «Кайтаруу» товарды кайра кадимки кылат.",
                "Archived products are hidden from the catalog and not sold; the receipt and sales history is kept. “Restore” makes the product regular again.",
                "Arşivlenen ürünler katalogda görünmez ve satılmaz; giriş ve satış geçmişi saklanır. «Geri al» ürünü yeniden normal yapar.",
                "Arxivdagi mahsulotlar katalogda ko'rinmaydi va sotilmaydi; kirim va sotuvlar tarixi saqlanadi. «Qaytarish» mahsulotni yana oddiy qiladi."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        });
        _search.Watermark = T("Поиск по названию или штрихкоду…", "Аталышы же штрихкод боюнча издөө…", "Search by name or barcode…", "Ad veya barkoda göre ara…", "Nomi yoki shtrix-kod bo'yicha qidiruv…");
        _search.TextChanged += (_, _) => Render(final: !_loading);
        body.Children.Add(_search);
        body.Children.Add(_status);
        body.Children.Add(scroll);
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close(Restored);
        _stop = SectionCards.Secondary(T("Отменить загрузку", "Жүктөөнү токтотуу", "Cancel loading", "Yüklemeyi iptal et", "Yuklashni bekor qilish"));
        _stop.IsVisible = false;
        _stop.Click += (_, _) => _cts?.Cancel();
        _refresh = SectionCards.Secondary(T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"));
        _refresh.Click += async (_, _) => { if (!_loading) await LoadAsync(force: true); };
        var shell = SectionCards.Shell(Title ?? "", null, body, SectionCards.Buttons(_stop, _refresh, close));
        shell.InitialFocus = close;
        Content = shell;
        // 2026-11-03 (аудит №3, Т3-04): закрытие окна отменяет чтение (раньше оно шло дальше и начиналось заново при следующем открытии).
        Closed += (_, _) => { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } };
        Opened += async (_, _) => await LoadAsync(force: false);
    }

    private sealed record ArchiveCache(string AccountKey, DateTime SavedUtc, List<JsonElement> Items);

    private static string AccountKey => UserPreferences.Instance.LastDataAccountKey ?? "";

    private static List<JsonElement>? TryReadCache(out DateTime savedUtc)
    {
        savedUtc = default;
        try
        {
            if (!File.Exists(CachePath) || AccountKey.Length == 0)
                return null;
            var cache = JsonSerializer.Deserialize<ArchiveCache>(File.ReadAllText(CachePath));
            if (cache is null || !string.Equals(cache.AccountKey, AccountKey, StringComparison.OrdinalIgnoreCase))
                return null;
            var age = DateTime.UtcNow - cache.SavedUtc;
            if (age < TimeSpan.Zero || age > CacheLifetime)
                return null;
            savedUtc = cache.SavedUtc;
            return cache.Items;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Склад: кэш архива товаров не прочитан ({ex.Message}).", "WARNING");
            return null;
        }
    }

    private static void WriteCache(List<JsonElement> items)
    {
        try
        {
            if (AccountKey.Length == 0)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            AtomicFile.WriteAllText(CachePath, JsonSerializer.Serialize(new ArchiveCache(AccountKey, DateTime.UtcNow, items)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Склад: кэш архива товаров не записан ({ex.Message}).", "WARNING");
        }
    }

    private static void DropCache()
    {
        try { if (File.Exists(CachePath)) File.Delete(CachePath); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Склад: кэш архива товаров не удалён ({ex.Message}).", "WARNING"); }
    }

    private async Task LoadAsync(bool force)
    {
        if (_loading)
            return;
        _found.Clear();
        _list.Children.Clear();

        if (!force && TryReadCache(out var savedUtc) is { } cached)
        {
            _found.AddRange(cached);
            Render(final: true);
            var minutes = Math.Max(0, (int)(DateTime.UtcNow - savedUtc).TotalMinutes);
            _status.Text = StatusFound() + " " + T($"Список из памяти (обновлён {minutes} мин назад). «Обновить» — прочитать заново.", $"Тизме эстеликтен ({minutes} мин мурун жаңыртылган). «Жаңыртуу» — кайра окуу.",
                $"The list is from memory (updated {minutes} min ago). “Refresh” reads it again.", $"Liste bellekten ({minutes} dk önce güncellendi). «Yenile» yeniden okur.", $"Ro'yxat xotiradan ({minutes} daq. oldin yangilangan). «Yangilash» qayta o'qiydi.");
            return;
        }
        if (force)
            DropCache();

        _loading = true;
        _stop.IsVisible = true;
        _refresh.IsEnabled = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _status.Text = T("Загружаю архив…", "Архив жүктөлүүдө…", "Loading the archive…", "Arşiv yükleniyor…", "Arxiv yuklanmoqda…");
        var scanned = 0;
        int? totalSeen = null;
        try
        {
            // 2026-11-02 (приёмка круга 3): архив читается постранично до конца; «пусто» — только если ответ получен и архивных товаров реально 0.
            // 2026-11-03 (аудит №3, Т3-04): «Просмотрено N из M», найденные товары появляются сразу, чтение отменяется кнопкой и закрытием окна.
            var progress = new Progress<(int Loaded, int? Total)>(p =>
            {
                scanned = p.Loaded;
                totalSeen = p.Total;
                if (_loading)
                    _status.Text = StatusProgress(scanned, totalSeen);
            });
            await _api.ListArchivedProductsAsync(progress, token, item => Dispatcher.UIThread.Post(() =>
            {
                if (!_loading)
                    return;
                _found.Add(item);
                if (Matches(item))
                    _list.Children.Add(Row(item));
            })).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _loading = false;
            Render(final: true);
            _status.Text = T($"Загрузка остановлена: просмотрено {scanned}{(totalSeen is { } t ? $" из {t}" : "")} товаров, найдено в архиве: {_found.Count}. Список может быть неполным — «Обновить» начнёт сначала.",
                $"Жүктөө токтотулду: {scanned}{(totalSeen is { } t2 ? $" / {t2}" : "")} товар каралды, архивден табылды: {_found.Count}. Тизме толук эмес болушу мүмкүн — «Жаңыртуу» башынан баштайт.",
                $"Loading stopped: {scanned}{(totalSeen is { } t3 ? $" of {t3}" : "")} products checked, found in the archive: {_found.Count}. The list may be incomplete — “Refresh” starts over.",
                $"Yükleme durduruldu: {scanned}{(totalSeen is { } t4 ? $" / {t4}" : "")} ürün tarandı, arşivde bulunan: {_found.Count}. Liste eksik olabilir — «Yenile» baştan başlar.",
                $"Yuklash to'xtatildi: {scanned}{(totalSeen is { } t5 ? $" / {t5}" : "")} mahsulot ko'rildi, arxivda topildi: {_found.Count}. Ro'yxat to'liq bo'lmasligi mumkin — «Yangilash» boshidan boshlaydi.");
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _loading = false;
            PosLogger.Log($"Склад: архив товаров не загружен ({ex.Message}).", "WARNING");
            Render(final: true);
            _status.Text = T("Не удалось загрузить архив: нет связи с сервером или сервер не отдаёт архивные товары.",
                "Архивди жүктөө мүмкүн болгон жок: сервер менен байланыш жок же сервер архивдеги товарларды бербейт.",
                "Could not load the archive: no connection to the server, or the server does not return archived products.",
                "Arşiv yüklenemedi: sunucuyla bağlantı yok ya da sunucu arşivlenen ürünleri vermiyor.",
                "Arxivni yuklab bo'lmadi: server bilan aloqa yo'q yoki server arxivdagi mahsulotlarni bermaydi.");
            return;
        }
        finally
        {
            _loading = false;
            _stop.IsVisible = false;
            _refresh.IsEnabled = true;
        }

        WriteCache(_found.ToList());
        Render(final: true);
    }

    private string StatusProgress(int scanned, int? total) => (total is { } t
        ? T($"Просмотрено {scanned} из {t}", $"Каралды {scanned} / {t}", $"Checked {scanned} of {t}", $"Taranan {scanned} / {t}", $"Ko'rildi {scanned} / {t}")
        : T($"Просмотрено {scanned}", $"Каралды {scanned}", $"Checked {scanned}", $"Taranan {scanned}", $"Ko'rildi {scanned}"))
        + T($", найдено в архиве: {_found.Count}…", $", архивден табылды: {_found.Count}…", $", found in the archive: {_found.Count}…", $", arşivde bulunan: {_found.Count}…", $", arxivda topildi: {_found.Count}…");

    private string StatusFound() => _found.Count == 0
        ? T("В архиве пусто.", "Архив бош.", "The archive is empty.", "Arşiv boş.", "Arxiv bo'sh.")
        : T($"Товаров в архиве: {_found.Count}.", $"Архивдеги товарлар: {_found.Count}.", $"Products in the archive: {_found.Count}.", $"Arşivdeki ürünler: {_found.Count}.", $"Arxivdagi mahsulotlar: {_found.Count}.");

    private bool Matches(JsonElement item)
    {
        var q = (_search.Text ?? "").Trim();
        return q.Length == 0
               || Text(item, "name").Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || Text(item, "barcode").Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>Перерисовывает список по поиску. <paramref name="final"/> — чтение закончено: сортировка по названию и итог в строке состояния.</summary>
    private void Render(bool final)
    {
        _list.Children.Clear();
        var shown = (final ? _found.OrderBy(i => Text(i, "name"), StringComparer.CurrentCultureIgnoreCase) : _found.AsEnumerable()).Where(Matches).ToList();
        foreach (var item in shown)
            _list.Children.Add(Row(item));
        if (final)
        {
            _status.Text = StatusFound() + ((_search.Text ?? "").Trim().Length > 0
                ? " " + T($"Показано по поиску: {shown.Count}.", $"Издөө боюнча көрсөтүлдү: {shown.Count}.", $"Shown by search: {shown.Count}.", $"Aramaya göre gösterilen: {shown.Count}.", $"Qidiruv bo'yicha ko'rsatildi: {shown.Count}.")
                : "");
        }
    }

    private static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : ""
            : "";

    private Control Row(JsonElement item)
    {
        var id = Text(item, "id");
        var name = Text(item, "name");
        var barcode = Text(item, "barcode");
        var stockRaw = Text(item, "quantity");
        // 2026-11-03 (аудит №3, Т3-10): остаток без хвоста «.000»; Т3-05: архивный товар с остатком касса не видит и не учитывает в «Склад по закупке» — строка выделена.
        var hasNumber = double.TryParse(stockRaw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var stockNum);
        var hasStock = hasNumber && stockNum > 0.0005;
        var stock = hasNumber ? stockNum.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) : stockRaw;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2) };
        var info = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(name) ? id : name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var detail = string.Join(" · ", new[]
        {
            barcode.Length > 0 ? barcode : null,
            stock.Length > 0 ? T($"остаток {stock}", $"калдык {stock}", $"stock {stock}", $"stok {stock}", $"qoldiq {stock}") : null,
        }.Where(s => s is not null));
        if (detail.Length > 0)
        {
            var d = new TextBlock { Text = detail, FontSize = 12 };
            d.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            info.Children.Add(d);
        }
        if (hasStock)
        {
            var warn = new TextBlock
            {
                Text = T("В архиве остался товар: касса его не видит и в «Склад по закупке» он не входит. Верните из архива и спишите остаток.",
                    "Архивде товар калды: касса аны көрбөйт жана «Сатып алуу боюнча склад» ичине кирбейт. Архивден кайтарып, калдыкты эсептен чыгарыңыз.",
                    "Stock is left in the archive: the till does not see it and it is not counted in “Warehouse at cost”. Restore the product and write the stock off.",
                    "Arşivde stok kaldı: kasa onu görmez ve «Alış değerli depo»ya girmez. Ürünü arşivden alıp stoğu düşün.",
                    "Arxivda qoldiq qoldi: kassa uni ko'rmaydi va «Xarid bo'yicha ombor»ga kirmaydi. Mahsulotni arxivdan qaytarib, qoldiqni hisobdan chiqaring."),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };
            warn.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushDanger"));
            info.Children.Add(warn);
        }
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);
        // 2026-11-03 (аудит №3, Т3-03): без права «Склад: редактирование» вернуть из архива нельзя — кнопки нет, архив только для просмотра.
        if (_canRestore)
        {
            var restore = SectionCards.Secondary(T("Вернуть", "Кайтаруу", "Restore", "Geri al", "Qaytarish"));
            restore.Click += async (_, _) => await RestoreAsync(id, name, restore);
            Grid.SetColumn(restore, 1);
            grid.Children.Add(restore);
        }
        return grid;
    }

    private async Task RestoreAsync(string id, string name, Button button)
    {
        if (_busy || string.IsNullOrWhiteSpace(id))
            return;
        _busy = true;
        button.IsEnabled = false;
        try
        {
            await _api.UnarchiveProductAsync(id).ConfigureAwait(true);
            PosLogger.Log($"AUDIT: товар «{name}» ({id}) возвращён из архива.", "INFO");
            RemovedProductMemory.Forget(id); // 2026-11-06: товар снова в каталоге — запись об удалённом больше не нужна
            Restored = true;
            RestoredIds.Add(id);
            if (button.Parent is Grid row)
                _list.Children.Remove(row);
            _found.RemoveAll(i => Text(i, "id") == id); // 2026-11-03 (Т3-04): список и кэш без вернувшегося товара
            if (!_loading)
                WriteCache(_found.ToList());
            else
                DropCache();
            _status.Text = T($"«{name}» возвращён из архива. Каталог обновится при закрытии окна.", $"«{name}» архивден кайтарылды. Терезе жабылганда каталог жаңыланат.",
                $"“{name}” was restored from the archive. The catalog refreshes when you close the window.", $"«{name}» arşivden geri alındı. Pencere kapanınca katalog yenilenir.",
                $"«{name}» arxivdan qaytarildi. Oyna yopilganda katalog yangilanadi.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Склад: товар «{name}» ({id}) не возвращён из архива ({ex.Message}).", "WARNING");
            _status.Text = T($"Не удалось вернуть «{name}» из архива. Проверьте связь с сервером и повторите.", $"«{name}» архивден кайтаруу мүмкүн болгон жок. Сервер менен байланышты текшерип, кайталаңыз.",
                $"Could not restore “{name}” from the archive. Check the server connection and try again.", $"«{name}» arşivden geri alınamadı. Sunucu bağlantısını kontrol edip tekrar deneyin.",
                $"«{name}» ni arxivdan qaytarib bo'lmadi. Server bilan aloqani tekshirib, qayta urinib ko'ring.");
            button.IsEnabled = true;
        }
        finally
        {
            _busy = false;
        }
    }
}
