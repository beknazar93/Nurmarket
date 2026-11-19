using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-13 (решение владельца по сравнению с Paloma365 и Rosta): «Проверка перед отправкой» — отмеченные товары одним списком: нет PLU или кода,
/// PLU больше таблицы весов, дубли, цена 0 или выше поля цены весов, товаров больше, чем вмещают весы, сумма выше потолка типа штрихкода,
/// символы названия, которых весы не печатают. У строк, где можно, кнопка «Исправить». Правила — ScalePreflight.cs (без окна); здесь — сбор данных строк,
/// окно со списком и исправления. Автоотправка (ScalesPluWindow.AutoSend.cs) тем же списком отсеивает товары с ошибками.</summary>
public partial class ScalesPluWindow
{
    /// <summary>Строки для проверки: отмеченные товары; номер — закреплённый или тот, что товар получит при отправке (курсив в колонке PLU).</summary>
    private List<ScalePreflightRow> PreflightRows() =>
        _allRows.Where(r => r.IsSelected)
            .Select(r => new ScalePreflightRow(
                r.Id, r.Name, (decimal)r.Price,
                int.TryParse((r.PluText ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var plu) && plu > 0 ? plu : null,
                long.TryParse((r.BarcodeCode ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : null,
                r.BarcodeCode))
            .ToList();

    /// <summary>Пределы марки: номер ячейки, код, поле цены, число товаров, потолок суммы этикетки. Чего без связи с весами не узнать (число знаков цены
    /// у Штрих-ПРИНТ, размер его таблицы) — не проверяется: это проверит сама отправка.</summary>
    private ScalePreflightContext PreflightContext()
    {
        var prefs = UserPreferences.Instance;
        if (IsShtrikhPc)
        {
            var pc = ShtrikhPcSettings.Current;
            return new ScalePreflightContext((int)Math.Min(int.MaxValue, ShtrikhPcImportWriter.MaxCode), pc.MaxProductCode, ScalePriceLimits.ShtrikhPcMaxUnits, Math.Clamp(pc.PriceDecimals, 0, 3), 0,
                0m, ScaleNameChars.HasUnprintable);
        }
        if (IsCas)
        {
            // 2026-11-13: CAS — номер PLU до 999999, код до 999999, цена до 999999 в наименьших единицах (предел меню весов), знаки цены — из настроек
            var cas = CasScaleSettings.Current;
            return new ScalePreflightContext(CasProtocol.MaxPluNumber, 999_999, CasProtocol.MaxPriceUnits, cas.PricePoint, 0, 0m, ScaleNameChars.HasUnprintable);
        }
        if (IsDahuaWire)
        {
            var decimals = DahuaPricePoint;
            var ceiling = 0m;
            if (IsRongtaLan && RongtaBarcodeFormat.Find(prefs.RongtaBarcodeType) is { } type)
                ceiling = RongtaBarcodeFormat.MaxAmountSom(type, decimals);
            return new ScalePreflightContext(DahuaTmProtocol.MaxPluNumber, 9_999_999, ScalePriceLimits.DahuaMaxUnits, decimals, DahuaTmProtocol.MaxPluNumber, ceiling, ScaleNameChars.HasUnprintable);
        }
        // Штрих-ПРИНТ: число знаков цены и размер таблицы читаются с весов при отправке; здесь — известное.
        return new ScalePreflightContext(MaxPlu, 999_999, long.MaxValue, 2, _shtrikhTableSize, 0m, ScaleNameChars.HasUnprintable);
    }

    private List<ScalePreflightIssue> BuildPreflightIssues() => ScalePreflight.Check(PreflightRows(), PreflightContext());

    /// <summary>Исправление одного замечания (или всего списка для «Товаров больше, чем вмещают весы»); что сделано — в журнал [SCALES].</summary>
    private void FixIssue(ScalePreflightIssue issue)
    {
        var profile = LabelScaleStore.Active;
        switch (issue.Kind)
        {
            case ScalePreflightKind.NoPlu:
            case ScalePreflightKind.PluTooLong:
            case ScalePreflightKind.DuplicatePlu:
                if (issue.ProductId is null || !KassaNumbering || UseCatalogPlu)
                    break; // PLU из карточки — правится в карточке товара
                profile.PluNumbers.Remove(issue.ProductId);
                PinNumbersForSend(new[] { issue.ProductId });
                break;
            case ScalePreflightKind.NoCode:
            case ScalePreflightKind.CodeTooLong:
                if (issue.ProductId is null)
                    break;
                profile.BarcodeCodes.Remove(issue.ProductId);
                if (_allRows.FirstOrDefault(r => r.Id == issue.ProductId) is { } row)
                    row.BarcodeCode = ""; // пустой «Код в ШК» — отправка выдаст свободный код (CheckSendBatch)
                break;
            case ScalePreflightKind.OverLimit:
            {
                var limit = PreflightContext().MaxProducts;
                if (limit > 0)
                    foreach (var extra in _allRows.Where(r => r.IsSelected).Skip(limit))
                        extra.IsSelected = false;
                break;
            }
            case ScalePreflightKind.AmountOverCeiling:
                UserPreferences.Instance.RongtaBarcodeType = RongtaBarcodeFormat.RecommendedTypeNumber;
                UserPreferences.Instance.RongtaServerBarcodeType = RongtaBarcodeFormat.RecommendedTypeNumber;
                UserPreferences.Instance.SaveToDisk();
                break;
        }
        LabelScaleStore.Save();
        RefreshPluNumbers();
        UpdateBarcodeExample();
        PosLogger.Log($"Весы «{profile.Name}»: проверка перед отправкой — исправлено «{ScalePreflight.Title(issue.Kind)}»" + (issue.Name.Length > 0 ? $" у «{issue.Name}»" : "") + ".", "SCALES");
    }

    private async void Preflight_Click(object? sender, RoutedEventArgs e)
    {
        var window = new ScalesPreflightWindow(BuildPreflightIssues, FixIssue);
        await window.ShowDialog(this).ConfigureAwait(true);
        StatusText.Text = ScalePreflight.Summary(BuildPreflightIssues());
    }
}

/// <summary>Окно «Проверка перед отправкой»: список замечаний (товар, что не так, «Исправить»), итог сверху, «Проверить заново» и «Закрыть».
/// Оболочка и кнопки — общие (ModalShell, SectionCards).</summary>
public sealed class ScalesPreflightWindow : Window
{
    private readonly Func<List<ScalePreflightIssue>> _check;
    private readonly Action<ScalePreflightIssue> _fix;
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };

    public ScalesPreflightWindow(Func<List<ScalePreflightIssue>> check, Action<ScalePreflightIssue> fix)
    {
        _check = check;
        _fix = fix;
        Title = Tr.T("Проверка перед отправкой", "Жөнөтүүдөн мурун текшерүү", "Check before sending", "Göndermeden önce kontrol", "Yuborishdan oldin tekshirish");
        SectionCards.AsModal(this, 912);

        _summary.Bind(TextBlock.ForegroundProperty, _summary.GetResourceObservable("BrushText"));
        var scroll = new ScrollViewer
        {
            MaxHeight = 360,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _list,
        };
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(_summary);
        body.Children.Add(scroll);

        var again = SectionCards.Secondary(Tr.T("Проверить заново", "Кайра текшерүү", "Check again", "Yeniden kontrol et", "Qayta tekshirish"));
        again.Click += (_, _) => Rebuild();
        var close = SectionCards.Primary(Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), isDefault: true);
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        Content = SectionCards.Shell(Title ?? "", null, body, SectionCards.Buttons(again, close));
        Rebuild();
    }

    private void Rebuild()
    {
        var issues = _check();
        _summary.Text = ScalePreflight.Summary(issues);
        _list.Children.Clear();
        foreach (var issue in issues.OrderByDescending(i => i.Severity).ThenBy(i => i.Kind).Take(300))
            _list.Children.Add(Card(issue));
        if (issues.Count > 300)
            _list.Children.Add(new TextBlock { Text = $"… +{issues.Count - 300}" });
    }

    private Control Card(ScalePreflightIssue issue)
    {
        var mark = issue.Severity == ScalePreflightSeverity.Error ? "✗ " : "⚠ ";
        var text = new StackPanel { Spacing = 2 };
        var head = new TextBlock { Text = mark + ScalePreflight.Title(issue.Kind) + (issue.Name.Length > 0 ? " — «" + issue.Name + "»" : ""), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        head.Bind(TextBlock.ForegroundProperty, head.GetResourceObservable(issue.Severity == ScalePreflightSeverity.Error ? "BrushDanger" : "BrushWarning"));
        var detail = new TextBlock { Text = ScalePreflight.Describe(issue), TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        detail.Bind(TextBlock.ForegroundProperty, detail.GetResourceObservable("BrushTextSoft"));
        text.Children.Add(head);
        text.Children.Add(detail);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(text);
        if (issue.CanFix)
        {
            var fix = SectionCards.Secondary(Tr.T("Исправить", "Оңдоо", "Fix", "Düzelt", "Tuzatish"));
            fix.VerticalAlignment = VerticalAlignment.Center;
            fix.Margin = new Thickness(12, 0, 0, 0);
            fix.Click += (_, _) =>
            {
                _fix(issue);
                Rebuild();
            };
            Grid.SetColumn(fix, 1);
            grid.Children.Add(fix);
        }
        var card = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(12, 8), Child = grid };
        card.Bind(Border.CornerRadiusProperty, card.GetResourceObservable("NurRadiusCard")); // 2026-11-13 (Д-8): радиус из общей шкалы
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("BrushBorder"));
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("BrushPanel"));
        return card;
    }
}
