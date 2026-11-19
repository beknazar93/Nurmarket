using Avalonia.Controls;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-19 (список владельца, п. 7: «Старая загрузка» плодила окна — каждое новое открывалось под мастером, кнопку нажимали снова и снова):
/// единое правило «одно окно — один экземпляр» для немодальных окон и разделов. Ключ — тип окна (или своя строка). Повторное открытие
/// не создаёт второе окно, а выводит уже открытое на передний план (свёрнутое разворачивается). Закрытое окно из учёта убирается само.
/// Модальные диалоги от двойного нажатия защищает <see cref="PosDialogHost"/> (повтор того же типа в течение 0,7 с).</summary>
public static class WindowSingleton
{
    private static readonly Dictionary<string, Window> Open = new();

    /// <summary>true — окно с таким ключом уже открыто: оно выведено вперёд, новое создавать не нужно.</summary>
    public static bool TryActivate(string key)
    {
        if (!Open.TryGetValue(key, out var existing))
            return false;
        if (!existing.IsVisible)
        {
            Open.Remove(key);
            return false;
        }
        try
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
        }
        catch
        {
            // вывод вперёд — удобство; окно всё равно одно
        }
        return true;
    }

    /// <summary>Запомнить открытое окно; при закрытии запись снимается.</summary>
    public static void Register(string key, Window window)
    {
        Open[key] = window;
        window.Closed += (_, _) =>
        {
            if (Open.TryGetValue(key, out var current) && ReferenceEquals(current, window))
                Open.Remove(key);
        };
    }

    /// <summary>Показать окно (немодально, поверх владельца) или вывести вперёд уже открытое такого же типа. Возвращает окно, которое теперь на экране.</summary>
    public static T ShowOnce<T>(Window? owner, Func<T> create) where T : Window
    {
        var key = KeyOf<T>();
        if (Open.TryGetValue(key, out var existing) && existing is T same && existing.IsVisible)
        {
            TryActivate(key);
            return same;
        }
        var window = create();
        if (owner != null)
            window.Show(owner);
        else
            window.Show();
        Register(key, window);
        return window;
    }

    /// <summary>Ключ по типу окна.</summary>
    public static string KeyOf<T>() where T : Window => typeof(T).FullName ?? typeof(T).Name;

    /// <summary>Открытое окно по ключу (для проверок).</summary>
    public static Window? Find(string key) => Open.TryGetValue(key, out var w) ? w : null;

    /// <summary>Только для тестов (2026-11-19): забыть учтённые окна — окно прошлого сценария, оставшееся «открытым», не должно подменять новое.</summary>
    public static void ResetForTests() => Open.Clear();

    /// <summary>Сколько окон учтено сейчас (для проверок).</summary>
    public static int Count => Open.Count;
}
