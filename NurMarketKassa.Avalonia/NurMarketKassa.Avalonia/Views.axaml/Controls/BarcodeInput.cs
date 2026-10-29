using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Controls;

/// <summary>2026-10-29, требование владельца «в штрихкод сделай только number»: поля ВВОДА штрихкода (карточка товара, «Доп. штрихкод», массовое сканирование,
/// «+ Новый товар» в приёмке, штрихкод в накладной) принимают только цифры. Буквы, пробелы, дефисы и символы отбрасываются при наборе, при скане
/// (сканер в чужой раскладке даёт «реакр» — это не цифры, они отбрасываются) и при вставке Ctrl+V (из вставки остаются цифры). Длина — до 14 знаков
/// (EAN-8, EAN-13, UPC-A, GTIN-14), нужны минимум 4. Цифры не теряются: фильтр пропускает каждую цифру как есть.
/// Поля ПОИСКА («Код или название») этим не ограничиваются — фильтр включается только там, где его подключили.
/// Программная подстановка текста (например, открыли существующий товар со старым нецифровым кодом) не меняется: фильтр работает только на ввод.</summary>
public static class BarcodeInput
{
    public const int MinLength = 4;
    public const int MaxLength = 14;

    /// <summary>Только цифры ASCII (латинские цифры); арабско-индийские и прочие цифры Unicode тоже отбрасываются.</summary>
    public static string DigitsOnly(string? text) => new((text ?? "").Where(char.IsAsciiDigit).ToArray());

    /// <summary>Код годен для нового товара: только цифры, 4–14 знаков.</summary>
    public static bool IsValid(string? code) => !string.IsNullOrEmpty(code) && code.Length is >= MinLength and <= MaxLength && code.All(char.IsAsciiDigit);

    /// <summary>Пустой — подсказки нет; иначе текст, что не так с кодом (на 5 языках).</summary>
    public static string Hint(string? code)
    {
        if (string.IsNullOrEmpty(code) || IsValid(code))
            return "";
        if (!code.All(char.IsAsciiDigit))
            return Tr.T("Штрихкод — только цифры (4–14 знаков).", "Штрих-код — цифралар гана (4–14 белги).", "A barcode is digits only (4–14 characters).",
                "Barkod yalnızca rakamlardan oluşur (4–14 karakter).", "Shtrix-kod faqat raqamlardan iborat (4–14 belgi).");
        return Tr.T("Штрихкод — от 4 до 14 цифр (EAN-8, EAN-13, UPC, GTIN-14).", "Штрих-код — 4төн 14кө чейин цифра (EAN-8, EAN-13, UPC, GTIN-14).",
            "A barcode is 4 to 14 digits (EAN-8, EAN-13, UPC, GTIN-14).", "Barkod 4 ile 14 rakam arasındadır (EAN-8, EAN-13, UPC, GTIN-14).",
            "Shtrix-kod 4 dan 14 gacha raqam (EAN-8, EAN-13, UPC, GTIN-14).");
    }

    /// <summary>Подключить фильтр к <paramref name="root"/> (окно или само поле): ввод и вставка в тех TextBox, для которых <paramref name="isBarcodeBox"/> — true.</summary>
    public static void Attach(Control root, Func<TextBox, bool> isBarcodeBox)
    {
        root.AddHandler(InputElement.TextInputEvent, (_, e) =>
        {
            if (e.Source is not TextBox box || !isBarcodeBox(box))
                return;
            var digits = DigitsOnly(e.Text);
            var room = MaxLength - ((box.Text?.Length ?? 0) - Math.Abs(box.SelectionEnd - box.SelectionStart));
            if (digits.Length > room)
                digits = room > 0 ? digits[..room] : "";
            if (digits.Length == 0)
                e.Handled = true;
            else
                e.Text = digits;
        }, RoutingStrategies.Tunnel);

        root.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (e.Source is not TextBox box || !isBarcodeBox(box))
                return;
            var paste = (e.Key == Key.V && NurMarketKassa.AvaloniaHost.Services.PlatformKeys.HasCommand(e.KeyModifiers)) || (e.Key == Key.Insert && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            if (!paste)
                return;
            e.Handled = true; // сами вставим только цифры
            string? clip = null;
            try
            {
                if (TopLevel.GetTopLevel(box)?.Clipboard is { } clipboard)
                    clip = await clipboard.GetTextAsync().ConfigureAwait(true);
            }
            catch (Exception)
            {
                return;
            }
            var digits = DigitsOnly(clip);
            if (digits.Length == 0)
                return;
            var text = box.Text ?? "";
            var start = Math.Min(box.SelectionStart, box.SelectionEnd);
            var end = Math.Max(box.SelectionStart, box.SelectionEnd);
            var room = MaxLength - (text.Length - (end - start));
            if (room <= 0)
                return;
            if (digits.Length > room)
                digits = digits[..room];
            box.Text = text[..start] + digits + text[end..];
            box.CaretIndex = start + digits.Length;
        }, RoutingStrategies.Tunnel);
    }
}
