using System;
using System.Collections.Generic;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-10-23, ТЗ «Маркетплейс — редизайн и медиа» (П-1): контент страницы «подробно» и витрины по Id элементов каталога —
// группа, значок, строка пользы, чипы, слайды, анимация, выгоды, шаги, «для кого», условия цены. Только данные и тексты (Tr.T на 5 языках);
// логика активации, цены и условия открытия остаются в LocalMarketplaceCatalogSource. Тексты — только то, что проверено по коду
// (источники — в ТЗ, раздел 5). Функции — MarketplaceContent.Features.cs, пакеты/виды/темы — MarketplaceContent.Looks.cs.
internal static partial class MarketplaceContent
{
    /// <summary>Дополнить собранные элементы каталога: группа витрины и контент страницы «подробно». Вызывается в конце GetItems().</summary>
    internal static void Enrich(IList<MarketplaceItem> items)
    {
        foreach (var item in items)
        {
            item.Group = GroupOf(item);
            try
            {
                Fill(item);
            }
            catch (Exception ex)
            {
                // Контент — украшение страницы: сбой в одном элементе не должен ронять весь каталог.
                PosLogger.Log($"Маркетплейс: контент «{item.Id}» не собрался: {ex.Message}", "WARNING");
            }
        }
    }

    // Группа по Kind/Id (ТЗ 5.0). Функция, которой нет в таблице, остаётся в «Продажах» (значение по умолчанию).
    private static MarketplaceGroup GroupOf(MarketplaceItem item)
    {
        switch (item.Kind)
        {
            case MarketplaceKind.Theme:
                return MarketplaceGroup.Themes;
            case MarketplaceKind.Layout:
                return MarketplaceGroup.Layouts;
            case MarketplaceKind.Service:
                return MarketplaceGroup.Packs;
        }

        return item.Id switch
        {
            "sms" => MarketplaceGroup.Soon,
            "timesheet" or "export" or "telegram" => MarketplaceGroup.Owner,
            "scales" or "analytics" or "pricetag" or "bulktag" or "labeleditor" => MarketplaceGroup.Warehouse,
            _ => MarketplaceGroup.Sales, // loyalty, shiftstats, language
        };
    }

    private static void Fill(MarketplaceItem item)
    {
        switch (item.Id)
        {
            case "loyalty": Loyalty(item); return;
            case "timesheet": Timesheet(item); return;
            case "scales": Scales(item); return;
            case "analytics": Analytics(item); return;
            case "pricetag": PriceTag(item); return;
            case "bulktag": BulkTag(item); return;
            case "labeleditor": LabelEditor(item); return;
            case "export": Export(item); return;
            case "telegram": Telegram(item); return;
            case "shiftstats": ShiftStats(item); return;
            case "language": Language(item); return;
            case "sms": Sms(item); return;
            case "customerdisplay": CustomerDisplay(item); return;
            case "themeeditor": ThemeEditor(item); return;
        }

        if (item.Id.StartsWith("pack:", StringComparison.Ordinal))
            Pack(item);
        else if (item.Id.StartsWith("layout:", StringComparison.Ordinal))
            Layout(item);
        else if (item.Id.StartsWith("theme:", StringComparison.Ordinal))
            Theme(item);
        else if (item.Id.StartsWith("sitetheme:", StringComparison.Ordinal))
            SiteTheme(item); // 2026-10-27: темы сайта — MarketplaceSiteThemes.cs
    }

    // ------------------------------------------------------------------ общее

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static MarketplaceBenefit B(string iconKey, string title, string text) => new(iconKey, title, text);

    private static MarketplaceSlide S(int number, string caption) => new("slide-" + number, caption);

    private static MarketplaceDemo Demo(string caption) => new("demo", caption);

    /// <summary>Цена в скобках для шага «Активируйте…»; у функции без суммы в коде («Платно») — пусто, цену не придумываем.</summary>
    private static string PriceInBrackets(MarketplaceItem item) =>
        item.Price?.Amount is null ? "" : " (" + item.Price.Label + ")";

    /// <summary>Первый шаг «Как включить» для функций с разовой активацией ключом.</summary>
    private static string ActivateStep(MarketplaceItem item)
    {
        var p = PriceInBrackets(item);
        // 2026-10-24 (приёмка, ПР-3, С-8): шаг не должен звать платить за уже купленное.
        return T($"Если функция ещё не активирована — активируйте ключом{p}.", $"Функция али активдештирилбесе — ачкыч менен активдештириңиз{p}.",
            $"If the function is not activated yet, activate it with a serial key{p}.", $"İşlev henüz etkinleştirilmediyse seri anahtarla etkinleştirin{p}.",
            $"Funksiya hali faollashtirilmagan bo'lsa, seriya kaliti bilan faollashtiring{p}.");
    }

    /// <summary>Условия цены: разовая активация серийным ключом.</summary>
    private static string OnceTerms() => T(
        "Разовая активация серийным ключом", "Сериялык ачкыч менен бир жолку активдештирүү",
        "One-time activation with a serial key", "Seri anahtarla tek seferlik etkinleştirme", "Seriya kaliti bilan bir martalik faollashtirish");

    /// <summary>Условия цены для того, что на «Старте» подключается за 1500 + 400 сом/мес, а на «Стандарте» входит в тариф.
    /// Суммы — из TariffGate, чтобы не разойтись с ценой на кнопке.</summary>
    private static string TariffTerms()
    {
        var fee = TariffGate.PackActivationFee;
        var month = TariffGate.PackMonthlyFee;
        return TariffGate.IsStartTariff
            ? T($"Подключение {fee} сом + {month} сом в месяц. На тарифе «Стандарт» входит в тариф.",
                $"Туташтыруу {fee} сом + айына {month} сом. «Стандарт» тарифине кирет.",
                $"Connection {fee} som + {month} som per month. Included in the “Standard” plan.",
                $"Bağlantı {fee} som + aylık {month} som. «Standart» tarifesine dahildir.",
                $"Ulash {fee} so'm + oyiga {month} so'm. «Standart» tarifiga kiradi.")
            : T("Входит в тариф «Стандарт»", "«Стандарт» тарифине кирет", "Included in the “Standard” plan",
                "«Standart» tarifesine dahildir", "«Standart» tarifiga kiradi");
    }

    /// <summary>Условия цены пакета «Стандарта» на «Старте» (ТЗ 5.14).</summary>
    private static string PackTerms() => T(
        $"Подключение {TariffGate.PackActivationFee} сом + {TariffGate.PackMonthlyFee} сом в месяц. На тарифе «Стандарт» всё это уже входит.",
        $"Туташтыруу {TariffGate.PackActivationFee} сом + айына {TariffGate.PackMonthlyFee} сом. «Стандарт» тарифинде мунун баары кирет.",
        $"Connection {TariffGate.PackActivationFee} som + {TariffGate.PackMonthlyFee} som per month. All of this is already included in the “Standard” plan.",
        $"Bağlantı {TariffGate.PackActivationFee} som + aylık {TariffGate.PackMonthlyFee} som. «Standart» tarifesinde bunların hepsi dahildir.",
        $"Ulash {TariffGate.PackActivationFee} so'm + oyiga {TariffGate.PackMonthlyFee} so'm. «Standart» tarifida bularning hammasi kiradi.");

    private static string SubscribeStep()
    {
        var fee = TariffGate.PackActivationFee;
        var month = TariffGate.PackMonthlyFee;
        return T($"«Старт»: подключите ({fee} сом + {month} сом/мес); «Стандарт»: уже входит.",
            $"«Старт»: туташтырыңыз ({fee} сом + айына {month} сом); «Стандарт»: тарифке кирет.",
            $"“Start”: connect it ({fee} som + {month} som/mo); “Standard”: already included.",
            $"«Start»: bağlayın ({fee} som + aylık {month} som); «Standart»: zaten dahildir.",
            $"«Start»: ulang ({fee} so'm + oyiga {month} so'm); «Standart»: tarifga kiradi.");
    }

    private static string LightVariant() => T("Светлый вариант", "Ачык вариант", "Light variant", "Açık varyant", "Yorug' variant");

    private static string DarkVariant() => T("Тёмный вариант", "Караңгы вариант", "Dark variant", "Koyu varyant", "Qorong'i variant");
}
