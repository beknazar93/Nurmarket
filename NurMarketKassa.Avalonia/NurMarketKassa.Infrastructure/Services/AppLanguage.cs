namespace NurMarketKassa.Services;

public enum AppLanguage
{
    Russian,
    Kyrgyz,
    // Добавлены в конец (не переставлены) — Language сериализуется как обычное число,
    // вставка перед существующими значениями сдвинула бы уже сохранённые настройки кассиров.
    Turkish,
    Uzbek,
    English,
}
