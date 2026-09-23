using System.IO;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Concurrency;

/// <summary>
/// КОНКУРЕНТНОСТЬ: <see cref="DeferredCartsStore"/> — «прочитать-изменить-сохранить» из нескольких потоков.
/// <para>
/// Хранилище пишет в РЕАЛЬНЫЙ файл профиля пользователя
/// (<c>%APPDATA%\NurMarketKassa\deferred_carts.json</c>), поэтому класс снимает побайтовый бэкап
/// файла в конструкторе и точно восстанавливает его в <see cref="Dispose"/> — включая случай,
/// когда файла изначально не было (тогда он удаляется). Восстановление происходит и при падении
/// теста, потому что xUnit всегда вызывает Dispose. Все тестовые записи используют префикс
/// <c>test-concurrency-</c> и дополнительно удаляются через <c>RemoveIds</c>.
/// </para>
/// </summary>
public sealed class DeferredCartsStoreConcurrencyTests : IDisposable
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NurMarketKassa",
        "deferred_carts.json");

    private readonly bool _fileExistedBefore;
    private readonly byte[]? _originalBytes;

    public DeferredCartsStoreConcurrencyTests()
    {
        _fileExistedBefore = File.Exists(FilePath);
        _originalBytes = _fileExistedBefore ? File.ReadAllBytes(FilePath) : null;
    }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (_fileExistedBefore && _originalBytes != null)
            File.WriteAllBytes(FilePath, _originalBytes);
        else if (File.Exists(FilePath))
            File.Delete(FilePath);

        var tempPath = FilePath + ".tmp";
        if (File.Exists(tempPath))
            File.Delete(tempPath);
    }

    [Fact]
    public async Task Add_FromFiftyParallelTasks_LosesNoEntries_AndCompletesWithoutHang()
    {
        const int writerCount = 50;
        var preExistingIds = DeferredCartsStore.LoadAll().Select(x => x.Id).ToArray();

        var entries = Enumerable.Range(0, writerCount)
            .Select(i => new DeferredCartEntry
            {
                Id = $"test-concurrency-{Guid.NewGuid():N}",
                Label = $"Параллельная запись {i}",
                CartJson = $$"""{"items":[],"marker":{{i}}}""",
            })
            .ToArray();

        var addedIds = entries.Select(x => x.Id).ToArray();

        try
        {
            var writes = entries.Select(entry => Task.Run(() => DeferredCartsStore.Add(entry))).ToArray();

            // Таймаут = детектор зависания: UpdateGate/FileLock не должны давать взаимную блокировку.
            var completion = Task.WhenAll(writes);
            var finished = await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(30)));
            finished.Should().BeSameAs(
                completion,
                "possible deadlock: {0} параллельных DeferredCartsStore.Add не завершились за 30 секунд",
                writerCount);
            await completion;

            var stored = DeferredCartsStore.LoadAll();
            var storedIds = stored.Select(x => x.Id).ToArray();

            storedIds.Should().Contain(
                addedIds,
                "потерянные обновления: read-modify-write должен быть атомарным под общим UpdateGate");
            if (preExistingIds.Length > 0)
            {
                storedIds.Should().Contain(
                    preExistingIds,
                    "конкурентная запись не имеет права затирать ранее сохранённые отложенные чеки пользователя");
            }

            DeferredCartsStore.Count().Should().Be(preExistingIds.Length + writerCount);

            // Содержимое записей не должно смешаться между потоками.
            foreach (var entry in entries)
            {
                var found = DeferredCartsStore.TryGetById(entry.Id);
                found.Should().NotBeNull();
                found!.Label.Should().Be(entry.Label);
                found.CartJson.Should().Be(entry.CartJson);
            }
        }
        finally
        {
            DeferredCartsStore.RemoveIds(addedIds);
        }

        DeferredCartsStore.LoadAll().Select(x => x.Id)
            .Should().NotIntersectWith(addedIds, "тестовые записи обязаны быть вычищены из файла пользователя");
    }

    [Fact]
    public async Task AddAndRemove_Interleaved_NeverCorruptsJsonFile()
    {
        const int rounds = 30;
        var preExistingCount = DeferredCartsStore.Count();
        var ids = Enumerable.Range(0, rounds)
            .Select(_ => $"test-concurrency-{Guid.NewGuid():N}")
            .ToArray();

        try
        {
            var writers = ids.Select(id => Task.Run(() => DeferredCartsStore.Add(new DeferredCartEntry
            {
                Id = id,
                Label = "interleaved",
            })));

            var readers = Enumerable.Range(0, rounds).Select(reader => Task.Run(() =>
            {
                for (var i = 0; i < 20; i++)
                {
                    // Чтение параллельно с записью не должно ловить частично записанный файл:
                    // SaveAll пишет во временный файл и подменяет его атомарно.
                    DeferredCartsStore.LoadAll().Should().NotBeNull();
                    _ = DeferredCartsStore.TryGetLatest();
                }
            }));

            var completion = Task.WhenAll(writers.Concat(readers));
            var finished = await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(30)));
            finished.Should().BeSameAs(completion, "possible deadlock: смешанные чтение/запись не завершились за 30 секунд");
            await completion;

            DeferredCartsStore.Count().Should().Be(preExistingCount + rounds);
        }
        finally
        {
            DeferredCartsStore.RemoveIds(ids);
        }

        DeferredCartsStore.Count().Should().Be(preExistingCount);
    }
}
