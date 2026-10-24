namespace NurMarketKassa.Core.Contracts;

public interface IUserPrompts
{
    Task<bool> ConfirmAsync(string message);

    /// <summary>2026-10-22 (модалки, пакет 0): вопрос со своими глаголами на кнопках («Удалить чек» / «Отмена»);
    /// null — умолчания окна («Продолжить» / «Отмена»), <paramref name="danger"/> — красная главная кнопка.
    /// Реализация по умолчанию (для заглушек и тестовых подсказок) — обычный <see cref="ConfirmAsync(string)"/>.</summary>
    Task<bool> ConfirmAsync(string message, string? confirmText, string? cancelText = null, bool danger = false) =>
        ConfirmAsync(message);
    void ShowToast(string message, bool isWarning = false);
    void ShowWarning(string message);
    void ShowError(string message);

    /// <summary>Запрашивает пароль кассы (Company.cashier_password) для подтверждения
    /// операции; возвращает true только если введённый пароль совпал. Диалог сам
    /// показывает ошибку и даёт повторить ввод при несовпадении, false — если кассир
    /// закрыл окно (Отмена).</summary>
    Task<bool> ConfirmWithPasswordAsync(string title, string message, string expectedPassword);

    /// <summary>2026-09-08: как ConfirmWithPasswordAsync, но проверка — произвольная функция, а
    /// не сравнение с одним заранее известным паролем. Нужен для личных кодов доступа сотрудников
    /// (EmployeeAccessGate), где верных значений несколько.</summary>
    Task<bool> ConfirmWithCodeAsync(string title, string message, Func<string, bool> validator);
}
