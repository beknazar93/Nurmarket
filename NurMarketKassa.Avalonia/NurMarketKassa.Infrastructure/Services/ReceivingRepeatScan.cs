using System.Globalization;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

/// <summary>2026-11-09 (владелец: в мобилке при повторном скане того же товара сканер останавливается и спрашивает количество — «и это же должно быть и на закупках в десктопе»).
/// Чистая логика (без окон — проверяется тестами) по образцу мобильного RepeatScanGuard: скан того же кода СРАЗУ после добавления — повтор.
/// Повтором код перестаёт быть, когда отсканировали другой код, закрыли приёмку (<see cref="Reset"/>), прошла пауза дольше <see cref="ResetAfter"/>
/// или настройка «Спрашивать количество при повторном скане» выключена. Ответ — только «ново / повтор»; саму строку и окно «Сколько штук?» ведёт приёмка.</summary>
public enum ReceivingScanVerdict
{
    /// <summary>Новый скан: добавить как обычно (+1 или количество из поля).</summary>
    Fresh,

    /// <summary>Тот же код сразу после добавления: остановить ввод и спросить «Сколько штук?».</summary>
    Repeat,
}

public sealed class ReceivingRepeatScanGuard
{
    /// <summary>Пауза, после которой тот же код снова считается новым (кладовщик отошёл и вернулся). Настраивается здесь.</summary>
    public static readonly TimeSpan DefaultResetAfter = TimeSpan.FromSeconds(20);

    private readonly Func<bool> _enabled;
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _resetAfter;
    private string? _code;
    private DateTime _at;

    /// <param name="enabled">Включена ли настройка; по умолчанию — <see cref="UserPreferences.AskQtyOnRepeatScan"/> (ВКЛ).</param>
    /// <param name="clock">Часы (для тестов).</param>
    /// <param name="resetAfter">Пауза сброса; по умолчанию <see cref="DefaultResetAfter"/>.</param>
    public ReceivingRepeatScanGuard(Func<bool>? enabled = null, Func<DateTime>? clock = null, TimeSpan? resetAfter = null)
    {
        _enabled = enabled ?? (() => UserPreferences.Instance.AskQtyOnRepeatScan);
        _clock = clock ?? (() => DateTime.UtcNow);
        _resetAfter = resetAfter ?? DefaultResetAfter;
    }

    /// <summary>Последний засчитанный код (null — отсчёта нет).</summary>
    public string? LastCode => _code;

    private static string Norm(string? code) => (code ?? "").Trim();

    /// <summary>Что это за скан. Состояние не меняет (кроме устаревания по паузе): после добавления вызвать <see cref="Counted"/>.</summary>
    public ReceivingScanVerdict Check(string? code)
    {
        var c = Norm(code);
        if (c.Length == 0 || _code is null || !_enabled())
            return ReceivingScanVerdict.Fresh;
        if (_clock() - _at > _resetAfter)
        {
            _code = null;
            return ReceivingScanVerdict.Fresh;
        }
        return string.Equals(c, _code, StringComparison.OrdinalIgnoreCase) ? ReceivingScanVerdict.Repeat : ReceivingScanVerdict.Fresh;
    }

    /// <summary>Код засчитан (строка добавлена или количество прибавлено, в том числе после окна «Сколько штук?»): с этого момента его повтор — повтор. Другой код прежний отсчёт заменяет.</summary>
    public void Counted(string? code)
    {
        var c = Norm(code);
        if (c.Length == 0)
        {
            Reset();
            return;
        }
        _code = c;
        _at = _clock();
    }

    /// <summary>Сбросить (закрыли приёмку, очистили список, добавили товар не сканом): следующий код — новый.</summary>
    public void Reset() => _code = null;

    /// <summary>2026-11-11 (Д-8, живая проверка «Склада»): ручная правка количества строки ПОСЛЕДНЕГО кода — не сброс, а работа с той же строкой:
    /// код остаётся последним, пауза <see cref="ResetAfter"/> считается заново от конца правки. Раньше пока кладовщик набирал число (≈ 30 с), пауза
    /// истекала, и следующий скан того же товара молча прибавлял +1 без «Сколько штук?». Сброшенный (другим кодом, закрытием, добавлением не сканом) код не оживляет.</summary>
    public void Touch()
    {
        if (_code is not null)
            _at = _clock();
    }
}

/// <summary>2026-11-09: общие действия окна «Сколько штук?» над строкой приёмки — единица, дробность, разбор числа, прибавление (одно и то же в «Склад → Приёмка» и «Закупки → Приём товара»).</summary>
public static class ReceivingRepeatScan
{
    /// <summary>Самое большое число, которое окно принимает (защита от лишнего нуля и от штрихкода, попавшего в поле).</summary>
    public const double MaxQuantity = 1_000_000;

    /// <summary>Дробное ли количество в окне: в режиме упаковок — нет (упаковки целые); весовой товар и дробные единицы (л, кг, м…) — да; неизвестная своя единица — да (ничего не запрещаем).</summary>
    public static bool IsFractional(ReceivingLineVm line) =>
        !line.PackMode && (line.IsWeight || UnitCatalog.IsFractional(line.Unit) != false);

    /// <summary>Сколько уже в строке: в режиме упаковок — упаковок, иначе в единицах строки.</summary>
    public static double Current(ReceivingLineVm line) => line.PackMode ? line.PackCount : line.Quantity;

    /// <summary>Единица рядом с числом: «шт», «л», «кг»; в режиме упаковок — «упак.».</summary>
    public static string UnitLabel(ReceivingLineVm line) =>
        line.PackMode ? Tr.T("упак.", "таңгак", "packs", "koli", "qadoq") : UnitCatalog.Display(line.Unit);

    /// <summary>Название в заголовке окна: название строки, у безымянной (новый товар) — штрихкод.</summary>
    public static string Title(ReceivingLineVm line) =>
        !string.IsNullOrWhiteSpace(line.ProductName) ? line.ProductName : !string.IsNullOrWhiteSpace(line.Barcode) ? line.Barcode : Tr.T("Товар", "Товар", "Product", "Ürün", "Mahsulot");

    /// <summary>Число как в окне: без хвостовых нулей, с запятой («2,5»).</summary>
    public static string Format(double value) => value.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"));

    /// <summary>«Уже в приёмке: 3 шт. Сколько добавить?» — вопрос окна.</summary>
    public static string Message(ReceivingLineVm line)
    {
        var have = (Format(Current(line)) + " " + UnitLabel(line)).Trim();
        if (!have.EndsWith('.'))
            have += "."; // «шт.» уже с точкой — вторую не ставим
        return Tr.T($"Уже в приёмке: {have} Сколько добавить?", $"Кабыл алууда бар: {have} Канчасын кошосуз?", $"Already in the receiving: {have} How many to add?",
            $"Kabulde zaten var: {have} Kaç tane eklensin?", $"Qabulda allaqachon bor: {have} Nechta qo'shiladi?");
    }

    /// <summary>Разбор числа из поля окна: запятая или точка, пробелы разрядов; больше нуля и не больше <see cref="MaxQuantity"/>. Целое (штуки, упаковки) округляется до целого,
    /// дробное — до 3 знаков (больше сервер не принимает). null — число не годится.</summary>
    public static double? Parse(string? text, bool fractional)
    {
        var s = (text ?? "").Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace(',', '.').Trim();
        if (s.Length == 0 || !double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) || double.IsNaN(v) || double.IsInfinity(v))
            return null;
        v = fractional ? Math.Round(v, 3, MidpointRounding.AwayFromZero) : Math.Round(v, 0, MidpointRounding.AwayFromZero);
        return v > 0 && v <= MaxQuantity ? v : null;
    }

    /// <summary>Шаг кнопок «+» и «−»: 1 для штук и упаковок, 0,5 для дробных единиц.</summary>
    public static double Step(bool fractional) => fractional ? 0.5 : 1;

    /// <summary>2026-11-11: число после «+»/«−» окна: до 3 знаков, не меньше шага и не больше <see cref="MaxQuantity"/> (расчёт здесь, а не в окне — проверка check-money-in-views).</summary>
    public static double Bump(double current, double delta, bool fractional) =>
        Math.Min(Math.Max(Math.Round(current + delta, 3), Step(fractional)), MaxQuantity);

    /// <summary>Прибавить <paramref name="amount"/> к строке (а не новая строка): в режиме упаковок — к числу упаковок (штуки пересчитает строка), иначе к количеству.
    /// Сумма округляется до 3 знаков (0,1 + 0,2 = 0,3), если у строки не было лишних знаков — их строка пометит сама (QuantityTooPrecise).</summary>
    public static void AddTo(ReceivingLineVm line, double amount)
    {
        if (amount <= 0 || double.IsNaN(amount) || double.IsInfinity(amount))
            return;
        // Упаковки прибавляются штуками (N × штук в упаковке; «поштучная продажа» — склад ведёт упаковки, множитель 1): так штуки остаются точными, а число упаковок
        // строка досчитывает сама (пересчёт из штук округляет до 6 знаков — прибавлять к нему напрямую значило бы получить 73,000008 шт).
        if (line.PackMode)
            amount *= line.PackSale ? 1 : line.PiecesPerPack > 0 ? line.PiecesPerPack : 1;
        var sum = line.Quantity + amount;
        line.Quantity = ReceivingLineVm.QuantityTooPrecise(line.Quantity) ? sum : Math.Round(sum, 3, MidpointRounding.AwayFromZero);
    }

    /// <summary>Строка приёмки, в которую уходит скан этого кода: по штрихкоду строки, иначе по товару, которого этот код (доп. штрихкод, 12↔13 цифр) означает. null — такой строки нет.</summary>
    public static ReceivingLineVm? FindLine(IEnumerable<ReceivingLineVm> lines, string code)
    {
        var c = (code ?? "").Trim();
        if (c.Length == 0)
            return null;
        var list = lines as IReadOnlyCollection<ReceivingLineVm> ?? lines.ToList();
        var byBarcode = list.FirstOrDefault(l => string.Equals(l.Barcode, c, StringComparison.OrdinalIgnoreCase));
        if (byBarcode is not null)
            return byBarcode;
        var product = ProductCodeLookup.FindLocal(ProductCodeLookup.Normalize(c), allowWeightLabel: false)?.Product;
        return product is null ? null : list.FirstOrDefault(l => string.Equals(l.ProductId, product.Id, StringComparison.OrdinalIgnoreCase));
    }
}
