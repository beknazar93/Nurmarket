using System.Text;

namespace NurMarketKassa.Services;

// 2026-11-20 (просьба владельца «автоматическое подтягивание базы, если указать папку программы, и с подсказкой, где по стандарту база»):
// справочник «программа → где обычно лежат данные → облако ли это». Здесь ТОЛЬКО список путей и текстов — диск не обходится.
// Что проверено, а что гипотеза — таблица в docs/Отчеты/Автоподтяжка базы из папки программы — исследование (20.11.2026).md.
// Пути с пометкой «гипотеза» подобраны по обычным соглашениям Windows (Program Files, ProgramData, AppData) — реальных установок этих программ у нас не было;
// если папки нет, касса просто не предлагает её, ошибки нет.

/// <summary>Статус способа чтения данных программы напрямую из папки.</summary>
public enum ProgramFolderSupport
{
    /// <summary>Облако: на компьютере данных нет — только выгрузка файла.</summary>
    Cloud,
    /// <summary>Папку читаем (SQLite / DBF / выгрузки Excel, CSV, XML внутри неё).</summary>
    Readable,
    /// <summary>База в закрытом формате или нужен драйвер: читается только выгрузка.</summary>
    NeedsExport,
}

/// <summary>Подсказка по одной программе: где её папка по стандарту, облако ли она.</summary>
public sealed class ProgramFolderHint
{
    internal ProgramFolderHint(string profileId, ProgramFolderSupport support, string[] standardPaths, string[] folderWords, Func<string> note)
    {
        ProfileId = profileId;
        Support = support;
        StandardPaths = standardPaths;
        FolderWords = folderWords;
        _note = note;
    }

    private readonly Func<string> _note;

    /// <summary>Код программы (как <see cref="ImportSourceProfile.Id"/>; «ctmax» — «CT MAX», профиля столбцов пока нет).</summary>
    public string ProfileId { get; }

    public ProgramFolderSupport Support { get; }

    /// <summary>Стандартные места: могут содержать %ПЕРЕМЕННЫЕ% окружения (раскрываются в <see cref="ExpandedStandardPaths"/>).</summary>
    public IReadOnlyList<string> StandardPaths { get; }

    /// <summary>Слова (строчными), по которым имя папки/файла выдаёт эту программу.</summary>
    public IReadOnlyList<string> FolderWords { get; }

    /// <summary>Пояснение на языке интерфейса: где данные и что делать, если папка не читается.</summary>
    public string Note => _note();

    /// <summary>Стандартные места с раскрытыми переменными (без проверки существования).</summary>
    public IEnumerable<string> ExpandedStandardPaths() => StandardPaths.Select(Expand).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Из стандартных мест — только существующие папки (проверка наличия, без обхода содержимого).</summary>
    public IEnumerable<string> ExistingStandardFolders() => ExpandedStandardPaths().Where(p => SafeDirExists(p));

    /// <summary>Текст «Обычно здесь: …» для подсказки под кнопкой; пусто, если путей нет (облако).</summary>
    public string UsuallyHereText()
    {
        var list = ExpandedStandardPaths().Take(4).ToList();
        if (list.Count == 0)
            return "";
        return Tr.T("Обычно здесь: ", "Адатта бул жерде: ", "Usually here: ", "Genellikle burada: ", "Odatda bu yerda: ") + string.Join("  ·  ", list);
    }

    private static bool SafeDirExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    internal static string Expand(string template)
    {
        try
        {
            var s = template;
            foreach (var (name, value) in EnvFolders())
                s = s.Replace("%" + name + "%", value, StringComparison.OrdinalIgnoreCase);
            return s.Contains('%') ? "" : s;
        }
        catch
        {
            return "";
        }
    }

    // Папки пользователя — через AppPaths (подмена данных для тестов), остальные — через систему.
    private static IEnumerable<(string Name, string Value)> EnvFolders()
    {
        yield return ("APPDATA", AppPaths.Roaming);
        yield return ("LOCALAPPDATA", AppPaths.Local);
        yield return ("PROGRAMDATA", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        yield return ("PROGRAMFILES", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        yield return ("PROGRAMFILESX86", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        yield return ("DOCUMENTS", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        yield return ("USERPROFILE", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }
}

public static class ProgramFolderHints
{
    private static string Cloud(string name, string where) => Tr.T(
        $"{name} хранит данные в облаке — папки с базой на компьютере нет. Выгрузите файл из {where} (подсказка «Как выгрузить» на плитке) и выберите его кнопкой «Выбрать файл…».",
        $"{name} маалыматты булутта сактайт — компьютерде база папкасы жок. Файлды {where} программасынан чыгарып («Кантип чыгаруу» кеңеши), «Файлды тандоо…» баскычы менен тандаңыз.",
        $"{name} keeps its data in the cloud — there is no database folder on the computer. Export a file from {where} (the “How to export” tip on the tile) and choose it with “Choose file…”.",
        $"{name} verileri bulutta tutar — bilgisayarda veritabanı klasörü yoktur. {where} içinden bir dosya dışa aktarın («Nasıl alınır» ipucu) ve «Dosya seç…» ile seçin.",
        $"{name} ma'lumotlarni bulutda saqlaydi — kompyuterda baza papkasi yo'q. {where} dan fayl chiqaring (plitkadagi «Qanday chiqarish» maslahati) va «Faylni tanlash…» bilan tanlang.");

    public static readonly IReadOnlyList<ProgramFolderHint> All =
    [
        // 1С: список баз лежит в ibases.v8i (проверено по документации/форумам 1С); сама файловая база — 1Cv8.1CD (закрытый формат).
        new("1c", ProgramFolderSupport.NeedsExport,
            ["%APPDATA%\\1C\\1CEStart", "%PROGRAMDATA%\\1C\\1CEStart", "%PROGRAMFILES%\\1cv8", "%PROGRAMFILESX86%\\1cv8", "%DOCUMENTS%\\InfoBase", "C:\\1C"],
            ["1cv8", "1c", "1с", "infobase", "1cestart"],
            () => Tr.T("1С хранит базу в файле 1Cv8.1CD (закрытый формат) или на сервере SQL — напрямую её не прочитать. Касса найдёт в папке выгрузки: Excel, CSV и XML 1С (CommerceML: import.xml, offers.xml). Выгрузите номенклатуру из 1С в такую папку.",
                "1С базаны 1Cv8.1CD файлында (жабык формат) же SQL серверинде сактайт — аны түз окуу мүмкүн эмес. Бирок папкадан Excel, CSV жана 1С XML (CommerceML: import.xml, offers.xml) табылат. Номенклатураны 1С'тен ушундай папкага чыгарыңыз.",
                "1C keeps the database in a 1Cv8.1CD file (closed format) or on an SQL server — it cannot be read directly. The till will find exports in the folder: Excel, CSV and 1C XML (CommerceML: import.xml, offers.xml). Export the items from 1C into such a folder.",
                "1C veritabanını 1Cv8.1CD dosyasında (kapalı biçim) veya SQL sunucusunda tutar — doğrudan okunamaz. Kasa klasörde dışa aktarımları bulur: Excel, CSV ve 1C XML (CommerceML: import.xml, offers.xml). Ürünleri 1C'den böyle bir klasöre aktarın.",
                "1C bazani 1Cv8.1CD faylida (yopiq format) yoki SQL serverda saqlaydi — uni to'g'ridan-to'g'ri o'qib bo'lmaydi. Kassa papkadan Excel, CSV va 1C XML (CommerceML: import.xml, offers.xml) eksportlarini topadi. Nomenklaturani 1C dan shunday papkaga chiqaring.")),

        new("moysklad", ProgramFolderSupport.Cloud, [], ["moysklad", "мойсклад"], () => Cloud("МойСклад", "МойСклад")),
        new("poster", ProgramFolderSupport.Cloud, [], ["poster"], () => Cloud("Poster", "Poster")),
        new("loyverse", ProgramFolderSupport.Cloud, [], ["loyverse"], () => Cloud("Loyverse", "Loyverse")),

        // Paloma365, Umag, Rosta: облачные сервисы с программой для Windows; где именно лежит локальный кэш — гипотеза (образцов установок нет).
        new("paloma365", ProgramFolderSupport.Readable,
            ["%LOCALAPPDATA%\\Paloma365", "%APPDATA%\\Paloma365", "%PROGRAMDATA%\\Paloma365", "%PROGRAMFILES%\\Paloma365", "%PROGRAMFILESX86%\\Paloma365", "C:\\Paloma365"],
            ["paloma"],
            () => Tr.T("Paloma365 — облачный сервис: основные данные в облаке. Если у вас стоит программа для Windows, укажите её папку: касса прочитает базу SQLite/DBF и выгрузки внутри. Не нашли — выгрузите файл (Номенклатура → Экспорт в Excel).",
                "Paloma365 — булут кызматы: негизги маалымат булутта. Windows программасы орнотулган болсо, анын папкасын көрсөтүңүз: касса ичиндеги SQLite/DBF базаны жана чыгарылган файлдарды окуйт. Табылбаса — файлды чыгарыңыз (Номенклатура → Excel'ге экспорт).",
                "Paloma365 is a cloud service: the main data is in the cloud. If the Windows program is installed, point to its folder: the till reads an SQLite/DBF database and exports inside. If nothing is found — export a file (Items → Export to Excel).",
                "Paloma365 bir bulut hizmetidir: ana veriler bulutta. Windows programı kuruluysa klasörünü gösterin: kasa içindeki SQLite/DBF veritabanını ve dışa aktarımları okur. Bulunmazsa — dosya dışa aktarın (Ürünler → Excel'e aktar).",
                "Paloma365 — bulut xizmati: asosiy ma'lumotlar bulutda. Windows dasturi o'rnatilgan bo'lsa, uning papkasini ko'rsating: kassa ichidagi SQLite/DBF bazani va eksportlarni o'qiydi. Topilmasa — fayl chiqaring (Nomenklatura → Excel ga eksport).")),

        new("umag", ProgramFolderSupport.Readable,
            ["%LOCALAPPDATA%\\UMAG", "%APPDATA%\\UMAG", "%PROGRAMDATA%\\UMAG", "%PROGRAMFILES%\\UMAG", "%PROGRAMFILESX86%\\UMAG", "C:\\UMAG"],
            ["umag"],
            () => Tr.T("Umag — облачный сервис с работой без интернета. Если у вас стоит программа для Windows, укажите её папку: касса прочитает базу SQLite/DBF и выгрузки внутри. Не нашли — выгрузите файл из личного кабинета Umag.",
                "Umag — интернетсиз иштей турган булут кызматы. Windows программасы орнотулган болсо, анын папкасын көрсөтүңүз: касса ичиндеги SQLite/DBF базаны жана чыгарылган файлдарды окуйт. Табылбаса — файлды Umag жеке кабинетинен чыгарыңыз.",
                "Umag is a cloud service that also works offline. If the Windows program is installed, point to its folder: the till reads an SQLite/DBF database and exports inside. If nothing is found — export a file from the Umag account.",
                "Umag çevrimdışı da çalışan bir bulut hizmetidir. Windows programı kuruluysa klasörünü gösterin: kasa içindeki SQLite/DBF veritabanını ve dışa aktarımları okur. Bulunmazsa — Umag hesabından dosya dışa aktarın.",
                "Umag — internetsiz ham ishlaydigan bulut xizmati. Windows dasturi o'rnatilgan bo'lsa, uning papkasini ko'rsating: kassa ichidagi SQLite/DBF bazani va eksportlarni o'qiydi. Topilmasa — faylni Umag shaxsiy kabinetidan chiqaring.")),

        new("rosta", ProgramFolderSupport.Readable,
            ["%LOCALAPPDATA%\\Rosta", "%APPDATA%\\Rosta", "%PROGRAMDATA%\\Rosta", "%PROGRAMFILES%\\Rosta", "%PROGRAMFILESX86%\\Rosta", "C:\\Rosta"],
            ["rosta"],
            () => Tr.T("Rosta (Back Office и Tradepoint): Front работает без интернета. Укажите папку программы — касса прочитает базу SQLite/DBF и выгрузки внутри. Не нашли — выгрузите «Товары → Экспорт» (Excel).",
                "Rosta (Back Office жана Tradepoint): Front интернетсиз иштейт. Программанын папкасын көрсөтүңүз — касса ичиндеги SQLite/DBF базаны жана чыгарылган файлдарды окуйт. Табылбаса — «Товарлар → Экспорт» (Excel) чыгарыңыз.",
                "Rosta (Back Office and Tradepoint): the Front works offline. Point to the program folder — the till reads an SQLite/DBF database and exports inside. If nothing is found — export “Products → Export” (Excel).",
                "Rosta (Back Office ve Tradepoint): Front çevrimdışı çalışır. Program klasörünü gösterin — kasa içindeki SQLite/DBF veritabanını ve dışa aktarımları okur. Bulunmazsa — «Ürünler → Dışa aktar» (Excel) alın.",
                "Rosta (Back Office va Tradepoint): Front internetsiz ishlaydi. Dastur papkasini ko'rsating — kassa ichidagi SQLite/DBF bazani va eksportlarni o'qiydi. Topilmasa — «Tovarlar → Eksport» (Excel) chiqaring.")),

        // CT MAX (ctmax.kg): по сайту программа для Windows подключается к серверу («адрес сервера, логин и пароль»), отдельной базы на ПК нет (проверено по ctmax.kg; тип серверной базы не опубликован).
        // Образец выгрузки владельца — Excel из 4 столбцов «Штрих Код / Наименование / Цена / Закупочная» (шапка в 3-й строке, названия групп стоят строками среди товаров), остатков нет.
        new("ctmax", ProgramFolderSupport.NeedsExport,
            ["%LOCALAPPDATA%\\CTmax", "%APPDATA%\\CTmax", "%PROGRAMDATA%\\CTmax", "%PROGRAMFILES%\\CTmax", "%PROGRAMFILESX86%\\CTmax", "C:\\CTmax"],
            ["ctmax", "ct max"],
            () => Tr.T("CT MAX подключается к серверу (адрес, логин и пароль выдаёт организация), своей базы на компьютере нет. Выгрузите товары в Excel из CT MAX и выберите файл. Если у вас стоит папка с выгрузками — укажите её: касса найдёт в ней таблицы.",
                "CT MAX серверге туташат (дарегин, логин жана сырсөздү уюм берет), компьютерде өз базасы жок. Товарларды CT MAX'тан Excel'ге чыгарып, файлды тандаңыз. Чыгарылган файлдары бар папка болсо — аны көрсөтүңүз: касса ичинен таблицаларды табат.",
                "CT MAX connects to a server (the organisation issues the address, login and password), there is no database on the computer. Export the products to Excel from CT MAX and choose the file. If you have a folder with exports — point to it: the till finds the tables inside.",
                "CT MAX bir sunucuya bağlanır (adres, kullanıcı adı ve parolayı kuruluş verir), bilgisayarda kendi veritabanı yoktur. Ürünleri CT MAX'ten Excel'e aktarın ve dosyayı seçin. Dışa aktarımların olduğu bir klasör varsa — gösterin: kasa içindeki tabloları bulur.",
                "CT MAX serverga ulanadi (manzil, login va parolni tashkilot beradi), kompyuterda o'z bazasi yo'q. Mahsulotlarni CT MAX dan Excel ga chiqaring va faylni tanlang. Eksportlar bor papka bo'lsa — uni ko'rsating: kassa ichidan jadvallarni topadi.")),
    ];

    public static ProgramFolderHint? For(string? profileId) =>
        string.IsNullOrEmpty(profileId) ? null : All.FirstOrDefault(h => h.ProfileId == profileId);

    /// <summary>Пояснение для «Excel / CSV» и неизвестной программы: что вообще можно указать.</summary>
    public static string GenericNote() => Tr.T(
        "Укажите папку программы (или папку с её выгрузками): касса сама найдёт в ней базу SQLite, таблицу DBF, файлы Excel, CSV и XML 1С и подтянет товары. Закрытые базы (1С .1CD, Firebird, Access, SQL Server) напрямую не читаются — выгрузите файл.",
        "Программанын папкасын (же анын чыгарылган файлдары бар папканы) көрсөтүңүз: касса ичинен SQLite базаны, DBF таблицасын, Excel, CSV жана 1С XML файлдарын өзү табып, товарларды алат. Жабык базалар (1С .1CD, Firebird, Access, SQL Server) түз окулбайт — файлды чыгарыңыз.",
        "Point to the program folder (or a folder with its exports): the till finds an SQLite database, a DBF table, Excel, CSV and 1C XML files inside and pulls the products. Closed databases (1C .1CD, Firebird, Access, SQL Server) cannot be read directly — export a file.",
        "Program klasörünü (veya dışa aktarımların olduğu klasörü) gösterin: kasa içinde SQLite veritabanı, DBF tablosu, Excel, CSV ve 1C XML dosyalarını kendisi bulur ve ürünleri alır. Kapalı veritabanları (1C .1CD, Firebird, Access, SQL Server) doğrudan okunamaz — dosya dışa aktarın.",
        "Dastur papkasini (yoki uning eksportlari bor papkani) ko'rsating: kassa ichidan SQLite baza, DBF jadval, Excel, CSV va 1C XML fayllarini o'zi topib, mahsulotlarni oladi. Yopiq bazalar (1C .1CD, Firebird, Access, SQL Server) to'g'ridan-to'g'ri o'qilmaydi — fayl chiqaring.");

    /// <summary>Узнать программу по имени папки/файла (слова из <see cref="ProgramFolderHint.FolderWords"/>); null — не узнана.</summary>
    public static ProgramFolderHint? GuessByPath(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var lower = text.ToLowerInvariant();
        foreach (var h in All)
        {
            foreach (var w in h.FolderWords)
            {
                // «1c» слишком короткое слово: ищем его только как отдельный сегмент пути («...\1C\...»), иначе «1c» нашлось бы в любом GUID.
                if (w.Length <= 2)
                {
                    var segs = lower.Split('\\', '/');
                    if (segs.Any(s => s == w))
                        return h;
                }
                else if (lower.Contains(w, StringComparison.Ordinal))
                    return h;
            }
        }
        return null;
    }

    /// <summary>Пути баз 1С из списка баз ibases.v8i («File="C:\\Base";»): текст файла читается только на чтение, берутся лишь пути файловых баз.</summary>
    public static IReadOnlyList<string> ParseOneCBaseList(string v8iText)
    {
        var list = new List<string>();
        foreach (var raw in v8iText.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Connect=", StringComparison.OrdinalIgnoreCase))
                continue;
            var i = line.IndexOf("File=\"", StringComparison.OrdinalIgnoreCase);
            if (i < 0)
                continue;
            var start = i + 6;
            var end = line.IndexOf('"', start);
            if (end > start)
                list.Add(line[start..end]);
        }
        return list;
    }

    /// <summary>Базы 1С из списков пользователя и общего (ibases.v8i); читается чужой файл настроек, содержимое не журналируется.</summary>
    public static IReadOnlyList<string> FindOneCFileBases()
    {
        var found = new List<string>();
        foreach (var dir in new[] { "%APPDATA%\\1C\\1CEStart", "%PROGRAMDATA%\\1C\\1CEStart" })
        {
            try
            {
                var path = Path.Combine(ProgramFolderHint.Expand(dir), "ibases.v8i");
                if (!File.Exists(path))
                    continue;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8, true);
                foreach (var p in ParseOneCBaseList(sr.ReadToEnd()))
                    if (Directory.Exists(p))
                        found.Add(p);
            }
            catch
            {
                // нет прав или файл занят — подсказка просто не появится
            }
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
