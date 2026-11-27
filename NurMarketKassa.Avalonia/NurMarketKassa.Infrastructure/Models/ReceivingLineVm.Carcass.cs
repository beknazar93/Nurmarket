using NurMarketKassa.Services;

#nullable enable

namespace NurMarketKassa.Models;

// 2026-11-26 (просьба владельца для мясных магазинов: «при покупке коровы вес сразу убавлять, для продажи остаётся другой вес»): «Выход мяса» в строке приёмки.
// Главные значения строки остаются прежними: Quantity = вес В ПРОДАЖУ (нетто), PurchasePrice = себестоимость килограмма в продаже (сумма закупки ÷ нетто, до 4 знаков) — их читают проведение,
// очередь «Неотправленное», накладная, итоги, цена продажи и наценка (как у упаковок, ReceivingLineVm в ReceivingLineVm.cs). Блок «Выход мяса» только ВВОДИТ их иначе:
// куплено (брутто) + цена за кг ИЛИ сумма закупки + выход % ИЛИ вес в продажу. Из каждой пары «главное» то, что вписали последним, второе показывается расчётным.
// Сам расчёт — CarcassYield (decimal, Infrastructure), здесь только состояние строки и связь с полями «Кол-во» / «Закупка». Новых полей API нет: на сервер уходят количество и цена закупки,
// а «Брутто 300 кг, выход 75 %, отходы 75 кг» — в примечание прихода (CarcassNote → comment документа).
public sealed partial class ReceivingLineVm
{
    private bool _carcassOn, _inCarcassCalc;
    private decimal _cGross;
    private decimal? _cPrice, _cTotal, _cYield, _cNet; // из каждой пары (цена / сумма, выход / нетто) задано одно — то, что вписали последним
    private string? _cGrossRaw, _cPriceRaw, _cTotalRaw, _cYieldRaw, _cNetRaw; // сырой набранный текст поля, пока в нём курсор
    private CarcassYieldResult? _cResult;
    private CarcassYieldProblem _cProblem;
    private string _carcassNoteStored = "";

    /// <summary>Строку можно принять с выходом: модуль включён (<see cref="CarcassYieldFeature"/>), товар весовой (кг или г) и строка не в режиме упаковок.</summary>
    public bool CarcassAllowed => !_packMode && (IsWeight || NurMarketKassa.Services.UnitCatalog.Normalize(_unit) == "г") && CarcassYieldFeature.IsAvailable;

    /// <summary>Блок «Выход мяса» включён у строки.</summary>
    public bool CarcassOn => _carcassOn;

    /// <summary>Последний расчёт (null — посчитать нельзя, причина в <see cref="CarcassProblem"/>).</summary>
    public CarcassYieldResult? CarcassResult => _cResult;

    public CarcassYieldProblem CarcassProblem => _carcassOn ? _cProblem : CarcassYieldProblem.None;

    /// <summary>Выход в % (расчётный или вписанный) — для запоминания товару; null — нет расчёта.</summary>
    public decimal? CarcassYieldPercent => _carcassOn && _cResult is { } r ? r.YieldPercent : null;

    /// <summary>Включить или выключить блок. Включение берёт «Кол-во» как купленный вес, «Закупку» как цену за кг брутто, а выход — прошлый у этого товара (или 100 %: ничего не меняется,
    /// пока выход не вписан). Выключение оставляет вес в продажу и себестоимость как есть (как у упаковок).</summary>
    public void SetCarcass(bool on)
    {
        if (_carcassOn == on)
            return;
        if (on && !CarcassAllowed)
            return;
        _carcassOn = on;
        if (on)
        {
            _cGross = ToCarcassDecimal(Quantity);
            _cPrice = PurchasePrice > 0 ? ToCarcassDecimal(PurchasePrice) : null;
            _cTotal = null;
            _cYield = CarcassYieldMemory.Suggest(_productId, _barcode) ?? 100m;
            _cNet = null;
            _cGrossRaw = _cPriceRaw = _cTotalRaw = _cYieldRaw = _cNetRaw = null;
            RecalcCarcass(writeQuantity: true, writePrice: true);
        }
        else
        {
            _carcassNoteStored = "";
            NotifyCarcass();
        }
    }

    private static decimal ToCarcassDecimal(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) >= 1e9 ? 0m : Math.Round((decimal)value, 6, MidpointRounding.AwayFromZero);

    /// <summary>Пересчитать и записать в «Кол-во» (вес в продажу) и «Закупку» (себестоимость кг в продаже). Нельзя посчитать — «Кол-во» становится 0 (строку не проведут), цена остаётся.</summary>
    private void RecalcCarcass(bool writeQuantity, bool writePrice)
    {
        _cResult = CarcassYield.Calculate(new CarcassYieldInput(_cGross, _cPrice, _cTotal, _cYield, _cNet), out _cProblem);
        if (!_inCarcassCalc)
        {
            _inCarcassCalc = true;
            try
            {
                if (_cResult is { } r)
                {
                    if (writeQuantity)
                        Quantity = (double)r.Net;
                    if (writePrice)
                    {
                        PurchasePrice = r.HasTotal ? (double)r.UnitPrice : 0;
                        if (r.HasTotal)
                        {
                            _purchaseManual = true; // цену посчитали из суммы — смена поставщика её не подменяет
                            ApplyAutoSale(); // цена продажи от себестоимости кг в продаже (автонаценка строки)
                        }
                    }
                }
                else if (writeQuantity)
                    Quantity = 0;
            }
            finally
            {
                _inCarcassCalc = false;
            }
        }
        NotifyCarcass();
    }

    /// <summary>Правка основной ячейки «Кол-во» (вес в продажу, в том числе «+»/«−», повторный скан и «Esc»): выход пересчитывается от купленного, сумма закупки не меняется — цена за кг в продаже следует за весом.</summary>
    private void CarcassQuantityEditedOutside()
    {
        _cGrossRaw = _cPriceRaw = _cTotalRaw = _cYieldRaw = _cNetRaw = null; // правят основную ячейку (в блоке курсора нет, «+»/«−» фокус не берут) — показ полей блока берётся из расчёта, а не из набранного текста
        _cNet = ToCarcassDecimal(Quantity);
        _cYield = null;
        if (_cNet > _cGross)
            _cGross = _cNet.Value; // в продажу не больше купленного: купленное подтягивается (выход 100 %)
        RecalcCarcass(writeQuantity: false, writePrice: true);
    }

    /// <summary>Правка основной ячейки «Закупка» (цена кг в продаже): сумма закупки = вес в продажу × цена, цена за кг брутто следует за ней. Введённую цену не переписываем.</summary>
    private void CarcassPriceEditedOutside()
    {
        _cGrossRaw = _cPriceRaw = _cTotalRaw = _cYieldRaw = _cNetRaw = null;
        var price = ToCarcassDecimal(PurchasePrice);
        var total = price > 0 ? CarcassYield.LineTotal(ToCarcassDecimal(Quantity), price) : 0m;
        _cTotal = total > 0 ? total : null;
        _cPrice = null;
        RecalcCarcass(writeQuantity: false, writePrice: false);
    }

    // ------------------------------------------------------------------ поля блока (текст, как у остальных ячеек строки)

    private static string ShowNumber(decimal? value, int decimals) => value is { } v && v > 0 ? CarcassYield.FormatNumber(v, decimals) : "";

    /// <summary>Куплено (вес «с костью»).</summary>
    public string CarcassGrossText
    {
        get => _cGrossRaw ?? ShowNumber(_cGross, 3);
        set
        {
            _cGrossRaw = (value ?? "").Trim();
            if (_cGrossRaw.Length == 0)
                _cGross = 0;
            else if (CarcassYield.TryParseNumber(_cGrossRaw, out var v))
                _cGross = v;
            else
                return;
            RecalcCarcass(writeQuantity: true, writePrice: true);
        }
    }

    /// <summary>Цена за единицу купленного веса (за кг брутто).</summary>
    public string CarcassPriceText
    {
        get => _cPriceRaw ?? ShowNumber(_cPrice ?? (_cResult is { HasTotal: true } r ? r.PricePerUnitGross : null), CarcassYield.PriceDecimals);
        set
        {
            _cPriceRaw = (value ?? "").Trim();
            if (_cPriceRaw.Length == 0)
                _cPrice = null;
            else if (CarcassYield.TryParseNumber(_cPriceRaw, out var v))
                _cPrice = v > 0 ? v : null;
            else
                return;
            _cTotal = null;
            RecalcCarcass(writeQuantity: true, writePrice: true);
        }
    }

    /// <summary>Общая сумма закупки (не меняется от выхода).</summary>
    public string CarcassTotalText
    {
        get => _cTotalRaw ?? ShowNumber(_cTotal ?? (_cResult is { HasTotal: true } r ? r.Total : null), 2);
        set
        {
            _cTotalRaw = (value ?? "").Trim();
            if (_cTotalRaw.Length == 0)
                _cTotal = null;
            else if (CarcassYield.TryParseNumber(_cTotalRaw, out var v))
                _cTotal = v > 0 ? v : null;
            else
                return;
            _cPrice = null;
            RecalcCarcass(writeQuantity: true, writePrice: true);
        }
    }

    /// <summary>Выход, % (сколько от купленного идёт в продажу).</summary>
    public string CarcassYieldText
    {
        get => _cYieldRaw ?? ShowNumber(_cYield ?? _cResult?.YieldPercent, 2);
        set
        {
            _cYieldRaw = (value ?? "").Trim();
            if (_cYieldRaw.Length == 0)
                _cYield = null;
            else if (CarcassYield.TryParseNumber(_cYieldRaw, out var v))
                _cYield = v;
            else
                return;
            _cNet = null;
            RecalcCarcass(writeQuantity: true, writePrice: true);
        }
    }

    /// <summary>Вес в продажу (нетто).</summary>
    public string CarcassNetText
    {
        get => _cNetRaw ?? ShowNumber(_cNet ?? _cResult?.Net, 3);
        set
        {
            _cNetRaw = (value ?? "").Trim();
            if (_cNetRaw.Length == 0)
                _cNet = null;
            else if (CarcassYield.TryParseNumber(_cNetRaw, out var v))
                _cNet = v;
            else
                return;
            _cYield = null;
            RecalcCarcass(writeQuantity: true, writePrice: true);
        }
    }

    /// <summary>Курсор ушёл из поля блока: сырой набранный текст заменяется нормальным показом (расчётные значения других полей уже обновлены).</summary>
    public void CarcassCommitText()
    {
        if (_cGrossRaw is null && _cPriceRaw is null && _cTotalRaw is null && _cYieldRaw is null && _cNetRaw is null)
            return;
        _cGrossRaw = _cPriceRaw = _cTotalRaw = _cYieldRaw = _cNetRaw = null;
        NotifyCarcass();
    }

    // ------------------------------------------------------------------ тексты блока

    /// <summary>«Куплено 300 кг → в продаже 225 кг (отходы 75 кг · 25 %) · себестоимость 533,33 сом/кг»; пусто — посчитать нельзя.</summary>
    public string CarcassResultText => _carcassOn && _cResult is { } r ? CarcassYieldTexts.ResultLine(r, _unit) : "";

    /// <summary>«К оплате поставщику: 120 000,00 сом» — точная сумма закупки; пусто — сумма не задана.</summary>
    public string CarcassPayText => _carcassOn && _cResult is { HasTotal: true } r ? CarcassYieldTexts.PayLine(r) : "";

    /// <summary>Красная пометка: сервер запишет сумму строки не ровно (цена до 4 знаков); пусто — сходится.</summary>
    public string CarcassLossText => _carcassOn && _cResult is { HasRoundingLoss: true } r ? CarcassYieldTexts.LossLine(r) : "";

    /// <summary>Красная причина, почему расчёт невозможен; пусто — всё в порядке.</summary>
    public string CarcassProblemText => CarcassYieldTexts.ProblemText(CarcassProblem);

    public bool CarcassHasProblem => CarcassProblem != CarcassYieldProblem.None;

    /// <summary>Примечание прихода «Брутто 300 кг, выход 75 %, отходы 75 кг» (comment документа прихода). Пусто — выхода нет или отходов нет. У строки из очереди — сохранённый текст.</summary>
    public string CarcassNote
    {
        get => _carcassOn ? (_cResult is { Waste: > 0 } r ? CarcassYieldTexts.Note(r, _unit) : "") : _carcassNoteStored;
        set => _carcassNoteStored = (value ?? "").Trim();
    }

    private void NotifyCarcass()
    {
        OnPropertyChanged(nameof(CarcassOn));
        OnPropertyChanged(nameof(CarcassGrossText));
        OnPropertyChanged(nameof(CarcassPriceText));
        OnPropertyChanged(nameof(CarcassTotalText));
        OnPropertyChanged(nameof(CarcassYieldText));
        OnPropertyChanged(nameof(CarcassNetText));
        OnPropertyChanged(nameof(CarcassResultText));
        OnPropertyChanged(nameof(CarcassPayText));
        OnPropertyChanged(nameof(CarcassLossText));
        OnPropertyChanged(nameof(CarcassProblemText));
        OnPropertyChanged(nameof(CarcassHasProblem));
        OnPropertyChanged(nameof(CarcassNote));
    }

    // ------------------------------------------------------------------ черновик приёмки (ReceivingLineVm.Draft.cs): состояние блока переживает перезапуск

    private void CarcassFromDraft(ReceivingDraftLine d)
    {
        _carcassNoteStored = d.CarcassNote ?? "";
        if (d.CarcassOn != true || _packMode)
            return;
        _carcassOn = true;
        _cGross = Math.Max(0, d.CarcassGross ?? 0);
        _cPrice = d.CarcassPrice;
        _cTotal = d.CarcassTotal;
        _cYield = d.CarcassYield;
        _cNet = d.CarcassNet;
        _cResult = CarcassYield.Calculate(new CarcassYieldInput(_cGross, _cPrice, _cTotal, _cYield, _cNet), out _cProblem); // только показ: строка уже такая, какой её оставили
    }
}
