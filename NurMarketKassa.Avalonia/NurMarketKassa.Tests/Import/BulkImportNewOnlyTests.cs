using System.Text;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using Xunit;

namespace NurMarketKassa.Tests.Import;

/// <summary>2026-11-20 (мастер «Массовая загрузка», шаг «Проверьте»: «Только добавить новые»): товары, которые уже есть на складе, пропускаются
/// («пропущен: уже есть на складе»), новые создаются как раньше; обычный режим прихода не меняется.</summary>
public sealed class BulkImportNewOnlyTests
{
    private static BulkImportPlan Build(bool newOnly)
    {
        var csv = "Штрихкод;Название;Количество;Закупка;Цена продажи;Единица\n"
            + "4600000000011;Уже есть;5;80;110;шт\n"
            + "2000000000028;Новый товар;2;10;15;шт\n";
        var table = ProductFileReader.ReadCsv(Encoding.UTF8.GetBytes(csv), "t.csv").Table!;
        var mapping = ProductImportMapping.AutoDetect(table.Headers, table);
        var existing = new CatalogProductTileVm("e1", "Уже есть", "", false) { Barcode = "4600000000011", Quantity = 3 };
        return BulkImportPlan.Build(table, mapping, new BulkImportSettings { NewOnly = newOnly }, [existing]);
    }

    [Fact]
    public void New_only_skips_products_already_in_stock_and_creates_new_ones()
    {
        var plan = Build(newOnly: true);
        Assert.Equal(1, plan.NewCount);
        Assert.Equal(1, plan.UnchangedCount);
        Assert.Equal(1, plan.ReadyCount);
        Assert.Single(plan.Lines);
        Assert.Equal("2000000000028", plan.Lines[0].Barcode);
        var skipped = plan.Rows.Single(r => r.Kind == BulkImportRowKind.Receipt);
        Assert.True(skipped.Unchanged);
        Assert.Contains("уже есть на складе", skipped.Note);
        Assert.Equal(2, plan.Rows.Count);
    }

    [Fact]
    public void Without_new_only_existing_products_get_a_receipt()
    {
        var plan = Build(newOnly: false);
        Assert.Equal(1, plan.NewCount);
        Assert.Equal(1, plan.ReceiptCount);
        Assert.Equal(0, plan.UnchangedCount);
        Assert.Equal(2, plan.ReadyCount);
        Assert.Equal(2, plan.Lines.Count);
    }
}
