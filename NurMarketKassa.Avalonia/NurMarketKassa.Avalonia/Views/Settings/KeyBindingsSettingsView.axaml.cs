using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>
/// Настройки → «Клавиши» (2026-09-27, просьба владельца: «добавь в настройки назначение клавиш с
/// значениями по умолчанию, например оплата на Enter, чтобы можно было переназначить»). Тот же
/// список действий и те же правила, что в окне «Горячие» на верхней панели кассы
/// (PosHotkeyService), просто постоянной страницей настроек. Только в кассе: в программе
/// владельца клавиш кассира нет.
/// </summary>
public partial class KeyBindingsSettingsView : UserControl
{
    private readonly PosHotkeyService _hotkeys = new();
    private ObservableCollection<HotkeyRow> _rows = new();

    public KeyBindingsSettingsView()
    {
        InitializeComponent();
        // Страница может открываться много раз, а клавиши — меняться в окне «Горячие»: читаем
        // сохранённое при каждом показе.
        AttachedToVisualTree += (_, _) => LoadRows();
        // Tunnel: поле ввода само забирает Delete/Backspace, Ctrl+A и т.п. раньше обычного
        // обработчика — перехватываем нажатие до него.
        AddHandler(KeyDownEvent, OnGestureKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void LoadRows()
    {
        _rows = new ObservableCollection<HotkeyRow>(PosHotkeyService.Definitions.Select(definition =>
            new HotkeyRow(definition.Action, definition.Title, definition.Description, _hotkeys.GetGesture(definition.Action))));
        RowsList.ItemsSource = _rows;
        ShowError(null);
        SavedText.IsVisible = false;
    }

    private void OnGestureKeyDown(object? sender, KeyEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox { Tag: PosHotkeyAction action })
            return;
        // Tab — к следующему полю, Delete/Backspace — снять назначение.
        if (!PosHotkeyService.TryCapture(e, out var gesture, out var error))
            return;

        e.Handled = true;
        SavedText.IsVisible = false;
        if (error is not null)
        {
            ShowError(error);
            return;
        }

        _rows.First(x => x.Action == action).Gesture = gesture ?? "";
        ShowError(null);
    }

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var definition in PosHotkeyService.Definitions)
            _rows.First(x => x.Action == definition.Action).Gesture = definition.DefaultGesture;
        ShowError(null);
        SavedText.IsVisible = false;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var values = _rows.ToDictionary(x => x.Action, x => x.Gesture);
        if (!_hotkeys.Save(values, out var error))
        {
            ShowError(error ?? Tr.T("Не удалось сохранить клавиши.", "Баскычтарды сактоо мүмкүн болгон жок.",
                "Could not save the keys.", "Tuşlar kaydedilemedi.", "Tugmalarni saqlab bo'lmadi."));
            return;
        }

        ShowError(null);
        SavedText.Text = Tr.T("Сохранено. Новые клавиши уже работают на кассе.", "Сакталды. Жаңы баскычтар кассада иштеп жатат.",
            "Saved. The new keys already work on the till.", "Kaydedildi. Yeni tuşlar kasada çalışıyor.",
            "Saqlandi. Yangi tugmalar kassada allaqachon ishlayapti.");
        SavedText.IsVisible = true;
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.IsVisible = !string.IsNullOrEmpty(message);
    }
}
