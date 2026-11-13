using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

public enum ShtrikhPcTab { General, Barcode, Print, Quick, Exchange, Reports, Advanced }

public enum ShtrikhPcParamKind { Bool, Int, Text, Choice }

/// <summary>2026-11-06: один параметр конфигурации весов «Штрих РС-200С2» (файл Config.txt, строка «&lt;N номер; значение»). Номера, значения по умолчанию и диапазоны — руководство
/// администратора, п. 6.3 (таблица параметров, стр. 14–19), Паспорт версий 2.1.3.5–2.1.3.9 (700–704, 953–957). Подписи — на 5 языках.</summary>
public sealed class ShtrikhPcParam
{
    public int Number { get; init; }
    public ShtrikhPcTab Tab { get; init; }
    public ShtrikhPcParamKind Kind { get; init; }
    public string Default { get; init; } = "";
    public int Min { get; init; }
    public int Max { get; init; }
    public Func<string> Title { get; init; } = () => "";
    public Func<string>? Note { get; init; }
    /// <summary>Варианты для Choice: индекс = значение параметра.</summary>
    public Func<string[]>? Choices { get; init; }
    /// <summary>Только показать (версии): весы пишут их сами, менять нельзя.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>Группа внутри вкладки (заголовок карточки).</summary>
    public Func<string>? Group { get; init; }
}

/// <summary>Одна строка сверки «весы ↔ касса».</summary>
public sealed record ShtrikhPcCheckItem(bool Ok, string Text);

/// <summary>Изменение параметра: что было на весах и что станет.</summary>
public sealed record ShtrikhPcChange(int Number, string Old, string New);

/// <summary>Прочитанная конфигурация весов: параметры по номерам (в порядке файла).</summary>
public sealed class ShtrikhPcConfigData
{
    private readonly Dictionary<int, string> _values = new();
    private readonly List<int> _order = new();

    public IReadOnlyList<int> Numbers => _order;
    public int Count => _order.Count;

    public bool Has(int number) => _values.ContainsKey(number);

    public string? Get(int number) => _values.TryGetValue(number, out var v) ? v : null;

    public int GetInt(int number, int fallback) =>
        _values.TryGetValue(number, out var v) && int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    public void Set(int number, string value)
    {
        if (!_values.ContainsKey(number))
            _order.Add(number);
        _values[number] = value;
    }

    public ShtrikhPcConfigData Clone()
    {
        var copy = new ShtrikhPcConfigData();
        foreach (var n in _order)
            copy.Set(n, _values[n]);
        return copy;
    }
}

/// <summary>2026-11-06: разбор и запись строк конфигурации «&lt;N» (руководство, п. 6.3 и 8): <c>&lt;N    1; 1</c> — номер, «;», значение; кодировка Windows-1251.
/// Чтение — из копии Config.txt или из Export\Export.txt (если на весах включён экспорт конфигурации, параметр 804). Запись — файлом импорта строками «&lt;N» ТОЛЬКО для
/// изменённых параметров и ТОЛЬКО если на весах включён импорт конфигурации (параметр 814 = 1). Опасные параметры — отдельное подтверждение (<see cref="DangerReason"/>).</summary>
public static class ShtrikhPcConfig
{
    // ------------------------------------------------------------------ разбор

    /// <summary>Разбирает текст конфигурации. Принимает «&lt;N 12; 2», «&lt;N12;2» и «&lt;N;12;2»; строки других БД («&lt;D», «&lt;R» …) и пустые пропускает. Повтор номера — последний.</summary>
    public static ShtrikhPcConfigData Parse(string? text)
    {
        var data = new ShtrikhPcConfigData();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t', '﻿');
            if (!line.StartsWith("<N", StringComparison.Ordinal))
                continue;
            var rest = line[2..].TrimStart();
            if (rest.StartsWith(';'))
                rest = rest[1..];
            var at = rest.IndexOf(';');
            if (at <= 0)
                continue;
            if (!int.TryParse(rest[..at].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
                continue;
            data.Set(number, rest[(at + 1)..].Trim());
        }
        return data;
    }

    public static ShtrikhPcConfigData ParseBytes(byte[] bytes) => Parse(ShtrikhPcImportWriter.Decode(bytes));

    // ------------------------------------------------------------------ проверка значения

    /// <summary>Значение подходит параметру? null — да; иначе текст ошибки (5 языков).</summary>
    public static string? Validate(ShtrikhPcParam p, string? value)
    {
        var v = (value ?? "").Trim();
        switch (p.Kind)
        {
            case ShtrikhPcParamKind.Text:
                if (v.IndexOfAny(new[] { ';', '\r', '\n' }) >= 0)
                    return Tr.T($"Параметр {p.Number}: в тексте нельзя «;» и переводы строки.", $"{p.Number}-параметр: текстте «;» жана саптын которулушу болбойт.",
                        $"Parameter {p.Number}: the text cannot contain “;” or line breaks.", $"Parametre {p.Number}: metinde «;» ve satır sonu olamaz.", $"{p.Number}-parametr: matnda «;» va qator o‘tkazish bo‘lmaydi.");
                return null;
            case ShtrikhPcParamKind.Bool:
                return v is "0" or "1" ? null : RangeError(p, 0, 1);
            default:
                if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < p.Min || n > p.Max)
                    return RangeError(p, p.Min, p.Max);
                return null;
        }
    }

    private static string RangeError(ShtrikhPcParam p, int min, int max) =>
        Tr.T($"Параметр {p.Number}: нужно целое число от {min} до {max}.", $"{p.Number}-параметр: {min}дөн {max}го чейинки бүтүн сан керек.",
            $"Parameter {p.Number}: an integer from {min} to {max} is required.", $"Parametre {p.Number}: {min} ile {max} arasında tam sayı gerekir.", $"{p.Number}-parametr: {min} dan {max} gacha butun son kerak.");

    // ------------------------------------------------------------------ дифф

    /// <summary>Только изменённые параметры: <paramref name="edited"/> — значения из окна (номер → значение), <paramref name="baseline"/> — прочитанное с весов. Параметр, которого нет
    /// на весах, считается изменённым, только если значение не совпало со значением по умолчанию из руководства.</summary>
    public static List<ShtrikhPcChange> Diff(ShtrikhPcConfigData baseline, IReadOnlyDictionary<int, string> edited)
    {
        var changes = new List<ShtrikhPcChange>();
        foreach (var (number, value) in edited.OrderBy(e => e.Key))
        {
            var param = Find(number);
            var now = Normalize(param, value);
            var was = baseline.Get(number);
            if (was is null)
            {
                if (param is not null && string.Equals(Normalize(param, param.Default), now, StringComparison.Ordinal))
                    continue;
                changes.Add(new ShtrikhPcChange(number, "", now));
                continue;
            }
            if (!string.Equals(Normalize(param, was), now, StringComparison.Ordinal))
                changes.Add(new ShtrikhPcChange(number, was.Trim(), now));
        }
        return changes;
    }

    private static string Normalize(ShtrikhPcParam? p, string? value)
    {
        var v = (value ?? "").Trim();
        if (p is not null && p.Kind is ShtrikhPcParamKind.Int or ShtrikhPcParamKind.Bool or ShtrikhPcParamKind.Choice
            && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return n.ToString(CultureInfo.InvariantCulture);
        return v;
    }

    /// <summary>Применяет изменения к копии прочитанной конфигурации (что станет на весах после записи).</summary>
    public static ShtrikhPcConfigData Apply(ShtrikhPcConfigData baseline, IEnumerable<ShtrikhPcChange> changes)
    {
        var copy = baseline.Clone();
        foreach (var c in changes)
            copy.Set(c.Number, c.New);
        return copy;
    }

    /// <summary>Что надо записать, чтобы вернуть <paramref name="target"/> (например, резервную копию) поверх <paramref name="current"/>: только расходящиеся параметры, которые есть в target.</summary>
    public static List<ShtrikhPcChange> DiffToRestore(ShtrikhPcConfigData current, ShtrikhPcConfigData target)
    {
        var changes = new List<ShtrikhPcChange>();
        foreach (var number in target.Numbers)
        {
            var p = Find(number);
            if (p is { ReadOnly: true } || number is >= 2001 and <= 2360)
                continue;
            var want = Normalize(p, target.Get(number));
            var have = current.Get(number);
            if (have is null || !string.Equals(Normalize(p, have), want, StringComparison.Ordinal))
                changes.Add(new ShtrikhPcChange(number, have?.Trim() ?? "", want));
        }
        return changes;
    }

    // ------------------------------------------------------------------ файл импорта

    /// <summary>Файл импорта только с параметрами конфигурации: строка 1 «##@@&amp;&amp;», строка 2 «#» (не обработан), затем «&lt;N номер;значение» (начало строки — по настройке
    /// <see cref="ShtrikhPcSettings.LineStyle"/>, как у товаров), CRLF. Товаров и команд в нём нет.</summary>
    public static string BuildImportText(IEnumerable<ShtrikhPcChange> changes, ShtrikhPcSettings settings)
    {
        var sb = new StringBuilder();
        sb.Append(ShtrikhPcImportWriter.HeaderLine1).Append("\r\n");
        sb.Append('#').Append("\r\n");
        foreach (var c in changes)
            sb.Append(BuildLine(c.Number, c.New, settings)).Append("\r\n");
        return sb.ToString();
    }

    public static string BuildLine(int number, string value, ShtrikhPcSettings settings)
    {
        var head = settings.LineStyle switch
        {
            ShtrikhPcSettings.LineGlued => "<N",
            ShtrikhPcSettings.LineSemicolon => "<N;",
            _ => "<N ",
        };
        return head + number.ToString(CultureInfo.InvariantCulture) + ";" + value.Trim();
    }

    // ------------------------------------------------------------------ опасные параметры

    /// <summary>Причина, по которой параметр требует отдельного подтверждения; null — обычный параметр.</summary>
    public static Func<string>? DangerReason(int number)
    {
        switch (number)
        {
            case 1:
                return () => Tr.T("Номер весов печатается на этикетке и идентифицирует весы в отчётах. Менять только осознанно.", "Таразанын номери этикеткага басылат жана отчёттордо таразаны аныктайт. Аң-сезим менен гана өзгөртүңүз.",
                    "The scale number is printed on the label and identifies the scale in reports. Change it only deliberately.", "Tartı numarası etikete basılır ve raporlarda tartıyı tanımlar. Yalnızca bilerek değiştirin.", "Tarozi raqami yorliqqa bosiladi va hisobotlarda tarozini aniqlaydi. Faqat ongli ravishda o‘zgartiring.");
            case 6 or 801 or 802 or 803:
                return () => Tr.T("Меняет папку/имена файлов обмена на весах: касса пишет по прежним путям, и обмен перестанет работать, пока вы не измените те же пути в «Обмен файлами» кассы.",
                    "Таразадагы алмашуу папкасын/файл аттарын өзгөртөт: касса мурунку жолдор менен жазат, жана кассанын «Файл алмашуу» бөлүмүндө бирдей жолдорду өзгөртмөйүнчө алмашуу иштебей калат.",
                    "Changes the exchange folder/file names on the scale: the till keeps writing to the old paths and the exchange stops working until you change the same paths in the till's “File exchange”.",
                    "Tartıdaki değişim klasörünü/dosya adlarını değiştirir: kasa eski yollara yazmayı sürdürür ve kasadaki «Dosya değişimi»nde aynı yolları değiştirene kadar değişim çalışmaz.",
                    "Tarozidagi almashuv papkasi/fayl nomlarini o‘zgartiradi: kassa eski yo‘llarga yozaveradi va kassadagi «Fayl almashuvi»da bir xil yo‘llarni o‘zgartirmaguncha almashuv ishlamaydi.");
            case 804 or 805 or 806 or 807 or 808 or 815 or 816 or 817 or 818 or 819:
                return () => Tr.T("Включает или выключает приём/выгрузку этих данных через файл. Выключенный «Импорт товаров» (817) или «Импорт ресурсов» (819) — и товары или картинки перестанут загружаться.",
                    "Бул маалыматтарды файл аркылуу кабыл алууну/чыгарууну күйгүзөт же өчүрөт. «Товарларды импорттоо» (817) же «Ресурстарды импорттоо» (819) өчүрүлсө — товарлар же сүрөттөр жүктөлбөй калат.",
                    "Turns receiving/exporting this data through a file on or off. With “Import goods” (817) or “Import resources” (819) off, goods or pictures stop loading.",
                    "Bu verilerin dosya ile alınmasını/dışa aktarılmasını açar veya kapatır. «Ürün içe aktarma» (817) veya «Kaynak içe aktarma» (819) kapalıysa ürünler veya resimler yüklenmez.",
                    "Bu ma’lumotlarni fayl orqali qabul qilish/chiqarishni yoqadi yoki o‘chiradi. «Tovarlarni import» (817) yoki «Resurslarni import» (819) o‘chirilsa — tovarlar yoki rasmlar yuklanmaydi.");
            case 814:
                return () => Tr.T("Импорт конфигурации — через него касса меняет настройки. Если выключить (0), касса больше не сможет менять настройки весов файлом: включить обратно можно только на самих весах (Конфигурация → База данных → Импорт → «Конфигурация»).",
                    "Конфигурацияны импорттоо — ал аркылуу касса жөндөөлөрдү өзгөртөт. Өчүрсөңүз (0), касса таразанын жөндөөлөрүн файл менен өзгөртө албайт: кайра күйгүзүү таразанын өзүндө гана (Конфигурация → База данных → Импорт → «Конфигурация»).",
                    "Import of the configuration — the till changes settings through it. If turned off (0), the till can no longer change the scale settings with a file: it can only be turned back on at the scale itself (Configuration → Database → Import → “Configuration”).",
                    "Yapılandırma içe aktarma — kasa ayarları bununla değiştirir. Kapatılırsa (0) kasa artık tartı ayarlarını dosyayla değiştiremez: yalnızca tartının kendisinde yeniden açılabilir (Конфигурация → База данных → Импорт → «Конфигурация»).",
                    "Konfiguratsiyani import — kassa sozlamalarni shu orqali o‘zgartiradi. O‘chirilsa (0), kassa tarozi sozlamalarini fayl bilan o‘zgartira olmaydi: qayta yoqish faqat tarozining o‘zida (Конфигурация → База данных → Импорт → «Конфигурация»).");
            case 17:
                return () => Tr.T("«Выключение питания»: после выхода из программы весов компьютер весов выключится. Менять только если понимаете последствия.", "«Кубатты өчүрүү»: тараза программасынан чыккандан кийин таразанын компьютери өчөт. Кесепетин түшүнсөңүз гана өзгөртүңүз.",
                    "“Power off”: after leaving the scale program the scale computer shuts down. Change only if you understand the effect.", "«Güç kapatma»: tartı programından çıkınca tartı bilgisayarı kapanır. Yalnızca sonucunu anlıyorsanız değiştirin.", "«Quvvatni o‘chirish»: tarozi dasturidan chiqqach tarozi kompyuteri o‘chadi. Oqibatini tushunsangiz o‘zgartiring.");
            case 27:
                return () => Tr.T("Режим самообслуживания меняет работу всех весов (экран покупателя вместо экрана оператора). Менять только осознанно.", "Өз алдынча тейлөө режими таразанын бардык иштешин өзгөртөт (оператордун экраны ордуна сатып алуучунун экраны). Аң-сезим менен гана өзгөртүңүз.",
                    "Self-service mode changes how the whole scale works (customer screen instead of the operator screen). Change it only deliberately.", "Self servis modu tüm tartının çalışmasını değiştirir (operatör ekranı yerine müşteri ekranı). Yalnızca bilerek değiştirin.", "O‘zini-o‘zi xizmat ko‘rsatish rejimi butun tarozi ishini o‘zgartiradi (operator ekrani o‘rniga xaridor ekrani). Faqat ongli o‘zgartiring.");
            case 15 or 28:
                return () => Tr.T("Системный параметр весового модуля (не предназначен для изменения администратором). Значение 0 в «Опросе весового модуля» останавливает взвешивание.", "Таразы модулунун системалык параметри (администратор өзгөртүүгө арналган эмес). «Таразы модулун сурамжылоодо» 0 мааниси таразылоону токтотот.",
                    "A system parameter of the weighing module (not meant to be changed by the administrator). Value 0 in “Weighing module polling” stops weighing.", "Tartı modülünün sistem parametresi (yöneticinin değiştirmesi için değil). «Tartı modülü sorgusu»nda 0 değeri tartımı durdurur.", "Tarozi modulining tizim parametri (administrator o‘zgartirishi uchun emas). «Tarozi modulini so‘rash»da 0 qiymat tortishni to‘xtatadi.");
            case 214 or 951 or 952:
                return () => Tr.T("Путь/имя файла, который программа весов открывает сама. Неверное значение — и редактор этикеток или главный экран (сценарий) перестанут открываться.", "Тараза программасы өзү ачкан файлдын жолу/аты. Туура эмес маани — этикетка редактору же башкы экран (сценарий) ачылбай калат.",
                    "A path/file name the scale program opens by itself. A wrong value and the label editor or the main screen (scenario) stops opening.", "Tartı programının kendisinin açtığı yol/dosya adı. Yanlış değer — etiket düzenleyici veya ana ekran (senaryo) açılmaz olur.", "Tarozi dasturi o‘zi ochadigan yo‘l/fayl nomi. Noto‘g‘ri qiymat — yorliq muharriri yoki bosh ekran (ssenariy) ochilmay qoladi.");
            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ каталог

    private static Func<string> T(string ru, string ky, string en, string tr, string uz) => () => Tr.T(ru, ky, en, tr, uz);

    private static Func<string[]> C(params (string Ru, string Ky, string En, string Tr, string Uz)[] items) =>
        () => items.Select(i => Tr.T(i.Ru, i.Ky, i.En, i.Tr, i.Uz)).ToArray();

    private static ShtrikhPcParam P(int n, ShtrikhPcTab tab, ShtrikhPcParamKind kind, string def, int min, int max, Func<string> title, Func<string>? note = null,
        Func<string[]>? choices = null, bool readOnly = false, Func<string>? group = null) =>
        new() { Number = n, Tab = tab, Kind = kind, Default = def, Min = min, Max = max, Title = title, Note = note, Choices = choices, ReadOnly = readOnly, Group = group };

    private static readonly Func<string> GSystem = T("Система", "Система", "System", "Sistem", "Tizim");
    private static readonly Func<string> GWork = T("Рабочий режим", "Иш режими", "Work mode", "Çalışma modu", "Ish rejimi");
    private static readonly Func<string> GBarcodeWeight = T("Весовой и штучный товар", "Салмактуу жана даана товар", "Weighed and piece goods", "Tartılı ve adetli ürün", "Vaznli va donali tovar");
    private static readonly Func<string> GLabel = T("Этикетка", "Этикетка", "Label", "Etiket", "Yorliq");
    private static readonly Func<string> GPrinter = T("Печать", "Басып чыгаруу", "Printing", "Yazdırma", "Chop etish");
    private static readonly Func<string> GQuick = T("Быстрый доступ", "Тез жеткиликтүүлүк", "Quick access", "Hızlı erişim", "Tez kirish");
    private static readonly Func<string> GScreen = T("Экран и кнопки", "Экран жана баскычтар", "Screen and buttons", "Ekran ve düğmeler", "Ekran va tugmalar");
    private static readonly Func<string> GScreenNames = T("Названия виртуальных экранов (самообслуживание)", "Виртуалдык экрандардын аталыштары (өз алдынча тейлөө)", "Virtual screen names (self-service)", "Sanal ekran adları (self servis)", "Virtual ekran nomlari (o‘zini-o‘zi xizmat)");
    private static readonly Func<string> GAds = T("Реклама", "Жарнама", "Advertising", "Reklam", "Reklama");
    private static readonly Func<string> GFiles = T("Файлы обмена", "Алмашуу файлдары", "Exchange files", "Değişim dosyaları", "Almashuv fayllari");
    private static readonly Func<string> GExport = T("Что выгружать (экспорт)", "Эмнени чыгаруу (экспорт)", "What to export", "Neler dışa aktarılır", "Nimani chiqarish (eksport)");
    private static readonly Func<string> GImport = T("Что принимать (импорт)", "Эмнени кабыл алуу (импорт)", "What to import", "Neler içe aktarılır", "Nimani qabul qilish (import)");
    private static readonly Func<string> GSelf = T("Самообслуживание", "Өз алдынча тейлөө", "Self-service", "Self servis", "O‘zini-o‘zi xizmat");
    private static readonly Func<string> GCustomer = T("Экран покупателя (шрифты и цвета)", "Сатып алуучунун экраны (шрифттер жана түстөр)", "Customer screen (fonts and colours)", "Müşteri ekranı (yazı tipleri ve renkler)", "Xaridor ekrani (shriftlar va ranglar)");
    private static readonly Func<string> GVersions = T("Сведения о весах (только чтение)", "Тараза жөнүндө маалымат (окуу гана)", "Scale information (read only)", "Tartı bilgisi (salt okunur)", "Tarozi haqida ma’lumot (faqat o‘qish)");
    private static readonly Func<string> GReports = T("Отчёты и журнал", "Отчёттор жана журнал", "Reports and log", "Raporlar ve günlük", "Hisobotlar va jurnal");

    private static List<ShtrikhPcParam> BuildCatalog()
    {
        var G = ShtrikhPcTab.General; var B = ShtrikhPcTab.Barcode; var Pr = ShtrikhPcTab.Print; var Q = ShtrikhPcTab.Quick; var E = ShtrikhPcTab.Exchange; var R = ShtrikhPcTab.Reports; var A = ShtrikhPcTab.Advanced;
        const ShtrikhPcParamKind Bool = ShtrikhPcParamKind.Bool; const ShtrikhPcParamKind Int = ShtrikhPcParamKind.Int; const ShtrikhPcParamKind Txt = ShtrikhPcParamKind.Text; const ShtrikhPcParamKind Ch = ShtrikhPcParamKind.Choice;
        var formats4 = C(("Код-Вес", "Код-Салмак", "Code-Weight", "Kod-Ağırlık", "Kod-Vazn"), ("Код-Стоимость", "Код-Баа", "Code-Cost", "Kod-Tutar", "Kod-Narx"),
            ("Вес-Код", "Салмак-Код", "Weight-Code", "Ağırlık-Kod", "Vazn-Kod"), ("Стоимость-Код", "Баа-Код", "Cost-Code", "Tutar-Kod", "Narx-Kod"));
        var formatsPiece = C(("Код-Количество", "Код-Саны", "Code-Quantity", "Kod-Adet", "Kod-Miqdor"), ("Код-Стоимость", "Код-Баа", "Code-Cost", "Kod-Tutar", "Kod-Narx"),
            ("Количество-Код", "Саны-Код", "Quantity-Code", "Adet-Kod", "Miqdor-Kod"), ("Стоимость-Код", "Баа-Код", "Cost-Code", "Tutar-Kod", "Narx-Kod"));
        var list = new List<ShtrikhPcParam>
        {
            // ---- Общие параметры (п. 7.3)
            P(1, G, Int, "1", 1, 999, T("Номер весов", "Таразанын номери", "Scale number", "Tartı numarası", "Tarozi raqami"),
                T("Может печататься на этикетке (зависит от формата этикетки).", "Этикеткага басылышы мүмкүн (этикетка форматына көз каранды).", "May be printed on the label (depends on the label format).", "Etikete basılabilir (etiket biçimine bağlı).", "Yorliqqa bosilishi mumkin (yorliq formatiga bog‘liq)."), group: GSystem),
            P(2, G, Txt, "Штрих РС-200", 0, 0, T("Описание весов", "Таразанын сыпаттамасы", "Scale description", "Tartı açıklaması", "Tarozi tavsifi"), group: GSystem),
            P(3, G, Int, "10", 0, 9999, T("Блокировка, минуты (0 — выключена)", "Бөгөттөө, мүнөт (0 — өчүк)", "Lock, minutes (0 — off)", "Kilit, dakika (0 — kapalı)", "Bloklash, daqiqa (0 — o‘chiq)"),
                T("Через столько минут бездействия оператора система блокируется (возврат к регистрации пользователя).", "Оператор ушунча мүнөт аракетсиз болсо, система бөгөттөлөт (колдонуучуну каттоого кайтат).", "After this many idle minutes the system locks (back to user sign-in).", "Operatör bu kadar dakika işlem yapmazsa sistem kilitlenir (kullanıcı girişine döner).", "Operator shuncha daqiqa harakatsiz bo‘lsa, tizim bloklanadi (foydalanuvchi ro‘yxatiga qaytadi)."), group: GSystem),
            P(4, G, Int, "5", 1, 3600, T("Показ рекламы, секунд на картинку", "Жарнама, сүрөткө секунд", "Advertising, seconds per picture", "Reklam, resim başına saniye", "Reklama, rasmga soniya"),
                T("Показ идёт на экране покупателя, пока товар не выбран.", "Көрсөтүү товар тандалмайынча сатып алуучунун экранында жүрөт.", "Shown on the customer screen while no product is selected.", "Ürün seçilmedikçe müşteri ekranında gösterilir.", "Tovar tanlanmaguncha xaridor ekranida ko‘rsatiladi."), group: GSystem),
            P(5, G, Bool, "0", 0, 1, T("Звук", "Үн", "Sound", "Ses", "Tovush"), group: GSystem),
            P(10, G, Bool, "0", 0, 1, T("Курсор мыши", "Чычкан курсору", "Mouse cursor", "Fare imleci", "Sichqoncha kursori"),
                T("Показывать курсор в рабочем режиме и в конфигурации (рекомендуется внешняя мышь и клавиатура).", "Иш режиминде жана конфигурацияда курсорду көрсөтүү (тышкы чычкан жана клавиатура сунушталат).", "Show the cursor in the working mode and in configuration (an external mouse and keyboard are recommended).", "İmleci çalışma modunda ve yapılandırmada göster (harici fare ve klavye önerilir).", "Kursorni ish rejimida va konfiguratsiyada ko‘rsatish (tashqi sichqoncha va klaviatura tavsiya etiladi)."), group: GSystem),
            P(15, G, Int, "250", 0, 9999, T("Опрос весового модуля, мс", "Таразы модулун сурамжылоо, мс", "Weighing module polling, ms", "Tartı modülü sorgusu, ms", "Tarozi modulini so‘rash, ms"),
                T("Рекомендуется 250. 0 — опрос выключен (взвешивание остановится).", "250 сунушталат. 0 — өчүк (таразылоо токтойт).", "250 recommended. 0 — polling off (weighing stops).", "250 önerilir. 0 — sorgu kapalı (tartım durur).", "250 tavsiya etiladi. 0 — o‘chiq (tortish to‘xtaydi)."), group: GSystem),
            P(16, G, Int, "1", 1, 9, T("Коэффициент опроса модуля печати", "Басып чыгаруу модулун сурамжылоо коэффициенти", "Print module polling divider", "Yazdırma modülü sorgu bölleni", "Chop etish modulini so‘rash koeffitsienti"),
                T("Делитель периода опроса принтера относительно опроса весов; 1 — каждые 250 мс.", "Принтерди сурамжылоо мезгилинин бөлгүчү; 1 — ар 250 мс.", "Divider of the printer polling period; 1 — every 250 ms.", "Yazıcı sorgu periyodunun bölleni; 1 — her 250 ms.", "Printerni so‘rash davrining bo‘luvchisi; 1 — har 250 ms."), group: GSystem),
            P(17, G, Bool, "1", 0, 1, T("Выключение питания", "Кубатты өчүрүү", "Power off", "Güç kapatma", "Quvvatni o‘chirish"),
                T("1 — после выхода из программы выключается компьютер весов; 0 — выход в ОС.", "1 — программадан чыккандан кийин таразанын компьютери өчөт; 0 — ОСко чыгуу.", "1 — the scale computer shuts down after leaving the program; 0 — exit to the OS.", "1 — programdan çıkınca tartı bilgisayarı kapanır; 0 — işletim sistemine çıkış.", "1 — dasturdan chiqqach tarozi kompyuteri o‘chadi; 0 — OTga chiqish."), group: GSystem),
            P(27, G, Bool, "0", 0, 1, T("Режим самообслуживания", "Өз алдынча тейлөө режими", "Self-service mode", "Self servis modu", "O‘zini-o‘zi xizmat rejimi"),
                T("Включает режим самообслуживания (кнопка «Самообслуживание» в «Система и печать»).", "Өз алдынча тейлөө режимин күйгүзөт («Система и печать» ичиндеги «Самообслуживание» баскычы).", "Turns self-service on (the “Self-service” button in “System and printing”).", "Self servisi açar («Система и печать»teki «Самообслуживание» düğmesi).", "O‘zini-o‘zi xizmatni yoqadi («Система и печать»dagi «Самообслуживание» tugmasi)."), group: GSystem),
            P(7, G, Ch, "0", 0, 2, T("Сброс выбранного товара", "Тандалган товарды ыргытуу", "Reset of the selected product", "Seçili ürünü sıfırlama", "Tanlangan tovarni tashlash"),
                choices: C(("Нет", "Жок", "No", "Hayır", "Yo‘q"), ("После печати", "Басып чыгаргандан кийин", "After printing", "Yazdırmadan sonra", "Chop etgandan keyin"), ("Через заданное время бездействия", "Бекитилген аракетсиздиктен кийин", "After the idle time set", "Belirlenen boşta kalma süresinden sonra", "Belgilangan harakatsizlikdan keyin")), group: GWork),
            P(8, G, Int, "0", 0, 99999, T("Время простоя перед сбросом товара", "Товарды ыргытуудан мурунку токтоо убактысы", "Idle time before the product reset", "Ürün sıfırlamadan önceki bekleme süresi", "Tovarni tashlashdan oldingi kutish vaqti"),
                T("Работает при «Сброс выбранного товара» = «Через заданное время».", "«Тандалган товарды ыргытуу» = «Бекитилген убакыттан кийин» болгондо иштейт.", "Works when “Reset of the selected product” = after the idle time.", "«Seçili ürünü sıfırlama» = belirlenen süre olduğunda çalışır.", "«Tanlangan tovarni tashlash» = belgilangan vaqtdan keyin bo‘lganda ishlaydi."), group: GWork),
            P(9, G, Ch, "0", 0, 2, T("Просмотр базы товаров по умолчанию", "Товарлар базасын демейки көрүү", "Default view of the goods database", "Ürün veritabanı varsayılan görünümü", "Tovarlar bazasini standart ko‘rish"),
                choices: C(("Быстрый доступ", "Тез жеткиликтүүлүк", "Quick access", "Hızlı erişim", "Tez kirish"), ("Табличный просмотр", "Таблица менен көрүү", "Table view", "Tablo görünümü", "Jadval ko‘rinishi"), ("Поиск", "Издөө", "Search", "Arama", "Qidiruv")), group: GWork),
            P(12, G, Int, "2", 0, 3, T("Число десятичных знаков цены и стоимости", "Баанын жана наркынын ондук белгилеринин саны", "Number of decimal places of price and cost", "Fiyat ve tutarın ondalık hane sayısı", "Narx va qiymatning o‘nlik xonalari soni"),
                T("Влияет на формат цены в файле и на ПРЕДЕЛ цены: при 2 знаках — до 99 999,99, при 3 — до 9 999,999, при 0 — до 9 999 999. Должно совпадать с «Знаков в цене» кассы.", "Файлдагы баа форматына жана баанын ЧЕГИНЕ таасир этет: 2 белгиде — 99 999,99 га чейин, 3 те — 9 999,999 га чейин, 0 до — 9 999 999 га чейин. Кассанын «Баадагы белгилер» менен дал келиши керек.", "Affects the price format in the file and the price LIMIT: with 2 decimals up to 99,999.99, with 3 up to 9,999.999, with 0 up to 9,999,999. Must match the till's “Price decimals”.", "Dosyadaki fiyat biçimini ve fiyat SINIRINI etkiler: 2 hanede 99.999,99'a, 3 hanede 9.999,999'a, 0 hanede 9.999.999'a kadar. Kasadaki «Fiyat ondalığı» ile aynı olmalı.", "Fayldagi narx formatiga va narx CHEGARASIGA ta’sir qiladi: 2 xonada 99 999,99 gacha, 3 xonada 9 999,999 gacha, 0 xonada 9 999 999 gacha. Kassadagi «Narxdagi xonalar» bilan mos bo‘lishi kerak."), group: GWork),
            P(14, G, Bool, "0", 0, 1, T("Переход к описанию штучного товара", "Даана товардын сыпаттамасына өтүү", "Go to the piece-product description", "Adetli ürün açıklamasına geçiş", "Donali tovar tavsifiga o‘tish"),
                T("Автоматический переход к вводу количества штучного товара при его выборе.", "Даана товар тандалганда санын киргизүүгө автоматтык өтүү.", "Automatic switch to entering the quantity when a piece product is selected.", "Adetli ürün seçildiğinde adet girişine otomatik geçiş.", "Donali tovar tanlanganda miqdorni kiritishga avtomatik o‘tish."), group: GWork),
            P(25, G, Int, "0", 0, 3, T("Набор фоновых картинок", "Фон сүрөттөрдүн топтому", "Background picture set", "Arka plan resim seti", "Fon rasmlari to‘plami"), group: GWork),
            P(26, G, Ch, "0", 0, 1, T("Цветовая схема", "Түс схемасы", "Colour scheme", "Renk şeması", "Ranglar sxemasi"),
                choices: C(("Тёмная", "Караңгы", "Dark", "Koyu", "Qorong‘i"), ("Светлая", "Жарык", "Light", "Açık", "Yorug‘")), group: GWork),

            // ---- Штрих-код (п. 7.6)
            P(301, B, Ch, "0", 0, 0, T("Тип штрих-кода", "Штрих-код түрү", "Barcode type", "Barkod türü", "Shtrix-kod turi"), choices: C(("EAN-13", "EAN-13", "EAN-13", "EAN-13", "EAN-13")), group: GBarcodeWeight),
            P(302, B, Bool, "1", 0, 1, T("Дополнять штрих-код нулями до 13 знаков", "Штрих-кодду 13 белгиге чейин нөлдөр менен толтуруу", "Pad the barcode with zeros to 13 digits", "Barkodu 13 haneye sıfırlarla tamamla", "Shtrix-kodni 13 raqamgacha nollar bilan to‘ldirish"), group: GBarcodeWeight),
            P(303, B, Int, "20", 20, 29, T("Префикс штрих-кода весового товара", "Салмактуу товардын штрих-код префикси", "Weighed product barcode prefix", "Tartılı ürün barkod öneki", "Vaznli tovar shtrix-kod prefiksi"),
                T("20…29. Касса читает весовые этикетки по этому префиксу.", "20…29. Касса салмактуу этикеткаларды ушул префикс боюнча окуйт.", "20…29. The till reads weight labels by this prefix.", "20…29. Kasa tartı etiketlerini bu önekle okur.", "20…29. Kassa vazn yorliqlarini shu prefiks bo‘yicha o‘qiydi."), group: GBarcodeWeight),
            P(304, B, Int, "21", 20, 29, T("Префикс штрих-кода штучного товара", "Даана товардын штрих-код префикси", "Piece product barcode prefix", "Adetli ürün barkod öneki", "Donali tovar shtrix-kod prefiksi"),
                T("20…29. Не должен совпадать с префиксом весового.", "20…29. Салмактуунун префикси менен дал келбеши керек.", "20…29. Must differ from the weighed prefix.", "20…29. Tartılı önekle aynı olmamalı.", "20…29. Vaznli prefiks bilan bir xil bo‘lmasligi kerak."), group: GBarcodeWeight),
            P(305, B, Ch, "1", 0, 3, T("Формат штрих-кода весового товара", "Салмактуу товардын штрих-код форматы", "Weighed product barcode format", "Tartılı ürün barkod biçimi", "Vaznli tovar shtrix-kod formati"), choices: formats4, group: GBarcodeWeight),
            P(306, B, Ch, "1", 0, 3, T("Формат штрих-кода штучного товара", "Даана товардын штрих-код форматы", "Piece product barcode format", "Adetli ürün barkod biçimi", "Donali tovar shtrix-kod formati"), choices: formatsPiece, group: GBarcodeWeight),
            P(307, B, Int, "5", 2, 6, T("Число символов кода товара в штрих-коде (с КС)", "Штрих-коддогу товар кодунун белгилеринин саны (КС менен)", "Number of product code characters in the barcode (with check digit)", "Barkoddaki ürün kodu karakter sayısı (KS dahil)", "Shtrix-koddagi tovar kodi belgilari soni (KS bilan)"),
                T("На код и значение вместе остаётся 10 знаков: при 6 знаках кода на стоимость/вес — всего 4 (стоимость до 99,99, вес до 9,999 кг).", "Код жана маани бирге 10 белгиде калат: коддун 6 белгисинде наркка/салмакка бардыгы 4 (баа 99,99 га чейин, салмак 9,999 кг га чейин).", "Code and value share 10 characters: with a 6-digit code only 4 remain for cost/weight (cost up to 99.99, weight up to 9.999 kg).", "Kod ve değer birlikte 10 karakterdir: 6 haneli kodda tutar/ağırlık için yalnızca 4 kalır (tutar 99,99'a, ağırlık 9,999 kg'a kadar).", "Kod va qiymat birgalikda 10 belgi: 6 xonali kodda qiymat/vaznga atigi 4 qoladi (qiymat 99,99 gacha, vazn 9,999 kg gacha)."), group: GBarcodeWeight),
            P(308, B, Bool, "0", 0, 1, T("КС кода товара", "Товар кодунун КС", "Product code check digit", "Ürün kodu KS", "Tovar kodi KS"),
                T("Контрольная сумма кода товара справа от кода: число знаков кода уменьшается на единицу.", "Товар кодунун контролдук суммасы коддун оң жагында: коддун белгилеринин саны бирге азаят.", "A check sum of the product code to the right of the code: the number of code digits decreases by one.", "Ürün kodunun kontrol toplamı kodun sağında: kod hane sayısı bir azalır.", "Tovar kodining nazorat yig‘indisi kodning o‘ng tomonida: kod xonalari soni bittaga kamayadi."), group: GBarcodeWeight),
            P(39, B, Ch, "0", 0, 1, T("Печать штрих-кода из базы товаров", "Товарлар базасынан штрих-код басуу", "Print the barcode from the goods database", "Barkodu ürün veritabanından yazdır", "Shtrix-kodni tovarlar bazasidan chop etish"),
                choices: C(("Печатать весовой штрих-код", "Салмактуу штрих-кодду басуу", "Print the weight barcode", "Tartı barkodunu yazdır", "Vazn shtrix-kodini chop etish"), ("Печатать штрих-код из базы", "Базадагы штрих-кодду басуу", "Print the barcode from the database", "Veritabanındaki barkodu yazdır", "Bazadagi shtrix-kodni chop etish")), group: GBarcodeWeight),

            // ---- Печать (п. 7.7)
            P(201, Pr, Txt, "", 0, 0, T("Заголовок этикетки", "Этикетканын башкы сабы", "Label header", "Etiket başlığı", "Yorliq sarlavhasi"),
                T("Печатается вместо названия, когда товар не выбран. Зависит от формата этикетки.", "Товар тандалбаса, аталыштын ордуна басылат. Этикетка форматына көз каранды.", "Printed instead of the name when no product is selected. Depends on the label format.", "Ürün seçilmediğinde adın yerine basılır. Etiket biçimine bağlı.", "Tovar tanlanmaganda nom o‘rniga bosiladi. Yorliq formatiga bog‘liq."), group: GLabel),
            P(202, Pr, Txt, "", 0, 0, T("Название магазина на этикетке", "Этикеткадагы дүкөндүн аталышы", "Shop name on the label", "Etiketteki mağaza adı", "Yorliqdagi do‘kon nomi"), group: GLabel),
            P(203, Pr, Txt, "0.2", 0, 0, T("Вес автопечати, кг (порог)", "Автобасуу салмагы, кг (босого)", "Auto-print weight, kg (threshold)", "Otomatik yazdırma ağırlığı, kg (eşik)", "Avtochop vazni, kg (chegara)"),
                T("Порог срабатывания автопечати: меньший вес не печатается (от НмПВ до НПВ).", "Автобасуунун иштөө босогосу: аз салмак басылбайт (НмПВдан НПВга чейин).", "Auto-print threshold: a smaller weight is not printed (from min to max capacity).", "Otomatik yazdırma eşiği: daha küçük ağırlık yazdırılmaz (alt ve üst sınır arasında).", "Avtochop chegarasi: kichikroq vazn chop etilmaydi (eng kichik va eng katta vazn orasida)."), group: GLabel),
            P(213, Pr, Int, "0", 0, 99999, T("Выбранный код формата этикетки", "Тандалган этикетка форматынын коду", "Selected label format code", "Seçili etiket biçimi kodu", "Tanlangan yorliq formati kodi"),
                T("Выбирается на весах в «Система и печать»; содержит код записи из базы форматов этикеток.", "Таразада «Система и печать» ичинде тандалат; этикетка форматтарынын базасындагы жазуунун кодун камтыйт.", "Chosen on the scale in “System and printing”; holds the record code from the label format database.", "Tartıda «Система и печать»te seçilir; etiket biçimi veritabanındaki kayıt kodunu içerir.", "Tarozida «Система и печать»da tanlanadi; yorliq formatlari bazasidagi yozuv kodini saqlaydi."), group: GLabel),
            P(215, Pr, Int, "0", 0, 9999, T("Номер этикетки (сквозной, автоинкремент)", "Этикетка номери (өтмө, авто өсүү)", "Label number (running, auto-increment)", "Etiket numarası (sürekli, otomatik artar)", "Yorliq raqami (uzluksiz, avtomatik oshadi)"), group: GLabel),
            P(214, Pr, Txt, @"Shared\Labels\LabelEditor.exe", 0, 0, T("Файл редактора этикеток", "Этикетка редакторунун файлы", "Label editor file", "Etiket düzenleyici dosyası", "Yorliq muharriri fayli"),
                T("Путь и имя файла относительно pcscale.exe.", "pcscale.exe га салыштырмалуу жол жана файл аты.", "Path and file name relative to pcscale.exe.", "pcscale.exe'ye göre yol ve dosya adı.", "pcscale.exe ga nisbatan yo‘l va fayl nomi."), group: GLabel),
            P(208, Pr, Ch, "0", 0, 1, T("Тип печати", "Басып чыгаруу түрү", "Print type", "Yazdırma türü", "Chop etish turi"),
                choices: C(("Этикетка", "Этикетка", "Label", "Etiket", "Yorliq"), ("Лента", "Тасма", "Tape", "Şerit", "Lenta")), group: GPrinter),
            P(209, Pr, Bool, "1", 0, 1, T("Блокировка печати", "Басып чыгарууну бөгөттөө", "Print lock", "Yazdırma kilidi", "Chop etishni bloklash"), group: GPrinter),
            P(210, Pr, Bool, "0", 0, 1, T("Автопечать", "Автобасуу", "Auto-print", "Otomatik yazdırma", "Avtochop"), group: GPrinter),
            P(211, Pr, Int, "8", 1, 15, T("Яркость печати (1–15)", "Басып чыгаруу жарыктыгы (1–15)", "Print brightness (1–15)", "Yazdırma parlaklığı (1–15)", "Chop etish yorqinligi (1–15)"),
                T("Настройка печатающего устройства; настраивается и на весах в «Система и печать».", "Басып чыгаруучу түзмөктүн жөндөөсү; таразада «Система и печать» ичинде да жөндөлөт.", "A printer setting; also set on the scale in “System and printing”.", "Yazıcı ayarı; tartıda «Система и печать»te de ayarlanır.", "Chop etish qurilmasi sozlamasi; tarozida «Система и печать»da ham sozlanadi."), group: GPrinter),

            // ---- Быстрый доступ и экран (п. 7.5, 7.10)
            P(401, Q, Bool, "1", 0, 1, T("Разрешить менять коды клавиш быстрого доступа в рабочем режиме", "Иш режиминде тез жеткиликтүүлүк баскычтарынын коддорун өзгөртүүгө уруксат", "Allow changing quick-access key codes in the working mode", "Çalışma modunda hızlı erişim tuşu kodlarının değiştirilmesine izin ver", "Ish rejimida tez kirish tugmalari kodlarini o‘zgartirishga ruxsat"), group: GQuick),
            P(402, Q, Int, "3", 0, 3, T("Число виртуальных экранов клавиш быстрого доступа (0–3)", "Тез жеткиликтүүлүк баскычтарынын виртуалдык экрандарынын саны (0–3)", "Number of virtual quick-access screens (0–3)", "Hızlı erişim sanal ekran sayısı (0–3)", "Tez kirish virtual ekranlari soni (0–3)"), group: GQuick),
            P(403, Q, Bool, "1", 0, 1, T("Не показывать номера на клавишах быстрого доступа", "Тез жеткиликтүүлүк баскычтарында номерлерди көрсөтпөө", "Do not show numbers on quick-access keys", "Hızlı erişim tuşlarında numaraları gösterme", "Tez kirish tugmalarida raqamlarni ko‘rsatmaslik"), group: GQuick),
            P(31, Q, Bool, "1", 0, 1, T("Показывать кнопку «>0<» (ноль)", "«>0<» (нөл) баскычын көрсөтүү", "Show the “>0<” (zero) button", "«>0<» (sıfır) düğmesini göster", "«>0<» (nol) tugmasini ko‘rsatish"), group: GScreen),
            P(32, Q, Bool, "1", 0, 1, T("Показывать кнопку «>T<» (тара)", "«>T<» (тара) баскычын көрсөтүү", "Show the “>T<” (tare) button", "«>T<» (dara) düğmesini göster", "«>T<» (tara) tugmasini ko‘rsatish"), group: GScreen),
            P(40, Q, Bool, "1", 0, 1, T("Показывать кнопку «Печать»", "«Печать» баскычын көрсөтүү", "Show the “Print” button", "«Печать» düğmesini göster", "«Печать» tugmasini ko‘rsatish"), group: GScreen),
            P(41, Q, Bool, "1", 0, 1, T("Показывать кнопку «Промотка»", "«Промотка» баскычын көрсөтүү", "Show the “Feed” button", "«Промотка» düğmesini göster", "«Промотка» tugmasini ko‘rsatish"), group: GScreen),
            P(700, Q, Bool, "0", 0, 1, T("Показ рекламы", "Жарнаманы көрсөтүү", "Show advertising", "Reklamı göster", "Reklamani ko‘rsatish"), group: GAds),
            P(702, Q, Int, "60", 0, 99999, T("Через сколько секунд включать рекламу", "Жарнаманы канча секунддан кийин күйгүзүү", "Seconds before advertising starts", "Reklam kaç saniye sonra başlar", "Reklama necha soniyadan keyin yoqiladi"), group: GAds),
            P(703, Q, Int, "5", 1, 3600, T("Время смены слайдов рекламы, секунд", "Жарнама слайддарын алмаштыруу убактысы, секунд", "Advertising slide change time, seconds", "Reklam slayt değişim süresi, saniye", "Reklama slaydlarini almashtirish vaqti, soniya"), group: GAds),
            P(704, Q, Bool, "0", 0, 1, T("Реклама на весь экран при самообслуживании", "Өз алдынча тейлөөдө жарнама бүт экранга", "Full-screen advertising in self-service", "Self serviste tam ekran reklam", "O‘zini-o‘zi xizmatda to‘liq ekran reklama"), group: GAds),

            // ---- Обмен файлами (п. 7.4)
            P(6, E, Txt, @"Shared\", 0, 0, T("Папка импорта-экспорта", "Импорт-экспорт папкасы", "Import/export folder", "İçe/dışa aktarma klasörü", "Import/eksport papkasi"), T("Папка весов, в которую касса кладёт файлы. Пути ниже — относительно неё.", "Касса файлдарды салган таразанын папкасы. Төмөнкү жолдор ага салыштырмалуу.", "The scale folder where the till puts files. The paths below are relative to it.", "Kasanın dosya koyduğu tartı klasörü. Aşağıdaki yollar buna göredir.", "Kassa fayllarni qo‘yadigan tarozi papkasi. Quyidagi yo‘llar unga nisbatan."), group: GFiles),
            P(801, E, Txt, @"Import\PCScale.txt", 0, 0, T("Имя файла импорта", "Импорт файлынын аты", "Import file name", "İçe aktarma dosya adı", "Import fayli nomi"), group: GFiles),
            P(802, E, Txt, @"Export\Export.txt", 0, 0, T("Имя файла экспорта", "Экспорт файлынын аты", "Export file name", "Dışa aktarma dosya adı", "Eksport fayli nomi"), group: GFiles),
            P(803, E, Txt, @"Import\PCScale.flz", 0, 0, T("Имя флаг-файла импорта", "Импорттун желек-файлынын аты", "Import flag file name", "İçe aktarma bayrak dosyası adı", "Import bayroq-fayli nomi"), group: GFiles),
            P(11, E, Int, "10", 1, 9999, T("Обновление базы из файла импорта, минуты", "Базаны импорт файлынан жаңыртуу, мүнөт", "Database update from the import file, minutes", "İçe aktarma dosyasından veritabanı güncelleme, dakika", "Bazani import faylidan yangilash, daqiqa"),
                T("Как часто весы проверяют флаг-файл. В кассе это «Обновление» — тот же срок ожидания.", "Тараза желек-файлды канчалык көп текшерет. Кассада бул «Обновление» — ошол эле күтүү мөөнөтү.", "How often the scale checks the flag file. The same waiting time as “Update” in the till.", "Tartının bayrak dosyasını ne sıklıkla kontrol ettiği. Kasadaki «Обновление» ile aynı bekleme süresi.", "Tarozi bayroq-faylni qanchalik tez-tez tekshiradi. Kassadagi «Обновление» bilan bir xil kutish vaqti."), group: GFiles),
            P(804, E, Bool, "1", 0, 1, T("Экспорт конфигурации", "Конфигурацияны экспорттоо", "Export configuration", "Yapılandırmayı dışa aktar", "Konfiguratsiyani eksport"), T("Нужен, чтобы касса могла прочитать настройки весов из Export\\Export.txt.", "Касса таразанын жөндөөлөрүн Export\\Export.txt тен окуу үчүн керек.", "Needed for the till to read the scale settings from Export\\Export.txt.", "Kasanın tartı ayarlarını Export\\Export.txt'den okuyabilmesi için gerekir.", "Kassa tarozi sozlamalarini Export\\Export.txt dan o‘qishi uchun kerak."), group: GExport),
            P(805, E, Bool, "1", 0, 1, T("Экспорт тем помощи", "Жардам темаларын экспорттоо", "Export help topics", "Yardım konularını dışa aktar", "Yordam mavzularini eksport"), group: GExport),
            P(806, E, Bool, "1", 0, 1, T("Экспорт списка пользователей", "Колдонуучулар тизмесин экспорттоо", "Export the user list", "Kullanıcı listesini dışa aktar", "Foydalanuvchilar ro‘yxatini eksport"), group: GExport),
            P(807, E, Bool, "1", 0, 1, T("Экспорт товаров", "Товарларды экспорттоо", "Export goods", "Ürünleri dışa aktar", "Tovarlarni eksport"), group: GExport),
            P(808, E, Bool, "1", 0, 1, T("Экспорт этикеток", "Этикеткаларды экспорттоо", "Export labels", "Etiketleri dışa aktar", "Yorliqlarni eksport"), group: GExport),
            P(814, E, Bool, "0", 0, 1, T("Импорт конфигурации", "Конфигурацияны импорттоо", "Import configuration", "Yapılandırmayı içe aktar", "Konfiguratsiyani import"), T("Нужен, чтобы касса могла менять настройки весов файлом (и назначать кнопки быстрого доступа).", "Касса таразанын жөндөөлөрүн файл менен өзгөртүүсү (жана тез жеткиликтүүлүк баскычтарын дайындоосу) үчүн керек.", "Needed for the till to change scale settings with a file (and to assign quick-access buttons).", "Kasanın tartı ayarlarını dosyayla değiştirebilmesi (ve hızlı erişim düğmelerini atayabilmesi) için gerekir.", "Kassa tarozi sozlamalarini fayl bilan o‘zgartirishi (va tez kirish tugmalarini belgilashi) uchun kerak."), group: GImport),
            P(815, E, Bool, "1", 0, 1, T("Импорт тем помощи", "Жардам темаларын импорттоо", "Import help topics", "Yardım konularını içe aktar", "Yordam mavzularini import"), group: GImport),
            P(816, E, Bool, "0", 0, 1, T("Импорт списка пользователей", "Колдонуучулар тизмесин импорттоо", "Import the user list", "Kullanıcı listesini içe aktar", "Foydalanuvchilar ro‘yxatini import"), group: GImport),
            P(817, E, Bool, "1", 0, 1, T("Импорт товаров", "Товарларды импорттоо", "Import goods", "Ürünleri içe aktar", "Tovarlarni import"), group: GImport),
            P(818, E, Bool, "1", 0, 1, T("Импорт этикеток", "Этикеткаларды импорттоо", "Import labels", "Etiketleri içe aktar", "Yorliqlarni import"), group: GImport),
            P(819, E, Bool, "1", 0, 1, T("Импорт ресурсов (картинки)", "Ресурстарды импорттоо (сүрөттөр)", "Import resources (pictures)", "Kaynakları içe aktar (resimler)", "Resurslarni import (rasmlar)"), group: GImport),

            // ---- Отчётность (п. 7.9)
            P(601, R, Bool, "1", 0, 1, T("Сохранять отчёты", "Отчёттарды сактоо", "Save reports", "Raporları kaydet", "Hisobotlarni saqlash"), group: GReports),
            P(602, R, Txt, @"Export\ReportExport.txt", 0, 0, T("Имя файла выгрузки отчётов", "Отчёттор файлынын аты", "Report export file name", "Rapor dışa aktarma dosya adı", "Hisobotlar fayli nomi"), group: GReports),
            P(603, R, Int, "30", 1, 9999, T("Хранить отчёты, дней", "Отчёттарды сактоо, күн", "Keep reports, days", "Raporları sakla, gün", "Hisobotlarni saqlash, kun"), group: GReports),
            P(604, R, Bool, "1", 0, 1, T("Проверять флаг экспорта отчётов", "Отчёттарды экспорттоо желегин текшерүү", "Check the report export flag", "Rapor dışa aktarma bayrağını kontrol et", "Hisobotlar eksporti bayrog‘ini tekshirish"), group: GReports),
            P(605, R, Txt, @"Export\ReportExport.flz", 0, 0, T("Имя флаг-файла экспорта отчётов", "Отчёттарды экспорттоо желек-файлынын аты", "Report export flag file name", "Rapor dışa aktarma bayrak dosyası adı", "Hisobotlar eksporti bayroq-fayli nomi"), group: GReports),
            P(606, R, Bool, "0", 0, 1, T("Вести лог-файл", "Лог-файлды жүргүзүү", "Keep a log file", "Günlük dosyası tut", "Log-fayl yuritish"), group: GReports),
            P(607, R, Int, "30", 1, 9999, T("Хранить записи лог-файла, дней", "Лог-файл жазууларын сактоо, күн", "Keep log records, days", "Günlük kayıtlarını sakla, gün", "Log yozuvlarini saqlash, kun"), group: GReports),

            // ---- Дополнительно: самообслуживание (п. 7.10, Паспорт версий 2.1.3.7–2.1.3.9)
            P(951, A, Txt, @"SelfScenario\", 0, 0, T("Папка сценариев самообслуживания", "Өз алдынча тейлөө сценарийлеринин папкасы", "Self-service scenario folder", "Self servis senaryo klasörü", "O‘zini-o‘zi xizmat ssenariylari papkasi"), group: GSelf),
            P(952, A, Txt, "", 0, 0, T("Файл сценария (главный экран)", "Сценарий файлы (башкы экран)", "Scenario file (main screen)", "Senaryo dosyası (ana ekran)", "Ssenariy fayli (bosh ekran)"),
                T("Касса пересобирает этот файл при выгрузке товаров (у владельца Semya.ssc).", "Касса товарларды жүктөөдө бул файлды кайра түзөт (ээсинде Semya.ssc).", "The till rebuilds this file when uploading goods (Semya.ssc at the owner's).", "Kasa ürün yüklerken bu dosyayı yeniden kurar (sahibinde Semya.ssc).", "Kassa tovar yuklaganda bu faylni qayta quradi (egasida Semya.ssc)."), group: GSelf),
            P(953, A, Bool, "0", 0, 1, T("Перезагружать сценарий после импорта", "Импорттон кийин сценарийди кайра жүктөө", "Reload the scenario after import", "İçe aktarmadan sonra senaryoyu yeniden yükle", "Importdan keyin ssenariyni qayta yuklash"),
                T("С флаг-файлом ниже весы перечитывают главный экран сами после загрузки товаров.", "Төмөнкү желек-файл менен тараза товарларды жүктөгөндөн кийин башкы экранды өзү кайра окуйт.", "With the flag file below the scale re-reads the main screen by itself after loading goods.", "Aşağıdaki bayrak dosyasıyla tartı ürünler yüklendikten sonra ana ekranı kendisi yeniden okur.", "Quyidagi bayroq-fayl bilan tarozi tovarlar yuklangach bosh ekranni o‘zi qayta o‘qiydi."), group: GSelf),
            P(954, A, Txt, "flag", 0, 0, T("Имя флаг-файла перезагрузки сценария", "Сценарийди кайра жүктөө желек-файлынын аты", "Scenario reload flag file name", "Senaryo yeniden yükleme bayrak dosyası adı", "Ssenariyni qayta yuklash bayroq-fayli nomi"),
                T("Касса кладёт пустой файл с этим именем в папку обмена и в папку сценариев. Должно совпадать с «Имя флага» в «Дополнительно» кассы.", "Касса ушул аттагы бош файлды алмашуу папкасына жана сценарийлер папкасына салат. Кассанын «Дополнительно» ичиндеги «Желек аты» менен дал келиши керек.", "The till puts an empty file with this name into the exchange folder and the scenarios folder. Must match the till's flag name in “Advanced”.", "Kasa bu adla boş bir dosyayı değişim klasörüne ve senaryo klasörüne koyar. Kasadaki «Gelişmiş»teki bayrak adıyla aynı olmalı.", "Kassa shu nomli bo‘sh faylni almashuv papkasiga va ssenariylar papkasiga qo‘yadi. Kassadagi «Qo‘shimcha»dagi bayroq nomi bilan mos bo‘lishi kerak."), group: GSelf),
            P(955, A, Bool, "0", 0, 1, T("Переход на главный экран при бездействии", "Аракетсиздикте башкы экранга өтүү", "Go to the main screen when idle", "Boştayken ana ekrana geç", "Harakatsizlikda bosh ekranga o‘tish"), group: GSelf),
            P(956, A, Int, "60", 1, 99999, T("Время до перехода на главный экран, секунд", "Башкы экранга өтүүгө чейинки убакыт, секунд", "Seconds before going to the main screen", "Ana ekrana geçmeden önceki süre, saniye", "Bosh ekranga o‘tishgacha vaqt, soniya"), group: GSelf),
            P(957, A, Int, "1", 0, 1, T("Переход после печати в самообслуживании (0/1)", "Өз алдынча тейлөөдө басып чыгаргандан кийинки өтүү (0/1)", "Move after printing in self-service (0/1)", "Self serviste yazdırmadan sonra geçiş (0/1)", "O‘zini-o‘zi xizmatda chop etgandan keyingi o‘tish (0/1)"),
                T("На главный экран или на последний — переключатель из Паспорта версий 2.1.3.9; у владельца 1.", "Башкы экранга же акыркыга — 2.1.3.9 версиялар паспортунан которгуч; ээсинде 1.", "Main screen or the last one — a switch from the Version Passport 2.1.3.9; the owner has 1.", "Ana ekrana veya sonuncuya — Sürüm Pasaportu 2.1.3.9'dan anahtar; sahibinde 1.", "Bosh ekranga yoki oxirgisiga — Versiyalar pasporti 2.1.3.9 dagi o‘tkazgich; egasida 1."), group: GSelf),
            P(901, A, Int, "7", 0, 99, T("Размер шрифта названия товара", "Товар аталышынын шрифт өлчөмү", "Product name font size", "Ürün adı yazı boyutu", "Tovar nomi shrift o‘lchami"), group: GCustomer),
            P(902, A, Int, "1", 0, 99, T("Цвет шрифта названия товара", "Товар аталышынын шрифт түсү", "Product name font colour", "Ürün adı yazı rengi", "Tovar nomi shrift rangi"), group: GCustomer),
            P(903, A, Int, "3", 0, 99, T("Размер шрифта краткого описания", "Кыскача сыпаттаманын шрифт өлчөмү", "Short description font size", "Kısa açıklama yazı boyutu", "Qisqa tavsif shrift o‘lchami"), group: GCustomer),
            P(904, A, Int, "0", 0, 99, T("Цвет шрифта краткого описания", "Кыскача сыпаттаманын шрифт түсү", "Short description font colour", "Kısa açıklama yazı rengi", "Qisqa tavsif shrift rangi"), group: GCustomer),
            P(905, A, Int, "0", 0, 99, T("Размер шрифта полного описания", "Толук сыпаттаманын шрифт өлчөмү", "Full description font size", "Tam açıklama yazı boyutu", "To‘liq tavsif shrift o‘lchami"), group: GCustomer),
            P(906, A, Int, "0", 0, 99, T("Цвет шрифта полного описания", "Толук сыпаттаманын шрифт түсү", "Full description font colour", "Tam açıklama yazı rengi", "To‘liq tavsif shrift rangi"), group: GCustomer),
            P(907, A, Int, "15", 0, 99, T("Цвет фона полного описания", "Толук сыпаттаманын фон түсү", "Full description background colour", "Tam açıklama arka plan rengi", "To‘liq tavsif fon rangi"), group: GCustomer),
            P(28, A, Int, "10", 0, 999, T("Число попыток установки нуля", "Нөлдү коюу аракеттеринин саны", "Number of zeroing attempts", "Sıfırlama deneme sayısı", "Nolga qo‘yish urinishlari soni"), group: GSystem),

            // ---- Сведения о весах (только чтение; п. 6.3, параметры 18–24)
            P(18, A, Txt, "", 0, 0, T("Версия ПО", "ПО версиясы", "Software version", "Yazılım sürümü", "DT versiyasi"), readOnly: true, group: GVersions),
            P(19, A, Txt, "", 0, 0, T("Версия драйвера весового устройства", "Таразы түзмөгүнүн драйверинин версиясы", "Weighing device driver version", "Tartı cihazı sürücü sürümü", "Tarozi qurilmasi drayveri versiyasi"), readOnly: true, group: GVersions),
            P(20, A, Txt, "", 0, 0, T("Версия драйвера принтера", "Принтер драйверинин версиясы", "Printer driver version", "Yazıcı sürücü sürümü", "Printer drayveri versiyasi"), readOnly: true, group: GVersions),
            P(21, A, Txt, "", 0, 0, T("Версия весового устройства", "Таразы түзмөгүнүн версиясы", "Weighing device version", "Tartı cihazı sürümü", "Tarozi qurilmasi versiyasi"), readOnly: true, group: GVersions),
            P(22, A, Txt, "", 0, 0, T("Версия UI.DLL", "UI.DLL версиясы", "UI.DLL version", "UI.DLL sürümü", "UI.DLL versiyasi"), readOnly: true, group: GVersions),
            P(23, A, Txt, "", 0, 0, T("Версия CreateLabel.DLL", "CreateLabel.DLL версиясы", "CreateLabel.DLL version", "CreateLabel.DLL sürümü", "CreateLabel.DLL versiyasi"), readOnly: true, group: GVersions),
            P(24, A, Txt, "", 0, 0, T("Версия принтера", "Принтердин версиясы", "Printer version", "Yazıcı sürümü", "Printer versiyasi"), readOnly: true, group: GVersions),
        };
        // 450–469: названия виртуальных экранов быстрого доступа (режим самообслуживания), по умолчанию «Группа товаров 1…20» (п. 7.5)
        for (var i = 0; i < 20; i++)
        {
            var n = i + 1;
            list.Add(P(450 + i, Q, Txt, "Группа товаров " + n.ToString(CultureInfo.InvariantCulture), 0, 0,
                T($"Название виртуального экрана {n}", $"Виртуалдык экрандын аталышы {n}", $"Virtual screen {n} name", $"Sanal ekran {n} adı", $"Virtual ekran {n} nomi"), group: GScreenNames));
        }
        return list;
    }

    private static readonly List<ShtrikhPcParam> CatalogList = BuildCatalog();

    public static IReadOnlyList<ShtrikhPcParam> Catalog => CatalogList;

    public static ShtrikhPcParam? Find(int number) => CatalogList.FirstOrDefault(p => p.Number == number);

    /// <summary>Параметры из прочитанного файла, которых нет в каталоге: имена виртуальных экранов 450–469, коды быстрого доступа 2001–2360 и недокументированные (показываются только для чтения).</summary>
    public static IReadOnlyList<int> UnknownNumbers(ShtrikhPcConfigData data) =>
        data.Numbers.Where(n => Find(n) is null && n is not (>= 2001 and <= 2360)).OrderBy(n => n).ToList();

    /// <summary>Сколько кнопок быстрого доступа назначено (значение не 0).</summary>
    public static int AssignedQuickKeys(ShtrikhPcConfigData data) =>
        data.Numbers.Count(n => n is >= 2001 and <= 2360 && data.Get(n) is { } v && v.Trim() != "0" && v.Trim().Length > 0);

    // ------------------------------------------------------------------ сверка весов с кассой

    private static string FormatName(int code) => code switch
    {
        0 => ShtrikhPcSettings.FormatCodeWeight,
        1 => ShtrikhPcSettings.FormatCodeAmount,
        2 => ShtrikhPcSettings.FormatWeightCode,
        3 => ShtrikhPcSettings.FormatAmountCode,
        _ => "",
    };

    public static int FormatCode(string format) => format switch
    {
        ShtrikhPcSettings.FormatCodeAmount => 1,
        ShtrikhPcSettings.FormatWeightCode => 2,
        ShtrikhPcSettings.FormatAmountCode => 3,
        _ => 0,
    };

    private static string YesNo(bool value) => value ? Tr.T("да", "ооба", "yes", "evet", "ha") : Tr.T("нет", "жок", "no", "hayır", "yo‘q");

    /// <summary>Сверка «весы ↔ касса»: префиксы и форматы штрих-кода, число знаков кода и КС, знаки цены (и предел цены), импорт конфигурации/товаров/ресурсов, перезагрузка сценария.
    /// Зелёная плашка — все строки Ok.</summary>
    public static List<ShtrikhPcCheckItem> Compare(ShtrikhPcSettings kassa, ShtrikhPcConfigData scale)
    {
        var list = new List<ShtrikhPcCheckItem>();
        void Row(int number, string what, string? scaleText, string kassaText, bool ok) =>
            list.Add(new ShtrikhPcCheckItem(ok, ok
                ? Tr.T($"{what} (параметр {number}): {kassaText} — совпадает.", $"{what} ({number}-параметр): {kassaText} — дал келет.", $"{what} (parameter {number}): {kassaText} — matches.", $"{what} (parametre {number}): {kassaText} — aynı.", $"{what} ({number}-parametr): {kassaText} — mos.")
                : Tr.T($"{what} (параметр {number}): на весах {scaleText ?? "—"}, в кассе {kassaText}.", $"{what} ({number}-параметр): таразада {scaleText ?? "—"}, кассада {kassaText}.", $"{what} (parameter {number}): scale {scaleText ?? "—"}, till {kassaText}.", $"{what} (parametre {number}): tartıda {scaleText ?? "—"}, kasada {kassaText}.", $"{what} ({number}-parametr): tarozida {scaleText ?? "—"}, kassada {kassaText}.")));

        var decimals = scale.GetInt(12, -1);
        if (decimals is >= 0 and <= 3)
        {
            var max = ScalePriceLimits.FormatMax(ScalePriceLimits.ShtrikhPcMaxUnits, decimals);
            Row(12, Tr.T("Знаков после запятой в цене", "Баадагы үтүрдөн кийинки белгилер", "Price decimals", "Fiyat ondalığı", "Narxdagi verguldan keyingi xonalar"), decimals.ToString(CultureInfo.InvariantCulture),
                kassa.PriceDecimals.ToString(CultureInfo.InvariantCulture), decimals == kassa.PriceDecimals);
            list.Add(new ShtrikhPcCheckItem(true, Tr.T($"Предел цены при {decimals} знаках: {max} сом (поле цены БД весов — 7 знаков).", $"{decimals} белгиде баанын чеги: {max} сом (таразы базасынын баа талаасы — 7 белги).",
                $"Price limit with {decimals} decimals: {max} som (the scale database price field has 7 digits).", $"{decimals} hanede fiyat sınırı: {max} som (tartı veritabanı fiyat alanı 7 hane).", $"{decimals} xonada narx chegarasi: {max} so‘m (tarozi bazasining narx maydoni — 7 raqam).")));
        }
        if (scale.Has(303))
            Row(303, Tr.T("Префикс весового товара", "Салмактуу товардын префикси", "Weighed product prefix", "Tartılı ürün öneki", "Vaznli tovar prefiksi"), scale.Get(303), kassa.BarcodePrefix.ToString(CultureInfo.InvariantCulture), scale.GetInt(303, -1) == kassa.BarcodePrefix);
        if (scale.Has(305))
            Row(305, Tr.T("Формат весового товара", "Салмактуу товардын форматы", "Weighed product format", "Tartılı ürün biçimi", "Vaznli tovar formati"), ShtrikhPcSettings.FormatTitle(FormatName(scale.GetInt(305, 0))), ShtrikhPcSettings.FormatTitle(kassa.BarcodeFormat), FormatName(scale.GetInt(305, -1)) == kassa.BarcodeFormat);
        if (scale.Has(307))
            Row(307, Tr.T("Число знаков кода товара", "Товар кодунун белгилеринин саны", "Product code length", "Ürün kodu uzunluğu", "Tovar kodi uzunligi"), scale.Get(307), kassa.CodeLength.ToString(CultureInfo.InvariantCulture), scale.GetInt(307, -1) == kassa.CodeLength);
        if (scale.Has(308))
            Row(308, Tr.T("КС кода товара", "Товар кодунун КС", "Product code check digit", "Ürün kodu KS", "Tovar kodi KS"), YesNo(scale.GetInt(308, 0) == 1), YesNo(kassa.CodeChecksum), (scale.GetInt(308, 0) == 1) == kassa.CodeChecksum);
        if (kassa.PieceBarcodeEnabled && scale.Has(304))
            Row(304, Tr.T("Префикс штучного товара", "Даана товардын префикси", "Piece product prefix", "Adetli ürün öneki", "Donali tovar prefiksi"), scale.Get(304), kassa.PiecePrefix.ToString(CultureInfo.InvariantCulture), scale.GetInt(304, -1) == kassa.PiecePrefix);
        if (kassa.PieceBarcodeEnabled && scale.Has(306))
        {
            // у штучного товара варианты другие: 0 код-количество, 1 код-стоимость, 2 количество-код, 3 стоимость-код; касса читает только «со стоимостью» (1 и 3)
            var pieceCode = scale.GetInt(306, -1);
            var pieceTitle = Find(306)?.Choices?.Invoke() is { } choices && pieceCode >= 0 && pieceCode < choices.Length ? choices[pieceCode] : scale.Get(306);
            Row(306, Tr.T("Формат штучного товара", "Даана товардын форматы", "Piece product format", "Adetli ürün biçimi", "Donali tovar formati"), pieceTitle, ShtrikhPcSettings.FormatTitle(kassa.PieceFormat), pieceCode is 1 or 3 && FormatName(pieceCode) == kassa.PieceFormat);
        }

        void Import(int number, string what)
        {
            if (!scale.Has(number))
                return;
            var on = scale.GetInt(number, 0) == 1;
            list.Add(new ShtrikhPcCheckItem(on, on
                ? Tr.T($"{what} (параметр {number}) включён.", $"{what} ({number}-параметр) күйгүзүлгөн.", $"{what} (parameter {number}) is on.", $"{what} (parametre {number}) açık.", $"{what} ({number}-parametr) yoqilgan.")
                : Tr.T($"{what} (параметр {number}) ВЫКЛЮЧЕН: касса не сможет этим пользоваться — включите на весах (Конфигурация → База данных → Импорт).", $"{what} ({number}-параметр) ӨЧҮК: касса аны колдоно албайт — таразада күйгүзүңүз (Конфигурация → База данных → Импорт).",
                    $"{what} (parameter {number}) is OFF: the till cannot use it — turn it on at the scale (Configuration → Database → Import).", $"{what} (parametre {number}) KAPALI: kasa bunu kullanamaz — tartıda açın (Конфигурация → База данных → Импорт).", $"{what} ({number}-parametr) O‘CHIQ: kassa undan foydalana olmaydi — tarozida yoqing (Конфигурация → База данных → Импорт).")));
        }
        Import(814, Tr.T("Импорт конфигурации", "Конфигурацияны импорттоо", "Import configuration", "Yapılandırma içe aktarma", "Konfiguratsiyani import"));
        Import(817, Tr.T("Импорт товаров", "Товарларды импорттоо", "Import goods", "Ürün içe aktarma", "Tovarlarni import"));
        if (kassa.SendPictures)
            Import(819, Tr.T("Импорт ресурсов (картинок)", "Ресурстарды импорттоо (сүрөттөр)", "Import resources (pictures)", "Kaynak içe aktarma (resimler)", "Resurslarni import (rasmlar)"));
        if (kassa.ScenarioReloadFlagEnabled && scale.Has(953))
        {
            var on = scale.GetInt(953, 0) == 1;
            var scaleFlag = scale.Get(954)?.Trim() ?? "—";
            var sameName = string.Equals(scaleFlag, kassa.ScenarioReloadFlag, StringComparison.Ordinal);
            var state = on ? Tr.T("включена", "күйгүзүлгөн", "on", "açık", "yoqilgan") : Tr.T("выключена", "өчүк", "off", "kapalı", "o‘chiq");
            list.Add(new ShtrikhPcCheckItem(on && sameName, on && sameName
                ? Tr.T($"Перезагрузка сценария (953/954): включена, имя флага «{kassa.ScenarioReloadFlag}» совпадает.", $"Сценарийди кайра жүктөө (953/954): күйгүзүлгөн, желек аты «{kassa.ScenarioReloadFlag}» дал келет.", $"Scenario reload (953/954): on, the flag name “{kassa.ScenarioReloadFlag}” matches.", $"Senaryo yeniden yükleme (953/954): açık, bayrak adı «{kassa.ScenarioReloadFlag}» aynı.", $"Ssenariyni qayta yuklash (953/954): yoqilgan, bayroq nomi «{kassa.ScenarioReloadFlag}» mos.")
                : Tr.T($"Перезагрузка сценария (953/954): на весах {state}, имя флага «{scaleFlag}», в кассе «{kassa.ScenarioReloadFlag}» — главный экран сам не обновится.", $"Сценарийди кайра жүктөө (953/954): таразада {state}, желек аты «{scaleFlag}», кассада «{kassa.ScenarioReloadFlag}» — башкы экран өзү жаңыланбайт.",
                    $"Scenario reload (953/954): the scale has it {state}, flag name “{scaleFlag}”, the till “{kassa.ScenarioReloadFlag}” — the main screen will not refresh by itself.", $"Senaryo yeniden yükleme (953/954): tartıda {state}, bayrak adı «{scaleFlag}», kasada «{kassa.ScenarioReloadFlag}» — ana ekran kendiliğinden güncellenmez.", $"Ssenariyni qayta yuklash (953/954): tarozida {state}, bayroq nomi «{scaleFlag}», kassada «{kassa.ScenarioReloadFlag}» — bosh ekran o‘zi yangilanmaydi.")));
        }
        list.AddRange(ScreenChecks(kassa, scale)); // 2026-11-12: имена файлов обмена, режим весов и файл сценария
        return list;
    }

    // ------------------------------------------------------------------ дойдёт ли товар до экрана весов

    /// <summary>Путь из настроек весов (801/803/951) в одном виде для сравнения: «/»→«\», без пробелов и «\» по краям. Регистр не важен — папка Windows весов.</summary>
    private static string SamePathKey(string? path) => (path ?? "").Trim().Replace('/', '\\').Trim('\\').ToUpperInvariant();

    /// <summary>Относительный путь с весов, который можно подставить в кассу: не пустой, без диска, «..» и запрещённых знаков.</summary>
    private static bool IsSafeRelative(string? path)
    {
        var p = (path ?? "").Trim();
        return p.Length > 0 && p.Length <= 200 && !p.Contains("..", StringComparison.Ordinal) && p.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) < 0 && !p.StartsWith('\\') && !p.StartsWith('/');
    }

    /// <summary>2026-11-12 (жалоба владельца «товары отправляются, но на экране весов РС200 их нет»): проверки, от которых зависит, увидит ли покупатель/продавец товар на экране весов.
    /// По руководству администратора РС-200С2 (п. 6.3, 7.4, 7.5, 7.10, 8.1) и описанию драйвера SetRetail для РС-200 С3:
    /// <list type="bullet">
    /// <item>801/803 — файл импорта и флаг-файл: если весы ждут другое имя (в самом руководстве встречаются и «PCScale.txt», и «Import.txt»), они НИКОГДА не заберут файл кассы;</item>
    /// <item>27 = 1 (самообслуживание) — главный экран строится из сценария 951\952: касса должна писать тот же файл, иначе обновляет сценарий, который весы не показывают;</item>
    /// <item>27 = 0 (обычный режим) — главный экран это кнопки быстрого доступа 2001–2360 (по 18 на экран), сценарий не используется: без назначения кнопок (касса — «Кнопки быстрого доступа»,
    /// весы — «Импорт конфигурации» 814 = 1) товар есть в базе весов, но находится только поиском или по коду.</item>
    /// </list>
    /// Строки, которых нет в прочитанном файле, не проверяются.</summary>
    public static List<ShtrikhPcCheckItem> ScreenChecks(ShtrikhPcSettings kassa, ShtrikhPcConfigData scale)
    {
        var list = new List<ShtrikhPcCheckItem>();
        void FileName(int number, string what, string? scaleValue, string kassaValue)
        {
            if (string.IsNullOrWhiteSpace(scaleValue))
                return;
            var ok = SamePathKey(scaleValue) == SamePathKey(kassaValue);
            var sv = scaleValue.Trim();
            list.Add(new ShtrikhPcCheckItem(ok, ok
                ? Tr.T($"{what} (параметр {number}): {sv} — совпадает.", $"{what} ({number}-параметр): {sv} — дал келет.", $"{what} (parameter {number}): {sv} — matches.", $"{what} (parametre {number}): {sv} — aynı.", $"{what} ({number}-parametr): {sv} — mos.")
                : Tr.T($"{what} (параметр {number}): весы ждут «{sv}», касса пишет «{kassaValue}» — весы не заберут товары. Нажмите «Подтянуть настройки кассы из весов» или поправьте в кассе «Файлы обмена».",
                    $"{what} ({number}-параметр): тараза «{sv}» күтөт, касса «{kassaValue}» жазат — тараза товарларды албайт. «Кассанын жөндөөлөрүн таразадан алуу» басыңыз же кассада «Алмашуу файлдарын» оңдоңуз.",
                    $"{what} (parameter {number}): the scale expects “{sv}”, the till writes “{kassaValue}” — the scale will not pick up the goods. Press “Pull the till settings from the scale” or fix “Exchange files” in the till.",
                    $"{what} (parametre {number}): tartı «{sv}» bekliyor, kasa «{kassaValue}» yazıyor — tartı ürünleri almaz. «Kasa ayarlarını tartıdan al»a basın veya kasada «Değişim dosyaları»nı düzeltin.",
                    $"{what} ({number}-parametr): tarozi «{sv}» kutadi, kassa «{kassaValue}» yozadi — tarozi tovarlarni olmaydi. «Kassa sozlamalarini tarozidan olish» ni bosing yoki kassada «Almashuv fayllari» ni tuzating.")));
        }
        FileName(801, Tr.T("Файл импорта", "Импорт файлы", "Import file", "İçe aktarma dosyası", "Import fayli"), scale.Get(801), kassa.ImportFile);
        FileName(803, Tr.T("Флаг-файл импорта", "Импорттун желек-файлы", "Import flag file", "İçe aktarma bayrak dosyası", "Import bayroq-fayli"), scale.Get(803), kassa.FlagFile);

        if (!scale.Has(27))
            return list;
        if (scale.GetInt(27, 0) == 1)
        {
            if (!kassa.UpdateScenario)
                return list;
            var scaleFile = (scale.Get(952) ?? "").Trim();
            var scaleFolder = (scale.Get(951) ?? "").Trim();
            var fileOk = scaleFile.Length > 0 && SamePathKey(scaleFile) == SamePathKey(kassa.ScenarioFile);
            var folderOk = scaleFolder.Length == 0 || SamePathKey(scaleFolder) == SamePathKey(kassa.ScenarioFolder);
            var ok = fileOk && folderOk;
            var scaleShown = scaleFile.Length == 0 ? "—" : (scaleFolder.Length == 0 ? scaleFile : scaleFolder.TrimEnd('\\', '/') + "\\" + scaleFile);
            var kassaShown = kassa.ScenarioFolder + "\\" + kassa.ScenarioFile;
            list.Add(new ShtrikhPcCheckItem(ok, ok
                ? Tr.T($"Самообслуживание (параметр 27): главный экран — сценарий {kassaShown}, касса пишет его же.", $"Өз алдынча тейлөө (27-параметр): башкы экран — {kassaShown} сценарийи, касса ошону жазат.",
                    $"Self-service (parameter 27): the main screen is the scenario {kassaShown}, the till writes the same one.", $"Self servis (parametre 27): ana ekran {kassaShown} senaryosu, kasa aynısını yazıyor.", $"O‘zini-o‘zi xizmat (27-parametr): bosh ekran — {kassaShown} ssenariysi, kassa xuddi shuni yozadi.")
                : Tr.T($"Самообслуживание (параметр 27): весы показывают сценарий «{scaleShown}» (951/952), а касса пишет «{kassaShown}» — новые товары на главный экран не попадут. Нажмите «Подтянуть настройки кассы из весов».",
                    $"Өз алдынча тейлөө (27-параметр): тараза «{scaleShown}» сценарийин көрсөтөт (951/952), касса болсо «{kassaShown}» жазат — жаңы товарлар башкы экранга чыкпайт. «Кассанын жөндөөлөрүн таразадан алуу» басыңыз.",
                    $"Self-service (parameter 27): the scale shows the scenario “{scaleShown}” (951/952), but the till writes “{kassaShown}” — new goods will not reach the main screen. Press “Pull the till settings from the scale”.",
                    $"Self servis (parametre 27): tartı «{scaleShown}» senaryosunu gösteriyor (951/952), kasa ise «{kassaShown}» yazıyor — yeni ürünler ana ekrana çıkmaz. «Kasa ayarlarını tartıdan al»a basın.",
                    $"O‘zini-o‘zi xizmat (27-parametr): tarozi «{scaleShown}» ssenariysini ko‘rsatadi (951/952), kassa esa «{kassaShown}» yozadi — yangi tovarlar bosh ekranga chiqmaydi. «Kassa sozlamalarini tarozidan olish» ni bosing.")));
        }
        else
        {
            var keysOk = kassa.QuickKeys && scale.GetInt(814, 0) == 1;
            list.Add(new ShtrikhPcCheckItem(keysOk, keysOk
                ? Tr.T("Обычный режим (параметр 27 = 0): главный экран — кнопки быстрого доступа, касса их назначает (814 включён).", "Кадимки режим (27 = 0): башкы экран — тез жеткиликтүүлүк баскычтары, касса аларды дайындайт (814 күйгүзүлгөн).",
                    "Normal mode (parameter 27 = 0): the main screen is the quick-access buttons, the till assigns them (814 on).", "Normal mod (parametre 27 = 0): ana ekran hızlı erişim düğmeleri, kasa bunları atıyor (814 açık).", "Oddiy rejim (27 = 0): bosh ekran — tez kirish tugmalari, kassa ularni biriktiradi (814 yoqilgan).")
                : Tr.T("Обычный режим (параметр 27 = 0): на главном экране весов — кнопки быстрого доступа (2001–2360), сценарий не используется. Товары загрузятся в базу весов, но найдутся только поиском или по коду. Чтобы они встали на кнопки: в кассе включите «Кнопки быстрого доступа», на весах — «Импорт конфигурации» (814).",
                    "Кадимки режим (27 = 0): таразанын башкы экранында — тез жеткиликтүүлүк баскычтары (2001–2360), сценарий колдонулбайт. Товарлар таразанын базасына жүктөлөт, бирок издөө же код менен гана табылат. Баскычтарга чыгышы үчүн: кассада «Тез жеткиликтүүлүк баскычтарын», таразада «Конфигурацияны импорттоону» (814) күйгүзүңүз.",
                    "Normal mode (parameter 27 = 0): the scale main screen shows quick-access buttons (2001–2360), the scenario is not used. Goods load into the scale database but are found only by search or code. To put them on buttons: turn on “Quick-access buttons” in the till and “Import configuration” (814) on the scale.",
                    "Normal mod (parametre 27 = 0): tartının ana ekranında hızlı erişim düğmeleri (2001–2360) var, senaryo kullanılmıyor. Ürünler tartı veritabanına yüklenir ama yalnızca arama veya kodla bulunur. Düğmelere çıkmaları için: kasada «Hızlı erişim düğmeleri»ni, tartıda «Yapılandırma içe aktarma»yı (814) açın.",
                    "Oddiy rejim (27 = 0): tarozining bosh ekranida tez kirish tugmalari (2001–2360), ssenariy ishlatilmaydi. Tovarlar tarozi bazasiga yuklanadi, lekin faqat qidiruv yoki kod bilan topiladi. Tugmalarga chiqishi uchun: kassada «Tez kirish tugmalari» ni, tarozida «Konfiguratsiyani import» (814) ni yoqing.")));
        }
        return list;
    }

    /// <summary>2026-11-12: только НЕпройденные проверки «товар дойдёт до экрана»: <see cref="ScreenChecks"/> плюс выключенный импорт товаров (817) и картинок (819, если касса их шлёт).
    /// Показывается после отправки товаров на РС-200, если удалось прочитать настройки весов.</summary>
    public static List<string> ScreenProblems(ShtrikhPcSettings kassa, ShtrikhPcConfigData scale)
    {
        var problems = new List<string>();
        if (scale.Has(817) && scale.GetInt(817, 0) != 1)
            problems.Add(Tr.T("на весах выключен «Импорт товаров» (817) — товары не загрузятся", "таразада «Товарларды импорттоо» (817) өчүк — товарлар жүктөлбөйт", "“Import goods” (817) is off on the scale — goods will not load",
                "tartıda «Ürün içe aktarma» (817) kapalı — ürünler yüklenmez", "tarozida «Tovarlarni import» (817) o‘chiq — tovarlar yuklanmaydi"));
        if (kassa.SendPictures && scale.Has(819) && scale.GetInt(819, 0) != 1)
            problems.Add(Tr.T("на весах выключен «Импорт ресурсов» (819) — картинок на кнопках не будет", "таразада «Ресурстарды импорттоо» (819) өчүк — баскычтарда сүрөт болбойт", "“Import resources” (819) is off on the scale — the buttons will have no pictures",
                "tartıda «Kaynak içe aktarma» (819) kapalı — düğmelerde resim olmaz", "tarozida «Resurslarni import» (819) o‘chiq — tugmalarda rasm bo‘lmaydi"));
        problems.AddRange(ScreenChecks(kassa, scale).Where(c => !c.Ok).Select(c => c.Text));
        return problems;
    }

    /// <summary>Подтягивает в кассу значения с весов (знаки цены, префиксы, форматы, число знаков кода, КС, имя флага перезагрузки). Возвращает, сколько полей изменилось.</summary>
    public static int ApplyScaleToKassa(ShtrikhPcSettings kassa, ShtrikhPcConfigData scale)
    {
        var changed = 0;
        void Int(int current, int value, Action<int> set)
        {
            if (current != value)
            {
                set(value);
                changed++;
            }
        }
        var decimals = scale.GetInt(12, -1);
        if (decimals is >= 0 and <= 3)
            Int(kassa.PriceDecimals, decimals, v => kassa.PriceDecimals = v);
        var prefix = scale.GetInt(303, -1);
        if (prefix is >= 20 and <= 29)
            Int(kassa.BarcodePrefix, prefix, v => kassa.BarcodePrefix = v);
        var piece = scale.GetInt(304, -1);
        if (piece is >= 20 and <= 29)
            Int(kassa.PiecePrefix, piece, v => kassa.PiecePrefix = v);
        var format = FormatName(scale.GetInt(305, -1));
        if (format.Length > 0 && kassa.BarcodeFormat != format)
        {
            kassa.BarcodeFormat = format;
            changed++;
        }
        var pieceFormat = FormatName(scale.GetInt(306, -1));
        if (pieceFormat.Length > 0 && ShtrikhPcSettings.PieceFormats.Contains(pieceFormat) && kassa.PieceFormat != pieceFormat)
        {
            kassa.PieceFormat = pieceFormat;
            changed++;
        }
        var length = scale.GetInt(307, -1);
        if (length is >= 1 and <= 6)
            Int(kassa.CodeLength, length, v => kassa.CodeLength = v);
        if (scale.Has(308) && kassa.CodeChecksum != (scale.GetInt(308, 0) == 1))
        {
            kassa.CodeChecksum = scale.GetInt(308, 0) == 1;
            changed++;
        }
        if (scale.Has(954) && !string.IsNullOrWhiteSpace(scale.Get(954)))
        {
            var flag = ShtrikhPcScenarioWriter.SafeFlagName(scale.Get(954));
            if (kassa.ScenarioReloadFlag != flag)
            {
                kassa.ScenarioReloadFlag = flag;
                changed++;
            }
        }
        // 2026-11-12: имена файлов обмена и файл сценария главного экрана — тоже с весов (иначе касса пишет файл, который весы не ждут или не показывают).
        void FilePath(int number, string current, Action<string> set)
        {
            var value = scale.Get(number)?.Trim();
            if (IsSafeRelative(value) && SamePathKey(value) != SamePathKey(current))
            {
                set(value!);
                changed++;
            }
        }
        FilePath(801, kassa.ImportFile, v => kassa.ImportFile = v);
        FilePath(803, kassa.FlagFile, v => kassa.FlagFile = v);
        var scenarioFolder = scale.Get(951)?.Trim().TrimEnd('\\', '/');
        FilePath(951, kassa.ScenarioFolder, _ => kassa.ScenarioFolder = scenarioFolder!);
        var scenarioFile = scale.Get(952)?.Trim();
        if (!string.IsNullOrEmpty(scenarioFile) && ShtrikhPcScenarioWriter.SafeFileName(scenarioFile) == scenarioFile && SamePathKey(scenarioFile) != SamePathKey(kassa.ScenarioFile))
        {
            kassa.ScenarioFile = scenarioFile;
            changed++;
        }
        kassa.Normalize();
        return changed;
    }
}
