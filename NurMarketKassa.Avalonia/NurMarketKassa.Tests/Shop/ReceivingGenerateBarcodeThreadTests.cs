using System.Collections.Concurrent;
using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-22 (владелец: «при нажатии на сгенерировать штрихкод выходит ошибка» на новом товаре в «Склад → Приёмка»): GenerateForReceivingLineAsync ждал код сервера через ConfigureAwait(false)
/// и записывал <c>line.Barcode</c> уже в потоке пула, а не в потоке окна — привязанное поле «Штрихкод» падало с «Call from invalid thread», касса показывала окно «Ошибка».
/// Тест гоняет метод в «потоке окна» (однопоточный контекст) и требует, чтобы свойство менялось именно в нём.</summary>
[Collection("UserPreferences")]
public sealed class ReceivingGenerateBarcodeThreadTests : IDisposable
{
    public ReceivingGenerateBarcodeThreadTests()
    {
        ServerBarcodes.ResetForTests();
        ServerBarcodes.NextOverride = null;
    }

    public void Dispose()
    {
        ServerBarcodes.NextOverride = null;
        ServerBarcodes.ResetForTests();
        NoFactoryBarcodes.ResetForTests();
    }

    [Fact]
    public void Server_barcode_is_written_to_the_line_on_the_calling_UI_thread()
    {
        // сервер отвечает не сразу (как настоящий запрос) — продолжение после await возвращается в «поток окна» только при ConfigureAwait(true)
        ServerBarcodes.NextOverride = async (count, _) =>
        {
            await Task.Delay(30).ConfigureAwait(false);
            return new[] { "4600000000011" };
        };
        var line = new ReceivingLineVm { ProductName = "Новый товар", Source = ReceivingSource.Unknown };
        var allLines = new List<ReceivingLineVm> { line };
        var changedOn = new List<int>();
        line.PropertyChanged += (_, _) => changedOn.Add(Environment.CurrentManagedThreadId);

        using var ui = new SingleThreadContext();
        var uiThread = ui.ThreadId;
        var ok = ui.Run(() => BarcodeGenerator.GenerateForReceivingLineAsync(line, allLines));

        ok.Should().BeTrue();
        line.Barcode.Should().Be("4600000000011");
        changedOn.Should().NotBeEmpty();
        changedOn.Should().OnlyContain(id => id == uiThread, "поле «Штрихкод» привязано к окну — менять его можно только в потоке окна");
    }

    [Fact]
    public void Local_fallback_barcode_is_also_written_on_the_UI_thread_when_the_server_is_down()
    {
        ServerBarcodes.NextOverride = async (_, _) =>
        {
            await Task.Delay(20).ConfigureAwait(false);
            return null;
        };
        var line = new ReceivingLineVm { ProductName = "Новый товар", Source = ReceivingSource.Unknown };
        var changedOn = new List<int>();
        line.PropertyChanged += (_, _) => changedOn.Add(Environment.CurrentManagedThreadId);

        using var ui = new SingleThreadContext();
        var uiThread = ui.ThreadId;
        var ok = ui.Run(() => BarcodeGenerator.GenerateForReceivingLineAsync(line, new[] { line }));

        ok.Should().BeTrue();
        BarcodeGenerator.HasValidCheckDigit(line.Barcode).Should().BeTrue();
        changedOn.Should().OnlyContain(id => id == uiThread);
    }

    /// <summary>Однопоточный контекст синхронизации: свой поток и очередь — как поток окна Avalonia (продолжения await возвращаются в него).</summary>
    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public int ThreadId { get; private set; }

        public SingleThreadContext()
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                ThreadId = Environment.CurrentManagedThreadId;
                SetSynchronizationContext(this);
                ready.Set();
                foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                    callback(state);
            })
            { IsBackground = true };
            _thread.Start();
            ready.Wait();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public T Run<T>(Func<Task<T>> body)
        {
            var done = new TaskCompletionSource<T>();
            Post(async _ =>
            {
                try { done.SetResult(await body()); }
                catch (Exception ex) { done.SetException(ex); }
            }, null);
            return done.Task.GetAwaiter().GetResult();
        }

        public void Dispose() => _queue.CompleteAdding();
    }
}
