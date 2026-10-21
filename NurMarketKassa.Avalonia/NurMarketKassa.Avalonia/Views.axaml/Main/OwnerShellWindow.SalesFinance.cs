using System;
using System.Linq;
using Avalonia.Controls;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-18, решение владельца (аудит, правило 8 «Финансы ≈ Продажи»): вместо пунктов «Продажи» (группа
/// «Продажи») и «Финансы» (группа «Отчёты») — один пункт «Продажи и финансы» в группе «Продажи».
///
/// Что внутри — по правам и «Разделам меню», как было у каждого окна:
/// • часть «Продажи» — право «Продажи» (ViewSales) и не скрыт ключ «sales»;
/// • часть «Финансы» — право «Аналитика» (ViewAnalytics) и не скрыт ключ «finance».
/// Обе — окно «Финансов» с встроенными «Продажами» (FinanceWindow.AsSalesAndFinance): вкладки «Чеки», «Возвраты»,
/// «История», «Смены», «ABC». Только одна — прежнее окно этой части без изменений (и название пункта — её: «Продажи»
/// или «Финансы»). Ни одной — пункта нет. Тариф — как у обоих: не на «Старте» (TariffGate.CanUseSalesAnalytics).
/// «Аналитика» — по-прежнему отдельный пункт «Отчётов» (другое окно, не повтор).</summary>
public partial class OwnerShellWindow
{
    /// <summary>Ключ пункта меню и раздела. Старые ключи «sales» и «finance» остаются ключами частей в «Разделах меню».</summary>
    private const string SalesFinanceKey = "salesfinance";

    /// <summary>2026-10-18, ревью: служба прав недоступна — «права нет» (как Authorize), а не «есть».</summary>
    private static bool HasPermissionSafe(string permission)
    {
        try
        {
            return App.GetRequiredService<IPermissionService>().HasPermission(permission);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: права для «Продаж и финансов» не прочитаны ({ex.Message}) — часть скрыта.", "WARNING");
            return false;
        }
    }

    /// <summary>Какие части раздела доступны (право и «Разделы меню»).</summary>
    private static (bool Sales, bool Finance) SalesFinanceParts() =>
        (HasPermissionSafe(PosPermissions.ViewSales) && !SectionVisibility.IsHidden("sales"),
         HasPermissionSafe(PosPermissions.ViewAnalytics) && !SectionVisibility.IsHidden("finance"));

    private static string SalesFinanceNavTitle((bool Sales, bool Finance) parts) => parts switch
    {
        (true, true) => FinanceWindow.SalesAndFinanceTitle,
        (true, false) => Tr.T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar"),
        _ => Tr.T("Финансы", "Каржы", "Finance", "Finans", "Moliya"),
    };

    /// <summary>Открыть раздел; tab — checks, returns, history, shifts, abc (null — какая была).</summary>
    private void OpenSalesFinance(string? tab = null)
    {
        if (!TariffGate.CanUseSalesAnalytics)
            return;
        var parts = SalesFinanceParts();
        if (!parts.Sales && !parts.Finance)
        {
            // Пункта в меню в этом случае нет; сюда ведут «Все продажи» сводки и Нур Советник — прежнее сообщение
            // «Недостаточно прав» с подсказкой, какую галочку включить на сайте.
            Authorize(SectionVisibility.IsHidden("sales") ? PosPermissions.ViewAnalytics : PosPermissions.ViewSales);
            return;
        }

        var variant = parts.Sales && parts.Finance ? "both" : parts.Sales ? "sales" : "finance";
        // Права или «Разделы меню» поменялись, пока раздел был открыт раньше, — открываем заново в новом составе.
        var existing = _sections.FirstOrDefault(s => s.Key == SalesFinanceKey);
        if (existing != null && !Equals(existing.Window.Tag, variant))
        {
            _sections.Remove(existing);
            existing.Window.Close();
        }

        OpenSection(SalesFinanceKey, () =>
        {
            Window window = variant switch
            {
                "both" => App.GetRequiredService<FinanceWindow>().AsSalesAndFinance(),
                "sales" => App.GetRequiredService<SalesWindow>(),
                _ => App.GetRequiredService<FinanceWindow>(),
            };
            window.Tag = variant;
            return window;
        });

        if (tab != null && _sections.FirstOrDefault(s => s.Key == SalesFinanceKey)?.Window is FinanceWindow finance)
            finance.SelectSalesFinanceTab(tab);
    }

    /// <summary>2026-10-19, этап О10: кнопки «Все продажи» и «Долги клиентов» раздела «Аналитика» — те же переходы, что у
    /// «Все продажи» сводки и пункта меню «Долги клиентов» (права и тариф проверяются там же).</summary>
    public void OpenAllSales() => AllSales_Click(null, new Avalonia.Interactivity.RoutedEventArgs());

    public void OpenDebtsSection() => OpenDebts();

    /// <summary>Вкладка по словам фразы Нур Советника («открой смены», «покажи возвраты»…); иначе — «Чеки».</summary>
    private static string SalesFinanceTabFromWords(string phrase)
    {
        var p = phrase.ToLowerInvariant();
        if (p.Contains("смен") || p.Contains("shift") || p.Contains("vardiya") || p.Contains("smena"))
            return "shifts";
        if (p.Contains("возврат") || p.Contains("кайтар") || p.Contains("return") || p.Contains("iade") || p.Contains("qaytar"))
            return "returns";
        if (p.Contains("истори") || p.Contains("тарых") || p.Contains("history") || p.Contains("geçmiş") || p.Contains("tarix"))
            return "history";
        if (p.Contains("abc") || p.Contains("авс"))
            return "abc";
        return "checks";
    }
}
