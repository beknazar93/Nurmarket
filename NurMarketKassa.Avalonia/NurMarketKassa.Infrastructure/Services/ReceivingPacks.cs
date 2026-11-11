using System.Text.Json;
using NurMarketKassa.Models;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>2026-11-07 (решение владельца «в приёмке надо добавить функцию — при приёме принять упаковками»): чистые расчёты режима «Принять упаковками»
/// в ручной приёмке («Закупки» → «Приём товара»). Кассир вводит число упаковок, сколько штук в упаковке и цену ЗА УПАКОВКУ; в строку приёмки
/// (<see cref="ReceivingLineVm.Quantity"/> / <see cref="ReceivingLineVm.PurchasePrice"/>) и дальше на сервер, в очередь «Неотправленное», накладную, этикетки
/// идут уже штуки и цена ЗА ШТУКУ.
///
/// Точность (проверено ранее по серверу): количество — до 3 знаков (<see cref="ReceivingLineVm.QuantityTooPrecise"/>), цена закупки — до
/// <see cref="ReceivingPricing.PurchaseDecimals"/> = 4 знаков. Цена за штуку = цена упаковки ÷ штук, округление до 4 знаков (AwayFromZero, в decimal). Если упаковка не делится
/// ровно, «штуки × цена за штуку» может отличаться от «упаковки × цена упаковки» на копейки — это показывается строке (<see cref="LossText"/>), молча сумма не меняется:
/// сервер запишет именно штуки × цена за штуку (<see cref="ReceivingPricing.LineTotal"/>), его и показывает таблица.</summary>
public static class ReceivingPacks
{
    private static bool Ok(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0 && v < 1e9;

    /// <summary>Штуки = упаковки × штук в упаковке (в decimal, без шума double). Нет одного из чисел — 0.</summary>
    public static double Pieces(double packs, double perPack)
    {
        if (!Ok(packs) || !Ok(perPack) || packs <= 0 || perPack <= 0)
            return 0;
        return (double)Math.Round((decimal)packs * (decimal)perPack, 9, MidpointRounding.AwayFromZero);
    }

    /// <summary>Цена за штуку = цена упаковки ÷ штук в упаковке, округление до 4 знаков (то, что хранит сервер). Нет цены или размера упаковки — 0.</summary>
    public static double PiecePrice(double packPrice, double perPack)
    {
        if (!Ok(packPrice) || !Ok(perPack) || packPrice <= 0 || perPack <= 0)
            return 0;
        return (double)Math.Round((decimal)packPrice / (decimal)perPack, ReceivingPricing.PurchaseDecimals, MidpointRounding.AwayFromZero);
    }

    /// <summary>Цена упаковки из цены за штуку (обратный счёт для показа): штука × штук в упаковке, до 4 знаков.</summary>
    public static double PackPriceFromPiece(double piecePrice, double perPack)
    {
        if (!Ok(piecePrice) || !Ok(perPack) || piecePrice <= 0 || perPack <= 0)
            return 0;
        return (double)Math.Round((decimal)piecePrice * (decimal)perPack, ReceivingPricing.PurchaseDecimals, MidpointRounding.AwayFromZero);
    }

    /// <summary>Сколько упаковок в данном числе штук (для показа), до 6 знаков.</summary>
    public static double PacksFromPieces(double pieces, double perPack)
    {
        if (!Ok(pieces) || !Ok(perPack) || pieces <= 0 || perPack <= 0)
            return 0;
        return (double)Math.Round((decimal)pieces / (decimal)perPack, 6, MidpointRounding.AwayFromZero);
    }

    /// <summary>Сумма «по упаковкам»: упаковки × цена упаковки, до копеек по итогу (как ReceivingPricing.LineTotal).</summary>
    public static double PackTotal(double packs, double packPrice)
    {
        if (!Ok(packs) || !Ok(packPrice))
            return 0;
        return (double)Math.Round((decimal)packs * (decimal)packPrice, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Разница «по упаковкам» минус «как проведёт сервер» (штуки × цена за штуку). Не ноль — упаковка не делится на цену за штуку без остатка в 4 знаках,
    /// и копейки накладной меняются.</summary>
    public static double RoundingDelta(double packs, double perPack, double packPrice)
    {
        var served = ReceivingPricing.LineTotal(Pieces(packs, perPack), PiecePrice(packPrice, perPack));
        return Math.Round(PackTotal(packs, packPrice) - served, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Сумма строки отличается от «упаковки × цена упаковки» хотя бы на копейку.</summary>
    public static bool HasRoundingLoss(double packs, double perPack, double packPrice) =>
        perPack > 0 && packs > 0 && packPrice > 0 && Math.Abs(RoundingDelta(packs, perPack, packPrice)) >= 0.005;

    /// <summary>Красная пометка строки: «цена за штуку округлена до 4 знаков — сумма строки 99 999,90 вместо 100 000,00».</summary>
    public static string LossText(double packs, double perPack, double packPrice)
    {
        if (!HasRoundingLoss(packs, perPack, packPrice))
            return "";
        var served = MoneyText.Som(ReceivingPricing.LineTotal(Pieces(packs, perPack), PiecePrice(packPrice, perPack)));
        var exact = MoneyText.Som(PackTotal(packs, packPrice));
        return Tr.T($"Упаковка не делится ровно: цена за штуку округлена до 4 знаков — сумма строки {served} вместо {exact}",
            $"Таңгак так бөлүнбөйт: даананын баасы 4 цифрага тегизделди — саптын суммасы {exact} ордуна {served}",
            $"The pack does not divide evenly: the unit price is rounded to 4 decimals — line total {served} instead of {exact}",
            $"Koli tam bölünmüyor: adet fiyatı 4 basamağa yuvarlandı — satır tutarı {exact} yerine {served}",
            $"Qadoq teng bo'linmaydi: dona narxi 4 xonagacha yaxlitlandi — qator summasi {exact} o'rniga {served}");
    }

    // ------------------------------------------------------------------ 2026-11-07: «упаковка с поштучной продажей» (подтверждение)

    /// <summary>Текст подтверждения перед проведением строк «упаковка с поштучной продажей»: единица остатка товара меняется — спрашиваем явно.</summary>
    /// <summary>2026-11-10 (упаковка у товара склада): остаток <paramref name="stock"/> (в штуках, а если у товара уже есть упаковка по <paramref name="oldPerPack"/> шт — в таких упаковках)
    /// в упаковках по <paramref name="newPerPack"/> шт. false — не делится на ЦЕЛЫЕ упаковки (дробный остаток штучного товара сервер не принимает; такую строку не проводим).</summary>
    public static bool TryConvertStock(double stock, double oldPerPack, double newPerPack, out double newStock)
    {
        newStock = 0;
        if (newPerPack <= 0 || double.IsNaN(stock) || double.IsInfinity(stock))
            return false;
        var pieces = (decimal)stock * (decimal)(oldPerPack > 0 ? oldPerPack : 1);
        var packs = pieces / (decimal)newPerPack;
        if (Math.Abs(packs - Math.Round(packs)) > 0.000001m)
            return false;
        newStock = (double)Math.Round(packs);
        return true;
    }

    public static string PackSaleConfirmMessage(IReadOnlyList<ReceivingLineVm> lines)
    {
        static string N(double v) => v.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
        var shown = lines.Take(10).Select(l =>
        {
            var name = string.IsNullOrWhiteSpace(l.ProductName) ? l.Barcode : l.ProductName;
            var per = N(l.PackSaleSize);
            var packs = N(l.Quantity);
            var piece = N(PurchaseReceivingService.PackPieceSalePrice(l));
            // 2026-11-10 (упаковка у товара склада): «Было: N шт → станет: M упак. по K шт; остаток …; приход …; продажа поштучно: цена штуки …».
            if (l.PackSaleConvertsExisting)
            {
                var stock = N(l.StockBefore);
                var after = l.ConvertedStock is { } a ? N(a) : "?";
                var packPrice = N(l.PurchasePrice);
                var was = l.CardPackSize > 0
                    ? Tr.T($"упаковка по {N(l.CardPackSize)} шт, остаток {stock} упак.", $"таңгак {N(l.CardPackSize)} даанадан, калдык {stock} таңгак", $"a pack of {N(l.CardPackSize)} pcs, stock {stock} packs", $"{N(l.CardPackSize)} adetlik koli, stok {stock} koli", $"{N(l.CardPackSize)} donalik qadoq, qoldiq {stock} qadoq")
                    : Tr.T($"{stock} шт", $"{stock} даана", $"{stock} pcs", $"{stock} adet", $"{stock} dona");
                return "• " + name + ": " + Tr.T($"было: {was} → станет: {after} упак. по {per} шт; приход: +{packs} упак. по {packPrice} сом за упаковку; продажа поштучно: цена штуки {piece} сом",
                    $"болгон: {was} → болот: {after} таңгак, ар бири {per} даана; кирүү: +{packs} таңгак, таңгагы {packPrice} сом; даана менен сатуу: даананын баасы {piece} сом",
                    $"was: {was} → will be: {after} packs of {per} pcs; receipt: +{packs} packs at {packPrice} som per pack; piece sale: piece price {piece} som",
                    $"önce: {was} → sonra: {per} adetlik {after} koli; giriş: +{packs} koli, kolisi {packPrice} som; adetli satış: adet fiyatı {piece} som",
                    $"avval: {was} → bo'ladi: {per} donalik {after} qadoq; kirim: +{packs} qadoq, qadog'i {packPrice} so'm; donalab sotish: dona narxi {piece} so'm");
            }
            return "• " + name + ": " + Tr.T($"{packs} упак. по {per} шт, штука в продаже {piece} сом", $"{packs} таңгак, ар бири {per} дана, даанасы сатууда {piece} сом",
                $"{packs} packs of {per} pcs, piece on sale {piece} som", $"{packs} koli, her biri {per} adet, adet satışı {piece} som", $"{packs} qadoq, har biri {per} dona, donasi sotuvda {piece} so'm");
        }).ToList();
        if (lines.Count > shown.Count)
            shown.Add(Tr.T($"…и ещё {lines.Count - shown.Count}", $"…жана дагы {lines.Count - shown.Count}", $"…and {lines.Count - shown.Count} more", $"…ve {lines.Count - shown.Count} tane daha", $"…va yana {lines.Count - shown.Count}"));
        var one = lines.Count == 1 ? N(lines[0].PackSaleSize) : "N";
        return Tr.T($"Товар будет вестись упаковками по {one} шт, остаток — в упаковках (поштучная продажа из упаковки, как в карточке товара). Продолжить?",
                $"Товар таңгак менен жүргүзүлөт, ар бири {one} дана, калдык — таңгакта (таңгактан даана менен сатуу, товардын картасындагыдай). Улантабызбы?",
                $"The product will be kept in packs of {one} pcs, the stock — in packs (single pieces from a pack, as in the product card). Continue?",
                $"Ürün {one} adetlik kolilerle tutulacak, stok — kolilerde (koliden tek tek satış, ürün kartındaki gibi). Devam edilsin mi?",
                $"Mahsulot {one} donalik qadoqlar bilan yuritiladi, qoldiq — qadoqlarda (qadoqdan donalab sotish, mahsulot kartasidagidek). Davom etamizmi?")
            + "\n\n" + string.Join("\n", shown);
    }

    // ------------------------------------------------------------------ карточка товара

    /// <summary>У товара в каталоге есть «поштучная продажа из упаковки» (PieceOption): склад ведёт его УПАКОВКАМИ (остаток в упаковках, «шт» = остаток × штук в упаковке — см. PackageChoiceDialog),
    /// поэтому «Принимаем» и цена закупки у него уже за упаковку. Умножать на «штук в упаковке» нельзя — остаток вырос бы в N раз. Возвращает штук в упаковке (0 — такой опции нет).</summary>
    public static double PieceOptionSize(ReceivingLineVm line)
    {
        if (string.IsNullOrEmpty(line.ProductId))
            return 0;
        try
        {
            var tile = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, line.ProductId, StringComparison.OrdinalIgnoreCase));
            return tile?.PieceOption is { QuantityInPackage: > 0 } option ? option.QuantityInPackage : 0;
        }
        catch (InvalidOperationException)
        {
            return 0; // каталог обновлялся в этот момент
        }
    }
}

/// <summary>2026-11-07: «штук в упаковке» по товару, который уже принимали упаковками, — запоминается ЛОКАЛЬНО (файл receiving-pack-sizes.json рядом с настройками).
/// В карточку товара на сервере не пишется: единственное поле «упаковки» карточки (packages / piece_option) — это ПОШТУЧНАЯ ПРОДАЖА из упаковки, она меняет
/// единицу складского остатка товара, и включать её из приёмки нельзя. Ключ — id товара и/или штрихкод. Ошибки чтения/записи не бросаются.</summary>
public static class ReceivingPackSizes
{
    private static readonly object Gate = new();
    private static Dictionary<string, double>? _map;

    /// <summary>Стенд проверки: своя папка вместо %APPDATA% (до первого обращения).</summary>
    public static string? DataDirOverride { get; set; }

    /// <summary>Стенд проверки: ничего не писать на диск.</summary>
    public static bool PersistenceDisabled { get; set; }

    private static string FilePath => Path.Combine(
        DataDirOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName),
        "receiving-pack-sizes.json");

    /// <summary>Стенд: забыть прочитанное (следующее обращение читает файл заново).</summary>
    public static void Reset()
    {
        lock (Gate)
            _map = null;
    }

    private static Dictionary<string, double> Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(FilePath)) is { } read)
                return new Dictionary<string, double>(read.Where(kv => kv.Value > 0 && kv.Value < 1e9), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приёмка упаковками: файл размеров упаковок не прочитан ({ex.GetType().Name}) — начнём заново.", "WARNING");
        }
        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Keys(ReceivingLineVm line)
    {
        if (!string.IsNullOrWhiteSpace(line.ProductId))
            yield return "id:" + line.ProductId!.Trim();
        if (!string.IsNullOrWhiteSpace(line.Barcode))
            yield return "bc:" + line.Barcode.Trim();
    }

    /// <summary>Сколько штук в упаковке было в прошлый раз у этого товара (0 — не запоминали).</summary>
    public static double Suggest(ReceivingLineVm line)
    {
        lock (Gate)
        {
            _map ??= Load();
            foreach (var key in Keys(line))
                if (_map.TryGetValue(key, out var v) && v > 0)
                    return v;
        }
        return 0;
    }

    /// <summary>Запомнить размер упаковки строки (если она принята упаковками и размер известен).</summary>
    public static void Remember(ReceivingLineVm line)
    {
        if (!line.PackMode || line.PackSale || line.PiecesPerPack <= 0 || line.PiecesPerPack >= 1e9)
            return; // 2026-11-07: «поштучная продажа» запоминается самой карточкой товара (упаковка), локальная память размеров ей не нужна
        try
        {
            Dictionary<string, double> snapshot;
            lock (Gate)
            {
                _map ??= Load();
                var changed = false;
                foreach (var key in Keys(line))
                    if (!_map.TryGetValue(key, out var old) || Math.Abs(old - line.PiecesPerPack) > 1e-9)
                    {
                        _map[key] = line.PiecesPerPack;
                        changed = true;
                    }
                if (!changed || PersistenceDisabled)
                    return;
                snapshot = new Dictionary<string, double>(_map);
            }
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приёмка упаковками: размер упаковки не записан ({ex.GetType().Name}) — не страшно, введите в следующий раз.", "WARNING");
        }
    }
}
