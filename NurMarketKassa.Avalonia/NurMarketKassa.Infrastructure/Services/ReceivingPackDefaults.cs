using NurMarketKassa.Models;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>2026-11-11 (владелец: «упаковку тоже сделай СВЕРХУ»): способ приёмки по умолчанию для НОВЫХ строк, выбранный переключателем на верхней панели
/// «Склад → Приёмка» и «Закупки → Приём товара». Три способа — те же, что в меню строки (ReceivingPackMenu): «в штуках», «упаковками → на склад штуки»,
/// «упаковка с поштучной продажей».</summary>
public enum ReceivingPackDefaultMode
{
    /// <summary>В штуках: количество и цена закупки — за штуку (как было).</summary>
    Pieces = 0,

    /// <summary>Упаковками: вводятся упаковки, «шт в упак.» и цена за упаковку; на склад идут штуки.</summary>
    Packs = 1,

    /// <summary>Поштучная продажа из упаковки: остаток — в упаковках, новый товар заводится с упаковкой.</summary>
    PackSale = 2,
}

/// <summary>2026-11-11: применение способа приёмки по умолчанию к строкам. Только решает, КАКИМ строкам можно, и вызывает уже готовые переключатели строки
/// (<see cref="ReceivingLineVm.SetPackMode"/>, <see cref="ReceivingLineVm.SetPackSale"/>) — расчёты упаковок, пределы цен и подтверждение «Было → станет»
/// не дублируются и не обходятся. Правила:
/// • товар, который склад уже ведёт упаковками (упаковка в карточке, <see cref="ReceivingLineVm.CardPackSize"/> &gt; 0), остаётся «упак. = N шт» — не трогаем;
/// • весовой товар и строка с размерами — не трогаем (как и в меню строки);
/// • «поштучная продажа» — только НОВЫМ товарам: у товара склада она меняет единицу остатка, это делается явно в самой строке (меню «упак. ▾») с подтверждением;
/// • размер упаковки: сначала память по товару (<see cref="ReceivingPackSizes"/>), затем «штук в упаковке по умолчанию» с панели.</summary>
public static class ReceivingPackDefaults
{
    /// <summary>Что случилось со строками при «Применить ко всем».</summary>
    public readonly record struct Outcome(int Applied, int Unchanged, int SkippedCardPack, int SkippedUnsupported, int SkippedExistingProduct)
    {
        public int Skipped => SkippedCardPack + SkippedUnsupported + SkippedExistingProduct;
    }

    /// <summary>Режим, который строка имеет сейчас (для сравнения).</summary>
    public static ReceivingPackDefaultMode ModeOf(ReceivingLineVm line) =>
        line.PackSale ? ReceivingPackDefaultMode.PackSale : line.PackMode ? ReceivingPackDefaultMode.Packs : ReceivingPackDefaultMode.Pieces;

    /// <summary>Допустимый размер «штук в упаковке по умолчанию»: больше 0 и меньше 1e9 (как у размера упаковки строки).</summary>
    public static bool IsValidSize(double size) => !double.IsNaN(size) && !double.IsInfinity(size) && size > 0 && size < 1e9;

    /// <summary>2026-11-12 (аудит Л-24): для «поштучной продажи» штук в упаковке — целое число от 2 (как в карточке товара и в проверке прихода: PurchaseReceivingService); для «Упаковками» — любое число больше 0.
    /// Раньше поле панели принимало 0,5 и 1, а строка «поштучной продажи» с таким размером потом не проводилась.</summary>
    public static bool IsValidSizeFor(ReceivingPackDefaultMode mode, double size) =>
        IsValidSize(size) && (mode != ReceivingPackDefaultMode.PackSale || (size >= 2 && Math.Abs(size - Math.Round(size)) < 1e-9));

    /// <summary>2026-11-12 (аудит Л-9): способ, который не «В штуках», надо показывать заметно — иначе один скан молча превращается в упаковку (N штук).</summary>
    public static bool IsNonDefault(ReceivingPackDefaultMode mode) => mode != ReceivingPackDefaultMode.Pieces;

    /// <summary>2026-11-12 (аудит Л-9, решение владельца): способ приёма живёт один день и одно открытие приёмки. Выбранный вчера (или в другом открытии) — «В штуках»;
    /// после проведения прихода способ сбрасывается вызывающим кодом. <paramref name="chosenOn"/> — день, когда способ выбрали.</summary>
    public static ReceivingPackDefaultMode EffectiveMode(ReceivingPackDefaultMode chosen, DateTime chosenOn, DateTime now) =>
        chosen != ReceivingPackDefaultMode.Pieces && chosenOn.Date == now.Date ? chosen : ReceivingPackDefaultMode.Pieces;

    /// <summary>2026-11-12 (аудит Л-10): «поштучная продажа» к НОВОЙ строке товара склада не применяется (меняет единицу остатка — только в самой строке с подтверждением);
    /// панель один раз скажет об этом, а не промолчит. true — именно этот случай.</summary>
    public static bool IsSkippedExistingProduct(ReceivingLineVm line, ReceivingPackDefaultMode mode) => Check(line, mode) == Verdict.ExistingProduct;

    private enum Verdict { Apply, Same, CardPack, Unsupported, ExistingProduct }

    private static Verdict Check(ReceivingLineVm line, ReceivingPackDefaultMode mode)
    {
        if (line.CardPackSize > 0)
            return Verdict.CardPack;
        if (line.IsWeight || line.VariantQuantities is { Count: > 0 })
            return Verdict.Unsupported;
        if (mode == ReceivingPackDefaultMode.PackSale && !line.PackSale)
        {
            if (!line.CanPackSale)
                return Verdict.Unsupported;
            if (!line.IsNew)
                return Verdict.ExistingProduct;
        }
        return ModeOf(line) == mode ? Verdict.Same : Verdict.Apply;
    }

    private static double SizeFor(ReceivingLineVm line, double defaultSize)
    {
        var remembered = ReceivingPackSizes.Suggest(line);
        return remembered > 0 ? remembered : IsValidSize(defaultSize) ? defaultSize : 0;
    }

    /// <summary>2026-11-12 (Л-24): размер для «поштучной продажи» — только целый от 2; непригодный (1, 0,5) не подставляем, строка попросит ввести («Штук в упаковке?»), а не уйдёт непроводимой.</summary>
    private static double SizeFor(ReceivingLineVm line, ReceivingPackDefaultMode mode, double defaultSize)
    {
        var size = SizeFor(line, defaultSize);
        return IsValidSizeFor(mode, size) ? size : 0;
    }

    private static void Switch(ReceivingLineVm line, ReceivingPackDefaultMode mode, double defaultSize)
    {
        switch (mode)
        {
            case ReceivingPackDefaultMode.Pieces:
                line.SetPackMode(false); // из «поштучной продажи» SetPackMode(false) сначала выходит в штуки (числа пересчитываются)
                break;
            case ReceivingPackDefaultMode.Packs:
                line.SetPackMode(true, SizeFor(line, mode, defaultSize));
                break;
            default:
                line.SetPackSale(true, SizeFor(line, mode, defaultSize));
                break;
        }
    }

    /// <summary>НОВАЯ строка (скан, выбор из списка, «+ Новый товар»): режим по умолчанию. true — режим включён. Строка, у которой режим уже свой
    /// (после черновика, «Вернуть»), сюда не попадает — вызывающий зовёт метод только для только что созданной строки.</summary>
    public static bool ApplyToNew(ReceivingLineVm line, ReceivingPackDefaultMode mode, double defaultSize)
    {
        if (mode == ReceivingPackDefaultMode.Pieces || Check(line, mode) != Verdict.Apply)
            return false;
        Switch(line, mode, defaultSize);
        return true;
    }

    /// <summary>Сколько строк изменится кнопкой «Применить ко всем» и у скольких из них уже введены значения (количество не «1» или уже упаковки) —
    /// для вопроса «пересчитать?». Строки не меняются.</summary>
    public static (int WillChange, int WithValues) Preview(IEnumerable<ReceivingLineVm> lines, ReceivingPackDefaultMode mode)
    {
        int change = 0, values = 0;
        foreach (var line in lines)
        {
            if (Check(line, mode) != Verdict.Apply)
                continue;
            change++;
            // 2026-11-12 (аудит У-2): «введено» — и количество не «1», и уже упаковки, и цены закупки/продажи (раньше строки с ценами и количеством 1 пересчитывались без вопроса).
            if (HasEnteredValues(line))
                values++;
        }
        return (change, values);
    }

    /// <summary>В строке уже что-то введено руками или посчитано: количество не «1», упаковки или цена закупки/продажи.</summary>
    public static bool HasEnteredValues(ReceivingLineVm line) =>
        Math.Abs(line.Quantity - 1) > 1e-9 || line.PackMode || line.PurchasePrice > 0 || line.SalePrice > 0;

    /// <summary>2026-11-12 (аудит У-2): состояние строки до «Ко всем строкам» — чтобы вернуть кнопкой «Вернуть». Запоминаются только строки, которые изменятся.</summary>
    public sealed class Snapshot
    {
        internal readonly List<(ReceivingLineVm Line, ReceivingPackDefaultMode Mode, double Quantity, double PurchasePrice, double SalePrice, double PiecesPerPack)> Rows = new();

        /// <summary>Сколько строк запомнено.</summary>
        public int Count => Rows.Count;
    }

    /// <summary>Запомнить строки, которые «Ко всем строкам» изменит (вызывать до <see cref="ApplyToAll"/>).</summary>
    public static Snapshot Capture(IEnumerable<ReceivingLineVm> lines, ReceivingPackDefaultMode mode)
    {
        var snap = new Snapshot();
        foreach (var line in lines)
            if (Check(line, mode) == Verdict.Apply)
                snap.Rows.Add((line, ModeOf(line), line.Quantity, line.PurchasePrice, line.SalePrice, line.PiecesPerPack));
        return snap;
    }

    /// <summary>Вернуть запомненные строки: способ строки и её числа как были. Возвращает, сколько строк восстановлено.</summary>
    public static int Restore(Snapshot snapshot)
    {
        var restored = 0;
        foreach (var (line, mode, quantity, purchase, sale, per) in snapshot.Rows)
        {
            Switch(line, mode, per);
            if (Math.Abs(line.Quantity - quantity) > 1e-9)
                line.Quantity = quantity;
            if (Math.Abs(line.PurchasePrice - purchase) > 1e-9)
                line.PurchasePrice = purchase;
            if (Math.Abs(line.SalePrice - sale) > 1e-9)
                line.SalePrice = sale;
            restored++;
        }
        return restored;
    }

    /// <summary>«Применить ко всем строкам»: каждой допустимой строке — режим (числа пересчитываются самой строкой, как при выборе в меню строки).</summary>
    public static Outcome ApplyToAll(IEnumerable<ReceivingLineVm> lines, ReceivingPackDefaultMode mode, double defaultSize)
    {
        int applied = 0, same = 0, card = 0, unsupported = 0, existing = 0;
        foreach (var line in lines.ToList())
        {
            switch (Check(line, mode))
            {
                case Verdict.Apply:
                    Switch(line, mode, defaultSize);
                    applied++;
                    break;
                case Verdict.Same:
                    same++;
                    break;
                case Verdict.CardPack:
                    card++;
                    break;
                case Verdict.Unsupported:
                    unsupported++;
                    break;
                default:
                    existing++;
                    break;
            }
        }
        return new Outcome(applied, same, card, unsupported, existing);
    }
}
