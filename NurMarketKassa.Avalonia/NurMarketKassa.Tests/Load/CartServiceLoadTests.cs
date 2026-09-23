using System.Diagnostics;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;

namespace NurMarketKassa.Tests.Load;

/// <summary>
/// НАГРУЗКА: стресс-профиль корзины (<see cref="CartService"/>).
/// <para>
/// Каждая операция редактора чека полностью перепарсивает JSON-снимок корзины, поэтому
/// стоимость добавления растёт вместе с числом строк. Тесты фиксируют, что на реальных
/// для кассы объёмах (крупный опт: тысячи позиций, тысячи правок количества) операции
/// остаются в разумном бюджете времени И арифметика итогов остаётся точной.
/// </para>
/// <para>
/// Бюджеты времени намеренно щедрые (кратный запас к локальному прогону), чтобы тест
/// ловил алгоритмическую деградацию, а не медленное железо CI.
/// </para>
/// <para>
/// ИЗМЕРЕННАЯ ХАРАКТЕРИСТИКА (не гипотеза): стоимость добавления позиции растёт
/// сверхлинейно, потому что <c>ReceiptSnapshotCartEditor.AddProduct</c> на КАЖДОМ вызове
/// заново парсит и сериализует ВЕСЬ снимок чека несколько раз
/// (<c>ParseCartRoot</c> → <c>RecalcCartTotals</c> (ToJsonString + Parse) → <c>ApplyRoot</c>).
/// Доминирующая часть стоимости — <c>ParseCartRoot</c>, который на каждый вызов заново
/// строит полное mutable-дерево <c>JsonObject</c> из текста для всех уже добавленных строк;
/// это неизбежно, пока canonical-состояние корзины — JSON-текст, а не персистентное дерево
/// между вызовами. Один из избыточных проходов (повторный парсинг внутри <c>SetCart</c>)
/// устранён (<c>CartSession.SetCartFromOwnedDocument</c>), это дало ~10% ускорение
/// (~31 с → ~28 с на 2000 позициях) без изменения архитектуры хранения — полное устранение
/// O(n²) потребовало бы более рискованной переделки модели хранения, решено этого не делать.
/// Замер на этой машине после фикса: 100 позиций — ~190 мс, 200 — ~680 мс, 2000 — ~28 с.
/// На реальных чеках (10–100 строк) это незаметно.
/// Бюджет 2000-позиционного теста — анти-hang предохранитель (запас к ~28 с),
/// острая же регрессионная проверка стоимости одной позиции живёт в
/// <see cref="AddItem_TwoHundredProducts_StaysWithinPerItemBudget"/>.
/// </para>
/// </summary>
public sealed class CartServiceLoadTests : IDisposable
{
    private readonly CartService _sut = new();

    public void Dispose() => _sut.Dispose();

    [Fact]
    public void AddItem_TwoThousandDistinctProducts_KeepsTotalsExact_WithinTimeBudget()
    {
        CartTestHelper.StartEmptyCart(_sut);

        const int productCount = 2_000;
        var products = new CatalogProductTileVm[productCount];
        var expectedTotal = 0m;

        for (var i = 0; i < productCount; i++)
        {
            // Целые цены: сумма проверяется точно, без плавающей погрешности.
            var price = 1m + (i % 100);
            expectedTotal += price;
            products[i] = CartTestHelper.CreateProduct(
                $"test-load-sku-{i:D5}",
                $"Нагрузочный товар {i:D5}",
                price);
        }

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < productCount; i++)
            _sut.AddItem(products[i], 1);
        stopwatch.Stop();

        _sut.LineCount.Should().Be(productCount);
        _sut.TotalQuantity.Should().Be(productCount);
        _sut.TotalAmount.Should().Be(expectedTotal);
        _sut.Items.Select(x => x.ProductId).Distinct().Should().HaveCount(productCount);

        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(70),
            "добавление {0} позиций заняло {1} мс при измеренной базовой линии ~31000 мс — " +
            "это уже не известная квадратичность, а зависание или новая деградация",
            productCount,
            stopwatch.ElapsedMilliseconds);
    }

    [Fact]
    public void AddItem_TwoHundredProducts_StaysWithinPerItemBudget()
    {
        // Острая регрессионная проверка: на реалистичном для кассы объёме (крупный чек ~200 строк)
        // средняя стоимость позиции измеряется в единицах миллисекунд. Замер базовой линии — 754 мс.
        CartTestHelper.StartEmptyCart(_sut);

        const int productCount = 200;
        var products = Enumerable.Range(0, productCount)
            .Select(i => CartTestHelper.CreateProduct(
                $"test-load-fast-{i:D3}",
                $"Позиция {i:D3}",
                10m))
            .ToArray();

        var stopwatch = Stopwatch.StartNew();
        foreach (var product in products)
            _sut.AddItem(product, 1);
        stopwatch.Stop();

        _sut.LineCount.Should().Be(productCount);
        _sut.TotalAmount.Should().Be(productCount * 10m);

        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(5),
            "чек из {0} строк собирался {1} мс ({2:F2} мс/позиция) при базовой линии ~3.8 мс/позиция",
            productCount,
            stopwatch.ElapsedMilliseconds,
            stopwatch.Elapsed.TotalMilliseconds / productCount);
    }

    [Fact]
    public void UpdateQuantity_FiveThousandUpdatesOnSmallCart_KeepsTotalsExact_WithoutDegradation()
    {
        CartTestHelper.StartEmptyCart(_sut);

        const int lineCount = 20;
        const int updateCount = 5_000;

        for (var i = 0; i < lineCount; i++)
        {
            _sut.AddItem(
                CartTestHelper.CreateProduct(
                    $"test-load-upd-{i:D2}",
                    $"Позиция {i:D2}",
                    10m + i),
                1);
        }

        var itemIds = _sut.Items.Select(x => x.Id!).ToArray();
        itemIds.Should().HaveCount(lineCount).And.OnlyContain(id => !string.IsNullOrEmpty(id));

        var stopwatch = Stopwatch.StartNew();
        for (var k = 0; k < updateCount; k++)
        {
            var lineIndex = k % lineCount;
            var quantity = (k / lineCount) + 1;
            _sut.UpdateQuantity(itemIds[lineIndex], quantity);
        }
        stopwatch.Stop();

        // Последняя запись для каждой строки — итерации 4980..4999 → количество 250.
        const int expectedQuantityPerLine = updateCount / lineCount;
        var expectedTotal = Enumerable.Range(0, lineCount)
            .Sum(i => (10m + i) * expectedQuantityPerLine);

        _sut.LineCount.Should().Be(lineCount);
        _sut.TotalQuantity.Should().Be(lineCount * expectedQuantityPerLine);
        _sut.TotalAmount.Should().Be(expectedTotal);
        _sut.Items.Should().OnlyContain(x => x.Quantity == expectedQuantityPerLine);

        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(20),
            "{0} обновлений количества на {1} строках заняли {2} мс",
            updateCount,
            lineCount,
            stopwatch.ElapsedMilliseconds);
    }
}
