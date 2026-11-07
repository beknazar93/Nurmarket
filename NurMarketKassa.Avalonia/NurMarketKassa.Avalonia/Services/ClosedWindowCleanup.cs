using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-07 (память владельца, Б-J1): закрытое окно раздела могло оставаться в памяти, потому что Avalonia при Window.Close() НЕ отсоединяет содержимое от дерева:
/// бесконечная анимация (индикатор загрузки ProgressBar «IsIndeterminate» в шаблоне) продолжает висеть на общих часах отрисовки и тянет за собой весь раздел
/// (ProgressBar → шаблон → родитель → окно), подписки «при отсоединении от дерева» (OnDetachedFromVisualTree у выбора поставщика и др.) тоже не срабатывают.
/// Цепочку нашёл тест ClosedSectionLeakTests (обход кучи от статических полей). Здесь — после закрытия раздел явно отсоединяет своё содержимое: сначала снимаются
/// индикаторы загрузки, затем Content = null (всё дерево получает DetachedFromVisualTree, анимации и подписки отпускаются). Вызывается в последнюю очередь в Closed
/// оболочки владельца; ошибка отсоединения не страшна — окно уже закрыто.</summary>
public static class ClosedWindowCleanup
{
    public static void Release(Window window)
    {
        try
        {
            foreach (var bar in window.GetVisualDescendants().OfType<ProgressBar>())
            {
                if (bar.IsIndeterminate)
                    bar.IsIndeterminate = false;
            }

            window.Content = null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно раздела после закрытия не отсоединено ({ex.Message}).", "WARNING");
        }
    }
}
