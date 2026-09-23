using System;

namespace NurMarketKassa.Services;

/// <summary>2026-09-08: "Максимальная скидка" — реальная серверная настройка компании
/// (видна на app.nurcrm.kg, Моя компания → Касса; CompanyDto.MaxDiscountPercent), не
/// локальная. Ограничивает скидку на позицию и на весь чек для сотрудников; на владельца/
/// админа не действует — проверка на это делается в местах использования (BasketPanelViewModel).</summary>
public static class MaxDiscountGate
{
    public static decimal? Limit => CompanyInfoService.LastCompany?.MaxDiscountPercent;

    /// <summary>Кто освобождён от потолка (владелец/админ). Ставится один раз при старте, когда
    /// сервис прав уже собран. Проектам ViewModels сервис прав доступен не везде, а потолок
    /// должен действовать во ВСЕХ местах ввода скидки одинаково — отсюда общий крючок.</summary>
    public static Func<bool>? IsExempt { get; set; }

    /// <summary>Действует ли потолок прямо сейчас.
    ///
    /// Намеренно fail-closed: пока неизвестно, кто перед кассой, ограничение применяется.
    /// Прежняя проверка в корзине была написана наоборот — `!(_permissions?.HasPermission(...)
    /// ?? true)` снимала потолок, когда сервис прав не подан, то есть отключала ограничение
    /// ровно в той ситуации, где о пользователе ничего не известно.</summary>
    public static bool AppliesNow => Limit is not null && !(IsExempt?.Invoke() ?? false);

    /// <summary>Превышает ли скидка потолок. Сумма приводится к процентам от базы, иначе
    /// ограничение обходилось бы простым переключением «процент → сумма».</summary>
    public static bool Exceeds(double enteredValue, bool isPercent, double baseSum)
    {
        if (!AppliesNow || Limit is not { } limit)
            return false;

        var percent = isPercent
            ? enteredValue
            : baseSum > 0 ? enteredValue / baseSum * 100 : 0;

        return percent > (double)limit + 1e-6;
    }
}
