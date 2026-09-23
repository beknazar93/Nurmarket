using Avalonia.Controls;
using Avalonia.Input;
using NurMarketKassa.Interfaces;

namespace NurMarketKassa.AvaloniaHost.Services;

public sealed class AvaloniaKeyboardWedgeBarcodeService : IBarcodeInputService
{
    private const int BarcodeInterkeyMs = 220;
    private const int MinBarcodeLen = 4;
    private const int BarcodeMaxLen = 64;

    // 2026-09-16, живой баг ("инпут не работает нигде в модалке") — эта служба ловит KeyDown
    // ГЛОБАЛЬНО по всему окну (см. Window_KeyDown в ProductEditDialog/WarehouseWindow/MainWindow),
    // чтобы сканер штрихкода срабатывал, даже если фокус не в поле "Штрихкод". Раньше КАЖДАЯ
    // подходящая по символу клавиша (цифра/буква/пробел/минус/точка) сразу помечалась
    // e.Handled=true — что перехватывало и ЛЮБОЙ обычный набор текста человеком (название
    // товара, количество и т.д.) в ЛЮБОМ другом поле того же окна: символ никогда не доходил
    // до сфокусированного TextBox. Раньше буфер только СБРАСЫВАЛСЯ при медленном вводе, но
    // e.Handled всё равно ставился безусловно. Теперь перехватываем клавишу только начиная
    // с 3-го подряд символа, пришедшего БЫСТРО (délta <= BarcodeInterkeyMs) — сканер штрихкода
    // печатает десятки символов за миллисекунды и легко проходит этот порог, а обычный,
    // пусть даже быстрый, набор человеком почти никогда не выдерживает 2 подряд интервала
    // короче 220мс. Первые 1-2 символа настоящего скана всё равно не потеряются: они попадают
    // в буфер (и значит в итоговый код при Enter), просто не блокируются от параллельного
    // попадания в сфокусированное поле — маленький визуальный побочный эффект вместо полной
    // поломки печати.
    private const int MinFastRunToIntercept = 2;

    private string _barcodeBuf = "";
    private long _barcodeLastTick;
    private int _fastRunLength;

    public event Action<string>? BarcodeScanned;

    public bool HasBufferedInput => _barcodeBuf.Length > 0;

    public void ProcessKeyDown(KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        if (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Alt) || mods.HasFlag(KeyModifiers.Meta))
            return;

        if (e.Key == Key.Enter)
        {
            if (_barcodeBuf.Length >= MinBarcodeLen && _fastRunLength >= MinFastRunToIntercept)
            {
                e.Handled = true;
                var code = _barcodeBuf;
                _barcodeBuf = "";
                _fastRunLength = 0;
                BarcodeScanned?.Invoke(code);
            }
            else
            {
                _barcodeBuf = "";
                _fastRunLength = 0;
            }
            return;
        }

        var shift = mods.HasFlag(KeyModifiers.Shift);
        var ch = KeyToBarcodeChar(e.Key, shift);
        if (ch == null) return;

        var now = Environment.TickCount64;
        var delta = now - _barcodeLastTick;
        var isFastFollowUp = delta is >= 0 and <= BarcodeInterkeyMs && _barcodeBuf.Length > 0;
        if (isFastFollowUp)
            _fastRunLength++;
        else
        {
            _barcodeBuf = "";
            _fastRunLength = 0;
        }
        _barcodeLastTick = now;

        _barcodeBuf += ch;
        if (_barcodeBuf.Length > BarcodeMaxLen)
            _barcodeBuf = _barcodeBuf[(_barcodeBuf.Length - BarcodeMaxLen)..];

        if (_fastRunLength >= MinFastRunToIntercept)
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
