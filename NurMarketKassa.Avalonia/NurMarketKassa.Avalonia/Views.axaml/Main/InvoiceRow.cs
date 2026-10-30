using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-30, форма «Накладная прихода»: строка таблицы. Количество, наценка и цена реализации правятся на месте (в режиме правки);
/// наценка и цена реализации пересчитываются друг из друга. Чистые расчёты — в InvoiceMath, здесь только состояние строки и тексты ячеек.</summary>
internal sealed class InvoiceRow : INotifyPropertyChanged
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public int No { get; init; }
    public string LineId { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string Code { get; init; } = "";
    public string Barcode { get; set; } = "";
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    /// <summary>Сколько «живёт» в приходе при открытии формы (проведено − возвращено + доприходы).</summary>
    public double OrigQty { get; init; }
    public double ReturnedQty { get; init; }
    public double Purchase { get; init; }
    /// <summary>Закупка в приходе, на которой строка открыта (для «было» при отмене).</summary>
    public double OrigSale { get; init; }
    public bool SaleEstimated { get; init; }
    public bool IsExtra { get; init; }

    public EditLineInput? Input { get; set; }
    /// <summary>Остаток товара на складе (читается при проверке правки). null — ещё не читали.</summary>
    public double? Stock { get; set; }

    private double _qty;
    private double _sale;
    private double? _prev;
    private bool _isNew;
    private bool _error;
    private string? _errorText;
    private double? _min;

    /// <summary>Строку меняют руками: (строка, прежнее количество). Окно складывает это в стек отмены и пересчитывает итог.</summary>
    public event Action<InvoiceRow, double>? QtyEdited;
    public event Action<InvoiceRow>? PriceEdited;
    public event PropertyChangedEventHandler? PropertyChanged;

    public InvoiceRow(double qty, double sale)
    {
        _qty = qty;
        _sale = sale;
        OrigQty = qty;
        OrigSale = sale;
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void RaiseAll(params string[] names)
    {
        foreach (var n in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private static readonly string[] QtyDependents =
    {
        nameof(QtyText), nameof(SumText), nameof(Dim), nameof(Struck), nameof(StripeKey), nameof(WasText), nameof(Changed), nameof(Qty),
    };

    public double Qty
    {
        get => _qty;
        set
        {
            value = Math.Round(Math.Max(0, value), 3);
            if (Math.Abs(value - _qty) < 0.0005)
                return;
            var old = _qty;
            _qty = value;
            RaiseAll(QtyDependents);
            QtyEdited?.Invoke(this, old);
        }
    }

    /// <summary>Вернуть количество без записи в стек отмены (кнопка «Отменить правки», Ctrl+Z).</summary>
    public void SetQtySilently(double value)
    {
        _qty = Math.Round(Math.Max(0, value), 3);
        RaiseAll(QtyDependents);
    }

    public double Sale
    {
        get => _sale;
        private set
        {
            _sale = Math.Round(Math.Max(0, value), 2);
            RaiseAll(nameof(Sale), nameof(SaleText), nameof(MarkupText), nameof(ProfitText), nameof(StripeKey), nameof(NoteText), nameof(FlagKey));
        }
    }

    public double? Prev
    {
        get => _prev;
        set { _prev = value; RaiseAll(nameof(PurchaseMark), nameof(MarkKey), nameof(NoteText)); }
    }

    public bool IsNew
    {
        get => _isNew;
        set { _isNew = value; RaiseAll(nameof(NoteText)); }
    }

    public double? Min
    {
        get => _min;
        set { _min = value; RaiseAll(nameof(WasText)); }
    }

    public bool HasError
    {
        get => _error;
        set { _error = value; RaiseAll(nameof(HasError), nameof(ErrorText), nameof(NoteText)); }
    }

    public string? ErrorText
    {
        get => _errorText;
        set { _errorText = value; HasError = !string.IsNullOrEmpty(value); }
    }

    public InvoiceLineFacts Facts => new(_qty, Purchase, _sale, _prev, _isNew);

    public bool Changed => Math.Abs(_qty - OrigQty) > 0.0005;
    public bool Struck => Changed && _qty <= 0.0005;
    public bool Dim => _qty <= 0.0005;

    // ------------------------------------------------------------------ тексты ячеек

    private static string Num(double v) => v.ToString("0.###", Ru);
    public string NoText => No.ToString(Ru);
    public string QtyText
    {
        get => Num(_qty);
        set
        {
            if (TryParse(value, out var v))
                Qty = v;
            else
                RaiseAll(nameof(QtyText));
        }
    }

    public string PurchaseText => Purchase > 0 ? MoneyText.Som(Purchase) : "—";
    public string SumText => MoneyText.Som(ReceivingPricing.LineTotal(_qty, Purchase));

    public string MarkupText
    {
        get => InvoiceMath.MarkupFromSale(Purchase, _sale) is { } m ? (SaleEstimated && !PriceTouched ? "≈ " : "") + m.ToString("0.##", Ru) : "—";
        set
        {
            if (!TryParse((value ?? "").Replace("≈", "").Replace("%", ""), out var m) || Purchase <= 0)
            {
                RaiseAll(nameof(MarkupText));
                return;
            }
            PriceTouched = true;
            Sale = InvoiceMath.SaleFromMarkup(Purchase, m, ReceivingMarkupSettingsStore.Current.Rounding);
            PriceEdited?.Invoke(this);
        }
    }

    public string SaleText
    {
        get => _sale > 0 ? _sale.ToString("0.##", Ru) : "—";
        set
        {
            if (!TryParse(value, out var s))
            {
                RaiseAll(nameof(SaleText));
                return;
            }
            PriceTouched = true;
            Sale = s;
            PriceEdited?.Invoke(this);
        }
    }

    /// <summary>Наценку или цену реализации правили руками в этой форме (расчёт «а что если»: в карточку товара не пишется).</summary>
    public bool PriceTouched { get; private set; }

    public string ProfitText => InvoiceMath.ProfitPerUnit(Purchase, _sale) is var p && _sale > 0 && Purchase > 0 ? p.ToString("0.##", Ru) : "—";

    /// <summary>«↑ +12 %» рядом с закупкой, если она выросла против прошлой.</summary>
    public string PurchaseMark => InvoiceMath.RaisePercent(Facts) is var r && InvoiceMath.IsRaised(Facts) ? "↑ +" + r.ToString("0", Ru) + " %" : "";
    /// <summary>Ключ кисти маркера: рост больше 10 % — красным, иначе жёлтым.</summary>
    public string MarkKey => InvoiceMath.RaisePercent(Facts) > 10 ? "BrushDanger" : "BrushWarning";

    /// <summary>Полоса слева: ниже закупки — Danger, маржа &lt; 10 % — Warning, ошибка правки — Danger; иначе прозрачная.</summary>
    public string StripeKey => _error || InvoiceMath.IsBelowCost(Facts) ? "BrushDanger" : InvoiceMath.IsLowMargin(Facts) ? "BrushWarning" : "";
    public string FlagKey => InvoiceMath.IsBelowCost(Facts) ? "BrushDanger" : InvoiceMath.IsLowMargin(Facts) ? "BrushWarning" : "BrushText";

    /// <summary>«было 10 · мин. 7» под количеством в режиме правки.</summary>
    public string WasText => Changed
        ? Tr.T("было ", "болчу ", "was ", "önceki ", "avval ") + Num(OrigQty) + (_min is { } m && m > 0 ? Tr.T(" · мин. ", " · мин. ", " · min ", " · min ", " · min ") + Num(m) : "")
        : (_min is { } m2 && m2 > 0 ? Tr.T("мин. ", "мин. ", "min ", "min ", "min ") + Num(m2) : "");

    /// <summary>Бейджи примечания (через « · »): ниже закупки, маржа, новый, возврат, ошибка.</summary>
    public string NoteText
    {
        get
        {
            var parts = new List<string>();
            if (_error && !string.IsNullOrEmpty(_errorText))
                parts.Add(_errorText!);
            else if (InvoiceMath.IsBelowCost(Facts))
                parts.Add(Tr.T("ниже закупки", "сатып алуудан төмөн", "below cost", "maliyetin altında", "xariddan past"));
            else if (InvoiceMath.IsLowMargin(Facts) && InvoiceMath.MarkupFromSale(Purchase, _sale) is { } mm)
                parts.Add(Tr.T("маржа ", "маржа ", "margin ", "marj ", "marja ") + mm.ToString("0", Ru) + " %");
            if (_isNew)
                parts.Add(Tr.T("новый", "жаңы", "new", "yeni", "yangi"));
            if (_qty <= 0.0005 && !Changed)
                parts.Add(Tr.T("0 шт", "0 даана", "0 pcs", "0 adet", "0 dona"));
            if (ReturnedQty > 0.0005)
                parts.Add(Tr.T("возврат ", "кайтаруу ", "returned ", "iade ", "qaytarildi ") + Num(ReturnedQty));
            if (IsExtra)
                parts.Add(Tr.T("доприход", "кошумча", "addition", "ek giriş", "qo'shimcha"));
            return string.Join(" · ", parts);
        }
    }

    public bool NeedsAttention => InvoiceMath.NeedsAttention(Facts);

    public static bool TryParse(string? text, out double value) =>
        double.TryParse((text ?? "").Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace(',', '.').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;

    /// <summary>Вернуть цену реализации и наценку, как были при открытии (отмена расчёта).</summary>
    public void ResetPrice()
    {
        PriceTouched = false;
        Sale = OrigSale;
    }

    public void Refresh() => RaiseAll(nameof(NoteText), nameof(StripeKey), nameof(MarkupText), nameof(SaleText), nameof(ProfitText), nameof(PurchaseMark), nameof(WasText), nameof(FlagKey));
}
