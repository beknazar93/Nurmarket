using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Fiscal;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-14 (П0-1, ответ Умара 13.11 вечер): карточка «Фискальный чек» под чеком или возвратом — статус «Пробит / Не пробит / Ошибка / Ожидает», ФД, ФМ, РН ККМ, фискальный признак,
/// время, смена ККМ, ссылка проверки (QR) с кнопками «Открыть» и «Копировать». У пробитого (accepted) чека — замок: «Чек пробит в ККМ, изменить нельзя — оформите возврат».
/// Карточка видна ТОЛЬКО когда сервер знает фискализацию и режим не «выключено» (<see cref="FiscalSettings.ShowsFiscalUi"/>) — иначе экран остаётся как сегодня.
/// Ничего не шлёт на сервер и в ККМ — только показывает данные чека (поля fiscal_* из ответа сервера). Печать реквизитов в чеке — отдельно (<see cref="FiscalReceiptTemplate"/>, выключена).</summary>
public sealed class FiscalRequisitesCard : Border
{
    private readonly TextBlock _title = new() { Name = "FiscalTitle", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _badge = new() { Name = "FiscalStatusBadge", VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 2) };
    private readonly TextBlock _badgeText = new() { Name = "FiscalStatusText", FontWeight = FontWeight.SemiBold };
    private readonly StackPanel _rows = new() { Name = "FiscalRows", Spacing = 2 };
    private readonly TextBlock _error = new() { Name = "FiscalErrorText", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel _qrRow = new() { Name = "FiscalQrRow", Spacing = 6, IsVisible = false };
    private readonly Button _open;
    private readonly Button _copy;
    private readonly TextBlock _qrUrl = new() { Name = "FiscalQrUrlText", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _lock = new() { Name = "FiscalLockText", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private string? _qr;

    /// <summary>Только для тестов и снимков: показывать карточку независимо от режима компании.</summary>
    public bool ForceVisible { get; set; }

    protected override Type StyleKeyOverride => typeof(Border);

    public FiscalRequisitesCard()
    {
        Name = "FiscalCard";
        IsVisible = false;
        Padding = new Thickness(12);
        BorderThickness = new Thickness(1);
        Margin = new Thickness(0, 8, 0, 0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Bind(BackgroundProperty, this.GetResourceObservable("BrushPanelSoft"));
        Bind(BorderBrushProperty, this.GetResourceObservable("BrushBorder"));
        Bind(CornerRadiusProperty, this.GetResourceObservable("NurRadiusCard"));

        _title.Bind(TextBlock.FontSizeProperty, _title.GetResourceObservable("NurFontBody"));
        _title.Bind(TextBlock.ForegroundProperty, _title.GetResourceObservable("BrushText"));
        _badge.Bind(Border.CornerRadiusProperty, _badge.GetResourceObservable("NurRadiusSmall"));
        _badgeText.Bind(TextBlock.FontSizeProperty, _badgeText.GetResourceObservable("NurFontCaption"));
        _badge.Child = _badgeText;
        _error.Bind(TextBlock.FontSizeProperty, _error.GetResourceObservable("NurFontCaption"));
        _error.Bind(TextBlock.ForegroundProperty, _error.GetResourceObservable("BrushDanger"));
        _qrUrl.Bind(TextBlock.ForegroundProperty, _qrUrl.GetResourceObservable("BrushTextSoft"));
        _lock.Bind(TextBlock.FontSizeProperty, _lock.GetResourceObservable("NurFontCaption"));
        _lock.Bind(TextBlock.ForegroundProperty, _lock.GetResourceObservable("BrushText"));

        _open = Make("FiscalQrOpenButton", "nur-secondary",
            Tr.T("Открыть", "Ачуу", "Open", "Aç", "Ochish"));
        _copy = Make("FiscalQrCopyButton", "nur-ghost",
            Tr.T("Копировать", "Көчүрүү", "Copy", "Kopyala", "Nusxalash"));
        _open.Click += (_, _) => OpenQr();
        _copy.Click += async (_, _) => await CopyQrAsync().ConfigureAwait(true);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        _open.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(_open);
        buttons.Children.Add(_copy);
        _qrRow.Children.Add(_qrUrl);
        _qrRow.Children.Add(buttons);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(_title);
        Grid.SetColumn(_badge, 1);
        header.Children.Add(_badge);
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(header);
        stack.Children.Add(_rows);
        stack.Children.Add(_error);
        stack.Children.Add(_qrRow);
        stack.Children.Add(_lock);
        Child = stack;
    }

    private static Button Make(string name, string cls, string text)
    {
        var b = new Button { Name = name, Content = text, MinHeight = 44, Padding = new Thickness(14, 0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        b.Classes.Add(cls);
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    /// <summary>Показать реквизиты. <c>null</c> или «none» — «Не пробит» (старый чек или чек, который касса ещё не пробила).</summary>
    public void Show(FiscalRequisites? requisites, FiscalDocKind kind)
    {
        var r = requisites ?? FiscalRequisites.None;
        IsVisible = ForceVisible || FiscalSettings.ShowsFiscalUi;
        _title.Text = kind == FiscalDocKind.Return
            ? Tr.T("Фискальный возврат (ККМ)", "Фискалдык кайтаруу (ККМ)", "Fiscal return (cash register)", "Mali iade (ÖKC)", "Fiskal qaytarish (KKM)")
            : Tr.T("Фискальный чек (ККМ)", "Фискалдык чек (ККМ)", "Fiscal receipt (cash register)", "Mali fiş (ÖKC)", "Fiskal chek (KKM)");
        _badgeText.Text = FiscalStatusNames.Label(r.Status);
        SetBadgeColors(r.Status);

        _rows.Children.Clear();
        AddRow("FiscalDocText", Tr.T("ФД (фискальный документ)", "ФД (фискалдык документ)", "FD (fiscal document)", "FB (mali belge)", "FH (fiskal hujjat)"), r.DocNumber);
        AddRow("FiscalMemoryText", Tr.T("ФМ (фискальный модуль)", "ФМ (фискалдык модуль)", "FM (fiscal memory)", "FM (mali bellek)", "FM (fiskal modul)"), r.MemoryNumber);
        AddRow("FiscalRegText", Tr.T("РН ККМ", "ККМ РН", "KKM reg. no.", "ÖKC sicil no", "KKM RN"), r.KkmRegNumber);
        AddRow("FiscalSignText", Tr.T("Фискальный признак", "Фискалдык белги", "Fiscal sign", "Mali işaret", "Fiskal belgi"), r.Sign);
        AddRow("FiscalAtText", Tr.T("Время в ККМ", "ККМдеги убакыт", "Cash register time", "ÖKC saati", "KKMdagi vaqt"),
            r.At?.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));
        AddRow("FiscalShiftText", Tr.T("Смена ККМ", "ККМ сменасы", "Cash register shift", "ÖKC vardiyası", "KKM smenasi"), r.KkmShiftId);
        if (_rows.Children.Count == 0 && r.Status == FiscalStatus.None)
            _rows.Children.Add(Soft(Tr.T("Фискальный чек не пробит.", "Фискалдык чек бектилген эмес.", "No fiscal receipt has been issued.", "Mali fiş kesilmedi.", "Fiskal chek urilmagan.")));

        var hasError = !string.IsNullOrWhiteSpace(r.Error);
        _error.IsVisible = hasError;
        _error.Text = hasError ? Tr.T("Ошибка ККМ: ", "ККМ катасы: ", "Cash register error: ", "ÖKC hatası: ", "KKM xatosi: ") + r.Error!.Trim() : "";

        _qr = string.IsNullOrWhiteSpace(r.QrUrl) ? null : r.QrUrl!.Trim();
        _qrRow.IsVisible = _qr is not null;
        _qrUrl.Text = _qr ?? "";
        _open.IsEnabled = _qr is not null && PlatformLauncher.IsLaunchableUrl(_qr);

        _lock.IsVisible = r.IsLocked;
        _lock.Text = r.IsLocked ? (kind == FiscalDocKind.Return ? FiscalErrors.LockedReturnText : FiscalErrors.LockedSaleText) : "";
    }

    private const string PopupTag = "fiscal-popup";

    /// <summary>Поставить в панель (подвал всплывающей карточки чека) карточки: чека и возвратов этого чека. Прежние карточки (от другого чека) убираются. Без включённой фискализации
    /// карточки скрыты и места не занимают. <paramref name="index"/> — место вставки в панели.</summary>
    public static void Place(Panel? host, int index, FiscalRequisites? sale, IEnumerable<FiscalRequisites>? returns = null, bool force = false)
    {
        if (host is null)
            return;
        foreach (var old in host.Children.OfType<FiscalRequisitesCard>().Where(c => Equals(c.Tag, PopupTag)).ToList())
            host.Children.Remove(old);
        var at = Math.Min(Math.Max(index, 0), host.Children.Count);
        void Add(FiscalRequisites? r, FiscalDocKind kind)
        {
            var card = new FiscalRequisitesCard { Tag = PopupTag, ForceVisible = force };
            card.Show(r, kind);
            host.Children.Insert(at++, card);
        }
        Add(sale, FiscalDocKind.Sale);
        foreach (var r in returns ?? Enumerable.Empty<FiscalRequisites>())
            Add(r, FiscalDocKind.Return);
    }

    /// <summary>Скрыть карточку (чек не выбран).</summary>
    public void Clear() => IsVisible = false;

    /// <summary>Ссылка проверки, которую открывает кнопка «Открыть» (для тестов).</summary>
    public string? QrUrl => _qr;

    private void SetBadgeColors(FiscalStatus status)
    {
        var (bg, fg) = status switch
        {
            FiscalStatus.Accepted => ("BrushSuccessSoft", "BrushSuccess"),
            FiscalStatus.Failed => ("BrushDangerSoft", "BrushDanger"),
            FiscalStatus.Pending => ("BrushWarningSoft", "BrushText"),
            _ => ("BrushPanel", "BrushTextSoft"),
        };
        _badge.Bind(Border.BackgroundProperty, _badge.GetResourceObservable(bg));
        _badgeText.Bind(TextBlock.ForegroundProperty, _badgeText.GetResourceObservable(fg));
    }

    private void AddRow(string name, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var l = Soft(label);
        l.Width = 190;
        l.Margin = new Thickness(0, 0, 10, 0);
        var v = new SelectableTextBlock { Name = name, Text = value!.Trim(), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        v.Bind(TextBlock.FontSizeProperty, v.GetResourceObservable("NurFontBody"));
        v.Bind(TextBlock.ForegroundProperty, v.GetResourceObservable("BrushText"));
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);
        _rows.Children.Add(grid);
    }

    private static TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.FontSizeProperty, t.GetResourceObservable("NurFontCaption"));
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private void OpenQr()
    {
        if (_qr is null || PlatformLauncher.OpenUrl(_qr))
            return;
        ShowHint(Tr.T("Не удалось открыть ссылку — скопируйте её.", "Шилтемени ачуу мүмкүн болгон жок — көчүрүп алыңыз.", "Could not open the link — copy it instead.",
            "Bağlantı açılamadı — kopyalayın.", "Havolani ochib bo'lmadi — nusxa oling."));
    }

    private async Task CopyQrAsync()
    {
        if (_qr is null)
            return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(_qr).ConfigureAwait(true);
                ShowHint(Tr.T("Ссылка скопирована.", "Шилтеме көчүрүлдү.", "Link copied.", "Bağlantı kopyalandı.", "Havola nusxalandi."));
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Фискальная карточка: ссылка не скопирована ({ex.GetType().Name}).", "WARNING");
        }
        ShowHint(Tr.T("Не удалось скопировать ссылку.", "Шилтемени көчүрүү мүмкүн болгон жок.", "Could not copy the link.", "Bağlantı kopyalanamadı.", "Havolani nusxalab bo'lmadi."));
    }

    /// <summary>Короткая подсказка под кнопками (в тексте ошибки карточки не пишем — это не ошибка ККМ).</summary>
    private void ShowHint(string text)
    {
        _qrUrl.Text = text;
        var url = _qr;
        Avalonia.Threading.DispatcherTimer.RunOnce(() => { if (_qr == url) _qrUrl.Text = url ?? ""; }, TimeSpan.FromSeconds(3));
    }
}
