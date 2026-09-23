using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NurMarketKassa.Models
{
    /// <summary>Стандартная обёртка API для списков (results / count).</summary>
    public class ApiListResponse<T>
    {
        [JsonPropertyName("results")]
        public List<T>? Results { get; set; }

        [JsonPropertyName("count")]
        public int? Count { get; set; }
    }

    /// <summary>Упрощённое представление товара из поиска / каталога.</summary>
    public class ProductDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("price")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
        public decimal? Price { get; set; }

        [JsonPropertyName("barcode")]
        public string? Barcode { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("brand")]
        public string? Brand { get; set; }

        [JsonPropertyName("image")]
        public string? ImageUrl { get; set; }

        [JsonPropertyName("quantity")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
        public double? Quantity { get; set; }

        [JsonPropertyName("unit")]
        public string? Unit { get; set; }

        [JsonPropertyName("must_weigh")]
        public bool MustWeigh { get; set; }

        [JsonPropertyName("is_weight")]
        public bool IsWeight { get; set; }

        [JsonPropertyName("is_weight_product")]
        public bool IsWeightProduct { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("stock_quantity")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
        public double? StockQuantity { get; set; }

        [JsonPropertyName("stock_weight")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
        public double? StockWeight { get; set; }

        [JsonPropertyName("is_favorite")]
        public bool IsFavorite { get; set; }

        public bool ResolvesMustWeigh()
        {
            if (MustWeigh || IsWeight || IsWeightProduct)
                return true;
            var unit = (Unit ?? "").Trim().ToLowerInvariant();
            return unit is "кг" or "kg" or "kг";
        }

        public double ResolvesQuantity(bool mustWeigh)
        {
            if (mustWeigh)
            {
                if (StockWeight is > 0)
                    return StockWeight.Value;
                if (StockQuantity is not null)
                    return StockQuantity.Value;
                return Quantity ?? 0;
            }

            if (StockQuantity is not null)
                return StockQuantity.Value;
            if (Quantity is not null)
                return Quantity.Value;
            if (StockWeight is not null)
                return StockWeight.Value;
            return 0;
        }
    }

    /// <summary>GET /api/users/company/</summary>
    public sealed class CompanyDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("inn")]
        public string? Inn { get; set; }

        [JsonPropertyName("address")]
        public string? Address { get; set; }

        /// <summary>Начало оплаченного периода подписки NurCRM (ISO 8601).</summary>
        [JsonPropertyName("start_date")]
        public string? StartDate { get; set; }

        /// <summary>Конец оплаченного периода подписки NurCRM (ISO 8601).</summary>
        [JsonPropertyName("end_date")]
        public string? EndDate { get; set; }

        /// <summary>Название тарифного плана (поле "subscription_plan.name").</summary>
        [JsonPropertyName("subscription_plan_name")]
        public string? SubscriptionPlanName { get; set; }

        /// <summary>Цена тарифного плана (поле "subscription_plan.price").</summary>
        [JsonPropertyName("subscription_plan_price")]
        public string? SubscriptionPlanPrice { get; set; }

        /// <summary>Подключённые доп. услуги (поля "can_view_documents/whatsapp/instagram/
        /// telegram/showcase" — по названию похожи на права доступа, но на уровне КОМПАНИИ
        /// (не пользователя) они отражают именно то, какие доп. модули подключены к тарифу).</summary>
        [JsonPropertyName("can_view_documents")]
        public bool CanViewDocuments { get; set; }

        [JsonPropertyName("can_view_whatsapp")]
        public bool CanViewWhatsapp { get; set; }

        [JsonPropertyName("can_view_instagram")]
        public bool CanViewInstagram { get; set; }

        [JsonPropertyName("can_view_telegram")]
        public bool CanViewTelegram { get; set; }

        [JsonPropertyName("can_view_showcase")]
        public bool CanViewShowcase { get; set; }

        /// <summary>Раскладка весового штрих-кода: "plu" (PLU 5 цифр) или "code" (внутренний
        /// код товара 6 цифр, используют весы Rongta) — из GET /users/settings/company/.</summary>
        [JsonPropertyName("scale_barcode_layout")]
        public string? ScaleBarcodeLayout { get; set; }

        /// <summary>Режим значения в весовом штрих-коде: "auto" (по префиксу: 20→вес, 25→сумма),
        /// "weight" (всегда граммы) или "amount" (всегда сом×100) — из GET /users/settings/company/.</summary>
        [JsonPropertyName("scale_barcode_mode")]
        public string? ScaleBarcodeMode { get; set; }

        /// <summary>Пароль кассы (Company.cashier_password на бэкенде) — подтверждает
        /// удаление/изменение позиций в чеке. Пустой/null — проверка паролем отключена.</summary>
        [JsonPropertyName("cashier_password")]
        public string? CashierPassword { get; set; }

        /// <summary>2026-09-08: "Код на удаление из корзины" — видно на app.nurcrm.kg (Моя
        /// компания → Касса), реальное серверное поле, ОТДЕЛЬНОЕ от CashierPassword выше.
        /// Точное имя поля в JSON не подтверждено (см. NurMarketApiClient.ParseCompanyDto —
        /// пробует несколько вариантов), поэтому здесь [JsonPropertyName] условный, для
        /// единообразия с остальными полями — фактическое заполнение идёт вручную.
        /// null/пусто — код не задан, удаление из корзины проходит без подтверждения (как на
        /// сайте). Владелец/админ удаляют без кода независимо от этого поля.</summary>
        [JsonPropertyName("cart_delete_code")]
        public string? CartDeleteCode { get; set; }

        /// <summary>2026-09-08: "Максимальная скидка" (%) — тоже видно на app.nurcrm.kg, там
        /// же. Ограничение для сотрудников на скидку по позиции и на весь чек; null — без
        /// ограничения. На владельца/админа не действует.</summary>
        [JsonPropertyName("max_discount_percent")]
        public decimal? MaxDiscountPercent { get; set; }
    }

    /// <summary>2026-09-08: один сотрудник компании — с app.nurcrm.kg, раздел "Сотрудники".
    /// Точный URL списка и имена JSON-полей не подтверждены (нет доступа к серверному коду) —
    /// NurMarketApiClient.GetEmployeesAsync пробует несколько правдоподобных вариантов подряд,
    /// как уже сделано для CompanyDto.CartDeleteCode/MaxDiscountPercent.</summary>
    public sealed class EmployeeInfoDto
    {
        public string? Id { get; set; }
        public string? FullName { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? RoleName { get; set; }
        /// <summary>Поле "custom_role" в JSON сотрудника — id роли, нужен, чтобы предвыбрать
        /// роль при повторном сохранении/показе (2026-09-21).</summary>
        public string? RoleId { get; set; }

        /// <summary>2026-09-08: сервер сам генерирует пароль сотрудника при создании и
        /// возвращает его в ответе POST .../employees/create/ (подтверждено — сайт сразу
        /// показывает его в окне "Логин сотрудника"). Только для свежесозданного сотрудника —
        /// в списке (GET) сервер пароль, разумеется, не отдаёт, там всегда null.</summary>
        public string? Password { get; set; }

        /// <summary>Текущие доступы (can_view_*) этого сотрудника — подтверждено живым GET
        /// api/users/employees/ (2026-09-21), см. doc-comment у EmployeeAccessFlags.</summary>
        public EmployeeAccessFlags Access { get; set; } = new();
    }

    /// <summary>2026-09-08: роль сотрудника — с app.nurcrm.kg, раздел "Сотрудники → Роли".
    /// Точный URL списка/создания и имя JSON-поля не подтверждены — см. NurMarketApiClient.</summary>
    public sealed class RoleInfoDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>Доступы сотрудника — поля can_view_* (2026-09-21, подтверждено живым GET
    /// api/users/employees/ на реальном аккаунте сектора "Магазин": разбор полного JSON
    /// сотрудников с реально включёнными правами, сверено с формой "Управление доступами" на
    /// сайте по скриншоту владельца). Раньше касса при создании сотрудника отправляла ВСЕ эти
    /// флаги как false — сотрудник создавался фактически без явных прав (полагаясь только на
    /// роль), хотя сайт даёт кассиру выбрать их индивидуально при создании/редактировании.
    /// CanViewProducts/CanViewCashier переиспользуются для чекбоксов "Склад"/"Интерфейс кассира"
    /// и в группе "Базовые доступы", и в группе "Дополнительные услуги" — на сайте оба чекбокса
    /// в каждой паре включают один и тот же флаг (не два разных, отдельного поля для второго
    /// вхождения в JSON сотрудника нет).</summary>
    public sealed class EmployeeAccessFlags
    {
        // ---------- Базовые доступы ----------
        public bool CanViewCashbox { get; set; }
        public bool CanViewAnalytics { get; set; }
        public bool CanViewProducts { get; set; }
        public bool CanViewSale { get; set; }
        public bool CanViewClients { get; set; }
        public bool CanViewBrandCategory { get; set; }
        public bool CanViewEmployees { get; set; }
        public bool CanViewSettings { get; set; }
        public bool CanViewMarketProcurement { get; set; }
        public bool CanViewMarketSupplier { get; set; }

        // ---------- Секторные доступы ----------
        public bool CanViewCashier { get; set; }
        public bool CanViewShifts { get; set; }
        public bool CanViewDocument { get; set; }

        // ---------- Касса Маркета ----------
        public bool CanViewMarketDiscount { get; set; }
        public bool CanViewMarketEditPrice { get; set; }
        public bool CanViewMarketDeleteCartItem { get; set; }
        public bool CanViewMarketEmployeeReturn { get; set; }

        // ---------- Дополнительные услуги ----------
        public bool CanViewMarketLabel { get; set; }
        public bool CanViewMarketScales { get; set; }
        public bool CanViewWhatsapp { get; set; }
        public bool CanViewTelegram { get; set; }
        public bool CanViewInstagram { get; set; }
        public bool CanViewDocuments { get; set; }

        public Dictionary<string, object> ToRequestFields() => new()
        {
            ["can_view_cashbox"] = CanViewCashbox,
            ["can_view_analytics"] = CanViewAnalytics,
            ["can_view_products"] = CanViewProducts,
            ["can_view_sale"] = CanViewSale,
            ["can_view_clients"] = CanViewClients,
            ["can_view_brand_category"] = CanViewBrandCategory,
            ["can_view_employees"] = CanViewEmployees,
            ["can_view_settings"] = CanViewSettings,
            ["can_view_market_procurement"] = CanViewMarketProcurement,
            ["can_view_market_supplier"] = CanViewMarketSupplier,
            ["can_view_cashier"] = CanViewCashier,
            ["can_view_shifts"] = CanViewShifts,
            ["can_view_document"] = CanViewDocument,
            ["can_view_market_discount"] = CanViewMarketDiscount,
            ["can_view_market_edit_price"] = CanViewMarketEditPrice,
            ["can_view_market_delete_cart_item"] = CanViewMarketDeleteCartItem,
            ["can_view_market_employee_return"] = CanViewMarketEmployeeReturn,
            ["can_view_market_label"] = CanViewMarketLabel,
            ["can_view_market_scales"] = CanViewMarketScales,
            ["can_view_whatsapp"] = CanViewWhatsapp,
            ["can_view_telegram"] = CanViewTelegram,
            ["can_view_instagram"] = CanViewInstagram,
            ["can_view_documents"] = CanViewDocuments,
        };
    }
}