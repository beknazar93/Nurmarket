using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.ViewModels;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.ViewModels;

public sealed class FrmKeyboardViewModel : INotifyPropertyChanged
{
    public sealed class KeyButton
    {
        public KeyButton(int code) => Key = code;

        public int Key { get; }

        public string Value =>
            FrmKeyboard.IsShift
                ? User32Interop.ToAscii(Key, withShift: true)
                : User32Interop.ToAscii(Key);
    }

    public static class User32Interop
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ToUnicode(
            uint virtualKeyCode,
            uint scanCode,
            byte[] keyboardState,
            StringBuilder receivingBuffer,
            int bufferSize,
            uint flags);

        public static string GetCharsFromKeys(uint virtualKeyCode, bool shift)
        {
            var buf = new StringBuilder(256);
            var keyboardState = new byte[256];
            if (shift)
                keyboardState[0x10] = 0xFF;

            ToUnicode(virtualKeyCode, 0, keyboardState, buf, 256, 0);
            return buf.ToString();
        }

        public static string ToAscii(int keyCode, bool withShift = false)
        {
            var isShift = withShift || FrmKeyboard.IsShift;
            var s = GetCharsFromKeys((uint)keyCode, isShift);

            // CapsLock via Win32 GetKeyState
            if ((GetKeyState(0x14) & 1) != 0)
                s = isShift ? s.ToLowerInvariant() : s.ToUpperInvariant();

            return s;
        }

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);
    }

    private const int DefaultButtonSize = 50;

    private static int _buttonBaseSize = DefaultButtonSize;
    private Visibility _lettersVisibility = Visibility.Visible;

    public System.Timers.Timer? Timer;

    public int BaseSize => _buttonBaseSize;
    public int Button050Size => (int)(BaseSize * 0.5);
    public int Button150Size => (int)(BaseSize * 1.4);
    public int Button170Size => (int)(Button150Size * 1.24 + ButtonsMargin);
    public int Button200Size => (int)(BaseSize * 2.0) + 2 * ButtonsMargin;
    public int Button220Size => (int)(Button150Size * 1.46 + 4 * ButtonsMargin);
    public int Button300Size => (int)(BaseSize * 3.0) + 4 * ButtonsMargin;
    public int ButtonSpaceSize => (int)(BaseSize * 8.5 + 16 * ButtonsMargin);
    public int ButtonsMargin => (int)(BaseSize / 20.0) + 1;

    public Visibility LettersVisibility
    {
        get => _lettersVisibility;
        set
        {
            if (_lettersVisibility == value)
                return;

            UpdateFormPosition();
            _lettersVisibility = value;
            NumEnterVisibility = _lettersVisibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            OnPropertyChanged(nameof(NumEnterVisibility));
            OnPropertyChanged();
        }
    }

    public Visibility NumEnterVisibility { get; private set; } = Visibility.Collapsed;

    public ICommand HideShowLettersCommand => new RelayCommand(() =>
    {
        LettersVisibility = LettersVisibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    });

    public Visibility ShiftBorderVisibility { get; set; }
    public Visibility CapsIndicatorVisibility { get; set; }

    public KeyButton Button_TAB => new(9);
    public KeyButton Q_button => new(81);
    public KeyButton W_button => new(87);
    public KeyButton E_button => new(69);
    public KeyButton R_button => new(82);
    public KeyButton T_button => new(84);
    public KeyButton Y_button => new(89);
    public KeyButton U_button => new(85);
    public KeyButton I_button => new(73);
    public KeyButton O_button => new(79);
    public KeyButton P_button => new(80);
    public KeyButton Button_219 => new(219);
    public KeyButton Button_220 => new(220);
    public KeyButton Button_221 => new(221);
    public KeyButton Button_CAPS => new(20);
    public KeyButton A_button => new(65);
    public KeyButton S_button => new(83);
    public KeyButton D_button => new(68);
    public KeyButton F_button => new(70);
    public KeyButton G_button => new(71);
    public KeyButton H_button => new(72);
    public KeyButton J_button => new(74);
    public KeyButton K_button => new(75);
    public KeyButton L_button => new(76);
    public KeyButton Button_186 => new(186);
    public KeyButton Button_222 => new(222);
    public KeyButton Button_ENTER => new(13);
    public KeyButton Button_LSHIFT => new(160);
    public KeyButton Z_button => new(90);
    public KeyButton X_button => new(88);
    public KeyButton C_button => new(67);
    public KeyButton V_button => new(86);
    public KeyButton B_button => new(66);
    public KeyButton N_button => new(78);
    public KeyButton M_button => new(77);
    public KeyButton Button_188 => new(188);
    public KeyButton Button_190 => new(190);
    public KeyButton Button_191 => new(191);
    public KeyButton Button_RSHIFT => new(161);
    public KeyButton Button_SPACE => new(32);
    public KeyButton Button_D_DOT => new(110);
    public KeyButton Button_D0 => new(96);
    public KeyButton Button_D1 => new(97);
    public KeyButton Button_D2 => new(98);
    public KeyButton Button_D3 => new(99);
    public KeyButton Button_D4 => new(100);
    public KeyButton Button_D5 => new(101);
    public KeyButton Button_D6 => new(102);
    public KeyButton Button_D7 => new(103);
    public KeyButton Button_D8 => new(104);
    public KeyButton Button_D9 => new(105);
    public KeyButton Button_BackSpace => new(8);
    public KeyButton Button_Delete => new(46);

    public string CurrnetLang =>
        CultureInfo.CurrentCulture.ThreeLetterISOLanguageName.ToUpperInvariant();

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateButtons()
    {
        _buttonBaseSize = DefaultButtonSize;
        RaiseSizePropertiesChanged();
        RaiseKeyPropertiesChanged();
    }

    private void UpdateFormPosition()
    {
        try
        {
            if (FrmKeyboard.CurrentForm is not Window curF)
                return;

            var coeff = LettersVisibility == Visibility.Visible ? 1 : -1;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var pos = curF.Position;
                curF.Position = new Avalonia.PixelPoint(pos.X + (int)(8 * BaseSize * coeff), pos.Y);
            });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"On-screen keyboard placement failed: {ex.GetType().Name}", "WARNING");
        }
    }

    private void RaiseSizePropertiesChanged()
    {
        OnPropertyChanged(nameof(BaseSize));
        OnPropertyChanged(nameof(Button050Size));
        OnPropertyChanged(nameof(Button150Size));
        OnPropertyChanged(nameof(Button170Size));
        OnPropertyChanged(nameof(Button200Size));
        OnPropertyChanged(nameof(Button220Size));
        OnPropertyChanged(nameof(Button300Size));
        OnPropertyChanged(nameof(ButtonSpaceSize));
        OnPropertyChanged(nameof(ButtonsMargin));
    }

    private void RaiseKeyPropertiesChanged()
    {
        OnPropertyChanged(nameof(Button_TAB));
        OnPropertyChanged(nameof(Q_button));
        OnPropertyChanged(nameof(W_button));
        OnPropertyChanged(nameof(E_button));
        OnPropertyChanged(nameof(R_button));
        OnPropertyChanged(nameof(T_button));
        OnPropertyChanged(nameof(Y_button));
        OnPropertyChanged(nameof(U_button));
        OnPropertyChanged(nameof(I_button));
        OnPropertyChanged(nameof(O_button));
        OnPropertyChanged(nameof(P_button));
        OnPropertyChanged(nameof(Button_219));
        OnPropertyChanged(nameof(Button_220));
        OnPropertyChanged(nameof(Button_221));
        OnPropertyChanged(nameof(Button_CAPS));
        OnPropertyChanged(nameof(A_button));
        OnPropertyChanged(nameof(S_button));
        OnPropertyChanged(nameof(D_button));
        OnPropertyChanged(nameof(F_button));
        OnPropertyChanged(nameof(G_button));
        OnPropertyChanged(nameof(H_button));
        OnPropertyChanged(nameof(J_button));
        OnPropertyChanged(nameof(K_button));
        OnPropertyChanged(nameof(L_button));
        OnPropertyChanged(nameof(Button_186));
        OnPropertyChanged(nameof(Button_222));
        OnPropertyChanged(nameof(Button_ENTER));
        OnPropertyChanged(nameof(Button_LSHIFT));
        OnPropertyChanged(nameof(Z_button));
        OnPropertyChanged(nameof(X_button));
        OnPropertyChanged(nameof(C_button));
        OnPropertyChanged(nameof(V_button));
        OnPropertyChanged(nameof(B_button));
        OnPropertyChanged(nameof(N_button));
        OnPropertyChanged(nameof(M_button));
        OnPropertyChanged(nameof(Button_188));
        OnPropertyChanged(nameof(Button_190));
        OnPropertyChanged(nameof(Button_191));
        OnPropertyChanged(nameof(Button_RSHIFT));
        OnPropertyChanged(nameof(Button_SPACE));
        OnPropertyChanged(nameof(Button_D_DOT));
        OnPropertyChanged(nameof(Button_D0));
        OnPropertyChanged(nameof(Button_D1));
        OnPropertyChanged(nameof(Button_D2));
        OnPropertyChanged(nameof(Button_D3));
        OnPropertyChanged(nameof(Button_D4));
        OnPropertyChanged(nameof(Button_D5));
        OnPropertyChanged(nameof(Button_D6));
        OnPropertyChanged(nameof(Button_D7));
        OnPropertyChanged(nameof(Button_D8));
        OnPropertyChanged(nameof(Button_D9));
        OnPropertyChanged(nameof(Button_BackSpace));
        OnPropertyChanged(nameof(Button_Delete));
        OnPropertyChanged(nameof(CurrnetLang));
    }

    public void RefreshIndicators(bool isShift, bool capsLock)
    {
        ShiftBorderVisibility = isShift ? Visibility.Visible : Visibility.Collapsed;
        CapsIndicatorVisibility = capsLock ? Visibility.Visible : Visibility.Collapsed;
        OnPropertyChanged(nameof(ShiftBorderVisibility));
        OnPropertyChanged(nameof(CapsIndicatorVisibility));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
