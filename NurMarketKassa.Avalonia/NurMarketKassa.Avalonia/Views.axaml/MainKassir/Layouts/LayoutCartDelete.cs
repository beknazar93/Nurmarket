using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir.Layouts;

/// <summary>2026-11-06: В6 Д4-04 — удаление строки чека в раскладках «Карточки», «Минимал», «Таблица», «Профи» и «1С»
/// двумя нажатиями, как в основном виде (BasketPanelView.CartDelete_Click): первое нажатие превращает кнопку в красное «Удалить?»
/// на 3 с, второе удаляет (коды доступа, причина и журнал — как раньше, в RemoveCommand). Промах пальцем или мышью по соседней
/// кнопке больше не убирает товар. «Взвод» держим по строке чека (ссылка + время), а не по кнопке: кнопки строк переиспользуются.</summary>
internal static class LayoutCartDelete
{
    private static readonly TimeSpan ArmWindow = TimeSpan.FromSeconds(3);

    private static CartLineItemVm? _armedLine;
    private static DateTime _armedUtc;
    private static Button? _armedButton;
    private static object? _originalContent;
    private static double _originalWidth;
    private static double _originalMinWidth;

    /// <summary>Нужно ли второе нажатие считать подтверждением (та же строка, не позже 3 с).</summary>
    internal static bool IsConfirmed(object? armedLine, DateTime armedUtc, object? line, DateTime nowUtc) =>
        armedLine is not null && ReferenceEquals(armedLine, line) && nowUtc - armedUtc < ArmWindow;

    private static void Disarm()
    {
        if (_armedButton is not null)
        {
            _armedButton.Content = _originalContent;
            _armedButton.Width = _originalWidth;
            _armedButton.MinWidth = _originalMinWidth;
        }
        _armedLine = null;
        _armedButton = null;
        _originalContent = null;
    }

    /// <summary>Обработчик Click кнопки «удалить» в строке чека (DataContext кнопки — CartLineItemVm).</summary>
    internal static void OnClick(object? sender)
    {
        if (sender is not Button { DataContext: CartLineItemVm line } btn)
            return;
        if (!IsConfirmed(_armedLine, _armedUtc, line, DateTime.UtcNow))
        {
            Disarm();
            _armedLine = line;
            _armedUtc = DateTime.UtcNow;
            _armedButton = btn;
            _originalContent = btn.Content;
            _originalWidth = btn.Width;
            _originalMinWidth = btn.MinWidth;
            var minWidth = double.IsNaN(btn.Width) ? btn.MinWidth : Math.Max(btn.Width, btn.MinWidth);
            // Кнопка с фиксированной шириной значка растёт под слово «Удалить?»; красная «плашка» — внутри кнопки, чтобы не зависеть от стилей раскладки.
            btn.Content = new Border
            {
                Background = ResolveBrush(btn, "BrushDanger", Brushes.Firebrick),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 0),
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = new TextBlock
                {
                    Text = Tr.T("Удалить?", "Өчүрөбү?", "Delete?", "Silinsin mi?", "O'chirilsinmi?"),
                    Foreground = Brushes.White,
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            };
            btn.Width = double.NaN;
            btn.MinWidth = minWidth;
            DispatcherTimer.RunOnce(() =>
            {
                if (ReferenceEquals(_armedLine, line))
                    Disarm();
            }, ArmWindow);
            return;
        }
        Disarm();
        if (line.RemoveCommand?.CanExecute(line) == true)
            line.RemoveCommand.Execute(line);
    }

    private static IBrush ResolveBrush(Control host, string key, IBrush fallback) =>
        host.TryFindResource(key, host.ActualThemeVariant, out var res) && res is IBrush b ? b : fallback;
}
