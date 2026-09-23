namespace NurMarketKassa.Services;

/// <summary>Фасад над <see cref="DatabaseService"/> для пользовательских слов-единиц измерения
/// голосового парсера (2026-09-04) — база для расширения встроенного русского/кыргызского
/// списка в VoiceCommandParser своими словами ("мешок", "ящик" и т.п.), без правки кода.</summary>
public static class VoiceLexiconStore
{
    private static DatabaseService Db => DatabaseService.Instance;

    public static void AddUnitWord(string word, string? abbreviation) =>
        Db.AddVoiceUnitWord(word, abbreviation);

    public static void RemoveUnitWord(int id) =>
        Db.RemoveVoiceUnitWord(id);

    public static List<(int Id, string Word, string? Abbreviation)> LoadUnitWords() =>
        Db.LoadVoiceUnitWords();

    /// <summary>2026-09-08: "обучение" — привязка конкретной произносимой фразы к товару, в обход
    /// обычного поиска по словам названия. См. DatabaseService.AddVoiceProductAlias.</summary>
    public static void AddProductAlias(string phrase, string productId, string productTitle) =>
        Db.AddVoiceProductAlias(phrase, productId, productTitle);

    public static void RemoveProductAlias(int id) =>
        Db.RemoveVoiceProductAlias(id);

    public static List<(int Id, string Phrase, string ProductId, string ProductTitle)> LoadProductAliases() =>
        Db.LoadVoiceProductAliases();
}
