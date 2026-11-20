using System.Text;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Import;

/// <summary>2026-11-20: чтение двоичного .xls (ExcelDataReader) в мастере «Массовая загрузка». Мини-файл .xls собирает <see cref="MiniXls"/>
/// (контейнер OLE + BIFF8), так что тесты не зависят от образцов; образец avtogarant-offline.xls из docs/Образцы проверяется, если лежит локально.</summary>
[Collection("UserPreferences")]
public sealed class ProductFileReaderXlsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "xls-" + Guid.NewGuid().ToString("N")[..8]);

    public ProductFileReaderXlsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    [Fact]
    public void Mini_xls_is_read_as_rows_with_text_and_numbers()
    {
        var path = Path.Combine(_dir, "price.xls");
        File.WriteAllBytes(path, MiniXls.Build("Прайс",
        [
            ["Прайс-лист от 20.11.2026"],
            ["Наименование", "Артикул", "Бренд", "Цена"],
            ["Фильтр масляный", "W 811/80", "Mann", 450d],
            ["Колодки передние", "FDB 1234", "Ferodo", 2500.5],
            [],
            ["Штрихкод-число", 4600000000001d, "", 10d],
        ]));
        var r = ProductFileReader.ReadFile(path);
        Assert.True(r.Ok, r.Error);
        var t = r.Table!;
        Assert.Equal("Прайс", t.SheetName);
        Assert.Equal(5, t.Rows.Count); // пустая строка пропущена
        Assert.Equal("Фильтр масляный", t.Rows[2][0]);
        Assert.Equal("450", t.Rows[2][3]);
        Assert.True(t.Rows[2].IsNumeric(3));
        Assert.Equal("2500.5", t.Rows[3][3]);
        Assert.Equal(6, t.Rows[4].FileRow); // номер строки как в Excel (пустая пятая учтена)
        Assert.Equal("4600000000001", t.Rows[4][1]); // без экспоненты
        Assert.Equal(1, t.HeaderIndex); // заголовки найдены под строкой с датой
    }

    [Fact]
    public void Mini_xls_goes_through_the_auto_parts_profile()
    {
        var path = Path.Combine(_dir, "autoparts.xls");
        File.WriteAllBytes(path, MiniXls.Build("Лист1",
        [
            ["Автозапчасти оптом"],
            ["Наименование", "Каталожный номер", "Бренд", "Остаток", "Цена"],
            ["Фильтры масляные"],
            ["Фильтр масляный Mann W 811/80", "W 811/80", "Mann", "20–50", 450d],
            ["Колодки Ferodo FDB 1234", "FDB 1234", "Ferodo", 4d, 2500d],
            ["Свеча без цены", "BKR6E", "NGK", 5d],
        ]));
        var r = ProductFileReader.ReadFile(path);
        Assert.True(r.Ok, r.Error);
        Assert.True(AutoPartsPriceImport.LooksLikePrice(r.Table!));
        r.Table!.SetHeaderRow(AutoPartsPriceImport.FindHeaderRow(r.Table));
        var p = AutoPartsPriceImport.Prepare(r.Table, null, []);
        Assert.Equal(2, p.Created);
        Assert.Equal("Mann", p.Rows.First(x => x.Status != AutoPartsRowStatus.Skipped).Brand);
    }

    [Fact]
    public void Xls_sheet_can_be_chosen_by_name_and_hidden_sheets_are_not_listed()
    {
        var path = Path.Combine(_dir, "two.xls");
        File.WriteAllBytes(path, MiniXls.Build(
            ("Первый", false, [["А", "Б"], ["1", 2d]]),
            ("Скрытый", true, [["Х"], ["секрет"]]),
            ("Третий", false, [["В", "Г"], ["3", 4d]])));
        var first = ProductFileReader.ReadFile(path);
        Assert.True(first.Ok, first.Error);
        Assert.Equal("Первый", first.Table!.SheetName);
        Assert.Equal(["Первый", "Третий"], first.Table.Sheets);
        var third = ProductFileReader.ReadFile(path, "Третий");
        Assert.True(third.Ok, third.Error);
        Assert.Equal("Третий", third.Table!.SheetName);
        Assert.Equal("В", third.Table.Rows[0][0]);
    }

    [Fact]
    public void Garbage_after_ole_signature_gives_a_clear_message()
    {
        var path = Path.Combine(_dir, "bad.xls");
        File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]);
        var r = ProductFileReader.ReadFile(path);
        Assert.False(r.Ok);
        Assert.Contains(".xls", r.Error);
        Assert.Contains(".xlsx", r.Error);
    }

    private static string? Sample(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "docs", "Образцы", "Автозапчасти", name);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    [Fact]
    public void Avtogarant_sample_xls_is_read_and_recognised()
    {
        var path = Sample("avtogarant-offline.xls");
        if (path is null)
            return; // образца нет в этой копии (он в .gitignore)
        var r = ProductFileReader.ReadFile(path);
        Assert.True(r.Ok, r.Error);
        Assert.True(r.Table!.Rows.Count > 2000, "строк: " + r.Table.Rows.Count);
        Assert.True(AutoPartsPriceImport.LooksLikePrice(r.Table));
        r.Table.SetHeaderRow(AutoPartsPriceImport.FindHeaderRow(r.Table));
        var p = AutoPartsPriceImport.Prepare(r.Table, null, []);
        Assert.True(p.Created > 2000, "к созданию: " + p.Created);
        Assert.True(p.Rows.Where(x => x.Status != AutoPartsRowStatus.Skipped).All(x => x.Sale > 0 && x.Name.Length > 0));
    }
}
