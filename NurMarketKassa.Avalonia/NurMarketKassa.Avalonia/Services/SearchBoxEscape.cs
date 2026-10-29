using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-29 (аудит всех полей поиска): Esc в поле поиска сначала очищает текст, и только пустое поле отдаёт Esc окну
/// (закрыть/назад), как было. «Поле поиска» — любое поле с классом clearButton или с «Search» в имени (ProductSearchBox,
/// EmployeeSearchBox, MovementsSearchBox…). Один обработчик на всё приложение вместо правки каждого окна.</summary>
internal static class SearchBoxEscape
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        InputElement.KeyDownEvent.AddClassHandler<TextBox>(OnKeyDown, RoutingStrategies.Tunnel);
        // Длина запроса ограничена (120): вставка абзаца из мессенджера в поиск не должна подвешивать окно.
        Control.LoadedEvent.AddClassHandler<TextBox>((box, _) =>
        {
            if (box.MaxLength == 0 && IsSearchLike(box))
                box.MaxLength = 120;
        }, RoutingStrategies.Direct);
    }

    private static bool IsSearchLike(TextBox box) =>
        box.PasswordChar == default && (box.Classes.Contains("clearButton")
            || (box.Name?.Contains("Search", StringComparison.OrdinalIgnoreCase) ?? false)
            || (box.Name?.Contains("BarcodeInput", StringComparison.OrdinalIgnoreCase) ?? false)); // поле «Код или название» в чеке

    private static void OnKeyDown(TextBox box, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || box.IsReadOnly || string.IsNullOrEmpty(box.Text))
            return;
        if (!IsSearchLike(box))
            return;
        box.Text = "";
        e.Handled = true;
    }
}
