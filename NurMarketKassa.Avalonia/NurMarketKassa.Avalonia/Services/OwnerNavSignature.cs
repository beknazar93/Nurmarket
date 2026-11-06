using System.Text;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06, А-8 (отчёт «Аналогичные баги»): меню программы владельца пересоздавало левую колонку целиком (BuildNavigation) от
/// любого события прав/тарифа/«Разделов меню» — значки и пункты «мигали» и прыгали, терялись прокрутка и наведение, хотя набор пунктов
/// не менялся. Подпись состава — какие группы и пункты (ключ + подпись на текущем языке) сейчас видны, по порядку. Событие пересобирает
/// меню, только если подпись изменилась. Чистая логика без Avalonia — чтобы проверить тестом.</summary>
internal sealed class OwnerNavSignature
{
    private readonly StringBuilder _text = new();

    /// <summary>Группа вошла в меню (появляется с первым видимым пунктом).</summary>
    public void Group(string key, string title) => _text.Append('[').Append(key).Append(':').Append(title).Append(']');

    /// <summary>Видимый пункт меню.</summary>
    public void Item(string key, string text) => _text.Append(key).Append(':').Append(text).Append(';');

    public override string ToString() => _text.ToString();

    /// <summary>Нужно ли пересобирать меню: прежней подписи нет (меню ещё не строилось) или состав изменился.</summary>
    public static bool NeedsRebuild(string? previous, string current) => previous == null || !string.Equals(previous, current, StringComparison.Ordinal);
}
