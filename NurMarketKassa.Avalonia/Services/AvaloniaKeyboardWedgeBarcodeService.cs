using Avalonia.Controls;
using Avalonia.Input;
using NurMarketKassa.Interfaces;

namespace NurMarketKassa.AvaloniaHost.Services;

public sealed class AvaloniaKeyboardWedgeBarcodeService : IBarcodeInputService
{
    private const int BarcodeInterkeyMs = 220;
    private const int MinBarcodeLen = 4;
    private const int BarcodeMaxLen = 64;

    private string _barcodeBuf = "";
    private long _barcodeLastTick;

    public event Action<string>? BarcodeScanned;

    public void ProcessKeyDown(KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        if (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Alt) || mods.HasFlag(KeyModifiers.Meta))
            return;

        if (e.Key == Key.Enter)
        {
            if (_barcodeBuf.Length >= MinBarcodeLen)
            {
                e.Handled = true;
                var code = _barcodeBuf;
                _barcodeBuf = "";
                BarcodeScanned?.Invoke(code);
            }
            else
            {
                _barcodeBuf = "";
            }
            return;
        }

        var shift = mods.HasFlag(KeyModifiers.Shift);
        var ch = KeyToBarcodeChar(e.Key, shift);
        if (ch == null) return;

        var now = Environment.TickCount64;
        var delta = now - _barcodeLastTick;
        if (delta < 0 || delta > BarcodeInterkeyMs)
            _barcodeBuf = "";
        _barcodeLastTick = now;

        _barcodeBuf += ch;
        if (_barcodeBuf.Length > BarcodeMaxLen)
            _barcodeBuf = _barcodeBuf[(_barcodeBuf.Length - BarcodeMaxLen)..];
        e.Handled = true;
    }

    private static string? KeyToBarcodeChar(Key key, bool shift)
    {
        if (key is >= Key.D0 and <= Key.D9)
            return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return ((char)('0' + (key - Key.NumPad0))).ToString();
        if (key is >= Key.A and <= Key.Z)
        {
            var c = (char)('a' + (key - Key.A));
            return shift ? char.ToUpperInvariant(c).ToString() : c.ToString();
        }
        if (key == Key.Space) return " ";
        if (key == Key.OemMinus || key == Key.Subtract) return "-";
        if (key == Key.OemPeriod || key == Key.Decimal) return ".";
        return null;
    }
}
