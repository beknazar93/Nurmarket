using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Core.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11 (Д-7, живая проверка «Склада» на NBS): память программы владельца выросла 330 → 944 МБ за проверку, на «Ревизии» частная память до 2,1 ГБ
/// (журнал: «зафиксировано сборщиком 1888 МБ», gen2 2021 МБ при живых 400 МБ — на «Ревизии» создавались и умирали гигабайты объектов).
/// Замер в Avalonia.Headless на каталоге 15 000 товаров: обход всех вкладок «Склада», «Начать ревизию», пять раз открыть-закрыть окно.
/// Цифры пишутся в %TEMP%\nurmarket-tests\warehouse-memory.txt (до/после — в отчёте).</summary>
[Collection("UiScenarios")]
public sealed class WarehouseMemoryUiTests(ITestOutputHelper output)
{
    private const int CatalogSize = 15_000;

    private static long Managed()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        return GC.GetTotalMemory(true);
    }

    private static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.0") + " МБ";

    private static void SeedCatalog()
    {
        var list = new List<CatalogProductTileVm>(CatalogSize);
        for (var i = 0; i < CatalogSize; i++)
        {
            var id = "mem-" + i.ToString("D5");
            list.Add(new CatalogProductTileVm(id, $"Товар для замера памяти № {i} — длинное название с размером и цветом", (100 + i % 900).ToString() + ",00 сом", false)
            {
                Barcode = "46" + i.ToString("D11"),
                Unit = "шт",
                Quantity = i % 37,
                Category = "Категория " + (i % 40),
            });
        }
        CatalogCacheService.SetProducts(list);
        UiShot.Settle(4);
    }

    private static int CountByTypeName(Visual root, string typeName) =>
        root.GetVisualDescendants().Count(v => v.GetType().Name == typeName);

    [AvaloniaFact]
    public async Task Warehouse_AllTabs_And_Revision_On15000Catalog_MemoryBounded_ListVirtualized()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var oldProducts = CatalogCacheService.Products.ToList();
        var report = new List<string>();
        void Note(string s)
        {
            report.Add(s);
            output.WriteLine(s);
        }

        try
        {
            SeedCatalog();
            CatalogCacheService.Products.Count.Should().Be(CatalogSize);
            var baseline = Managed();
            Note($"каталог {CatalogSize}: управляемая куча до окна {Mb(baseline)}, рабочий набор {Mb(Process.GetCurrentProcess().WorkingSet64)}");

            var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
            // как в программе владельца: раздел встроен (AsOwnerSection) и обёрнут в масштаб 90 % (OwnerShellWindow.WrapSectionForScale) — так было на NBS
            window.AsOwnerSection();
            var content = (Control)window.Content!;
            window.Content = null;
            window.Content = new LayoutTransformControl
            {
                Child = new SectionFitHost { Child = content },
                LayoutTransform = new Avalonia.Media.ScaleTransform(0.9, 0.9),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            };
            long afterTabs, afterRevision, revisionPeak;
            int revisionRows;
            try
            {
                using var _ = UiShot.Render(window, new UiSize(1600, 900));
                UiShot.Settle(10);
                var tabs = UiDrive.Find<TabControl>(window, "WarehouseTabs");
                foreach (var item in tabs.Items.OfType<TabItem>().Where(t => t.IsVisible).ToList())
                {
                    tabs.SelectedItem = item;
                    UiShot.Settle(10);
                    Note($"вкладка «{item.Header}»: куча {Mb(GC.GetTotalMemory(false))}");
                }
                afterTabs = Managed();
                Note($"после обхода вкладок: куча {Mb(afterTabs)} (+{Mb(afterTabs - baseline)})");

                // «Ревизия» → «Начать ревизию» (сервер в тестах недоступен — остатки берутся с этого компьютера)
                tabs.SelectedItem = UiDrive.Find<TabItem>(window, "RevisionTabItem");
                UiShot.Settle(6);
                var start = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && (b.Content as string ?? (b.Content as TextBlock)?.Text ?? "").Contains("Начать ревизию"));
                // сервер в тестах — заглушка: обновление остатков перед ревизией подменяем «успешно, без изменений», иначе общий каталог теста (2 товара) затёр бы 15 000
                var syncOverride = typeof(WarehouseWindow).GetProperty("RevisionSyncOverride", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                syncOverride.SetValue(null, (Func<CancellationToken, Task<CatalogSyncResult>>)(_ => Task.FromResult(new CatalogSyncResult { Success = true })));
                CatalogCacheService.Products.Count.Should().Be(CatalogSize, "каталог 15 000 на месте перед «Начать ревизию»");
                var before = GC.CollectionCount(2);
                var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                UiDrive.Click(window, start);
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline && CountByTypeName(window, "RevisionRowView") == 0)
                    UiShot.Settle(2, 20);
                UiShot.Settle(10);
                revisionPeak = GC.GetTotalMemory(false);
                revisionRows = CountByTypeName(window, "RevisionRowView");
                var revList = window.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.ItemsSource?.GetType().GetGenericArguments().FirstOrDefault()?.Name == "RevisionRow");
                revList!.ItemCount.Should().BeGreaterThan(CatalogSize - 10, "в ревизии весь каталог 15 000");
                Note($"список ревизии: элементов {revList?.ItemCount}, размер {revList?.Bounds.Width:0}×{revList?.Bounds.Height:0}, контейнеров {revList?.GetRealizedContainers().Count()}");
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                afterRevision = Managed();
                Note($"ревизия начата: строк на экране (RevisionRowView) {revisionRows}; выделено за старт {Mb(allocated)}; куча сразу {Mb(revisionPeak)}, после сборки {Mb(afterRevision)} (+{Mb(afterRevision - baseline)} к исходной); сборок gen2 {GC.CollectionCount(2) - before}");

                revisionRows.Should().BeLessThan(200, "список ревизии виртуализирован: на экране только видимые строки, а не 15 000 (Д-7)");
                (afterRevision - baseline).Should().BeLessThan(400L * 1024 * 1024, "после «Начать ревизию» на 15 000 товаров живых объектов не больше 400 МБ сверх исходного");
                allocated.Should().BeLessThan(1200L * 1024 * 1024, "старт ревизии не выделяет гигабайты временных объектов (журнал NBS: gen2 2 ГБ)");

                // как на NBS: скан товара в поле ревизии (сканер — цифры и Enter), факт, затем «Бросить ревизию» (RevisionDetach)
                var search = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && (t.Watermark?.ToString() ?? "").StartsWith("Скан или поиск"));
                var allocScan = GC.GetTotalAllocatedBytes(precise: true);
                for (var k = 0; k < 3; k++)
                {
                    search.Focus();
                    UiShot.Pump();
                    foreach (var ch in "46" + (1234 + k).ToString("D11"))
                        window.KeyTextInput(ch.ToString());
                    window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
                    UiShot.Settle(10);
                    window.KeyTextInput("9");
                    window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
                    UiShot.Settle(10);
                }
                var scanAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocScan;
                var afterScans = Managed();
                Note($"3 скана с фактом: выделено {Mb(scanAllocated)}, куча после сборки {Mb(afterScans)}; строк на экране {CountByTypeName(window, "RevisionRowView")}");
                var allocDetach = GC.GetTotalAllocatedBytes(precise: true);
                typeof(WarehouseWindow).GetMethod("RevisionDetach", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null);
                UiShot.Settle(10);
                var afterDetach = Managed();
                Note($"ревизия брошена: выделено {Mb(GC.GetTotalAllocatedBytes(precise: true) - allocDetach)}, куча после сборки {Mb(afterDetach)}");
                scanAllocated.Should().BeLessThan(600L * 1024 * 1024, "сканы в ревизии не выделяют сотни мегабайт");
            }
            finally
            {
                typeof(WarehouseWindow).GetProperty("RevisionSyncOverride", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);
                window.Close();
                UiShot.Pump();
            }

            // пять раз открыть-закрыть окно склада (все вкладки) — рост ограничен, окна не копятся
            var weak = new List<WeakReference>();
            long afterFirst = 0;
            for (var round = 0; round < 5; round++)
            {
                var w = TestHost.Services.GetRequiredService<WarehouseWindow>();
                weak.Add(new WeakReference(w));
                using (UiShot.Render(w, new UiSize(1600, 900)))
                {
                    UiShot.Settle(6);
                    var t = UiDrive.Find<TabControl>(w, "WarehouseTabs");
                    // все видимые вкладки (в том числе «Штрихкоды» — она собирается кодом, без имени)
                    foreach (var ti in t.Items.OfType<TabItem>().Where(x => x.IsVisible).ToList())
                    {
                        t.SelectedItem = ti;
                        UiShot.Settle(6);
                    }
                }
                w.Close();
                UiShot.Pump();
                var m = Managed();
                if (round == 0)
                    afterFirst = m;
                Note($"открыть-закрыть №{round + 1}: куча {Mb(m)}");
            }
            var last = Managed();
            (last - afterFirst).Should().BeLessThan(60L * 1024 * 1024, "5 раз открыть-закрыть склад — рост кучи ограничен (нет утечки окон/подписок)");
            var alive = weak.Count(r => r.IsAlive);
            Note($"живых окон склада после 5 закрытий: {alive} из 5");
            alive.Should().BeLessThanOrEqualTo(1, "закрытые окна склада собираются сборщиком");
        }
        finally
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests");
                Directory.CreateDirectory(dir);
                File.WriteAllLines(Path.Combine(dir, "warehouse-memory.txt"), report);
            }
            catch
            {
                // отчёт — для глаз
            }
            CatalogCacheService.SetProducts(oldProducts);
            UiShot.Settle(2);
        }
    }
}
