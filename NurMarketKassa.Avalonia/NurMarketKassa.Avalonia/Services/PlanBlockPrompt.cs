using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// 2026-11-20 (ответ Умара 20.11, В.2, тариф «Мини»): понятное окно, когда сервер отказал по тарифу компании — 403 <c>plan_client_not_allowed</c> («Мини» работает только с телефона: касса на ПК, Android-касса и программа
/// владельца не пускаются; отказ приходит на входе, на обновлении входа и на любом запросе) или 403 <c>plan_limit</c> (лимит тарифа: сотрудник, вторая касса). Окно показывает текст сервера (<c>detail</c>), пояснение и кнопку
/// «Перейти на «Старт»» (окно «Подписка» владельца/админа, иначе страница компании в браузере). Окно стандартное (PosConfirmDialog, без своих полей) и показывается не чаще раза в 5 минут (PlanBlockMonitor);
/// продажи, смена и очередь не затрагиваются. Подписка одна на программу: <see cref="Install"/> вызывается при запуске.
/// </summary>
public static class PlanBlockPrompt
{
    private static int _installed;
    private static int _showing;

    /// <summary>Окно тарифа сейчас открыто (другие вопросы поверх него не показываются).</summary>
    public static bool IsShowing => Volatile.Read(ref _showing) != 0;

    /// <summary>Адрес личного кабинета компании (оплата и смена тарифа), если окна «Подписка» нет.</summary>
    internal const string AccountUrl = "https://app.nurcrm.kg/";

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;
        PlanBlockMonitor.Blocked += block => Dispatcher.UIThread.Post(() => _ = AskAsync(block), DispatcherPriority.Normal);
    }

    /// <summary>Название тарифа для кнопки: start → «Старт», standard → «Стандарт»; незнакомый код показывается как есть.</summary>
    public static string PlanName(string? code) => (code ?? "start").Trim().ToLowerInvariant() switch
    {
        "start" or "" => Tr.T("Старт", "Старт", "Start", "Başlangıç", "Start"),
        "standard" => Tr.T("Стандарт", "Стандарт", "Standard", "Standart", "Standart"),
        "mini" => Tr.T("Мини", "Мини", "Mini", "Mini", "Mini"),
        var other => other.Length == 0 ? other : char.ToUpperInvariant(other[0]) + other[1..],
    };

    public static string Title(PlanBlock block) => block.IsClientBlock
        ? Tr.T("Тариф не подходит для этой программы", "Тариф бул программага ылайыксыз", "The plan does not fit this program", "Tarife bu programa uygun değil", "Tarif bu dasturga mos kelmaydi")
        : Tr.T("Лимит тарифа", "Тариф чеги", "Plan limit", "Tarife sınırı", "Tarif chegarasi");

    /// <summary>Текст окна: ответ сервера (detail, уже на понятном языке) + пояснение, что данные не потеряны. Нет detail — свой текст по коду.</summary>
    public static string Message(PlanBlock block)
    {
        var upgrade = PlanName(block.UpgradePlan);
        var body = !string.IsNullOrWhiteSpace(block.Detail)
            ? block.Detail!.Trim()
            : block.IsClientBlock
                ? Tr.T($"Тариф «{PlanName(block.Plan)}» работает только с телефона. Касса на ПК и программа владельца с ним не работают. Чтобы пользоваться ими, смените тариф на «{upgrade}».",
                    $"«{PlanName(block.Plan)}» тарифи телефондон гана иштейт. ПК кассасы жана ээсинин программасы анда иштебейт. Аларды колдонуу үчүн тарифти «{upgrade}» деп алмаштырыңыз.",
                    $"The “{PlanName(block.Plan)}” plan works only from a phone. The PC till and the owner program do not work with it. To use them, switch the plan to “{upgrade}”.",
                    $"«{PlanName(block.Plan)}» tarifesi yalnızca telefondan çalışır. PC kasası ve sahip programı onunla çalışmaz. Kullanmak için tarifeyi «{upgrade}» olarak değiştirin.",
                    $"«{PlanName(block.Plan)}» tarifi faqat telefondan ishlaydi. Kompyuter kassasi va egasi dasturi u bilan ishlamaydi. Ulardan foydalanish uchun tarifni «{upgrade}» ga almashtiring.")
                : Tr.T($"Достигнут лимит тарифа «{PlanName(block.Plan)}». Чтобы добавить ещё, смените тариф на «{upgrade}».",
                    $"«{PlanName(block.Plan)}» тарифинин чегине жетти. Дагы кошуу үчүн тарифти «{upgrade}» деп алмаштырыңыз.",
                    $"The “{PlanName(block.Plan)}” plan limit is reached. To add more, switch the plan to “{upgrade}”.",
                    $"«{PlanName(block.Plan)}» tarifesi sınırına ulaşıldı. Daha fazla eklemek için tarifeyi «{upgrade}» olarak değiştirin.",
                    $"«{PlanName(block.Plan)}» tarifi chegasiga yetildi. Ko'proq qo'shish uchun tarifni «{upgrade}» ga almashtiring.");
        return body + "\n\n" + Tr.T("Ваши данные, продажи и очередь на этом компьютере сохранены.",
            "Бул компьютердеги маалыматтар, сатуулар жана кезек сакталды.",
            "Your data, sales and queue on this computer are saved.",
            "Bu bilgisayardaki verileriniz, satışlarınız ve kuyruğunuz saklandı.",
            "Bu kompyuterdagi ma'lumotlar, sotuvlar va navbat saqlangan.");
    }

    public static string ConfirmText(PlanBlock block) =>
        Tr.T($"Перейти на «{PlanName(block.UpgradePlan)}»", $"«{PlanName(block.UpgradePlan)}» тарифине өтүү", $"Switch to “{PlanName(block.UpgradePlan)}”", $"«{PlanName(block.UpgradePlan)}» tarifesine geç", $"«{PlanName(block.UpgradePlan)}» tarifiga o'tish");

    private static Window? Top() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.LastOrDefault(w => w.IsActive && w.IsVisible) ?? desktop.MainWindow
            : null;

    public static async Task AskAsync(PlanBlock block)
    {
        // Окно «Сессия истекла» открыто — тарифное покажем сразу после него (не две модалки сразу).
        if (SessionExpiredPrompt.IsShowing)
        {
            DispatcherTimer.RunOnce(() => _ = AskAsync(block), TimeSpan.FromSeconds(2));
            return;
        }
        if (Interlocked.Exchange(ref _showing, 1) != 0)
            return;
        try
        {
            var owner = Top();
            var open = await PosConfirmDialog.ShowModalAsync(owner, Title(block), Message(block), ConfirmText(block),
                Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish")).ConfigureAwait(true);
            if (open)
                OpenUpgrade(owner);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Окно «Тариф» не показано: {ex.Message}", "WARNING");
        }
        finally
        {
            Volatile.Write(ref _showing, 0);
        }
    }

    /// <summary>«Перейти на Старт»: окно «Подписка» (владелец/админ при включённой оплате), иначе страница компании в браузере.</summary>
    internal static void OpenUpgrade(Window? owner)
    {
        try
        {
            if (SubscriptionWindow.CanOffer())
            {
                SubscriptionWindow.ShowOrActivate(owner);
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Окно «Подписка» не открылось из окна «Тариф»: {ex.Message}", "WARNING");
        }
        PlatformLauncher.OpenUrl(AccountUrl);
    }
}
