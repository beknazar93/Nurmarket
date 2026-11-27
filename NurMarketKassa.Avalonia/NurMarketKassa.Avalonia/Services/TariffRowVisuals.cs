using System;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-26 («Своё» собирается из Маркетплейса): значок и цвет пакета тарифа в строке диалога «Тариф «Своё»» — те же, что у его карточки в Маркетплейсе
// (MarketplaceContent.Looks / Features: «pack:*» и отдельные карточки весов, Telegram-бота, итогов смены, выгрузки аналитики). Коды — серверные (GET public/tariffs/ packs[].code).
// Правило то же, что у модулей: новый пакет сервера = новая строка здесь; без неё строка получает запасной значок, а тест TariffRowVisualsTests красный.
public static class TariffRowVisuals
{
    public static readonly string[] KnownPackCodes =
        ["ai", "clients", "sales_analytics", "restock", "salary", "debts", "service", "scales", "telegram_bot", "shift_analytics", "analytics_export"];

    public static bool HasPack(string code) => Array.IndexOf(KnownPackCodes, code.ToLowerInvariant()) >= 0;

    public static (string Icon, string Color) Pack(string code) => code.ToLowerInvariant() switch
    {
        "ai" => ("robot-outline", "#7C3AED"),
        "clients" => ("account-group-outline", "#DB2777"),
        "sales_analytics" => ("chart-line", "#2563EB"),
        "restock" => ("package-variant-closed", "#D97706"),
        "salary" => ("cash-multiple", "#16A34A"),
        "debts" => ("book-account-outline", "#DC2626"),
        "service" => ("lifebuoy", "#0891B2"),
        "scales" => ("scale", "#0284C7"),
        "telegram_bot" => ("send-outline", "#0EA5E9"),
        "shift_analytics" => ("receipt-text-outline", "#059669"),
        "analytics_export" => ("file-export-outline", "#65A30D"),
        _ => (MarketplaceModuleContent.FallbackIcon, "#64748B"),
    };
}
