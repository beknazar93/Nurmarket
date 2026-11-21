using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>2026-11-20 (обход кассы, п. 24): общий кеш вариантов товара — один запрос на товар, свежее не перезапрашивается,
/// признак «вариантов нет» от сервера и правка карточки. Запускать только tools/qa/test-safe.ps1.</summary>
[Collection("Wave5Database")]
public sealed class ProductVariantCacheTests
{
    private static async Task<T> WithLoaderAsync<T>(Func<string, CancellationToken, Task<List<ProductVariantDto>>> loader, Func<Task<T>> body)
    {
        var old = ProductVariantCache.Loader;
        try
        {
            ProductVariantCache.Clear();
            ProductVariantCache.Loader = loader;
            return await body();
        }
        finally
        {
            ProductVariantCache.Loader = old;
            ProductVariantCache.Clear();
        }
    }

    [Fact]
    public async Task ParallelRequests_ForOneProduct_HitServerOnce()
    {
        var calls = 0;
        var gate = new TaskCompletionSource();
        var all = await WithLoaderAsync(async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return new List<ProductVariantDto> { new() { Id = "v1", Size = "42", Quantity = 3 } };
        }, async () =>
        {
            var tasks = Enumerable.Range(0, 6).Select(_ => ProductVariantCache.GetAsync("pd1", TimeSpan.FromSeconds(5))).ToArray();
            await Task.Delay(150);
            gate.SetResult();
            return await Task.WhenAll(tasks);
        });

        calls.Should().Be(1, "параллельные запросы одного товара объединяются");
        all.Should().OnlyContain(l => l != null && l.Count == 1 && l[0].Quantity == 3);
    }

    [Fact]
    public async Task SecondRead_FromFreshCache_DoesNotAskServer()
    {
        var calls = 0;
        await WithLoaderAsync((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new List<ProductVariantDto> { new() { Id = "v1", Size = "M", Quantity = 1 } });
        }, async () =>
        {
            await ProductVariantCache.GetAsync("pf1", TimeSpan.FromSeconds(2));
            await ProductVariantCache.GetAsync("pf1", TimeSpan.FromSeconds(2));
            await ProductVariantCache.GetAsync("pf1", TimeSpan.FromSeconds(2));
            return 0;
        });

        calls.Should().Be(1);
    }

    [Fact]
    public async Task MarkStale_ShowsCachedAtOnce_AndRefreshesInBackground()
    {
        var calls = 0;
        await WithLoaderAsync((_, _) =>
        {
            var n = Interlocked.Increment(ref calls);
            return Task.FromResult(new List<ProductVariantDto> { new() { Id = "v1", Size = "M", Quantity = n } });
        }, async () =>
        {
            await ProductVariantCache.GetAsync("ps1", TimeSpan.FromSeconds(2));
            ProductVariantCache.MarkStale("ps1");
            var shown = await ProductVariantCache.GetAsync("ps1", TimeSpan.FromSeconds(2));
            shown![0].Quantity.Should().Be(1, "устаревшее показывается сразу");
            for (var i = 0; i < 40 && Volatile.Read(ref calls) < 2; i++)
                await Task.Delay(50);
            return 0;
        });

        calls.Should().Be(2, "после пометки «устарело» — одно фоновое обновление");
    }

    [Fact]
    public void ServerFlag_NoVariants_IsKnown_AndPutWithVariantsClearsIt()
    {
        var old = ProductVariantCache.Loader;
        try
        {
            ProductVariantCache.Clear();
            ProductVariantCache.KnownWithoutVariants("pn1").Should().BeFalse("признака нет — ничего не известно");
            ProductVariantCache.NoteServerFlag("pn1", false);
            ProductVariantCache.KnownWithoutVariants("pn1").Should().BeTrue();
            ProductVariantCache.NoteServerFlag("pn2", true);
            ProductVariantCache.KnownWithoutVariants("pn2").Should().BeFalse();

            // Правка в карточке товара добавила размер — прежний признак сервера недействителен.
            ProductVariantCache.Put("pn1", new List<ProductVariantDto> { new() { Id = "v9", Size = "L", Quantity = 1 } });
            ProductVariantCache.KnownWithoutVariants("pn1").Should().BeFalse();
            ProductVariantCache.IsFresh("pn1", TimeSpan.FromMinutes(1)).Should().BeTrue();

            ProductVariantCache.NoteServerFlag("pn3", false);
            ProductVariantCache.NoteServerFlag("pn3", null);
            ProductVariantCache.KnownWithoutVariants("pn3").Should().BeFalse("сервер перестал отдавать признак");
        }
        finally
        {
            ProductVariantCache.Loader = old;
            ProductVariantCache.Clear();
        }
    }

    [Fact]
    public async Task IsLoading_TrueWhileRequestRuns_And_IsFreshAfter()
    {
        var gate = new TaskCompletionSource();
        await WithLoaderAsync(async (_, _) =>
        {
            await gate.Task;
            return new List<ProductVariantDto>();
        }, async () =>
        {
            var t = ProductVariantCache.GetAsync("pl1", TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            ProductVariantCache.IsLoading("pl1").Should().BeTrue();
            ProductVariantCache.IsFresh("pl1", TimeSpan.FromMinutes(1)).Should().BeFalse();
            gate.SetResult();
            (await t)!.Should().BeEmpty();
            ProductVariantCache.IsFresh("pl1", TimeSpan.FromMinutes(1)).Should().BeTrue();
            return 0;
        });
    }

    [Fact]
    public async Task ServerFails_ReturnsNull_WithoutBreakingNextRead()
    {
        var fail = true;
        await WithLoaderAsync((_, _) => fail
            ? Task.FromException<List<ProductVariantDto>>(new InvalidOperationException("нет сети"))
            : Task.FromResult(new List<ProductVariantDto> { new() { Id = "v1", Size = "S", Quantity = 2 } }),
        async () =>
        {
            (await ProductVariantCache.GetAsync("pe1", TimeSpan.FromSeconds(2))).Should().BeNull();
            fail = false;
            await Task.Delay(100); // дать снять запись «запрос идёт» после неудачи
            (await ProductVariantCache.GetAsync("pe1", TimeSpan.FromSeconds(2)))!.Single().Quantity.Should().Be(2);
            return 0;
        });
    }
}
