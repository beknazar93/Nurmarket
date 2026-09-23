using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;

namespace NurMarketKassa.Tests.Concurrency;

/// <summary>
/// КОНКУРЕНТНОСТЬ / ЗАВИСАНИЯ: <see cref="CartService"/> под одновременным доступом.
/// <para>
/// В кассе корзину трогают одновременно: UI-поток, поток сканера штрихкодов, поток весов
/// и фоновая синхронизация. Всё состояние сериализовано одним монитором <c>_sync</c>.
/// Тесты не проверяют «правильную» итоговую сумму при гонке (порядок операций недетерминирован),
/// они проверяют два обязательных свойства: (1) операции гарантированно ЗАВЕРШАЮТСЯ —
/// нет взаимной блокировки, (2) не вылетает <see cref="InvalidOperationException"/> вида
/// «Collection was modified» из-за чтения проекций во время правки снимка.
/// </para>
/// </summary>
public sealed class CartServiceConcurrencyTests : IDisposable
{
    private static readonly TimeSpan HangBudget = TimeSpan.FromSeconds(15);

    private readonly CartService _sut = new();

    public void Dispose() => _sut.Dispose();

    [Fact]
    public async Task MixedReadWriteStorm_CompletesWithoutDeadlockOrCollectionErrors()
    {
        CartTestHelper.StartEmptyCart(_sut);

        const int workerCount = 20;
        const int iterationsPerWorker = 60;

        // Стартовый набор строк, чтобы у читателей и у UpdateQuantity сразу была работа.
        for (var i = 0; i < workerCount; i++)
        {
            _sut.AddItem(
                CartTestHelper.CreateProduct($"test-concurrency-seed-{i:D2}", $"Стартовая {i:D2}", 10m + i),
                1);
        }

        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var workers = Enumerable.Range(0, workerCount).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < iterationsPerWorker; i++)
            {
                try
                {
                    switch ((worker + i) % 4)
                    {
                        case 0:
                            _sut.AddItem(
                                CartTestHelper.CreateProduct(
                                    $"test-concurrency-{worker:D2}-{i:D3}",
                                    $"Товар {worker:D2}-{i:D3}",
                                    5m + (i % 20)),
                                1);
                            break;

                        case 1:
                            var snapshot = _sut.Items;
                            if (snapshot.Count > 0)
                            {
                                var line = snapshot[(worker + i) % snapshot.Count];
                                if (!string.IsNullOrEmpty(line.Id))
                                    _sut.UpdateQuantity(line.Id!, 1 + ((worker + i) % 5));
                            }
                            break;

                        case 2:
                            // Проекции обязаны отдавать согласованный неизменяемый снимок.
                            var items = _sut.Items;
                            var recomputed = items.Sum(x => x.LineTotal);
                            recomputed.Should().BeGreaterThanOrEqualTo(0m);
                            break;

                        default:
                            _ = _sut.TotalAmount;
                            _ = _sut.TotalQuantity;
                            _ = _sut.LineCount;
                            _ = _sut.GetRawText();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
        })).ToArray();

        var completion = Task.WhenAll(workers);
        var finished = await Task.WhenAny(completion, Task.Delay(HangBudget));
        finished.Should().BeSameAs(
            completion,
            "possible deadlock: {0} параллельных потоков не завершили работу с корзиной за {1} с",
            workerCount,
            HangBudget.TotalSeconds);
        await completion;

        failures.Should().BeEmpty(
            "конкурентный доступ к корзине не должен бросать исключений; первое: {0}",
            failures.FirstOrDefault()?.ToString() ?? "<нет>");

        // Состояние остаётся связным: сумма чека совпадает с суммой строк.
        var finalItems = _sut.Items;
        finalItems.Should().NotBeEmpty();
        _sut.LineCount.Should().Be(finalItems.Count);
        _sut.TotalAmount.Should().BeApproximately(finalItems.Sum(x => x.LineTotal), 0.01m);
        _sut.TotalQuantity.Should().BeApproximately(finalItems.Sum(x => x.Quantity), 0.0001);
    }

    [Fact]
    public async Task ParallelAddOfDistinctProducts_LosesNoLines()
    {
        CartTestHelper.StartEmptyCart(_sut);

        const int productCount = 200;
        var products = Enumerable.Range(0, productCount)
            .Select(i => CartTestHelper.CreateProduct(
                $"test-concurrency-distinct-{i:D3}",
                $"Уникальный {i:D3}",
                1m + (i % 50)))
            .ToArray();

        var expectedTotal = Enumerable.Range(0, productCount).Sum(i => 1m + (i % 50));

        var completion = Task.WhenAll(products.Select(p => Task.Run(() => _sut.AddItem(p, 1))));
        var finished = await Task.WhenAny(completion, Task.Delay(HangBudget));
        finished.Should().BeSameAs(completion, "possible deadlock: параллельное добавление позиций не завершилось");
        await completion;

        _sut.LineCount.Should().Be(productCount, "ни одна строка не должна потеряться под lock(_sync)");
        _sut.TotalQuantity.Should().Be(productCount);
        _sut.TotalAmount.Should().Be(expectedTotal);
        _sut.Items.Select(x => x.ProductId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ClearWhileWritersRun_NeverThrows_AndLeavesConsistentState()
    {
        CartTestHelper.StartEmptyCart(_sut);

        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    _sut.AddItem(
                        CartTestHelper.CreateProduct($"test-concurrency-clear-{i:D5}", $"Позиция {i}", 7m),
                        1);
                }
                catch (InvalidOperationException)
                {
                    // Ожидаемо и допустимо: Clear() закрыл чек между проверкой и правкой.
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }

                i++;
            }
        });

        var cleaner = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    _sut.Clear();
                    CartTestHelper.StartEmptyCart(_sut);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
        });

        var completion = Task.WhenAll(writer, cleaner);
        var finished = await Task.WhenAny(completion, Task.Delay(HangBudget));
        finished.Should().BeSameAs(
            completion,
            "possible deadlock: Clear() параллельно с AddItem заблокировал корзину");
        await completion;

        failures.Should().BeEmpty(
            "Clear() параллельно с записью не должен бросать неожиданных исключений; первое: {0}",
            failures.FirstOrDefault()?.ToString() ?? "<нет>");

        // Даже после шторма итоги пересчитываются без исключений и остаются связными.
        var items = _sut.Items;
        _sut.LineCount.Should().Be(items.Count);
        _sut.TotalAmount.Should().BeApproximately(items.Sum(x => x.LineTotal), 0.01m);
    }
}
