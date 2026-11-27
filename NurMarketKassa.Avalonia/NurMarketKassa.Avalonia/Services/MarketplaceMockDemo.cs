using System;
using System.Collections.Generic;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-26 (замечание владельца: «Где описание и гифки его работы!? Создай для ВСЕХ функций!»): демонстрация работы функции в Маркетплейсе,
// нарисованная КОДОМ — макет окна кассы из простых частей (строка, поле, кнопка, плашка, переключатель, итог), 3–5 шагов с подписью на 5 языках.
// Почему не GIF: файлы GIF (Assets/marketplace/<id>/demo*.gif) надо рисовать и перекодировать отдельно для каждого языка и темы; код-макет сам следует
// теме (светлая/тёмная), языку и размеру шрифта, не раздувает установщик и проверяется тестами (текст не обрезан, шаги не вылезают за рамку).
// Здесь — ТОЛЬКО данные (без типов Avalonia): их читают тесты и рисует MarketplaceMockDemoPlayer (Views/Settings).
// Реальных данных клиентов нет: имена, номера и суммы выдуманные. Каждый новый модуль сервера = запись в MarketplaceDemos.* (иначе тест красный).

/// <summary>Тон цвета текста: обычный, хороший (зелёный), предупреждение (янтарный), плохой (красный), акцент.</summary>
public enum MockTone { Normal, Good, Warn, Bad, Accent }

/// <summary>Часть макета окна. Hot = «сюда смотрит/нажимает пользователь» — подсвечена рамкой акцента.</summary>
public abstract record MockPart;

/// <summary>Поле ввода с подписью над ним (Hot — в поле печатают).</summary>
public sealed record MockInput(string Label, string Value, bool Hot = false) : MockPart;

/// <summary>Строка списка/чека: слева текст (и мелкая строка под ним), справа значение.</summary>
public sealed record MockRow(string Text, string Right = "", MockTone Tone = MockTone.Normal, bool Hot = false, string Sub = "") : MockPart;

/// <summary>Кнопка (Primary — акцентная; Hot — её нажимают).</summary>
public sealed record MockButton(string Text, bool Hot = false, bool Primary = true) : MockPart;

/// <summary>Плашка-сообщение (результат действия, предупреждение, подсказка).</summary>
public sealed record MockNote(string Text, MockTone Tone = MockTone.Normal) : MockPart;

/// <summary>Переключатель «название — вкл/выкл».</summary>
public sealed record MockToggle(string Label, bool On, bool Hot = false) : MockPart;

/// <summary>Итоговая строка крупным шрифтом («Итого — 1 250»).</summary>
public sealed record MockTotal(string Label, string Value, bool Hot = false) : MockPart;

/// <summary>Ряд «таблеток» (фильтр, размеры, категории); Selected — какая выбрана (-1 — никакая).</summary>
public sealed record MockChips(IReadOnlyList<string> Items, int Selected) : MockPart;

/// <summary>Ряд крупных плиток (быстрые товары, категории); Hot — номер подсвеченной (-1 — никакая).</summary>
public sealed record MockTiles(IReadOnlyList<string> Items, int Hot = -1) : MockPart;

/// <summary>Один кадр: название окна и его содержимое сверху вниз.</summary>
public sealed record MockScene(string Title, IReadOnlyList<MockPart> Parts);

/// <summary>Шаг демонстрации: подпись под макетом («Нажмите…», «Видно…», «Результат…») и сам кадр.</summary>
public sealed record MockStep(string Caption, MockScene Scene);

/// <summary>Запись реестра: шаги демонстрации и (необязательно) «Что получите» — ровно 3 пункта, заменяют Benefits карточки; у пакетов «Стандарта» ещё подробное
/// «Что делает» (2–3 предложения с примером чисел; заменяет короткое описание из списка пакетов) и «Для кого» (если у карточки его нет).</summary>
internal sealed record MarketplaceDemoEntry(Func<IReadOnlyList<MockStep>> Steps, Func<IReadOnlyList<MarketplaceBenefit>>? Gains = null,
    Func<string>? What = null, Func<IReadOnlyList<string>>? Audience = null);

/// <summary>Короткие конструкторы кадров — чтобы описание шага читалось как сам кадр. Подключать так: <c>using static NurMarketKassa.AvaloniaHost.Services.MockDsl;</c>.</summary>
internal static class MockDsl
{
    public static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Шаг: подпись, название окна, части сверху вниз.</summary>
    public static MockStep Step(string caption, string title, params MockPart[] parts) => new(caption, new MockScene(title, parts));

    public static MockInput Input(string label, string value, bool hot = false) => new(label, value, hot);

    public static MockRow Row(string text, string right = "", MockTone tone = MockTone.Normal, bool hot = false, string sub = "") => new(text, right, tone, hot, sub);

    public static MockButton Btn(string text, bool hot = false, bool primary = true) => new(text, hot, primary);

    public static MockNote Note(string text, MockTone tone = MockTone.Normal) => new(text, tone);

    public static MockToggle Toggle(string label, bool on, bool hot = false) => new(label, on, hot);

    public static MockTotal Total(string label, string value, bool hot = false) => new(label, value, hot);

    public static MockChips Chips(int selected, params string[] items) => new(items, selected);

    public static MockTiles Tiles(int hot, params string[] items) => new(items, hot);

    /// <summary>Пункт «Что получите»: ключ значка (MarketplaceIcons), заголовок, одно-два предложения.</summary>
    public static MarketplaceBenefit Gain(string iconKey, string title, string text) => new(iconKey, title, text);
}

/// <summary>Реестр демонстраций по коду функции: модуль сервера — его код ("part_numbers"), пакет — "pack:ai", Смета/Прокат/Ремонт/Trade-in — их код.
/// Тексты лежат в MarketplaceDemos.*.cs по группам; Apply вызывается из MarketplaceContent.Enrich.</summary>
public static partial class MarketplaceDemos
{
    private static readonly Dictionary<string, MarketplaceDemoEntry> Map = Build();

    private static Dictionary<string, MarketplaceDemoEntry> Build()
    {
        var map = new Dictionary<string, MarketplaceDemoEntry>(StringComparer.OrdinalIgnoreCase);
        AddGroupA(map);
        AddGroupB(map);
        AddGroupC(map);
        AddGroupD(map);
        AddGroupE(map); // 2026-11-26: бот, торг бота, ЭСФ, онлайн-ККМ, рассылки, 1С, «заполним базу», QR, электронный чек, сканер на складе
        return map;
    }

    /// <summary>Ключ реестра по Id карточки: у общей карточки модуля убираем «module:».</summary>
    public static string KeyOf(string itemId) => itemId.StartsWith("module:", StringComparison.Ordinal) ? itemId["module:".Length..] : itemId;

    /// <summary>Коды, для которых есть демонстрация (для проверки полноты по списку модулей сервера).</summary>
    public static IReadOnlyCollection<string> Keys => Map.Keys;

    public static bool Has(string key) => Map.ContainsKey(key);

    /// <summary>Шаги демонстрации на языке программы; пусто — записи нет.</summary>
    public static IReadOnlyList<MockStep> StepsOf(string key) => Map.TryGetValue(key, out var e) ? e.Steps() : [];

    /// <summary>«Что получите» на языке программы; пусто — записи нет.</summary>
    public static IReadOnlyList<MarketplaceBenefit> GainsOf(string key) => Map.TryGetValue(key, out var e) && e.Gains is { } g ? g() : [];

    /// <summary>Подробное «Что делает» пакета; пусто — записи нет.</summary>
    public static string WhatOf(string key) => Map.TryGetValue(key, out var e) && e.What is { } w ? w() : "";

    /// <summary>«Для кого» пакета; пусто — записи нет.</summary>
    public static IReadOnlyList<string> AudienceOf(string key) => Map.TryGetValue(key, out var e) && e.Audience is { } a ? a() : [];

    /// <summary>Дописать карточке демонстрацию и «Что получите» (если записано). Ошибка в записи не должна ронять каталог.</summary>
    public static void Apply(MarketplaceItem item)
    {
        var key = KeyOf(item.Id);
        if (!Map.TryGetValue(key, out var entry))
            return;
        try
        {
            item.MockDemo = entry.Steps();
            if (entry.Gains is { } gains && gains() is { Count: > 0 } list)
                item.Benefits = list;
            if (entry.What is { } what && what() is { Length: > 0 } longText)
                item.LongDescription = longText;
            if (item.Audience.Count == 0 && entry.Audience is { } audience && audience() is { Count: > 0 } who)
                item.Audience = who;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Маркетплейс: демонстрация «{key}» не собралась: {ex.Message}", "WARNING");
        }
    }
}
