using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-11-26 (требование владельца: «тариф «Своё» должен собираться из Маркетплейса»): полоса над витриной — «Ваш тариф: Стандарт · 3 600 сом/мес» и кнопка «Собрать тариф «Своё»».
/// Пока владелец отмечает карточки («Добавить в тариф» на плитках и страницах), полоса показывает «Что изменится: +Ремонт, −ИИ-помощник» и итог в месяц ОТ СЕРВЕРА
/// (users/company/tariff/quote/ с packs[], modules[], remove[]); касса цену не считает. Кнопка открывает тот же диалог «Тариф «Своё»» (TariffCustomWindow), что и «Аккаунт → Тариф»;
/// выбор у них общий — TariffDraft. Видна только владельцу (TariffDraft.CanBuild).</summary>
public sealed class TariffStrip : Border
{
    private readonly ITariffApi _api;
    private readonly TextBlock _plan = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _changes = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _total = new() { FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _open = new() { MinHeight = 40, Padding = new Thickness(16, 6), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _reset = new() { MinHeight = 40, Padding = new Thickness(14, 6), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private CancellationTokenSource? _cts;
    private int _version;
    private bool _attached;
    private bool _failed;
    private TariffQuote? _quote;

    /// <summary>Открыть диалог «Своё»; по умолчанию — окно TariffCustomWindow поверх окна Маркетплейса. Для проверок подменяется.</summary>
    public Func<Task>? OpenDialog { get; set; }

    public TariffStrip(ITariffApi api)
    {
        _api = api;
        Padding = new Thickness(14, 8);
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(1);
        Margin = new Thickness(0, 0, 0, 12);
        this.Bind(BackgroundProperty, this.GetResourceObservable("BrushPanelSoft"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("BrushBorder"));
        _plan.Bind(TextBlock.ForegroundProperty, _plan.GetResourceObservable("BrushText"));
        _changes.Bind(TextBlock.ForegroundProperty, _changes.GetResourceObservable("BrushTextSoft"));
        _total.Bind(TextBlock.ForegroundProperty, _total.GetResourceObservable("BrushText"));
        _open.Classes.Add("btn-primary");
        _reset.Classes.Add("btn-secondary");
        Avalonia.Automation.AutomationProperties.SetName(_open, "tariff-strip-open");
        Avalonia.Automation.AutomationProperties.SetName(_reset, "tariff-strip-reset");

        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _plan, _changes, _total } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _reset, _open } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(texts);
        grid.Children.Add(buttons);
        Child = grid;

        _open.Click += async (_, _) =>
        {
            // 2026-11-26: «Собрать тариф «Своё»» включает режим сборки («+» на плитках); «Оформить» (набор есть или режим уже включён) открывает диалог.
            if (!TariffDraft.BuildMode && !TariffDraft.HasChanges)
            {
                TariffDraft.EnterBuildMode();
                return;
            }
            await OpenAsync().ConfigureAwait(true);
        };
        _reset.Click += (_, _) =>
        {
            // 2026-11-26: в режиме сборки кнопка — «Отмена» (сброс набора и выход из режима); вне режима (набор остался от диалога) — «Сбросить».
            if (TariffDraft.BuildMode)
                TariffDraft.CancelBuildMode();
            else
                TariffDraft.Clear();
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = QuoteAsync();
        };
        Refresh();
    }

    // ───────────── для проверок ─────────────
    public string PlanText => _plan.Text ?? "";
    public string ChangesLine => _changes.IsVisible ? _changes.Text ?? "" : "";
    public string TotalText => _total.IsVisible ? _total.Text ?? "" : "";
    public Button OpenButton => _open;
    public Button ResetButton => _reset;
    public Task QuoteNowForTest() { _debounce.Stop(); return QuoteAsync(); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_attached)
            return;
        _attached = true;
        TariffDraft.Changed += OnDraftChanged;
        PublicModules.Changed += OnDraftChanged; // цены и названия пришли позже — перерисовать
        Refresh();
        if (TariffDraft.HasChanges)
            _debounce.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!_attached)
            return;
        _attached = false;
        TariffDraft.Changed -= OnDraftChanged;
        PublicModules.Changed -= OnDraftChanged;
        _debounce.Stop();
        _cts?.Cancel();
    }

    private void OnDraftChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            DraftChanged();
        else
            Dispatcher.UIThread.Post(DraftChanged);
    }

    private void DraftChanged()
    {
        // Набор поменялся — прежний расчёт к нему не относится.
        _version++;
        _quote = null;
        _failed = false;
        _cts?.Cancel();
        _debounce.Stop();
        Refresh();
        if (TariffDraft.HasChanges)
            _debounce.Start();
    }

    private static string M(decimal value)
    {
        var a = MoneyText.Amount(value);
        if (a.EndsWith(",00", StringComparison.Ordinal))
            a = a[..^3];
        return a + " " + MoneyText.Currency;
    }

    private static string PerMonth() => Tr.T("в месяц", "айына", "a month", "ayda", "oyiga");

    /// <summary>Текущий тариф: название от сервера и цена в месяц из каталога тарифов (public/tariffs; цену касса не придумывает — нет каталога, нет и цены).</summary>
    private string PlanLine()
    {
        var name = TariffGate.CurrentPlanName;
        if (string.IsNullOrWhiteSpace(name))
            name = UserPreferences.Instance.LastKnownPlanName;
        var catalog = TariffApi.CachedCatalog;
        var code = TariffCustomRules.ResolvePlanCode(catalog, name);
        var price = code is null ? null : catalog?.Plan(code)?.Price;
        var head = Tr.T("Ваш тариф: ", "Тарифиңиз: ", "Your plan: ", "Tarifeniz: ", "Tarifingiz: ");
        if (string.IsNullOrWhiteSpace(name))
            return head + "—";
        return head + name + (price is > 0 ? " · " + M(price.Value) + " " + PerMonth() : "");
    }

    private void Refresh()
    {
        _plan.Text = PlanLine();
        var any = TariffDraft.HasChanges;
        _changes.IsVisible = any;
        _changes.Text = any ? Tr.T("Что изменится: ", "Эмне өзгөрөт: ", "What changes: ", "Neler değişecek: ", "Nima o'zgaradi: ") + TariffDraft.ChangesText(4) : "";
        var mode = TariffDraft.BuildMode;
        // 2026-11-26: режим сборки — подсказка вместо «Что изменится», пока ничего не отмечено; «Отмена» всегда видна.
        if (mode && !any)
        {
            _changes.IsVisible = true;
            _changes.Text = Tr.T("Режим сборки: нажимайте «+» на плитках и «Добавить в тариф» на страницах функций", "Түзүү режими: функциялардын плиткаларындагы «+» жана беттериндеги «Тарифке кошуу» баскычын басыңыз",
                "Build mode: press “+” on the tiles and “Add to the plan” on the function pages", "Oluşturma modu: işlev kutularındaki «+» ve sayfalardaki «Tarifeye ekle»ye basın", "Yig'ish rejimi: funksiyalar plitkalaridagi «+» va sahifalaridagi «Tarifga qo'shish» tugmasini bosing");
        }
        _reset.IsVisible = any || mode;
        _reset.Content = mode
            ? Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")
            : Tr.T("Сбросить", "Тазалоо", "Reset", "Sıfırla", "Tozalash");
        _open.Content = any || mode
            ? Tr.T("Оформить тариф «Своё»", "«Өзүмдүкү» тарифин жазуу", "Arrange the “Custom” plan", "«Özel» tarifeyi oluştur", "«O'zimniki» tarifini rasmiylashtirish")
            : Tr.T("Собрать тариф «Своё»", "«Өзүмдүкү» тарифин түзүү", "Build the “Custom” plan", "«Özel» tarifeyi oluştur", "«O'zimniki» tarifini yig'ish");
        RenderTotal(any);
    }

    private void RenderTotal(bool any)
    {
        _total.IsVisible = any;
        if (!any)
            return;
        if (_failed)
            _total.Text = Tr.T("Не удалось рассчитать, повторите", "Эсептөө мүмкүн болбоду, кайталаңыз", "Could not calculate, please try again", "Hesaplanamadı, tekrar deneyin", "Hisoblab bo'lmadi, qayta urinib ko'ring");
        else if (_quote is { } q)
            _total.Text = Tr.T($"Итого в месяц: {M(q.TotalMonth ?? q.Base + q.Monthly)}", $"Айына бардыгы: {M(q.TotalMonth ?? q.Base + q.Monthly)}", $"Total a month: {M(q.TotalMonth ?? q.Base + q.Monthly)}",
                $"Aylık toplam: {M(q.TotalMonth ?? q.Base + q.Monthly)}", $"Oyiga jami: {M(q.TotalMonth ?? q.Base + q.Monthly)}");
        else
            _total.Text = Tr.T("Считаю…", "Эсептеп жатам…", "Calculating…", "Hesaplanıyor…", "Hisoblanmoqda…");
    }

    /// <summary>Цену набора считает сервер: тот же запрос, что в диалоге, с выбором из общего набора. Нет ответа — «Не удалось рассчитать», без цифры.</summary>
    private async Task QuoteAsync()
    {
        if (!TariffDraft.HasChanges)
            return;
        var version = _version;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var snap = CompanyModules.Snapshot;
            var quote = await _api.QuoteAsync(TariffDraft.AddedPacks, TariffDraft.EffectiveModules(), snap is { Profile.Length: > 0 } ? snap.Profile : null,
                TariffDraft.RemovedPacks, "month", cts.Token).ConfigureAwait(true);
            if (version != _version)
                return;
            _quote = quote;
            _failed = false;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (version != _version)
                return;
            PosLogger.Log($"Полоса тарифа: расчёт не получен: {ex.Message}", "WARNING");
            _quote = null;
            _failed = true;
        }
        RenderTotal(TariffDraft.HasChanges);
    }

    private async Task OpenAsync()
    {
        try
        {
            if (OpenDialog is { } custom)
            {
                await custom().ConfigureAwait(true);
                return;
            }

            var owner = TopLevel.GetTopLevel(this) as Window;
            await PosDialogHost.ShowAsync(new TariffCustomWindow(_api, TariffGate.CurrentPlanName), owner).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Окно «Своё» из Маркетплейса не открылось: {ex}", "WARNING");
        }
    }
}
