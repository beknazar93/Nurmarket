using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-11, запрос владельца «массовая печать ценников/штрихкодов — выведи в Склад сверху! Как в вебе!»: раздел программы владельца «Товары → Штрихкоды и ценники».
/// Тот же экран, что вкладка «Штрихкоды» в «Складе» (<see cref="BarcodePrintTabHost"/> → <see cref="BarcodePrintView"/>: выбор товаров, размер этикетки, копии, «Печать на A4»,
/// «Тест-печать», «Виды ценников…»), но отдельным пунктом меню — чтобы не искать вкладку внутри «Склада». Права — как у «Склада» (проверяет пункт меню) и «Печать штрих-кодов»;
/// платность прежняя (массовая печать — платная функция): не куплена — вместо печати подсказка с «Ввести серийный номер».</summary>
public sealed class BarcodePrintSectionWindow : Window, IOwnerSection, IOwnerSectionKeepAlive
{
    private readonly BarcodePrintTabHost _host = new();

    public BarcodePrintSectionWindow()
    {
        Title = Tr.T("Штрихкоды и ценники", "Штрих-коддор жана баа белгилери", "Barcodes and price tags", "Barkodlar ve fiyat etiketleri", "Shtrix-kodlar va narx yorliqlari");
        Width = 1000;
        Height = 760;
        this.Bind(BackgroundProperty, this.GetResourceObservable("BrushWindowBackdrop"));
        var card = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, // 2026-11-11: ширину задаёт раздел, а не содержимое
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            BoxShadow = BoxShadows.Parse("0 4 12 0 #0D000000"),
            Child = _host,
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("BrushPanel"));
        Content = new Border { Padding = new Thickness(24, 12, 24, 20), Child = card, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        Closed += (_, _) => _host.Shutdown();
    }

    /// <summary>Название раздела уже в полосе программы владельца, своих кнопок у окна нет — прятать нечего.</summary>
    public void AsOwnerSection()
    {
    }

    /// <summary>2026-11-12 (Л-4): идёт печать/генерация — оболочка не закрывает спрятанный раздел и не считает его «несохранённым» молча.</summary>
    public bool? HasUnsavedChanges => _host.View?.IsBusy == true ? true : null;

    public bool KeepAliveWhenHidden => _host.View?.IsBusy == true;

    /// <summary>Содержимое раздела (печать или подсказка «не куплено / нет права»): для проверок.</summary>
    public BarcodePrintTabHost Host => _host;
}
