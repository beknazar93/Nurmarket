using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>Вид экрана для снимков: «Окно» — большое окно кассы/владельца (снимается в двух размерах 1024×640 и 1366×768),
/// «Диалог» — модальное окно по размеру содержимого (один размер — свой).</summary>
public enum ScreenKind { Window, Dialog }

/// <summary>Один экран для сквозных снимков и проверки обрезки текста. <see cref="SkipReason"/> — причина, по которой экран нельзя поднять без железа/сети
/// (тест помечается Skip и попадает в отчёт).</summary>
public sealed record ScreenDef(string Name, ScreenKind Kind, Func<Window> Create, string? SkipReason = null);

/// <summary>2026-11-06 (В14): каталог экранов кассы и программы владельца, которые поднимаются в Headless на пустых данных (хост служб — TestHost,
/// сервер — заглушка «нет связи», база — временная папка). Все надписи на экране приходят из настоящего приложения (Tr.T + словари строк).</summary>
public static class ScreenCatalog
{
    private static T Svc<T>() where T : notnull => TestHost.Services.GetRequiredService<T>();

    public static readonly IReadOnlyList<ScreenDef> All =
    [
        // --- большие окна
        new("main-kassa", ScreenKind.Window, () => Svc<MainWindow>()),
        new("warehouse", ScreenKind.Window, () => Svc<WarehouseWindow>()),
        new("purchases", ScreenKind.Window, () => new PurchasesWindow()),
        new("clients", ScreenKind.Window, () => Svc<ClientsWindow>()),
        new("finance", ScreenKind.Window, () => Svc<FinanceWindow>()),
        new("sales", ScreenKind.Window, () => Svc<SalesWindow>()),
        new("settings", ScreenKind.Window, () => Svc<PosSettingsWindow>()),
        new("marketplace", ScreenKind.Window, () => new MarketplaceWindow()),
        new("owner-shell", ScreenKind.Window, () => new OwnerShellWindow()),
        // 2026-11-07 (живая проверка 1.17.81): «Приём товара» (панель скана при 1024) — окно «Закупки» с открытым мастером.
        new("purchases-receive", ScreenKind.Window, () => { var w = new PurchasesWindow(); w.OpenReceive(null); return w; }),
        // 2026-11-08 (ответ Умара 08.11, п.39 и п.37): «Отзывы» и «Коллекции» витрины (разделы группы «Сайт»); сервер — заглушка «нет связи».
        new("site-reviews", ScreenKind.Window, () => new SiteReviewsWindow()),
        new("site-collections", ScreenKind.Window, () => new SiteCollectionsWindow()),
        // --- диалоги
        new("checkout", ScreenKind.Dialog, () => Svc<CheckoutDialog>()),
        new("open-shift", ScreenKind.Dialog, () => new OpenShiftDialog()),
        new("close-shift", ScreenKind.Dialog, () => new CloseShiftDialog()),
        new("return-sale", ScreenKind.Dialog, () => new ReturnSaleDialog()),
        new("pay-debt", ScreenKind.Dialog, () => new PayDebtDialog()),
        new("shift-details", ScreenKind.Dialog, () => new ShiftDetailsDialog()),
        new("sale-success", ScreenKind.Dialog, () => new SaleSuccessDialog(450, false)),
        new("confirm", ScreenKind.Dialog, () => new PosConfirmDialog(
            Tr.T("Закрыть смену", "Сменаны жабуу", "Close the shift", "Vardiyayı kapat", "Smenani yopish"),
            Tr.T("Закрыть смену и потерять чек?", "Сменаны жаап, чекти жоготосузбу?", "Close the shift and lose the receipt?", "Vardiyayı kapatıp fişi kaybedelim mi?", "Smenani yopib, chekni yo'qotamizmi?"),
            Tr.T("Закрыть смену", "Сменаны жабуу", "Close the shift", "Vardiyayı kapat", "Smenani yopish"), null, PosConfirmAccent.Danger)),
        // Тот же вопрос, что задаёт SessionExpiredPrompt.AskAsync (тексты скопированы из него).
        new("session-expired", ScreenKind.Dialog, () => new PosConfirmDialog(
            Tr.T("Сессия истекла", "Сессиянын мөөнөтү бүттү", "Session expired", "Oturum süresi doldu", "Sessiya muddati tugadi"),
            Tr.T("Срок входа истёк. Войдите заново.\n\nПродажи, смена и корзины сохранены — после входа всё отправится на сервер само.",
                "Кирүүнүн мөөнөтү бүттү. Кайра кириңиз.\n\nСатуулар, смена жана себеттер сакталды — кирген соң баары серверге өзү жөнөтүлөт.",
                "Your sign-in has expired. Please sign in again.\n\nSales, the shift and carts are saved — after you sign in everything is sent to the server automatically.",
                "Oturumun süresi doldu. Lütfen yeniden giriş yapın.\n\nSatışlar, vardiya ve sepetler kayıtlı — girişten sonra her şey sunucuya otomatik gönderilir.",
                "Kirish muddati tugadi. Qaytadan kiring.\n\nSotuvlar, smena va savatlar saqlangan — kirgandan keyin hammasi serverga o'zi yuboriladi."),
            Tr.T("Войти", "Кирүү", "Sign in", "Giriş yap", "Kirish"),
            Tr.T("Позже", "Кийинчерээк", "Later", "Daha sonra", "Keyinroq"))),
        new("product-edit", ScreenKind.Dialog, () => new ProductEditDialog()),
        new("barcode-print", ScreenKind.Dialog, () => new BarcodePrintWindow()),
        new("scale-shtrikh-rs200", ScreenKind.Dialog, () => new ShtrikhScaleSettingsWindow()),
        // 2026-11-07 (Б-F1): окно «Настройки весов Штрих РС-200» (Настройки → Весы → Штрих РС-200 → «Настройки весов…»).
        new("scale-shtrikhpc-settings", ScreenKind.Dialog, () => new ShtrikhPcSettingsWindow()),
        // 2026-11-07 (Б-A1): автосписок каталога в узкой панели (окно 1366 при масштабе 150 %) — цена не обрезается.
        new("catalog-list-narrow", ScreenKind.Dialog, () => CatalogListFixture.Create(470, 640).Window),
        // То же в широкой панели (окно ≥ 1000): штрихкод и остаток — отдельные колонки, заголовки над значениями.
        new("catalog-list-wide", ScreenKind.Dialog, () => CatalogListFixture.Create(1000, 460).Window),
        new("unsent-outbox", ScreenKind.Dialog, () => OutboxWindow.ShowOrActivate(null)),
    ];

    public static ScreenDef Get(string name) => All.Single(s => s.Name == name);

    /// <summary>Данные для xUnit: экран × тема × язык × размер (у диалогов размер пустой).</summary>
    public static IEnumerable<object[]> SnapshotMatrix()
    {
        foreach (var screen in All)
        foreach (var theme in Enum.GetValues<UiTheme>())
        foreach (var lang in UiShot.Languages)
        {
            if (screen.Kind == ScreenKind.Window)
            {
                foreach (var size in new[] { UiSize.Small, UiSize.Large })
                    yield return [screen.Name, theme, lang, size.ToString()];
            }
            else
                yield return [screen.Name, theme, lang, ""];
        }
    }

    public static UiSize? ParseSize(string size) =>
        string.IsNullOrEmpty(size) ? null : new UiSize(int.Parse(size.Split('x')[0]), int.Parse(size.Split('x')[1]));
}
