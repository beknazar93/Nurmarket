namespace NurMarketKassa.Services.Hardware;

/// <summary>Распознанные намерения голосовой команды кассира (после ключевого слова "касса").</summary>
public enum VoiceIntent
{
    Unknown,
    AddProduct,
    RemoveLastItem,
    ClearCart,
    FindProduct,
    RepeatLast,
    Cancel,
    Pay,
}

/// <summary>Была ли произнесённая единица явным сигналом "поштучно" (шт/штука/даана/…) или
/// "целой пачкой" (пачка/упаковка/…) — см. VoiceCommandParser.ExtractQuantity (2026-09-04).
/// None — единица не названа, названа единица веса/объёма, или кастомная единица без такой
/// семантики; в этом случае, если у товара вообще есть выбор, кассиру всё равно нужно уточнить
/// через PackageChoiceDialog, т.к. само число неоднозначно (могут иметься в виду и штуки, и пачки).</summary>
public enum VoiceUnitKind
{
    None,
    Piece,
    Pack,
}

/// <summary>Структурированная модель голосовой команды — результат работы IVoiceCommandParser.
/// Чисто текстовые данные, без ссылок на конкретный товар каталога (это уже задача Resolver'а,
/// см. VoiceCommandParser.FindProducts) — такое разделение позволяет тестировать парсер сотнями
/// текстовых примеров без микрофона и без загруженного каталога.</summary>
public sealed record VoiceCommand
{
    public VoiceIntent Intent { get; init; } = VoiceIntent.Unknown;
    public string ProductText { get; init; } = "";
    public double Quantity { get; init; } = 1;
    public string? Unit { get; init; }
    public VoiceUnitKind UnitKind { get; init; } = VoiceUnitKind.None;
    public double Confidence { get; init; } = 1.0;
    public bool RequiresConfirmation { get; init; }
    public string RawText { get; init; } = "";
}

/// <summary>Разбирает текст голосовой команды (уже без ключевого слова "касса") в структурированную
/// VoiceCommand. Не знает ничего про аудио/Vosk/каталог — только текст на входе, DTO на выходе.</summary>
public interface IVoiceCommandParser
{
    VoiceCommand Parse(string commandText);
}
