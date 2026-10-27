using System;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;

namespace NurMarketKassa.AvaloniaHost.Views.Controls;

/// <summary>2026-10-27 (приёмка Opus 26.10, Д-4, безопасность): у обычного TextBox UI Automation отдаёт Text открытым
/// текстом, даже если поле скрыто точками (PasswordChar) — любая программа на ПК читала ключ Gemini/токен бота через
/// ValuePattern. Этот TextBox внешне такой же (стиль и шаблон TextBox), но пока PasswordChar задан, UIA получает только
/// маску из точек; когда поле открыто («Показать», PasswordChar = '\0') — значение как обычно. Ввод и вставка не затронуты.</summary>
public class SecretTextBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override AutomationPeer OnCreateAutomationPeer() => new SecretTextBoxAutomationPeer(this);

    /// <summary>Скрыто ли значение от UI Automation сейчас.</summary>
    internal bool IsMasked => PasswordChar != '\0';
}

public sealed class SecretTextBoxAutomationPeer : TextBoxAutomationPeer, IValueProvider
{
    public SecretTextBoxAutomationPeer(SecretTextBox owner) : base(owner) { }

    string? IValueProvider.Value
    {
        get
        {
            var box = (SecretTextBox)Owner;
            if (!box.IsMasked)
                return box.Text;
            var len = box.Text?.Length ?? 0;
            return len == 0 ? "" : new string(box.PasswordChar, len);
        }
    }
}
