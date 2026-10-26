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
    private ulong? _barcodeLastRawTs;
    private int _fastRunLength;

    public event Action<string>? BarcodeScanned;

    public bool HasBufferedInput => _barcodeBuf.Length > 0;

    /// <summary>2026-10-20: в буфере готовый код сканера — следующий Enter его завершит (см. MainWindow.OnScanEnterTunnel).</summary>
    public bool HasCompleteScan => _barcodeBuf.Length >= MinBarcodeLen && _fastRunLength >= MinFastRunToIntercept;

    /// <summary>2026-10-20 (П1-0): готовый код сканера, последняя клавиша которого пришла только что (до 300 мс) —
    /// окно оплаты по такому Enter не платит (CheckoutDialog).</summary>
    public bool HasFreshCompleteScan => HasCompleteScan && Environment.TickCount64 - _barcodeLastTick is >= 0 and < 300;

    /// <summary>2026-10-20 (П1-0): сканер только что (до 300 мс) отдал код — Enter сразу за ним (у многих сканеров
    /// конец кода — два Enter, CR+LF) не нажимает «Оплату» и не платит в окне оплаты.</summary>
    public bool ScanJustCompleted =>
        _lastSource == ScanSource.Keyboard && _lastCode != null && Environment.TickCount64 - _lastCodeTick is >= 0 and < 300;

    /// <summary>2026-10-20 (П1-0): забыть набранный сканером код, никуда его не передавая.</summary>
    public void DiscardBuffer()
    {
        _barcodeBuf = "";
        _fastRunLength = 0;
    }

    // 2026-10-20: метка времени нажатия. KeyEventArgs в Avalonia 11 её не несёт, а «сырые» события ввода в
    // сборках Avalonia закрыты. В Windows KeyDown обрабатывается прямо внутри сообщения WM_KEYDOWN, и
    // GetMessageTime() — время этого сообщения, т.е. когда клавиша нажата (так же время берёт сама Avalonia).
    // На Linux/Android метки нет — пауза меряется как раньше.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetMessageTime();

    internal static ulong? KeyPressTimestamp()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            // 0 — сообщения нет (поток без очереди сообщений, напр. проверка без окна): меряем как раньше.
            var time = unchecked((uint)GetMessageTime());
            return time == 0 ? null : time;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>2026-10-04, владелец: «доступ к камере в Android, чтобы её как сканер можно было использовать
    /// в кассе, при создании товаров, приёмке». Код, считанный камерой (CameraScan), — как скан
    /// USB-сканера: те же обработчики в кассе, карточке товара и на складе.</summary>
    public void Inject(string code) => InjectFrom(code, ScanSource.Other);

    private void InjectFrom(string code, ScanSource source)
    {
        var trimmed = code?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return;
        _barcodeBuf = "";
        _fastRunLength = 0;
        Remember(trimmed, source);
        BarcodeScanned?.Invoke(trimmed);
    }

    /// <summary>2026-10-05, владелец: «изучи Android POS-кассы, сканер… чтобы программа работала 100%». Встроенный
    /// сканер POS-терминала (Sunmi, iMin, Urovo, Newland, iData…) отдаёт код рассылкой Android (AndroidHardwareScanner).
    /// Многие при этом ещё и «печатают» тот же код как клавиатура — без проверки товар добавлялся бы дважды.
    /// Тот же код другим путём в пределах <see cref="DuplicateWindowMs"/> второй раз не принимается.</summary>
    public void InjectDeviceScan(string code)
    {
        var trimmed = code?.Trim();
        if (string.IsNullOrEmpty(trimmed) || IsEcho(trimmed, ScanSource.Device))
            return;
        InjectFrom(trimmed, ScanSource.Device);
    }

    private const int DuplicateWindowMs = 800;
    private string? _lastCode;
    private long _lastCodeTick;
    private ScanSource _lastSource;

    /// <summary>Откуда пришёл скан: клавиатурный ввод сканера, рассылка встроенного сканера терминала, камера и др.</summary>
    private enum ScanSource { Keyboard, Device, Other }

    private void Remember(string code, ScanSource source)
    {
        _lastCode = code;
        _lastCodeTick = Environment.TickCount64;
        _lastSource = source;
    }

    /// <summary>2026-10-05, проверка перед выпуском 1.17.50: «эхо» — тот же код за 0,8 с, но ДРУГИМ путём (терминал
    /// отдал его и рассылкой, и как клавиатура). Раньше отсекался любой повтор — и на ПК два одинаковых товара,
    /// отсканированных USB-сканером быстрее 0,8 с, засчитывались как один. Повтор тем же путём — всегда новый скан.</summary>
    private bool IsEcho(string code, ScanSource source) =>
        source != ScanSource.Other
        && _lastSource != ScanSource.Other
        && _lastSource != source
        && string.Equals(_lastCode, code, StringComparison.Ordinal)
        && Environment.TickCount64 - _lastCodeTick < DuplicateWindowMs;

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
                // 2026-10-05: тот же код только что пришёл рассылкой встроенного сканера — второй раз не добавляем.
                if (IsEcho(code, ScanSource.Keyboard))
                    return;
                Remember(code, ScanSource.Keyboard);
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

        // 2026-10-20, живая проверка (200 сканов подряд: принято 59, у части кодов отрезано начало —
        // «7644469162» вместо «7807644469162»): паузу мерили по времени ОБРАБОТКИ клавиши. Пока касса
        // добавляла предыдущий товар (большой чек — до секунды), клавиши следующего кода ждали в очереди,
        // и «пауза» между двумя цифрами одного кода выходила больше 220 мс — буфер сбрасывался посреди кода.
        // Теперь пауза — по времени НАЖАТИЯ (метка самого события клавиатуры от системы); если метки нет —
        // как раньше, по часам при обработке.
        var now = Environment.TickCount64;
        var raw = KeyPressTimestamp();
        long delta;
        if (raw is { } rawTs && _barcodeLastRawTs is { } lastRawTs)
            delta = rawTs >= lastRawTs ? (long)Math.Min(rawTs - lastRawTs, long.MaxValue) : -1;
        else
            delta = now - _barcodeLastTick;
        var isFastFollowUp = delta is >= 0 and <= BarcodeInterkeyMs && _barcodeBuf.Length > 0;
        if (isFastFollowUp)
            _fastRunLength++;
        else
        {
            _barcodeBuf = "";
            _fastRunLength = 0;
        }
        _barcodeLastTick = now;
        _barcodeLastRawTs = raw;

        _barcodeBuf += ch;
        if (_barcodeBuf.Length > BarcodeMaxLen)
            _barcodeBuf = _barcodeBuf[(_barcodeBuf.Length - BarcodeMaxLen)..];

        // 2026-10-24, владелец: «в полях (наименование, штрихкод, цена) вводится только 2 символа и перестаёт».
        // Обоснование про «220 мс» выше неверно: обычная печать — 80–200 мс на символ, серия набиралась и у человека, а
        // обработанный KeyDown в Windows не даёт символу дойти до поля. В правимом поле символы не глушим — скан
        // там распознают окна по Enter после строгой серии (ProductEditDialog, склад, поиск кассы); вне полей — как раньше.
        if (_fastRunLength >= MinFastRunToIntercept && e.Source is not TextBox { IsReadOnly: false })
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
