using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Layout;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Режимы единой вкладки «Ценники и штрихкоды».</summary>
public enum LabelsHubMode { Print, Editor, Templates }

/// <summary>2026-11-21, владелец: «собери в складе все редакторы в одну вкладку — слишком во многих местах». Содержимое единственной
/// вкладки «Ценники и штрихкоды» Склада: режимы «Печать» (выбор товаров, ценники и штрихкоды, генерация, A4), «Редактор» (визуальный
/// редактор этикетки/ценника) и «Шаблоны». Заглушка: режимы «Редактор» и «Шаблоны» подключаются в LabelsHubView.Modes.cs.</summary>
public sealed partial class LabelsHubView : UserControl
{
    private readonly BarcodePrintTabHost _printHost = new();

    public LabelsHubView()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        _printHost.Unlocked += () => Unlocked?.Invoke();
        Content = _printHost;
        InitModes();
    }

    /// <summary>Печать куплена или активирована серийным номером.</summary>
    public event Action? Unlocked;

    /// <summary>Режим «Печать» (выбор товаров, ценники, штрихкоды).</summary>
    public BarcodePrintTabHost PrintHost => _printHost;

    /// <summary>Текущий режим.</summary>
    public LabelsHubMode Mode { get; private set; } = LabelsHubMode.Print;

    /// <summary>Показать режим. Вызывается при каждом показе вкладки (<see cref="Refresh"/>).</summary>
    public void ShowMode(LabelsHubMode mode)
    {
        if (Mode == LabelsHubMode.Print && mode != LabelsHubMode.Print)
            _printHost.View?.SaveState(); // правки размера/полей в «Печать» — на диск, чтобы редактор открыл тот же шаблон
        Mode = mode;
        ApplyMode();
    }

    /// <summary>Отмеченные товары — в «Печать»: отмечены и показаны первыми. false — нет права «Печать штрих-кодов» или не куплено.</summary>
    public bool ApplySelection(IReadOnlyList<string> productIds, Window? owner)
    {
        var ok = _printHost.ApplySelection(productIds, owner);
        if (ok)
            ShowMode(LabelsHubMode.Print);
        return ok;
    }

    /// <summary>Обновить при показе вкладки.</summary>
    public void Refresh()
    {
        _printHost.Refresh();
        ApplyMode();
    }

    /// <summary>Закрытие окна/раздела: остановить таймеры печати.</summary>
    public void Shutdown() => _printHost.Shutdown();

    partial void InitModes();

    partial void ApplyMode();
}
