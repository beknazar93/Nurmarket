using System.Text;
using Android.Content;
using NurMarketKassa.Services;

namespace NurMarketKassa.Droid;

/// <summary>2026-10-05, владелец: «изучи параметры Android POS-касс — сканер, чековый принтер — сделай, чтобы наша
/// программа 100% работала на этих устройствах». Встроенный сканер POS-терминала (ручные Sunmi V2/V2s/V3, iMin
/// Swift/Falcon, Urovo, Newland, iData, Senraise, Chainway, Seuic и их китайские аналоги) отдаёт считанный код одним
/// из двух способов: «печатает» его как клавиатура (это касса уже понимает — AvaloniaKeyboardWedgeBarcodeService)
/// или присылает рассылкой Android (broadcast). Здесь — приём рассылок всех известных марок: код уходит в кассу
/// как скан USB-сканера, тот же код с клавиатуры следом второй раз не добавляется (InjectDeviceScan).
/// Действия и поля — из документации производителей (Sunmi Scanner User Guide, iMin Scanner Integration,
/// Urovo ScanManager, Newland PDA API Handbook, iData) и распространённых китайских прошивок.</summary>
internal static class AndroidHardwareScanner
{
    /// <summary>Действие рассылки → марка (для журнала).</summary>
    private static readonly Dictionary<string, string> Actions = new(StringComparer.Ordinal)
    {
        ["com.sunmi.scanner.ACTION_DATA_CODE_RECEIVED"] = "Sunmi",
        ["com.imin.scanner.api.RESULT_ACTION"] = "iMin",
        ["android.intent.ACTION_DECODE_DATA"] = "Urovo",
        ["urovo.rcv.message"] = "Urovo",
        ["nlscan.action.SCANNER_RESULT"] = "Newland",
        ["android.intent.action.SCANRESULT"] = "iData",
        ["scan.rcv.message"] = "сканер (scan.rcv.message)",
        ["com.scanner.broadcast"] = "сканер (com.scanner.broadcast)",
        ["com.android.server.scannerservice.broadcast"] = "Seuic",
        ["com.android.scanservice.scancontext"] = "сканер (scancontext)",
        ["com.barcode.sendBroadcast"] = "сканер (sendBroadcast)",
        ["com.zkc.scancode"] = "ZKC",
        ["android.intent.action.DECODE_DATA"] = "сканер (DECODE_DATA)",
    };

    /// <summary>Поля с кодом строкой — в порядке, в каком их обычно присылают.</summary>
    private static readonly string[] StringExtras =
    {
        "data", "barcode_string", "barcodeString", "SCAN_BARCODE1", "value", "decode_data_str", "decode_data",
        "scannerdata", "Scan_context", "code", "BARCODE", "barcode", "barCode", "result", "text",
    };

    /// <summary>Поля с кодом байтами (часто вместе с "length").</summary>
    private static readonly string[] ByteExtras = { "source_byte", "barocode", "barcode", "decode_data", "data" };

    private static Receiver? _receiver;

    public static void Install(Context context)
    {
        if (_receiver is not null)
            return;
        try
        {
            var filter = new IntentFilter();
            foreach (var action in Actions.Keys)
                filter.AddAction(action);
            _receiver = new Receiver();
            // Рассылки приходят от службы сканера (другое приложение) — получатель должен быть «открытым».
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                context.RegisterReceiver(_receiver, filter, ReceiverFlags.Exported);
            else
                context.RegisterReceiver(_receiver, filter);
            PosLogger.Log("Android: встроенный сканер POS — приём рассылок включён (Sunmi, iMin, Urovo, Newland, iData и др.).", "INFO");
        }
        catch (Exception ex)
        {
            _receiver = null;
            PosLogger.Log($"Android: приём рассылок сканера не включён: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Код из рассылки: сначала строковые поля, потом байтовые (UTF-8, с учётом "length").</summary>
    internal static string? ReadCode(Intent intent)
    {
        // Newland: при неудачном скане SCAN_STATE = "fail".
        if (string.Equals(intent.GetStringExtra("SCAN_STATE"), "fail", StringComparison.OrdinalIgnoreCase))
            return null;
        foreach (var key in StringExtras)
        {
            string? text = null;
            try { text = intent.GetStringExtra(key); } catch { /* в поле не строка */ }
            if (!string.IsNullOrWhiteSpace(text))
                return Clean(text);
        }
        var length = intent.GetIntExtra("length", -1);
        foreach (var key in ByteExtras)
        {
            byte[]? bytes = null;
            try { bytes = intent.GetByteArrayExtra(key); } catch { /* в поле не байты */ }
            if (bytes is { Length: > 0 })
            {
                var count = length > 0 && length <= bytes.Length ? length : bytes.Length;
                var text = Encoding.UTF8.GetString(bytes, 0, count);
                if (!string.IsNullOrWhiteSpace(text))
                    return Clean(text);
            }
        }
        return null;
    }

    /// <summary>Без перевода строки и управляющих символов (некоторые сканеры дописывают \r\n или префикс AIM).</summary>
    private static string Clean(string text) =>
        new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();

    private sealed class Receiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action is not { } action)
                return;
            var code = ReadCode(intent);
            if (string.IsNullOrEmpty(code) || code.Length > 128)
                return;
            var brand = Actions.TryGetValue(action, out var b) ? b : action;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (NurMarketKassa.AvaloniaHost.App.GetRequiredService<NurMarketKassa.Interfaces.IBarcodeInputService>()
                        is NurMarketKassa.AvaloniaHost.Services.AvaloniaKeyboardWedgeBarcodeService wedge)
                    {
                        PosLogger.Log($"Встроенный сканер ({brand}): код {code.Length} симв.", "CART");
                        wedge.InjectDeviceScan(code);
                    }
                }
                catch (Exception ex)
                {
                    // Касса ещё не запущена (рассылка пришла во время загрузки) — скан просто пропускается.
                    PosLogger.Log($"Встроенный сканер ({brand}): код не принят: {ex.Message}", "WARNING");
                }
            });
        }
    }
}
