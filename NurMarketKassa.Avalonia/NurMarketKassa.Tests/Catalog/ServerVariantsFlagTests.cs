using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>2026-11-21 (ответ Умара 21.11, п. B): has_variants (bool) и variants_count (число активных вариантов) приходят в products/list/ и products/changes. Карточка плитки и кеш вариантов их читают,
/// а фоновый обход вариантов («Одежда») не спрашивает products/&lt;id&gt;/variants/ у товаров без вариантов (цель — убрать ~180 запросов за 5 минут). Нет полей — обход как раньше. Запускать только tools/qa/test-safe.ps1.</summary>
[Collection("Wave5Database")]
public sealed class ServerVariantsFlagTests
{
    private const string ApiBase = "https://app.nurcrm.kg/";

    private static int _barcodeSeq = 100;

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CatalogProductTileVm Tile(string id, string extra)
    {
        var tile = ProductCatalogMapper.TryTile(J("{\"id\":\"" + id + "\",\"name\":\"Футболка " + id + "\",\"price\":\"500\",\"quantity\":\"3\",\"barcode\":\"4600000009" + Interlocked.Increment(ref _barcodeSeq).ToString("000") + "\"" + extra + "}"), ApiBase);
        tile.Should().NotBeNull();
        return tile!;
    }

    [Fact]
    public void Tile_reads_has_variants_and_variants_count_and_tells_the_variant_cache()
    {
        ProductVariantCache.Clear();
        try
        {
            var withVariants = Tile("fl1", ",\"has_variants\":true,\"variants_count\":3");
            withVariants.ServerHasVariants.Should().BeTrue();
            withVariants.ServerVariantsCount.Should().Be(3);
            ProductVariantCache.KnownWithoutVariants("fl1").Should().BeFalse();

            var without = Tile("fl2", ",\"has_variants\":false,\"variants_count\":0");
            without.ServerHasVariants.Should().BeFalse();
            without.ServerVariantsCount.Should().Be(0);
            ProductVariantCache.KnownWithoutVariants("fl2").Should().BeTrue("сервер сказал «вариантов нет» — обход их не запрашивает");
        }
        finally
        {
            ProductVariantCache.Clear();
        }
    }

    [Fact]
    public void Count_alone_is_enough_zero_means_no_variants_and_a_positive_count_means_variants()
    {
        ProductVariantCache.Clear();
        try
        {
            Tile("fl3", ",\"variants_count\":0").ServerHasVariants.Should().BeFalse();
            ProductVariantCache.KnownWithoutVariants("fl3").Should().BeTrue();

            Tile("fl4", ",\"variants_count\":\"2\"").ServerHasVariants.Should().BeTrue("число строкой тоже читается");
            ProductVariantCache.KnownWithoutVariants("fl4").Should().BeFalse();

            // has_variants важнее счётчика, если они расходятся
            Tile("fl5", ",\"has_variants\":false,\"variants_count\":4").ServerHasVariants.Should().BeFalse();
        }
        finally
        {
            ProductVariantCache.Clear();
        }
    }

    [Fact]
    public void Without_the_fields_nothing_is_known_and_an_earlier_flag_is_not_wiped_by_a_short_card()
    {
        ProductVariantCache.Clear();
        try
        {
            var plain = Tile("fl6", "");
            plain.ServerHasVariants.Should().BeNull();
            plain.ServerVariantsCount.Should().BeNull();
            ProductVariantCache.KnownWithoutVariants("fl6").Should().BeFalse("поля нет — ничего не известно, обход как раньше");

            Tile("fl7", ",\"has_variants\":false");
            Tile("fl7", ""); // сокращённая карточка (поиск, детали) без полей
            ProductVariantCache.KnownWithoutVariants("fl7").Should().BeTrue("признак из списка товаров сокращённая карточка не затирает");

            Tile("fl7", ",\"has_variants\":true");
            ProductVariantCache.KnownWithoutVariants("fl7").Should().BeFalse("у товара появились варианты — следующий список это сообщает");
        }
        finally
        {
            ProductVariantCache.Clear();
        }
    }

    [Fact]
    public void Garbage_in_the_count_is_ignored()
    {
        ProductVariantCache.Clear();
        try
        {
            var t = Tile("fl8", ",\"variants_count\":-5");
            t.ServerVariantsCount.Should().BeNull();
            t.ServerHasVariants.Should().BeNull();
            Tile("fl9", ",\"variants_count\":null").ServerHasVariants.Should().BeNull();
        }
        finally
        {
            ProductVariantCache.Clear();
        }
    }

    [Fact]
    public async Task Background_sweep_asks_the_server_only_about_products_that_may_have_variants()
    {
        var oldLoader = ProductVariantCache.Loader;
        var oldStart = VariantBarcodeIndex.SweepStartDelay;
        var oldItem = VariantBarcodeIndex.SweepItemDelay;
        var asked = new ConcurrentBag<string>();
        var enough = new TaskCompletionSource();
        try
        {
            ProductVariantCache.Clear();
            ProductVariantCache.Loader = (id, _) =>
            {
                asked.Add(id);
                if (asked.Count >= 3)
                    enough.TrySetResult();
                return Task.FromResult(new List<ProductVariantDto>());
            };
            VariantBarcodeIndex.SweepStartDelay = TimeSpan.Zero;
            VariantBarcodeIndex.SweepItemDelay = TimeSpan.Zero;
            string[] ids = ["sw-no1", "sw-no2", "sw-yes", "sw-unk1", "sw-unk2"];
            ProductVariantCache.NoteServerFlag("sw-no1", false);
            ProductVariantCache.NoteServerFlag("sw-no2", false);
            ProductVariantCache.NoteServerFlag("sw-yes", true);
            // sw-unk*: сервер полей не дал (старый ответ) — спрашиваем, как раньше

            using var cts = new CancellationTokenSource();
            var run = VariantBarcodeIndex.RunBackgroundAsync(() => true, () => ids, cts.Token);
            await enough.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(400); // если бы обход собирался спросить и «лишние» товары — он успел бы
            cts.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(15));

            asked.Should().BeEquivalentTo(["sw-yes", "sw-unk1", "sw-unk2"], "товары с has_variants=false не запрашиваются");
        }
        finally
        {
            ProductVariantCache.Loader = oldLoader;
            VariantBarcodeIndex.SweepStartDelay = oldStart;
            VariantBarcodeIndex.SweepItemDelay = oldItem;
            ProductVariantCache.Clear();
        }
    }
}
