using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-27: «QR + PIN» — после скана бейджа, если владелец задал PIN. Поле только для цифр, Enter — подтвердить,
/// Esc — отмена. Окно строится в коде (как другие модалки).</summary>
internal sealed class CashierBadgePinWindow : Window
{
    private readonly TextBox _pin;

    public string? Pin { get; private set; }

    private CashierBadgePinWindow(string? name)
    {
        Title = Tr.T("PIN бейджа", "Бейдждин PIN-коду", "Badge PIN", "Rozet PIN'i", "Beydj PIN-kodi");
        SectionCards.AsModal(this, 420);
        _pin = SectionCards.Field(new TextBox
        {
            PasswordChar = '●',
            MaxLength = CashierBadgeRegistry.PinMaxLength,
            FontSize = 22,
            MinHeight = 46,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Watermark = Tr.T("PIN-код", "PIN-код", "PIN code", "PIN kodu", "PIN-kod"),
        });
        Avalonia.Automation.AutomationProperties.SetName(_pin, Title);
        _pin.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return)
            {
                e.Handled = true;
                Confirm();
            }
        };

        var ok = SectionCards.Primary(Tr.T("Войти", "Кирүү", "Sign in", "Giriş yap", "Kirish"));
        ok.Click += (_, _) => Confirm();
        var cancel = SectionCards.Cancel(Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(false);

        var who = string.IsNullOrWhiteSpace(name)
            ? Tr.T("Введите PIN-код бейджа.", "Бейдждин PIN-кодун киргизиңиз.", "Enter the badge PIN.", "Rozet PIN'ini girin.", "Beydj PIN-kodini kiriting.")
            : Tr.T($"Кассир: {name}. Введите PIN-код бейджа.", $"Кассир: {name}. Бейдждин PIN-кодун киргизиңиз.", $"Cashier: {name}. Enter the badge PIN.",
                $"Kasiyer: {name}. Rozet PIN'ini girin.", $"Kassir: {name}. Beydj PIN-kodini kiriting.");
        Content = SectionCards.Shell(Title!, who, _pin, SectionCards.Buttons(cancel, ok));
        Opened += (_, _) => _pin.Focus();
    }

    private void Confirm()
    {
        var text = (_pin.Text ?? "").Trim();
        if (text.Length == 0)
            return;
        Pin = text;
        Close(true);
    }

    /// <summary>PIN или null (окно закрыто).</summary>
    public static async Task<string?> AskAsync(Window owner, string? name)
    {
        var w = new CashierBadgePinWindow(name);
        var ok = await PosDialogHost.ShowAsync(w, owner);
        return ok == true ? w.Pin : null;
    }
}
